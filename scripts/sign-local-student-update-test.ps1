[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$TestKeyDirectory
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$keyRoot = [IO.Path]::GetFullPath($TestKeyDirectory)
if ($keyRoot -eq $repository -or $keyRoot.StartsWith($repository + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '本机测试私钥必须保存在仓库外。'
}

$installer = [IO.Path]::GetFullPath($InstallerPath)
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
$setupDll = Join-Path $packageRoot 'VeyonCampus.StudentSetup.dll'
$agentDll = Join-Path $packageRoot 'WebsitePolicyAgent/VeyonCampus.Agent.dll'
foreach ($requiredFile in @($installer, $setupDll, $agentDll)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "缺少待签名包文件：$requiredFile"
    }
    if ((Get-Item -LiteralPath $requiredFile).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "待签名包文件不能是重解析点：$requiredFile"
    }
}

$setupVersion = [System.Reflection.AssemblyName]::GetAssemblyName($setupDll).Version.ToString(3)
$agentVersion = [System.Reflection.AssemblyName]::GetAssemblyName($agentDll).Version.ToString(3)
$expectedName = "VeyonCampus-Student-Setup-$setupVersion-win-x64.exe"
if ([IO.Path]::GetFileName($installer) -cne $expectedName) {
    throw "安装器文件名与包版本不匹配：期望 $expectedName"
}

$privateKeyPath = Join-Path $keyRoot 'release-private.pem'
$publicKeyPath = Join-Path $keyRoot 'release-public.pem'
$secretPath = Join-Path $keyRoot 'test-passphrase.dpapi'
foreach ($requiredKeyFile in @($privateKeyPath, $publicKeyPath, $secretPath)) {
    if (-not (Test-Path -LiteralPath $requiredKeyFile -PathType Leaf)) {
        throw "本机测试签名材料不完整：$requiredKeyFile"
    }
}

$secure = (Get-Content -LiteralPath $secretPath -Raw).Trim() | ConvertTo-SecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $passphrase = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $nodePath = (Get-Command node.exe -ErrorAction Stop).Source
    $helper = Join-Path $PSScriptRoot 'sign-local-student-update-test.cjs'
    $inputJson = @{
        installer = $installer
        version = $setupVersion
        agentVersion = $agentVersion
        privateKey = $privateKeyPath
        publicKey = $publicKeyPath
        passphrase = $passphrase
    } | ConvertTo-Json -Compress
    $output = $inputJson | & $nodePath $helper
    if ($LASTEXITCODE -ne 0) { throw '本机测试 StudentSetup 签名失败。' }
    $result = ($output -join '') | ConvertFrom-Json
    Write-Host "已签署本机测试 StudentSetup $($result.version) / SYSTEM Agent $($result.agentVersion)；未上传线上。"
    Write-Host "签名清单：$($result.sidecar)"
    Write-Host "SHA-256：$($result.sha256)"
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    $passphrase = $null
    $secure = $null
}
