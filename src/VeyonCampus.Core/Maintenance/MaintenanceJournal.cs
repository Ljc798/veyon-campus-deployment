namespace VeyonCampus.Core.Maintenance;

/// <summary>Immutable persistence DTO with per-device active-operation deduplication.</summary>
public sealed class MaintenanceJournal
{
    public const int CurrentSchemaVersion = 4;
    public int SchemaVersion { get; }
    public DateTimeOffset UpdatedUtc { get; }
    public IReadOnlyList<MaintenanceRun> Runs { get; }
    public IReadOnlyList<DesiredMaintenanceConfiguration> DesiredConfigurations { get; }
    public IReadOnlyList<ReportedMaintenanceConfiguration> ReportedConfigurations { get; }
    public IReadOnlyList<PendingMaintenanceConfiguration> PendingConfigurations { get; }

    public MaintenanceJournal(DateTimeOffset updatedUtc, IReadOnlyList<MaintenanceRun> runs,
        int schemaVersion = CurrentSchemaVersion,
        IReadOnlyList<DesiredMaintenanceConfiguration>? desiredConfigurations = null,
        IReadOnlyList<ReportedMaintenanceConfiguration>? reportedConfigurations = null,
        IReadOnlyList<PendingMaintenanceConfiguration>? pendingConfigurations = null)
    {
        if (schemaVersion is < 1 or > CurrentSchemaVersion)
            throw new InvalidDataException($"不支持的 Maintenance journal Schema {schemaVersion}。");
        ArgumentNullException.ThrowIfNull(runs);
        // Schema 2 adds signed transaction readbacks; Schema 3 adds Desired/Reported snapshots;
        // Schema 4 adds durable offline configuration intents.
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
        var desired = (desiredConfigurations ?? Array.Empty<DesiredMaintenanceConfiguration>()).ToArray();
        var reported = (reportedConfigurations ?? Array.Empty<ReportedMaintenanceConfiguration>()).ToArray();
        var pending = (pendingConfigurations ?? Array.Empty<PendingMaintenanceConfiguration>()).ToArray();
        if (desired.GroupBy(item => (item.DeviceIdentityFingerprint, item.Module)).Any(group => group.Count() > 1) ||
            reported.GroupBy(item => (item.DeviceIdentityFingerprint, item.Module)).Any(group => group.Count() > 1) ||
            pending.GroupBy(item => (item.DeviceIdentityFingerprint, item.Module)).Any(group => group.Count() > 1))
            throw new InvalidDataException("Maintenance journal 存在重复的 Desired/Reported 配置键。");
        if (desired.Any(item => reported.Any(value => value.DeviceIdentityFingerprint == item.DeviceIdentityFingerprint &&
                                                       value.Module == item.Module && value.Revision > item.Revision)))
            throw new InvalidDataException("Reported 配置 revision 超过对应设备当前 Desired revision。");
        DesiredConfigurations = Array.AsReadOnly(desired);
        ReportedConfigurations = Array.AsReadOnly(reported);
        PendingConfigurations = Array.AsReadOnly(pending);
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
        return new MaintenanceJournal(nowUtc, Runs.Append(requested).ToArray(), SchemaVersion,
            DesiredConfigurations, ReportedConfigurations, PendingConfigurations);
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
        return new MaintenanceJournal(nowUtc, updated, SchemaVersion,
            DesiredConfigurations, ReportedConfigurations, PendingConfigurations);
    }

    public MaintenanceJournal SetDesiredConfiguration(DesiredMaintenanceConfiguration desired,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var existing = DesiredConfigurations.FirstOrDefault(item =>
            item.DeviceIdentityFingerprint == desired.DeviceIdentityFingerprint && item.Module == desired.Module);
        if (existing is not null && desired.Revision < existing.Revision)
            throw new InvalidDataException("Desired 配置 revision 不能倒退。");
        if (existing is not null && desired.Revision == existing.Revision && existing != desired)
            throw new InvalidDataException("同一 Desired revision 不能更改 schema、指纹或签名意图。");
        var reported = ReportedConfigurations.FirstOrDefault(item =>
            item.DeviceIdentityFingerprint == desired.DeviceIdentityFingerprint && item.Module == desired.Module);
        if (reported is not null && reported.Revision > desired.Revision)
            throw new InvalidDataException("新的 Desired revision 不能低于已签名 Reported revision。");
        var values = DesiredConfigurations.Where(item =>
                item.DeviceIdentityFingerprint != desired.DeviceIdentityFingerprint || item.Module != desired.Module)
            .Append(desired).ToArray();
        return new MaintenanceJournal(nowUtc, Runs, CurrentSchemaVersion, values, ReportedConfigurations,
            PendingConfigurations);
    }

    public MaintenanceJournal SetReportedConfiguration(ReportedMaintenanceConfiguration reported,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(reported);
        var desired = DesiredConfigurations.FirstOrDefault(item =>
            item.DeviceIdentityFingerprint == reported.DeviceIdentityFingerprint && item.Module == reported.Module);
        if (desired is null)
            throw new InvalidDataException("没有对应的 Desired 配置；拒绝接纳孤立的 Reported 状态。");
        if (reported.Revision > desired.Revision)
            throw new InvalidDataException("Reported revision 不能超过 Desired revision。");
        var existing = ReportedConfigurations.FirstOrDefault(item =>
            item.DeviceIdentityFingerprint == reported.DeviceIdentityFingerprint && item.Module == reported.Module);
        if (existing is not null && (reported.Revision < existing.Revision || reported.CollectedUtc < existing.CollectedUtc))
            throw new InvalidDataException("Reported revision 或采集时间不能倒退。");
        if (existing is not null && reported.Revision == existing.Revision &&
            (reported.SchemaVersion != existing.SchemaVersion || reported.Fingerprint != existing.Fingerprint ||
             reported.ApplyState != existing.ApplyState || reported.CollectedUtc <= existing.CollectedUtc))
            throw new InvalidDataException("同一 Reported revision 只能以内容相同且时间更新的签名读回刷新。");
        var values = ReportedConfigurations.Where(item =>
                item.DeviceIdentityFingerprint != reported.DeviceIdentityFingerprint || item.Module != reported.Module)
            .Append(reported).ToArray();
        return new MaintenanceJournal(nowUtc, Runs, CurrentSchemaVersion, DesiredConfigurations, values,
            PendingConfigurations);
    }

    public MaintenanceJournal SetPendingConfiguration(PendingMaintenanceConfiguration pending,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var existing = PendingConfigurations.FirstOrDefault(item =>
            item.DeviceIdentityFingerprint == pending.DeviceIdentityFingerprint && item.Module == pending.Module);
        if (existing is not null && pending.QueuedUtc < existing.QueuedUtc)
            throw new InvalidDataException("离线 Desired 配置的排队时间不能倒退。");
        if (existing == pending) return this;
        var values = PendingConfigurations.Where(item =>
                item.DeviceIdentityFingerprint != pending.DeviceIdentityFingerprint || item.Module != pending.Module)
            .Append(pending).ToArray();
        return new MaintenanceJournal(nowUtc, Runs, CurrentSchemaVersion, DesiredConfigurations,
            ReportedConfigurations, values);
    }

    public MaintenanceJournal ClearPendingConfiguration(string deviceIdentityFingerprint,
        MaintenanceConfigurationModule module, string expectedFingerprint, DateTimeOffset nowUtc)
    {
        var identity = MaintenanceValidation.Fingerprint(deviceIdentityFingerprint,
            nameof(deviceIdentityFingerprint));
        if (!Enum.IsDefined(module)) throw new ArgumentOutOfRangeException(nameof(module));
        var fingerprint = MaintenanceValidation.Fingerprint(expectedFingerprint, nameof(expectedFingerprint));
        var existing = PendingConfigurations.FirstOrDefault(item => item.DeviceIdentityFingerprint == identity &&
            item.Module == module);
        if (existing is null || existing.Fingerprint != fingerprint) return this;
        var values = PendingConfigurations.Where(item => item.DeviceIdentityFingerprint != identity ||
            item.Module != module).ToArray();
        return new MaintenanceJournal(nowUtc, Runs, CurrentSchemaVersion, DesiredConfigurations,
            ReportedConfigurations, values);
    }

    public PendingMaintenanceConfiguration? FindPendingConfiguration(string deviceIdentityFingerprint,
        MaintenanceConfigurationModule module)
    {
        var identity = MaintenanceValidation.Fingerprint(deviceIdentityFingerprint,
            nameof(deviceIdentityFingerprint));
        if (!Enum.IsDefined(module)) throw new ArgumentOutOfRangeException(nameof(module));
        return PendingConfigurations.FirstOrDefault(item =>
            item.DeviceIdentityFingerprint == identity && item.Module == module);
    }

    public MaintenanceRun? Find(Guid operationId) => Runs.FirstOrDefault(run => run.OperationId == operationId);

    public bool IsConfigurationInSync(string deviceIdentityFingerprint, MaintenanceConfigurationModule module)
    {
        return GetConfigurationStatus(deviceIdentityFingerprint, module).State ==
               MaintenanceConfigurationReconciliationState.InSync;
    }

    public MaintenanceConfigurationReconciliationStatus GetConfigurationStatus(
        string deviceIdentityFingerprint, MaintenanceConfigurationModule module)
    {
        var identity = MaintenanceValidation.Fingerprint(deviceIdentityFingerprint,
            nameof(deviceIdentityFingerprint));
        if (!Enum.IsDefined(module)) throw new ArgumentOutOfRangeException(nameof(module));
        var desired = DesiredConfigurations.FirstOrDefault(item =>
            item.DeviceIdentityFingerprint == identity && item.Module == module);
        var reported = ReportedConfigurations.FirstOrDefault(item =>
            item.DeviceIdentityFingerprint == identity && item.Module == module);
        var pending = PendingConfigurations.FirstOrDefault(item =>
            item.DeviceIdentityFingerprint == identity && item.Module == module);
        return MaintenanceConfigurationReconciliation.GetStatus(desired, reported, pending);
    }

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
