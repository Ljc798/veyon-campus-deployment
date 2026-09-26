using Avalonia;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--website-policy-agent", var configPath])
        {
            if (OperatingSystem.IsWindows())
            {
                try { WebsitePolicyAgent.RunAsync(configPath).GetAwaiter().GetResult(); }
                catch (Exception exception)
                {
                    WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath, exception.GetType().Name);
                }
            }
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
