using VeyonCampus.Core.Maintenance;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.App.Maintenance;

public sealed record MaintenanceAdapterInspection(bool IdentityVerified,
    MaintenanceComponentVersions CurrentVersions, MaintenanceCapabilities? Capabilities,
    MaintenanceEvidence? IdentityEvidence, MaintenanceEvidence? CapabilitiesEvidence, string Detail,
    string? UpdateTransactionState = null, bool? AuthenticatedChannelConnected = null,
    DateTimeOffset? AuthenticatedChannelCheckedUtc = null,
    StudentAgentTeacherChannelDiagnosticsSnapshot? TeacherChannelDiagnostics = null);

public enum MaintenanceAdapterDisposition
{
    NoChanges,
    UpdateCommittedAndHealthy,
    UpdatePendingVerification,
    UpdateRolledBack,
    ConfigurationSynchronized,
    PendingVerification,
    RecoveryRequired,
    ManualRequired,
    Failed
}

public sealed record MaintenanceAdapterResult(Guid OperationId, MaintenanceAdapterDisposition Disposition,
    string Detail, MaintenanceEvidence? Evidence,
    MaintenanceComponentVersions? VerifiedCurrentVersions = null,
    string? VerifiedUpdateTransactionState = null, DateTimeOffset? VerifiedStatusUtc = null,
    long? ConfigurationRevision = null);

public sealed record MaintenanceUpdateRequest(MaintenancePlan Plan);

public sealed record MaintenanceUpdateCommandPrepared(Guid OperationId, Guid CommandId,
    string SignedCommandSha256);

public sealed record MaintenanceInspectionSnapshot(Guid OperationId, MaintenanceDeviceIdentity Device,
    MaintenanceRunState State, MaintenanceComponentVersions? CurrentVersions,
    MaintenanceCapabilities? Capabilities, string Detail, string? UpdateTransactionState = null,
    bool? AuthenticatedChannelConnected = null, DateTimeOffset? AuthenticatedChannelCheckedUtc = null,
    StudentAgentTeacherChannelDiagnosticsSnapshot? TeacherChannelDiagnostics = null);

public sealed record MaintenanceCoordinatorProgress(Guid OperationId, MaintenanceRunState State,
    string Detail, int Completed, int Total, Guid? CommandId = null, string? Target = null,
    IReadOnlyList<StudentUpdatePhaseTiming>? PhaseTimings = null,
    StudentUpdateEnvironmentCategories? Environment = null);

/// <summary>Legacy adapter boundary; the implementation uses signed status/HTTP and the existing LAN updater.</summary>
public interface ITeacherMaintenanceAdapter
{
    Task<MaintenanceAdapterInspection> InspectAsync(MaintenanceDeviceIdentity device,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MaintenanceAdapterResult>> OfferUpdatesAsync(
        IReadOnlyList<MaintenanceUpdateRequest> requests,
        Func<MaintenanceUpdateCommandPrepared, CancellationToken, Task> beforeCommandSend,
        IProgress<MaintenanceCoordinatorProgress>? progress, CancellationToken cancellationToken);

    Task<MaintenanceAdapterResult> OfferMaintenanceServiceUpdateAsync(MaintenancePlan plan,
        Func<MaintenanceUpdateCommandPrepared, CancellationToken, Task> beforeCommandSend,
        IProgress<MaintenanceCoordinatorProgress>? progress, CancellationToken cancellationToken);

    Task<MaintenanceAdapterResult> SynchronizeConfigurationAsync(MaintenancePlan plan,
        Func<MaintenanceUpdateCommandPrepared, CancellationToken, Task> beforeCommandSend,
        CancellationToken cancellationToken);

    Task<MaintenanceAdapterResult> ReconcileAsync(MaintenanceRun run, CancellationToken cancellationToken);
}

/// <summary>Device-level workflow; UI callers only submit identities/plans and render structured results.</summary>
public sealed class TeacherMaintenanceCoordinator
{
    private readonly IMaintenanceRunStore _store;
    private readonly ITeacherMaintenanceAdapter _adapter;

    public TeacherMaintenanceCoordinator(IMaintenanceRunStore store, ITeacherMaintenanceAdapter adapter)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public async Task<IReadOnlyList<MaintenanceInspectionSnapshot>> InspectAsync(
        IEnumerable<MaintenanceDeviceIdentity> devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var normalized = devices.GroupBy(device => device.Key)
            .Select(group => group.Last()).ToArray();
        var results = new List<MaintenanceInspectionSnapshot>(normalized.Length);
        foreach (var device in normalized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journal = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            var active = journal.Runs.FirstOrDefault(run => !run.IsTerminal && run.Device.Key == device.Key);
            if (active is not null)
            {
                var routed = active.UpdateRouteAddress(device.RouteAddress);
                if (!ReferenceEquals(routed, active))
                {
                    await _store.UpdateAsync(routed, cancellationToken).ConfigureAwait(false);
                    active = routed;
                }
                if (active.State is MaintenanceRunState.Discovered or MaintenanceRunState.IdentityVerified)
                {
                    results.Add(await InspectRunAsync(active, device, cancellationToken).ConfigureAwait(false));
                    continue;
                }
                results.Add(ToInspectionSnapshot(active, "该设备已有活动 operationId；没有创建第二个维护操作。"));
                continue;
            }

            var created = MaintenanceRun.Start(Guid.NewGuid(), device, DateTimeOffset.UtcNow);
            journal = await _store.AddOrGetAsync(created, cancellationToken).ConfigureAwait(false);
            var run = journal.Find(created.OperationId);
            if (run is null)
            {
                run = journal.Runs.FirstOrDefault(item => !item.IsTerminal && item.Device.Key == device.Key)
                      ?? throw new InvalidDataException("保存维护 operationId 后未读回活动设备记录。");
                run = run.UpdateRouteAddress(device.RouteAddress);
                journal = await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
                run = journal.Find(run.OperationId) ?? run;
                results.Add(ToInspectionSnapshot(run, "已有 operationId 抢先占用此设备；未重复检查或下发。"));
                continue;
            }

            results.Add(await InspectRunAsync(run, device, cancellationToken).ConfigureAwait(false));
        }
        return Array.AsReadOnly(results.ToArray());
    }

    public async Task<MaintenanceRun> PlanAsync(Guid operationId, MaintenancePlan plan,
        MaintenanceEvidence planEvidence, string reason, CancellationToken cancellationToken = default)
    {
        var run = await RequireRunAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (run.State == MaintenanceRunState.PlanReady)
        {
            if (run.Plan?.OperationId == plan.OperationId && SameTarget(run.Plan, plan)) return run;
            throw new InvalidOperationException("此 operationId 已绑定其他维护计划；请重新检查设备后创建新 operationId。");
        }
        var planned = run.SetPlan(plan, planEvidence, reason, DateTimeOffset.UtcNow);
        await _store.UpdateAsync(planned, cancellationToken).ConfigureAwait(false);
        return planned;
    }

    public async Task<MaintenanceRun> MarkManualRequiredAsync(Guid operationId, string reason,
        CancellationToken cancellationToken = default)
    {
        var run = await RequireRunAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (run.IsTerminal) return run;
        var manual = run.TransitionTo(MaintenanceRunState.ManualRequired, Sanitize(reason),
            run.Transitions.LastOrDefault(item => item.To == MaintenanceRunState.IdentityVerified)?.Evidence,
            DateTimeOffset.UtcNow);
        await _store.UpdateAsync(manual, cancellationToken).ConfigureAwait(false);
        return manual;
    }

    public async Task<IReadOnlyList<MaintenanceResult>> ExecuteAsync(IEnumerable<Guid> operationIds,
        IProgress<MaintenanceCoordinatorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationIds);
        var ids = operationIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        var initial = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var runs = ids.Select(id => initial.Find(id) ?? throw new KeyNotFoundException(
            $"Maintenance operationId {id:D} 不存在。" )).ToArray();
        var ready = runs.Where(run => run.State == MaintenanceRunState.PlanReady && run.Plan is not null).ToArray();

        foreach (var run in ready.Where(item => item.Plan!.OperationKind == MaintenanceOperationKind.Reconcile))
            await ResumeAsync(run.OperationId, cancellationToken).ConfigureAwait(false);

        foreach (var run in ready.Where(item => item.Plan!.OperationKind == MaintenanceOperationKind.MaintenanceServiceUpdate))
        {
            var plan = run.Plan!;
            try
            {
                var result = await _adapter.OfferMaintenanceServiceUpdateAsync(plan,
                    async (prepared, token) =>
                    {
                        await PersistPreparedUpdateCommandAsync(prepared, token).ConfigureAwait(false);
                        progress?.Report(new MaintenanceCoordinatorProgress(plan.OperationId,
                            MaintenanceRunState.Updating,
                            "MaintenanceService 签名更新命令已写入 journal，正在由独立 SYSTEM 服务执行。",
                            0, 1, prepared.CommandId, plan.Device.RouteAddress));
                    }, progress, cancellationToken).ConfigureAwait(false);
                await ApplyUpdateResultAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await MarkPendingOrFailedAsync(plan.OperationId,
                    "教师端停止等待 MaintenanceService 自更新；请使用原 commandId 重新读取事务。",
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or CryptographicException or
                                              InvalidOperationException or ArgumentException or TimeoutException)
            {
                await MarkPendingOrFailedAsync(plan.OperationId,
                    "MaintenanceService 更新工作流异常：" + Sanitize(exception.Message), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        var softwareUpdates = new List<MaintenanceUpdateRequest>();
        foreach (var run in ready.Where(item => item.Plan!.OperationKind is MaintenanceOperationKind.SoftwareUpdate or
                                                 MaintenanceOperationKind.FullMaintenance))
        {
            if (!NeedsSoftwareUpdate(run.Plan!, out var reason))
            {
                if (reason is not null)
                {
                    var manual = run.TransitionTo(MaintenanceRunState.ManualRequired, reason,
                        run.Transitions.FirstOrDefault(item => item.To == MaintenanceRunState.IdentityVerified)?.Evidence,
                        DateTimeOffset.UtcNow);
                    await _store.UpdateAsync(manual, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (run.Plan!.SynchronizeConfiguration) continue;
                var noChanges = run.TransitionTo(MaintenanceRunState.NoChanges, "Setup 与 Agent 目标均无需更新。",
                    TrustedEvidence(run), DateTimeOffset.UtcNow)
                    .TransitionTo(MaintenanceRunState.Completed, "软件版本已核验为无需更新。",
                        TrustedEvidence(run), DateTimeOffset.UtcNow);
                await _store.UpdateAsync(noChanges, cancellationToken).ConfigureAwait(false);
                continue;
            }
            softwareUpdates.Add(new MaintenanceUpdateRequest(run.Plan!));
        }

        if (softwareUpdates.Count > 0)
        {
            try
            {
                var results = await _adapter.OfferUpdatesAsync(softwareUpdates,
                    async (prepared, token) =>
                    {
                        await PersistPreparedUpdateCommandAsync(prepared, token).ConfigureAwait(false);
                        var target = softwareUpdates.FirstOrDefault(request =>
                            request.Plan.OperationId == prepared.OperationId)?.Plan.Device.RouteAddress;
                        progress?.Report(new MaintenanceCoordinatorProgress(prepared.OperationId,
                            MaintenanceRunState.Updating,
                            "签名更新命令与 operationId 已写入 journal，正在发送至学生 Agent。",
                            0, softwareUpdates.Count, prepared.CommandId, target));
                    }, progress, cancellationToken).ConfigureAwait(false);
                var byOperation = results.ToDictionary(result => result.OperationId);
                foreach (var request in softwareUpdates)
                {
                    if (!byOperation.TryGetValue(request.Plan.OperationId, out var result))
                    {
                        await MarkPendingOrFailedAsync(request.Plan.OperationId,
                            "更新适配器未返回此设备的结果。", cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    await ApplyUpdateResultAsync(result, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                foreach (var request in softwareUpdates)
                    await MarkPendingOrFailedAsync(request.Plan.OperationId,
                        "教师端停止等待；已发送或可能已发送的设备需重新查询事务。",
                        CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or CryptographicException or
                                              InvalidOperationException or ArgumentException or TimeoutException)
            {
                foreach (var request in softwareUpdates)
                    await MarkPendingOrFailedAsync(request.Plan.OperationId,
                        "更新适配器异常：" + Sanitize(exception.Message), CancellationToken.None)
                        .ConfigureAwait(false);
            }
        }

        var current = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var configurationRuns = current.Runs.Where(run => ids.Contains(run.OperationId) && run.Plan is not null &&
            run.Plan.SynchronizeConfiguration &&
            (run.State == MaintenanceRunState.PlanReady ||
             run.State == MaintenanceRunState.AgentHealthVerified)).ToArray();
        for (var index = 0; index < configurationRuns.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = configurationRuns[index];
            await ExecuteConfigurationSyncAsync(run.Plan!, index, configurationRuns.Length,
                progress, cancellationToken).ConfigureAwait(false);
        }

        var finalJournal = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(ids.Select(id => finalJournal.Find(id))
            .Where(run => run is not null)
            .Select(run => MaintenanceResult.FromRun(run!, run!.Transitions.LastOrDefault()?.Reason ??
                "维护仍在等待验证。"))
            .ToArray());
    }

    public async Task<MaintenanceRun> ResumeAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var run = await RequireRunAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (run.IsTerminal) return run;
        if (run.State == MaintenanceRunState.Discovered)
            return (await InspectAsync([run.Device], cancellationToken).ConfigureAwait(false))
                .First(item => item.Device.Key == run.Device.Key) is { } refreshed
                ? (await RequireRunAsync(refreshed.OperationId, cancellationToken).ConfigureAwait(false))
                : run;
        var result = await _adapter.ReconcileAsync(run, cancellationToken).ConfigureAwait(false);
        if (result.Disposition == MaintenanceAdapterDisposition.UpdateCommittedAndHealthy)
        {
            run = await RecordVerifiedStatusAsync(run, result, cancellationToken).ConfigureAwait(false);
            var evidence = RequireEvidence(run, result.Evidence, requireCommand: true);
            run = await MarkAgentHealthyAsync(run, evidence, result.Detail, cancellationToken).ConfigureAwait(false);
            if (run.Plan?.SynchronizeConfiguration == true)
                await ExecuteConfigurationSyncAsync(run.Plan, 0, 1, null, cancellationToken).ConfigureAwait(false);
            else if (!run.IsTerminal)
            {
                run = run.TransitionTo(MaintenanceRunState.Completed, result.Detail, evidence,
                    DateTimeOffset.UtcNow);
                await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
            }
            return await RequireRunAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        return await ApplyReconciliationResultAsync(run, result, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MaintenanceResult>> GetResultsAsync(MaintenanceDeviceKey? device = null,
        CancellationToken cancellationToken = default)
    {
        var journal = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(journal.Runs.Where(run => device is null || run.Device.Key == device.Value)
            .Select(run => MaintenanceResult.FromRun(run,
                run.Transitions.LastOrDefault()?.Reason ?? "已发现设备，尚未执行维护。"))
            .ToArray());
    }

    public async Task<MaintenanceConfigurationReconciliationStatus> GetConfigurationStatusAsync(
        string deviceIdentityFingerprint, MaintenanceConfigurationModule module,
        CancellationToken cancellationToken = default)
    {
        var journal = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return journal.GetConfigurationStatus(deviceIdentityFingerprint, module);
    }

    public async Task<PendingMaintenanceConfiguration?> GetPendingConfigurationAsync(
        string deviceIdentityFingerprint, MaintenanceConfigurationModule module,
        CancellationToken cancellationToken = default)
    {
        var journal = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return journal.FindPendingConfiguration(deviceIdentityFingerprint, module);
    }

    public Task QueuePendingConfigurationAsync(PendingMaintenanceConfiguration pending,
        CancellationToken cancellationToken = default) =>
        _store.SetPendingConfigurationAsync(pending, cancellationToken);

    public Task ClearPendingConfigurationAsync(string deviceIdentityFingerprint,
        MaintenanceConfigurationModule module, string expectedFingerprint,
        CancellationToken cancellationToken = default) =>
        _store.ClearPendingConfigurationAsync(deviceIdentityFingerprint, module, expectedFingerprint,
            cancellationToken);

    public async Task<MaintenanceRun?> GetActiveOperationAsync(string campusId, string deviceIdentityFingerprint,
        CancellationToken cancellationToken = default)
    {
        var journal = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return journal.Runs.FirstOrDefault(run => !run.IsTerminal && run.Device.CampusId == campusId &&
            run.Device.PinnedIdentityFingerprint == deviceIdentityFingerprint);
    }

    /// <summary>Reads current signed device facts without creating or advancing a maintenance operation.</summary>
    public Task<MaintenanceAdapterInspection> InspectCurrentAsync(MaintenanceDeviceIdentity device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        return _adapter.InspectAsync(device, cancellationToken);
    }

    private async Task PersistPreparedUpdateCommandAsync(MaintenanceUpdateCommandPrepared prepared,
        CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(prepared.OperationId, cancellationToken).ConfigureAwait(false);
        var evidence = new MaintenanceEvidence("signed-update-command", run.Device.PinnedIdentityFingerprint,
            prepared.SignedCommandSha256, prepared.CommandId, DateTimeOffset.UtcNow);
        if (run.AgentCommandId == prepared.CommandId && run.State is MaintenanceRunState.UpdateOffered or
                MaintenanceRunState.Updating)
            return;
        var offered = run.OfferUpdate(prepared.CommandId, evidence, "签名更新命令已生成，先记录 operationId/commandId。",
            DateTimeOffset.UtcNow);
        var updating = offered.TransitionTo(MaintenanceRunState.Updating,
            "更新命令即将通过已固定身份的旧 Agent 通道发送。", evidence, DateTimeOffset.UtcNow);
        await _store.UpdateAsync(updating, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyUpdateResultAsync(MaintenanceAdapterResult result,
        CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(result.OperationId, cancellationToken).ConfigureAwait(false);
        run = await RecordVerifiedStatusAsync(run, result, cancellationToken).ConfigureAwait(false);
        switch (result.Disposition)
        {
            case MaintenanceAdapterDisposition.NoChanges:
                if (run.State == MaintenanceRunState.PlanReady)
                {
                    run = run.TransitionTo(MaintenanceRunState.NoChanges, result.Detail, TrustedEvidence(run),
                        DateTimeOffset.UtcNow).TransitionTo(MaintenanceRunState.Completed, result.Detail,
                        TrustedEvidence(run), DateTimeOffset.UtcNow);
                    await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
                }
                break;
            case MaintenanceAdapterDisposition.UpdateCommittedAndHealthy:
                var healthyEvidence = RequireEvidence(run, result.Evidence, requireCommand: true);
                run = await MarkAgentHealthyAsync(run, healthyEvidence, result.Detail, cancellationToken)
                    .ConfigureAwait(false);
                if (run.Plan?.SynchronizeConfiguration != true)
                {
                    run = run.TransitionTo(MaintenanceRunState.Completed, result.Detail, healthyEvidence,
                        DateTimeOffset.UtcNow);
                    await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
                }
                break;
            case MaintenanceAdapterDisposition.UpdatePendingVerification:
            case MaintenanceAdapterDisposition.PendingVerification:
                await MarkPendingOrFailedAsync(result.OperationId, result.Detail, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case MaintenanceAdapterDisposition.UpdateRolledBack:
                run = RequireRunHasCommand(run, result.Evidence);
                run = run.TransitionTo(MaintenanceRunState.RolledBack, result.Detail, result.Evidence,
                    DateTimeOffset.UtcNow);
                await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
                break;
            case MaintenanceAdapterDisposition.RecoveryRequired:
                run = RequireRunHasCommand(run, result.Evidence);
                run = run.TransitionTo(MaintenanceRunState.RecoveryRequired, result.Detail, result.Evidence,
                    DateTimeOffset.UtcNow);
                await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
                break;
            case MaintenanceAdapterDisposition.ManualRequired:
            case MaintenanceAdapterDisposition.Failed:
                if (run.AgentCommandId is null && run.State == MaintenanceRunState.PlanReady)
                {
                    var failed = run.TransitionTo(result.Disposition == MaintenanceAdapterDisposition.ManualRequired
                            ? MaintenanceRunState.ManualRequired
                            : MaintenanceRunState.Failed,
                        result.Detail, result.Evidence, DateTimeOffset.UtcNow);
                    await _store.UpdateAsync(failed, cancellationToken).ConfigureAwait(false);
                }
                else await MarkPendingOrFailedAsync(result.OperationId, result.Detail, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException("更新适配器返回了配置同步终态。");
        }
    }

    private async Task ExecuteConfigurationSyncAsync(MaintenancePlan plan, int index, int total,
        IProgress<MaintenanceCoordinatorProgress>? progress, CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(plan.OperationId, cancellationToken).ConfigureAwait(false);
        MaintenanceAdapterResult result;
        try
        {
            result = await _adapter.SynchronizeConfigurationAsync(plan, async (prepared, token) =>
            {
                var current = await RequireRunAsync(prepared.OperationId, token).ConfigureAwait(false);
                if (current.OperationId != plan.OperationId || current.Device.Key != plan.Device.Key)
                    throw new InvalidDataException("配置同步命令与当前维护设备身份不一致。");
                var evidence = new MaintenanceEvidence("signed-configuration-command",
                    current.Device.PinnedIdentityFingerprint, prepared.SignedCommandSha256,
                    prepared.CommandId, DateTimeOffset.UtcNow);
                if (current.State != MaintenanceRunState.ConfigurationSyncing)
                {
                    current = current.TransitionTo(MaintenanceRunState.ConfigurationSyncing,
                        "已记录配置同步 commandId 与配置目标摘要，随后发送签名命令。", evidence,
                        DateTimeOffset.UtcNow);
                    await _store.UpdateAsync(current, token).ConfigureAwait(false);
                    progress?.Report(new MaintenanceCoordinatorProgress(plan.OperationId,
                        MaintenanceRunState.ConfigurationSyncing,
                        "签名配置 commandId 已写入 journal，正在发送并等待签名读回。",
                        index, total, prepared.CommandId, plan.Device.RouteAddress));
                }
                else if (current.Transitions.LastOrDefault(item =>
                             item.To == MaintenanceRunState.ConfigurationSyncing)?.Evidence?.CommandId !=
                         prepared.CommandId)
                {
                    throw new InvalidOperationException(
                        "此 operationId 已绑定配置同步 commandId；拒绝并发创建第二条配置命令。");
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await MarkPendingOrFailedAsync(plan.OperationId,
                "教师端停止等待配置同步；结果需重新读取学生端状态。", CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or
                                          InvalidOperationException or ArgumentException or TimeoutException)
        {
            await MarkPendingOrFailedAsync(plan.OperationId,
                "配置同步异常：" + Sanitize(exception.Message), CancellationToken.None).ConfigureAwait(false);
            return;
        }

        run = await RequireRunAsync(plan.OperationId, cancellationToken).ConfigureAwait(false);
        run = await RecordVerifiedStatusAsync(run, result, cancellationToken).ConfigureAwait(false);
        if (result.Disposition == MaintenanceAdapterDisposition.ConfigurationSynchronized)
        {
            var evidence = RequireEvidence(run, result.Evidence, requireCommand: false);
            if (run.State != MaintenanceRunState.ConfigurationSyncing)
            {
                run = run.TransitionTo(MaintenanceRunState.ConfigurationSyncing,
                    "配置同步回执已核验。", evidence, DateTimeOffset.UtcNow);
            }
            run = run.TransitionTo(MaintenanceRunState.Completed, result.Detail, evidence, DateTimeOffset.UtcNow);
            await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
            await PersistConfigurationSnapshotsAsync(run, result, cancellationToken).ConfigureAwait(false);
        }
        else if (result.Disposition == MaintenanceAdapterDisposition.PendingVerification)
        {
            await MarkPendingOrFailedAsync(plan.OperationId, result.Detail, cancellationToken).ConfigureAwait(false);
        }
        else if (result.Disposition == MaintenanceAdapterDisposition.RecoveryRequired)
        {
            run = run.TransitionTo(MaintenanceRunState.RecoveryRequired, result.Detail, result.Evidence,
                DateTimeOffset.UtcNow);
            await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            run = run.TransitionTo(result.Disposition == MaintenanceAdapterDisposition.ManualRequired
                    ? MaintenanceRunState.ManualRequired
                    : MaintenanceRunState.Failed,
                result.Detail, result.Evidence, DateTimeOffset.UtcNow);
            await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
        }
        progress?.Report(new MaintenanceCoordinatorProgress(plan.OperationId, run.State, result.Detail,
            index + 1, total, result.Evidence?.CommandId, plan.Device.RouteAddress));
    }

    private async Task<MaintenanceRun> ApplyReconciliationResultAsync(MaintenanceRun run,
        MaintenanceAdapterResult result, CancellationToken cancellationToken)
    {
        run = await RecordVerifiedStatusAsync(run, result, cancellationToken).ConfigureAwait(false);
        if (result.Disposition == MaintenanceAdapterDisposition.PendingVerification) return run;
        if (result.Disposition == MaintenanceAdapterDisposition.ConfigurationSynchronized)
        {
            var evidence = RequireEvidence(run, result.Evidence, requireCommand: false);
            if (run.State != MaintenanceRunState.ConfigurationSyncing)
                run = run.TransitionTo(MaintenanceRunState.ConfigurationSyncing,
                    "签名读回确认目标教师连接配置已生效。", evidence, DateTimeOffset.UtcNow);
            run = run.TransitionTo(MaintenanceRunState.Completed, result.Detail, evidence, DateTimeOffset.UtcNow);
            await PersistConfigurationSnapshotsAsync(run, result, cancellationToken).ConfigureAwait(false);
        }
        else if (result.Disposition == MaintenanceAdapterDisposition.UpdateRolledBack)
        {
            run = RequireRunHasCommand(run, result.Evidence);
            run = run.TransitionTo(MaintenanceRunState.RolledBack, result.Detail, result.Evidence,
                DateTimeOffset.UtcNow);
        }
        else if (result.Disposition == MaintenanceAdapterDisposition.RecoveryRequired)
        {
            if (run.State == MaintenanceRunState.PendingVerification)
                run = run.TransitionTo(MaintenanceRunState.RecoveryRequired, result.Detail, result.Evidence,
                    DateTimeOffset.UtcNow);
            else if (run.State != MaintenanceRunState.RecoveryRequired)
                run = run.TransitionTo(MaintenanceRunState.PendingVerification, result.Detail, result.Evidence,
                    DateTimeOffset.UtcNow).TransitionTo(MaintenanceRunState.RecoveryRequired, result.Detail,
                    result.Evidence, DateTimeOffset.UtcNow);
        }
        else if (result.Disposition is MaintenanceAdapterDisposition.ManualRequired or
                 MaintenanceAdapterDisposition.Failed)
        {
            var next = result.Disposition == MaintenanceAdapterDisposition.ManualRequired
                ? MaintenanceRunState.ManualRequired
                : MaintenanceRunState.Failed;
            run = run.TransitionTo(next, result.Detail, result.Evidence, DateTimeOffset.UtcNow);
        }
        await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
        return run;
    }

    private async Task PersistConfigurationSnapshotsAsync(MaintenanceRun run, MaintenanceAdapterResult result,
        CancellationToken cancellationToken)
    {
        if (run.Plan?.ConfigurationSha256 is not { } targetFingerprint) return;
        var responseEvidence = result.Evidence;
        var commandEvidence = run.Transitions.LastOrDefault(item =>
            item.To == MaintenanceRunState.ConfigurationSyncing)?.Evidence;
        if (result.ConfigurationRevision is not { } revision || responseEvidence?.Sha256 is not { } responseSha ||
            commandEvidence?.Sha256 is not { } commandSha)
        {
            // Legacy Agent sync returns a signed Agent acknowledgement rather than the newer
            // Maintenance revision DTO. It can clear the delivery queue after that verified success,
            // but it must not fabricate a Desired/Reported revision.
            await _store.ClearPendingConfigurationAsync(run.Device.PinnedIdentityFingerprint,
                MaintenanceConfigurationModule.TeacherConnectivity, targetFingerprint, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        using var signingKey = WebsitePolicySigningKeyStore.Open(run.Device.CampusId);
        var signerFingerprint = StudentAgentResponseCryptography.GetFingerprint(signingKey.PublicKeyPem);
        var issuedUtc = run.Transitions.Last(item => item.To == MaintenanceRunState.ConfigurationSyncing)
            .OccurredUtc;
        var desired = new DesiredMaintenanceConfiguration(run.Device.PinnedIdentityFingerprint,
            MaintenanceConfigurationModule.TeacherConnectivity, 1, revision, targetFingerprint,
            signerFingerprint, commandSha, issuedUtc);
        var journal = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var previouslyDesired = journal.DesiredConfigurations.FirstOrDefault(item =>
            item.DeviceIdentityFingerprint == desired.DeviceIdentityFingerprint &&
            item.Module == desired.Module);
        if (previouslyDesired is not null && (previouslyDesired.Revision != revision ||
            previouslyDesired.Fingerprint != targetFingerprint))
            throw new InvalidDataException("设备签名读回 revision 与 Teacher Desired 配置不匹配。");
        var reported = new ReportedMaintenanceConfiguration(run.Device.PinnedIdentityFingerprint,
            MaintenanceConfigurationModule.TeacherConnectivity, 1, revision, targetFingerprint,
            MaintenanceConfigurationApplyState.Applied, responseSha,
            result.VerifiedStatusUtc ?? responseEvidence!.VerifiedUtc);
        await _store.SetDesiredConfigurationAsync(desired, cancellationToken).ConfigureAwait(false);
        await _store.SetReportedConfigurationAsync(reported, cancellationToken).ConfigureAwait(false);
        await _store.ClearPendingConfigurationAsync(run.Device.PinnedIdentityFingerprint,
            MaintenanceConfigurationModule.TeacherConnectivity, targetFingerprint, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MaintenanceRun> RecordVerifiedStatusAsync(MaintenanceRun run,
        MaintenanceAdapterResult result, CancellationToken cancellationToken)
    {
        if (result.VerifiedCurrentVersions is null && result.VerifiedUpdateTransactionState is null)
            return run;
        if (result.VerifiedCurrentVersions is null || result.VerifiedStatusUtc is null || result.Evidence is null)
            throw new InvalidDataException("维护适配器返回不完整的签名学生状态事实。" );
        var observed = run.RecordVerifiedStatus(result.VerifiedCurrentVersions,
            result.VerifiedUpdateTransactionState, result.VerifiedStatusUtc.Value, result.Evidence);
        await _store.UpdateAsync(observed, cancellationToken).ConfigureAwait(false);
        return observed;
    }

    private async Task<MaintenanceRun> MarkAgentHealthyAsync(MaintenanceRun run,
        MaintenanceEvidence evidence, string reason, CancellationToken cancellationToken)
    {
        evidence = RequireEvidence(run, evidence, requireCommand: true);
        if (run.State == MaintenanceRunState.PendingVerification)
            run = run.TransitionTo(MaintenanceRunState.AgentHealthVerified, reason, evidence, DateTimeOffset.UtcNow);
        else if (run.State == MaintenanceRunState.RecoveryRequired)
            run = run.TransitionTo(MaintenanceRunState.PendingVerification, reason, evidence, DateTimeOffset.UtcNow)
                .TransitionTo(MaintenanceRunState.AgentHealthVerified, reason, evidence, DateTimeOffset.UtcNow);
        else
        {
            if (run.State == MaintenanceRunState.UpdateOffered)
                run = run.TransitionTo(MaintenanceRunState.Updating, reason, evidence, DateTimeOffset.UtcNow);
            if (run.State == MaintenanceRunState.Updating)
                run = run.TransitionTo(MaintenanceRunState.WaitingForCommit, reason, evidence, DateTimeOffset.UtcNow);
            if (run.State == MaintenanceRunState.WaitingForCommit)
                run = run.TransitionTo(MaintenanceRunState.AgentHealthVerified, reason, evidence, DateTimeOffset.UtcNow);
        }
        await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
        return run;
    }

    private async Task MarkPendingOrFailedAsync(Guid operationId, string reason,
        CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (run.IsTerminal || run.State is MaintenanceRunState.PendingVerification or
                MaintenanceRunState.RecoveryRequired) return;
        var next = run.AgentCommandId is not null || (run.State is MaintenanceRunState.ConfigurationSyncing or
                       MaintenanceRunState.AgentHealthVerified)
            ? MaintenanceRunState.PendingVerification
            : MaintenanceRunState.Failed;
        run = run.TransitionTo(next, Sanitize(reason), null, DateTimeOffset.UtcNow);
        await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MaintenanceRun> RequireRunAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var journal = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return journal.Find(operationId) ?? throw new KeyNotFoundException(
            $"Maintenance operationId {operationId:D} 不存在。" );
    }

    private async Task<MaintenanceInspectionSnapshot> InspectRunAsync(MaintenanceRun run,
        MaintenanceDeviceIdentity device, CancellationToken cancellationToken)
    {
        try
        {
            var inspection = await _adapter.InspectAsync(device, cancellationToken).ConfigureAwait(false);
            if (!inspection.IdentityVerified || inspection.Capabilities is null ||
                inspection.IdentityEvidence is null || inspection.CapabilitiesEvidence is null)
            {
                run = run.TransitionTo(MaintenanceRunState.ManualRequired,
                    "学生 Agent 身份或签名能力未通过核验；需要重新核对。", inspection.IdentityEvidence,
                    DateTimeOffset.UtcNow);
                await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
                return new MaintenanceInspectionSnapshot(run.OperationId, device, run.State,
                    inspection.CurrentVersions, null, inspection.Detail, inspection.UpdateTransactionState,
                    inspection.AuthenticatedChannelConnected, inspection.AuthenticatedChannelCheckedUtc,
                    inspection.TeacherChannelDiagnostics);
            }

            run = run.VerifyIdentity(inspection.IdentityEvidence, "学生 Agent 固定身份已核验。",
                inspection.IdentityEvidence.VerifiedUtc);
            await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
            run = run.VerifyCapabilities(inspection.Capabilities, inspection.CurrentVersions,
                inspection.CapabilitiesEvidence,
                "学生 Agent 签名能力已核验。", inspection.CapabilitiesEvidence.VerifiedUtc);
            await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
            if (inspection.UpdateTransactionState is { } transactionState && transactionState is not
                ("notFound" or "committed" or "rolledBack" or "superseded"))
            {
                run = run.TransitionTo(MaintenanceRunState.ManualRequired,
                    $"学生端现有更新事务状态为 {transactionState}；缺少可恢复的 operationId，未创建新维护计划。",
                    inspection.IdentityEvidence, DateTimeOffset.UtcNow);
                await _store.UpdateAsync(run, cancellationToken).ConfigureAwait(false);
                return new MaintenanceInspectionSnapshot(run.OperationId, device, run.State,
                    inspection.CurrentVersions, inspection.Capabilities,
                    "学生端已有未结束、损坏或未知状态的更新事务；请先核对原事务。",
                    inspection.UpdateTransactionState, inspection.AuthenticatedChannelConnected,
                    inspection.AuthenticatedChannelCheckedUtc, inspection.TeacherChannelDiagnostics);
            }
            return new MaintenanceInspectionSnapshot(run.OperationId, device, run.State,
                inspection.CurrentVersions, inspection.Capabilities, inspection.Detail,
                inspection.UpdateTransactionState, inspection.AuthenticatedChannelConnected,
                inspection.AuthenticatedChannelCheckedUtc, inspection.TeacherChannelDiagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or
                                          InvalidOperationException or ArgumentException or TimeoutException)
        {
            var currentJournal = await _store.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            run = currentJournal.Find(run.OperationId) ?? run;
            if (!run.IsTerminal)
            {
                run = run.TransitionTo(MaintenanceRunState.Failed,
                    "只读设备检查失败：" + Sanitize(exception.Message), null, DateTimeOffset.UtcNow);
                await _store.UpdateAsync(run, CancellationToken.None).ConfigureAwait(false);
            }
            return ToInspectionSnapshot(run, "只读设备检查失败：" + Sanitize(exception.Message));
        }
    }

    private static MaintenanceEvidence RequireEvidence(MaintenanceRun run, MaintenanceEvidence? evidence,
        bool requireCommand)
    {
        if (evidence is null || !string.Equals(evidence.DeviceIdentityFingerprint,
                run.Device.PinnedIdentityFingerprint, StringComparison.Ordinal) ||
            requireCommand && run.AgentCommandId is { } command && evidence.CommandId != command)
            throw new InvalidDataException("维护回执身份或 Agent commandId 与 operation 不匹配。");
        return evidence;
    }

    private static MaintenanceRun RequireRunHasCommand(MaintenanceRun run, MaintenanceEvidence? evidence)
    {
        if (run.AgentCommandId is null || evidence?.CommandId != run.AgentCommandId)
            throw new InvalidDataException("恢复或回滚结果缺少匹配的 Agent commandId。");
        _ = RequireEvidence(run, evidence, requireCommand: true);
        return run;
    }

    private static MaintenanceEvidence TrustedEvidence(MaintenanceRun run) =>
        run.Transitions.FirstOrDefault(item => item.To == MaintenanceRunState.IdentityVerified)?.Evidence
        ?? throw new InvalidDataException("缺少当前 operation 的固定身份核验凭证。");

    private static bool NeedsSoftwareUpdate(MaintenancePlan plan, out string? refusal)
    {
        refusal = null;
        var requiresUpdate = false;
        foreach (var (target, current, label) in new[]
                 {
                     (plan.TargetSetupVersion, plan.CurrentVersions.StudentSetupVersion, "StudentSetup"),
                     (plan.TargetAgentVersion, plan.CurrentVersions.AgentRuntimeVersion, "Agent")
                 })
        {
            if (target is null) continue;
            if (current is null)
            {
                refusal = $"当前 {label} 版本未知；禁止自动安装，请先重新读取设备状态。";
                return false;
            }
            var comparison = ApplicationReleaseClient.CompareVersions(target, current);
            if (comparison < 0)
            {
                refusal = $"{label} 目标低于当前版本；已阻止潜在降级。";
                return false;
            }
            requiresUpdate |= comparison > 0;
        }
        return requiresUpdate;
    }

    private static bool SameTarget(MaintenancePlan first, MaintenancePlan second) =>
        first.OperationKind == second.OperationKind && first.Device.Key == second.Device.Key &&
        string.Equals(first.TargetSetupVersion, second.TargetSetupVersion, StringComparison.Ordinal) &&
        string.Equals(first.TargetAgentVersion, second.TargetAgentVersion, StringComparison.Ordinal) &&
        string.Equals(first.TargetMaintenanceServiceVersion, second.TargetMaintenanceServiceVersion,
            StringComparison.Ordinal) &&
        string.Equals(first.TargetReleaseSha256, second.TargetReleaseSha256, StringComparison.Ordinal) &&
        string.Equals(first.ConfigurationSha256, second.ConfigurationSha256, StringComparison.Ordinal) &&
        first.Channel == second.Channel &&
        first.SynchronizeConfiguration == second.SynchronizeConfiguration;

    private static MaintenanceInspectionSnapshot ToInspectionSnapshot(MaintenanceRun run, string detail) =>
        new(run.OperationId, run.Device, run.State, run.CurrentVersions,
            run.Capabilities, detail);

    private static string Sanitize(string value)
    {
        var normalized = string.Concat(value.Where(character => !char.IsControl(character))).Trim();
        return normalized.Length > 512 ? normalized[..512] : normalized;
    }
}
