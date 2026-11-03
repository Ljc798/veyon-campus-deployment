using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using VeyonCampus.Core.Maintenance;

namespace VeyonCampus.Core;

public static class ApplicationReleaseUpdateHandoff
{
    private const string HelperFileName = "VeyonCampus.UpdateHelper.exe";
    private const string TeacherUpdateLeaseName = "Local\\VeyonCampus_TeacherConsole_InstallUpdate";
    private const string StudentUpdateLeaseName = "Local\\VeyonCampus_StudentSetup_InstallUpdate";
    private const string UpdateHelperReadyEventPrefix = "Local\\VeyonCampus_UpdateHelperReady_";

    public static void Start(string installerPath, ApplicationReleaseRole role, string currentVersion,
        IProgress<InstallerResourceProgress>? progress = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("应用更新安装仅支持 Windows。");
        if (role == ApplicationReleaseRole.MaintenanceService)
            throw new NotSupportedException("MaintenanceService 必须走独立 SYSTEM 服务更新事务；不能使用 Teacher/Student 应用更新助手。");

        _ = ApplicationReleaseClient.CompareVersions(currentVersion, currentVersion);
        var fullInstallerPath = Path.GetFullPath(installerPath);
        var updatesDirectory = Path.Combine(VeyonCampusPaths.GetLocalApplicationDataDirectory(),
            "VeyonCampus", "Updates");
        EnsureSamePath(Path.GetDirectoryName(fullInstallerPath) ?? "", updatesDirectory,
            "暂存安装器必须位于当前用户的 VeyonCampus 更新目录。");
        EnsureDirectoryIsNotReparsePoint(updatesDirectory);
        var release = ApplicationReleaseClient.ReadVerifiedStagedRelease(fullInstallerPath, role,
            DeploymentPackageApiConfiguration.GetApiBaseAddress(), ApplicationReleaseTrust.LoadPinnedPublicKeyPem(),
            progress);
        if (ApplicationReleaseClient.CompareVersions(release.Manifest.Version, currentVersion) <= 0)
            throw new InvalidDataException("更新版本必须严格高于当前版本。");

        var (roleSlug, executableName) = GetRolePaths(role);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installDirectory = Path.Combine(programFiles, "Veyon Campus", roleSlug);
        var currentExecutable = Environment.ProcessPath
                                ?? throw new InvalidOperationException("无法确定当前应用程序路径。");
        EnsureSamePath(currentExecutable, Path.Combine(installDirectory, executableName),
            "应用必须从固定的 Program Files 安装目录启动后才能自我更新。");

        var helperPath = Path.Combine(programFiles, "Veyon Campus", "Updater", roleSlug, currentVersion,
            HelperFileName);
        if (!File.Exists(helperPath))
            throw new FileNotFoundException("当前安装目录缺少更新助手；请先重新安装当前教师端版本。", HelperFileName);
        if ((File.GetAttributes(helperPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新助手不能是符号链接或重解析点。");

        var leaseName = GetUpdateLeaseName(role);
        using var updateLease = new Semaphore(1, 1, leaseName, out var leaseCreatedNew);
        if (!leaseCreatedNew || !updateLease.WaitOne(0))
            throw new InvalidOperationException("另一个教师端或学生端安装/更新事务正在运行；请等待其完成后重试。");

        var readyEventName = UpdateHelperReadyEventPrefix + Guid.NewGuid().ToString("N");
        using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName,
            out var eventCreatedNew);
        if (!eventCreatedNew)
        {
            updateLease.Release();
            throw new InvalidOperationException("无法创建更新助手交接信号；当前应用仍保持运行。");
        }

        using var currentProcess = Process.GetCurrentProcess();
        var startTicks = currentProcess.StartTime.ToUniversalTime().Ticks;
        var startInfo = new ProcessStartInfo(helperPath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(helperPath)!
        };
        startInfo.ArgumentList.Add("--apply-release");
        startInfo.ArgumentList.Add(role == ApplicationReleaseRole.TeacherConsole ? "TeacherConsole" : "StudentSetup");
        startInfo.ArgumentList.Add(fullInstallerPath);
        startInfo.ArgumentList.Add(currentProcess.Id.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(startTicks.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(currentVersion);
        startInfo.ArgumentList.Add(leaseName);
        startInfo.ArgumentList.Add(readyEventName);

        Process? helper = null;
        try
        {
            helper = Process.Start(startInfo)
                     ?? throw new InvalidOperationException("无法启动应用更新助手。");
            if (!readyEvent.WaitOne(TimeSpan.FromSeconds(15)) || helper.HasExited)
                throw new InvalidOperationException("更新助手未能接管安装事务；当前应用保持运行，可以重试。");
        }
        catch
        {
            if (helper is not null)
            {
                try
                {
                    if (!helper.HasExited)
                    {
                        helper.Kill(entireProcessTree: true);
                        helper.WaitForExit(5_000);
                    }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                                  InvalidOperationException or TimeoutException)
                {
                    // Keep the lease closed until the helper exits; a concurrent installer must not race it.
                }
            }

            if (helper is null || helper.HasExited)
            {
                try { updateLease.Release(); }
                catch (SemaphoreFullException) { }
            }
            throw;
        }
        finally
        {
            helper?.Dispose();
        }
    }

    public static int RunHelper(string[] args)
    {
        try
        {
            if (args is ["--apply-release", var roleText, var installerPath, var processIdText,
                    var processStartTicksText, var currentVersion, var leaseName, var readyEventName])
            {
                var role = ParseRole(roleText);
                if (!string.Equals(leaseName, GetUpdateLeaseName(role), StringComparison.Ordinal) ||
                    !IsValidUpdateReadyEventName(readyEventName))
                    throw new InvalidDataException("更新助手事务锁参数无效。");
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("应用更新安装仅支持 Windows。");
                if (!int.TryParse(processIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ||
                    processId <= 0 ||
                    !long.TryParse(processStartTicksText, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var processStartTicks) || processStartTicks <= 0)
                    throw new InvalidDataException("更新父进程标识无效。");
                using var updateLease = Semaphore.OpenExisting(leaseName);
                using var readyEvent = EventWaitHandle.OpenExisting(readyEventName);
                readyEvent.Set();
                try
                {
                    ApplyReleaseAsCurrentUser(installerPath, role, processId, processStartTicks, currentVersion);
                    return 0;
                }
                finally
                {
                    updateLease.Release();
                }
            }
            if (args is ["--elevated-apply-release", var elevatedRole, var encodedInstallerPath,
                    var encodedCurrentVersion])
            {
                var decodedInstallerPath = Encoding.UTF8.GetString(Convert.FromBase64String(encodedInstallerPath));
                var decodedCurrentVersion = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCurrentVersion));
                ApplyReleaseAsAdministrator(decodedInstallerPath, ParseRole(elevatedRole), decodedCurrentVersion);
                return 0;
            }
            if (args is ["--maintenance-apply-release", var encodedInstallerPath, var encodedConfigPath,
                    var commandIdText, var encodedCurrentVersion])
            {
                var installerPath = Encoding.UTF8.GetString(Convert.FromBase64String(encodedInstallerPath));
                var configPath = Encoding.UTF8.GetString(Convert.FromBase64String(encodedConfigPath));
                var currentVersion = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCurrentVersion));
                if (!Guid.TryParseExact(commandIdText, "N", out var commandId) || commandId == Guid.Empty)
                    throw new InvalidDataException("Maintenance 更新事务编号无效。");
                ApplyReleaseFromMaintenanceService(installerPath, configPath, currentVersion, commandId);
                return 0;
            }
            if (args is ["--maintenance-apply-service-release", var encodedInstallerPath,
                    var encodedConfigPath, var commandIdText, var encodedCurrentVersion, var encodedTargetVersion])
            {
                var installerPath = Encoding.UTF8.GetString(Convert.FromBase64String(encodedInstallerPath));
                var configPath = Encoding.UTF8.GetString(Convert.FromBase64String(encodedConfigPath));
                var currentVersion = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCurrentVersion));
                var targetVersion = Encoding.UTF8.GetString(Convert.FromBase64String(encodedTargetVersion));
                if (!Guid.TryParseExact(commandIdText, "N", out var commandId) || commandId == Guid.Empty)
                    throw new InvalidDataException("MaintenanceService 更新事务编号无效。");
                ApplyMaintenanceServiceRelease(installerPath, configPath, commandId, currentVersion, targetVersion);
                return 0;
            }
            return 2;
        }
        catch (Exception exception)
        {
            WriteFailureLog(exception);
            return 1;
        }
    }

    /// <summary>
    /// Starts the already deployed, versioned UpdateHelper under SYSTEM. The helper owns the
    /// machine update mutex and can let SCM stop this service before StudentSetup replaces files.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void StartFromMaintenanceService(string installerPath, string configPath, Guid commandId)
    {
        EnsureSystemIdentity();
        if (commandId == Guid.Empty) throw new InvalidDataException("Maintenance 更新事务编号无效。");
        var fullConfigPath = MaintenanceServiceConfigurationStore.ResolveAgentConfigurationPath();
        EnsureSamePath(fullConfigPath, configPath, "Maintenance 更新配置路径不是当前唯一受信任 Agent 配置。" );
        var currentVersion = StudentApplicationUpdateProcessor.TryReadInstalledStudentSetupVersion()
                             ?? throw new InvalidDataException("无法读取当前 StudentSetup 版本；未启动更新助手。" );
        var fullInstallerPath = Path.GetFullPath(installerPath);
        EnsureMaintenanceServiceStagingPath(fullInstallerPath, fullConfigPath);
        var release = ReadVerifiedRelease(fullInstallerPath, ApplicationReleaseRole.StudentSetup);
        var updateTransaction = MaintenanceServiceUpdateExecutor.ReadTransaction(fullConfigPath);
        if (updateTransaction is null || updateTransaction.CommandId != commandId ||
            updateTransaction.State is not ("pending" or "inProgress"))
            throw new InvalidDataException("UpdateHelper 找不到可执行的原 commandId Maintenance 更新事务。" );
        if (ApplicationReleaseClient.CompareVersions(release.Manifest.Version, currentVersion) <= 0)
            throw new InvalidDataException("Maintenance StudentSetup 更新目标必须高于当前安装版本。" );
        EnsurePolicyCapabilities(ApplicationReleaseRole.StudentSetup, release.Manifest);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var serviceExecutable = Environment.ProcessPath
                                ?? throw new InvalidOperationException("无法确定当前 Maintenance 服务路径。");
        var serviceDirectory = Path.GetDirectoryName(Path.GetFullPath(serviceExecutable))
                               ?? throw new InvalidDataException("Maintenance 服务目录无效。");
        var helperPath = Path.Combine(serviceDirectory, HelperFileName);
        if (!File.Exists(helperPath))
            helperPath = StudentApplicationUpdateHandoff.GetUpdateHelperPath(programFiles, currentVersion);
        PathLinkSecurity.RejectLinks(helperPath);
        if (!File.Exists(helperPath))
            throw new FileNotFoundException("Maintenance 与 StudentSetup 版本目录均缺少 UpdateHelper；未启动更新。", helperPath);
        var startInfo = new ProcessStartInfo(helperPath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(helperPath)!
        };
        startInfo.ArgumentList.Add("--maintenance-apply-release");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(fullInstallerPath)));
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(fullConfigPath)));
        startInfo.ArgumentList.Add(commandId.ToString("N"));
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(currentVersion)));
        using var helper = Process.Start(startInfo)
                           ?? throw new InvalidOperationException("无法启动 SYSTEM StudentSetup 更新助手。" );
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void StartMaintenanceServiceReleaseHelper(string installerPath, string configPath, Guid commandId)
    {
        EnsureSystemIdentity();
        if (commandId == Guid.Empty) throw new InvalidDataException("MaintenanceService 更新事务编号无效。");
        var fullConfigPath = MaintenanceServiceConfigurationStore.ResolveAgentConfigurationPath();
        EnsureSamePath(fullConfigPath, configPath, "MaintenanceService 更新配置路径不是当前唯一受信任 Agent 配置。");
        var fullInstallerPath = Path.GetFullPath(installerPath);
        EnsureMaintenanceServiceStagingPath(fullInstallerPath, fullConfigPath);
        var transaction = MaintenanceServiceReleaseUpdateExecutor.ReadTransaction(fullConfigPath);
        if (transaction is null || transaction.CommandId != commandId ||
            transaction.State is not ("pending" or "inProgress"))
            throw new InvalidDataException("MaintenanceService 更新助手找不到原 commandId 的待执行事务。");
        var serviceExecutable = Path.GetFullPath(Environment.ProcessPath ??
            throw new InvalidOperationException("无法确定当前 Maintenance 服务路径。"));
        var helperPath = Path.Combine(Path.GetDirectoryName(serviceExecutable)!, HelperFileName);
        PathLinkSecurity.RejectLinks(serviceExecutable);
        PathLinkSecurity.RejectLinks(helperPath);
        if (!File.Exists(helperPath))
            throw new FileNotFoundException("当前 MaintenanceService 版本目录缺少独立 UpdateHelper。", helperPath);

        var currentVersion = MaintenanceServiceCommandDispatcher.GetServiceVersion();
        var release = ReadVerifiedRelease(fullInstallerPath, ApplicationReleaseRole.MaintenanceService);
        if (release.Manifest.MaintenanceProtocolVersion != MaintenanceProtocolV1.CurrentVersion ||
            !string.Equals(release.Manifest.Version, transaction.TargetVersion, StringComparison.Ordinal) ||
            !string.Equals(currentVersion, transaction.PreviousVersion, StringComparison.Ordinal))
            throw new InvalidDataException("MaintenanceService 包、协议、当前版本与事务记录不一致。");
        var startInfo = new ProcessStartInfo(helperPath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(helperPath)!
        };
        startInfo.ArgumentList.Add("--maintenance-apply-service-release");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(fullInstallerPath)));
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(fullConfigPath)));
        startInfo.ArgumentList.Add(commandId.ToString("N"));
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(currentVersion)));
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(transaction.TargetVersion)));
        using var helper = Process.Start(startInfo)
                           ?? throw new InvalidOperationException("无法启动 MaintenanceService 版本化 SYSTEM UpdateHelper。");
    }

    private static void ApplyMaintenanceServiceRelease(string installerPath, string configPath, Guid commandId,
        string currentVersion, string targetVersion)
    {
        EnsureSystemIdentity();
        _ = ApplicationReleaseClient.CompareVersions(currentVersion, currentVersion);
        _ = ApplicationReleaseClient.CompareVersions(targetVersion, targetVersion);
        var canonicalConfigPath = MaintenanceServiceConfigurationStore.ResolveAgentConfigurationPath();
        EnsureSamePath(canonicalConfigPath, configPath,
            "UpdateHelper 的 MaintenanceService 配置路径不是当前唯一受信任 Agent 配置。");
        var fullInstallerPath = Path.GetFullPath(installerPath);
        EnsureMaintenanceServiceStagingPath(fullInstallerPath, canonicalConfigPath);
        var helperExecutable = Path.GetFullPath(Environment.ProcessPath ??
            throw new InvalidOperationException("无法确定 MaintenanceService UpdateHelper 路径。"));
        var helperDirectory = Path.GetDirectoryName(helperExecutable)
                              ?? throw new InvalidDataException("MaintenanceService UpdateHelper 目录无效。");
        var serviceExecutable = Path.Combine(helperDirectory, "VeyonCampus.MaintenanceService.exe");
        EnsureSamePath(helperExecutable, Path.Combine(helperDirectory, HelperFileName),
            "MaintenanceService 更新必须由版本目录中的固定 UpdateHelper 执行。");
        PathLinkSecurity.RejectLinks(helperExecutable);
        PathLinkSecurity.RejectLinks(serviceExecutable);
        var release = ReadVerifiedRelease(fullInstallerPath, ApplicationReleaseRole.MaintenanceService);
        if (!File.Exists(serviceExecutable) ||
            !string.Equals(ReadProductVersion(serviceExecutable), currentVersion, StringComparison.Ordinal) ||
            !string.Equals(release.Manifest.Version, targetVersion, StringComparison.Ordinal) ||
            release.Manifest.MaintenanceProtocolVersion != MaintenanceProtocolV1.CurrentVersion)
            throw new InvalidDataException("MaintenanceService Helper、源/目标版本或协议与签名包不一致。");
        var transaction = MaintenanceServiceReleaseUpdateExecutor.ReadTransaction(canonicalConfigPath);
        if (transaction is null || transaction.CommandId != commandId || transaction.State is not ("pending" or "inProgress") ||
            !string.Equals(transaction.PreviousVersion, currentVersion, StringComparison.Ordinal) ||
            !string.Equals(transaction.TargetVersion, targetVersion, StringComparison.Ordinal) ||
            !string.Equals(transaction.ReleaseSha256, release.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UpdateHelper 找不到与签名包匹配的 MaintenanceService commandId 事务。");

        using var updateLock = StudentUpdateAtomicTransaction.AcquireMachineLock(TimeSpan.Zero);
        var updateState = StudentUpdateAtomicTransaction.ReadCurrentState(canonicalConfigPath);
        if (updateState is "inProgress" or "needsReview")
            throw new InvalidOperationException("StudentSetup/Agent 原子事务正在处理或需要恢复；MaintenanceService 更新不执行。");

        using var currentProcess = Process.GetCurrentProcess();
        MaintenanceServiceReleaseUpdateExecutor.MarkHelperStarted(canonicalConfigPath, commandId,
            currentProcess.Id, currentProcess.StartTime.ToUniversalTime().Ticks);
        try
        {
            _ = ReadVerifiedRelease(fullInstallerPath, ApplicationReleaseRole.MaintenanceService);
            var installerInfo = new ProcessStartInfo(fullInstallerPath)
            {
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(fullInstallerPath)!
            };
            using var installer = Process.Start(installerInfo)
                                  ?? throw new InvalidOperationException("无法启动签名 MaintenanceService 安装包。");
            if (!installer.WaitForExit((int)TimeSpan.FromMinutes(15).TotalMilliseconds))
            {
                try { installer.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
                throw new TimeoutException("MaintenanceService 安装器超过 15 分钟；由旧版本执行回滚。");
            }
            if (installer.ExitCode != 0)
                throw new InvalidOperationException($"MaintenanceService 安装器退出码为 {installer.ExitCode}。");

            var targetExecutable = GetMaintenanceServiceExecutable(targetVersion);
            VerifyMaintenanceServiceRelease(targetExecutable, targetVersion);
            MaintenanceServiceReleaseUpdateExecutor.MarkCompleted(canonicalConfigPath, commandId, "committed");
            MaintenanceServiceReleaseUpdateExecutor.TryDeleteStagedPackage(fullInstallerPath);
        }
        catch (Exception updateFailure) when (updateFailure is IOException or UnauthorizedAccessException or
                                               InvalidDataException or InvalidOperationException or TimeoutException or
                                               Win32Exception or System.Security.SecurityException or ArgumentException)
        {
            Exception? rollbackFailure = null;
            try
            {
                _ = RunServiceProcess(serviceExecutable, "--install-and-start-service", TimeSpan.FromMinutes(2));
                VerifyMaintenanceServiceRelease(serviceExecutable, currentVersion);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or TimeoutException or
                                              Win32Exception or System.Security.SecurityException or ArgumentException)
            { rollbackFailure = exception; }

            MaintenanceServiceReleaseUpdateExecutor.MarkCompleted(canonicalConfigPath, commandId,
                rollbackFailure is null ? "rolledBack" : "needsReview");
            if (rollbackFailure is null)
                MaintenanceServiceReleaseUpdateExecutor.TryDeleteStagedPackage(fullInstallerPath);
            else
                throw new IOException("MaintenanceService 更新失败且旧版本恢复未完成；保留事务和签名包供现场核对。",
                    new AggregateException(updateFailure, rollbackFailure));
            throw;
        }
    }

    private static void VerifyMaintenanceServiceRelease(string executablePath, string expectedVersion)
    {
        var fullExecutablePath = Path.GetFullPath(executablePath);
        PathLinkSecurity.RejectLinks(fullExecutablePath);
        if (!File.Exists(fullExecutablePath) ||
            !string.Equals(ReadProductVersion(fullExecutablePath), expectedVersion, StringComparison.Ordinal))
            throw new InvalidDataException("MaintenanceService 版本化文件版本读回不匹配。");
        var registeredImagePath = ReadMaintenanceServiceImagePath();
        EnsureSamePath(registeredImagePath, fullExecutablePath,
            "SCM Maintenance ImagePath 未指向目标版本化服务文件。");
        if (RunServiceProcess(fullExecutablePath, "--verify-running", TimeSpan.FromSeconds(45)) != 0)
            throw new InvalidOperationException("SCM 未报告目标 MaintenanceService 正在运行。");
        if (RunServiceProcess(fullExecutablePath, "--verify-installation", TimeSpan.FromSeconds(45)) != 0)
            throw new InvalidOperationException("MaintenanceService SYSTEM 配置和身份健康核验失败。");
    }

    private static string GetMaintenanceServiceExecutable(string version)
    {
        _ = ApplicationReleaseClient.CompareVersions(version, version);
        var executablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Veyon Campus", "MaintenanceService", "Versions", version, "VeyonCampus.MaintenanceService.exe");
        PathLinkSecurity.RejectLinksAllowingMissingComponents(executablePath);
        return executablePath;
    }

    private static string ReadMaintenanceServiceImagePath()
    {
        using var serviceKey = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\VeyonCampusMaintenanceService", writable: false);
        var imagePath = serviceKey?.GetValue("ImagePath") as string;
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new InvalidDataException("SCM Maintenance ImagePath 缺失。");
        var value = imagePath.Trim();
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote <= 1) throw new InvalidDataException("SCM Maintenance ImagePath 引号无效。");
            value = value[1..closingQuote];
        }
        else
        {
            var extension = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (extension < 0) throw new InvalidDataException("SCM Maintenance ImagePath 缺少 EXE 文件名。");
            value = value[..(extension + 4)];
        }
        return Path.GetFullPath(value);
    }

    private static int RunServiceProcess(string executablePath, string arguments, TimeSpan timeout)
    {
        using var process = Process.Start(new ProcessStartInfo(executablePath)
        {
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        }) ?? throw new InvalidOperationException("无法启动 MaintenanceService 健康核验命令。");
        if (!process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
            throw new TimeoutException("MaintenanceService 健康核验命令超时。");
        }
        return process.ExitCode;
    }

    private static string? ReadProductVersion(string executablePath) =>
        FileVersionInfo.GetVersionInfo(executablePath).ProductVersion?.Split('+')[0];

    private static void ApplyReleaseAsCurrentUser(string installerPath, ApplicationReleaseRole role,
        int parentProcessId, long parentStartTicks, string currentVersion)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("应用更新安装仅支持 Windows。");
        _ = ApplicationReleaseClient.CompareVersions(currentVersion, currentVersion);
        var fullInstallerPath = Path.GetFullPath(installerPath);
        var updatesDirectory = Path.Combine(VeyonCampusPaths.GetLocalApplicationDataDirectory(),
            "VeyonCampus", "Updates");
        EnsureSamePath(Path.GetDirectoryName(fullInstallerPath) ?? "", updatesDirectory,
            "暂存安装器路径无效。");
        EnsureUserStagingPath(fullInstallerPath);
        var release = ReadVerifiedRelease(fullInstallerPath, role);
        if (ApplicationReleaseClient.CompareVersions(release.Manifest.Version, currentVersion) <= 0)
            throw new InvalidDataException("更新版本必须严格高于当前版本。");
        EnsurePolicyCapabilities(role, release.Manifest);

        var (roleSlug, executableName) = GetRolePaths(role);
        var installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Veyon Campus", roleSlug);
        EnsureDirectoryIsNotReparsePoint(installDirectory);
        var executablePath = Path.Combine(installDirectory, executableName);
        var currentRoleInfo = ReadRoleInfo(installDirectory, role);
        if (!string.Equals(currentRoleInfo.Version, currentVersion, StringComparison.Ordinal))
            throw new InvalidDataException("当前安装版本与正在运行的版本不一致；未执行更新。");

        WaitForParentProcess(parentProcessId, parentStartTicks, executablePath);
        _ = ReadVerifiedRelease(fullInstallerPath, role);
        var helperPath = Environment.ProcessPath
                         ?? throw new InvalidOperationException("无法确定更新助手路径。");
        var elevatedStartInfo = new ProcessStartInfo(helperPath)
        {
            Arguments = string.Join(" ", "--elevated-apply-release",
                role == ApplicationReleaseRole.TeacherConsole ? "TeacherConsole" : "StudentSetup",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(fullInstallerPath)),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(currentVersion))),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(helperPath)!
        };
        try
        {
            using (var elevatedHelper = Process.Start(elevatedStartInfo)
                                        ?? throw new InvalidOperationException("无法启动管理员更新助手。"))
            {
                elevatedHelper.WaitForExit();
                if (elevatedHelper.ExitCode != 0)
                    throw new InvalidOperationException($"管理员更新助手返回失败代码 {elevatedHelper.ExitCode}；当前安装未报告成功。");
            }

            var installedRoleInfo = ReadRoleInfo(installDirectory, role);
            if (!string.Equals(installedRoleInfo.Version, release.Manifest.Version, StringComparison.Ordinal) ||
                !File.Exists(executablePath) ||
                (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("安装器退出后版本回读不匹配；未重启应用。");

            _ = Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = installDirectory
            }) ?? throw new InvalidOperationException("新版本安装成功，但无法重启应用。");
        }
        catch
        {
            TryRestartExistingVersion(installDirectory, executablePath, role, currentVersion);
            throw;
        }
    }

    private static void ApplyReleaseAsAdministrator(string installerPath, ApplicationReleaseRole role,
        string currentVersion, bool requireUserStaging = true, bool maintenanceServiceSelfUpdate = false,
        Action? verifyAdditionalComponents = null, Action? commitAdditionalTransaction = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("应用更新安装仅支持 Windows。");
        _ = ApplicationReleaseClient.CompareVersions(currentVersion, currentVersion);
        var fullInstallerPath = Path.GetFullPath(installerPath);
        if (requireUserStaging) EnsureUserStagingPath(fullInstallerPath);
        var release = ReadVerifiedRelease(fullInstallerPath, role);
        if (ApplicationReleaseClient.CompareVersions(release.Manifest.Version, currentVersion) <= 0)
            throw new InvalidDataException("更新版本必须严格高于当前版本。");
        EnsurePolicyCapabilities(role, release.Manifest);

        var (roleSlug, executableName) = GetRolePaths(role);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installDirectory = Path.Combine(programFiles, "Veyon Campus", roleSlug);
        PathLinkSecurity.RejectLinks(installDirectory);
        var executablePath = Path.Combine(installDirectory, executableName);
        var currentRoleInfo = ReadRoleInfo(installDirectory, role);
        if (!string.Equals(currentRoleInfo.Version, currentVersion, StringComparison.Ordinal))
            throw new InvalidDataException("当前安装版本与正在运行的版本不一致；未执行更新。");
        if (!File.Exists(executablePath) || (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("当前安装程序缺失或不是普通文件；未执行更新。");
        VerifyNoReparsePointsInTree(installDirectory);

        var stagingRoot = Path.Combine(programFiles, "Veyon Campus", "UpdateStaging");
        Directory.CreateDirectory(stagingRoot);
        PathLinkSecurity.RejectLinks(stagingRoot);
        var protectedStagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(protectedStagingDirectory);
        try
        {
            var protectedInstallerPath = Path.Combine(protectedStagingDirectory, release.Manifest.FileName);
            File.Copy(fullInstallerPath, protectedInstallerPath, overwrite: false);
            File.Copy(fullInstallerPath + ".release.json", protectedInstallerPath + ".release.json",
                overwrite: false);
            release = ApplicationReleaseClient.ReadVerifiedStagedRelease(protectedInstallerPath, role,
                DeploymentPackageApiConfiguration.GetApiBaseAddress(), ApplicationReleaseTrust.LoadPinnedPublicKeyPem());
            CleanupOldHelperVersions(role, currentVersion, release.Manifest.Version);

            var recoveryRoot = Path.Combine(programFiles, "Veyon Campus", "UpdateRecovery", roleSlug);
            Directory.CreateDirectory(recoveryRoot);
            PathLinkSecurity.RejectLinks(recoveryRoot);
            var recoveryDirectory = Path.Combine(recoveryRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recoveryDirectory);
            PathLinkSecurity.RejectLinks(recoveryDirectory);
            var previousInstallationDirectory = Path.Combine(recoveryDirectory, "Previous");
            VeyonCampusProcessCleanup.StopInstalledRoleProcesses(
                role == ApplicationReleaseRole.StudentSetup ? "StudentSetup" : "TeacherConsole",
                preserveUpdateHelper: true);
            Directory.Move(installDirectory, previousInstallationDirectory);

            try
            {
                var installerStartInfo = new ProcessStartInfo(protectedInstallerPath)
                {
                    Arguments = "/SILENT /NOCANCEL /NORESTART /CLOSEAPPLICATIONS",
                    UseShellExecute = false,
                    WorkingDirectory = protectedStagingDirectory
                };
                installerStartInfo.Environment["VEYONCAMPUS_SELF_UPDATE"] = "1";
                if (maintenanceServiceSelfUpdate)
                    installerStartInfo.Environment["VEYONCAMPUS_MAINTENANCE_SELF_UPDATE"] = "1";
                using (var installer = Process.Start(installerStartInfo)
                                       ?? throw new InvalidOperationException("无法启动签名安装器。"))
                {
                    installer.WaitForExit();
                    if (installer.ExitCode != 0)
                        throw new InvalidOperationException(
                            $"Inno Setup 返回失败代码 {installer.ExitCode}；当前安装未报告成功。");
                }

                VerifyNoReparsePointsInTree(installDirectory);
                var installedRoleInfo = ReadRoleInfo(installDirectory, role);
                if (!string.Equals(installedRoleInfo.Version, release.Manifest.Version, StringComparison.Ordinal) ||
                    !File.Exists(executablePath) ||
                    (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("安装器退出后版本回读不匹配；未重启应用。");
                verifyAdditionalComponents?.Invoke();
                commitAdditionalTransaction?.Invoke();
            }
            catch (Exception updateFailure)
            {
                RestorePreviousInstallation(installDirectory, previousInstallationDirectory, recoveryDirectory,
                    executableName, role, currentVersion, updateFailure);
            }

            TryRemovePreviousInstallation(previousInstallationDirectory, recoveryDirectory, role, currentVersion);
        }
        finally
        {
            try { Directory.Delete(protectedStagingDirectory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void ApplyReleaseFromMaintenanceService(string installerPath, string configPath,
        string currentVersion, Guid commandId)
    {
        EnsureSystemIdentity();
        _ = ApplicationReleaseClient.CompareVersions(currentVersion, currentVersion);
        var canonicalConfigPath = MaintenanceServiceConfigurationStore.ResolveAgentConfigurationPath();
        EnsureSamePath(canonicalConfigPath, configPath,
            "UpdateHelper 的 Maintenance 配置路径不是当前唯一受信任 Agent 配置。" );
        var fullInstallerPath = Path.GetFullPath(installerPath);
        EnsureMaintenanceServiceStagingPath(fullInstallerPath, canonicalConfigPath);
        var fullProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var helperExecutable = Path.GetFullPath(Environment.ProcessPath ??
            throw new InvalidOperationException("无法确定 SYSTEM UpdateHelper 路径。"));
        var helperDirectory = Path.GetDirectoryName(helperExecutable)
                              ?? throw new InvalidDataException("SYSTEM UpdateHelper 目录无效。");
        var maintenanceHelperPath = Path.Combine(helperDirectory, HelperFileName);
        var maintenanceServiceExecutable = Path.Combine(helperDirectory, "VeyonCampus.MaintenanceService.exe");
        var studentHelperPath = StudentApplicationUpdateHandoff.GetUpdateHelperPath(fullProgramFiles,
            currentVersion);
        if (File.Exists(maintenanceServiceExecutable))
        {
            EnsureSamePath(helperExecutable, maintenanceHelperPath,
                "Maintenance 更新必须由与当前独立服务同目录的版本化 UpdateHelper 执行。" );
            PathLinkSecurity.RejectLinks(maintenanceServiceExecutable);
        }
        else
            EnsureSamePath(helperExecutable, studentHelperPath,
                "兼容的 StudentSetup 维护事务必须由当前 StudentSetup 版本的 UpdateHelper 执行。" );
        var release = ReadVerifiedRelease(fullInstallerPath, ApplicationReleaseRole.StudentSetup);
        if (ApplicationReleaseClient.CompareVersions(release.Manifest.Version, currentVersion) <= 0)
            throw new InvalidDataException("Maintenance StudentSetup 更新目标必须高于当前安装版本。" );
        EnsurePolicyCapabilities(ApplicationReleaseRole.StudentSetup, release.Manifest);
        var maintenanceServiceDirectory = Path.Combine(fullProgramFiles, "Veyon Campus", "MaintenanceService",
            "Current");
        string? maintenanceServiceSnapshot = null;
        using var currentProcess = Process.GetCurrentProcess();
        MaintenanceServiceUpdateJournal.MarkHelperStarted(canonicalConfigPath, commandId, currentProcess.Id,
            currentProcess.StartTime.ToUniversalTime().Ticks);
        try
        {
            using var machineUpdateLock = StudentUpdateAtomicTransaction.AcquireMachineLock(TimeSpan.Zero);
            var updateState = StudentUpdateAtomicTransaction.ReadCurrentState(canonicalConfigPath);
            if (updateState is "inProgress" or "needsReview")
                throw new InvalidOperationException("已有 StudentSetup/Agent 原子事务；本次 Maintenance 更新保持待核对。" );
            maintenanceServiceSnapshot = CreateMaintenanceServiceSnapshot(maintenanceServiceDirectory,
                fullProgramFiles, commandId);
            ApplyReleaseAsAdministrator(fullInstallerPath, ApplicationReleaseRole.StudentSetup, currentVersion,
                requireUserStaging: false, maintenanceServiceSelfUpdate: true,
                verifyAdditionalComponents: () => VerifyInstalledMaintenanceService(maintenanceServiceDirectory,
                    release.Manifest.Version),
                commitAdditionalTransaction: () => MaintenanceServiceUpdateJournal.MarkCompleted(
                    canonicalConfigPath, commandId, "committed"));
            TryDeleteMaintenanceServiceSnapshot(maintenanceServiceSnapshot);
            MaintenanceServiceUpdateExecutor.TryDeleteStagedPackage(fullInstallerPath);
        }
        catch (Exception updateFailure)
        {
            Exception? serviceRestoreFailure = null;
            if (maintenanceServiceSnapshot is not null)
            {
                try { RestoreMaintenanceServiceSnapshot(maintenanceServiceDirectory, maintenanceServiceSnapshot); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  InvalidDataException or InvalidOperationException or
                                                  System.ComponentModel.Win32Exception or TimeoutException or
                                                  System.Security.SecurityException)
                { serviceRestoreFailure = exception; }
            }
            else TryRestartInstalledMaintenanceService(fullProgramFiles);
            var transaction = MaintenanceServiceUpdateExecutor.ReadTransaction(canonicalConfigPath);
            string? installedVersion = null;
            try { installedVersion = StudentApplicationUpdateProcessor.TryReadInstalledStudentSetupVersion(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or System.Security.SecurityException)
            { WriteFailureLog(new IOException("Maintenance 更新失败后无法读取 StudentSetup 恢复版本。", exception)); }
            string? installedAgentVersion = null;
            try
            {
                installedAgentVersion = MaintenanceServiceUpdateExecutor.ReadInstalledVersions(canonicalConfigPath,
                    DateTimeOffset.UtcNow).AgentRuntimeVersion;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or System.Security.SecurityException or
                                              CryptographicException or InvalidOperationException)
            {
                WriteFailureLog(new IOException("Maintenance 更新失败后无法读取 Agent 恢复版本。", exception));
            }
            var failedState = serviceRestoreFailure is null && transaction is not null &&
                              transaction.PreviousAgentVersion is { } previousAgent &&
                              string.Equals(installedVersion, transaction.PreviousSetupVersion,
                                  StringComparison.Ordinal) &&
                              string.Equals(installedAgentVersion, previousAgent, StringComparison.Ordinal)
                ? "rolledBack"
                : "needsReview";
            MaintenanceServiceUpdateJournal.MarkCompleted(canonicalConfigPath, commandId, failedState);
            if (failedState == "rolledBack")
                MaintenanceServiceUpdateExecutor.TryDeleteStagedPackage(fullInstallerPath);
            if (serviceRestoreFailure is not null)
                throw new IOException("Maintenance 自更新失败，独立服务旧版恢复失败；更新事务已标记需人工核对。",
                    new AggregateException(updateFailure, serviceRestoreFailure));
            throw;
        }
    }

    private static string CreateMaintenanceServiceSnapshot(string serviceDirectory, string programFiles,
        Guid commandId)
    {
        PathLinkSecurity.RejectLinks(serviceDirectory);
        if (!Directory.Exists(serviceDirectory))
            throw new DirectoryNotFoundException("Maintenance 自更新缺少当前服务目录；拒绝无回滚副本地替换。" );
        VerifyNoReparsePointsInTree(serviceDirectory);
        var recoveryRoot = Path.Combine(programFiles, "Veyon Campus", "UpdateRecovery", "MaintenanceService");
        PathLinkSecurity.RejectLinksAllowingMissingComponents(recoveryRoot);
        Directory.CreateDirectory(recoveryRoot);
        PathLinkSecurity.RejectLinks(recoveryRoot);
        AgentFileSecurity.SecurePrivateDirectory(recoveryRoot);
        var snapshotDirectory = Path.Combine(recoveryRoot, commandId.ToString("N"));
        if (Directory.Exists(snapshotDirectory) || File.Exists(snapshotDirectory))
            throw new IOException("Maintenance 自更新恢复目录已存在；拒绝覆盖现有恢复材料。" );
        Directory.CreateDirectory(snapshotDirectory);
        AgentFileSecurity.SecurePrivateDirectory(snapshotDirectory);
        try
        {
            var count = 0;
            long bytes = 0;
            CopyMaintenanceServiceTree(serviceDirectory, snapshotDirectory, ref count, ref bytes);
            VerifyNoReparsePointsInTree(snapshotDirectory);
            var snapshotExecutable = Path.Combine(snapshotDirectory, "VeyonCampus.MaintenanceService.exe");
            if (!File.Exists(snapshotExecutable))
                throw new InvalidDataException("Maintenance 自更新恢复副本缺少服务可执行文件。" );
            return snapshotDirectory;
        }
        catch
        {
            TryDeleteMaintenanceServiceSnapshot(snapshotDirectory);
            throw;
        }
    }

    private static void CopyMaintenanceServiceTree(string sourceDirectory, string destinationDirectory,
        ref int fileCount, ref long totalBytes)
    {
        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            PathLinkSecurity.RejectLinks(sourcePath);
            var attributes = File.GetAttributes(sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(destinationPath);
                AgentFileSecurity.SecurePrivateDirectory(destinationPath);
                CopyMaintenanceServiceTree(sourcePath, destinationPath, ref fileCount, ref totalBytes);
                continue;
            }
            if ((attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("Maintenance 服务恢复副本包含非普通文件。" );
            var length = new FileInfo(sourcePath).Length;
            if (++fileCount > 512 || length < 0 || totalBytes > 512L * 1024 * 1024 - length)
                throw new InvalidDataException("Maintenance 服务恢复副本超过文件数量或总大小限制。" );
            totalBytes += length;
            File.Copy(sourcePath, destinationPath, overwrite: false);
            AgentFileSecurity.SecurePrivateFile(destinationPath);
        }
    }

    private static void RestoreMaintenanceServiceSnapshot(string serviceDirectory, string snapshotDirectory)
    {
        PathLinkSecurity.RejectLinks(serviceDirectory);
        PathLinkSecurity.RejectLinks(snapshotDirectory);
        VerifyNoReparsePointsInTree(snapshotDirectory);
        var snapshotExecutable = Path.Combine(snapshotDirectory, "VeyonCampus.MaintenanceService.exe");
        if (!File.Exists(snapshotExecutable))
            throw new InvalidDataException("Maintenance 自更新恢复副本缺少服务可执行文件；保留恢复材料。" );
        _ = RunMaintenanceServiceControl(snapshotExecutable, "--stop-service");
        var failedDirectory = serviceDirectory + ".failed-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(serviceDirectory))
        {
            VerifyNoReparsePointsInTree(serviceDirectory);
            Directory.Move(serviceDirectory, failedDirectory);
        }
        else if (File.Exists(serviceDirectory))
            throw new IOException("Maintenance 服务目录被普通文件占用；保留恢复材料。" );
        try
        {
            Directory.Move(snapshotDirectory, serviceDirectory);
            VerifyNoReparsePointsInTree(serviceDirectory);
            var restoredExecutable = Path.Combine(serviceDirectory, "VeyonCampus.MaintenanceService.exe");
            _ = RunMaintenanceServiceControl(restoredExecutable, "--install-and-start-service");
            if (RunMaintenanceServiceControl(restoredExecutable, "--verify-running") != 0)
                throw new IOException("Maintenance 服务恢复后未达到 SCM Running 状态。" );
            if (Directory.Exists(failedDirectory))
            {
                VerifyNoReparsePointsInTree(failedDirectory);
                Directory.Delete(failedDirectory, recursive: true);
            }
            var recoveryRoot = Path.GetDirectoryName(snapshotDirectory);
            if (recoveryRoot is not null && Directory.Exists(recoveryRoot) &&
                !Directory.EnumerateFileSystemEntries(recoveryRoot).Any())
                Directory.Delete(recoveryRoot);
        }
        catch (Exception restoreFailure)
        {
            throw new IOException(
                $"Maintenance 服务回滚未能启动旧服务；恢复副本已移动到：{serviceDirectory}；失败服务目录现场：{failedDirectory}。原恢复副本路径：{snapshotDirectory}。",
                restoreFailure);
        }
    }

    private static void VerifyInstalledMaintenanceService(string serviceDirectory, string expectedVersion)
    {
        var executablePath = Path.Combine(serviceDirectory, "VeyonCampus.MaintenanceService.exe");
        PathLinkSecurity.RejectLinks(executablePath);
        if (!File.Exists(executablePath) || RunMaintenanceServiceControl(executablePath, "--verify-running") != 0)
            throw new InvalidDataException("Maintenance 自更新后服务未通过 SCM Running 状态核验。" );
        var startInfo = new ProcessStartInfo(executablePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = serviceDirectory
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo)
                           ?? throw new InvalidOperationException("无法读取更新后 Maintenance 服务版本。" );
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(35_000))
        {
            try { process.Kill(entireProcessTree: false); }
            catch (Exception exception) when (exception is InvalidOperationException or
                                              System.ComponentModel.Win32Exception) { }
            throw new TimeoutException("读取更新后 Maintenance 服务版本超时。" );
        }
        var installedVersion = output.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(installedVersion) ||
            ApplicationReleaseClient.CompareVersions(installedVersion, expectedVersion) != 0)
            throw new InvalidDataException("Maintenance 服务程序集版本与已签名 StudentSetup 发行目标不匹配。" );
    }

    private static void TryDeleteMaintenanceServiceSnapshot(string snapshotDirectory)
    {
        try
        {
            if (!Directory.Exists(snapshotDirectory)) return;
            VerifyNoReparsePointsInTree(snapshotDirectory);
            Directory.Delete(snapshotDirectory, recursive: true);
            var recoveryRoot = Path.GetDirectoryName(snapshotDirectory);
            if (recoveryRoot is not null && Directory.Exists(recoveryRoot) &&
                !Directory.EnumerateFileSystemEntries(recoveryRoot).Any())
                Directory.Delete(recoveryRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.Security.SecurityException)
        {
            WriteFailureLog(new IOException("Maintenance 服务恢复快照未能清理；保留在 " + snapshotDirectory, exception));
        }
    }

    private static void TryRestartInstalledMaintenanceService(string programFiles)
    {
        try
        {
            var serviceExecutable = Path.Combine(programFiles, "Veyon Campus", "MaintenanceService", "Current",
                "VeyonCampus.MaintenanceService.exe");
            if (!File.Exists(serviceExecutable)) return;
            PathLinkSecurity.RejectLinks(serviceExecutable);
            if (RunMaintenanceServiceControl(serviceExecutable, "--query-service") == 0)
                _ = RunMaintenanceServiceControl(serviceExecutable, "--start-service");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidOperationException or System.ComponentModel.Win32Exception or
                                          System.Security.SecurityException or TimeoutException)
        {
            // The signed transaction remains needsReview; service restart is retried by SCM/installer recovery.
        }
    }

    private static int RunMaintenanceServiceControl(string serviceExecutable, string argument)
    {
        var startInfo = new ProcessStartInfo(serviceExecutable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(serviceExecutable)!
        };
        startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
                           ?? throw new InvalidOperationException("无法启动 Maintenance 服务控制命令。" );
        if (!process.WaitForExit(35_000))
        {
            try { process.Kill(entireProcessTree: false); }
            catch (Exception exception) when (exception is InvalidOperationException or
                                              System.ComponentModel.Win32Exception) { }
            throw new TimeoutException("Maintenance 服务控制命令超时。" );
        }
        return process.ExitCode;
    }

    private static void EnsureMaintenanceServiceStagingPath(string installerPath, string configPath)
    {
        var fullPath = Path.GetFullPath(installerPath);
        var serviceDirectory = MaintenanceServiceIdentityStore.GetServiceDirectory(configPath);
        var stagingDirectory = Path.Combine(serviceDirectory,
            MaintenanceServiceUpdateExecutor.StagingDirectoryName);
        EnsureSamePath(Path.GetDirectoryName(fullPath) ?? "", stagingDirectory,
            "Maintenance 更新包必须位于 SYSTEM 私有暂存目录。" );
        PathLinkSecurity.RejectLinks(stagingDirectory);
        PathLinkSecurity.RejectLinks(fullPath);
        if (!AgentFileSecurity.IsPrivate(stagingDirectory, directory: true))
            throw new UnauthorizedAccessException("Maintenance 更新暂存目录 ACL 未限制为 SYSTEM 和 Administrators。" );
    }

    private static void EnsureSystemIdentity()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Maintenance StudentSetup 更新仅支持 Windows。" );
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18")
            throw new UnauthorizedAccessException("Maintenance StudentSetup 更新助手必须以 SYSTEM 身份运行。" );
    }

    private static void RestorePreviousInstallation(string installDirectory, string previousInstallationDirectory,
        string recoveryDirectory, string executableName, ApplicationReleaseRole role, string currentVersion,
        Exception updateFailure)
    {
        string? failedInstallationDirectory = null;
        try
        {
            if (Directory.Exists(installDirectory))
            {
                VerifyNoReparsePointsInTree(installDirectory);
                failedInstallationDirectory = Path.Combine(recoveryDirectory, "Failed-" + Guid.NewGuid().ToString("N"));
                Directory.Move(installDirectory, failedInstallationDirectory);
            }
            else if (File.Exists(installDirectory))
            {
                throw new IOException("更新后的安装路径被非目录占用；旧版仍保留在恢复目录。");
            }

            Directory.Move(previousInstallationDirectory, installDirectory);
            VerifyNoReparsePointsInTree(installDirectory);
            var restoredRoleInfo = ReadRoleInfo(installDirectory, role);
            var executablePath = Path.Combine(installDirectory, executableName);
            if (!string.Equals(restoredRoleInfo.Version, currentVersion, StringComparison.Ordinal) ||
                !File.Exists(executablePath) ||
                (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("旧版本安装目录恢复后读回不匹配。");
        }
        catch (Exception rollbackFailure)
        {
            throw new AggregateException(
                $"应用更新失败且旧版本恢复未完成；旧版本保留路径：{previousInstallationDirectory}。",
                updateFailure, rollbackFailure);
        }

        var failureLocation = failedInstallationDirectory is null
            ? ""
            : $"失败的新文件保留在 {failedInstallationDirectory}。";
        throw new InvalidOperationException($"应用更新失败；旧版本 {currentVersion} 已恢复。{failureLocation}",
            updateFailure);
    }

    private static void TryRemovePreviousInstallation(string previousInstallationDirectory,
        string recoveryDirectory, ApplicationReleaseRole role, string expectedVersion)
    {
        try
        {
            VerifyNoReparsePointsInTree(previousInstallationDirectory);
            var previousRoleInfo = ReadRoleInfo(previousInstallationDirectory, role);
            if (!string.Equals(previousRoleInfo.Version, expectedVersion, StringComparison.Ordinal))
                throw new InvalidDataException("旧版本恢复副本的角色或版本读回不匹配；未删除。");

            var uninstallerPath = Path.Combine(previousInstallationDirectory, "unins000.exe");
            if (File.Exists(uninstallerPath))
            {
                PathLinkSecurity.RejectLinks(uninstallerPath);
                File.Delete(uninstallerPath);
            }
            Directory.Delete(previousInstallationDirectory, recursive: true);
            Directory.Delete(recoveryDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or JsonException or System.Security.SecurityException)
        {
            WriteFailureLog(new IOException(
                $"新版本已通过版本读回；旧版暂存目录未能清理：{previousInstallationDirectory}。", exception));
        }
    }

    private static void VerifyNoReparsePointsInTree(string directory)
    {
        PathLinkSecurity.RejectLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"应用目录包含重解析点：{entry}。");
            if ((attributes & FileAttributes.Directory) != 0)
                VerifyNoReparsePointsInTree(entry);
        }
    }

    private static ApplicationReleaseEnvelope ReadVerifiedRelease(string installerPath,
        ApplicationReleaseRole role) => ApplicationReleaseClient.ReadVerifiedStagedRelease(installerPath, role,
        DeploymentPackageApiConfiguration.GetApiBaseAddress(), ApplicationReleaseTrust.LoadPinnedPublicKeyPem());

    private static void EnsurePolicyCapabilities(ApplicationReleaseRole role, ApplicationReleaseManifest manifest)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("应用更新安装仅支持 Windows。");
        if (role != ApplicationReleaseRole.StudentSetup) return;
        ApplicationReleaseCompatibility.EnsureSupports(manifest,
            WindowsApplicationPolicyStateStore.HasAnyActiveState(),
            WindowsStudentSystemPolicyAgent.HasAnyActiveState());
    }

    private static void CleanupOldHelperVersions(ApplicationReleaseRole role, string currentVersion,
        string targetVersion)
    {
        var roleSlug = GetRolePaths(role).RoleSlug;
        var helperRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Veyon Campus", "Updater", roleSlug);
        if (!Directory.Exists(helperRoot) ||
            (File.GetAttributes(helperRoot) & FileAttributes.ReparsePoint) != 0)
            return;
        try
        {
            foreach (var helperDirectory in Directory.EnumerateDirectories(helperRoot))
            {
                var directoryVersion = Path.GetFileName(helperDirectory);
                if (directoryVersion is null || directoryVersion == currentVersion || directoryVersion == targetVersion ||
                    (File.GetAttributes(helperDirectory) & FileAttributes.ReparsePoint) != 0)
                    continue;
                try { Directory.Delete(helperDirectory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void EnsureUserStagingPath(string installerPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(installerPath)
                                          ?? throw new InvalidDataException("暂存安装器目录无效。"));
        var campusDirectory = directory.Parent
                              ?? throw new InvalidDataException("暂存用户配置目录无效。");
        var localDirectory = campusDirectory.Parent
                             ?? throw new InvalidDataException("暂存用户配置目录无效。");
        var appDataDirectory = localDirectory.Parent
                               ?? throw new InvalidDataException("暂存用户配置目录无效。");
        var profileDirectory = appDataDirectory.Parent
                               ?? throw new InvalidDataException("暂存用户配置目录无效。");
        if (directory.Name != "Updates" || campusDirectory.Name != "VeyonCampus" ||
            localDirectory.Name != "Local" || appDataDirectory.Name != "AppData")
            throw new InvalidDataException("暂存安装器必须位于用户 LocalAppData 更新目录。");
        EnsureDirectoryIsNotReparsePoint(directory.FullName);
        EnsureDirectoryIsNotReparsePoint(campusDirectory.FullName);
        EnsureDirectoryIsNotReparsePoint(localDirectory.FullName);
        EnsureDirectoryIsNotReparsePoint(appDataDirectory.FullName);
        EnsureDirectoryIsNotReparsePoint(profileDirectory.FullName);
    }

    private static void WaitForParentProcess(int processId, long startTicks, string expectedExecutablePath)
    {
        Process parent;
        try { parent = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.HasExited) return;
            var actualExecutablePath = parent.MainModule?.FileName
                                       ?? throw new InvalidDataException("更新父进程路径不可用。");
            EnsureSamePath(actualExecutablePath, expectedExecutablePath, "更新父进程不是当前已安装的应用。");
            if (parent.StartTime.ToUniversalTime().Ticks != startTicks)
                throw new InvalidDataException("更新父进程标识已过期。");
            if (!parent.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
                throw new TimeoutException("等待应用退出超时；未启动安装器。");
        }
    }

    private static RoleInformation ReadRoleInfo(string installDirectory, ApplicationReleaseRole role)
    {
        var markerPath = Path.Combine(installDirectory, "veyon-campus-role.json");
        if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("应用角色标记不能是符号链接或重解析点。");
        var markerInfo = new FileInfo(markerPath);
        if (markerInfo.Length is < 1 or > 4096)
            throw new InvalidDataException("应用角色标记大小无效。");
        var information = JsonSerializer.Deserialize<RoleInformation>(File.ReadAllBytes(markerPath),
                              new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                          ?? throw new InvalidDataException("应用角色标记为空。");
        var expectedRole = role == ApplicationReleaseRole.TeacherConsole ? "TeacherConsole" : "StudentSetup";
        var expectedProduct = role == ApplicationReleaseRole.TeacherConsole
            ? "VeyonCampus.TeacherConsole"
            : "VeyonCampus.StudentSetup";
        if (information.SchemaVersion != 1 || information.Role != expectedRole ||
            information.Product != expectedProduct)
            throw new InvalidDataException("应用角色标记与更新角色不符。");
        _ = ApplicationReleaseClient.CompareVersions(information.Version, information.Version);
        return information;
    }

    private static void TryRestartExistingVersion(string installDirectory, string executablePath,
        ApplicationReleaseRole role, string expectedVersion)
    {
        try
        {
            var installedRoleInfo = ReadRoleInfo(installDirectory, role);
            if (!string.Equals(installedRoleInfo.Version, expectedVersion, StringComparison.Ordinal) ||
                !File.Exists(executablePath) ||
                (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
                return;
            _ = Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = installDirectory
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.ComponentModel.Win32Exception or InvalidOperationException or
                                          JsonException)
        {
        }
    }

    private static (string RoleSlug, string ExecutableName) GetRolePaths(ApplicationReleaseRole role) => role switch
    {
        ApplicationReleaseRole.TeacherConsole => ("Teacher", "VeyonCampus.Teacher.exe"),
        ApplicationReleaseRole.StudentSetup => ("Student", "VeyonCampus.StudentSetup.exe"),
        _ => throw new InvalidDataException("更新角色无效。")
    };

    private static ApplicationReleaseRole ParseRole(string roleText) => roleText switch
    {
        "TeacherConsole" => ApplicationReleaseRole.TeacherConsole,
        "StudentSetup" => ApplicationReleaseRole.StudentSetup,
        _ => throw new InvalidDataException("更新角色无效。")
    };

    private static string GetUpdateLeaseName(ApplicationReleaseRole role) => role switch
    {
        ApplicationReleaseRole.TeacherConsole => TeacherUpdateLeaseName,
        ApplicationReleaseRole.StudentSetup => StudentUpdateLeaseName,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private static bool IsValidUpdateReadyEventName(string value) =>
        value.StartsWith(UpdateHelperReadyEventPrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(value[UpdateHelperReadyEventPrefix.Length..], "N", out _);

    private static void EnsureSamePath(string actualPath, string expectedPath, string message)
    {
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(message);
    }

    private static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新目录不能是符号链接或重解析点。");
    }

    private static void WriteFailureLog(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(VeyonCampusPaths.GetLocalApplicationDataDirectory(),
                "VeyonCampus", "Updates");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "update-helper.log");
            File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} {exception.GetType().Name}: {exception.Message}{Environment.NewLine}");
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException) { }
    }

    private sealed record RoleInformation(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("product")] string Product,
        [property: JsonPropertyName("version")] string Version);
}
