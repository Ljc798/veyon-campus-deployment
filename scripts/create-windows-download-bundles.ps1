[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$StudentInstallerPath,
    [Parameter(Mandatory = $true)]
    [string]$TeacherInstallerPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'),
    [switch]$Beta
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -and
    $outputPath -ne $artifactsRoot) {
    throw "Download bundles must be written inside artifacts/: $outputPath"
}

$roles = @(
    [ordered]@{
        Role = 'Student'
        InstallerPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $StudentInstallerPath))
        ExpectedName = "VeyonCampus-Student-Setup-$Version-win-x64.exe"
        FriendlyName = '安装-学生电脑.exe'
        GuideName = '安装前请阅读.md'
        BundleName = "VeyonCampus-Student-Setup-$Version-win-x64.zip"
        Audience = '学生电脑'
    },
    [ordered]@{
        Role = 'Teacher'
        InstallerPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $TeacherInstallerPath))
        ExpectedName = "VeyonCampus-Teacher-Setup-$Version-win-x64.exe"
        FriendlyName = '安装-教师电脑.exe'
        GuideName = '安装前请阅读.md'
        BundleName = "VeyonCampus-Teacher-Setup-$Version-win-x64.zip"
        Audience = '教师电脑'
    }
)

if (-not (Test-Path -LiteralPath $outputPath -PathType Container)) {
    New-Item -Path $outputPath -ItemType Directory -Force | Out-Null
}

$releaseLabel = if ($Beta) { 'Beta 测试包' } else { 'Windows x64 安装包' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($role in $roles) {
    if (-not (Test-Path -LiteralPath $role.InstallerPath -PathType Leaf)) {
        throw "缺少 $($role.Role) 安装器：$($role.InstallerPath)"
    }
    if ([IO.Path]::GetFileName($role.InstallerPath) -cne $role.ExpectedName) {
        throw "$($role.Role) 安装器文件名与版本不符；预期 $($role.ExpectedName)。"
    }

    $zipPath = Join-Path $outputPath $role.BundleName
    if (Test-Path -LiteralPath $zipPath) {
        throw "下载 ZIP 已存在，拒绝覆盖：$zipPath"
    }

    $stagingPath = Join-Path ([IO.Path]::GetTempPath()) ("VeyonCampus-DownloadBundle-" + [guid]::NewGuid().ToString('N'))
    New-Item -Path $stagingPath -ItemType Directory -Force | Out-Null
    try {
        $friendlyInstallerPath = Join-Path $stagingPath $role.FriendlyName
        Copy-Item -LiteralPath $role.InstallerPath -Destination $friendlyInstallerPath
        $readmePath = Join-Path $stagingPath $role.GuideName
        $betaInstructions = if ($Beta) {
            @'

> 这是测试版。请只在可恢复的测试电脑上安装，不要先安装到正式课堂设备或真实学生账户。安装器尚未进行 Authenticode 数字签名，Windows 可能显示未知发布者提示；如果遇到安全拦截，请暂停并联系测试负责人，不要关闭 Windows 安全防护。
'@
        } else { '' }
        $readme = @"
# Veyon Campus $($role.Audience)安装说明

版本：$Version（$releaseLabel）

此 ZIP 只包含两个文件：本说明和适用于**$($role.Audience)**的安装程序。教师电脑请选择教师包，学生电脑请选择学生包。

## 安装步骤

1. 在下载的 ZIP 文件上点击鼠标右键，选择“全部提取”，再打开解压出来的文件夹。
2. 双击“$($role.FriendlyName)”。不要直接在 ZIP 预览窗口里运行安装程序。
3. 按屏幕上的提示完成安装。如果 Windows 询问是否允许更改设备，请核对安装包来源；不确定时选择“否”并联系学校管理员。
4. 安装完成后，从桌面或开始菜单打开 Veyon Campus。
$betaInstructions
如果安装过程中出现与本说明不符的提示，请先停止并联系提供此安装包的管理员。不要关闭杀毒软件，也不要把教师包和学生包装错电脑。
"@
        [IO.File]::WriteAllText($readmePath, $readme.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
        Compress-Archive -LiteralPath @($readmePath, $friendlyInstallerPath) `
            -DestinationPath $zipPath -CompressionLevel NoCompression

        $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $entryNames = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
            $expectedEntries = @($role.GuideName, $role.FriendlyName) | Sort-Object
            if ($entryNames.Count -ne 2 -or
                $entryNames[0] -cne $expectedEntries[0] -or
                $entryNames[1] -cne $expectedEntries[1]) {
                throw "下载 ZIP 的目录结构不符合预期：$zipPath"
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        if (Test-Path -LiteralPath $stagingPath -PathType Container) {
            Remove-Item -LiteralPath $stagingPath -Recurse -Force
        }
    }

    Write-Host "已生成 $($role.Role) 新手下载包：$zipPath"
}
