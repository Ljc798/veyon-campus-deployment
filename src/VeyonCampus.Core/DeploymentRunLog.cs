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

/// <summary>Optional process boundary for secrets sent outside command-line arguments.</summary>
public interface IStandardInputProcessLauncher
{
    ProcessOutcome RunWithStandardInput(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, string standardInput,
        Action<string, string, int?, string>? log = null);
}

/// <summary>Default implementation; keeps the existing ProcessRunner contract.</summary>
public sealed class DefaultProcessLauncher : IProcessLauncher, IStandardInputProcessLauncher
{
    public ProcessOutcome Run(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout,
        Action<string, string, int?, string>? log = null)
        => RunCore(fileName, arguments, workingDirectory, timeout, null, log);

    public ProcessOutcome RunWithStandardInput(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, string standardInput,
        Action<string, string, int?, string>? log = null)
        => RunCore(fileName, arguments, workingDirectory, timeout, standardInput, log);

    private static ProcessOutcome RunCore(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, string? standardInput,
        Action<string, string, int?, string>? log)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var description = Truncate(string.Join(' ', arguments));
        var runner = new ProcessRunner();
        try
        {
            if (standardInput is null)
                runner.Run(fileName, arguments, workingDirectory, timeout);
            else
                runner.RunWithStandardInput(fileName, arguments, workingDirectory, timeout, standardInput);
            var outcome = new ProcessOutcome(fileName, arguments,
                runner.ExitCode == 0 ? ProcessOutcomeKind.Success : ProcessOutcomeKind.Failed,
                runner.ExitCode, runner.Stdout, runner.Stderr);
            log?.Invoke(fileName, description, runner.ExitCode, standardInput is null
                ? $"进程退出码 {runner.ExitCode}：{Truncate(runner.Stdout)}"
                : $"进程退出码 {runner.ExitCode}；标准输入任务的输出已省略。");
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
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or
                                   System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            var outcome = new ProcessOutcome(fileName, arguments, ProcessOutcomeKind.LaunchRefused,
                null, "", ex.Message);
            log?.Invoke(fileName, description, null, standardInput is null
                ? $"无法启动进程：{ex.Message}" : "无法启动标准输入任务；详细输出已省略。");
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
    private const int MaximumHistoryDirectories = 256;
    private const int MaximumLogBytes = 128 * 1024;
    private const int MaximumLogEvents = 2048;
    private static readonly JsonSerializerOptions HistoryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

    /// <summary>Returns the per-user deployment log directory used by the App.</summary>
    public static string GetDefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "runs");

    /// <summary>
    /// Reads the newest bounded, locally stored run record. Logs deliberately
    /// contain status codes and timestamps only; free-form step details,
    /// account names, configuration contents, and secrets are not persisted.
    /// An unfinished run is surfaced as NeedsReview so a restart cannot make
    /// an interrupted system change look successful.
    /// </summary>
    public static DeploymentRunHistory? ReadLatestHistory(string root)
    {
        if (!Directory.Exists(root)) return null;

        DirectoryInfo[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(root)
                .Select(path => new DirectoryInfo(path))
                .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(directory => new FileInfo(Path.Combine(directory.FullName, "run.jsonl")).LastWriteTimeUtc)
                .Take(MaximumHistoryDirectories)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // If a run directory exists but cannot be enumerated, do not let
            // unreadable history silently remove the recovery gate.
            return CreateUnreadableHistory("unavailable", DateTimeOffset.UtcNow, string.Empty);
        }

        if (directories.Length == 0) return null;

        // Every run rewrites run.jsonl at each safe step boundary. The most
        // recently updated run record is therefore the one that must gate a
        // new system modification. A damaged or incomplete newest record is
        // surfaced as NeedsReview instead of falling back to an older success.
        var newest = directories[0];
        return TryReadHistory(newest) ?? CreateUnreadableHistory(newest);
    }

    private static DeploymentRunHistory CreateUnreadableHistory(DirectoryInfo directory)
    {
        var path = Path.Combine(directory.FullName, "run.jsonl");
        DateTime timestamp;
        try
        {
            var log = new FileInfo(path);
            timestamp = log.Exists ? log.LastWriteTimeUtc : directory.CreationTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            timestamp = directory.CreationTimeUtc;
        }
        return CreateUnreadableHistory(directory.Name, new DateTimeOffset(timestamp), path);
    }

    private static DeploymentRunHistory CreateUnreadableHistory(string runId, DateTimeOffset timestamp,
        string logPath) => new(
            runId,
            timestamp,
            null,
            ExecutionPlan.NeedsReview,
            false,
            Array.Empty<DeploymentRunHistoryStep>(),
            logPath,
            WasInterrupted: true);

    private static DeploymentRunHistory? TryReadHistory(DirectoryInfo directory)
    {
        if (!Guid.TryParseExact(directory.Name, "N", out _)) return null;
        var path = Path.Combine(directory.FullName, "run.jsonl");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Length is <= 0 or > MaximumLogBytes) return null;

            var records = File.ReadAllLines(path);
            if (records.Length is 0 or > MaximumLogEvents) return null;

            DateTimeOffset? startedAt = null;
            DateTimeOffset? finishedAt = null;
            var finalStatus = ExecutionPlan.NeedsReview;
            var rebootRequired = false;
            var hasFinishedEvent = false;
            var steps = new List<DeploymentRunHistoryStep>();

            foreach (var line in records)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var record = JsonSerializer.Deserialize<DeploymentRunLogEvent>(line, HistoryJsonOptions);
                if (record is null || !string.Equals(record.RunId, directory.Name, StringComparison.Ordinal))
                    return null;

                if (record.EventKind == "run-started" && record.TimeUtc is { } startTime)
                    startedAt ??= startTime;

                if ((record.EventKind is "step" or "verification") &&
                    IsSafeStepId(record.StepId) && IsKnownStatus(record.Status))
                {
                    var existing = steps.FindIndex(step => step.StepId == record.StepId);
                    var step = new DeploymentRunHistoryStep(record.StepId!, record.Status!);
                    if (existing >= 0) steps[existing] = step;
                    else steps.Add(step);
                }

                rebootRequired |= record.RebootRequired == true;
                if (record.EventKind == "run-finished" && IsKnownStatus(record.Status) &&
                    record.TimeUtc is { } finishTime)
                {
                    hasFinishedEvent = true;
                    finishedAt = finishTime;
                    finalStatus = record.Status!;
                    rebootRequired = record.RebootRequired == true;
                }
            }

            if (startedAt is null) return null;
            return new DeploymentRunHistory(
                directory.Name,
                startedAt.Value,
                finishedAt,
                hasFinishedEvent ? finalStatus : ExecutionPlan.NeedsReview,
                rebootRequired,
                Array.AsReadOnly(steps.ToArray()),
                path,
                WasInterrupted: !hasFinishedEvent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsKnownStatus(string? status) => status is
        ExecutionPlan.NotStarted or ExecutionPlan.Running or ExecutionPlan.Succeeded or
        ExecutionPlan.Failed or ExecutionPlan.Cancelled or ExecutionPlan.Skipped or
        ExecutionPlan.RequiresReboot or ExecutionPlan.PartiallyCompleted or ExecutionPlan.NeedsReview;

    private static bool IsSafeStepId(string? stepId) => !string.IsNullOrEmpty(stepId) &&
        stepId.Length <= 64 && stepId.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-');

    private sealed record DeploymentRunLogEvent(
        string? RunId,
        string? EventKind,
        string? StepId,
        string? Status,
        int? ExitCode,
        bool? RebootRequired,
        DateTimeOffset? TimeUtc);

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
                rebootRequired = result?.RebootRequired,
                timeUtc = DateTime.UtcNow
            };
            _buffer.AppendLine(JsonSerializer.Serialize(record));
            var staging = LogPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var bytes = Encoding.UTF8.GetBytes(_buffer.ToString());
            try
            {
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 16 * 1024, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(staging, LogPath, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                try { if (File.Exists(staging)) File.Delete(staging); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Records that the run ended with the summarized status.</summary>
    public void Finish(string overallStatus, bool rebootRequired)
    {
        ReportEvent("run-finished", overallStatus,
            new StepResult("summary", overallStatus, "运行结束", RebootRequired: rebootRequired), null);
    }
}

/// <summary>Safe fields reconstructed from one local run.jsonl file.</summary>
public sealed record DeploymentRunHistoryStep(string StepId, string Status);

public sealed record DeploymentRunHistory(
    string RunId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string Status,
    bool RebootRequired,
    IReadOnlyList<DeploymentRunHistoryStep> Steps,
    string LogPath,
    bool WasInterrupted);
