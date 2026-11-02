using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using VeyonCampus.App;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class StudentAgentChannelChecks
{
    public static async Task RunAsync()
    {
        await CheckConcurrentRequestsAsync();
        await CheckLateResponseAfterTimeoutAsync();
        CheckAdmissionAfterClassEnds();
        await CheckRejectedChannelCloseIsAwaitedAsync();
        await CheckCampusIsolationAndReconnectAsync();
        await CheckRolledBackAgentReconnectAsync();
        CheckTeacherEndpointReconnectConfiguration();
        CheckWebSocketSubProtocolNegotiation();
        Console.WriteLine("PASS Agent channel concurrency, late replies, post-class admission, campus isolation, close handling, and endpoint refresh");
    }

    private static async Task CheckConcurrentRequestsAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var busyResponses = new List<StudentAgentChannelMessage>();
        await using var dispatcher = new StudentAgentChannelRequestDispatcher(async (_, token) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, current);
            if (current == StudentAgentChannelRequestDispatcher.MaximumConcurrentRequests)
                allStarted.TrySetResult();
            try { await release.Task.WaitAsync(token).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref active); }
        }, (message, _) =>
        {
            lock (busyResponses) busyResponses.Add(message);
            return Task.CompletedTask;
        });

        var dispatches = Enumerable.Range(0, StudentAgentChannelRequestDispatcher.MaximumConcurrentRequests + 4)
            .Select(index => dispatcher.DispatchAsync(new StudentAgentChannelMessage(
                StudentAgentChannelProtocol.CurrentVersion, "http-request", RequestId: Guid.NewGuid(),
                Method: "GET", PathAndQuery: $"/v1/test/{index}"), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(dispatches).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Expect(maximumActive == StudentAgentChannelRequestDispatcher.MaximumConcurrentRequests,
            "同一 Agent 上的独立 HTTP 请求未并行执行或超过并发上限。");
        lock (busyResponses)
            Expect(busyResponses.Count == 4 && busyResponses.All(message => message.StatusCode == 503),
                "达到并发上限后没有返回有界的 503 回执。");
        release.TrySetResult();
        await dispatcher.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private static async Task CheckLateResponseAfterTimeoutAsync()
    {
        var socket = new FixtureWebSocket();
        await using var connection = new TeacherAgentChannelConnection(socket);
        using var connectionLifetime = new CancellationTokenSource();
        var run = connection.RunAsync(connectionLifetime.Token);

        using var timedRequest = CreateRequest();
        using var requestTimeout = new CancellationTokenSource();
        var timedTask = connection.SendHttpAsync(CreateChannelRequest(timedRequest), requestTimeout.Token);
        var sentRequest = await socket.ReadSentAsync("http-request").WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        requestTimeout.Cancel();
        await ExpectCanceledAsync(timedTask).ConfigureAwait(false);

        await socket.EnqueueAsync(new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion,
            "http-response", RequestId: sentRequest.RequestId, StatusCode: 200,
            Body: "late response"u8.ToArray())).ConfigureAwait(false);

        using var nextRequest = CreateRequest();
        var nextTask = connection.SendHttpAsync(CreateChannelRequest(nextRequest), connectionLifetime.Token);
        var nextSent = await socket.ReadSentAsync("http-request").WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        await socket.EnqueueAsync(new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion,
            "http-response", RequestId: nextSent.RequestId, StatusCode: 200,
            Body: "next response"u8.ToArray())).ConfigureAwait(false);
        var nextResponse = await nextTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Expect(nextResponse.StatusCode == 200 && Encoding.UTF8.GetString(nextResponse.Body!) == "next response",
            "迟到回执后新 HTTP 请求没有继续工作。");

        await socket.EnqueueAsync(new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion,
            "http-response", RequestId: Guid.NewGuid(), StatusCode: 200)).ConfigureAwait(false);
        var rejectedUnknownResponse = false;
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (InvalidDataException) { rejectedUnknownResponse = true; }
        Expect(rejectedUnknownResponse, "未知请求编号的回执没有关闭连接。");
    }

    private static void CheckAdmissionAfterClassEnds()
    {
        Expect(StudentAgentChannelAdmissionPolicy.CanAdmitPinnedAgent("campus-a", "campus-a", "pc-01"),
            "课堂结束后，当前校区已固定身份的 Agent 不能建立基础连接。");
        Expect(!StudentAgentChannelAdmissionPolicy.CanAdmitPinnedAgent("campus-a", "campus-b", "pc-01"),
            "另一个校区的 Agent 被错误接受。");
        Expect(!StudentAgentChannelAdmissionPolicy.CanAdmitPinnedAgent("campus-a", "campus-a", " bad target "),
            "格式无效的设备目标被接受。");
    }

    private static async Task CheckRejectedChannelCloseIsAwaitedAsync()
    {
        var socket = new FixtureWebSocket { CloseGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously) };
        var close = TeacherMobileControlService.CloseRejectedAgentChannelAsync(socket, "untrusted agent",
            CancellationToken.None);
        await socket.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Expect(!close.IsCompleted, "拒绝握手时没有等待 WebSocket Close 帧发送完成。");
        socket.CloseGate.TrySetResult();
        await close.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Expect(socket.State == WebSocketState.Closed, "拒绝握手后 WebSocket 未关闭。");
    }

    private static async Task CheckCampusIsolationAndReconnectAsync()
    {
        var registry = new StudentAgentChannelRegistry((_, _) => { }, (_, _) => false);
        static Func<StudentAgentChannelMessage, CancellationToken, Task<StudentAgentChannelMessage>> Respond(
            string campus) => (_, _) => Task.FromResult(new StudentAgentChannelMessage(
                StudentAgentChannelProtocol.CurrentVersion, "response", CampusId: campus));

        using var campusA = registry.Register("campus-a", "pc-01", Respond("campus-a"));
        using var campusB = registry.Register("campus-b", "PC-01", Respond("campus-b"));
        Expect(registry.IsConnected("campus-a", "PC-01") && registry.IsConnected("campus-b", "pc-01"),
            "不同校区相同电脑名/IP 的连接发生覆盖。");
        var aResponse = await registry.SendAsync("campus-a", "pc-01",
            new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion, "test"))
            .ConfigureAwait(false);
        var bResponse = await registry.SendAsync("campus-b", "PC-01",
            new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion, "test"))
            .ConfigureAwait(false);
        Expect(aResponse.CampusId == "campus-a" && bResponse.CampusId == "campus-b",
            "相同电脑名/IP 的请求跨校区路由。");

        using var replacement = registry.Register("campus-a", "pc-01", Respond("replacement"));
        campusA.Dispose();
        Expect(registry.IsConnected("campus-a", "pc-01") && registry.IsConnected("campus-b", "pc-01"),
            "旧 WebSocket 断开时误删了重连或另一个校区的连接。");
        var replacementResponse = await registry.SendAsync("campus-a", "pc-01",
            new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion, "test"))
            .ConfigureAwait(false);
        Expect(replacementResponse.CampusId == "replacement" &&
               registry.HasConnectedBefore("campus-a", "pc-01") &&
               registry.HasConnectedBefore("campus-b", "pc-01"),
            "断开/重连交替后注册表或历史记录丢失校区边界。");
    }

    private static async Task CheckRolledBackAgentReconnectAsync()
    {
        var registry = new StudentAgentChannelRegistry((_, _) => { }, (_, _) => true);
        static Task<StudentAgentChannelMessage> Respond(string agentBuild) =>
            Task.FromResult(new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion,
                "response", CampusId: "campus-a", Target: agentBuild));

        var oldAgentBeforeUpdate = registry.Register("campus-a", "pc-rollback",
            (_, _) => Respond("agent-0.4.56"));
        var failedNewAgent = registry.Register("campus-a", "pc-rollback",
            (_, _) => Respond("agent-0.4.57-candidate"));
        oldAgentBeforeUpdate.Dispose();
        failedNewAgent.Dispose();

        using var restoredOldAgent = registry.Register("campus-a", "pc-rollback",
            (_, _) => Respond("agent-0.4.56-restored"));
        Expect(StudentAgentChannelAdmissionPolicy.CanAdmitPinnedAgent("campus-a", "campus-a", "pc-rollback") &&
               registry.HasConnectedBefore("campus-a", "pc-rollback") &&
               registry.IsConnected("campus-a", "pc-rollback"),
            "升级回滚后的旧 Agent 被历史连接记录阻止重新注册或接入。");
        var response = await registry.SendAsync("campus-a", "pc-rollback",
            new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion, "test"))
            .ConfigureAwait(false);
        Expect(response.Target == "agent-0.4.56-restored",
            "升级回滚后教师端仍将请求发送给已退出的新 Agent 会话。");
    }

    private static void CheckTeacherEndpointReconnectConfiguration()
    {
        using var teacherKey = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=Veyon Campus Mobile Control CA", teacherKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(2));
        var rootBase64 = Convert.ToBase64String(root.Export(X509ContentType.Cert));

        using var campusSigningKey = RSA.Create(2048);
        var publicKey = campusSigningKey.ExportSubjectPublicKeyInfoPem();
        var original = new WebsitePolicyAgentConfig("campus-a", publicKey,
            TeacherChannelEndpoints: [ClassroomEventCryptography.CreateTeacherEndpoint(
                System.Net.IPAddress.Parse("192.168.1.10"))],
            TeacherChannelRootCertificateBase64: rootBase64);
        var updated = original with
        {
            TeacherChannelEndpoints = [ClassroomEventCryptography.CreateTeacherEndpoint(
                System.Net.IPAddress.Parse("192.168.1.11"))]
        };
        var refreshed = StudentAgentTeacherChannelClient.ValidateReconnectConfiguration(original, updated);
        Expect(refreshed.TeacherChannelEndpoints?.Single() ==
               ClassroomEventCryptography.CreateTeacherEndpoint(System.Net.IPAddress.Parse("192.168.1.11")),
            "重连循环没有接受经相同校区身份和固定 TLS 根证书验证的更新教师地址。");
        var identityChangeRejected = false;
        try
        {
            _ = StudentAgentTeacherChannelClient.ValidateReconnectConfiguration(original,
                updated with { CampusId = "campus-b" });
        }
        catch (InvalidDataException) { identityChangeRejected = true; }
        Expect(identityChangeRejected, "重连配置改变了已固定的校区身份。");
    }

    private static void CheckWebSocketSubProtocolNegotiation()
    {
        Expect(StudentAgentChannelProtocol.SelectWebSocketSubProtocol(
                   ["veyon-campus-agent-channel.v2", StudentAgentChannelProtocol.WebSocketSubProtocolV1]) ==
               StudentAgentChannelProtocol.WebSocketSubProtocolV1,
            "当前教师端没有选择与旧 Agent 兼容的 v1 子协议。");
        Expect(StudentAgentChannelProtocol.SelectWebSocketSubProtocol(["veyon-campus-agent-channel.v2"]) is null,
            "没有共同能力的通道版本被错误降级接受。");
        Expect(StudentAgentChannelProtocol.SelectWebSocketSubProtocol(Array.Empty<string>()) is null &&
               StudentAgentChannelProtocol.IsCompatibleNegotiatedSubProtocol(null) &&
               StudentAgentChannelProtocol.IsCompatibleNegotiatedSubProtocol(
                   StudentAgentChannelProtocol.WebSocketSubProtocolV1) &&
               !StudentAgentChannelProtocol.IsCompatibleNegotiatedSubProtocol("veyon-campus-agent-channel.v2"),
            "不支持旧版无子协议握手或错误接受未知子协议。");
    }

    private static StudentAgentChannelMessage CreateChannelRequest(HttpRequestMessage request) =>
        new(StudentAgentChannelProtocol.CurrentVersion, "http-request", RequestId: Guid.NewGuid(),
            Method: request.Method.Method, PathAndQuery: request.RequestUri!.PathAndQuery);

    private static HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, "http://127.0.0.1:17400/v1/test");

    private static async Task ExpectCanceledAsync(Task<StudentAgentChannelMessage> task)
    {
        try
        {
            await task.ConfigureAwait(false);
            throw new InvalidDataException("请求超时后仍返回成功。");
        }
        catch (OperationCanceledException) { }
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (observed >= value || Interlocked.CompareExchange(ref maximum, value, observed) == observed) return;
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class FixtureWebSocket : WebSocket
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<StudentAgentChannelMessage> _sent =
            Channel.CreateUnbounded<StudentAgentChannelMessage>();
        private WebSocketState _state = WebSocketState.Open;
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? CloseGate { get; init; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public async Task EnqueueAsync(StudentAgentChannelMessage message) =>
            await _incoming.Writer.WriteAsync(StudentAgentChannelProtocol.Serialize(message)).ConfigureAwait(false);

        public async Task<StudentAgentChannelMessage> ReadSentAsync(string type)
        {
            while (true)
            {
                var message = await _sent.Reader.ReadAsync().ConfigureAwait(false);
                if (message.Type == type) return message;
            }
        }

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _incoming.Writer.TryComplete();
        }

        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            CloseStarted.TrySetResult();
            if (CloseGate is not null) await CloseGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
            _sent.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            var bytes = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > buffer.Count) throw new InvalidDataException("Fixture WebSocket 帧过大。");
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            if (messageType != WebSocketMessageType.Text || !endOfMessage)
                throw new InvalidDataException("Fixture WebSocket 只接受完整文本帧。");
            var message = StudentAgentChannelProtocol.Deserialize(buffer.AsSpan());
            return _sent.Writer.WriteAsync(message, cancellationToken).AsTask();
        }
    }
}
