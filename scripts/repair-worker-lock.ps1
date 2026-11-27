#Requires -Version 5.1
<#
.SYNOPSIS
    找出并清掉占用 Veyon Campus 提权 Worker 的进程。

.DESCRIPTION
    部署工具报「Worker 文件仍被其他进程占用（Windows 错误 32）」时，真正的原因是
    C:\Program Files\Veyon Campus\<角色>\Worker\VeyonCampus.Worker.exe 被别的进程打开了，
    和当前账号是不是管理员无关。常见占用者：
      * 上一次部署或 retry 工具留下的 Worker 进程（最常见）
      * 杀毒软件正在扫描刚释放的 exe
      * 同时开着两个部署窗口
    默认只检查并打印，加 -Fix 才会结束属于本产品的残留进程。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\repair-worker-lock.ps1
    powershell -ExecutionPolicy Bypass -File .\repair-worker-lock.ps1 -Fix
#>
[CmdletBinding()]
param(
    [switch]$Fix
)

$ErrorActionPreference = 'Continue'

$roots = @(
    (Join-Path $env:ProgramFiles 'Veyon Campus\Student'),
    (Join-Path $env:ProgramFiles 'Veyon Campus\Teacher')
)
$workerRelative = 'Worker\VeyonCampus.Worker.exe'

Write-Host ''
Write-Host 'Veyon Campus 提权 Worker 占用检查' -ForegroundColor Cyan
Write-Host ('=' * 42)

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host ("当前账户：{0}" -f $identity.Name)
Write-Host ("管理员权限：{0}" -f $(if ($isAdmin) { '是' } else { '否（需要管理员才能结束进程）' }))

function Test-WorkerLocked {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        $stream.Dispose()
        return $false
    } catch {
        return $true
    }
}

$found = $false
foreach ($root in $roots) {
    $worker = Join-Path $root $workerRelative
    if (-not (Test-Path -LiteralPath $worker)) { continue }
    $found = $true
    Write-Host ''
    Write-Host ("Worker：{0}" -f $worker) -ForegroundColor Gray

    $locked = Test-WorkerLocked -Path $worker
    if ($locked -eq $false) {
        Write-Host '  状态：未被占用。' -ForegroundColor Green
        continue
    }
    Write-Host '  状态：被占用（Windows 会因此拒绝启动提权 Worker）。' -ForegroundColor Yellow

    $owners = @()
    try {
        $owners = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop |
            Where-Object { $_.ExecutablePath -and $_.ExecutablePath -ieq $worker })
    } catch {
        Write-Host ('  无法枚举进程：{0}' -f $_.Exception.Message) -ForegroundColor Red
    }

    if ($owners.Count -eq 0) {
        Write-Host '  没有发现同名进程；占用者可能是杀毒软件、安装程序或系统扫描，' -ForegroundColor Yellow
        Write-Host '  请在「资源监视器 → CPU → 关联的句柄」中搜索 VeyonCampus.Worker.exe。' -ForegroundColor Yellow
        continue
    }

    foreach ($owner in $owners) {
        Write-Host ("  占用进程：{0}（PID {1}）" -f $owner.Name, $owner.ProcessId)
    }

    if (-not $Fix) {
        Write-Host '  加 -Fix 可结束上面这些本产品的残留进程。' -ForegroundColor Yellow
        continue
    }
    if (-not $isAdmin) {
        Write-Host '  当前不是管理员，无法结束进程；请以管理员身份重新运行。' -ForegroundColor Red
        continue
    }

    foreach ($owner in $owners) {
        try {
            Stop-Process -Id $owner.ProcessId -Force -ErrorAction Stop
            Write-Host ("  已结束 PID {0}。" -f $owner.ProcessId) -ForegroundColor Green
        } catch {
            Write-Host ("  结束 PID {0} 失败：{1}" -f $owner.ProcessId, $_.Exception.Message) -ForegroundColor Red
        }
    }

    Start-Sleep -Seconds 1
    $stillLocked = Test-WorkerLocked -Path $worker
    if ($stillLocked -eq $false) {
        Write-Host '  复查：Worker 已可正常打开。' -ForegroundColor Green
    } else {
        Write-Host '  复查：仍被占用，请重启这台电脑后再部署。' -ForegroundColor Yellow
    }
}

if (-not $found) {
    Write-Host ''
    Write-Host '没有在这台电脑上找到 Veyon Campus 安装目录；确认部署工具装在本机。' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '完成。如果仍被占用：关闭所有部署窗口与 retry 工具，重启电脑，再运行部署工具。' -ForegroundColor Cyan
