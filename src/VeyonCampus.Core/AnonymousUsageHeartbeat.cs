using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>Sends a minimal, anonymous once-a-day presence signal when explicitly configured.</summary>
public static class AnonymousUsageHeartbeat
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static Uri ValidateEndpoint(string endpoint)
    {
        if (endpoint.Length is 0 or > 2048 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("遥测地址必须是 HTTPS 地址；仅本机回环地址允许使用 HTTP，且不能含凭据、查询参数或片段。");
        return uri;
    }

    public static async Task RunAsync(string endpoint, string installationIdPath,
        CancellationToken cancellationToken = default)
    {
        var uri = ValidateEndpoint(endpoint);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var timer = new PeriodicTimer(Interval);
        string? installationId = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                installationId ??= await GetOrCreateInstallationIdAsync(installationIdPath, cancellationToken)
                    .ConfigureAwait(false);
                using var response = await client.PostAsJsonAsync(uri,
                    new HeartbeatRequest(installationId), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Telemetry is best-effort; network or local storage failures must not affect the agent.
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static async Task<string> GetOrCreateInstallationIdAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path)) return await ReadInstallationIdAsync(path, cancellationToken).ConfigureAwait(false);

        var parent = Path.GetDirectoryName(Path.GetFullPath(path))
                     ?? throw new InvalidDataException("匿名安装标识目录无效。");
        Directory.CreateDirectory(parent);
        var candidate = Guid.NewGuid().ToString("N");
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                256, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var bytes = Encoding.ASCII.GetBytes(candidate);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return candidate;
        }
        catch (IOException) when (File.Exists(path))
        {
            return await ReadInstallationIdAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadInstallationIdAsync(string path, CancellationToken cancellationToken)
    {
        var value = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
        if (value.Length != 32 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException("本机匿名安装标识格式无效。");
        return value.ToLowerInvariant();
    }

    private sealed record HeartbeatRequest([property: JsonPropertyName("installationId")] string InstallationId);
}
