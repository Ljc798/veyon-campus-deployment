[CmdletBinding()]
param(
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$serviceName = 'VeyonCampusMaintenanceService'
$recoveryTaskName = '\VeyonCampusMaintenanceServiceRecovery'
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$programData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
$maintenanceRoot = [IO.Path]::GetFullPath((Join-Path $programFiles 'Veyon Campus\MaintenanceService'))
$agentConfigRoot = [IO.Path]::GetFullPath((Join-Path $programData 'VeyonCampus\WebsitePolicy'))

function Get-RelativePathUnderRoot {
    param([string]$Root, [string]$Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $fullRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    return $fullPath.Substring($prefix.Length)
}

function Test-NoReparsePointPath {
    param([string]$Root, [string]$Path)
    try {
        $relative = Get-RelativePathUnderRoot -Root $Root -Path $Path
        if ([string]::IsNullOrWhiteSpace($relative)) { return $false }
        $current = [IO.Path]::GetFullPath($Root)
        if (-not (Test-Path -LiteralPath $current -PathType Container)) { return $false }
        if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $false
        }
        foreach ($segment in ($relative -split '[\\/]')) {
            $current = Join-Path $current $segment
            if (-not (Test-Path -LiteralPath $current)) { return $false }
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                return $false
            }
        }
        return $true
    }
    catch { return $false }
}

function Get-ServiceExecutablePath {
    param([string]$PathName)
    if ([string]::IsNullOrWhiteSpace($PathName)) { return $null }
    if ($PathName -match '^\s*"(?<path>[^"]+\.exe)"(?:\s|$)') { return $Matches.path }
    if ($PathName -match '^\s*(?<path>.+?\.exe)(?:\s|$)') { return $Matches.path.Trim('"') }
    return $null
}

function Get-ProductVersion {
    param([string]$Path)
    if (-not (Test-NoReparsePointPath -Root $maintenanceRoot -Path $Path)) { return $null }
    try {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        return [ordered]@{
            file = (Get-RelativePathUnderRoot -Root $maintenanceRoot -Path $Path)
            productVersion = $version.ProductVersion
            fileVersion = $version.FileVersion
        }
    }
    catch { return $null }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw '该证据收集器只能在 Windows 上运行。'
}

$service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
$serviceEvidence = [ordered]@{
    installed = $null -ne $service
    state = $null
    startMode = $null
    startName = $null
    imagePathManaged = $false
    imagePathRelative = $null
    serviceBinary = $null
    updateHelper = $null
}
if ($null -ne $service) {
    $serviceEvidence.state = [string]$service.State
    $serviceEvidence.startMode = [string]$service.StartMode
    $serviceEvidence.startName = [string]$service.StartName
    $serviceExecutable = Get-ServiceExecutablePath -PathName ([string]$service.PathName)
    if ($null -ne $serviceExecutable) {
        $serviceExecutable = [IO.Path]::GetFullPath($serviceExecutable)
        $relative = Get-RelativePathUnderRoot -Root $maintenanceRoot -Path $serviceExecutable
        if ($null -ne $relative) {
            $serviceEvidence.imagePathManaged = $true
            $serviceEvidence.imagePathRelative = $relative
            $serviceEvidence.serviceBinary = Get-ProductVersion -Path $serviceExecutable
            $helperPath = Join-Path ([IO.Path]::GetDirectoryName($serviceExecutable)) 'VeyonCampus.UpdateHelper.exe'
            $serviceEvidence.updateHelper = Get-ProductVersion -Path $helperPath
        }
    }
}

$taskEvidence = [ordered]@{
    present = $false
    principalIsSystem = $null
    executableManaged = $false
    executableRelative = $null
    arguments = $null
    bootTriggerPresent = $null
}
$schtasks = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'schtasks.exe'
$taskOutput = & $schtasks /Query /TN $recoveryTaskName /XML 2>$null
if ($LASTEXITCODE -eq 0) {
    $taskEvidence.present = $true
    $taskXml = [xml]($taskOutput -join "`n")
    $principal = $taskXml.SelectSingleNode("//*[local-name()='Principal']/*[local-name()='UserId']")
    $command = $taskXml.SelectSingleNode("//*[local-name()='Actions']/*[local-name()='Exec']/*[local-name()='Command']")
    $arguments = $taskXml.SelectSingleNode("//*[local-name()='Actions']/*[local-name()='Exec']/*[local-name()='Arguments']")
    $workingDirectory = $taskXml.SelectSingleNode("//*[local-name()='Actions']/*[local-name()='Exec']/*[local-name()='WorkingDirectory']")
    $taskEvidence.principalIsSystem = $null -ne $principal -and $principal.InnerText -eq 'S-1-5-18'
    $taskEvidence.arguments = if ($null -ne $arguments) { $arguments.InnerText } else { $null }
    $taskEvidence.bootTriggerPresent = $null -ne $taskXml.SelectSingleNode("//*[local-name()='BootTrigger']")
    if ($null -ne $command -and $null -ne $workingDirectory) {
        $commandPath = [IO.Path]::GetFullPath($command.InnerText)
        $workingPath = [IO.Path]::GetFullPath($workingDirectory.InnerText)
        $relative = Get-RelativePathUnderRoot -Root $maintenanceRoot -Path $commandPath
        $commandDirectory = [IO.Path]::GetDirectoryName($commandPath)
        $directHelperPath = $null -ne $relative -and
            $relative -match '^(?:Current|Versions\\[^\\]+)\\VeyonCampus\.UpdateHelper\.exe$'
        $taskEvidence.executableManaged = $null -ne $relative -and
            $directHelperPath -and
            [IO.Path]::GetFileName($commandPath) -eq 'VeyonCampus.UpdateHelper.exe' -and
            $taskEvidence.arguments -eq '--recover-maintenance-service' -and
            $taskEvidence.principalIsSystem -and
            [string]::Equals($commandDirectory, $workingPath, [StringComparison]::OrdinalIgnoreCase)
        if ($taskEvidence.executableManaged) { $taskEvidence.executableRelative = $relative }
    }
}

$transactionEvidence = @()
$seenTransactionPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $agentConfigRoot -PathType Container) {
    if (Test-NoReparsePointPath -Root $programData -Path $agentConfigRoot) {
        foreach ($configDirectory in Get-ChildItem -LiteralPath $agentConfigRoot -Directory) {
            if ($configDirectory.Name -notmatch '^[a-fA-F0-9]{24}$' -or
                -not (Test-NoReparsePointPath -Root $programData -Path $configDirectory.FullName)) { continue }
            $configFile = Join-Path $configDirectory.FullName ('agent-' + $configDirectory.Name + '.json')
            if (-not (Test-NoReparsePointPath -Root $programData -Path $configFile)) { continue }
            $maintenanceDirectory = Join-Path $configDirectory.FullName 'Maintenance'
            $transactionPath = Join-Path $maintenanceDirectory 'maintenance-service-release-transaction.json'
            if ($seenTransactionPaths.Contains($transactionPath) -or
                -not (Test-Path -LiteralPath $transactionPath -PathType Leaf) -or
                -not (Test-NoReparsePointPath -Root $programData -Path $transactionPath)) { continue }
            [void]$seenTransactionPaths.Add($transactionPath)
            try {
                $transaction = Get-Content -LiteralPath $transactionPath -Raw | ConvertFrom-Json
                $transactionEvidence += [ordered]@{
                    state = [string]$transaction.state
                    previousVersion = [string]$transaction.previousVersion
                    targetVersion = [string]$transaction.targetVersion
                    updatedUtc = [string]$transaction.updatedUtc
                }
            }
            catch {
                $transactionEvidence += [ordered]@{ state = 'unreadable'; previousVersion = $null; targetVersion = $null; updatedUtc = $null }
            }
        }
    }
}

$report = [ordered]@{
    schemaVersion = 1
    capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    scope = 'read-only; excludes campus/device identity, network address, operationId, commandId, keys, and raw error text'
    service = $serviceEvidence
    recoveryTask = $taskEvidence
    releaseTransactions = @($transactionEvidence)
}
$json = $report | ConvertTo-Json -Depth 8
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $evidenceDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'VeyonCampus-Maintenance-Evidence'
    New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
    $OutputPath = Join-Path $evidenceDirectory ('maintenance-service-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($fullOutputPath)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) { throw '证据输出目录无效。' }
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
[IO.File]::WriteAllText($fullOutputPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output $fullOutputPath
