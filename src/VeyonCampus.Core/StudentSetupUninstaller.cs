using System.Runtime.Versioning;

namespace VeyonCampus.Core;

/// <summary>Stops the installed student application, then safely removes its managed Agent before Inno deletes files.</summary>
public static class StudentSetupUninstaller
{
    private const string Step = "student-setup-uninstall";
    public static StepResult Run(bool preserveConfiguration = false)
    {
        if (!OperatingSystem.IsWindows())
            return new(Step, ExecutionPlan.Failed, "学生端卸载仅支持 Windows。");
        if (!IsElevated())
            return new(Step, ExecutionPlan.Failed, "学生端完整卸载需要管理员权限。");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installRoot = Path.GetFullPath(AppContext.BaseDirectory);
        if (!IsExpectedInstallRoot(installRoot, programFiles))
            return new(Step, ExecutionPlan.Failed, "完整卸载只能从已安装的 Veyon Campus 学生目录启动。");

        try
        {
            VeyonCampusProcessCleanup.StopInstalledRoleProcesses("StudentSetup");
            if (!preserveConfiguration)
                VeyonCampusProcessCleanup.StopInstalledRoleProcesses("TeacherConsole");
            var agentRemoval = WebsitePolicyAgentInstaller.Uninstall();
            if (agentRemoval.Status != ExecutionPlan.Succeeded)
                return new(Step, agentRemoval.Status,
                    "学生程序已停止，但后台 Agent 或其策略未能安全清理；保留安装文件以便检查并重试。" +
                    agentRemoval.Detail);
            StudentSetupIntegrationCleanup.RemoveOwnedIntegration();
            if (preserveConfiguration)
            {
                VeyonCampusUninstallDataCleanup.RemoveTemporaryArtifacts();
                return new(Step, ExecutionPlan.Succeeded,
                    "学生程序进程、后台 Agent、已部署学生限制及 Edge 启动集成已清理；本机 Veyon Campus 和 Veyon 配置已保留，可供重新安装使用。可以删除学生端安装文件。");
            }

            VeyonCampusConfigurationReset.ClearVeyonConfiguration(restartService: false);
            WebsitePolicySigningKeyStore.RemoveProductOwnedKeys();
            MobileControlIdentityCleanup.RemoveOwnedIdentity();
            VeyonCampusUninstallDataCleanup.RemoveAllUserProductData(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            VeyonCampusUninstallDataCleanup.RemoveAllMachineProductData(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            VeyonCampusProductRegistryCleanup.RemoveAll();
            return new(Step, ExecutionPlan.Succeeded,
                "学生程序进程、后台 Agent、Edge 启动集成、本机 Veyon 配置、认证密钥、地点、用户配置文件数据和系统级缓存均已清理。可以删除学生端安装文件。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.ComponentModel.Win32Exception or InvalidOperationException or
                                          System.Security.SecurityException or System.Security.Cryptography.CryptographicException)
        {
            return new(Step, ExecutionPlan.NeedsReview,
                $"学生端停止进程或清理后台 Agent 时失败：{exception.Message}；安装文件已保留。");
        }
    }

    internal static bool IsExpectedInstallRoot(string installRoot, string programFiles)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(programFiles)) return false;
        try
        {
            var expected = Path.GetFullPath(Path.Combine(programFiles, "Veyon Campus", "Student"));
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)), expected,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool HasTeacherInstallation(string programFiles)
    {
        if (string.IsNullOrWhiteSpace(programFiles)) return false;
        try
        {
            return Directory.Exists(Path.Combine(Path.GetFullPath(programFiles), "Veyon Campus", "Teacher"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool HasStudentInstallation(string programFiles)
    {
        if (string.IsNullOrWhiteSpace(programFiles)) return false;
        try
        {
            return Directory.Exists(Path.Combine(Path.GetFullPath(programFiles), "Veyon Campus", "Student"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void RemoveStudentUserData(string localApplicationData, bool preserveTeacherData)
        => VeyonCampusUninstallDataCleanup.RemoveStudentData(localApplicationData, preserveTeacherData);

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

}
