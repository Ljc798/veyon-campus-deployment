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

        var helperPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Veyon Campus", "Updater", "Student", targetVersion, HelperFileName);
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

            var expectedAgentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VeyonCampus", "WebsitePolicyAgent", targetVersion, "VeyonCampus.Agent.exe");
            EnsureSamePath(agentPath, expectedAgentPath,
                "更新助手拒绝启动非固定版本目录中的 Agent。");
            PathLinkSecurity.RejectLinks(expectedAgentPath);
            WebsitePolicyAgentInstaller.ValidateAgentUpdateRollbackPaths(expectedAgentPath,
                previousAgentPath, configPath, targetVersion, previousVersion);
            var transaction = StudentUpdateAtomicTransaction.Read(configPath)
                              ?? throw new InvalidDataException("缺少学生更新原子事务记录。");
            if (transaction.CommandId != commandId ||
                transaction.State != StudentUpdateTransactionState.ActivationPending ||
                transaction.TargetAgentVersion != targetVersion ||
                transaction.PreviousAgentVersion != previousVersion ||
                !string.Equals(transaction.ConfigurationFingerprint, configFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("学生更新事务记录与 Agent 更新助手参数不匹配。");

            try
            {
                WebsitePolicyAgentInstaller.WaitForManagedAgentExit(processId, startTicks, TimeSpan.FromMinutes(2));
                WebsitePolicyAgentInstaller.StartStagedAgentUpdate(expectedAgentPath, configPath);
                if (!WebsitePolicyAgentInstaller.WaitForUpdatedAgentHealth(TimeSpan.FromSeconds(90),
                        configFingerprint, targetVersion))
                    throw new TimeoutException("新版本 Agent 未在时限内通过版本及校区配置健康读回。");
                StudentUpdateAtomicTransaction.Commit(configPath, transaction);
                return 0;
            }
            catch (Exception updateFailure)
            {
                try { StudentUpdateAtomicTransaction.Rollback(configPath, transaction, updateFailure); }
                catch (Exception rollbackFailure)
                {
                    WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath,
                        new AggregateException("新版本 Agent 更新失败，且联合回滚尚未完成。", updateFailure,
                            rollbackFailure));
                    return 1;
                }
                WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath,
                    new InvalidOperationException("新版本 Agent 更新失败；StudentSetup 和旧 Agent 已恢复。",
                        updateFailure));
                return 1;
            }
        }
        catch (Exception exception)
        {
            if (configPath is not null)
            {
                try
                {
                    var transaction = StudentUpdateAtomicTransaction.Read(configPath);
                    if (transaction is not null && transaction.State is not (StudentUpdateTransactionState.Committed or
                            StudentUpdateTransactionState.RolledBack))
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
