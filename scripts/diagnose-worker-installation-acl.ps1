[CmdletBinding()]
param(
    [ValidateSet('TeacherConsole', 'StudentSetup')]
    [string]$Role = 'TeacherConsole'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw '该只读诊断仅支持 Windows。'
}
if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    throw '请使用 64 位 PowerShell 运行该诊断，以匹配 win-x64 TeacherConsole 的 Program Files 路径。'
}

$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$roleFolder = if ($Role -eq 'StudentSetup') { 'Student' } else { 'Teacher' }
$installRoot = [IO.Path]::GetFullPath((Join-Path $programFiles "Veyon Campus\$roleFolder"))
if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) {
    throw "找不到安装目录：$installRoot"
}

$currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$untrustedSids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($sid in @($currentUserSid, 'S-1-1-0', 'S-1-5-11', 'S-1-5-32-545', 'S-1-5-4')) {
    [void]$untrustedSids.Add($sid)
}

# Keep this primitive-rights mask aligned with WorkerInstallationGuard.WriteRights.
$writeMask = [int]([Security.AccessControl.FileSystemRights]::WriteData -bor
    [Security.AccessControl.FileSystemRights]::AppendData -bor
    [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
    [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
    [Security.AccessControl.FileSystemRights]::Delete -bor
    [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
    [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
    [Security.AccessControl.FileSystemRights]::TakeOwnership)

$findings = [Collections.Generic.List[object]]::new()
$readErrors = [Collections.Generic.List[object]]::new()
$checked = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$pending = [Collections.Generic.Stack[string]]::new()

function Get-IdentityLabel([string]$Sid) {
    if ($Sid -eq $currentUserSid) { return 'Current UI user' }
    switch ($Sid) {
        'S-1-1-0' { return 'Everyone' }
        'S-1-5-11' { return 'Authenticated Users' }
        'S-1-5-32-545' { return 'Users' }
        'S-1-5-4' { return 'Interactive users' }
        default { return 'Untrusted Windows principal' }
    }
}

$scopePaths = [Collections.Generic.List[string]]::new()
$scopePaths.Add($programFiles)
$current = $programFiles
foreach ($segment in @('Veyon Campus', $roleFolder)) {
    $current = Join-Path $current $segment
    $scopePaths.Add($current)
}
$scopePaths.Add((Join-Path $installRoot 'Worker'))
$scopePaths.Add((Join-Path $installRoot 'Worker\VeyonCampus.Worker.exe'))
$scopePaths.Add((Join-Path $installRoot $(if ($Role -eq 'StudentSetup') { 'VeyonCampus.StudentSetup.exe' } else { 'VeyonCampus.Teacher.exe' })))
$scopePaths.Add((Join-Path $installRoot 'veyon-campus-role.json'))

# Inspect parent ACLs without walking their contents. The C# guard recursively
# checks only the role install tree after checking each parent component.
foreach ($path in $scopePaths) {
    $fullPath = [IO.Path]::GetFullPath($path)
    if (Test-Path -LiteralPath $fullPath) { $pending.Push($fullPath) }
    else { $readErrors.Add([pscustomobject]@{ Path = $fullPath; Error = 'Required installation object is missing.' }) }
}
$pending.Push($installRoot)

while ($pending.Count -gt 0) {
    $path = $pending.Pop()
    if (-not $checked.Add($path)) { continue }

    try {
        $attributes = [IO.File]::GetAttributes($path)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $findings.Add([pscustomobject]@{ Path = $path; Identity = ''; Rights = 'ReparsePoint'; Owner = '' })
            continue
        }

        $acl = Get-Acl -LiteralPath $path
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($untrustedSids.Contains($owner)) {
            $findings.Add([pscustomobject]@{
                Path = $path; Identity = ''; Rights = 'Untrusted owner'; Owner = Get-IdentityLabel $owner
            })
        }

        foreach ($rule in $acl.Access) {
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
            $sid = try {
                if ($rule.IdentityReference -is [Security.Principal.SecurityIdentifier]) {
                    $rule.IdentityReference.Value
                } else {
                    $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
                }
            } catch {
                continue
            }
            $rights = [int]$rule.FileSystemRights
            if ($untrustedSids.Contains($sid) -and (($rights -band $writeMask) -ne 0)) {
                $findings.Add([pscustomobject]@{
                    Path = $path
                    Identity = Get-IdentityLabel $sid
                    Rights = "$($rule.FileSystemRights) (0x$('{0:X8}' -f [uint32]$rights))"
                    Owner = Get-IdentityLabel $owner
                })
            }
        }

        $insideInstallTree = $path.Equals($installRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith($installRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)
        if ($insideInstallTree -and ($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($path)) {
                $pending.Push([IO.Path]::GetFullPath($child))
            }
        }
    } catch {
        $readErrors.Add([pscustomobject]@{ Path = $path; Error = $_.Exception.Message })
    }
}

Write-Output "Role: $Role"
Write-Output "Install root: $installRoot"
Write-Output "Checked objects: $($checked.Count)"
if ($findings.Count -eq 0) {
    Write-Output 'No untrusted write ACL, untrusted owner, or reparse point was found.'
} else {
    Write-Output 'Potential Worker guard blockers (read-only results):'
    $findings | Sort-Object Path, Identity, Rights -Unique | Format-List Path, Identity, Rights, Owner | Out-String | Write-Output
}
if ($readErrors.Count -gt 0) {
    Write-Output 'Objects whose ACL could not be read:'
    $readErrors | Sort-Object Path -Unique | Format-List Path, Error | Out-String | Write-Output
}
