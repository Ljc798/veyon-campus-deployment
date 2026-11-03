[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,

    [Parameter(Mandatory = $true)]
    [string]$PrivateKeyPath
)

$ErrorActionPreference = 'Stop'
$nodeCommand = Get-Command node.exe -ErrorAction Stop
$signer = Join-Path $PSScriptRoot 'sign-local-student-update-stable.cjs'
$installer = [IO.Path]::GetFullPath($InstallerPath)
$privateKey = [IO.Path]::GetFullPath($PrivateKeyPath)
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "找不到 StudentSetup 安装器：$installer"
}
if (-not (Test-Path -LiteralPath $privateKey -PathType Leaf)) {
    throw '找不到 Developer Release 私钥文件。'
}

$securePassphrase = Read-Host '输入 Developer Release 私钥口令（不会回显）' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassphrase)
try {
    $passphrase = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $request = @{
        installer = $installer
        privateKey = $privateKey
        passphrase = $passphrase
    } | ConvertTo-Json -Compress
    $request | & $nodeCommand.Source $signer
    if ($LASTEXITCODE -ne 0) {
        throw '本机 stable 更新包签名失败；未上传或发布。'
    }
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    $securePassphrase.Dispose()
    $passphrase = $null
    $request = $null
}
