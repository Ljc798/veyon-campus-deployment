using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public sealed record LocalAccountFacts(string Name, bool Exists, string? Sid, string? PrincipalSource,
    bool? Enabled, bool? IsAdministrator, bool? IsUsersMember, bool? HasOtherLocalGroups);

/// <summary>Non-secret account facts frozen by preflight and rechecked before mutation.</summary>
public sealed record AccountExecutionSnapshot(string? StudentAccountName, string? StudentSid,
    string? AdminAccountName, string? AdminSid);

public sealed record AccountPreflightResult(AccountExecutionSnapshot? Snapshot,
    IReadOnlyList<PreflightCheck> Checks);

/// <summary>Inspects and modifies explicitly selected local Windows accounts.</summary>
public sealed class WindowsAccountAdapter
{
    // Kept as a compatibility constant for older callers. Account mutations are
    // available only through the preflight-bound methods below.
    public const string PreviewOnlyReason = "账户操作需要管理员权限和明确的本地账户 SID；管理员新密码需两次输入一致。";
    private const string LocalSidPattern = @"^S-1-5-21-\d+-\d+-\d+-\d+$";
    private const string UserGroupSid = "S-1-5-32-545";
    private const string AdministratorsGroupSid = "S-1-5-32-544";
    private readonly IProcessLauncher _launcher;

    public WindowsAccountAdapter(IProcessLauncher? launcher = null) =>
        _launcher = launcher ?? new DefaultProcessLauncher();

    public static bool IsValidAccountName(string name, string label)
    {
        _ = label;
        return name.Length is >= 1 and <= 20 && name == name.Trim() && !name.Any(char.IsControl) &&
               name.IndexOfAny(['\\', '/', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '"', '@']) < 0 &&
               name is not "." and not "..";
    }

    public LocalAccountFacts? ReadLocalAccountFacts(string accountName)
    {
        if (!OperatingSystem.IsWindows() || !IsValidAccountName(accountName, "本地账户")) return null;
        var outcome = WindowsPowerShell.Run(_launcher, GetFactsScript(accountName), TimeSpan.FromSeconds(30));
        return outcome.Ok ? ParseFacts(outcome.Stdout, accountName) : null;
    }

    /// <summary>Compatibility read-back used by earlier account identity checks.</summary>
    public (string? Sid, string State) ReadAccount(string accountName)
    {
        var facts = ReadLocalAccountFacts(accountName);
        if (facts is null) return (null, "本地账户不存在或查询失败；未确认 SID。");
        if (!facts.Exists) return (null, $"未找到本地账户 {accountName}。");
        if (facts.Sid is null) return (null, "本地账户查询未返回有效 SID；需要人工核对。");
        return (facts.Sid, $"已定位本地账户 {accountName}（SID {facts.Sid}）。");
    }

    /// <summary>Legacy entry point intentionally cannot mutate without a preflight SID snapshot.</summary>
    public StepResult CreateStudentAccount(string accountName) =>
        new("student-account", ExecutionPlan.NeedsReview,
            "账户创建需要经过预检绑定的 SID 快照和一次性初始密码；未执行修改。");

    /// <summary>Legacy entry point intentionally cannot mutate without a preflight SID snapshot.</summary>
    public StepResult ChangeAdminPassword(string accountName, string newPassword) =>
        new("admin-password", ExecutionPlan.NeedsReview,
            "管理员密码修改需要经过预检绑定的本地 SID 快照；未执行修改。");

    public static AccountPreflightResult CheckSelectedAccounts(PlanInput input,
        IProcessLauncher? launcher = null)
    {
        var checks = new List<PreflightCheck>();
        if (!input.Operations.CreateStudent && !input.Operations.ChangeAdminPassword)
            return new(null, checks);
        if (!OperatingSystem.IsWindows())
        {
            checks.Add(new("accounts", CheckLevel.Blocked, "账户操作仅支持 Windows；未修改账户。"));
            return new(null, checks);
        }

        var adapter = new WindowsAccountAdapter(launcher);
        string? studentName = null;
        string? studentSid = null;
        string? adminName = null;
        string? adminSid = null;

        if (input.Operations.CreateStudent)
        {
            studentName = input.StudentAccountName;
            var facts = adapter.ReadLocalAccountFacts(studentName);
            if (facts is null)
                checks.Add(new("student-account", CheckLevel.Blocked,
                    "无法可靠读取本地账户及内置组状态；阻止创建学生账户。"));
            else if (!facts.Exists)
                checks.Add(new("student-account", CheckLevel.Pass,
                    $"本地账户 {studentName} 当前不存在；将创建并验证为已启用的普通 Users 组成员。"));
            else if (!IsValidLocalSid(facts.Sid))
                checks.Add(new("student-account", CheckLevel.Blocked,
                    $"已存在同名本地账户 {studentName}，但 SID 无法确认；不会覆盖或重建。"));
            else if (facts.PrincipalSource != "Local" || facts.Enabled != true ||
                     facts.IsAdministrator != false || facts.IsUsersMember != true || facts.HasOtherLocalGroups != false)
                checks.Add(new("student-account", CheckLevel.Blocked,
                    $"已存在同名账户 {studentName}，但它不是仅属于 Users 组的已启用本地普通账户；不会启用、降权、改密或重建。"));
            else
            {
                studentSid = facts.Sid;
                checks.Add(new("student-account", CheckLevel.Warning,
                    $"已存在启用的普通本地账户 {studentName}（SID {studentSid}）；执行时将跳过，不重置其密码。"));
            }
        }

        if (input.Operations.ChangeAdminPassword)
        {
            adminName = input.AdminAccountName;
            var facts = adapter.ReadLocalAccountFacts(adminName);
            if (facts is null || !facts.Exists || !IsValidLocalSid(facts.Sid))
                checks.Add(new("admin-account", CheckLevel.Blocked,
                    $"无法确认本地管理员账户 {adminName} 的 SID；不会尝试匹配域账户或其他同名账户。"));
            else if (facts.PrincipalSource != "Local" || facts.Enabled != true || facts.IsAdministrator != true)
                checks.Add(new("admin-account", CheckLevel.Blocked,
                    $"本地账户 {adminName} 未确认处于启用状态且属于内置 Administrators 组；未修改密码。"));
            else
            {
                adminSid = facts.Sid;
                checks.Add(new("admin-account", CheckLevel.Pass,
                    $"目标是已启用的本地管理员 {adminName}（SID {adminSid}）；将再次核对 SID 后设置密码。"));
            }
        }

        return new(new AccountExecutionSnapshot(studentName, studentSid, adminName, adminSid), checks);
    }

    public StepResult CreateStudentAccount(string accountName, string? initialPassword, string? expectedSid)
    {
        const string stepId = "student-account";
        if (!OperatingSystem.IsWindows()) return new(stepId, ExecutionPlan.Failed, "学生账户创建仅支持 Windows。");
        if (!IsValidAccountName(accountName, "学生账户")) return new(stepId, ExecutionPlan.Failed, "学生账户名无效。");

        var before = ReadLocalAccountFacts(accountName);
        if (before is null) return FailedBeforeMutation(stepId, "无法重新读取学生账户状态；未开始创建。");
        if (expectedSid is not null)
        {
            if (before.Exists && SameSid(before.Sid, expectedSid) && IsStandardEnabledUser(before))
                return new(stepId, ExecutionPlan.Skipped,
                    $"普通账户 {accountName}（SID {expectedSid}）已存在；保留原密码与权限，没有重复创建。");
            return FailedBeforeMutation(stepId, "学生账户与预检时的 SID 或组状态不一致；没有覆盖现有账户。");
        }
        if (before.Exists)
            return FailedBeforeMutation(stepId, "预检后出现同名本地账户；为避免覆盖未知账户，没有创建或改密。");
        if (initialPassword is not null && !IsValidPasswordInput(initialPassword))
            return new(stepId, ExecutionPlan.Failed, "初始密码包含不支持的控制字符；未创建账户。");

        var script = CreateStudentScript(accountName, initialPassword is not null);
        var outcome = initialPassword is null
            ? WindowsPowerShell.Run(_launcher, script, TimeSpan.FromSeconds(45))
            : WindowsPowerShell.RunWithStandardInput(_launcher, script,
                TimeSpan.FromSeconds(45), initialPassword + Environment.NewLine);
        return InterpretMutation(outcome, stepId, "学生账户创建结果无法确认；请检查账户和组状态，不要盲目重试。");
    }

    public StepResult ChangeAdminPassword(string accountName, string newPassword, string expectedSid)
    {
        const string stepId = "admin-password";
        if (!OperatingSystem.IsWindows()) return new(stepId, ExecutionPlan.Failed, "管理员密码修改仅支持 Windows。");
        if (!IsValidAccountName(accountName, "管理员账户") || !IsValidLocalSid(expectedSid))
            return new(stepId, ExecutionPlan.Failed, "管理员账户名或预检 SID 无效；未修改密码。");
        if (!IsValidPasswordInput(newPassword)) return new(stepId, ExecutionPlan.Failed, "新密码为空或包含不支持的控制字符；未修改密码。");

        var before = ReadLocalAccountFacts(accountName);
        if (before is null || !before.Exists || !SameSid(before.Sid, expectedSid) ||
            before.PrincipalSource != "Local" || before.Enabled != true || before.IsAdministrator != true)
            return FailedBeforeMutation(stepId,
                "目标账户 SID、启用状态或 Administrators 组成员身份与预检不一致；未修改密码。");

        var script = ChangePasswordScript(accountName, expectedSid);
        var outcome = WindowsPowerShell.RunWithStandardInput(_launcher, script,
            TimeSpan.FromSeconds(45), newPassword + Environment.NewLine);
        return InterpretMutation(outcome, stepId,
            "密码设置结果无法确认；请使用受控方式核对目标账户，不要盲目重试。", expectedSid);
    }

    private static string GetFactsScript(string accountName)
    {
        var name = WindowsPowerShell.Literal(accountName);
        var template = """
            $name = __NAME__
            $matches = @(Get-LocalUser -ErrorAction Stop | Where-Object { [string]::Equals($_.Name, $name, [System.StringComparison]::OrdinalIgnoreCase) })
            if ($matches.Count -eq 0) {
                [pscustomobject]@{ Name = $name; Exists = $false; Sid = $null; PrincipalSource = $null; Enabled = $null; IsAdministrator = $null; IsUsersMember = $null } | ConvertTo-Json -Compress
                return
            }
            if ($matches.Count -ne 1) { exit 1 }
            $user = $matches[0]
            $administrators = Get-LocalGroup -SID ([System.Security.Principal.SecurityIdentifier]::new('__ADMINS__')) -ErrorAction Stop
            $users = Get-LocalGroup -SID ([System.Security.Principal.SecurityIdentifier]::new('__USERS__')) -ErrorAction Stop
            $adminMembers = @(Get-LocalGroupMember -Group $administrators -ErrorAction Stop)
            $userMembers = @(Get-LocalGroupMember -Group $users -ErrorAction Stop)
            $isAdmin = @($adminMembers | Where-Object { $_.SID -and $_.SID.Value -eq $user.SID.Value }).Count -gt 0
            $isUser = @($userMembers | Where-Object { $_.SID -and $_.SID.Value -eq $user.SID.Value }).Count -gt 0
            $otherLocalGroups = @()
            foreach ($group in @(Get-LocalGroup -ErrorAction Stop)) {
                if ($group.SID.Value -eq '__USERS__') { continue }
                $members = @(Get-LocalGroupMember -Group $group -ErrorAction Stop)
                if (@($members | Where-Object { $_.SID -and $_.SID.Value -eq $user.SID.Value }).Count -gt 0) {
                    $otherLocalGroups += $group.SID.Value
                }
            }
            [pscustomobject]@{ Name = $user.Name; Exists = $true; Sid = $user.SID.Value; PrincipalSource = [string]$user.PrincipalSource; Enabled = [bool]$user.Enabled; IsAdministrator = $isAdmin; IsUsersMember = $isUser; HasOtherLocalGroups = ($otherLocalGroups.Count -gt 0) } | ConvertTo-Json -Compress
        """;
        return ReplaceTokens(template, ("__NAME__", name),
            ("__ADMINS__", AdministratorsGroupSid), ("__USERS__", UserGroupSid));
    }

    private static string CreateStudentScript(string accountName, bool hasPassword)
    {
        var template = """
        $name = __NAME__
        $hasPassword = __HAS_PASSWORD__
        $plainPassword = $null
        $securePassword = $null
        if ($hasPassword) {
            $plainPassword = [Console]::In.ReadLine()
            if ([string]::IsNullOrEmpty($plainPassword)) {
                [pscustomobject]@{ Status = 'failed'; Sid = $null } | ConvertTo-Json -Compress
                return
            }
            $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force
            $plainPassword = $null
        }
        try {
            $matches = @(Get-LocalUser -ErrorAction Stop | Where-Object { [string]::Equals($_.Name, $name, [System.StringComparison]::OrdinalIgnoreCase) })
            if ($matches.Count -ne 0) {
                [pscustomobject]@{ Status = 'failed'; Sid = $null } | ConvertTo-Json -Compress
                return
            }
            if ($hasPassword) {
                $newUser = New-LocalUser -Name $name -Password $securePassword -ErrorAction Stop
            }
            else {
                $newUser = New-LocalUser -Name $name -NoPassword -ErrorAction Stop
            }
            $usersGroup = Get-LocalGroup -SID ([System.Security.Principal.SecurityIdentifier]::new('__USERS__')) -ErrorAction Stop
            $administrators = Get-LocalGroup -SID ([System.Security.Principal.SecurityIdentifier]::new('__ADMINS__')) -ErrorAction Stop
            $userMembers = @(Get-LocalGroupMember -Group $usersGroup -ErrorAction Stop)
            if (@($userMembers | Where-Object { $_.SID -and $_.SID.Value -eq $newUser.SID.Value }).Count -eq 0) {
                Add-LocalGroupMember -Group $usersGroup -Member $newUser -ErrorAction Stop | Out-Null
            }
            $adminMembers = @(Get-LocalGroupMember -Group $administrators -ErrorAction Stop)
            $userMembers = @(Get-LocalGroupMember -Group $usersGroup -ErrorAction Stop)
            $otherLocalGroups = @()
            foreach ($group in @(Get-LocalGroup -ErrorAction Stop)) {
                if ($group.SID.Value -eq '__USERS__') { continue }
                $members = @(Get-LocalGroupMember -Group $group -ErrorAction Stop)
                if (@($members | Where-Object { $_.SID -and $_.SID.Value -eq $newUser.SID.Value }).Count -gt 0) {
                    $otherLocalGroups += $group.SID.Value
                }
            }
            $verified = Get-LocalUser -SID $newUser.SID -ErrorAction Stop
            $isAdmin = @($adminMembers | Where-Object { $_.SID -and $_.SID.Value -eq $verified.SID.Value }).Count -gt 0
            $isUser = @($userMembers | Where-Object { $_.SID -and $_.SID.Value -eq $verified.SID.Value }).Count -gt 0
            if ($verified.Enabled -ne $true -or $isAdmin -or -not $isUser -or $otherLocalGroups.Count -gt 0) {
                [pscustomobject]@{ Status = 'needs-review'; Sid = $verified.SID.Value } | ConvertTo-Json -Compress
                return
            }
            [pscustomobject]@{ Status = 'created'; Sid = $verified.SID.Value } | ConvertTo-Json -Compress
        }
        catch {
            $confirmedAbsent = $false
            $sidAfterFailure = $null
            try {
                $afterFailure = @(Get-LocalUser -ErrorAction Stop | Where-Object { [string]::Equals($_.Name, $name, [System.StringComparison]::OrdinalIgnoreCase) })
                $confirmedAbsent = $afterFailure.Count -eq 0
                if ($afterFailure.Count -eq 1) { $sidAfterFailure = $afterFailure[0].SID.Value }
            }
            catch { }
            [pscustomobject]@{ Status = $(if ($confirmedAbsent) { 'failed' } else { 'needs-review' }); Sid = $sidAfterFailure } | ConvertTo-Json -Compress
        }
        finally {
            if ($null -ne $securePassword) { $securePassword.Dispose() }
            $plainPassword = $null
        }
        """;
        return ReplaceTokens(template,
            ("__NAME__", WindowsPowerShell.Literal(accountName)),
            ("__HAS_PASSWORD__", hasPassword ? "$true" : "$false"),
            ("__ADMINS__", AdministratorsGroupSid), ("__USERS__", UserGroupSid));
    }

    private static string ChangePasswordScript(string accountName, string expectedSid)
    {
        var template = """
        $name = __NAME__
        $expectedSid = __SID__
        $plainPassword = [Console]::In.ReadLine()
        if ([string]::IsNullOrEmpty($plainPassword)) {
            [pscustomobject]@{ Status = 'failed'; Sid = $null } | ConvertTo-Json -Compress
            return
        }
        $securePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force
        $plainPassword = $null
        $settingAttempted = $false
        try {
            $matches = @(Get-LocalUser -ErrorAction Stop | Where-Object { [string]::Equals($_.Name, $name, [System.StringComparison]::OrdinalIgnoreCase) })
            if ($matches.Count -ne 1 -or $matches[0].SID.Value -ne $expectedSid -or
                $matches[0].PrincipalSource -ne 'Local' -or $matches[0].Enabled -ne $true) {
                [pscustomobject]@{ Status = 'failed'; Sid = $null } | ConvertTo-Json -Compress
                return
            }
            $user = $matches[0]
            $administrators = Get-LocalGroup -SID ([System.Security.Principal.SecurityIdentifier]::new('__ADMINS__')) -ErrorAction Stop
            $adminMembers = @(Get-LocalGroupMember -Group $administrators -ErrorAction Stop)
            if (@($adminMembers | Where-Object { $_.SID -and $_.SID.Value -eq $user.SID.Value }).Count -eq 0) {
                [pscustomobject]@{ Status = 'failed'; Sid = $null } | ConvertTo-Json -Compress
                return
            }
            $settingAttempted = $true
            Set-LocalUser -SID $user.SID -Password $securePassword -ErrorAction Stop
            $verified = Get-LocalUser -SID $user.SID -ErrorAction Stop
            $adminMembers = @(Get-LocalGroupMember -Group $administrators -ErrorAction Stop)
            if ($verified.SID.Value -ne $expectedSid -or $verified.PrincipalSource -ne 'Local' -or $verified.Enabled -ne $true -or
                @($adminMembers | Where-Object { $_.SID -and $_.SID.Value -eq $verified.SID.Value }).Count -eq 0) {
                [pscustomobject]@{ Status = 'needs-review'; Sid = $verified.SID.Value } | ConvertTo-Json -Compress
                return
            }
            [pscustomobject]@{ Status = 'password-set'; Sid = $verified.SID.Value } | ConvertTo-Json -Compress
        }
        catch {
            [pscustomobject]@{ Status = $(if ($settingAttempted) { 'needs-review' } else { 'failed' }); Sid = $null } | ConvertTo-Json -Compress
        }
        finally {
            if ($null -ne $securePassword) { $securePassword.Dispose() }
            $plainPassword = $null
        }
        """;
        return ReplaceTokens(template,
            ("__NAME__", WindowsPowerShell.Literal(accountName)),
            ("__SID__", WindowsPowerShell.Literal(expectedSid)),
            ("__ADMINS__", AdministratorsGroupSid));
    }

    private StepResult InterpretMutation(ProcessOutcome outcome, string stepId, string uncertaintyDetail,
        string? expectedSid = null)
    {
        if (!outcome.Ok)
            return new(stepId, ExecutionPlan.NeedsReview, uncertaintyDetail, outcome.ExitCode);
        try
        {
            using var document = JsonDocument.Parse(outcome.Stdout);
            var root = document.RootElement;
            var status = root.TryGetProperty("Status", out var statusElement) ? statusElement.GetString() : null;
            var sid = root.TryGetProperty("Sid", out var sidElement) && sidElement.ValueKind == JsonValueKind.String
                ? sidElement.GetString() : null;
            if (stepId == "student-account" && status == "created" && IsValidLocalSid(sid))
            {
                var after = ReadLocalAccountFactsBySid(sid!);
                return after is not null && after.Exists && SameSid(after.Sid, sid) && IsStandardEnabledUser(after)
                    ? new(stepId, ExecutionPlan.Succeeded, $"已创建并读回普通学生账户（SID {sid}）。", outcome.ExitCode)
                    : new(stepId, ExecutionPlan.NeedsReview,
                        "账户创建命令返回成功，但新账户的 SID、启用状态或普通用户组成员身份未能确认。", outcome.ExitCode);
            }
            if (stepId == "admin-password" && status == "password-set" &&
                IsValidLocalSid(sid) && SameSid(sid, expectedSid))
                return new(stepId, ExecutionPlan.Succeeded,
                    $"已向 Windows 提交密码设置，并确认目标管理员仍是 SID {sid}；密码值不会被读回或记录。", outcome.ExitCode);
            if (status == "failed")
                return new(stepId, ExecutionPlan.Failed,
                    stepId == "student-account"
                        ? "Windows 拒绝创建，或账户名称发生冲突；检查本机密码策略和账户状态后再重试。"
                        : "设置密码前目标账户状态发生变化或 Windows 拒绝操作；未确认密码已修改。",
                    outcome.ExitCode);
            if (status == "needs-review")
                return new(stepId, ExecutionPlan.NeedsReview,
                    stepId == "student-account"
                        ? "账户创建过程后状态无法完全确认；请检查目标账户及组状态，不要盲目重试。"
                        : "Windows 在设置密码时返回错误；App 无法确认旧密码是否仍可用，请核对目标账户，不要盲目重试。",
                    outcome.ExitCode);
        }
        catch (JsonException) { }
        return new(stepId, ExecutionPlan.NeedsReview, uncertaintyDetail, outcome.ExitCode);
    }

    private LocalAccountFacts? ReadLocalAccountFactsBySid(string sid)
    {
        var script = "$user = Get-LocalUser -SID ([System.Security.Principal.SecurityIdentifier]::new(" +
                     WindowsPowerShell.Literal(sid) + ")) -ErrorAction Stop; " + FactsFromUserScript();
        var outcome = WindowsPowerShell.Run(_launcher, script, TimeSpan.FromSeconds(30));
        return outcome.Ok ? ParseFacts(outcome.Stdout, expectedName: null) : null;
    }

    private static string FactsFromUserScript()
    {
        var template = """
        $administrators = Get-LocalGroup -SID ([System.Security.Principal.SecurityIdentifier]::new('__ADMINS__')) -ErrorAction Stop
        $users = Get-LocalGroup -SID ([System.Security.Principal.SecurityIdentifier]::new('__USERS__')) -ErrorAction Stop
        $adminMembers = @(Get-LocalGroupMember -Group $administrators -ErrorAction Stop)
        $userMembers = @(Get-LocalGroupMember -Group $users -ErrorAction Stop)
        $isAdmin = @($adminMembers | Where-Object { $_.SID -and $_.SID.Value -eq $user.SID.Value }).Count -gt 0
        $isUser = @($userMembers | Where-Object { $_.SID -and $_.SID.Value -eq $user.SID.Value }).Count -gt 0
        $otherLocalGroups = @()
        foreach ($group in @(Get-LocalGroup -ErrorAction Stop)) {
            if ($group.SID.Value -eq '__USERS__') { continue }
            $members = @(Get-LocalGroupMember -Group $group -ErrorAction Stop)
            if (@($members | Where-Object { $_.SID -and $_.SID.Value -eq $user.SID.Value }).Count -gt 0) {
                $otherLocalGroups += $group.SID.Value
            }
        }
        [pscustomobject]@{ Name = $user.Name; Exists = $true; Sid = $user.SID.Value; PrincipalSource = [string]$user.PrincipalSource; Enabled = [bool]$user.Enabled; IsAdministrator = $isAdmin; IsUsersMember = $isUser; HasOtherLocalGroups = ($otherLocalGroups.Count -gt 0) } | ConvertTo-Json -Compress
        """;
        return ReplaceTokens(template, ("__ADMINS__", AdministratorsGroupSid), ("__USERS__", UserGroupSid));
    }

    private static string ReplaceTokens(string template, params (string Token, string Value)[] replacements)
    {
        var values = replacements.ToDictionary(item => item.Token, item => item.Value, StringComparer.Ordinal);
        return Regex.Replace(template, "__NAME__|__SID__|__HAS_PASSWORD__|__ADMINS__|__USERS__",
            match => values[match.Value], RegexOptions.CultureInvariant);
    }

    private static LocalAccountFacts? ParseFacts(string output, string? expectedName)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (!root.TryGetProperty("Name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                nameElement.GetString() is not { } name ||
                (expectedName is not null && !string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase)) ||
                !root.TryGetProperty("Exists", out var existsElement) ||
                existsElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var exists = existsElement.GetBoolean();
            var sid = ReadStringOrNull(root, "Sid");
            var principalSource = ReadStringOrNull(root, "PrincipalSource");
            var enabled = ReadBooleanOrNull(root, "Enabled");
            var isAdministrator = ReadBooleanOrNull(root, "IsAdministrator");
            var isUsersMember = ReadBooleanOrNull(root, "IsUsersMember");
            var hasOtherGroups = ReadBooleanOrNull(root, "HasOtherLocalGroups");
            if (!exists)
                return sid is null && principalSource is null && enabled is null && isAdministrator is null &&
                       isUsersMember is null && hasOtherGroups is null
                    ? new(name, false, null, null, null, null, null, null) : null;
            return IsValidLocalSid(sid) && !string.IsNullOrWhiteSpace(principalSource) &&
                   enabled is not null && isAdministrator is not null && isUsersMember is not null && hasOtherGroups is not null
                ? new(name, true, sid, principalSource, enabled, isAdministrator, isUsersMember, hasOtherGroups) : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? ReadStringOrNull(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() : null;

    private static bool? ReadBooleanOrNull(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) ? element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        } : null;

    private static bool IsValidPasswordInput(string password) =>
        password.Length is > 0 and <= 127 && !password.Any(char.IsControl);

    private static bool IsValidLocalSid(string? sid) => sid is not null && Regex.IsMatch(sid, LocalSidPattern,
        RegexOptions.CultureInvariant);

    private static bool SameSid(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsStandardEnabledUser(LocalAccountFacts facts) =>
        facts.Exists && facts.PrincipalSource == "Local" && IsValidLocalSid(facts.Sid) && facts.Enabled == true &&
        facts.IsAdministrator == false && facts.IsUsersMember == true && facts.HasOtherLocalGroups == false;

    private static StepResult FailedBeforeMutation(string stepId, string detail) =>
        new(stepId, ExecutionPlan.Failed, detail);
}
