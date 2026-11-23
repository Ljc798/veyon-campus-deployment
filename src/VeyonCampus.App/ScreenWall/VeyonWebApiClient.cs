using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.App;

/// <summary>Thumbnail sizing presets measured against Veyon 4.11.2 (see the 2026-11-23 spike record).</summary>
internal sealed record ScreenWallQuality(string Name, int Width, int Height, int JpegQuality)
{
    internal static readonly ScreenWallQuality Thumb = new("thumb", 320, 180, 55);
    internal static readonly ScreenWallQuality Large = new("large", 480, 270, 65);
    internal static readonly ScreenWallQuality Detail = new("detail", 960, 540, 80);

    internal static ScreenWallQuality ForScale(bool large) => large ? Large : Thumb;
}

/// <summary>
/// A typed failure from the local Veyon WebAPI. The API reuses HTTP 503 for two very different
/// conditions (unsupported image format = code 9, first frame not ready = code 10), so callers must
/// switch on <see cref="Code"/> and never on the status code alone.
/// </summary>
internal sealed class VeyonWebApiException : Exception
{
    internal VeyonWebApiException(string message, int statusCode, int code) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    internal int StatusCode { get; }
    internal int Code { get; }

    internal bool IsConnectionInvalid => StatusCode == (int)HttpStatusCode.Unauthorized && Code == 2;
    internal bool IsCredentialRejected => StatusCode is 401 or 400;
    internal bool IsDeviceUnreachable => StatusCode == (int)HttpStatusCode.RequestTimeout;
    internal bool IsFirstFramePending => StatusCode == 503 && Code == 10;
    internal bool IsImageFormatUnsupported => StatusCode == 503 && Code == 9;
    internal bool IsCapacityExceeded => StatusCode == 429;
}

/// <summary>
/// The only HTTP surface the screen wall uses. Fixed to loopback: the Veyon WebAPI has no TLS and its
/// key authentication sends the private key in the request body, so a remote base address would hand
/// the teacher's key to anyone on the wire.
/// </summary>
internal sealed class VeyonWebApiClient : IDisposable
{
    internal const int DefaultPort = 11080;
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(25);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly int _port;

    internal VeyonWebApiClient(int port = DefaultPort)
    {
        _port = port;
        _baseUrl = $"http://127.0.0.1:{port}/api/v1/";
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            BaseAddress = new Uri(_baseUrl)
        };
    }

    internal string BaseUrl => _baseUrl;

    /// <summary>
    /// Cheap loopback probe. A TCP connect is enough to distinguish "WebAPI not enabled" from
    /// "enabled but authentication fails", which are different instructions for the teacher.
    /// </summary>
    internal async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, _port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or
                                           ObjectDisposedException or ArgumentException)
        {
            return false;
        }
    }

    internal async Task<string> ReadAuthenticationMethodAsync(string host, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"authentication/{Escape(host)}");
        var body = await SendForBodyAsync(request, AuthenticationTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("methods", out var methods) &&
                methods.ValueKind == JsonValueKind.Array && methods.GetArrayLength() > 0 &&
                methods[0].GetString() is { Length: > 0 } method)
                return method;
        }
        catch (JsonException)
        {
            // Falls through to the typed error below; the response was not the documented shape.
        }

        throw new VeyonWebApiException("WebAPI 未返回可用的认证方式。", 200, 0);
    }

    internal async Task<string> AuthenticateAsync(string host, string method, VeyonKeyCredential credential,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            method,
            credentials = new { keyname = credential.KeyName, keydata = credential.PrivateKeyPem }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"authentication/{Escape(host)}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await SendAsync(request, AuthenticationTimeout, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw BuildError(response.StatusCode, body);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("connection-uid", out var uid) &&
                uid.GetString() is { Length: > 0 } connectionUid)
                return connectionUid;
        }
        catch (JsonException)
        {
            // Falls through to the header fallback below.
        }

        if (response.Headers.TryGetValues("Connection-Uid", out var values) &&
            values.FirstOrDefault() is { Length: > 0 } headerUid)
            return headerUid;
        throw new VeyonWebApiException("认证响应缺少连接标识。", (int)response.StatusCode, 0);
    }

    internal async Task<byte[]> ReadFrameAsync(string connectionUid, ScreenWallQuality quality,
        CancellationToken cancellationToken)
    {
        var uri = $"framebuffer?width={quality.Width}&height={quality.Height}&format=jpg" +
                  $"&compression=5&quality={quality.JpegQuality}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Connection-Uid", connectionUid);
        var bytes = await SendForBytesAsync(request, FrameTimeout, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0) throw new VeyonWebApiException("WebAPI 返回了空画面。", 200, 0);
        return bytes;
    }

    internal async Task ReleaseAsync(string host, string connectionUid, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"authentication/{Escape(host)}");
        request.Headers.TryAddWithoutValidation("Connection-Uid", connectionUid);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            _ = response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or
                                           IOException)
        {
            // Releasing is best effort: the API also expires idle connections on its own.
        }
    }

    private async Task<byte[]> SendForBytesAsync(HttpRequestMessage request, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw BuildError(response.StatusCode, body);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendForBodyAsync(HttpRequestMessage request, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw BuildError(response.StatusCode, body);
        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            return await _http.SendAsync(request, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VeyonWebApiException("WebAPI 请求超时。", (int)HttpStatusCode.RequestTimeout, 8);
        }
        catch (HttpRequestException exception)
        {
            throw new VeyonWebApiException("无法连接本机 Veyon WebAPI。", 0, 0) { Source = exception.Source };
        }
    }

    private static VeyonWebApiException BuildError(HttpStatusCode statusCode, string body)
    {
        var code = 0;
        var message = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("code", out var errorCode) && errorCode.TryGetInt32(out var parsed))
                    code = parsed;
                if (error.TryGetProperty("message", out var errorMessage))
                    message = errorMessage.GetString() ?? "";
            }
            else if (root.TryGetProperty("code", out var topCode) && topCode.TryGetInt32(out var topParsed))
                code = topParsed;
        }
        catch (JsonException)
        {
            // Non-JSON error body; the status code is all we have.
        }

        var description = string.IsNullOrWhiteSpace(message)
            ? $"WebAPI 返回 HTTP {(int)statusCode}（code {code}）。"
            : $"WebAPI 返回 HTTP {(int)statusCode}（code {code}）：{message}";
        return new VeyonWebApiException(description, (int)statusCode, code);
    }

    private static string Escape(string host) => Uri.EscapeDataString(host);

    public void Dispose() => _http.Dispose();
}
