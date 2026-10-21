[CmdletBinding()]
param(
    [Guid]$CaseId = [Guid]::NewGuid(),
    [ValidateSet('before', 'after', 'failed', 'rolledBack')]
    [string]$Phase = 'before',
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:TaskNamespace = 'http://schemas.microsoft.com/windows/2004/02/mit/task'
$script:ProgramDataRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
$script:ProgramFilesRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$script:AgentConfigRoot = Join-Path $script:ProgramDataRoot 'VeyonCampus\WebsitePolicy'
$script:AgentBinaryRoot = Join-Path $script:ProgramDataRoot 'VeyonCampus\WebsitePolicyAgent'
$script:RecoveryTaskNames = @(
    'VeyonCampus-StudentUpdate-Recovery',
    'VeyonCampus-StudentUpdate-Watchdog'
)

function Get-RelativePathUnderRoot {
    param([string]$Root, [string]$Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $fullRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    return $fullPath.Substring($prefix.Length)
}

function Test-ExistingPathWithoutReparsePoints {
    param([string]$Root, [string]$Path)
    try {
        $relative = Get-RelativePathUnderRoot -Root $Root -Path $Path
        if ([string]::IsNullOrWhiteSpace($relative)) { return $false }
        $current = [IO.Path]::GetFullPath($Root)
        if (-not (Test-Path -LiteralPath $current)) { return $false }
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        foreach ($segment in ($relative -split '[\\/]')) {
            $current = Join-Path $current $segment
            if (-not (Test-Path -LiteralPath $current)) { return $false }
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        }
        return $true
    }
    catch { return $false }
}

function Get-PrivacySalt {
    param([Guid]$Id)
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw '无法定位当前用户的本地应用数据目录。'
    }
    $saltDirectory = Join-Path $env:LOCALAPPDATA 'VeyonCampus\G0Evidence'
    $RequestedPath = Join-Path $saltDirectory ($Id.ToString('N') + '.salt')

    $fullPath = [IO.Path]::GetFullPath($RequestedPath)
    $parent = Split-Path -Parent $fullPath
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        $bytes = [IO.File]::ReadAllBytes($fullPath)
        if ($bytes.Length -ne 32) { throw '隐私盐文件长度无效；请为新验收案例使用新的 CaseId。' }
        return ,$bytes
    }

    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) }
    finally { $rng.Dispose() }
    $stream = [IO.FileStream]::new($fullPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    return ,$bytes
}

function Get-HmacTag {
    param([byte[]]$Key, [byte[]]$Value)
    if ($null -eq $Value) { return $null }
    $hmac = [Security.Cryptography.HMACSHA256]::new($Key)
    try { return ([BitConverter]::ToString($hmac.ComputeHash($Value))).Replace('-', '') }
    finally { $hmac.Dispose() }
}

function ConvertFrom-HexString {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length % 2 -ne 0 -or $Value -notmatch '^[A-Fa-f0-9]+$') {
        throw 'invalid hex'
    }
    $byteCount = [int]($Value.Length / 2)
    $bytes = New-Object byte[] $byteCount
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($Value.Substring($i * 2, 2), 16)
    }
    return ,$bytes
}

function Test-BytesEqual {
    param([byte[]]$Left, [byte[]]$Right)
    if ($null -eq $Left -or $null -eq $Right -or $Left.Length -ne $Right.Length) { return $false }
    $difference = 0
    for ($i = 0; $i -lt $Left.Length; $i++) { $difference = $difference -bor ($Left[$i] -bxor $Right[$i]) }
    return $difference -eq 0
}

function Get-JsonPropertyValue {
    param($Object, [string]$Name)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-RoleMarker {
    param([string]$Directory)
    $markerPath = Join-Path $Directory 'veyon-campus-role.json'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        return @{ status = 'not-found'; version = $null }
    }
    try {
        if (-not (Test-ExistingPathWithoutReparsePoints -Root $Directory -Path $markerPath)) {
            return @{ status = 'unsafe-path'; version = $null }
        }
        if ((Get-Item -LiteralPath $markerPath).Length -gt 4096) {
            return @{ status = 'invalid-size'; version = $null }
        }
        $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($marker.schemaVersion -ne 1 -or $marker.role -ne 'StudentSetup' -or
            $marker.product -ne 'VeyonCampus.StudentSetup' -or
            [string]::IsNullOrWhiteSpace([string]$marker.version)) {
            return @{ status = 'invalid-marker'; version = $null }
        }
        return @{ status = 'read-successfully'; version = [string]$marker.version }
    }
    catch { return @{ status = 'unavailable'; version = $null } }
}

function Test-RestrictedTaskAcl {
    param([string]$TaskName)
    $scheduler = $null
    $folder = $null
    $task = $null
    try {
        $scheduler = New-Object -ComObject Schedule.Service
        $scheduler.Connect()
        $folder = $scheduler.GetFolder('\')
        $task = $folder.GetTask('\' + $TaskName)
        $sddl = [string]$task.GetSecurityDescriptor(7)
        $descriptor = [regex]::Match($sddl,
            '^O:(?<owner>SY|BA)(?:G:(?:SY|BA|S-\d+(?:-\d+)+))?D:(?<flags>P(?:AI|AR)?)(?<aces>(?:\([^()]*\))*)$',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if (-not $descriptor.Success) { return $false }
        $acePattern = [regex]::new('\((?<type>[^;]*);(?<flags>[^;]*);(?<rights>[^;]*);(?<object>[^;]*);(?<inherited>[^;]*);(?<sid>[^;]*)\)',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        $aces = $acePattern.Matches($descriptor.Groups['aces'].Value)
        if ($aces.Count -ne 2 -or (($aces | ForEach-Object { $_.Value }) -join '') -cne $descriptor.Groups['aces'].Value) {
            return $false
        }
        $identities = @()
        foreach ($ace in $aces) {
            $sid = $ace.Groups['sid'].Value.ToUpperInvariant()
            $rights = $ace.Groups['rights'].Value.ToUpperInvariant()
            if ($ace.Groups['type'].Value.ToUpperInvariant() -ne 'A' -or
                $ace.Groups['flags'].Value.Length -ne 0 -or $ace.Groups['object'].Value.Length -ne 0 -or
                $ace.Groups['inherited'].Value.Length -ne 0 -or
                $rights -notin @('GA', 'FA', '0X001F01FF') -or
                $sid -notin @('SY', 'S-1-5-18', 'BA', 'S-1-5-32-544')) {
                return $false
            }
            $identities += if ($sid -in @('SY', 'S-1-5-18')) { 'SY' } else { 'BA' }
        }
        return (@($identities | Sort-Object -Unique) -join ',') -ceq 'BA,SY'
    }
    catch { return $null }
    finally {
        foreach ($item in @($task, $folder, $scheduler)) {
            if ($null -ne $item -and [Runtime.InteropServices.Marshal]::IsComObject($item)) {
                try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) }
                catch { }
            }
        }
    }
}

function Read-TaskSnapshot {
    param(
        [string]$TaskName,
        [ValidateSet('agent', 'recovery', 'watchdog')][string]$Kind,
        [string]$ExpectedConfigPath
    )
    $result = [ordered]@{
        queryStatus = 'unavailable'
        present = $false
        principalIsSystem = $null
        actionVersion = $null
        actionFileExists = $null
        configBindingMatches = $null
        restrictedAcl = $null
        expectedTriggerPresent = $null
        watchdogIntervalIsOneMinute = $null
        internalConfigPath = $null
    }
    try {
        $xmlLines = @(& schtasks.exe /Query /TN $TaskName /XML 2>$null)
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0 -or $xmlLines.Count -eq 0) { return $result }
        $xml = [System.Xml.XmlDocument]::new()
        $xml.LoadXml(($xmlLines -join [Environment]::NewLine))
        $namespace = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
        $namespace.AddNamespace('t', $script:TaskNamespace)
        $principal = $xml.SelectSingleNode('/t:Task/t:Principals/t:Principal/t:UserId', $namespace)
        $command = $xml.SelectSingleNode('/t:Task/t:Actions/t:Exec/t:Command', $namespace)
        $arguments = $xml.SelectSingleNode('/t:Task/t:Actions/t:Exec/t:Arguments', $namespace)
        if ($null -eq $principal -or $null -eq $command -or $null -eq $arguments) { return $result }

        $result.queryStatus = 'read-successfully'
        $result.present = $true
        $result.principalIsSystem = $principal.InnerText -eq 'S-1-5-18'
        $fullCommand = [IO.Path]::GetFullPath($command.InnerText.Trim().Trim('"'))
        $relative = Get-RelativePathUnderRoot -Root $script:AgentBinaryRoot -Path $fullCommand
        $parts = if ($null -ne $relative) { @($relative -split '[\\/]') } else { @() }
        $versionPattern = '^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?$'
        $managedAction = $parts.Count -eq 2 -and $parts[0] -match $versionPattern -and
            $parts[1] -ieq 'VeyonCampus.Agent.exe'
        if ($managedAction) {
            $result.actionVersion = $parts[0]
            $result.actionFileExists = Test-ExistingPathWithoutReparsePoints -Root $script:AgentBinaryRoot -Path $fullCommand
        }

        $argumentText = $arguments.InnerText.Trim()
        $configPattern = if ($Kind -eq 'agent') {
            '^--website-policy-agent\s+"(?<path>[^"]+)"$'
        }
        else { '^--recover-student-update\s+"(?<path>[^"]+)"$' }
        $argumentMatch = [regex]::Match($argumentText, $configPattern)
        if ($argumentMatch.Success) {
            $configPath = [IO.Path]::GetFullPath($argumentMatch.Groups['path'].Value)
            $relativeConfig = Get-RelativePathUnderRoot -Root $script:AgentConfigRoot -Path $configPath
            if ($null -eq $relativeConfig) {
                $validConfig = $false
            }
            else {
                $configParts = @($relativeConfig -split '[\\/]')
                $validConfig = $configParts.Count -eq 2 -and
                    $configParts[0] -match '^[0-9A-Fa-f]{24}$' -and
                    $configParts[1] -ieq ('agent-' + $configParts[0] + '.json')
            }
            if ($validConfig -and $Kind -eq 'agent') {
                $result.configBindingMatches = $true
                $result.internalConfigPath = $configPath
            }
            elseif ($validConfig -and -not [string]::IsNullOrWhiteSpace($ExpectedConfigPath)) {
                $result.configBindingMatches = [string]::Equals($configPath, $ExpectedConfigPath,
                    [StringComparison]::OrdinalIgnoreCase)
            }
        }

        $triggerPath = if ($Kind -eq 'agent') { '/t:Task/t:Triggers/t:BootTrigger' }
            elseif ($Kind -eq 'recovery') { '/t:Task/t:Triggers/t:BootTrigger' }
            else { '/t:Task/t:Triggers/t:CalendarTrigger' }
        $trigger = $xml.SelectSingleNode($triggerPath, $namespace)
        $result.expectedTriggerPresent = $null -ne $trigger
        if ($Kind -eq 'watchdog' -and $null -ne $trigger) {
            $interval = $trigger.SelectSingleNode('t:Repetition/t:Interval', $namespace)
            $result.watchdogIntervalIsOneMinute = $null -ne $interval -and $interval.InnerText -eq 'PT1M'
        }
        $result.restrictedAcl = Test-RestrictedTaskAcl -TaskName $TaskName
        return $result
    }
    catch { return $result }
}

function Get-ConfigFingerprint {
    param($Config)
    $deploymentId = ''
    $deploymentIdValue = Get-JsonPropertyValue -Object $Config -Name 'deploymentId'
    if ($null -ne $deploymentIdValue -and [string]$deploymentIdValue -ne '') {
        $parsedId = [Guid]::Empty
        if (-not [Guid]::TryParse([string]$deploymentIdValue, [ref]$parsedId)) { throw 'invalid' }
        $deploymentId = $parsedId.ToString()
    }
    $fields = @(
        [string](Get-JsonPropertyValue -Object $Config -Name 'campusId'),
        [string](Get-JsonPropertyValue -Object $Config -Name 'publicKeyPem'),
        [string](Get-JsonPropertyValue -Object $Config -Name 'telemetryEndpoint'),
        $deploymentId,
        [string](Get-JsonPropertyValue -Object $Config -Name 'applicationVersion'),
        [string](Get-JsonPropertyValue -Object $Config -Name 'applicationPolicyPublicKeyPem'),
        [string](Get-JsonPropertyValue -Object $Config -Name 'studentSystemPolicyPublicKeyPem')
    )
    $payload = [Text.Encoding]::UTF8.GetBytes(($fields -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ,($sha.ComputeHash($payload)) }
    finally { $sha.Dispose() }
}

function Test-PrivateFileAcl {
    param([string]$Path, [bool]$IsDirectory)
    try {
        $item = Get-Item -LiteralPath $Path -Force
        $acl = $item.GetAccessControl()
        if (-not $acl.AreAccessRulesProtected) { return $false }
        $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
        if ($rules.Count -ne 2) { return $false }
        $expectedSids = @('S-1-5-18', 'S-1-5-32-544')
        $seen = @()
        foreach ($rule in $rules) {
            $sid = $rule.IdentityReference.Value
            $expectedInheritance = if ($IsDirectory) {
                [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                    [Security.AccessControl.InheritanceFlags]::ObjectInherit
            } else { [Security.AccessControl.InheritanceFlags]::None }
            if ($sid -notin $expectedSids -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
                $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl -or
                $rule.InheritanceFlags -ne $expectedInheritance -or
                $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None -or $rule.IsInherited) {
                return $false
            }
            $seen += $sid
        }
        return (@($seen | Sort-Object -Unique) -join ',') -ceq 'S-1-5-18,S-1-5-32-544'
    }
    catch { return $null }
}

function Read-TransactionSnapshot {
    param([string]$ConfigPath, [byte[]]$Salt)
    $result = [ordered]@{
        journalStatus = 'not-found'
        state = $null
        previousSetupVersion = $null
        targetSetupVersion = $null
        previousAgentVersion = $null
        targetAgentVersion = $null
        commandCorrelationTag = $null
        journalAclRestricted = $null
        transactionRecoveryDirectoryPresent = $null
        previousSetupRecoveryPresent = $null
        failedCandidatePresent = $null
        installerStagingPresent = $null
        lastErrorPresent = $null
    }
    if ([string]::IsNullOrWhiteSpace($ConfigPath)) { return $result }
    try {
        $campusHash = [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($ConfigPath))
        if ($campusHash -notmatch '^[0-9A-Fa-f]{24}$') { return $result }
        $transactionsRoot = Join-Path $script:ProgramDataRoot 'VeyonCampus\StudentUpdateTransactions'
        $campusDirectory = Join-Path $transactionsRoot $campusHash
        $journalPath = Join-Path $campusDirectory 'student-update-transaction.json'
        if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf)) { return $result }
        if (-not (Test-ExistingPathWithoutReparsePoints -Root $transactionsRoot -Path $journalPath)) {
            $result.journalStatus = 'unsafe-path'
            return $result
        }
        if ((Get-Item -LiteralPath $journalPath).Length -gt 64KB) {
            $result.journalStatus = 'invalid-size'
            return $result
        }

        $result.journalStatus = 'unavailable'
        $result.journalAclRestricted = [ordered]@{
            transactionsRoot = if (Test-Path -LiteralPath $transactionsRoot -PathType Container) {
                Test-PrivateFileAcl -Path $transactionsRoot -IsDirectory $true
            } else { $null }
            campusDirectory = if (Test-Path -LiteralPath $campusDirectory -PathType Container) {
                Test-PrivateFileAcl -Path $campusDirectory -IsDirectory $true
            } else { $null }
            journal = Test-PrivateFileAcl -Path $journalPath -IsDirectory $false
        }
        $record = Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([int]$record.schemaVersion -ne 1 -or [string]$record.commandId -notmatch '^[0-9a-fA-F-]{36}$') {
            return $result
        }
        $result.journalStatus = 'read-successfully'
        $result.state = [string]$record.state
        $result.previousSetupVersion = [string]$record.previousSetupVersion
        $result.targetSetupVersion = [string]$record.targetSetupVersion
        $result.previousAgentVersion = [string]$record.previousAgentVersion
        $result.targetAgentVersion = [string]$record.targetAgentVersion
        $commandBytes = [Text.Encoding]::UTF8.GetBytes(([string]$record.commandId).ToLowerInvariant())
        $result.commandCorrelationTag = Get-HmacTag -Key $Salt -Value $commandBytes
        $result.lastErrorPresent = -not [string]::IsNullOrWhiteSpace([string]$record.lastError)
        $commandId = ([Guid]$record.commandId).ToString('N')
        $recoveryDirectory = Join-Path $script:ProgramFilesRoot ('Veyon Campus\UpdateRecovery\Student\' + $commandId)
        $previousDirectory = Join-Path $recoveryDirectory 'Previous'
        $failedDirectory = Join-Path $recoveryDirectory 'FailedCandidate'
        $stagingDirectory = Join-Path ([IO.Path]::GetDirectoryName($ConfigPath)) ('student-update-' + $commandId)
        $result.transactionRecoveryDirectoryPresent = Test-ExistingPathWithoutReparsePoints -Root (Join-Path $script:ProgramFilesRoot 'Veyon Campus\UpdateRecovery\Student') -Path $recoveryDirectory
        $result.previousSetupRecoveryPresent = Test-ExistingPathWithoutReparsePoints -Root $recoveryDirectory -Path $previousDirectory
        $result.failedCandidatePresent = Test-ExistingPathWithoutReparsePoints -Root $recoveryDirectory -Path $failedDirectory
        $result.installerStagingPresent = if (Test-ExistingPathWithoutReparsePoints -Root ([IO.Path]::GetDirectoryName($ConfigPath)) -Path $stagingDirectory) {
            @(Get-ChildItem -LiteralPath $stagingDirectory -Filter '*.exe' -File -ErrorAction SilentlyContinue).Count -gt 0
        } else { $false }
        return $result
    }
    catch { return $result }
}

function Get-HealthSnapshot {
    param([byte[]]$Salt, [byte[]]$ExpectedConfigFingerprint)
    $result = [ordered]@{
        status = 'unavailable'
        agentVersion = $null
        matchesTaskVersion = $null
        matchesLocalConfiguration = $null
        configurationCorrelationTag = $null
    }
    try {
        $request = [Net.HttpWebRequest]::Create('http://127.0.0.1:39173/health')
        $request.Method = 'GET'
        $request.Timeout = 3000
        $request.ReadWriteTimeout = 3000
        $request.AllowAutoRedirect = $false
        $request.Proxy = $null
        $response = $request.GetResponse()
        try {
            $reader = [IO.StreamReader]::new($response.GetResponseStream())
            try {
                $buffer = New-Object char[] 16
                $charactersRead = $reader.Read($buffer, 0, $buffer.Length)
                $body = [string]::new($buffer, 0, $charactersRead)
            }
            finally { $reader.Dispose() }
            $statusCode = [int]$response.StatusCode
            $headers = $response.Headers
        }
        finally { $response.Dispose() }
        $fingerprint = [string]$headers['X-VeyonCampus-Agent-Config']
        $version = [string]$headers['X-VeyonCampus-Agent-Version']
        if ($statusCode -ne 200 -or $body -ne 'ready' -or
            $fingerprint -notmatch '^[A-Fa-f0-9]{64}$' -or [string]::IsNullOrWhiteSpace($version)) {
            $result.status = 'invalid-response'
            return $result
        }
        $healthBytes = ConvertFrom-HexString -Value $fingerprint
        $result.status = 'healthy'
        $result.agentVersion = $version
        $result.matchesLocalConfiguration = Test-BytesEqual -Left $healthBytes -Right $ExpectedConfigFingerprint
        $result.configurationCorrelationTag = Get-HmacTag -Key $Salt -Value $healthBytes
        return $result
    }
    catch { return $result }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw '此采集器只能在 Windows 测试机上运行。'
}
if ($PSVersionTable.PSVersion.Major -lt 5) {
    throw '此采集器需要 Windows PowerShell 5.1 或更高版本。'
}
if ($CaseId -eq [Guid]::Empty) {
    throw 'CaseId 不能是空 GUID。'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($identity.User.Value -ne 'S-1-5-18' -and
    -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请使用管理员 PowerShell 运行；采集器需要读取 SYSTEM 任务和受保护 journal ACL。'
}

$salt = Get-PrivacySalt -Id $CaseId
$programFilesRoot = $script:ProgramFilesRoot
$programDataRoot = $script:ProgramDataRoot
$setupDirectory = Join-Path $programFilesRoot 'Veyon Campus\Student'
$setupMarker = Get-RoleMarker -Directory $setupDirectory
$setupExecutable = Join-Path $setupDirectory 'VeyonCampus.StudentSetup.exe'
$agentTask = Read-TaskSnapshot -TaskName 'VeyonCampus-WebsitePolicyAgent' -Kind agent -ExpectedConfigPath $null
$agentTaskXmlConfig = $agentTask.internalConfigPath
[void]$agentTask.Remove('internalConfigPath')

$configuration = [ordered]@{
    status = if ($null -ne $agentTaskXmlConfig -and (Test-Path -LiteralPath $agentTaskXmlConfig -PathType Leaf)) {
        'read-successfully'
    } elseif ($null -eq $agentTaskXmlConfig) { 'task-binding-unavailable' } else { 'not-found' }
    fileContinuityTag = $null
    agentIdentityKeyPresent = $false
    agentIdentityContinuityTag = $null
    runtimeFingerprintMatchesFile = $null
}
$configFingerprint = $null
if ($configuration.status -eq 'read-successfully') {
    try {
        if (-not (Test-ExistingPathWithoutReparsePoints -Root $script:AgentConfigRoot -Path $agentTaskXmlConfig)) {
            throw 'unsafe path'
        }
        if ((Get-Item -LiteralPath $agentTaskXmlConfig).Length -gt 64KB) { throw 'invalid size' }
        $configBytes = [IO.File]::ReadAllBytes($agentTaskXmlConfig)
        $configuration.fileContinuityTag = Get-HmacTag -Key $salt -Value $configBytes
        $configObject = [Text.Encoding]::UTF8.GetString($configBytes) | ConvertFrom-Json
        $configFingerprint = Get-ConfigFingerprint -Config $configObject
        $identityPath = Join-Path ([IO.Path]::GetDirectoryName($agentTaskXmlConfig)) 'agent-identity.json'
        if (Test-Path -LiteralPath $identityPath -PathType Leaf) {
            $configuration.agentIdentityKeyPresent = $true
            if (-not (Test-ExistingPathWithoutReparsePoints -Root $script:AgentConfigRoot -Path $identityPath) -or
                (Get-Item -LiteralPath $identityPath).Length -gt 32KB) { throw 'unsafe identity path' }
            $configuration.agentIdentityContinuityTag = Get-HmacTag -Key $salt -Value ([IO.File]::ReadAllBytes($identityPath))
        }
    }
    catch { $configuration.status = 'unavailable' }
}

$health = if ($null -ne $configFingerprint) {
    Get-HealthSnapshot -Salt $salt -ExpectedConfigFingerprint $configFingerprint
} else {
    [ordered]@{ status = 'not-checked'; agentVersion = $null; matchesTaskVersion = $null; matchesLocalConfiguration = $null; configurationCorrelationTag = $null }
}
if ($null -ne $health.agentVersion -and $null -ne $agentTask.actionVersion) {
    $health.matchesTaskVersion = [string]::Equals([string]$health.agentVersion, [string]$agentTask.actionVersion,
        [StringComparison]::OrdinalIgnoreCase)
}
$configuration.runtimeFingerprintMatchesFile = $health.matchesLocalConfiguration

$transaction = Read-TransactionSnapshot -ConfigPath $agentTaskXmlConfig -Salt $salt
$recoveryTasks = [ordered]@{
    boot = Read-TaskSnapshot -TaskName $script:RecoveryTaskNames[0] -Kind recovery -ExpectedConfigPath $agentTaskXmlConfig
    watchdog = Read-TaskSnapshot -TaskName $script:RecoveryTaskNames[1] -Kind watchdog -ExpectedConfigPath $agentTaskXmlConfig
}
[void]$recoveryTasks.boot.Remove('internalConfigPath')
[void]$recoveryTasks.watchdog.Remove('internalConfigPath')
$drives = @(Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction SilentlyContinue)
$programFilesDrive = [IO.Path]::GetPathRoot($programFilesRoot)
$programDataDrive = [IO.Path]::GetPathRoot($programDataRoot)
$programFilesVolume = $drives | Where-Object { $_.DeviceID -ieq $programFilesDrive.TrimEnd('\') } | Select-Object -First 1
$programDataVolume = $drives | Where-Object { $_.DeviceID -ieq $programDataDrive.TrimEnd('\') } | Select-Object -First 1
$os = Get-CimInstance -ClassName Win32_OperatingSystem

$report = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    caseId = $CaseId.ToString('D')
    phase = $Phase
    g0Acceptance = $false
    evidenceScope = 'read-only-local-snapshot; does-not-execute-update-or-recovery-tests'
    operatingSystem = [ordered]@{
        caption = [string]$os.Caption
        version = [string]$os.Version
        build = [string]$os.BuildNumber
        architecture = [string]$os.OSArchitecture
    }
    storage = [ordered]@{
        programFilesFreeBytes = if ($null -ne $programFilesVolume) { [long]$programFilesVolume.FreeSpace } else { $null }
        programDataFreeBytes = if ($null -ne $programDataVolume) { [long]$programDataVolume.FreeSpace } else { $null }
    }
    studentSetup = [ordered]@{
        markerStatus = $setupMarker.status
        markerVersion = $setupMarker.version
        executablePresent = Test-ExistingPathWithoutReparsePoints -Root $setupDirectory -Path $setupExecutable
    }
    activeAgent = [ordered]@{
        task = $agentTask
        health = $health
    }
    configurationContinuity = $configuration
    recoveryTasks = $recoveryTasks
    transaction = $transaction
}

$json = $report | ConvertTo-Json -Depth 8
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Write-Output $json
}
else {
    $fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $fullOutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and
        -not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    [IO.File]::WriteAllText($fullOutputPath, $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Write-Host '已保存脱敏只读快照；g0Acceptance 固定为 false。' -ForegroundColor Green
}
