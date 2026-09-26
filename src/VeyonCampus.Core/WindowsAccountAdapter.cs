using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

/// <summary>Read-only local account facts. Mutations await password and SID confirmation.</summary>
public sealed class WindowsAccountAdapter
{
    public const string PreviewOnlyReason = "账户操作目前仅支持预览；初始密码输入与目标 SID 确认尚未接入，未执行任何修改。";
    private readonly IProcessLauncher _launcher;

    public WindowsAccountAdapter(IProcessLauncher? launcher = null) =>
        _launcher = launcher ?? new DefaultProcessLauncher();

    public static bool IsValidAccountName(string name, string label)
    {
        _ = label;
        return name.Length is >= 1 and <= 20 && name == name.Trim() && !name.Any(char.IsControl) &&
               name.IndexOfAny(['\\', '/', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '"']) < 0 &&
               name is not "." and not "..";
    }

    public (string? Sid, string State) ReadAccount(string accountName)
    {
        if (!OperatingSystem.IsWindows()) return (null, "非 Windows；账户检查不适用。");
        if (!IsValidAccountName(accountName, "本地账户")) return (null, "本地账户名无效。");
        var outcome = WindowsPowerShell.Run(_launcher,
            "$u = Get-LocalUser -Name " + WindowsPowerShell.Literal(accountName) +
            " -ErrorAction Stop; [pscustomobject]@{ Name = $u.Name; Sid = $u.SID.Value } | ConvertTo-Json -Compress",
            TimeSpan.FromSeconds(30));
        if (!outcome.Ok) return (null, "本地账户不存在或查询失败；未确认 SID。");
        try
        {
            using var document = JsonDocument.Parse(outcome.Stdout);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String &&
                string.Equals(name.GetString(), accountName, StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("Sid", out var sid) && sid.ValueKind == JsonValueKind.String &&
                sid.GetString() is { } value && Regex.IsMatch(value, @"^S-1-5-21-\d+-\d+-\d+-\d+$"))
                return (value, $"已定位本地账户 {accountName}（SID {value}）。");
        }
        catch (JsonException) { }
        return (null, "本地账户查询未返回匹配的名称及有效 SID；需要人工核对。");
    }

    // Direct callers cannot bypass the application's preview-only restriction.
    public StepResult CreateStudentAccount(string accountName) =>
        new("student-account", ExecutionPlan.NeedsReview, PreviewOnlyReason);

    public StepResult ChangeAdminPassword(string accountName, string newPassword) =>
        new("admin-password", ExecutionPlan.NeedsReview, PreviewOnlyReason);
}
