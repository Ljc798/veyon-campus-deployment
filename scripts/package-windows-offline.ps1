[CmdletBinding()]
param(
    [string]$OutputDirectory,
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
$coreProjectPath = Join-Path $repoRoot 'src/VeyonCampus.Core/VeyonCampus.Core.csproj'
$trustSourcePath = Join-Path $repoRoot 'src/VeyonCampus.Core/VeyonInstallerTrust.cs'
$resourceVerifierProjectPath = Join-Path $repoRoot 'tools/VerifyEmbeddedResource/VerifyEmbeddedResource.csproj'

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

$appProjectXml = [xml](Get-Content -LiteralPath $appProjectPath -Raw -Encoding UTF8)
$appVersion = [string](@($appProjectXml.Project.PropertyGroup | ForEach-Object { $_.Version } |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1)[0])
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    throw '无法从 VeyonCampus.App.csproj 读取应用版本。'
}

$coreProjectText = Get-Content -LiteralPath $coreProjectPath -Raw -Encoding UTF8
$trustSourceText = Get-Content -LiteralPath $trustSourcePath -Raw -Encoding UTF8
$installerRelativePath = Get-RequiredMatchValue $coreProjectText '<EmbeddedResource\s+Include="([^"]+)"' 'Veyon 安装器嵌入路径'
$resourceName = Get-RequiredMatchValue $coreProjectText 'LogicalName="([^"]+)"' 'Veyon 安装器资源名'
$expectedFileName = Get-RequiredMatchValue $trustSourceText 'const string FileName\s*=\s*"([^"]+)"' 'Veyon 安装器文件名'
$expectedSizeText = Get-RequiredMatchValue $trustSourceText 'const long FileSize\s*=\s*([0-9_]+)' 'Veyon 安装器大小'
$expectedSize = [long]($expectedSizeText -replace '_', '')
$expectedSha256 = Get-RequiredMatchValue $trustSourceText 'const string Sha256\s*=\s*"([0-9A-Fa-f]{64})"' 'Veyon 安装器 SHA-256'
$installerPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $coreProjectPath) $installerRelativePath))
if ([IO.Path]::GetFileName($installerPath) -ne $expectedFileName) {
    throw "嵌入的安装器文件名与应用固定基线不一致：$installerPath"
}
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "找不到 Veyon 安装器：$installerPath"
}
$installerInfo = Get-Item -LiteralPath $installerPath
$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
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
    $OutputDirectory = Join-Path $artifactsRoot "windows-x64-v$appVersion"
}
if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = Join-Path $artifactsRoot "VeyonCampus-$appVersion-win-x64-offline.zip"
}
$publishDirectory = Resolve-ArtifactPath $OutputDirectory '发布文件夹'
$zipFile = Resolve-ArtifactPath $ZipPath 'ZIP 文件'
if ($zipFile.StartsWith($publishDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ZIP 文件不能放在发布文件夹内部。'
}
Assert-NoReparsePointsUnderArtifacts $publishDirectory
Assert-NoReparsePointsUnderArtifacts $zipFile

if ($Clean) {
    foreach ($path in @($publishDirectory, $zipFile)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}
elseif ((Test-Path -LiteralPath $publishDirectory) -or (Test-Path -LiteralPath $zipFile)) {
    throw "输出位置已存在。若要替换本脚本生成的同版本产物，请加 -Clean。`n文件夹：$publishDirectory`nZIP：$zipFile"
}

if (-not $SkipRestore) {
    Invoke-Dotnet @('restore', $appProjectPath, '-r', 'win-x64', '--locked-mode', '-p:NuGetAudit=false')
}

Invoke-Dotnet @('publish', $appProjectPath, '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '--no-restore', '-o', $publishDirectory)

$appExePath = Join-Path $publishDirectory 'VeyonCampus.exe'
$coreDllPath = Join-Path $publishDirectory 'VeyonCampus.Core.dll'
if (-not (Test-Path -LiteralPath $appExePath -PathType Leaf)) {
    throw "发布结果中缺少 VeyonCampus.exe：$publishDirectory"
}
if (-not (Test-Path -LiteralPath $coreDllPath -PathType Leaf)) {
    throw "发布结果中缺少嵌入 Veyon 安装器的 VeyonCampus.Core.dll：$publishDirectory"
}

Invoke-Dotnet @('run', '--project', $resourceVerifierProjectPath, '-c', 'Release',
    '-p:NuGetAudit=false', '--', $coreDllPath, [string]$expectedSize, $expectedSha256.ToUpperInvariant(), $resourceName)

$zipParent = Split-Path -Parent $zipFile
if (-not (Test-Path -LiteralPath $zipParent -PathType Container)) {
    New-Item -Path $zipParent -ItemType Directory -Force | Out-Null
}
Compress-Archive -LiteralPath $publishDirectory -DestinationPath $zipFile -CompressionLevel Optimal

Write-Host ''
Write-Host 'Windows x64 离线发布包已完成：' -ForegroundColor Green
Write-Host "文件夹：$publishDirectory"
Write-Host "压缩包：$zipFile"
Write-Host "启动程序：$appExePath"
Write-Host '学生电脑需解压整个 ZIP，并与教师端为该校区生成的配置包一起分发。' -ForegroundColor Yellow
