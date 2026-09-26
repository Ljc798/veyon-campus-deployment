using System.Text;

namespace VeyonCampus.Core;

/// <summary>Runs fixed Windows management scripts without profiles or interactive prompts.</summary>
internal static class WindowsPowerShell
{
    public static ProcessOutcome Run(IProcessLauncher launcher, string script, TimeSpan timeout)
        => RunCore(launcher, script, timeout, null);

    public static ProcessOutcome RunWithStandardInput(IProcessLauncher launcher, string script,
        TimeSpan timeout, string standardInput)
    {
        if (launcher is not IStandardInputProcessLauncher inputLauncher)
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return ProcessOutcome.NeedsReviewResult(
                Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
                "当前进程执行器不支持安全标准输入；没有执行账户修改。");
        }
        return RunCore(launcher, script, timeout, standardInput);
    }

    private static ProcessOutcome RunCore(IProcessLauncher launcher, string script,
        TimeSpan timeout, string? standardInput)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var executable = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        // Scripts contain public names and queries, never passwords. Encoding
        // preserves Unicode; interpolated literals must still be quoted.
        var command = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
                      "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); " +
                      "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " +
                      "try { " + script + " } catch { exit 1 }";
        var arguments = new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) };
        return standardInput is null
            ? launcher.Run(executable, arguments, system, timeout)
            : ((IStandardInputProcessLauncher)launcher).RunWithStandardInput(
                executable, arguments, system, timeout, standardInput);
    }

    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
