param(
    [switch]$Disable,
    [int]$Port = 11080,
    [string]$VeyonCli = ""
)

# Enables the local Veyon WebAPI that the teacher screen wall reads frames from, and pins it to
# loopback. The WebAPI has no TLS and its key-file authentication carries the private key in the
# request body, so a rule that allows the whole subnet would leak the teacher's key. Run elevated.
$ErrorActionPreference = "Stop"
$ruleName = "VeyonCampus Teacher WebAPI (loopback only)"

if (-not $VeyonCli) {
    $candidates = @(
        (Join-Path $env:ProgramFiles "Veyon\veyon-cli.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Veyon\veyon-cli.exe")
    ) | Where-Object { $_ -and (Test-Path $_) }
    $VeyonCli = $candidates | Select-Object -First 1
}
if (-not $VeyonCli) { throw "veyon-cli.exe not found; install Veyon first." }

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script changes the Veyon service and firewall; run it as administrator."
}

function Remove-LoopbackRule {
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
}

if ($Disable) {
    & $VeyonCli config set WebAPI/HttpServerEnabled false | Out-Null
    Restart-Service VeyonService -Force
    Remove-LoopbackRule
    Write-Output "WebAPI disabled; loopback firewall rule removed."
    exit 0
}

& $VeyonCli config set WebAPI/HttpServerEnabled true | Out-Null
& $VeyonCli config set WebAPI/HttpServerPort $Port | Out-Null
Restart-Service VeyonService -Force

$deadline = (Get-Date).AddSeconds(20)
$listening = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $listening = [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    if ($listening) { break }
}
if (-not $listening) { throw "Veyon WebAPI did not start listening on port $Port." }

# Allow the port only from this machine. The Veyon service still binds 0.0.0.0; the firewall is what
# keeps the key-bearing authentication request off the wire.
Remove-LoopbackRule
# IPv6 loopback is not accepted as a remote address by New-NetFirewallRule, and the wall only ever
# dials 127.0.0.1, so the rule pins IPv4 loopback.
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP `
    -LocalPort $Port -RemoteAddress 127.0.0.1 -Profile Any -ErrorAction Stop | Out-Null
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    throw "Loopback firewall rule was not created; refusing to leave the WebAPI exposed."
}

Write-Output "WebAPI enabled on port $Port; inbound allowed from loopback only (rule: $ruleName)."
