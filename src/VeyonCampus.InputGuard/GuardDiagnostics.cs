using System.ComponentModel;
using System.Text;

namespace VeyonCampus.InputGuard;

internal static class GuardDiagnostics
{
    internal static void Write(string stage, Exception exception)
    {
        var code = exception is Win32Exception native ? $" Win32={native.NativeErrorCode}" : "";
        Write(stage, $"{exception.GetType().Name}{code}: {exception.Message}");
    }

    /// <summary>
    /// Appends a plain line. Startup and shutdown records use this so the log shows whether the
    /// service mode was ever entered, which an exception-only log cannot answer.
    /// </summary>
    /// <remarks>
    /// The Guard can run under two very different tokens: the student (started by Companion) or
    /// SYSTEM (started by the Agent into the session). Their LocalAppData profiles are different, so
    /// a log written to the caller's LocalAppData disappears when the token changes. ProgramData is
    /// the shared, readable location; LocalAppData stays as the fallback for a locked-down machine.
    /// </remarks>
    internal static void Write(string stage, string detail)
    {
        detail = detail.Replace('\r', ' ').Replace('\n', ' ');
        if (detail.Length > 600) detail = detail[..600];
        var line = $"{DateTimeOffset.UtcNow:O}\t{stage}\t{detail}{Environment.NewLine}";
        foreach (var root in EnumerateRoots())
        {
            try
            {
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "guard-server.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 128 * 1024) File.WriteAllText(path, "");
                File.AppendAllText(path, line, Encoding.UTF8);
                return;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or
                                                ArgumentException or System.Security.SecurityException)
            {
                // Try the next location; diagnostics must never break the Guard.
            }
        }
    }

    private static IEnumerable<string> EnumerateRoots()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(common))
            yield return Path.Combine(common, "VeyonCampus", "InputGuard");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            yield return Path.Combine(local, "VeyonCampus", "InputGuard");
    }
}
