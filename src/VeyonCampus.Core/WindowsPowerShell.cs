using System.Text;

namespace VeyonCampus.Core;

/// <summary>Runs fixed Windows management scripts without profiles or interactive prompts.</summary>
internal static class WindowsPowerShell
{
    public static ProcessOutcome Run(IProcessLauncher launcher, string script, TimeSpan timeout)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var executable = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        // Scripts contain public names and queries, never passwords. Encoding
        // preserves Unicode; interpolated literals must still be quoted.
        var command = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
                      "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " +
                      "try { " + script + " } catch { exit 1 }";
        return launcher.Run(executable,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(command))], system, timeout);
    }

    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
