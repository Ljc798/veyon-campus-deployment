namespace VeyonCampus.Core;

public enum StudentMaintenancePlanKind
{
    NoOp,
    UpdateOnly,
    ConfigurationOnly,
    UpdateThenConfiguration,
    RecoveryRequired,
    ManualRequired
}

public enum StudentMaintenanceTransport
{
    None,
    AuthenticatedAgentChannel,
    TrustedLegacyHttp,
    Unavailable
}

public sealed record StudentMaintenancePlanningInput(
    bool AgentIdentityTrusted,
    string? CurrentSetupVersion,
    string? CurrentAgentVersion,
    string TargetSetupVersion,
    string? TargetAgentVersion,
    int? StudentUpdateProtocolVersion,
    string? UpdateTransactionState,
    bool? TeacherChannelConfigurationMatches,
    bool HasAuthenticatedAgentChannel,
    string ReleaseChannel = "stable");

public sealed record StudentMaintenancePlan(
    StudentMaintenancePlanKind Kind,
    StudentMaintenanceTransport UpdateTransport,
    bool NeedsSoftwareUpdate,
    bool NeedsConfigurationSync,
    string Reason);

public enum StudentMaintenanceUpdateDisposition
{
    Unknown,
    NoUpdateRequired,
    CommittedAndHealthy,
    PendingVerification,
    InProgress,
    RecoveryRequired,
    RollbackPending,
    RolledBack,
    Failed,
    ManualRequired,
    IdentityChanged
}

public sealed record StudentMaintenanceUpdateOutcome(
    string Target,
    StudentMaintenanceUpdateDisposition Disposition,
    string Detail);

public sealed record StudentMaintenanceConfigurationSyncGate(
    IReadOnlyList<string> AllowedTargets,
    IReadOnlyList<StudentMaintenanceUpdateOutcome> BlockedTargets);

/// <summary>
/// Carries the per-device update conclusion into the next maintenance phase. Unknown or
/// missing results fail closed; only a confirmed no-op or verified committed update may sync.
/// </summary>
public sealed class StudentMaintenanceUpdateBatchResult
{
    private static readonly HashSet<StudentMaintenanceUpdateDisposition> ConfigurationSyncAllowed =
    [
        StudentMaintenanceUpdateDisposition.NoUpdateRequired,
        StudentMaintenanceUpdateDisposition.CommittedAndHealthy
    ];

    private readonly Dictionary<string, StudentMaintenanceUpdateOutcome> _outcomes;

    public StudentMaintenanceUpdateBatchResult(IEnumerable<StudentMaintenanceUpdateOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        _outcomes = new Dictionary<string, StudentMaintenanceUpdateOutcome>(StringComparer.OrdinalIgnoreCase);
        foreach (var outcome in outcomes)
        {
            ArgumentNullException.ThrowIfNull(outcome);
            if (string.IsNullOrWhiteSpace(outcome.Target))
                throw new ArgumentException("Maintenance target cannot be empty.", nameof(outcomes));
            _outcomes[outcome.Target] = outcome;
        }
    }

    public static StudentMaintenanceUpdateBatchResult UnknownFor(IEnumerable<string> targets) =>
        new(targets.Select(target => new StudentMaintenanceUpdateOutcome(target,
            StudentMaintenanceUpdateDisposition.Unknown, "尚无可信更新结论。")));

    public static StudentMaintenanceUpdateOutcome FromUpdateResult(StudentApplicationUpdatePushResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var disposition = result.PendingVerification
            ? StudentMaintenanceUpdateDisposition.PendingVerification
            : result.RolledBack
                ? StudentMaintenanceUpdateDisposition.RolledBack
                : result.NeedsReview
                    ? StudentMaintenanceUpdateDisposition.RecoveryRequired
                    : result.Succeeded
                        ? StudentMaintenanceUpdateDisposition.CommittedAndHealthy
                        : StudentMaintenanceUpdateDisposition.Failed;
        return new StudentMaintenanceUpdateOutcome(result.Target, disposition, result.Detail);
    }

    public StudentMaintenanceUpdateBatchResult WithOutcome(StudentMaintenanceUpdateOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var copy = new Dictionary<string, StudentMaintenanceUpdateOutcome>(_outcomes,
            StringComparer.OrdinalIgnoreCase)
        {
            [outcome.Target] = outcome
        };
        return new StudentMaintenanceUpdateBatchResult(copy.Values);
    }

    public StudentMaintenanceConfigurationSyncGate PlanConfigurationSync(IEnumerable<string> selectedTargets)
    {
        ArgumentNullException.ThrowIfNull(selectedTargets);
        var allowed = new List<string>();
        var blocked = new List<StudentMaintenanceUpdateOutcome>();
        foreach (var target in selectedTargets)
        {
            if (string.IsNullOrWhiteSpace(target))
                continue;
            if (!_outcomes.TryGetValue(target, out var outcome))
                outcome = new StudentMaintenanceUpdateOutcome(target,
                    StudentMaintenanceUpdateDisposition.Unknown, "没有此设备的签名更新结论。");
            if (ConfigurationSyncAllowed.Contains(outcome.Disposition))
                allowed.Add(target);
            else
                blocked.Add(outcome);
        }
        return new StudentMaintenanceConfigurationSyncGate(Array.AsReadOnly(allowed.ToArray()),
            Array.AsReadOnly(blocked.ToArray()));
    }
}

/// <summary>
/// Plans safe per-device maintenance from authenticated status facts. Unknown connection
/// configuration is treated as needing an idempotent signed sync; it is never assumed current.
/// </summary>
public static class StudentMaintenancePlanner
{
    public static StudentMaintenancePlan Create(StudentMaintenancePlanningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _ = ApplicationReleaseClient.CompareVersions(input.TargetSetupVersion, input.TargetSetupVersion);

        if (!input.AgentIdentityTrusted)
            return Manual("学生 Agent 身份尚未固定；先完成人工核对，再执行更新或连接配置同步。");

        if (input.UpdateTransactionState is "inProgress" or "needsReview")
            return new StudentMaintenancePlan(StudentMaintenancePlanKind.RecoveryRequired,
                StudentMaintenanceTransport.Unavailable, false, false,
                input.UpdateTransactionState == "inProgress"
                    ? "学生端已有未结束的原子更新事务；先重新查询该事务，不能并发下发新更新。"
                    : "学生端上次原子更新需要人工恢复核对；保留恢复材料，不下发新更新或配置。" );

        if (input.UpdateTransactionState is { } transactionState && transactionState is not
            ("notFound" or "committed" or "rolledBack" or "superseded"))
            return Manual($"学生端返回未知的原子更新状态“{transactionState}”；暂停配置同步并保留设备待核对。");

        if (string.IsNullOrWhiteSpace(input.CurrentSetupVersion) ||
            string.IsNullOrWhiteSpace(input.CurrentAgentVersion))
            return Manual("无法可信读取当前 StudentSetup 与 Agent 版本；需要人工维护。");

        var setupComparison = ApplicationReleaseClient.CompareVersions(input.CurrentSetupVersion,
            input.TargetSetupVersion);
        var agentComparison = input.TargetAgentVersion is null
            ? 0
            : ApplicationReleaseClient.CompareVersions(input.CurrentAgentVersion, input.TargetAgentVersion);
        var needsSoftwareUpdate = setupComparison < 0 || agentComparison < 0;

        if (input.TargetAgentVersion is null && setupComparison >= 0)
            return Manual("正式发行清单没有已签名且绑定安装包摘要的 Agent 目标版本；StudentSetup 已达标，但无法判断 Agent 是否需要更新。请发布带有 Agent 目标版本声明的正式发行，或人工维护。 ");

        if (setupComparison > 0 && agentComparison < 0)
            return Manual($"当前 StudentSetup {input.CurrentSetupVersion} 高于目标 {input.TargetSetupVersion}，但 Agent {input.CurrentAgentVersion} 低于目标 {input.TargetAgentVersion}；不能降级 Setup 覆盖修复。");

        if (needsSoftwareUpdate && !StudentAgentUpdateCompatibility.SupportsUpdateStatusProtocol(
                input.CurrentAgentVersion, input.StudentUpdateProtocolVersion, input.ReleaseChannel))
            return Manual("当前 Agent 未签名报告受支持的原子更新协议；不会下发安装包。");

        var needsConfigurationSync = input.TeacherChannelConfigurationMatches != true;
        var transport = !needsSoftwareUpdate
            ? StudentMaintenanceTransport.None
            : input.HasAuthenticatedAgentChannel
                ? StudentMaintenanceTransport.AuthenticatedAgentChannel
                : StudentMaintenanceTransport.TrustedLegacyHttp;

        if (needsSoftwareUpdate && transport == StudentMaintenanceTransport.Unavailable)
            return Manual("没有受支持且已认证的学生更新通道；不会下发安装包。");

        var kind = (needsSoftwareUpdate, needsConfigurationSync) switch
        {
            (false, false) => StudentMaintenancePlanKind.NoOp,
            (true, false) => StudentMaintenancePlanKind.UpdateOnly,
            (false, true) => StudentMaintenancePlanKind.ConfigurationOnly,
            _ => StudentMaintenancePlanKind.UpdateThenConfiguration
        };
        var reason = kind switch
        {
            StudentMaintenancePlanKind.NoOp => "软件和教师连接配置均已达标。",
            StudentMaintenancePlanKind.UpdateOnly => "仅需更新软件；连接配置已核验。",
            StudentMaintenancePlanKind.ConfigurationOnly => "软件版本已达标；需要核验并同步教师连接配置。",
            _ => "先更新完整 StudentSetup，再核验并同步教师连接配置。"
        };
        return new StudentMaintenancePlan(kind, transport, needsSoftwareUpdate,
            needsConfigurationSync, reason);
    }

    private static StudentMaintenancePlan Manual(string reason) =>
        new(StudentMaintenancePlanKind.ManualRequired, StudentMaintenanceTransport.Unavailable,
            false, false, reason);
}
