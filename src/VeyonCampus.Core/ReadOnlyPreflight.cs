using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VeyonCampus.Core;

public enum CheckLevel { Pass, Warning, Blocked, Unknown, NotApplicable }
public sealed record PreflightCheck(string Id, CheckLevel Level, string Detail);
public sealed record PreflightReport(DateTimeOffset CheckedAt, string PlanSha256, string? PackageSha256,
    IReadOnlyList<PreflightCheck> Checks, AccountExecutionSnapshot? Accounts = null)
{
    public bool HasBlocker => Checks.Any(c => c.Level == CheckLevel.Blocked);
}

/// <summary>Only reads local state. This is an early report, not permission to modify Windows.</summary>
public static class ReadOnlyPreflight
{
    public static PreflightReport Check(PlanInput input, string? availableInstallerPath = null)
    {
        DeploymentPlan.Create(input);
        var packageSha256 = input.Operations.InstallVeyon ? Hash(new {
            input.Package!.Root, input.Package.ConfigSha256, input.Package.PublicKeySha256,
            input.Package.InstallerSha256, input.Package.PublicKeyFingerprint
        }) : null;
        var checks = new List<PreflightCheck>();
        var accountResult = WindowsAccountAdapter.CheckSelectedAccounts(input);
        checks.AddRange(accountResult.Checks);
        var planSha256 = Hash(new {
            input.Campus, input.Prefix, input.Number, input.StudentAccountName, input.AdminAccountName,
            input.Operations, Accounts = accountResult.Snapshot, PackageSha256 = packageSha256
        });
        if (!OperatingSystem.IsWindows())
        {
            checks.Add(new("platform", CheckLevel.Blocked, "当前不是 Windows；可以预览计划，但不能执行部署。"));
            checks.Add(new("windows-system", CheckLevel.NotApplicable, "Windows 系统、权限和服务检查在当前平台不适用；未采集或推断这些状态。"));
            checks.Add(new("domain-membership", CheckLevel.NotApplicable, "域或工作组状态仅在 Windows 上检查；当前未采集或推断。"));
            checks.Add(new("restore-environment", CheckLevel.NotApplicable, "还原保护检查仅在 Windows 上检查；当前未采集或推断。"));
            return new PreflightReport(DateTimeOffset.UtcNow, planSha256, packageSha256, checks, accountResult.Snapshot);
        }

        var facts = PlatformFacts.Collect();
        var os = Environment.OSVersion.Version;
        var major = os.Build >= 22000 ? "Windows 11" : "Windows 10";
        checks.Add(os.Build >= 10240
            ? new("os", CheckLevel.Pass, $"检测到 {major}（{facts.OperatingSystemVersion}，系统构建 {os.Build}）；具体兼容性仍需实机验收。")
            : new("os", CheckLevel.Blocked, $"系统构建 {os.Build} 不在当前计划的 Windows 10/11 范围内。"));
        checks.Add(RuntimeInformation.OSArchitecture == Architecture.X64
            ? new("architecture", CheckLevel.Pass, $"检测到 Windows {facts.SystemArchitecture}。")
            : new("architecture", CheckLevel.Blocked, $"当前架构为 {facts.SystemArchitecture}；本阶段仅计划支持 x64。"));
        checks.Add(new("computer", CheckLevel.Pass, $"当前计算机名：{facts.ComputerName}"));
        checks.Add(EvaluateWorkerAvailability(VeyonCampusRole.StudentSetup, facts.ElevationDetail));
        var domainMembership = facts.DomainMembership ?? new DomainMembershipFacts(null, "域或工作组状态未知；需要现场核对。");
        checks.Add(new("domain-membership", domainMembership.IsDomainJoined switch
        {
            true => CheckLevel.Warning,
            false => CheckLevel.Pass,
            null => CheckLevel.Unknown
        }, domainMembership.Detail));
        var restoreEnvironment = facts.RestoreEnvironment ??
            new RestoreEnvironmentFacts(RestoreEnvironmentEvidence.Unknown, "还原环境未知；需要现场核对。");
        checks.Add(new("restore-environment", restoreEnvironment.Evidence switch
        {
            RestoreEnvironmentEvidence.Possible or RestoreEnvironmentEvidence.NoEvidence => CheckLevel.Warning,
            _ => CheckLevel.Unknown
        }, restoreEnvironment.Detail));
        checks.Add(facts.RebootDetail.StartsWith("注册表未发现重启待办标记", StringComparison.Ordinal)
            ? new("reboot", CheckLevel.Pass, facts.RebootDetail)
            : facts.RebootDetail.StartsWith("注册表显示有重启待办", StringComparison.Ordinal)
                ? new("reboot", CheckLevel.Warning, facts.RebootDetail)
                : new("reboot", CheckLevel.Unknown, facts.RebootDetail));
        checks.Add(facts.DiskDetail.Contains("无法确认") || facts.DiskDetail.Contains("未就绪") || facts.DiskDetail.Contains("读取失败")
            ? new("disk", CheckLevel.Unknown, facts.DiskDetail)
            : new("disk", CheckLevel.Pass, facts.DiskDetail));
        checks.Add(facts.HasInteractiveVeyonProcess switch
        {
            false => new PreflightCheck("processes", CheckLevel.Pass, facts.ProcessDetail),
            true => new PreflightCheck("processes", CheckLevel.Warning, facts.ProcessDetail),
            null => new PreflightCheck("processes", CheckLevel.Unknown, facts.ProcessDetail)
        });
        var veyon = facts.Veyon ?? VeyonFacts.Probe();
        checks.Add(veyon.Status == VeyonFacts.NotInstalled
            ? new("veyon", CheckLevel.Warning,
                veyon.AsText() + $" App 内嵌固定版本 Veyon {VeyonInstallerTrust.Version} 安装资源，可离线安装。")
            : veyon.Status == "installed" && VeyonFacts.IsSupportedVersionDetail(veyon.VersionDetail)
                ? new("veyon", CheckLevel.Pass,
                    $"已检测到兼容的 Veyon {VeyonInstallerTrust.Version}。{veyon.AsText()}")
                : new("veyon", CheckLevel.Unknown,
                    veyon.Status == "installed"
                        ? $"检测到 Veyon，但版本或安装完整性无法确认：{veyon.AsText()}"
                        : veyon.AsText()));
        if (input.Operations.InstallVeyon)
        {
            input.Package!.VerifyUnchanged();
            checks.Add(veyon.Status == VeyonFacts.NotInstalled
                ? new("veyon-version", CheckLevel.Pass, $"未检测到已有 Veyon；安装计划固定使用版本 {VeyonInstallerTrust.Version}。")
                : veyon.Status == "installed" && VeyonFacts.IsSupportedVersionDetail(veyon.VersionDetail)
                    ? new("veyon-version", CheckLevel.Pass, $"已安装版本与固定基线 Veyon {VeyonInstallerTrust.Version} 一致。")
                    : new("veyon-version", CheckLevel.Blocked,
                        $"无法确认当前 Veyon 与固定基线 {VeyonInstallerTrust.Version} 一致；为避免覆盖未知或较新版本，已阻止安装/配置。{veyon.VersionDetail}") );
            checks.Add(new("public-key", CheckLevel.Pass,
                $"已重新读取部署包和 RSA 公钥，指纹 {input.Package.PublicKeyFingerprint[..12]}…，资料摘要 {input.Package.PackageFingerprint[..12]}…"));
            var installerPath = availableInstallerPath ?? input.Package.InstallerPath;
            if (installerPath is null)
                checks.Add(new("installer", CheckLevel.Blocked, "未能定位 App 内嵌或旧包中的 Veyon 安装资源；不能安装。"));
            else
            {
                checks.Add(new("installer", CheckLevel.Pass, "已定位固定版本 Veyon 安装资源；正在核对大小、SHA-256 和签名。"));
                var trust = VeyonInstallerTrust.Check(installerPath);
                checks.Add(new("installer-trust", trust.IsAllowed ? CheckLevel.Pass : CheckLevel.Blocked,
                    trust.Detail));
            }
        }
        if (input.Operations.RenameComputer)
            checks.Add(domainMembership.IsDomainJoined switch
            {
                true => new("rename", CheckLevel.Blocked,
                    "目标名称已通过规则检查，但本机已加入域；当前版本不执行域设备改名。"),
                false => new("rename", CheckLevel.Unknown,
                    "已确认本机处于工作组环境；待生效名称及名称冲突仍需改名前复核。"),
                _ => new("rename", CheckLevel.Blocked,
                    "无法确认本机域或工作组状态；为避免套用错误流程，当前计划阻止改名及组合操作。")
            });
        return new PreflightReport(DateTimeOffset.UtcNow, planSha256, packageSha256, checks, accountResult.Snapshot);
    }

    public static PreflightCheck EvaluatePrivilege(bool? isElevated, string detail) =>
        new("privilege", isElevated switch
        {
            true => CheckLevel.Pass,
            false => CheckLevel.Blocked,
            null => CheckLevel.Unknown
        }, detail);

    private static PreflightCheck EvaluateWorkerAvailability(VeyonCampusRole role, string elevationDetail)
    {
        try
        {
            var caller = WorkerInstallationGuard.CaptureCurrentUiIdentity(role);
            var installation = WorkerInstallationGuard.ValidateCurrentUi(role, caller);
            return new("privilege", CheckLevel.Pass,
                $"部署权限和安装文件已验证。{elevationDetail}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or ArgumentException)
        {
            return new("privilege", CheckLevel.Blocked,
                "当前安装无法安全启动受限提权 Worker；请从匹配版本的 Program Files 安装目录运行并修复安装。" + exception.Message);
        }
    }

    public static bool IsExecutable(PreflightReport? report) => report is not null && !report.HasBlocker &&
        report.Checks.Any(check => check.Id == "privilege" && check.Level == CheckLevel.Pass);

    public static bool IsCurrent(PreflightReport? report, PlanInput? checkedInput, PlanInput currentInput,
        DateTimeOffset nowUtc) => IsExecutable(report) && checkedInput == currentInput && report is not null &&
        nowUtc >= report.CheckedAt && nowUtc - report.CheckedAt <= TimeSpan.FromMinutes(5);

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
