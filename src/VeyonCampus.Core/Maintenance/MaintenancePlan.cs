namespace VeyonCampus.Core.Maintenance;

public enum MaintenanceOperationKind
{
    FullMaintenance,
    SoftwareUpdate,
    ConfigurationSync,
    Reconcile,
    MaintenanceServiceUpdate
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
    public string? TargetMaintenanceServiceVersion { get; }
    public string? TargetMaintenanceServiceReleaseSha256 { get; }
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
        string reason, DateTimeOffset plannedUtc, bool synchronizeConfiguration = false,
        string? targetMaintenanceServiceVersion = null,
        string? targetMaintenanceServiceReleaseSha256 = null)
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
        TargetMaintenanceServiceVersion = MaintenanceValidation.OptionalVersion(targetMaintenanceServiceVersion,
            nameof(targetMaintenanceServiceVersion));
        var normalizedTargetReleaseSha256 = targetReleaseSha256 is null
            ? null
            : MaintenanceValidation.Fingerprint(targetReleaseSha256, nameof(targetReleaseSha256));
        TargetMaintenanceServiceReleaseSha256 = targetMaintenanceServiceReleaseSha256 is null
            ? operationKind == MaintenanceOperationKind.MaintenanceServiceUpdate
                ? normalizedTargetReleaseSha256
                : null
            : MaintenanceValidation.Fingerprint(targetMaintenanceServiceReleaseSha256,
                nameof(targetMaintenanceServiceReleaseSha256));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        if (!string.Equals(capabilities.AgentIdentityFingerprint, device.PinnedIdentityFingerprint,
                StringComparison.Ordinal))
            throw new InvalidDataException("签名能力集身份与维护设备固定身份不匹配。");
        if (currentVersions.AgentRuntimeVersion is { } currentAgent &&
            !string.Equals(currentAgent, capabilities.AgentRuntimeVersion, StringComparison.Ordinal))
            throw new InvalidDataException("能力集 Agent 运行版本与当前版本快照不一致。");
        TargetReleaseSha256 = operationKind == MaintenanceOperationKind.MaintenanceServiceUpdate
            ? null : normalizedTargetReleaseSha256;
        ConfigurationSha256 = configurationSha256 is null
            ? null
            : MaintenanceValidation.Fingerprint(configurationSha256, nameof(configurationSha256));
        SynchronizeConfiguration = synchronizeConfiguration || operationKind == MaintenanceOperationKind.ConfigurationSync;
        if ((operationKind is MaintenanceOperationKind.FullMaintenance or MaintenanceOperationKind.SoftwareUpdate) &&
            (TargetSetupVersion is not null || TargetAgentVersion is not null) && TargetReleaseSha256 is null)
            throw new InvalidDataException("软件更新维护计划必须绑定目标发行 SHA-256。");
        if (operationKind == MaintenanceOperationKind.MaintenanceServiceUpdate &&
            (currentVersions.MaintenanceVersion is null || TargetSetupVersion is not null ||
             TargetAgentVersion is not null || TargetReleaseSha256 is not null ||
             (TargetMaintenanceServiceVersion is null) != (TargetMaintenanceServiceReleaseSha256 is null)))
            throw new InvalidDataException("MaintenanceService 检查计划必须绑定当前版本；更新时须绑定独立发行 SHA-256。");
        if (operationKind == MaintenanceOperationKind.FullMaintenance &&
            (TargetMaintenanceServiceVersion is null) != (TargetMaintenanceServiceReleaseSha256 is null))
            throw new InvalidDataException("多阶段维护计划的 MaintenanceService 版本和独立发行摘要必须同时存在。");
        if (TargetMaintenanceServiceVersion is not null &&
            operationKind is not (MaintenanceOperationKind.MaintenanceServiceUpdate or
                MaintenanceOperationKind.FullMaintenance))
            throw new InvalidDataException("只有 MaintenanceService 更新或多阶段维护计划可以携带 MaintenanceService 目标版本。");
        if (TargetMaintenanceServiceVersion is not null && currentVersions.MaintenanceVersion is null)
            throw new InvalidDataException("MaintenanceService 更新计划缺少签名读回的当前服务版本。");
        if (TargetMaintenanceServiceVersion is not null &&
            ApplicationReleaseClient.CompareVersions(TargetMaintenanceServiceVersion!,
                currentVersions.MaintenanceVersion!) <= 0)
            throw new InvalidDataException("MaintenanceService 目标版本必须高于签名读回的当前版本。");
        if (TargetMaintenanceServiceVersion is null && TargetMaintenanceServiceReleaseSha256 is not null)
            throw new InvalidDataException("MaintenanceService 发行摘要缺少对应目标版本。");
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
            SynchronizeConfiguration, TargetMaintenanceServiceVersion, TargetMaintenanceServiceReleaseSha256);
}
