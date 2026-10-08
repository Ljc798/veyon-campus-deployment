[CmdletBinding()]
param(
    [string]$BundleDirectory = (Join-Path $PSScriptRoot '../artifacts/update-test'),
    [string]$TeacherPath = (Join-Path $env:ProgramFiles 'Veyon Campus/Teacher/VeyonCampus.Teacher.exe')
)
$ErrorActionPreference = 'Stop'
$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
if (-not (Test-Path -LiteralPath $TeacherPath -PathType Leaf)) { throw 'Install the public-key-enabled Teacher 0.4.55 baseline first.' }
$node = (Get-Command node.exe -ErrorAction Stop).Source
$serverScript = Join-Path $PSScriptRoot 'local-update-test.cjs'
$server = Start-Process -FilePath $node -ArgumentList @("`"$serverScript`"", 'serve', "`"$bundleRoot`"") `
    -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $bundleRoot 'server.log') `
    -RedirectStandardError (Join-Path $bundleRoot 'server-error.log')
$oldApi = $env:VEYONCAMPUS_DEPLOYMENT_PACKAGES_API_BASE_URL
try {
    Start-Sleep -Seconds 2
    if ($server.HasExited) { throw 'Local test server did not start; check server-error.log or port 39176.' }
    $env:VEYONCAMPUS_DEPLOYMENT_PACKAGES_API_BASE_URL = 'http://127.0.0.1:39176/'
    Start-Process -FilePath $TeacherPath
    Write-Host 'Teacher local update test: 0.4.55 -> 0.4.56. Open Updates, check, then download/restart.'
    Read-Host 'After verifying the restarted version, press Enter to stop the temporary server' | Out-Null
} finally {
    $env:VEYONCAMPUS_DEPLOYMENT_PACKAGES_API_BASE_URL = $oldApi
    if (-not $server.HasExited) { Stop-Process -Id $server.Id }
}
