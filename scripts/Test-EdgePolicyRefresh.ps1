[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-JsonPropertyValue {
    param([object]$Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

Write-Output '=== 当前执行上下文 ==='
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
[pscustomobject]@{
    User = $identity.Name
    UserSid = $identity.User.Value
    SessionId = (Get-Process -Id $PID).SessionId
    Elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} | Format-List

Write-Output '=== 相关进程 ==='
$processes = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -in @('msedge.exe', 'VeyonCampus.EdgePolicyExecutor.exe',
        'VeyonCampus.StudentCompanion.exe', 'VeyonCampus.Agent.exe')
}
$processRows = foreach ($process in $processes) {
    $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwner -ErrorAction SilentlyContinue
    $version = $null
    if ($process.Name -eq 'msedge.exe' -and $process.ExecutablePath -and
        (Test-Path -LiteralPath $process.ExecutablePath -PathType Leaf)) {
        $version = (Get-Item -LiteralPath $process.ExecutablePath).VersionInfo.ProductVersion
    }
    [pscustomobject]@{
        Process = $process.Name
        ProcessId = $process.ProcessId
        User = if ($owner.User) { "$($owner.Domain)\$($owner.User)" } else { '(unavailable)' }
        SessionId = $process.SessionId
        Version = $version
        Path = $process.ExecutablePath
    }
}
$processRows | Format-Table -AutoSize -Wrap

Write-Output '=== 执行器登录任务 ==='
$taskOutput = & "$env:windir\System32\schtasks.exe" /Query /TN 'Veyon Campus Edge Policy Executor' /FO LIST /V 2>$null
if ($LASTEXITCODE -eq 0) {
    $taskOutput | Where-Object { $_ -match 'TaskName|Status|Logon Mode|Run As User|任务名称|状态|登录模式|运行用户' }
}
else {
    Write-Output 'ScheduledTask=Missing'
}

Write-Output '=== Edge 机器策略摘要（不显示域名） ==='
$edgePolicyRoot = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge'
$policyRows = foreach ($listName in @('URLBlocklist', 'URLAllowlist')) {
    $listPath = Join-Path $edgePolicyRoot $listName
    $items = @()
    if (Test-Path -LiteralPath $listPath) {
        $key = Get-Item -LiteralPath $listPath
        foreach ($name in $key.GetValueNames()) {
            $index = 0
            if ([int]::TryParse($name, [ref]$index)) {
                $items += [string]$key.GetValue($name)
            }
        }
    }
    $canonical = [string]::Join("`n", ($items | Sort-Object))
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $sha = $hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)) }
    finally { $hasher.Dispose() }
    [pscustomobject]@{ List = $listName; Count = $items.Count; Sha256 = [BitConverter]::ToString($sha).Replace('-', '') }
}
$policyRows | Format-Table -AutoSize

Write-Output '=== Agent 策略元数据 ==='
& reg.exe query 'HKLM\SOFTWARE\VeyonCampus\WebsitePolicy' /v Revision 2>$null
& reg.exe query 'HKLM\SOFTWARE\VeyonCampus\WebsitePolicy' /v EdgeExecutionMode 2>$null

Write-Output '=== Agent 健康与签名刷新队列 ==='
try {
    $health = Invoke-WebRequest -Uri 'http://127.0.0.1:39174/health' -TimeoutSec 3 -UseBasicParsing
    [pscustomobject]@{
        HealthStatus = $health.StatusCode
        AgentVersion = $health.Headers['X-VeyonCampus-Agent-Version']
    } | Format-List
}
catch {
    Write-Output "AgentHealth=Unavailable; Error=$($_.Exception.GetType().Name)"
}
try {
    $response = Invoke-RestMethod -Uri 'http://127.0.0.1:39174/v1/edge-policy-refresh/local' -TimeoutSec 3
    $payload = Get-JsonPropertyValue $response 'payload'
    if ($null -eq $payload) { $payload = Get-JsonPropertyValue $response 'Payload' }
    if ($null -eq $payload) { $payload = $response }
    $requestId = Get-JsonPropertyValue $payload 'requestId'
    if ($null -eq $requestId) { $requestId = Get-JsonPropertyValue $payload 'RequestId' }
    $revision = Get-JsonPropertyValue $payload 'revision'
    if ($null -eq $revision) { $revision = Get-JsonPropertyValue $payload 'Revision' }
    $requestedUtc = Get-JsonPropertyValue $payload 'requestedUtc'
    if ($null -eq $requestedUtc) { $requestedUtc = Get-JsonPropertyValue $payload 'RequestedUtc' }
    $schemaVersion = Get-JsonPropertyValue $payload 'schemaVersion'
    if ($null -eq $schemaVersion) { $schemaVersion = Get-JsonPropertyValue $payload 'SchemaVersion' }
    [pscustomobject]@{
        RequestId = $requestId
        Revision = $revision
        RequestedUtc = $requestedUtc
        SchemaVersion = $schemaVersion
    } | Format-List
}
catch {
    Write-Output "RefreshQueue=Unavailable; Error=$($_.Exception.GetType().Name)"
}

Write-Output '=== 当前用户 Edge 执行器状态与日志 ==='
$executorDirectory = Join-Path $env:LOCALAPPDATA 'VeyonCampus/EdgePolicyExecutor'
$statePath = Join-Path $executorDirectory 'state.json'
$logPath = Join-Path $executorDirectory 'edge-refresh.log'
if (Test-Path -LiteralPath $statePath -PathType Leaf) {
    try {
        $stateFile = Get-Item -LiteralPath $statePath -Force
        if (($stateFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $stateFile.Length -gt 32KB) {
            throw [IO.InvalidDataException]::new('执行器状态文件包含链接或超过大小限制。')
        }
        $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
        [pscustomobject]@{
            Status = Get-JsonPropertyValue $state 'Status'
            Revision = Get-JsonPropertyValue $state 'Revision'
            BrowserVersion = Get-JsonPropertyValue $state 'BrowserVersion'
            AttemptCount = Get-JsonPropertyValue $state 'AttemptCount'
            UpdatedUtc = Get-JsonPropertyValue $state 'UpdatedUtc'
            ErrorCode = Get-JsonPropertyValue $state 'ErrorCode'
        } | Format-List
    }
    catch {
        Write-Output "ExecutorState=Unreadable; Error=$($_.Exception.GetType().Name)"
    }
}
else {
    Write-Output 'ExecutorState=Missing (确认执行器是否已随登录启动)'
}

if (Test-Path -LiteralPath $logPath -PathType Leaf) {
    try {
        $logFile = Get-Item -LiteralPath $logPath -Force
        if (($logFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $logFile.Length -gt 256KB) {
            throw [IO.InvalidDataException]::new('执行器日志包含链接或超过大小限制。')
        }
        $logRows = foreach ($line in (Get-Content -LiteralPath $logPath -Tail 10 -Encoding UTF8)) {
            try { $line | ConvertFrom-Json -ErrorAction Stop }
            catch { [pscustomobject]@{ eventName = 'UnreadableLogLine'; errorCode = 'LOG_PARSE_ERROR' } }
        }
        $logRows | Select-Object utc, status, revision, browserVersion, browserIntegrityLevelRid,
            sessionId, attemptCount, errorCode, eventName | Format-Table -AutoSize -Wrap
    }
    catch {
        Write-Output "ExecutorLog=Unreadable; Error=$($_.Exception.GetType().Name)"
    }
}
else {
    Write-Output 'ExecutorLog=Missing'
}
