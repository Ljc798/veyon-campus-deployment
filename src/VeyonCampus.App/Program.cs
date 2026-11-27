using System.Threading;
using System.Runtime.InteropServices;
using Avalonia;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal static class Program
{
    internal static EventWaitHandle? ActivationSignal { get; private set; }

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
        if (args.Length >= 1 && args[0] == "--enable-local-webapi")
        {
            Environment.ExitCode = RunEnableLocalWebApi(args.Length >= 2 ? args[1] : null);
            return;
        }
#endif
        var role = typeof(App).Assembly.GetName().Name ?? "VeyonCampus";
        EventWaitHandle? activationSignal = null;
        try
        {
            activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset,
                $"Local\\{role}.ActivateWindow");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         PlatformNotSupportedException)
        {
            // The console can still start if Windows does not allow the optional
            // second-launch activation signal to be created.
        }

        using var activationSignalLifetime = activationSignal;
        using var instanceMutex = new Mutex(true, $"Local\\{role}.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            try { activationSignal?.Set(); }
            catch (ObjectDisposedException) { }
            return;
        }

        ActivationSignal = activationSignal;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            ActivationSignal = null;
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

#if TEACHER_CONSOLE
    /// <summary>
    /// Headless branch behind the screen wall's "启用本机 WebAPI" action and the installer's post-install
    /// step. It runs before the single-instance mutex so an elevated copy never fights the running window.
    /// </summary>
    private static int RunEnableLocalWebApi(string? portText)
    {
        var port = int.TryParse(portText, out var parsed) ? parsed : VeyonLocalWebApi.DefaultPort;
        try
        {
            var outcome = VeyonLocalWebApi.EnsureEnabled(port);
            WriteLocalWebApiDiagnostic(outcome.Detail);
            return outcome.Succeeded ? 0 : 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
                                          UnauthorizedAccessException or PlatformNotSupportedException or
                                          System.ComponentModel.Win32Exception)
        {
            WriteLocalWebApiDiagnostic("启用本机 WebAPI 失败：" + exception.Message);
            return 1;
        }
    }

    private static void WriteLocalWebApiDiagnostic(string detail)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "local-webapi.log"),
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + " " + detail + Environment.NewLine,
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          NotSupportedException or ArgumentException)
        {
            // Diagnostics are best-effort; the exit code is the contract.
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
