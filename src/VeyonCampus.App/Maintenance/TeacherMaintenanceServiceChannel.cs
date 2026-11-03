using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;

namespace VeyonCampus.App;

internal sealed partial class TeacherMobileControlService
{
    private const int MaximumActiveMaintenanceChannels = 128;
    private int _activeMaintenanceChannels;

    private async Task HandleMaintenanceServiceChannelAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (Interlocked.Increment(ref _activeMaintenanceChannels) > MaximumActiveMaintenanceChannels)
        {
            Interlocked.Decrement(ref _activeMaintenanceChannels);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        try
        {
            var requested = context.WebSockets.WebSocketRequestedProtocols;
            var subProtocol = requested.Contains(MaintenanceServiceChannelProtocol.WebSocketSubProtocol,
                StringComparer.Ordinal)
                ? MaintenanceServiceChannelProtocol.WebSocketSubProtocol
                : null;
            if (requested.Count > 0 && subProtocol is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var socket = subProtocol is null
                ? await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false)
                : await context.WebSockets.AcceptWebSocketAsync(subProtocol).ConfigureAwait(false);
            var campusId = CurrentCampusId();
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await SendMaintenanceMessageAsync(socket, new MaintenanceServiceChannelMessage(
                MaintenanceServiceChannelProtocol.CurrentVersion, "challenge", campusId, nonce),
                handshakeTimeout.Token).ConfigureAwait(false);
            var hello = MaintenanceServiceChannelProtocol.Deserialize(
                await ReceiveMaintenanceMessageAsync(socket, handshakeTimeout.Token).ConfigureAwait(false));
            if (hello.Type != "hello" || hello.CampusId != campusId ||
                hello.AgentIdentityFingerprint is null || hello.SignedEnrollmentJson is null)
            {
                await CloseRejectedMaintenanceChannelAsync(socket, "invalid identity", handshakeTimeout.Token)
                    .ConfigureAwait(false);
                return;
            }

            var matchingAgentPins = _agentTrustStore.List().Where(pin => pin.CampusId == campusId &&
                string.Equals(pin.Fingerprint, hello.AgentIdentityFingerprint,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchingAgentPins.Length != 1)
            {
                await CloseRejectedMaintenanceChannelAsync(socket, "untrusted agent identity",
                    handshakeTimeout.Token).ConfigureAwait(false);
                return;
            }

            var agentPin = matchingAgentPins[0];
            if (!StudentAgentChannelAdmissionPolicy.CanAdmitPinnedAgent(campusId, agentPin.CampusId,
                    agentPin.Target))
            {
                await CloseRejectedMaintenanceChannelAsync(socket, "campus identity mismatch",
                    handshakeTimeout.Token).ConfigureAwait(false);
                return;
            }

            var verifiedEnrollment = MaintenanceServiceIdentityStore.VerifyEnrollment(
                hello.SignedEnrollmentJson, agentPin.PublicKeyPem, campusId, agentPin.Fingerprint,
                DateTimeOffset.UtcNow);
            if (!MaintenanceServiceChannelProtocol.VerifyHello(hello, campusId, nonce, verifiedEnrollment))
            {
                await CloseRejectedMaintenanceChannelAsync(socket, "invalid maintenance proof",
                    handshakeTimeout.Token).ConfigureAwait(false);
                return;
            }

            var existingMaintenancePin = _maintenanceTrustStore.FindTrustedPin(agentPin.CampusId,
                agentPin.Target, agentPin.Fingerprint);
            if (existingMaintenancePin is not null &&
                existingMaintenancePin.MaintenanceIdentityFingerprint !=
                verifiedEnrollment.MaintenanceIdentityFingerprint)
            {
                _ = _maintenanceTrustStore.QueueKeyRotation(agentPin.Target, campusId, agentPin.Fingerprint,
                    existingMaintenancePin.MaintenanceIdentityFingerprint, hello.SignedEnrollmentJson,
                    DateTimeOffset.UtcNow);
                await CloseRejectedMaintenanceChannelAsync(socket, "maintenance identity rotation awaits teacher approval",
                    handshakeTimeout.Token).ConfigureAwait(false);
                return;
            }

            // First-time pinning occurs only after both the Agent-signed enrollment and a fresh
            // challenge signature prove the service possesses its separately stored private key.
            var maintenancePin = _maintenanceTrustStore.PinEnrollment(agentPin.Target, campusId,
                agentPin.Fingerprint, hello.SignedEnrollmentJson);
            await SendMaintenanceMessageAsync(socket, new MaintenanceServiceChannelMessage(
                MaintenanceServiceChannelProtocol.CurrentVersion, "ready", campusId,
                Target: agentPin.Target,
                MaintenanceIdentityFingerprint: maintenancePin.MaintenanceIdentityFingerprint),
                handshakeTimeout.Token).ConfigureAwait(false);

            await using var connection = new TeacherMaintenanceServiceConnection(socket);
            using var registration = MaintenanceServiceChannelHub.Register(campusId, agentPin.Target,
                connection.SendRequestAsync);
            try { _maintenanceServiceChannelConnected?.Invoke(agentPin.Target); }
            catch (InvalidOperationException)
            {
                // UI notification is best-effort; it must not tear down an authenticated service channel.
            }
            try { await connection.RunAsync(context.RequestAborted).ConfigureAwait(false); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception exception) when (exception is WebSocketException or IOException or InvalidDataException or
                                              ObjectDisposedException or OperationCanceledException)
            {
                // The hub registration is removed on exit; the SYSTEM service reconnects independently.
            }
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or InvalidDataException or
                                          CryptographicException or UnauthorizedAccessException or
                                          System.Security.SecurityException or ArgumentException or
                                          InvalidOperationException or OperationCanceledException)
        {
            // Invalid/untrusted handshakes are closed without reflecting details to the LAN peer.
        }
        finally { Interlocked.Decrement(ref _activeMaintenanceChannels); }
    }

    private static async Task SendMaintenanceMessageAsync(WebSocket socket,
        MaintenanceServiceChannelMessage message, CancellationToken cancellationToken)
    {
        var bytes = MaintenanceServiceChannelProtocol.Serialize(message);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReceiveMaintenanceMessageAsync(WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Maintenance 服务在身份核验前关闭了连接。");
            if (result.MessageType != WebSocketMessageType.Text ||
                message.Length + result.Count > MaintenanceServiceChannelProtocol.MaximumMessageBytes)
                throw new InvalidDataException("Maintenance 身份消息类型或大小无效。");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return message.ToArray();
        }
    }

    private static async Task CloseRejectedMaintenanceChannelAsync(WebSocket socket, string reason,
        CancellationToken cancellationToken)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or
                                          ObjectDisposedException) { }
    }
}

internal sealed class TeacherMaintenanceServiceConnection(WebSocket socket) : IAsyncDisposable
{
    private const int MaximumPendingRequests = 16;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(35);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _pending = new();
    private int _disposed;

    public async Task<string> SendRequestAsync(string signedRequestJson, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(TeacherMaintenanceServiceConnection));
        if (_pending.Count >= MaximumPendingRequests)
            throw new IOException("目标 Maintenance 服务的并发请求已达到上限。");
        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) throw new InvalidOperationException("Maintenance requestId 冲突。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            await SendAsync(new MaintenanceServiceChannelMessage(MaintenanceServiceChannelProtocol.CurrentVersion,
                    "request", RequestId: requestId, SignedRequestJson: signedRequestJson), timeout.Token)
                .ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("等待学生 Maintenance 服务响应超时。" ); }
        finally { _pending.TryRemove(requestId, out _); }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytes = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var message = MaintenanceServiceChannelProtocol.Deserialize(bytes);
                if (message.Type == "heartbeat") continue;
                if (message.Type != "response" || message.RequestId is not { } requestId ||
                    message.SignedResponseJson is null)
                    throw new InvalidDataException("学生 Maintenance 服务返回了非响应消息。");
                if (_pending.TryGetValue(requestId, out var completion))
                    completion.TrySetResult(message.SignedResponseJson);
            }
        }
        finally
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("学生 Maintenance 服务连接已关闭。"));
        }
    }

    private async Task SendAsync(MaintenanceServiceChannelMessage message,
        CancellationToken cancellationToken)
    {
        var bytes = MaintenanceServiceChannelProtocol.Serialize(message);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                cancellationToken).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    private async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("学生 Maintenance 服务已关闭连接。");
            if (result.MessageType != WebSocketMessageType.Text ||
                message.Length + result.Count > MaintenanceServiceChannelProtocol.MaximumMessageBytes)
                throw new InvalidDataException("Maintenance 通道响应帧类型或大小无效。");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return message.ToArray();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("学生 Maintenance 服务连接已释放。"));
        }
        return ValueTask.CompletedTask;
    }
}
