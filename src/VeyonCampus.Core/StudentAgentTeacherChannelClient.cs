using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

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
        StudentAgentTeacherChannelDiagnostics.Initialize(configPath);
        StudentAgentTeacherChannelDiagnostics.RecordConfiguration(configPath, FingerprintConfiguration(config));
        if (config.TeacherChannelEndpoints is not { Count: > 0 })
        {
            StudentAgentTeacherChannelDiagnostics.RecordFailure(configPath, "MissingEndpoint");
            throw new InvalidDataException("教师端地址未配置。" );
        }
        if (string.IsNullOrWhiteSpace(config.TeacherChannelRootCertificateBase64))
        {
            StudentAgentTeacherChannelDiagnostics.RecordFailure(configPath, "MissingRootCertificate");
            throw new InvalidDataException("教师端 TLS 根证书未配置。" );
        }
        try
        {
            StudentAgentChannelProtocol.ValidateBootstrapConfiguration(config.CampusId,
                config.TeacherChannelEndpoints, config.TeacherChannelRootCertificateBase64);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or
                                          CryptographicException)
        {
            StudentAgentTeacherChannelDiagnostics.RecordFailure(configPath, "InvalidConfiguration");
            throw;
        }
        var originalTrust = config;
        var failureCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            WebsitePolicyAgentConfig latestConfig;
            try
            {
                var bytes = await File.ReadAllBytesAsync(configPath, cancellationToken).ConfigureAwait(false);
                latestConfig = JsonSerializer.Deserialize<WebsitePolicyAgentConfig>(bytes,
                    WebsitePolicyAgent.JsonOptions)
                    ?? throw new InvalidDataException("学生网站策略代理配置为空。");
                latestConfig = ValidateReconnectConfiguration(originalTrust, latestConfig);
                StudentAgentTeacherChannelDiagnostics.RecordConfiguration(configPath,
                    FingerprintConfiguration(latestConfig));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidDataException or CryptographicException or JsonException or ArgumentException)
            {
                var classified = exception is StudentAgentTeacherChannelFailureException
                    ? exception
                    : new StudentAgentTeacherChannelFailureException("InvalidConfiguration", exception);
                StudentAgentTeacherChannelDiagnostics.RecordFailure(configPath,
                    StudentAgentTeacherChannelDiagnostics.ClassifyFailure(classified));
                WebsitePolicyAgentInstaller.ReportAgentOperationFailure(configPath, "teacher-channel-config",
                    Guid.NewGuid().ToString("N")[..12], classified);
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                continue;
            }

            if (latestConfig.TeacherChannelEndpoints is not { Count: > 0 } endpoints ||
                latestConfig.TeacherChannelRootCertificateBase64 is not { Length: > 0 } rootBase64)
            {
                StudentAgentTeacherChannelDiagnostics.RecordFailure(configPath,
                    latestConfig.TeacherChannelEndpoints is not { Count: > 0 }
                        ? "MissingEndpoint" : "MissingRootCertificate");
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                continue;
            }

            using var teacherRoot = StudentAgentChannelProtocol.ValidateRootCertificate(rootBase64);
            var connected = false;
            foreach (var endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StudentAgentTeacherChannelDiagnostics.RecordAttempt(configPath);
                try
                {
                    await ConnectAndServeAsync(configPath, latestConfig, endpoint, teacherRoot,
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
                    StudentAgentTeacherChannelDiagnostics.RecordFailure(configPath,
                        StudentAgentTeacherChannelDiagnostics.ClassifyFailure(exception));
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

    internal static WebsitePolicyAgentConfig ValidateReconnectConfiguration(
        WebsitePolicyAgentConfig trustedConfiguration, WebsitePolicyAgentConfig candidate)
    {
        ArgumentNullException.ThrowIfNull(trustedConfiguration);
        ArgumentNullException.ThrowIfNull(candidate);
        WebsitePolicySigningKeyStore.ValidateCampusId(candidate.CampusId);
        if (!string.Equals(candidate.CampusId, trustedConfiguration.CampusId, StringComparison.Ordinal) ||
            !WebsitePolicyAgentInstaller.PublicKeysEqual(candidate.PublicKeyPem,
                trustedConfiguration.PublicKeyPem))
            throw new InvalidDataException("教师地址重载不能改变学生 Agent 已固定的校区身份或签名公钥。");
        StudentAgentChannelProtocol.ValidateBootstrapConfiguration(candidate.CampusId,
            candidate.TeacherChannelEndpoints, candidate.TeacherChannelRootCertificateBase64);
        return candidate;
    }

    private static async Task ConnectAndServeAsync(string configPath, WebsitePolicyAgentConfig config,
        string endpoint, X509Certificate2 teacherRoot, RSA agentIdentityKey,
        CancellationToken cancellationToken)
    {
        var uri = BuildWebSocketUri(endpoint);
        using var socket = new ClientWebSocket();
        using var sendGate = new SemaphoreSlim(1, 1);
        socket.Options.AddSubProtocol(StudentAgentChannelProtocol.WebSocketSubProtocolV1);
        socket.Options.KeepAliveInterval = StudentAgentChannelProtocol.HeartbeatInterval;
        string? certificateFailureCode = null;
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
        {
            try
            {
                var valid = ValidateTeacherCertificate(certificate, chain, errors, teacherRoot);
                if (!valid) certificateFailureCode = "TlsValidationFailure";
                return valid;
            }
            catch (Exception exception) when (exception is CryptographicException or ArgumentException or
                                              InvalidOperationException)
            {
                certificateFailureCode = "TlsValidationFailure";
                return false;
            }
        };
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await socket.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested &&
                                                            certificateFailureCode is null)
        {
            throw new StudentAgentTeacherChannelFailureException("TcpConnectFailure", exception);
        }
        catch (Exception exception) when (certificateFailureCode is { } failureCode &&
                                          (exception is not OperationCanceledException ||
                                           !cancellationToken.IsCancellationRequested))
        {
            throw new StudentAgentTeacherChannelFailureException(failureCode, exception);
        }
        if (!StudentAgentChannelProtocol.IsCompatibleNegotiatedSubProtocol(socket.SubProtocol))
            throw new StudentAgentTeacherChannelFailureException("ProtocolMismatch",
                new InvalidDataException("教师端选择了不受支持的 Agent 通道子协议。"));

        var challenge = DeserializeTeacherMessage(
            await ReceiveMessageAsync(socket, connectTimeout.Token).ConfigureAwait(false));
        if (challenge.Type != "challenge" || challenge.CampusId != config.CampusId ||
            string.IsNullOrWhiteSpace(challenge.Nonce))
            throw new StudentAgentTeacherChannelFailureException("AuthenticationRejected",
                new InvalidDataException("教师端连接挑战无效。"));
        var hello = StudentAgentChannelProtocol.CreateHello(config.CampusId, challenge.Nonce, agentIdentityKey);
        await SendMessageAsync(socket, hello, sendGate, connectTimeout.Token).ConfigureAwait(false);
        var ready = DeserializeTeacherMessage(
            await ReceiveMessageAsync(socket, connectTimeout.Token).ConfigureAwait(false));
        if (ready.Type != "ready" || ready.CampusId != config.CampusId ||
            string.IsNullOrWhiteSpace(ready.Target))
            throw new StudentAgentTeacherChannelFailureException("AuthenticationRejected",
                new InvalidDataException("教师端没有确认学生 Agent 身份。"));
        try { _ = ClassroomEventCryptography.NormalizeSingleTarget(ready.Target); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            throw new StudentAgentTeacherChannelFailureException("AuthenticationRejected", exception);
        }
        StudentAgentTeacherChannelDiagnostics.RecordConnected(configPath);

        using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var requestDispatcher = new StudentAgentChannelRequestDispatcher(
            (request, token) => ForwardRequestAsync(configPath, socket, request, sendGate, token),
            (message, token) => SendMessageAsync(socket, message, sendGate, token));
        var receiveTask = ReceiveLoopAsync(socket, requestDispatcher, sessionLifetime.Token);
        var heartbeatTask = HeartbeatLoopAsync(socket, sendGate, sessionLifetime.Token);
        var completed = await Task.WhenAny(receiveTask, heartbeatTask).ConfigureAwait(false);
        sessionLifetime.Cancel();
        try { await completed.ConfigureAwait(false); }
        finally
        {
            try { await Task.WhenAll(receiveTask, heartbeatTask).ConfigureAwait(false); }
            catch (OperationCanceledException) when (sessionLifetime.IsCancellationRequested) { }
            await requestDispatcher.DrainAsync().ConfigureAwait(false);
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
        if (!cancellationToken.IsCancellationRequested)
            throw new IOException("教师端认证长连接已关闭；Agent 将重新连接。" );
    }

    private static async Task ReceiveLoopAsync(ClientWebSocket socket,
        StudentAgentChannelRequestDispatcher requestDispatcher, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var bytes = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            var message = DeserializeTeacherMessage(bytes);
            if (message.Type == "heartbeat-ack") continue;
            if (message.Type != "http-request")
                throw new StudentAgentTeacherChannelFailureException("ProtocolMismatch",
                    new InvalidDataException("教师端发送了未知连接消息。"));
            await requestDispatcher.DispatchAsync(message, cancellationToken).ConfigureAwait(false);
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
                    throw new StudentAgentTeacherChannelFailureException("ProtocolMismatch",
                        new InvalidDataException("学生连接帧类型或大小无效。"));
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
            !IPAddress.TryParse(baseUri.Host, out var address) ||
            !ClassroomEventCryptography.IsPrivateIpv4Address(address))
            throw new StudentAgentTeacherChannelFailureException("InvalidConfiguration",
                new InvalidDataException("教师端连接地址无效。"));
        var builder = new UriBuilder(baseUri)
        {
            Scheme = Uri.UriSchemeWss,
            Path = StudentAgentChannelProtocol.WebSocketPath,
            Query = ""
        };
        return builder.Uri;
    }

    private static StudentAgentChannelMessage DeserializeTeacherMessage(ReadOnlySpan<byte> bytes)
    {
        try { return StudentAgentChannelProtocol.Deserialize(bytes); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            throw new StudentAgentTeacherChannelFailureException("ProtocolMismatch", exception);
        }
    }

    internal static string? FingerprintConfiguration(WebsitePolicyAgentConfig config)
    {
        try
        {
            return StudentAgentChannelProtocol.GetBootstrapConfigurationFingerprint(config.CampusId,
                config.TeacherChannelEndpoints, config.TeacherChannelRootCertificateBase64);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or
                                          CryptographicException)
        {
            // Preserve a safe diagnostic record even when the bootstrap data itself is malformed.
            return null;
        }
    }
}

/// <summary>Runs independent HTTP requests concurrently while keeping WebSocket receives serialized.</summary>
internal sealed class StudentAgentChannelRequestDispatcher : IAsyncDisposable
{
    internal const int MaximumConcurrentRequests = 16;
    private readonly SemaphoreSlim _requestLimit = new(MaximumConcurrentRequests, MaximumConcurrentRequests);
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();
    private readonly Func<StudentAgentChannelMessage, CancellationToken, Task> _forwardAsync;
    private readonly Func<StudentAgentChannelMessage, CancellationToken, Task> _sendAsync;
    private int _disposed;

    public StudentAgentChannelRequestDispatcher(
        Func<StudentAgentChannelMessage, CancellationToken, Task> forwardAsync,
        Func<StudentAgentChannelMessage, CancellationToken, Task> sendAsync)
    {
        _forwardAsync = forwardAsync ?? throw new ArgumentNullException(nameof(forwardAsync));
        _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
    }

    public async Task DispatchAsync(StudentAgentChannelMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(StudentAgentChannelRequestDispatcher));
        if (request.Type != "http-request" || request.RequestId is not { } requestId || requestId == Guid.Empty)
            throw new InvalidDataException("教师转发请求缺少有效请求编号。");

        if (!_requestLimit.Wait(0))
        {
            await _sendAsync(new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion,
                "http-response", RequestId: requestId, StatusCode: 503,
                Body: Encoding.UTF8.GetBytes("student agent is busy")), cancellationToken).ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inFlight.TryAdd(requestId, completion.Task))
        {
            _requestLimit.Release();
            throw new InvalidDataException("教师连接重复使用了正在处理的请求编号。");
        }

        _ = RunRequestAsync(request, requestId, completion, cancellationToken);
    }

    private async Task RunRequestAsync(StudentAgentChannelMessage request, Guid requestId,
        TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        // Give DispatchAsync time to publish the tracked task before a fast local request can finish.
        await Task.Yield();
        try
        {
            await _forwardAsync(request, cancellationToken).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            _inFlight.TryRemove(requestId, out _);
            _requestLimit.Release();
        }
    }

    public async Task DrainAsync()
    {
        while (true)
        {
            var active = _inFlight.Values.ToArray();
            if (active.Length == 0) return;
            await Task.WhenAll(active).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await DrainAsync().ConfigureAwait(false); }
        finally { _requestLimit.Dispose(); }
    }
}

/// <summary>Reloads the SYSTEM Agent's teacher connection after a verified bootstrap sync.</summary>
public sealed class StudentAgentTeacherChannelSupervisor
{
    private readonly object _gate = new();
    private readonly Channel<bool> _reload = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private CancellationTokenSource? _activeSession;

    public void RequestReload()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            active = _activeSession;
            _reload.Writer.TryWrite(true);
        }
        try { active?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task RunAsync(string configPath, RSA agentIdentityKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(agentIdentityKey);
        StudentAgentTeacherChannelDiagnostics.Initialize(configPath);
        while (!cancellationToken.IsCancellationRequested)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate) _activeSession = session;
            try
            {
                var bytes = await File.ReadAllBytesAsync(configPath, cancellationToken).ConfigureAwait(false);
                var config = JsonSerializer.Deserialize<WebsitePolicyAgentConfig>(bytes, WebsitePolicyAgent.JsonOptions)
                             ?? throw new InvalidDataException("学生网站策略代理配置为空。");
                StudentAgentTeacherChannelDiagnostics.RecordConfiguration(configPath,
                    StudentAgentTeacherChannelClient.FingerprintConfiguration(config));
                if (config.TeacherChannelEndpoints is not { Count: > 0 } ||
                    string.IsNullOrWhiteSpace(config.TeacherChannelRootCertificateBase64))
                {
                    StudentAgentTeacherChannelDiagnostics.RecordFailure(configPath,
                        config.TeacherChannelEndpoints is not { Count: > 0 }
                            ? "MissingEndpoint" : "MissingRootCertificate");
                    await _reload.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var running = StudentAgentTeacherChannelClient.RunAsync(configPath, config, agentIdentityKey,
                    session.Token);
                using var reloadReadLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var reload = _reload.Reader.ReadAsync(reloadReadLifetime.Token).AsTask();
                var completed = await Task.WhenAny(running, reload).ConfigureAwait(false);
                if (completed == reload)
                {
                    session.Cancel();
                    try { await running.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (session.IsCancellationRequested) { }
                    await reload.ConfigureAwait(false);
                }
                else
                {
                    try { await running.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (session.IsCancellationRequested) { }
                    reloadReadLifetime.Cancel();
                    try { await reload.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (reloadReadLifetime.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidDataException or CryptographicException or JsonException or PlatformNotSupportedException or
                ArgumentException)
            {
                var classified = exception is StudentAgentTeacherChannelFailureException
                    ? exception
                    : new StudentAgentTeacherChannelFailureException("InvalidConfiguration", exception);
                StudentAgentTeacherChannelDiagnostics.RecordFailure(configPath,
                    StudentAgentTeacherChannelDiagnostics.ClassifyFailure(classified));
                WebsitePolicyAgentInstaller.ReportAgentOperationFailure(configPath, "teacher-channel",
                    Guid.NewGuid().ToString("N")[..12], classified);
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            }
            finally
            {
                lock (_gate)
                    if (ReferenceEquals(_activeSession, session)) _activeSession = null;
            }
        }
    }
}
