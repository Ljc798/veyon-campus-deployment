[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$InstallerPaths,
    [Parameter(Mandatory = $true)]
    [string]$SignatureManifestPath,
    [string]$ExpectedThumbprint = $env:VEYONCAMPUS_AUTHENTICODE_THUMBPRINT,
    [string]$TimestampUrl = $env:VEYONCAMPUS_AUTHENTICODE_TIMESTAMP_URL
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$pfxPath = $null
$certificate = $null
$password = $env:VEYONCAMPUS_AUTHENTICODE_PFX_PASSWORD

function Resolve-ArtifactsPath {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Description)
    $resolved = [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
    if (-not $resolved.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description 必须位于仓库 artifacts 目录中。"
    }
    return $resolved
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }
    throw 'Windows SDK SignTool 未安装或无法定位。'
}

try {
    $pfxBase64 = $env:VEYONCAMPUS_AUTHENTICODE_PFX_BASE64
    if ([string]::IsNullOrWhiteSpace($pfxBase64) -or [string]::IsNullOrWhiteSpace($password)) {
        throw 'SIGNING-BLOCKED：未配置 Authenticode PFX 与密码。'
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
        throw 'SIGNING-BLOCKED：未配置 Authenticode 发布者指纹。'
    }
    $normalizedThumbprint = ($ExpectedThumbprint -replace '\s', '').ToUpperInvariant()
    if ($normalizedThumbprint -notmatch '^[0-9A-F]{40}$') {
        throw 'SIGNING-BLOCKED：未配置有效的 Authenticode 证书 SHA-1 指纹。'
    }
    if ($TimestampUrl -notmatch '^https?://[^\s]+$') {
        throw 'SIGNING-BLOCKED：未配置 RFC 3161 时间戳 URL。'
    }
    if ($InstallerPaths.Count -ne 2) {
        throw '必须同时签署 StudentSetup 与 TeacherConsole 两个安装器。'
    }

    $resolvedInstallers = @($InstallerPaths | ForEach-Object {
        Resolve-ArtifactsPath -Path $_ -Description '安装器'
    })
    if (@($resolvedInstallers | Select-Object -Unique).Count -ne 2) {
        throw 'StudentSetup 与 TeacherConsole 安装器路径必须不同。'
    }
    foreach ($installer in $resolvedInstallers) {
        if (-not (Test-Path -LiteralPath $installer -PathType Leaf) -or
            (Get-Item -LiteralPath $installer -Force).Length -le 0) {
            throw "待签署安装器不存在或为空：$([IO.Path]::GetFileName($installer))"
        }
    }
    $manifestPath = Resolve-ArtifactsPath -Path $SignatureManifestPath -Description '签名清单'

    try {
        $pfxBytes = [Convert]::FromBase64String(($pfxBase64 -replace '\s', ''))
    } catch [FormatException] {
        throw 'SIGNING-BLOCKED：Authenticode PFX Secret 不是有效的 Base64。'
    }
    if ($pfxBytes.Length -le 0 -or $pfxBytes.Length -gt 5MB) {
        throw 'SIGNING-BLOCKED：Authenticode PFX Secret 大小无效。'
    }
    $temporaryRoot = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { $env:RUNNER_TEMP } else { $env:TEMP }
    if ([string]::IsNullOrWhiteSpace($temporaryRoot)) { throw '无法定位临时目录以保护签名凭据。' }
    $pfxPath = Join-Path $temporaryRoot ('veyon-campus-authenticode-' + [guid]::NewGuid().ToString('N') + '.pfx')
    [IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
    [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)

    try {
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [IO.File]::ReadAllBytes($pfxPath), $password,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    } catch {
        throw 'SIGNING-BLOCKED：无法使用已配置密码打开 Authenticode PFX。'
    }
    if (-not $certificate.HasPrivateKey -or $certificate.Thumbprint.ToUpperInvariant() -cne $normalizedThumbprint) {
        throw 'SIGNING-BLOCKED：PFX 私钥或证书指纹与配置不匹配。'
    }
    $now = [DateTime]::UtcNow
    if ($now -lt $certificate.NotBefore.ToUniversalTime() -or $now -ge $certificate.NotAfter.ToUniversalTime()) {
        throw 'SIGNING-BLOCKED：Authenticode 证书当前不在有效期内。'
    }
    $ekuExtension = $certificate.Extensions | Where-Object {
        $_.Oid.Value -eq '2.5.29.37'
    } | Select-Object -First 1
    if (-not $ekuExtension) { throw 'SIGNING-BLOCKED：证书没有明确的 Code Signing EKU。' }
    $eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($ekuExtension, $ekuExtension.Critical)
    if (-not ($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })) {
        throw 'SIGNING-BLOCKED：证书不包含 Code Signing EKU。'
    }

    $signTool = Find-SignTool
    $signatureRecords = [Collections.Generic.List[object]]::new()
    foreach ($installer in $resolvedInstallers) {
        & $signTool sign /f $pfxPath /p $password /fd SHA256 /tr $TimestampUrl /td SHA256 $installer
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode 签名失败：$([IO.Path]::GetFileName($installer))。"
        }
        & $signTool verify /pa /all /v $installer
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode 信任链核验失败：$([IO.Path]::GetFileName($installer))。"
        }
        $signature = Get-AuthenticodeSignature -FilePath $installer
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -cne $normalizedThumbprint -or
            $null -eq $signature.TimeStamperCertificate) {
            throw "签名状态、发布者指纹或时间戳核验失败：$([IO.Path]::GetFileName($installer))。"
        }
        $signatureRecords.Add([pscustomobject]@{
            file = [IO.Path]::GetFileName($installer)
            sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
            authenticodeStatus = 'valid'
            signerSubject = $signature.SignerCertificate.Subject
            signerThumbprint = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
            signerNotAfterUtc = $signature.SignerCertificate.NotAfter.ToUniversalTime().ToString('O')
            timestampSubject = $signature.TimeStamperCertificate.Subject
            timestampThumbprint = $signature.TimeStamperCertificate.Thumbprint.ToUpperInvariant()
        })
    }

    $manifest = [pscustomobject]@{
        schemaVersion = 1
        generatedUtc = [DateTime]::UtcNow.ToString('O')
        digestAlgorithm = 'SHA-256'
        timestampProtocol = 'RFC 3161'
        signatures = @($signatureRecords)
    } | ConvertTo-Json -Depth 5
    $manifestParent = Split-Path -Parent $manifestPath
    if (-not (Test-Path -LiteralPath $manifestParent -PathType Container)) {
        New-Item -Path $manifestParent -ItemType Directory -Force | Out-Null
    }
    [IO.File]::WriteAllText($manifestPath, $manifest + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Write-Host "Authenticode 已验证并生成清单：$manifestPath"
} finally {
    if ($certificate) { $certificate.Dispose() }
    if ($pfxPath -and (Test-Path -LiteralPath $pfxPath -PathType Leaf)) {
        Remove-Item -LiteralPath $pfxPath -Force
    }
    $password = $null
    $pfxBase64 = $null
}
