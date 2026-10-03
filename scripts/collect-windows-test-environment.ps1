[CmdletBinding()]
param(
    [string]$OutputPath,
    [ValidateSet('confirmed', 'not-confirmed', 'unknown')]
    [string]$RestoreEnvironmentStatus = 'unknown',
    [ValidateSet('enrolled', 'not-enrolled', 'unknown')]
    [string]$MdmEnrollmentStatus = 'unknown',
    # Retained for existing automation; this collector is always noninteractive.
    [switch]$NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
    throw '此采集脚本只能在 Windows 10/11 测试机上运行。'
}

$os = Get-CimInstance -ClassName Win32_OperatingSystem
$computer = Get-CimInstance -ClassName Win32_ComputerSystem
$processors = @(Get-CimInstance -ClassName Win32_Processor)
$videoControllers = @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction SilentlyContinue)
$versionInfo = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue

$servicingBuild = [string]$os.BuildNumber
if ($null -ne $versionInfo -and $null -ne $versionInfo.UBR) {
    $servicingBuild = "$($os.BuildNumber).$([int]$versionInfo.UBR)"
}

$hotfixQueryStatus = 'unknown'
$reportedHotfixIds = @()
try {
    $hotfixes = @(Get-CimInstance -ClassName Win32_QuickFixEngineering -ErrorAction Stop)
    $reportedHotfixIds = @($hotfixes | ForEach-Object { [string]$_.HotFixID } |
        Where-Object { $_ -match '^KB\d+$' } | Sort-Object -Unique)
    $hotfixQueryStatus = 'read-successfully'
}
catch {
    $hotfixQueryStatus = 'query-failed'
}

$applicationIdentity = [ordered]@{
    queryStatus = 'unknown'
    serviceState = 'unknown'
    startMode = 'unknown'
}
try {
    $identityService = Get-CimInstance -ClassName Win32_Service -Filter "Name='AppIDSvc'" -ErrorAction Stop
    if ($null -eq $identityService) {
        $applicationIdentity.queryStatus = 'service-not-found'
        $applicationIdentity.serviceState = 'not-found'
    }
    else {
        $applicationIdentity.queryStatus = 'read-successfully'
        $applicationIdentity.serviceState = [string]$identityService.State
        $applicationIdentity.startMode = [string]$identityService.StartMode
    }
}
catch {
    $applicationIdentity.queryStatus = 'query-failed'
}

$effectiveAppLockerPolicy = [ordered]@{
    queryStatus = 'unknown'
    reportedRuleCount = $null
    ruleCollections = @()
    queryScope = 'Get-AppLockerPolicy -Effective reads local and Group Policy policy; it does not include AppLocker CSP/MDM policy.'
}
try {
    if ($null -eq (Get-Command -Name Get-AppLockerPolicy -ErrorAction SilentlyContinue)) {
        $effectiveAppLockerPolicy.queryStatus = 'cmdlet-unavailable'
    }
    else {
        $policyXmlText = [string](Get-AppLockerPolicy -Effective -Xml -ErrorAction Stop)
        $policyXml = [xml]$policyXmlText
        $collectionSummaries = @()
        $totalRuleCount = 0
        foreach ($collection in @($policyXml.SelectNodes('/AppLockerPolicy/RuleCollection'))) {
            $ruleCount = @($collection.ChildNodes | Where-Object {
                $_.NodeType -eq [System.Xml.XmlNodeType]::Element
            }).Count
            $totalRuleCount += $ruleCount
            $collectionSummaries += [ordered]@{
                type = [string]$collection.GetAttribute('Type')
                enforcementMode = [string]$collection.GetAttribute('EnforcementMode')
                reportedRuleCount = $ruleCount
            }
        }
        $effectiveAppLockerPolicy.queryStatus = if ($totalRuleCount -gt 0) { 'rules-reported' } else { 'no-rules-reported' }
        $effectiveAppLockerPolicy.reportedRuleCount = $totalRuleCount
        $effectiveAppLockerPolicy.ruleCollections = @($collectionSummaries)
    }
}
catch {
    $effectiveAppLockerPolicy.queryStatus = 'query-failed'
}

$architectureMap = @{ 0 = 'x86'; 5 = 'ARM'; 9 = 'x64'; 12 = 'ARM64'; 6 = 'Itanium' }
$architectures = @($processors | ForEach-Object {
    $key = [int]$_.Architecture
    if ($architectureMap.ContainsKey($key)) { $architectureMap[$key] } else { "Unknown-$key" }
} | Sort-Object -Unique)

$displayModes = @($videoControllers | Where-Object {
    $_.CurrentHorizontalResolution -gt 0 -and $_.CurrentVerticalResolution -gt 0
} | ForEach-Object {
    "$($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)"
} | Sort-Object -Unique)

$scalePercent = $null
$scaleSource = $null
try {
    if ($null -eq ('VeyonWindowsDpiProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class VeyonWindowsDpiProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    public static uint GetPrimaryTaskbarDpi()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        return taskbar == IntPtr.Zero ? 0 : GetDpiForWindow(taskbar);
    }
}
'@
    }

    $taskbarDpi = [VeyonWindowsDpiProbe]::GetPrimaryTaskbarDpi()
    if ($taskbarDpi -gt 0) {
        $scalePercent = [int][Math]::Round(($taskbarDpi / 96.0) * 100)
        $scaleSource = 'primary display taskbar DPI'
    }
}
catch {
    # Keep the scale unknown if the current session has no queryable shell window.
}

if ($null -eq $scalePercent) {
    $scaleRegistry = Get-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name LogPixels -ErrorAction SilentlyContinue
    if ($null -ne $scaleRegistry -and [int]$scaleRegistry.LogPixels -gt 0) {
        $scalePercent = [int][Math]::Round(([int]$scaleRegistry.LogPixels / 96.0) * 100)
        $scaleSource = 'HKCU LogPixels; per-monitor scaling may differ'
    }
}

# Human-confirmed facts are explicit command parameters. Missing values stay
# "unknown" so CI and scripted collection never block for console input.

$displayScaleDescription = if ($null -eq $scalePercent) {
    'unknown'
}
else {
    "$scalePercent% ($scaleSource)"
}

$report = [ordered]@{
    schemaVersion = 2
    capturedAtLocal = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
    operatingSystem = [string]$os.Caption
    version = [string]$os.Version
    build = [string]$os.BuildNumber
    architecture = if ($architectures.Count -eq 1) { $architectures[0] } else { $architectures -join ', ' }
    memoryGiB = [Math]::Round(([double]$computer.TotalPhysicalMemory / 1GB), 1)
    displayResolution = if ($displayModes.Count -gt 0) { $displayModes -join ', ' } else { 'unknown' }
    displayScale = $displayScaleDescription
    domainMembership = if ($computer.PartOfDomain) { 'domain-joined' } else { 'workgroup' }
    restoreEnvironment = $RestoreEnvironmentStatus
    applicationControlCompatibility = [ordered]@{
        windowsEdition = [string]$os.Caption
        editionId = if ($null -ne $versionInfo) { [string]$versionInfo.EditionID } else { 'unknown' }
        operatingSystemSkuId = if ($null -ne $os.OperatingSystemSKU) { [int]$os.OperatingSystemSKU } else { $null }
        displayVersion = if ($null -ne $versionInfo) { [string]$versionInfo.DisplayVersion } else { 'unknown' }
        servicingBuild = $servicingBuild
        hotfixQueryStatus = $hotfixQueryStatus
        kb5024351ListedByWin32QuickFixEngineering = if ($hotfixQueryStatus -eq 'read-successfully') {
            $reportedHotfixIds -contains 'KB5024351'
        } else { $null }
        reportedHotfixIds = $reportedHotfixIds
        applicationIdentityService = $applicationIdentity
        effectiveGroupPolicyAppLocker = $effectiveAppLockerPolicy
        mdmEnrollmentStatus = $MdmEnrollmentStatus
        manualReviewRequired = $true
    }
}

$json = $report | ConvertTo-Json -Depth 4
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Write-Output $json
}
else {
    $resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $resolvedOutputPath
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $encoding = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($resolvedOutputPath, $json + [Environment]::NewLine, $encoding)
    Write-Host "已保存不含设备名、账户名或域名的环境摘要：$resolvedOutputPath" -ForegroundColor Green
}
