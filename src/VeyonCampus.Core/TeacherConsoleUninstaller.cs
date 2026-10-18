using System.Runtime.Versioning;
using System.Security.Principal;

namespace VeyonCampus.Core;

/// <summary>Removes teacher-only runtime state before the installer removes the console files.</summary>
public static class TeacherConsoleUninstaller
{
    private const string Step = "teacher-console-uninstall";
    private static readonly IReadOnlyDictionary<string, (string Port, int Profiles)> OwnedFirewallRules =
        new Dictionary<string, (string Port, int Profiles)>(StringComparer.OrdinalIgnoreCase)
        {
            ["VeyonCampusStudentUpdate"] = ("39175", 3),
            ["VeyonCampusTeacherMobileControl"] = ("39176", int.MaxValue),
            ["VeyonCampusTeacherMobileBootstrap"] = ("39177", int.MaxValue)
        };

    public static StepResult Run(bool preserveConfiguration = false)
    {
        if (!OperatingSystem.IsWindows())
            return new(Step, ExecutionPlan.Failed, "教师端卸载清理仅支持 Windows。");
        if (!IsElevated())
            return new(Step, ExecutionPlan.Failed, "教师端完整卸载需要管理员权限。");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installRoot = Path.GetFullPath(AppContext.BaseDirectory);
        if (!IsExpectedInstallRoot(installRoot, programFiles))
            return new(Step, ExecutionPlan.Failed, "完整卸载只能从已安装的 Veyon Campus 教师目录启动。");

        try
        {
            VeyonCampusProcessCleanup.StopInstalledRoleProcesses("TeacherConsole");
            if (!preserveConfiguration)
                VeyonCampusProcessCleanup.StopInstalledRoleProcesses("StudentSetup");
            RemoveOwnedFirewallRules();
            var studentInstalled = StudentSetupUninstaller.HasStudentInstallation(programFiles);
            if (!studentInstalled || !preserveConfiguration)
            {
                var agentRemoval = WebsitePolicyAgentInstaller.Uninstall();
                if (agentRemoval.Status != ExecutionPlan.Succeeded)
                    return new(Step, agentRemoval.Status,
                        "教师程序已停止，但后台 Agent 或其策略未能安全清理；保留安装文件以便检查并重试。" +
                        agentRemoval.Detail);
            }

            if (preserveConfiguration)
            {
                VeyonCampusUninstallDataCleanup.RemoveTemporaryArtifacts();
                return new(Step, ExecutionPlan.Succeeded,
                    "教师控制台进程和教师专用防火墙规则已清理；本机 Veyon Campus 和 Veyon 配置已保留，可供重新安装使用。");
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
                "教师控制台进程、防火墙规则、本机 Veyon 配置、认证密钥、地点、用户配置文件数据和系统级缓存均已清理。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.ComponentModel.Win32Exception or InvalidOperationException or
                                          System.Security.SecurityException or System.Runtime.InteropServices.COMException or
                                          Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or
                                          System.Security.Cryptography.CryptographicException)
        {
            return new(Step, ExecutionPlan.NeedsReview,
                $"教师端卸载清理未完成，安装文件已保留：{exception.Message}");
        }
    }

    internal static bool IsExpectedInstallRoot(string installRoot, string programFiles)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(programFiles)) return false;
        try
        {
            var expected = Path.GetFullPath(Path.Combine(programFiles, "Veyon Campus", "Teacher"));
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)), expected,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    internal static void RemoveOwnedFirewallRules()
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                         ?? throw new IOException("无法连接 Windows 防火墙以清理教师端规则。");
        dynamic policy = Activator.CreateInstance(policyType)
                         ?? throw new IOException("无法创建 Windows 防火墙管理对象。");
        try
        {
            var rulesByName = OwnedFirewallRules.Keys.ToDictionary(name => name,
                _ => new List<object>(), StringComparer.OrdinalIgnoreCase);
            foreach (dynamic rule in policy.Rules)
            {
                var name = (string)rule.Name;
                if (!rulesByName.TryGetValue(name, out var matches)) continue;
                var expected = OwnedFirewallRules[name];
                if ((int)rule.Protocol != 6 || (string)rule.LocalPorts != expected.Port ||
                    (int)rule.Direction != 1 || (int)rule.Action != 1 || !(bool)rule.Enabled ||
                    !string.Equals((string)rule.RemoteAddresses, "LocalSubnet", StringComparison.OrdinalIgnoreCase) ||
                    (int)rule.Profiles != expected.Profiles ||
                    !string.IsNullOrEmpty((string)rule.ServiceName) ||
                    !string.Equals((string)rule.LocalAddresses, "*", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals((string)rule.RemotePorts, "*", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty((string)rule.ApplicationName))
                    throw new IOException($"防火墙规则 {name} 的设置与本工具创建的规则不符；未删除该规则。");
                matches.Add(rule);
            }

            foreach (var (name, matches) in rulesByName)
                if (matches.Count > 0) policy.Rules.Remove(name);

            foreach (dynamic rule in policy.Rules)
                if (OwnedFirewallRules.ContainsKey((string)rule.Name))
                    throw new IOException("教师端防火墙规则删除后仍存在。");
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(policy))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(policy);
        }
    }

}
