[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipRestore,
    [switch]$SkipCoreChecks,
    [switch]$WindowsPackageSmoke,
    [switch]$CollectWindowsEnvironment
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repoRoot 'VeyonCampus.slnx'
$appProjectPath = Join-Path $repoRoot 'src/VeyonCampus.App/VeyonCampus.App.csproj'
$checksProjectPath = Join-Path $repoRoot 'tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj'
$packageScriptPath = Join-Path $PSScriptRoot 'package-windows-offline.ps1'
$environmentScriptPath = Join-Path $PSScriptRoot 'collect-windows-test-environment.ps1'
$script:DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source

function Invoke-DotNet {
    param([string[]]$Arguments)

    Write-Host ("`n> dotnet " + ($Arguments -join ' ')) -ForegroundColor DarkCyan
    & $script:DotNetPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "dotnet 命令失败，退出代码：$exitCode"
    }
}

Push-Location $repoRoot
try {
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'

    $sdkVersion = (& $script:DotNetPath --version | Select-Object -First 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') {
        throw ".NET 10 SDK is required; detected '$sdkVersion'."
    }
    Write-Host ".NET SDK $sdkVersion" -ForegroundColor Green

    if (-not $SkipRestore) {
        Invoke-DotNet @('restore', 'VeyonCampus.slnx', '--locked-mode')
    }

    Invoke-DotNet @('build', 'VeyonCampus.slnx', '-c', $Configuration, '--no-restore')
    Invoke-DotNet @('build', $appProjectPath, '-c', $Configuration, '--no-restore', '-p:VeyonCampusRole=TeacherConsole')

    if (-not $SkipCoreChecks) {
        Invoke-DotNet @('run', '--project', $checksProjectPath, '-c', $Configuration, '--no-build', '--no-restore')
    }

    if ($CollectWindowsEnvironment) {
        & $environmentScriptPath -NonInteractive
        if ($LASTEXITCODE -ne 0) { throw 'Windows 环境摘要采集失败。' }
    }

    if ($WindowsPackageSmoke) {
        if ($env:OS -ne 'Windows_NT') {
            throw '-WindowsPackageSmoke must run on a Windows host.'
        }

        $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
        $smokeRoot = Join-Path $repoRoot "artifacts/windows-smoke-$stamp"
        foreach ($role in @('StudentSetup', 'TeacherConsole')) {
            $slug = if ($role -eq 'StudentSetup') { 'student-setup' } else { 'teacher-console' }
            & $packageScriptPath -Role $role `
                -OutputDirectory (Join-Path $smokeRoot $slug) `
                -ZipPath (Join-Path $smokeRoot "$slug.zip")
            if ($LASTEXITCODE -ne 0) { throw "Windows package smoke failed for $role." }
        }
    }

    Write-Host "`n检查入口完成。" -ForegroundColor Green
}
finally {
    Pop-Location
}
