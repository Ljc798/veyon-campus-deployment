namespace VeyonCampus.Core.Maintenance;

/// <summary>Immutable persistence DTO with per-device active-operation deduplication.</summary>
public sealed class MaintenanceJournal
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; }
    public DateTimeOffset UpdatedUtc { get; }
    public IReadOnlyList<MaintenanceRun> Runs { get; }

    public MaintenanceJournal(DateTimeOffset updatedUtc, IReadOnlyList<MaintenanceRun> runs,
        int schemaVersion = CurrentSchemaVersion)
    {
        if (schemaVersion is not (1 or CurrentSchemaVersion))
            throw new InvalidDataException($"不支持的 Maintenance journal Schema {schemaVersion}。");
        ArgumentNullException.ThrowIfNull(runs);
        // Schema 2 adds signed post-operation version and transaction readbacks. Schema 1 remains
        // readable because those facts are optional; the next write upgrades its schema marker.
        SchemaVersion = CurrentSchemaVersion;
        UpdatedUtc = MaintenanceValidation.Utc(updatedUtc);
        var snapshots = runs.ToArray();
        if (snapshots.Select(run => run.OperationId).Distinct().Count() != snapshots.Length)
            throw new InvalidDataException("Maintenance journal 存在重复 operationId。");
        var activeDuplicates = snapshots.Where(run => !run.IsTerminal)
            .GroupBy(run => run.Device.Key)
            .FirstOrDefault(group => group.Count() > 1);
        if (activeDuplicates is not null)
            throw new InvalidDataException("同一固定设备身份存在多个活动 Maintenance 操作。");
        Runs = Array.AsReadOnly(snapshots.OrderBy(run => run.CreatedUtc).ToArray());
    }

    public static MaintenanceJournal Empty(DateTimeOffset nowUtc) => new(nowUtc, Array.Empty<MaintenanceRun>());

    /// <summary>Returns the existing operation on retries; never creates a second active run for one device.</summary>
    public MaintenanceJournal AddOrGet(MaintenanceRun requested, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var byOperation = Runs.FirstOrDefault(run => run.OperationId == requested.OperationId);
        if (byOperation is not null)
        {
            if (byOperation.Device.Key != requested.Device.Key ||
                requested.Plan is not null && !SameIntent(byOperation.Plan, requested.Plan))
                throw new InvalidDataException("重复 operationId 绑定了不同设备或维护目标；拒绝复用事务。");
            return this;
        }
        if (Runs.Any(run => !run.IsTerminal && run.Device.Key == requested.Device.Key))
            throw new InvalidOperationException("该设备已有活动维护操作；必须先恢复或核对原 operationId。");
        return new MaintenanceJournal(nowUtc, Runs.Append(requested).ToArray(), SchemaVersion);
    }

    public MaintenanceJournal Update(MaintenanceRun replacement, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var currentRuns = Runs.ToArray();
        var index = Array.FindIndex(currentRuns, run => run.OperationId == replacement.OperationId);
        if (index < 0) throw new KeyNotFoundException("Maintenance operationId 不在 journal 中。");
        var existing = Runs[index];
        if (existing.Device.Key != replacement.Device.Key || existing.CreatedUtc != replacement.CreatedUtc ||
            existing.Transitions.Count > replacement.Transitions.Count ||
            !existing.Transitions.SequenceEqual(replacement.Transitions.Take(existing.Transitions.Count)) ||
            existing.Capabilities is not null && !string.Equals(existing.Capabilities.SignedResponseSha256,
                replacement.Capabilities?.SignedResponseSha256, StringComparison.Ordinal) ||
            existing.CurrentVersions is not null && existing.CurrentVersions != replacement.CurrentVersions ||
            existing.AgentCommandId is not null && existing.AgentCommandId != replacement.AgentCommandId ||
            existing.VerifiedStatusUtc is not null &&
            (replacement.VerifiedStatusUtc is null || replacement.VerifiedStatusUtc < existing.VerifiedStatusUtc) ||
            existing.VerifiedStatusUtc == replacement.VerifiedStatusUtc &&
            (existing.VerifiedCurrentVersions != replacement.VerifiedCurrentVersions ||
             !string.Equals(existing.VerifiedUpdateTransactionState,
                 replacement.VerifiedUpdateTransactionState, StringComparison.Ordinal)) ||
            existing.VerifiedStatusUtc is null && replacement.VerifiedStatusUtc is null &&
            (replacement.VerifiedCurrentVersions is not null || replacement.VerifiedUpdateTransactionState is not null) ||
            existing.Plan is not null && !SameIntent(existing.Plan, replacement.Plan))
            throw new InvalidDataException("Maintenance journal 更新不是当前操作的单调延续。");
        if (replacement.UpdatedUtc < existing.UpdatedUtc)
            throw new InvalidDataException("Maintenance journal 更新时间不能倒退。");
        var updated = currentRuns;
        updated[index] = replacement;
        return new MaintenanceJournal(nowUtc, updated, SchemaVersion);
    }

    public MaintenanceRun? Find(Guid operationId) => Runs.FirstOrDefault(run => run.OperationId == operationId);

    private static bool SameIntent(MaintenancePlan? first, MaintenancePlan? second)
    {
        if (first is null || second is null) return first is null && second is null;
        return first.OperationId == second.OperationId && first.OperationKind == second.OperationKind &&
               string.Equals(first.TargetSetupVersion, second.TargetSetupVersion, StringComparison.Ordinal) &&
               string.Equals(first.TargetAgentVersion, second.TargetAgentVersion, StringComparison.Ordinal) &&
               string.Equals(first.TargetReleaseSha256, second.TargetReleaseSha256, StringComparison.Ordinal) &&
               string.Equals(first.ConfigurationSha256, second.ConfigurationSha256, StringComparison.Ordinal) &&
               first.Channel == second.Channel &&
               first.SynchronizeConfiguration == second.SynchronizeConfiguration;
    }
}
