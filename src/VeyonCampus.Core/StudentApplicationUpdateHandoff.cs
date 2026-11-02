using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public static class StudentApplicationUpdateHandoff
{
    private const string HelperFileName = "VeyonCampus.UpdateHelper.exe";

    [SupportedOSPlatform("windows")]
    public static void Start(string expectedAgentExecutablePath, string previousAgentExecutablePath,
        string configPath, string targetVersion, string previousVersion, string expectedConfigFingerprint,
        Guid commandId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("学生网站策略代理更新仅支持 Windows。");
        EnsureSystemIdentity();
        _ = ApplicationReleaseClient.CompareVersions(targetVersion, targetVersion);
        if (!Regex.IsMatch(expectedConfigFingerprint, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("新 Agent 健康读回指纹无效。");
        if (commandId == Guid.Empty) throw new InvalidDataException("学生更新事务编号无效。");

        var expectedAgentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "WebsitePolicyAgent", targetVersion, "VeyonCampus.Agent.exe");
        EnsureSamePath(expectedAgentExecutablePath, expectedAgentPath,
            "新 Agent 必须位于版本隔离的受保护 ProgramData 目录。");
        var fullConfigPath = ValidateConfigPath(configPath);
        WebsitePolicyAgentInstaller.ValidateAgentUpdateRollbackPaths(expectedAgentPath,
            previousAgentExecutablePath, fullConfigPath, targetVersion, previousVersion);
        var transaction = StudentUpdateAtomicTransaction.Read(fullConfigPath)
                          ?? throw new InvalidDataException("缺少学生更新原子事务记录；拒绝切换 Agent。");
        if (transaction.CommandId != commandId || transaction.State != StudentUpdateTransactionState.ActivationPending ||
            transaction.TargetAgentVersion != targetVersion || transaction.PreviousAgentVersion != previousVersion)
            throw new InvalidDataException("学生更新原子事务记录与 Agent 切换命令不匹配。");
        PathLinkSecurity.RejectLinks(expectedAgentPath);
        if (!File.Exists(expectedAgentPath))
            throw new FileNotFoundException("ProgramData 中缺少已暂存的新 Agent。", expectedAgentPath);

        var helperPath = GetUpdateHelperPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), transaction.TargetSetupVersion);
        PathLinkSecurity.RejectLinks(helperPath);
        if (!File.Exists(helperPath))
            throw new FileNotFoundException("StudentSetup 安装器没有部署对应版本的更新助手。", helperPath);

        using var currentProcess = Process.GetCurrentProcess();
        var startInfo = new ProcessStartInfo(helperPath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(helperPath)!
        };
        startInfo.ArgumentList.Add("--start-student-agent");
        startInfo.ArgumentList.Add(expectedAgentPath);
        startInfo.ArgumentList.Add(Path.GetFullPath(previousAgentExecutablePath));
        startInfo.ArgumentList.Add(fullConfigPath);
        startInfo.ArgumentList.Add(currentProcess.Id.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(currentProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(targetVersion);
        startInfo.ArgumentList.Add(previousVersion);
        startInfo.ArgumentList.Add(expectedConfigFingerprint);
        startInfo.ArgumentList.Add(commandId.ToString("N"));
        using var helper = Process.Start(startInfo)
                           ?? throw new InvalidOperationException("无法启动 Student Agent 更新助手。");
        try
        {
            StudentUpdateAtomicTransaction.RecordHandoffHelperStarted(fullConfigPath, commandId, helper.Id,
                helper.StartTime.ToUniversalTime().Ticks);
        }
        catch
        {
            try
            {
                if (!helper.HasExited) helper.Kill(entireProcessTree: false);
                helper.WaitForExit(5000);
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                              System.ComponentModel.Win32Exception) { }
            throw;
        }
    }

    internal static string GetUpdateHelperPath(string programFilesDirectory, string studentSetupVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programFilesDirectory);
        _ = ApplicationReleaseClient.CompareVersions(studentSetupVersion, studentSetupVersion);
        return Path.Combine(programFilesDirectory,
            "Veyon Campus", "Updater", "Student", studentSetupVersion, HelperFileName);
    }

    public static int RunHelper(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        string? configPath = null;
        try
        {
            if (args.Length != 10 || args[0] != "--start-student-agent")
                return 2;
            var agentPath = args[1];
            var previousAgentPath = args[2];
            configPath = ValidateConfigPath(args[3]);
            var processIdText = args[4];
            var startTicksText = args[5];
            var targetVersion = args[6];
            var previousVersion = args[7];
            var configFingerprint = args[8];
            if (!Guid.TryParseExact(args[9], "N", out var commandId) || commandId == Guid.Empty)
                throw new InvalidDataException("学生更新事务编号无效。");
            EnsureSystemIdentity();
            using var updateLock = StudentUpdateAtomicTransaction.AcquireMachineLock(TimeSpan.FromSeconds(90));
            if (!int.TryParse(processIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ||
                processId <= 0 ||
                !long.TryParse(startTicksText, NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks) ||
                startTicks <= 0)
                throw new InvalidDataException("待退出 Student Agent 进程标识无效。");
            _ = ApplicationReleaseClient.CompareVersions(targetVersion, targetVersion);
            _ = ApplicationReleaseClient.CompareVersions(previousVersion, previousVersion);
            if (!Regex.IsMatch(configFingerprint, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("新 Agent 健康读回指纹无效。");

            var transaction = StudentUpdateAtomicTransaction.Read(configPath)
                              ?? throw new InvalidDataException("缺少学生更新原子事务记录。");
            if (transaction.CommandId != commandId ||
                transaction.State != StudentUpdateTransactionState.ActivationPending ||
                transaction.TargetAgentVersion != targetVersion ||
                transaction.PreviousAgentVersion != previousVersion ||
                !string.Equals(transaction.ConfigurationFingerprint, configFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("学生更新事务记录与 Agent 更新助手参数不匹配。");
            StudentUpdateAtomicTransaction.VerifyTargetSetupInstalled(transaction);
            var expectedHelperPath = GetUpdateHelperPath(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), transaction.TargetSetupVersion);
            EnsureSamePath(Environment.ProcessPath ?? "", expectedHelperPath,
                "更新助手不属于本次已验真的 StudentSetup 版本目录。");

            var expectedAgentPath = StudentUpdateAtomicTransaction.GetAgentExecutablePath(targetVersion);
            EnsureSamePath(agentPath, expectedAgentPath,
                "更新助手拒绝启动非固定版本目录中的 Agent。");
            PathLinkSecurity.RejectLinks(expectedAgentPath);
            var aliasDirectoryToClean = (string?)null;
            WebsitePolicyAgentInstaller.WaitForManagedAgentExit(processId, startTicks, TimeSpan.FromMinutes(2));

            var setupAgentDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Veyon Campus", "Student", "WebsitePolicyAgent");
            var setupAgentExecutable = Path.Combine(setupAgentDirectory, "VeyonCampus.Agent.exe");
            PathLinkSecurity.RejectLinks(setupAgentDirectory);
            PathLinkSecurity.RejectLinks(setupAgentExecutable);
            PathLinkSecurity.RejectLinks(Path.ChangeExtension(setupAgentExecutable, ".dll"));
            var bundledAgentVersion = WorkerInstallationGuard.GetAssemblyProductVersion(setupAgentExecutable);
            var legacySetupVersionAlias = string.Equals(targetVersion, transaction.TargetSetupVersion,
                    StringComparison.Ordinal) &&
                string.Equals(transaction.TargetAgentVersion, transaction.TargetSetupVersion,
                    StringComparison.Ordinal) &&
                !string.Equals(bundledAgentVersion, targetVersion, StringComparison.Ordinal);
            if (legacySetupVersionAlias)
            {
                if (!string.Equals(WebsitePolicyAgentInstaller.BuildVersion, bundledAgentVersion,
                        StringComparison.Ordinal) ||
                    !WebsitePolicyAgentInstaller.StagedAgentMatchesSetupPayload(setupAgentDirectory, targetVersion))
                    throw new InvalidDataException("旧版事务把 StudentSetup 版本当作 Agent 版本，但暂存 Agent 与已验签 Setup 内置文件不一致；已停止切换。");
                if (ApplicationReleaseClient.CompareVersions(bundledAgentVersion, previousVersion) < 0)
                    throw new InvalidDataException("StudentSetup 内置 Agent 低于现有 Agent；旧版别名事务不能安全升级。");

                if (string.Equals(bundledAgentVersion, previousVersion, StringComparison.Ordinal))
                {
                    WebsitePolicyAgentInstaller.RestoreAgentTaskActionForTransaction(expectedAgentPath,
                        previousAgentPath, configPath);
                    transaction = StudentUpdateAtomicTransaction.RebaseLegacySetupVersionAgentAlias(configPath,
                        transaction, targetVersion, bundledAgentVersion);
                    WebsitePolicyAgentInstaller.StartCurrentAgentTask(configPath);
                    if (!WebsitePolicyAgentInstaller.WaitForUpdatedAgentHealth(TimeSpan.FromSeconds(30),
                            configFingerprint, bundledAgentVersion))
                        throw new TimeoutException("StudentSetup 已更新，但恢复后的现有 Agent 未通过配置和健康检查。");
                    StudentUpdateAtomicTransaction.Commit(configPath, transaction);
                    TryRemoveLegacySetupVersionAlias(setupAgentDirectory, targetVersion, configPath, commandId);
                    return 0;
                }

                var staged = WebsitePolicyAgentInstaller.StageAgentUpdate(setupAgentDirectory,
                    bundledAgentVersion, configPath);
                WebsitePolicyAgentInstaller.RestoreAgentTaskActionForTransaction(expectedAgentPath,
                    previousAgentPath, configPath);
                transaction = StudentUpdateAtomicTransaction.RebaseLegacySetupVersionAgentAlias(configPath,
                    transaction, targetVersion, bundledAgentVersion);
                targetVersion = bundledAgentVersion;
                agentPath = staged.TargetExecutablePath;
                expectedAgentPath = StudentUpdateAtomicTransaction.GetAgentExecutablePath(targetVersion);
                WebsitePolicyAgentInstaller.ActivateStagedAgentUpdate(expectedAgentPath,
                    previousAgentPath, configPath);
                aliasDirectoryToClean = args[6];
            }
            WebsitePolicyAgentInstaller.ValidateAgentUpdateRollbackPaths(expectedAgentPath,
                previousAgentPath, configPath, targetVersion, previousVersion);

            var activationResult = ExecuteAgentActivation(processId, startTicks, expectedAgentPath, configPath,
                configFingerprint, targetVersion, commandId, transaction);
            if (activationResult == 0 && aliasDirectoryToClean is not null)
                TryRemoveLegacySetupVersionAlias(setupAgentDirectory, aliasDirectoryToClean, configPath, commandId);
            return activationResult;
        }
        catch (Exception exception)
        {
            if (configPath is not null)
            {
                try
                {
                    var transaction = StudentUpdateAtomicTransaction.Read(configPath);
                    if (transaction is not null && transaction.State is not (StudentUpdateTransactionState.Committed or
                            StudentUpdateTransactionState.RolledBack or StudentUpdateTransactionState.Superseded))
                        StudentUpdateAtomicTransaction.Rollback(configPath, transaction, exception);
                }
                catch (Exception recoveryException)
                {
                    WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath,
                        new AggregateException("Student Agent 更新助手异常；自动恢复仍待重试。", exception,
                            recoveryException));
                    return 1;
                }
                WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath, exception);
            }
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryRemoveLegacySetupVersionAlias(string sourceDirectory, string aliasVersion,
        string configPath, Guid commandId)
    {
        try
        {
            WebsitePolicyAgentInstaller.RemoveLegacyAgentSetupVersionAlias(sourceDirectory, aliasVersion,
                configPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or ArgumentException or InvalidOperationException)
        {
            WebsitePolicyAgentInstaller.ReportAgentOperationFailure(configPath, "legacy-agent-alias-cleanup",
                commandId.ToString("N")[..12], exception);
        }
    }

    [SupportedOSPlatform("windows")]
    private static int ExecuteAgentActivation(int processId, long startTicks, string expectedAgentPath,
        string configPath, string configFingerprint, string targetVersion, Guid commandId,
        StudentUpdateTransactionRecord transaction)
    {
        using var activationTiming = StudentUpdatePhaseTimingStore.Begin(configPath, commandId,
            StudentUpdatePhase.AgentActivate);
        StudentUpdatePhaseTimingRecorder? healthTiming = null;
        try
        {
            var result = ApplyAgentUpdateWithRollback(
                () => WebsitePolicyAgentInstaller.WaitForManagedAgentExit(processId, startTicks,
                    TimeSpan.FromMinutes(2)),
                () => WebsitePolicyAgentInstaller.StartStagedAgentUpdate(expectedAgentPath, configPath),
                () => activationTiming.Complete(),
                () =>
                {
                    healthTiming = StudentUpdatePhaseTimingStore.Begin(configPath, commandId,
                        StudentUpdatePhase.HealthAndCommit);
                    var healthy = WebsitePolicyAgentInstaller.WaitForUpdatedAgentHealth(TimeSpan.FromSeconds(90),
                        configFingerprint, targetVersion);
                    if (!healthy)
                    {
                        healthTiming.Complete(succeeded: false);
                        healthTiming = null;
                    }
                    return healthy;
                },
                () =>
                {
                    try
                    {
                        StudentUpdateAtomicTransaction.Commit(configPath, transaction);
                        healthTiming?.Complete();
                    }
                    catch
                    {
                        healthTiming?.Complete(succeeded: false);
                        throw;
                    }
                    finally { healthTiming = null; }
                },
                updateFailure =>
                {
                    using var rollbackTiming = StudentUpdatePhaseTimingStore.Begin(configPath, commandId,
                        StudentUpdatePhase.Rollback);
                    try
                    {
                        StudentUpdateAtomicTransaction.Rollback(configPath, transaction, updateFailure);
                        rollbackTiming.Complete();
                    }
                    catch
                    {
                        rollbackTiming.Complete(succeeded: false);
                        throw;
                    }
                },
                failure => WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath, failure));
            activationTiming.Complete(succeeded: result == 0);
            return result;
        }
        catch
        {
            activationTiming.Complete(succeeded: false);
            throw;
        }
        finally { healthTiming?.Complete(succeeded: false); }
    }

    private static int ApplyAgentUpdateWithRollback(Action waitForPreviousExit, Action startUpdated,
        Action candidateStarted, Func<bool> waitForUpdatedHealth, Action commitUpdate, Action<Exception> restorePrevious,
        Action<Exception> reportFailure)
    {
        try
        {
            waitForPreviousExit();
            startUpdated();
            candidateStarted();
            if (!waitForUpdatedHealth())
                throw new TimeoutException("新版本 Agent 未在时限内通过版本及校区配置健康读回。");
            commitUpdate();
            return 0;
        }
        catch (Exception updateFailure)
        {
            try { restorePrevious(updateFailure); }
            catch (Exception rollbackFailure)
            {
                reportFailure(new AggregateException("新版本 Agent 更新失败，且联合回滚尚未完成。", updateFailure,
                    rollbackFailure));
                return 1;
            }
            reportFailure(new InvalidOperationException("新版本 Agent 更新失败；StudentSetup 和旧 Agent 已恢复。",
                updateFailure));
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ValidateConfigPath(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        var configRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "WebsitePolicy"));
        var rootPrefix = configRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Student Agent 配置路径不在固定 ProgramData 根目录中。");
        var relative = Path.GetRelativePath(configRoot, fullPath);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[0-9A-Fa-f]{24}$", RegexOptions.CultureInvariant) ||
            !string.Equals(parts[1], "agent-" + parts[0] + ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Student Agent 配置文件路径格式无效。");
        PathLinkSecurity.RejectLinks(fullPath);
        return fullPath;
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureSystemIdentity()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18")
            throw new UnauthorizedAccessException("Student Agent 更新助手只能由 SYSTEM 任务执行。");
    }

    private static void EnsureSamePath(string actualPath, string expectedPath, string message)
    {
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(message);
    }
}
