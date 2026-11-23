#requires -Version 5.1
<#
InputGuard 现场诊断（在学生机上以“当前登录的学生用户”运行，不需要管理员）。

只读：不修改任何设置、不启动/停止任何进程。输出用于回答一个问题——
“教师端点键鼠锁定时，为什么 Agent 读不到交互桌面 Guard 的状态”。
#>
[CmdletBinding()]
param(
    [string]$GuardDirectory = (Join-Path $env:ProgramFiles 'Veyon Campus\Student\InputGuard')
)

$ErrorActionPreference = 'Continue'
function Section($title) { Write-Host "`n=== $title ===" -ForegroundColor Cyan }

Section '会话'
$currentSession = (Get-Process -Id $PID).SessionId
"当前进程会话: $currentSession"
try { query user 2>&1 | ForEach-Object { $_ } } catch { "query user 失败: $($_.Exception.Message)" }

Section 'Guard 进程'
$guards = Get-Process -Name 'VeyonCampus.StudentInputGuard' -ErrorAction SilentlyContinue
if (-not $guards) {
    '未发现 VeyonCampus.StudentInputGuard 进程 —— Agent 会读到“状态未知”。'
}
else {
    foreach ($process in $guards) {
        $path = try { $process.MainModule.FileName } catch { '<无法读取>' }
        "PID=$($process.Id) Session=$($process.SessionId) Start=$($process.StartTime) Path=$path"
    }
}

Section 'Companion 进程（负责启动 Guard）'
$companions = Get-Process -Name 'VeyonCampus.Companion' -ErrorAction SilentlyContinue
if (-not $companions) { '未发现 Companion 进程 —— Guard 不会被自动拉起。' }
else { foreach ($process in $companions) { "PID=$($process.Id) Session=$($process.SessionId)" } }

Section '命名管道'
$pipes = [System.IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like '*StudentInputGuard*' }
if (-not $pipes) { '没有 VeyonCampus.StudentInputGuard.* 管道 —— Guard 未在监听。' }
else { $pipes }

Section '安装目录'
"期望目录: $GuardDirectory"
if (Test-Path -LiteralPath $GuardDirectory) {
    Get-ChildItem -LiteralPath $GuardDirectory -Filter 'VeyonCampus.StudentInputGuard.exe' |
        ForEach-Object { "文件: $($_.FullName) 版本: $($_.VersionInfo.FileVersion)" }
}
else { '目录不存在 —— Agent 会报“未安装交互桌面 Guard”。' }

Section 'Guard 监督日志（最近 20 行）'
$supervisorLog = Join-Path $env:LOCALAPPDATA 'VeyonCampus\InputGuard\companion-supervisor.log'
if (Test-Path -LiteralPath $supervisorLog) { Get-Content -LiteralPath $supervisorLog -Tail 20 }
else { "没有 $supervisorLog（Companion 从未尝试启动 Guard）。" }

Section 'Guard 自身日志（最近 20 行）'
$guardLogCandidates = @(
    (Join-Path $env:ProgramData 'VeyonCampus\InputGuard\guard-server.log'),
    (Join-Path $env:LOCALAPPDATA 'VeyonCampus\InputGuard\guard-server.log')
)
$guardLog = $guardLogCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($guardLog) {
    "文件: $guardLog"
    Get-Content -LiteralPath $guardLog -Tail 20
    '（有 ServerStarting 说明进程进入过服务模式；BlockInputRejected 会带上错误码和当前桌面。）'
}
else { '没有 guard-server.log（Guard 从未进入服务模式，或从未写日志）。' }

Section 'Agent 运行日志中的 InputGuard 行（最近 20 行）'
$agentLog = Get-ChildItem -Path (Join-Path $env:ProgramData 'VeyonCampus\WebsitePolicy') `
    -Recurse -Filter 'agent-runtime.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($agentLog) {
    "文件: $($agentLog.FullName)"
    Select-String -LiteralPath $agentLog.FullName -Pattern 'input-guard|guard-launch|InputGuard|交互桌面' |
        Select-Object -Last 20 | ForEach-Object { $_.Line }
}
else { '没有找到 agent-runtime.log。' }

Section '结论'
$hasGuard = [bool]$guards
$hasPipe = [bool]$pipes
$directoryOk = Test-Path -LiteralPath $GuardDirectory
if (-not $directoryOk) {
    '安装目录缺失：Agent 会报“未安装交互桌面 Guard”，这也是锁定不了的首要原因。'
}
elseif (-not $hasGuard -and -not $hasPipe) {
    'Guard 完全没在运行：先看 companion-supervisor.log，再看 agent-runtime.log 里的 guard-launch 行（Agent 兜底启动的 Win32 错误码）。'
}
elseif ($hasGuard -and -not $hasPipe) {
    'Guard 进程存在但没有命名管道：可能刚启动，或已卡死（租约线程退出后管道不再服务）。'
}
elseif ($hasGuard -and $hasPipe) {
    'Guard 进程在监听。若教师端仍报“无法核验”，重点看上面的会话列表——多个 Active 会话且没有控制台会话时，Agent 会拒绝猜。'
}

Write-Host "`n把以上整段输出发回即可。" -ForegroundColor Yellow
