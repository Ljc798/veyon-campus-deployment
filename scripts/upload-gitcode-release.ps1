[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(beta-)?v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Tag,

    [string]$BundleDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tagMatch = [regex]::Match($Tag, '^(?:beta-)?v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')
$version = '{0}.{1}.{2}' -f $tagMatch.Groups[1].Value, $tagMatch.Groups[2].Value, $tagMatch.Groups[3].Value

if ([string]::IsNullOrWhiteSpace($BundleDirectory)) {
    $BundleDirectory = Join-Path $repoRoot (Join-Path 'artifacts' (Join-Path 'gitcode-upload' $Tag))
}
$BundleDirectory = [IO.Path]::GetFullPath($BundleDirectory)
New-Item -ItemType Directory -Path $BundleDirectory -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($env:GITCODE_OWNER)) { $env:GITCODE_OWNER = 'ljc_1126' }
if ([string]::IsNullOrWhiteSpace($env:GITCODE_REPO)) { $env:GITCODE_REPO = 'veyon-campus-deployment' }

$studentName = "VeyonCampus-Student-Setup-$version-win-x64.exe"
$teacherName = "VeyonCampus-Teacher-Setup-$version-win-x64.exe"
$checksumsName = 'SHA256SUMS'
$studentPath = Join-Path $BundleDirectory $studentName
$teacherPath = Join-Path $BundleDirectory $teacherName
$checksumsPath = Join-Path $BundleDirectory $checksumsName
$notesPath = Join-Path $BundleDirectory 'release-notes.md'

$githubHeaders = @{
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'veyon-campus-local-gitcode-release-uploader'
}
$releaseApi = "https://api.github.com/repos/Ljc798/veyon-campus-deployment/releases/tags/$Tag"
$commitApi = "https://api.github.com/repos/Ljc798/veyon-campus-deployment/commits/$Tag"
$assetBase = "https://github.com/Ljc798/veyon-campus-deployment/releases/download/$Tag"

Write-Output "Reading GitHub Release $Tag and preparing the two EXE installers..."
$githubRelease = Invoke-RestMethod -Method Get -Uri $releaseApi -Headers $githubHeaders
if ($githubRelease.tag_name -ne $Tag -or [string]::IsNullOrWhiteSpace($githubRelease.body)) {
    throw "GitHub Release $Tag is missing or has no release notes."
}
$githubCommit = Invoke-RestMethod -Method Get -Uri $commitApi -Headers $githubHeaders
if ($githubCommit.sha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Could not resolve GitHub tag $Tag to a commit."
}

foreach ($assetName in @($studentName, $teacherName, $checksumsName)) {
    $assetPath = Join-Path $BundleDirectory $assetName
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        Write-Output "Downloading $assetName to this computer..."
        Invoke-WebRequest -UseBasicParsing -Method Get -Uri "$assetBase/$assetName" -OutFile $assetPath -Headers $githubHeaders
    }
    if ((Get-Item -LiteralPath $assetPath).Length -lt 1) {
        throw "Downloaded release asset is empty: $assetName."
    }
}

$expectedHashes = @{}
foreach ($line in [IO.File]::ReadAllLines($checksumsPath)) {
    if ($line -match '^([a-fA-F0-9]{64})\s+\*?(.+)$') {
        $expectedHashes[$Matches[2].Trim()] = $Matches[1].ToLowerInvariant()
    }
}
foreach ($assetName in @($studentName, $teacherName)) {
    if (-not $expectedHashes.ContainsKey($assetName)) {
        throw "GitHub SHA256SUMS does not contain $assetName."
    }
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $BundleDirectory $assetName) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHashes[$assetName]) {
        throw "SHA-256 verification failed for $assetName. Delete that local file and run this script again."
    }
}

[IO.File]::WriteAllText($notesPath, [string]$githubRelease.body, [Text.UTF8Encoding]::new($false))

if (-not (Get-Command node.exe -ErrorAction SilentlyContinue)) {
    throw 'Node.js is required. Install the current Node.js LTS version, then run this command again.'
}

$gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue
if (-not $gitCommand) {
    $gitCandidates = @((Join-Path $env:ProgramFiles 'Git\cmd\git.exe'))
    $desktopGitRoot = Join-Path $env:LOCALAPPDATA 'GitHubDesktop'
    if (Test-Path -LiteralPath $desktopGitRoot -PathType Container) {
        $gitCandidates += Get-ChildItem -LiteralPath $desktopGitRoot -Directory -Filter 'app-*' |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object { Join-Path $_.FullName 'resources\app\git\cmd\git.exe' }
    }
    $gitPath = $gitCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $gitPath) { throw 'Git for Windows is required to verify the GitCode release tag.' }
    $env:PATH = (Split-Path -Parent $gitPath) + [IO.Path]::PathSeparator + $env:PATH
}

$originalToken = $env:GITCODE_TOKEN
$tokenWasSet = -not [string]::IsNullOrWhiteSpace($originalToken)
if (-not $tokenWasSet) {
    $secureToken = Read-Host 'Paste your GitCode personal access token (input stays hidden)' -AsSecureString
    $tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
    try {
        $env:GITCODE_TOKEN = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer)
    }
}

try {
    $publisher = Join-Path $PSScriptRoot 'publish-gitcode-release.cjs'
    & node $publisher $Tag $version $githubCommit.sha $notesPath $studentPath $teacherPath $checksumsPath
    if ($LASTEXITCODE -ne 0) { throw "GitCode Release upload exited with code $LASTEXITCODE." }
}
finally {
    if ($tokenWasSet) {
        $env:GITCODE_TOKEN = $originalToken
    }
    else {
        Remove-Item Env:\GITCODE_TOKEN -ErrorAction SilentlyContinue
    }
}
