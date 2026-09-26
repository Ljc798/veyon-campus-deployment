using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

/// <summary>
/// Windows computer-name adapter (P5 slice, execution not yet opened).
/// All methods are read-back based and never guess: a rename request that
/// cannot confirm the pending/active name is reported as NeedsReview, not
/// success. The independent restart action (P5-06) is a separate entry.
/// </summary>
public sealed class WindowsRenameAdapter
{
    private readonly IProcessLauncher _launcher;

    public WindowsRenameAdapter(IProcessLauncher? launcher = null) =>
        _launcher = launcher ?? new DefaultProcessLauncher();

    /// <summary>Reads the current name and whether a rename is already pending.</summary>
    public (string CurrentName, bool Pending) ReadCurrentName()
    {
        if (!OperatingSystem.IsWindows())
            return ("", false);
        var current = Environment.MachineName;
        // WmiQuery is a bounded read-only check: COMPUTERNAME / LADCOMPUTERNAME.
        var query = _launcher.Run("wmic.exe", new[] { "computer", "where", "name='" + current + "'", "get",
            "LadComputerName", "name", "/format:list" },
            @"C:\Windows\System32", TimeSpan.FromSeconds(15));
        bool pending = false;
        if (query.Ok)
        {
            var lines = query.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            foreach (var line in lines)
            {
                if (line.StartsWith("LadComputerName=", StringComparison.OrdinalIgnoreCase))
                {
                    var pendingName = line["LadComputerName=".Length..].Trim();
                    pending = !string.IsNullOrEmpty(pendingName) &&
                             !string.Equals(pendingName, current, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        return (current, pending);
    }

    /// <summary>
    /// Requests the rename through net.exe with an already-validated target
    /// name (the plan validates it through MachineNaming before execution).
    /// The name only becomes active after a reboot; the result must read
    /// back the pending state and is never reported as "already in effect".
    /// </summary>
    public StepResult RequestRename(string targetName)
    {
        if (!OperatingSystem.IsWindows())
            return new("rename", ExecutionPlan.Failed, "电脑改名仅支持 Windows。");
        if (targetName.Length is 0 or > 15 || !targetName.Any(char.IsAsciiLetter) ||
            !Regex.IsMatch(targetName, "^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant))
            return new("rename", ExecutionPlan.Failed, "目标电脑名无效：须包含英文字母，总长不超过 15 个字符。");

        var outcome = _launcher.Run("net.exe", new[] { "computer", targetName, "/domain" },
            @"C:\Windows\System32", TimeSpan.FromSeconds(30));
        if (outcome.Kind == ProcessOutcomeKind.LaunchRefused)
            return new("rename", ExecutionPlan.Failed, "无法启动 net.exe：" + outcome.Stderr);
        if (outcome.Kind == ProcessOutcomeKind.TimedOut || outcome.Kind == ProcessOutcomeKind.NeedsReview)
            return new("rename", ExecutionPlan.NeedsReview,
                "改名请求超时或状态未知；实际主机名需要重新检查。", outcome.ExitCode);
        if (outcome.ExitCode is not 0)
            return new("rename", ExecutionPlan.Failed,
                $"net computer 返回退出码 {outcome.ExitCode}；未继续。{Truncate(outcome.Stderr)}", outcome.ExitCode);

        var (current, pending) = ReadCurrentName();
        if (pending)
            return new("rename", ExecutionPlan.RequiresReboot,
                $"已请求改名为 {targetName}；当前名称 {current}，新名称需重启后生效。", outcome.ExitCode);
        if (string.Equals(current, targetName, StringComparison.OrdinalIgnoreCase))
            return new("rename", ExecutionPlan.Succeeded, $"目标名称与当前名称一致；无需修改。", outcome.ExitCode);
        return new("rename", ExecutionPlan.NeedsReview,
            $"net computer 已返回成功，但待生效名称读回未确认；需要重新检查。", outcome.ExitCode);
    }

    /// <summary>Domain check: only workgroup machines may rename locally (P5-03).</summary>
    public StepResult CheckDomainMembership()
    {
        if (!OperatingSystem.IsWindows())
            return new("rename", ExecutionPlan.Failed, "域检查仅支持 Windows。");
        var outcome = _launcher.Run("wmic.exe", new[] { "computer", "get", "Domain,PartOfDomain",
            "/format:list" }, @"C:\Windows\System32", TimeSpan.FromSeconds(15));
        if (!outcome.Ok)
            return new("rename", ExecutionPlan.NeedsReview, "域成员状态无法确认；按未知处理，不套用工作组流程。",
                outcome.ExitCode);
        bool inDomain = false;
        foreach (var line in outcome.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("PartOfDomain=", StringComparison.OrdinalIgnoreCase))
                inDomain = trimmed["PartOfDomain=".Length..].Trim() == "TRUE";
            else if (trimmed.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase) &&
                     !trimmed["Domain=".Length..].Trim().Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                inDomain = true;
        }
        return inDomain
            ? new("rename", ExecutionPlan.Failed,
                "本机为域成员；首版不支持域设备本地改名，该操作被阻断，其他可行操作不受影响。")
            : new("rename", ExecutionPlan.Succeeded, "本机为工作组机器；可以套用本地改名流程。");
    }

    /// <summary>Read back after reboot: active name equals target.</summary>
    public StepResult VerifyAfterReboot(string targetName)
    {
        var current = Environment.MachineName;
        if (string.Equals(current, targetName, StringComparison.OrdinalIgnoreCase))
            return new("rename", ExecutionPlan.Succeeded, $"重启后读回名称 {current}；改名已生效。");
        return new("rename", ExecutionPlan.NeedsReview,
            $"重启后当前名称 {current}，与目标 {targetName} 不一致；需要人工核对。");
    }

    private static string Truncate(string text)
    {
        text = text.Trim();
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}
