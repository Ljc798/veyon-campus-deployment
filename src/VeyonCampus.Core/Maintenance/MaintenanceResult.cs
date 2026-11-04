namespace VeyonCampus.Core.Maintenance;

public sealed record MaintenanceResult
{
    public Guid OperationId { get; }
    public MaintenanceDeviceKey Device { get; }
    public MaintenanceRunState State { get; }
    public Guid? AgentCommandId { get; }
    public MaintenanceComponentVersions? CurrentVersions { get; }
    public MaintenanceComponentVersions? VerifiedCurrentVersions { get; }
    public string? VerifiedUpdateTransactionState { get; }
    public DateTimeOffset? VerifiedStatusUtc { get; }
    public MaintenanceComponentVersions? TargetVersions { get; }
    public MaintenanceOperationKind? OperationKind { get; }
    public MaintenanceReleaseChannel? Channel { get; }
    public string? CurrentConfigurationFingerprint { get; }
    public string? CurrentTeacherChannelFingerprint { get; }
    public string? TargetTeacherChannelFingerprint { get; }
    public DateTimeOffset RecordedUtc { get; }
    public string Summary { get; }
    public MaintenanceEvidence? Evidence { get; }
    public bool StudentSetupUpdateAttempted { get; }
    public bool IsComplete => State == MaintenanceRunState.Completed;

    public MaintenanceResult(Guid operationId, MaintenanceDeviceKey device, MaintenanceRunState state,
        DateTimeOffset recordedUtc, string summary, MaintenanceEvidence? evidence,
        Guid? agentCommandId = null, MaintenanceComponentVersions? currentVersions = null,
        MaintenanceComponentVersions? targetVersions = null, MaintenanceOperationKind? operationKind = null,
        MaintenanceReleaseChannel? channel = null, string? currentConfigurationFingerprint = null,
        string? currentTeacherChannelFingerprint = null, string? targetTeacherChannelFingerprint = null,
        MaintenanceComponentVersions? verifiedCurrentVersions = null,
        string? verifiedUpdateTransactionState = null, DateTimeOffset? verifiedStatusUtc = null,
        bool studentSetupUpdateAttempted = false)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("维护 operationId 不能为空。", nameof(operationId));
        OperationId = operationId;
        Device = device;
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        State = state;
        if (agentCommandId == Guid.Empty)
            throw new ArgumentException("Agent 更新 commandId 不能为空。", nameof(agentCommandId));
        AgentCommandId = agentCommandId;
        CurrentVersions = currentVersions;
        VerifiedCurrentVersions = verifiedCurrentVersions;
        VerifiedUpdateTransactionState = verifiedUpdateTransactionState;
        VerifiedStatusUtc = verifiedStatusUtc is { } statusUtc
            ? MaintenanceValidation.Utc(statusUtc)
            : null;
        if (VerifiedUpdateTransactionState is not null && VerifiedUpdateTransactionState is not
            ("notFound" or "inProgress" or "committed" or "rolledBack" or "needsReview" or "superseded"))
            throw new InvalidDataException("维护结果中的签名事务状态无效。" );
        if ((VerifiedCurrentVersions is not null || VerifiedUpdateTransactionState is not null) &&
            VerifiedStatusUtc is null)
            throw new InvalidDataException("签名状态结果缺少采集时间。" );
        TargetVersions = targetVersions;
        if (operationKind is { } kind && !Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(operationKind));
        OperationKind = operationKind;
        if (channel is { } releaseChannel && !Enum.IsDefined(releaseChannel))
            throw new ArgumentOutOfRangeException(nameof(channel));
        Channel = channel;
        CurrentConfigurationFingerprint = currentConfigurationFingerprint is null
            ? null
            : MaintenanceValidation.Fingerprint(currentConfigurationFingerprint,
                nameof(currentConfigurationFingerprint));
        CurrentTeacherChannelFingerprint = currentTeacherChannelFingerprint is null
            ? null
            : MaintenanceValidation.Fingerprint(currentTeacherChannelFingerprint,
                nameof(currentTeacherChannelFingerprint));
        TargetTeacherChannelFingerprint = targetTeacherChannelFingerprint is null
            ? null
            : MaintenanceValidation.Fingerprint(targetTeacherChannelFingerprint,
                nameof(targetTeacherChannelFingerprint));
        RecordedUtc = MaintenanceValidation.Utc(recordedUtc);
        Summary = MaintenanceValidation.RequiredText(summary, nameof(summary));
        Evidence = evidence;
        StudentSetupUpdateAttempted = studentSetupUpdateAttempted;
        if (state == MaintenanceRunState.Completed &&
            !string.Equals(evidence?.DeviceIdentityFingerprint, device.PinnedIdentityFingerprint,
                StringComparison.Ordinal))
            throw new InvalidDataException("已完成维护结果缺少当前固定设备身份的确认凭证。");
    }

    public static MaintenanceResult FromRun(MaintenanceRun run, string summary,
        MaintenanceEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        var latestEvidence = evidence ?? run.Transitions.LastOrDefault()?.Evidence;
        return new MaintenanceResult(run.OperationId, run.Device.Key, run.State, run.UpdatedUtc, summary,
            latestEvidence, run.AgentCommandId, run.CurrentVersions,
            run.Plan is null ? null : new MaintenanceComponentVersions(run.Plan.TargetSetupVersion,
                run.Plan.TargetAgentVersion, null, run.Plan.TargetMaintenanceServiceVersion),
            run.Plan?.OperationKind, run.Plan?.Channel, run.Capabilities?.ConfigurationFingerprint,
            run.Capabilities?.TeacherChannelFingerprint, run.Plan?.ConfigurationSha256,
            run.VerifiedCurrentVersions, run.VerifiedUpdateTransactionState, run.VerifiedStatusUtc,
            run.AgentCommandId is not null && !run.LastUpdateCommandIsMaintenanceService &&
            run.Plan?.OperationKind is MaintenanceOperationKind.SoftwareUpdate or
                MaintenanceOperationKind.FullMaintenance);
    }
}
