using VeyonCampus.Core;

namespace VeyonCampus.Agent;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--website-policy-agent", var configPath])
        {
            if (!OperatingSystem.IsWindows()) return 2;
            try
            {
                WebsitePolicyAgent.RunAsync(configPath).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception exception)
            {
                WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath, exception);
                return 1;
            }
        }

        if (args is ["--cleanup-student-setup", var processIdText, var startTicksText, var bundleDirectory] &&
            int.TryParse(processIdText, out var processId) && long.TryParse(startTicksText, out var startTicks))
            return StudentSetupBundleCleanup.WaitAndRemove(processId, startTicks, bundleDirectory) ? 0 : 1;

        return 2;
    }
}
