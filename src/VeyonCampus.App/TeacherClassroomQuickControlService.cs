using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal static class TeacherClassroomQuickControlBuildFeatures
{
    public static bool PilotEnabled
    {
        get
        {
#if VEYONCAMPUS_CLASSROOM_QUICK_CONTROL_PILOT
            return true;
#else
            return false;
#endif
        }
    }
}

/// <summary>
/// Shared desktop/mobile application service for the short-lived classroom control layer.
/// It never starts legacy classroom policy modes. Ordinary release clears only this service's owned policies;
/// the explicit website-reset action clears each current website policy after signed compare-and-set preflight.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TeacherClassroomQuickControlService : IAsyncDisposable
{
    private const int MaximumOperationConcurrency = 16;
    private static readonly TimeSpan WebsiteCatchUpInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InputLeaseRenewalInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultWebsiteLifetime = TimeSpan.FromHours(1);

    private sealed record TargetBinding(Guid TargetId, string Target, string DisplayName);
    private sealed record CatchUpContext(TeacherClassroomEventContext Context, bool IsEndedRestore);
    private sealed record QuickReadback(StudentAgentStatusDeliveryResult Website,
        StudentInputGuardStatusDeliveryResult Guard);

    private readonly object _contextGate = new();
    private readonly Func<string> _activeCampusId;
    private readonly ClassroomQuickControlStateStore _stateStore;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _targetGates = new();
    private readonly ConcurrentDictionary<(Guid SessionId, Guid TargetId), string> _websiteObservations = new();
    private readonly HashSet<(Guid TargetId, Guid LeaseId)> _renewableLeases = [];
    private readonly Dictionary<Guid, TeacherClassroomEventContext> _endedRestoreContexts = [];
    private readonly CancellationTokenSource _shutdown = new();
    private TeacherClassroomEventContext? _context;
    private Task? _renewalTask;
    private Task? _websiteCatchUpTask;
    private bool _disposed;

    public TeacherClassroomQuickControlService(Func<string> activeCampusId, string? storageDirectory = null)
    {
        _activeCampusId = activeCampusId ?? throw new ArgumentNullException(nameof(activeCampusId));
        _stateStore = new ClassroomQuickControlStateStore(storageDirectory is null
            ? null : Path.Combine(storageDirectory, "classroom-quick-control.json"));
        RecoverPreviousProcessState();
    }

    public void SetClassroomContext(TeacherClassroomEventContext? context)
    {
        ThrowIfDisposed();
        lock (_contextGate)
        {
            var previous = _context;
            if (context is null)
            {
                if (previous is not null)
                {
                    SetSessionActive(previous.SessionId, active: false);
                    QueueEndedSessionRestores(previous);
                }
                _renewableLeases.Clear();
                _context = null;
            }
            else
            {
                ValidateContext(context);
                if (!string.Equals(_activeCampusId().Trim(), context.CampusId, StringComparison.Ordinal))
                    throw new InvalidDataException("快捷管控上下文与当前校区不匹配；未保存课堂状态。");
                var existing = _stateStore.Read(context.SessionId);
                if (existing is not null && (existing.CampusId != context.CampusId ||
                                             existing.RoomId != context.Session.Room.RoomId))
                    throw new InvalidDataException("快捷管控记录与当前课堂校区或地点不匹配；未发送命令。");
                var devices = existing?.Devices.ToDictionary(item => item.TargetId) ?? [];
                foreach (var targetId in context.TargetIds.Values)
                    devices.TryAdd(targetId, NotStartedState(targetId));
                var nextState = new ClassroomQuickControlSessionState(
                    ClassroomQuickControlStateStore.CurrentSchemaVersion, context.SessionId, context.CampusId,
                    context.Session.Room.RoomId, true, DateTimeOffset.UtcNow,
                    Array.AsReadOnly(devices.Values.OrderBy(item => item.TargetId).ToArray()));

                var sessionChanged = previous?.SessionId != context.SessionId;
                if (sessionChanged && previous is not null) SetSessionActive(previous.SessionId, active: false);
                try
                {
                    _stateStore.Save(nextState);
                }
                catch
                {
                    if (sessionChanged && previous is not null) SetSessionActive(previous.SessionId, active: true);
                    throw;
                }

                if (sessionChanged && previous is not null) QueueEndedSessionRestores(previous);
                if (sessionChanged) _renewableLeases.Clear();
                else
                {
                    var activeIds = context.TargetIds.Values.ToHashSet();
                    _renewableLeases.RemoveWhere(item => !activeIds.Contains(item.TargetId));
                }
                _context = context;
                if (_renewableLeases.Count > 0) EnsureRenewalLoop();
            }

        }
        EnsureWebsiteCatchUpLoop();
    }

    public ClassroomQuickControlSnapshot ReadSnapshot(Guid expectedSessionId)
    {
        var context = CaptureContext(expectedSessionId);
        var state = _stateStore.Read(context.SessionId);
        var byId = state?.Devices.ToDictionary(item => item.TargetId) ?? [];
        var displayNames = context.TargetIds.ToDictionary(pair => pair.Value,
            pair => context.TargetDisplayNames.GetValueOrDefault(pair.Key, "未填写姓名"));
        var targets = context.TargetIds.Values.Distinct().Select(targetId =>
        {
            var device = byId.GetValueOrDefault(targetId) ?? NotStartedState(targetId);
            return new ClassroomQuickControlTargetSnapshot(targetId,
                displayNames.GetValueOrDefault(targetId, "学生电脑"), device.Mode, device.Phase,
                device.UpdatedUtc, device.WebsiteRevision ?? device.PendingWebsiteRevision,
                device.InputLeaseId, device.ExpiresUtc, SanitizeTargetDetail(context, targetId, device.Detail))
            {
                WebsiteState = !HasWebsiteOwnership(device) &&
                    (device.WebsitePhase is null or ClassroomQuickControlPhase.NoneVerified) &&
                    device.Phase != ClassroomQuickControlPhase.WaitingForAgent
                    ? _websiteObservations.GetValueOrDefault((context.SessionId, targetId), "unknown")
                    : WebsiteState(device),
                InputState = device.InputLeaseId is null ? "notEnabled" :
                    device.InputExpiresUtc <= DateTimeOffset.UtcNow ? "needsReview" :
                    device.Phase == ClassroomQuickControlPhase.InputLockedVerified ? "locked" : "needsReview"
            };
        }).ToArray();
        return new ClassroomQuickControlSnapshot(context.SessionId,
            state?.UpdatedUtc ?? DateTimeOffset.UtcNow, Array.AsReadOnly(targets),
            TeacherClassroomQuickControlBuildFeatures.PilotEnabled,
            context.Session.Room.RoomName, context.Session.Room.ComputerCount,
            context.Session.Room.CampusName);
    }

    public async Task<ClassroomQuickControlOperationResult> ExecuteAsync(Guid expectedSessionId,
        ClassroomQuickControlAction action, IReadOnlyList<Guid>? selectedTargetIds = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(action)) throw new InvalidDataException("快捷管控操作无效。");
        if (!TeacherClassroomQuickControlBuildFeatures.PilotEnabled && action is not
            (ClassroomQuickControlAction.Release or ClassroomQuickControlAction.ClearWebsiteRestrictions))
            throw new InvalidOperationException("当前教师端构建未开放启用类课堂快捷管控；没有发送命令。恢复操作仍可使用。");
        var context = CaptureContext(expectedSessionId);
        var targets = SelectTargets(context, selectedTargetIds);
        if (targets.Count == 0) throw new InvalidDataException("当前课堂没有可操作的学生电脑。");
        var requestedUtc = DateTimeOffset.UtcNow;
        using var limit = new SemaphoreSlim(MaximumOperationConcurrency, MaximumOperationConcurrency);
        var tasks = targets.Select(async binding =>
        {
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            var targetGate = _targetGates.GetOrAdd(binding.TargetId, static _ => new SemaphoreSlim(1, 1));
            try
            {
                await targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await ExecuteTargetAsync(context, binding, action, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally { targetGate.Release(); }
            }
            finally { limit.Release(); }
        }).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var succeeded = results.Count(item => item.Succeeded);
        var waiting = results.Count(item => item.Phase == ClassroomQuickControlPhase.WaitingForAgent);
        var pending = results.Count(item => !item.Succeeded && item.NeedsReview);
        var failed = results.Length - succeeded - pending - waiting;
        var mode = action switch
        {
            ClassroomQuickControlAction.EnableWebsiteRestriction => "网站限制",
            ClassroomQuickControlAction.LockInput => "键鼠锁定",
            ClassroomQuickControlAction.ClearWebsiteRestrictions => "恢复全班网站访问",
            _ => "解除课堂管控"
        };
        EnsureWebsiteCatchUpLoop();
        return new ClassroomQuickControlOperationResult(context.SessionId, action, requestedUtc,
            Array.AsReadOnly(results), $"{mode}流程提交：已核验 {succeeded}/{results.Length} 台，等待 Agent 上线 {waiting} 台，待核对 {pending} 台，失败 {failed} 台。逐台进度会随签名回执更新。");
    }

    public async Task<ClassroomQuickControlOperationResult> ReleaseAllAsync(Guid expectedSessionId,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(expectedSessionId, ClassroomQuickControlAction.Release,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    private async Task<ClassroomQuickControlTargetResult> ExecuteTargetAsync(
        TeacherClassroomEventContext context, TargetBinding binding, ClassroomQuickControlAction action,
        CancellationToken cancellationToken, bool allowEndedRestore = false)
    {
        var previous = ReadDevice(context.SessionId, binding.TargetId);
        var requestedMode = action switch
        {
            ClassroomQuickControlAction.EnableWebsiteRestriction => DeviceControlMode.WebsiteRestriction,
            ClassroomQuickControlAction.LockInput => DeviceControlMode.InputLocked,
            _ => DeviceControlMode.None
        };
        try
        {
            if ((previous.Phase == ClassroomQuickControlPhase.WaitingForAgent ||
                 previous.WebsitePhase == ClassroomQuickControlPhase.WaitingForAgent) &&
                action == ClassroomQuickControlAction.Release && previous.Mode == DeviceControlMode.None &&
                previous.WebsiteRevision is null && previous.PendingWebsiteRevision is null &&
                previous.InputLeaseId is null)
            {
                var clear = SaveDevice(context, ClearState(previous,
                    "网站策略尚未发送；已取消等待任务。"));
                return Result(context, binding, previous.Mode, DeviceControlMode.None, clear.Mode,
                    ClassroomQuickControlPhase.NoneVerified, true, false, clear.Detail, null, null, null);
            }
            if (action == ClassroomQuickControlAction.Release &&
                previous.WebsitePhase == ClassroomQuickControlPhase.WaitingForAgent)
                previous = SaveDevice(context, previous with
                {
                    WebsitePhase = ClassroomQuickControlPhase.NoneVerified,
                    Phase = ClassroomQuickControlPhase.ReleasePending,
                    Detail = "已取消尚未发送的网站更新；正在解除已有管控。"
                });
            ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
            if (StudentInputGuardTransport.IsLocalMachineTarget(binding.Target))
            {
                if (action == ClassroomQuickControlAction.ClearWebsiteRestrictions)
                    return StoreWebsiteOnlyResult(context, binding, previous, requestedMode,
                        ClassroomQuickControlPhase.Conflict, false, true,
                        "已拒绝把教师电脑或本机回环地址作为学生控制目标。",
                        previous.WebsiteRevision, previous.PendingWebsiteRevision);
                return StoreResult(context, binding, previous, requestedMode, ClassroomQuickControlPhase.Conflict,
                    false, true, "已拒绝把教师电脑或本机回环地址作为学生控制目标。", previous.WebsiteRevision,
                    previous.PendingWebsiteRevision, previous.InputLeaseId, previous.ExpiresUtc);
            }

            var readback = await ReadStatusForActionAsync(context.CampusId, binding.Target, action,
                    cancellationToken)
                .ConfigureAwait(false);
            var website = readback.Website.Succeeded ? readback.Website.Status?.Website : null;
            if (action == ClassroomQuickControlAction.ClearWebsiteRestrictions)
                return await ClearAllWebsiteRestrictionsAsync(context, binding, previous, readback.Website,
                    cancellationToken, allowEndedRestore).ConfigureAwait(false);
            if (action == ClassroomQuickControlAction.EnableWebsiteRestriction && website is null)
            {
                if (readback.Website.TransientUnavailable)
                    return QueueWebsiteUntilAgentConnects(context, binding, previous,
                        readback.Website.Detail);
                if (previous.Phase == ClassroomQuickControlPhase.WaitingForAgent)
                    previous = !HasWebsiteOwnership(previous) && !HasInputOwnership(previous)
                        ? SaveDevice(context, ClearState(previous,
                            "Agent 已响应但身份或策略状态需要教师核对；自动推送已停止。"))
                        : SaveDevice(context, previous with { Phase = ClassroomQuickControlPhase.Unknown,
                            UpdatedUtc = DateTimeOffset.UtcNow,
                            Detail = "Agent 已响应但网站策略状态需要教师核对；保留已有管控归属。" });
                return StoreResult(context, binding, previous, requestedMode, ClassroomQuickControlPhase.Unknown,
                    false, readback.Website.NeedsReview,
                    "未取得可信的网站策略状态；没有执行网站策略操作。" + readback.Website.Detail,
                    previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
                    previous.ExpiresUtc);
            }

            var guardResult = readback.Guard;
            var state = await ReconcileOwnershipAsync(context, binding, previous, website, guardResult, action,
                cancellationToken).ConfigureAwait(false);
            if (state.Error is not null)
                return StoreResult(context, binding, state.Device, requestedMode, state.Phase,
                    false, true, state.Error, state.Device.WebsiteRevision,
                    state.Device.PendingWebsiteRevision, state.Device.InputLeaseId, state.Device.ExpiresUtc);
            previous = state.Device;

            return action switch
            {
                ClassroomQuickControlAction.EnableWebsiteRestriction => await EnableWebsiteAsync(context,
                    binding, previous, website!, guardResult, cancellationToken).ConfigureAwait(false),
                ClassroomQuickControlAction.LockInput => await LockInputAsync(context, binding, previous,
                    website, guardResult, cancellationToken).ConfigureAwait(false),
                ClassroomQuickControlAction.ClearWebsiteRestrictions => await ClearAllWebsiteRestrictionsAsync(
                    context, binding, previous, readback.Website, cancellationToken, allowEndedRestore)
                    .ConfigureAwait(false),
                ClassroomQuickControlAction.Release => await ReleaseAsync(context, binding, previous,
                    website, cancellationToken, allowEndedRestore).ConfigureAwait(false),
                _ => StoreResult(context, binding, previous, requestedMode, ClassroomQuickControlPhase.Conflict,
                    false, true, "快捷管控操作无效。", previous.WebsiteRevision,
                    previous.PendingWebsiteRevision, previous.InputLeaseId, previous.ExpiresUtc)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidOperationException exception) when (!IsTargetAllowed(context, binding.TargetId,
            allowEndedRestore))
        {
            var latest = TryReadDevice(context.SessionId, binding.TargetId) ?? previous;
            if (action == ClassroomQuickControlAction.ClearWebsiteRestrictions &&
                latest.WebsitePhase == ClassroomQuickControlPhase.ReleasePending &&
                latest.PendingWebsiteRevision is not null)
                return StoreWebsiteOnlyResult(context, binding, latest, requestedMode,
                    ClassroomQuickControlPhase.Unknown, false, true,
                    "课堂或目标电脑在操作期间变化；网站恢复结果需要核对，没有继续发送后续命令。",
                    latest.WebsiteRevision, latest.PendingWebsiteRevision);
            return Result(context, binding, previous.Mode, requestedMode, previous.Mode,
                ClassroomQuickControlPhase.Unknown, false, true,
                "课堂已结束或目标已变化；没有继续发送管控。" + Truncate(exception.Message, 300),
                previous.WebsiteRevision ?? previous.PendingWebsiteRevision, previous.InputLeaseId,
                previous.ExpiresUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or
                                          ArgumentException or System.Security.SecurityException or TaskCanceledException)
        {
            var latest = TryReadDevice(context.SessionId, binding.TargetId) ?? previous;
            if (action == ClassroomQuickControlAction.ClearWebsiteRestrictions)
                return StoreWebsiteOnlyResult(context, binding, latest, requestedMode,
                    ClassroomQuickControlPhase.Failed, false, true,
                    "网站限制没有完成恢复；保留原状态以便核对。" + Truncate(exception.Message, 420),
                    latest.WebsiteRevision, latest.PendingWebsiteRevision);
            return StoreResult(context, binding, latest, requestedMode, ClassroomQuickControlPhase.Failed,
                false, true, "该电脑的快捷管控未完成：" + Truncate(exception.Message, 420),
                latest.WebsiteRevision, latest.PendingWebsiteRevision, latest.InputLeaseId, latest.ExpiresUtc);
        }
    }

    private async Task<ClassroomQuickControlTargetResult> ClearAllWebsiteRestrictionsAsync(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, StudentAgentStatusDeliveryResult readback,
        CancellationToken cancellationToken, bool allowEndedRestore = false)
    {
        if (!readback.Succeeded || readback.Status is not { } status)
            return StoreWebsiteOnlyResult(context, binding, previous, DeviceControlMode.None,
                ClassroomQuickControlPhase.Unknown, false, true,
                "未取得可信的网站策略状态；没有修改该电脑。" + readback.Detail,
                previous.WebsiteRevision, previous.PendingWebsiteRevision);

        if (!SupportsPolicyCompareAndSet(status.AgentVersion))
            return StoreWebsiteOnlyResult(context, binding, previous, DeviceControlMode.None,
                ClassroomQuickControlPhase.Unsupported, false, true,
                "学生端版本不支持策略冲突保护；没有修改该电脑。请先更新学生 Agent。",
                previous.WebsiteRevision, previous.PendingWebsiteRevision);

        var website = status.Website;
        var now = DateTimeOffset.UtcNow;
        using var signingKey = WebsitePolicySigningKeyStore.Open(context.CampusId);
        var revision = WebsitePolicyRevisionStore.Next(context.CampusId, website.Revision);
        var disabled = WebsitePolicyCompiler.Create(context.CampusId, revision,
            WebsitePolicyMode.Disabled, Array.Empty<string>(), now,
            allowedDomains: Array.Empty<string>(), expectedRevision: website.Revision);
        var signed = WebsitePolicyCryptography.Sign(disabled, signingKey.PrivateKey);
        var pending = previous with
        {
            Mode = GetMode(previous.WebsiteRevision, revision, previous.InputLeaseId),
            Phase = HasInputOwnership(previous) ? previous.Phase : ClassroomQuickControlPhase.ReleasePending,
            WebsitePhase = ClassroomQuickControlPhase.ReleasePending,
            PendingWebsiteRevision = revision,
            UpdatedUtc = now,
            Detail = "正在清除当前电脑的黑名单和白名单；键鼠租约及其他限制保持不变。"
        };
        SaveDevice(context, pending);
        ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
        var pushed = await WebsitePolicyTransport.PushAsync([binding.Target], signed, context.CampusId,
            cancellationToken).ConfigureAwait(false);
        ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
        var verified = await ReadWebsiteStatusAsync(context.CampusId, binding.Target,
            signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
        ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
        if (verified.Succeeded && verified.Status?.Website is { Mode: WebsitePolicyMode.Disabled } current &&
            current.Revision >= revision)
        {
            var clear = SaveDevice(context, ClearWebsiteControl(pending,
                "黑名单和白名单已清除；Agent 签名读回确认网站策略已停用。"));
            return Result(context, binding, previous.Mode, DeviceControlMode.None, clear.Mode,
                ClassroomQuickControlPhase.NoneVerified, true, false, clear.Detail, null,
                clear.InputLeaseId, clear.InputExpiresUtc ?? clear.ExpiresUtc);
        }

        var phase = verified.Succeeded ? ClassroomQuickControlPhase.Conflict : ClassroomQuickControlPhase.Unknown;
        var detail = "网站限制尚未通过签名状态读回；保留待确认版本，键鼠租约和其他限制未修改。";
        var uncertain = pending with
        {
            Phase = HasInputOwnership(pending) ? pending.Phase : phase,
            WebsitePhase = phase,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Detail = detail
        };
        SaveDevice(context, uncertain);
        return Result(context, binding, previous.Mode, DeviceControlMode.None, uncertain.Mode,
            phase, false, true, detail + " " + pushed.SingleOrDefault()?.Detail + " " + verified.Detail,
            revision, uncertain.InputLeaseId, uncertain.InputExpiresUtc ?? uncertain.ExpiresUtc);
    }

    private ClassroomQuickControlTargetResult StoreWebsiteOnlyResult(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, DeviceControlMode requestedMode,
        ClassroomQuickControlPhase websitePhase, bool succeeded, bool needsReview, string detail,
        long? websiteRevision, long? pendingWebsiteRevision)
    {
        detail = SanitizeTargetDetail(context, binding.TargetId, detail);
        var stored = previous with
        {
            Mode = GetMode(websiteRevision, pendingWebsiteRevision, previous.InputLeaseId),
            Phase = HasInputOwnership(previous) ? previous.Phase : websitePhase,
            WebsitePhase = websitePhase,
            WebsiteRevision = websiteRevision,
            PendingWebsiteRevision = pendingWebsiteRevision,
            ExpiresUtc = HasInputOwnership(previous)
                ? previous.InputExpiresUtc ?? previous.ExpiresUtc
                : websiteRevision is not null || pendingWebsiteRevision is not null
                    ? previous.WebsiteExpiresUtc ?? previous.ExpiresUtc : null,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Detail = Truncate(detail, 512)
        };
        SaveDevice(context, stored);
        return Result(context, binding, previous.Mode, requestedMode, stored.Mode, websitePhase,
            succeeded, needsReview, stored.Detail,
            stored.WebsiteRevision ?? stored.PendingWebsiteRevision, stored.InputLeaseId, stored.ExpiresUtc);
    }

    private static bool SupportsPolicyCompareAndSet(string? agentVersion) =>
        !string.IsNullOrWhiteSpace(agentVersion) &&
        ApplicationReleaseClient.CompareVersions(agentVersion, "0.4.50") >= 0;

    private async Task<(ClassroomQuickControlDeviceState Device, ClassroomQuickControlPhase Phase, string? Error)>
        ReconcileOwnershipAsync(TeacherClassroomEventContext context, TargetBinding binding,
            ClassroomQuickControlDeviceState previous, WebsitePolicyReportedState? website,
            StudentInputGuardStatusDeliveryResult guardResult, ClassroomQuickControlAction action,
            CancellationToken cancellationToken)
    {
        var current = previous;
        if (action != ClassroomQuickControlAction.LockInput && website is not null &&
            HasWebsiteOwnership(current))
        {
            if (website.Mode == WebsitePolicyMode.Disabled &&
                website.Revision >= Math.Max(current.WebsiteRevision ?? 0, current.PendingWebsiteRevision ?? 0))
            {
                current = ClearWebsiteControl(current, "网站策略已读回为解除状态。");
                current = SaveDevice(context, current);
            }
            else if (current.Phase == ClassroomQuickControlPhase.ReleasePending &&
                     website.Mode != WebsitePolicyMode.Disabled &&
                     current.WebsiteRevision == website.Revision)
            {
                current = current with { Phase = ClassroomQuickControlPhase.WebsiteReported,
                    PendingWebsiteRevision = null, UpdatedUtc = DateTimeOffset.UtcNow,
                    Detail = "Agent 仍报告本工具创建的网站策略；正在重试已请求的恢复。" };
                current = SaveDevice(context, current);
            }
            else if (website.Mode != WebsitePolicyMode.Disabled &&
                     current.PendingWebsiteRevision == website.Revision &&
                     current.Phase != ClassroomQuickControlPhase.ReleasePending)
            {
                current = current with
                {
                    Mode = GetMode(current.WebsiteRevision ?? website.Revision, null,
                        current.InputLeaseId),
                    Phase = ClassroomQuickControlPhase.WebsiteReported,
                    WebsiteRevision = website.Revision,
                    PendingWebsiteRevision = null,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Detail = "恢复了本机记录的待确认策略版本；已签名状态与其一致。"
                };
                current = SaveDevice(context, current);
            }
            else if (website.Mode != WebsitePolicyMode.Disabled &&
                     current.WebsiteRevision != website.Revision)
                return (current, ClassroomQuickControlPhase.Conflict,
                    "学生机当前网站策略版本与本机快捷管控归属不一致；未覆盖或解除该策略。");
            else if (website.Mode != WebsitePolicyMode.Disabled && current.PendingWebsiteRevision is { } pending &&
                     pending != website.Revision)
                return (current, ClassroomQuickControlPhase.Conflict,
                    "学生机网站策略版本与待确认操作不一致；未继续切换。");
        }
        else if (action != ClassroomQuickControlAction.LockInput && website is not null &&
                 current.Mode == DeviceControlMode.None && current.PendingWebsiteRevision is { } pending)
        {
            if (website.Mode != WebsitePolicyMode.Disabled && website.Revision == pending)
            {
                current = current with { Mode = GetMode(pending, null, current.InputLeaseId),
                    Phase = ClassroomQuickControlPhase.WebsiteReported,
                    WebsiteRevision = pending, PendingWebsiteRevision = null,
                    WebsiteExpiresUtc = current.WebsiteExpiresUtc ?? current.ExpiresUtc,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Detail = "Agent 当前策略版本与本机待确认的签名推送版本一致；已恢复所有权记录。" };
                current = SaveDevice(context, current);
            }
            else if (website.Mode == WebsitePolicyMode.Disabled && website.Revision >= pending)
            {
                current = SaveDevice(context, ClearWebsiteControl(current,
                    "Agent 已签名读回停用状态；待确认的网站策略没有保持活动。"));
            }
        }
        else if (action == ClassroomQuickControlAction.EnableWebsiteRestriction && website is not null &&
                 website.Mode != WebsitePolicyMode.Disabled)
            return (current, ClassroomQuickControlPhase.Conflict,
                "学生机已有非本快捷管控拥有的网站策略；为保留其他教师或长期策略，没有覆盖它。");

        // The website and InputGuard channels are separate. A website-only operation never waits
        // for the local keyboard/mouse guard to answer and never changes an input lease.
        if (action is ClassroomQuickControlAction.EnableWebsiteRestriction or
            ClassroomQuickControlAction.ClearWebsiteRestrictions or ClassroomQuickControlAction.Release)
            return (current, current.Phase, null);

        if (!guardResult.Succeeded || guardResult.Status is not { } guard)
        {
            if (HasInputOwnership(current) &&
                !(action == ClassroomQuickControlAction.Release && HasWebsiteOwnership(current)))
                return (current, ClassroomQuickControlPhase.Unknown,
                    "InputGuard 状态未通过签名读回；键鼠租约保留待核对。" + guardResult.Detail);
            return (current, ClassroomQuickControlPhase.Unknown,
                action == ClassroomQuickControlAction.LockInput
                    ? "InputGuard 状态未知；没有发送键鼠锁定命令。" + guardResult.Detail
                    : null);
        }

        if (HasInputOwnership(current))
        {
            if (guard.Supported && guard.Phase == StudentInputGuardPhase.Locked &&
                guard.LeaseId == current.InputLeaseId)
            {
                current = current with { Mode = GetMode(current.WebsiteRevision,
                        current.PendingWebsiteRevision, current.InputLeaseId),
                    InputExpiresUtc = guard.LeaseExpiresUtc,
                    ExpiresUtc = guard.LeaseExpiresUtc };
                current = SaveDevice(context, current);
            }
            else if (guard.Supported &&
                     (guard.Phase is StudentInputGuardPhase.Unlocked or StudentInputGuardPhase.Expired) &&
                     guard.LeaseId is null)
            {
                RemoveRenewableLease(current.TargetId, current.InputLeaseId);
                current = SaveDevice(context, ClearInputControl(current,
                    "InputGuard 已签名读回未锁定状态。"));
            }
            else if (guard.Phase == StudentInputGuardPhase.GuardExited && guard.LeaseId is null)
            {
                if (action == ClassroomQuickControlAction.Release && HasWebsiteOwnership(current))
                    return (current, ClassroomQuickControlPhase.Unknown, null);
                return (current, ClassroomQuickControlPhase.Unknown,
                    "Guard 已退出但无法独立核验 Windows 当前输入状态；键鼠租约保留待核对。");
            }
            else
            {
                if (action == ClassroomQuickControlAction.Release && HasWebsiteOwnership(current))
                    return (current, ClassroomQuickControlPhase.Unknown, null);
                return (current, ClassroomQuickControlPhase.Conflict,
                    "InputGuard 当前租约与本机快捷管控记录不一致；没有修改键鼠管控。");
            }
        }
        else if (action == ClassroomQuickControlAction.LockInput && guard.Supported &&
                 guard.Phase == StudentInputGuardPhase.Locked)
            return (current, ClassroomQuickControlPhase.Conflict,
                "学生机已有本机快捷管控未拥有的输入锁定租约；没有发送新的键鼠锁定。");
        else if (action == ClassroomQuickControlAction.LockInput && guard.Supported &&
                 guard.Phase is not (StudentInputGuardPhase.Unlocked or
                     StudentInputGuardPhase.Expired or StudentInputGuardPhase.GuardExited))
            return (current, ClassroomQuickControlPhase.Unknown,
                "InputGuard 当前状态不是可核验的空闲状态；没有发送键鼠锁定命令。");
        return (current, current.Phase, null);
    }

    private async Task<ClassroomQuickControlTargetResult> EnableWebsiteAsync(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, WebsitePolicyReportedState website,
        StudentInputGuardStatusDeliveryResult guardResult, CancellationToken cancellationToken)
    {
        if (HasWebsiteOwnership(previous) &&
            website.Mode != WebsitePolicyMode.Disabled && previous.WebsiteRevision != website.Revision)
            return StoreResult(context, binding, previous, DeviceControlMode.WebsiteRestriction,
                ClassroomQuickControlPhase.Conflict, false, true,
                "网站策略版本在切换过程中变化；没有覆盖其他策略。", previous.WebsiteRevision,
                previous.PendingWebsiteRevision, previous.InputLeaseId, previous.ExpiresUtc);
        if (website.Mode != WebsitePolicyMode.Disabled && !HasWebsiteOwnership(previous))
            return StoreResult(context, binding, previous, DeviceControlMode.WebsiteRestriction,
                ClassroomQuickControlPhase.Conflict, false, true,
                "学生机已有其他来源的网站策略；没有覆盖它。", previous.WebsiteRevision,
                previous.PendingWebsiteRevision, previous.InputLeaseId, previous.ExpiresUtc);

        var draft = WebsitePolicyListDraftStore.Read(context.CampusId);
        if (draft is null)
            return StoreResult(context, binding, previous, DeviceControlMode.WebsiteRestriction,
                ClassroomQuickControlPhase.Failed, false, true,
                "当前校区没有已保存的网站名单；请先在策略页保存名单。", previous.WebsiteRevision,
                previous.PendingWebsiteRevision, previous.InputLeaseId, previous.ExpiresUtc);
        var blockedDomains = WebsitePolicyCompiler.NormalizeDomains(draft.BlockedDomains.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var allowed = WebsitePolicyCompiler.NormalizeDomains(draft.AllowedDomains.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        WebsitePolicyMode mode;
        try
        {
            mode = WebsitePolicyModeStore.Read(context.CampusId) ??
                WebsitePolicyModeSelection.Infer(draft.BlockedDomains, draft.AllowedDomains);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            return StoreResult(context, binding, previous, DeviceControlMode.WebsiteRestriction,
                ClassroomQuickControlPhase.Failed, false, true,
                "当前限制方式记录无法读取；没有发送网站策略。请在策略页核对并重新保存限制方式。" + exception.Message,
                previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId, previous.ExpiresUtc);
        }
        var domains = mode == WebsitePolicyMode.Allowlist ? allowed : blockedDomains;
        var policyAllowedDomains = mode == WebsitePolicyMode.Combined ? allowed : null;
        if (mode == WebsitePolicyMode.Blocklist && blockedDomains.Count == 0 ||
            mode == WebsitePolicyMode.Allowlist && allowed.Count == 0 ||
            mode == WebsitePolicyMode.Combined && blockedDomains.Count == 0 && allowed.Count == 0)
            return StoreResult(context, binding, previous, DeviceControlMode.WebsiteRestriction,
                ClassroomQuickControlPhase.Failed, false, true,
                "当前校区名单不符合所选限制方式；请先在策略页核对限制方式和网站名单。", previous.WebsiteRevision,
                previous.PendingWebsiteRevision, previous.InputLeaseId, previous.ExpiresUtc);

        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(DefaultWebsiteLifetime);
        using var signingKey = WebsitePolicySigningKeyStore.Open(context.CampusId);
        var revision = WebsitePolicyRevisionStore.Next(context.CampusId, website.Revision);
        var document = WebsitePolicyCompiler.Create(context.CampusId, revision, mode,
            domains, now, expires, policyAllowedDomains, website.Revision);
        var signed = WebsitePolicyCryptography.Sign(document, signingKey.PrivateKey);
        var applying = previous with
        {
            WebsitePhase = ClassroomQuickControlPhase.WebsiteApplying,
            Mode = GetMode(revision, revision, previous.InputLeaseId),
            Phase = HasWebsiteOwnership(previous)
                ? ClassroomQuickControlPhase.Switching : ClassroomQuickControlPhase.WebsiteApplying,
            PendingWebsiteRevision = revision,
            WebsiteExpiresUtc = expires,
            ExpiresUtc = previous.InputLeaseId is null ? expires : previous.ExpiresUtc,
            UpdatedUtc = now,
            Detail = previous.InputLeaseId is null
                ? "正在发送有到期时间、绑定预期版本的课堂网站策略。"
                : "正在独立更新网站策略；已有键鼠租约保持不变。"
        };
        SaveDevice(context, applying);
        ThrowIfContextChanged(context, binding.TargetId);
        _ = await WebsitePolicyTransport.PushAsync([binding.Target], signed, context.CampusId,
            cancellationToken).ConfigureAwait(false);
        ThrowIfContextChanged(context, binding.TargetId);
        var readback = await ReadWebsiteStatusAsync(context.CampusId, binding.Target, signingKey.PrivateKey,
            cancellationToken).ConfigureAwait(false);
        ThrowIfContextChanged(context, binding.TargetId);
        if (!readback.Succeeded || readback.Status is not { } status ||
            status.Website.Mode != mode || status.Website.Revision != revision)
        {
            var unknown = applying with
            {
                WebsitePhase = ClassroomQuickControlPhase.Unknown,
                Phase = ClassroomQuickControlPhase.Unknown,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Detail = "策略已发送但没有取得匹配的新鲜签名读回；保留待确认版本，未报告为已应用。"
            };
            SaveDevice(context, unknown);
            return Result(context, binding, previous.Mode, DeviceControlMode.WebsiteRestriction,
                applying.Mode, ClassroomQuickControlPhase.Unknown, false, true,
                unknown.Detail + " " + readback.Detail, revision, null, expires);
        }

        var edgeApplied = status.EdgeWebsite is { } edge && edge.Revision == revision &&
            edge.Profiles.Count > 0 && edge.Profiles.All(profile => profile.Status == "applied");
        var degraded = status.EdgeWebsite is { } edgeState && edgeState.Revision == revision &&
            edgeState.Profiles.Any(profile => profile.Status == "degraded");
        var phase = edgeApplied ? ClassroomQuickControlPhase.WebsiteApplied :
            ClassroomQuickControlPhase.WebsiteReported;
        var detail = edgeApplied
            ? "Agent 已签名读回当前名单版本，Edge 扩展报告已应用。"
            : "Agent 已签名读回当前名单版本；浏览器执行结果仍需核对。";
        if (!guardResult.Succeeded || guardResult.Status is null)
            detail += " 键鼠 Guard 状态未读回；这不影响网站策略，未发送键鼠命令。";
        if (degraded) detail += " Edge 有降级项，请查看设备健康详情。";
        var applied = applying with
        {
            WebsitePhase = phase,
            Mode = GetMode(revision, null, previous.InputLeaseId),
            Phase = phase,
            WebsiteRevision = revision,
            PendingWebsiteRevision = null,
            WebsiteExpiresUtc = expires,
            UpdatedUtc = status.CollectedUtc,
            Detail = detail
        };
        SaveDevice(context, applied);
        return Result(context, binding, previous.Mode, DeviceControlMode.WebsiteRestriction,
            applied.Mode, phase, true, degraded, detail, revision, previous.InputLeaseId, expires);
    }

    private ClassroomQuickControlTargetResult QueueWebsiteUntilAgentConnects(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, string connectionDetail)
    {
        var waiting = previous with { Phase = ClassroomQuickControlPhase.WaitingForAgent,
            WebsitePhase = ClassroomQuickControlPhase.WaitingForAgent,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Detail = "等待 Agent 上线；上线后会重新核对设备身份和最新已保存的网站名单，再逐台推送。" };
        SaveDevice(context, waiting);
        return Result(context, binding, previous.Mode, DeviceControlMode.WebsiteRestriction,
            previous.Mode, ClassroomQuickControlPhase.WaitingForAgent,
            false, false, waiting.Detail + " " + Truncate(connectionDetail, 320),
            waiting.WebsiteRevision ?? waiting.PendingWebsiteRevision, waiting.InputLeaseId, waiting.ExpiresUtc);
    }

    private async Task<ClassroomQuickControlTargetResult> LockInputAsync(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, WebsitePolicyReportedState? website,
        StudentInputGuardStatusDeliveryResult guardResult, CancellationToken cancellationToken)
    {
        if (!guardResult.Succeeded || guardResult.Status is not { Supported: true } guard)
            return StoreResult(context, binding, previous, DeviceControlMode.InputLocked,
                guardResult.Status?.Supported == false ? ClassroomQuickControlPhase.Unsupported :
                    ClassroomQuickControlPhase.Unknown, false, true,
                "InputGuard 没有提供可用的签名状态；没有发送锁定命令。" + guardResult.Detail,
                previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
                previous.ExpiresUtc);
        if (HasInputOwnership(previous))
            return StoreResult(context, binding, previous, DeviceControlMode.InputLocked,
                ClassroomQuickControlPhase.Conflict, false, true,
                "本工具已有键鼠租约；没有创建第二个租约。请先解除或核对原租约。",
                previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
                previous.InputExpiresUtc ?? previous.ExpiresUtc);
        if (!guard.Supported || guard.Phase is not (StudentInputGuardPhase.Unlocked or
                StudentInputGuardPhase.Expired or StudentInputGuardPhase.GuardExited))
            return StoreResult(context, binding, previous, DeviceControlMode.InputLocked,
                guard.Phase == StudentInputGuardPhase.Locked ? ClassroomQuickControlPhase.Conflict :
                    ClassroomQuickControlPhase.Unknown, false, true,
                "InputGuard 没有处于可开始租约的已核验状态；没有发送锁定命令。",
                previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
                previous.ExpiresUtc);

        var leaseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(StudentInputGuardCommandCryptography.MaximumLeaseLifetime);
        var locking = previous with
        {
            Mode = GetMode(previous.WebsiteRevision, previous.PendingWebsiteRevision, leaseId),
            Phase = ClassroomQuickControlPhase.InputLocking,
            InputLeaseId = leaseId,
            InputExpiresUtc = expires,
            ExpiresUtc = expires,
            UpdatedUtc = now,
            Detail = HasWebsiteOwnership(previous)
                ? "正在申请短期 InputGuard 租约；现有网站策略保持不变。"
                : "正在申请短期 InputGuard 租约；只有签名状态读回后才标记为已锁定。"
        };
        SaveDevice(context, locking);
        ThrowIfContextChanged(context, binding.TargetId);
        using var signingKey = WebsitePolicySigningKeyStore.Open(context.CampusId);
        var command = await StudentInputGuardTransport.ExecuteAsync(binding.Target, context.CampusId,
            signingKey.PrivateKey, context.Session.Room.RoomId, StudentInputGuardAction.Activate,
            leaseId, cancellationToken).ConfigureAwait(false);
        if (!command.Succeeded || command.Receipt?.Outcome != StudentInputGuardOutcome.Succeeded)
        {
            var uncertain = locking with { Phase = command.NeedsReview ? ClassroomQuickControlPhase.Unknown :
                    ClassroomQuickControlPhase.Failed,
                UpdatedUtc = DateTimeOffset.UtcNow, Detail = "锁定命令没有取得成功回执；保留租约编号以便核对。" + command.Detail };
            SaveDevice(context, uncertain);
            return Result(context, binding, previous.Mode, DeviceControlMode.InputLocked, locking.Mode,
                uncertain.Phase, false, command.NeedsReview, uncertain.Detail, null, leaseId, expires);
        }
        var verified = await StudentInputGuardTransport.ReadStatusAsync(binding.Target, context.CampusId,
            signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
        if (!verified.Succeeded || verified.Status is not { Supported: true,
                Phase: StudentInputGuardPhase.Locked } status || status.LeaseId != leaseId)
        {
            var uncertain = locking with { Phase = ClassroomQuickControlPhase.Unknown,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Detail = "Agent 已确认命令，但锁定租约没有通过后续签名读回；租约会在 20 秒内自行到期。" };
            SaveDevice(context, uncertain);
            return Result(context, binding, previous.Mode, DeviceControlMode.InputLocked, locking.Mode,
                ClassroomQuickControlPhase.Unknown, false, true, uncertain.Detail, null, leaseId, expires);
        }
        var locked = locking with
        {
            Mode = GetMode(previous.WebsiteRevision, previous.PendingWebsiteRevision, leaseId),
            Phase = ClassroomQuickControlPhase.InputLockedVerified,
            ExpiresUtc = status.LeaseExpiresUtc,
            InputExpiresUtc = status.LeaseExpiresUtc,
            UpdatedUtc = status.CollectedUtc,
            Detail = HasWebsiteOwnership(previous)
                ? "Agent 已签名读回当前键鼠租约；教师端每 5 秒续租。网站策略独立保持，关闭教师端或断线后键鼠租约最多 20 秒自动释放。"
                : "Agent 已签名读回当前锁定租约；教师端每 5 秒请求续租，教师端停止或断线后最多 20 秒自动释放。"
        };
        SaveDevice(context, locked);
        AddRenewableLease(binding.TargetId, leaseId);
        return Result(context, binding, previous.Mode, DeviceControlMode.InputLocked,
            locked.Mode, ClassroomQuickControlPhase.InputLockedVerified, true, false,
            locked.Detail, null, leaseId, status.LeaseExpiresUtc);
    }

    private async Task<ClassroomQuickControlTargetResult> ReleaseAsync(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, WebsitePolicyReportedState? website,
        CancellationToken cancellationToken, bool allowEndedRestore = false)
    {
        ClassroomQuickControlTargetResult? websiteResult = null;
        if (HasWebsiteOwnership(previous))
        {
            websiteResult = website is null
                ? StoreResult(context, binding, previous, DeviceControlMode.None,
                    ClassroomQuickControlPhase.ReleasePending, false, true,
                    "网站策略状态暂不可读；保留网站归属记录，未修改该策略。",
                    previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
                    previous.InputExpiresUtc ?? previous.ExpiresUtc)
                : await ReleaseOwnedWebsiteAsync(context, binding, previous, website, cancellationToken,
                    allowEndedRestore).ConfigureAwait(false);
            previous = ReadDevice(context.SessionId, binding.TargetId);
        }

        ClassroomQuickControlTargetResult? inputResult = null;
        if (HasInputOwnership(previous))
        {
            var inputReadback = await ReadInputGuardStatusAsync(context.CampusId, binding.Target,
                cancellationToken).ConfigureAwait(false);
            inputResult = await ReleaseOwnedInputAsync(context, binding, previous, inputReadback,
                cancellationToken, allowEndedRestore).ConfigureAwait(false);
            previous = ReadDevice(context.SessionId, binding.TargetId);
        }

        if (websiteResult is not null && inputResult is not null)
        {
            var succeeded = websiteResult.Succeeded && inputResult.Succeeded;
            var detail = succeeded
                ? "网站策略与本工具持有的键鼠租约已分别核验解除。"
                : string.Join(" ", new[] { websiteResult.Detail, inputResult.Detail }
                    .Where(item => !string.IsNullOrWhiteSpace(item)));
            var phase = succeeded ? ClassroomQuickControlPhase.NoneVerified :
                !websiteResult.Succeeded ? websiteResult.Phase : inputResult.Phase;
            var stored = SaveDevice(context, previous with { Phase = phase,
                UpdatedUtc = DateTimeOffset.UtcNow, Detail = Truncate(detail, 512) });
            return Result(context, binding, DeviceControlMode.WebsiteAndInputLocked,
                DeviceControlMode.None, stored.Mode, phase, succeeded,
                websiteResult.NeedsReview || inputResult.NeedsReview || !succeeded,
                stored.Detail, stored.WebsiteRevision ?? stored.PendingWebsiteRevision,
                stored.InputLeaseId, stored.InputExpiresUtc ?? stored.ExpiresUtc);
        }
        if (websiteResult is not null) return websiteResult;
        if (inputResult is not null) return inputResult;

        var guardResult = await ReadInputGuardStatusAsync(context.CampusId, binding.Target,
            cancellationToken).ConfigureAwait(false);
        if (website is null)
            return StoreResult(context, binding, previous, DeviceControlMode.None,
                ClassroomQuickControlPhase.Unknown, false, true,
                "网站策略状态暂不可读；没有报告整台电脑的课堂管控已经解除。",
                previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
                previous.ExpiresUtc);
        if (website?.Mode is not null && website.Mode != WebsitePolicyMode.Disabled)
            return StoreResult(context, binding, previous, DeviceControlMode.None,
                ClassroomQuickControlPhase.Conflict, false, true,
                "学生机当前网站限制不属于本快捷管控；没有解除其他策略。", previous.WebsiteRevision,
                previous.PendingWebsiteRevision, previous.InputLeaseId, previous.ExpiresUtc);
        if (guardResult.Succeeded && guardResult.Status is { Supported: true,
                Phase: StudentInputGuardPhase.Locked })
            return StoreResult(context, binding, previous, DeviceControlMode.None,
                ClassroomQuickControlPhase.Conflict, false, true,
                "学生机当前输入锁定租约没有本机归属记录；没有发送解除命令。", previous.WebsiteRevision,
                previous.PendingWebsiteRevision, previous.InputLeaseId, previous.ExpiresUtc);
        if (!guardResult.Succeeded)
            return StoreResult(context, binding, previous, DeviceControlMode.None,
                ClassroomQuickControlPhase.Unknown, false, true,
                "InputGuard 状态未知；没有报告快捷管控已解除。" + guardResult.Detail,
                previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
                previous.ExpiresUtc);
        if (guardResult.Status is { Supported: true } guard &&
            guard.Phase is not (StudentInputGuardPhase.Unlocked or StudentInputGuardPhase.Expired))
            return StoreResult(context, binding, previous, DeviceControlMode.None,
                ClassroomQuickControlPhase.Unknown, false, true,
                "InputGuard 状态尚未确认空闲；没有将该电脑报告为已解除。", previous.WebsiteRevision,
                previous.PendingWebsiteRevision, previous.InputLeaseId, previous.ExpiresUtc);
        if (guardResult.Status is { Supported: false, Phase: StudentInputGuardPhase.Unsupported })
        {
            var clearUnsupported = SaveDevice(context, ClearState(previous,
                "Agent 签名读回确认未安装 InputGuard；当前没有该协议创建的键鼠租约。"));
            return Result(context, binding, previous.Mode, DeviceControlMode.None, clearUnsupported.Mode,
                ClassroomQuickControlPhase.NoneVerified, true, false, clearUnsupported.Detail,
                null, null, null);
        }
        var clear = SaveDevice(context, ClearState(previous, "签名读回确认没有活动的快捷管控。"));
        return Result(context, binding, previous.Mode, DeviceControlMode.None, clear.Mode,
            ClassroomQuickControlPhase.NoneVerified, true, false, clear.Detail, null, null, null);
    }

    private async Task<ClassroomQuickControlTargetResult> ReleaseOwnedWebsiteAsync(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, WebsitePolicyReportedState website,
        CancellationToken cancellationToken, bool allowEndedRestore = false)
    {
        var ownedRevision = previous.WebsiteRevision;
        if (website.Mode == WebsitePolicyMode.Disabled && ownedRevision is { } endedRevision &&
            website.Revision >= Math.Max(endedRevision, previous.PendingWebsiteRevision ?? 0))
        {
            var clear = SaveDevice(context, ClearWebsiteControl(previous,
                "网站策略已解除或到期，并由 Agent 签名读回确认。"));
            return Result(context, binding, previous.Mode, DeviceControlMode.None, clear.Mode,
                clear.Phase, true, HasInputOwnership(clear), clear.Detail, null,
                clear.InputLeaseId, clear.InputExpiresUtc ?? clear.ExpiresUtc);
        }
        if (website.Mode == WebsitePolicyMode.Disabled && ownedRevision is null &&
            previous.PendingWebsiteRevision is { } pendingRevision && website.Revision >= pendingRevision)
        {
            var clear = SaveDevice(context, ClearWebsiteControl(previous,
                "待确认的网站策略没有保持活动状态；已读回解除。"));
            return Result(context, binding, previous.Mode, DeviceControlMode.None, clear.Mode,
                clear.Phase, true, HasInputOwnership(clear), clear.Detail, null,
                clear.InputLeaseId, clear.InputExpiresUtc ?? clear.ExpiresUtc);
        }
        if (ownedRevision is null || website.Mode == WebsitePolicyMode.Disabled || website.Revision != ownedRevision)
            return StoreResult(context, binding, previous, DeviceControlMode.None,
                ClassroomQuickControlPhase.Conflict, false, true,
                "当前网站策略版本不再匹配本快捷管控的所有权；没有解除其他策略。",
                previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
                previous.ExpiresUtc);

        using var signingKey = WebsitePolicySigningKeyStore.Open(context.CampusId);
        var now = DateTimeOffset.UtcNow;
        var revision = WebsitePolicyRevisionStore.Next(context.CampusId, website.Revision);
        var disabled = WebsitePolicyCompiler.Create(context.CampusId, revision, WebsitePolicyMode.Disabled,
            Array.Empty<string>(), now, expectedRevision: website.Revision);
        var signed = WebsitePolicyCryptography.Sign(disabled, signingKey.PrivateKey);
        var pending = previous with
        {
            Phase = ClassroomQuickControlPhase.ReleasePending,
            PendingWebsiteRevision = revision,
            UpdatedUtc = now,
            Mode = GetMode(previous.WebsiteRevision, revision, previous.InputLeaseId),
            Detail = "正在独立解除本工具持有的网站策略；键鼠租约保持不变。"
        };
        SaveDevice(context, pending);
        ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
        _ = await WebsitePolicyTransport.PushAsync([binding.Target], signed, context.CampusId,
            cancellationToken).ConfigureAwait(false);
        var result = await ReadWebsiteStatusAsync(context.CampusId, binding.Target, signingKey.PrivateKey,
            cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && result.Status?.Website.Mode == WebsitePolicyMode.Disabled &&
            result.Status.Website.Revision >= revision)
        {
            var clear = SaveDevice(context, ClearWebsiteControl(previous,
                "解除命令后已读回网站限制为停用。"));
            return Result(context, binding, previous.Mode, DeviceControlMode.None, clear.Mode,
                clear.Phase, true, HasInputOwnership(clear), clear.Detail, null,
                clear.InputLeaseId, clear.InputExpiresUtc ?? clear.ExpiresUtc);
        }
        var phase = result.Succeeded ? ClassroomQuickControlPhase.Conflict : ClassroomQuickControlPhase.Unknown;
        var uncertain = pending with { Mode = GetMode(previous.WebsiteRevision, revision,
                previous.InputLeaseId),
            Phase = phase, UpdatedUtc = DateTimeOffset.UtcNow,
            Detail = "网站解除尚未通过签名状态读回；保留归属与待确认版本，键鼠租约未改动。" };
        SaveDevice(context, uncertain);
        return Result(context, binding, previous.Mode, DeviceControlMode.None, previous.Mode, phase,
            false, true, uncertain.Detail + " " + result.Detail, previous.WebsiteRevision,
            previous.InputLeaseId, previous.InputExpiresUtc ?? previous.ExpiresUtc);
    }

    private async Task<ClassroomQuickControlTargetResult> ReleaseOwnedInputAsync(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, StudentInputGuardStatusDeliveryResult guardResult,
        CancellationToken cancellationToken, bool allowEndedRestore = false)
    {
        if (!guardResult.Succeeded || guardResult.Status is not { Supported: true } status)
            return StoreResult(context, binding, previous, DeviceControlMode.None,
                ClassroomQuickControlPhase.Unknown, false, true,
                "InputGuard 状态未通过签名读回；没有报告已解除。" + guardResult.Detail,
                previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
                previous.ExpiresUtc);
        if ((status.Phase is StudentInputGuardPhase.Unlocked or StudentInputGuardPhase.Expired) &&
            status.LeaseId is null)
        {
            RemoveRenewableLease(binding.TargetId, previous.InputLeaseId);
            var clear = SaveDevice(context, ClearInputControl(previous,
                "InputGuard 已签名读回未锁定状态；租约无需再次解除。"));
            return Result(context, binding, previous.Mode, DeviceControlMode.None, clear.Mode,
                clear.Phase, true, HasWebsiteOwnership(clear), clear.Detail,
                clear.WebsiteRevision ?? clear.PendingWebsiteRevision, null, clear.ExpiresUtc);
        }
        if (status.Phase != StudentInputGuardPhase.Locked || status.LeaseId != previous.InputLeaseId ||
            status.LeaseId is not { } leaseId)
            return StoreResult(context, binding, previous, DeviceControlMode.None,
                ClassroomQuickControlPhase.Conflict, false, true,
                "InputGuard 当前租约与本机所有权记录不匹配；没有解除其他锁定。",
                previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
                previous.ExpiresUtc);

        var pending = previous with { Phase = ClassroomQuickControlPhase.ReleasePending,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Detail = "正在向原锁定租约发送解除命令并等待签名状态读回。" };
        SaveDevice(context, pending);
        RemoveRenewableLease(binding.TargetId, leaseId);
        ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
        using var signingKey = WebsitePolicySigningKeyStore.Open(context.CampusId);
        var command = await StudentInputGuardTransport.ExecuteAsync(binding.Target, context.CampusId,
            signingKey.PrivateKey, context.Session.Room.RoomId, StudentInputGuardAction.Release, leaseId,
            cancellationToken).ConfigureAwait(false);
        var finalStatus = await StudentInputGuardTransport.ReadStatusAsync(binding.Target, context.CampusId,
            signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
        if (command.Succeeded && finalStatus.Succeeded && finalStatus.Status is { Supported: true } final &&
            final.LeaseId is null && (final.Phase is StudentInputGuardPhase.Unlocked or
                StudentInputGuardPhase.Expired))
        {
            var clear = SaveDevice(context, ClearInputControl(pending,
                "InputGuard 解除回执与后续签名状态均已确认。"));
            return Result(context, binding, previous.Mode, DeviceControlMode.None, clear.Mode,
                clear.Phase, true, HasWebsiteOwnership(clear), clear.Detail,
                clear.WebsiteRevision ?? clear.PendingWebsiteRevision, null, clear.ExpiresUtc);
        }
        var uncertain = pending with { Phase = ClassroomQuickControlPhase.Unknown,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Detail = "解除尚未通过签名状态读回；20 秒租约到期后 Guard 会自行恢复输入。" };
        SaveDevice(context, uncertain);
        return Result(context, binding, previous.Mode, DeviceControlMode.None, previous.Mode,
            ClassroomQuickControlPhase.Unknown, false, true,
            uncertain.Detail + " " + command.Detail + " " + finalStatus.Detail,
            null, leaseId, status.LeaseExpiresUtc);
    }

    private async Task<QuickReadback> ReadStatusForActionAsync(string campusId, string target,
        ClassroomQuickControlAction action, CancellationToken cancellationToken)
    {
        using var signingKey = WebsitePolicySigningKeyStore.Open(campusId);
        if (action is ClassroomQuickControlAction.EnableWebsiteRestriction or
            ClassroomQuickControlAction.ClearWebsiteRestrictions or ClassroomQuickControlAction.Release)
        {
            var website = await ReadWebsiteStatusAsync(campusId, target, signingKey.PrivateKey,
                cancellationToken).ConfigureAwait(false);
            return new QuickReadback(website, new StudentInputGuardStatusDeliveryResult(target, false,
                "本次只读取网站策略；键鼠 Guard 不参与网站操作。"));
        }

        var guard = await StudentInputGuardTransport.ReadStatusAsync(target, campusId,
            signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
        return new QuickReadback(new StudentAgentStatusDeliveryResult(target, false,
            "本次只读取键鼠 Guard；网站策略不参与键鼠操作。"), guard);
    }

    private static async Task<StudentInputGuardStatusDeliveryResult> ReadInputGuardStatusAsync(string campusId,
        string target, CancellationToken cancellationToken)
    {
        using var signingKey = WebsitePolicySigningKeyStore.Open(campusId);
        return await StudentInputGuardTransport.ReadStatusAsync(target, campusId, signingKey.PrivateKey,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StudentAgentStatusDeliveryResult> ReadWebsiteStatusAsync(string campusId,
        string target, RSA privateKey, CancellationToken cancellationToken)
    {
        var result = await WebsitePolicyStatusTransport.ReadAsync([target], campusId, privateKey,
            cancellationToken).ConfigureAwait(false);
        return result.Single();
    }

    private ClassroomQuickControlTargetResult StoreResult(TeacherClassroomEventContext context,
        TargetBinding binding, ClassroomQuickControlDeviceState previous, DeviceControlMode requestedMode,
        ClassroomQuickControlPhase phase, bool succeeded, bool needsReview, string detail,
        long? websiteRevision, long? pendingWebsiteRevision, Guid? leaseId, DateTimeOffset? expiresUtc)
    {
        detail = SanitizeTargetDetail(context, binding.TargetId, detail);
        var waitingWithoutOwnership = previous.Phase == ClassroomQuickControlPhase.WaitingForAgent &&
                                     previous.WebsiteRevision is null &&
                                     previous.PendingWebsiteRevision is null && !HasInputOwnership(previous) &&
                                     phase != ClassroomQuickControlPhase.WaitingForAgent;
        var stored = previous with { Mode = waitingWithoutOwnership ? DeviceControlMode.None :
                GetMode(websiteRevision, pendingWebsiteRevision, leaseId),
            Phase = phase, WebsiteRevision = websiteRevision,
            WebsitePhase = requestedMode == DeviceControlMode.WebsiteRestriction ? phase : previous.WebsitePhase,
            PendingWebsiteRevision = pendingWebsiteRevision, InputLeaseId = leaseId,
            ExpiresUtc = expiresUtc,
            WebsiteExpiresUtc = websiteRevision is not null || pendingWebsiteRevision is not null
                ? previous.WebsiteExpiresUtc ?? (leaseId is null ? expiresUtc : null) : null,
            InputExpiresUtc = leaseId is not null
                ? previous.InputExpiresUtc ?? (websiteRevision is null && pendingWebsiteRevision is null
                    ? expiresUtc : null) : null,
            UpdatedUtc = DateTimeOffset.UtcNow, Detail = Truncate(detail, 512) };
        stored = SaveDevice(context, stored);
        return Result(context, binding, previous.Mode, requestedMode, stored.Mode, phase, succeeded, needsReview,
            stored.Detail, stored.WebsiteRevision ?? stored.PendingWebsiteRevision,
            stored.InputLeaseId, stored.ExpiresUtc);
    }

    private static string SanitizeTargetDetail(TeacherClassroomEventContext context, Guid targetId, string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return detail;
        var target = context.TargetIds.FirstOrDefault(pair => pair.Value == targetId).Key;
        var displayName = !string.IsNullOrWhiteSpace(target)
            ? context.TargetDisplayNames.GetValueOrDefault(target, "未填写姓名") : "未填写姓名";
        var aliases = new List<string>();
        if (!string.IsNullOrWhiteSpace(target)) aliases.Add(target);
        var deviceLabel = context.Session.Targets.FirstOrDefault(item => item.TargetId == targetId)?.DeviceLabel;
        if (!string.IsNullOrWhiteSpace(deviceLabel)) aliases.Add(deviceLabel);
        foreach (var alias in aliases.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(item => item.Length))
            detail = detail.Replace(alias, displayName, StringComparison.OrdinalIgnoreCase);
        return detail;
    }

    private ClassroomQuickControlTargetResult Result(TeacherClassroomEventContext context, TargetBinding binding, DeviceControlMode previousMode,
        DeviceControlMode requestedMode, DeviceControlMode currentMode, ClassroomQuickControlPhase phase,
        bool succeeded, bool needsReview, string detail, long? websiteRevision, Guid? leaseId,
        DateTimeOffset? expiresUtc) => new(binding.TargetId, binding.DisplayName, previousMode,
        requestedMode, currentMode, phase, succeeded, needsReview, websiteRevision, leaseId,
        Truncate(SanitizeTargetDetail(context, binding.TargetId, detail), 512));

    private ClassroomQuickControlDeviceState SaveDevice(TeacherClassroomEventContext context,
        ClassroomQuickControlDeviceState device)
    {
        if (device.WebsitePhase == ClassroomQuickControlPhase.NoneVerified)
            _websiteObservations[(context.SessionId, device.TargetId)] = "notEnabled";
        var session = _stateStore.Read(context.SessionId) ?? new ClassroomQuickControlSessionState(
            ClassroomQuickControlStateStore.CurrentSchemaVersion, context.SessionId, context.CampusId,
            context.Session.Room.RoomId, true, DateTimeOffset.UtcNow, []);
        var devices = session.Devices.Where(item => item.TargetId != device.TargetId).Append(device)
            .OrderBy(item => item.TargetId).ToArray();
        _stateStore.Save(session with { UpdatedUtc = DateTimeOffset.UtcNow,
            Devices = Array.AsReadOnly(devices) });
        return device;
    }

    private ClassroomQuickControlDeviceState ReadDevice(Guid sessionId, Guid targetId) =>
        TryReadDevice(sessionId, targetId) ?? NotStartedState(targetId);

    private ClassroomQuickControlDeviceState? TryReadDevice(Guid sessionId, Guid targetId) =>
        _stateStore.Read(sessionId)?.Devices.SingleOrDefault(item => item.TargetId == targetId);

    private void SetSessionActive(Guid sessionId, bool active)
    {
        var state = _stateStore.Read(sessionId);
        if (state is not null)
            _stateStore.Save(state with { Active = active, UpdatedUtc = DateTimeOffset.UtcNow });
    }

    private void QueueEndedSessionRestores(TeacherClassroomEventContext context)
    {
        var state = _stateStore.Read(context.SessionId);
        if (state is null) return;
        var devices = state.Devices.Select(device => (device.Phase == ClassroomQuickControlPhase.WaitingForAgent ||
                device.WebsitePhase == ClassroomQuickControlPhase.WaitingForAgent) &&
                device.Mode == DeviceControlMode.None && device.WebsiteRevision is null &&
                device.PendingWebsiteRevision is null && device.InputLeaseId is null
            ? ClearState(device, "课堂已结束；取消尚未发送的网站策略。")
            : device.Mode != DeviceControlMode.None || device.PendingWebsiteRevision is not null &&
                (device.Phase is ClassroomQuickControlPhase.WebsiteApplying or ClassroomQuickControlPhase.Switching or
                    ClassroomQuickControlPhase.WaitingForAgent)
                ? device with { Phase = ClassroomQuickControlPhase.ReleasePending,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Detail = "课堂已结束；等待 Agent 上线后核对归属并恢复本工具创建的管控。" }
                : device).ToArray();
        _stateStore.Save(state with { Active = false, UpdatedUtc = DateTimeOffset.UtcNow,
            Devices = Array.AsReadOnly(devices) });
        if (devices.Any(device => device.Phase == ClassroomQuickControlPhase.ReleasePending))
            _endedRestoreContexts[context.SessionId] = context;
        else
            _endedRestoreContexts.Remove(context.SessionId);
    }

    private void RecoverPreviousProcessState()
    {
        foreach (var session in _stateStore.ReadAll().Where(item => item.Active))
        {
            var recovered = session.Devices.Select(device => HasInputOwnership(device)
                ? device with { Phase = ClassroomQuickControlPhase.Unknown, UpdatedUtc = DateTimeOffset.UtcNow,
                    Detail = "TeacherConsole 已重启；旧租约不会自动续期或重新锁定。请读回状态后手动解除。" }
                : device).ToArray();
            _stateStore.Save(session with { Active = false, UpdatedUtc = DateTimeOffset.UtcNow,
                Devices = Array.AsReadOnly(recovered) });
        }
    }

    private static ClassroomQuickControlDeviceState NotStartedState(Guid targetId) =>
        new(targetId, DeviceControlMode.None, ClassroomQuickControlPhase.NotStarted, null, null, null, null,
            DateTimeOffset.UtcNow, "尚未执行网站限制或键鼠锁定操作；这不表示 Agent 离线或策略不可用。");

    private static ClassroomQuickControlDeviceState ClearState(ClassroomQuickControlDeviceState device,
        string detail) => device with { Mode = DeviceControlMode.None,
            Phase = ClassroomQuickControlPhase.NoneVerified, WebsiteRevision = null,
            PendingWebsiteRevision = null, InputLeaseId = null, ExpiresUtc = null,
            WebsiteExpiresUtc = null, InputExpiresUtc = null,
            WebsitePhase = ClassroomQuickControlPhase.NoneVerified,
            UpdatedUtc = DateTimeOffset.UtcNow, Detail = detail };

    private static bool HasWebsiteOwnership(ClassroomQuickControlDeviceState device) =>
        device.WebsiteRevision is not null || device.PendingWebsiteRevision is not null;

    private static bool HasInputOwnership(ClassroomQuickControlDeviceState device) =>
        device.InputLeaseId is not null;

    internal static string WebsiteState(ClassroomQuickControlDeviceState device)
    {
        var phase = device.WebsitePhase ?? device.Phase;
        if (phase == ClassroomQuickControlPhase.ReleasePending) return "releasing";
        if (phase == ClassroomQuickControlPhase.WaitingForAgent) return "waitingForAgent";
        if (device.WebsiteExpiresUtc <= DateTimeOffset.UtcNow && HasWebsiteOwnership(device)) return "expired";
        if (phase is ClassroomQuickControlPhase.WebsiteApplying or ClassroomQuickControlPhase.Switching) return "applying";
        if (HasWebsiteOwnership(device)) return phase switch
        {
            ClassroomQuickControlPhase.WebsiteApplied => "applied",
            ClassroomQuickControlPhase.WebsiteReported => "reported",
            _ => "needsReview"
        };
        return phase is ClassroomQuickControlPhase.Conflict or ClassroomQuickControlPhase.Failed or
            ClassroomQuickControlPhase.Unsupported or ClassroomQuickControlPhase.Unknown
            ? "needsReview" : "notEnabled";
    }

    private static bool NeedsWebsiteCatchUp(ClassroomQuickControlDeviceState device) =>
        device.Phase == ClassroomQuickControlPhase.ReleasePending ||
        TeacherClassroomQuickControlBuildFeatures.PilotEnabled &&
        (device.WebsitePhase == ClassroomQuickControlPhase.WaitingForAgent ||
         device.Phase == ClassroomQuickControlPhase.WaitingForAgent || HasWebsiteOwnership(device));

    private async Task RefreshWebsiteStateAsync(TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState device, CancellationToken cancellationToken)
    {
        if (StudentInputGuardTransport.IsLocalMachineTarget(binding.Target)) return;
        using var key = WebsitePolicySigningKeyStore.Open(context.CampusId);
        var result = await ReadWebsiteStatusAsync(context.CampusId, binding.Target, key.PrivateKey,
            cancellationToken).ConfigureAwait(false);
        ThrowIfContextChanged(context, binding.TargetId);
        if (!HasWebsiteOwnership(device))
        {
            var observed = result.Succeeded && result.Status?.Website is { } actual
                ? actual.Mode == WebsitePolicyMode.Disabled ? "notEnabled" :
                    result.Status.EdgeWebsite is { } actualEdge && actualEdge.Revision == actual.Revision &&
                    actualEdge.Profiles.Count > 0 && actualEdge.Profiles.All(profile => profile.Status == "applied")
                        ? "applied" : "reported"
                : "unknown";
            _websiteObservations[(context.SessionId, binding.TargetId)] = observed;
            return;
        }
        if (!result.Succeeded || result.Status?.Website is not { } website)
        {
            SaveDevice(context, device with { WebsitePhase = ClassroomQuickControlPhase.Unknown });
            return;
        }
        if (website.Mode == WebsitePolicyMode.Disabled &&
            website.Revision >= Math.Max(device.WebsiteRevision ?? 0, device.PendingWebsiteRevision ?? 0))
        {
            SaveDevice(context, ClearWebsiteControl(device, "网站限制已解除或已到期。"));
            return;
        }
        var revision = device.PendingWebsiteRevision ?? device.WebsiteRevision;
        var matches = website.Mode != WebsitePolicyMode.Disabled && website.Revision == revision;
        var applied = matches && result.Status.EdgeWebsite is { } edge && edge.Revision == revision &&
            edge.Profiles.Count > 0 && edge.Profiles.All(profile => profile.Status == "applied");
        var phase = !matches ? ClassroomQuickControlPhase.Unknown : applied
            ? ClassroomQuickControlPhase.WebsiteApplied : ClassroomQuickControlPhase.WebsiteReported;
        SaveDevice(context, device with
        {
            WebsitePhase = phase,
            WebsiteRevision = matches ? revision : device.WebsiteRevision,
            PendingWebsiteRevision = matches ? null : device.PendingWebsiteRevision,
            Mode = matches ? GetMode(revision, null, device.InputLeaseId) : device.Mode,
            Phase = device.InputLeaseId is null ? phase : device.Phase
        });
    }

    private static DeviceControlMode GetMode(long? websiteRevision, long? pendingWebsiteRevision,
        Guid? inputLeaseId)
    {
        var websiteOwned = websiteRevision is not null || pendingWebsiteRevision is not null;
        var inputOwned = inputLeaseId is not null;
        return (websiteOwned, inputOwned) switch
        {
            (true, true) => DeviceControlMode.WebsiteAndInputLocked,
            (true, false) => DeviceControlMode.WebsiteRestriction,
            (false, true) => DeviceControlMode.InputLocked,
            _ => DeviceControlMode.None
        };
    }

    private static ClassroomQuickControlDeviceState ClearWebsiteControl(
        ClassroomQuickControlDeviceState device, string detail)
    {
        var hasInput = HasInputOwnership(device);
        return device with
        {
            Mode = GetMode(null, null, device.InputLeaseId),
            WebsiteRevision = null,
            PendingWebsiteRevision = null,
            WebsiteExpiresUtc = null,
            WebsitePhase = ClassroomQuickControlPhase.NoneVerified,
            ExpiresUtc = hasInput ? device.InputExpiresUtc ?? device.ExpiresUtc : null,
            Phase = hasInput
                ? device.Phase == ClassroomQuickControlPhase.InputLockedVerified
                    ? ClassroomQuickControlPhase.InputLockedVerified : ClassroomQuickControlPhase.Unknown
                : ClassroomQuickControlPhase.NoneVerified,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Detail = detail
        };
    }

    private static ClassroomQuickControlDeviceState ClearInputControl(
        ClassroomQuickControlDeviceState device, string detail)
    {
        var hasWebsite = HasWebsiteOwnership(device);
        return device with
        {
            Mode = GetMode(device.WebsiteRevision, device.PendingWebsiteRevision, null),
            InputLeaseId = null,
            InputExpiresUtc = null,
            ExpiresUtc = hasWebsite ? device.WebsiteExpiresUtc ?? device.ExpiresUtc : null,
            Phase = hasWebsite
                ? device.WebsiteRevision is not null
                    ? ClassroomQuickControlPhase.WebsiteReported : ClassroomQuickControlPhase.Unknown
                : ClassroomQuickControlPhase.NoneVerified,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Detail = detail
        };
    }

    private TeacherClassroomEventContext CaptureContext(Guid expectedSessionId)
    {
        lock (_contextGate)
        {
            if (_context is not { } context || context.SessionId != expectedSessionId)
                throw new InvalidOperationException("当前课堂已变化；请刷新状态后重试快捷管控。");
            ValidateContext(context);
            if (!string.Equals(_activeCampusId().Trim(), context.CampusId, StringComparison.Ordinal))
                throw new InvalidOperationException("当前校区已变化；请刷新课堂状态后重试。");
            return context;
        }
    }

    private bool IsCurrentTarget(TeacherClassroomEventContext context, Guid targetId)
    {
        lock (_contextGate)
            return _context is { } current && current.SessionId == context.SessionId &&
                   current.CampusId == context.CampusId &&
                   current.Session.Room.RoomId == context.Session.Room.RoomId &&
                   current.TargetIds.Values.Contains(targetId);
    }

    private bool IsTargetAllowed(TeacherClassroomEventContext context, Guid targetId, bool allowEndedRestore)
    {
        lock (_contextGate)
        {
            var isCurrent = _context is { } current && current.SessionId == context.SessionId &&
                            current.CampusId == context.CampusId &&
                            current.Session.Room.RoomId == context.Session.Room.RoomId &&
                            current.TargetIds.Values.Contains(targetId);
            var isEndedRestore = allowEndedRestore && _endedRestoreContexts.TryGetValue(context.SessionId,
                out var ended) && ended.CampusId == context.CampusId &&
                ended.Session.Room.RoomId == context.Session.Room.RoomId && ended.TargetIds.Values.Contains(targetId);
            return isCurrent || isEndedRestore;
        }
    }

    private void ThrowIfContextChanged(TeacherClassroomEventContext context, Guid targetId,
        bool allowEndedRestore = false)
    {
        lock (_contextGate)
        {
            var isCurrent = _context is { } current && current.SessionId == context.SessionId &&
                            current.CampusId == context.CampusId &&
                            current.Session.Room.RoomId == context.Session.Room.RoomId &&
                            current.TargetIds.Values.Contains(targetId);
            var isEndedRestore = allowEndedRestore && _endedRestoreContexts.TryGetValue(context.SessionId,
                out var ended) && ended.CampusId == context.CampusId &&
                ended.Session.Room.RoomId == context.Session.Room.RoomId && ended.TargetIds.Values.Contains(targetId);
            if (!isCurrent && !isEndedRestore)
                throw new InvalidOperationException("课堂或目标电脑在操作期间变化；已停止该电脑的后续操作。");
        }
    }

    private void EnsureWebsiteCatchUpLoop()
    {
        lock (_contextGate)
        {
            if (_disposed || _shutdown.IsCancellationRequested) return;
            var hasActiveWork = _context is not null;
            var hasRestoreWork = _endedRestoreContexts.Keys.Any(sessionId =>
                _stateStore.Read(sessionId)?.Devices.Any(device =>
                    device.Phase == ClassroomQuickControlPhase.ReleasePending) == true);
            if (!hasActiveWork && !hasRestoreWork) return;
            if (_websiteCatchUpTask is { IsCompleted: false }) return;
            _websiteCatchUpTask = Task.Run(WebsiteCatchUpLoopAsync);
        }
    }

    private async Task WebsiteCatchUpLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                CatchUpContext[] contexts;
                lock (_contextGate)
                {
                    var available = _endedRestoreContexts.Values.Select(item => new CatchUpContext(item, true)).ToList();
                    if (_context is { } active) available.Add(new CatchUpContext(active, false));
                    contexts = available.ToArray();
                }
                if (contexts.Length == 0) return;

                var work = contexts.SelectMany(item =>
                {
                    var state = _stateStore.Read(item.Context.SessionId);
                    if (state is null || item.IsEndedRestore && state.Active ||
                        !item.IsEndedRestore && !state.Active)
                        return Enumerable.Empty<(CatchUpContext Context, Guid TargetId,
                            ClassroomQuickControlAction Action)>();
                    return state.Devices.Where(device =>
                            item.IsEndedRestore
                                ? device.Phase == ClassroomQuickControlPhase.ReleasePending
                                : true)
                        .Select(device => (Context: item, device.TargetId,
                            Action: device.Phase == ClassroomQuickControlPhase.ReleasePending
                                ? ClassroomQuickControlAction.Release
                                : ClassroomQuickControlAction.EnableWebsiteRestriction));
                }).ToArray();
                if (work.Length == 0)
                {
                    RemoveCompletedEndedRestoreContexts();
                    return;
                }

                using var limit = new SemaphoreSlim(MaximumOperationConcurrency, MaximumOperationConcurrency);
                var tasks = work.Select(async item =>
                {
                    await limit.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    var targetGate = _targetGates.GetOrAdd(item.TargetId, static _ => new SemaphoreSlim(1, 1));
                    try
                    {
                        await targetGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                        try
                        {
                            var context = item.Context.Context;
                            var current = TryReadDevice(context.SessionId, item.TargetId);
                            if (current is null ||
                                item.Action == ClassroomQuickControlAction.Release &&
                                current.Phase != ClassroomQuickControlPhase.ReleasePending) return;
                            if (!item.Context.IsEndedRestore && !IsCurrentTarget(context, item.TargetId)) return;
                            var binding = SelectTargets(context, [item.TargetId]).Single();
                            if (item.Action == ClassroomQuickControlAction.EnableWebsiteRestriction &&
                                current.WebsitePhase != ClassroomQuickControlPhase.WaitingForAgent &&
                                current.Phase != ClassroomQuickControlPhase.WaitingForAgent)
                            {
                                await RefreshWebsiteStateAsync(context, binding, current, _shutdown.Token)
                                    .ConfigureAwait(false);
                                return;
                            }
                            _ = await ExecuteTargetAsync(context, binding, item.Action, _shutdown.Token,
                                item.Context.IsEndedRestore).ConfigureAwait(false);
                        }
                        finally { targetGate.Release(); }
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                        InvalidDataException or InvalidOperationException or CryptographicException or
                        PlatformNotSupportedException or ArgumentException or System.Security.SecurityException or
                        TaskCanceledException)
                    {
                        // The persisted waiting state remains visible; the next bounded pass will retry it.
                    }
                    finally { limit.Release(); }
                }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
                RemoveCompletedEndedRestoreContexts();
                await Task.Delay(WebsiteCatchUpInterval, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception)
        {
            // Fail closed. A later classroom sync or explicit command can restart the bounded loop.
            try { await Task.Delay(WebsiteCatchUpInterval, _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }
        finally
        {
            lock (_contextGate) _websiteCatchUpTask = null;
            if (!_shutdown.IsCancellationRequested) EnsureWebsiteCatchUpLoop();
        }
    }

    private void RemoveCompletedEndedRestoreContexts()
    {
        lock (_contextGate)
            foreach (var sessionId in _endedRestoreContexts.Keys.ToArray())
            {
                var state = _stateStore.Read(sessionId);
                if (state is null || !state.Devices.Any(device =>
                        device.Phase == ClassroomQuickControlPhase.ReleasePending))
                    _endedRestoreContexts.Remove(sessionId);
            }
    }

    private static void ValidateContext(TeacherClassroomEventContext context)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(context.CampusId);
        if (context.SessionId == Guid.Empty || context.Session.SessionId != context.SessionId ||
            context.Session.Status != ClassroomSessionStatus.Active || context.Session.Room.RoomId == Guid.Empty ||
            context.Targets.Count == 0 || context.Targets.Count > ClassroomSession.MaximumTargets)
            throw new InvalidDataException("活动课堂快照不完整；没有执行快捷管控。");
        var targets = WebsitePolicyTransport.NormalizeTargets(context.Targets);
        if (context.TargetIds.Count != targets.Count || context.TargetIds.Any(pair =>
                pair.Value == Guid.Empty || !targets.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) ||
            context.TargetIds.Values.Distinct().Count() != context.TargetIds.Count)
            throw new InvalidDataException("活动课堂目标 ID 映射不完整或重复；没有执行快捷管控。");
    }

    private IReadOnlyList<TargetBinding> SelectTargets(TeacherClassroomEventContext context,
        IReadOnlyList<Guid>? selectedTargetIds)
    {
        var ids = selectedTargetIds is null ? context.TargetIds.Values.ToArray() : selectedTargetIds.ToArray();
        if (ids.Length is 0 or > ClassroomSession.MaximumTargets || ids.Any(id => id == Guid.Empty) ||
            ids.Distinct().Count() != ids.Length)
            throw new InvalidDataException("快捷管控目标清单无效。");
        var byId = context.TargetIds.ToDictionary(pair => pair.Value, pair => pair.Key);
        var displayNames = context.TargetIds.ToDictionary(pair => pair.Value,
            pair => context.TargetDisplayNames.GetValueOrDefault(pair.Key, "未填写姓名"));
        return Array.AsReadOnly(ids.Select(id =>
        {
            if (!byId.TryGetValue(id, out var target))
                throw new InvalidDataException("快捷管控目标不属于当前活动课堂；没有发送命令。");
            return new TargetBinding(id, target, displayNames.GetValueOrDefault(id, "未填写姓名"));
        }).ToArray());
    }

    private void AddRenewableLease(Guid targetId, Guid leaseId)
    {
        lock (_contextGate)
        {
            _renewableLeases.RemoveWhere(item => item.TargetId == targetId && item.LeaseId != leaseId);
            _renewableLeases.Add((targetId, leaseId));
        }
        EnsureRenewalLoop();
    }

    private void RemoveRenewableLease(Guid targetId, Guid? leaseId)
    {
        lock (_contextGate)
            _renewableLeases.RemoveWhere(item => item.TargetId == targetId &&
                (leaseId is null || item.LeaseId == leaseId));
    }

    private void EnsureRenewalLoop()
    {
        lock (_contextGate)
        {
            if (_disposed || _renewableLeases.Count == 0 || _renewalTask is { IsCompleted: false }) return;
            _renewalTask = Task.Run(RenewalLoopAsync);
        }
    }

    private async Task RenewalLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await Task.Delay(InputLeaseRenewalInterval, _shutdown.Token).ConfigureAwait(false);
                TeacherClassroomEventContext? context;
                (Guid TargetId, Guid LeaseId)[] leases;
                lock (_contextGate)
                {
                    context = _context;
                    leases = _renewableLeases.ToArray();
                }
                if (context is null || leases.Length == 0) return;
                using var limit = new SemaphoreSlim(MaximumOperationConcurrency, MaximumOperationConcurrency);
                var tasks = leases.Select(async lease =>
                {
                    await limit.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    try { await RenewOneAsync(context, lease.TargetId, lease.LeaseId, _shutdown.Token)
                            .ConfigureAwait(false); }
                    finally { limit.Release(); }
                }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception) { /* All target failures are persisted by RenewOneAsync; loop exits fail-closed. */ }
        finally
        {
            lock (_contextGate) _renewalTask = null;
            EnsureRenewalLoop();
        }
    }

    private async Task RenewOneAsync(TeacherClassroomEventContext context, Guid targetId, Guid leaseId,
        CancellationToken cancellationToken)
    {
        if (_shutdown.IsCancellationRequested) return;
        var binding = SelectTargets(context, [targetId]).Single();
        var targetGate = _targetGates.GetOrAdd(targetId, static _ => new SemaphoreSlim(1, 1));
        await targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfContextChanged(context, targetId);
            var previous = ReadDevice(context.SessionId, targetId);
            if (!HasInputOwnership(previous) || previous.InputLeaseId != leaseId ||
                previous.Phase != ClassroomQuickControlPhase.InputLockedVerified)
            {
                RemoveRenewableLease(targetId, leaseId);
                return;
            }
            using var signingKey = WebsitePolicySigningKeyStore.Open(context.CampusId);
            var result = await StudentInputGuardTransport.ExecuteAsync(binding.Target, context.CampusId,
                signingKey.PrivateKey, context.Session.Room.RoomId, StudentInputGuardAction.Renew, leaseId,
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && result.Receipt?.Outcome == StudentInputGuardOutcome.Succeeded)
            {
                var readback = await StudentInputGuardTransport.ReadStatusAsync(binding.Target,
                    context.CampusId, signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
                if (readback.Succeeded && readback.Status is { Supported: true,
                        Phase: StudentInputGuardPhase.Locked } status && status.LeaseId == leaseId)
                {
                    SaveDevice(context, previous with { Mode = GetMode(previous.WebsiteRevision,
                            previous.PendingWebsiteRevision, previous.InputLeaseId),
                        Phase = ClassroomQuickControlPhase.InputLockedVerified,
                        ExpiresUtc = status.LeaseExpiresUtc, InputExpiresUtc = status.LeaseExpiresUtc,
                        UpdatedUtc = status.CollectedUtc,
                        Detail = "续租已收到签名回执并通过状态读回。" });
                    return;
                }
                result = result with { Succeeded = false,
                    Detail = "续租回执后状态读回未匹配原租约；租约到期后会自动释放。" };
            }
            RemoveRenewableLease(targetId, leaseId);
            SaveDevice(context, previous with { Phase = ClassroomQuickControlPhase.Unknown,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Detail = "InputGuard 续租未确认；已停止该设备的自动续租，原短租约到期后会自动释放。" +
                         " " + result.Detail });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or
                                          ArgumentException or System.Security.SecurityException)
        {
            RemoveRenewableLease(targetId, leaseId);
            var previous = TryReadDevice(context.SessionId, targetId);
            if (previous is not null)
                SaveDevice(context, previous with { Phase = ClassroomQuickControlPhase.Unknown,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Detail = "InputGuard 续租未确认；已停止该设备的自动续租，原短租约到期后会自动释放。" +
                             " " + Truncate(exception.Message, 300) });
        }
        finally { targetGate.Release(); }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TeacherClassroomQuickControlService));
    }

    private static string Truncate(string value, int maxLength) =>
        new(value.Where(character => !char.IsControl(character)).Take(maxLength).ToArray());

    public async ValueTask DisposeAsync()
    {
        Task? renewalTask;
        Task? websiteCatchUpTask;
        lock (_contextGate)
        {
            if (_disposed) return;
            _disposed = true;
            _renewableLeases.Clear();
            renewalTask = _renewalTask;
            websiteCatchUpTask = _websiteCatchUpTask;
        }
        _shutdown.Cancel();
        if (renewalTask is not null)
        {
            try { await renewalTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (websiteCatchUpTask is not null)
        {
            try { await websiteCatchUpTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _shutdown.Dispose();
        foreach (var gate in _targetGates.Values) gate.Dispose();
    }

}
