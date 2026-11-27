<#
.SYNOPSIS
    只读诊断屏幕墙「认证被拒绝：密钥不匹配」的真实原因。

.DESCRIPTION
    在教师机上运行，不修改任何系统状态：不写注册表、不改 Veyon 配置、不安装任何东西。
    它按屏幕墙实际使用的链路（本机 Veyon WebAPI 的密钥认证）对每台学生机逐把私钥尝试，
    从而区分下面四种情况：

      1. 学生机不信任本机任何私钥   → 该机缺少校区公钥，重新推送学生配置包
      2. 本机根本没有私钥           → 教师端 Veyon 密钥目录不完整
      3. 学生机认证方式不是密钥认证 → 学生机 Authentication/Method 不是 1
      4. 学生机不可达               → 网络/主机名/Veyon 服务问题，与密钥无关

    注意：屏幕墙不依赖「当前选中的校区」，它会把本机 private 目录里每把私钥都试一遍。
    因此「逐把尝试的结果」是唯一权威判据，下面的校区 keyId 对比只用于说明现场状态。

.PARAMETER Targets
    要诊断的学生机地址（主机名或 IP）。省略时从本机课堂座位表读取。

.PARAMETER CampusId
    当前校区 ID。省略时依次尝试座位表里的校区 ProfileId 与网站策略历史里的 campusId。

.PARAMETER Port
    本机 Veyon WebAPI 端口，默认 11080。

.PARAMETER OutputPath
    可选，把完整输出同时写入该文本文件，便于把现场结果发回分析。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\diagnose-screen-wall-auth.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\diagnose-screen-wall-auth.ps1 -Targets pc-01,pc-02 -OutputPath .\wall-auth.txt

.NOTES
    Windows PowerShell 5.1 与 PowerShell 7 均可运行（脚本内不使用 7.x 专属参数）。
#>
[CmdletBinding()]
param(
    [string[]]$Targets,
    [string]$CampusId,
    [int]$Port = 11080,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

# 不强制改写控制台编码：跟随当前终端（936 或 65001）输出，避免中文变成乱码。

if ($OutputPath) {
    try { Start-Transcript -Path $OutputPath -Force | Out-Null } catch { }
}

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
    throw '此脚本只能在 Windows 教师机上运行。'
}

function Write-Section {
    param([string]$Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('-' * [Math]::Max(8, $Text.Length)) -ForegroundColor DarkCyan
}

# 与 VeyonCampus.Core/VeyonAuthKeyId.cs 一致：SHA-256(campusId) 前 16 字节，
# 每字节拆成两个字母（高 4 位、低 4 位，0..15 -> A..P），前缀 C，共 33 字符。
function Get-CampusKeyId {
    param([string]$Id)
    if ([string]::IsNullOrWhiteSpace($Id)) { return $null }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Id)) } finally { $sha.Dispose() }
    $chars = New-Object char[] 33
    $chars[0] = 'C'
    for ($i = 0; $i -lt 16; $i++) {
        $chars[1 + $i * 2] = [char]([int][char]'A' + ($hash[$i] -shr 4))
        $chars[2 + $i * 2] = [char]([int][char]'A' + ($hash[$i] -band 0x0F))
    }
    return (-join $chars)
}

# Veyon 的拒绝原因藏在响应体里（code/message），只打状态码会丢掉最有用的信息。
function Get-ErrorBody {
    param($ErrorRecord)
    $text = $null
    try {
        if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
            $text = $ErrorRecord.ErrorDetails.Message
        }
    }
    catch { }
    if (-not $text) {
        try {
            $response = $ErrorRecord.Exception.Response
            if ($response) {
                $stream = $response.GetResponseStream()
                if ($stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    $text = $reader.ReadToEnd()
                    $reader.Close()
                }
            }
        }
        catch { }
    }
    if (-not $text) { return $null }
    $text = ($text -replace '\s+', ' ').Trim()
    if ($text.Length -gt 400) { $text = $text.Substring(0, 400) + '…' }
    return $text
}

$privateRoot = Join-Path $env:ProgramData 'Veyon\keys\private'
$publicRoot = Join-Path $env:ProgramData 'Veyon\keys\public'

Write-Section '会话权限'
$isElevated = $false
try {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    $isElevated = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}
catch { }
if ($isElevated) {
    Write-Host '  管理员会话：可以读取 Veyon 私钥目录。' -ForegroundColor Green
}
else {
    Write-Host '  普通用户会话：Veyon 私钥目录可能读不到，逐台认证会缺私钥。' -ForegroundColor Yellow
    Write-Host '  如果下面是「共 0 把可用私钥」，请右键以管理员身份重新运行本脚本。' -ForegroundColor Yellow
}

Write-Section '教师机 Veyon 私钥'
$privateKeys = @()
if (Test-Path -LiteralPath $privateRoot) {
    foreach ($dir in Get-ChildItem -LiteralPath $privateRoot -Directory -ErrorAction SilentlyContinue) {
        $keyFile = Join-Path $dir.FullName 'key'
        if (-not (Test-Path -LiteralPath $keyFile)) {
            $items = @(Get-ChildItem -LiteralPath $dir.FullName -ErrorAction SilentlyContinue)
            $content = if ($items.Count -gt 0) { ($items | ForEach-Object { $_.Name }) -join ', ' } else { '空或无权读取' }
            Write-Host ("  {0}  （缺少 key 文件；实际内容：{1}）" -f $dir.Name, $content) -ForegroundColor DarkYellow
            continue
        }
        $pem = Get-Content -LiteralPath $keyFile -Raw -ErrorAction SilentlyContinue
        if ($pem -notmatch 'PRIVATE KEY') {
            Write-Host ("  {0}  （不是 PEM 私钥，屏幕墙会跳过）" -f $dir.Name) -ForegroundColor DarkYellow
            continue
        }
        $privateKeys += [pscustomobject]@{ Name = $dir.Name; Pem = $pem }
        Write-Host ("  {0}" -f $dir.Name) -ForegroundColor Green
    }
}
else {
    Write-Host ("  找不到 {0}" -f $privateRoot) -ForegroundColor Red
}
Write-Host ("  共 {0} 把可用私钥" -f $privateKeys.Count)
if ($privateKeys.Count -eq 0) {
    Write-Host '  没有私钥时屏幕墙只会显示「找不到可用的 Veyon 私钥」，与网络无关。' -ForegroundColor Red
}

Write-Section '教师机 Veyon 公钥目录'
if (Test-Path -LiteralPath $publicRoot) {
    $pubNames = @(Get-ChildItem -LiteralPath $publicRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Name })
    if ($pubNames.Count -eq 0) { Write-Host '  （空）' } else { $pubNames | ForEach-Object { Write-Host ("  {0}" -f $_) } }
}
else {
    Write-Host '  （公钥目录不存在）' -ForegroundColor DarkYellow
}

# 座位表：优先取校区 ProfileId 和学生机地址，避免要求现场人员手抄。
$gridPath = Join-Path $env:LOCALAPPDATA 'VeyonCampus\Teacher\classroom-seat-grid.json'
$gridCampusId = $null
$gridHosts = @()
if (Test-Path -LiteralPath $gridPath) {
    try {
        $grid = Get-Content -LiteralPath $gridPath -Raw | ConvertFrom-Json
        foreach ($layout in @($grid.layouts)) {
            if (-not $layout) { continue }
            $campusProperty = $layout.PSObject.Properties['campusProfileId']
            if (-not $gridCampusId -and $campusProperty -and $campusProperty.Value) {
                $gridCampusId = [string]$campusProperty.Value
            }
            $seatsProperty = $layout.PSObject.Properties['seats']
            if (-not $seatsProperty -or -not $seatsProperty.Value) { continue }
            foreach ($seat in @($seatsProperty.Value)) {
                if (-not $seat) { continue }
                $hostProperty = $seat.PSObject.Properties['veyonHostAddress']
                if ($hostProperty -and $hostProperty.Value) { $gridHosts += [string]$hostProperty.Value }
            }
        }
        $gridHosts = @($gridHosts | Where-Object { $_ } | Select-Object -Unique)
    }
    catch {
        Write-Host ("  座位表无法解析：{0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
    }
}

if ([string]::IsNullOrWhiteSpace($CampusId) -and $gridCampusId) { $CampusId = $gridCampusId }
if ([string]::IsNullOrWhiteSpace($CampusId)) {
    $historyDir = Join-Path $env:LOCALAPPDATA 'VeyonCampus\WebsitePolicy'
    if (Test-Path -LiteralPath $historyDir) {
        $hit = Get-ChildItem -LiteralPath $historyDir -Filter *.json -ErrorAction SilentlyContinue |
            Select-Object -First 50 | ForEach-Object {
                $text = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue
                if ($text) {
                    $m = [regex]::Matches($text, '"campusId"\s*:\s*"([^"]+)"')
                    if ($m.Count -gt 0) { $m[0].Groups[1].Value }
                }
            } | Where-Object { $_ } | Select-Object -Unique -First 1
        if ($hit) { $CampusId = $hit }
    }
}

Write-Section '校区与 keyId 对照'
$expectedKeyId = $null
if ($CampusId) {
    $expectedKeyId = Get-CampusKeyId -Id $CampusId
    Write-Host ("  当前校区 ID : {0}" -f $CampusId)
    Write-Host ("  对应 keyId  : {0}" -f $expectedKeyId) -ForegroundColor Yellow
    if ($gridCampusId -and $gridCampusId -ne $CampusId) {
        Write-Host ("  座位表校区  : {0}（keyId {1}）" -f $gridCampusId, (Get-CampusKeyId -Id $gridCampusId))
    }
    $hasExpected = @($privateKeys | Where-Object { $_.Name -ceq $expectedKeyId }).Count -gt 0
    if ($hasExpected) {
        Write-Host '  本机拥有当前校区的私钥。' -ForegroundColor Green
    }
    else {
        Write-Host '  本机没有当前校区的私钥（本机私钥来自其他校区或更早的配置）。' -ForegroundColor DarkYellow
        Write-Host '  若学生机是用当前校区配置包安装的，认证必然全部被拒。' -ForegroundColor DarkYellow
    }
}
else {
    Write-Host '  未能确定校区 ID，跳过对照。可用 -CampusId 显式指定。' -ForegroundColor DarkYellow
}

Write-Section '本机 Veyon WebAPI'
$baseUrl = "http://127.0.0.1:$Port/api/v1/"
$webApiReady = $false
# 不传 Connection-Uid 时 WebAPI 会返回 4xx；只要拿到 HTTP 状态码就说明服务在监听。
try {
    $probe = Invoke-WebRequest -Uri ($baseUrl + 'framebuffer') -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
    $webApiReady = $true
    Write-Host ("  可用（HTTP {0}）" -f [int]$probe.StatusCode) -ForegroundColor Green
}
catch {
    $probeStatus = $null
    try { if ($_.Exception.Response) { $probeStatus = [int]$_.Exception.Response.StatusCode } } catch { }
    if ($probeStatus) {
        $webApiReady = $true
        Write-Host ("  可用（HTTP {0}，未带会话属正常）" -f $probeStatus) -ForegroundColor Green
    }
    else {
        Write-Host ("  不可用：{0}" -f $_.Exception.Message) -ForegroundColor Red
        Write-Host '  先在课堂页黄色提示条点「启用本机 WebAPI」，或运行 VeyonCampus.Teacher.exe --enable-local-webapi。' -ForegroundColor Yellow
    }
}

if (-not $Targets -or $Targets.Count -eq 0) { $Targets = $gridHosts }
if (-not $Targets -or $Targets.Count -eq 0) {
    Write-Host ''
    Write-Host '没有可诊断的学生机地址。请用 -Targets 指定，例如：-Targets pc-01,pc-02' -ForegroundColor Yellow
    if ($OutputPath) { try { Stop-Transcript | Out-Null } catch { } }
    return
}
Write-Host ("`n待诊断学生机：{0} 台" -f @($Targets).Count)
if (-not $webApiReady) {
    if ($OutputPath) { try { Stop-Transcript | Out-Null } catch { } }
    return
}

Write-Section '逐台认证尝试（与屏幕墙同一条链路）'
$summaries = @()
foreach ($targetHost in $Targets) {
    Write-Host ''
    Write-Host ("[{0}]" -f $targetHost) -ForegroundColor White

    $method = $null
    try {
        $methodResponse = Invoke-RestMethod -Uri ($baseUrl + 'authentication/' + [uri]::EscapeDataString($targetHost)) `
            -TimeoutSec 45 -ErrorAction Stop
        $methodsProperty = $methodResponse.PSObject.Properties['methods']
        if ($methodsProperty -and $methodsProperty.Value) {
            $method = @($methodsProperty.Value)[0]
        }
        if (-not $method) { throw 'WebAPI 未返回认证方式。' }
    }
    catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -eq 408 -or $_.Exception -is [System.Threading.Tasks.TaskCanceledException] -or
            $_.Exception.InnerException -is [System.Threading.Tasks.TaskCanceledException]) {
            Write-Host '  不可达：WebAPI 等待学生机超时（与密钥无关，先查网络、Veyon 服务、主机名）' -ForegroundColor Red
            $summaries += [pscustomobject]@{ Host = $targetHost; Verdict = '不可达（非密钥问题）' }
        }
        else {
            Write-Host ("  读取认证方式失败：{0}" -f $_.Exception.Message) -ForegroundColor Red
            $summaries += [pscustomobject]@{ Host = $targetHost; Verdict = '读取认证方式失败' }
        }
        continue
    }

    Write-Host ("  学生机认证方式：{0}" -f $method)
    if ($method -and $method -notmatch '^key') {
        Write-Host '  该学生机不是密钥认证方式，教师端提供的私钥不会被采用。' -ForegroundColor DarkYellow
    }

    $accepted = @()
    foreach ($key in $privateKeys) {
        $body = @{
            method      = $method
            credentials = @{ keyname = $key.Name; keydata = $key.Pem }
        } | ConvertTo-Json -Depth 5 -Compress

        try {
            $auth = Invoke-RestMethod -Uri ($baseUrl + 'authentication/' + [uri]::EscapeDataString($targetHost)) `
                -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 45 -ErrorAction Stop
            Write-Host ("  密钥 {0} → 接受" -f $key.Name) -ForegroundColor Green
            $accepted += $key.Name
            $uidProperty = $auth.PSObject.Properties['connection-uid']
            $uid = if ($uidProperty) { $uidProperty.Value } else { $null }
            if ($uid) {
                # 释放这次试探占用的会话，避免占满 WebAPI 的连接额度。
                try {
                    Invoke-WebRequest -Uri ($baseUrl + 'authentication/' + [uri]::EscapeDataString($targetHost)) `
                        -Method Delete -Headers @{ 'Connection-Uid' = [string]$uid } -TimeoutSec 5 `
                        -UseBasicParsing -ErrorAction Stop | Out-Null
                }
                catch { }
            }
        }
        catch {
            $status = $null
            if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
            if ($status -eq 401 -or $status -eq 400) {
                Write-Host ("  密钥 {0} → 拒绝（HTTP {1}）" -f $key.Name, $status) -ForegroundColor DarkYellow
            }
            else {
                Write-Host ("  密钥 {0} → 未确认（{1}）" -f $key.Name, $_.Exception.Message) -ForegroundColor DarkYellow
            }
            $bodyText = Get-ErrorBody -ErrorRecord $_
            if ($bodyText) {
                Write-Host ("    Veyon 返回：{0}" -f $bodyText) -ForegroundColor DarkGray
            }
        }
    }

    if ($accepted.Count -gt 0) {
        $verdict = '可认证（接受：' + ($accepted -join ', ') + '）'
        if ($expectedKeyId -and ($accepted -notcontains $expectedKeyId)) {
            $verdict += '；注意接受的不是当前校区密钥'
        }
    }
    elseif ($privateKeys.Count -eq 0) {
        $verdict = '本机没有私钥，无法认证'
    }
    elseif ($expectedKeyId -and (@($privateKeys | Where-Object { $_.Name -ceq $expectedKeyId }).Count -gt 0)) {
        $verdict = '学生机不信任本机任何密钥 → 该机缺少校区公钥，重新推送学生配置包'
    }
    elseif ($expectedKeyId) {
        $verdict = '全部拒绝且本机无当前校区私钥 → 教师机需重新生成并推送学生配置包'
    }
    else {
        $verdict = '全部拒绝 → 学生机缺少本机公钥，或认证方式不是密钥认证'
    }
    Write-Host ("  结论：{0}" -f $verdict) -ForegroundColor Cyan
    $summaries += [pscustomobject]@{ Host = $targetHost; Verdict = $verdict }
}

Write-Section '汇总'
$summaries | Format-Table -AutoSize | Out-String -Width 220 | Write-Host

Write-Host '判读要点：' -ForegroundColor Yellow
Write-Host '  · 全部 401，且本机有当前校区私钥 → 在失败的学生机上运行：'
Write-Host '      & "C:\Program Files\Veyon\veyon-cli.exe" authkeys list'
Write-Host '    看不到上面「对应 keyId」同名的 public 项，就是缺公钥：对该机重新推送学生配置包。'
Write-Host '  · 全部 401，且本机没有当前校区私钥 → 这台教师机不是生成配置包的那台。'
Write-Host '    只重装教师端不会把公钥带到学生机，必须在教师端重新生成校区密钥并推送。'
Write-Host '  · 学生机认证方式不是 keyfile → 在该机运行：'
Write-Host '      & "C:\Program Files\Veyon\veyon-cli.exe" config set Authentication/Method 1'
Write-Host '  · 认证成功但画面仍黑 → 与本脚本无关，转去查屏幕采集（Veyon 版本、桌面会话）。'

if ($OutputPath) {
    Write-Host ''
    Write-Host ("完整结果已写入：{0}" -f (Resolve-Path -LiteralPath $OutputPath -ErrorAction SilentlyContinue)) -ForegroundColor Yellow
    try { Stop-Transcript | Out-Null } catch { }
}
