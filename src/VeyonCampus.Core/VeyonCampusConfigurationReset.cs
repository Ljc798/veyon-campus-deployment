using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace VeyonCampus.Core;

/// <summary>Clears Veyon Campus data and the complete local Veyon configuration after explicit confirmation.</summary>
public static class VeyonCampusConfigurationReset
{
    private const string VeyonConfigurationKey = @"SOFTWARE\Veyon Solutions";

    public static string ClearInstalledConfiguration(string role, int parentProcessId, long parentStartTicks)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("清空本机配置仅支持 Windows。");
        if (!IsElevated())
            throw new UnauthorizedAccessException("清空本机和 Veyon 配置需要管理员权限。");
        if (parentProcessId <= 0 || parentStartTicks <= 0)
            throw new ArgumentException("待关闭的控制台进程标识无效。");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var expectedInstallRoot = role switch
        {
            "TeacherConsole" => TeacherConsoleUninstaller.IsExpectedInstallRoot(installRoot, programFiles),
            "StudentSetup" => StudentSetupUninstaller.IsExpectedInstallRoot(installRoot, programFiles),
            _ => false
        };
        if (!expectedInstallRoot)
            throw new InvalidOperationException("清空配置只能从已安装的 Veyon Campus 程序启动。");

        WaitForParentToExit(parentProcessId, parentStartTicks);
        VeyonCampusProcessCleanup.StopInstalledRoleProcesses("TeacherConsole");
        VeyonCampusProcessCleanup.StopInstalledRoleProcesses("StudentSetup");

        var agentRemoval = WebsitePolicyAgentInstaller.Uninstall();
        if (agentRemoval.Status != ExecutionPlan.Succeeded)
            throw new IOException("学生策略 Agent 或其策略尚未安全恢复：" + agentRemoval.Detail);

        StudentSetupIntegrationCleanup.RemoveOwnedIntegration(removeShortcuts: false);
        TeacherConsoleUninstaller.RemoveOwnedFirewallRules();
        ClearVeyonConfiguration();
        WebsitePolicySigningKeyStore.RemoveProductOwnedKeys();
        MobileControlIdentityCleanup.RemoveOwnedIdentity();
        VeyonCampusUninstallDataCleanup.RemoveAllUserProductData(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        VeyonCampusUninstallDataCleanup.RemoveAllMachineProductData(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        VeyonCampusProductRegistryCleanup.RemoveAll();

        return "本机 Veyon Campus 配置和缓存已清空；Veyon 全局配置、认证密钥及地点也已重置。软件仍保留，请重新打开后从首次设置开始。";
    }

    public static void ClearVeyonConfiguration(bool restartService = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("清空 Veyon 配置仅支持 Windows。");

        StopVeyonConfigurationUis();
        var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath();
        if (cliPath is not null)
        {
            ClearVeyonNetworkObjects(cliPath);
            ClearVeyonAuthenticationKeys(cliPath);
            RunVeyonCommand(cliPath, ["config", "clear"], "清空 Veyon 系统配置");
        }
        else if (VeyonFacts.Probe().Status != VeyonFacts.NotInstalled)
            throw new IOException("检测到 Veyon 安装，但找不到 Veyon CLI；无法确认内置地点和认证密钥已清理。请修复 Veyon 后重试。");

        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path, "Veyon"));
        VeyonCampusUninstallDataCleanup.RemoveDefaultVeyonAuthenticationKeys(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), programFiles);

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            hive.DeleteSubKeyTree(VeyonConfigurationKey, throwOnMissingSubKey: false);
            using var readback = hive.OpenSubKey(VeyonConfigurationKey, writable: false);
            if (readback is not null)
                throw new IOException($"Veyon 配置在 {view} 注册表视图中仍存在；没有报告清理成功。");
        }

        if (!restartService) return;

        var facts = VeyonFacts.Probe();
        if (facts.ServiceRuntimeState is null)
        {
            if (facts.Status == "installed")
                throw new IOException("Veyon 配置已删除，但无法确认 VeyonService 状态；请检查服务后重试。");
            return;
        }

        var service = WindowsVeyonAdapter.RestartVeyonService(new ProcessRunner());
        if (!service.Ok)
            throw new IOException("Veyon 配置已删除，但服务未能重新读取默认配置：" + service.Detail);
    }

    private static void ClearVeyonNetworkObjects(string cliPath) =>
        RunVeyonCommand(cliPath, ["networkobjects", "clear"], "清空 Veyon 地点和电脑名单");

    private static void ClearVeyonAuthenticationKeys(string cliPath)
    {
        var listing = RunVeyonCommand(cliPath, ["authkeys", "list"], "读取 Veyon 认证密钥清单",
            outputLimitChars: 1_048_576);
        if (listing.StdoutTruncated)
            throw new IOException("Veyon 认证密钥清单超出读取限制；没有继续清空配置。");
        var entries = VeyonTeacherKeyProvisioner.ParseListingEntries(listing.Stdout);
        if (entries is null)
            throw new InvalidDataException("Veyon 认证密钥清单格式无法核验；没有继续清空配置。");

        foreach (var entry in entries)
            RunVeyonCommand(cliPath, ["authkeys", "delete", entry], $"删除 Veyon 认证密钥 {entry}");

        var readback = RunVeyonCommand(cliPath, ["authkeys", "list"], "复核 Veyon 认证密钥清单",
            outputLimitChars: 1_048_576);
        if (readback.StdoutTruncated || VeyonTeacherKeyProvisioner.ParseListingEntries(readback.Stdout) is not { Count: 0 })
            throw new IOException("删除后仍读到 Veyon 认证密钥；没有报告清空成功。");
    }

    private static ProcessRunner RunVeyonCommand(string cliPath, IReadOnlyList<string> arguments, string action,
        int outputLimitChars = 2048)
    {
        var runner = new ProcessRunner();
        runner.Run(cliPath, arguments, Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(30),
            outputLimitChars: outputLimitChars);
        if (runner.ExitCode is not 0)
        {
            var detail = string.IsNullOrWhiteSpace(runner.Stderr) ? runner.Stdout : runner.Stderr;
            detail = detail.Trim();
            if (detail.Length > 300) detail = detail[..300] + "…";
            throw new IOException($"{action}失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）。{detail}");
        }
        return runner;
    }

    [SupportedOSPlatform("windows")]
    private static void StopVeyonConfigurationUis()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Veyon"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Veyon")
        }.Where(Directory.Exists).Select(Path.GetFullPath).ToArray();

        foreach (var processName in new[] { "veyon-master", "veyon-configurator" })
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                string imagePath;
                try
                {
                    imagePath = Path.GetFullPath(process.MainModule?.FileName
                                                 ?? throw new IOException("无法读取 Veyon 界面进程路径。"));
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                                  System.Security.SecurityException or IOException)
                {
                    throw new IOException("无法核验 Veyon 界面进程路径；配置未清空。", exception);
                }
                if (!roots.Any(root => IsWithin(imagePath, root))) continue;
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(10_000))
                        throw new IOException($"{processName} 在 10 秒内未关闭。");
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    if (!process.HasExited)
                        throw new IOException($"无法关闭 Veyon 程序 {processName}。", exception);
                }
            }
        }
    }

    private static void WaitForParentToExit(int processId, long startTicks)
    {
        Process parent;
        try { parent = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }

        using (parent)
        {
            try
            {
                if (parent.StartTime.ToUniversalTime().Ticks != startTicks) return;
                if (!parent.WaitForExit((int)TimeSpan.FromSeconds(90).TotalMilliseconds))
                    throw new IOException("控制台窗口没有退出；没有清理打开中的配置文件。");
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                if (!parent.HasExited)
                    throw new IOException("无法确认控制台进程已经退出；没有清理配置。", exception);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class VeyonCampusProductRegistryCleanup
{
    [SupportedOSPlatform("windows")]
    public static void RemoveAll()
    {
        const string path = @"Software\VeyonCampus";
        using (var userHive = Registry.CurrentUser)
        {
            userHive.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            using var readback = userHive.OpenSubKey(path, writable: false);
            if (readback is not null)
                throw new IOException("Veyon Campus 当前用户注册表配置仍存在。");
        }

        using (var users = Registry.Users.OpenSubKey("", writable: true)
                           ?? throw new IOException("无法读取 Windows 用户注册表配置。"))
        {
            foreach (var sid in users.GetSubKeyNames().Where(name =>
                         System.Text.RegularExpressions.Regex.IsMatch(name,
                             @"^S-1-5-21-\d+-\d+-\d+-\d+$",
                             System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            {
                var userProductPath = $@"{sid}\{path}";
                users.DeleteSubKeyTree(userProductPath, throwOnMissingSubKey: false);
                using var readback = users.OpenSubKey(userProductPath, writable: false);
                if (readback is not null)
                    throw new IOException($"Veyon Campus 用户注册表配置仍存在（{sid}）。");
            }
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            hive.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            using var readback = hive.OpenSubKey(path, writable: false);
            if (readback is not null)
                throw new IOException($"Veyon Campus 系统注册表配置在 {view} 视图中仍存在。");
        }
    }
}
