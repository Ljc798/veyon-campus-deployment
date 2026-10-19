[CmdletBinding()]
param(
    [ValidateSet('StudentSetup', 'TeacherConsole')]
    [string]$Role = 'StudentSetup',
    [string]$OutputDirectory,
    [string]$InstallerPath,
    [string]$ReleasePublicKeyPath,
    [string]$EdgeExtensionId,
    [string]$ZipPath,
    [switch]$EdgeRealtimeGatePassed,
    [switch]$EdgeRealtimeManualTest,
    [switch]$SkipRestore,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [string]::IsNullOrWhiteSpace($EdgeExtensionId) -and $EdgeExtensionId -cnotmatch '^[a-p]{32}$') {
    throw 'EdgeExtensionId 必须是 Microsoft Edge Add-ons 分配的 32 位扩展 ID（字符范围 a-p）。'
}
if ($EdgeRealtimeManualTest -and $EdgeRealtimeGatePassed) {
    throw 'EdgeRealtimeManualTest 是独立的试用构建路径，不能与正式 EdgeRealtimeGatePassed 同时使用。'
}
if ($EdgeRealtimeManualTest) {
    $pilotPublicKeyPath = Join-Path $PSScriptRoot '../specs/edge-realtime-website-policy/pilot-extension-public-key.json'
    if (-not (Test-Path -LiteralPath $pilotPublicKeyPath -PathType Leaf)) {
        throw "找不到手动试用扩展公钥：$pilotPublicKeyPath"
    }
    try {
        $pilotManifest = Get-Content -LiteralPath $pilotPublicKeyPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $pilotExtensionKey = [string]$pilotManifest.key
        $pilotExtensionPublicKeyBytes = [Convert]::FromBase64String($pilotExtensionKey)
    }
    catch {
        throw '手动试用扩展公钥文件格式无效。'
    }
    $pilotKeyHashProvider = [Security.Cryptography.SHA256]::Create()
    try {
        $pilotKeyHash = $pilotKeyHashProvider.ComputeHash($pilotExtensionPublicKeyBytes)
    }
    finally {
        $pilotKeyHashProvider.Dispose()
    }
    $pilotIdAlphabet = 'abcdefghijklmnop'
    $pilotExpectedExtensionId = -join (0..15 | ForEach-Object {
        $byte = [int]$pilotKeyHash[$_]
        $pilotIdAlphabet[[int]($byte -shr 4)]
        $pilotIdAlphabet[[int]($byte -band 15)]
    })
    if ($EdgeExtensionId -and $EdgeExtensionId -cne $pilotExpectedExtensionId) {
        throw "EdgeExtensionId 与手动试用公钥不一致；试用扩展 ID 应为 $pilotExpectedExtensionId。"
    }
    $EdgeExtensionId = $pilotExpectedExtensionId
    Write-Host "Edge 手动试用扩展 ID：$EdgeExtensionId" -ForegroundColor Yellow
}
if ($EdgeRealtimeGatePassed -and ($Role -ne 'TeacherConsole' -or [string]::IsNullOrWhiteSpace($EdgeExtensionId))) {
    throw 'EdgeRealtimeGatePassed 只能用于配置了商店扩展 ID 的 TeacherConsole 包。'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$appProjectPath = Join-Path $repoRoot 'src/VeyonCampus.App/VeyonCampus.App.csproj'
$updateHelperProjectPath = Join-Path $repoRoot 'tools/VeyonCampus.UpdateHelper/VeyonCampus.UpdateHelper.csproj'
$coreProjectPath = Join-Path $repoRoot 'src/VeyonCampus.Core/VeyonCampus.Core.csproj'
$companionProjectPath = Join-Path $repoRoot 'src/VeyonCampus.Companion/VeyonCampus.Companion.csproj'
$companionManifestPath = Join-Path $repoRoot 'src/VeyonCampus.Companion/StudentCompanion.manifest'
$edgePolicyExecutorProjectPath = Join-Path $repoRoot 'src/VeyonCampus.EdgePolicyExecutor/VeyonCampus.EdgePolicyExecutor.csproj'
$edgePolicyExecutorManifestPath = Join-Path $repoRoot 'src/VeyonCampus.EdgePolicyExecutor/EdgePolicyExecutor.manifest'
$trustSourcePath = Join-Path $repoRoot 'src/VeyonCampus.Core/VeyonInstallerTrust.cs'
$resourceVerifierProjectPath = Join-Path $repoRoot 'tools/VerifyEmbeddedResource/VerifyEmbeddedResource.csproj'
$runtimeManifestPath = Join-Path $repoRoot 'packaging/third-party/dotnet-runtime-manifest.json'
$runtimeBootstrapScriptPath = Join-Path $repoRoot 'scripts/Get-DotNetRuntimeInstallers.ps1'
$installerScriptPath = Join-Path $repoRoot $(if ($Role -eq 'StudentSetup') {
    'installer/VeyonCampus-Student.iss'
} else {
    'installer/VeyonCampus-Teacher.iss'
})

function Get-RequiredMatchValue {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Description
    )

    $match = [regex]::Match($Text, $Pattern)
    if (-not $match.Success) {
        throw "无法从项目文件读取 $($Description)。"
    }
    return $match.Groups[1].Value
}

function Resolve-ArtifactPath {
    param([string]$Path, [string]$Description)

    if ([IO.Path]::IsPathRooted($Path)) {
        $fullPath = [IO.Path]::GetFullPath($Path)
    }
    else {
        $fullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
    }

    if (-not $fullPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$($Description) 必须放在仓库的 artifacts 目录中：$fullPath"
    }
    return $fullPath
}

function Assert-NoReparsePointsUnderArtifacts {
    param([string]$Path)

    $relativePath = $Path.Substring($artifactsPrefix.Length)
    $parts = $relativePath -split '[\\/]+'
    $currentPath = $artifactsRoot
    for ($index = 0; $index -lt $parts.Length; $index++) {
        $currentPath = Join-Path $currentPath $parts[$index]
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "打包路径包含符号链接或重解析点，已停止以免写入其他位置：$currentPath"
            }
            if ($index -lt ($parts.Length - 1) -and -not $item.PSIsContainer) {
                throw "打包路径的父级不是文件夹：$currentPath"
            }
        }
    }
}

function Invoke-Dotnet {
    param([string[]]$Arguments)

    Write-Host ("`n> dotnet " + ($Arguments -join ' ')) -ForegroundColor DarkCyan
    & $script:dotnetPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "dotnet 命令失败，退出代码：$exitCode"
    }
}

function Invoke-InnoSetup {
    param([string[]]$Arguments)

    Write-Host ("`n> ISCC " + ($Arguments -join ' ')) -ForegroundColor DarkCyan
    & $script:isccPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Inno Setup 编译失败，退出代码：$exitCode"
    }
}

$appProjectXml = [xml](Get-Content -LiteralPath $appProjectPath -Raw -Encoding UTF8)
$appVersion = [string](@($appProjectXml.Project.PropertyGroup | ForEach-Object { $_.Version } |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1)[0])
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    throw '无法从 VeyonCampus.App.csproj 读取应用版本。'
}

if ($Role -eq 'StudentSetup') {
    $studentInstallerTemplate = Get-Content -LiteralPath $installerScriptPath -Raw -Encoding UTF8
    if ($studentInstallerTemplate -notmatch '(?im)^\s*CloseApplications=no\s*$' -or
        $studentInstallerTemplate -notmatch '(?im)^\s*RestartApplications=no\s*$' -or
        $studentInstallerTemplate -notmatch '(?im)^\s*function PrepareToInstall\(var NeedsRestart: Boolean\): String;\s*$' -or
        $studentInstallerTemplate -notmatch '--stop-installed-role-processes StudentSetup') {
        throw 'StudentSetup 安装器必须在安装前静默停止已安装的学生端进程，并避免 Windows 重启应用提示。'
    }
    if ($studentInstallerTemplate -notmatch '(?im)^\s*function InitializeUninstall\(\): Boolean;\s*$' -or
        $studentInstallerTemplate -notmatch '--uninstall-student-runtime') {
        throw 'StudentSetup 卸载器必须先调用受控清理入口；清理失败时不得删除 Agent 和策略恢复文件。'
    }
    if ($studentInstallerTemplate -match '(?im)^\s*Source:.*Flags:.*\brestartreplace\b') {
        throw 'StudentSetup 安装器不得将锁定文件延迟到重启时替换；更新事务必须立即失败并回滚。'
    }
    if ($studentInstallerTemplate -notmatch '(?im)^\s*Filename:\s*"\{app\}\\StudentCompanion\\VeyonCampus\.StudentCompanion\.exe";.*Parameters:\s*"--startup";.*Flags:.*\brunasoriginaluser\b.*\bskipifsilent\b' -or
        $studentInstallerTemplate -notmatch '(?im)^\s*Filename:\s*"\{app\}\\EdgePolicyExecutor\\VeyonCampus\.EdgePolicyExecutor\.exe";.*Parameters:\s*"--startup";.*Flags:.*\brunasoriginaluser\b.*\bskipifsilent\b') {
        throw 'StudentSetup 安装器必须以原始非提升用户令牌启动桌面 Companion 和独立 Edge 执行器。'
    }
    if ($studentInstallerTemplate -notmatch '(?im)^\s*Filename:\s*"\{app\}\\VeyonCampus\.StudentSetup\.exe";.*Flags:.*\bpostinstall\b.*\bskipifsilent\b' -or
        $studentInstallerTemplate -match '(?im)^\s*Filename:\s*"\{app\}\\VeyonCampus\.StudentSetup\.exe";.*Flags:.*\brunasoriginaluser\b') {
        throw 'StudentSetup 管理界面必须继承提升后的安装器令牌，不能用原始非提升用户令牌启动。'
    }
    if ($studentInstallerTemplate -notmatch '(?im)^\s*Source:\s*"EdgePolicyExecutor\.task\.xml";.*Flags:.*\bdontcopy\b') {
        throw 'StudentSetup 安装器必须嵌入 Edge 执行器登录/崩溃恢复任务定义。'
    }
    $edgeExecutorTaskPath = Join-Path $repoRoot 'installer/EdgePolicyExecutor.task.xml'
    if (-not (Test-Path -LiteralPath $edgeExecutorTaskPath -PathType Leaf)) {
        throw 'StudentSetup 缺少 Edge 策略执行器登录/崩溃恢复任务定义。'
    }
    try {
        [xml]$edgeExecutorTask = Get-Content -LiteralPath $edgeExecutorTaskPath -Raw -Encoding UTF8
        $taskNamespaces = [System.Xml.XmlNamespaceManager]::new($edgeExecutorTask.NameTable)
        $taskNamespaces.AddNamespace('task', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
        $groupId = $edgeExecutorTask.SelectSingleNode('/task:Task/task:Principals/task:Principal/task:GroupId', $taskNamespaces)
        $logonType = $edgeExecutorTask.SelectSingleNode('/task:Task/task:Principals/task:Principal/task:LogonType', $taskNamespaces)
        $runLevel = $edgeExecutorTask.SelectSingleNode('/task:Task/task:Principals/task:Principal/task:RunLevel', $taskNamespaces)
        $restartCount = $edgeExecutorTask.SelectSingleNode('/task:Task/task:Settings/task:RestartOnFailure/task:Count', $taskNamespaces)
        $restartInterval = $edgeExecutorTask.SelectSingleNode('/task:Task/task:Settings/task:RestartOnFailure/task:Interval', $taskNamespaces)
        if ($groupId.InnerText -ne 'S-1-5-32-545' -or $logonType.InnerText -ne 'Group' -or
            $runLevel.InnerText -ne 'LeastPrivilege' -or $restartCount.InnerText -ne '3' -or
            $restartInterval.InnerText -ne 'PT1M' -or
            $studentInstallerTemplate -notmatch 'RemoveEdgePolicyExecutorTask\(' -or
            $studentInstallerTemplate -notmatch 'RegisterEdgePolicyExecutorTask\(') {
            throw 'Edge 执行器任务必须在普通用户登录会话启动、最小权限运行，并配置有限崩溃恢复和卸载清理。'
        }
    }
    catch {
        throw "Edge 执行器任务定义无效：$($_.Exception.Message)"
    }
    $companionProgramText = Get-Content -LiteralPath (Join-Path $repoRoot 'src/VeyonCampus.Companion/Program.cs') -Raw -Encoding UTF8
    if ($companionProgramText -notmatch 'RegisterApplicationRestart' -or
        $companionProgramText -notmatch 'RegisterApplicationRestart\("--startup"') {
        throw 'Student Companion 必须注册 Windows 安装重启恢复，并以后台启动参数重新打开。'
    }
}
else {
    $teacherInstallerTemplate = Get-Content -LiteralPath $installerScriptPath -Raw -Encoding UTF8
    if ($teacherInstallerTemplate -notmatch '(?im)^\s*CloseApplications=no\s*$' -or
        $teacherInstallerTemplate -notmatch '(?im)^\s*RestartApplications=no\s*$') {
        throw 'TeacherConsole 安装器必须避免 Windows 重启应用提示，并在完成后保持应用关闭。'
    }
    if ($teacherInstallerTemplate -notmatch '(?im)^\s*function PrepareToInstall\(var NeedsRestart: Boolean\): String;\s*$' -or
        $teacherInstallerTemplate -notmatch '--stop-installed-role-processes TeacherConsole') {
        throw 'TeacherConsole 安装器必须在安装前按程序安装路径静默停止教师端进程。'
    }
    $requiredFirewallPatterns = @(
        'procedure ConfigureMobileFirewallRule',
        'set rule name=',
        'add rule name=',
        'remoteip=LocalSubnet profile=any enable=yes',
        "ConfigureMobileFirewallRule('VeyonCampusTeacherMobileControl', '39176')",
        "ConfigureMobileFirewallRule('VeyonCampusTeacherMobileBootstrap', '39177')"
    )
    foreach ($pattern in $requiredFirewallPatterns) {
        if ($teacherInstallerTemplate -notmatch [regex]::Escape($pattern)) {
            throw "TeacherConsole 防火墙规则缺少可更新/首次新增的本地子网配置：$pattern"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ReleasePublicKeyPath)) {
    $ReleasePublicKeyPath = [Environment]::GetEnvironmentVariable('VEYONCAMPUS_RELEASE_PUBLIC_KEY_PATH')
}
$releasePublicKeyFullPath = $null
$releasePublicKeyFingerprint = $null
if (-not [string]::IsNullOrWhiteSpace($ReleasePublicKeyPath)) {
    if ([IO.Path]::IsPathRooted($ReleasePublicKeyPath)) {
        $releasePublicKeyFullPath = [IO.Path]::GetFullPath($ReleasePublicKeyPath)
    }
    else {
        $releasePublicKeyFullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $ReleasePublicKeyPath))
    }
    if (-not (Test-Path -LiteralPath $releasePublicKeyFullPath -PathType Leaf)) {
        throw "找不到 Developer Release PEM 公钥：$releasePublicKeyFullPath"
    }
    $releasePublicKeyText = Get-Content -LiteralPath $releasePublicKeyFullPath -Raw -Encoding UTF8
    if ($releasePublicKeyText.IndexOf('PRIVATE KEY', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw '安装器只能嵌入 Developer Release 公钥，绝不能嵌入发布私钥。'
    }
    $nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $nodeCommand) {
        throw 'Node.js is required to validate the Developer Release RSA public key.'
    }
    $env:VEYONCAMPUS_RELEASE_KEY_TO_VALIDATE = $releasePublicKeyFullPath
    $nodeSource = @'
const crypto = require('node:crypto');
const fs = require('node:fs');
const key = crypto.createPublicKey(fs.readFileSync(process.env.VEYONCAMPUS_RELEASE_KEY_TO_VALIDATE));
const bits = key.asymmetricKeyDetails?.modulusLength ?? 0;
if (key.asymmetricKeyType !== 'rsa' || bits < 2048 || bits > 4096) {
  throw new Error('Developer Release key must be RSA 2048–4096 bits.');
}
console.log(crypto.createHash('sha256')
  .update(key.export({ type: 'spki', format: 'der' }))
  .digest('hex').toUpperCase());
'@
    try {
        $nodeValidationOutput = @(& $nodeCommand.Source -e $nodeSource)
        if ($LASTEXITCODE -ne 0) {
            throw 'Developer Release RSA 公钥校验失败。'
        }
        if ($nodeValidationOutput.Count -ne 1 -or
            ([string]$nodeValidationOutput[0]) -notmatch '^[0-9A-Fa-f]{64}$') {
            throw 'Developer Release RSA 公钥指纹输出无效。'
        }
        $releasePublicKeyFingerprint = ([string]$nodeValidationOutput[0]).Trim().ToUpperInvariant()
        Write-Host "Validated Developer Release public key (SPKI SHA-256 $releasePublicKeyFingerprint)."
    }
    finally {
        Remove-Item Env:VEYONCAMPUS_RELEASE_KEY_TO_VALIDATE -ErrorAction SilentlyContinue
    }
}

function ConvertTo-InnoPascalStringLiteral {
    param([string]$Value)

    return "'" + $Value.Replace("'", "''") + "'"
}

$coreProjectText = Get-Content -LiteralPath $coreProjectPath -Raw -Encoding UTF8
$trustSourceText = Get-Content -LiteralPath $trustSourcePath -Raw -Encoding UTF8
$installerRelativePath = Get-RequiredMatchValue $coreProjectText '<EmbeddedResource\s+Include="([^"]+)"' 'Veyon 安装器嵌入路径'
$resourceName = Get-RequiredMatchValue $coreProjectText 'LogicalName="([^"]+)"' 'Veyon 安装器资源名'
$expectedFileName = Get-RequiredMatchValue $trustSourceText 'const string FileName\s*=\s*"([^"]+)"' 'Veyon 安装器文件名'
$expectedSizeText = Get-RequiredMatchValue $trustSourceText 'const long FileSize\s*=\s*([0-9_]+)' 'Veyon 安装器大小'
$expectedSize = [long]($expectedSizeText -replace '_', '')
$expectedSha256 = Get-RequiredMatchValue $trustSourceText 'const string Sha256\s*=\s*"([0-9A-Fa-f]{64})"' 'Veyon 安装器 SHA-256'
$embeddedVeyonInstallerPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $coreProjectPath) $installerRelativePath))
if ([IO.Path]::GetFileName($embeddedVeyonInstallerPath) -ne $expectedFileName) {
    throw "嵌入的安装器文件名与应用固定基线不一致：$embeddedVeyonInstallerPath"
}
if (-not (Test-Path -LiteralPath $embeddedVeyonInstallerPath -PathType Leaf)) {
    throw "找不到 Veyon 安装器：$embeddedVeyonInstallerPath"
}
$installerInfo = Get-Item -LiteralPath $embeddedVeyonInstallerPath
$installerHash = (Get-FileHash -LiteralPath $embeddedVeyonInstallerPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($installerInfo.Length -ne $expectedSize -or $installerHash -ne $expectedSha256.ToUpperInvariant()) {
    throw "Veyon 安装器与应用内固定基线不符。期望大小/SHA-256：$expectedSize / $expectedSha256；实际：$($installerInfo.Length) / $installerHash"
}
Write-Host "已核对内嵌 Veyon $expectedFileName（$expectedSize 字节，SHA-256 $installerHash）。" -ForegroundColor Green

$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
$dotnetCandidates = @(
    $env:VEYONCAMPUS_DOTNET_PATH,
    (Join-Path $programFiles 'dotnet/dotnet.exe'),
    $(if ($programFilesX86) { Join-Path $programFilesX86 'dotnet/dotnet.exe' }),
    $(if (Get-Command dotnet -ErrorAction SilentlyContinue) { (Get-Command dotnet).Source })
) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique

$script:dotnetPath = $null
$sdkVersion = $null
foreach ($candidate in $dotnetCandidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    $versionOutput = & $candidate --version 2>&1
    $versionExitCode = $LASTEXITCODE
    $candidateVersion = [string]($versionOutput | Select-Object -First 1)
    if ($versionExitCode -eq 0 -and $candidateVersion -match '^10\.') {
        $script:dotnetPath = $candidate
        $sdkVersion = $candidateVersion
        break
    }
}
if (-not $script:dotnetPath) {
    throw '需要 .NET 10 SDK 才能打包。请先安装 .NET 10 SDK，然后重新运行此脚本。'
}
Write-Host ".NET SDK：$sdkVersion ($script:dotnetPath)" -ForegroundColor Green

if (-not (Test-Path -LiteralPath $artifactsRoot -PathType Container)) {
    New-Item -Path $artifactsRoot -ItemType Directory -Force | Out-Null
}
$artifactsItem = Get-Item -LiteralPath $artifactsRoot -Force
if (($artifactsItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "artifacts 目录不能是符号链接或重解析点：$artifactsRoot"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $roleSlug = if ($Role -eq 'StudentSetup') { 'student-setup' } else { 'teacher-console' }
    $OutputDirectory = Join-Path $artifactsRoot "windows-x64-v$appVersion-$roleSlug"
}
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $roleName = if ($Role -eq 'StudentSetup') { 'Student' } else { 'Teacher' }
    $InstallerPath = Join-Path $artifactsRoot "VeyonCampus-$roleName-Setup-$appVersion-win-x64.exe"
}
$publishDirectory = Resolve-ArtifactPath $OutputDirectory '发布文件夹'
$zipFile = $null
if (-not [string]::IsNullOrWhiteSpace($ZipPath)) {
    $zipFile = Resolve-ArtifactPath $ZipPath 'ZIP 文件'
}
$installerFile = Resolve-ArtifactPath $InstallerPath 'Inno Setup 安装器'
$runtimeManifest = Get-Content -LiteralPath $runtimeManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($runtimeManifest.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$runtimeManifest.version)) {
    throw '固定的 .NET 运行时来源清单无效。'
}
$runtimeVersion = [string]$runtimeManifest.version
$runtimeInstallerDirectory = Join-Path $artifactsRoot "tooling/dotnet-runtime/$runtimeVersion"
$dotnetRuntimeInstallerPath = Join-Path $runtimeInstallerDirectory ([string]$runtimeManifest.runtime.fileName)
$aspnetCoreRuntimeInstallerPath = Join-Path $runtimeInstallerDirectory ([string]$runtimeManifest.aspnetCoreRuntime.fileName)
if ($zipFile -and $zipFile.StartsWith($publishDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ZIP 文件不能放在发布文件夹内部。'
}
if ($installerFile.StartsWith($publishDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
    ($zipFile -and [string]::Equals($installerFile, $zipFile, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Inno Setup 安装器必须放在发布文件夹之外，且不能与 ZIP 路径相同。'
}
if ([IO.Path]::GetExtension($installerFile) -ine '.exe') {
    throw "Inno Setup 安装器路径必须以 .exe 结尾：$installerFile"
}
Assert-NoReparsePointsUnderArtifacts $publishDirectory
if ($zipFile) {
    Assert-NoReparsePointsUnderArtifacts $zipFile
}
Assert-NoReparsePointsUnderArtifacts $installerFile

if ($Clean) {
    $cleanupPaths = @($publishDirectory, $installerFile)
    if ($zipFile) {
        $cleanupPaths += $zipFile
    }
    foreach ($path in $cleanupPaths) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}
elseif ((Test-Path -LiteralPath $publishDirectory) -or ($zipFile -and (Test-Path -LiteralPath $zipFile)) -or
    (Test-Path -LiteralPath $installerFile)) {
    throw "输出位置已存在。若要替换本脚本生成的同版本产物，请加 -Clean。`n文件夹：$publishDirectory`n安装器：$installerFile"
}

if (-not (Test-Path -LiteralPath $runtimeBootstrapScriptPath -PathType Leaf)) {
    throw "找不到 .NET 运行时获取脚本：$runtimeBootstrapScriptPath"
}
$runtimeParameters = @{
    Version = $runtimeVersion
    OutputDirectory = $runtimeInstallerDirectory
}
if ($Role -eq 'TeacherConsole') {
    $runtimeParameters.IncludeAspNetCoreRuntime = $true
}
& $runtimeBootstrapScriptPath @runtimeParameters
if (-not (Test-Path -LiteralPath $dotnetRuntimeInstallerPath -PathType Leaf) -or
    ($Role -eq 'TeacherConsole' -and -not (Test-Path -LiteralPath $aspnetCoreRuntimeInstallerPath -PathType Leaf))) {
    throw '当前角色所需的 .NET 运行时安装器未准备完整。'
}

if (-not $SkipRestore) {
    if ($Role -eq 'StudentSetup') {
        $companionRestoreArguments = @('restore', $companionProjectPath, '-r', 'win-x64', '--locked-mode',
            '-p:NuGetAudit=false', '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json',
            '-p:VeyonCampusIncludeInstaller=false')
        if ($releasePublicKeyFullPath) {
            $companionRestoreArguments += "-p:VeyonCampusReleasePublicKeyPath=$releasePublicKeyFullPath"
        }
        Invoke-Dotnet $companionRestoreArguments

        $executorRestoreArguments = @('restore', $edgePolicyExecutorProjectPath, '-r', 'win-x64', '--locked-mode',
            '-p:NuGetAudit=false', '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json',
            '-p:VeyonCampusIncludeInstaller=false')
        Invoke-Dotnet $executorRestoreArguments
    }

    $restoreArguments = @('restore', $appProjectPath, '-r', 'win-x64', '--locked-mode',
        '-p:NuGetAudit=false', "-p:VeyonCampusRole=$Role", '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json')
    if ($releasePublicKeyFullPath) {
        $restoreArguments += "-p:VeyonCampusReleasePublicKeyPath=$releasePublicKeyFullPath"
    }
    if ($EdgeExtensionId) { $restoreArguments += "-p:VeyonCampusEdgeExtensionId=$EdgeExtensionId" }
    if ($EdgeRealtimeGatePassed) { $restoreArguments += '-p:VeyonCampusEdgeRealtimeGatePassed=true' }
    if ($EdgeRealtimeManualTest -and $Role -eq 'TeacherConsole') {
        $restoreArguments += '-p:VeyonCampusEdgeRealtimePilot=true'
    }
    Invoke-Dotnet $restoreArguments
}

$publishArguments = @('publish', $appProjectPath, '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'false', '--no-restore', "-p:VeyonCampusRole=$Role",
    '-p:AppHostDotNetSearch=Global', '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json',
    '-o', $publishDirectory)
if ($releasePublicKeyFullPath) {
    $publishArguments += "-p:VeyonCampusReleasePublicKeyPath=$releasePublicKeyFullPath"
}
if ($EdgeExtensionId) { $publishArguments += "-p:VeyonCampusEdgeExtensionId=$EdgeExtensionId" }
if ($EdgeRealtimeGatePassed) { $publishArguments += '-p:VeyonCampusEdgeRealtimeGatePassed=true' }
if ($EdgeRealtimeManualTest -and $Role -eq 'TeacherConsole') {
    $publishArguments += '-p:VeyonCampusEdgeRealtimePilot=true'
}
Invoke-Dotnet $publishArguments

if ($EdgeRealtimeManualTest -and $Role -eq 'StudentSetup') {
    $publishedExtensionManifestPath = Join-Path $publishDirectory 'EdgeExtension/manifest.json'
    if (-not (Test-Path -LiteralPath $publishedExtensionManifestPath -PathType Leaf)) {
        throw "学生试用包发布结果缺少 Edge 扩展清单：$publishedExtensionManifestPath"
    }
    $publishedExtensionManifest = Get-Content -LiteralPath $publishedExtensionManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $publishedExtensionManifest | Add-Member -NotePropertyName key -NotePropertyValue $pilotExtensionKey -Force
    [IO.File]::WriteAllText($publishedExtensionManifestPath,
        (ConvertTo-Json -InputObject $publishedExtensionManifest -Depth 20) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}
if ($EdgeRealtimeManualTest) {
    $pilotGuidePath = Join-Path $repoRoot 'specs/edge-realtime-website-policy/manual-test-guide.md'
    if (-not (Test-Path -LiteralPath $pilotGuidePath -PathType Leaf)) {
        throw "找不到 Edge 手动试用说明：$pilotGuidePath"
    }
    $pilotGuide = (Get-Content -LiteralPath $pilotGuidePath -Raw -Encoding UTF8).Replace('{EXTENSION_ID}', $EdgeExtensionId)
    [IO.File]::WriteAllText((Join-Path $publishDirectory 'EDGE-REALTIME-MANUAL-TEST.md'), $pilotGuide,
        [Text.UTF8Encoding]::new($false))
}

$noticeScriptPath = Join-Path $repoRoot 'scripts/Copy-WindowsThirdPartyNotices.ps1'
if (-not (Test-Path -LiteralPath $noticeScriptPath -PathType Leaf)) {
    throw "找不到第三方许可材料打包脚本：$noticeScriptPath"
}
& $noticeScriptPath -PublishDirectory $publishDirectory -Role $Role -RepositoryRoot $repoRoot

$appExeName = if ($Role -eq 'StudentSetup') { 'VeyonCampus.StudentSetup.exe' } else { 'VeyonCampus.Teacher.exe' }
$appExePath = Join-Path $publishDirectory $appExeName
if (-not (Test-Path -LiteralPath $appExePath -PathType Leaf)) {
    throw "发布结果中缺少角色入口程序 $appExeName：$publishDirectory"
}
$runtimeConfigPath = Join-Path $publishDirectory ([IO.Path]::ChangeExtension($appExeName, '.runtimeconfig.json'))
if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
    throw "框架依赖发布结果缺少 runtimeconfig.json：$runtimeConfigPath"
}
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$includedFrameworksProperty = $runtimeConfig.runtimeOptions.PSObject.Properties['includedFrameworks']
if ($null -ne $includedFrameworksProperty -and @($includedFrameworksProperty.Value).Count -gt 0) {
    throw 'Windows 安装包必须使用框架依赖发布，不能把 .NET 运行时重复放进每个组件目录。'
}
$expectedFrameworks = @('Microsoft.NETCore.App')
if ($Role -eq 'TeacherConsole') { $expectedFrameworks += 'Microsoft.AspNetCore.App' }
$frameworksProperty = $runtimeConfig.runtimeOptions.PSObject.Properties['frameworks']
if ($null -eq $frameworksProperty) {
    $frameworksProperty = $runtimeConfig.runtimeOptions.PSObject.Properties['framework']
}
$actualFrameworks = @()
if ($null -ne $frameworksProperty) {
    $actualFrameworks = @($frameworksProperty.Value | ForEach-Object { [string]$_.name } | Sort-Object -Unique)
}
if (($actualFrameworks -join '|') -cne (($expectedFrameworks | Sort-Object -Unique) -join '|')) {
    throw "应用所需 .NET framework 与安装器预置运行时不一致：$($actualFrameworks -join ', ')"
}
$workerExePath = Join-Path $publishDirectory 'Worker/VeyonCampus.Worker.exe'
if (-not (Test-Path -LiteralPath $workerExePath -PathType Leaf)) {
    throw "发布结果中缺少匹配的提权 Worker：$workerExePath"
}
$appAssemblyPath = [IO.Path]::ChangeExtension($appExePath, '.dll')
$workerAssemblyPath = [IO.Path]::ChangeExtension($workerExePath, '.dll')
if (-not (Test-Path -LiteralPath $appAssemblyPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $workerAssemblyPath -PathType Leaf)) {
    throw '发布结果缺少 UI 或提权 Worker 托管程序集。'
}
$appProductVersion = [Reflection.AssemblyName]::GetAssemblyName($appAssemblyPath).Version.ToString(3)
$workerProductVersion = [Reflection.AssemblyName]::GetAssemblyName($workerAssemblyPath).Version.ToString(3)
if ([string]::IsNullOrWhiteSpace($appProductVersion) -or $appProductVersion -ne $workerProductVersion -or
    $appProductVersion -ne $appVersion) {
    throw "发布文件版本不一致；应用=$appProductVersion，Worker=$workerProductVersion，项目=$appVersion。"
}

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
if ($Role -eq 'StudentSetup') {
    $teacherArtifacts = @($publishedFiles | Where-Object { $_.Name -match '^VeyonCampus\.Teacher(?:\.|$)' })
    if ($teacherArtifacts.Count -gt 0) {
        throw "学生部署包混入教师端产物：$($teacherArtifacts[0].FullName)"
    }

    $companionExePath = Join-Path $publishDirectory 'StudentCompanion/VeyonCampus.StudentCompanion.exe'
    $companionAssemblyPath = Join-Path $publishDirectory 'StudentCompanion/VeyonCampus.StudentCompanion.dll'
    if (-not (Test-Path -LiteralPath $companionExePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $companionAssemblyPath -PathType Leaf)) {
        throw "学生部署包缺少独立 Student Companion 程序：$companionExePath"
    }
    $edgePolicyExecutorExePath = Join-Path $publishDirectory 'EdgePolicyExecutor/VeyonCampus.EdgePolicyExecutor.exe'
    $edgePolicyExecutorAssemblyPath = Join-Path $publishDirectory 'EdgePolicyExecutor/VeyonCampus.EdgePolicyExecutor.dll'
    if (-not (Test-Path -LiteralPath $edgePolicyExecutorExePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $edgePolicyExecutorAssemblyPath -PathType Leaf)) {
        throw "学生部署包缺少独立 Edge 策略执行器：$edgePolicyExecutorExePath"
    }
    $edgeNativeHostPath = Join-Path $publishDirectory 'EdgeNativeHost/VeyonCampus.EdgeNativeHost.exe'
    if (-not (Test-Path -LiteralPath $edgeNativeHostPath -PathType Leaf)) {
        throw "发布结果中缺少 Edge Native Messaging Host：$edgeNativeHostPath"
    }
    foreach ($extensionFile in @('manifest.json', 'background.js', 'policyCompiler.js')) {
        $extensionPath = Join-Path $publishDirectory "EdgeExtension/$extensionFile"
        if (-not (Test-Path -LiteralPath $extensionPath -PathType Leaf)) {
            throw "发布结果中缺少 Edge 实时策略扩展文件：$extensionPath"
        }
    }
    $companionVersion = [Reflection.AssemblyName]::GetAssemblyName($companionAssemblyPath).Version.ToString(3)
    if ($companionVersion -ne $appVersion) {
        throw "Student Companion 版本与 StudentSetup 不一致：Companion=$companionVersion，StudentSetup=$appVersion。"
    }
    $edgePolicyExecutorVersion = [Reflection.AssemblyName]::GetAssemblyName($edgePolicyExecutorAssemblyPath).Version.ToString(3)
    if ($edgePolicyExecutorVersion -ne $appVersion) {
        throw "Edge 策略执行器版本与 StudentSetup 不一致：Executor=$edgePolicyExecutorVersion，StudentSetup=$appVersion。"
    }
    $companionProjectText = Get-Content -LiteralPath $companionProjectPath -Raw -Encoding UTF8
    $companionManifestText = Get-Content -LiteralPath $companionManifestPath -Raw -Encoding UTF8
    if ($companionProjectText -notmatch '<ApplicationManifest[^>]*>StudentCompanion\.manifest</ApplicationManifest>' -or
        $companionManifestText -notmatch '<requestedExecutionLevel\s+level="asInvoker"\s+uiAccess="false"\s*/>') {
        throw 'Student Companion 必须使用 asInvoker 清单并按当前用户身份运行。'
    }
    $edgePolicyExecutorProjectText = Get-Content -LiteralPath $edgePolicyExecutorProjectPath -Raw -Encoding UTF8
    $edgePolicyExecutorManifestText = Get-Content -LiteralPath $edgePolicyExecutorManifestPath -Raw -Encoding UTF8
    if ($edgePolicyExecutorProjectText -notmatch '<ApplicationManifest[^>]*>EdgePolicyExecutor\.manifest</ApplicationManifest>' -or
        $edgePolicyExecutorManifestText -notmatch '<requestedExecutionLevel\s+level="asInvoker"\s+uiAccess="false"\s*/>') {
        throw 'Edge 策略执行器必须使用 asInvoker 清单，不能提升权限操作学生 Edge。'
    }
}

else {
    $studentArtifacts = @($publishedFiles | Where-Object { $_.Name -match '^VeyonCampus\.StudentSetup(?:\.|$)' })
    $agentDirectory = Join-Path $publishDirectory 'WebsitePolicyAgent'
    $companionDirectory = Join-Path $publishDirectory 'StudentCompanion'
    $edgePolicyExecutorDirectory = Join-Path $publishDirectory 'EdgePolicyExecutor'
    $companionArtifacts = @($publishedFiles | Where-Object { $_.Name -match '^VeyonCampus\.StudentCompanion(?:\.|$)' })
    if ($studentArtifacts.Count -gt 0 -or (Test-Path -LiteralPath $agentDirectory) -or
        (Test-Path -LiteralPath $companionDirectory) -or (Test-Path -LiteralPath $edgePolicyExecutorDirectory) -or
        $companionArtifacts.Count -gt 0) {
        throw '教师控制台包混入学生部署程序、Student Companion、Edge 策略执行器或网站策略 Agent。'
    }
}

# Release symbols are useful for debugging, but are not required at runtime. Native
# Avalonia dependencies can contribute very large PDB files, so keep them out of
# both the installed payload and the StudentSetup bundle manifest.
$debugSymbolFiles = @($publishedFiles | Where-Object { $_.Extension -ieq '.pdb' })
if ($debugSymbolFiles.Count -gt 0) {
    $debugSymbolBytes = ($debugSymbolFiles | Measure-Object -Property Length -Sum).Sum
    foreach ($debugSymbolFile in $debugSymbolFiles) {
        Remove-Item -LiteralPath $debugSymbolFile.FullName -Force
    }
    Write-Host ("已从安装包移除 {0} 个调试符号文件（{1:N2} MiB）。" -f
        $debugSymbolFiles.Count, ($debugSymbolBytes / 1MB)) -ForegroundColor Green
}

if (-not $releasePublicKeyFullPath) {
    Write-Host '未配置 Developer Release PEM 公钥；此安装器会安全停用应用更新。' -ForegroundColor Yellow
}

$product = if ($Role -eq 'StudentSetup') { 'VeyonCampus.StudentSetup' } else { 'VeyonCampus.TeacherConsole' }
$roleInfoPath = Join-Path $publishDirectory 'veyon-campus-role.json'
[IO.File]::WriteAllText($roleInfoPath,
    (ConvertTo-Json -InputObject ([ordered]@{ schemaVersion = 1; role = $Role; product = $product; version = $appVersion }) -Depth 3) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

if ($Role -eq 'StudentSetup') {
    $agentExePath = Join-Path $publishDirectory 'WebsitePolicyAgent/VeyonCampus.Agent.exe'
    if (-not (Test-Path -LiteralPath $agentExePath -PathType Leaf)) {
        throw "发布结果中缺少独立后台代理 VeyonCampus.Agent.exe：$agentExePath"
    }

    $wallpaperAssetPath = Join-Path $publishDirectory 'WebsitePolicyAgent/Assets/StudentDefaultWallpaper-v1.jpg'
    if (-not (Test-Path -LiteralPath $wallpaperAssetPath -PathType Leaf)) {
        throw "Student Agent 发布结果缺少统一壁纸资源：$wallpaperAssetPath"
    }
    $wallpaperAssetSha256 = (Get-FileHash -LiteralPath $wallpaperAssetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($wallpaperAssetSha256 -ne '2cbcf8ce55cf0694193f6e7d06754b55f659b6673fa517673dc277ec6dd0e33e') {
        throw "Student Agent 壁纸资源 SHA-256 不匹配：$wallpaperAssetPath"
    }

    $bundleFiles = @(
        Get-ChildItem -LiteralPath $publishDirectory -File -Recurse |
            Where-Object { $_.Name -notin @('veyon-campus-student-setup.json', '.veyon-campus-student-setup') } |
            ForEach-Object {
                [ordered]@{
                    path = $_.FullName.Substring($publishDirectory.TrimEnd('\', '/').Length + 1).Replace('\', '/')
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
                }
            }
    )
    $bundleManifest = [ordered]@{
        schemaVersion = 1
        product = 'VeyonCampus.StudentSetup'
        files = $bundleFiles
    }
    $bundleManifestPath = Join-Path $publishDirectory 'veyon-campus-student-setup.json'
    [IO.File]::WriteAllText($bundleManifestPath,
        (ConvertTo-Json -InputObject $bundleManifest -Depth 5) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $publishDirectory '.veyon-campus-student-setup'),
        'VeyonCampusStudentSetupBundle-v1', [Text.UTF8Encoding]::new($false))
}

$coreDllPath = Join-Path $publishDirectory 'VeyonCampus.Core.dll'
if (-not (Test-Path -LiteralPath $coreDllPath -PathType Leaf)) {
    throw "发布结果中缺少嵌入 Veyon 安装器的 VeyonCampus.Core.dll：$publishDirectory"
}
$coreAssemblyPaths = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse -Filter 'VeyonCampus.Core.dll')
if ($coreAssemblyPaths.Count -eq 0) {
    throw "发布结果中缺少嵌入 Veyon 安装器的 VeyonCampus.Core.dll：$publishDirectory"
}

$releasePublicKeyResourceName = 'VeyonCampus.Core.ApplicationReleasePublicKey.pem'
$releasePublicKeyBytes = if ($releasePublicKeyFullPath) { (Get-Item -LiteralPath $releasePublicKeyFullPath).Length } else { 0 }
$releasePublicKeySha256 = if ($releasePublicKeyFullPath) {
    (Get-FileHash -LiteralPath $releasePublicKeyFullPath -Algorithm SHA256).Hash.ToUpperInvariant()
} else { $null }
foreach ($coreAssembly in $coreAssemblyPaths) {
    $verifyKeyArguments = @('run', '--project', $resourceVerifierProjectPath, '-c', 'Release',
        '-p:NuGetAudit=false')
    if ($SkipRestore) {
        $verifyKeyArguments += '--no-restore'
    }
    $verifyKeyArguments += '--'
    if ($releasePublicKeyFullPath) {
        $verifyKeyArguments += @($coreAssembly.FullName, [string]$releasePublicKeyBytes,
            $releasePublicKeySha256, $releasePublicKeyResourceName)
    }
    else {
        $verifyKeyArguments += @('--expect-missing', $coreAssembly.FullName, $releasePublicKeyResourceName)
    }
    Invoke-Dotnet $verifyKeyArguments
}

$verifyEmbeddedResourceArguments = @('run', '--project', $resourceVerifierProjectPath, '-c', 'Release',
    '-p:NuGetAudit=false')
if ($SkipRestore) {
    $verifyEmbeddedResourceArguments += '--no-restore'
}
$verifyEmbeddedResourceArguments += @('--', $coreDllPath, [string]$expectedSize,
    $expectedSha256.ToUpperInvariant(), $resourceName)
Invoke-Dotnet $verifyEmbeddedResourceArguments

$programFilesX86Path = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
$userProgramsPath = Join-Path $env:LOCALAPPDATA 'Programs'
$isccCandidates = @(
    $env:VEYONCAMPUS_ISCC_PATH,
    $(if ($programFilesX86Path) { Join-Path $programFilesX86Path 'Inno Setup 6/ISCC.exe' }),
    (Join-Path $userProgramsPath 'Inno Setup 6/ISCC.exe'),
    $(if (Get-Command ISCC.exe -ErrorAction SilentlyContinue) { (Get-Command ISCC.exe).Source })
) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique
$script:isccPath = $null
foreach ($candidate in $isccCandidates) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $script:isccPath = $candidate
        break
    }
}
if (-not $script:isccPath) {
    throw '需要 Inno Setup 6。请安装 Inno Setup 6 并确保 ISCC.exe 可用，然后重新运行此脚本。'
}

$updateHelperOutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ("VeyonCampusUpdateHelper-$Role-" + [guid]::NewGuid().ToString('N'))
$helperRestoreArguments = @('restore', $updateHelperProjectPath, '-r', 'win-x64',
    '-p:NuGetAudit=false', '-p:VeyonCampusIncludeInstaller=false',
    '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json')
if ($releasePublicKeyFullPath) {
    $helperRestoreArguments += "-p:VeyonCampusReleasePublicKeyPath=$releasePublicKeyFullPath"
}
if (-not $SkipRestore) {
    Invoke-Dotnet $helperRestoreArguments
}
elseif (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $updateHelperProjectPath) 'obj/project.assets.json') -PathType Leaf)) {
    throw 'UpdateHelper 缺少本地 NuGet assets 文件；请先不使用 -SkipRestore 完成一次还原。'
}
$helperPublishArguments = @('publish', $updateHelperProjectPath, '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'false', '--no-restore', '-p:PublishSingleFile=true',
    '-p:AppHostDotNetSearch=Global', '-p:DebugType=None',
    '-p:VeyonCampusIncludeInstaller=false', '-p:VeyonCampusCoreLockFile=packages.win-x64.lock.json',
    '-o', $updateHelperOutputDirectory)
if ($releasePublicKeyFullPath) {
    $helperPublishArguments += "-p:VeyonCampusReleasePublicKeyPath=$releasePublicKeyFullPath"
}
Invoke-Dotnet $helperPublishArguments
$updateHelperExePath = Join-Path $updateHelperOutputDirectory 'VeyonCampus.UpdateHelper.exe'
if (-not (Test-Path -LiteralPath $updateHelperExePath -PathType Leaf)) {
    throw "发布结果中缺少单文件更新助手：$updateHelperExePath"
}
$helperKeyArguments = @('--verify-release-key')
if ($releasePublicKeyFingerprint) {
    $helperKeyArguments += $releasePublicKeyFingerprint
}
$helperKeyProbe = Start-Process -FilePath $updateHelperExePath -ArgumentList $helperKeyArguments `
    -Wait -PassThru -WindowStyle Hidden
if ($releasePublicKeyFingerprint) {
    if ($helperKeyProbe.ExitCode -ne 0) {
        throw '单文件更新助手未嵌入与安装器相同的 Developer Release 公钥。'
    }
    Write-Host 'PASS 单文件更新助手已嵌入固定 Developer Release 公钥。'
}
elseif ($helperKeyProbe.ExitCode -ne 3) {
    throw '未配置发行公钥时，单文件更新助手没有按预期安全停用更新。'
}
else {
    Write-Host 'PASS 未配置发行公钥时，单文件更新助手安全停用更新。'
}

$installerParent = Split-Path -Parent $installerFile
if (-not (Test-Path -LiteralPath $installerParent -PathType Container)) {
    New-Item -Path $installerParent -ItemType Directory -Force | Out-Null
}
$outputName = [IO.Path]::GetFileNameWithoutExtension($installerFile)
$generatedScriptName = ".VeyonCampus-$Role-$([guid]::NewGuid().ToString('N')).iss"
$generatedInstallerScriptPath = Join-Path (Split-Path -Parent $installerScriptPath) $generatedScriptName
$installerScriptInclude = [IO.Path]::GetFileName($installerScriptPath)
$generatedInstallerScript = @(
    ('#define public AppVersion ' + (ConvertTo-InnoPascalStringLiteral $appVersion))
    ('#define public PublishDirectory ' + (ConvertTo-InnoPascalStringLiteral $publishDirectory))
    ('#define public UpdateHelperDirectory ' + (ConvertTo-InnoPascalStringLiteral $updateHelperOutputDirectory))
    ('#define public RuntimeVersion ' + (ConvertTo-InnoPascalStringLiteral $runtimeVersion))
    ('#define public DotNetRuntimeInstallerPath ' + (ConvertTo-InnoPascalStringLiteral $dotnetRuntimeInstallerPath))
    ('#define public AspNetCoreRuntimeInstallerPath ' + (ConvertTo-InnoPascalStringLiteral $aspnetCoreRuntimeInstallerPath))
    ('#define public EdgeExtensionId "' + $EdgeExtensionId + '"')
    ('#define public EdgeExtensionForceInstall "' + $(if ($EdgeRealtimeManualTest) { 'no' } else { 'yes' }) + '"')
    ('#include "' + $installerScriptInclude + '"')
) -join [Environment]::NewLine
try {
    [IO.File]::WriteAllText($generatedInstallerScriptPath,
        $generatedInstallerScript + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Invoke-InnoSetup @(
        ('-o' + $installerParent),
        ('-f' + $outputName),
        $generatedInstallerScriptPath
    )
}
finally {
    if (Test-Path -LiteralPath $generatedInstallerScriptPath) {
        Remove-Item -LiteralPath $generatedInstallerScriptPath -Force
    }
    if (Test-Path -LiteralPath $updateHelperOutputDirectory) {
        Remove-Item -LiteralPath $updateHelperOutputDirectory -Recurse -Force
    }
}
if (-not (Test-Path -LiteralPath $installerFile -PathType Leaf)) {
    throw "Inno Setup 未生成预期安装器：$installerFile"
}

if ($zipFile) {
    $zipParent = Split-Path -Parent $zipFile
    if (-not (Test-Path -LiteralPath $zipParent -PathType Container)) {
        New-Item -Path $zipParent -ItemType Directory -Force | Out-Null
    }
    Compress-Archive -LiteralPath $publishDirectory -DestinationPath $zipFile -CompressionLevel Optimal
}

Write-Host ''
Write-Host 'Windows x64 Inno Setup 安装器已完成：' -ForegroundColor Green
Write-Host "文件夹：$publishDirectory"
Write-Host "安装器：$installerFile"
$publishedBytes = (Get-ChildItem -LiteralPath $publishDirectory -File -Recurse |
    Measure-Object -Property Length -Sum).Sum
$installerBytes = (Get-Item -LiteralPath $installerFile).Length
Write-Host ("应用文件：{0:N2} MiB；离线安装器：{1:N2} MiB" -f
    ($publishedBytes / 1MB), ($installerBytes / 1MB))
if ($zipFile) {
    Write-Host "可选诊断 ZIP：$zipFile"
}
Write-Host "启动程序：$appExePath"
if ($Role -eq 'StudentSetup') {
    Write-Host "独立后台代理：$agentExePath"
    Write-Host '将学生 Setup EXE 与教师为该校区生成的配置包配套分发。部署后可只读验证并清理便携学生工具。' -ForegroundColor Yellow
}
else {
    Write-Host '教师控制台不含学生部署界面；学生端请使用 StudentSetup 角色包。' -ForegroundColor Yellow
}
