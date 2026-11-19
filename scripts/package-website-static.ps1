[CmdletBinding()]
param(
    [string]$OutputPath,
    [switch]$SkipBuild,

    # Keeps the /admin console and the CloudBase configuration in the build. Use this while the
    # CloudBase subscription is active: the pages are still static, the console talks to the API
    # directly, and nothing has to be bound to a custom domain that is not access-filed with Tencent.
    [switch]$WithAdmin
)

# Produces the zip that gets uploaded to static hosting. Without -WithAdmin the site reads the newest
# release straight from GitCode/GitHub and ships pure files: no backend, no environment variables.
# Build it with build:static-only, which also removes the admin entry that would otherwise lead to
# a console pointing at a retired API.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$siteRoot = Join-Path $repoRoot 'website'
$distRoot = Join-Path $siteRoot 'dist'

if (-not $SkipBuild) {
    Push-Location $siteRoot
    try {
        $script = if ($WithAdmin) { 'build' } else { 'build:static-only' }
        & npm run $script
        if ($LASTEXITCODE -ne 0) { throw "npm run $script 失败（退出码 $LASTEXITCODE）。" }
    }
    finally { Pop-Location }
}

if (-not (Test-Path (Join-Path $distRoot 'index.html'))) {
    throw "没有找到 $distRoot\index.html，请先构建。"
}

# Every route except '/' and '/docs' is a clean URL with no file behind it, so a host that does not
# fall back to index.html answers /product and /download with 404. EdgeOne Pages reads this file;
# object storage gets the same behaviour from its "error document" setting instead.
$edgeOneConfig = Join-Path $distRoot 'edgeone.json'
if (-not (Test-Path $edgeOneConfig)) {
    $edgeOneSource = Join-Path $siteRoot 'public\edgeone.json'
    if (Test-Path $edgeOneSource) { Copy-Item -LiteralPath $edgeOneSource -Destination $edgeOneConfig }
    else { throw "缺少 public\edgeone.json：EdgeOne 部署后刷新 /product 这类路由会返回 404。" }
}

# The admin module is tree-shaken out in static-only mode; catching it here means nobody ships a
# console whose every request returns 402 from a retired environment. -WithAdmin asks for that
# module on purpose, so the check only guards the backend-free build.
if (-not $WithAdmin) {
    $adminLeak = Get-ChildItem -Path (Join-Path $distRoot 'assets') -Filter '*.js' -File |
        Select-String -Pattern 'cloud-admin|v1/admin/database' -SimpleMatch -List
    if ($adminLeak) {
        throw "静态产物里仍然包含后台管理代码（$($adminLeak[0].Filename)），请确认使用的是 build:static-only。"
    }
}

if (-not $OutputPath) {
    $prefix = if ($WithAdmin) { 'website-with-admin' } else { 'website-static' }
    $OutputPath = Join-Path $repoRoot ("artifacts\{0}-{1}.zip" -f $prefix, (Get-Date -Format 'yyyyMMdd'))
}
$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
if (Test-Path $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force }

Compress-Archive -Path (Join-Path $distRoot '*') -DestinationPath $OutputPath -CompressionLevel Optimal
$archive = Get-Item $OutputPath
$fileCount = (Get-ChildItem -Path $distRoot -Recurse -File).Count
"{0} — {1:N0} 个文件，{2:N0} 字节" -f $archive.FullName, $fileCount, $archive.Length
