using System.Collections.Concurrent;
using System.Net.WebSockets;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal sealed class TeacherAgentChannelConnection : IAsyncDisposable
{
    private const int MaximumRetiredRequestIds = 256;
    private static readonly TimeSpan RetiredRequestLifetime = TimeSpan.FromMinutes(2);
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _requestLimit = new(16, 16);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<StudentAgentChannelMessage>> _pending = new();
    private readonly object _retiredGate = new();
    private readonly Queue<Guid> _retiredOrder = new();
    private readonly Dictionary<Guid, DateTimeOffset> _retiredRequestIds = [];
    private DateTimeOffset _lastHeartbeatUtc = DateTimeOffset.UtcNow;
    private int _disposed;

    public TeacherAgentChannelConnection(WebSocket socket) =>
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));

    public async Task<StudentAgentChannelMessage> SendHttpAsync(StudentAgentChannelMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Type != "http-request" || request.RequestId is not { } requestId)
            throw new InvalidDataException("教师转发请求无效。");
        await _requestLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new TaskCompletionSource<StudentAgentChannelMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(requestId, pending))
                throw new InvalidOperationException("教师连接请求 ID 重复。");
            var sent = false;
            try
            {
                await SendAsync(request, cancellationToken).ConfigureAwait(false);
                sent = true;
                return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (sent && !pending.Task.IsCompleted) RememberRetiredRequest(requestId);
                _pending.TryRemove(requestId, out _);
            }
        }
        finally { _requestLimit.Release(); }
    }

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var receive = ReceiveLoopAsync(lifetime.Token);
        var heartbeatWatch = WatchHeartbeatAsync(lifetime);
        var completed = await Task.WhenAny(receive, heartbeatWatch).ConfigureAwait(false);
        lifetime.Cancel();
        if (completed == heartbeatWatch && !requestAborted.IsCancellationRequested)
            _socket.Abort();
        try { await completed.ConfigureAwait(false); }
        finally
        {
            try { await Task.WhenAll(receive, heartbeatWatch).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var message = StudentAgentChannelProtocol.Deserialize(
                await ReceiveMessageAsync(_socket, cancellationToken).ConfigureAwait(false));
            if (message.Type == "heartbeat")
            {
                _lastHeartbeatUtc = DateTimeOffset.UtcNow;
                await SendAsync(new StudentAgentChannelMessage(
                    StudentAgentChannelProtocol.CurrentVersion, "heartbeat-ack"), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            if (message.Type == "http-response" && message.RequestId is { } requestId &&
                requestId != Guid.Empty && message.StatusCode is >= 100 and <= 599 &&
                message.Body is not { Length: > StudentAgentChannelProtocol.MaximumResponseBodyBytes })
            {
                if (_pending.TryGetValue(requestId, out var pending))
                {
                    pending.TrySetResult(message);
                    continue;
                }
                if (ConsumeRetiredRequest(requestId)) continue;
            }
            throw new InvalidDataException("学生 Agent 发送了未知或无对应请求的连接消息。");
        }
    }

    private void RememberRetiredRequest(Guid requestId)
    {
        lock (_retiredGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneRetiredRequests(now);
            if (_retiredRequestIds.ContainsKey(requestId)) return;
            while (_retiredRequestIds.Count >= MaximumRetiredRequestIds && _retiredOrder.TryDequeue(out var oldest))
                _retiredRequestIds.Remove(oldest);
            _retiredRequestIds[requestId] = now + RetiredRequestLifetime;
            _retiredOrder.Enqueue(requestId);
            if (_retiredOrder.Count > MaximumRetiredRequestIds * 2)
            {
                var retained = _retiredOrder.Where(_retiredRequestIds.ContainsKey).ToArray();
                _retiredOrder.Clear();
                foreach (var retainedId in retained) _retiredOrder.Enqueue(retainedId);
            }
        }
    }

    private bool ConsumeRetiredRequest(Guid requestId)
    {
        lock (_retiredGate)
        {
            PruneRetiredRequests(DateTimeOffset.UtcNow);
            return _retiredRequestIds.Remove(requestId);
        }
    }

    private void PruneRetiredRequests(DateTimeOffset now)
    {
        while (_retiredOrder.TryPeek(out var requestId))
        {
            if (!_retiredRequestIds.TryGetValue(requestId, out var expiresUtc))
            {
                _retiredOrder.Dequeue();
                continue;
            }
            if (expiresUtc > now) break;
            _retiredOrder.Dequeue();
            _retiredRequestIds.Remove(requestId);
        }
    }

    private async Task WatchHeartbeatAsync(CancellationTokenSource lifetime)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow - _lastHeartbeatUtc <= TimeSpan.FromSeconds(65)) continue;
            throw new WebSocketException("学生 Agent 心跳超时；已将电脑标记为离线。");
        }
    }

    private async Task SendAsync(StudentAgentChannelMessage message, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0 || _socket.State != WebSocketState.Open)
            throw new HttpRequestException("学生 Agent 连接已断开。");
        var bytes = StudentAgentChannelProtocol.Serialize(message);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException exception)
        {
            throw new HttpRequestException("学生 Agent 连接已断开。", exception);
        }
        finally { _sendGate.Release(); }
    }

    private static async Task<byte[]> ReceiveMessageAsync(WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("学生 Agent 已关闭连接。");
            if (result.MessageType != WebSocketMessageType.Text ||
                message.Length + result.Count > StudentAgentChannelProtocol.MaximumMessageBytes)
                throw new InvalidDataException("学生 Agent 连接帧类型或大小无效。");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return message.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var pending in _pending.Values)
            pending.TrySetException(new HttpRequestException("学生 Agent 已离线。"));
        _pending.Clear();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed",
                    CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException) { }
        _sendGate.Dispose();
        _requestLimit.Dispose();
    }
}
