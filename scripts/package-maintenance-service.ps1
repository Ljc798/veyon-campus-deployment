[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string]$InstallerPath,
    [string]$ReleasePublicKeyPath,
    [string]$TestReleasePublicKeyPath,
    [switch]$SkipRestore,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$projectPath = Join-Path $repoRoot 'src/VeyonCampus.MaintenanceService/VeyonCampus.MaintenanceService.csproj'
$helperProjectPath = Join-Path $repoRoot 'tools/VeyonCampus.UpdateHelper/VeyonCampus.UpdateHelper.csproj'
$installerTemplatePath = Join-Path $repoRoot 'installer/VeyonCampus-MaintenanceService.iss'

if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8)
    $Version = [string](@($projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1)[0])
}
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw 'Maintenance Service 版本必须符合 SemVer X.Y.Z 格式。'
}

if (-not (Test-Path -LiteralPath $artifactsRoot -PathType Container)) {
    New-Item -Path $artifactsRoot -ItemType Directory -Force | Out-Null
}
$artifactsItem = Get-Item -LiteralPath $artifactsRoot -Force
if (($artifactsItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "artifacts 目录不能是符号链接或重解析点：$artifactsRoot"
}

function Resolve-ArtifactPath {
    param([string]$Path, [string]$Description)
    $resolved = if ([IO.Path]::IsPathRooted($Path)) {
        [IO.Path]::GetFullPath($Path)
    } else {
        [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
    }
    if (-not $resolved.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description 必须放在仓库 artifacts 目录中：$resolved"
    }
    return $resolved
}

function Assert-NoReparsePointInPath {
    param([string]$Path, [string]$Description)
    $resolved = [IO.Path]::GetFullPath($Path)
    $cursor = $resolved
    while ($cursor.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description 路径包含符号链接或重解析点：$cursor"
            }
        }
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactsRoot "windows-x64-v$Version-maintenance-service"
}
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $artifactsRoot "VeyonCampus-Maintenance-Setup-$Version-win-x64.exe"
}
$publishDirectory = Resolve-ArtifactPath $OutputDirectory 'Maintenance 发布目录'
$installerFile = Resolve-ArtifactPath $InstallerPath 'Maintenance 安装器'
if ([IO.Path]::GetExtension($installerFile) -ine '.exe' -or
    $installerFile.StartsWith($publishDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
    $publishDirectory.StartsWith($installerFile.TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $publishDirectory.Equals($installerFile, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Maintenance 发布目录和安装器必须互不包含，且安装器必须是 .exe 文件。'
}

Assert-NoReparsePointInPath $publishDirectory 'Maintenance 发布目录'
Assert-NoReparsePointInPath $installerFile 'Maintenance 安装器'
if ($Clean) {
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $installerFile) {
        Remove-Item -LiteralPath $installerFile -Force
    }
} elseif ((Test-Path -LiteralPath $publishDirectory) -or (Test-Path -LiteralPath $installerFile)) {
    throw "Maintenance 打包输出已存在；确认后加 -Clean 替换。`n目录：$publishDirectory`n安装器：$installerFile"
}

$dotnetCandidates = @(
    $env:VEYONCAMPUS_DOTNET_PATH,
    (Join-Path ${env:ProgramFiles} 'dotnet/dotnet.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'dotnet/dotnet.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique
$dotnetPath = $null
foreach ($candidate in $dotnetCandidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    $versionOutput = & $candidate --version 2>&1
    if ($LASTEXITCODE -eq 0 -and [string]($versionOutput | Select-Object -First 1) -match '^10\.') {
        $dotnetPath = $candidate
        break
    }
}
if (-not $dotnetPath) {
    throw '需要 .NET 10 SDK；请设置 VEYONCAMPUS_DOTNET_PATH 指向 64 位 dotnet.exe。'
}

$arguments = @('publish', $projectPath, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
    '-p:AppHostDotNetSearch=Global', "-p:Version=$Version", "-p:InformationalVersion=$Version",
    '-p:IncludeSourceRevisionInInformationalVersion=false', '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json',
    '-p:VeyonCampusIncludeInstaller=false', '-o', $publishDirectory)
if ($ReleasePublicKeyPath) {
    $arguments += "-p:VeyonCampusReleasePublicKeyPath=$([IO.Path]::GetFullPath($ReleasePublicKeyPath))"
}
if ($TestReleasePublicKeyPath) {
    $arguments += "-p:VeyonCampusTestReleasePublicKeyPath=$([IO.Path]::GetFullPath($TestReleasePublicKeyPath))"
}
if (-not $SkipRestore) {
    $restoreArguments = @('restore', $projectPath, '-r', 'win-x64', '-p:NuGetAudit=false',
        '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json')
    if ($ReleasePublicKeyPath) {
        $restoreArguments += "-p:VeyonCampusReleasePublicKeyPath=$([IO.Path]::GetFullPath($ReleasePublicKeyPath))"
    }
    if ($TestReleasePublicKeyPath) {
        $restoreArguments += "-p:VeyonCampusTestReleasePublicKeyPath=$([IO.Path]::GetFullPath($TestReleasePublicKeyPath))"
    }
    & $dotnetPath @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw "Maintenance Service restore failed ($LASTEXITCODE)." }
} elseif (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $projectPath) 'obj/project.assets.json') -PathType Leaf)) {
    throw 'Maintenance Service 缺少 NuGet assets；请先不使用 -SkipRestore 完成一次还原。'
}
& $dotnetPath @arguments
if ($LASTEXITCODE -ne 0) { throw "Maintenance Service publish failed ($LASTEXITCODE)." }

$helperAssetsPath = Join-Path (Split-Path -Parent $helperProjectPath) 'obj/project.assets.json'
if (-not $SkipRestore) {
    $helperRestoreArguments = @('restore', $helperProjectPath, '-r', 'win-x64', '-p:NuGetAudit=false',
        '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json')
    if ($ReleasePublicKeyPath) {
        $helperRestoreArguments += "-p:VeyonCampusReleasePublicKeyPath=$([IO.Path]::GetFullPath($ReleasePublicKeyPath))"
    }
    if ($TestReleasePublicKeyPath) {
        $helperRestoreArguments += "-p:VeyonCampusTestReleasePublicKeyPath=$([IO.Path]::GetFullPath($TestReleasePublicKeyPath))"
    }
    & $dotnetPath @helperRestoreArguments
    if ($LASTEXITCODE -ne 0) { throw "Maintenance UpdateHelper restore failed ($LASTEXITCODE)." }
} elseif (-not (Test-Path -LiteralPath $helperAssetsPath -PathType Leaf)) {
    throw 'Maintenance UpdateHelper 缺少 NuGet assets；请先不使用 -SkipRestore 完成一次还原。'
}
$helperPublishDirectory = Join-Path $publishDirectory '.maintenance-helper-publish'
$helperArguments = @('publish', $helperProjectPath, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
    '--no-restore', '-p:PublishSingleFile=true', '-p:AppHostDotNetSearch=Global', '-p:DebugType=None',
    "-p:Version=$Version", "-p:InformationalVersion=$Version", '-p:IncludeSourceRevisionInInformationalVersion=false',
    '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json', '-p:VeyonCampusIncludeInstaller=false',
    '-o', $helperPublishDirectory)
if ($ReleasePublicKeyPath) {
    $helperArguments += "-p:VeyonCampusReleasePublicKeyPath=$([IO.Path]::GetFullPath($ReleasePublicKeyPath))"
}
if ($TestReleasePublicKeyPath) {
    $helperArguments += "-p:VeyonCampusTestReleasePublicKeyPath=$([IO.Path]::GetFullPath($TestReleasePublicKeyPath))"
}
& $dotnetPath @helperArguments
if ($LASTEXITCODE -ne 0) { throw "Maintenance UpdateHelper publish failed ($LASTEXITCODE)." }
$helperExePath = Join-Path $helperPublishDirectory 'VeyonCampus.UpdateHelper.exe'
if (-not (Test-Path -LiteralPath $helperExePath -PathType Leaf)) {
    throw "Maintenance 发布结果缺少 SYSTEM UpdateHelper：$helperExePath"
}
Copy-Item -LiteralPath $helperExePath -Destination (Join-Path $publishDirectory 'VeyonCampus.UpdateHelper.exe')
Remove-Item -LiteralPath $helperPublishDirectory -Recurse -Force

$exePath = Join-Path $publishDirectory 'VeyonCampus.MaintenanceService.exe'
$assemblyPath = Join-Path $publishDirectory 'VeyonCampus.MaintenanceService.dll'
$runtimeConfigPath = Join-Path $publishDirectory 'VeyonCampus.MaintenanceService.runtimeconfig.json'
foreach ($requiredPath in @($exePath, $assemblyPath, $runtimeConfigPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Maintenance Service 发布结果缺少文件：$requiredPath"
    }
}
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion
if ([string]::IsNullOrWhiteSpace($fileVersion) -or $fileVersion.Split('+')[0] -cne $Version.Split('+')[0]) {
    throw "Maintenance Service 文件版本与独立发行版本不一致：期望 $Version，实际 $fileVersion"
}
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$runtimeOptions = $runtimeConfig.runtimeOptions
$runtimeFrameworksProperty = $runtimeOptions.PSObject.Properties['frameworks']
if ($null -eq $runtimeFrameworksProperty) {
    $runtimeFrameworksProperty = $runtimeOptions.PSObject.Properties['framework']
}
$runtimeFrameworks = @()
if ($null -ne $runtimeFrameworksProperty) {
    $runtimeFrameworks = @($runtimeFrameworksProperty.Value)
}
if ($runtimeFrameworks.Count -ne 1 -or $runtimeFrameworks[0].name -cne 'Microsoft.NETCore.App') {
    throw 'Maintenance Service 必须使用统一安装的 Microsoft.NETCore.App runtime。'
}

$noticeScript = Join-Path $PSScriptRoot 'Copy-WindowsThirdPartyNotices.ps1'
& $noticeScript -PublishDirectory $publishDirectory -Role MaintenanceService -RepositoryRoot $repoRoot

$programFilesX86 = ${env:ProgramFiles(x86)}
$isccCandidates = @(
    $env:VEYONCAMPUS_ISCC_PATH,
    $(if ($programFilesX86) { Join-Path $programFilesX86 'Inno Setup 6/ISCC.exe' }),
    (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'),
    $(if (Get-Command ISCC.exe -ErrorAction SilentlyContinue) { (Get-Command ISCC.exe).Source })
) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique
$isccPath = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $isccPath) { throw '需要 Inno Setup 6；请设置 VEYONCAMPUS_ISCC_PATH 指向 ISCC.exe。' }

$installerParent = Split-Path -Parent $installerFile
New-Item -Path $installerParent -ItemType Directory -Force | Out-Null
$generatedPath = Join-Path (Split-Path -Parent $installerTemplatePath) ".VeyonCampus-Maintenance-$([guid]::NewGuid().ToString('N')).iss"
$generated = @(
    ('#define public AppVersion "' + $Version + '"')
    ('#define public PublishDirectory "' + $publishDirectory + '"')
    ('#include "' + [IO.Path]::GetFileName($installerTemplatePath) + '"')
) -join [Environment]::NewLine
try {
    [IO.File]::WriteAllText($generatedPath, $generated + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    & $isccPath ('-o' + $installerParent) ('-f' + [IO.Path]::GetFileNameWithoutExtension($installerFile)) $generatedPath
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败 ($LASTEXITCODE)." }
} finally {
    if (Test-Path -LiteralPath $generatedPath) { Remove-Item -LiteralPath $generatedPath -Force }
}
if (-not (Test-Path -LiteralPath $installerFile -PathType Leaf)) {
    throw "Inno Setup 未生成预期 Maintenance Service 安装器：$installerFile"
}
Write-Host "Maintenance Service 独立安装器源码打包路径已完成：$installerFile" -ForegroundColor Green
