$ErrorActionPreference = 'Stop'

function Invoke-InstallerSmokeTest {
    param(
        [string]$InstallerPattern,
        [string]$Role,
        [string]$RoleInfoRole,
        [string]$ExecutableName,
        [bool]$ExpectAgent
    )

    $artifactDirectory = Join-Path $env:GITHUB_WORKSPACE 'artifacts'
    $installers = @(Get-ChildItem -LiteralPath $artifactDirectory -Filter $InstallerPattern -File)
    if ($installers.Count -ne 1) {
        throw "Expected exactly one $Role installer; found $($installers.Count)."
    }

    $installDirectory = Join-Path $env:ProgramFiles "Veyon Campus\$Role"
    $install = Start-Process -FilePath $installers[0].FullName -ArgumentList @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/SP-'
    ) -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "$Role installer returned $($install.ExitCode)."
    }

    $executablePath = Join-Path $installDirectory $ExecutableName
    $roleInfoPath = Join-Path $installDirectory 'veyon-campus-role.json'
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $roleInfoPath -PathType Leaf)) {
        throw "$Role installer did not install its entry point and role metadata."
    }

    $roleInfo = Get-Content -LiteralPath $roleInfoPath -Raw | ConvertFrom-Json
    if ($roleInfo.role -ne $RoleInfoRole -or [string]::IsNullOrWhiteSpace($roleInfo.version)) {
        throw "$Role installer wrote invalid role metadata."
    }

    $agentPath = Join-Path $installDirectory 'WebsitePolicyAgent\VeyonCampus.Agent.exe'
    $companionPath = Join-Path $installDirectory 'StudentCompanion\VeyonCampus.StudentCompanion.exe'
    $studentExecutablePath = Join-Path $installDirectory 'VeyonCampus.StudentSetup.exe'
    $teacherExecutablePath = Join-Path $installDirectory 'VeyonCampus.Teacher.exe'
    if ((Test-Path -LiteralPath $agentPath -PathType Leaf) -ne $ExpectAgent -or
        (Test-Path -LiteralPath $companionPath -PathType Leaf) -or
        ($Role -eq 'Student' -and (Test-Path -LiteralPath $teacherExecutablePath)) -or
        ($Role -eq 'Teacher' -and (Test-Path -LiteralPath $studentExecutablePath))) {
        throw "$Role installer contains the wrong role payload."
    }

    $startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonStartup)
    $startupShortcuts = @(Get-ChildItem -LiteralPath $startupDirectory -Filter 'Veyon Campus*.lnk' -File)
    if ($startupShortcuts.Count -ne 0) {
        throw "$Role basic-edition installer unexpectedly created a classroom companion startup shortcut."
    }

    $helperPath = Join-Path $env:ProgramFiles "Veyon Campus\Updater\$Role\$($roleInfo.version)\VeyonCampus.UpdateHelper.exe"
    if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
        throw "$Role installer did not install its version-matched update helper."
    }

    $uninstallerPath = Join-Path $installDirectory 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstallerPath -PathType Leaf)) {
        throw "$Role installer did not register an uninstaller."
    }

    $uninstall = Start-Process -FilePath $uninstallerPath -ArgumentList @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART'
    ) -Wait -PassThru
    $remainingShortcuts = @(Get-ChildItem -LiteralPath $startupDirectory -Filter 'Veyon Campus*.lnk' -File)
    if ($uninstall.ExitCode -ne 0 -or
        (Test-Path -LiteralPath $executablePath) -or
        (Test-Path -LiteralPath $helperPath) -or
        (Test-Path -LiteralPath $companionPath) -or
        $remainingShortcuts.Count -gt 0) {
        throw "$Role uninstall smoke test failed (exit $($uninstall.ExitCode))."
    }

    Write-Host "$Role install, payload isolation, helper, metadata, shortcut, and uninstall passed."
}

Invoke-InstallerSmokeTest 'VeyonCampus-Student-Setup-*-win-x64.exe' 'Student' 'StudentSetup' 'VeyonCampus.StudentSetup.exe' $true
Invoke-InstallerSmokeTest 'VeyonCampus-Teacher-Setup-*-win-x64.exe' 'Teacher' 'TeacherConsole' 'VeyonCampus.Teacher.exe' $false
