using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>Native AppLocker adapter. All scripts are fixed; data travels on stdin, never as PowerShell source.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationPolicyBackend : IApplicationPolicyBackend, IApplicationPolicyAuditSource
{
    private readonly string[] _protectedExecutables;

    public WindowsApplicationPolicyBackend(IEnumerable<string> protectedExecutables)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("AppLocker 执行需要 Windows x64 进程。");
        _protectedExecutables = protectedExecutables.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void VerifyEnvironmentAndStudents(IReadOnlyList<string> studentSids)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("应用策略只能由 SYSTEM Agent 或管理员维护进程执行。");
        Invoke("""
            Get-Command Get-AppLockerPolicy,Set-AppLockerPolicy,Get-AppLockerFileInformation,New-AppLockerPolicy -ErrorAction Stop | Out-Null
            $computer = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
            if ($computer.PartOfDomain) { throw 'Domain-joined computer requires administrator review.' }
            $service = Get-Service AppIDSvc -ErrorAction Stop
            if ($service.Status -ne 'Running') {
                Start-Service AppIDSvc -ErrorAction Stop
                $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(15))
            }
            $enrollments = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Enrollments' -ErrorAction SilentlyContinue
            foreach ($entry in $enrollments) {
                $item = Get-ItemProperty -LiteralPath $entry.PSPath
                if ($null -ne $item.ProviderID -and $item.ProviderID -ne '') { throw 'MDM enrollment requires administrator review.' }
            }
            $admins = @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop)
            foreach ($sid in $data.sids) {
                $account = Get-LocalUser -SID ([System.Security.Principal.SecurityIdentifier]::new([string]$sid)) -ErrorAction Stop
                if (-not $account.Enabled -or $account.PrincipalSource -ne 'Local') { throw 'Student must be an enabled local account.' }
                if (@($admins | Where-Object { $_.SID.Value -eq $sid }).Count -ne 0) { throw 'Student account is an administrator.' }
            }
            [Console]::Out.Write('OK')
            """, new { sids = studentSids });
    }

    public string ReadLocalPolicyXml() => ReadPolicy(effective: false);
    public string ReadEffectivePolicyXml() => ReadPolicy(effective: true);
    private static string ReadPolicy(bool effective)
    {
        var result = Invoke(effective
            ? "$xml = Get-AppLockerPolicy -Effective -Xml -ErrorAction Stop; [Console]::Out.Write([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($xml)))"
            : "$xml = Get-AppLockerPolicy -Local -Xml -ErrorAction Stop; [Console]::Out.Write([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($xml)))", new { });
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(result)); }
        catch (FormatException ex) { throw new InvalidDataException("AppLocker XML 读回编码无效。", ex); }
    }

    public IReadOnlyCollection<string> ReadProtectedAppLockerHashes()
    {
        if (_protectedExecutables.Length == 0) throw new InvalidDataException("缺少更新与恢复组件清单。");
        foreach (var path in _protectedExecutables)
        {
            PathLinkSecurity.RejectLinks(path);
            if (!File.Exists(path)) throw new InvalidDataException("更新或恢复组件缺失；不能证明规则不会误伤。");
        }
        var result = Invoke("""
            $hashes = @()
            foreach ($path in $data.paths) {
                $information = @(Get-AppLockerFileInformation -Path ([string]$path) -ErrorAction Stop)
                if ($information.Count -ne 1 -or $null -eq $information[0].Hash) { throw 'Missing AppLocker file hash.' }
                $generated = [xml]($information | New-AppLockerPolicy -RuleType Hash -User 'S-1-1-0' -Xml -ErrorAction Stop)
                $values = @($generated.SelectNodes('/AppLockerPolicy/RuleCollection/FileHashRule/Conditions/FileHashCondition/FileHash/@Data'))
                if ($values.Count -ne 1) { throw 'Unexpected generated hash rule.' }
                $hash = [string]$values[0].Value
                if ($hash.StartsWith('0x')) { $hash = $hash.Substring(2) }
                if ($hash -notmatch '^[0-9a-fA-F]{64}$') { throw 'Unexpected AppLocker file hash.' }
                $hashes += $hash
            }
            [Console]::Out.Write((ConvertTo-Json -InputObject @($hashes) -Compress))
            """, new { paths = _protectedExecutables });
        try { return JsonSerializer.Deserialize<string[]>(result) ?? throw new InvalidDataException("恢复组件哈希为空。"); }
        catch (JsonException ex) { throw new InvalidDataException("恢复组件哈希读回格式无效。", ex); }
    }

    public IReadOnlyCollection<ApplicationPolicyAuditEvent> ReadEvents(DateTimeOffset sinceUtc)
    {
        var result = Invoke("""
            $start = [DateTimeOffset]::Parse([string]$data.sinceUtc, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
            $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-AppLocker/EXE and DLL'; Id = 8003,8004; StartTime = $start } -MaxEvents 10001 -ErrorAction SilentlyContinue)
            if ($events.Count -gt 10000) { throw 'AUDIT_EVENT_LIMIT' }
            $items = foreach ($event in $events) {
                [xml]$xml = $event.ToXml()
                $fields = @{}
                foreach ($field in $xml.Event.EventData.Data) { $fields[[string]$field.Name] = [string]$field.InnerText }
                $sid = [string]$fields['TargetUserSid']
                if ([string]::IsNullOrWhiteSpace($sid)) { $sid = [string]$fields['UserSid'] }
                if ([string]::IsNullOrWhiteSpace($sid)) { $sid = [string]$fields['User'] }
                if ([string]::IsNullOrWhiteSpace($sid) -and $null -ne $event.UserId) { $sid = [string]$event.UserId.Value }
                [pscustomobject]@{
                    timeUtc = $event.TimeCreated.ToUniversalTime().ToString('O', [Globalization.CultureInfo]::InvariantCulture)
                    eventId = [int]$event.Id
                    userSid = $sid
                    ruleId = [string]$fields['RuleId']
                }
            }
            [Console]::Out.Write((ConvertTo-Json -InputObject @($items) -Compress -Depth 3))
            """, new { sinceUtc = sinceUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) });
        try
        {
            var items = JsonSerializer.Deserialize<AuditWireEvent[]>(result) ?? [];
            var events = new List<ApplicationPolicyAuditEvent>(items.Length);
            foreach (var item in items)
            {
                if (!DateTimeOffset.TryParseExact(item.TimeUtc, "O", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var eventTime) ||
                    !Guid.TryParse(item.RuleId, out var ruleId) || string.IsNullOrWhiteSpace(item.UserSid)) continue;
                events.Add(new ApplicationPolicyAuditEvent(eventTime.ToUniversalTime(), item.EventId, item.UserSid, ruleId));
            }
            return events.AsReadOnly();
        }
        catch (JsonException ex) { throw new InvalidDataException("AppLocker 审计事件读回格式无效。", ex); }
    }

    public void WriteLocalPolicyXml(string xml)
    {
        ApplicationPolicyRuntime.CanonicalXml(xml);
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        PathLinkSecurity.RejectLinks(parent);
        var directory = Path.Combine(parent, "VeyonCampus-AppLocker-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("AppLocker 暂存路径已存在。");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).Create(security);
        var path = Path.Combine(directory, "policy.xml");
        try
        {
            PathLinkSecurity.RejectLinks(directory);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(xml));
                stream.Flush(flushToDisk: true);
            }
            Invoke("Set-AppLockerPolicy -XmlPolicy ([string]$data.path) -ErrorAction Stop; [Console]::Out.Write('OK')", new { path });
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory, recursive: false);
        }
    }

    private static string Invoke(string script, object data)
    {
        var command = """
            $ErrorActionPreference = 'Stop'
            [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            try {
                $data = ConvertFrom-Json ([Console]::In.ReadToEnd()) -ErrorAction Stop
            """ + "\n" + script + "\n} catch { [Console]::Error.Write('APPLOCKER_OPERATION_FAILED'); exit 1 }";
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var runner = new ProcessRunner();
        runner.RunWithStandardInput(powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(command))],
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), TimeSpan.FromSeconds(45),
            JsonSerializer.Serialize(data), outputLimitChars: 8 * 1024 * 1024);
        if (runner.ExitCode != 0 || runner.StdoutTruncated || runner.StderrTruncated)
            throw new IOException("Windows AppLocker 操作失败或读回超过限制；没有确认策略应用成功。");
        return runner.Stdout;
    }

    private sealed record AuditWireEvent(
        [property: JsonPropertyName("timeUtc")] string TimeUtc,
        [property: JsonPropertyName("eventId")] int EventId,
        [property: JsonPropertyName("userSid")] string UserSid,
        [property: JsonPropertyName("ruleId")] string RuleId);
}
