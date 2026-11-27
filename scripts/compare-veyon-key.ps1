<#
.SYNOPSIS
    打印本机 Veyon 密钥库里每条校区密钥的指纹，用于两台机器对照。

.DESCRIPTION
    屏幕墙提示「认证被拒绝：密钥不匹配」时，需要判断教师端私钥与学生端公钥是不是同一对。
    Veyon 只按密钥名匹配，不校验内容；一旦教师端密钥被重建（重装 Veyon、重置配置、换机器），
    名字不变（名字由校区 ID 哈希得到）、内容却换了，学生机会一律拒绝。

    本脚本只读：不创建、不导入、不删除任何密钥。
    在教师机和出问题的学生机各跑一次，对比打印出来的「指纹」即可：
      · 两边指纹相同  → 密钥配对正常，认证失败另有原因（网络、认证方式、目标机器不是这台）。
      · 两边指纹不同  → 学生端那把是旧公钥，需要删除后重新下发。

.PARAMETER KeyId
    只查看指定密钥名。默认列出密钥库中所有带公钥的密钥。

.PARAMETER ExpectedFingerprint
    可选。把另一台机器打印的指纹传进来，本脚本直接给出「一致 / 不一致」的结论。

.PARAMETER KeyStoreRoot
    Veyon 密钥库根目录，默认 C:\ProgramData\Veyon\keys。

.PARAMETER OutputPath
    可选。把同样的内容另存为文本文件，方便发给别人比对。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\compare-veyon-key.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\compare-veyon-key.ps1 -KeyId CDBPDCGNJHEMBDFGAME3FHWLLJNGOEFH -OutputPath .\student-key.txt

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\compare-veyon-key.ps1 -ExpectedFingerprint "3F2A1B0C 8E4D5A6B 0C1D2E3F 4A5B6C7D"
#>
[CmdletBinding()]
param(
    [string[]]$KeyId,
    [string]$ExpectedFingerprint,
    [string]$KeyStoreRoot = 'C:\ProgramData\Veyon\keys',
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

# 不强制改写控制台编码：跟随当前终端（936 或 65001），避免中文乱码。
$script:lines = New-Object System.Collections.Generic.List[string]

function Write-Line {
    param([string]$Text, [string]$Color = '')
    $script:lines.Add($Text)
    if ($Color) { Write-Host $Text -ForegroundColor $Color } else { Write-Host $Text }
}

function Get-PemFingerprint {
    <#
        指纹定义与 Veyon 一致，也与本项目的学生包指纹一致：对公钥的 DER 主体取 SHA-256。
        PEM 里 -----BEGIN/END----- 之间的 base64 正文就是 SPKI DER，因此先归一化再去 base64 解码。
        这样得到的值与 Veyon 配置器「认证密钥」里显示、以及教师端推送时比对的是同一个值。
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    $raw = [System.IO.File]::ReadAllText($Path)
    $body = ($raw -split "`r?`n" | Where-Object { $_ -notmatch '^-{3,}' }) -join ''
    $body = $body -replace '\s', ''
    if ($body.Length -eq 0) { return $null }

    try {
        $der = [System.Convert]::FromBase64String($body)
    }
    catch {
        return $null
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($der)
    }
    finally {
        $sha.Dispose()
    }
    return ([System.BitConverter]::ToString($hash) -replace '-', '')
}

function Format-Fingerprint {
    param([string]$Hex)
    $groups = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $Hex.Length; $i += 4) {
        $len = [Math]::Min(4, $Hex.Length - $i)
        $groups.Add($Hex.Substring($i, $len))
    }
    return ($groups -join ' ')
}

function Normalize-Fingerprint {
    param([string]$Text)
    if (-not $Text) { return '' }
    return ($Text -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
}

# ---- 最小 DER 工具：只为「从私钥推出公钥」服务 ---------------------------------
# PowerShell 5.1 没有 RSA.ImportFromPem，所以手工读 PKCS#8 / PKCS#1 的 DER，
# 取出 modulus/exponent 再拼出 SubjectPublicKeyInfo，指纹定义与 Veyon 完全一致。

function Read-DerTlv {
    param([byte[]]$Data, [int]$Offset)
    $tag = $Data[$Offset]
    $index = $Offset + 1
    $lengthByte = $Data[$index]
    $index++
    if ($lengthByte -lt 0x80) {
        $length = $lengthByte
    }
    else {
        $count = $lengthByte -band 0x7F
        $length = 0
        for ($i = 0; $i -lt $count; $i++) { $length = ($length * 256) + $Data[$index + $i] }
        $index += $count
    }
    $value = New-Object byte[] $length
    [Array]::Copy($Data, $index, $value, 0, $length)
    return @{ Tag = [int]$tag; Value = $value; Next = ($index + $length) }
}

function Add-DerLength {
    param($Target, [int]$Length)
    if ($Length -lt 0x80) {
        $Target.Add([byte]$Length)
    }
    else {
        $bytes = New-Object System.Collections.Generic.List[byte]
        $value = $Length
        while ($value -gt 0) {
            $bytes.Insert(0, [byte]($value -band 0xFF))
            $value = $value -shr 8
        }
        $Target.Add([byte](0x80 -bor $bytes.Count))
        $Target.AddRange($bytes.ToArray())
    }
}

function New-DerElement {
    param([byte]$Tag, [byte[]]$Value)
    $list = New-Object System.Collections.Generic.List[byte]
    $list.Add($Tag)
    Add-DerLength -Target $list -Length $Value.Length
    $list.AddRange($Value)
    return , $list.ToArray()
}

function New-DerInteger {
    param([byte[]]$Value)
    $start = 0
    while ($start -lt ($Value.Length - 1) -and $Value[$start] -eq 0) { $start++ }
    $trimmed = New-Object System.Collections.Generic.List[byte]
    for ($i = $start; $i -lt $Value.Length; $i++) { $trimmed.Add($Value[$i]) }
    if (($trimmed[0] -band 0x80) -ne 0) { $trimmed.Insert(0, [byte]0) }
    return New-DerElement -Tag 0x02 -Value $trimmed.ToArray()
}

function Get-PublicFingerprintFromPrivatePem {
    param([Parameter(Mandatory = $true)][string]$Path)

    $raw = [System.IO.File]::ReadAllText($Path)
    $body = ($raw -split "`r?`n" | Where-Object { $_ -notmatch '^-{3,}' }) -join ''
    $body = $body -replace '\s', ''
    if ($body.Length -eq 0) { return $null }
    $der = [System.Convert]::FromBase64String($body)

    $outer = Read-DerTlv -Data $der -Offset 0
    if ($outer.Tag -ne 0x30) { return $null }

    $inner = $outer.Value
    $pos = 0
    $elements = New-Object System.Collections.Generic.List[object]
    while ($pos -lt $inner.Length) {
        $tlv = Read-DerTlv -Data $inner -Offset $pos
        $elements.Add($tlv)
        $pos = $tlv.Next
    }
    if ($elements.Count -lt 3) { return $null }

    $rsaPrivate = $null
    if ($elements[0].Tag -eq 0x02 -and $elements[1].Tag -eq 0x30 -and $elements[2].Tag -eq 0x04) {
        # PKCS#8 PrivateKeyInfo：第 3 个元素是装着 PKCS#1 的 OCTET STRING
        $pkcs1 = Read-DerTlv -Data $elements[2].Value -Offset 0
        if ($pkcs1.Tag -ne 0x30) { return $null }
        $rsaPrivate = $pkcs1.Value
    }
    elseif ($elements[0].Tag -eq 0x02 -and $elements[1].Tag -eq 0x02) {
        # PKCS#1 RSAPrivateKey
        $rsaPrivate = $inner
    }
    else {
        return $null
    }

    $pos = 0
    $fields = New-Object System.Collections.Generic.List[object]
    while ($pos -lt $rsaPrivate.Length) {
        $tlv = Read-DerTlv -Data $rsaPrivate -Offset $pos
        $fields.Add($tlv)
        $pos = $tlv.Next
    }
    if ($fields.Count -lt 3) { return $null }

    $modulus = $fields[1].Value
    $exponent = $fields[2].Value
    if ($modulus.Length -eq 0 -or $exponent.Length -eq 0) { return $null }

    $rsaPublicContent = New-Object System.Collections.Generic.List[byte]
    $rsaPublicContent.AddRange([byte[]](New-DerInteger -Value $modulus))
    $rsaPublicContent.AddRange([byte[]](New-DerInteger -Value $exponent))
    $rsaPublic = New-DerElement -Tag 0x30 -Value $rsaPublicContent.ToArray()

    # AlgorithmIdentifier: rsaEncryption (1.2.840.113549.1.1.1) + NULL
    $algorithm = [byte[]](0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00)
    $bitContent = New-Object System.Collections.Generic.List[byte]
    $bitContent.Add([byte]0)
    $bitContent.AddRange([byte[]]$rsaPublic)
    $bitString = New-DerElement -Tag 0x03 -Value $bitContent.ToArray()

    $spkiContent = New-Object System.Collections.Generic.List[byte]
    $spkiContent.AddRange($algorithm)
    $spkiContent.AddRange([byte[]]$bitString)
    $spki = New-DerElement -Tag 0x30 -Value $spkiContent.ToArray()

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash($spki) } finally { $sha.Dispose() }
    return ([System.BitConverter]::ToString($hash) -replace '-', '')
}

# ---- 环境信息 ----------------------------------------------------------------
$machine = $env:COMPUTERNAME
Write-Line ''
Write-Line '============================================================'
Write-Line (" Veyon 校区密钥指纹检查    计算机：{0}" -f $machine)
Write-Line (" 时间：{0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$isElevated = $false
try {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    $isElevated = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}
catch { }
if ($isElevated) {
    Write-Line ' 会话权限：管理员（可读取 Veyon 私钥目录）'
}
else {
    Write-Line ' 会话权限：普通用户（Veyon 私钥目录可能读不到；核对私钥时请右键「以管理员身份运行」）'
}
Write-Line '============================================================'

$cliCandidates = @(
    (Join-Path $env:ProgramFiles 'Veyon\veyon-cli.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Veyon\veyon-cli.exe')
)
$cli = $null
foreach ($candidate in $cliCandidates) {
    if ($candidate -and (Test-Path -LiteralPath $candidate)) { $cli = $candidate; break }
}

$listedKeys = @{}
if ($null -ne $cli) {
    $version = (& $cli --version 2>&1 | Select-Object -First 1)
    Write-Line (" Veyon 版本：{0}" -f ([string]$version).Trim())
    $method = (& $cli config get Authentication/Method 2>&1 | Select-Object -First 1)
    $methodText = ([string]$method).Trim()
    $methodMeaning = '未知'
    if ($methodText -eq '1') { $methodMeaning = '密钥认证，正确' }
    elseif ($methodText -eq '0') { $methodMeaning = '密码认证，不适用于屏幕墙' }
    elseif ($methodText -eq '2') { $methodMeaning = 'LDAP/网络认证，不适用于屏幕墙' }
    Write-Line (" 认证方式：{0}（{1}）" -f $methodText, $methodMeaning)

    # Veyon 自己的清单比「文件是否读得到」更权威：私钥目录设有 ACL，非管理员会话可能读不到文件。
    $listingText = (& $cli authkeys list 2>&1 | Out-String)
    foreach ($rawLine in ($listingText -split "`r?`n")) {
        $line = $rawLine.Trim()
        if ($line -notmatch '^([^/]+)/(public|private)$') { continue }
        $entryName = $Matches[1]
        $entryType = $Matches[2]
        if (-not $listedKeys.ContainsKey($entryName)) { $listedKeys[$entryName] = @{ public = $false; private = $false } }
        $listedKeys[$entryName][$entryType] = $true
    }
}
else {
    Write-Line ' 未找到 veyon-cli.exe，跳过版本、认证方式与密钥清单检查。' 'Yellow'
}

Write-Line (" 密钥库：{0}" -f $KeyStoreRoot)

if (-not (Test-Path -LiteralPath $KeyStoreRoot)) {
    Write-Line ''
    Write-Line (" 密钥库目录不存在：{0}" -f $KeyStoreRoot) 'Red'
    Write-Line ' 说明本机没有配置过 Veyon 密钥（学生端未推送过配置包，或 Veyon 未安装）。' 'Yellow'
    if ($OutputPath) { $script:lines | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
    return
}

$publicRoot = Join-Path $KeyStoreRoot 'public'
$privateRoot = Join-Path $KeyStoreRoot 'private'

$names = @()
if (Test-Path -LiteralPath $publicRoot) {
    $names += @(Get-ChildItem -LiteralPath $publicRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'key') } |
        Select-Object -ExpandProperty Name)
}
if (Test-Path -LiteralPath $privateRoot) {
    $names += @(Get-ChildItem -LiteralPath $privateRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'key') } |
        Select-Object -ExpandProperty Name)
}
$names = @($names | Sort-Object -Unique)

if ($KeyId -and $KeyId.Count -gt 0) {
    $names = @($names | Where-Object { $KeyId -contains $_ })
}

if ($names.Count -eq 0) {
    Write-Line ''
    Write-Line ' 密钥库里没有找到匹配的密钥。' 'Yellow'
    if ($KeyId) { Write-Line (" 指定的密钥名：{0}" -f ($KeyId -join ', ')) 'Yellow' }
    if ($OutputPath) { $script:lines | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
    return
}

$expected = Normalize-Fingerprint -Text $ExpectedFingerprint
$mismatch = $false
$match = $false
$pairMismatch = $false

foreach ($name in $names) {
    Write-Line ''
    Write-Line ("[{0}]" -f $name)

    $publicPath = Join-Path (Join-Path $publicRoot $name) 'key'
    $privatePath = Join-Path (Join-Path $privateRoot $name) 'key'
    $hasPublic = Test-Path -LiteralPath $publicPath
    $hasPrivate = Test-Path -LiteralPath $privatePath

    $listed = $null
    if ($listedKeys.ContainsKey($name)) { $listed = $listedKeys[$name] }

    $privateDir = Join-Path $privateRoot $name
    $privateText = '无'
    if ($hasPrivate) { $privateText = '有' }
    elseif ($listed -and $listed.private) {
        $privateText = '有（Veyon 清单可见；当前会话无权读取文件，需以管理员运行才能核对内容）'
    }
    elseif (Test-Path -LiteralPath $privateDir) {
        $items = @(Get-ChildItem -LiteralPath $privateDir -ErrorAction SilentlyContinue)
        if ($items.Count -gt 0) {
            $privateText = '目录存在，但没有 key 文件（实际内容：' + (($items | ForEach-Object { $_.Name }) -join ', ') + '）'
        }
        else {
            $privateText = '目录存在但为空或无权读取'
        }
    }

    $publicText = '无'
    if ($hasPublic) { $publicText = '有' }
    elseif ($listed -and $listed.public) { $publicText = '有（Veyon 清单可见；当前会话无权读取文件）' }

    Write-Line ("  私钥：{0}    公钥：{1}" -f $privateText, $publicText)

    # 公钥文件读不到时退回 Veyon 自己的导出命令，避免把「权限」误报成「没有密钥」。
    $fingerprint = $null
    if ($hasPublic) {
        $fingerprint = Get-PemFingerprint -Path $publicPath
    }
    elseif ($listed -and $listed.public -and $cli) {
        $tempPem = Join-Path $env:TEMP ("veyon-campus-compare-" + [guid]::NewGuid().ToString('N') + ".pem")
        try {
            & $cli authkeys export ($name + '/public') $tempPem 2>&1 | Out-Null
            if (Test-Path -LiteralPath $tempPem) { $fingerprint = Get-PemFingerprint -Path $tempPem }
        }
        catch { }
        finally {
            if (Test-Path -LiteralPath $tempPem) { Remove-Item -LiteralPath $tempPem -Force -ErrorAction SilentlyContinue }
        }
    }

    if (-not $fingerprint) {
        Write-Line '  无法取得公钥内容，算不出指纹。' 'Yellow'
        continue
    }

    $short = $fingerprint.Substring(0, 32)
    Write-Line ("  指纹：{0}" -f (Format-Fingerprint -Hex $short))
    Write-Line ("  全长：{0}" -f $fingerprint)

    if ($hasPrivate) {
        try {
            $derived = Get-PublicFingerprintFromPrivatePem -Path $privatePath
            if (-not $derived) {
                Write-Line '  配对：私钥文件无法解析（不是 RSA PKCS#1/PKCS#8）。' 'Yellow'
            }
            elseif ($derived -eq $fingerprint) {
                Write-Line '  配对：私钥与公钥是同一对，本机密钥自洽。' 'Green'
            }
            else {
                Write-Line '  配对：私钥与公钥不是同一对 —— 本机密钥库自相矛盾。' 'Red'
                Write-Line ("        私钥对应的指纹：{0}" -f (Format-Fingerprint -Hex $derived.Substring(0, 32))) 'Red'
                Write-Line '        学生机信的是「公钥」这把；教师端签名用的是「私钥」这把，两边对不上，认证必然全被拒。' 'Red'
                $pairMismatch = $true
            }
        }
        catch {
            Write-Line ("  配对：无法核对私钥（{0}）" -f $_.Exception.Message) 'Yellow'
        }
    }

    if ($expected) {
        $expectedMatches = $false
        if ($expected.Length -le $fingerprint.Length) {
            if ($fingerprint.Substring(0, $expected.Length) -eq $expected) { $expectedMatches = $true }
        }
        if ($expectedMatches) {
            Write-Line '  对照结果：一致 —— 这把公钥就是对方那把私钥的另一半。' 'Green'
            $match = $true
        }
        else {
            Write-Line '  对照结果：不一致 —— 同名但不是同一把钥匙。' 'Red'
            $mismatch = $true
        }
    }
}

Write-Line ''
Write-Line '------------------------------------------------------------'
if ($pairMismatch) {
    Write-Line ' 结论：本机私钥与公钥不是同一对，密钥库自相矛盾。' 'Red'
    Write-Line '       屏幕墙会用私钥签名，学生机用手上的公钥验签，因此全部被拒。' 'Red'
    Write-Line '       处理：本机重新建校区密钥对，再把新公钥下发给全部学生机。' 'Red'
}
elseif ($expected) {
    if ($mismatch) {
        Write-Line ' 结论：存在不一样的密钥。学生端那把是旧公钥，需要删除后重新下发。' 'Red'
    }
    elseif ($match) {
        Write-Line ' 结论：指纹一致，密钥配对正常；认证失败另有原因（网络、认证方式或目标主机不对）。' 'Green'
    }
}
else {
    Write-Line ' 用法：把这行「指纹」和另一台机器上同名密钥的指纹对照，必须逐字相同。' 
    Write-Line '       也可以用 -ExpectedFingerprint "<对方的指纹>" 让脚本直接给结论。'
    Write-Line ' 说明：教师端正常应「私钥：有 公钥：有」；学生端正常应「私钥：无 公钥：有」。'
}
Write-Line '------------------------------------------------------------'

if ($OutputPath) {
    $script:lines | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    Write-Host ''
    Write-Host ("结果已保存到：{0}" -f $OutputPath) -ForegroundColor Cyan
}
