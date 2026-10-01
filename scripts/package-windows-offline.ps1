[CmdletBinding()]
param(
    [ValidateSet('StudentSetup', 'TeacherConsole')]
    [string]$Role = 'StudentSetup',
    [string]$OutputDirectory,
    [string]$InstallerPath,
    [string]$ReleasePublicKeyPath,
    [string]$ZipPath,
    [switch]$SkipRestore,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$appProjectPath = Join-Path $repoRoot 'src/VeyonCampus.App/VeyonCampus.App.csproj'
$updateHelperProjectPath = Join-Path $repoRoot 'tools/VeyonCampus.UpdateHelper/VeyonCampus.UpdateHelper.csproj'
$coreProjectPath = Join-Path $repoRoot 'src/VeyonCampus.Core/VeyonCampus.Core.csproj'
$trustSourcePath = Join-Path $repoRoot 'src/VeyonCampus.Core/VeyonInstallerTrust.cs'
$resourceVerifierProjectPath = Join-Path $repoRoot 'tools/VerifyEmbeddedResource/VerifyEmbeddedResource.csproj'
$installerScriptPath = Join-Path $repoRoot $(if ($Role -eq 'StudentSetup') {
    'installer/VeyonCampus-Student.iss'
} else {
    'installer/VeyonCampus-Teacher.iss'
})

function Get-RequiredMatchValue {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Description
    )

    $match = [regex]::Match($Text, $Pattern)
    if (-not $match.Success) {
        throw "无法从项目文件读取 $($Description)。"
    }
    return $match.Groups[1].Value
}

function Resolve-ArtifactPath {
    param([string]$Path, [string]$Description)

    if ([IO.Path]::IsPathRooted($Path)) {
        $fullPath = [IO.Path]::GetFullPath($Path)
    }
    else {
        $fullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
    }

    if (-not $fullPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$($Description) 必须放在仓库的 artifacts 目录中：$fullPath"
    }
    return $fullPath
}

function Assert-NoReparsePointsUnderArtifacts {
    param([string]$Path)

    $relativePath = $Path.Substring($artifactsPrefix.Length)
    $parts = $relativePath -split '[\\/]+'
    $currentPath = $artifactsRoot
    for ($index = 0; $index -lt $parts.Length; $index++) {
        $currentPath = Join-Path $currentPath $parts[$index]
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "打包路径包含符号链接或重解析点，已停止以免写入其他位置：$currentPath"
            }
            if ($index -lt ($parts.Length - 1) -and -not $item.PSIsContainer) {
                throw "打包路径的父级不是文件夹：$currentPath"
            }
        }
    }
}

function Invoke-Dotnet {
    param([string[]]$Arguments)

    Write-Host ("`n> dotnet " + ($Arguments -join ' ')) -ForegroundColor DarkCyan
    & $script:dotnetPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "dotnet 命令失败，退出代码：$exitCode"
    }
}

function Invoke-InnoSetup {
    param([string[]]$Arguments)

    Write-Host ("`n> ISCC " + ($Arguments -join ' ')) -ForegroundColor DarkCyan
    & $script:isccPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Inno Setup 编译失败，退出代码：$exitCode"
    }
}

$appProjectXml = [xml](Get-Content -LiteralPath $appProjectPath -Raw -Encoding UTF8)
$appVersion = [string](@($appProjectXml.Project.PropertyGroup | ForEach-Object { $_.Version } |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1)[0])
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    throw '无法从 VeyonCampus.App.csproj 读取应用版本。'
}

if ([string]::IsNullOrWhiteSpace($ReleasePublicKeyPath)) {
    $ReleasePublicKeyPath = [Environment]::GetEnvironmentVariable('VEYONCAMPUS_RELEASE_PUBLIC_KEY_PATH')
}
$releasePublicKeyFullPath = $null
if (-not [string]::IsNullOrWhiteSpace($ReleasePublicKeyPath)) {
    if ([IO.Path]::IsPathRooted($ReleasePublicKeyPath)) {
        $releasePublicKeyFullPath = [IO.Path]::GetFullPath($ReleasePublicKeyPath)
    }
    else {
        $releasePublicKeyFullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $ReleasePublicKeyPath))
    }
    if (-not (Test-Path -LiteralPath $releasePublicKeyFullPath -PathType Leaf)) {
        throw "找不到 Developer Release PEM 公钥：$releasePublicKeyFullPath"
    }
    $releasePublicKeyText = Get-Content -LiteralPath $releasePublicKeyFullPath -Raw -Encoding UTF8
    if ($releasePublicKeyText.IndexOf('PRIVATE KEY', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw '安装器只能嵌入 Developer Release 公钥，绝不能嵌入发布私钥。'
    }
    $nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $nodeCommand) {
        throw 'Node.js is required to validate the Developer Release RSA public key.'
    }
    $env:VEYONCAMPUS_RELEASE_KEY_TO_VALIDATE = $releasePublicKeyFullPath
    $nodeSource = @'
const crypto = require('node:crypto');
const fs = require('node:fs');
const key = crypto.createPublicKey(fs.readFileSync(process.env.VEYONCAMPUS_RELEASE_KEY_TO_VALIDATE));
const bits = key.asymmetricKeyDetails?.modulusLength ?? 0;
if (key.asymmetricKeyType !== 'rsa' || bits < 2048 || bits > 4096) {
  throw new Error('Developer Release key must be RSA 2048–4096 bits.');
}
console.log(`Validated Developer Release RSA-${bits} public key.`);
'@
    try {
        & $nodeCommand.Source -e $nodeSource
        if ($LASTEXITCODE -ne 0) {
            throw 'Developer Release RSA 公钥校验失败。'
        }
    }
    finally {
        Remove-Item Env:VEYONCAMPUS_RELEASE_KEY_TO_VALIDATE -ErrorAction SilentlyContinue
    }
}

$coreProjectText = Get-Content -LiteralPath $coreProjectPath -Raw -Encoding UTF8
$trustSourceText = Get-Content -LiteralPath $trustSourcePath -Raw -Encoding UTF8
$installerRelativePath = Get-RequiredMatchValue $coreProjectText '<EmbeddedResource\s+Include="([^"]+)"' 'Veyon 安装器嵌入路径'
$resourceName = Get-RequiredMatchValue $coreProjectText 'LogicalName="([^"]+)"' 'Veyon 安装器资源名'
$expectedFileName = Get-RequiredMatchValue $trustSourceText 'const string FileName\s*=\s*"([^"]+)"' 'Veyon 安装器文件名'
$expectedSizeText = Get-RequiredMatchValue $trustSourceText 'const long FileSize\s*=\s*([0-9_]+)' 'Veyon 安装器大小'
$expectedSize = [long]($expectedSizeText -replace '_', '')
$expectedSha256 = Get-RequiredMatchValue $trustSourceText 'const string Sha256\s*=\s*"([0-9A-Fa-f]{64})"' 'Veyon 安装器 SHA-256'
$embeddedVeyonInstallerPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $coreProjectPath) $installerRelativePath))
if ([IO.Path]::GetFileName($embeddedVeyonInstallerPath) -ne $expectedFileName) {
    throw "嵌入的安装器文件名与应用固定基线不一致：$embeddedVeyonInstallerPath"
}
if (-not (Test-Path -LiteralPath $embeddedVeyonInstallerPath -PathType Leaf)) {
    throw "找不到 Veyon 安装器：$embeddedVeyonInstallerPath"
}
$installerInfo = Get-Item -LiteralPath $embeddedVeyonInstallerPath
$installerHash = (Get-FileHash -LiteralPath $embeddedVeyonInstallerPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($installerInfo.Length -ne $expectedSize -or $installerHash -ne $expectedSha256.ToUpperInvariant()) {
    throw "Veyon 安装器与应用内固定基线不符。期望大小/SHA-256：$expectedSize / $expectedSha256；实际：$($installerInfo.Length) / $installerHash"
}
Write-Host "已核对内嵌 Veyon $expectedFileName（$expectedSize 字节，SHA-256 $installerHash）。" -ForegroundColor Green

$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
$dotnetCandidates = @(
    (Join-Path $programFiles 'dotnet/dotnet.exe'),
    $(if ($programFilesX86) { Join-Path $programFilesX86 'dotnet/dotnet.exe' }),
    $(if (Get-Command dotnet -ErrorAction SilentlyContinue) { (Get-Command dotnet).Source })
) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique

$script:dotnetPath = $null
$sdkVersion = $null
foreach ($candidate in $dotnetCandidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    $versionOutput = & $candidate --version 2>&1
    $versionExitCode = $LASTEXITCODE
    $candidateVersion = [string]($versionOutput | Select-Object -First 1)
    if ($versionExitCode -eq 0 -and $candidateVersion -match '^10\.') {
        $script:dotnetPath = $candidate
        $sdkVersion = $candidateVersion
        break
    }
}
if (-not $script:dotnetPath) {
    throw '需要 .NET 10 SDK 才能打包。请先安装 .NET 10 SDK，然后重新运行此脚本。'
}
Write-Host ".NET SDK：$sdkVersion ($script:dotnetPath)" -ForegroundColor Green

if (-not (Test-Path -LiteralPath $artifactsRoot -PathType Container)) {
    New-Item -Path $artifactsRoot -ItemType Directory -Force | Out-Null
}
$artifactsItem = Get-Item -LiteralPath $artifactsRoot -Force
if (($artifactsItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "artifacts 目录不能是符号链接或重解析点：$artifactsRoot"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $roleSlug = if ($Role -eq 'StudentSetup') { 'student-setup' } else { 'teacher-console' }
    $OutputDirectory = Join-Path $artifactsRoot "windows-x64-v$appVersion-$roleSlug"
}
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $roleName = if ($Role -eq 'StudentSetup') { 'Student' } else { 'Teacher' }
    $InstallerPath = Join-Path $artifactsRoot "VeyonCampus-$roleName-Setup-$appVersion-win-x64.exe"
}
$publishDirectory = Resolve-ArtifactPath $OutputDirectory '发布文件夹'
$zipFile = $null
if (-not [string]::IsNullOrWhiteSpace($ZipPath)) {
    $zipFile = Resolve-ArtifactPath $ZipPath 'ZIP 文件'
}
$installerFile = Resolve-ArtifactPath $InstallerPath 'Inno Setup 安装器'
if ($zipFile -and $zipFile.StartsWith($publishDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ZIP 文件不能放在发布文件夹内部。'
}
if ($installerFile.StartsWith($publishDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
    ($zipFile -and [string]::Equals($installerFile, $zipFile, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Inno Setup 安装器必须放在发布文件夹之外，且不能与 ZIP 路径相同。'
}
if ([IO.Path]::GetExtension($installerFile) -ine '.exe') {
    throw "Inno Setup 安装器路径必须以 .exe 结尾：$installerFile"
}
Assert-NoReparsePointsUnderArtifacts $publishDirectory
if ($zipFile) {
    Assert-NoReparsePointsUnderArtifacts $zipFile
}
Assert-NoReparsePointsUnderArtifacts $installerFile

if ($Clean) {
    $cleanupPaths = @($publishDirectory, $installerFile)
    if ($zipFile) {
        $cleanupPaths += $zipFile
    }
    foreach ($path in $cleanupPaths) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}
elseif ((Test-Path -LiteralPath $publishDirectory) -or ($zipFile -and (Test-Path -LiteralPath $zipFile)) -or
    (Test-Path -LiteralPath $installerFile)) {
    throw "输出位置已存在。若要替换本脚本生成的同版本产物，请加 -Clean。`n文件夹：$publishDirectory`n安装器：$installerFile"
}

if (-not $SkipRestore) {
    $restoreArguments = @('restore', $appProjectPath, '-r', 'win-x64', '--locked-mode',
        '-p:NuGetAudit=false', "-p:VeyonCampusRole=$Role", '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json')
    if ($releasePublicKeyFullPath) {
        $restoreArguments += "-p:VeyonCampusReleasePublicKeyPath=$releasePublicKeyFullPath"
    }
    Invoke-Dotnet $restoreArguments
}

$publishArguments = @('publish', $appProjectPath, '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '--no-restore', "-p:VeyonCampusRole=$Role",
    '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json', '-o', $publishDirectory)
if ($releasePublicKeyFullPath) {
    $publishArguments += "-p:VeyonCampusReleasePublicKeyPath=$releasePublicKeyFullPath"
}
Invoke-Dotnet $publishArguments

$appExeName = if ($Role -eq 'StudentSetup') { 'VeyonCampus.StudentSetup.exe' } else { 'VeyonCampus.Teacher.exe' }
$appExePath = Join-Path $publishDirectory $appExeName
if (-not (Test-Path -LiteralPath $appExePath -PathType Leaf)) {
    throw "发布结果中缺少角色入口程序 $appExeName：$publishDirectory"
}

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
if ($Role -eq 'StudentSetup') {
    $teacherArtifacts = @($publishedFiles | Where-Object { $_.Name -match '^VeyonCampus\.Teacher(?:\.|$)' })
    if ($teacherArtifacts.Count -gt 0) {
        throw "学生部署包混入教师端产物：$($teacherArtifacts[0].FullName)"
    }
}

else {
    $studentArtifacts = @($publishedFiles | Where-Object { $_.Name -match '^VeyonCampus\.StudentSetup(?:\.|$)' })
    $agentDirectory = Join-Path $publishDirectory 'WebsitePolicyAgent'
    if ($studentArtifacts.Count -gt 0 -or (Test-Path -LiteralPath $agentDirectory)) {
        throw '教师控制台包混入学生部署程序或网站策略 Agent。'
    }
}

if (-not $releasePublicKeyFullPath) {
    Write-Host '未配置 Developer Release PEM 公钥；此安装器会安全停用应用更新。' -ForegroundColor Yellow
}

$product = if ($Role -eq 'StudentSetup') { 'VeyonCampus.StudentSetup' } else { 'VeyonCampus.TeacherConsole' }
$roleInfoPath = Join-Path $publishDirectory 'veyon-campus-role.json'
[IO.File]::WriteAllText($roleInfoPath,
    (ConvertTo-Json -InputObject ([ordered]@{ schemaVersion = 1; role = $Role; product = $product; version = $appVersion }) -Depth 3) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

if ($Role -eq 'StudentSetup') {
    $agentExePath = Join-Path $publishDirectory 'WebsitePolicyAgent/VeyonCampus.Agent.exe'
    if (-not (Test-Path -LiteralPath $agentExePath -PathType Leaf)) {
        throw "发布结果中缺少独立后台代理 VeyonCampus.Agent.exe：$agentExePath"
    }

    $bundleFiles = @(
        Get-ChildItem -LiteralPath $publishDirectory -File -Recurse |
            Where-Object { $_.Name -notin @('veyon-campus-student-setup.json', '.veyon-campus-student-setup') } |
            ForEach-Object {
                [ordered]@{
                    path = $_.FullName.Substring($publishDirectory.TrimEnd('\', '/').Length + 1).Replace('\', '/')
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
                }
            }
    )
    $bundleManifest = [ordered]@{
        schemaVersion = 1
        product = 'VeyonCampus.StudentSetup'
        files = $bundleFiles
    }
    $bundleManifestPath = Join-Path $publishDirectory 'veyon-campus-student-setup.json'
    [IO.File]::WriteAllText($bundleManifestPath,
        (ConvertTo-Json -InputObject $bundleManifest -Depth 5) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $publishDirectory '.veyon-campus-student-setup'),
        'VeyonCampusStudentSetupBundle-v1', [Text.UTF8Encoding]::new($false))
}

$coreDllPath = Join-Path $publishDirectory 'VeyonCampus.Core.dll'
if (-not (Test-Path -LiteralPath $coreDllPath -PathType Leaf)) {
    throw "发布结果中缺少嵌入 Veyon 安装器的 VeyonCampus.Core.dll：$publishDirectory"
}

Invoke-Dotnet @('run', '--project', $resourceVerifierProjectPath, '-c', 'Release',
    '-p:NuGetAudit=false', '--', $coreDllPath, [string]$expectedSize, $expectedSha256.ToUpperInvariant(), $resourceName)

$programFilesX86Path = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
$userProgramsPath = Join-Path $env:LOCALAPPDATA 'Programs'
$isccCandidates = @(
    $(if ($programFilesX86Path) { Join-Path $programFilesX86Path 'Inno Setup 6/ISCC.exe' }),
    (Join-Path $userProgramsPath 'Inno Setup 6/ISCC.exe'),
    $(if (Get-Command ISCC.exe -ErrorAction SilentlyContinue) { (Get-Command ISCC.exe).Source })
) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique
$script:isccPath = $null
foreach ($candidate in $isccCandidates) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $script:isccPath = $candidate
        break
    }
}
if (-not $script:isccPath) {
    throw '需要 Inno Setup 6。请安装 Inno Setup 6 并确保 ISCC.exe 可用，然后重新运行此脚本。'
}

$updateHelperOutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ("VeyonCampusUpdateHelper-$Role-" + [guid]::NewGuid().ToString('N'))
$helperRestoreArguments = @('restore', $updateHelperProjectPath, '-r', 'win-x64',
    '-p:NuGetAudit=false', '-p:VeyonCampusIncludeInstaller=false',
    '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json')
if ($releasePublicKeyFullPath) {
    $helperRestoreArguments += "-p:VeyonCampusReleasePublicKeyPath=$releasePublicKeyFullPath"
}
Invoke-Dotnet $helperRestoreArguments
$helperPublishArguments = @('publish', $updateHelperProjectPath, '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '--no-restore', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None',
    '-p:VeyonCampusIncludeInstaller=false', '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json',
    '-o', $updateHelperOutputDirectory)
if ($releasePublicKeyFullPath) {
    $helperPublishArguments += "-p:VeyonCampusReleasePublicKeyPath=$releasePublicKeyFullPath"
}
Invoke-Dotnet $helperPublishArguments
$updateHelperExePath = Join-Path $updateHelperOutputDirectory 'VeyonCampus.UpdateHelper.exe'
if (-not (Test-Path -LiteralPath $updateHelperExePath -PathType Leaf)) {
    throw "发布结果中缺少单文件更新助手：$updateHelperExePath"
}

$installerParent = Split-Path -Parent $installerFile
if (-not (Test-Path -LiteralPath $installerParent -PathType Container)) {
    New-Item -Path $installerParent -ItemType Directory -Force | Out-Null
}
$outputName = [IO.Path]::GetFileNameWithoutExtension($installerFile)
try {
    Invoke-InnoSetup @(
        ('-dAppVersion="' + $appVersion + '"'),
        ('-dPublishDirectory="' + $publishDirectory + '"'),
        ('-o' + $installerParent),
        ('-f' + $outputName),
        ('-dUpdateHelperDirectory="' + $updateHelperOutputDirectory + '"'),
        $installerScriptPath
    )
}
finally {
    if (Test-Path -LiteralPath $updateHelperOutputDirectory) {
        Remove-Item -LiteralPath $updateHelperOutputDirectory -Recurse -Force
    }
}
if (-not (Test-Path -LiteralPath $installerFile -PathType Leaf)) {
    throw "Inno Setup 未生成预期安装器：$installerFile"
}

if ($zipFile) {
    $zipParent = Split-Path -Parent $zipFile
    if (-not (Test-Path -LiteralPath $zipParent -PathType Container)) {
        New-Item -Path $zipParent -ItemType Directory -Force | Out-Null
    }
    Compress-Archive -LiteralPath $publishDirectory -DestinationPath $zipFile -CompressionLevel Optimal
}

Write-Host ''
Write-Host 'Windows x64 Inno Setup 安装器已完成：' -ForegroundColor Green
Write-Host "文件夹：$publishDirectory"
Write-Host "安装器：$installerFile"
if ($zipFile) {
    Write-Host "可选诊断 ZIP：$zipFile"
}
Write-Host "启动程序：$appExePath"
if ($Role -eq 'StudentSetup') {
    Write-Host "独立后台代理：$agentExePath"
    Write-Host '将学生 Setup EXE 与教师为该校区生成的配置包配套分发。部署后可只读验证并清理便携学生工具。' -ForegroundColor Yellow
}
else {
    Write-Host '教师控制台不含学生部署界面；学生端请使用 StudentSetup 角色包。' -ForegroundColor Yellow
}
