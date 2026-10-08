[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Key', 'Sign')][string]$Action,
    [Parameter(Mandatory)][string]$KeyDirectory,
    [string]$ReleaseDirectory,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$keyRoot = [IO.Path]::GetFullPath($KeyDirectory)
if ($keyRoot -eq $repository -or $keyRoot.StartsWith($repository + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test private keys must remain outside the repository.'
}
$nodePath = (Get-Command node.exe -ErrorAction Stop).Source
$helper = Join-Path $PSScriptRoot 'local-update-test.cjs'
$secretPath = Join-Path $keyRoot 'test-passphrase.dpapi'
if ($Action -eq 'Key') {
    if (Test-Path -LiteralPath $keyRoot) { throw 'Choose a new test key directory; existing files are never replaced.' }
    New-Item -ItemType Directory -Path $keyRoot | Out-Null
    $acl = Get-Acl -LiteralPath $keyRoot
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),
            'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $keyRoot -AclObject $acl
    $passphrase = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $secure = ConvertTo-SecureString -String $passphrase -AsPlainText -Force
    $secure | ConvertFrom-SecureString | Set-Content -LiteralPath $secretPath -Encoding utf8
    @{action='key';outputDirectory=$keyRoot;passphrase=$passphrase} | ConvertTo-Json -Compress | & $nodePath $helper
} else {
    $secure = (Get-Content -LiteralPath $secretPath -Raw).Trim() | ConvertTo-SecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $passphrase = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        @{action='sign';directory=[IO.Path]::GetFullPath($ReleaseDirectory);version=$Version;
          privateKey=(Join-Path $keyRoot 'release-private.pem');publicKey=(Join-Path $keyRoot 'release-public.pem');
          passphrase=$passphrase} | ConvertTo-Json -Compress | & $nodePath $helper
    } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}
if ($LASTEXITCODE -ne 0) { throw 'Local update preparation failed.' }
$passphrase = $null
$secure = $null
