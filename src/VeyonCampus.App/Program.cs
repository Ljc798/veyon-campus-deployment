using System.Threading;
using System.Runtime.InteropServices;
using Avalonia;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if STUDENT_SETUP_APP
        if (args.Length >= 2 && args[0] == "--uninstall-student-runtime")
        {
            Environment.ExitCode = args.Length == 2
                ? RunStudentUninstall(args[1], preserveConfiguration: false)
                : args.Length == 3 && args[2] == "--preserve-config"
                    ? RunStudentUninstall(args[1], preserveConfiguration: true)
                    : 2;
            return;
        }
        if (args is ["--clear-product-configuration", "StudentSetup", var parentPid, var parentStartTicks])
        {
            Environment.ExitCode = RunConfigurationClear("StudentSetup", parentPid, parentStartTicks);
            return;
        }
#endif
#if TEACHER_CONSOLE
        if (args.Length >= 2 && args[0] == "--uninstall-teacher-runtime")
        {
            Environment.ExitCode = args.Length == 2
                ? RunTeacherUninstall(args[1], preserveConfiguration: false)
                : args.Length == 3 && args[2] == "--preserve-config"
                    ? RunTeacherUninstall(args[1], preserveConfiguration: true)
                    : 2;
            return;
        }
        if (args is ["--clear-product-configuration", "TeacherConsole", var parentPid, var parentStartTicks])
        {
            Environment.ExitCode = RunConfigurationClear("TeacherConsole", parentPid, parentStartTicks);
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
    private static int RunStudentUninstall(string diagnosticPath, bool preserveConfiguration)
    {
        try
        {
            var fullPath = Path.GetFullPath(diagnosticPath);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
                           Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) return 2;

            var result = StudentSetupUninstaller.Run(preserveConfiguration);
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

#if TEACHER_CONSOLE
    private static int RunTeacherUninstall(string diagnosticPath, bool preserveConfiguration)
    {
        try
        {
            var fullPath = Path.GetFullPath(diagnosticPath);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
                           Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) return 2;

            var result = TeacherConsoleUninstaller.Run(preserveConfiguration);
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

    private static int RunConfigurationClear(string role, string parentProcessIdText, string parentStartTicksText)
    {
        try
        {
            if (!int.TryParse(parentProcessIdText, out var parentProcessId) ||
                !long.TryParse(parentStartTicksText, out var parentStartTicks))
                throw new ArgumentException("待关闭的控制台进程标识无效。");
            var result = VeyonCampusConfigurationReset.ClearInstalledConfiguration(role,
                parentProcessId, parentStartTicks);
            ShowNativeMessageBox(result, "本机配置已清空", 0x00000040 | 0x00000000);
            return 0;
        }
        catch (Exception exception)
        {
            ShowNativeMessageBox("配置清理未完成。" + Environment.NewLine + Environment.NewLine + exception.Message,
                "清理失败", 0x00000010 | 0x00000000);
            return 1;
        }
    }

    private static void ShowNativeMessageBox(string text, string caption, uint type)
    {
        if (OperatingSystem.IsWindows()) _ = MessageBoxW(IntPtr.Zero, text, caption, type);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW", SetLastError = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
