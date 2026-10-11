using System.Threading;
using Avalonia;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if STUDENT_SETUP_APP
        if (args is ["--uninstall-student-runtime", var diagnosticPath])
        {
            Environment.ExitCode = RunStudentUninstall(diagnosticPath);
            return;
        }
#endif
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

#if STUDENT_SETUP_APP
    private static int RunStudentUninstall(string diagnosticPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(diagnosticPath);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
                           Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) return 2;

            var result = StudentSetupUninstaller.Run();
            File.WriteAllText(fullPath, result.Detail, new System.Text.UTF8Encoding(false));
            return result.Status == ExecutionPlan.Succeeded ? 0 : 1;
        }
        catch (Exception exception)
        {
            try
            {
                var fullPath = Path.GetFullPath(diagnosticPath);
                var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
                               Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(fullPath, exception.Message, new System.Text.UTF8Encoding(false));
            }
            catch (Exception) { }
            return 1;
        }
    }
#endif

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
