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
    $expectCompanion = $Role -eq 'Student'
    if ((Test-Path -LiteralPath $agentPath -PathType Leaf) -ne $ExpectAgent -or
        (Test-Path -LiteralPath $companionPath -PathType Leaf) -ne $expectCompanion -or
        ($Role -eq 'Student' -and (Test-Path -LiteralPath $teacherExecutablePath)) -or
        ($Role -eq 'Teacher' -and (Test-Path -LiteralPath $studentExecutablePath))) {
        throw "$Role installer contains the wrong role payload."
    }

    $startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonStartup)
    $startupShortcuts = @(Get-ChildItem -LiteralPath $startupDirectory -Filter 'Veyon Campus*.lnk' -File)
    if ($expectCompanion -and $startupShortcuts.Count -ne 1) {
        throw "Student installer should create exactly one Veyon Campus startup shortcut; found $($startupShortcuts.Count)."
    }
    if (-not $expectCompanion -and $startupShortcuts.Count -ne 0) {
        throw "Teacher installer unexpectedly created a Veyon Campus startup shortcut."
    }

    if ($expectCompanion) {
        $shell = New-Object -ComObject WScript.Shell
        $shortcutPath = $startupShortcuts[0].FullName
        $companionShortcut = $shell.CreateShortcut($shortcutPath)
        $shortcutTarget = [string]$companionShortcut.TargetPath
        $shortcutArguments = [string]$companionShortcut.Arguments
        $shortcutWorkingDirectory = [string]$companionShortcut.WorkingDirectory
        $expectedWorkingDirectory = Split-Path -Parent $companionPath
        $shortcutIsValid = $false
        if (-not [string]::IsNullOrWhiteSpace($shortcutTarget) -and
            -not [string]::IsNullOrWhiteSpace($shortcutWorkingDirectory)) {
            $shortcutIsValid =
                [IO.Path]::GetFullPath($shortcutTarget) -eq [IO.Path]::GetFullPath($companionPath) -and
                $shortcutArguments -eq '--startup' -and
                [IO.Path]::GetFullPath($shortcutWorkingDirectory) -eq [IO.Path]::GetFullPath($expectedWorkingDirectory)
        }
        if (-not $shortcutIsValid) {
            # Read the exact same installer-created link bytes through a simple
            # temporary path before treating an empty COM result as a bad shortcut.
            $asciiShortcutPath = Join-Path $env:RUNNER_TEMP 'veyon-student-startup-readback.lnk'
            Copy-Item -LiteralPath $shortcutPath -Destination $asciiShortcutPath -Force
            $asciiShortcut = $shell.CreateShortcut($asciiShortcutPath)
            $asciiTarget = [string]$asciiShortcut.TargetPath
            $asciiArguments = [string]$asciiShortcut.Arguments
            $asciiWorkingDirectory = [string]$asciiShortcut.WorkingDirectory
            $asciiShortcutIsValid = $false
            if (-not [string]::IsNullOrWhiteSpace($asciiTarget) -and
                -not [string]::IsNullOrWhiteSpace($asciiWorkingDirectory)) {
                $asciiShortcutIsValid =
                    [IO.Path]::GetFullPath($asciiTarget) -eq [IO.Path]::GetFullPath($companionPath) -and
                    $asciiArguments -eq '--startup' -and
                    [IO.Path]::GetFullPath($asciiWorkingDirectory) -eq [IO.Path]::GetFullPath($expectedWorkingDirectory)
            }
            if ($asciiShortcutIsValid) {
                Write-Host 'Student startup shortcut passed readback from an ASCII-named copy of the installed shortcut.'
            } else {
                $shortcutBytes = [IO.File]::ReadAllBytes($shortcutPath)
                $shortcutEmbeddedStrings = [regex]::Matches(
                    [Text.Encoding]::Unicode.GetString($shortcutBytes),
                    '[ -~]{4,}') | ForEach-Object { $_.Value }
                $shortcutFileSize = $shortcutBytes.Length
                $shortcutStringSummary = [string]::Join(' | ', $shortcutEmbeddedStrings)
                $probePath = Join-Path $env:RUNNER_TEMP 'veyon-shortcut-readback-probe.lnk'
                $probe = $shell.CreateShortcut($probePath)
                $probe.TargetPath = $companionPath
                $probe.Arguments = '--startup'
                $probe.WorkingDirectory = $expectedWorkingDirectory
                $probe.Save()
                $probeReadback = $shell.CreateShortcut($probePath)
                $probeSummary = "target=$([string]$probeReadback.TargetPath); arguments=$([string]$probeReadback.Arguments); workingDirectory=$([string]$probeReadback.WorkingDirectory)"
                throw "Student startup shortcut is invalid: path=$shortcutPath; target=$shortcutTarget; arguments=$shortcutArguments; workingDirectory=$shortcutWorkingDirectory; ASCII copy target=$asciiTarget; arguments=$asciiArguments; workingDirectory=$asciiWorkingDirectory; bytes=$shortcutFileSize; embeddedStrings=$shortcutStringSummary; COM probe=$probeSummary"
            }
        }
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
