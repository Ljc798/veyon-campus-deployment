using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VeyonCampus.Core;

namespace VeyonCampus.Core.Maintenance;

/// <summary>
/// SYSTEM-side Maintenance connector. It owns a separate TLS/WebSocket session and never
/// forwards commands through the Student Agent listener or classroom session state.
/// </summary>
public sealed class MaintenanceServiceTeacherChannelClient
{
    private const int MaximumReconnectDelaySeconds = 30;
    private readonly MaintenanceServiceCommandDispatcher _dispatcher;

    public MaintenanceServiceTeacherChannelClient(MaintenanceServiceCommandDispatcher? dispatcher = null) =>
        _dispatcher = dispatcher ?? new MaintenanceServiceCommandDispatcher();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task RunAsync(string configPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        EnsureSystemIdentity();
        var failureCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            MaintenanceServiceBootstrapSnapshot bootstrap;
            try
            {
                bootstrap = MaintenanceServiceConfigurationStore.LoadOrInitializeForSystemService(configPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or CryptographicException or
                                              System.Security.SecurityException or ArgumentException or
                                              InvalidOperationException)
            {
                await DelayAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (bootstrap.Endpoints is not { Count: > 0 } endpoints ||
                bootstrap.RootCertificateBase64 is not { Length: > 0 } rootBase64)
            {
                await DelayAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var teacherRoot = StudentAgentChannelProtocol.ValidateRootCertificate(rootBase64);
            var connected = false;
            foreach (var endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ConnectAndServeAsync(configPath, bootstrap, endpoint, teacherRoot,
                        cancellationToken).ConfigureAwait(false);
                    connected = true;
                    failureCount = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception exception) when (exception is WebSocketException or HttpRequestException or
                    IOException or InvalidDataException or CryptographicException or TaskCanceledException or
                    UriFormatException or AuthenticationException or ObjectDisposedException or
                    InvalidOperationException or UnauthorizedAccessException or ArgumentException)
                {
                    // Keep diagnostics bounded to the service host/event log; never include signed payloads.
                }
                if (connected) break;
            }

            if (cancellationToken.IsCancellationRequested) return;
            failureCount = Math.Min(failureCount + 1, 8);
            var delaySeconds = connected ? 1 : Math.Min(MaximumReconnectDelaySeconds,
                2 << Math.Min(failureCount - 1, 6));
            var jitter = 0.8 + RandomNumberGenerator.GetInt32(401) / 1000.0;
            await DelayAsync(TimeSpan.FromMilliseconds(delaySeconds * 1000 * jitter), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ConnectAndServeAsync(string configPath, MaintenanceServiceBootstrapSnapshot bootstrap,
        string endpoint, X509Certificate2 teacherRoot, CancellationToken cancellationToken)
    {
        var uri = BuildWebSocketUri(endpoint);
        using var socket = new ClientWebSocket();
        using var sendGate = new SemaphoreSlim(1, 1);
        socket.Options.AddSubProtocol(MaintenanceServiceChannelProtocol.WebSocketSubProtocol);
        socket.Options.KeepAliveInterval = MaintenanceServiceChannelProtocol.HeartbeatInterval;
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            ValidateTeacherCertificate(certificate, errors, teacherRoot);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { await socket.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Maintenance 教师 TLS 通道连接超时。", exception); }

        var challenge = MaintenanceServiceChannelProtocol.Deserialize(
            await ReceiveMessageAsync(socket, connectTimeout.Token).ConfigureAwait(false));
        if (challenge.Type != "challenge" || challenge.CampusId != bootstrap.CampusId ||
            challenge.Nonce is null)
            throw new InvalidDataException("教师端 Maintenance 身份挑战无效。");

        using var maintenanceKey = MaintenanceServiceIdentityStore.LoadOrCreateForSystemService(configPath);
        var agentFingerprint = StudentAgentIdentityKeyStore.ReadFingerprint(configPath);
        var signedEnrollment = MaintenanceServiceIdentityStore.CreateSignedEnrollment(configPath);
        var hello = MaintenanceServiceChannelProtocol.CreateHello(bootstrap.CampusId, challenge.Nonce,
            signedEnrollment, agentFingerprint,
            StudentAgentResponseCryptography.GetFingerprint(maintenanceKey.ExportSubjectPublicKeyInfoPem()),
            maintenanceKey);
        await SendMessageAsync(socket, hello, sendGate, connectTimeout.Token).ConfigureAwait(false);
        var ready = MaintenanceServiceChannelProtocol.Deserialize(
            await ReceiveMessageAsync(socket, connectTimeout.Token).ConfigureAwait(false));
        if (ready.Type != "ready" || ready.CampusId != bootstrap.CampusId ||
            ready.MaintenanceIdentityFingerprint is null ||
            !string.Equals(MaintenanceValidation.Fingerprint(ready.MaintenanceIdentityFingerprint,
                    nameof(ready.MaintenanceIdentityFingerprint)),
                StudentAgentResponseCryptography.GetFingerprint(maintenanceKey.ExportSubjectPublicKeyInfoPem()),
                StringComparison.Ordinal) || string.IsNullOrWhiteSpace(ready.Target))
            throw new InvalidDataException("教师端没有确认 Maintenance 独立身份。");
        _ = WebsitePolicyTransport.NormalizeTargets([ready.Target]);

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
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnect", CancellationToken.None)
                    .ConfigureAwait(false); }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException) { }
        }
        if (!cancellationToken.IsCancellationRequested)
            throw new IOException("教师端 Maintenance 认证长连接已关闭；服务将重新连接。");
    }

    private async Task ReceiveLoopAsync(string configPath, ClientWebSocket socket, SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = MaintenanceServiceChannelProtocol.Deserialize(
                await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false));
            if (message.Type == "heartbeat") continue;
            if (message.Type != "request" || message.RequestId is not { } requestId ||
                message.SignedRequestJson is null)
                throw new InvalidDataException("教师端发送了非 Maintenance 命令消息。");

            var dispatchResult = await _dispatcher.DispatchSignedRequestAsync(configPath,
                message.SignedRequestJson, cancellationToken: cancellationToken).ConfigureAwait(false);
            await SendMessageAsync(socket, new MaintenanceServiceChannelMessage(
                    MaintenanceServiceChannelProtocol.CurrentVersion, "response", RequestId: requestId,
                    SignedResponseJson: dispatchResult.SignedResponseJson), sendGate, cancellationToken)
                .ConfigureAwait(false);
            if (dispatchResult.AfterResponseSent is not null)
                await dispatchResult.AfterResponseSent().ConfigureAwait(false);
            if (dispatchResult.ReconnectAfterResponse) return;
        }
    }

    private static async Task HeartbeatLoopAsync(ClientWebSocket socket, SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MaintenanceServiceChannelProtocol.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await SendMessageAsync(socket, new MaintenanceServiceChannelMessage(
                MaintenanceServiceChannelProtocol.CurrentVersion, "heartbeat"), sendGate, cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task SendMessageAsync(ClientWebSocket socket, MaintenanceServiceChannelMessage message,
        SemaphoreSlim sendGate, CancellationToken cancellationToken)
    {
        var bytes = MaintenanceServiceChannelProtocol.Serialize(message);
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
                    throw new WebSocketException("教师端关闭了 Maintenance 服务连接。");
                if (result.MessageType != WebSocketMessageType.Text ||
                    message.Length + result.Count > MaintenanceServiceChannelProtocol.MaximumMessageBytes)
                    throw new InvalidDataException("Maintenance 通道帧类型或大小无效。");
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return message.ToArray();
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static Uri BuildWebSocketUri(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            !IPAddress.TryParse(baseUri.Host, out var address) ||
            !ClassroomEventCryptography.IsPrivateIpv4Address(address))
            throw new InvalidDataException("Maintenance 教师连接地址无效。");
        return new UriBuilder(baseUri)
        {
            Scheme = Uri.UriSchemeWss,
            Path = MaintenanceServiceChannelProtocol.WebSocketPath,
            Query = ""
        }.Uri;
    }

    private static bool ValidateTeacherCertificate(X509Certificate? certificate, SslPolicyErrors errors,
        X509Certificate2 trustedRoot)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 ||
            (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
        using var leaf = certificate is X509Certificate2 existing
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

    private static void EnsureSystemIdentity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Maintenance Service 仅支持 Windows。");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18")
            throw new UnauthorizedAccessException("独立 Maintenance 通道必须由 SYSTEM 服务运行。");
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}

/// <summary>Worker entry used by the future SCM host; it performs transaction recovery before reconnecting.</summary>
public sealed class MaintenanceServiceWorker
{
    private readonly MaintenanceServiceTeacherChannelClient _channel;
    private readonly Action<string> _log;

    public MaintenanceServiceWorker(MaintenanceServiceTeacherChannelClient? channel = null,
        Action<string>? log = null)
    {
        _channel = channel ?? new MaintenanceServiceTeacherChannelClient();
        _log = log ?? (_ => { });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Maintenance Service 仅支持 Windows。");
        string configPath;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                configPath = MaintenanceServiceConfigurationStore.ResolveAgentConfigurationPath();
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or System.Security.SecurityException or
                                              InvalidOperationException or ArgumentException)
            {
                await DelayAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
        using (MaintenanceServiceIdentityStore.LoadOrCreateForSystemService(configPath)) { }
        while (!MaintenanceServiceConfigurationStore.RecoverInterruptedTeacherEndpointUpdate(configPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { _ = StudentUpdateAtomicTransaction.RecoverFromCommand(configPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or System.Security.SecurityException or
                                              InvalidOperationException or ArgumentException)
            {
                // Keep the recovery journal and retry after the independent update transaction settles.
            }
            await DelayAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        _ = MaintenanceServiceConfigurationStore.LoadOrInitializeForSystemService(configPath);
        _ = StudentUpdateAtomicTransaction.RecoverFromCommand(configPath);
        _log(ReconcileInterruptedMaintenanceUpdate(configPath));
        await _channel.RunAsync(configPath, cancellationToken).ConfigureAwait(false);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string ReconcileInterruptedMaintenanceUpdate(string configPath)
    {
        try
        {
            var transaction = MaintenanceServiceUpdateExecutor.ReadTransaction(configPath);
            if (transaction is null || transaction.State is not ("pending" or "inProgress"))
                return "Maintenance startup recovery found no interrupted service update.";

            // A helper that survived the SCM restart still owns the transaction. Otherwise,
            // ReadStatus verifies the installed Setup, Agent and Maintenance versions before
            // committing; any mixed or unreadable state remains pending/needsReview for teachers.
            var status = MaintenanceServiceUpdateExecutor.ReadStatus(configPath, transaction.CommandId,
                DateTimeOffset.UtcNow);
            return "Maintenance startup recovery reconciled command " + transaction.CommandId.ToString("N") +
                   " to state " + (status?.State ?? "needsReview") + ".";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or
                                          InvalidOperationException or TimeoutException or
                                          System.Security.SecurityException or ArgumentException)
        {
            // Do not make the service unavailable because recovery evidence is damaged. Keep the
            // journal and recovery snapshot intact; signed transaction queries will report review.
            return "Maintenance startup recovery deferred; evidence retained (" + exception.GetType().Name + ").";
        }
    }
}
