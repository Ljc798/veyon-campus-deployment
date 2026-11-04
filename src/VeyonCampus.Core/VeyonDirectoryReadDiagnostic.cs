namespace VeyonCampus.Core;

/// <summary>
/// Structured diagnostic for a failed read-only Veyon Builtin Directory command
/// (`networkobjects dump`, `networkobjects export`, `config export`).
/// Stages: CliNotFound, CliFailed, ExportFileMissing, AccessDenied, ParseFailed,
/// Timeout, UnsupportedBackend, IdentityConflict, PartialRead.
/// </summary>
public sealed record VeyonDirectoryReadDiagnostic(
    string FailureStage,
    string Message,
    string CliPath,
    int? ExitCode,
    string Detail,
    string SubCommand,
    bool TimedOut,
    DateTimeOffset? LastSuccessfulReadUtc,
    string? TrustedSnapshotFingerprint)
{
    public const string None = "None";
    public const string CliNotFound = "CliNotFound";
    public const string CliFailed = "CliFailed";
    public const string ExportFileMissing = "ExportFileMissing";
    public const string AccessDenied = "AccessDenied";
    public const string ParseFailed = "ParseFailed";
    public const string Timeout = "Timeout";
    public const string UnsupportedBackend = "UnsupportedBackend";
    public const string IdentityConflict = "IdentityConflict";
    public const string PartialRead = "PartialRead";

    public bool IsCliUnavailable => FailureStage is CliNotFound or Timeout;

    /// <summary>Builds a validated diagnostic for the given stage.</summary>
    public static VeyonDirectoryReadDiagnostic For(string failureStage, string message,
        string cliPath, int? exitCode, string detail, string subCommand, bool timedOut,
        string? trustedSnapshotFingerprint = null)
    {
        if (!new[] { None, CliNotFound, CliFailed, ExportFileMissing, AccessDenied,
                     ParseFailed, Timeout, UnsupportedBackend, IdentityConflict, PartialRead }
                .Contains(failureStage, StringComparer.Ordinal))
            throw new ArgumentException("未知的 Veyon 目录诊断阶段：" + failureStage, nameof(failureStage));
        return new(failureStage, message, cliPath, exitCode, detail, subCommand, timedOut, null,
            trustedSnapshotFingerprint);
    }

    /// <summary>Success marker after a directory read completes.</summary>
    public static VeyonDirectoryReadDiagnostic Success(string cliPath, string subCommand,
        string? trustedSnapshotFingerprint = null) =>
        new(None, "", cliPath, 0, "", subCommand, false, DateTimeOffset.UtcNow,
            trustedSnapshotFingerprint);
}

/// <summary>Read failure with machine-readable CLI and last-success details.</summary>
public sealed class VeyonDirectoryReadException : InvalidOperationException
{
    public VeyonDirectoryReadException(VeyonDirectoryReadDiagnostic diagnostic, Exception? inner = null)
        : base(FormatMessage(diagnostic), inner) => Diagnostic = diagnostic;

    public VeyonDirectoryReadDiagnostic Diagnostic { get; }

    private static string FormatMessage(VeyonDirectoryReadDiagnostic diagnostic)
    {
        var exitCode = diagnostic.ExitCode is { } code ? $"（退出码 {code}）" : "";
        return $"{diagnostic.FailureStage}：{diagnostic.Message}{exitCode}";
    }
}
