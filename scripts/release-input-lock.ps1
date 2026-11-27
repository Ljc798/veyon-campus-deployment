#requires -Version 5.1
<#
现场应急：在一台学生机上立刻解除键鼠锁定，不需要教师端在线，也不需要等锁定到期。

必须在该学生机上以管理员身份运行。脚本按顺序做四件事：
  1) 停止 SYSTEM Agent 计划任务 —— 否则它的续租线程会在 5 秒内重新上锁；
  2) 结束交互桌面的 InputGuard —— BlockInput 的阻止随持有它的进程退出由 Windows 释放；
  3) 删除持久化锁定意愿文件 —— 学生机重启后不会再自动上锁；
  4) 重新启动 Agent —— 网站策略、课堂状态、教师连接恢复，但不恢复锁定。

参数 -DryRun 只报告将要做什么，不修改任何状态。
#>
[CmdletBinding()]
param(
    [string]$AgentTaskName = 'VeyonCampus-WebsitePolicyAgent',
    [string]$GuardProcessName = 'VeyonCampus.StudentInputGuard',
    [string]$AgentProcessName = 'VeyonCampus.Agent',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }
function Note($text) { Write-Host "   $text" }
function Warn($text) { Write-Host "   $text" -ForegroundColor Yellow }

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    {
        throw '必须以管理员身份运行：右键“以管理员身份运行 PowerShell”，再执行本脚本。'
    }
}

function Get-IntentFiles {
    $root = Join-Path $env:ProgramData 'VeyonCampus\WebsitePolicy'
    if (-not (Test-Path -LiteralPath $root)) { return @() }
    return @(Get-ChildItem -LiteralPath $root -Recurse -Filter 'input-guard-lock-intent.json' `
            -ErrorAction SilentlyContinue)
}

function Stop-AgentTask {
    $task = Get-ScheduledTask -TaskName $AgentTaskName -ErrorAction SilentlyContinue
    if ($null -eq $task) { Warn "没有找到计划任务 $AgentTaskName（可能未安装最新学生端）。"; return }
    if ($DryRun) { Note "将停止计划任务 $AgentTaskName"; return }
    Stop-ScheduledTask -TaskName $AgentTaskName -ErrorAction SilentlyContinue
    Note "已停止计划任务 $AgentTaskName"
}

function Stop-GuardProcesses {
    $guards = @(Get-Process -Name $GuardProcessName -ErrorAction SilentlyContinue)
    if ($guards.Count -eq 0) { Note '没有运行中的 InputGuard 进程。'; return }
    foreach ($guard in $guards) {
        if ($DryRun) { Note "将结束 InputGuard PID=$($guard.Id) Session=$($guard.SessionId)"; continue }
        try { Stop-Process -Id $guard.Id -Force -ErrorAction Stop; Note "已结束 InputGuard PID=$($guard.Id)" }
        catch { Warn "结束 InputGuard PID=$($guard.Id) 失败：$($_.Exception.Message)" }
    }
    if (-not $DryRun) { Start-Sleep -Milliseconds 400 }
}

function Stop-AgentProcesses {
    # 计划任务停止后进程偶尔仍在，续租线程会继续上锁，必须一并结束。
    $agents = @(Get-Process -Name $AgentProcessName -ErrorAction SilentlyContinue)
    foreach ($agent in $agents) {
        if ($DryRun) { Note "将结束 Agent PID=$($agent.Id)"; continue }
        try { Stop-Process -Id $agent.Id -Force -ErrorAction Stop; Note "已结束 Agent PID=$($agent.Id)" }
        catch { Warn "结束 Agent PID=$($agent.Id) 失败：$($_.Exception.Message)" }
    }
}

function Remove-IntentFiles {
    $files = Get-IntentFiles
    if ($files.Count -eq 0) { Note '没有持久化锁定意愿文件。'; return }
    foreach ($file in $files) {
        if ($DryRun) { Note "将删除 $($file.FullName)"; continue }
        try { Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop; Note "已删除 $($file.FullName)" }
        catch { throw "删除锁定意愿文件失败（$($file.FullName)）：$($_.Exception.Message)" }
    }
}

function Start-AgentTask {
    $task = Get-ScheduledTask -TaskName $AgentTaskName -ErrorAction SilentlyContinue
    if ($null -eq $task) { return }
    if ($DryRun) { Note "将启动计划任务 $AgentTaskName"; return }
    Start-ScheduledTask -TaskName $AgentTaskName -ErrorAction SilentlyContinue
    Note "已启动计划任务 $AgentTaskName"
}

Assert-Administrator

Step '当前状态'
$guards = @(Get-Process -Name $GuardProcessName -ErrorAction SilentlyContinue)
$intents = Get-IntentFiles
Note "InputGuard 进程：$($guards.Count) 个"
Note "持久化锁定文件：$($intents.Count) 个"
foreach ($intent in $intents) { Note "  $($intent.FullName)" }

Step '解除锁定'
Stop-AgentTask
Stop-AgentProcesses
Stop-GuardProcesses
Remove-IntentFiles
Start-AgentTask

Step '复核'
if ($DryRun) { Note 'DryRun：未修改任何状态。'; return }
Start-Sleep -Seconds 2
$remainingGuards = @(Get-Process -Name $GuardProcessName -ErrorAction SilentlyContinue)
$remainingIntents = Get-IntentFiles
$agentsAgain = @(Get-Process -Name $AgentProcessName -ErrorAction SilentlyContinue)
Note "InputGuard 进程：$($remainingGuards.Count) 个（正常应为 1 个新启动的空闲 Guard，或 0 个等待 Agent 拉起）"
Note "持久化锁定文件：$($remainingIntents.Count) 个（必须为 0）"
Note "Agent 进程：$($agentsAgain.Count) 个"
if ($remainingIntents.Count -gt 0) {
    Warn '锁定意愿仍在：请确认脚本以管理员身份运行，且没有第二个 Agent 实例持有该文件。'
    exit 1
}
Write-Host '键鼠已解除；网站策略与课堂状态会在 Agent 重新连上教师端后自动恢复。' -ForegroundColor Green
