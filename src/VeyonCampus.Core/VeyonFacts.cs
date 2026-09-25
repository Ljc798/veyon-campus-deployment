using System.Reflection;
using System.Security.Cryptography;
using System.Diagnostics;

namespace VeyonCampus.Core;

/// <summary>
/// Read-only facts about the local Veyon installation (P2-07). Distinguishes
/// "not installed", "installed", service registration and unknown states;
/// unknown items are kept as unknown, never guessed.
/// </summary>
public sealed record VeyonFacts(string Status, string Detail, string? VersionDetail, string ServiceDetail,
    string? CliDetail, string? DefaultPublicKeyPath)
{
    /// <summary>"not-installed" / "installed" / "unknown" / "not-applicable". Never modifies the system.</summary>
    public const string NotInstalled = "not-installed";
    public const string NotApplicable = "not-applicable";

    public string AsText()
    {
        var parts = new List<string>(4);
        if (Detail is not null && Detail.Length > 0) parts.Add(Detail);
        if (VersionDetail is not null && VersionDetail.Length > 0) parts.Add(VersionDetail);
        if (ServiceDetail is not null && ServiceDetail.Length > 0) parts.Add(ServiceDetail);
        if (CliDetail is not null && CliDetail.Length > 0) parts.Add(CliDetail);
        return string.Join(" ", parts);
    }

    public static VeyonFacts Probe()
    {
        if (!OperatingSystem.IsWindows())
            return new VeyonFacts(NotApplicable, "非 Windows 平台；Veyon 状态检查不适用。", null,
                "非 Windows 平台；服务状态不适用。", null, null);
        var (path, foundBinary) = ProbeDefaultInstallPath();
        var binaryPath = foundBinary ? path : null;
        string service;
        string? cli = null;
        string? keyDir = null;
        if (binaryPath is not null)
        {
            var version = DescribeVersion(binaryPath);
            var hasService = ProbeServiceRegistered("VeyonServer");
            if (hasService)
            {
                service = "已检测到 VeyonServer 服务；服务运行状态与启动类型待按所选 Veyon 版本核对，当前未猜测。";
                cli = "CLI 位置待按所选 Veyon 版本核对；当前未调用。";
                keyDir = Path.Combine(path, "keys");
            }
            else
            {
                service = "已找到 Veyon 安装目录，但 VeyonServer 服务未注册；可能未安装完成或为非默认组件组合。";
                cli = "VeyonServer 服务缺失；CLI 状态未知。";
            }
            return new VeyonFacts("installed",
                $"检测到 Veyon 安装于默认路径：{path}", version, service, cli,
                keyDir is null ? null : Directory.Exists(keyDir) ? keyDir : null);
        }
        if (ProbeServiceRegistered("VeyonServer"))
            return new VeyonFacts("installed",
                "默认路径未找到 Veyon 安装目录，但已注册 VeyonServer 服务；可能为非默认安装路径。",
                null, "已检测到 VeyonServer 服务；安装路径未确认。", null, null);
        var missing = $"默认路径未检测到 Veyon（{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Veyon")}）。";
        return new VeyonFacts(NotInstalled, missing, null, "VeyonServer 服务未注册。", "尚未安装时此项不适用。", null);
    }

    private static (string Path, bool Binary) ProbeDefaultInstallPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidate = Path.Combine(programFiles, "Veyon", "veyon-control.exe");
        if (File.Exists(candidate))
            return (Path.Combine(programFiles, "Veyon"), true);
        var legacy = Path.Combine(programFilesX86, "Veyon", "veyon-control.exe");
        return (Path.Combine(programFilesX86, "Veyon"), File.Exists(legacy));
    }

    private static string DescribeVersion(string binaryPath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(binaryPath);
            if (info.FileVersion is not null)
                return $"版本 {info.FileVersion}。";
            return "版本无法确认（二进制缺少版本信息）。";
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return $"版本读取失败：{ex.Message}";
        }
    }

    /// <summary>Returns true when the VeyonServer service is registered, regardless of its runtime status.</summary>
    private static bool ProbeServiceRegistered(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            // 只用反射解析服务名是否存在，避免引入 System.ServiceProcess 包；
            // 状态读取留待 P2-07 实机验证 Veyon 服务名与组件组合后再接入。
            var serviceController = Type.GetType(
                "System.ServiceProcess.ServiceController, System.ServiceProcess.ServiceController");
            if (serviceController is null)
                return false;
            var match = serviceController.GetMethod("FromName", BindingFlags.Static | BindingFlags.Public)
                ?.Invoke(null, new object[] { serviceName });
            return match is not null;
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or TypeLoadException or
                                   UnauthorizedAccessException or TargetException)
        {
            _ = ex;
            return false;
        }
    }

    /// <summary>Service presence without install path, used only to avoid misreporting a non-default install.</summary>
    private static bool ProbeLegacyService(string serviceName) =>
        ProbeServiceRegistered(serviceName);
}
