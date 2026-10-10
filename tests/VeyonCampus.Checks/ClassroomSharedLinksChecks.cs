using VeyonCampus.Companion;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class ClassroomSharedLinksChecks
{
    public static void Run()
    {
        Expect(ClassroomNoticeLinkParser.ExtractSingleHttpLink(
            "打开 https://example.edu/lesson. 下节课见。")?.AbsoluteUri == "https://example.edu/lesson");
        Expect(ClassroomNoticeLinkParser.ExtractSingleHttpLink("https://example.edu/path?q=1，")?.Query == "?q=1");
        Expect(ClassroomNoticeLinkParser.ExtractSingleHttpLink("https://example.edu/topic)")?.AbsolutePath == "/topic");
        Expect(ClassroomNoticeLinkParser.ExtractSingleHttpLink("http://[::1]")?.Host == "[::1]");
        Expect(ClassroomNoticeLinkParser.ExtractSingleHttpLink("javascript:alert(1)") is null);
        Expect(ClassroomNoticeLinkParser.ExtractSingleHttpLink(
            "https://teacher:secret@example.edu/") is null);
        Expect(ClassroomNoticeLinkParser.ExtractSingleHttpLink(
            "https://one.example https://two.example") is null);
        Expect(ClassroomNoticeLinkParser.ExtractSingleHttpLink("打开 example.edu") is null);

        var sessionId = Guid.NewGuid();
        var viewModel = new StudentCompanionViewModel();
        viewModel.ApplyStatus(ActiveClassroom(sessionId));
        var notice = CreateNotice(sessionId, "请在课前打开 https://example.edu/lesson。");
        viewModel.ApplyTeacherEvent(notice);
        Expect(viewModel.HasClassroomNoticeLink && viewModel.HelpStatus.Contains("课堂通知：", StringComparison.Ordinal));
        Expect(viewModel.GetActiveClassroomNoticeLink(DateTimeOffset.UtcNow)?.Host == "example.edu");

        viewModel.RefreshClassroomNoticeExpiry(notice.ExpiresUtc.AddMilliseconds(1));
        Expect(!viewModel.HasClassroomNoticeLink && !viewModel.HasHelpStatus &&
               viewModel.ClassroomNoticeExpiresUtc is null);

        var nextSessionId = Guid.NewGuid();
        viewModel.ApplyStatus(ActiveClassroom(nextSessionId));
        var nextNotice = CreateNotice(nextSessionId, "https://example.edu/new-class");
        viewModel.ApplyTeacherEvent(nextNotice);
        Expect(viewModel.HasClassroomNoticeLink);
        viewModel.ApplyStatus(ActiveClassroom(Guid.NewGuid()));
        Expect(!viewModel.HasClassroomNoticeLink && viewModel.ClassroomNoticeExpiresUtc is null);
    }

    private static StudentCompanionStatusSnapshot ActiveClassroom(Guid sessionId) =>
        new(StudentCompanionConnectionState.ClassroomActive, "LAB-01", 1, sessionId);

    private static ClassroomEvent CreateNotice(Guid sessionId, string message)
    {
        var now = DateTimeOffset.UtcNow;
        return new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, "demo", sessionId,
            Guid.NewGuid(), ClassroomEventCryptography.ClassroomNoticeTarget, ClassroomEventSender.Teacher,
            ClassroomEventType.ClassroomNotice, now, now.Add(ClassroomEventCryptography.MaximumEventLifetime),
            null, message, null);
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Classroom shared-link assertion failed.");
    }
}
