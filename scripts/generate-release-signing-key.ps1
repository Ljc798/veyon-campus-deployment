[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'VeyonCampus\ReleaseKeys\Test')
)

$ErrorActionPreference = 'Stop'
$nodeCommand = Get-Command node.exe -ErrorAction Stop
$outputDirectoryFullPath = [IO.Path]::GetFullPath($OutputDirectory)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryPrefix = $repositoryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if ($outputDirectoryFullPath.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputDirectoryFullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release signing keys must be stored outside the repository.'
}

$privateKeyPath = Join-Path $outputDirectoryFullPath 'release-private.pem'
$publicKeyPath = Join-Path $outputDirectoryFullPath 'release-public.pem'
if ((Test-Path -LiteralPath $privateKeyPath -PathType Leaf) -or
    (Test-Path -LiteralPath $publicKeyPath -PathType Leaf)) {
    throw "A key file already exists in $outputDirectoryFullPath. Choose a new output directory; existing keys are never overwritten."
}

New-Item -ItemType Directory -Path $outputDirectoryFullPath -Force | Out-Null
$firstPassphrase = Read-Host 'Set a passphrase for the encrypted private key (at least 16 characters)' -AsSecureString
$confirmationPassphrase = Read-Host 'Enter the same passphrase again' -AsSecureString
if ($firstPassphrase.Length -lt 16) {
    throw 'Use a passphrase with at least 16 characters.'
}

$firstPassphrasePointer = [IntPtr]::Zero
$confirmationPassphrasePointer = [IntPtr]::Zero
try {
    $firstPassphrasePointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($firstPassphrase)
    $confirmationPassphrasePointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirmationPassphrase)
    $passphraseText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($firstPassphrasePointer)
    $confirmationText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($confirmationPassphrasePointer)
    if (-not [string]::Equals($passphraseText, $confirmationText, [StringComparison]::Ordinal)) {
        throw 'The passphrases did not match.'
    }

    $env:VEYONCAMPUS_RELEASE_KEY_OUTPUT_DIRECTORY = $outputDirectoryFullPath
    $env:VEYONCAMPUS_RELEASE_KEY_PASSPHRASE = $passphraseText
    $nodeSource = @'
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const outputDirectory = process.env.VEYONCAMPUS_RELEASE_KEY_OUTPUT_DIRECTORY;
const passphrase = process.env.VEYONCAMPUS_RELEASE_KEY_PASSPHRASE;
const privateKeyPath = path.join(outputDirectory, 'release-private.pem');
const publicKeyPath = path.join(outputDirectory, 'release-public.pem');
const keyPair = crypto.generateKeyPairSync('rsa', {
  modulusLength: 3072,
  publicKeyEncoding: { type: 'spki', format: 'pem' },
  privateKeyEncoding: {
    type: 'pkcs8',
    format: 'pem',
    cipher: 'aes-256-cbc',
    passphrase
  }
});

fs.writeFileSync(privateKeyPath, keyPair.privateKey, { encoding: 'utf8', flag: 'wx', mode: 0o600 });
try {
  fs.writeFileSync(publicKeyPath, keyPair.publicKey, { encoding: 'utf8', flag: 'wx' });
} catch (error) {
  fs.rmSync(privateKeyPath, { force: true });
  throw error;
}
'@
    & $nodeCommand.Source -e $nodeSource
    if ($LASTEXITCODE -ne 0) {
        throw 'Node.js could not generate the release signing key pair.'
    }
}
finally {
    Remove-Item Env:VEYONCAMPUS_RELEASE_KEY_OUTPUT_DIRECTORY -ErrorAction SilentlyContinue
    Remove-Item Env:VEYONCAMPUS_RELEASE_KEY_PASSPHRASE -ErrorAction SilentlyContinue
    if ($firstPassphrasePointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($firstPassphrasePointer)
    }
    if ($confirmationPassphrasePointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($confirmationPassphrasePointer)
    }
    $passphraseText = $null
    $confirmationText = $null
}

Write-Host "Encrypted private key: $privateKeyPath"
Write-Host "Public key: $publicKeyPath"
Write-Host 'Keep the private key and its passphrase private. Only the public key may be shared or embedded in clients.'
