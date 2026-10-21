using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;

namespace VeyonCampus.Core;

public static class StudentUpdatePhase
{
    public const string TeacherOffer = "TeacherOffer";
    public const string LanTransfer = "LanTransfer";
    public const string IntegrityVerify = "IntegrityVerify";
    public const string PrepareAndBackup = "PrepareAndBackup";
    public const string SetupInstall = "SetupInstall";
    public const string AgentStage = "AgentStage";
    public const string AgentActivate = "AgentActivate";
    public const string HealthAndCommit = "HealthAndCommit";
    public const string Rollback = "Rollback";
    public const string TeacherReceipt = "TeacherReceipt";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        TeacherOffer, LanTransfer, IntegrityVerify, PrepareAndBackup, SetupInstall,
        AgentStage, AgentActivate, HealthAndCommit, Rollback, TeacherReceipt
    };

    public static bool IsKnown(string value) => Known.Contains(value);
}

public sealed record StudentUpdatePhaseTiming(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("startedUtc")] DateTimeOffset StartedUtc,
    [property: JsonPropertyName("completedUtc")] DateTimeOffset? CompletedUtc,
    [property: JsonPropertyName("elapsedMilliseconds")] long? ElapsedMilliseconds,
    [property: JsonPropertyName("bytes")] long? Bytes,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("integrityHashMilliseconds")] long? IntegrityHashMilliseconds = null,
    [property: JsonPropertyName("retryCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? RetryCount = null);

public sealed record StudentUpdatePhaseTimingSnapshot(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("collectedUtc")] DateTimeOffset CollectedUtc,
    [property: JsonPropertyName("phases")] IReadOnlyList<StudentUpdatePhaseTiming> Phases,
    [property: JsonPropertyName("environment")] StudentUpdateEnvironmentCategories? Environment = null);

public sealed record StudentUpdatePhaseTimingResponse(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("campusId")] string CampusId,
    [property: JsonPropertyName("nonce")] Guid Nonce,
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("collectedUtc")] DateTimeOffset CollectedUtc,
    [property: JsonPropertyName("phases")] IReadOnlyList<StudentUpdatePhaseTiming> Phases);

public sealed record StudentUpdatePhaseTimingResponseV2(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("campusId")] string CampusId,
    [property: JsonPropertyName("nonce")] Guid Nonce,
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("collectedUtc")] DateTimeOffset CollectedUtc,
    [property: JsonPropertyName("phases")] IReadOnlyList<StudentUpdatePhaseTiming> Phases,
    [property: JsonPropertyName("environment")] StudentUpdateEnvironmentCategories Environment);

public sealed record StudentUpdatePhaseTimingResponseV3(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("campusId")] string CampusId,
    [property: JsonPropertyName("nonce")] Guid Nonce,
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("collectedUtc")] DateTimeOffset CollectedUtc,
    [property: JsonPropertyName("phases")] IReadOnlyList<StudentUpdatePhaseTiming> Phases,
    [property: JsonPropertyName("environment")] StudentUpdateEnvironmentCategories Environment);

public static class StudentUpdatePhaseTimingCryptography
{
    public const string Purpose = "VeyonCampus.StudentUpdatePhaseTimingResponse.v1";
    public const string PurposeV2 = "VeyonCampus.StudentUpdatePhaseTimingResponse.v2";
    public const string PurposeV3 = "VeyonCampus.StudentUpdatePhaseTimingResponse.v3";
    public static readonly TimeSpan MaximumResponseAge = TimeSpan.FromMinutes(25);

    public static StudentUpdatePhaseTimingResponse Create(StudentUpdatePhaseTimingSnapshot snapshot,
        WebsitePolicyStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != 4 || request.UpdateCommandId is not { } commandId ||
            commandId != snapshot.CommandId)
            throw new InvalidDataException("学生更新计时记录与签名查询不匹配。");
        return new StudentUpdatePhaseTimingResponse(1, Purpose, request.CampusId, request.Nonce,
            commandId, snapshot.CollectedUtc, WithoutRetryCounts(snapshot.Phases));
    }

    public static StudentUpdatePhaseTimingResponseV2 CreateV2(StudentUpdatePhaseTimingSnapshot snapshot,
        WebsitePolicyStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != 4 || request.UpdateCommandId is not { } commandId ||
            commandId != snapshot.CommandId)
            throw new InvalidDataException("学生更新计时记录与签名查询不匹配。");
        var environment = snapshot.Environment ?? StudentUpdateEnvironmentCategories.Unknown;
        if (!StudentUpdateEnvironmentCategories.IsValid(environment))
            throw new InvalidDataException("学生更新环境分类无效。");
        return new StudentUpdatePhaseTimingResponseV2(2, PurposeV2, request.CampusId, request.Nonce,
            commandId, snapshot.CollectedUtc, WithoutRetryCounts(snapshot.Phases), environment);
    }

    public static StudentUpdatePhaseTimingResponseV3 CreateV3(StudentUpdatePhaseTimingSnapshot snapshot,
        WebsitePolicyStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != 4 || request.UpdateCommandId is not { } commandId ||
            commandId != snapshot.CommandId)
            throw new InvalidDataException("学生更新计时记录与签名查询不匹配。");
        var environment = snapshot.Environment ?? StudentUpdateEnvironmentCategories.Unknown;
        if (!StudentUpdateEnvironmentCategories.IsValid(environment))
            throw new InvalidDataException("学生更新环境分类无效。");
        return new StudentUpdatePhaseTimingResponseV3(3, PurposeV3, request.CampusId, request.Nonce,
            commandId, snapshot.CollectedUtc, snapshot.Phases, environment);
    }

    public static void Validate(StudentUpdatePhaseTimingResponse response, WebsitePolicyStatusRequest request,
        string expectedCampusId, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(request);
        var now = nowUtc.ToUniversalTime();
        if (request.SchemaVersion != 4 || request.UpdateCommandId is not { } commandId ||
            response.SchemaVersion != 1 || response.Purpose != Purpose || response.CampusId != expectedCampusId ||
            response.CampusId != request.CampusId || response.Nonce != request.Nonce ||
            response.CommandId != commandId || response.CollectedUtc.Offset != TimeSpan.Zero ||
            response.CollectedUtc > now.AddMinutes(2) || response.CollectedUtc < now - MaximumResponseAge ||
            response.Phases is null || response.Phases.Count > StudentUpdatePhaseTimingStore.MaximumPhases)
            throw new InvalidDataException("学生更新阶段计时回执与本次签名查询不匹配。");
        StudentUpdatePhaseTimingStore.ValidateResponsePhases(response.Phases, response.CollectedUtc);
    }

    public static void ValidateV2(StudentUpdatePhaseTimingResponseV2 response,
        WebsitePolicyStatusRequest request, string expectedCampusId, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(request);
        var now = nowUtc.ToUniversalTime();
        if (request.SchemaVersion != 4 || request.UpdateCommandId is not { } commandId ||
            response.SchemaVersion != 2 || response.Purpose != PurposeV2 ||
            response.CampusId != expectedCampusId || response.CampusId != request.CampusId ||
            response.Nonce != request.Nonce || response.CommandId != commandId ||
            response.CollectedUtc.Offset != TimeSpan.Zero || response.CollectedUtc > now.AddMinutes(2) ||
            response.CollectedUtc < now - MaximumResponseAge ||
            response.Phases is null || response.Phases.Count > StudentUpdatePhaseTimingStore.MaximumPhases ||
            !StudentUpdateEnvironmentCategories.IsValid(response.Environment))
            throw new InvalidDataException("学生更新阶段计时 v2 回执与本次签名查询不匹配。");
        StudentUpdatePhaseTimingStore.ValidateResponsePhases(response.Phases, response.CollectedUtc);
    }

    public static void ValidateV3(StudentUpdatePhaseTimingResponseV3 response,
        WebsitePolicyStatusRequest request, string expectedCampusId, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(request);
        var now = nowUtc.ToUniversalTime();
        if (request.SchemaVersion != 4 || request.UpdateCommandId is not { } commandId ||
            response.SchemaVersion != 3 || response.Purpose != PurposeV3 ||
            response.CampusId != expectedCampusId || response.CampusId != request.CampusId ||
            response.Nonce != request.Nonce || response.CommandId != commandId ||
            response.CollectedUtc.Offset != TimeSpan.Zero || response.CollectedUtc > now.AddMinutes(2) ||
            response.CollectedUtc < now - MaximumResponseAge ||
            response.Phases is null || response.Phases.Count > StudentUpdatePhaseTimingStore.MaximumPhases ||
            !StudentUpdateEnvironmentCategories.IsValid(response.Environment))
            throw new InvalidDataException("学生更新阶段计时 v3 回执与本次签名查询不匹配。");
        StudentUpdatePhaseTimingStore.ValidateResponsePhases(response.Phases, response.CollectedUtc,
            allowRetryCounts: true);
    }

    private static IReadOnlyList<StudentUpdatePhaseTiming> WithoutRetryCounts(
        IReadOnlyList<StudentUpdatePhaseTiming> phases) =>
        Array.AsReadOnly(phases.Select(phase => phase with { RetryCount = null }).ToArray());
}

/// <summary>
/// Best-effort local timing sidecar for a StudentSetup + SYSTEM Agent update.
/// It is deliberately independent of the recovery journal: diagnostics must never
/// decide whether a transaction commits, rolls back, or is recoverable.
/// </summary>
public static class StudentUpdatePhaseTimingStore
{
    public const int MaximumPhases = 16;
    public const int MaximumSnapshotBytes = 32 * 1024;
    private const string DirectoryName = "student-update-phase-timings";
    private const int MaximumFiles = 128;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static StudentUpdatePhaseTimingRecorder Begin(string configPath, Guid commandId, string phase)
    {
        return BeginAt(configPath, commandId, phase, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
    }

    public static StudentUpdatePhaseTimingRecorder BeginAt(string configPath, Guid commandId, string phase,
        DateTimeOffset startedUtc, long startedTimestamp)
    {
        if (!OperatingSystem.IsWindows() || commandId == Guid.Empty || !StudentUpdatePhase.IsKnown(phase) ||
            startedUtc.Offset != TimeSpan.Zero || startedTimestamp <= 0)
            return StudentUpdatePhaseTimingRecorder.Disabled;
        var recorder = new StudentUpdatePhaseTimingRecorder(configPath, commandId, phase,
            startedUtc, startedTimestamp);
        TryWrite(configPath, commandId,
            new StudentUpdatePhaseTiming(phase, startedUtc, null, null, null, "inProgress"));
        return recorder;
    }

    public static StudentUpdatePhaseTimingSnapshot? TryRead(string configPath, Guid commandId)
    {
        if (!OperatingSystem.IsWindows() || commandId == Guid.Empty) return null;
        try
        {
            var path = GetPath(configPath, commandId);
            if (!File.Exists(path)) return null;
            PathLinkSecurity.RejectLinks(path);
            if (OperatingSystem.IsWindows() && !AgentFileSecurity.IsPrivate(path, directory: false))
                return null;
            var info = new FileInfo(path);
            if (info.Length is < 1 or > MaximumSnapshotBytes) return null;
            var snapshot = JsonSerializer.Deserialize<StudentUpdatePhaseTimingSnapshot>(File.ReadAllBytes(path),
                               JsonOptions)
                           ?? throw new InvalidDataException("学生更新计时记录为空。");
            Validate(snapshot, commandId);
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or InvalidDataException or
                                          JsonException or ArgumentException or CryptographicException)
        {
            return null;
        }
    }

    public static void RecordCompleted(string configPath, Guid commandId, string phase,
        DateTimeOffset startedUtc, long startedTimestamp, bool succeeded = true, long? bytes = null,
        long? integrityHashMilliseconds = null)
    {
        if (!OperatingSystem.IsWindows() || commandId == Guid.Empty || !StudentUpdatePhase.IsKnown(phase)) return;
        var elapsed = (long)Math.Max(0,
            (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency);
        TryWrite(configPath, commandId,
            new StudentUpdatePhaseTiming(phase, startedUtc, DateTimeOffset.UtcNow, elapsed,
                bytes is < 0 ? null : bytes, succeeded ? "succeeded" : "failed",
                integrityHashMilliseconds is < 0 ? null : integrityHashMilliseconds));
    }

    public static void AddIntegrityHashMeasurement(string configPath, Guid commandId,
        long hashMilliseconds, long bytes, bool? succeeded = null)
    {
        if (!OperatingSystem.IsWindows() || commandId == Guid.Empty || hashMilliseconds < 0 || bytes < 0) return;
        var existing = TryRead(configPath, commandId)?.Phases
            .FirstOrDefault(phase => phase.Phase == StudentUpdatePhase.IntegrityVerify);
        var now = DateTimeOffset.UtcNow;
        var updated = existing is null
            ? new StudentUpdatePhaseTiming(StudentUpdatePhase.IntegrityVerify, now, now, hashMilliseconds,
                bytes, succeeded == false ? "failed" : "succeeded", hashMilliseconds)
            : existing with
            {
                CompletedUtc = now,
                ElapsedMilliseconds = Math.Min(86_400_000,
                    (existing.ElapsedMilliseconds ?? 0) + hashMilliseconds),
                Bytes = bytes,
                Outcome = succeeded == false ? "failed" : existing.Outcome,
                IntegrityHashMilliseconds = hashMilliseconds
            };
        TryWrite(configPath, commandId, updated);
    }

    public static void RecordEnvironment(string configPath, Guid commandId,
        StudentUpdateEnvironmentCategories environment)
    {
        if (!OperatingSystem.IsWindows() || commandId == Guid.Empty ||
            !StudentUpdateEnvironmentCategories.IsValid(environment)) return;
        TryWrite(configPath, commandId, timing: null, environment: environment);
    }

    internal static void ValidateResponsePhases(IReadOnlyList<StudentUpdatePhaseTiming> phases,
        DateTimeOffset? collectedUtc = null, bool allowRetryCounts = false)
    {
        if (phases.Count > MaximumPhases) throw new InvalidDataException("学生更新阶段计时过多。");
        foreach (var phase in phases)
        {
            if (phase is null || !StudentUpdatePhase.IsKnown(phase.Phase) ||
                phase.StartedUtc.Offset != TimeSpan.Zero ||
                (phase.CompletedUtc is { } completed && completed.Offset != TimeSpan.Zero) ||
                !IsValidOutcome(phase.Outcome) ||
                (phase.ElapsedMilliseconds is < 0 or > 86_400_000) ||
                (phase.Bytes is < 0 or > 512L * 1024 * 1024) ||
                (phase.IntegrityHashMilliseconds is < 0 or > 86_400_000) ||
                (phase.RetryCount is not null && (!allowRetryCounts || phase.Phase != StudentUpdatePhase.LanTransfer ||
                                                   phase.RetryCount is < 0 or >
                                                       StudentApplicationUpdateArtifactTransfer.MaximumRetryCount)) ||
                ((phase.CompletedUtc is null) != (phase.ElapsedMilliseconds is null)) ||
                (phase.CompletedUtc is { } end && end < phase.StartedUtc) ||
                (collectedUtc is { } collected &&
                 (phase.StartedUtc < collected.AddDays(-1) || phase.StartedUtc > collected.AddMinutes(2) ||
                  phase.CompletedUtc is { } completedAfter && completedAfter > collected.AddMinutes(2))) ||
                (phase.Outcome == "inProgress" && phase.CompletedUtc is not null) ||
                (phase.Outcome != "inProgress" && phase.CompletedUtc is null))
                throw new InvalidDataException("学生更新阶段计时字段无效。");
        }
    }

    internal static void TryWrite(string configPath, Guid commandId, StudentUpdatePhaseTiming timing)
    {
        TryWrite(configPath, commandId, timing, environment: null);
    }

    private static void TryWrite(string configPath, Guid commandId, StudentUpdatePhaseTiming? timing,
        StudentUpdateEnvironmentCategories? environment)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (commandId == Guid.Empty ||
                (timing is not null && (!StudentUpdatePhase.IsKnown(timing.Phase) ||
                                        !IsValidOutcome(timing.Outcome))) ||
                (environment is not null && !StudentUpdateEnvironmentCategories.IsValid(environment))) return;
            var path = GetPath(configPath, commandId);
            var directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            PathLinkSecurity.RejectLinks(directory);
            if (OperatingSystem.IsWindows() && !AgentFileSecurity.IsPrivate(directory, directory: true))
                AgentFileSecurity.SecurePrivateDirectory(directory);

            StudentUpdatePhaseTimingSnapshot snapshot;
            if (File.Exists(path))
            {
                PathLinkSecurity.RejectLinks(path);
                if (OperatingSystem.IsWindows() && !AgentFileSecurity.IsPrivate(path, directory: false))
                    return;
                var info = new FileInfo(path);
                if (info.Length is < 1 or > MaximumSnapshotBytes) return;
                snapshot = JsonSerializer.Deserialize<StudentUpdatePhaseTimingSnapshot>(File.ReadAllBytes(path),
                                JsonOptions)
                            ?? throw new InvalidDataException("学生更新计时记录为空。");
                Validate(snapshot, commandId);
            }
            else
            {
                snapshot = new StudentUpdatePhaseTimingSnapshot(1, commandId,
                    DateTimeOffset.UtcNow, Array.Empty<StudentUpdatePhaseTiming>());
            }

            var phases = timing is null
                ? snapshot.Phases.ToArray()
                : snapshot.Phases.Where(item => item.Phase != timing.Phase).Append(timing)
                    .OrderBy(item => item.StartedUtc).TakeLast(MaximumPhases).ToArray();
            var updated = snapshot with
            {
                CollectedUtc = DateTimeOffset.UtcNow,
                Phases = Array.AsReadOnly(phases),
                Environment = environment ?? snapshot.Environment
            };
            Validate(updated, commandId);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions);
            if (bytes.Length > MaximumSnapshotBytes) return;
            WriteAtomically(path, bytes);
            Prune(directory, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or InvalidDataException or
                                          JsonException or ArgumentException or CryptographicException or
                                          NotSupportedException)
        {
            // Timing is diagnostic only. A failure to store it cannot interrupt an update.
        }
    }

    [SupportedOSPlatform("windows")]
    private static string GetPath(string configPath, Guid commandId)
    {
        var directory = StudentUpdateAtomicTransaction.GetAgentConfigurationDirectory(configPath);
        PathLinkSecurity.RejectLinks(directory);
        return Path.Combine(directory, DirectoryName, commandId.ToString("N") + ".json");
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsWindows()) AgentFileSecurity.SecurePrivateFile(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            if (OperatingSystem.IsWindows()) AgentFileSecurity.SecurePrivateFile(path);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Prune(string directory, string currentPath)
    {
        var candidates = Directory.EnumerateFiles(directory, "*.json")
            .Where(path => !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                try
                {
                    PathLinkSecurity.RejectLinks(path);
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) return null;
                    return new FileInfo(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  ArgumentException) { return null; }
            })
            .Where(info => info is not null)
            .OrderByDescending(info => info!.LastWriteTimeUtc)
            .ToArray();
        foreach (var old in candidates.Skip(MaximumFiles - 1))
        {
            try { old!.Delete(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Validate(StudentUpdatePhaseTimingSnapshot snapshot, Guid commandId)
    {
        var now = DateTimeOffset.UtcNow;
        if (snapshot.SchemaVersion != 1 || snapshot.CommandId != commandId ||
            snapshot.CollectedUtc.Offset != TimeSpan.Zero ||
            snapshot.CollectedUtc > now.AddMinutes(2) ||
            snapshot.CollectedUtc < now - StudentUpdatePhaseTimingCryptography.MaximumResponseAge ||
            snapshot.Phases is null || snapshot.Phases.Count > MaximumPhases)
            throw new InvalidDataException("学生更新计时记录与本次命令不匹配。");
        ValidateResponsePhases(snapshot.Phases, snapshot.CollectedUtc, allowRetryCounts: true);
        if (snapshot.Environment is not null &&
            !StudentUpdateEnvironmentCategories.IsValid(snapshot.Environment))
            throw new InvalidDataException("学生更新环境分类无效。");
    }

    private static bool IsValidOutcome(string outcome) => outcome is "inProgress" or "succeeded" or "failed";
}

public sealed class StudentUpdatePhaseTimingRecorder : IDisposable
{
    private readonly string? _configPath;
    private readonly Guid _commandId;
    private readonly string? _phase;
    private readonly DateTimeOffset _startedUtc;
    private readonly long _startedTimestamp;
    private int _completed;

    internal static StudentUpdatePhaseTimingRecorder Disabled { get; } = new();

    private StudentUpdatePhaseTimingRecorder() { }

    internal StudentUpdatePhaseTimingRecorder(string configPath, Guid commandId, string phase,
        DateTimeOffset startedUtc, long startedTimestamp)
    {
        _configPath = configPath;
        _commandId = commandId;
        _phase = phase;
        _startedUtc = startedUtc;
        _startedTimestamp = startedTimestamp;
    }

    public void Complete(bool succeeded = true, long? bytes = null, long? elapsedMilliseconds = null,
        int? retryCount = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0 || _phase is null || _configPath is null)
            return;
        var elapsed = elapsedMilliseconds ?? (long)Math.Max(0,
            (Stopwatch.GetTimestamp() - _startedTimestamp) * 1000d / Stopwatch.Frequency);
        StudentUpdatePhaseTimingStore.TryWrite(_configPath, _commandId,
            new StudentUpdatePhaseTiming(_phase, _startedUtc, DateTimeOffset.UtcNow, elapsed,
                bytes is < 0 ? null : bytes, succeeded ? "succeeded" : "failed",
                RetryCount: retryCount));
    }

    public void Dispose() => Complete(succeeded: false);
}
