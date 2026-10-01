using System.Threading;
using Avalonia;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var role = typeof(App).Assembly.GetName().Name ?? "VeyonCampus";
        using var instanceMutex = new Mutex(true, $"Local\\{role}.SingleInstance", out var createdNew);
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

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
