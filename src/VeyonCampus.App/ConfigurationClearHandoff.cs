using System.Diagnostics;

namespace VeyonCampus.App;

internal static class ConfigurationClearHandoff
{
    public static void Start(string role)
    {
        if (role is not ("StudentSetup" or "TeacherConsole"))
            throw new ArgumentException("Veyon Campus 程序角色无效。", nameof(role));
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("无法确认当前程序路径。");
        using var current = Process.GetCurrentProcess();
        var startedUtcTicks = current.StartTime.ToUniversalTime().Ticks;
        var startInfo = new ProcessStartInfo(executable)
        {
            Arguments = $"--clear-product-configuration {role} {current.Id} {startedUtcTicks}",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        using var helper = Process.Start(startInfo)
                           ?? throw new InvalidOperationException("Windows 没有启动管理员清理进程。");
    }
}
