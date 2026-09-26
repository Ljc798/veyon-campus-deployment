using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public sealed record ComputerNameState(string ActiveName, string ConfiguredName);

/// <summary>Workgroup-only rename with domain and pending-name read-back.</summary>
public sealed class WindowsRenameAdapter
{
    private readonly IProcessLauncher _launcher;

    public WindowsRenameAdapter(IProcessLauncher? launcher = null) =>
        _launcher = launcher ?? new DefaultProcessLauncher();

    public ComputerNameState? ReadCurrentName()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var outcome = WindowsPowerShell.Run(_launcher,
            @"$root = 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\'; " +
            "$active = (Get-ItemProperty -LiteralPath ($root + 'ActiveComputerName') -Name ComputerName).ComputerName; " +
            "$configured = (Get-ItemProperty -LiteralPath ($root + 'ComputerName') -Name ComputerName).ComputerName; " +
            "[pscustomobject]@{ ActiveName = $active; ConfiguredName = $configured } | ConvertTo-Json -Compress",
            TimeSpan.FromSeconds(15));
        if (!outcome.Ok) return null;
        try
        {
            var state = JsonSerializer.Deserialize<ComputerNameState>(outcome.Stdout);
            return state is not null && !string.IsNullOrWhiteSpace(state.ActiveName) &&
                   !string.IsNullOrWhiteSpace(state.ConfiguredName) ? state : null;
        }
        catch (JsonException) { return null; }
    }

    public StepResult RequestRename(string targetName)
    {
        if (!OperatingSystem.IsWindows())
            return new("rename", ExecutionPlan.Failed, "电脑改名仅支持 Windows。");
        if (targetName.Length is 0 or > 15 || !targetName.Any(char.IsAsciiLetter) ||
            !Regex.IsMatch(targetName, "^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?$"))
            return new("rename", ExecutionPlan.Failed, "目标电脑名无效。");

        var domain = CheckDomainMembership();
        if (!domain.Ok) return domain;
        var before = ReadCurrentName();
        if (before is null) return Unknown("当前名称及待生效名称无法确认；未请求改名。");
        if (!Same(before.ActiveName, before.ConfiguredName))
            return Same(before.ConfiguredName, targetName)
                ? Pending(targetName, before.ActiveName)
                : Unknown("已有其他待生效名称；请先重启并重新检查，未覆盖改名请求。");
        if (Same(before.ActiveName, targetName))
            return new("rename", ExecutionPlan.Skipped, "目标名称与当前名称一致；无需修改。");

        var outcome = WindowsPowerShell.Run(_launcher,
            "$result = Rename-Computer -NewName " + WindowsPowerShell.Literal(targetName) +
            " -Force -PassThru -WarningAction SilentlyContinue -ErrorAction Stop; " +
            "if ($result.HasSucceeded -ne $true) { exit 1 }", TimeSpan.FromSeconds(30));
        if (!outcome.Ok)
            return Unknown("改名命令未确认成功；实际名称需重新检查，不自动重试。", outcome.ExitCode);
        var after = ReadCurrentName();
        if (after is not null && Same(after.ConfiguredName, targetName))
            return Same(after.ActiveName, targetName)
                ? new("rename", ExecutionPlan.Succeeded, $"已读回目标名称 {targetName}。", outcome.ExitCode)
                : Pending(targetName, after.ActiveName, outcome.ExitCode);
        return Unknown("改名命令已返回，但未读回预期的待生效名称；需要重新检查。", outcome.ExitCode);
    }

    public StepResult CheckDomainMembership()
    {
        if (!OperatingSystem.IsWindows()) return Unknown("域检查仅支持 Windows。");
        var outcome = WindowsPowerShell.Run(_launcher,
            "Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop | " +
            "Select-Object PartOfDomain | ConvertTo-Json -Compress", TimeSpan.FromSeconds(15));
        if (outcome.Ok)
        {
            try
            {
                using var document = JsonDocument.Parse(outcome.Stdout);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("PartOfDomain", out var member))
                {
                    if (member.ValueKind == JsonValueKind.False)
                        return new("rename", ExecutionPlan.Succeeded, "已确认本机不是域成员。");
                    if (member.ValueKind == JsonValueKind.True)
                        return new("rename", ExecutionPlan.Failed, "本机为域成员；不支持本地改名，未执行修改。");
                }
            }
            catch (JsonException) { }
        }
        return Unknown("域成员状态无法确认；按未知处理，不套用工作组流程。", outcome.ExitCode);
    }

    public StepResult VerifyAfterReboot(string targetName)
    {
        var state = ReadCurrentName();
        return state is not null && Same(state.ActiveName, targetName) && Same(state.ConfiguredName, targetName)
            ? new("rename", ExecutionPlan.Succeeded, $"已读回活动名称 {targetName}；改名已生效。")
            : Unknown("活动名称或待生效名称与目标不一致，或无法读取；需要人工核对。");
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static StepResult Unknown(string detail, int? exitCode = null) =>
        new("rename", ExecutionPlan.NeedsReview, detail, exitCode);
    private static StepResult Pending(string target, string current, int? exitCode = null) =>
        new("rename", ExecutionPlan.RequiresReboot, $"已读回待生效名称 {target}；当前名称 {current}，需重启后生效。",
            exitCode, RebootRequired: true);
}
