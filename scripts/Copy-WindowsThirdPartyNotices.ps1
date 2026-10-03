[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [Parameter(Mandatory = $true)]
    [ValidateSet('StudentSetup', 'TeacherConsole')]
    [string]$Role,
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$publishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$appRoot = Join-Path $repositoryRoot 'src/VeyonCampus.App'
$lockPath = Join-Path $appRoot 'packages.lock.json'
$trustPath = Join-Path $repositoryRoot 'src/VeyonCampus.Core/VeyonInstallerTrust.cs'
$appName = if ($Role -eq 'StudentSetup') { 'VeyonCampus.StudentSetup' } else { 'VeyonCampus.Teacher' }
$runtimeConfigPath = Join-Path $publishDirectory "$appName.runtimeconfig.json"

if (-not (Test-Path -LiteralPath $publishDirectory -PathType Container)) {
    throw "发布目录不存在：$publishDirectory"
}
if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    throw "找不到 App NuGet 锁定文件：$lockPath"
}
if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
    throw "自包含发布结果缺少 runtimeconfig：$runtimeConfigPath"
}

$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$frameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
if ($frameworks.Count -eq 0) {
    $frameworks = @($runtimeConfig.runtimeOptions.frameworks)
}
if ($frameworks.Count -eq 0) {
    throw "无法从 runtimeconfig 确认随包发布的 .NET framework：$runtimeConfigPath"
}

$globalPackagesPath = [Environment]::GetEnvironmentVariable('NUGET_PACKAGES')
if ([string]::IsNullOrWhiteSpace($globalPackagesPath)) {
    $globalPackagesPath = Join-Path $env:USERPROFILE '.nuget/packages'
}
$globalPackagesPath = [IO.Path]::GetFullPath($globalPackagesPath)
$licenseOutput = Join-Path $publishDirectory 'licenses'
if (Test-Path -LiteralPath $licenseOutput) {
    Remove-Item -LiteralPath $licenseOutput -Recurse -Force
}
New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null

$lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
$packageVersions = @{}
foreach ($targetName in @('net10.0', 'net10.0/win-x64')) {
    $targetProperty = $lock.dependencies.PSObject.Properties[$targetName]
    if ($null -eq $targetProperty) {
        throw "NuGet 锁定文件缺少 Windows 发布目标：$targetName"
    }
    foreach ($dependency in $targetProperty.Value.PSObject.Properties) {
        if ([string]$dependency.Value.type -eq 'Project') { continue }
        $packageName = [string]$dependency.Name
        $packageVersion = [string]$dependency.Value.resolved
        if ([string]::IsNullOrWhiteSpace($packageVersion)) {
            throw "NuGet 锁定条目缺少精确版本：$packageName ($targetName)"
        }
        if ($packageVersions.ContainsKey($packageName) -and $packageVersions[$packageName] -ne $packageVersion) {
            throw "不同目标解析到不同版本，不能安全生成许可材料：$packageName"
        }
        $packageVersions[$packageName] = $packageVersion
    }
}

$components = [System.Collections.Generic.List[object]]::new()
$mitCopyrights = [System.Collections.Generic.List[string]]::new()
$pathSeparators = [char[]]@([char]92, [char]47)
foreach ($packageName in @($packageVersions.Keys | Sort-Object)) {
    $packageVersion = [string]$packageVersions[$packageName]
    $packageId = $packageName.ToLowerInvariant()
    $packageDirectory = Join-Path (Join-Path $globalPackagesPath $packageId) $packageVersion
    if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
        throw "锁定的 NuGet 包没有恢复到全局缓存：$packageName $packageVersion"
    }
    $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
    if ($null -eq $nuspec) {
        throw "NuGet 包缺少 nuspec 许可元数据：$packageName $packageVersion"
    }
    [xml]$nuspecXml = Get-Content -LiteralPath $nuspec.FullName -Raw -Encoding UTF8
    $metadata = $nuspecXml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    if ($null -eq $metadata) { throw "NuGet nuspec 缺少 metadata：$packageName $packageVersion" }
    $licenseNode = $metadata.SelectSingleNode('*[local-name()="license"]')
    $licenseType = ''
    $licenseValue = ''
    if ($null -ne $licenseNode) {
        if ($null -ne $licenseNode.Attributes['type']) { $licenseType = [string]$licenseNode.Attributes['type'].Value }
        $licenseValue = [string]$licenseNode.InnerText
    }
    $copyrightNode = $metadata.SelectSingleNode('*[local-name()="copyright"]')
    $copyright = if ($null -ne $copyrightNode) { [string]$copyrightNode.InnerText } else { '' }
    $projectUrlNode = $metadata.SelectSingleNode('*[local-name()="projectUrl"]')
    $projectUrl = if ($null -ne $projectUrlNode) { [string]$projectUrlNode.InnerText } else { '' }
    if ([string]::IsNullOrWhiteSpace($projectUrl)) {
        $repositoryNode = $metadata.SelectSingleNode('*[local-name()="repository"]')
        if ($null -ne $repositoryNode -and $null -ne $repositoryNode.Attributes['url']) {
            $projectUrl = [string]$repositoryNode.Attributes['url'].Value
        }
    }
    if ([string]::IsNullOrWhiteSpace($licenseType) -or [string]::IsNullOrWhiteSpace($licenseValue)) {
        throw "NuGet 包缺少可审核的 SPDX 许可或许可文件引用：$packageName $packageVersion"
    }

    $componentFolderName = "$packageId-$packageVersion"
    $componentDirectory = Join-Path $licenseOutput $componentFolderName
    New-Item -ItemType Directory -Path $componentDirectory -Force | Out-Null
    $licenseFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse |
        Where-Object { $_.Name -match '^(?i:LICENSE|NOTICE|COPYING|THIRD-PARTY)' })

    if ($licenseType -eq 'expression' -and $licenseValue -eq 'MIT') {
        if (-not [string]::IsNullOrWhiteSpace($copyright) -and -not $mitCopyrights.Contains($copyright)) {
            $mitCopyrights.Add($copyright)
        }
    }
    elseif ($licenseType -eq 'file') {
        $relativeLicense = $licenseValue.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $licenseSource = [IO.Path]::GetFullPath((Join-Path $packageDirectory $relativeLicense))
        $packagePrefix = $packageDirectory.TrimEnd($pathSeparators) + [IO.Path]::DirectorySeparatorChar
        if (-not $licenseSource.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "NuGet 许可文件路径越界：$packageName $licenseValue"
        }
        if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) {
            throw "NuGet 声明的许可文件缺失：$packageName $packageVersion $licenseValue"
        }
        if (-not ($licenseFiles | Where-Object { [string]::Equals($_.FullName, $licenseSource, [StringComparison]::OrdinalIgnoreCase) })) {
            $licenseFiles += Get-Item -LiteralPath $licenseSource
        }
    }
    else {
        if ($licenseFiles.Count -eq 0) {
            throw "未审核的 NuGet 许可表达式且包内没有许可文件：$packageName $packageVersion ($licenseType $licenseValue)"
        }
    }

    foreach ($licenseFile in $licenseFiles) {
        $relative = $licenseFile.FullName.Substring($packageDirectory.TrimEnd($pathSeparators).Length).TrimStart($pathSeparators)
        $destination = Join-Path $componentDirectory $relative
        $destinationParent = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        Copy-Item -LiteralPath $licenseFile.FullName -Destination $destination -Force
    }

    $components.Add([pscustomobject]@{
        Name = $packageName
        Version = $packageVersion
        LicenseType = $licenseType
        License = $licenseValue
        Copyright = $copyright
        ProjectUrl = $projectUrl
        LicenseDirectory = "licenses/$componentFolderName"
    })
}

if ($mitCopyrights.Count -gt 0) {
    $mitText = @(
        'MIT License'
        ''
        ($mitCopyrights | Sort-Object -Unique | ForEach-Object { [string]$_ })
        ''
        'Permission is hereby granted, free of charge, to any person obtaining a copy'
        'of this software and associated documentation files (the "Software"), to deal'
        'in the Software without restriction, including without limitation the rights'
        'to use, copy, modify, merge, publish, distribute, sublicense, and/or sell'
        'copies of the Software, and to permit persons to whom the Software is'
        'furnished to do so, subject to the following conditions:'
        ''
        'The above copyright notice and this permission notice shall be included in all'
        'copies or substantial portions of the Software.'
        ''
        'THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR'
        'IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,'
        'FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE'
        'AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER'
        'LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,'
        'OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE'
        'SOFTWARE.'
    ) -join [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $licenseOutput 'MIT-LICENSE.txt'), $mitText + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

$runtimeComponents = [System.Collections.Generic.List[object]]::new()
foreach ($framework in $frameworks) {
    $frameworkName = [string]$framework.name
    $frameworkVersion = [string]$framework.version
    $packageId = switch ($frameworkName) {
        'Microsoft.NETCore.App' { 'microsoft.netcore.app.runtime.win-x64' }
        'Microsoft.AspNetCore.App' { 'microsoft.aspnetcore.app.runtime.win-x64' }
        'Microsoft.WindowsDesktop.App' { 'microsoft.windowsdesktop.app.runtime.win-x64' }
        default { throw "未识别的自包含运行时，需人工核对许可材料：$frameworkName" }
    }
    $runtimePackage = Join-Path (Join-Path $globalPackagesPath $packageId) $frameworkVersion
    if (-not (Test-Path -LiteralPath $runtimePackage -PathType Container)) {
        throw "找不到与 runtimeconfig 完全匹配的 .NET 运行时包：$packageId $frameworkVersion"
    }
    $runtimeOut = Join-Path $licenseOutput ("$packageId-$frameworkVersion")
    New-Item -ItemType Directory -Path $runtimeOut -Force | Out-Null
    $runtimeFiles = @(Get-ChildItem -LiteralPath $runtimePackage -File |
        Where-Object { $_.Name -match '^(?i:LICENSE|NOTICE|COPYING|THIRD-PARTY)' })
    if (-not ($runtimeFiles | Where-Object { $_.Name -match '^(?i:LICENSE)' })) {
        throw "运行时包缺少 LICENSE 文件：$packageId $frameworkVersion"
    }
    if ($frameworkName -eq 'Microsoft.NETCore.App' -and
        -not ($runtimeFiles | Where-Object { $_.Name -match '^(?i:THIRD-PARTY-NOTICES)' })) {
        throw ".NET Core 运行时包缺少 THIRD-PARTY-NOTICES：$packageId $frameworkVersion"
    }
    foreach ($runtimeFile in $runtimeFiles) {
        Copy-Item -LiteralPath $runtimeFile.FullName -Destination (Join-Path $runtimeOut $runtimeFile.Name) -Force
    }
    $runtimeComponents.Add([pscustomobject]@{ Name = $frameworkName; Version = $frameworkVersion; Package = $packageId })
}

$trustText = Get-Content -LiteralPath $trustPath -Raw -Encoding UTF8
$embeddedMatch = [regex]::Match($trustText, 'const string FileName\s*=\s*"veyon-(\d+\.\d+\.\d+)\.\d+-win64-setup\.exe"')
if (-not $embeddedMatch.Success) {
    throw "无法从 Veyon 固定基线读取内嵌安装器版本：$trustPath"
}
$veyonVersion = $embeddedMatch.Groups[1].Value
$veyonLicensePath = Join-Path $repositoryRoot "packaging/third-party/Veyon-$veyonVersion-COPYING.txt"
if (-not (Test-Path -LiteralPath $veyonLicensePath -PathType Leaf)) {
    throw "内嵌 Veyon $veyonVersion 缺少对应 COPYING；停止生成不完整的分发包。"
}
$veyonLicenseText = Get-Content -LiteralPath $veyonLicensePath -Raw -Encoding UTF8
if ($veyonLicenseText -notmatch 'GNU GENERAL PUBLIC LICENSE' -or $veyonLicenseText -notmatch 'OpenSSL') {
    throw "内嵌 Veyon COPYING 与预期的上游许可文本不符：$veyonLicensePath"
}
$veyonOutput = Join-Path $licenseOutput "Veyon-$veyonVersion-COPYING.txt"
Copy-Item -LiteralPath $veyonLicensePath -Destination $veyonOutput -Force

$noticeLines = [System.Collections.Generic.List[string]]::new()
$noticeLines.Add('Veyon Campus Windows package: third-party software notices')
$noticeLines.Add('')
$noticeLines.Add('These files describe third-party components only. They do not grant a license to Veyon Campus source code; the repository license is a separate owner decision.')
$noticeLines.Add('')
$noticeLines.Add('Locked NuGet dependency graph (net10.0 and net10.0/win-x64):')
foreach ($component in $components) {
    $line = "- $($component.Name) $($component.Version): $($component.LicenseType) $($component.License)"
    if (-not [string]::IsNullOrWhiteSpace($component.Copyright)) { $line += "; $($component.Copyright)" }
    if (-not [string]::IsNullOrWhiteSpace($component.ProjectUrl)) { $line += "; $($component.ProjectUrl)" }
    $noticeLines.Add($line)
    $noticeLines.Add("  License and upstream notices: $($component.LicenseDirectory)/")
}
$noticeLines.Add('')
$noticeLines.Add('Self-contained .NET runtime components:')
foreach ($runtime in $runtimeComponents) {
    $noticeLines.Add("- $($runtime.Name) $($runtime.Version): see licenses/$($runtime.Package)-$($runtime.Version)/")
}
$noticeLines.Add('')
$noticeLines.Add("The package also contains the separate Veyon $veyonVersion installer. Upstream identifies Veyon as GPL-2.0 and its COPYING file contains an OpenSSL linking exception. The corresponding source and distribution arrangement must be reviewed before publishing; this notice file alone does not establish source-code compliance.")
$noticeLines.Add("- Upstream source and license: https://github.com/veyon/veyon/tree/v$veyonVersion")
$noticeLines.Add("- Included license text: licenses/Veyon-$veyonVersion-COPYING.txt")

[IO.File]::WriteAllText((Join-Path $publishDirectory 'THIRD-PARTY-NOTICES.txt'),
    ($noticeLines -join [Environment]::NewLine) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Host "已复制 $($components.Count) 个锁定 NuGet 组件、$($runtimeComponents.Count) 个 .NET runtime 和 Veyon $veyonVersion 许可材料。" -ForegroundColor Green
