$ErrorActionPreference = 'Stop'

function Assert-StartupShortcut {
    param(
        [string]$ShortcutPath,
        [string]$ExpectedTargetPath,
        [string]$ExpectedWorkingDirectory,
        [string]$Description
    )

    $shell = New-Object -ComObject WScript.Shell
    $asciiShortcutPath = Join-Path $env:RUNNER_TEMP ("veyon-startup-" + [Guid]::NewGuid().ToString('N') + '.lnk')
    Copy-Item -LiteralPath $ShortcutPath -Destination $asciiShortcutPath -Force
    foreach ($readPath in @($ShortcutPath, $asciiShortcutPath)) {
        $shortcut = $shell.CreateShortcut($readPath)
        $shortcutTarget = [string]$shortcut.TargetPath
        $shortcutArguments = [string]$shortcut.Arguments
        $shortcutWorkingDirectory = [string]$shortcut.WorkingDirectory
        if (-not [string]::IsNullOrWhiteSpace($shortcutTarget) -and
            -not [string]::IsNullOrWhiteSpace($shortcutWorkingDirectory) -and
            [IO.Path]::GetFullPath($shortcutTarget) -eq [IO.Path]::GetFullPath($ExpectedTargetPath) -and
            $shortcutArguments -eq '--startup' -and
            [IO.Path]::GetFullPath($shortcutWorkingDirectory) -eq [IO.Path]::GetFullPath($ExpectedWorkingDirectory)) {
            return
        }
    }

    throw "$Description startup shortcut is invalid: path=$ShortcutPath; target=$shortcutTarget; arguments=$shortcutArguments; workingDirectory=$shortcutWorkingDirectory."
}

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
    $studentCompanionShortcutName = 'Veyon Campus ' + (-join @(
        [char]0x5B66, [char]0x751F, [char]0x8BFE, [char]0x5802, [char]0x52A9, [char]0x624B
    )) + '.lnk'
    $edgePolicyExecutorShortcutName = 'Veyon Campus Edge ' + (-join @(
        [char]0x7B56, [char]0x7565, [char]0x6267, [char]0x884C, [char]0x5668
    )) + '.lnk'
    $expectedStudentStartupShortcutNames = @(
        $studentCompanionShortcutName,
        $edgePolicyExecutorShortcutName
    )
    if ($expectCompanion -and $startupShortcuts.Count -ne $expectedStudentStartupShortcutNames.Count) {
        throw "Student installer should create exactly $($expectedStudentStartupShortcutNames.Count) Veyon Campus startup shortcuts; found $($startupShortcuts.Count)."
    }
    if (-not $expectCompanion -and $startupShortcuts.Count -ne 0) {
        throw "Teacher installer unexpectedly created a Veyon Campus startup shortcut."
    }

    if ($expectCompanion) {
        foreach ($expectedName in $expectedStudentStartupShortcutNames) {
            if ($startupShortcuts.Name -cnotcontains $expectedName) {
                throw "Student installer did not create the expected startup shortcut '$expectedName'."
            }
        }

        $expectedCompanionWorkingDirectory = Split-Path -Parent $companionPath
        Assert-StartupShortcut `
            (Join-Path $startupDirectory $expectedStudentStartupShortcutNames[0]) `
            $companionPath `
            $expectedCompanionWorkingDirectory `
            'Student companion'

        $executorPath = Join-Path $installDirectory 'EdgePolicyExecutor\VeyonCampus.EdgePolicyExecutor.exe'
        $expectedExecutorWorkingDirectory = Split-Path -Parent $executorPath
        Assert-StartupShortcut `
            (Join-Path $startupDirectory $expectedStudentStartupShortcutNames[1]) `
            $executorPath `
            $expectedExecutorWorkingDirectory `
            'Edge policy executor'
    }

    $helperPath = Join-Path $env:ProgramFiles "Veyon Campus\Updater\$Role\$($roleInfo.version)\VeyonCampus.UpdateHelper.exe"
    if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
        throw "$Role installer did not install its version-matched update helper."
    }

    $uninstallerPath = Join-Path $installDirectory 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstallerPath -PathType Leaf)) {
        throw "$Role installer did not register an uninstaller."
    }

    $uninstallLogPath = Join-Path $env:RUNNER_TEMP "VeyonCampus-$Role-uninstall.log"
    Remove-Item -LiteralPath $uninstallLogPath -Force -ErrorAction SilentlyContinue
    $uninstall = Start-Process -FilePath $uninstallerPath -ArgumentList @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        "/LOG=`"$uninstallLogPath`""
    ) -Wait -PassThru
    $remainingShortcuts = @(Get-ChildItem -LiteralPath $startupDirectory -Filter 'Veyon Campus*.lnk' -File)
    if ($uninstall.ExitCode -ne 0 -or
        (Test-Path -LiteralPath $executablePath) -or
        (Test-Path -LiteralPath $helperPath) -or
        (Test-Path -LiteralPath $companionPath) -or
        $remainingShortcuts.Count -gt 0) {
        Write-Host "$Role uninstall details: executable=$(Test-Path -LiteralPath $executablePath); helper=$(Test-Path -LiteralPath $helperPath); companion=$(Test-Path -LiteralPath $companionPath); shortcuts=$($remainingShortcuts.Count); log=$uninstallLogPath"
        if (Test-Path -LiteralPath $uninstallLogPath -PathType Leaf) {
            Write-Host "---- $Role Inno Setup uninstall log ----"
            Get-Content -LiteralPath $uninstallLogPath
            Write-Host "---- end $Role Inno Setup uninstall log ----"
        }
        throw "$Role uninstall smoke test failed (exit $($uninstall.ExitCode))."
    }

    Write-Host "$Role install, payload isolation, helper, metadata, shortcut, and uninstall passed."
}

Invoke-InstallerSmokeTest 'VeyonCampus-Student-Setup-*-win-x64.exe' 'Student' 'StudentSetup' 'VeyonCampus.StudentSetup.exe' $true
Invoke-InstallerSmokeTest 'VeyonCampus-Teacher-Setup-*-win-x64.exe' 'Teacher' 'TeacherConsole' 'VeyonCampus.Teacher.exe' $false

