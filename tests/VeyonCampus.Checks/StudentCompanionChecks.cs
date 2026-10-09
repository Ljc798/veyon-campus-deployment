using System.Security.Cryptography;
using VeyonCampus.Companion;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class StudentCompanionChecks
{
    public static void Run()
    {
        var viewModel = new StudentCompanionViewModel();
        Expect(viewModel.StatusTitle == "等待教师端连接");
        Expect(viewModel.ClassroomSummary == "未连接");
        Expect(!viewModel.HasClassroomSummary);

        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ConnectedWithoutClass));
        Expect(viewModel.StatusTitle == "当前没有进行中的课堂");
        Expect(viewModel.ClassroomSummary == "教师端已连接");
        Expect(viewModel.HasClassroomSummary);

        var sessionId = Guid.NewGuid();
        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "LAB-01", 1, sessionId));
        Expect(viewModel.StatusTitle == "课堂进行中");
        Expect(viewModel.ClassroomSummary == "LAB-01 · 1 台电脑");
        Expect(viewModel.HasClassroomSummary);

        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "机房 A", 150, sessionId, ClassroomMode.Practice));
        Expect(viewModel.ClassroomSummary == "机房 A · 150 台电脑 · 练习");

        Reject(() => viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "LAB-01", 151)));
        Reject(() => viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, " LAB-01", 1)));
        Reject(() => viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "LAB\n01", 1)));
        Reject(() => viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.Disconnected, TargetCount: 1)));
        Reject(() => viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            (StudentCompanionConnectionState)42)));

        CheckSingleActionHelpFlow();

        CheckSignedLocalSnapshots();
    }

    private static void CheckSingleActionHelpFlow()
    {
        var sessionId = Guid.NewGuid();
        var helpEventId = Guid.NewGuid();
        var resolvedEventId = Guid.NewGuid();
        var resolveCalls = 0;
        var viewModel = new StudentCompanionViewModel(
            (requestedSession, _) =>
            {
                Expect(requestedSession == sessionId);
                return Task.FromResult(new StudentAgentClassroomEventSubmission(true, sessionId, helpEventId,
                    DateTimeOffset.UtcNow));
            },
            (requestedSession, correlatedHelpId, _) =>
            {
                resolveCalls++;
                Expect(requestedSession == sessionId && correlatedHelpId == helpEventId);
                return Task.FromResult(new StudentAgentClassroomEventSubmission(true, sessionId, resolvedEventId,
                    DateTimeOffset.UtcNow, helpEventId));
            });
        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "LAB-01", 1, sessionId));
        Expect(viewModel.CanRequestHelp && viewModel.HasHelpAction && viewModel.HelpActionText == "需要老师帮助");
        viewModel.RequestHelpAsync().GetAwaiter().GetResult();
        Expect(!viewModel.CanRequestHelp && !viewModel.HasHelpAction &&
               viewModel.HelpStatus == "求助已发送给老师。");

        viewModel.ApplyTeacherEvent(CreateTeacherEvent(sessionId, ClassroomEventType.HelpAcknowledged,
            helpEventId, null));
        Expect(viewModel.HelpStatus == "老师已看到你的求助。" && !viewModel.CanRequestHelp);
        viewModel.ApplyTeacherEvent(CreateTeacherEvent(sessionId, ClassroomEventType.TeacherReply,
            helpEventId, "我马上来看。"));
        Expect(viewModel.HelpStatus == "老师回复：我马上来看。" && !viewModel.CanRequestHelp &&
               viewModel.CanResolveHelp && viewModel.HasHelpAction && viewModel.HelpActionText == "标记已解决");

        var notice = CreateTeacherEvent(sessionId, ClassroomEventType.ClassroomNotice, Guid.Empty,
            "请准备下课。") with { Target = ClassroomEventCryptography.ClassroomNoticeTarget };
        viewModel.ApplyTeacherEvent(notice);
        viewModel.ApplyTeacherEvent(notice);
        Expect(viewModel.HelpStatus == "老师回复：我马上来看。\n课堂通知：请准备下课。" &&
               viewModel.CanResolveHelp);

        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.Disconnected));
        Expect(!viewModel.CanActivateHelpAction &&
               viewModel.HelpStatus == "老师回复：我马上来看。\n课堂通知：请准备下课。");
        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "LAB-01", 1, sessionId));
        Expect(viewModel.CanResolveHelp);
        viewModel.ActivateHelpActionAsync().GetAwaiter().GetResult();
        Expect(resolveCalls == 1 && viewModel.CanRequestHelp && !viewModel.CanResolveHelp &&
               viewModel.HelpStatus == "已标记为解决。\n课堂通知：请准备下课。" &&
               viewModel.HelpActionText == "需要老师帮助");

        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "LAB-01", 1, Guid.NewGuid()));
        Expect(viewModel.HelpStatus.Length == 0 && viewModel.CanRequestHelp);
        viewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ConnectedWithoutClass));
        Expect(!viewModel.CanRequestHelp && viewModel.HelpStatus.Length == 0);

        var quickReplyEventId = Guid.NewGuid();
        StudentCompanionViewModel? fastReplyViewModel = null;
        fastReplyViewModel = new StudentCompanionViewModel((requestedSession, _) =>
        {
            fastReplyViewModel!.ApplyTeacherEvent(CreateTeacherEvent(requestedSession,
                ClassroomEventType.TeacherReply, quickReplyEventId, "我马上来看。"));
            return Task.FromResult(new StudentAgentClassroomEventSubmission(true, requestedSession,
                quickReplyEventId, DateTimeOffset.UtcNow));
        }, (requestedSession, helpRequestId, _) => Task.FromResult(
            new StudentAgentClassroomEventSubmission(true, requestedSession, Guid.NewGuid(),
                DateTimeOffset.UtcNow, helpRequestId)));
        fastReplyViewModel.ApplyStatus(new StudentCompanionStatusSnapshot(
            StudentCompanionConnectionState.ClassroomActive, "LAB-01", 1, sessionId));
        fastReplyViewModel.RequestHelpAsync().GetAwaiter().GetResult();
        Expect(fastReplyViewModel.CanResolveHelp && fastReplyViewModel.HelpStatus == "老师回复：我马上来看。");
    }

    private static ClassroomEvent CreateTeacherEvent(Guid sessionId, ClassroomEventType type,
        Guid correlationId, string? message)
    {
        var now = DateTimeOffset.UtcNow;
        return new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo", sessionId,
            Guid.NewGuid(), "STUDENT-PC-01", ClassroomEventSender.Teacher, type, now,
            now.Add(ClassroomEventCryptography.MaximumEventLifetime), null, message,
            correlationId == Guid.Empty ? null : correlationId);
    }

    private static void CheckSignedLocalSnapshots()
    {
        using var agentKey = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        string Sign(ClassroomStatusSnapshot snapshot) => StudentAgentResponseCryptography.Sign(snapshot, agentKey);

        var active = new ClassroomStatusSnapshot(true, Guid.NewGuid(), "机房 A", 24,
            now, now.AddMinutes(1));
        var mappedActive = StudentCompanionStatusPoller.MapSignedSnapshot(Sign(active), now.AddSeconds(1));
        Expect(mappedActive.ConnectionState == StudentCompanionConnectionState.ClassroomActive &&
               mappedActive.RoomName == "机房 A" && mappedActive.TargetCount == 24 &&
               mappedActive.SessionId == active.SessionId);

        var practice = active with { Mode = ClassroomMode.Practice };
        var mappedPractice = StudentCompanionStatusPoller.MapSignedSnapshot(Sign(practice), now.AddSeconds(1));
        Expect(mappedPractice.Mode == ClassroomMode.Practice);

        var noClass = new ClassroomStatusSnapshot(true, ReceivedUtc: now, ExpiresUtc: now.AddMinutes(1));
        Expect(StudentCompanionStatusPoller.MapSignedSnapshot(Sign(noClass), now.AddSeconds(1)).ConnectionState ==
               StudentCompanionConnectionState.ConnectedWithoutClass);

        var expired = active with { ExpiresUtc = now.AddSeconds(1) };
        Expect(StudentCompanionStatusPoller.MapSignedSnapshot(Sign(expired), now.AddSeconds(2)).ConnectionState ==
               StudentCompanionConnectionState.Disconnected);

        Reject(() => StudentCompanionStatusPoller.MapSignedSnapshot(Sign(
            new ClassroomStatusSnapshot(false, Guid.NewGuid())), now));
        Reject(() => StudentCompanionStatusPoller.MapSignedSnapshot(Sign(active with { TargetCount = 151 }), now));
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Student Companion status assertion failed.");
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException("Student Companion accepted an invalid status snapshot.");
    }
}
