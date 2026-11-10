<#
.SYNOPSIS
    只读诊断教师机与教室学生机的连接前提，解释课堂管控按钮为什么不可用。

.DESCRIPTION
    在教师机上运行，不修改任何系统状态：不启动/停止服务、不写注册表、不发送策略。
    输出 Veyon 目录、认证密钥、校区签名密钥、教师通道端口和各学生机可达性，
    最后给出“课堂管控不可用”的候选原因，供现场对照处理。

.PARAMETER OutputPath
    可选，额外把完整结果写入该文件（UTF-8）。

.PARAMETER SkipNetworkProbe
    跳过逐台学生机 DNS/TCP 探测，只在机器很多或网络受限时使用。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\diagnose-classroom-connection.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\diagnose-classroom-connection.ps1 -OutputPath .\classroom-diagnosis.txt
#>
[CmdletBinding()]
param(
    [string]$OutputPath,
    [switch]$SkipNetworkProbe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = New-Object System.Text.UTF8Encoding($false)
}
catch { }

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
    throw '此脚本只能在 Windows 教师机上运行。'
}

$lines = New-Object System.Collections.Generic.List[string]
function Add-Line {
    param([string]$Text = '')
    $lines.Add($Text)
    Write-Host $Text
}

function Get-RegistryValueString {
    param([string]$Path, [string]$Name)
    try {
        $item = Get-ItemProperty -Path $Path -Name $Name -ErrorAction Stop
        return [string]$item.$Name
    }
    catch { return $null }
}

# ---------- 环境 ----------
Add-Line '=== 1. 环境 ==='
Add-Line ("计算机名        : {0}" -f $env:COMPUTERNAME)
Add-Line ("当前用户        : {0}" -f [Security.Principal.WindowsIdentity]::GetCurrent().Name)
$isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Add-Line ("管理员权限      : {0}" -f $(if ($isElevated) { '是' } else { '否（注册表 HKCU 仍可读）' }))

$installRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)
$installed = @()
foreach ($root in $installRoots) {
    $installed += @(Get-ItemProperty $root -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties.Name -contains 'DisplayName' -and $_.DisplayName -like '*Veyon*' } |
        Select-Object DisplayName, DisplayVersion)
}
if ($installed.Count -eq 0) {
    Add-Line '已安装组件    : 未发现 Veyon 相关安装记录'
}
else {
    foreach ($item in $installed) {
        Add-Line ("已安装组件    : {0} {1}" -f $item.DisplayName, $item.DisplayVersion)
    }
}

$teacherProcess = @(Get-Process -Name 'VeyonCampus.Teacher' -ErrorAction SilentlyContinue)
Add-Line ("教师端进程    : {0}" -f $(if ($teacherProcess.Count -gt 0) { '运行中' } else { '未运行' }))

$addresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' })
foreach ($address in $addresses) {
    Add-Line ("本机 IPv4     : {0} ({1})" -f $address.IPAddress, $address.InterfaceAlias)
}

# ---------- Veyon 服务与端口 ----------
Add-Line ''
Add-Line '=== 2. Veyon 服务与端口 ==='
$veyonService = Get-Service -Name 'VeyonService' -ErrorAction SilentlyContinue
if ($null -eq $veyonService) {
    Add-Line 'VeyonService  : 未安装'
}
else {
    Add-Line ("VeyonService  : {0} / {1}" -f $veyonService.Status, $veyonService.StartType)
}

$veyonRoot = 'HKLM:\SOFTWARE\Veyon Solutions\Veyon'
$serverPort = Get-RegistryValueString -Path "$veyonRoot\Network" -Name 'VeyonServerPort'
if ([string]::IsNullOrWhiteSpace($serverPort)) { $serverPort = '11100' }
Add-Line ("VeyonServerPort: {0}" -f $serverPort)
$authMethod = Get-RegistryValueString -Path "$veyonRoot\Authentication" -Name 'Method'
$authKey = Get-RegistryValueString -Path "$veyonRoot\Authentication" -Name 'Key'
Add-Line ("认证方式       : Method={0} Key={1}" -f $authMethod, $(if ([string]::IsNullOrWhiteSpace($authKey)) { '(未显式指定，使用默认密钥)' } else { $authKey }))

$listeningPorts = @(39175, 39176, 39177, [int]$serverPort)
foreach ($port in $listeningPorts) {
    $listener = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
    Add-Line ("监听端口 {0,-6}: {1}" -f $port, $(if ($listener.Count -gt 0) { '正在监听' } else { '未监听' }))
}

$firewallRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like '*Veyon*' } |
    Select-Object DisplayName, Enabled, Direction, Action)
if ($firewallRules.Count -eq 0) {
    Add-Line '防火墙规则     : 未发现名称包含 Veyon 的规则'
}
else {
    foreach ($rule in $firewallRules) {
        Add-Line ("防火墙规则     : {0} | Enabled={1} {2} {3}" -f $rule.DisplayName, $rule.Enabled, $rule.Direction, $rule.Action)
    }
}

# ---------- Veyon CLI ----------
Add-Line ''
Add-Line '=== 3. Veyon 地点与电脑 ==='
$veyonCli = $null
foreach ($candidate in @('C:\Program Files\Veyon\veyon-cli.exe', 'C:\Program Files (x86)\Veyon\veyon-cli.exe')) {
    if (Test-Path $candidate) { $veyonCli = $candidate; break }
}

$networkObjects = @()
if ($null -eq $veyonCli) {
    Add-Line 'veyon-cli     : 未找到，无法读取地点与电脑'
}
else {
    Add-Line ("veyon-cli     : {0}" -f $veyonCli)
    try {
        $authKeys = @(& $veyonCli authkeys list 2>&1 | Where-Object { $_ -match '/' })
        Add-Line ("认证密钥       : {0}" -f $(if ($authKeys.Count -gt 0) { ($authKeys -join ', ') } else { '未列出任何密钥（教师机没有认证密钥，学生机无法通过认证）' }))
    }
    catch {
        Add-Line ("认证密钥       : 读取失败 - {0}" -f $_.Exception.Message)
    }

    try {
        $configOutput = & $veyonCli config list 2>&1
        $networkLine = @($configOutput | Where-Object { $_ -like 'BuiltinDirectory/NetworkObjects=*' }) | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($networkLine)) {
            Add-Line '地点与电脑     : Veyon 内置目录为空（教师端无法列出任何学生机）'
        }
        else {
            $json = $networkLine.Substring('BuiltinDirectory/NetworkObjects='.Length)
            $networkObjects = @($json | ConvertFrom-Json)
            Add-Line ("网络对象数量   : {0}" -f $networkObjects.Count)
        }
    }
    catch {
        Add-Line ("地点与电脑     : 读取失败 - {0}" -f $_.Exception.Message)
    }
}

$locations = @($networkObjects | Where-Object { $_.Type -eq 2 })
$computers = @($networkObjects | Where-Object { $_.Type -eq 3 })
Add-Line ("地点数量       : {0}" -f $locations.Count)
Add-Line ("电脑数量       : {0}" -f $computers.Count)
foreach ($location in $locations) {
    $children = @($computers | Where-Object { $_.ParentUid -eq $location.Uid })
    Add-Line ("  - 地点 {0} ({1}) : {2} 台" -f $location.Name, $location.Uid, $children.Count)
}

# ---------- 学生机可达性 ----------
Add-Line ''
Add-Line '=== 4. 学生机可达性 ==='
if ($SkipNetworkProbe) {
    Add-Line '已按参数跳过逐台探测。'
}
elseif ($computers.Count -eq 0) {
    Add-Line '没有可探测的电脑。'
}
else {
    $probeResults = @()
    foreach ($computer in $computers) {
        $hostAddress = [string]$computer.HostAddress
        $location = $locations | Where-Object { $_.Uid -eq $computer.ParentUid } | Select-Object -First 1
        $locationName = if ($null -ne $location) { [string]$location.Name } else { '?' }
        $resolved = ''
        $addressesForHost = @()
        $parsedIp = $null
        if ([System.Net.IPAddress]::TryParse($hostAddress, [ref]$parsedIp)) {
            $addressesForHost = @($hostAddress)
            $resolved = 'IP 直填'
        }
        else {
            try {
                $addressesForHost = @([System.Net.Dns]::GetHostAddresses($hostAddress) |
                    Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
                    ForEach-Object { $_.IPAddressToString })
                $resolved = if ($addressesForHost.Count -gt 0) { $addressesForHost -join ',' } else { '解析为空' }
            }
            catch { $resolved = '解析失败' }
        }

        $tcpState = if ($addressesForHost.Count -gt 0) { '连接失败' } else { 'DNS 解析失败' }
        if ($addressesForHost.Count -gt 0) {
            foreach ($target in $addressesForHost) {
                $client = New-Object System.Net.Sockets.TcpClient
                try {
                    $connect = $client.BeginConnect($target, [int]$serverPort, $null, $null)
                    if ($connect.AsyncWaitHandle.WaitOne(1500, $false) -and $client.Connected) {
                        $tcpState = "TCP $serverPort 通"
                        break
                    }
                }
                catch { }
                finally { $client.Close() }
            }
        }

        $probeResults += [pscustomobject]@{
            Location = $locationName
            Name     = [string]$computer.Name
            Host     = $hostAddress
            Resolved = $resolved
            Tcp      = $tcpState
        }
    }

    foreach ($result in $probeResults) {
        Add-Line ("  [{0}] {1} | Host={2} | DNS={3} | {4}" -f $result.Location, $result.Name, $result.Host, $result.Resolved, $result.Tcp)
    }
    $unreachable = @($probeResults | Where-Object { $_.Tcp -ne "TCP $serverPort 通" })
    Add-Line ("可达性汇总     : 通 {0} 台，不通 {1} 台" -f ($probeResults.Count - $unreachable.Count), $unreachable.Count)
}

# ---------- 校区签名与课堂前置 ----------
Add-Line ''
Add-Line '=== 5. 校区签名密钥（课堂管控前置） ==='
$policyRoot = 'HKCU:\Software\VeyonCampus\WebsitePolicy'
$campusKeys = @()
if (Test-Path $policyRoot) {
    $campusKeys = @(Get-ChildItem $policyRoot -ErrorAction SilentlyContinue |
        Where-Object { $null -ne $_.Property -and @($_.Property).Count -gt 0 })
}
Add-Line ("网站策略密钥条目: {0}" -f $campusKeys.Count)
foreach ($key in $campusKeys) {
    Add-Line ("  - 键 {0}（键名是校区标识的 SHA-256，不能反推校区名）" -f $key.PSChildName)
}
if ($campusKeys.Count -eq 0) {
    Add-Line '  → 本机没有任何校区网站签名密钥：无法开始课堂，课堂管控按钮会保持不可用。'
}

$historyPath = Join-Path $env:LOCALAPPDATA 'VeyonCampus\WebsitePolicy'
if (Test-Path $historyPath) {
    foreach ($file in @(Get-ChildItem $historyPath -Filter '*.json' -ErrorAction SilentlyContinue)) {
        Add-Line ("本地策略记录   : {0}" -f $file.FullName)
        try {
            $content = Get-Content $file.FullName -Raw -ErrorAction Stop
            $matches = [regex]::Matches($content, '"campusId"\s*:\s*"([^"]+)"')
            $ids = @($matches | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
            if ($ids.Count -gt 0) { Add-Line ("  campusId     : {0}" -f ($ids -join ', ')) }
        }
        catch { }
    }
}

$seatGrid = Join-Path $env:LOCALAPPDATA 'VeyonCampus\Teacher\classroom-seat-grid.json'
Add-Line ("座位图配置     : {0}" -f $(if (Test-Path $seatGrid) { $seatGrid } else { '不存在（尚未配置教室）' }))

$sessionFile = Join-Path $env:LOCALAPPDATA 'VeyonCampus\Teacher\classroom-sessions.json'
if (-not (Test-Path $sessionFile)) {
    Add-Line '课堂会话       : 从未开始过课堂（没有 classroom-sessions.json）'
}
else {
    try {
        $sessions = @(Get-Content $sessionFile -Raw -ErrorAction Stop | ConvertFrom-Json)
        $last = $sessions | Select-Object -Last 1
        Add-Line ("课堂会话       : {0} 条记录；最近一条状态={1}，教室={2}，目标 {3} 台，开始于 {4}" -f `
                $sessions.Count, $last.status, $last.room.roomName, @($last.targets).Count, $last.startedAtUtc)
    }
    catch {
        Add-Line ("课堂会话       : 读取失败 - {0}" -f $_.Exception.Message)
    }
}

# ---------- 结论 ----------
Add-Line ''
Add-Line '=== 6. 判定 ==='
$reasons = New-Object System.Collections.Generic.List[string]
if ($null -eq $veyonService -or $veyonService.Status -ne 'Running') {
    $reasons.Add('Veyon 服务未运行：教师端无法读取设备目录，也无法连接学生机。')
}
if ($null -ne $veyonCli) {
    $authKeyCount = @(& $veyonCli authkeys list 2>$null | Where-Object { $_ -match '/' }).Count
    if ($authKeyCount -eq 0) {
        $reasons.Add('教师机没有 Veyon 认证密钥：学生机会拒绝连接（认证失败）。需要在 Veyon 中导入或生成密钥，并把公钥部署到学生机。')
    }
}
if ($computers.Count -eq 0) {
    $reasons.Add('Veyon 内置目录里没有电脑：教师端设备数为 0，无法开始课堂。')
}
elseif (-not $SkipNetworkProbe) {
    $unreachableCount = @($probeResults | Where-Object { $_.Tcp -ne "TCP $serverPort 通" }).Count
    if ($unreachableCount -eq $computers.Count) {
        $reasons.Add('所有学生机都不通：优先检查学生机是否开机、Veyon 服务是否运行、Windows 计算机名是否等于 Veyon 里的 Host（如 PC-01）、以及防火墙。')
    }
    elseif ($unreachableCount -gt 0) {
        $reasons.Add(('有 {0} 台学生机不通：这些机器在课堂里会显示离线，课堂管控只会对在线设备生效。' -f $unreachableCount))
    }
}
if ($campusKeys.Count -eq 0) {
    $reasons.Add('本机没有校区网站签名密钥：即使设备在线，"开始课堂"与课堂管控仍不可用。需要用生成过学生端配置包的同一校区身份，或重新生成配置包并部署学生端。')
}
if (-not (Test-Path $seatGrid)) {
    $reasons.Add('本机没有教室座位配置：需要先在"配置与部署 → 地点与学生名单"里选择或创建教室。')
}

if ($reasons.Count -eq 0) {
    Add-Line '未发现阻断项。如果按钮仍不可用，请确认已经在课堂页点击"开始课堂"，并让教师端停留在课堂页超过 3 分钟后重新读取一次设备名单。'
}
else {
    $index = 1
    foreach ($reason in $reasons) {
        Add-Line ("  {0}. {1}" -f $index, $reason)
        $index++
    }
}

Add-Line ''
Add-Line '诊断结束。本脚本只读取本机状态，没有修改任何配置。'

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolved = [System.IO.Path]::GetFullPath($OutputPath)
    $parent = [System.IO.Path]::GetDirectoryName($resolved)
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [System.IO.File]::WriteAllLines($resolved, $lines, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("结果已写入 {0}" -f $resolved)
}
