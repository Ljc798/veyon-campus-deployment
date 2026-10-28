using System.Collections.Concurrent;
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
internal sealed class TeacherClassroomQuickControlService : IAsyncDisposable
{
    private const int MaximumOperationConcurrency = 16;
    private static readonly TimeSpan WebsiteCatchUpInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InputLeaseRenewalInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ControlExpiryCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultWebsiteLifetime = TimeSpan.FromHours(1);

    private sealed record TargetBinding(Guid TargetId, string Target, string DisplayName);
    private sealed record CatchUpContext(TeacherClassroomEventContext Context, bool IsEndedRestore);
    private sealed record QuickReadback(StudentAgentStatusDeliveryResult Website,
        StudentInputGuardStatusDeliveryResult Guard);

    private sealed class StaleClassroomQuickControlTaskException(string message)
        : InvalidOperationException(message);

    private readonly object _contextGate = new();
    private readonly Func<string> _activeCampusId;
    private readonly ClassroomQuickControlStateStore _stateStore;
    private readonly ClassroomSessionStore _classroomSessionStore;
    private readonly TeacherCampusDirectoryStore _campusDirectoryStore;
    private readonly ClassroomSigningContextStore _classroomSigningContextStore;
    private readonly ITeacherNetworkDirectory _networkDirectory;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _targetGates = new();
    private readonly SemaphoreSlim _websiteCatchUpWakeSignal = new(0, 1);
    private readonly ConcurrentDictionary<(Guid SessionId, Guid TargetId), string> _websiteObservations = new();
    private readonly HashSet<(Guid TargetId, Guid LeaseId)> _renewableLeases = [];
    private readonly Dictionary<Guid, TeacherClassroomEventContext> _endedRestoreContexts = [];
    private readonly CancellationTokenSource _shutdown = new();
    private TeacherClassroomEventContext? _context;
    private Task? _renewalTask;
    private Task? _websiteCatchUpTask;
    private Task? _controlExpiryTask;
    private bool _websiteCatchUpRequested;
    private int _endedRestoreContextRecoveryQueued;
    private bool _disposed;

    public TeacherClassroomQuickControlService(Func<string> activeCampusId, string? storageDirectory = null,
        ITeacherNetworkDirectory? networkDirectory = null)
    {
        _activeCampusId = activeCampusId ?? throw new ArgumentNullException(nameof(activeCampusId));
        _stateStore = new ClassroomQuickControlStateStore(storageDirectory is null
            ? null : Path.Combine(storageDirectory, "classroom-quick-control.json"));
        _classroomSessionStore = new ClassroomSessionStore(storageDirectory is null
            ? null : Path.Combine(storageDirectory, "classroom-sessions.json"));
        _campusDirectoryStore = new TeacherCampusDirectoryStore(storageDirectory is null
            ? null : Path.Combine(storageDirectory, "campus-directory.json"));
        _classroomSigningContextStore = new ClassroomSigningContextStore(storageDirectory is null
            ? null : Path.Combine(storageDirectory, "classroom-signing-context.json"));
        _networkDirectory = networkDirectory ??
            TeacherNetworkDirectoryFactory.CreateForCurrentPlatform(_campusDirectoryStore.Load);
        RecoverPreviousProcessState();
    }

    public void SetClassroomContext(TeacherClassroomEventContext? context)
    {
        ThrowIfDisposed();
        if (context is not null)
        {
            lock (_contextGate)
            {
                if (_context is { } previous && HasSameClassroomScope(previous, context))
                {
                    // The status timer refreshes this context periodically. Keep display metadata
                    // current without rewriting the durable control file on every refresh.
                    _context = context;
                    return;
                }
            }
        }

        lock (_contextGate)
        {
            var previous = _context;
            if (context is null)
            {
                if (previous is not null)
                {
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
                var savedClassroom = _classroomSessionStore.ReadRecent()
                    .SingleOrDefault(item => item.SessionId == context.SessionId);
                if (savedClassroom is not { Status: ClassroomSessionStatus.Active } ||
                    savedClassroom.Room.RoomId != context.Session.Room.RoomId ||
                    _classroomSessionStore.ReadActive() is { } activeClassroom &&
                    activeClassroom.SessionId != context.SessionId)
                    throw new InvalidOperationException("课堂记录已结束或已切换；没有恢复旧课堂快捷管控上下文。");
                var existing = _stateStore.Read(context.SessionId);
                if (existing?.ClosedUtc is not null)
                    throw new InvalidOperationException("课堂快捷管控状态已终结；没有重新激活旧课堂。");
                var scopeChanged = existing is not null && (existing.CampusId != context.CampusId ||
                    existing.RoomId != context.Session.Room.RoomId);
                var activeIds = context.TargetIds.Values.ToHashSet();
                var removedDevices = existing?.Devices.Where(item => scopeChanged ||
                    !activeIds.Contains(item.TargetId)).ToArray() ?? [];
                if (removedDevices.Any(item => HasWebsiteOwnership(item) || HasInputOwnership(item) ||
                                               item.Phase == ClassroomQuickControlPhase.ReleasePending))
                    throw new InvalidDataException("旧课堂范围仍有未解除的快捷管控；请先解除后再更新控制范围。");

                var previousContext = previous is not null && previous.SessionId == context.SessionId
                    ? previous : null;
                var deviceSetChanged = existing is not null && (existing.Devices.Count != activeIds.Count ||
                    existing.Devices.Any(item => !activeIds.Contains(item.TargetId)));
                var contextChanged = previousContext is not null &&
                    (previousContext.CampusId != context.CampusId ||
                     previousContext.Session.Room.RoomId != context.Session.Room.RoomId ||
                     !previousContext.Targets.SequenceEqual(context.Targets, StringComparer.OrdinalIgnoreCase) ||
                     previousContext.TargetIds.Count != context.TargetIds.Count ||
                     previousContext.TargetIds.Any(pair => !context.TargetIds.TryGetValue(pair.Key, out var id) ||
                         id != pair.Value));
                var devices = scopeChanged ? [] : existing?.Devices.Where(item => activeIds.Contains(item.TargetId))
                    .ToDictionary(item => item.TargetId) ?? [];
                foreach (var targetId in context.TargetIds.Values)
                    devices.TryAdd(targetId, NotStartedState(targetId));
                var now = DateTimeOffset.UtcNow;
                var nextState = new ClassroomQuickControlSessionState(
                    ClassroomQuickControlStateStore.CurrentSchemaVersion, context.SessionId, context.CampusId,
                    context.Session.Room.RoomId, true, now,
                    Array.AsReadOnly(devices.Values.OrderBy(item => item.TargetId).ToArray()))
                {
                    StateRevision = checked((existing?.StateRevision ?? 0) + 1),
                    ControlExpiresUtc = scopeChanged || deviceSetChanged || contextChanged
                        ? null : existing?.ControlExpiresUtc
                };

                var sessionChanged = previous?.SessionId != context.SessionId;
                if (sessionChanged && previous is not null) QueueEndedSessionRestores(previous);
                var saved = _stateStore.Save(nextState);
                if (!saved.Active || saved.ClosedUtc is not null)
                    throw new InvalidOperationException("课堂已结束；没有恢复旧课堂快捷管控上下文。");
                if (sessionChanged) _renewableLeases.Clear();
                else
                {
                    _renewableLeases.RemoveWhere(item => !activeIds.Contains(item.TargetId));
                }
                _context = context;
                if (context is not null) _endedRestoreContexts.Remove(context.SessionId);
                if (_renewableLeases.Count > 0) EnsureRenewalLoop();
            }
        }
        ScheduleEndedRestoreContextRecovery();
        EnsureWebsiteCatchUpLoop();
        EnsureControlExpiryLoop();
    }

    private static bool HasSameClassroomScope(TeacherClassroomEventContext left,
        TeacherClassroomEventContext right) =>
        left.SessionId == right.SessionId && left.CampusId == right.CampusId &&
        left.Session.Room.RoomId == right.Session.Room.RoomId &&
        left.Targets.SequenceEqual(right.Targets, StringComparer.OrdinalIgnoreCase) &&
        left.TargetIds.Count == right.TargetIds.Count &&
        left.TargetIds.All(pair => right.TargetIds.TryGetValue(pair.Key, out var targetId) &&
                                   targetId == pair.Value);

    public void NotifyStudentAgentConnected(string target)
    {
        var normalizedTarget = WebsitePolicyTransport.NormalizeTargets([target]).Single();
        CatchUpContext[] contexts;
        lock (_contextGate)
        {
            if (_disposed) return;
            var available = _endedRestoreContexts.Values.Select(item => new CatchUpContext(item, true)).ToList();
            if (_context is { } active) available.Add(new CatchUpContext(active, false));
            contexts = available.ToArray();
        }

        foreach (var item in contexts)
        {
            var targetId = item.Context.TargetIds.FirstOrDefault(pair =>
                string.Equals(pair.Key, normalizedTarget, StringComparison.OrdinalIgnoreCase));
            if (targetId.Value == Guid.Empty) continue;
            var device = TryReadDevice(item.Context.SessionId, targetId.Value);
            if (device is { TaskId: { } taskId, DesiredRevision: > 0 } &&
                ClassroomQuickControlDesiredState.NeedsCatchUp(device))
                ClearCatchUpRetry(item.Context.SessionId, targetId.Value, device.DesiredRevision, taskId);
        }
        EnsureWebsiteCatchUpLoop();
    }

    public void DetachForShutdown()
    {
        ThrowIfDisposed();
        lock (_contextGate)
        {
            if (_context is { } context)
                _ = _stateStore.PauseSession(context.SessionId, DateTimeOffset.UtcNow,
                    "teacher-stop", "TeacherConsole 关闭或重启；保留活动课堂期望状态，不创建课堂结束恢复任务。");
            _renewableLeases.Clear();
            _context = null;
        }
    }

    public ClassroomQuickControlSnapshot ReadSnapshot(Guid expectedSessionId)
    {
        var context = CaptureContext(expectedSessionId);
        var state = _stateStore.Read(context.SessionId);
        return BuildSnapshot(context, state);
    }

    internal ClassroomQuickControlSnapshot ReadSavedSnapshot(TeacherClassroomEventContext context)
    {
        ThrowIfDisposed();
        ValidateContext(context);
        var active = _classroomSessionStore.ReadActive();
        var state = _stateStore.Read(context.SessionId);
        if (active?.SessionId != context.SessionId || active.Room.RoomId != context.Session.Room.RoomId ||
            !string.Equals(_activeCampusId().Trim(), context.CampusId, StringComparison.Ordinal) ||
            state is not null && (state.ClosedUtc is not null || state.CampusId != context.CampusId ||
                state.RoomId != context.Session.Room.RoomId ||
                !state.Devices.Select(item => item.TargetId).ToHashSet().SetEquals(context.TargetIds.Values)))
            throw new InvalidOperationException("本机保存的管控记录与当前课堂范围不一致；原记录已保留，请核对课堂范围。");
        return BuildSnapshot(context, state);
    }

    private ClassroomQuickControlSnapshot BuildSnapshot(TeacherClassroomEventContext context,
        ClassroomQuickControlSessionState? state)
    {
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
                DesiredAction = device.DesiredAction,
                RetryAfterUtc = device.RetryAfterUtc,
                WebsiteState = !HasWebsiteOwnership(device) &&
                    (device.WebsitePhase is null or ClassroomQuickControlPhase.NoneVerified) &&
                    device.Phase != ClassroomQuickControlPhase.WaitingForAgent
                    ? _websiteObservations.GetValueOrDefault((context.SessionId, targetId), "unknown")
                    : WebsiteState(device),
                InputState = device.InputLeaseId is null ? "notEnabled" :
                    device.Phase == ClassroomQuickControlPhase.WaitingForAgent ? "waitingForAgent" :
                    device.InputExpiresUtc <= DateTimeOffset.UtcNow ? "needsReview" :
                    device.Phase == ClassroomQuickControlPhase.InputLockedVerified ? "locked" : "needsReview"
            };
        }).ToArray();
        return new ClassroomQuickControlSnapshot(context.SessionId,
            state?.UpdatedUtc ?? DateTimeOffset.UtcNow, Array.AsReadOnly(targets),
            TeacherClassroomQuickControlBuildFeatures.PilotEnabled,
            context.Session.Room.RoomName, context.Session.Room.ComputerCount,
            context.Session.Room.CampusName)
        {
            ControlExpiresUtc = state?.ControlExpiresUtc,
            StateRevision = state?.StateRevision ?? 0
        };
    }

    public async Task<ClassroomQuickControlOperationResult> ExecuteAsync(Guid expectedSessionId,
        ClassroomQuickControlAction action, IReadOnlyList<Guid>? selectedTargetIds = null,
        DateTimeOffset? controlExpiresUtc = null, CancellationToken cancellationToken = default,
        DateTimeOffset? expectedControlExpiresUtc = null, string source = "teacher-command")
    {
        if (!Enum.IsDefined(action)) throw new InvalidDataException("快捷管控操作无效。");
        if (!TeacherClassroomQuickControlBuildFeatures.PilotEnabled && action is not
            (ClassroomQuickControlAction.Release or ClassroomQuickControlAction.ClearWebsiteRestrictions or
                ClassroomQuickControlAction.UnlockInput))
            throw new InvalidOperationException("当前教师端构建未开放启用类课堂快捷管控；没有发送命令。恢复操作仍可使用。");
        var context = CaptureContext(expectedSessionId);
        var initialControlExpiry = _stateStore.Read(context.SessionId)?.ControlExpiresUtc;
        DateTimeOffset? requestedControlExpiry = null;
        if (action is ClassroomQuickControlAction.EnableWebsiteRestriction or ClassroomQuickControlAction.LockInput)
        {
            var expires = ResolveControlExpiry(controlExpiresUtc, DateTimeOffset.UtcNow);
            ValidateControlExpiry(expires, DateTimeOffset.UtcNow);
            requestedControlExpiry = expires;
        }
        var targets = SelectTargets(context, selectedTargetIds);
        if (targets.Count == 0) throw new InvalidDataException("当前课堂没有可操作的学生电脑。");
        var requestedUtc = DateTimeOffset.UtcNow;
        var clearControlExpiryWithRelease = action == ClassroomQuickControlAction.Release &&
            (expectedControlExpiresUtc is not null || selectedTargetIds is null);
        var updateControlExpiry = requestedControlExpiry is not null || clearControlExpiryWithRelease;
        var operationSource = expectedControlExpiresUtc is null ? source : "automatic-deadline";
        var reason = expectedControlExpiresUtc is null
            ? $"{(source == "teacher-mobile" ? "手机端" : "教师端")}请求 {action}。"
            : $"课堂策略到达截止时间 {expectedControlExpiresUtc.Value:O}。";
        IReadOnlyList<ClassroomQuickControlDeviceState> operations;
        try
        {
            operations = _stateStore.BeginOperations(context.SessionId,
                targets.Select(binding => binding.TargetId).ToArray(), action, operationSource, reason,
                requestedControlExpiry, updateControlExpiry,
                expectedControlExpiresUtc: expectedControlExpiresUtc);
        }
        catch (InvalidOperationException) when (expectedControlExpiresUtc is { } expectedExpiry &&
                                                  IsControlDeadlineStale(context.SessionId, expectedExpiry))
        {
            var skipped = targets.Select(binding => SkippedOperationResult(context, binding, action,
                TryReadDevice(context.SessionId, binding.TargetId),
                "已跳过失效的截止任务；课堂状态或截止时间已变化。")).ToArray();
            return new ClassroomQuickControlOperationResult(context.SessionId, action, requestedUtc,
                Array.AsReadOnly(skipped), "旧截止任务已失效；没有发送恢复命令。");
        }
        if (requestedControlExpiry is not null) EnsureControlExpiryLoop();
        var operationsByTarget = operations.ToDictionary(item => item.TargetId);
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
                    var operation = operationsByTarget[binding.TargetId];
                    var current = TryReadDevice(context.SessionId, binding.TargetId);
                    if (current is null || current.DesiredRevision != operation.DesiredRevision ||
                        current.TaskId != operation.TaskId)
                        return SkippedOperationResult(context, binding, action, current,
                            $"已忽略过期任务 {operation.TaskId:D}；设备已有更新的期望状态。");
                    if (current.RetryAfterUtc is { } retryAfter && retryAfter > DateTimeOffset.UtcNow)
                        return SkippedOperationResult(context, binding, action, current,
                            $"设备仍离线或状态未确认；已保留待同步任务，约 {Math.Ceiling((retryAfter - DateTimeOffset.UtcNow).TotalSeconds):0} 秒后再试。");
                    if (current.UpdatedUtc > operation.UpdatedUtc)
                        return SkippedOperationResult(context, binding, action, current,
                            "同一任务已由另一工作线程处理；已读取权威状态，没有重复发送。");
                    var result = await ExecuteTargetAsync(context, binding, action, cancellationToken,
                            operation.DesiredRevision, operation.TaskId!.Value)
                        .ConfigureAwait(false);
                    var latest = TryReadDevice(context.SessionId, binding.TargetId);
                    if (latest is { } latestState && latestState.DesiredRevision == operation.DesiredRevision &&
                        latestState.TaskId == operation.TaskId)
                    {
                        if (ClassroomQuickControlDesiredState.NeedsCatchUp(latestState))
                            RecordCatchUpFailure(context.SessionId, binding.TargetId,
                                operation.DesiredRevision, operation.TaskId!.Value);
                        else
                            ClearCatchUpRetry(context.SessionId, binding.TargetId,
                                operation.DesiredRevision, operation.TaskId!.Value);
                    }
                    return result;
                }
                finally { targetGate.Release(); }
            }
            finally { limit.Release(); }
        }).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var expiryToPreserve = expectedControlExpiresUtc ?? initialControlExpiry;
        var expiryStillMatches = _stateStore.Read(context.SessionId)?.ControlExpiresUtc == expiryToPreserve;
        if (action == ClassroomQuickControlAction.Release && expiryStillMatches &&
            _stateStore.Read(context.SessionId) is { } afterRelease &&
            afterRelease.Devices.All(device => !HasWebsiteOwnership(device) && !HasInputOwnership(device) &&
                device.DesiredAction != ClassroomQuickControlAction.EnableWebsiteRestriction &&
                device.Phase != ClassroomQuickControlPhase.WebsiteApplying &&
                device.Phase != ClassroomQuickControlPhase.Switching))
            _ = _stateStore.TrySetControlExpiry(context.SessionId, expiryToPreserve, null,
                expectedControlExpiresUtc is null ? source : "automatic-deadline",
                "恢复意图已持久化；清除当前截止计时器。");
        else if (action == ClassroomQuickControlAction.ClearWebsiteRestrictions &&
                 _stateStore.Read(context.SessionId) is { } afterClear &&
                 afterClear.Devices.All(device => !HasWebsiteOwnership(device) && !HasInputOwnership(device)))
            _ = _stateStore.TrySetControlExpiry(context.SessionId, expiryToPreserve, null,
                source, "网站策略恢复已完成；清除课堂截止计时器。");
        else if (action == ClassroomQuickControlAction.UnlockInput &&
                 _stateStore.Read(context.SessionId) is { } afterUnlock &&
                 afterUnlock.Devices.All(device => !HasWebsiteOwnership(device) && !HasInputOwnership(device)))
            _ = _stateStore.TrySetControlExpiry(context.SessionId, expiryToPreserve, null,
                source, "键鼠恢复已完成；清除课堂截止计时器。");
        var succeeded = results.Count(item => item.Succeeded);
        var waiting = results.Count(item => item.Phase == ClassroomQuickControlPhase.WaitingForAgent);
        var pending = results.Count(item => !item.Succeeded && item.NeedsReview);
        var failed = results.Length - succeeded - pending - waiting;
        var online = results.Length - waiting;
        var mode = action switch
        {
            ClassroomQuickControlAction.EnableWebsiteRestriction => "网站限制",
            ClassroomQuickControlAction.LockInput => "键鼠锁定",
            ClassroomQuickControlAction.UnlockInput => "解除键鼠锁定",
            ClassroomQuickControlAction.ClearWebsiteRestrictions => "恢复全班网站访问",
            _ => "解除课堂管控"
        };
        EnsureWebsiteCatchUpLoop();
        var summary = online == 0
            ? $"{mode}已提交；设备上线后自动同步。"
            : $"{mode}：在线设备已核验 {succeeded}/{online} 台，需核对 {pending} 台，失败 {failed} 台。" +
              (waiting > 0 ? "未上线设备不计入统计，上线后自动同步。" : "");
        return new ClassroomQuickControlOperationResult(context.SessionId, action, requestedUtc,
            Array.AsReadOnly(results), summary);
    }

    public async Task<ClassroomQuickControlOperationResult> ReleaseAllAsync(Guid expectedSessionId,
        CancellationToken cancellationToken = default, IReadOnlyList<Guid>? selectedTargetIds = null) =>
        await ExecuteAsync(expectedSessionId, ClassroomQuickControlAction.Release, selectedTargetIds,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    internal static DateTimeOffset ResolveControlExpiry(DateTimeOffset? requestedUtc, DateTimeOffset nowUtc)
    {
        // A new Apply starts a new deadline. Reusing the previous value lets its old expiry timer
        // undo the newly applied policy later; mobile Apply requests also arrive without a deadline.
        return (requestedUtc ?? nowUtc.Add(DefaultWebsiteLifetime)).ToUniversalTime();
    }

    internal static DateTimeOffset ResolveControlExpiry(DateTimeOffset? requestedUtc,
        DateTimeOffset? previousExpiryUtc, DateTimeOffset nowUtc) => ResolveControlExpiry(requestedUtc, nowUtc);

    private static void ValidateControlExpiry(DateTimeOffset expiresUtc, DateTimeOffset nowUtc)
    {
        var expiry = expiresUtc.ToUniversalTime();
        var now = nowUtc.ToUniversalTime();
        if (expiry <= now || expiry - now > WebsitePolicyCompiler.MaximumPolicyLifetime)
            throw new InvalidDataException("快捷管控截止时间必须晚于当前时间，且不能超过 24 小时。");
        if (expiry.ToLocalTime().Date != now.ToLocalTime().Date)
            throw new InvalidDataException("快捷管控只能持续到今天结束，不能跨天。");
    }

    private void EnsureControlExpiryLoop()
    {
        lock (_contextGate)
        {
            if (_disposed || _shutdown.IsCancellationRequested || _context is null ||
                _stateStore.Read(_context.SessionId)?.ControlExpiresUtc is null ||
                _controlExpiryTask is { IsCompleted: false }) return;
            _controlExpiryTask = Task.Run(ControlExpiryLoopAsync);
        }
    }

    private async Task ControlExpiryLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TeacherClassroomEventContext? context;
                lock (_contextGate) context = _context;
                if (context is null) return;
                var expires = _stateStore.Read(context.SessionId)?.ControlExpiresUtc;
                if (expires is null) return;
                var remaining = expires.Value - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    try
                    {
                        _ = await ExecuteAsync(context.SessionId, ClassroomQuickControlAction.Release,
                            cancellationToken: _shutdown.Token, expectedControlExpiresUtc: expires.Value)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                        InvalidDataException or InvalidOperationException or CryptographicException or
                        PlatformNotSupportedException or ArgumentException or System.Security.SecurityException)
                    {
                        // Persisted ownership remains available for the normal agent catch-up loop.
                        try
                        {
                            _ = _stateStore.TrySetControlExpiry(context.SessionId, expires, null,
                                "automatic-deadline",
                                "自动截止处理失败；仅移除仍匹配的旧截止计时器，保留待同步设备记录。");
                        }
                        catch (Exception) { }
                    }
                    return;
                }
                await Task.Delay(remaining < ControlExpiryCheckInterval
                    ? remaining : ControlExpiryCheckInterval, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            lock (_contextGate) _controlExpiryTask = null;
            if (!_shutdown.IsCancellationRequested) EnsureControlExpiryLoop();
        }
    }

    private async Task<ClassroomQuickControlTargetResult> ExecuteTargetAsync(
        TeacherClassroomEventContext context, TargetBinding binding, ClassroomQuickControlAction action,
        CancellationToken cancellationToken, long desiredRevision, Guid taskId,
        bool allowEndedRestore = false)
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
            EnsureCurrentTask(context.SessionId, binding.TargetId, desiredRevision, taskId, action,
                allowEndedRestore);
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
                    binding, previous, website!, guardResult, cancellationToken, allowEndedRestore).ConfigureAwait(false),
                ClassroomQuickControlAction.LockInput => await LockInputAsync(context, binding, previous,
                    website, guardResult, cancellationToken, allowEndedRestore).ConfigureAwait(false),
                ClassroomQuickControlAction.UnlockInput => await UnlockInputAsync(context, binding, previous,
                    guardResult, cancellationToken, allowEndedRestore).ConfigureAwait(false),
                ClassroomQuickControlAction.ClearWebsiteRestrictions => await ClearAllWebsiteRestrictionsAsync(
                    context, binding, previous, readback.Website, cancellationToken, allowEndedRestore)
                    .ConfigureAwait(false),
                ClassroomQuickControlAction.Release => await ReleaseAsync(context, binding, previous,
                    website, readback.Website.TransientUnavailable, cancellationToken, allowEndedRestore)
                    .ConfigureAwait(false),
                _ => StoreResult(context, binding, previous, requestedMode, ClassroomQuickControlPhase.Conflict,
                    false, true, "快捷管控操作无效。", previous.WebsiteRevision,
                    previous.PendingWebsiteRevision, previous.InputLeaseId, previous.ExpiresUtc)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (StaleClassroomQuickControlTaskException exception)
        {
            var latest = TryReadDevice(context.SessionId, binding.TargetId) ?? previous;
            return Result(context, binding, previous.Mode, requestedMode, latest.Mode, latest.Phase,
                false, true, exception.Message, latest.WebsiteRevision ?? latest.PendingWebsiteRevision,
                latest.InputLeaseId, latest.ExpiresUtc);
        }
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
            if (latest.DesiredRevision != desiredRevision || latest.TaskId != taskId ||
                latest.DesiredAction != action)
                return Result(context, binding, previous.Mode, requestedMode, latest.Mode, latest.Phase,
                    false, true, "已忽略旧任务的失败回执；当前设备期望状态属于较新的任务。",
                    latest.WebsiteRevision ?? latest.PendingWebsiteRevision, latest.InputLeaseId,
                    latest.ExpiresUtc);
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
        pending = SaveDevice(context, pending);
        EnsureCurrentTask(context.SessionId, binding.TargetId, pending.DesiredRevision,
            pending.TaskId!.Value, ClassroomQuickControlAction.ClearWebsiteRestrictions, allowEndedRestore);
        ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
        var pushed = await WebsitePolicyTransport.PushAsync([binding.Target], signed, context.CampusId,
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentTask(context.SessionId, binding.TargetId, pending.DesiredRevision,
            pending.TaskId!.Value, ClassroomQuickControlAction.ClearWebsiteRestrictions, allowEndedRestore);
        ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
        var verified = await ReadWebsiteStatusAsync(context.CampusId, binding.Target,
            signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
        EnsureCurrentTask(context.SessionId, binding.TargetId, pending.DesiredRevision,
            pending.TaskId!.Value, ClassroomQuickControlAction.ClearWebsiteRestrictions, allowEndedRestore);
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

        var phase = verified.TransientUnavailable ? ClassroomQuickControlPhase.WaitingForAgent :
            verified.Succeeded ? ClassroomQuickControlPhase.Conflict : ClassroomQuickControlPhase.Unknown;
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
        if (action is not (ClassroomQuickControlAction.LockInput or ClassroomQuickControlAction.UnlockInput) && website is not null &&
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
                     (current.WebsiteRevision == website.Revision ||
                      current.PendingWebsiteRevision == website.Revision))
            {
                current = current with { Phase = ClassroomQuickControlPhase.WebsiteReported,
                    WebsiteRevision = website.Revision, PendingWebsiteRevision = null,
                    Mode = GetMode(website.Revision, null, current.InputLeaseId),
                    WebsitePhase = ClassroomQuickControlPhase.WebsiteReported,
                    UpdatedUtc = DateTimeOffset.UtcNow,
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
        else if (action is not (ClassroomQuickControlAction.LockInput or ClassroomQuickControlAction.UnlockInput) && website is not null &&
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

        if (action == ClassroomQuickControlAction.UnlockInput && guardResult.TransientUnavailable)
            return (current, ClassroomQuickControlPhase.WaitingForAgent, null);

        if (!guardResult.Succeeded || guardResult.Status is not { } guard)
        {
            if (HasInputOwnership(current) &&
                !(action == ClassroomQuickControlAction.Release && HasWebsiteOwnership(current)))
                return (current, ClassroomQuickControlPhase.Unknown,
                    "InputGuard 状态未通过签名读回；键鼠租约保留待核对。" + guardResult.Detail);
            return (current, ClassroomQuickControlPhase.Unknown,
                action is ClassroomQuickControlAction.LockInput or ClassroomQuickControlAction.UnlockInput
                    ? "InputGuard 状态未知；没有修改键鼠锁定。" + guardResult.Detail
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
        else if ((action is ClassroomQuickControlAction.LockInput or ClassroomQuickControlAction.UnlockInput) && guard.Supported &&
                 guard.Phase == StudentInputGuardPhase.Locked)
            return (current, ClassroomQuickControlPhase.Conflict,
                action == ClassroomQuickControlAction.UnlockInput
                    ? "学生机的键鼠锁定不属于本快捷管控；为避免解除其他来源的锁定，没有修改它。"
                    : "学生机已有本机快捷管控未拥有的输入锁定租约；没有发送新的键鼠锁定。");
        else if ((action is ClassroomQuickControlAction.LockInput or ClassroomQuickControlAction.UnlockInput) && guard.Supported &&
                 guard.Phase is not (StudentInputGuardPhase.Unlocked or
                     StudentInputGuardPhase.Expired or StudentInputGuardPhase.GuardExited))
            return (current, ClassroomQuickControlPhase.Unknown,
                "InputGuard 当前状态无法核验；没有修改键鼠锁定。");
        return (current, current.Phase, null);
    }

    private async Task<ClassroomQuickControlTargetResult> EnableWebsiteAsync(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, WebsitePolicyReportedState website,
        StudentInputGuardStatusDeliveryResult guardResult, CancellationToken cancellationToken,
        bool allowEndedRestore = false)
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
        var expires = _stateStore.Read(context.SessionId)?.ControlExpiresUtc ?? now.Add(DefaultWebsiteLifetime);
        ValidateControlExpiry(expires, now);
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
        applying = SaveDevice(context, applying);
        EnsureCurrentTask(context.SessionId, binding.TargetId, applying.DesiredRevision,
            applying.TaskId!.Value, ClassroomQuickControlAction.EnableWebsiteRestriction, allowEndedRestore);
        ThrowIfContextChanged(context, binding.TargetId);
        _ = await WebsitePolicyTransport.PushAsync([binding.Target], signed, context.CampusId,
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentTask(context.SessionId, binding.TargetId, applying.DesiredRevision,
            applying.TaskId!.Value, ClassroomQuickControlAction.EnableWebsiteRestriction, allowEndedRestore);
        ThrowIfContextChanged(context, binding.TargetId);
        var readback = await ReadWebsiteStatusAsync(context.CampusId, binding.Target, signingKey.PrivateKey,
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentTask(context.SessionId, binding.TargetId, applying.DesiredRevision,
            applying.TaskId!.Value, ClassroomQuickControlAction.EnableWebsiteRestriction, allowEndedRestore);
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
        if (degraded) detail += " Edge 有降级项，请查看“设备与更新”。";
        var applied = applying with
        {
            WebsitePhase = phase,
            Mode = GetMode(revision, null, previous.InputLeaseId),
            Phase = phase,
            WebsiteRevision = revision,
            PendingWebsiteRevision = null,
            WebsiteExpiresUtc = expires,
            UpdatedUtc = DateTimeOffset.UtcNow,
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
        StudentInputGuardStatusDeliveryResult guardResult, CancellationToken cancellationToken,
        bool allowEndedRestore = false)
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
        locking = SaveDevice(context, locking);
        EnsureCurrentTask(context.SessionId, binding.TargetId, locking.DesiredRevision,
            locking.TaskId!.Value, ClassroomQuickControlAction.LockInput, allowEndedRestore);
        ThrowIfContextChanged(context, binding.TargetId);
        using var signingKey = WebsitePolicySigningKeyStore.Open(context.CampusId);
        var command = await StudentInputGuardTransport.ExecuteAsync(binding.Target, context.CampusId,
            signingKey.PrivateKey, context.Session.Room.RoomId, StudentInputGuardAction.Activate,
            leaseId, cancellationToken).ConfigureAwait(false);
        EnsureCurrentTask(context.SessionId, binding.TargetId, locking.DesiredRevision,
            locking.TaskId!.Value, ClassroomQuickControlAction.LockInput, allowEndedRestore);
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
        EnsureCurrentTask(context.SessionId, binding.TargetId, locking.DesiredRevision,
            locking.TaskId!.Value, ClassroomQuickControlAction.LockInput, allowEndedRestore);
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
            UpdatedUtc = DateTimeOffset.UtcNow,
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

    private async Task<ClassroomQuickControlTargetResult> UnlockInputAsync(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, StudentInputGuardStatusDeliveryResult guardResult,
        CancellationToken cancellationToken, bool allowEndedRestore = false)
    {
        if (!guardResult.Succeeded || guardResult.Status is not { Supported: true } guard)
            return StoreResult(context, binding, previous, DeviceControlMode.None,
                guardResult.Status?.Supported == false ? ClassroomQuickControlPhase.Unsupported :
                    guardResult.TransientUnavailable ? ClassroomQuickControlPhase.WaitingForAgent :
                    ClassroomQuickControlPhase.Unknown, false, !guardResult.TransientUnavailable,
                guardResult.TransientUnavailable
                    ? "Agent 暂不可达；已保留键鼠恢复意图，设备上线后按退避间隔重新核对。" + guardResult.Detail
                    : "InputGuard 没有提供可信的签名状态；没有解除键鼠锁定。" + guardResult.Detail,
                previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
                previous.InputExpiresUtc ?? previous.ExpiresUtc);

        if (HasInputOwnership(previous))
            return await ReleaseOwnedInputAsync(context, binding, previous, guardResult,
                ClassroomQuickControlAction.UnlockInput, cancellationToken, allowEndedRestore).ConfigureAwait(false);

        if ((guard.Phase is StudentInputGuardPhase.Unlocked or StudentInputGuardPhase.Expired) &&
            guard.LeaseId is null)
        {
            var confirmed = SaveDevice(context, ClearInputControl(previous,
                "InputGuard 已签名读回未锁定状态；网站限制保持不变。"));
            return Result(context, binding, previous.Mode, DeviceControlMode.None, confirmed.Mode,
                confirmed.Phase, true, false, confirmed.Detail,
                confirmed.WebsiteRevision ?? confirmed.PendingWebsiteRevision, null,
                confirmed.WebsiteExpiresUtc ?? confirmed.ExpiresUtc);
        }

        return StoreResult(context, binding, previous, DeviceControlMode.None,
            guard.Phase == StudentInputGuardPhase.Locked ? ClassroomQuickControlPhase.Conflict :
                ClassroomQuickControlPhase.Unknown, false, true,
            guard.Phase == StudentInputGuardPhase.Locked
                ? "学生机当前键鼠锁定没有本机所有权记录；没有解除其他来源的锁定。"
                : "InputGuard 状态尚未确认未锁定；没有报告键鼠已解除。",
            previous.WebsiteRevision, previous.PendingWebsiteRevision, previous.InputLeaseId,
            previous.InputExpiresUtc ?? previous.ExpiresUtc);
    }

    private async Task<ClassroomQuickControlTargetResult> ReleaseAsync(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, WebsitePolicyReportedState? website,
        bool websiteTransientUnavailable, CancellationToken cancellationToken,
        bool allowEndedRestore = false)
    {
        ClassroomQuickControlTargetResult? websiteResult = null;
        if (HasWebsiteOwnership(previous))
        {
            websiteResult = website is null
                ? StoreResult(context, binding, previous, DeviceControlMode.None,
                    websiteTransientUnavailable ? ClassroomQuickControlPhase.WaitingForAgent :
                        ClassroomQuickControlPhase.Unknown,
                    false, !websiteTransientUnavailable,
                    websiteTransientUnavailable
                        ? "Agent 暂不可达；已保留恢复期望状态，设备上线后按退避间隔重新核对。"
                        : "网站策略状态暂不可读；保留网站归属记录，未修改该策略。",
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
                ClassroomQuickControlAction.Release, cancellationToken, allowEndedRestore).ConfigureAwait(false);
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
        pending = SaveDevice(context, pending);
        EnsureCurrentTask(context.SessionId, binding.TargetId, pending.DesiredRevision,
            pending.TaskId!.Value, ClassroomQuickControlAction.Release, allowEndedRestore);
        ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
        _ = await WebsitePolicyTransport.PushAsync([binding.Target], signed, context.CampusId,
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentTask(context.SessionId, binding.TargetId, pending.DesiredRevision,
            pending.TaskId!.Value, ClassroomQuickControlAction.Release, allowEndedRestore);
        ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
        var result = await ReadWebsiteStatusAsync(context.CampusId, binding.Target, signingKey.PrivateKey,
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentTask(context.SessionId, binding.TargetId, pending.DesiredRevision,
            pending.TaskId!.Value, ClassroomQuickControlAction.Release, allowEndedRestore);
        ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
        if (result.Succeeded && result.Status?.Website.Mode == WebsitePolicyMode.Disabled &&
            result.Status.Website.Revision >= revision)
        {
            var clear = SaveDevice(context, ClearWebsiteControl(pending,
                "解除命令后已读回网站限制为停用。"));
            return Result(context, binding, previous.Mode, DeviceControlMode.None, clear.Mode,
                clear.Phase, true, HasInputOwnership(clear), clear.Detail, null,
                clear.InputLeaseId, clear.InputExpiresUtc ?? clear.ExpiresUtc);
        }
        var phase = result.TransientUnavailable ? ClassroomQuickControlPhase.WaitingForAgent :
            result.Succeeded ? ClassroomQuickControlPhase.Conflict : ClassroomQuickControlPhase.Unknown;
        var uncertain = pending with { Mode = GetMode(previous.WebsiteRevision, revision,
                previous.InputLeaseId),
            Phase = phase, WebsitePhase = phase, UpdatedUtc = DateTimeOffset.UtcNow,
            Detail = "网站解除尚未通过签名状态读回；保留归属与待确认版本，键鼠租约未改动。" };
        SaveDevice(context, uncertain);
        return Result(context, binding, previous.Mode, DeviceControlMode.None, previous.Mode, phase,
            false, true, uncertain.Detail + " " + result.Detail, previous.WebsiteRevision,
            previous.InputLeaseId, previous.InputExpiresUtc ?? previous.ExpiresUtc);
    }

    private async Task<ClassroomQuickControlTargetResult> ReleaseOwnedInputAsync(
        TeacherClassroomEventContext context, TargetBinding binding,
        ClassroomQuickControlDeviceState previous, StudentInputGuardStatusDeliveryResult guardResult,
        ClassroomQuickControlAction action, CancellationToken cancellationToken,
        bool allowEndedRestore = false)
    {
        if (!guardResult.Succeeded || guardResult.Status is not { Supported: true } status)
            return StoreResult(context, binding, previous, DeviceControlMode.None,
                guardResult.TransientUnavailable ? ClassroomQuickControlPhase.WaitingForAgent :
                    ClassroomQuickControlPhase.Unknown,
                false, !guardResult.TransientUnavailable,
                guardResult.TransientUnavailable
                    ? "Agent 暂不可达；已保留键鼠恢复意图，设备上线后按退避间隔重新核对。" + guardResult.Detail
                    : "InputGuard 状态未通过签名读回；没有报告已解除。" + guardResult.Detail,
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
        pending = SaveDevice(context, pending);
        RemoveRenewableLease(binding.TargetId, leaseId);
        EnsureCurrentTask(context.SessionId, binding.TargetId, pending.DesiredRevision,
            pending.TaskId!.Value, action, allowEndedRestore);
        ThrowIfContextChanged(context, binding.TargetId, allowEndedRestore);
        using var signingKey = WebsitePolicySigningKeyStore.Open(context.CampusId);
        var command = await StudentInputGuardTransport.ExecuteAsync(binding.Target, context.CampusId,
            signingKey.PrivateKey, context.Session.Room.RoomId, StudentInputGuardAction.Release, leaseId,
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentTask(context.SessionId, binding.TargetId, pending.DesiredRevision,
            pending.TaskId!.Value, action, allowEndedRestore);
        var finalStatus = await StudentInputGuardTransport.ReadStatusAsync(binding.Target, context.CampusId,
            signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
        EnsureCurrentTask(context.SessionId, binding.TargetId, pending.DesiredRevision,
            pending.TaskId!.Value, action, allowEndedRestore);
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
        var uncertainPhase = finalStatus.TransientUnavailable
            ? ClassroomQuickControlPhase.WaitingForAgent : ClassroomQuickControlPhase.Unknown;
        var uncertain = pending with { Phase = uncertainPhase,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Detail = finalStatus.TransientUnavailable
                ? "Agent 在解除后暂不可达；保留键鼠恢复意图，设备上线后按退避间隔重新核对。"
                : "解除尚未通过签名状态读回；20 秒租约到期后 Guard 会自行恢复输入。" };
        SaveDevice(context, uncertain);
        return Result(context, binding, previous.Mode, DeviceControlMode.None, previous.Mode,
            uncertainPhase, false, !finalStatus.TransientUnavailable,
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
            WebsitePhase = requestedMode == DeviceControlMode.WebsiteRestriction ||
                           (previous.DesiredAction is ClassroomQuickControlAction.Release or
                               ClassroomQuickControlAction.ClearWebsiteRestrictions) &&
                           (websiteRevision is not null || pendingWebsiteRevision is not null)
                ? phase : previous.WebsitePhase,
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

    private ClassroomQuickControlTargetResult SkippedOperationResult(TeacherClassroomEventContext context,
        TargetBinding binding, ClassroomQuickControlAction action, ClassroomQuickControlDeviceState? current,
        string detail)
    {
        var state = current ?? NotStartedState(binding.TargetId);
        var requestedMode = action switch
        {
            ClassroomQuickControlAction.EnableWebsiteRestriction => DeviceControlMode.WebsiteRestriction,
            ClassroomQuickControlAction.LockInput => DeviceControlMode.InputLocked,
            _ => DeviceControlMode.None
        };
        return Result(context, binding, state.Mode, requestedMode, state.Mode, state.Phase,
            false, false, detail, state.WebsiteRevision ?? state.PendingWebsiteRevision,
            state.InputLeaseId, state.InputExpiresUtc ?? state.ExpiresUtc);
    }

    private ClassroomQuickControlDeviceState SaveDevice(TeacherClassroomEventContext context,
        ClassroomQuickControlDeviceState device, string source = "agent-execution")
    {
        if (device.DesiredRevision > 0 && device.TaskId is { } taskId &&
            !_stateStore.IsCurrentTask(context.SessionId, device.TargetId, device.DesiredRevision, taskId))
            throw new StaleClassroomQuickControlTaskException(
                $"已忽略跨课堂过期任务 {taskId:D}（Revision={device.DesiredRevision}）；设备已有更新的权威任务。");
        if (device.WebsitePhase == ClassroomQuickControlPhase.NoneVerified)
            _websiteObservations[(context.SessionId, device.TargetId)] = "notEnabled";
        device = device with { ChangeSource = source, TriggerReason = device.Detail };
        var session = _stateStore.Read(context.SessionId) ?? new ClassroomQuickControlSessionState(
            ClassroomQuickControlStateStore.CurrentSchemaVersion, context.SessionId, context.CampusId,
            context.Session.Room.RoomId, true, DateTimeOffset.UtcNow, []);
        var devices = session.Devices.Where(item => item.TargetId != device.TargetId).Append(device)
            .OrderBy(item => item.TargetId).ToArray();
        var saved = _stateStore.Save(session with { UpdatedUtc = DateTimeOffset.UtcNow,
            Devices = Array.AsReadOnly(devices) });
        var current = saved.Devices.Single(item => item.TargetId == device.TargetId);
        if (current.DesiredRevision != device.DesiredRevision || current.TaskId != device.TaskId)
            throw new StaleClassroomQuickControlTaskException(
                $"已忽略旧任务 {device.TaskId?.ToString("D") ?? "(legacy)"} 的状态回写；当前 Revision={current.DesiredRevision}，TaskId={current.TaskId?.ToString("D") ?? "(none)"}。");
        if (current != device &&
            (device.StateRevision == long.MaxValue || current.StateRevision != device.StateRevision + 1 ||
             current != (device with { StateRevision = current.StateRevision })))
            throw new StaleClassroomQuickControlTaskException(
                $"已忽略基于旧状态 Revision={device.StateRevision} 的回写（来源={source}）；当前状态 Revision={current.StateRevision}。");
        return current;
    }

    private void EnsureCurrentTask(Guid sessionId, Guid targetId, long desiredRevision, Guid taskId,
        ClassroomQuickControlAction action, bool allowEndedRestore = false)
    {
        var session = _stateStore.Read(sessionId);
        var current = TryReadDevice(sessionId, targetId);
        var sessionAllowsAction = session is { ClosedUtc: null, Active: true } ||
            allowEndedRestore && action == ClassroomQuickControlAction.Release &&
            session is { Active: false, ClosedUtc: not null };
        if (!sessionAllowsAction || current is null || current.DesiredRevision != desiredRevision ||
            current.TaskId != taskId || current.DesiredAction != action ||
            !_stateStore.IsCurrentTask(sessionId, targetId, desiredRevision, taskId))
            throw new StaleClassroomQuickControlTaskException(
                $"已取消过期快捷管控任务 {taskId:D}（Revision={desiredRevision}）；同设备当前期望状态已变化。");
    }

    private ClassroomQuickControlDeviceState ReadDevice(Guid sessionId, Guid targetId) =>
        TryReadDevice(sessionId, targetId) ?? NotStartedState(targetId);

    private ClassroomQuickControlDeviceState? TryReadDevice(Guid sessionId, Guid targetId) =>
        _stateStore.Read(sessionId)?.Devices.SingleOrDefault(item => item.TargetId == targetId);

    private void QueueEndedSessionRestores(TeacherClassroomEventContext context)
    {
        var state = _stateStore.CloseSession(context.SessionId, DateTimeOffset.UtcNow);
        if (state.Devices.Any(device => device.DesiredAction == ClassroomQuickControlAction.Release &&
                                        (HasWebsiteOwnership(device) || HasInputOwnership(device))))
            _endedRestoreContexts[context.SessionId] = context;
        else
            _endedRestoreContexts.Remove(context.SessionId);
    }

    private void RecoverPreviousProcessState()
    {
        IReadOnlyDictionary<Guid, ClassroomSession> classroomSessions;
        try { classroomSessions = _classroomSessionStore.ReadRecent().ToDictionary(item => item.SessionId); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { classroomSessions = new Dictionary<Guid, ClassroomSession>(); }

        foreach (var session in _stateStore.ReadAll())
        {
            var now = DateTimeOffset.UtcNow;
            if (session.ClosedUtc is null &&
                classroomSessions.TryGetValue(session.SessionId, out var classroom) &&
                classroom.Status == ClassroomSessionStatus.Ended)
            {
                _ = _stateStore.CloseSession(session.SessionId, now);
                continue;
            }
            if (!session.Active || session.ClosedUtc is not null) continue;
            _ = _stateStore.PauseSession(session.SessionId, now, "teacher-restart",
                "检测到 TeacherConsole 重启；栅栏上次进程任务并保留活动课堂期望状态，待当前课堂重新连接。");
        }
        ScheduleEndedRestoreContextRecovery();
    }

    private void ScheduleEndedRestoreContextRecovery()
    {
        if (Interlocked.Exchange(ref _endedRestoreContextRecoveryQueued, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try { RecoverEndedSessionRestoreContexts(); }
            finally { Interlocked.Exchange(ref _endedRestoreContextRecoveryQueued, 0); }
        });
    }

    private void RecoverEndedSessionRestoreContexts()
    {
        try
        {
            var pendingStates = _stateStore.ReadAll().Where(item => !item.Active &&
                item.Devices.Any(device => device.DesiredAction == ClassroomQuickControlAction.Release &&
                    (HasWebsiteOwnership(device) || HasInputOwnership(device)))).ToArray();
            if (pendingStates.Length == 0) return;
            var sessions = _classroomSessionStore.ReadRecent().Where(item =>
                    item.Status == ClassroomSessionStatus.Ended)
                .ToDictionary(item => item.SessionId);
            if (!pendingStates.Any(item => sessions.ContainsKey(item.SessionId))) return;
            var campuses = _campusDirectoryStore.Load();
            var locations = _networkDirectory.ReadLocations();
            var availableSigningCampuses = WebsitePolicySigningKeyStore.ReadCampusIds()
                .Where(item => !ApplicationPolicySigningKeyStore.IsInternalNamespace(item) &&
                               !StudentSystemPolicySigningKeyStore.IsInternalNamespace(item))
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (var state in pendingStates)
            {
                if (!sessions.TryGetValue(state.SessionId, out var endedSession)) continue;
                var campusMatches = campuses.Where(item => item.ProfileId == endedSession.Room.CampusProfileId)
                    .ToArray();
                var roomMatches = campusMatches.Length == 1
                    ? campusMatches[0].Rooms.Where(item => item.RoomId == endedSession.Room.RoomId).ToArray()
                    : [];
                if (campusMatches.Length != 1 || roomMatches.Length != 1) continue;
                var matched = ClassroomRestoreLedger.MatchCurrentTargets(endedSession, campusMatches[0],
                    roomMatches[0], locations);
                var pendingIds = state.Devices.Where(device =>
                        device.DesiredAction == ClassroomQuickControlAction.Release &&
                        (HasWebsiteOwnership(device) || HasInputOwnership(device)))
                    .Select(device => device.TargetId).ToHashSet();
                var targets = matched.Where(item => pendingIds.Contains(item.TargetId)).ToArray();
                if (targets.Length == 0) continue;
                var targetIds = targets.ToDictionary(item => item.Target, item => item.TargetId,
                    StringComparer.OrdinalIgnoreCase);
                var signingCampus = ResolveEndedRestoreSigningCampus(state.CampusId,
                    _classroomSigningContextStore.Read(endedSession.SessionId), availableSigningCampuses);
                if (signingCampus is null) continue;
                var targetNames = targets.ToDictionary(item => item.Target,
                    item => item.DeviceLabel, StringComparer.OrdinalIgnoreCase);
                var context = new TeacherClassroomEventContext(signingCampus, endedSession.SessionId,
                    Array.AsReadOnly(targetIds.Keys.ToArray()), targetIds, endedSession, targetNames);
                lock (_contextGate)
                {
                    if (_disposed || _context?.SessionId == endedSession.SessionId) continue;
                    _endedRestoreContexts[endedSession.SessionId] = context;
                }
            }
            EnsureWebsiteCatchUpLoop();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or CryptographicException or
            PlatformNotSupportedException or ArgumentException or System.Security.SecurityException or
            System.ComponentModel.Win32Exception or TimeoutException)
        {
            // Keep the durable release intent pending; a later explicit session sync can retry context recovery.
        }
    }

    internal static string? ResolveEndedRestoreSigningCampus(string recordedCampusId, string? pinnedCampusId,
        IReadOnlyList<string> availableSigningCampuses) =>
        (pinnedCampusId is null || string.Equals(pinnedCampusId, recordedCampusId, StringComparison.Ordinal)) &&
        availableSigningCampuses.Contains(recordedCampusId, StringComparer.Ordinal) ? recordedCampusId : null;

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
            SaveDevice(context, device with { WebsitePhase = ClassroomQuickControlPhase.Unknown },
                "agent-status-poll");
            return;
        }
        if (website.Mode == WebsitePolicyMode.Disabled &&
            website.Revision >= Math.Max(device.WebsiteRevision ?? 0, device.PendingWebsiteRevision ?? 0))
        {
            SaveDevice(context, ClearWebsiteControl(device, "网站限制已解除或已到期。"),
                "agent-status-poll");
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
        }, "agent-status-poll");
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
            if (_context is null && _endedRestoreContexts.Count == 0) return;
            if (_websiteCatchUpTask is { IsCompleted: false })
            {
                _websiteCatchUpRequested = true;
                try { _websiteCatchUpWakeSignal.Release(); }
                catch (SemaphoreFullException) { }
                return;
            }
            _websiteCatchUpRequested = false;
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

                var candidates = new List<(CatchUpContext Context, Guid TargetId,
                    ClassroomQuickControlAction Action, long DesiredRevision, Guid TaskId)>();
                foreach (var item in contexts)
                {
                    var state = _stateStore.Read(item.Context.SessionId);
                    if (state is null || item.IsEndedRestore && state.Active ||
                        !item.IsEndedRestore && !state.Active) continue;
                    foreach (var device in state.Devices)
                    {
                        var action = ClassroomQuickControlDesiredState.ResolveCatchUpAction(device);
                        if (action is null || item.IsEndedRestore && action != ClassroomQuickControlAction.Release ||
                            !ClassroomQuickControlDesiredState.NeedsCatchUp(device) ||
                            device.DesiredRevision < 1 || device.TaskId is null ||
                            !_stateStore.IsCurrentTask(item.Context.SessionId, device.TargetId,
                                device.DesiredRevision, device.TaskId.Value))
                            continue;
                        candidates.Add((item, device.TargetId, action.Value, device.DesiredRevision,
                            device.TaskId.Value));
                    }
                }
                if (candidates.Count == 0)
                {
                    RemoveCompletedEndedRestoreContexts();
                    return;
                }

                var nowUtc = DateTimeOffset.UtcNow;
                var work = candidates.Where(item => IsCatchUpDue(item.Context.Context.SessionId,
                    item.TargetId, item.DesiredRevision, item.TaskId, nowUtc)).ToArray();
                if (work.Length == 0)
                {
                    await WaitForWebsiteCatchUpOrIntervalAsync().ConfigureAwait(false);
                    continue;
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
                            if (current is null || current.DesiredRevision != item.DesiredRevision ||
                                current.TaskId != item.TaskId ||
                                ClassroomQuickControlDesiredState.ResolveCatchUpAction(current) != item.Action ||
                                !ClassroomQuickControlDesiredState.NeedsCatchUp(current))
                            {
                                ClearCatchUpRetry(context.SessionId, item.TargetId,
                                    item.DesiredRevision, item.TaskId);
                                return;
                            }
                            if (!item.Context.IsEndedRestore && !IsCurrentTarget(context, item.TargetId)) return;
                            var binding = SelectTargets(context, [item.TargetId]).Single();
                            if (item.Action == ClassroomQuickControlAction.EnableWebsiteRestriction &&
                                (current.DesiredAction != item.Action ||
                                 current.WebsitePhase is not (ClassroomQuickControlPhase.WaitingForAgent or
                                    ClassroomQuickControlPhase.WebsiteApplying or
                                    ClassroomQuickControlPhase.Switching) &&
                                 current.PendingWebsiteRevision is null))
                            {
                                await RefreshWebsiteStateAsync(context, binding, current, _shutdown.Token)
                                    .ConfigureAwait(false);
                                var refreshed = TryReadDevice(context.SessionId, item.TargetId);
                                if (refreshed is not null &&
                                    !ClassroomQuickControlDesiredState.NeedsCatchUp(refreshed))
                                    ClearCatchUpRetry(context.SessionId, item.TargetId,
                                        item.DesiredRevision, item.TaskId);
                                else
                                    RecordCatchUpFailure(context.SessionId, item.TargetId,
                                        item.DesiredRevision, item.TaskId);
                                return;
                            }
                            _ = await ExecuteTargetAsync(context, binding, item.Action, _shutdown.Token,
                                item.DesiredRevision, item.TaskId, item.Context.IsEndedRestore)
                                .ConfigureAwait(false);
                            if (!ClassroomQuickControlDesiredState.NeedsCatchUp(
                                    TryReadDevice(context.SessionId, item.TargetId) ?? current))
                                ClearCatchUpRetry(context.SessionId, item.TargetId,
                                    item.DesiredRevision, item.TaskId);
                            else
                                RecordCatchUpFailure(context.SessionId, item.TargetId,
                                    item.DesiredRevision, item.TaskId);
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
                        RecordCatchUpFailure(item.Context.Context.SessionId, item.TargetId,
                            item.DesiredRevision, item.TaskId);
                    }
                    finally { limit.Release(); }
                }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
                RemoveCompletedEndedRestoreContexts();
                await WaitForWebsiteCatchUpOrIntervalAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception)
        {
            // Fail closed. A later classroom sync or explicit command can restart the bounded loop.
            try { await WaitForWebsiteCatchUpOrIntervalAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }
        finally
        {
            lock (_contextGate)
            {
                _websiteCatchUpTask = null;
                if (_websiteCatchUpRequested && !_disposed && !_shutdown.IsCancellationRequested &&
                    (_context is not null || _endedRestoreContexts.Count > 0))
                {
                    _websiteCatchUpRequested = false;
                    _websiteCatchUpTask = Task.Run(WebsiteCatchUpLoopAsync);
                }
            }
        }
    }

    private async Task WaitForWebsiteCatchUpOrIntervalAsync() =>
        _ = await _websiteCatchUpWakeSignal.WaitAsync(WebsiteCatchUpInterval, _shutdown.Token)
            .ConfigureAwait(false);

    private bool IsCatchUpDue(Guid sessionId, Guid targetId, long desiredRevision, Guid taskId,
        DateTimeOffset nowUtc)
    {
        var current = TryReadDevice(sessionId, targetId);
        return current is not null && current.DesiredRevision == desiredRevision && current.TaskId == taskId &&
               _stateStore.IsCurrentTask(sessionId, targetId, desiredRevision, taskId) &&
               (current.RetryAfterUtc is null || current.RetryAfterUtc <= nowUtc);
    }

    private void RecordCatchUpFailure(Guid sessionId, Guid targetId, long desiredRevision, Guid taskId)
    {
        try { _ = _stateStore.UpdateRetry(sessionId, targetId, desiredRevision, taskId); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         InvalidDataException or InvalidOperationException) { }
    }

    private void ClearCatchUpRetry(Guid sessionId, Guid targetId, long desiredRevision, Guid taskId)
    {
        try { _ = _stateStore.ClearRetry(sessionId, targetId, desiredRevision, taskId); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         InvalidDataException or InvalidOperationException) { }
    }

    private void RemoveCompletedEndedRestoreContexts()
    {
        lock (_contextGate)
            foreach (var sessionId in _endedRestoreContexts.Keys.ToArray())
            {
                var state = _stateStore.Read(sessionId);
                if (state is null || !state.Devices.Any(device =>
                        device.DesiredAction == ClassroomQuickControlAction.Release &&
                        (HasWebsiteOwnership(device) || HasInputOwnership(device)) &&
                        device.Phase != ClassroomQuickControlPhase.Conflict))
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
        ClassroomQuickControlDeviceState? renewalState = null;
        await targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfContextChanged(context, targetId);
            var previous = ReadDevice(context.SessionId, targetId);
            renewalState = previous;
            if (!HasInputOwnership(previous) || previous.InputLeaseId != leaseId ||
                previous.Phase != ClassroomQuickControlPhase.InputLockedVerified)
            {
                RemoveRenewableLease(targetId, leaseId);
                return;
            }
            var sessionState = _stateStore.Read(context.SessionId);
            if (sessionState is { Active: true, ClosedUtc: null, ControlExpiresUtc: { } controlExpiry } &&
                controlExpiry <= DateTimeOffset.UtcNow)
            {
                // The session deadline loop owns the single class-wide transition. Creating a
                // per-device Release here races that transition and can mint a second task/revision.
                RemoveRenewableLease(targetId, leaseId);
                EnsureControlExpiryLoop();
                return;
            }
            using var signingKey = WebsitePolicySigningKeyStore.Open(context.CampusId);
            if (!IsCurrentRenewalState(context.SessionId, targetId, renewalState)) return;
            var result = await StudentInputGuardTransport.ExecuteAsync(binding.Target, context.CampusId,
                signingKey.PrivateKey, context.Session.Room.RoomId, StudentInputGuardAction.Renew, leaseId,
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && result.Receipt?.Outcome == StudentInputGuardOutcome.Succeeded)
            {
                if (!IsCurrentRenewalState(context.SessionId, targetId, renewalState)) return;
                var readback = await StudentInputGuardTransport.ReadStatusAsync(binding.Target,
                    context.CampusId, signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
                if (readback.Succeeded && readback.Status is { Supported: true,
                        Phase: StudentInputGuardPhase.Locked } status && status.LeaseId == leaseId)
                {
                    if (!IsCurrentRenewalState(context.SessionId, targetId, renewalState)) return;
                    SaveDevice(context, renewalState with { Mode = GetMode(renewalState.WebsiteRevision,
                            renewalState.PendingWebsiteRevision, renewalState.InputLeaseId),
                        Phase = ClassroomQuickControlPhase.InputLockedVerified,
                        ExpiresUtc = status.LeaseExpiresUtc, InputExpiresUtc = status.LeaseExpiresUtc,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        Detail = "续租已收到签名回执并通过状态读回。" });
                    return;
                }
                result = result with { Succeeded = false,
                    Detail = "续租回执后状态读回未匹配原租约；租约到期后会自动释放。" };
            }
            if (!IsCurrentRenewalState(context.SessionId, targetId, renewalState)) return;
            RemoveRenewableLease(targetId, leaseId);
            SaveDevice(context, renewalState with { Phase = ClassroomQuickControlPhase.Unknown,
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
            if (!IsCurrentRenewalState(context.SessionId, targetId, renewalState)) return;
            RemoveRenewableLease(targetId, leaseId);
            if (renewalState is not null)
                SaveDevice(context, renewalState with { Phase = ClassroomQuickControlPhase.Unknown,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Detail = "InputGuard 续租未确认；已停止该设备的自动续租，原短租约到期后会自动释放。" +
                             " " + Truncate(exception.Message, 300) });
        }
        finally { targetGate.Release(); }
    }

    private bool IsCurrentRenewalState(Guid sessionId, Guid targetId,
        ClassroomQuickControlDeviceState? expected)
    {
        if (expected is null || _stateStore.Read(sessionId) is not
            { Active: true, ClosedUtc: null }) return false;
        var current = TryReadDevice(sessionId, targetId);
        return current is not null && current.DesiredRevision == expected.DesiredRevision &&
               current.TaskId == expected.TaskId && current.DesiredAction == expected.DesiredAction &&
               current.InputLeaseId == expected.InputLeaseId &&
               expected.TaskId is { } taskId && expected.DesiredRevision > 0 &&
               _stateStore.IsCurrentTask(sessionId, targetId, expected.DesiredRevision, taskId);
    }

    private bool IsControlDeadlineStale(Guid sessionId, DateTimeOffset expected)
    {
        var session = _stateStore.Read(sessionId);
        return session is null || !session.Active || session.ClosedUtc is not null ||
               session.ControlExpiresUtc != expected;
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
        Task? controlExpiryTask;
        lock (_contextGate)
        {
            if (_disposed) return;
            _disposed = true;
            _renewableLeases.Clear();
            renewalTask = _renewalTask;
            websiteCatchUpTask = _websiteCatchUpTask;
            controlExpiryTask = _controlExpiryTask;
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
        if (controlExpiryTask is not null)
        {
            try { await controlExpiryTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }
        _shutdown.Dispose();
        _websiteCatchUpWakeSignal.Dispose();
        foreach (var gate in _targetGates.Values) gate.Dispose();
    }

}
