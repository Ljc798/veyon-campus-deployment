using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

internal static class WindowsLocalGroupMembershipScript
{
    internal const string Functions = """
        function Get-LocalGroupMembershipMap {
            $membership = @{}
            foreach ($group in @(Get-LocalGroup -ErrorAction Stop)) {
                if ($null -eq $group.SID) { throw 'Local group SID enumeration is incomplete.' }
                $principals = @(Get-LocalGroupMember -SID $group.SID -ErrorAction Stop)
                if (@($principals | Where-Object { $null -eq $_.SID }).Count -gt 0) {
                    throw 'Local group member SID enumeration is incomplete.'
                }
                $members = @($principals | ForEach-Object { [string]$_.SID.Value })
                $membership[[string]$group.SID.Value] = $members
            }
            return ,$membership
        }
        """;

    internal const string EmitMembershipMap = """
        [Console]::Out.Write((ConvertTo-Json -InputObject ([pscustomobject]@{ groups = $membership }) -Compress -Depth 6))
        """;
}

internal static class WindowsStudentSystemPolicyScripts
{
    internal const string PreflightStudents = WindowsLocalGroupMembershipScript.Functions + """
        $computer=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        if($computer.PartOfDomain) { throw 'Domain-managed computer requires review.' }
        if($data.requireNetworkSettingsPageVisibility -or $data.requireDesktopWallpaperPolicy) {
         $edition=(Get-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name EditionID -ErrorAction Stop).EditionID
         if(@($data.supportedSettingsPageVisibilityEditions) -notcontains [string]$edition) {
          if($data.requireDesktopWallpaperPolicy) { throw 'The Windows desktop-wallpaper policy requires Windows Pro, Enterprise, Education, or IoT Enterprise.' }
          throw 'Settings Page Visibility requires a supported Windows Pro, Enterprise, Education, or IoT Enterprise edition.'
         }
        }
        $enrollments=Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Enrollments' -ErrorAction SilentlyContinue
        foreach($entry in $enrollments) { $x=Get-ItemProperty -LiteralPath $entry.PSPath; if($x.ProviderID) { throw 'MDM enrollment requires review.' } }
        foreach($sid in $data.sids) {
         $account=Get-LocalUser -SID ([Security.Principal.SecurityIdentifier]::new([string]$sid)) -ErrorAction Stop
         if(-not $account.Enabled -or $account.PrincipalSource -ne 'Local') { throw 'Student must be an enabled local account.' }
         $profile=Get-CimInstance Win32_UserProfile -ErrorAction Stop | Where-Object { $_.SID -eq $sid -and -not $_.Special } | Select-Object -First 1
         if($null -eq $profile -or -not (Test-Path -LiteralPath (Join-Path $profile.LocalPath 'NTUSER.DAT') -PathType Leaf)) { throw 'Student profile is not ready.' }
        }
        $membership = Get-LocalGroupMembershipMap
        """ + WindowsLocalGroupMembershipScript.EmitMembershipMap;
}

internal static class WindowsLocalGroupMembershipGraph
{
    private const int MaximumGroups = 4096;
    private const int MaximumMemberships = 65536;
    private static readonly Regex SidPattern = new(
        @"^S-1-(?:0|[1-9][0-9]{0,14})(?:-(?:0|[1-9][0-9]{0,9})){1,15}$",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1024 * 1024)
            throw new InvalidDataException("本地组成员关系读回大小无效。");
        try
        {
            var wire = JsonSerializer.Deserialize<MembershipWire>(json, new JsonSerializerOptions
                { PropertyNameCaseInsensitive = true });
            if (wire?.Groups is null || wire.Groups.Count is 0 or > MaximumGroups)
                throw new InvalidDataException("本地组成员关系读回为空或数量超限。");
            var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
            var totalMemberships = 0;
            foreach (var (groupSid, memberSids) in wire.Groups)
            {
                if (!IsSid(groupSid) || memberSids is null || result.ContainsKey(groupSid))
                    throw new InvalidDataException("本地组 SID 清单无效。");
                var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var memberSid in memberSids)
                {
                    if (!IsSid(memberSid) || !members.Add(memberSid))
                        throw new InvalidDataException("本地组成员 SID 清单无效或重复。");
                    if (++totalMemberships > MaximumMemberships)
                        throw new InvalidDataException("本地组成员关系数量超限。");
                }
                result.Add(groupSid, Array.AsReadOnly(members.ToArray()));
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("本地组成员关系读回格式无效。", ex);
        }
    }

    internal static bool IsMemberOf(IReadOnlyDictionary<string, IReadOnlyCollection<string>> groups,
        string groupSid, string memberSid)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var visitedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingGroups = new Stack<string>();
        pendingGroups.Push(groupSid);
        while (pendingGroups.TryPop(out var currentGroup))
        {
            if (!visitedGroups.Add(currentGroup) || !groups.TryGetValue(currentGroup, out var members)) continue;
            foreach (var currentMember in members)
            {
                if (string.Equals(currentMember, memberSid, StringComparison.OrdinalIgnoreCase)) return true;
                if (groups.ContainsKey(currentMember)) pendingGroups.Push(currentMember);
            }
        }
        return false;
    }

    internal static void RequireGroups(IReadOnlyDictionary<string, IReadOnlyCollection<string>> groups,
        IReadOnlyCollection<string> requiredGroupSids)
    {
        if (requiredGroupSids.Any(groupSid => !groups.ContainsKey(groupSid)))
            throw new InvalidDataException("必需的 Windows 本地安全组缺失；没有修改学生策略。");
    }

    private static bool IsSid(string? value) => value is not null && SidPattern.IsMatch(value);

    private sealed record MembershipWire(
        [property: JsonPropertyName("groups")] Dictionary<string, string[]>? Groups);
}

internal static class WindowsApplicationPolicyPreflight
{
    internal static void VerifyStudentsNotAdministrators(IReadOnlyList<string> studentSids,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> localGroups)
    {
        WindowsLocalGroupMembershipGraph.RequireGroups(localGroups, ["S-1-5-32-544"]);
        if (studentSids.Any(sid => WindowsLocalGroupMembershipGraph.IsMemberOf(localGroups, "S-1-5-32-544", sid)))
            throw new InvalidDataException("学生账户通过嵌套本地组属于 Administrators；没有启用 AppLocker。");
    }
}

internal static class WindowsStudentSystemPolicyPreflight
{
    private static readonly IReadOnlyList<string> RestrictedGroups = Array.AsReadOnly(
        ["S-1-5-32-544", "S-1-5-32-548", "S-1-5-32-556"]);

    internal static void VerifyStudentsNotPrivileged(IReadOnlyList<string> studentSids,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> localGroups)
    {
        WindowsLocalGroupMembershipGraph.RequireGroups(localGroups, RestrictedGroups);
        foreach (var sid in studentSids)
        {
            if (WindowsLocalGroupMembershipGraph.IsMemberOf(localGroups, RestrictedGroups[0], sid))
                throw new InvalidDataException("学生账户通过嵌套本地组属于 Administrators；没有修改学生策略。");
            if (WindowsLocalGroupMembershipGraph.IsMemberOf(localGroups, RestrictedGroups[1], sid) ||
                WindowsLocalGroupMembershipGraph.IsMemberOf(localGroups, RestrictedGroups[2], sid))
                throw new InvalidDataException("学生账户通过嵌套本地组属于账户或网络配置操作员组；没有修改学生策略。");
        }
    }
}
