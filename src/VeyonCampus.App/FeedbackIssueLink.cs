using System.Diagnostics;

namespace VeyonCampus.App;

internal static class FeedbackIssueLink
{
    private const string Url = "https://github.com/Ljc798/veyon-campus-deployment/issues/new";
    public const string EmailAddress = "loujiachen6@gmail.com";
    public const string WeChatId = "ljc16607923402";

    public static void Open()
    {
        _ = Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
    }

    public static void OpenEmail()
    {
        _ = Process.Start(new ProcessStartInfo($"mailto:{EmailAddress}") { UseShellExecute = true });
    }
}
