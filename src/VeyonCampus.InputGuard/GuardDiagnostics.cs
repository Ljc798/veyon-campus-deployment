using System.ComponentModel;
using System.Text;

namespace VeyonCampus.InputGuard;

internal static class GuardDiagnostics
{
    internal static void Write(string stage, Exception exception)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "InputGuard");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "guard-server.log");
            if (File.Exists(path) && new FileInfo(path).Length >= 128 * 1024) File.WriteAllText(path, "");
            var code = exception is Win32Exception native ? $" Win32={native.NativeErrorCode}" : "";
            var detail = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
            if (detail.Length > 600) detail = detail[..600];
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O}\t{stage}\t{exception.GetType().Name}{code}: {detail}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
