using Avalonia;

namespace VeyonCampus.Companion;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var name = OperatingSystem.IsWindows()
            ? @"Local\VeyonCampus.StudentCompanion.SingleInstance"
            : "VeyonCampus.StudentCompanion.SingleInstance";
        using var instanceMutex = new Mutex(true, name, out var createdNew);
        if (!createdNew) return;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            instanceMutex.ReleaseMutex();
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
