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
        get => WindowsDefaultWallpaper.Resolve();
    }

    public void VerifyEnvironmentAndStudents(IReadOnlyList<string> studentSids,
        bool requireNetworkSettingsPageVisibility, bool requireDesktopWallpaperPolicy)
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
        var request = JsonSerializer.Serialize(new
        {
            sids = studentSids,
            requireNetworkSettingsPageVisibility,
            requireDesktopWallpaperPolicy,
            supportedSettingsPageVisibilityEditions = StudentSystemPolicyCompiler.SettingsPageVisibilitySupportedEditions
        });
        var output = InvokeScript(WindowsStudentSystemPolicyScripts.PreflightStudents, request);
        var groups = WindowsLocalGroupMembershipGraph.Parse(output);
        WindowsStudentSystemPolicyPreflight.VerifyStudentsNotPrivileged(studentSids, groups);
    }

    public IReadOnlyDictionary<string, StudentSystemPolicyValueState> ReadValues(IReadOnlyCollection<string> resources)
    {
        var result = new SortedDictionary<string, StudentSystemPolicyValueState>(StringComparer.Ordinal);
        var registry = new List<WireValue>();
        var registryAcls = new List<WireValue>();
        var accounts = new List<WireValue>();
        foreach (var resource in resources)
        {
            if (TryParseRegistryAcl(resource, out _, out _))
            {
                registryAcls.Add(new WireValue(resource, false, null));
            }
            else if (TryParseLsa(resource, out var sid, out var right))
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
        foreach (var value in InvokeRegistry("read-acl", registryAcls)) result.Add(value.Key,
            value.Exists ? new StudentSystemPolicyValueState(true, value.Value) : Missing);
        foreach (var value in InvokeAccounts("read", accounts)) result.Add(value.Key,
            value.Exists ? new StudentSystemPolicyValueState(true, value.Value) : Missing);
        return result;
    }

    public void WriteValues(IReadOnlyDictionary<string, StudentSystemPolicyValueState> values)
    {
        var registry = new List<WireValue>();
        var registryAcls = new List<WireValue>();
        var accounts = new List<WireValue>();
        foreach (var pair in values)
        {
            if (TryParseRegistryAcl(pair.Key, out _, out _))
            {
                var aclValue = pair.Value.Value;
                if (pair.Value.Exists)
                {
                    if (aclValue is null || aclValue.Kind is not ("registry-acl" or "registry-acl-sddl") ||
                        aclValue.Kind == "registry-acl" && aclValue != StudentSystemPolicyValue.RegistryAclReadOnly() ||
                        aclValue.Kind == "registry-acl-sddl" && (aclValue.Value.Length is 0 or > 16 * 1024))
                        throw new InvalidDataException("系统策略注册表 ACL 资源无效。");
                }
                else if (aclValue is not null)
                    throw new InvalidDataException("缺失的系统策略注册表 ACL 不得携带恢复值。");
                registryAcls.Add(new WireValue(pair.Key, pair.Value.Exists, pair.Value.Value));
            }
            else if (TryParseLsa(pair.Key, out var sid, out var right))
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
        _ = InvokeRegistry("write-acl", registryAcls);
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
$sections=[System.Security.AccessControl.AccessControlSections]::Owner -bor
 [System.Security.AccessControl.AccessControlSections]::Group -bor
 [System.Security.AccessControl.AccessControlSections]::Access
function Get-StudentHiveRoot([string]$sid) {
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
  }
  $loaded[$sid]=$mount
 }
 return "Registry::HKEY_USERS\$($loaded[$sid])"
}
function New-StudentReadOnlyRegistryAcl([string]$sid) {
 $security=[Microsoft.Win32.RegistrySecurity]::new()
 $security.SetAccessRuleProtection($true,$false)
 $security.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
 $security.SetGroup([System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
 $system=[System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
 $administrators=[System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
 $student=[System.Security.Principal.SecurityIdentifier]::new($sid)
 $none=[System.Security.AccessControl.InheritanceFlags]::None
 $propagation=[System.Security.AccessControl.PropagationFlags]::None
 $allow=[System.Security.AccessControl.AccessControlType]::Allow
 $security.AddAccessRule([System.Security.AccessControl.RegistryAccessRule]::new($system,[System.Security.AccessControl.RegistryRights]::FullControl,$none,$propagation,$allow))
 $security.AddAccessRule([System.Security.AccessControl.RegistryAccessRule]::new($administrators,[System.Security.AccessControl.RegistryRights]::FullControl,$none,$propagation,$allow))
 $security.AddAccessRule([System.Security.AccessControl.RegistryAccessRule]::new($student,[System.Security.AccessControl.RegistryRights]::ReadKey,$none,$propagation,$allow))
 return $security
}
function Test-StudentReadOnlyRegistryAcl([string]$path,[string]$sid) {
 $security=Get-Acl -LiteralPath $path -ErrorAction Stop
 if($security.GetOwner([System.Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-18' -or
    $security.GetGroup([System.Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544' -or
    -not $security.AreAccessRulesProtected) { return $false }
 $expected=@{}
 $expected['S-1-5-18']=[int][System.Security.AccessControl.RegistryRights]::FullControl
 $expected['S-1-5-32-544']=[int][System.Security.AccessControl.RegistryRights]::FullControl
 $expected[$sid]=[int][System.Security.AccessControl.RegistryRights]::ReadKey
 $rules=@($security.GetAccessRules($true,$true,[System.Security.Principal.SecurityIdentifier]))
 if($rules.Count -ne 3) { return $false }
 foreach($rule in $rules) {
  $identity=$rule.IdentityReference.Value
  if(-not $expected.ContainsKey($identity) -or $rule.IsInherited -or
     $rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or
     [int]$rule.RegistryRights -ne $expected[$identity] -or
     $rule.InheritanceFlags -ne [System.Security.AccessControl.InheritanceFlags]::None -or
     $rule.PropagationFlags -ne [System.Security.AccessControl.PropagationFlags]::None) { return $false }
  $null=$expected.Remove($identity)
 }
 return $expected.Count -eq 0
}
function Get-OriginalRegistryAclSddl([string]$path) {
 $security=Get-Acl -LiteralPath $path -ErrorAction Stop
 return $security.GetSecurityDescriptorSddlForm($sections)
}
try {
 foreach($item in @($data.values)) {
  $p=([string]$item.key).Split('|',4)
  if($p[0] -eq 'machine' -and $p.Length -eq 3) { $root='Registry::HKEY_LOCAL_MACHINE'; $sub=$p[1]; $name=$p[2] }
  elseif(($p[0] -eq 'user' -and $p.Length -eq 4) -or ($p[0] -eq 'user-acl' -and $p.Length -eq 3)) {
   $sid=$p[1]
   if($sid -notmatch '^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$') { throw 'Invalid student SID.' }
   $root=Get-StudentHiveRoot $sid
   $sub=$p[2]
   if($p[0] -eq 'user') { $name=$p[3] } else {
    $allowed=@('Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop','Software\Microsoft\Windows\CurrentVersion\Policies\System','Software\Policies\Microsoft\WindowsStore','Software\Policies\Microsoft\Windows\Network Connections','Software\Microsoft\Windows\CurrentVersion\Policies\Explorer')
    if($allowed -notcontains $sub) { throw 'Unapproved student policy ACL key.' }
   }
  } else { throw 'Invalid registry resource.' }
  $path=Join-Path $root $sub
  if($data.action -eq 'read-acl' -or $data.action -eq 'write-acl') {
   if($p[0] -ne 'user-acl') { throw 'Invalid registry ACL resource.' }
   if($data.action -eq 'read-acl') {
    $exists=Test-Path -LiteralPath $path
    $value=$null
    if($exists) {
     if(Test-StudentReadOnlyRegistryAcl $path $sid) { $value=[pscustomobject]@{kind='registry-acl';value='student-read-only-v1'} }
     else { $value=[pscustomobject]@{kind='registry-acl-sddl';value=(Get-OriginalRegistryAclSddl $path)} }
    }
    $result += [pscustomobject]@{key=[string]$item.key;exists=$exists;value=$value}
   } else {
    if([bool]$item.exists) {
     if(-not (Test-Path -LiteralPath $path)) { New-Item -Path $path -Force | Out-Null }
     if([string]$item.value.kind -eq 'registry-acl') {
      if([string]$item.value.value -ne 'student-read-only-v1') { throw 'Invalid managed registry ACL marker.' }
      $security=New-StudentReadOnlyRegistryAcl $sid
      Set-Acl -LiteralPath $path -AclObject $security -ErrorAction Stop
      if(-not (Test-StudentReadOnlyRegistryAcl $path $sid)) { throw 'Managed registry ACL did not read back.' }
     } elseif([string]$item.value.kind -eq 'registry-acl-sddl') {
      $security=[Microsoft.Win32.RegistrySecurity]::new()
      $security.SetSecurityDescriptorSddlForm([string]$item.value.value,$sections)
      Set-Acl -LiteralPath $path -AclObject $security -ErrorAction Stop
      if((Get-OriginalRegistryAclSddl $path) -ne [string]$item.value.value) { throw 'Original registry ACL did not read back.' }
     } else { throw 'Unsupported registry ACL value.' }
     $result += [pscustomobject]@{key=[string]$item.key;exists=$true;value=$item.value}
    } else {
     if($null -ne $item.value) { throw 'Missing registry ACL value cannot contain a descriptor.' }
     if(Test-Path -LiteralPath $path) {
      $key=Get-Item -LiteralPath $path
      if($key.GetValueNames().Count -ne 0 -or $key.GetSubKeyNames().Count -ne 0) { throw 'Cannot remove an originally absent policy key that now contains data.' }
      Remove-Item -LiteralPath $path -Force -ErrorAction Stop
     }
     $result += [pscustomobject]@{key=[string]$item.key;exists=$false;value=$null}
    }
   }
  } elseif($data.action -eq 'write') {
   if($p[0] -eq 'user-acl') { throw 'Invalid registry value resource.' }
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
   if($p[0] -eq 'user-acl') { throw 'Invalid registry value resource.' }
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
  } else { throw 'Invalid registry action.' }
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

    private static void ValidateRegistryResource(string resource)
    {
        var parts = resource.Split('|');
        if (parts.Length == 4 && parts[0] == "user" && parts[1].StartsWith("S-1-5-21-", StringComparison.Ordinal) &&
            (parts[2] == @"Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop" && parts[3] == "NoChangingWallPaper" ||
             parts[2] == @"Software\Microsoft\Windows\CurrentVersion\Policies\System" && parts[3] is "Wallpaper" or "WallpaperStyle" or "DisableChangePassword" ||
             parts[2] == @"Software\Policies\Microsoft\WindowsStore" && parts[3] == "RemoveWindowsStore" ||
             parts[2] == @"Software\Policies\Microsoft\Windows\Network Connections" &&
             parts[3] is "NC_LanProperties" or "NC_AddRemoveComponents" or "NC_AllowAdvancedTCPIPConfig" or
                 "NC_AdvancedSettings" or "NC_NewConnectionWizard" or "NC_RasMyProperties" or
                 "NC_RasAllUserProperties" or "NC_RasChangeProperties" or "NC_DeleteConnection" or
                 "NC_DeleteAllUserConnection" or "NC_RenameConnection" or "NC_RenameLanConnection" or
                 "NC_RenameMyRasConnection" or "NC_RenameAllUserRasConnection" ||
             parts[2] == @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" &&
                 parts[3] is "NoControlPanel" or "SettingsPageVisibility")) return;
        if (parts.Length == 3 && parts[0] == "machine" &&
            (parts[1] == @"Software\Policies\Microsoft\Windows\Installer" && parts[2] is "DisableMSI" or "DisableUserInstalls" ||
             parts[1] == @"Software\Policies\Microsoft\WindowsStore" && parts[2] == "RemoveWindowsStore" ||
             parts[1] == @"Software\Policies\Microsoft\Windows\Appx" && parts[2] == "BlockNonAdminUserInstall")) return;
        throw new InvalidDataException("系统策略拒绝未识别的注册表资源。");
    }

    private static bool TryParseRegistryAcl(string resource, out string sid, out string subKey)
    {
        sid = "";
        subKey = "";
        var parts = resource.Split('|');
        if (parts.Length != 3 || parts[0] != "user-acl") return false;
        sid = parts[1];
        subKey = parts[2];
        if (!System.Text.RegularExpressions.Regex.IsMatch(sid,
                @"^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            subKey is not ( @"Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop" or
                @"Software\Microsoft\Windows\CurrentVersion\Policies\System" or
                @"Software\Policies\Microsoft\WindowsStore" or
                @"Software\Policies\Microsoft\Windows\Network Connections" or
                @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
            throw new InvalidDataException("系统策略注册表 ACL 资源无效。");
        return true;
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
