[CmdletBinding()]
param(
    [string]$OutputPath,
    [switch]$SkipBuild
)

# Produces the zip that gets uploaded to static hosting. The site reads the newest release
# straight from GitCode/GitHub, so the build is pure files: no backend, no environment variables.
# Build it with build:static-only, which also removes the admin entry that would otherwise lead to
# a console pointing at a retired API.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$siteRoot = Join-Path $repoRoot 'website'
$distRoot = Join-Path $siteRoot 'dist'

if (-not $SkipBuild) {
    Push-Location $siteRoot
    try {
        & npm run build:static-only
        if ($LASTEXITCODE -ne 0) { throw "npm run build:static-only 失败（退出码 $LASTEXITCODE）。" }
    }
    finally { Pop-Location }
}

if (-not (Test-Path (Join-Path $distRoot 'index.html'))) {
    throw "没有找到 $distRoot\index.html，请先构建。"
}

# The admin module is tree-shaken out in static-only mode; catching it here means nobody ships a
# console whose every request returns 402 from a retired environment.
$adminLeak = Get-ChildItem -Path (Join-Path $distRoot 'assets') -Filter '*.js' -File |
    Select-String -Pattern 'cloud-admin|v1/admin/database' -SimpleMatch -List
if ($adminLeak) {
    throw "静态产物里仍然包含后台管理代码（$($adminLeak[0].Filename)），请确认使用的是 build:static-only。"
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot ("artifacts\website-static-{0}.zip" -f (Get-Date -Format 'yyyyMMdd'))
}
$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
if (Test-Path $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force }

Compress-Archive -Path (Join-Path $distRoot '*') -DestinationPath $OutputPath -CompressionLevel Optimal
$archive = Get-Item $OutputPath
$fileCount = (Get-ChildItem -Path $distRoot -Recurse -File).Count
"{0} — {1:N0} 个文件，{2:N0} 字节" -f $archive.FullName, $fileCount, $archive.Length
