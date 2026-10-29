using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>
/// SYSTEM-side connector. The Agent initiates one authenticated WebSocket to the configured
/// teacher service, reconnecting with bounded backoff when the teacher is unavailable.
/// </summary>
public static class StudentAgentTeacherChannelClient
{
    private static readonly HttpClient LocalAgentClient = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(25) };

    public static async Task RunAsync(string configPath, WebsitePolicyAgentConfig config,
        RSA agentIdentityKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(agentIdentityKey);
        StudentAgentChannelProtocol.ValidateBootstrapConfiguration(config.CampusId,
            config.TeacherChannelEndpoints, config.TeacherChannelRootCertificateBase64);
        if (config.TeacherChannelEndpoints is not { Count: > 0 } endpoints ||
            config.TeacherChannelRootCertificateBase64 is not { Length: > 0 } rootBase64)
            return;

        using var teacherRoot = StudentAgentChannelProtocol.ValidateRootCertificate(rootBase64);
        var failureCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = false;
            foreach (var endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ConnectAndServeAsync(configPath, config, endpoint, teacherRoot,
                        agentIdentityKey, cancellationToken).ConfigureAwait(false);
                    connected = true;
                    failureCount = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception exception) when (exception is WebSocketException or HttpRequestException or
                    IOException or InvalidDataException or CryptographicException or JsonException or
                    TaskCanceledException or UriFormatException or AuthenticationException or
                    ObjectDisposedException or InvalidOperationException)
                {
                    WebsitePolicyAgentInstaller.ReportAgentOperationFailure(configPath, "teacher-channel",
                        Guid.NewGuid().ToString("N")[..12], exception);
                }
                if (connected) break;
            }
            if (cancellationToken.IsCancellationRequested) return;
            failureCount = Math.Min(failureCount + 1, 8);
            var baseSeconds = connected ? 1 : Math.Min(30, 2 << Math.Min(failureCount - 1, 6));
            var jitter = 0.8 + RandomNumberGenerator.GetInt32(401) / 1000.0;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(baseSeconds * 1000 * jitter), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private static async Task ConnectAndServeAsync(string configPath, WebsitePolicyAgentConfig config,
        string endpoint, X509Certificate2 teacherRoot, RSA agentIdentityKey,
        CancellationToken cancellationToken)
    {
        var uri = BuildWebSocketUri(endpoint);
        using var socket = new ClientWebSocket();
        using var sendGate = new SemaphoreSlim(1, 1);
        socket.Options.KeepAliveInterval = StudentAgentChannelProtocol.HeartbeatInterval;
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            ValidateTeacherCertificate(certificate, chain, errors, teacherRoot);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        await socket.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);

        var challenge = StudentAgentChannelProtocol.Deserialize(
            await ReceiveMessageAsync(socket, connectTimeout.Token).ConfigureAwait(false));
        if (challenge.Type != "challenge" || challenge.CampusId != config.CampusId ||
            string.IsNullOrWhiteSpace(challenge.Nonce))
            throw new InvalidDataException("教师端连接挑战无效。");
        var hello = StudentAgentChannelProtocol.CreateHello(config.CampusId, challenge.Nonce, agentIdentityKey);
        await SendMessageAsync(socket, hello, sendGate, connectTimeout.Token).ConfigureAwait(false);
        var ready = StudentAgentChannelProtocol.Deserialize(
            await ReceiveMessageAsync(socket, connectTimeout.Token).ConfigureAwait(false));
        if (ready.Type != "ready" || ready.CampusId != config.CampusId ||
            string.IsNullOrWhiteSpace(ready.Target))
            throw new InvalidDataException("教师端没有确认学生 Agent 身份。");
        _ = ClassroomEventCryptography.NormalizeSingleTarget(ready.Target);

        using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiveTask = ReceiveLoopAsync(configPath, socket, sendGate, sessionLifetime.Token);
        var heartbeatTask = HeartbeatLoopAsync(socket, sendGate, sessionLifetime.Token);
        var completed = await Task.WhenAny(receiveTask, heartbeatTask).ConfigureAwait(false);
        sessionLifetime.Cancel();
        try { await completed.ConfigureAwait(false); }
        finally
        {
            try { await Task.WhenAll(receiveTask, heartbeatTask).ConfigureAwait(false); }
            catch (OperationCanceledException) when (sessionLifetime.IsCancellationRequested) { }
        }
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnect",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException) { }
        }
    }

    private static async Task ReceiveLoopAsync(string configPath, ClientWebSocket socket,
        SemaphoreSlim sendGate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var bytes = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            var message = StudentAgentChannelProtocol.Deserialize(bytes);
            if (message.Type == "heartbeat-ack") continue;
            if (message.Type != "http-request")
                throw new InvalidDataException("教师端发送了未知连接消息。");
            await ForwardRequestAsync(configPath, socket, message, sendGate, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ForwardRequestAsync(string configPath, ClientWebSocket socket,
        StudentAgentChannelMessage request, SemaphoreSlim sendGate, CancellationToken cancellationToken)
    {
        try
        {
            if (request.RequestId is not { } requestId || string.IsNullOrWhiteSpace(request.Method) ||
                string.IsNullOrWhiteSpace(request.PathAndQuery) || !request.PathAndQuery.StartsWith("/",
                    StringComparison.Ordinal) || request.PathAndQuery.StartsWith("//", StringComparison.Ordinal) ||
                request.PathAndQuery.Contains('\\') || request.PathAndQuery.Contains('#') ||
                request.PathAndQuery.Any(char.IsControl) ||
                request.Body is { Length: > StudentAgentChannelProtocol.MaximumRequestBodyBytes })
                throw new InvalidDataException("教师端转发请求格式无效。");

            var requestUri = new Uri("http://127.0.0.1:" + WebsitePolicyAgent.Port + request.PathAndQuery);
            using var localRequest = new HttpRequestMessage(new HttpMethod(request.Method), requestUri);
            if (request.Body is { Length: > 0 } body)
            {
                localRequest.Content = new ByteArrayContent(body);
                if (request.ContentType is { Length: > 0 } contentType)
                    localRequest.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            if (request.Headers is not null)
                foreach (var pair in request.Headers)
                {
                    if (pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                        pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!localRequest.Headers.TryAddWithoutValidation(pair.Key, pair.Value) &&
                        localRequest.Content is not null)
                        localRequest.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                }
            using var localResponse = await LocalAgentClient.SendAsync(localRequest,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var responseBody = await localResponse.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (responseBody.Length > StudentAgentChannelProtocol.MaximumResponseBodyBytes)
                throw new InvalidDataException("学生 Agent 本地响应超过连接传输限制。");
            var responseHeaders = localResponse.Headers.ToDictionary(pair => pair.Key,
                pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in localResponse.Content.Headers)
                responseHeaders[pair.Key] = pair.Value.ToArray();
            var response = new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion,
                "http-response", RequestId: requestId, ContentType: localResponse.Content.Headers.ContentType?.ToString(),
                Headers: responseHeaders, Body: responseBody, StatusCode: (int)localResponse.StatusCode);
            await SendMessageAsync(socket, response, sendGate, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
            InvalidDataException or UriFormatException or ArgumentException or ObjectDisposedException or
            WebSocketException or TaskCanceledException)
        {
            WebsitePolicyAgentInstaller.ReportAgentOperationFailure(configPath, "teacher-channel-request",
                Guid.NewGuid().ToString("N")[..12], exception);
            if (request.RequestId is not { } requestId) return;
            try
            {
                var failure = new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion,
                    "http-response", RequestId: requestId, StatusCode: 503,
                    Body: System.Text.Encoding.UTF8.GetBytes("student agent request failed"));
                await SendMessageAsync(socket, failure, sendGate, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception sendException) when (sendException is WebSocketException or
                OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static async Task HeartbeatLoopAsync(ClientWebSocket socket, SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(StudentAgentChannelProtocol.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await SendMessageAsync(socket, new StudentAgentChannelMessage(
                StudentAgentChannelProtocol.CurrentVersion, "heartbeat"), sendGate, cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task SendMessageAsync(ClientWebSocket socket, StudentAgentChannelMessage message,
        SemaphoreSlim sendGate, CancellationToken cancellationToken)
    {
        var bytes = StudentAgentChannelProtocol.Serialize(message);
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                cancellationToken).ConfigureAwait(false);
        }
        finally { sendGate.Release(); }
    }

    private static async Task<byte[]> ReceiveMessageAsync(ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("教师端已关闭学生 Agent 连接。");
                if (result.MessageType != WebSocketMessageType.Text ||
                    message.Length + result.Count > StudentAgentChannelProtocol.MaximumMessageBytes)
                    throw new InvalidDataException("学生连接帧类型或大小无效。");
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return message.ToArray();
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static bool ValidateTeacherCertificate(X509Certificate? certificate, X509Chain? suppliedChain,
        SslPolicyErrors errors, X509Certificate2 trustedRoot)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 ||
            (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
        using var leaf = certificate as X509Certificate2 is { } existing
            ? new X509Certificate2(existing)
            : new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        return chain.Build(leaf);
    }

    private static Uri BuildWebSocketUri(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            !ClassroomEventCryptography.IsPrivateIpv4Address(IPAddress.Parse(baseUri.Host)))
            throw new InvalidDataException("教师端连接地址无效。");
        var builder = new UriBuilder(baseUri)
        {
            Scheme = Uri.UriSchemeWss,
            Path = StudentAgentChannelProtocol.WebSocketPath,
            Query = ""
        };
        return builder.Uri;
    }
}
