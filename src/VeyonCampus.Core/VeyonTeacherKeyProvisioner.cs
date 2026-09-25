namespace VeyonCampus.Core;

public enum VeyonAuthKeyListingState
{
    Missing,
    PublicOnly,
    PrivateOnly,
    CompletePair,
    Unrecognized
}

public sealed record VeyonPublicKeyProvisionResult(StepResult Step, bool Created);

/// <summary>
/// Reuses or creates a campus key pair inside Veyon's configured key store,
/// then exports only its public half to a caller-owned temporary file.
/// </summary>
public sealed class VeyonTeacherKeyProvisioner
{
    public VeyonPublicKeyProvisionResult ExportPublicKey(string campusId, string destinationPath)
    {
        var created = false;
        StepResult Result(string status, string detail, int? exitCode = null) =>
            new("veyon-teacher-key", status, detail, exitCode);

        try
        {
            if (!OperatingSystem.IsWindows())
                return new(Result(ExecutionPlan.Failed, "教师密钥必须由 Windows 上已安装的 Veyon 管理；本机不支持生成学生部署包。"), false);

            var platform = PlatformFacts.Collect();
            if (platform.IsElevated != true)
                return new(Result(ExecutionPlan.Failed, "读写 Veyon 受控密钥目录需要管理员权限；请以管理员身份重新启动 App。"), false);

            var facts = VeyonFacts.Probe();
            if (facts.Status != "installed" || !VeyonFacts.IsSupportedVersionDetail(facts.VersionDetail))
                return new(Result(ExecutionPlan.NeedsReview,
                    $"需要先安装并确认固定版本 Veyon {VeyonInstallerTrust.Version}；目前状态：{facts.AsText()}"), false);

            var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath();
            if (cliPath is null)
                return new(Result(ExecutionPlan.Failed, "找不到 Veyon CLI；请确认教师端 Veyon 已完整安装。"), false);

            var fullDestination = Path.GetFullPath(destinationPath);
            var parent = Path.GetDirectoryName(fullDestination);
            if (parent is null || !Directory.Exists(parent) || File.Exists(fullDestination) || Directory.Exists(fullDestination))
                return new(Result(ExecutionPlan.Failed, "公钥导出临时路径无效或已存在；没有覆盖任何文件。"), false);

            var keyId = VeyonAuthKeyId.ForCampus(campusId);
            var listing = Run(cliPath, "authkeys", "list");
            if (listing.ExitCode is not 0)
                return new(Result(ExecutionPlan.Failed,
                    $"无法读取 Veyon 密钥清单（退出码 {listing.ExitCode}）；没有创建或导出密钥。{Truncate(listing.Stderr)}", listing.ExitCode), false);

            var state = ParseListing(listing.Stdout, keyId);
            if (state == VeyonAuthKeyListingState.Unrecognized)
                return new(Result(ExecutionPlan.NeedsReview,
                    "Veyon 返回的密钥清单格式无法安全识别；为避免覆盖或重复创建，已停止。"), false);
            if (state is VeyonAuthKeyListingState.PublicOnly or VeyonAuthKeyListingState.PrivateOnly)
                return new(Result(ExecutionPlan.NeedsReview,
                    "Veyon 中已存在该校区的不完整密钥对；没有重建、覆盖或导出，请先在 Veyon 密钥目录中核对。"), false);

            if (state == VeyonAuthKeyListingState.Missing)
            {
                var create = Run(cliPath, "authkeys", "create", keyId);
                if (create.ExitCode is not 0)
                    return new(Result(ExecutionPlan.Failed,
                        $"Veyon 创建该校区密钥对失败（退出码 {create.ExitCode}）。{Truncate(create.Stderr)}", create.ExitCode), false);
                created = true;

                // Confirm both halves exist before exporting anything. A partial
                // create is retained for manual review rather than overwritten.
                listing = Run(cliPath, "authkeys", "list");
                if (listing.ExitCode is not 0 || ParseListing(listing.Stdout, keyId) != VeyonAuthKeyListingState.CompletePair)
                    return new(Result(ExecutionPlan.NeedsReview,
                        "Veyon 创建命令已运行，但未能读回完整密钥对；密钥保留在 Veyon 密钥目录中，未覆盖或删除。", listing.ExitCode), true);
            }

            var export = Run(cliPath, "authkeys", "export", VeyonAuthKeyId.PublicKeyForCampus(campusId), fullDestination);
            if (export.ExitCode is not 0 || !File.Exists(fullDestination))
                return new(Result(ExecutionPlan.Failed,
                    $"Veyon 未能导出该校区公钥（退出码 {export.ExitCode?.ToString() ?? "未知"}）；教师私钥未导出。{Truncate(export.Stderr)}", export.ExitCode), created);

            _ = PackageBuilder.ReadPublicKeyPem(fullDestination);
            var detail = created
                ? "已在 Veyon 受控密钥目录创建并读回密钥对，只导出公钥供学生校区配置包使用。"
                : "已复用 Veyon 受控密钥目录中的现有密钥对，只导出公钥供学生校区配置包使用。";
            return new(Result(ExecutionPlan.Succeeded, detail, export.ExitCode), created);
        }
        catch (Exception ex)
        {
            return new(Result(ExecutionPlan.Failed, $"Veyon 密钥处理失败；教师私钥没有导出：{ex.Message}"), created);
        }
    }

    public static VeyonAuthKeyListingState ParseListing(string output, string keyId)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        var hasPublic = false;
        var hasPrivate = false;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(line,
                    "^[A-Za-z]+/(?:public|private)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                return VeyonAuthKeyListingState.Unrecognized;
            if (line.Equals(keyId + "/public", StringComparison.Ordinal)) hasPublic = true;
            if (line.Equals(keyId + "/private", StringComparison.Ordinal)) hasPrivate = true;
        }

        return (hasPublic, hasPrivate) switch
        {
            (false, false) => VeyonAuthKeyListingState.Missing,
            (true, false) => VeyonAuthKeyListingState.PublicOnly,
            (false, true) => VeyonAuthKeyListingState.PrivateOnly,
            _ => VeyonAuthKeyListingState.CompletePair
        };
    }

    private static ProcessRunner Run(string cliPath, params string[] arguments)
    {
        var runner = new ProcessRunner();
        runner.Run(cliPath, arguments, Path.GetDirectoryName(cliPath)!,
            TimeSpan.FromSeconds(WindowsVeyonAdapter.CliTimeoutSeconds));
        return runner;
    }

    private static string Truncate(string text)
    {
        text = text.Trim();
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}
