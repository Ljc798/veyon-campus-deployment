namespace VeyonCampus.Core.Maintenance;

public enum MaintenanceRunState
{
    Discovered,
    IdentityVerified,
    CapabilitiesVerified,
    PlanReady,
    UpdateOffered,
    Updating,
    WaitingForCommit,
    AgentHealthVerified,
    MaintenanceServiceHealthVerified,
    ConfigurationSyncing,
    NoChanges,
    PendingVerification,
    RecoveryRequired,
    RolledBack,
    ManualRequired,
    Failed,
    Completed
}

public sealed class MaintenanceEvidence : IEquatable<MaintenanceEvidence>
{
    public string Kind { get; }
    public string? DeviceIdentityFingerprint { get; }
    public string? Sha256 { get; }
    public Guid? CommandId { get; }
    public DateTimeOffset VerifiedUtc { get; }

    public MaintenanceEvidence(string kind, string? deviceIdentityFingerprint, string? sha256,
        Guid? commandId, DateTimeOffset verifiedUtc)
    {
        Kind = MaintenanceValidation.RequiredText(kind, nameof(kind), 64);
        DeviceIdentityFingerprint = deviceIdentityFingerprint is null
            ? null
            : MaintenanceValidation.Fingerprint(deviceIdentityFingerprint, nameof(deviceIdentityFingerprint));
        Sha256 = sha256 is null ? null : MaintenanceValidation.Fingerprint(sha256, nameof(sha256));
        if (commandId == Guid.Empty) throw new ArgumentException("维护证据 commandId 不能为空。", nameof(commandId));
        CommandId = commandId;
        VerifiedUtc = MaintenanceValidation.Utc(verifiedUtc);
    }

    // Journal 每次从磁盘重读都会产生新的 evidence 实例；必须按值比较，
    // 否则 MaintenanceTransition 记录相等性会在重读后失败，事务被误判为非单调延续。
    public bool Equals(MaintenanceEvidence? other) => other is not null &&
        string.Equals(Kind, other.Kind, StringComparison.Ordinal) &&
        string.Equals(DeviceIdentityFingerprint, other.DeviceIdentityFingerprint, StringComparison.Ordinal) &&
        string.Equals(Sha256, other.Sha256, StringComparison.Ordinal) &&
        CommandId == other.CommandId && VerifiedUtc == other.VerifiedUtc;

    public override bool Equals(object? obj) => obj is MaintenanceEvidence other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Kind, DeviceIdentityFingerprint, Sha256, CommandId, VerifiedUtc);

    public static bool operator ==(MaintenanceEvidence? left, MaintenanceEvidence? right) =>
        EqualityComparer<MaintenanceEvidence?>.Default.Equals(left, right);

    public static bool operator !=(MaintenanceEvidence? left, MaintenanceEvidence? right) => !(left == right);
}

public sealed record MaintenanceTransition(MaintenanceRunState From, MaintenanceRunState To,
    DateTimeOffset OccurredUtc, string Reason, MaintenanceEvidence? Evidence);

/// <summary>An immutable per-device state-machine snapshot.</summary>
public sealed class MaintenanceRun
{
    private static readonly IReadOnlyDictionary<MaintenanceRunState, IReadOnlySet<MaintenanceRunState>> Allowed =
        new Dictionary<MaintenanceRunState, IReadOnlySet<MaintenanceRunState>>
        {
            [MaintenanceRunState.Discovered] = Set(MaintenanceRunState.IdentityVerified,
                MaintenanceRunState.ManualRequired, MaintenanceRunState.Failed),
            [MaintenanceRunState.IdentityVerified] = Set(MaintenanceRunState.CapabilitiesVerified,
                MaintenanceRunState.ManualRequired, MaintenanceRunState.Failed),
            [MaintenanceRunState.CapabilitiesVerified] = Set(MaintenanceRunState.PlanReady,
                MaintenanceRunState.ManualRequired, MaintenanceRunState.Failed),
            [MaintenanceRunState.PlanReady] = Set(MaintenanceRunState.UpdateOffered,
                MaintenanceRunState.ConfigurationSyncing, MaintenanceRunState.NoChanges,
                MaintenanceRunState.ManualRequired, MaintenanceRunState.Failed),
            [MaintenanceRunState.UpdateOffered] = Set(MaintenanceRunState.Updating,
                MaintenanceRunState.PendingVerification, MaintenanceRunState.RecoveryRequired,
                MaintenanceRunState.RolledBack, MaintenanceRunState.Failed),
            [MaintenanceRunState.Updating] = Set(MaintenanceRunState.WaitingForCommit,
                MaintenanceRunState.MaintenanceServiceHealthVerified,
                MaintenanceRunState.PendingVerification, MaintenanceRunState.RecoveryRequired,
                MaintenanceRunState.RolledBack, MaintenanceRunState.Failed),
            [MaintenanceRunState.WaitingForCommit] = Set(MaintenanceRunState.AgentHealthVerified,
                MaintenanceRunState.MaintenanceServiceHealthVerified,
                MaintenanceRunState.PendingVerification, MaintenanceRunState.RecoveryRequired,
                MaintenanceRunState.RolledBack, MaintenanceRunState.Failed),
            [MaintenanceRunState.AgentHealthVerified] = Set(MaintenanceRunState.ConfigurationSyncing,
                MaintenanceRunState.Completed, MaintenanceRunState.PendingVerification,
                MaintenanceRunState.RecoveryRequired, MaintenanceRunState.ManualRequired,
                MaintenanceRunState.Failed),
            [MaintenanceRunState.MaintenanceServiceHealthVerified] = Set(MaintenanceRunState.UpdateOffered,
                MaintenanceRunState.ConfigurationSyncing, MaintenanceRunState.Completed,
                MaintenanceRunState.PendingVerification, MaintenanceRunState.RecoveryRequired,
                MaintenanceRunState.ManualRequired, MaintenanceRunState.Failed),
            [MaintenanceRunState.ConfigurationSyncing] = Set(MaintenanceRunState.Completed,
                MaintenanceRunState.PendingVerification, MaintenanceRunState.RecoveryRequired,
                MaintenanceRunState.ManualRequired, MaintenanceRunState.Failed),
            [MaintenanceRunState.NoChanges] = Set(MaintenanceRunState.Completed, MaintenanceRunState.Failed),
            [MaintenanceRunState.PendingVerification] = Set(MaintenanceRunState.WaitingForCommit,
                MaintenanceRunState.AgentHealthVerified, MaintenanceRunState.MaintenanceServiceHealthVerified,
                MaintenanceRunState.ConfigurationSyncing,
                MaintenanceRunState.Completed, MaintenanceRunState.RolledBack, MaintenanceRunState.RecoveryRequired,
                MaintenanceRunState.ManualRequired, MaintenanceRunState.Failed),
            [MaintenanceRunState.RecoveryRequired] = Set(MaintenanceRunState.PendingVerification,
                MaintenanceRunState.RolledBack, MaintenanceRunState.ManualRequired, MaintenanceRunState.Failed)
        };

    public Guid OperationId { get; }
    public MaintenanceDeviceIdentity Device { get; }
    public MaintenanceRunState State { get; }
    public MaintenanceCapabilities? Capabilities { get; }
    public MaintenanceComponentVersions? CurrentVersions { get; }
    public MaintenancePlan? Plan { get; }
    public Guid? AgentCommandId { get; }
    public MaintenanceComponentVersions? VerifiedCurrentVersions { get; }
    public string? VerifiedUpdateTransactionState { get; }
    public DateTimeOffset? VerifiedStatusUtc { get; }
    public DateTimeOffset CreatedUtc { get; }
    public DateTimeOffset UpdatedUtc { get; }
    public IReadOnlyList<MaintenanceTransition> Transitions { get; }
    public bool IsMaintenanceServiceUpdateStage =>
        (State is MaintenanceRunState.UpdateOffered or MaintenanceRunState.Updating or
            MaintenanceRunState.WaitingForCommit or MaintenanceRunState.PendingVerification or
            MaintenanceRunState.RecoveryRequired) &&
        (Plan?.OperationKind == MaintenanceOperationKind.MaintenanceServiceUpdate ||
         Plan?.TargetMaintenanceServiceVersion is not null &&
         Transitions.LastOrDefault(item => item.To == MaintenanceRunState.UpdateOffered)?.Evidence?.Kind ==
         "signed-maintenance-service-update-command");
    public bool LastUpdateCommandIsMaintenanceService =>
        Transitions.LastOrDefault(item => item.To == MaintenanceRunState.UpdateOffered)?.Evidence?.Kind ==
        "signed-maintenance-service-update-command";
    public bool IsTerminal => State is MaintenanceRunState.Completed or MaintenanceRunState.RolledBack or
        MaintenanceRunState.Failed or MaintenanceRunState.ManualRequired;

    public MaintenanceRun(Guid operationId, MaintenanceDeviceIdentity device, MaintenanceRunState state,
        MaintenanceCapabilities? capabilities, MaintenanceComponentVersions? currentVersions,
        MaintenancePlan? plan, Guid? agentCommandId,
        DateTimeOffset createdUtc, DateTimeOffset updatedUtc, IReadOnlyList<MaintenanceTransition> transitions,
        MaintenanceComponentVersions? verifiedCurrentVersions = null,
        string? verifiedUpdateTransactionState = null, DateTimeOffset? verifiedStatusUtc = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("维护 operationId 不能为空。", nameof(operationId));
        ArgumentNullException.ThrowIfNull(device);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (agentCommandId == Guid.Empty) throw new ArgumentException("Agent 更新 commandId 不能为空。", nameof(agentCommandId));
        if ((verifiedCurrentVersions is not null || verifiedUpdateTransactionState is not null) &&
            verifiedStatusUtc is null)
            throw new InvalidDataException("签名状态事实缺少采集时间。" );
        if (verifiedUpdateTransactionState is not null && verifiedUpdateTransactionState is not
            ("notFound" or "inProgress" or "committed" or "rolledBack" or "needsReview" or "superseded"))
            throw new InvalidDataException("签名更新事务状态值未知。" );
        ArgumentNullException.ThrowIfNull(transitions);
        var created = MaintenanceValidation.Utc(createdUtc);
        var updated = MaintenanceValidation.Utc(updatedUtc);
        if (updated < created) throw new InvalidDataException("Maintenance run 更新时间早于创建时间。");
        if (capabilities is not null && !string.Equals(capabilities.AgentIdentityFingerprint,
                device.PinnedIdentityFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Maintenance run 能力集绑定了另一台设备。");
        if (plan is not null && (plan.OperationId != operationId || plan.Device.Key != device.Key ||
                                 currentVersions is null || plan.CurrentVersions != currentVersions ||
                                 capabilities is null || !string.Equals(plan.Capabilities.SignedResponseSha256,
                                     capabilities.SignedResponseSha256, StringComparison.Ordinal)))
            throw new InvalidDataException("Maintenance run 计划与 operationId、设备身份或能力集不一致。");
        if ((state is MaintenanceRunState.CapabilitiesVerified or MaintenanceRunState.PlanReady or
                MaintenanceRunState.UpdateOffered or MaintenanceRunState.Updating or
                MaintenanceRunState.WaitingForCommit or MaintenanceRunState.AgentHealthVerified or
                MaintenanceRunState.MaintenanceServiceHealthVerified or
                MaintenanceRunState.ConfigurationSyncing or MaintenanceRunState.NoChanges or
                MaintenanceRunState.Completed) && currentVersions is null)
            throw new InvalidDataException("Maintenance run 已核验能力，但缺少当前组件版本快照。");
        if ((state is MaintenanceRunState.PlanReady or MaintenanceRunState.UpdateOffered or
                MaintenanceRunState.Updating or MaintenanceRunState.WaitingForCommit or
                MaintenanceRunState.AgentHealthVerified or MaintenanceRunState.MaintenanceServiceHealthVerified or
                MaintenanceRunState.ConfigurationSyncing or
                MaintenanceRunState.NoChanges or MaintenanceRunState.Completed) && plan is null)
            throw new InvalidDataException("Maintenance run 已进入计划执行阶段，但没有绑定维护计划。");
        if ((state is MaintenanceRunState.UpdateOffered or MaintenanceRunState.Updating or
                MaintenanceRunState.WaitingForCommit or MaintenanceRunState.AgentHealthVerified or
                MaintenanceRunState.MaintenanceServiceHealthVerified or
                MaintenanceRunState.RolledBack) && agentCommandId is null)
            throw new InvalidDataException("Maintenance run 更新阶段缺少 Agent commandId。");
        var history = transitions.ToArray();
        var expectedState = MaintenanceRunState.Discovered;
        var previousTime = created;
        foreach (var transition in history)
        {
            if (transition.From != expectedState || !Enum.IsDefined(transition.To) ||
                transition.OccurredUtc < previousTime ||
                string.IsNullOrWhiteSpace(transition.Reason) || transition.Reason.Length > 512 ||
                transition.Reason.Any(char.IsControl))
                throw new InvalidDataException("Maintenance run 状态历史不连续或时间/原因无效。");
            expectedState = transition.To;
            previousTime = transition.OccurredUtc;
        }
        if (expectedState != state || history.Length == 0 && state != MaintenanceRunState.Discovered ||
            updated < previousTime)
            throw new InvalidDataException("Maintenance run 当前状态与其完整转换历史不一致。");
        var latestUpdateCommand = history.LastOrDefault(item => item.To == MaintenanceRunState.UpdateOffered)
            ?.Evidence?.CommandId;
        if (agentCommandId != latestUpdateCommand)
            throw new InvalidDataException("Maintenance run 当前 commandId 与追加式更新阶段历史不一致。");
        if (agentCommandId is not null && history.LastOrDefault(item =>
                item.To == MaintenanceRunState.UpdateOffered)?.Evidence?.Kind is not
            ("signed-update-command" or "signed-maintenance-service-update-command"))
            throw new InvalidDataException("Maintenance run 当前更新阶段缺少已识别的签名命令摘要证据。");
        OperationId = operationId;
        Device = device;
        State = state;
        Capabilities = capabilities;
        CurrentVersions = currentVersions;
        Plan = plan;
        AgentCommandId = agentCommandId;
        VerifiedCurrentVersions = verifiedCurrentVersions;
        VerifiedUpdateTransactionState = verifiedUpdateTransactionState;
        VerifiedStatusUtc = verifiedStatusUtc is { } statusUtc
            ? MaintenanceValidation.Utc(statusUtc)
            : null;
        CreatedUtc = created;
        UpdatedUtc = updated;
        Transitions = Array.AsReadOnly(history);
    }

    public static MaintenanceRun Start(Guid operationId, MaintenanceDeviceIdentity device,
        DateTimeOffset startedUtc)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("维护 operationId 不能为空。", nameof(operationId));
        ArgumentNullException.ThrowIfNull(device);
        return new MaintenanceRun(operationId, device, MaintenanceRunState.Discovered, null, null, null,
            null, startedUtc, startedUtc, Array.Empty<MaintenanceTransition>());
    }

    public MaintenanceRun UpdateRouteAddress(string routeAddress)
    {
        var updatedDevice = Device.WithRouteAddress(routeAddress);
        if (string.Equals(updatedDevice.RouteAddress, Device.RouteAddress, StringComparison.OrdinalIgnoreCase))
            return this;
        var updatedPlan = Plan?.WithDevice(updatedDevice);
        return new MaintenanceRun(OperationId, updatedDevice, State, Capabilities, CurrentVersions, updatedPlan,
            AgentCommandId, CreatedUtc, UpdatedUtc, Transitions, VerifiedCurrentVersions,
            VerifiedUpdateTransactionState, VerifiedStatusUtc);
    }

    public MaintenanceRun VerifyIdentity(MaintenanceEvidence evidence, string reason, DateTimeOffset nowUtc) =>
        TransitionTo(MaintenanceRunState.IdentityVerified, reason, evidence, nowUtc);

    public MaintenanceRun VerifyCapabilities(MaintenanceCapabilities capabilities,
        MaintenanceComponentVersions currentVersions, MaintenanceEvidence evidence, string reason,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(currentVersions);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!string.Equals(capabilities.AgentIdentityFingerprint, Device.PinnedIdentityFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(evidence.DeviceIdentityFingerprint, Device.PinnedIdentityFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(evidence.Sha256, capabilities.SignedResponseSha256, StringComparison.Ordinal))
            throw new InvalidDataException("签名能力集来自另一台设备；维护状态未推进。");
        if (currentVersions.AgentRuntimeVersion is { } currentAgent &&
            !string.Equals(currentAgent, capabilities.AgentRuntimeVersion, StringComparison.Ordinal))
            throw new InvalidDataException("签名能力集 Agent 版本与组件版本快照不一致。");
        var withCapabilities = Copy(capabilities: capabilities, currentVersions: currentVersions);
        return withCapabilities.TransitionTo(MaintenanceRunState.CapabilitiesVerified, reason, evidence, nowUtc);
    }

    public MaintenanceRun SetPlan(MaintenancePlan plan, MaintenanceEvidence evidence, string reason,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.OperationId != OperationId || plan.Device.Key != Device.Key)
            throw new InvalidDataException("维护计划 operationId 或固定设备身份与当前运行不匹配。");
        if (Capabilities is null ||
            CurrentVersions is null || plan.CurrentVersions != CurrentVersions ||
            !string.Equals(plan.Capabilities.SignedResponseSha256, Capabilities.SignedResponseSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException("维护计划能力集与当前已验签能力集不一致。");
        return Copy(plan: plan).TransitionTo(MaintenanceRunState.PlanReady, reason, evidence, nowUtc);
    }

    public MaintenanceRun OfferUpdate(Guid agentCommandId, MaintenanceEvidence evidence, string reason,
        DateTimeOffset nowUtc)
    {
        if (agentCommandId == Guid.Empty) throw new ArgumentException("Agent 更新 commandId 不能为空.", nameof(agentCommandId));
        if (AgentCommandId is { } existingCommand)
        {
            if (existingCommand == agentCommandId)
            {
                if (State is MaintenanceRunState.UpdateOffered or MaintenanceRunState.Updating) return this;
                throw new InvalidOperationException("此 operationId 的新阶段 commandId 与上一阶段重复。");
            }
            if (State != MaintenanceRunState.MaintenanceServiceHealthVerified ||
                Plan?.OperationKind != MaintenanceOperationKind.FullMaintenance ||
                Plan.TargetSetupVersion is null && Plan.TargetAgentVersion is null ||
                !string.Equals(Transitions.LastOrDefault(item =>
                    item.To == MaintenanceRunState.MaintenanceServiceHealthVerified)?.Evidence?.Kind,
                    "signed-maintenance-service-committed", StringComparison.Ordinal))
                throw new InvalidOperationException("此 operationId 已绑定更新 commandId；当前阶段不允许开始第二阶段。");
        }
        if (Plan is null || Plan.OperationKind is MaintenanceOperationKind.ConfigurationSync or
                MaintenanceOperationKind.Reconcile)
            throw new InvalidOperationException("当前维护计划没有可下发的软件更新。");
        if (Plan.TargetMaintenanceServiceVersion is not null &&
            (State == MaintenanceRunState.PlanReady &&
             evidence.Kind != "signed-maintenance-service-update-command" ||
             State == MaintenanceRunState.MaintenanceServiceHealthVerified &&
             evidence.Kind != "signed-update-command"))
            throw new InvalidOperationException("多阶段维护必须先记录独立 MaintenanceService 命令，再启动 StudentSetup 阶段。");
        if (!Allowed.TryGetValue(State, out var allowed) || !allowed.Contains(MaintenanceRunState.UpdateOffered))
            throw new InvalidOperationException($"当前状态 {State} 不允许开始更新阶段。");
        if (evidence.CommandId != agentCommandId || evidence.Sha256 is null ||
            !string.Equals(evidence.DeviceIdentityFingerprint, Device.PinnedIdentityFingerprint,
                StringComparison.Ordinal))
            throw new InvalidDataException("更新阶段凭据没有绑定当前设备和新 commandId。");
        var occurredUtc = MaintenanceValidation.Utc(nowUtc);
        var transition = new MaintenanceTransition(State, MaintenanceRunState.UpdateOffered, occurredUtc,
            MaintenanceValidation.RequiredText(reason, nameof(reason)), evidence);
        return new MaintenanceRun(OperationId, Device, MaintenanceRunState.UpdateOffered, Capabilities,
            CurrentVersions, Plan, agentCommandId, CreatedUtc, occurredUtc,
            Transitions.Append(transition).ToArray(), VerifiedCurrentVersions,
            VerifiedUpdateTransactionState, VerifiedStatusUtc);
    }

    public MaintenanceRun RecordVerifiedStatus(MaintenanceComponentVersions verifiedVersions,
        string? updateTransactionState, DateTimeOffset verifiedUtc, MaintenanceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(verifiedVersions);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!string.Equals(evidence.DeviceIdentityFingerprint, Device.PinnedIdentityFingerprint,
                StringComparison.Ordinal) || evidence.Sha256 is null)
            throw new InvalidDataException("签名状态事实没有绑定当前固定设备身份和响应摘要。" );
        if (evidence.Kind is "signed-update-transaction-status" or
            "signed-maintenance-update-transaction-status" or "signed-maintenance-service-committed" or
            "signed-maintenance-service-transaction-status")
        {
            if (AgentCommandId is not { } commandId || evidence.CommandId != commandId)
                throw new InvalidDataException("签名更新状态 commandId 与当前维护操作不一致。" );
        }
        else if (evidence.Kind is "signed-configuration-final-status" or
                 "signed-maintenance-configuration-status")
        {
            var configurationCommandId = Transitions.LastOrDefault(item =>
                item.To == MaintenanceRunState.ConfigurationSyncing)?.Evidence?.CommandId;
            if (configurationCommandId is null || evidence.CommandId != configurationCommandId)
                throw new InvalidDataException("签名配置状态 commandId 与当前维护操作不一致。" );
        }
        else throw new InvalidDataException("维护状态读回证据类型无效。" );
        if (verifiedVersions.MaintenanceVersion is null && Plan?.TargetMaintenanceServiceVersion is { } targetService &&
            Transitions.Any(item => item.To == MaintenanceRunState.MaintenanceServiceHealthVerified &&
                item.Evidence?.Kind == "signed-maintenance-service-committed") &&
            (evidence.Kind is "signed-update-transaction-status" or
                "signed-maintenance-update-transaction-status"))
            verifiedVersions = new MaintenanceComponentVersions(verifiedVersions.StudentSetupVersion,
                verifiedVersions.AgentRuntimeVersion, verifiedVersions.AgentAssemblyVersion, targetService);
        var now = DateTimeOffset.UtcNow;
        return new MaintenanceRun(OperationId, Device, State, Capabilities, CurrentVersions, Plan, AgentCommandId,
            CreatedUtc, now < UpdatedUtc ? UpdatedUtc : now, Transitions, verifiedVersions,
            updateTransactionState, verifiedUtc);
    }

    public MaintenanceRun TransitionTo(MaintenanceRunState next, string reason, MaintenanceEvidence? evidence,
        DateTimeOffset nowUtc)
    {
        if (!Enum.IsDefined(next)) throw new ArgumentOutOfRangeException(nameof(next));
        var normalizedReason = MaintenanceValidation.RequiredText(reason, nameof(reason));
        var occurredUtc = MaintenanceValidation.Utc(nowUtc);
        if (next == State) return this;
        if (!Allowed.TryGetValue(State, out var allowed) || !allowed.Contains(next))
            throw new InvalidOperationException($"不允许维护状态从 {State} 转换到 {next}。");
        ValidateTransitionEvidence(next, evidence);
        var transition = new MaintenanceTransition(State, next, occurredUtc, normalizedReason, evidence);
        return new MaintenanceRun(OperationId, Device, next, Capabilities, CurrentVersions, Plan, AgentCommandId,
            CreatedUtc, occurredUtc, Transitions.Append(transition).ToArray(), VerifiedCurrentVersions,
            VerifiedUpdateTransactionState, VerifiedStatusUtc);
    }

    private void ValidateTransitionEvidence(MaintenanceRunState next, MaintenanceEvidence? evidence)
    {
        if (next == MaintenanceRunState.CapabilitiesVerified && Capabilities is null)
            throw new InvalidOperationException("没有已验签能力集，不能进入 CapabilitiesVerified。");
        if (next == MaintenanceRunState.PlanReady && Plan is null)
            throw new InvalidOperationException("没有与设备身份绑定的维护计划，不能进入 PlanReady。");
        if ((next is MaintenanceRunState.PlanReady or MaintenanceRunState.NoChanges) &&
            !string.Equals(evidence?.DeviceIdentityFingerprint, Device.PinnedIdentityFingerprint,
                StringComparison.Ordinal))
            throw new InvalidDataException($"维护状态 {next} 缺少当前固定设备身份的确认凭证。");
        if (next == MaintenanceRunState.MaintenanceServiceHealthVerified &&
            (Plan?.TargetMaintenanceServiceVersion is null ||
             evidence?.Kind != "signed-maintenance-service-committed"))
            throw new InvalidDataException("MaintenanceService 阶段缺少已绑定目标版本的签名提交与健康证据。");
        if (next == MaintenanceRunState.UpdateOffered && AgentCommandId is null)
            throw new InvalidOperationException("尚未绑定 Agent commandId，不能进入 UpdateOffered。");
        if (next is MaintenanceRunState.IdentityVerified or MaintenanceRunState.CapabilitiesVerified or
            MaintenanceRunState.AgentHealthVerified or MaintenanceRunState.MaintenanceServiceHealthVerified or
            MaintenanceRunState.RolledBack or
            MaintenanceRunState.Completed)
        {
            if (evidence is null || !string.Equals(evidence.DeviceIdentityFingerprint,
                    Device.PinnedIdentityFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException($"维护状态 {next} 缺少当前固定设备身份的确认凭证。");
        }
        if ((next is MaintenanceRunState.UpdateOffered or MaintenanceRunState.Updating or
                MaintenanceRunState.WaitingForCommit or MaintenanceRunState.AgentHealthVerified or
                MaintenanceRunState.MaintenanceServiceHealthVerified or MaintenanceRunState.RolledBack) &&
            AgentCommandId is { } expectedCommand &&
            evidence?.CommandId != expectedCommand)
            throw new InvalidDataException("维护状态缺少与 operationId 绑定的 Agent commandId 证据。");
        if (next == MaintenanceRunState.UpdateOffered && evidence?.CommandId != AgentCommandId)
            throw new InvalidDataException("更新下发状态必须绑定同一 Agent commandId 的证据。");
    }

    private MaintenanceRun Copy(MaintenanceCapabilities? capabilities = null,
        MaintenanceComponentVersions? currentVersions = null, MaintenancePlan? plan = null,
        Guid? agentCommandId = null) =>
        new(OperationId, Device, State, capabilities ?? Capabilities, currentVersions ?? CurrentVersions,
            plan ?? Plan, agentCommandId ?? AgentCommandId, CreatedUtc, UpdatedUtc, Transitions,
            VerifiedCurrentVersions, VerifiedUpdateTransactionState, VerifiedStatusUtc);

    private static IReadOnlySet<MaintenanceRunState> Set(params MaintenanceRunState[] values) =>
        new HashSet<MaintenanceRunState>(values);
}
