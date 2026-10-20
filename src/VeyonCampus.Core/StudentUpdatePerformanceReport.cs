using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public sealed record StudentUpdatePerformanceSample(
    string Outcome,
    bool Attempted,
    string? SourceStudentSetupVersion,
    string? SourceAgentVersion,
    StudentUpdateEnvironmentCategories? Environment,
    IReadOnlyList<StudentUpdatePhaseTiming> Phases);

public sealed record StudentUpdatePerformancePreparation(
    [property: JsonPropertyName("releaseLookupMilliseconds")] long? ReleaseLookupMilliseconds,
    [property: JsonPropertyName("timeToTeacherCacheReadyMilliseconds")] long? TimeToTeacherCacheReadyMilliseconds,
    [property: JsonPropertyName("teacherCacheHit")] bool? TeacherCacheHit,
    [property: JsonPropertyName("teacherFetchElapsedMilliseconds")] long? TeacherFetchElapsedMilliseconds,
    [property: JsonPropertyName("teacherFetchNetworkBytes")] long? TeacherFetchNetworkBytes,
    [property: JsonPropertyName("artifactSizeBytes")] long? ArtifactSizeBytes,
    [property: JsonPropertyName("teacherFetchBytesPerSecond")] long? TeacherFetchBytesPerSecond = null);

public sealed record StudentUpdatePerformanceBatchObservation(
    string BatchSizeBand,
    bool TeacherCachePreparationRequired,
    long? TimeToTeacherCacheReadyMilliseconds,
    long ClassroomRolloutDurationMilliseconds,
    bool Failed);

public sealed record StudentUpdatePerformanceCounts(
    [property: JsonPropertyName("selectedTargetCount")] int SelectedTargetCount,
    [property: JsonPropertyName("alreadyCurrentCount")] int AlreadyCurrentCount,
    [property: JsonPropertyName("preflightFailureCount")] int PreflightFailureCount,
    [property: JsonPropertyName("attemptedCount")] int AttemptedCount,
    [property: JsonPropertyName("succeededCount")] int SucceededCount,
    [property: JsonPropertyName("rolledBackCount")] int RolledBackCount,
    [property: JsonPropertyName("needsReviewCount")] int NeedsReviewCount,
    [property: JsonPropertyName("failedCount")] int FailedCount,
    [property: JsonPropertyName("notStartedCount")] int NotStartedCount,
    [property: JsonPropertyName("unresolvedCount")] int UnresolvedCount);

public sealed record StudentUpdatePerformanceMetric(
    [property: JsonPropertyName("sampleCount")] int SampleCount,
    [property: JsonPropertyName("completedCount")] int CompletedCount,
    [property: JsonPropertyName("failedCount")] int FailedCount,
    [property: JsonPropertyName("failureRate")] double? FailureRate,
    [property: JsonPropertyName("p50Milliseconds")] long? P50Milliseconds,
    [property: JsonPropertyName("p95Milliseconds")] long? P95Milliseconds,
    [property: JsonPropertyName("maxMilliseconds")] long? MaxMilliseconds);

public sealed record StudentUpdatePerformanceGroup(
    [property: JsonPropertyName("batchSizeBand")] string BatchSizeBand,
    [property: JsonPropertyName("networkType")] string NetworkType,
    [property: JsonPropertyName("osRelease")] string OsRelease,
    [property: JsonPropertyName("targetCount")] int TargetCount,
    [property: JsonPropertyName("succeededCount")] int SucceededCount,
    [property: JsonPropertyName("rolledBackCount")] int RolledBackCount,
    [property: JsonPropertyName("needsReviewCount")] int NeedsReviewCount,
    [property: JsonPropertyName("failedCount")] int FailedCount,
    [property: JsonPropertyName("timeToStudentInstalled")] StudentUpdatePerformanceMetric TimeToStudentInstalled,
    [property: JsonPropertyName("timeToTeacherConfirmed")] StudentUpdatePerformanceMetric TimeToTeacherConfirmed,
    [property: JsonPropertyName("phases")] IReadOnlyList<StudentUpdatePerformancePhaseMetric> Phases);

public sealed record StudentUpdatePerformancePhaseMetric(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("timing")] StudentUpdatePerformanceMetric Timing);

public sealed record StudentUpdatePerformanceBatchSizeGroup(
    [property: JsonPropertyName("batchSizeBand")] string BatchSizeBand,
    [property: JsonPropertyName("rolloutCount")] int RolloutCount,
    [property: JsonPropertyName("timeToTeacherCacheReady")] StudentUpdatePerformanceMetric TimeToTeacherCacheReady,
    [property: JsonPropertyName("classroomRolloutDuration")] StudentUpdatePerformanceMetric ClassroomRolloutDuration);

public sealed record StudentUpdatePerformanceSourceVersionGroup(
    [property: JsonPropertyName("studentSetupVersion")] string StudentSetupVersion,
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("targetCount")] int TargetCount);

public sealed record StudentUpdatePerformanceReport(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("rolloutId")] Guid RolloutId,
    [property: JsonPropertyName("startedUtc")] DateTimeOffset StartedUtc,
    [property: JsonPropertyName("completedUtc")] DateTimeOffset CompletedUtc,
    [property: JsonPropertyName("targetVersion")] string? TargetVersion,
    [property: JsonPropertyName("installationMode")] string InstallationMode,
    [property: JsonPropertyName("batchSizeBand")] string BatchSizeBand,
    [property: JsonPropertyName("classroomRolloutDurationMilliseconds")] long ClassroomRolloutDurationMilliseconds,
    [property: JsonPropertyName("counts")] StudentUpdatePerformanceCounts Counts,
    [property: JsonPropertyName("teacherPreparation")] StudentUpdatePerformancePreparation TeacherPreparation,
    [property: JsonPropertyName("timeToTeacherCacheReady")] StudentUpdatePerformanceMetric TimeToTeacherCacheReady,
    [property: JsonPropertyName("timeToStudentInstalled")] StudentUpdatePerformanceMetric TimeToStudentInstalled,
    [property: JsonPropertyName("timeToTeacherConfirmed")] StudentUpdatePerformanceMetric TimeToTeacherConfirmed,
    [property: JsonPropertyName("classroomRolloutDuration")] StudentUpdatePerformanceMetric ClassroomRolloutDuration,
    [property: JsonPropertyName("groups")] IReadOnlyList<StudentUpdatePerformanceGroup> Groups,
    [property: JsonPropertyName("sourceVersions")] IReadOnlyList<StudentUpdatePerformanceSourceVersionGroup> SourceVersions,
    [property: JsonPropertyName("batchSizeGroups")] IReadOnlyList<StudentUpdatePerformanceBatchSizeGroup> BatchSizeGroups);

/// <summary>
/// Produces a compact, aggregate-only report. The input samples deliberately have no
/// target name, address, campus identity, command ID, path, or download URL fields.
/// </summary>
public static class StudentUpdatePerformanceReportBuilder
{
    public const int MaximumReportBytes = 256 * 1024;
    private const int MaximumSourceVersionGroups = 32;
    public const int MaximumBatchObservations = 128;
    private static readonly HashSet<string> KnownBatchSizeBands = new(StringComparer.Ordinal)
    {
        "0", "1", "2-9", "10-19", "20+"
    };

    private static readonly string[] KnownPhases =
    [
        StudentUpdatePhase.TeacherOffer,
        StudentUpdatePhase.LanTransfer,
        StudentUpdatePhase.IntegrityVerify,
        StudentUpdatePhase.PrepareAndBackup,
        StudentUpdatePhase.SetupInstall,
        StudentUpdatePhase.AgentStage,
        StudentUpdatePhase.AgentActivate,
        StudentUpdatePhase.HealthAndCommit,
        StudentUpdatePhase.Rollback,
        StudentUpdatePhase.TeacherReceipt
    ];

    public static StudentUpdatePerformanceReport Create(Guid rolloutId, DateTimeOffset startedUtc,
        DateTimeOffset completedUtc, long classroomRolloutDurationMilliseconds, string? targetVersion,
        int selectedTargetCount, int alreadyCurrentCount, int preflightFailureCount, int notStartedCount,
        int unresolvedCount, bool teacherCachePreparationRequired,
        StudentUpdatePerformancePreparation teacherPreparation,
        IReadOnlyList<StudentUpdatePerformanceSample> samples,
        IReadOnlyList<StudentUpdatePerformanceBatchObservation>? previousBatchObservations = null)
    {
        ArgumentNullException.ThrowIfNull(teacherPreparation);
        ArgumentNullException.ThrowIfNull(samples);
        if (rolloutId == Guid.Empty || startedUtc.Offset != TimeSpan.Zero || completedUtc.Offset != TimeSpan.Zero ||
            completedUtc < startedUtc || classroomRolloutDurationMilliseconds < 0 || selectedTargetCount < 0)
            throw new InvalidDataException("学生端更新性能报告的批次信息无效。");

        var safeSamples = samples.Where(sample => sample is not null && IsValidOutcome(sample.Outcome))
            .Select(NormalizeSample).ToArray();
        var batchSizeBand = GetBatchSizeBand(selectedTargetCount);
        var succeededCount = safeSamples.Count(sample => sample.Attempted && sample.Outcome == "succeeded");
        var rolledBackCount = safeSamples.Count(sample => sample.Attempted && sample.Outcome == "rolledBack");
        var needsReviewCount = safeSamples.Count(sample => sample.Outcome == "needsReview");
        var failedCount = safeSamples.Count(sample => sample.Outcome == "failed");
        var attemptedCount = safeSamples.Count(sample => sample.Attempted);
        var normalizedPreflightFailures = Math.Clamp(preflightFailureCount, 0, selectedTargetCount);
        var normalizedAlreadyCurrent = Math.Clamp(alreadyCurrentCount, 0, selectedTargetCount - normalizedPreflightFailures);
        var normalizedNotStarted = Math.Clamp(notStartedCount, 0,
            selectedTargetCount - normalizedPreflightFailures - normalizedAlreadyCurrent);
        var normalizedUnresolved = Math.Clamp(unresolvedCount, 0,
            selectedTargetCount - normalizedPreflightFailures - normalizedAlreadyCurrent - normalizedNotStarted);

        var groups = safeSamples.Where(sample => sample.Attempted)
            .GroupBy(sample => (sample.Environment!.NetworkType, sample.Environment.OsRelease))
            .Select(group => BuildGroup(batchSizeBand, group.Key.NetworkType, group.Key.OsRelease,
                group.ToArray()))
            .OrderBy(group => group.NetworkType, StringComparer.Ordinal)
            .ThenBy(group => group.OsRelease, StringComparer.Ordinal)
            .ToArray();
        var sourceVersions = BuildSourceVersionGroups(safeSamples.Where(sample => sample.Attempted));
        var normalizedTargetVersion = NormalizeVersion(targetVersion);
        var preparation = NormalizePreparation(teacherPreparation);
        var overallAttempted = safeSamples.Where(sample => sample.Attempted).ToArray();
        var batchFailed = safeSamples.Any(sample => sample.Outcome != "succeeded") ||
                          normalizedNotStarted > 0 || normalizedUnresolved > 0;
        var currentObservation = new StudentUpdatePerformanceBatchObservation(batchSizeBand,
            teacherCachePreparationRequired, preparation.TimeToTeacherCacheReadyMilliseconds,
            classroomRolloutDurationMilliseconds, batchFailed);
        var priorObservations = (previousBatchObservations ?? Array.Empty<StudentUpdatePerformanceBatchObservation>())
            .Where(IsValidObservation)
            .TakeLast(selectedTargetCount > 0 ? 127 : MaximumBatchObservations);
        var history = selectedTargetCount > 0
            ? priorObservations.Append(currentObservation).ToArray()
            : priorObservations.ToArray();
        var batchSizeGroups = BuildBatchSizeGroups(history);
        var currentBatchGroup = batchSizeGroups.FirstOrDefault(group => group.BatchSizeBand == batchSizeBand) ??
                                new StudentUpdatePerformanceBatchSizeGroup(batchSizeBand, 0,
                                    CreateMetric(0, Array.Empty<long>(), 0),
                                    CreateMetric(0, Array.Empty<long>(), 0));
        var report = new StudentUpdatePerformanceReport(1, rolloutId, startedUtc, completedUtc,
            normalizedTargetVersion, "fullInstaller", batchSizeBand,
            classroomRolloutDurationMilliseconds,
            new StudentUpdatePerformanceCounts(selectedTargetCount, normalizedAlreadyCurrent,
                normalizedPreflightFailures, attemptedCount, succeededCount, rolledBackCount,
                needsReviewCount, failedCount, normalizedNotStarted, normalizedUnresolved),
            preparation,
            currentBatchGroup.TimeToTeacherCacheReady,
            BuildInstalledMetric(overallAttempted),
            BuildTeacherConfirmedMetric(overallAttempted),
            currentBatchGroup.ClassroomRolloutDuration,
            Array.AsReadOnly(groups),
            Array.AsReadOnly(sourceVersions),
            Array.AsReadOnly(batchSizeGroups));
        if (Serialize(report).Length > MaximumReportBytes)
            throw new InvalidDataException("学生端更新性能报告超过大小限制。");
        return report;
    }

    public static byte[] Serialize(StudentUpdatePerformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });
    }

    public static string GetBatchSizeBand(int selectedTargetCount) => selectedTargetCount switch
    {
        <= 0 => "0",
        1 => "1",
        <= 9 => "2-9",
        <= 19 => "10-19",
        _ => "20+"
    };

    private static StudentUpdatePerformanceSample NormalizeSample(StudentUpdatePerformanceSample sample)
    {
        var environment = StudentUpdateEnvironmentCategories.IsValid(sample.Environment)
            ? sample.Environment!
            : StudentUpdateEnvironmentCategories.Unknown;
        var phases = (sample.Phases ?? Array.Empty<StudentUpdatePhaseTiming>())
            .Where(phase => phase is not null && StudentUpdatePhase.IsKnown(phase.Phase) &&
                            phase.ElapsedMilliseconds is >= 0 and <= 86_400_000 &&
                            phase.Outcome is "succeeded" or "failed")
            .ToArray();
        return sample with
        {
            SourceStudentSetupVersion = NormalizeVersion(sample.SourceStudentSetupVersion),
            SourceAgentVersion = NormalizeVersion(sample.SourceAgentVersion),
            Environment = environment,
            Phases = Array.AsReadOnly(phases)
        };
    }

    private static StudentUpdatePerformanceGroup BuildGroup(string batchSizeBand, string networkType,
        string osRelease, IReadOnlyList<StudentUpdatePerformanceSample> samples)
    {
        var succeededCount = samples.Count(sample => sample.Outcome == "succeeded");
        var rolledBackCount = samples.Count(sample => sample.Outcome == "rolledBack");
        var needsReviewCount = samples.Count(sample => sample.Outcome == "needsReview");
        var failedCount = samples.Count(sample => sample.Outcome == "failed");
        var phases = KnownPhases.Select(phase =>
            {
                var phaseSamples = samples.SelectMany(sample => sample.Phases)
                    .Where(timing => timing.Phase == phase).ToArray();
                return new StudentUpdatePerformancePhaseMetric(phase, BuildPhaseMetric(phaseSamples));
            })
            .Where(phase => phase.Timing.SampleCount > 0)
            .ToArray();
        return new StudentUpdatePerformanceGroup(batchSizeBand, networkType, osRelease, samples.Count,
            succeededCount, rolledBackCount, needsReviewCount, failedCount,
            BuildInstalledMetric(samples), BuildTeacherConfirmedMetric(samples), Array.AsReadOnly(phases));
    }

    private static StudentUpdatePerformanceMetric BuildInstalledMetric(
        IReadOnlyList<StudentUpdatePerformanceSample> samples)
    {
        var succeeded = samples.Where(sample => sample.Outcome == "succeeded").ToArray();
        var values = succeeded.Select(GetStudentInstalledDuration).Where(value => value.HasValue)
            .Select(value => value!.Value).ToArray();
        var failed = samples.Count(sample => sample.Outcome != "succeeded");
        return CreateMetric(samples.Count, values, failed);
    }

    private static long? GetStudentInstalledDuration(StudentUpdatePerformanceSample sample)
    {
        var lan = sample.Phases.FirstOrDefault(phase => phase.Phase == StudentUpdatePhase.LanTransfer);
        var health = sample.Phases.FirstOrDefault(phase => phase.Phase == StudentUpdatePhase.HealthAndCommit);
        if (lan is not { Outcome: "succeeded" } || health is not { Outcome: "succeeded" } ||
            lan.StartedUtc.Offset != TimeSpan.Zero || health.CompletedUtc is not { } completed ||
            completed.Offset != TimeSpan.Zero ||
            completed < lan.StartedUtc) return null;
        var elapsed = (long)(completed - lan.StartedUtc).TotalMilliseconds;
        return elapsed is >= 0 and <= 86_400_000 ? elapsed : null;
    }

    private static StudentUpdatePerformanceMetric BuildTeacherConfirmedMetric(
        IReadOnlyList<StudentUpdatePerformanceSample> samples)
    {
        var receipts = samples.Select(sample => sample.Phases.FirstOrDefault(
            phase => phase.Phase == StudentUpdatePhase.TeacherReceipt)).ToArray();
        var values = receipts.Select(phase => phase?.ElapsedMilliseconds)
            .Where(value => value is >= 0 and <= 86_400_000)
            .Select(value => value!.Value).ToArray();
        var failed = receipts.Count(phase => phase is null || phase.Outcome != "succeeded");
        return CreateMetric(receipts.Length, values, failed);
    }

    private static StudentUpdatePerformanceMetric BuildPhaseMetric(
        IReadOnlyList<StudentUpdatePhaseTiming> phases)
    {
        var values = phases.Select(phase => phase.ElapsedMilliseconds)
            .Where(value => value is >= 0 and <= 86_400_000)
            .Select(value => value!.Value).ToArray();
        var failed = phases.Count(phase => phase.Outcome == "failed");
        return CreateMetric(phases.Count, values, failed);
    }

    private static StudentUpdatePerformanceMetric CreateMetric(int sampleCount, IReadOnlyList<long> values,
        int failedCount)
    {
        var ordered = values.Order().ToArray();
        var normalizedFailed = Math.Clamp(failedCount, 0, sampleCount);
        return new StudentUpdatePerformanceMetric(sampleCount, ordered.Length, normalizedFailed,
            sampleCount == 0 ? null : normalizedFailed / (double)sampleCount,
            Percentile(ordered, 0.50), Percentile(ordered, 0.95), ordered.Length == 0 ? null : ordered[^1]);
    }

    private static long? Percentile(IReadOnlyList<long> values, double percentile)
    {
        if (values.Count == 0) return null;
        var index = (int)Math.Ceiling(percentile * values.Count) - 1;
        return values[Math.Clamp(index, 0, values.Count - 1)];
    }

    private static StudentUpdatePerformanceSourceVersionGroup[] BuildSourceVersionGroups(
        IEnumerable<StudentUpdatePerformanceSample> samples)
    {
        var groups = samples.GroupBy(sample =>
                (sample.SourceStudentSetupVersion ?? "unknown", sample.SourceAgentVersion ?? "unknown"))
            .Select(group => new StudentUpdatePerformanceSourceVersionGroup(group.Key.Item1, group.Key.Item2,
                group.Count()))
            .OrderByDescending(group => group.TargetCount)
            .ThenBy(group => group.StudentSetupVersion, StringComparer.Ordinal)
            .ThenBy(group => group.AgentVersion, StringComparer.Ordinal)
            .ToArray();
        if (groups.Length <= MaximumSourceVersionGroups) return groups;
        var retained = groups.Take(MaximumSourceVersionGroups - 1).ToList();
        retained.Add(new StudentUpdatePerformanceSourceVersionGroup("other", "other",
            groups.Skip(MaximumSourceVersionGroups - 1).Sum(group => group.TargetCount)));
        return retained.ToArray();
    }

    private static StudentUpdatePerformanceBatchSizeGroup[] BuildBatchSizeGroups(
        IReadOnlyList<StudentUpdatePerformanceBatchObservation> observations) => observations
        .GroupBy(observation => observation.BatchSizeBand, StringComparer.Ordinal)
        .Select(group =>
        {
            var cacheAttempts = group.Where(observation => observation.TeacherCachePreparationRequired).ToArray();
            var cacheDurations = cacheAttempts.Select(observation => observation.TimeToTeacherCacheReadyMilliseconds)
                .Where(value => value is >= 0 and <= 86_400_000)
                .Select(value => value!.Value).ToArray();
            var classroomDurations = group.Select(observation => observation.ClassroomRolloutDurationMilliseconds)
                .Where(value => value is >= 0 and <= 86_400_000).ToArray();
            return new StudentUpdatePerformanceBatchSizeGroup(group.Key, group.Count(),
                CreateMetric(cacheAttempts.Length, cacheDurations,
                    cacheAttempts.Count(observation => observation.TimeToTeacherCacheReadyMilliseconds is null)),
                CreateMetric(group.Count(), classroomDurations, group.Count(observation => observation.Failed)));
        })
        .OrderBy(group => Array.IndexOf(new[] { "0", "1", "2-9", "10-19", "20+" }, group.BatchSizeBand))
        .ToArray();

    private static bool IsValidObservation(StudentUpdatePerformanceBatchObservation observation) =>
        observation is not null && KnownBatchSizeBands.Contains(observation.BatchSizeBand) &&
        observation.ClassroomRolloutDurationMilliseconds is >= 0 and <= 86_400_000 &&
        (observation.TimeToTeacherCacheReadyMilliseconds is null or >= 0 and <= 86_400_000) &&
        (observation.TeacherCachePreparationRequired ||
         observation.TimeToTeacherCacheReadyMilliseconds is null);

    private static StudentUpdatePerformancePreparation NormalizePreparation(
        StudentUpdatePerformancePreparation preparation)
    {
        var elapsed = ClampDuration(preparation.TeacherFetchElapsedMilliseconds);
        var bytes = ClampBytes(preparation.TeacherFetchNetworkBytes);
        var throughput = preparation.TeacherCacheHit == true || elapsed is not > 0 || bytes is null
            ? null
            : (long?)Math.Clamp(bytes.Value * 1000d / elapsed.Value, 0d, 512d * 1024 * 1024);
        return new StudentUpdatePerformancePreparation(ClampDuration(preparation.ReleaseLookupMilliseconds),
            ClampDuration(preparation.TimeToTeacherCacheReadyMilliseconds), preparation.TeacherCacheHit,
            elapsed, bytes, ClampBytes(preparation.ArtifactSizeBytes), throughput);
    }

    private static long? ClampDuration(long? value) => value is >= 0 and <= 86_400_000 ? value : null;
    private static long? ClampBytes(long? value) => value is >= 0 and <= 512L * 1024 * 1024 ? value : null;

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            _ = ApplicationReleaseClient.CompareVersions(value, value);
            return value;
        }
        catch (InvalidDataException) { return null; }
    }

    private static bool IsValidOutcome(string outcome) =>
        outcome is "succeeded" or "rolledBack" or "needsReview" or "failed";
}
