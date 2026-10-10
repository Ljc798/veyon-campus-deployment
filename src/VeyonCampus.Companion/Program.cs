using Avalonia;
using System.Runtime.InteropServices;

namespace VeyonCampus.Companion;

internal static class Program
{
    private const string ActivationEventName = @"Local\VeyonCampus.StudentCompanion.Activate";
    internal static EventWaitHandle? ActivationEvent { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
            _ = RegisterApplicationRestart("--startup", 0);

        var name = OperatingSystem.IsWindows()
            ? @"Local\VeyonCampus.StudentCompanion.SingleInstance"
            : "VeyonCampus.StudentCompanion.SingleInstance";
        using var activationEvent = OperatingSystem.IsWindows()
            ? new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName)
            : null;
        using var instanceMutex = new Mutex(true, name, out var createdNew);
        if (!createdNew)
        {
            try { activationEvent?.Set(); }
            catch (ObjectDisposedException) { }
            return;
        }

        ActivationEvent = activationEvent;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            ActivationEvent = null;
            instanceMutex.ReleaseMutex();
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterApplicationRestart")]
    private static extern int RegisterApplicationRestart(string commandLine, uint flags);
}
