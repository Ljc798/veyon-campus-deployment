namespace VeyonCampus.Core;

/// <summary>
/// Windows adapter (first slice, P4-01/03/04/05): installs Veyon from a
/// verified in-package installer, imports the public key via Veyon CLI,
/// and restarts the Veyon service. All external calls go through
/// <see cref="ProcessRunner"/>. Read-backs never guess: missing pieces
/// report unknown, not success.
///
/// CLI shape follows the 4.11.2 workflow proven in the legacy scripts
/// (student_v3.txt): set "Authentication/Method", then import the campus
/// key into Veyon's configured public-key directory.
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
        var installedFacts = VeyonFacts.Probe();
        if (installedFacts.Status != VeyonFacts.NotInstalled &&
            (installedFacts.Status != "installed" || !VeyonFacts.IsSupportedVersionDetail(installedFacts.VersionDetail)))
            return new("veyon-install", ExecutionPlan.Failed,
                $"当前 Veyon 版本或安装状态不符合固定基线 {VeyonInstallerTrust.Version}；为避免覆盖未知或较新版本，没有启动安装器。{installedFacts.VersionDetail}");
        var installerPath = package.InstallerPath
            ?? throw new InvalidDataException("所选部署包不含安装资源；旧版部署包尚不支持离线安装。");
        try { package.VerifyUnchanged(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new("veyon-install", ExecutionPlan.Failed, $"部署包在安装前发生变化：{ex.Message}");
        }
        var trust = VeyonInstallerTrust.Check(installerPath);
        if (!trust.IsAllowed || !trust.AuthenticodeVerified)
            return new("veyon-install", ExecutionPlan.Failed,
                $"安装前信任校验未通过；没有启动安装器。{trust.Detail}");
        var (status, detail, exitCode) = RunInstaller(installerPath, isTeacher);
        return new("veyon-install", status, detail, exitCode, exitCode == 3010);
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
        if (facts.Status != "installed" || !VeyonFacts.IsSupportedVersionDetail(facts.VersionDetail))
            return new("veyon-key", ExecutionPlan.NeedsReview,
                $"无法确认当前安装为已验证版本 Veyon {VeyonInstallerTrust.Version}；没有更改密钥配置。{facts.VersionDetail}");
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
        if (exit == 0)
        {
            var component = isTeacher ? "教师组件（含 Veyon Master）" : "学生组件（不含 Veyon Master）";
            return (ExecutionPlan.Succeeded,
                $"安装器完成（退出码 {exit}）；已安装{component}。", exit);
        }
        if (exit == 3010)
            return (ExecutionPlan.Failed, "安装器返回 3010；需要重启后重新检查，当前不继续依赖步骤。", exit);
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
        StepResult Partial(string detail, int? exitCode = null) =>
            new("veyon-key", ExecutionPlan.PartiallyCompleted,
                "Veyon 配置已部分修改，未完成的步骤需人工核对。" + detail, exitCode);
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
            return Partial(
                $"认证方式读回不一致：期望 {KeyAuthMethod}，实际 {readBack}；停止后续配置。", runner.ExitCode);
        steps.Add("读回 Authentication/Method 一致");

        // 2. 导入校区公钥（旧脚本：authkeys import "$Campus/public" <pem>）。
        // Veyon copies it into its configured public-key store; never persist a
        // path into the removable deployment package in system configuration.
        var keyName = VeyonAuthKeyId.PublicKeyForCampus(package.Campus);
        runner.Run(cliPath, new[] { "authkeys", "import", keyName, package.PublicKeyPath },
            Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(CliTimeoutSeconds));
        if (runner.ExitCode is not 0)
            return Partial(
                $"公钥导入失败（退出码 {runner.ExitCode}）；可能需要重新安装后重试。{Truncate(runner.Stderr)}", runner.ExitCode);
        steps.Add($"已导入公钥（{keyName}，指纹 {package.PublicKeyFingerprint[..12]}…）");

        // 3. 重启 Veyon 服务（服务名与旧脚本一致：VeyonService）
        if (!isTeacher)
        {
            var service = RestartVeyonService(runner);
            if (!service.Ok)
                return service with
                {
                    Status = ExecutionPlan.PartiallyCompleted,
                    Detail = "Veyon 配置已部分修改，未完成的步骤需人工核对。" +
                             string.Join("；", steps.Append(service.Detail))
                };
        }
        else
            steps.Add("教师端 Master 安装完成；日常账户连接验证留待 P7。");

        return new("veyon-key", ExecutionPlan.Succeeded, string.Join("；", steps));
    }

    private static StepResult RestartVeyonService(ProcessRunner runner)
    {
        // sc.exe has no restart verb: query, stop, wait for STOPPED, start, then read RUNNING back.
        try
        {
            var initialState = QueryVeyonServiceState(runner);
            if (initialState.State is null)
                return new("veyon-key", ExecutionPlan.Failed,
                    $"公钥已导入，但无法确认 VeyonService 状态：{Truncate(initialState.Output)}", initialState.ExitCode);

            if (initialState.State == WindowsServiceState.StartPending &&
                !WaitForVeyonServiceState(runner, WindowsServiceState.Running, out var startingDetail))
                return new("veyon-key", ExecutionPlan.Failed,
                    $"公钥已导入，但 VeyonService 启动状态未完成：{startingDetail}");

            var currentState = initialState.State == WindowsServiceState.StartPending
                ? WindowsServiceState.Running
                : initialState.State;
            if (currentState == WindowsServiceState.Running)
            {
                runner.Run("sc.exe", new[] { "stop", VeyonFacts.ServiceName }, @"C:\Windows\System32", TimeSpan.FromSeconds(30));
                if (!WaitForVeyonServiceState(runner, WindowsServiceState.Stopped, out var stoppingDetail))
                    return new("veyon-key", ExecutionPlan.Failed,
                        $"公钥已导入，但 VeyonService 未能停止：{stoppingDetail}", runner.ExitCode);
            }

            else if (currentState == WindowsServiceState.StopPending)
            {
                if (!WaitForVeyonServiceState(runner, WindowsServiceState.Stopped, out var stoppingDetail))
                    return new("veyon-key", ExecutionPlan.Failed,
                        $"公钥已导入，但 VeyonService 未能完成停止：{stoppingDetail}", runner.ExitCode);
            }
            else if (currentState != WindowsServiceState.Stopped)
            {
                return new("veyon-key", ExecutionPlan.Failed,
                    $"公钥已导入，但 VeyonService 状态为“{WindowsServiceState.Describe(currentState)}”；没有继续启动服务。");
            }

            runner.Run("sc.exe", new[] { "start", VeyonFacts.ServiceName }, @"C:\Windows\System32", TimeSpan.FromSeconds(30));
            if (!WaitForVeyonServiceState(runner, WindowsServiceState.Running, out var runningDetail))
                return new("veyon-key", ExecutionPlan.Failed,
                    $"公钥已导入，但 VeyonService 未能启动并读回运行状态：{runningDetail}", runner.ExitCode);
            return new("veyon-key", ExecutionPlan.Succeeded, "VeyonService 已停止并重新启动，最终状态读回为 RUNNING。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.IO.IOException or TimeoutException)
        {
            return new("veyon-key", ExecutionPlan.Failed, $"公钥导入命令已完成，但重启 VeyonService 失败：{ex.Message}");
        }
    }

    private static (int? State, string Output, int? ExitCode) QueryVeyonServiceState(ProcessRunner runner)
    {
        runner.Run("sc.exe", new[] { "query", VeyonFacts.ServiceName }, @"C:\Windows\System32",
            TimeSpan.FromSeconds(15));
        var output = Truncate(runner.Stdout + " " + runner.Stderr);
        return (runner.ExitCode == 0 ? WindowsServiceState.Parse(runner.Stdout) : null, output, runner.ExitCode);
    }

    private static bool WaitForVeyonServiceState(ProcessRunner runner, int expectedState, out string detail)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ServiceTimeoutSeconds);
        (int? State, string Output, int? ExitCode) latest = (null, "无状态读回", null);
        while (DateTime.UtcNow < deadline)
        {
            latest = QueryVeyonServiceState(runner);
            if (latest.State == expectedState)
            {
                detail = WindowsServiceState.Describe(latest.State);
                return true;
            }
            Thread.Sleep(500);
        }
        detail = $"期望“{WindowsServiceState.Describe(expectedState)}”，最后读回“{WindowsServiceState.Describe(latest.State)}”；{latest.Output}";
        return false;
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
                    keyImported = false; // Authentication/Method alone does not prove the expected public key was imported.
                    keyDetail = runner.ExitCode is 0 && method == KeyAuthMethod
                        ? $"密钥认证方式已读回；预期公钥指纹 {expectedPackage.PublicKeyFingerprint[..12]}… 尚未从 Veyon 密钥库独立核验。"
                        : $"认证方式读回为 {method}，需人工核对";
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or
                                           UnauthorizedAccessException or System.IO.IOException)
                {
                    keyDetail = $"公钥读回失败：{ex.Message}";
                }
            }
        }
        var serviceState = ReadServiceRuntimeState();
        return new VeyonVerification(
            installState,
            facts.VersionDetail ?? "版本未确认",
            keyImported,
            keyDetail,
            serviceState);
    }

    private static string ReadServiceRuntimeState()
    {
        if (!OperatingSystem.IsWindows()) return "不适用：非 Windows。";
        try
        {
            var runner = new ProcessRunner();
            runner.Run("sc.exe", new[] { "query", VeyonFacts.ServiceName }, @"C:\Windows\System32",
                TimeSpan.FromSeconds(15));
            if (runner.ExitCode != 0) return "未运行或未注册：" + Truncate(runner.Stdout + " " + runner.Stderr);
            var state = WindowsServiceState.Parse(runner.Stdout);
            return state == WindowsServiceState.Running
                ? "VeyonService 正在运行。"
                : $"VeyonService 已注册但{WindowsServiceState.Describe(state)}：{Truncate(runner.Stdout)}";
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or UnauthorizedAccessException or IOException)
        { return "未知：VeyonService 状态读取失败：" + ex.Message; }
    }
}

/// <summary>Read-back facts after a Veyon install. No secrets.</summary>
public sealed record VeyonVerification(
    string InstallState,
    string Version,
    bool KeyImported,
    string KeyDetail,
    string ServiceState);
