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
    public const string ServiceName = "VeyonService";

    public static bool IsSupportedVersionDetail(string? versionDetail)
    {
        const string prefix = "版本 ";
        const string suffix = "。";
        if (versionDetail is null || !versionDetail.StartsWith(prefix, StringComparison.Ordinal) ||
            !versionDetail.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        var actualText = versionDetail[prefix.Length..^suffix.Length];
        if (!Version.TryParse(actualText, out var actual) ||
            !Version.TryParse(VeyonInstallerTrust.Version, out var expected))
            return false;

        return actual.Major == expected.Major && actual.Minor == expected.Minor &&
               actual.Build == expected.Build &&
               (actual.Revision == expected.Revision || actual.Revision == -1 && expected.Revision == 0);
    }

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
        var (path, cliPath) = ProbeDefaultInstallPath();
        string service;
        string? cli = null;
        string? keyDir = null;
        if (cliPath is not null)
        {
            var version = DescribeVersion(cliPath);
            var hasService = ProbeServiceRegistered(ServiceName);
            if (hasService == true)
            {
                service = $"已检测到 {ServiceName} 服务；服务运行状态与启动类型待按所选 Veyon 版本核对，当前未猜测。";
                cli = "CLI 位置待按所选 Veyon 版本核对；当前未调用。";
                keyDir = Path.Combine(path, "keys");
            }
            else if (hasService == false)
            {
                service = $"已找到 Veyon 安装目录，但 {ServiceName} 服务未注册；可能是安装损坏或组件不完整。";
                cli = "由于安装状态不完整，CLI 状态未知。";
                return new VeyonFacts("unknown", $"检测到不完整的 Veyon 安装：{path}", version,
                    service, cli, null);
            }
            else
            {
                service = $"{ServiceName} 服务注册状态无法确认。";
                cli = "CLI 状态未知。";
                return new VeyonFacts("unknown", $"找到 Veyon 安装目录，但无法确认服务注册状态：{path}",
                    version, service, cli, null);
            }
            return new VeyonFacts("installed",
                $"检测到 Veyon 安装于默认路径：{path}", version, service, cli,
                keyDir is null ? null : Directory.Exists(keyDir) ? keyDir : null);
        }
        var servicePresent = ProbeServiceRegistered(ServiceName);
        if (servicePresent == true)
            return new VeyonFacts("installed",
                $"默认路径未找到 Veyon 安装目录，但已注册 {ServiceName} 服务；可能为非默认安装路径。",
                null, $"已检测到 {ServiceName} 服务；安装路径未确认。", null, null);
        if (servicePresent is null)
            return new VeyonFacts("unknown", "默认路径未检测到 Veyon；服务查询失败，不能判定未安装。",
                null, $"{ServiceName} 服务状态未知。", null, null);
        var missing = $"默认路径未检测到 Veyon（{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Veyon")}）。";
        return new VeyonFacts(NotInstalled, missing, null, $"{ServiceName} 服务未注册。", "尚未安装时此项不适用。", null);
    }

    private static (string Path, string? CliPath) ProbeDefaultInstallPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidateDirectory = Path.Combine(programFiles, "Veyon");
        var candidateCli = Path.Combine(candidateDirectory, "veyon-cli.exe");
        if (File.Exists(candidateCli))
            return (candidateDirectory, candidateCli);

        var legacyDirectory = Path.Combine(programFilesX86, "Veyon");
        var legacyCli = Path.Combine(legacyDirectory, "veyon-cli.exe");
        if (File.Exists(legacyCli))
            return (legacyDirectory, legacyCli);

        return (candidateDirectory, null);
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

    /// <summary>Returns whether the VeyonService is registered; null preserves query errors as unknown.</summary>
    private static bool? ProbeServiceRegistered(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var runner = new ProcessRunner();
            runner.Run("sc.exe", new[] { "query", serviceName }, @"C:\Windows\System32", TimeSpan.FromSeconds(10));
            if (runner.ExitCode == 0) return true;
            var output = runner.Stdout + " " + runner.Stderr;
            if (output.Contains("1060", StringComparison.Ordinal)) return false;
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or
                                   UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
