using System.Security.Cryptography;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;

namespace VeyonCampus.App.Maintenance;

public sealed record TeacherMaintenanceTargetResult(string Target, Guid? OperationId,
    MaintenanceRunState State, string Summary, MaintenanceResult? Result = null,
    MaintenanceComponentVersions? CurrentVersions = null, MaintenanceCapabilities? Capabilities = null,
    MaintenanceComponentVersions? TargetVersions = null, MaintenanceOperationKind? OperationKind = null,
    MaintenanceReleaseChannel? Channel = null, string? UpdateTransactionState = null,
    bool? AuthenticatedChannelConnected = null, DateTimeOffset? AuthenticatedChannelCheckedUtc = null,
    StudentAgentTeacherChannelDiagnosticsSnapshot? TeacherChannelDiagnostics = null);

/// <summary>Plans, executes, and resumes device maintenance while keeping the ViewModel out of wire details.</summary>
public sealed class TeacherMaintenanceWorkflow
{
    private readonly TeacherMaintenanceCoordinator _coordinator;
    private readonly TeacherMaintenanceArtifactProvider _releases;
    private readonly StudentAgentIdentityTrustStore _trustStore;

    public TeacherMaintenanceWorkflow(TeacherMaintenanceCoordinator coordinator,
        TeacherMaintenanceArtifactProvider releases, StudentAgentIdentityTrustStore? trustStore = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _releases = releases ?? throw new ArgumentNullException(nameof(releases));
        _trustStore = trustStore ?? new StudentAgentIdentityTrustStore();
    }

    public async Task<IReadOnlyList<TeacherMaintenanceTargetResult>> MaintainAsync(string campusId,
        IEnumerable<string> targets, MaintenanceReleaseChannel channel,
        IProgress<MaintenanceCoordinatorProgress>? progress = null,
        CancellationToken cancellationToken = default,
        MaintenanceOperationKind? requestedOperation = null)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        ArgumentNullException.ThrowIfNull(targets);
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        if (requestedOperation is { } requested && requested is not (MaintenanceOperationKind.FullMaintenance or
                MaintenanceOperationKind.SoftwareUpdate or MaintenanceOperationKind.ConfigurationSync or
                MaintenanceOperationKind.MaintenanceServiceUpdate))
            throw new ArgumentOutOfRangeException(nameof(requestedOperation));
        var normalizedTargets = WebsitePolicyTransport.NormalizeTargets(targets);
        var outcomes = new List<TeacherMaintenanceTargetResult>();
        var devices = new List<MaintenanceDeviceIdentity>();
        var devicesByKey = new Dictionary<MaintenanceDeviceKey, MaintenanceDeviceIdentity>();
        var targetCountsByKey = new Dictionary<MaintenanceDeviceKey, int>();

        foreach (var target in normalizedTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pinnedPublicKey = _trustStore.FindTrustedPublicKey(campusId, target);
            if (pinnedPublicKey is null)
            {
                outcomes.Add(new TeacherMaintenanceTargetResult(target, null, MaintenanceRunState.ManualRequired,
                    "学生 Agent 身份尚未由教师核对固定；未发送维护命令。"));
                continue;
            }
            var fingerprint = StudentAgentResponseCryptography.GetFingerprint(pinnedPublicKey);
            var device = new MaintenanceDeviceIdentity(campusId, fingerprint, target);
            targetCountsByKey[device.Key] = targetCountsByKey.GetValueOrDefault(device.Key) + 1;
            devicesByKey[device.Key] = device;
        }

        foreach (var (key, count) in targetCountsByKey)
        {
            if (count > 1)
            {
                var duplicateRoutes = normalizedTargets.Where(target =>
                {
                    var keyPem = _trustStore.FindTrustedPublicKey(campusId, target);
                    return keyPem is not null && StudentAgentResponseCryptography.GetFingerprint(keyPem) ==
                        key.PinnedIdentityFingerprint;
                });
                foreach (var duplicate in duplicateRoutes)
                    outcomes.Add(new TeacherMaintenanceTargetResult(duplicate, null,
                        MaintenanceRunState.ManualRequired,
                        "多个网络地址使用同一 Agent 身份指纹；为避免把克隆身份当作多台设备，未执行维护。"));
                devicesByKey.Remove(key);
                continue;
            }
            if (devicesByKey.TryGetValue(key, out var device)) devices.Add(device);
        }

        if (devices.Count == 0) return Array.AsReadOnly(outcomes.ToArray());
        if (requestedOperation is not (MaintenanceOperationKind.SoftwareUpdate or
                MaintenanceOperationKind.MaintenanceServiceUpdate))
        {
            try
            {
                var desiredConnectivity = LegacyAgentMaintenanceAdapter
                    .ReadCurrentTeacherChannelConfiguration(campusId);
                using var signer = WebsitePolicySigningKeyStore.Open(campusId);
                var signerFingerprint = StudentAgentResponseCryptography.GetFingerprint(signer.PublicKeyPem);
                foreach (var device in devices)
                {
                    var status = await _coordinator.GetConfigurationStatusAsync(device.PinnedIdentityFingerprint,
                        MaintenanceConfigurationModule.TeacherConnectivity, cancellationToken).ConfigureAwait(false);
                    var existingPending = await _coordinator.GetPendingConfigurationAsync(
                        device.PinnedIdentityFingerprint, MaintenanceConfigurationModule.TeacherConnectivity,
                        cancellationToken).ConfigureAwait(false);
                    if (status.State == MaintenanceConfigurationReconciliationState.InSync &&
                        string.Equals(status.DesiredFingerprint, desiredConnectivity.Fingerprint,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (existingPending is not null)
                            await _coordinator.ClearPendingConfigurationAsync(device.PinnedIdentityFingerprint,
                                MaintenanceConfigurationModule.TeacherConnectivity, existingPending.Fingerprint,
                                cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    var revisionHint = existingPending?.NextRevisionHint ?? checked(
                        Math.Max(status.DesiredRevision ?? 0, status.ReportedRevision ?? 0) + 1);
                    await _coordinator.QueuePendingConfigurationAsync(new PendingMaintenanceConfiguration(
                        device.PinnedIdentityFingerprint, campusId,
                        MaintenanceConfigurationModule.TeacherConnectivity, 1, revisionHint,
                        desiredConnectivity.Fingerprint, signerFingerprint, desiredConnectivity.Endpoints,
                        desiredConnectivity.RootCertificateBase64, DateTimeOffset.UtcNow), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or CryptographicException or
                                              InvalidOperationException or ArgumentException)
            {
                foreach (var device in devices)
                    outcomes.Add(new TeacherMaintenanceTargetResult(device.RouteAddress, null,
                        MaintenanceRunState.PendingVerification,
                        "教师连接 Desired 未能持久化；离线重发队列未更新。本次维护检查继续执行：" +
                        Sanitize(exception.Message)));
            }
        }
        var inspections = await _coordinator.InspectAsync(devices, cancellationToken).ConfigureAwait(false);
        var executableOperationIds = new List<Guid>();
        var snapshotsByOperation = inspections.ToDictionary(item => item.OperationId);

        foreach (var snapshot in inspections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.State == MaintenanceRunState.PlanReady ||
                snapshot.State == MaintenanceRunState.AgentHealthVerified)
            {
                executableOperationIds.Add(snapshot.OperationId);
                continue;
            }
            if (snapshot.State is MaintenanceRunState.Updating or MaintenanceRunState.UpdateOffered or
                MaintenanceRunState.WaitingForCommit or MaintenanceRunState.PendingVerification or
                MaintenanceRunState.RecoveryRequired or MaintenanceRunState.ConfigurationSyncing)
            {
                var resumed = await _coordinator.ResumeAsync(snapshot.OperationId, cancellationToken)
                    .ConfigureAwait(false);
                outcomes.Add(ToOutcome(resumed, resumed.Transitions.LastOrDefault()?.Reason ??
                    "原维护事务仍在等待可信读回。", snapshot.AuthenticatedChannelConnected,
                    snapshot.UpdateTransactionState, snapshot.AuthenticatedChannelCheckedUtc,
                    snapshot.TeacherChannelDiagnostics));
            }
        }

        var planableInspections = inspections.Where(item => item.State == MaintenanceRunState.CapabilitiesVerified)
            .ToArray();
        var isMaintenanceServiceOperation = requestedOperation == MaintenanceOperationKind.MaintenanceServiceUpdate;
        var needsRelease = requestedOperation != MaintenanceOperationKind.ConfigurationSync &&
                           !isMaintenanceServiceOperation &&
                           planableInspections.Length > 0;
        TeacherMaintenanceRelease? release = null;
        string? desiredChannelFingerprint = null;
        string? releaseFailure = null;
        if (planableInspections.Length > 0 && !isMaintenanceServiceOperation && (needsRelease || requestedOperation is null or
                MaintenanceOperationKind.FullMaintenance or MaintenanceOperationKind.ConfigurationSync))
        {
            try
            {
                if (needsRelease)
                    release = await _releases.GetLatestReleaseAsync(channel, cancellationToken).ConfigureAwait(false);
                desiredChannelFingerprint = LegacyAgentMaintenanceAdapter
                    .ReadCurrentTeacherChannelFingerprint(campusId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              InvalidDataException or CryptographicException or IOException or
                                              UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                releaseFailure = "无法准备受信任的 StudentSetup 目标：" + Sanitize(exception.Message);
            }
        }

        foreach (var snapshot in inspections.Where(item => item.State == MaintenanceRunState.CapabilitiesVerified))
        {
            if (isMaintenanceServiceOperation)
            {
                if (snapshot.Capabilities is null || snapshot.CurrentVersions?.MaintenanceVersion is not { } currentMaintenanceVersion)
                {
                    outcomes.Add(new TeacherMaintenanceTargetResult(snapshot.Device.RouteAddress, snapshot.OperationId,
                        MaintenanceRunState.ManualRequired,
                        "无法从独立签名读回确认 MaintenanceService 当前版本；未创建更新计划。",
                        CurrentVersions: snapshot.CurrentVersions, Capabilities: snapshot.Capabilities,
                        UpdateTransactionState: snapshot.UpdateTransactionState,
                        AuthenticatedChannelConnected: snapshot.AuthenticatedChannelConnected,
                        AuthenticatedChannelCheckedUtc: snapshot.AuthenticatedChannelCheckedUtc,
                        TeacherChannelDiagnostics: snapshot.TeacherChannelDiagnostics));
                    continue;
                }

                MaintenanceServiceReleaseArtifact? serviceArtifact;
                try
                {
                    serviceArtifact = await _releases.GetNewerMaintenanceServiceReleaseAsync(
                        currentMaintenanceVersion, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                                  InvalidDataException or CryptographicException or IOException or
                                                  UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    outcomes.Add(new TeacherMaintenanceTargetResult(snapshot.Device.RouteAddress, snapshot.OperationId,
                        MaintenanceRunState.Failed,
                        "无法准备独立 MaintenanceService 候选发行：" + Sanitize(exception.Message),
                        CurrentVersions: snapshot.CurrentVersions, Capabilities: snapshot.Capabilities,
                        UpdateTransactionState: snapshot.UpdateTransactionState));
                    continue;
                }

                var servicePlan = new MaintenancePlan(snapshot.OperationId, snapshot.Device,
                    MaintenanceOperationKind.MaintenanceServiceUpdate, snapshot.CurrentVersions!,
                    null, null, snapshot.Capabilities, serviceArtifact?.Release.Manifest.Sha256, null,
                    MaintenanceReleaseChannel.Stable,
                    serviceArtifact is null ? "检查 MaintenanceService 独立发行版本。" :
                        "更新至签名 MaintenanceService 发行 " + serviceArtifact.Release.Manifest.Version + "。",
                    DateTimeOffset.UtcNow, synchronizeConfiguration: false,
                    targetMaintenanceServiceVersion: serviceArtifact?.Release.Manifest.Version);
                var servicePlanEvidence = new MaintenanceEvidence("signed-maintenance-service-plan",
                    snapshot.Device.PinnedIdentityFingerprint,
                    serviceArtifact?.Release.Manifest.Sha256 ?? snapshot.Capabilities.SignedResponseSha256,
                    null, snapshot.Capabilities.VerifiedUtc);
                var servicePlanRun = await _coordinator.PlanAsync(snapshot.OperationId, servicePlan,
                    servicePlanEvidence, servicePlan.Reason, cancellationToken).ConfigureAwait(false);
                executableOperationIds.Add(servicePlanRun.OperationId);
                continue;
            }

            if ((needsRelease && release is null) ||
                requestedOperation != MaintenanceOperationKind.ConfigurationSync && release is null ||
                desiredChannelFingerprint is null || snapshot.Capabilities is null ||
                snapshot.CurrentVersions is null)
            {
                outcomes.Add(new TeacherMaintenanceTargetResult(snapshot.Device.RouteAddress, snapshot.OperationId,
                    snapshot.State, releaseFailure ?? "维护检查缺少版本、能力或教师连接指纹；未创建计划。",
                    CurrentVersions: snapshot.CurrentVersions, Capabilities: snapshot.Capabilities,
                    UpdateTransactionState: snapshot.UpdateTransactionState,
                    AuthenticatedChannelConnected: snapshot.AuthenticatedChannelConnected,
                    AuthenticatedChannelCheckedUtc: snapshot.AuthenticatedChannelCheckedUtc,
                    TeacherChannelDiagnostics: snapshot.TeacherChannelDiagnostics));
                continue;
            }

            var capability = snapshot.Capabilities;
            var isConfigurationOnly = requestedOperation == MaintenanceOperationKind.ConfigurationSync;
            var targetSetupVersion = isConfigurationOnly
                ? snapshot.CurrentVersions.StudentSetupVersion
                : release!.Release.Manifest.Version;
            var targetAgentVersion = isConfigurationOnly
                ? snapshot.CurrentVersions.AgentRuntimeVersion
                : release!.StudentAgentVersion;
            var setupIsBelowTarget = targetSetupVersion is not null &&
                                     snapshot.CurrentVersions.StudentSetupVersion is { } currentSetupVersion &&
                                     ApplicationReleaseClient.CompareVersions(currentSetupVersion,
                                         targetSetupVersion) < 0;
            if (targetSetupVersion is null || targetAgentVersion is null && !setupIsBelowTarget)
            {
                var reason = targetAgentVersion is null
                    ? "StudentSetup 已达到或超过候选版本，但发行没有可信 Agent 目标；不能判断 Agent 是否达标。"
                    : "维护计划缺少已签名 Setup 版本，不能确定完整安装包是否需要更新。";
                var manual = await _coordinator.MarkManualRequiredAsync(snapshot.OperationId, reason,
                    cancellationToken).ConfigureAwait(false);
                outcomes.Add(ToOutcome(manual, reason, snapshot.AuthenticatedChannelConnected,
                    snapshot.UpdateTransactionState, snapshot.AuthenticatedChannelCheckedUtc,
                    snapshot.TeacherChannelDiagnostics));
                continue;
            }
            var planning = StudentMaintenancePlanner.Create(new StudentMaintenancePlanningInput(
                AgentIdentityTrusted: true,
                CurrentSetupVersion: snapshot.CurrentVersions.StudentSetupVersion,
                CurrentAgentVersion: snapshot.CurrentVersions.AgentRuntimeVersion,
                TargetSetupVersion: targetSetupVersion,
                TargetAgentVersion: targetAgentVersion,
                StudentUpdateProtocolVersion: capability.StudentUpdateProtocolVersion,
                UpdateTransactionState: snapshot.UpdateTransactionState ?? "unknown",
                TeacherChannelConfigurationMatches: capability.TeacherChannelFingerprint is { } reported
                    ? string.Equals(reported, desiredChannelFingerprint, StringComparison.OrdinalIgnoreCase)
                    : null,
                HasAuthenticatedAgentChannel: StudentAgentChannelHub.IsConnected(campusId,
                    snapshot.Device.RouteAddress),
                ReleaseChannel: isConfigurationOnly ? "stable" : release!.Release.Manifest.ReleaseChannel ?? "stable"));

            if (planning.Kind is StudentMaintenancePlanKind.ManualRequired or
                StudentMaintenancePlanKind.RecoveryRequired)
            {
                var manual = await _coordinator.MarkManualRequiredAsync(snapshot.OperationId, planning.Reason,
                    cancellationToken).ConfigureAwait(false);
                outcomes.Add(ToOutcome(manual, planning.Reason, snapshot.AuthenticatedChannelConnected,
                    snapshot.UpdateTransactionState, snapshot.AuthenticatedChannelCheckedUtc,
                    snapshot.TeacherChannelDiagnostics));
                continue;
            }

            var needsSoftwareUpdate = requestedOperation == MaintenanceOperationKind.ConfigurationSync
                ? false
                : requestedOperation == MaintenanceOperationKind.SoftwareUpdate
                    ? planning.NeedsSoftwareUpdate
                    : planning.NeedsSoftwareUpdate;
            var needsConfigurationSync = requestedOperation == MaintenanceOperationKind.SoftwareUpdate
                ? false
                : requestedOperation == MaintenanceOperationKind.ConfigurationSync || planning.NeedsConfigurationSync;
            var kind = requestedOperation switch
            {
                MaintenanceOperationKind.ConfigurationSync => MaintenanceOperationKind.ConfigurationSync,
                MaintenanceOperationKind.SoftwareUpdate => MaintenanceOperationKind.SoftwareUpdate,
                _ => (needsSoftwareUpdate, needsConfigurationSync) switch
                {
                    (true, true) => MaintenanceOperationKind.FullMaintenance,
                    (true, false) => MaintenanceOperationKind.SoftwareUpdate,
                    (false, true) => MaintenanceOperationKind.ConfigurationSync,
                    _ => MaintenanceOperationKind.SoftwareUpdate
                }
            };
            var plan = new MaintenancePlan(snapshot.OperationId, snapshot.Device, kind,
                snapshot.CurrentVersions, targetSetupVersion, targetAgentVersion,
                capability, isConfigurationOnly ? null : release!.Release.Manifest.Sha256,
                needsConfigurationSync ? desiredChannelFingerprint : null,
                isConfigurationOnly ? MaintenanceReleaseChannel.Stable : channel,
                requestedOperation == MaintenanceOperationKind.ConfigurationSync
                    ? "按请求仅核验并同步教师连接配置。"
                    : requestedOperation == MaintenanceOperationKind.SoftwareUpdate
                        ? "按请求仅更新 StudentSetup 与 Agent，不执行连接配置同步。"
                        : planning.Reason,
                DateTimeOffset.UtcNow, needsConfigurationSync);
            var planEvidence = new MaintenanceEvidence("signed-maintenance-plan",
                snapshot.Device.PinnedIdentityFingerprint, capability.SignedResponseSha256, null,
                capability.VerifiedUtc);
            var planned = await _coordinator.PlanAsync(snapshot.OperationId, plan, planEvidence,
                planning.Reason, cancellationToken).ConfigureAwait(false);
            executableOperationIds.Add(planned.OperationId);
        }

        if (executableOperationIds.Count > 0)
        {
            var results = await _coordinator.ExecuteAsync(executableOperationIds, progress, cancellationToken)
                .ConfigureAwait(false);
            foreach (var result in results)
            {
                var route = snapshotsByOperation[result.OperationId].Device.RouteAddress;
                outcomes.Add(new TeacherMaintenanceTargetResult(
                    route, result.OperationId,
                    result.State, result.Summary, result, result.CurrentVersions, null, result.TargetVersions,
                    result.OperationKind, result.Channel,
                    result.VerifiedUpdateTransactionState ??
                    snapshotsByOperation[result.OperationId].UpdateTransactionState,
                    StudentAgentChannelHub.IsConnected(campusId, route), DateTimeOffset.UtcNow,
                    snapshotsByOperation[result.OperationId].TeacherChannelDiagnostics));
            }
        }

        var finalResults = await _coordinator.GetResultsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var reportedOperations = outcomes.Where(item => item.OperationId is not null)
            .Select(item => item.OperationId!.Value).ToHashSet();
        foreach (var result in finalResults.Where(item => snapshotsByOperation.ContainsKey(item.OperationId) &&
                                                          !reportedOperations.Contains(item.OperationId)))
        {
            var route = snapshotsByOperation[result.OperationId].Device.RouteAddress;
            outcomes.Add(new TeacherMaintenanceTargetResult(
                route, result.OperationId, result.State,
                result.Summary, result, result.CurrentVersions, null, result.TargetVersions,
                result.OperationKind, result.Channel,
                result.VerifiedUpdateTransactionState ??
                snapshotsByOperation[result.OperationId].UpdateTransactionState,
                StudentAgentChannelHub.IsConnected(campusId, route), DateTimeOffset.UtcNow,
                snapshotsByOperation[result.OperationId].TeacherChannelDiagnostics));
        }
        for (var index = 0; index < outcomes.Count; index++)
        {
            var outcome = outcomes[index];
            if (outcome.OperationId is not { } operationId ||
                !snapshotsByOperation.TryGetValue(operationId, out var snapshot)) continue;
            var configStatus = await _coordinator.GetConfigurationStatusAsync(
                snapshot.Device.PinnedIdentityFingerprint, MaintenanceConfigurationModule.TeacherConnectivity,
                cancellationToken).ConfigureAwait(false);
            outcomes[index] = outcome with { Summary = AppendConfigurationStatus(outcome.Summary, configStatus) };
        }
        return Array.AsReadOnly(outcomes.ToArray());
    }

    public async Task<bool> HasPendingTeacherConnectivityAsync(string campusId, string target,
        CancellationToken cancellationToken = default)
    {
        var pinnedPublicKey = _trustStore.FindTrustedPublicKey(campusId, target);
        if (pinnedPublicKey is null) return false;
        var fingerprint = StudentAgentResponseCryptography.GetFingerprint(pinnedPublicKey);
        return await _coordinator.GetPendingConfigurationAsync(fingerprint,
            MaintenanceConfigurationModule.TeacherConnectivity, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<IReadOnlyList<TeacherMaintenanceTargetResult>> SynchronizePendingTeacherConnectivityAsync(
        string campusId, string target, CancellationToken cancellationToken = default)
    {
        var pinnedPublicKey = _trustStore.FindTrustedPublicKey(campusId, target);
        if (pinnedPublicKey is null) return Array.Empty<TeacherMaintenanceTargetResult>();
        var fingerprint = StudentAgentResponseCryptography.GetFingerprint(pinnedPublicKey);
        var pending = await _coordinator.GetPendingConfigurationAsync(fingerprint,
            MaintenanceConfigurationModule.TeacherConnectivity, cancellationToken).ConfigureAwait(false);
        if (pending is null || !string.Equals(pending.CampusId, campusId, StringComparison.Ordinal))
            return Array.Empty<TeacherMaintenanceTargetResult>();
        var active = await _coordinator.GetActiveOperationAsync(campusId, fingerprint, cancellationToken)
            .ConfigureAwait(false);
        if (active is not null && (active.Plan?.SynchronizeConfiguration != true ||
            !string.Equals(active.Plan.ConfigurationSha256, pending.Fingerprint,
                StringComparison.OrdinalIgnoreCase)))
            return Array.Empty<TeacherMaintenanceTargetResult>();
        return await MaintainAsync(campusId, [target], MaintenanceReleaseChannel.Stable,
            cancellationToken: cancellationToken,
            requestedOperation: MaintenanceOperationKind.ConfigurationSync).ConfigureAwait(false);
    }

    public async Task<TeacherMaintenanceTargetResult> ResumeAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("维护 operationId 不能为空。", nameof(operationId));
        var run = await _coordinator.ResumeAsync(operationId, cancellationToken).ConfigureAwait(false);
        MaintenanceAdapterInspection? inspection = null;
        try
        {
            inspection = await _coordinator.InspectCurrentAsync(run.Device, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or HttpRequestException or
                                          TaskCanceledException or InvalidOperationException or ArgumentException)
        {
            // A failed optional refresh must not erase the already reconciled transaction outcome.
        }
        var outcome = ToOutcome(run, run.Transitions.LastOrDefault()?.Reason ?? "已重新核对维护事务。",
            StudentAgentChannelHub.IsConnected(run.Device.CampusId, run.Device.RouteAddress),
            run.VerifiedUpdateTransactionState ?? inspection?.UpdateTransactionState, DateTimeOffset.UtcNow,
            inspection?.TeacherChannelDiagnostics);
        var configurationStatus = await _coordinator.GetConfigurationStatusAsync(
            run.Device.PinnedIdentityFingerprint, MaintenanceConfigurationModule.TeacherConnectivity,
            cancellationToken).ConfigureAwait(false);
        return outcome with { Summary = AppendConfigurationStatus(outcome.Summary, configurationStatus) };
    }

    private static string AppendConfigurationStatus(string summary,
        MaintenanceConfigurationReconciliationStatus status)
    {
        var label = status.State switch
        {
            MaintenanceConfigurationReconciliationState.NoDesiredConfiguration => "无教师连接配置目标",
            MaintenanceConfigurationReconciliationState.QueuedForDelivery => "等待设备重连后同步",
            MaintenanceConfigurationReconciliationState.AwaitingReport => "等待学生端签名读回",
            MaintenanceConfigurationReconciliationState.PendingApplication => "配置等待应用",
            MaintenanceConfigurationReconciliationState.Rejected => "学生端拒绝配置",
            MaintenanceConfigurationReconciliationState.Drifted => "配置存在漂移",
            MaintenanceConfigurationReconciliationState.InSync => "配置已同步",
            _ => "配置状态未知"
        };
        var revisions = status.DesiredRevision is { } desiredRevision
            ? $"Desired r{desiredRevision} / Reported {status.ReportedRevision?.ToString() ?? "未上报"}"
            : "未设置 Desired revision";
        var lastReport = status.LastReportedUtc is { } reportedUtc
            ? $"；最近签名读回 {reportedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "；尚无签名读回";
        return $"{summary} 配置：{label}（{revisions}）{lastReport}。";
    }

    private static TeacherMaintenanceTargetResult ToOutcome(MaintenanceRun run, string summary,
        bool? authenticatedChannelConnected = null, string? updateTransactionState = null,
        DateTimeOffset? authenticatedChannelCheckedUtc = null,
        StudentAgentTeacherChannelDiagnosticsSnapshot? channelDiagnostics = null) =>
        new(run.Device.RouteAddress, run.OperationId, run.State, summary,
            MaintenanceResult.FromRun(run, summary), run.CurrentVersions, run.Capabilities,
            run.Plan is null ? null : new MaintenanceComponentVersions(run.Plan.TargetSetupVersion,
                run.Plan.TargetAgentVersion, null, null),
            run.Plan?.OperationKind, run.Plan?.Channel,
            run.VerifiedUpdateTransactionState ?? updateTransactionState,
            authenticatedChannelConnected, authenticatedChannelCheckedUtc, channelDiagnostics);

    private static string Sanitize(string value)
    {
        var normalized = string.Concat(value.Where(character => !char.IsControl(character))).Trim();
        return normalized.Length > 512 ? normalized[..512] : normalized;
    }
}
