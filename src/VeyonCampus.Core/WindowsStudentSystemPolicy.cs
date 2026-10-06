using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsStudentSystemPolicyStateStore : IStudentSystemPolicyStateStore
{
    private const int MaximumStateBytes = 4 * 1024 * 1024;
    private readonly string _campusId;
    private readonly string _directory;
    private readonly string _statePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public WindowsStudentSystemPolicyStateStore(string campusId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("系统策略状态存储仅支持 Windows。");
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        _campusId = campusId;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)));
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "StudentSystemPolicy", hash);
        _statePath = Path.Combine(_directory, "state.json");
    }

    public StudentSystemPolicyRuntimeState? Read()
    {
        if (!Directory.Exists(_directory)) return null;
        PathLinkSecurity.RejectLinks(_directory);
        VerifyAcl(_directory, true);
        if (!File.Exists(_statePath)) return null;
        PathLinkSecurity.RejectLinks(_statePath);
        VerifyAcl(_statePath, false);
        var info = new FileInfo(_statePath);
        if (info.Length is <= 0 or > MaximumStateBytes) throw new InvalidDataException("系统策略状态文件大小无效。");
        var bytes = File.ReadAllBytes(_statePath);
        if (bytes.Length != info.Length || bytes.Length > MaximumStateBytes)
            throw new InvalidDataException("系统策略状态读取期间发生变化。");
        PolicyJson.RejectDuplicateFields(bytes);
        var state = JsonSerializer.Deserialize<StudentSystemPolicyRuntimeState>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("系统策略状态为空。");
        Validate(state);
        return state;
    }

    public void Save(StudentSystemPolicyRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        Directory.CreateDirectory(_directory);
        PathLinkSecurity.RejectLinks(_directory);
        SecurePath(_directory, true);
        if (File.Exists(_statePath))
        {
            PathLinkSecurity.RejectLinks(_statePath);
            VerifyAcl(_statePath, false);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumStateBytes) throw new InvalidDataException("系统策略状态超过大小限制。");
        var temporary = Path.Combine(_directory, ".state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            SecurePath(temporary, false);
            File.Move(temporary, _statePath, overwrite: true);
            VerifyAcl(_statePath, false);
            var readBack = File.ReadAllBytes(_statePath);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(readBack)))
                throw new IOException("系统策略状态原子写入后摘要读回不一致。");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Validate(StudentSystemPolicyRuntimeState state)
    {
        if (state.CampusId != _campusId || state.Revision <= 0 || state.Revision != state.Policy.Revision ||
            state.Policy.CampusId != _campusId) throw new InvalidDataException("持久系统策略校区或版本不匹配。");
        StudentSystemPolicyCompiler.Validate(state.Policy);
        if (state.OriginalValues.Keys.Any(key => key.Length > 2048) ||
            state.InstalledValues.Keys.Any(key => key.Length > 2048) ||
            state.OriginalValues.Keys.Any(key => !state.InstalledValues.ContainsKey(key) && state.Pending == false &&
                StudentSystemPolicyCompiler.DesiredValues(state.Policy).ContainsKey(key)))
            throw new InvalidDataException("持久系统策略资源清单无效。");
        if (state.Pending && (state.PendingPreviousValues is null || state.PendingTargetValues is null))
            throw new InvalidDataException("持久系统策略事务缺少恢复数据。");
    }

    internal static bool HasAnyActiveState()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "StudentSystemPolicy");
        if (!Directory.Exists(root)) return false;
        PathLinkSecurity.RejectLinks(root);
        foreach (var path in Directory.EnumerateFiles(root, "state.json", SearchOption.AllDirectories))
        {
            try
            {
                PathLinkSecurity.RejectLinks(path);
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length is <= 0 or > MaximumStateBytes) return true;
                PolicyJson.RejectDuplicateFields(bytes);
                var state = JsonSerializer.Deserialize<StudentSystemPolicyRuntimeState>(bytes, JsonOptions);
                if (state is null) return true;
                var store = new WindowsStudentSystemPolicyStateStore(state.CampusId);
                if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(store._statePath), StringComparison.OrdinalIgnoreCase))
                    return true;
                VerifyAcl(store._directory, true);
                VerifyAcl(path, false);
                store.Validate(state);
                if (state.Pending || state.InstalledValues.Count > 0) return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                               System.Security.SecurityException or System.Text.Json.JsonException or ArgumentException)
            { return true; }
        }
        return false;
    }

    private static void SecurePath(string path, bool directory)
    {
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        var inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
        VerifyAcl(path, directory);
    }

    private static void VerifyAcl(string path, bool directory)
    {
        FileSystemSecurity actual = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        if (!actual.AreAccessRulesProtected) throw new UnauthorizedAccessException("系统策略状态 ACL 未受保护。");
        var rules = actual.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2 || rules.Any(rule => rule.AccessControlType != AccessControlType.Allow ||
                rule.FileSystemRights != FileSystemRights.FullControl || rule.IsInherited ||
                (rule.IdentityReference.Value is not "S-1-5-18" and not "S-1-5-32-544")))
            throw new UnauthorizedAccessException("系统策略状态 ACL 不是 SYSTEM/Administrators 专用。");
    }
}

/// <summary>Applies only policy values emitted by StudentSystemPolicyCompiler; external values are snapshotted and restored.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStudentSystemPolicyBackend : IStudentSystemPolicyBackend
{
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int PolicyLookupNames = 0x00000810;

    public string DefaultWallpaperPath
    {
        get => WindowsDefaultWallpaper.Resolve(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    public void VerifyEnvironmentAndStudents(IReadOnlyList<string> studentSids)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("系统策略只能由 SYSTEM Agent 或管理员执行。");
        foreach (var sid in studentSids.Distinct(StringComparer.Ordinal))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(sid,
                    @"^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidDataException("系统策略目标 SID 格式无效。");
        }
        var request = JsonSerializer.Serialize(new { sids = studentSids });
        InvokeScript(PreflightStudentsScript, request);
    }

    public IReadOnlyDictionary<string, StudentSystemPolicyValueState> ReadValues(IReadOnlyCollection<string> resources)
    {
        var result = new SortedDictionary<string, StudentSystemPolicyValueState>(StringComparer.Ordinal);
        var registry = new List<WireValue>();
        var accounts = new List<WireValue>();
        foreach (var resource in resources)
        {
            if (TryParseLsa(resource, out var sid, out var right))
            {
                var assigned = HasAccountRight(sid, right);
                result.Add(resource, new StudentSystemPolicyValueState(true, StudentSystemPolicyValue.Boolean(assigned)));
            }
            else if (TryParseAccount(resource, out _, out _))
            {
                accounts.Add(new WireValue(resource, false, null));
            }
            else
            {
                ValidateRegistryResource(resource);
                registry.Add(new WireValue(resource, false, null));
            }
        }
        foreach (var value in InvokeRegistry("read", registry)) result.Add(value.Key,
            value.Exists ? new StudentSystemPolicyValueState(true, value.Value) : Missing);
        foreach (var value in InvokeAccounts("read", accounts)) result.Add(value.Key,
            value.Exists ? new StudentSystemPolicyValueState(true, value.Value) : Missing);
        return result;
    }

    public void WriteValues(IReadOnlyDictionary<string, StudentSystemPolicyValueState> values)
    {
        var registry = new List<WireValue>();
        var accounts = new List<WireValue>();
        foreach (var pair in values)
        {
            if (TryParseLsa(pair.Key, out var sid, out var right))
            {
                if (!pair.Value.Exists || pair.Value.Value?.Kind != "boolean" ||
                    !bool.TryParse(pair.Value.Value.Value, out var assigned))
                    throw new InvalidDataException("LSA 权限恢复值格式无效。");
                SetAccountRight(sid, right, assigned);
            }
            else if (TryParseAccount(pair.Key, out _, out _))
            {
                if (!pair.Value.Exists || pair.Value.Value?.Kind != "boolean" ||
                    !bool.TryParse(pair.Value.Value.Value, out _))
                    throw new InvalidDataException("本地账户密码权限恢复值格式无效。");
                accounts.Add(new WireValue(pair.Key, pair.Value.Exists, pair.Value.Value));
            }
            else
            {
                ValidateRegistryResource(pair.Key);
                if (pair.Value.Exists && pair.Value.Value?.Kind is not ("dword" or "registry-dword" or "string"))
                    throw new InvalidDataException("系统策略注册表值类型无效。");
                registry.Add(new WireValue(pair.Key, pair.Value.Exists, pair.Value.Value));
            }
        }
        _ = InvokeRegistry("write", registry);
        _ = InvokeAccounts("write", accounts);
    }

    private static WireReadValue[] InvokeRegistry(string action, IReadOnlyCollection<WireValue> values)
    {
        var request = JsonSerializer.Serialize(new { action, values });
        var output = InvokeScript(RegistryScript, request);
        try
        {
            var response = JsonSerializer.Deserialize<WireResponse>(output, new JsonSerializerOptions
                { PropertyNameCaseInsensitive = true })
                           ?? throw new InvalidDataException("系统策略注册表读回为空。");
            return response.Values;
        }
        catch (JsonException exception) { throw new InvalidDataException("系统策略注册表响应无效。", exception); }
    }

    private static WireReadValue[] InvokeAccounts(string action, IReadOnlyCollection<WireValue> values)
    {
        if (values.Count == 0) return Array.Empty<WireReadValue>();
        var request = JsonSerializer.Serialize(new { action, values });
        var output = InvokeScript(AccountScript, request);
        try
        {
            var response = JsonSerializer.Deserialize<WireResponse>(output, new JsonSerializerOptions
                { PropertyNameCaseInsensitive = true })
                           ?? throw new InvalidDataException("学生账户权限读回为空。");
            return response.Values;
        }
        catch (JsonException exception) { throw new InvalidDataException("学生账户权限响应无效。", exception); }
    }

    private static string InvokeScript(string script, string json)
    {
        var command = "$ErrorActionPreference='Stop'\n[Console]::InputEncoding=[Text.UTF8Encoding]::new($false)\n" +
                      "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)\n$data=ConvertFrom-Json ([Console]::In.ReadToEnd()) -ErrorAction Stop\n" + script;
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32",
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var runner = new ProcessRunner();
        runner.RunWithStandardInput(executable,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))],
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), TimeSpan.FromSeconds(60), json,
            outputLimitChars: 1024 * 1024);
        if (runner.ExitCode != 0 || runner.StdoutTruncated || runner.StderrTruncated)
            throw new IOException("Windows 学生机系统策略预检或注册表操作失败；没有确认限制已应用。");
        return runner.Stdout;
    }

    private const string RegistryScript = """
$result=@()
$loaded=@{}
try {
 foreach($item in @($data.values)) {
  $p=([string]$item.key).Split('|',4)
  if($p[0] -eq 'machine' -and $p.Length -eq 3) { $root='Registry::HKEY_LOCAL_MACHINE'; $sub=$p[1]; $name=$p[2] }
  elseif($p[0] -eq 'user' -and $p.Length -eq 4) {
   $sid=$p[1]
   if($sid -notmatch '^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$') { throw 'Invalid student SID.' }
   if(-not $loaded.ContainsKey($sid)) {
    $profile=Get-CimInstance Win32_UserProfile -ErrorAction Stop | Where-Object { $_.SID -eq $sid -and -not $_.Special } | Select-Object -First 1
    if($null -eq $profile -or [string]::IsNullOrWhiteSpace($profile.LocalPath)) { throw 'Student profile is missing.' }
    $hiveFile=Join-Path $profile.LocalPath 'NTUSER.DAT'
    if(-not (Test-Path -LiteralPath $hiveFile -PathType Leaf) -or ((Get-Item -LiteralPath $profile.LocalPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Student profile path is unsafe.' }
    if($profile.Loaded) { $mount=$sid }
    else {
     $sha=[Security.Cryptography.SHA256]::Create()
     try { $digest=$sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sid)) } finally { $sha.Dispose() }
     $mount='VeyonCampus_'+([BitConverter]::ToString($digest).Replace('-','').Substring(0,24))
     & "$env:SystemRoot\System32\reg.exe" load "HKU\$mount" $hiveFile | Out-Null
     if($LASTEXITCODE -ne 0) { throw 'Unable to load student profile hive.' }
     $loaded[$sid]=$mount
    }
    if(-not $loaded.ContainsKey($sid)) { $loaded[$sid]=$mount }
   }
   $root="Registry::HKEY_USERS\$($loaded[$sid])"; $sub=$p[2]; $name=$p[3]
  }
  $path=Join-Path $root $sub
  if($data.action -eq 'write') {
   if([bool]$item.exists) {
    New-Item -Path $path -Force | Out-Null
    if([string]$item.value.kind -eq 'dword') {
     $v=[int]$item.value.value
     New-ItemProperty -LiteralPath $path -Name $name -Value $v -PropertyType DWord -Force | Out-Null
    } elseif([string]$item.value.kind -eq 'string') {
     New-ItemProperty -LiteralPath $path -Name $name -Value ([string]$item.value.value) -PropertyType String -Force | Out-Null
    } else { throw 'Unsupported registry value type.' }
   } else { if(Test-Path -LiteralPath $path) { Remove-ItemProperty -LiteralPath $path -Name $name -ErrorAction SilentlyContinue } }
  } elseif($data.action -eq 'read') {
   $exists=$false; $value=$null
   if(Test-Path -LiteralPath $path) {
    $key=Get-Item -LiteralPath $path
    if($key.GetValueNames() -contains $name) {
     $kind=$key.GetValueKind($name)
     if($kind -eq [Microsoft.Win32.RegistryValueKind]::DWord) {
      $exists=$true; $value=[pscustomobject]@{kind='dword';value=[string]$key.GetValue($name)}
     } elseif($kind -eq [Microsoft.Win32.RegistryValueKind]::String) {
      $exists=$true; $value=[pscustomobject]@{kind='string';value=[string]$key.GetValue($name)}
     } else { throw 'Managed registry value exists with an unsupported type.' }
    }
   }
   $result += [pscustomobject]@{key=[string]$item.key;exists=$exists;value=$value}
  } else { throw 'Invalid registry resource or action.' }
 }
 [Console]::Out.Write((ConvertTo-Json -InputObject ([pscustomobject]@{values=@($result)}) -Compress -Depth 4))
} finally {
 foreach($mount in @($loaded.Values | Where-Object { $_ -notmatch '^S-1-5-21-' })) {
  & "$env:SystemRoot\System32\reg.exe" unload "HKU\$mount" | Out-Null
  if($LASTEXITCODE -ne 0) { throw 'Unable to unload student profile hive.' }
 }
}
""";

    private const string AccountScript = """
$result=@()
foreach($item in @($data.values)) {
 $p=([string]$item.key).Split('|',3)
 if($p.Length -ne 3 -or $p[0] -ne 'account' -or $p[2] -ne 'PasswordChangeable' -or
    $p[1] -notmatch '^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$') { throw 'Invalid local account resource.' }
 $sid=[string]$p[1]
 $accounts=@(Get-CimInstance Win32_UserAccount -Filter "LocalAccount=True AND SID='$sid'" -ErrorAction Stop)
 if($accounts.Count -ne 1) { throw 'Student local account is missing or ambiguous.' }
 $account=$accounts[0]
 if($data.action -eq 'write') {
  $changeable=[bool]::Parse([string]$item.value.value)
  $net=Join-Path $env:SystemRoot 'System32\net.exe'
  $switch=if($changeable) { '/passwordchg:yes' } else { '/passwordchg:no' }
  $null=& $net ([string]$account.Name) $switch 2>&1
  if($LASTEXITCODE -ne 0) { throw 'Unable to update the local account password-change flag.' }
  $account=Get-CimInstance Win32_UserAccount -Filter "LocalAccount=True AND SID='$sid'" -ErrorAction Stop
  if([bool]$account.PasswordChangeable -ne $changeable) { throw 'Local account password-change flag did not read back.' }
 }
 $result += [pscustomobject]@{key=[string]$item.key;exists=$true;value=[pscustomobject]@{kind='boolean';value=([string][bool]$account.PasswordChangeable).ToLowerInvariant()}}
}
[Console]::Out.Write((ConvertTo-Json -InputObject ([pscustomobject]@{values=@($result)}) -Compress -Depth 4))
""";

    private const string PreflightStudentsScript = """
$computer=Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
if($computer.PartOfDomain) { throw 'Domain-managed computer requires review.' }
$enrollments=Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Enrollments' -ErrorAction SilentlyContinue
foreach($entry in $enrollments) { $x=Get-ItemProperty -LiteralPath $entry.PSPath; if($x.ProviderID) { throw 'MDM enrollment requires review.' } }
$admins=@(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop)
$restricted=@()
foreach($groupSid in @('S-1-5-32-548','S-1-5-32-556')) { $restricted += @(Get-LocalGroupMember -SID $groupSid -ErrorAction Stop) }
foreach($sid in $data.sids) {
 $account=Get-LocalUser -SID ([Security.Principal.SecurityIdentifier]::new([string]$sid)) -ErrorAction Stop
 if(-not $account.Enabled -or $account.PrincipalSource -ne 'Local') { throw 'Student must be an enabled local account.' }
 if(@($admins | Where-Object { $_.SID.Value -eq $sid }).Count -gt 0) { throw 'Student must not be an administrator.' }
 if(@($restricted | Where-Object { $_.SID.Value -eq $sid }).Count -gt 0) { throw 'Student belongs to an account/network operator group.' }
 $profile=Get-CimInstance Win32_UserProfile -ErrorAction Stop | Where-Object { $_.SID -eq $sid -and -not $_.Special } | Select-Object -First 1
 if($null -eq $profile -or -not (Test-Path -LiteralPath (Join-Path $profile.LocalPath 'NTUSER.DAT') -PathType Leaf)) { throw 'Student profile is not ready.' }
}
[Console]::Out.Write('OK')
""";

    private static void ValidateRegistryResource(string resource)
    {
        var parts = resource.Split('|');
        if (parts.Length == 4 && parts[0] == "user" && parts[1].StartsWith("S-1-5-21-", StringComparison.Ordinal) &&
            (parts[2] == @"Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop" && parts[3] == "NoChangingWallPaper" ||
             parts[2] == @"Software\Microsoft\Windows\CurrentVersion\Policies\System" && parts[3] is "Wallpaper" or "WallpaperStyle" or "DisableChangePassword" ||
             parts[2] == @"Software\Policies\Microsoft\WindowsStore" && parts[3] == "RemoveWindowsStore" ||
             parts[2] == @"Software\Policies\Microsoft\Windows\Network Connections" &&
             parts[3] is "NC_LanProperties" or "NC_AddRemoveComponents" or "NC_AllowAdvancedTCPIPConfig" or "NC_AdvancedSettings" or "NC_NewConnectionWizard" ||
             parts[2] == @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" && parts[3] == "NoControlPanel")) return;
        if (parts.Length == 3 && parts[0] == "machine" &&
            (parts[1] == @"Software\Policies\Microsoft\Windows\Installer" && parts[2] is "DisableMSI" or "DisableUserInstalls" ||
             parts[1] == @"Software\Policies\Microsoft\WindowsStore" && parts[2] == "RemoveWindowsStore" ||
             parts[1] == @"Software\Policies\Microsoft\Windows\Appx" && parts[2] == "BlockNonAdminUserInstall")) return;
        throw new InvalidDataException("系统策略拒绝未识别的注册表资源。");
    }

    private static bool TryParseLsa(string resource, out string sid, out string right)
    {
        sid = ""; right = "";
        var parts = resource.Split('|');
        if (parts.Length != 3 || parts[0] != "lsa" ||
            parts[2] is not ("SeSystemtimePrivilege" or "SeTimeZonePrivilege")) return false;
        sid = parts[1]; right = parts[2];
        if (!System.Text.RegularExpressions.Regex.IsMatch(sid,
                @"^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("LSA 系统策略 SID 格式无效。");
        return true;
    }

    private static bool TryParseAccount(string resource, out string sid, out string property)
    {
        sid = ""; property = "";
        var parts = resource.Split('|');
        if (parts.Length != 3 || parts[0] != "account" || parts[2] != "PasswordChangeable") return false;
        sid = parts[1]; property = parts[2];
        if (!System.Text.RegularExpressions.Regex.IsMatch(sid,
                @"^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("本地账户系统策略 SID 格式无效。");
        return true;
    }

    private static bool HasAccountRight(string sidValue, string right)
    {
        using var policy = OpenPolicy();
        var sid = new SecurityIdentifier(sidValue);
        var sidBytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(sidBytes, 0);
        var status = LsaEnumerateAccountRights(policy, sidBytes, out var buffer, out var count);
        if (status == StatusObjectNameNotFound) return false;
        ThrowLsa(status, "读取学生账户系统权限");
        try
        {
            var size = Marshal.SizeOf<LsaUnicodeString>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<LsaUnicodeString>(IntPtr.Add(buffer, i * size));
                if (Marshal.PtrToStringUni(item.Buffer, item.Length / 2) == right) return true;
            }
            return false;
        }
        finally { LsaFreeMemory(buffer); }
    }

    private static void SetAccountRight(string sidValue, string right, bool assigned)
    {
        using var policy = OpenPolicy();
        var sid = new SecurityIdentifier(sidValue);
        var sidBytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(sidBytes, 0);
        if (assigned == HasAccountRight(sidValue, right)) return;
        var rightName = MakeLsaString(right);
        try
        {
            var status = assigned
                ? LsaAddAccountRights(policy, sidBytes, [rightName], 1)
                : LsaRemoveAccountRights(policy, sidBytes, false, [rightName], 1);
            ThrowLsa(status, "更新学生账户系统权限");
        }
        finally { Marshal.FreeHGlobal(rightName.Buffer); }
    }

    private static SafeLsaPolicyHandle OpenPolicy()
    {
        var attributes = new LsaObjectAttributes { Length = Marshal.SizeOf<LsaObjectAttributes>() };
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyLookupNames, out var handle);
        ThrowLsa(status, "打开本地安全策略");
        return handle;
    }

    private static LsaUnicodeString MakeLsaString(string text)
    {
        var value = new LsaUnicodeString { Buffer = Marshal.StringToHGlobalUni(text), Length = checked((ushort)(text.Length * 2)), MaximumLength = checked((ushort)(text.Length * 2 + 2)) };
        return value;
    }

    private static void ThrowLsa(int status, string operation)
    {
        if (status == 0) return;
        var error = LsaNtStatusToWinError(status);
        throw new System.ComponentModel.Win32Exception(error, operation + "失败。");
    }

    private sealed class SafeLsaPolicyHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeLsaPolicyHandle() : base(true) { }
        protected override bool ReleaseHandle() => LsaClose(handle) == 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct LsaObjectAttributes
    { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public int Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct LsaUnicodeString
    { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    private sealed record WireValue(string Key, bool Exists, StudentSystemPolicyValue? Value);
    private sealed record WireResponse(WireReadValue[] Values);
    private sealed record WireReadValue(string Key, bool Exists, StudentSystemPolicyValue? Value);
    private static readonly StudentSystemPolicyValueState Missing = new(false, null);

    [DllImport("advapi32.dll")] private static extern int LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes attributes, int access, out SafeLsaPolicyHandle handle);
    [DllImport("advapi32.dll")] private static extern int LsaClose(IntPtr handle);
    [DllImport("advapi32.dll")] private static extern int LsaNtStatusToWinError(int status);
    [DllImport("advapi32.dll")] private static extern int LsaEnumerateAccountRights(SafeLsaPolicyHandle policy, byte[] accountSid, out IntPtr userRights, out uint countOfRights);
    [DllImport("advapi32.dll")] private static extern int LsaAddAccountRights(SafeLsaPolicyHandle policy, byte[] accountSid, [In] LsaUnicodeString[] userRights, uint countOfRights);
    [DllImport("advapi32.dll")] private static extern int LsaRemoveAccountRights(SafeLsaPolicyHandle policy, byte[] accountSid, [MarshalAs(UnmanagedType.U1)] bool allRights, [In] LsaUnicodeString[] userRights, uint countOfRights);
    [DllImport("advapi32.dll")] private static extern int LsaFreeMemory(IntPtr buffer);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsStudentSystemPolicyAgent
{
    public WindowsStudentSystemPolicyAgent(string campusId, string publicKeyPem,
        IStudentSoftwareExecutionPolicyCoordinator? softwareExecutionPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        Runtime = new StudentSystemPolicyRuntime(new WindowsStudentSystemPolicyBackend(),
            new WindowsStudentSystemPolicyStateStore(campusId), campusId, publicKeyPem, softwareExecutionPolicy);
    }

    public StudentSystemPolicyRuntime Runtime { get; }

    public static bool HasState(string campusId)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)));
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "StudentSystemPolicy", hash, "state.json");
        if (!Directory.Exists(Path.GetDirectoryName(path))) return false;
        PathLinkSecurity.RejectLinks(Path.GetDirectoryName(path)!);
        return File.Exists(path);
    }

    public static bool HasAnyState()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "StudentSystemPolicy");
        if (!Directory.Exists(root)) return false;
        PathLinkSecurity.RejectLinks(root);
        return Directory.EnumerateFiles(root, "state.json", SearchOption.AllDirectories).Any();
    }

    public static bool HasAnyActiveState() => WindowsStudentSystemPolicyStateStore.HasAnyActiveState();
}
