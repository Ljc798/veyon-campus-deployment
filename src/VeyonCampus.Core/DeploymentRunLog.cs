using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>Outcome of one external process call, with the three distinct failure phases.</summary>
public enum ProcessOutcomeKind { Success, LaunchRefused, Failed, TimedOut, NeedsReview }

/// <summary>
/// Structured result of a single external process invocation (P3-06, AR-02).
/// Distinguishes "rejected before start", "ran but failed" and "state
/// unknown after timeout"; the latter must never be silently retried by
/// callers that already made system modifications.
/// </summary>
public sealed record ProcessOutcome(
    string FileName,
    IReadOnlyList<string> Arguments,
    ProcessOutcomeKind Kind,
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool ModifiedBeforeFailure = false)
{
    public bool Ok => Kind == ProcessOutcomeKind.Success;

    public static ProcessOutcome NeedsReviewResult(string fileName, string detail) =>
        new(fileName, Array.Empty<string>(), ProcessOutcomeKind.NeedsReview, null, "", detail);
}

/// <summary>
/// Minimal process boundary so execution paths can be tested with fakes and
/// the real calls stay in one place (AR-05). The implementation is the
/// existing ProcessRunner behaviour; the interface only exposes the parts
/// execution handlers need.
/// </summary>
public interface IProcessLauncher
{
    ProcessOutcome Run(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout,
        Action<string, string, int?, string>? log = null);
}

/// <summary>Default implementation; keeps the existing ProcessRunner contract.</summary>
public sealed class DefaultProcessLauncher : IProcessLauncher
{
    public ProcessOutcome Run(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout,
        Action<string, string, int?, string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var description = Truncate(string.Join(' ', arguments));
        var runner = new ProcessRunner();
        try
        {
            runner.Run(fileName, arguments, workingDirectory, timeout);
            var outcome = new ProcessOutcome(fileName, arguments, ProcessOutcomeKind.Success,
                runner.ExitCode, runner.Stdout, runner.Stderr);
            log?.Invoke(fileName, description, runner.ExitCode,
                $"进程退出码 {runner.ExitCode}：{Truncate(runner.Stdout)}");
            return outcome;
        }
        catch (TimeoutException)
        {
            // A timeout after the process started leaves the real system
            // state unknown. Callers must record NeedsReview, not success.
            var outcome = new ProcessOutcome(fileName, arguments, ProcessOutcomeKind.TimedOut,
                runner.ExitCode, runner.Stdout, Truncate(runner.Stderr), ModifiedBeforeFailure: true);
            log?.Invoke(fileName, description, null,
                "进程超时；实际系统状态需核对，不自动重试。");
            return outcome;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.IO.IOException)
        {
            var outcome = new ProcessOutcome(fileName, arguments, ProcessOutcomeKind.LaunchRefused,
                null, "", ex.Message);
            log?.Invoke(fileName, description, null, $"无法启动进程：{ex.Message}");
            return outcome;
        }
    }

    private static string Truncate(string text)
    {
        text = text.Trim();
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}

/// <summary>
/// Structured, log-safe run record (P3-09, ADR-008): atomic JSON files per
/// run under a caller-owned root. Secrets never enter the record; the same
/// run id appears in the log lines so evidence can be correlated.
/// </summary>
public sealed class DeploymentRunLog
{
    public string RunId { get; }
    public string RunDirectory { get; }
    public string LogPath { get; }
    private readonly object _writeLock = new();
    private readonly StringBuilder _buffer = new();

    private DeploymentRunLog(string runId, string runDirectory, string logPath)
    {
        RunId = runId;
        RunDirectory = runDirectory;
        LogPath = logPath;
    }

    /// <summary>Creates the run record under <paramref name="root"/> and reports run start.</summary>
    public static DeploymentRunLog Create(string root, string planFingerprint)
    {
        var runId = Guid.NewGuid().ToString("N");
        var runDirectory = Path.Combine(Path.GetFullPath(root), runId);
        Directory.CreateDirectory(runDirectory);
        var log = new DeploymentRunLog(runId, runDirectory, Path.Combine(runDirectory, "run.jsonl"));
        log.ReportEvent("run-started", planFingerprint, null, null);
        return log;
    }

    /// <summary>Appends a step-boundary event and rewrites the run file atomically.</summary>
    public void ReportEvent(string eventKind, string? stepId, StepResult? result, int? exitCode)
    {
        lock (_writeLock)
        {
            var record = new
            {
                runId = RunId,
                eventKind,
                stepId,
                status = result?.Status,
                exitCode,
                timeUtc = DateTime.UtcNow
            };
            _buffer.AppendLine(JsonSerializer.Serialize(record));
            var staging = LogPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(staging, _buffer.ToString());
            if (File.Exists(LogPath)) File.Delete(LogPath);
            File.Move(staging, LogPath);
        }
    }

    /// <summary>Records that the run ended with the summarized status.</summary>
    public void Finish(string overallStatus, bool rebootRequired)
    {
        ReportEvent("run-finished", overallStatus,
            new StepResult("summary", overallStatus, "运行结束", RebootRequired: rebootRequired), null);
    }
}
