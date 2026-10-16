using System.Diagnostics;

namespace VeyonCampus.App;

internal static class FeedbackIssueLink
{
    private const string GitHubIssuesUrl = "https://github.com/Ljc798/veyon-campus-deployment/issues/new";
    private const string GitCodeIssuesUrl = "https://gitcode.com/ljc_1126/veyon-campus-deployment/issues/new";
    public const string EmailAddress = "loujiachen6@gmail.com";
    public const string WeChatId = "ljc16607923402";

    public static void Open() => OpenGitHub();

    public static void OpenGitHub()
    {
        _ = Process.Start(new ProcessStartInfo(GitHubIssuesUrl) { UseShellExecute = true });
    }

    public static void OpenGitCode() =>
        _ = Process.Start(new ProcessStartInfo(GitCodeIssuesUrl) { UseShellExecute = true });

    public static void OpenEmail()
    {
        _ = Process.Start(new ProcessStartInfo($"mailto:{EmailAddress}") { UseShellExecute = true });
    }
}
