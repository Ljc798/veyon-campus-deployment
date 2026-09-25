using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace VeyonCampus.Core;

/// <summary>
/// Read-only platform facts for the preflight report. Never modifies system state;
/// unknown items surface as Unknown, not guesses.
/// </summary>
public sealed record PlatformFacts(
    string OperatingSystemVersion,
    string SystemArchitecture,
    string ComputerName,
    bool IsWindows,
    bool? IsElevated,
    string ElevationDetail,
    string RebootDetail,
    string DiskDetail,
    string VeyonDetail)
{
    private const string RegistryTypeFullName = "Microsoft.Win32.Registry, Microsoft.Win32.Registry";

    /// <summary>Collects the read-only facts for <see cref="ReadOnlyPreflight"/> on the current platform.</summary>
    public static PlatformFacts Collect()
    {
        var isWindows = OperatingSystem.IsWindows();
        bool? isElevated = null;
        string elevation = "非 Windows 平台；管理员身份检查不适用。";
        string reboot = "非 Windows 平台；Windows 重启待办检查不适用。";
        string disk = "非 Windows 平台；系统盘空间检查不适用。";
        if (isWindows)
        {
            (isElevated, elevation) = QueryElevation();
            reboot = QueryRebootPending();
            disk = DescribeSystemDriveSpace();
        }
        var veyon = VeyonFacts.Probe();
        var veyonDetail = isWindows
            ? string.Join(" ", veyon.Detail, veyon.VersionDetail, veyon.ServiceDetail)
            : "非 Windows 平台；Veyon 状态检查不适用。";
        return new PlatformFacts(
            Environment.OSVersion.VersionString,
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.MachineName,
            isWindows,
            isElevated,
            elevation,
            reboot,
            disk,
            veyonDetail);
    }

    private static (bool? IsElevated, string Detail) QueryElevation()
    {
        if (!OperatingSystem.IsWindows())
            return (null, "非 Windows 平台；管理员身份检查不适用。");
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            var isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            return isElevated
                ? (true, "当前进程以管理员身份运行。")
                : (false, "当前进程未以管理员身份运行；本版本尚无 UAC 执行器，部署会被阻止。请以管理员身份重新启动 App。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return (null, $"管理员身份无法确认：{ex.Message}");
        }
    }

    /// <summary>
    /// Reads reboot-pending registry markers on Windows only. Registry access goes through
    /// reflection so the Core assembly loads unchanged on every platform (Windows registry
    /// APIs are absent on macOS/Linux runtimes).
    /// </summary>
    private static string QueryRebootPending()
    {
        if (!OperatingSystem.IsWindows())
            return "非 Windows 平台；Windows 重启待办检查不适用。";
        try
        {
            var registryType = Type.GetType(RegistryTypeFullName);
            if (registryType is null)
                return "重启待办无法确认：当前运行时缺少注册表 API。";
            var localMachine = registryType.GetField("LocalMachine", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (localMachine is null)
                return "重启待办无法确认：注册表 LocalMachine 基类缺失。";
            var keyType = localMachine.GetType();
            object? OpenSubKey(object owner, string name)
            {
                var matches = keyType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(x => x.Name == "OpenSubKey" && x.GetParameters().Length == 2 &&
                                x.GetParameters()[0].ParameterType == typeof(string) &&
                                x.GetParameters()[1].ParameterType == typeof(bool));
                return matches.FirstOrDefault()?.Invoke(owner, new object[] { name, false });
            }
            var pendingKey = OpenSubKey(localMachine,
                @"SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations");
            var hasPending = pendingKey is not null;
            if (!hasPending)
            {
                var controlKey = OpenSubKey(localMachine, @"SYSTEM\CurrentControlSet\Control");
                hasPending = controlKey is not null && controlKey.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(x => x.Name == "GetValue" && x.GetParameters().Length == 1 &&
                                          x.GetParameters()[0].ParameterType == typeof(string))
                    ?.Invoke(controlKey, new object[] { "RebootRequired" }) is not null;
            }
            return hasPending
                ? "注册表显示有重启待办（改名或更新将在重启后生效）。"
                : "注册表未发现重启待办标记。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TargetException)
        {
            return $"重启待办无法确认：{ex.InnerException?.Message ?? ex.Message}";
        }
    }

    private static string DescribeSystemDriveSpace()
    {
        try
        {
            var driveRoot = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\') + "\\";
            var drive = new DriveInfo(driveRoot);
            if (!drive.IsReady)
                return $"系统盘 {drive.Name} 未就绪；磁盘空间无法确认。";
            var freeGiB = Math.Round(drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0, 1);
            return $"系统盘 {drive.Name} 可用 {freeGiB} GiB。";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return $"系统盘空间读取失败：{ex.Message}";
        }
    }
}
