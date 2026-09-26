using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

/// <summary>
/// Windows local-account adapter (P6 slice, execution not yet opened).
/// Every method reports typed facts; a target account is addressed by
/// explicit name plus verified SID, never guessed from the current user,
/// and created accounts are always standard users (P6-04).
/// </summary>
public sealed class WindowsAccountAdapter
{
    private readonly IProcessLauncher _launcher;

    public WindowsAccountAdapter(IProcessLauncher? launcher = null) =>
        _launcher = launcher ?? new DefaultProcessLauncher();

    public static bool IsValidAccountName(string name, string label)
    {
        if (name.Length is < 1 or > 20 || name.Any(char.IsControl) ||
            name.IndexOfAny(['\\', '/', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '"']) >= 0 ||
            name is "." or "..")
            return false;
        _ = label;
        return true;
    }

    /// <summary>Reads the target account's SID via net user (read-only).</summary>
    public (string? Sid, string State) ReadAccount(string accountName)
    {
        if (!OperatingSystem.IsWindows())
            return (null, "非 Windows；账户检查不适用。");
        var outcome = _launcher.Run("net.exe", new[] { "user", accountName },
            @"C:\Windows\System32", TimeSpan.FromSeconds(30));
        if (outcome.Kind == ProcessOutcomeKind.LaunchRefused)
            return (null, $"无法启动 net.exe：{Truncate(outcome.Stderr)}");
        if (outcome.Kind == ProcessOutcomeKind.TimedOut || outcome.Kind == ProcessOutcomeKind.NeedsReview)
            return (null, "账户查询超时或状态未知；不能猜测目标 SID。");
        if (outcome.ExitCode is not 0)
            return (null, $"账户 {accountName} 未找到或查询失败（退出码 {outcome.ExitCode}）。");

        string? sid = null;
        var lines = outcome.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
        var headerIndex = Array.FindIndex(lines, line =>
            line.StartsWith("User list for", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("账户名", StringComparison.Ordinal) ||
            Regex.IsMatch(line, "^Users", RegexOptions.CultureInvariant));
        for (var i = 0; i < lines.Length; i++)
        {
            if (i == 0) continue; // title line
            var match = Regex.Match(lines[i], @"S-\d+-\d+(-\d+)+", RegexOptions.CultureInvariant);
            if (match.Success && (i > headerIndex || headerIndex < 0))
            {
                sid = match.Value;
                break;
            }
        }
        string state = sid is not null
            ? $"已定位本地账户 {accountName}（SID {sid}）。"
            : $"查询成功但未解析出 {accountName} 的 SID；目标 SID 仍待人工核对。";
        return (sid, state);
    }

    /// <summary>Creates a new standard user (no password set here; P6-04/05).</summary>
    public StepResult CreateStudentAccount(string accountName)
    {
        if (!OperatingSystem.IsWindows())
            return new("student-account", ExecutionPlan.Failed, "创建账户仅支持 Windows。");
        if (!IsValidAccountName(accountName, "学生账户"))
            return new("student-account", ExecutionPlan.Failed, "学生账户名无效；请输入 1–20 个有效字符。");
        var existing = ReadAccount(accountName);
        if (existing.Sid is not null)
            return new("student-account", ExecutionPlan.Skipped,
                $"已存在同名本地账户（SID {existing.Sid}）；为保护已有数据，不重建。");
        var check = _launcher.Run("net.exe", new[] { "user", accountName },
            @"C:\Windows\System32", TimeSpan.FromSeconds(30));
        if (check.ExitCode is not 0)
        {
            var outcome = _launcher.Run("net.exe", new[] { "user", accountName, "/add" },
                @"C:\Windows\System32", TimeSpan.FromSeconds(30));
            if (outcome.Kind == ProcessOutcomeKind.TimedOut || outcome.Kind == ProcessOutcomeKind.NeedsReview)
                return new("student-account", ExecutionPlan.NeedsReview,
                    "创建账户超时或状态未知；可能已部分创建，需要重新检查。", outcome.ExitCode);
            if (outcome.ExitCode is not 0)
                return new("student-account", ExecutionPlan.Failed,
                    $"创建账户失败（退出码 {outcome.ExitCode}）。{Truncate(outcome.Stderr)}", outcome.ExitCode);
            var verify = ReadAccount(accountName);
            if (verify.Sid is not null)
                return new("student-account", ExecutionPlan.Succeeded,
                    $"已创建普通学生账户 {accountName}（SID {verify.Sid}）；初始密码将在后续独立步骤设置。", outcome.ExitCode);
            return new("student-account", ExecutionPlan.NeedsReview,
                "创建命令成功，但未能读回账户 SID；需要人工核对。", outcome.ExitCode);
        }
        return new("student-account", ExecutionPlan.Failed,
            "同名账户存在但无法解析 SID；停止创建，避免误改已有账户。", check.ExitCode);
    }

    /// <summary>
    /// Changes the password of the explicitly named local account. The old
    /// value cannot be read back; callers must mark this step unrecoverable
    /// and confirm the target SID before execution (P6-08).
    /// </summary>
    public StepResult ChangeAdminPassword(string accountName, string newPassword)
    {
        if (!OperatingSystem.IsWindows())
            return new("admin-password", ExecutionPlan.Failed, "修改密码仅支持 Windows。");
        if (!IsValidAccountName(accountName, "管理员账户"))
            return new("admin-password", ExecutionPlan.Failed, "管理员账户名无效；请输入 1–20 个有效字符。");
        var target = ReadAccount(accountName);
        if (target.Sid is null)
            return new("admin-password", ExecutionPlan.NeedsReview,
                $"无法确认目标账户 {accountName} 的本地 SID；为避免改错账户，未修改密码。", null);
        if (newPassword.Length is 0 or < 8)
            return new("admin-password", ExecutionPlan.Failed, "新密码长度不足；本机策略要求至少 8 个字符。");
        // The password is passed only to this single command; it is not
        // logged. net user ... <pw> is the documented local mechanism.
        var outcome = _launcher.Run("net.exe", new[] { "user", accountName, newPassword },
            @"C:\Windows\System32", TimeSpan.FromSeconds(30));
        if (outcome.Kind == ProcessOutcomeKind.TimedOut || outcome.Kind == ProcessOutcomeKind.NeedsReview)
            return new("admin-password", ExecutionPlan.NeedsReview,
                "改密请求超时或状态未知；旧密码无法读回，请人工核对当前可登录状态。", outcome.ExitCode);
        if (outcome.ExitCode is not 0)
            return new("admin-password", ExecutionPlan.Failed,
                $"改密失败（退出码 {outcome.ExitCode}），旧密码仍有效。{Truncate(outcome.Stderr)}", outcome.ExitCode);
        return new("admin-password", ExecutionPlan.Succeeded,
            $"已将本地账户 {accountName}（SID {target.Sid}）的密码修改为新值；旧密码不可读回。", outcome.ExitCode);
    }

    private static string Truncate(string text)
    {
        text = text.Trim();
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}
