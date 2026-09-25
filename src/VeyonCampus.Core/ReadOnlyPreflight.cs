using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VeyonCampus.Core;

public enum CheckLevel { Pass, Warning, Blocked, Unknown, NotApplicable }
public sealed record PreflightCheck(string Id, CheckLevel Level, string Detail);
public sealed record PreflightReport(DateTimeOffset CheckedAt, string PlanSha256, string? PackageSha256,
    IReadOnlyList<PreflightCheck> Checks)
{
    public bool HasBlocker => Checks.Any(c => c.Level == CheckLevel.Blocked);
}

/// <summary>Only reads local state. This is an early report, not permission to modify Windows.</summary>
public static class ReadOnlyPreflight
{
    public static PreflightReport Check(PlanInput input)
    {
        DeploymentPlan.Create(input);
        var packageSha256 = input.Operations.InstallVeyon ? Hash(new {
            input.Package!.Root, input.Package.ConfigSha256, input.Package.PublicKeySha256,
            input.Package.InstallerSha256, input.Package.PublicKeyFingerprint
        }) : null;
        var planSha256 = Hash(new {
            input.Campus, input.Prefix, input.Number, input.StudentAccountName, input.AdminAccountName,
            input.Operations, PackageSha256 = packageSha256
        });
        var checks = new List<PreflightCheck>();
        if (!OperatingSystem.IsWindows())
        {
            checks.Add(new("platform", CheckLevel.Blocked, "当前不是 Windows；可以预览计划，但不能执行部署。"));
            checks.Add(new("windows-system", CheckLevel.NotApplicable, "Windows 系统、权限和服务检查在当前平台不适用；未采集或推断这些状态。"));
            return new PreflightReport(DateTimeOffset.UtcNow, planSha256, packageSha256, checks);
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
        checks.Add(new("privilege", facts.ElevationDetail.StartsWith("当前进程以管理员身份运行", StringComparison.Ordinal)
            ? CheckLevel.Pass
            : CheckLevel.Unknown, facts.ElevationDetail));
        checks.Add(facts.RebootDetail.StartsWith("注册表未发现重启待办标记", StringComparison.Ordinal)
            ? new("reboot", CheckLevel.Pass, facts.RebootDetail)
            : facts.RebootDetail.StartsWith("注册表显示有重启待办", StringComparison.Ordinal)
                ? new("reboot", CheckLevel.Warning, facts.RebootDetail)
                : new("reboot", CheckLevel.Unknown, facts.RebootDetail));
        checks.Add(facts.DiskDetail.Contains("无法确认") || facts.DiskDetail.Contains("未就绪") || facts.DiskDetail.Contains("读取失败")
            ? new("disk", CheckLevel.Unknown, facts.DiskDetail)
            : new("disk", CheckLevel.Pass, facts.DiskDetail));
        checks.Add(new("veyon", CheckLevel.Unknown, facts.VeyonDetail));
        if (input.Operations.InstallVeyon)
        {
            input.Package!.VerifyUnchanged();
            checks.Add(new("public-key", CheckLevel.Pass,
                $"已重新读取部署包和 RSA 公钥，指纹 {input.Package.PublicKeyFingerprint[..12]}…，资料摘要 {input.Package.PackageFingerprint[..12]}…"));
            checks.Add(input.Package.InstallerPath is null
                ? new("installer", CheckLevel.Unknown, "旧版部署包不含安装资源；尚不能执行离线安装。")
                : new("installer", CheckLevel.Pass, "安装资源大小与摘要匹配；来源签名和目标版本仍需另行核对。"));
            if (input.Package.InstallerPath is not null)
                checks.Add(new("installer-trust", CheckLevel.Unknown,
                    "安装程序的数字签名、来源与目标 Veyon 版本尚未在本机验证。"));
        }
        if (input.Operations.RenameComputer)
            checks.Add(new("rename", CheckLevel.Unknown,
                "目标名称已通过规则检查；域成员、重名及待重启状态尚需 Windows 专项检查。"));
        if (input.Operations.CreateStudent || input.Operations.ChangeAdminPassword)
            checks.Add(new("accounts", CheckLevel.Unknown,
                "目标账户 SID、启用状态、组成员和密码策略尚需 Windows 专项检查。"));
        return new PreflightReport(DateTimeOffset.UtcNow, planSha256, packageSha256, checks);
    }

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
