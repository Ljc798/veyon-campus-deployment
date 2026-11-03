namespace VeyonCampus.Core.Maintenance;

public enum MaintenanceOperationKind
{
    FullMaintenance,
    SoftwareUpdate,
    ConfigurationSync,
    Reconcile
}

public enum MaintenanceReleaseChannel
{
    Stable,
    LocalTest
}

/// <summary>An immutable, identity-bound maintenance intent for a single device.</summary>
public sealed class MaintenancePlan
{
    public Guid OperationId { get; }
    public MaintenanceDeviceIdentity Device { get; }
    public MaintenanceOperationKind OperationKind { get; }
    public MaintenanceComponentVersions CurrentVersions { get; }
    public string? TargetSetupVersion { get; }
    public string? TargetAgentVersion { get; }
    public MaintenanceCapabilities Capabilities { get; }
    public string? TargetReleaseSha256 { get; }
    public string? ConfigurationSha256 { get; }
    public bool SynchronizeConfiguration { get; }
    public MaintenanceReleaseChannel Channel { get; }
    public string Reason { get; }
    public DateTimeOffset PlannedUtc { get; }

    public MaintenancePlan(Guid operationId, MaintenanceDeviceIdentity device,
        MaintenanceOperationKind operationKind, MaintenanceComponentVersions currentVersions,
        string? targetSetupVersion, string? targetAgentVersion, MaintenanceCapabilities capabilities,
        string? targetReleaseSha256, string? configurationSha256, MaintenanceReleaseChannel channel,
        string reason, DateTimeOffset plannedUtc, bool synchronizeConfiguration = false)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("维护 operationId 不能为空。", nameof(operationId));
        OperationId = operationId;
        Device = device ?? throw new ArgumentNullException(nameof(device));
        if (!Enum.IsDefined(operationKind)) throw new ArgumentOutOfRangeException(nameof(operationKind));
        OperationKind = operationKind;
        CurrentVersions = currentVersions ?? throw new ArgumentNullException(nameof(currentVersions));
        TargetSetupVersion = MaintenanceValidation.OptionalVersion(targetSetupVersion,
            nameof(targetSetupVersion));
        TargetAgentVersion = MaintenanceValidation.OptionalVersion(targetAgentVersion,
            nameof(targetAgentVersion));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        if (!string.Equals(capabilities.AgentIdentityFingerprint, device.PinnedIdentityFingerprint,
                StringComparison.Ordinal))
            throw new InvalidDataException("签名能力集身份与维护设备固定身份不匹配。");
        if (currentVersions.AgentRuntimeVersion is { } currentAgent &&
            !string.Equals(currentAgent, capabilities.AgentRuntimeVersion, StringComparison.Ordinal))
            throw new InvalidDataException("能力集 Agent 运行版本与当前版本快照不一致。");
        TargetReleaseSha256 = targetReleaseSha256 is null
            ? null
            : MaintenanceValidation.Fingerprint(targetReleaseSha256, nameof(targetReleaseSha256));
        ConfigurationSha256 = configurationSha256 is null
            ? null
            : MaintenanceValidation.Fingerprint(configurationSha256, nameof(configurationSha256));
        SynchronizeConfiguration = synchronizeConfiguration || operationKind == MaintenanceOperationKind.ConfigurationSync;
        if ((operationKind is MaintenanceOperationKind.FullMaintenance or MaintenanceOperationKind.SoftwareUpdate) &&
            (TargetSetupVersion is not null || TargetAgentVersion is not null) && TargetReleaseSha256 is null)
            throw new InvalidDataException("软件更新维护计划必须绑定目标发行 SHA-256。");
        if (SynchronizeConfiguration && ConfigurationSha256 is null)
            throw new InvalidDataException("配置同步计划必须绑定目标教师连接配置指纹。");
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        Channel = channel;
        Reason = MaintenanceValidation.RequiredText(reason, nameof(reason));
        PlannedUtc = MaintenanceValidation.Utc(plannedUtc);
    }

    public MaintenancePlan WithDevice(MaintenanceDeviceIdentity device) =>
        new(OperationId, device, OperationKind, CurrentVersions, TargetSetupVersion, TargetAgentVersion,
            Capabilities, TargetReleaseSha256, ConfigurationSha256, Channel, Reason, PlannedUtc,
            SynchronizeConfiguration);
}
