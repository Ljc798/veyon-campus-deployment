namespace VeyonCampus.Core;

/// <summary>
/// Veyon post-install verification slice (P4-07, AR-04). Reads back each
/// fact through the Veyon CLI: version, key-auth method, expected public
/// key identity, and service state. Each probe returns its own result; a
/// missing or failed probe is Unknown/NeedsReview, never inferred success.
/// </summary>
public sealed class WindowsVeyonVerificationService
{
    private readonly IProcessLauncher _launcher;

    public WindowsVeyonVerificationService(IProcessLauncher? launcher = null) =>
        _launcher = launcher ?? new DefaultProcessLauncher();

    /// <summary>Full local read-back after a student install + configure flow.</summary>
    public VeyonVerificationResult Verify(PackageContext package, bool isTeacher)
    {
        if (!OperatingSystem.IsWindows())
            return new VeyonVerificationResult(false,
                "当前不是 Windows；Veyon 验证仅支持 Windows。", null, null, null, null);

        var facts = VeyonFacts.Probe();
        if (facts.Status == VeyonFacts.NotInstalled)
            return new VeyonVerificationResult(false, "Veyon 尚未安装；验证未开始。", null, null, null, null);
        if (facts.Status != "installed" || !VeyonFacts.IsSupportedVersionDetail(facts.VersionDetail))
            return new VeyonVerificationResult(false,
                $"无法确认固定版本 Veyon {VeyonInstallerTrust.Version} 已安装。{facts.AsText()}",
                facts.VersionDetail, null, null, null);

        var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath();
        if (cliPath is null)
            return new VeyonVerificationResult(false,
                "未找到 Veyon CLI；无法独立读回认证方式和公钥。", facts.VersionDetail, null, null, null);

        var authMethod = ConfigGet(cliPath, "Authentication/Method");
        string keyStatus;
        bool keyMatches;
        if (authMethod.Ok && string.Equals(authMethod.Stdout.Trim(), "1", StringComparison.Ordinal))
        {
            var listing = AuthKeysList(cliPath);
            keyMatches = listing.Ok &&
                        listing.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Any(line => line.Equals(VeyonAuthKeyId.PublicKeyForCampus(package.Campus),
                                StringComparison.Ordinal));
            keyStatus = keyMatches
                ? $"Veyon 密钥清单包含预期公钥 {VeyonAuthKeyId.PublicKeyForCampus(package.Campus)}（摘要 {package.PublicKeyFingerprint[..12]}…）。"
                : $"Veyon 密钥清单中未找到预期公钥；不能判定导入成功。{Truncate(listing.Stdout + listing.Stderr)}";
        }
        else
        {
            keyMatches = false;
            keyStatus = "认证方式读回失败或未确认；公钥清单不能替代独立核验。";
        }

        var serviceState = QueryServiceState();
        bool ok = authMethod.Ok && string.Equals(authMethod.Stdout.Trim(), "1", StringComparison.Ordinal) &&
                  keyMatches && serviceState.Running is true;
        var detail = string.Join("；",
            $"认证方式读回：{(authMethod.Ok ? authMethod.Stdout.Trim() : "失败")}",
            keyStatus,
            $"服务状态：{serviceState.Describe}");
        return new VeyonVerificationResult(ok, detail, facts.VersionDetail,
            keyMatches ? "已读回" : "未确认", serviceState.Running is true ? "运行中" : serviceState.Describe,
            keyStatus);
    }

    /// <summary>Reads one configuration value; failure is reported, not guessed.</summary>
    public ProcessOutcome ConfigGet(string cliPath, string key) =>
        _launcher.Run(cliPath, new[] { "config", "get", key },
            Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(WindowsVeyonAdapter.CliTimeoutSeconds));

    /// <summary>Lists the configured public keys; the expected key must be a separate line match.</summary>
    public ProcessOutcome AuthKeysList(string cliPath) =>
        _launcher.Run(cliPath, new[] { "authkeys", "list" },
            Path.GetDirectoryName(cliPath)!, TimeSpan.FromSeconds(WindowsVeyonAdapter.CliTimeoutSeconds));

    private ProcessOutcome QueryServiceRaw() =>
        _launcher.Run("sc.exe", new[] { "query", VeyonFacts.ServiceName },
            @"C:\Windows\System32", TimeSpan.FromSeconds(15));

    private (bool? Running, string Describe) QueryServiceState()
    {
        var raw = QueryServiceRaw();
        if (!raw.Ok)
            return (null, "服务状态读取失败；未知。");
        var state = WindowsServiceState.Parse(raw.Stdout);
        if (state is null)
            return (null, "服务状态无法解析；未知。");
        return (state == WindowsServiceState.Running, WindowsServiceState.Describe(state));
    }

    private static string Truncate(string text)
    {
        text = text.Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}

/// <summary>Typed local verification facts. No secrets, no network claims.</summary>
public sealed record VeyonVerificationResult(
    bool Ok,
    string Detail,
    string? Version,
    string? KeyImported,
    string? ServiceState,
    string? KeyDetail);
