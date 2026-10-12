param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Student', 'Teacher')]
    [string]$Role,

    [string]$Directory = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
$resolvedDirectory = (Resolve-Path -LiteralPath $Directory).Path
$partPattern = "^VeyonCampus-$Role-Setup-(?<version>.+)-win-x64\.exe\.part(?<index>[0-9]{3})$"
$parts = @(Get-ChildItem -LiteralPath $resolvedDirectory -Filter "VeyonCampus-$Role-Setup-*.exe.part*" -File |
    Where-Object { $_.Name -match $partPattern } |
    Sort-Object Name)

if ($parts.Count -eq 0) {
    throw "No split $Role installer parts were found in $resolvedDirectory."
}

$outputName = $parts[0].Name -replace '\.part[0-9]{3}$', ''
for ($index = 0; $index -lt $parts.Count; $index++) {
    $partNumber = [int]$parts[$index].Name.Substring($parts[$index].Name.Length - 3)
    if ($partNumber -ne ($index + 1)) {
        throw "Installer parts must be complete and numbered from part001; found $($parts[$index].Name)."
    }
}

$checksumPath = Join-Path $resolvedDirectory 'SHA256SUMS'
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    throw 'Download SHA256SUMS into the same folder as the installer parts.'
}

$targetPath = Join-Path $resolvedDirectory $outputName
$output = [IO.File]::Open($targetPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    foreach ($part in $parts) {
        $input = [IO.File]::OpenRead($part.FullName)
        try { $input.CopyTo($output) }
        finally { $input.Dispose() }
    }
}
catch {
    $output.Dispose()
    Remove-Item -LiteralPath $targetPath -Force -ErrorAction SilentlyContinue
    throw
}
finally {
    if ($output) { $output.Dispose() }
}

$checksumLine = Get-Content -LiteralPath $checksumPath |
    Where-Object { $_ -match '^([0-9a-fA-F]{64})  (.+)$' -and $Matches[2] -ceq $outputName } |
    Select-Object -First 1
if (-not $checksumLine -or $checksumLine -notmatch '^([0-9a-fA-F]{64})  (.+)$') {
    Remove-Item -LiteralPath $targetPath -Force -ErrorAction SilentlyContinue
    throw "SHA256SUMS has no checksum entry for $outputName."
}

$expectedHash = $Matches[1].ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -cne $expectedHash) {
    Remove-Item -LiteralPath $targetPath -Force -ErrorAction SilentlyContinue
    throw "Checksum verification failed for $outputName; the assembled installer was removed."
}

Write-Host "Assembled and verified $outputName (SHA-256 $actualHash)."
