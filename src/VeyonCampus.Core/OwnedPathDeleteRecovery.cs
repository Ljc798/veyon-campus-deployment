using System.ComponentModel;

namespace VeyonCampus.Core;

/// <summary>
/// Last-resort recovery for one path that the caller has ALREADY proven to be product-owned.
/// It restores ownership/ACL with takeown+icacls so the caller can retry a single delete.
///
/// Safety boundary: this helper never enumerates, discovers, or broadens a target. It only
/// runs OS commands against the exact path supplied by the caller, which must have passed
/// VerifyProductRoot / RejectPathChain / reparse-point checks before reaching here. It does
/// not delete anything itself. If recovery fails it throws, so callers stay fail-closed and
/// never report a clean removal that did not happen.
///
/// This is the bounded equivalent of the broad "takeown + icacls" step in the local test
/// machine reset script, restricted to product-owned data during uninstall.
/// </summary>
internal static class OwnedPathDeleteRecovery
{
    internal const int DefaultTimeoutSeconds = 60;
    private const string AdministratorsSidGrant = "*S-1-5-32-544:(OI)(CI)F";

    /// <summary>A delete failure worth one ownership-recovery retry: permissions or file-in-use.</summary>
    internal static bool ShouldRetry(Exception exception) =>
        exception is UnauthorizedAccessException or IOException;

    /// <summary>Pure argument builder, exposed for checks without executing OS commands.</summary>
    internal static IReadOnlyList<string> BuildTakeOwnArguments(string verifiedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedPath);
        return ["/F", verifiedPath, "/R", "/D", "Y"];
    }

    /// <summary>Pure argument builder, exposed for checks without executing OS commands.</summary>
    internal static IReadOnlyList<string> BuildIcaclsArguments(string verifiedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedPath);
        return [verifiedPath, "/grant", AdministratorsSidGrant, "/T", "/C"];
    }

    /// <summary>
    /// Restores Administrators' ownership and full control on <paramref name="verifiedPath"/>.
    /// Called only from a catch block after a product-owned delete failed. On any failure the
    /// original delete error is preserved as the inner exception and the method throws, so the
    /// uninstall pipeline keeps its NeedsReview / retain-install-files behavior.
    /// </summary>
    internal static void RestoreOwnership(string verifiedPath, Exception originalFailure,
        TimeSpan? timeout = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("所有权恢复仅支持 Windows。");

        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedPath);
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var takeOwn = Path.Combine(systemDirectory, "takeown.exe");
        var icacls = Path.Combine(systemDirectory, "icacls.exe");
        var limit = timeout ?? TimeSpan.FromSeconds(DefaultTimeoutSeconds);

        try
        {
            RunSystemTool(takeOwn, BuildTakeOwnArguments(verifiedPath), systemDirectory, limit);
            RunSystemTool(icacls, BuildIcaclsArguments(verifiedPath), systemDirectory, limit);
        }
        catch (Exception recoveryFailure) when (recoveryFailure is IOException or UnauthorizedAccessException or
                                                Win32Exception or InvalidOperationException or TimeoutException or
                                                ArgumentException or NotSupportedException)
        {
            throw new IOException(
                $"清理“{verifiedPath}”时权限不足，且恢复本产品目录所有权失败：" +
                $"{recoveryFailure.Message}。原始错误：{originalFailure.Message}",
                originalFailure);
        }
    }

    private static void RunSystemTool(string toolPath, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout)
    {
        if (!File.Exists(toolPath))
            throw new IOException($"找不到系统工具 {Path.GetFileName(toolPath)}；无法恢复目录所有权。");

        var runner = new ProcessRunner();
        runner.Run(toolPath, arguments, workingDirectory, timeout, outputLimitChars: 4096);
        if (runner.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(runner.Stderr) ? runner.Stdout : runner.Stderr;
            detail = detail.Trim();
            if (detail.Length > 200) detail = detail[..200] + "…";
            throw new IOException(
                $"{Path.GetFileName(toolPath)} 失败（退出码 {runner.ExitCode?.ToString() ?? "未知"}）。" + detail);
        }
    }
}
