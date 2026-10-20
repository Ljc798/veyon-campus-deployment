using System.Security.Cryptography;
using VeyonCampus.App;
using VeyonCampus.Companion;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class ClassroomChatChecks
{
    private const string CampusId = "campus-demo";
    private const string Target = "PC-08";

    public static void Run()
    {
        CheckMessageProtocol();
        CheckTeacherTranscriptBuffer();
        CheckTeacherConversationPresentation();
        CheckStudentConversationLifecycle();
    }

    private static void CheckMessageProtocol()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var conversationId = ClassroomEventCryptography.CreateConversationId(sessionId, Target);
        Expect(conversationId == ClassroomEventCryptography.CreateConversationId(sessionId, "pc-08") &&
               conversationId != ClassroomEventCryptography.CreateConversationId(sessionId, "PC-09") &&
               conversationId != ClassroomEventCryptography.CreateConversationId(Guid.NewGuid(), Target));
        Expect(ClassroomEventCryptography.NormalizeChatMessage("  第一行\r\n第二行  ") == "第一行\n第二行");
        Expect(ClassroomEventCryptography.IsValidChatMessage(new string('x', 500)) &&
               ClassroomEventCryptography.IsValidChatMessage("第一行\n第二行") &&
               !ClassroomEventCryptography.IsValidChatMessage(" \r\n ") &&
               !ClassroomEventCryptography.IsValidChatMessage(new string('x', 501)) &&
               !ClassroomEventCryptography.IsValidChatMessage("a\tb"));

        using var teacherKey = RSA.Create(2048);
        using var agentKey = RSA.Create(2048);
        foreach (var (sender, key) in new[]
                 {
                     (ClassroomEventSender.Teacher, teacherKey),
                     (ClassroomEventSender.Student, agentKey)
                 })
        {
            var message = CreateEvent(sessionId, Target, sender, ClassroomEventType.ClassroomChatMessage,
                "第一行\n第二行", Guid.NewGuid(), now);
            var signed = ClassroomEventCryptography.SignEvent(message, key);
            var pem = key.ExportSubjectPublicKeyInfoPem();
            var verified = ClassroomEventCryptography.VerifyEvent(signed, CampusId, sessionId, Target, sender,
                pem, now.AddSeconds(1));
            Expect(verified.Event == message && verified.MatchesPinnedKey);
            Reject(() => ClassroomEventCryptography.VerifyEvent(signed, CampusId, sessionId, "PC-09", sender,
                pem, now.AddSeconds(1)));
            Reject(() => ClassroomEventCryptography.VerifyEvent(signed, CampusId, Guid.NewGuid(), Target, sender,
                pem, now.AddSeconds(1)));
            Reject(() => ClassroomEventCryptography.ValidateEvent(message with
                {
                    CorrelationId = Guid.NewGuid()
                }, CampusId, sessionId, Target, sender, now.AddSeconds(1)));
            Reject(() => ClassroomEventCryptography.SignEvent(message with { Message = "bad\u0001text" }, key));
        }
    }

    private static void CheckTeacherTranscriptBuffer()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var buffer = new ClassroomChatTranscriptBuffer();
        var teacherMessage = CreateEvent(sessionId, Target, ClassroomEventSender.Teacher,
            ClassroomEventType.ClassroomChatMessage, "老师消息", Guid.NewGuid(), now);
        buffer.AppendMessage(teacherMessage);
        var first = buffer.WaitForChangesAsync(sessionId, Target, 0, TimeSpan.FromSeconds(1),
            CancellationToken.None).GetAwaiter().GetResult();
        Expect(first.Revision == 1 && first.Messages.Count == 1 &&
               first.Messages[0] is { Sender: "teacher", Message: "老师消息", Delivered: false });

        buffer.AppendMessage(teacherMessage);
        var duplicate = buffer.WaitForChangesAsync(sessionId, Target, first.Revision,
            TimeSpan.FromMilliseconds(10), CancellationToken.None).GetAwaiter().GetResult();
        Expect(duplicate.Revision == first.Revision && duplicate.Messages.Count == 1);
        Reject(() => buffer.AppendMessage(teacherMessage with { Message = "被篡改" }));

        var studentMessage = CreateEvent(sessionId, Target, ClassroomEventSender.Student,
            ClassroomEventType.ClassroomChatMessage, "学生回复", Guid.NewGuid(), now.AddSeconds(1));
        var changeWaiter = buffer.WaitForChangesAsync(sessionId, Target, first.Revision,
            TimeSpan.FromSeconds(1), CancellationToken.None);
        buffer.AppendMessage(studentMessage);
        var second = changeWaiter.GetAwaiter().GetResult();
        Expect(second.Revision == 2 && second.Messages.Count == 2 &&
               second.Messages[1] is { Sender: "student", Message: "学生回复", Delivered: false });

        buffer.MarkDelivered(sessionId, Target, studentMessage.EventId);
        var unchanged = buffer.WaitForChangesAsync(sessionId, Target, second.Revision,
            TimeSpan.FromMilliseconds(10), CancellationToken.None).GetAwaiter().GetResult();
        Expect(unchanged.Revision == second.Revision && !unchanged.Messages[1].Delivered);
        buffer.MarkDelivered(sessionId, Target, teacherMessage.EventId);
        var delivered = buffer.WaitForChangesAsync(sessionId, Target, second.Revision,
            TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult();
        Expect(delivered.Revision == 3 && delivered.Messages[0].Delivered && !delivered.Messages[1].Delivered);

        var otherTarget = CreateEvent(sessionId, "PC-09", ClassroomEventSender.Student,
            ClassroomEventType.ClassroomChatMessage, "另一台电脑", Guid.NewGuid(), now);
        buffer.AppendMessage(otherTarget);
        var isolated = buffer.WaitForChangesAsync(sessionId, "PC-09", 0, TimeSpan.FromSeconds(1),
            CancellationToken.None).GetAwaiter().GetResult();
        Expect(isolated.Messages.Count == 1 && isolated.Messages[0].Message == "另一台电脑");
        var otherSession = buffer.WaitForChangesAsync(Guid.NewGuid(), Target, 0, TimeSpan.FromMilliseconds(10),
            CancellationToken.None).GetAwaiter().GetResult();
        Expect(otherSession.Messages.Count == 0);

        buffer.ClearTargets(sessionId, new HashSet<string>([Target], StringComparer.OrdinalIgnoreCase));
        var removedTarget = buffer.WaitForChangesAsync(sessionId, "PC-09", 0, TimeSpan.FromMilliseconds(10),
            CancellationToken.None).GetAwaiter().GetResult();
        Expect(removedTarget.Messages.Count == 0);
        buffer.ClearSession(sessionId);
        var clearedSession = buffer.WaitForChangesAsync(sessionId, Target, 0, TimeSpan.FromMilliseconds(10),
            CancellationToken.None).GetAwaiter().GetResult();
        Expect(clearedSession.Messages.Count == 0);

        var bounded = new ClassroomChatTranscriptBuffer();
        for (var index = 0; index < ClassroomChatTranscriptBuffer.MaximumMessagesPerConversation + 1; index++)
            bounded.AppendMessage(CreateEvent(sessionId, Target, ClassroomEventSender.Student,
                ClassroomEventType.ClassroomChatMessage, $"消息 {index}", Guid.NewGuid(), now.AddTicks(index)));
        var retained = bounded.WaitForChangesAsync(sessionId, Target, 0, TimeSpan.FromSeconds(1),
            CancellationToken.None).GetAwaiter().GetResult();
        Expect(retained.Messages.Count == ClassroomChatTranscriptBuffer.MaximumMessagesPerConversation &&
               retained.Messages[0].Message == "消息 1" && retained.Messages[^1].Message == "消息 200");
    }

    private static void CheckTeacherConversationPresentation()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var thread = new TeacherClassroomChatThread(Target, "陈小明", "第 1 排 · 第 2 列", isSupported: true);
        var incoming = CreateEvent(sessionId, Target, ClassroomEventSender.Student,
            ClassroomEventType.ClassroomChatMessage, "老师好", Guid.NewGuid(), now);
        thread.AddMessage(incoming, isIncoming: true);
        thread.AddMessage(incoming, isIncoming: true);
        Expect(thread.Title == "陈小明" && thread.Messages.Count == 1 && thread.UnreadCount == 1 &&
               thread.Messages[0].Speaker == "陈小明" && thread.DisplayTitle == "陈小明 · 1 条新消息");
        thread.UpdateDisplay("李小明", "PC-08 · 第 1 排 · 第 2 列", isSupported: true);
        Expect(thread.Messages[0].Speaker == "李小明");
        thread.MarkRead();
        Expect(thread.UnreadCount == 0 && thread.DisplayTitle == "李小明");

        var outgoing = CreateEvent(sessionId, Target, ClassroomEventSender.Teacher,
            ClassroomEventType.ClassroomChatMessage, "请看黑板", Guid.NewGuid(), now);
        thread.AddMessage(outgoing, isIncoming: false);
        var teacherItem = thread.Messages.Single(item => item.EventId == outgoing.EventId);
        Expect(teacherItem.Speaker == "老师" && teacherItem.HasDeliveryStatus &&
               teacherItem.DeliveryStatus == "等待学生端接收" && !teacherItem.CanRetry);
        thread.RefreshDeliveryStatuses(now.AddMinutes(3));
        Expect(teacherItem.CanRetry && teacherItem.DeliveryStatus == "未收到回执");
        Expect(thread.MarkRetrySubmitted(outgoing.EventId, now.AddMinutes(5)) && !teacherItem.CanRetry &&
               teacherItem.DeliveryStatus == "等待学生端接收");
        thread.MarkDelivered(outgoing.EventId);
        Expect(!teacherItem.CanRetry && teacherItem.DeliveryStatus == "学生端已收到");
    }

    private static void CheckStudentConversationLifecycle()
    {
        var sessionId = Guid.NewGuid();
        var calls = new List<(Guid SessionId, Guid EventId, string Message)>();
        var viewModel = new StudentCompanionViewModel(sendChatMessage: (requestedSession, eventId, message, _) =>
        {
            calls.Add((requestedSession, eventId, message));
            if (calls.Count is 1 or 3) throw new IOException("synthetic offline");
            return Task.FromResult(new StudentAgentClassroomEventSubmission(true, requestedSession, eventId,
                DateTimeOffset.UtcNow, ClassroomEventCryptography.CreateConversationId(requestedSession, Target)));
        });
        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.ClassroomActive,
            "测试机房", 1, sessionId));
        viewModel.ChatDraft = "  你好\r\n请回复。  ";
        Expect(viewModel.CanSendChatMessage);
        viewModel.SendClassroomChatMessageAsync().GetAwaiter().GetResult();
        Expect(calls.Count == 1 && calls[0].SessionId == sessionId &&
               calls[0].Message == "你好\n请回复。" && viewModel.CommunicationItems.Count == 1 &&
               viewModel.CommunicationItems[0].CanRetry &&
               viewModel.CommunicationItems[0].DeliveryStatus == "发送失败" &&
               viewModel.CommunicationStatus.Contains("消息未送出", StringComparison.Ordinal));

        var outgoing = viewModel.CommunicationItems[0];
        viewModel.ChatDraft = "新草稿";
        viewModel.RetryClassroomChatMessageAsync(outgoing.EventId).GetAwaiter().GetResult();
        Expect(calls.Count == 2 && calls[1] == calls[0] && !outgoing.CanRetry &&
               outgoing.DeliveryStatus == "已提交给教师端" && viewModel.ChatDraft == "新草稿");

        viewModel.SendClassroomChatMessageAsync().GetAwaiter().GetResult();
        var nextOutgoing = viewModel.CommunicationItems[1];
        Expect(calls.Count == 3 && calls[2].Message == "新草稿" && nextOutgoing.CanRetry &&
               nextOutgoing.DeliveryStatus == "发送失败");
        viewModel.RetryClassroomChatMessageAsync(nextOutgoing.EventId).GetAwaiter().GetResult();
        Expect(calls.Count == 4 && calls[3] == calls[2] && !nextOutgoing.CanRetry &&
               nextOutgoing.DeliveryStatus == "已提交给教师端" && viewModel.ChatDraft.Length == 0);

        var teacherMessage = CreateEvent(sessionId, Target, ClassroomEventSender.Teacher,
            ClassroomEventType.ClassroomChatMessage, "收到，谢谢。", Guid.NewGuid(), DateTimeOffset.UtcNow);
        Expect(viewModel.ApplyTeacherEvent(teacherMessage) && !viewModel.ApplyTeacherEvent(teacherMessage) &&
               viewModel.CommunicationItems.Count == 3 && viewModel.CommunicationItems[2].Speaker == "老师");

        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.ClassroomActive,
            "测试机房", 1, Guid.NewGuid()));
        Expect(viewModel.CommunicationItems.Count == 0 && viewModel.ChatDraft.Length == 0);
        Expect(!viewModel.ApplyTeacherEvent(teacherMessage));
        viewModel.ChatDraft = "新课堂消息";
        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ConnectedWithoutClass));
        Expect(viewModel.CommunicationItems.Count == 0 && viewModel.ChatDraft.Length == 0 &&
               !viewModel.CanSendChatMessage);
    }

    private static ClassroomEvent CreateEvent(Guid sessionId, string target, ClassroomEventSender sender,
        ClassroomEventType type, string? message, Guid eventId, DateTimeOffset issuedUtc)
    {
        var correlationId = type == ClassroomEventType.ClassroomChatMessage
            ? ClassroomEventCryptography.CreateConversationId(sessionId, target)
            : eventId;
        return new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, CampusId, sessionId, eventId,
            target, sender, type, issuedUtc.ToUniversalTime(), issuedUtc.ToUniversalTime().AddMinutes(2), null,
            message, correlationId);
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Classroom chat assertion failed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Classroom chat accepted invalid input.");
    }
}
