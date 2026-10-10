[CmdletBinding()]
param(
    [ValidateRange(30, 1800)]
    [int]$DurationSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw '该采集器只能在 Windows 教师电脑上运行。'
}

$sessionPath = Join-Path $env:LOCALAPPDATA 'VeyonCampus\Teacher\classroom-sessions.json'
if (-not (Test-Path -LiteralPath $sessionPath -PathType Leaf)) {
    throw '没有找到教师端课堂记录；请先启动一堂测试课堂，再运行采集器。'
}

$sessionDocument = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
$activeSessions = @($sessionDocument.sessions | Where-Object { $_.status -eq 'active' })
if ($activeSessions.Count -ne 1) {
    throw '教师端必须恰有一堂活动课堂；采集器未开始。'
}

$targetCount = @($activeSessions[0].targets).Count
if ($targetCount -lt 1 -or $targetCount -gt 150) {
    throw '活动课堂目标数量无效；采集器未开始。'
}

$teacherProcesses = @()
foreach ($processName in @('VeyonCampus.Teacher', 'VeyonCampus.TeacherConsole')) {
    $teacherProcesses += @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
}
$teacherProcesses = @($teacherProcesses | Sort-Object -Property Id -Unique)
if ($teacherProcesses.Count -ne 1) {
    throw '需要且只能有一个正在运行的 TeacherConsole 实例；采集器未开始。'
}
$teacherProcess = $teacherProcesses[0]

function Read-NetworkTotals {
    try {
        $statistics = @(Get-NetAdapterStatistics -ErrorAction Stop)
        if ($statistics.Count -eq 0) { return $null }
        $sentBytes = ($statistics | Measure-Object -Property SentBytes -Sum).Sum
        $receivedBytes = ($statistics | Measure-Object -Property ReceivedBytes -Sum).Sum
        if ($null -eq $sentBytes -or $null -eq $receivedBytes) { return $null }
        return [pscustomobject]@{
            SentBytes = [long]$sentBytes
            ReceivedBytes = [long]$receivedBytes
        }
    }
    catch {
        return $null
    }
}

function Get-Distribution {
    param([object[]]$Values)

    $sortedValues = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($sortedValues.Count -eq 0) {
        return [ordered]@{ count = 0; p50 = $null; p95 = $null; max = $null }
    }

    $p50Index = [Math]::Max(0, [int][Math]::Ceiling(0.50 * $sortedValues.Count) - 1)
    $p95Index = [Math]::Max(0, [int][Math]::Ceiling(0.95 * $sortedValues.Count) - 1)
    return [ordered]@{
        count = $sortedValues.Count
        p50 = [Math]::Round($sortedValues[$p50Index], 2)
        p95 = [Math]::Round($sortedValues[$p95Index], 2)
        max = [Math]::Round($sortedValues[-1], 2)
    }
}

$scenario = switch ($targetCount) {
    10 { '10' }
    24 { '24' }
    { $_ -ge 60 -and $_ -le 70 } { '60-70' }
    default { 'other' }
}

$timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmssZ')
$artifactDirectory = Join-Path $PSScriptRoot '..\artifacts\pre-release-test'
$null = New-Item -ItemType Directory -Path $artifactDirectory -Force
$outputPath = Join-Path $artifactDirectory "classroom-load-$timestamp.json"
if (Test-Path -LiteralPath $outputPath) {
    throw '采集报告文件已存在；为避免覆盖，采集器未开始。'
}

$samples = [System.Collections.Generic.List[object]]::new()
$networkStatus = 'available'
$previousNetwork = Read-NetworkTotals
if ($null -eq $previousNetwork) { $networkStatus = 'unavailable' }

$startedUtc = [DateTimeOffset]::UtcNow
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$previousElapsedSeconds = 0.0
$teacherProcess.Refresh()
$previousCpuSeconds = $teacherProcess.TotalProcessorTime.TotalSeconds

Write-Host "正在采集 $DurationSeconds 秒；活动课堂配置目标数：$targetCount。"
while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
    Start-Sleep -Milliseconds 1000
    $teacherProcess.Refresh()
    if ($teacherProcess.HasExited) {
        throw 'TeacherConsole 在采集期间退出；报告未生成。'
    }

    $elapsedSeconds = $clock.Elapsed.TotalSeconds
    $intervalSeconds = $elapsedSeconds - $previousElapsedSeconds
    if ($intervalSeconds -le 0) { continue }

    $cpuSeconds = $teacherProcess.TotalProcessorTime.TotalSeconds
    $cpuPercent = [Math]::Max(0, [Math]::Min(100,
        (($cpuSeconds - $previousCpuSeconds) / $intervalSeconds / [Environment]::ProcessorCount) * 100))
    $networkSentBytesPerSecond = $null
    $networkReceivedBytesPerSecond = $null
    if ($networkStatus -eq 'available') {
        $currentNetwork = Read-NetworkTotals
        if ($null -eq $currentNetwork) {
            $networkStatus = 'unavailable'
        }
        elseif ($currentNetwork.SentBytes -ge $previousNetwork.SentBytes -and
                $currentNetwork.ReceivedBytes -ge $previousNetwork.ReceivedBytes) {
            $networkSentBytesPerSecond = [Math]::Round(
                ($currentNetwork.SentBytes - $previousNetwork.SentBytes) / $intervalSeconds, 2)
            $networkReceivedBytesPerSecond = [Math]::Round(
                ($currentNetwork.ReceivedBytes - $previousNetwork.ReceivedBytes) / $intervalSeconds, 2)
        }
        $previousNetwork = $currentNetwork
    }

    $samples.Add([ordered]@{
        elapsedSeconds = [Math]::Round($elapsedSeconds, 2)
        teacherCpuPercentOfHost = [Math]::Round($cpuPercent, 2)
        teacherWorkingSetMiB = [Math]::Round($teacherProcess.WorkingSet64 / 1MB, 2)
        teacherPrivateMemoryMiB = [Math]::Round($teacherProcess.PrivateMemorySize64 / 1MB, 2)
        allAdaptersSentBytesPerSecond = $networkSentBytesPerSecond
        allAdaptersReceivedBytesPerSecond = $networkReceivedBytesPerSecond
    })

    $previousElapsedSeconds = $elapsedSeconds
    $previousCpuSeconds = $cpuSeconds
}
$clock.Stop()
$endedUtc = [DateTimeOffset]::UtcNow

$sampleArray = @($samples.ToArray())
$report = [ordered]@{
    schemaVersion = 1
    startedUtc = $startedUtc.ToString('O')
    endedUtc = $endedUtc.ToString('O')
    requestedDurationSeconds = $DurationSeconds
    actualDurationSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2)
    sampleIntervalSeconds = 1
    configuredTargetCount = $targetCount
    targetCountScenario = $scenario
    targetCountMeaning = '活动课堂中配置的目标数；不表示全部目标在线或已实际参与。'
    teacherApplicationVersion = $null
    sampleCount = $sampleArray.Count
    networkCounterStatus = $networkStatus
    networkCounterScope = '所有网络适配器的系统累计计数；包含教师机上其他应用的流量，不代表 TeacherConsole 专属流量。'
    summaries = [ordered]@{
        teacherCpuPercentOfHost = Get-Distribution @($sampleArray | ForEach-Object { $_.teacherCpuPercentOfHost })
        teacherWorkingSetMiB = Get-Distribution @($sampleArray | ForEach-Object { $_.teacherWorkingSetMiB })
        teacherPrivateMemoryMiB = Get-Distribution @($sampleArray | ForEach-Object { $_.teacherPrivateMemoryMiB })
        allAdaptersSentBytesPerSecond = Get-Distribution @($sampleArray | ForEach-Object { $_.allAdaptersSentBytesPerSecond })
        allAdaptersReceivedBytesPerSecond = Get-Distribution @($sampleArray | ForEach-Object { $_.allAdaptersReceivedBytesPerSecond })
    }
    samples = $sampleArray
}

try {
    $versionInfo = (Get-Item -LiteralPath $teacherProcess.Path -ErrorAction Stop).VersionInfo
    $report.teacherApplicationVersion = $versionInfo.ProductVersion
}
catch { }

$json = ConvertTo-Json -InputObject $report -Depth 8
$temporaryPath = "$outputPath.tmp"
[System.IO.File]::WriteAllText($temporaryPath, $json, [System.Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporaryPath -Destination $outputPath
Write-Host "采集完成：$($sampleArray.Count) 个样本。报告已保存到 $outputPath"
