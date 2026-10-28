using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VeyonCampus.Core;

/// <summary>Stable host identity used to reject InputGuard commands targeting the teacher itself.</summary>
internal static class StudentInputGuardMachineIdentity
{
    private const int MaximumMacIdentityOutputCharacters = 8192;
    private static readonly object MacIdentityGate = new();
    private static string? _macMachineId;

    public static string ReadLocalMachineId()
    {
        if (OperatingSystem.IsWindows()) return ReadWindowsMachineId();
        if (OperatingSystem.IsMacOS()) return ReadMacMachineId();
        throw new PlatformNotSupportedException("教师机身份只支持 Windows 和 macOS。");
    }

    /// <summary>Accepts exactly one nonempty UUID property from bounded IORegistry text.</summary>
    public static string ParseMacPlatformUuid(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length > MaximumMacIdentityOutputCharacters)
            throw new InvalidDataException("Mac 本机身份响应超过大小限制；拒绝 InputGuard 命令。");
        var matches = Regex.Matches(output,
            """^[ \t]*"IOPlatformUUID"[ \t]*=[ \t]*"([^"\r\n]+)"[ \t]*\r?$""",
            RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (matches.Count != 1 || !Guid.TryParseExact(matches[0].Groups[1].Value, "D", out var machineId) ||
            machineId == Guid.Empty)
            throw new InvalidDataException("Mac 本机身份缺失、重复或格式无效；拒绝 InputGuard 命令。");
        return machineId.ToString("N").ToUpperInvariant();
    }

    [SupportedOSPlatform("windows")]
    private static string ReadWindowsMachineId()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false)
                        ?? throw new InvalidDataException("Windows 本机身份注册表项不可读。");
        var value = key.GetValue("MachineGuid") as string;
        if (!Guid.TryParse(value, out var machineId) || machineId == Guid.Empty)
            throw new InvalidDataException("Windows MachineGuid 缺失或格式无效；拒绝 InputGuard 命令。");
        return machineId.ToString("N").ToUpperInvariant();
    }

    [SupportedOSPlatform("macos")]
    private static string ReadMacMachineId()
    {
        lock (MacIdentityGate)
        {
            if (_macMachineId is not null) return _macMachineId;
            var runner = new ProcessRunner();
            runner.Run("/usr/sbin/ioreg", ["-rd1", "-c", "IOPlatformExpertDevice"], "/",
                TimeSpan.FromSeconds(3), MaximumMacIdentityOutputCharacters);
            if (runner.ExitCode != 0 || runner.StdoutTruncated)
                throw new InvalidDataException("Mac 本机身份读取未完成；拒绝 InputGuard 命令。");
            _macMachineId = ParseMacPlatformUuid(runner.Stdout);
            return _macMachineId;
        }
    }
}
