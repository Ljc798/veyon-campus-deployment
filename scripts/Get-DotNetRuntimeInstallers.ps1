[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [switch]$IncludeAspNetCoreRuntime
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repositoryRoot 'packaging/third-party/dotnet-runtime-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "找不到 .NET 运行时固定来源清单：$manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$manifest.version)) {
    throw '固定的 .NET 运行时来源清单格式无效。'
}
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = [string]$manifest.version }
if ($Version -cne [string]$manifest.version) {
    throw "只允许获取清单固定的 .NET 运行时版本 $($manifest.version)，收到：$Version"
}

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactsRoot "tooling/dotnet-runtime/$Version"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "运行时安装器缓存必须位于仓库 artifacts 目录：$OutputDirectory"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Assert-MicrosoftRuntimeInstaller {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedHash,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($actualHash -cne $ExpectedHash.ToLowerInvariant()) {
        throw "运行时安装器 SHA-512 与固定清单不符：$Name"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        throw "运行时安装器没有有效的 Microsoft Authenticode 签名：$Name"
    }
}

if ($IncludeAspNetCoreRuntime) {
    $assets = @($manifest.runtime, $manifest.aspnetCoreRuntime)
}
else {
    $assets = @($manifest.runtime)
}

foreach ($asset in $assets) {
    $name = [string]$asset.fileName
    $url = [string]$asset.url
    $expectedHash = [string]$asset.sha512
    $escapedVersion = [regex]::Escape($Version)
    if ($name -cnotmatch "^(?:dotnet-runtime|aspnetcore-runtime)-$escapedVersion-win-x64\.exe$") {
        throw "固定清单中出现了意外运行时文件名：$name"
    }
    if ($url -notmatch "^https://builds\.dotnet\.microsoft\.com/dotnet/(?:aspnetcore/)?Runtime/$escapedVersion/" -or
        $expectedHash -notmatch '^[0-9a-fA-F]{128}$') {
        throw "运行时官方来源或 SHA-512 格式无效：$name"
    }

    $destination = Join-Path $OutputDirectory $name
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        Assert-MicrosoftRuntimeInstaller -Path $destination -ExpectedHash $expectedHash -Name $name
        continue
    }

    $partial = Join-Path $OutputDirectory ('.' + $name + '.download')
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    try {
        Invoke-WebRequest -Uri $url -OutFile $partial
        Assert-MicrosoftRuntimeInstaller -Path $partial -ExpectedHash $expectedHash -Name $name
        Move-Item -LiteralPath $partial -Destination $destination
    }
    finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}

Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object {
    $_.Name -in @($assets | ForEach-Object { [string]$_.fileName })
} | Select-Object Name, Length
