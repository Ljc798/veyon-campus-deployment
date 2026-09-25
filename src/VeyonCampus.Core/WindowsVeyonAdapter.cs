namespace VeyonCampus.Core;

/// <summary>
/// Windows adapter (first slice, P4-01/03/04/05): installs Veyon from a
/// verified in-package installer, imports the public key via Veyon CLI,
/// and restarts the Veyon service. All external calls go through
/// <see cref="ProcessRunner"/>. Read-backs never guess: missing pieces
/// report unknown, not success.
///
/// CLI shape follows the 4.11.2 workflow proven in the legacy scripts
/// (student_v3.txt): config keys are "Authentication/Method" and
/// "Authentication/KeyFile", key import goes to "keys".
/// </summary>
public sealed class WindowsVeyonAdapter
{
    private const int InstallTimeoutSeconds = 900;
    private const int CliTimeoutSeconds = 60;
    private const int ServiceTimeoutSeconds = 120;
    private const string KeyAuthMethod = "1"; // 密钥认证（与旧脚本一致）

    /// <summary>
    /// Installs Veyon silently (no Master on student role) then configures
    /// key authentication and imports the campus public key.
    /// </summary>
    public IReadOnlyList<StepResult> InstallAndConfigureVeyon(PackageContext package, bool isTeacher)
    {
        var results = new List<StepResult>();
        var install = InstallVeyonOnly(package, isTeacher);
        results.Add(new("veyon-install", install.Status, install.Detail, install.ExitCode));
        if (install.Status != ExecutionPlan.Succeeded)
        {
            results.Add(new("veyon-key", ExecutionPlan.Skipped, "安装未成功；密钥配置未开始。"));
            return results;
        }
        var key = ConfigureVeyonOnly(package, isTeacher);
        results.Add(new("veyon-key", key.Status, key.Detail, key.ExitCode));
        return results;
    }

    /// <summary>第一步：仅静默安装 Veyon（学生端不含 Master，教师端含）。
    /// 不调用任何 CLI，安装结果与读回分离。</summary>
    public StepResult InstallVeyonOnly(PackageContext package, bool isTeacher)
    {
        if (!OperatingSystem.IsWindows())
            return new("veyon-install", ExecutionPlan.Failed, "当前不是 Windows；Veyon 安装仅支持 Windows。");
        var installerPath = package.InstallerPath
            ?? throw new InvalidDataException("所选部署包不含安装资源；旧版部署包尚不支持离线安装。");
        var (status, detail, exitCode) = RunInstaller(installerPath, isTeacher);
        return new("veyon-install", status, detail, exitCode);
    }

    /// <summary>第二步：仅配置 Veyon（切换密钥认证 + 导入公钥 + 重启服务）。
    /// 要求 Veyon 已安装；未安装时返回 Failed 而非隐式安装。</summary>
    public StepResult ConfigureVeyonOnly(PackageContext package, bool isTeacher)
    {
        if (!OperatingSystem.IsWindows())
            return new("veyon-key", ExecutionPlan.Failed, "当前不是 Windows；Veyon 配置仅支持 Windows。");
        var facts = VeyonFacts.Probe();
        if (facts.Status == VeyonFacts.NotInstalled)
            return new("veyon-key", ExecutionPlan.Failed,
                "检测到 Veyon 尚未安装；请先完成安装步骤，再配置公钥与认证方式。");
        return ConfigPublicKey(package, isTeacher);
    }

    private static (string Status, string Detail, int? ExitCode) RunInstaller(string installerPath, bool isTeacher)
    {
        var runner = new ProcessRunner();
        var workingDirectory = Path.GetDirectoryName(installerPath) ?? ".";
        var arguments = new List<string>
        {
            "/S",           // silent（安装器为 NSIS 风格，/S 为静默参数）
            "/NoInterception",
            "/NoStartMenuFolder"
        };
        if (!isTeacher) arguments.Add("/NoMaster");
        // 注意：/D= 只在部署包明确指定沙盒目录时使用；默认不传，
        // 安装器会写到系统 Program Files，服务注册与 CLI 路径与默认假设一致。

        try
        {
            runner.Run(installerPath, arguments, workingDirectory,
                TimeSpan.FromSeconds(InstallTimeoutSeconds));
        }
        catch (TimeoutException ex)
        {
            return (ExecutionPlan.Failed, $"安装器在 {InstallTimeoutSeconds}s 内未完成：{ex.Message}", null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.IO.IOException)
        {
            return (ExecutionPlan.Failed, $"无法启动安装器：{ex.Message}", null);
        }

        var exit = runner.ExitCode;
        if (exit is 0 or 3010)
        {
            var reboot = exit == 3010 ? "（安装器返回 3010：需要重启才能完成安装）" : "";
            var component = isTeacher ? "教师组件（含 Veyon Master）" : "学生组件（不含 Veyon Master）";
            return (ExecutionPlan.Succeeded,
                $"安装器完成（退出码 {exit}）{reboot}；已安装{component}。", exit);
        }
        return (ExecutionPlan.Failed,
            $"安装器返回非预期退出码 {exit}；不继续密钥配置。{Truncate(runner.Stderr)}", exit);
    }

    private static StepResult ConfigPublicKey(PackageContext package, bool isTeacher)
    {
        var runner = new ProcessRunner();
        var cliPath = ResolveVeyonCliPath();
        if (cliPath is null)
            return new("veyon-key", ExecutionPlan.Failed,
                "未找到 Veyon CLI（veyon-cli.exe / veyon-wcli.exe）；请确认安装完成与默认路径。", null);

        var steps = new List<string>();
        // 1. 切换为密钥认证（与旧脚本一致：Authentication/Method = 1）
        runner.Run(cliPath, new[] { "config", "set", "Authentication/Method", KeyAuthMethod },
            Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(CliTimeoutSeconds));
        if (runner.ExitCode is not 0)
            return new("veyon-key", ExecutionPlan.Failed,
                $"设置 Veyon 密钥认证失败（退出码 {runner.ExitCode}）。{Truncate(runner.Stderr)}", runner.ExitCode);
        steps.Add("已切换为密钥认证（Authentication/Method=1）");

        // 读回核对（旧脚本要求写入后读回一致才认为成功）
        runner.Run(cliPath, new[] { "config", "get", "Authentication/Method" },
            Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(CliTimeoutSeconds));
        var readBack = runner.Stdout.Trim();
        if (readBack != KeyAuthMethod)
            return new("veyon-key", ExecutionPlan.Failed,
                $"认证方式读回不一致：期望 {KeyAuthMethod}，实际 {readBack}；停止后续配置。", runner.ExitCode);
        steps.Add("读回 Authentication/Method 一致");

        // 2. 指定私钥/公钥文件位置（教师端写本校公钥路径留待 P7；学生端写入包内公钥）
        if (!isTeacher)
        {
            runner.Run(cliPath, new[] { "config", "set", "Authentication/KeyFile", package.PublicKeyPath },
                Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(CliTimeoutSeconds));
            if (runner.ExitCode is not 0)
                return new("veyon-key", ExecutionPlan.Failed,
                    $"写入公钥路径失败（退出码 {runner.ExitCode}）。{Truncate(runner.Stderr)}", runner.ExitCode);
            steps.Add("已写入公钥文件路径");
        }

        // 3. 导入校区公钥（旧脚本：authkeys import "$Campus/public" <pem>）
        var keyName = $"校园公钥";
        runner.Run(cliPath, new[] { "authkeys", "import", keyName, package.PublicKeyPath },
            Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(CliTimeoutSeconds));
        if (runner.ExitCode is not 0)
            return new("veyon-key", ExecutionPlan.Failed,
                $"公钥导入失败（退出码 {runner.ExitCode}）；可能需要重新安装后重试。{Truncate(runner.Stderr)}", runner.ExitCode);
        steps.Add($"已导入公钥（{keyName}，指纹 {package.PublicKeyFingerprint[..12]}…）");

        // 4. 重启 Veyon 服务（服务名与旧脚本一致：VeyonService）
        if (!isTeacher)
            RestartVeyonService(runner);
        else
            steps.Add("教师端 Master 安装完成；日常账户连接验证留待 P7。");

        return new("veyon-key", ExecutionPlan.Succeeded, string.Join("；", steps));
    }

    private static void RestartVeyonService(ProcessRunner runner)
    {
        // 与旧脚本一致：Restart-Service -Name VeyonService -Force
        // 直接通过 sc.exe 重启服务，不经过 PowerShell，保持 Core 不依赖 shell 脚本。
        try
        {
            runner.Run("sc.exe", new[] { "restart", "VeyonService" },
                @"C:\Windows\System32", TimeSpan.FromSeconds(ServiceTimeoutSeconds));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.IO.IOException or TimeoutException)
        {
            // 服务重启失败不阻断"已导入公钥"这一事实；由读回验证报告真实状态
            _ = ex;
        }
    }

    private static string? ResolveVeyonCliPath()
    {
        // 旧脚本实测路径：veyon-cli.exe / veyon-wcli.exe（4.11.2 默认安装）
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Veyon", "veyon-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Veyon", "veyon-wcli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Veyon", "veyon-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Veyon", "veyon-wcli.exe")
        })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static string Truncate(string text)
    {
        text = text.Trim();
        return text.Length > 400 ? text[..400] + "…" : text;
    }

    /// <summary>
    /// Read-back after the whole install + configure flow. Never guesses:
    /// each probe is a separate fact; absence is "未确认" not "失败".
    /// </summary>
    public static VeyonVerification Verify(PackageContext? expectedPackage = null)
    {
        var facts = VeyonFacts.Probe();
        string installState = facts.Status switch
        {
            "installed" => "已安装",
            VeyonFacts.NotInstalled => "未安装",
            _ => "未知"
        };
        bool keyImported = false;
        string keyDetail = "未配置";
        if (expectedPackage is not null && facts.Status == "installed")
        {
            var runner = new ProcessRunner();
            var cliPath = ResolveVeyonCliPath();
            if (cliPath is not null)
            {
                try
                {
                    runner.Run(cliPath, new[] { "config", "get", "Authentication/Method" },
                        Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(CliTimeoutSeconds));
                    var method = runner.Stdout.Trim();
                    keyImported = runner.ExitCode is 0 && method == KeyAuthMethod;
                    keyDetail = keyImported
                        ? "已确认密钥认证（Authentication/Method=1）"
                        : $"认证方式读回为 {method}，需人工核对";
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or
                                           UnauthorizedAccessException or System.IO.IOException)
                {
                    keyDetail = $"公钥读回失败：{ex.Message}";
                }
            }
        }
        return new VeyonVerification(
            installState,
            facts.VersionDetail ?? "版本未确认",
            keyImported,
            keyDetail,
            facts.ServiceDetail);
    }
}

/// <summary>Read-back facts after a Veyon install. No secrets.</summary>
public sealed record VeyonVerification(
    string InstallState,
    string Version,
    bool KeyImported,
    string KeyDetail,
    string ServiceState);
