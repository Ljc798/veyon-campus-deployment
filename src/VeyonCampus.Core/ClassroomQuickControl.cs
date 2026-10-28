using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>Short-lived classroom controls; legacy policy modes are not part of this contract.</summary>
public enum DeviceControlMode
{
    None,
    WebsiteRestriction,
    InputLocked,
    WebsiteAndInputLocked
}

public enum ClassroomQuickControlAction
{
    EnableWebsiteRestriction,
    LockInput,
    Release,
    ClearWebsiteRestrictions,
    UnlockInput
}

public enum ClassroomQuickControlPhase
{
    NotStarted,
    NoneVerified,
    WaitingForAgent,
    WebsiteApplying,
    WebsiteReported,
    WebsiteApplied,
    InputLocking,
    InputLockedVerified,
    Switching,
    ReleasePending,
    Unknown,
    Conflict,
    Failed,
    Unsupported
}

/// <summary>
/// Records only the ownership evidence needed for safe compare-and-set and release. TargetId is the
/// stable room-directory identity; network addresses and student names are deliberately not persisted.
/// </summary>
public sealed record ClassroomQuickControlDeviceState(Guid TargetId, DeviceControlMode Mode,
    ClassroomQuickControlPhase Phase, long? WebsiteRevision, long? PendingWebsiteRevision, Guid? InputLeaseId,
    DateTimeOffset? ExpiresUtc, DateTimeOffset UpdatedUtc, string Detail)
{
    /// <summary>Monotonic per-device teacher task version across classroom sessions.</summary>
    public long DesiredRevision { get; init; }
    /// <summary>Monotonic version for accepted device-state writes within the desired operation.</summary>
    public long StateRevision { get; init; }
    public Guid? TaskId { get; init; }
    public ClassroomQuickControlAction? DesiredAction { get; init; }
    public int RetryFailureCount { get; init; }
    public DateTimeOffset? RetryAfterUtc { get; init; }
    public string ChangeSource { get; init; } = "legacy";
    public string TriggerReason { get; init; } = "";
    public DateTimeOffset? WebsiteExpiresUtc { get; init; }
    public DateTimeOffset? InputExpiresUtc { get; init; }
    public ClassroomQuickControlPhase? WebsitePhase { get; init; }
}

public sealed record ClassroomQuickControlSessionState(int SchemaVersion, Guid SessionId,
    string CampusId, Guid RoomId, bool Active, DateTimeOffset UpdatedUtc,
    IReadOnlyList<ClassroomQuickControlDeviceState> Devices)
{
    public long StateRevision { get; init; }
    public DateTimeOffset? ControlExpiresUtc { get; init; }
    /// <summary>Terminal end marker; restart recovery may pause an active session but cannot reopen a closed one.</summary>
    public DateTimeOffset? ClosedUtc { get; init; }
}

public sealed record ClassroomQuickControlTargetResult(Guid TargetId, string DisplayName,
    DeviceControlMode PreviousMode, DeviceControlMode RequestedMode, DeviceControlMode CurrentMode,
    ClassroomQuickControlPhase Phase, bool Succeeded, bool NeedsReview,
    long? WebsiteRevision, Guid? InputLeaseId, string Detail);

public sealed record ClassroomQuickControlOperationResult(Guid SessionId,
    ClassroomQuickControlAction Action, DateTimeOffset RequestedUtc,
    IReadOnlyList<ClassroomQuickControlTargetResult> Results, string Message);

public sealed record ClassroomQuickControlTargetSnapshot(Guid TargetId, string DisplayName,
    DeviceControlMode Mode, ClassroomQuickControlPhase Phase, DateTimeOffset UpdatedUtc,
    long? WebsiteRevision, Guid? InputLeaseId, DateTimeOffset? ExpiresUtc, string Detail)
{
    public string WebsiteState { get; init; } = "unknown";
    public string InputState { get; init; } = "unknown";
    public ClassroomQuickControlAction? DesiredAction { get; init; }
    public DateTimeOffset? RetryAfterUtc { get; init; }
}

public sealed record ClassroomQuickControlSnapshot(Guid SessionId, DateTimeOffset UpdatedUtc,
    IReadOnlyList<ClassroomQuickControlTargetSnapshot> Devices,
    bool PilotFeatureEnabled = false, string RoomName = "", int TargetCount = 0,
    string CampusName = "")
{
    public DateTimeOffset? ControlExpiresUtc { get; init; }
    public long StateRevision { get; init; }
}

public sealed record ClassroomQuickControlStateChange(DateTimeOffset TimeUtc, Guid SessionId, Guid TargetId,
    Guid? TaskId, long DesiredRevision, long? PolicyRevision, string OldState, string NewState,
    string Source, string TriggerReason, bool Accepted)
{
    public long StateRevision { get; init; }
    public long DeviceStateRevision { get; init; }
}

public static class ClassroomQuickControlDesiredState
{
    public static ClassroomQuickControlAction? ResolveCatchUpAction(ClassroomQuickControlDeviceState device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.DesiredAction is ClassroomQuickControlAction.Release or
            ClassroomQuickControlAction.EnableWebsiteRestriction or
            ClassroomQuickControlAction.ClearWebsiteRestrictions or
            ClassroomQuickControlAction.UnlockInput)
            return device.DesiredAction;
        var ownsWebsite = device.WebsiteRevision is not null || device.PendingWebsiteRevision is not null;
        if (device.DesiredAction is ClassroomQuickControlAction.LockInput or
            ClassroomQuickControlAction.UnlockInput && ownsWebsite &&
            (device.WebsitePhase == ClassroomQuickControlPhase.Unknown ||
             device.WebsitePhase is null && device.Phase == ClassroomQuickControlPhase.Unknown))
            return ClassroomQuickControlAction.EnableWebsiteRestriction;
        if (device.DesiredAction is not null) return null;
        return device.Phase == ClassroomQuickControlPhase.ReleasePending
            ? ClassroomQuickControlAction.Release
            : ownsWebsite && (device.WebsitePhase == ClassroomQuickControlPhase.Unknown ||
                device.WebsitePhase is null && device.Phase == ClassroomQuickControlPhase.Unknown)
                ? ClassroomQuickControlAction.EnableWebsiteRestriction
                : device.WebsitePhase is ClassroomQuickControlPhase.WaitingForAgent or
                ClassroomQuickControlPhase.WebsiteApplying or ClassroomQuickControlPhase.Switching
                ? ClassroomQuickControlAction.EnableWebsiteRestriction : null;
    }

    public static bool NeedsCatchUp(ClassroomQuickControlDeviceState device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Phase is ClassroomQuickControlPhase.Conflict or ClassroomQuickControlPhase.Unsupported)
            return false;
        var ownsWebsite = device.WebsiteRevision is not null || device.PendingWebsiteRevision is not null;
        var ownsInput = device.InputLeaseId is not null;
        return device.DesiredAction switch
        {
            ClassroomQuickControlAction.Release => ownsWebsite || ownsInput,
            ClassroomQuickControlAction.EnableWebsiteRestriction =>
                device.Phase is ClassroomQuickControlPhase.WaitingForAgent or
                    ClassroomQuickControlPhase.WebsiteApplying or ClassroomQuickControlPhase.Switching ||
                device.PendingWebsiteRevision is not null ||
                ownsWebsite && (device.WebsitePhase == ClassroomQuickControlPhase.Unknown ||
                    device.WebsitePhase is null && device.Phase == ClassroomQuickControlPhase.Unknown),
            ClassroomQuickControlAction.ClearWebsiteRestrictions =>
                device.PendingWebsiteRevision is not null || device.Phase is
                    ClassroomQuickControlPhase.ReleasePending or ClassroomQuickControlPhase.WaitingForAgent,
            ClassroomQuickControlAction.LockInput => ownsWebsite &&
                (device.WebsitePhase == ClassroomQuickControlPhase.Unknown ||
                 device.WebsitePhase is null && device.Phase == ClassroomQuickControlPhase.Unknown),
            ClassroomQuickControlAction.UnlockInput => ownsInput,
            null => device.Phase == ClassroomQuickControlPhase.ReleasePending ||
                    ownsWebsite && (device.WebsitePhase == ClassroomQuickControlPhase.Unknown ||
                        device.WebsitePhase is null && device.Phase == ClassroomQuickControlPhase.Unknown) ||
                    device.WebsitePhase is ClassroomQuickControlPhase.ReleasePending or
                        ClassroomQuickControlPhase.WaitingForAgent or ClassroomQuickControlPhase.WebsiteApplying or
                        ClassroomQuickControlPhase.Switching,
            _ => false
        };
    }
}

/// <summary>Bounded local ledger for quick-control ownership and unresolved releases.</summary>
public sealed class ClassroomQuickControlStateStore
{
    private sealed record StateFile(int SchemaVersion,
        IReadOnlyList<ClassroomQuickControlSessionState> Sessions);

    public const int CurrentSchemaVersion = 6;
    public const int MaximumTrackedSessions = 31;
    public const int MaximumDevicesPerSession = ClassroomSession.MaximumTargets;
    public const int MaximumFileBytes = 4 * 1024 * 1024;
    private const long MaximumChangeLogBytes = 2 * 1024 * 1024;
    private static readonly object ProcessGate = new();
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions ChangeLogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _path;

    public ClassroomQuickControlStateStore(string? path = null) =>
        _path = Path.GetFullPath(path ?? DefaultPath);

    public static string DefaultPath => Path.Combine(
        VeyonCampusPaths.GetLocalApplicationDataDirectory(),
        "VeyonCampus", "Teacher", "classroom-quick-control.json");

    public IReadOnlyList<ClassroomQuickControlSessionState> ReadAll()
    {
        lock (ProcessGate) return Array.AsReadOnly(ReadFile().Sessions.ToArray());
    }

    public ClassroomQuickControlSessionState? Read(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂 ID 无效。");
        lock (ProcessGate) return ReadFile().Sessions.SingleOrDefault(item => item.SessionId == sessionId);
    }

    public bool IsCurrentTask(Guid sessionId, Guid targetId, long desiredRevision, Guid taskId)
    {
        if (sessionId == Guid.Empty || targetId == Guid.Empty || desiredRevision < 1 || taskId == Guid.Empty)
            return false;
        lock (ProcessGate)
        {
            var heads = BuildTargetHeads(ReadFile().Sessions);
            return heads.TryGetValue(targetId, out var head) && head.SessionId == sessionId &&
                   head.Device.DesiredRevision == desiredRevision && head.Device.TaskId == taskId;
        }
    }

    public ClassroomQuickControlSessionState Save(ClassroomQuickControlSessionState state)
    {
        ValidateSession(state);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var current = ReadFile();
            var previous = current.Sessions.SingleOrDefault(item => item.SessionId == state.SessionId);
            var targetHeads = BuildTargetHeads(current.Sessions);
            if (previous is null && (state.ClosedUtc is not null || state.Devices.Any(device =>
                    device.DesiredRevision > 0 || device.TaskId is not null || device.Mode != DeviceControlMode.None ||
                    device.WebsiteRevision is not null || device.PendingWebsiteRevision is not null ||
                    device.InputLeaseId is not null)))
                throw new InvalidOperationException("课堂状态已结束或不存在；拒绝用迟到的设备回执重建旧课堂。");
            var merged = previous is null ? state with
            {
                StateRevision = Math.Max(1, state.StateRevision),
                Devices = Array.AsReadOnly(state.Devices.Select(device => device with
                    { StateRevision = Math.Max(1, device.StateRevision) }).ToArray())
            } : MergeWithoutRegression(previous, state, targetHeads);
            if (previous is not null)
            {
                var nextStateRevision = Math.Max(previous.StateRevision, merged.StateRevision);
                if (SessionContentChanged(previous, merged))
                {
                    if (nextStateRevision == long.MaxValue)
                        throw new InvalidDataException("课堂状态 Revision 已达到上限；状态未覆盖旧记录。");
                    nextStateRevision++;
                }
                merged = merged with { StateRevision = nextStateRevision };
            }
            var sessions = FitTrackedSessions(current.Sessions.Where(item => item.SessionId != state.SessionId)
                .Append(merged));
            if (sessions.Count(item => item.Active) > 1)
                throw new InvalidDataException("快捷管控仍有过多未完成课堂记录；请先核对待恢复项。");
            WriteFile(new StateFile(CurrentSchemaVersion, sessions));
            AppendStateChanges(previous, merged);
            if (previous is not null) AppendRejectedStaleChanges(previous, state, targetHeads);
            if (previous is not null && SessionMetadataChanged(previous, merged))
                AppendSessionChange(previous, merged,
                    merged.ClosedUtc != previous.ClosedUtc ? "classroom-ended" :
                    !previous.Active && merged.Active ? "teacher-resume" : "state-store",
                    merged.ClosedUtc != previous.ClosedUtc ? "课堂关闭状态已持久化。" :
                    !previous.Active && merged.Active ? "课堂快捷管控状态已在教师端重新连接。" :
                    "课堂范围或截止时间已持久化。");
            return merged;
        }
    }

    /// <summary>Assigns the next durable operation version before any network request is sent.</summary>
    public ClassroomQuickControlDeviceState BeginOperation(Guid sessionId, Guid targetId,
        ClassroomQuickControlAction action, string source, string reason,
        DateTimeOffset? expectedControlExpiresUtc = null) =>
        BeginOperations(sessionId, [targetId], action, source, reason,
            expectedControlExpiresUtc: expectedControlExpiresUtc).Single();

    /// <summary>
    /// Atomically records the new desired action for all selected devices and, for Apply, its new
    /// deadline before any target worker waits on a per-device gate or sends network traffic.
    /// </summary>
    public IReadOnlyList<ClassroomQuickControlDeviceState> BeginOperations(Guid sessionId,
        IReadOnlyList<Guid> targetIds, ClassroomQuickControlAction action, string source, string reason,
        DateTimeOffset? controlExpiresUtc = null, bool updateControlExpiry = false,
        DateTimeOffset? expectedControlExpiresUtc = null)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        if (sessionId == Guid.Empty || targetIds.Count == 0 ||
            targetIds.Count > MaximumDevicesPerSession || targetIds.Any(id => id == Guid.Empty) ||
            targetIds.Distinct().Count() != targetIds.Count || !Enum.IsDefined(action))
            throw new InvalidDataException("快捷管控任务标识无效。");
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (updateControlExpiry && controlExpiresUtc is { Offset: var expiryOffset } &&
            expiryOffset != TimeSpan.Zero ||
            expectedControlExpiresUtc is { Offset: var expectedOffset } && expectedOffset != TimeSpan.Zero)
            throw new InvalidDataException("快捷管控截止时间必须使用 UTC。");
        if (!updateControlExpiry && controlExpiresUtc is not null)
            throw new InvalidDataException("未请求更新课堂截止时间；没有创建任务。");
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var file = ReadFile();
            var session = file.Sessions.SingleOrDefault(item => item.SessionId == sessionId)
                          ?? throw new InvalidOperationException("课堂快捷管控状态已不存在；未创建任务。");
            if (!session.Active || session.ClosedUtc is not null)
                throw new InvalidOperationException("课堂已结束；未创建旧课堂快捷管控任务。");
            if (expectedControlExpiresUtc is { } expected && session.ControlExpiresUtc != expected)
                throw new InvalidOperationException("快捷管控截止时间已变化；已跳过旧截止任务。");
            var now = DateTimeOffset.UtcNow;
            var originalSession = session;
            var currentDevices = session.Devices.ToDictionary(item => item.TargetId);
            if (targetIds.Any(targetId => !currentDevices.ContainsKey(targetId)))
                throw new InvalidOperationException("目标设备不属于当前持久化课堂；未创建任务。");
            var sessionsById = file.Sessions.ToDictionary(item => item.SessionId);
            var originalOtherSessions = file.Sessions.Where(item => item.SessionId != sessionId)
                .ToDictionary(item => item.SessionId);
            var targetHeads = BuildTargetHeads(sessionsById.Values);
            var nextById = new Dictionary<Guid, ClassroomQuickControlDeviceState>(targetIds.Count);
            foreach (var targetId in targetIds)
            {
                var previous = currentDevices[targetId];
                var priorDevices = sessionsById.Values.Where(item => item.SessionId != sessionId)
                    .SelectMany(item => item.Devices).Where(item => item.TargetId == targetId).ToArray();
                var currentTaskIsLatest = previous.TaskId is { } priorTaskId &&
                    targetHeads.TryGetValue(targetId, out var targetHead) &&
                    targetHead.SessionId == sessionId && targetHead.Device.TaskId == priorTaskId &&
                    targetHead.Device.DesiredRevision == previous.DesiredRevision;
                if (previous.DesiredAction == action &&
                    ClassroomQuickControlDesiredState.NeedsCatchUp(previous) &&
                    currentTaskIsLatest &&
                    !priorDevices.Any(HasTransferableState) &&
                    (!updateControlExpiry || session.ControlExpiresUtc == controlExpiresUtc))
                {
                    nextById.Add(targetId, previous);
                    continue;
                }
                previous = AdoptPriorOwnership(previous, priorDevices);
                var desiredRevision = NextTargetRevision(sessionsById.Values, targetId);
                if (desiredRevision == long.MaxValue || previous.StateRevision == long.MaxValue)
                    throw new InvalidDataException("快捷管控 Revision 已达到上限；未创建新任务。");
                var websiteOwned = previous.WebsiteRevision is not null || previous.PendingWebsiteRevision is not null;
                var pendingPhase = action switch
                {
                    ClassroomQuickControlAction.EnableWebsiteRestriction => ClassroomQuickControlPhase.WebsiteApplying,
                    ClassroomQuickControlAction.LockInput => ClassroomQuickControlPhase.InputLocking,
                    ClassroomQuickControlAction.Release or ClassroomQuickControlAction.ClearWebsiteRestrictions or
                        ClassroomQuickControlAction.UnlockInput => ClassroomQuickControlPhase.ReleasePending,
                    _ => previous.Phase
                };
                var pendingWebsitePhase = action switch
                {
                    ClassroomQuickControlAction.EnableWebsiteRestriction => ClassroomQuickControlPhase.WebsiteApplying,
                    ClassroomQuickControlAction.Release or ClassroomQuickControlAction.ClearWebsiteRestrictions
                        when websiteOwned => ClassroomQuickControlPhase.ReleasePending,
                    _ => previous.WebsitePhase
                };
                nextById.Add(targetId, previous with
                {
                    DesiredRevision = desiredRevision + 1,
                    StateRevision = checked(previous.StateRevision + 1),
                    TaskId = Guid.NewGuid(),
                    DesiredAction = action,
                    Phase = pendingPhase,
                    WebsitePhase = pendingWebsitePhase,
                    RetryFailureCount = 0,
                    RetryAfterUtc = null,
                    ChangeSource = source,
                    TriggerReason = reason,
                    UpdatedUtc = now,
                    Detail = action switch
                    {
                        ClassroomQuickControlAction.EnableWebsiteRestriction => "网站限制任务已排队，等待 Agent 签名状态确认。",
                        ClassroomQuickControlAction.LockInput => "键鼠锁定任务已排队，等待 Agent 签名状态确认。",
                        ClassroomQuickControlAction.Release => "恢复任务已排队；只有 Agent 签名读回确认后才标记为已恢复。",
                        ClassroomQuickControlAction.ClearWebsiteRestrictions => "网站恢复任务已排队，等待 Agent 签名状态确认。",
                        ClassroomQuickControlAction.UnlockInput => "键鼠恢复任务已排队，等待 Agent 签名状态确认。",
                        _ => previous.Detail
                    }
                });

                foreach (var other in sessionsById.Values.Where(item => item.SessionId != sessionId &&
                             item.Devices.Any(device => device.TargetId == targetId)).ToArray())
                {
                    var oldDevice = other.Devices.Single(item => item.TargetId == targetId);
                    if (!HasTransferableState(oldDevice)) continue;
                    if (other.StateRevision == long.MaxValue)
                        throw new InvalidDataException("课堂状态 Revision 已达到上限；旧设备任务未作废。");
                    if (oldDevice.StateRevision == long.MaxValue)
                        throw new InvalidDataException("设备状态 Revision 已达到上限；旧设备任务未作废。");
                    var superseded = oldDevice with
                    {
                        Mode = DeviceControlMode.None,
                        Phase = ClassroomQuickControlPhase.Conflict,
                        WebsiteRevision = null,
                        PendingWebsiteRevision = null,
                        InputLeaseId = null,
                        ExpiresUtc = null,
                        WebsiteExpiresUtc = null,
                        InputExpiresUtc = null,
                        WebsitePhase = ClassroomQuickControlPhase.Conflict,
                        DesiredAction = null,
                        TaskId = Guid.NewGuid(),
                        RetryFailureCount = 0,
                        RetryAfterUtc = null,
                        StateRevision = oldDevice.StateRevision + 1,
                        ChangeSource = "superseded-by-newer-device-task",
                        TriggerReason = $"设备任务由新课堂 Revision {desiredRevision + 1} 接管；旧课堂任务已栅栏，归属由当前任务重新核验。",
                        UpdatedUtc = now,
                        Detail = "旧课堂任务已被更新的设备期望状态接管；本记录不代表设备已恢复，当前状态由新任务核验。"
                    };
                    var updatedOther = other with
                    {
                        StateRevision = other.StateRevision + 1,
                        UpdatedUtc = now,
                        Devices = Array.AsReadOnly(other.Devices.Select(item =>
                            item.TargetId == targetId ? superseded : item).ToArray())
                    };
                    sessionsById[other.SessionId] = updatedOther;
                }
            }
            var expiryChanged = updateControlExpiry && session.ControlExpiresUtc != controlExpiresUtc;
            var devicesChanged = nextById.Any(pair => pair.Value != currentDevices[pair.Key]);
            if (!expiryChanged && !devicesChanged)
                return Array.AsReadOnly(targetIds.Select(targetId => nextById[targetId]).ToArray());
            if (session.StateRevision == long.MaxValue)
                throw new InvalidDataException("课堂状态 Revision 已达到上限；未创建任务。");
            var updated = session with
            {
                StateRevision = session.StateRevision + 1,
                UpdatedUtc = now,
                ControlExpiresUtc = updateControlExpiry ? controlExpiresUtc : session.ControlExpiresUtc,
                Devices = Array.AsReadOnly(session.Devices.Select(item =>
                    nextById.GetValueOrDefault(item.TargetId, item)).ToArray())
            };
            ValidateSession(updated);
            sessionsById[sessionId] = updated;
            var sessions = FitTrackedSessions(sessionsById.Values);
            if (sessions.Count(item => item.Active) > 1)
                throw new InvalidDataException("快捷管控仍有过多未完成课堂记录；请先核对待恢复项。");
            WriteFile(new StateFile(CurrentSchemaVersion, sessions));
            AppendStateChanges(originalSession, updated);
            foreach (var other in originalOtherSessions.Values)
                if (sessionsById.TryGetValue(other.SessionId, out var changed) && changed != other)
                    AppendStateChanges(other, changed);
            if (expiryChanged)
                AppendSessionChange(originalSession, updated, source, reason);
            foreach (var other in originalOtherSessions.Values)
                if (sessionsById.TryGetValue(other.SessionId, out var changed) && changed != other)
                    AppendSessionChange(other, changed, "superseded-by-newer-device-task",
                        "较新的课堂设备任务已接管策略归属并栅栏旧任务；旧记录不表示设备已恢复。");
            return Array.AsReadOnly(targetIds.Select(targetId => nextById[targetId]).ToArray());
        }
    }

    public void SetControlExpiry(Guid sessionId, DateTimeOffset? expiresUtc,
        string source = "teacher-command", string reason = "教师端调整课堂策略截止时间。")
    {
        if (sessionId == Guid.Empty || expiresUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new InvalidDataException("课堂截止时间无效。");
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var file = ReadFile();
            var session = file.Sessions.SingleOrDefault(item => item.SessionId == sessionId)
                          ?? throw new InvalidOperationException("课堂快捷管控状态已不存在。");
            if (!session.Active || session.ClosedUtc is not null)
                throw new InvalidOperationException("课堂已结束；不能更新课堂截止时间。");
            if (session.ControlExpiresUtc == expiresUtc) return;
            if (session.StateRevision == long.MaxValue)
                throw new InvalidDataException("课堂状态 Revision 已达到上限；截止时间未更新。");
            var updated = session with { ControlExpiresUtc = expiresUtc,
                StateRevision = session.StateRevision + 1, UpdatedUtc = DateTimeOffset.UtcNow };
            ValidateSession(updated);
            ReplaceSession(file, updated);
            AppendSessionChange(session, updated, source, reason);
        }
    }

    public bool TrySetControlExpiry(Guid sessionId, DateTimeOffset? expectedCurrent,
        DateTimeOffset? expiresUtc, string source = "automatic-deadline",
        string reason = "课堂策略截止时间已清除。")
    {
        if (sessionId == Guid.Empty ||
            expectedCurrent is { Offset: var expectedOffset } && expectedOffset != TimeSpan.Zero ||
            expiresUtc is { Offset: var expiryOffset } && expiryOffset != TimeSpan.Zero)
            throw new InvalidDataException("课堂截止时间无效。");
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var file = ReadFile();
            var session = file.Sessions.SingleOrDefault(item => item.SessionId == sessionId);
            if (session is null || !session.Active || session.ClosedUtc is not null ||
                session.ControlExpiresUtc != expectedCurrent) return false;
            if (session.StateRevision == long.MaxValue)
                throw new InvalidDataException("课堂状态 Revision 已达到上限；截止时间未更新。");
            var updated = session with { ControlExpiresUtc = expiresUtc,
                StateRevision = session.StateRevision + 1, UpdatedUtc = DateTimeOffset.UtcNow };
            ValidateSession(updated);
            ReplaceSession(file, updated);
            AppendSessionChange(session, updated, source, reason);
            return true;
        }
    }

    /// <summary>Closes a session once, fencing in-flight Apply results and creating only the needed release tasks.</summary>
    public ClassroomQuickControlSessionState CloseSession(Guid sessionId, DateTimeOffset closedUtc)
    {
        if (sessionId == Guid.Empty || closedUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("课堂关闭标识或时间无效。");
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var file = ReadFile();
            var session = file.Sessions.SingleOrDefault(item => item.SessionId == sessionId);
            if (session is null) throw new InvalidOperationException("课堂快捷管控状态已不存在。");
            if (session.ClosedUtc is not null) return session;
            if (session.StateRevision == long.MaxValue)
                throw new InvalidDataException("课堂状态 Revision 已达到上限；课堂关闭状态未覆盖旧记录。");
            var devices = session.Devices.Select(device =>
            {
                var ownsWebsite = device.WebsiteRevision is not null || device.PendingWebsiteRevision is not null;
                var ownsInput = device.InputLeaseId is not null;
                var applyInFlight = device.DesiredAction is ClassroomQuickControlAction.EnableWebsiteRestriction or
                    ClassroomQuickControlAction.LockInput ||
                    device.Phase is ClassroomQuickControlPhase.WebsiteApplying or ClassroomQuickControlPhase.Switching or
                        ClassroomQuickControlPhase.InputLocking or ClassroomQuickControlPhase.WaitingForAgent;
                if (ownsWebsite || ownsInput)
                {
                    var latestDesiredRevision = NextTargetRevision(file.Sessions, device.TargetId);
                    if (latestDesiredRevision == long.MaxValue || device.StateRevision == long.MaxValue)
                        throw new InvalidDataException("设备快捷管控 Revision 已达到上限；课堂关闭状态未覆盖旧记录。");
                    var task = Guid.NewGuid();
                    var now = closedUtc;
                    return device with
                    {
                        DesiredRevision = latestDesiredRevision + 1,
                        StateRevision = checked(device.StateRevision + 1),
                        TaskId = task,
                        DesiredAction = ClassroomQuickControlAction.Release,
                        RetryFailureCount = 0,
                        RetryAfterUtc = null,
                        Phase = ClassroomQuickControlPhase.ReleasePending,
                        WebsitePhase = ownsWebsite ? ClassroomQuickControlPhase.ReleasePending : device.WebsitePhase,
                        UpdatedUtc = now,
                        ChangeSource = "classroom-ended",
                        TriggerReason = "课堂结束；为已有归属创建一次恢复任务，后续重试沿用相同 Revision 和 TaskId。",
                        Detail = "课堂已结束；等待 Agent 上线后核对归属并恢复本工具创建的管控。"
                    };
                }
                if (applyInFlight)
                {
                    var latestDesiredRevision = NextTargetRevision(file.Sessions, device.TargetId);
                    if (latestDesiredRevision == long.MaxValue || device.StateRevision == long.MaxValue)
                        throw new InvalidDataException("设备快捷管控 Revision 已达到上限；课堂关闭状态未覆盖旧记录。");
                    return ClearUnownedState(device with
                    {
                        DesiredRevision = latestDesiredRevision + 1,
                        StateRevision = checked(device.StateRevision + 1),
                        TaskId = Guid.NewGuid(),
                        DesiredAction = null,
                        RetryFailureCount = 0,
                        RetryAfterUtc = null,
                        ChangeSource = "classroom-ended",
                        TriggerReason = "课堂结束；取消尚未落地且没有设备归属的旧 Apply 任务。"
                    }, "课堂已结束；已取消尚未落地的快捷管控任务。");
                }
                return device;
            }).ToArray();
            var ended = session with
            {
                Active = false,
                ClosedUtc = closedUtc,
                StateRevision = checked(session.StateRevision + 1),
                UpdatedUtc = closedUtc,
                Devices = Array.AsReadOnly(devices)
            };
            ValidateSession(ended);
            ReplaceSession(file, ended);
            AppendStateChanges(session, ended);
            AppendSessionChange(session, ended, "classroom-ended",
                "课堂结束；已封存期望状态并栅栏所有旧任务。");
            return ended;
        }
    }

    /// <summary>
    /// Pauses an active classroom without ending it. Fences in-flight work so a closing Teacher process
    /// cannot publish a late result; the same desired action is reconciled when the active class resumes.
    /// </summary>
    public ClassroomQuickControlSessionState PauseSession(Guid sessionId, DateTimeOffset pausedUtc,
        string source = "teacher-stop", string reason = "教师端进程关闭；保留当前课堂期望状态，重连后继续核对。")
    {
        if (sessionId == Guid.Empty || pausedUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("课堂暂停标识或时间无效。");
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var file = ReadFile();
            var session = file.Sessions.SingleOrDefault(item => item.SessionId == sessionId)
                          ?? throw new InvalidOperationException("课堂快捷管控状态已不存在。");
            if (session.ClosedUtc is not null || !session.Active) return session;
            if (session.StateRevision == long.MaxValue)
                throw new InvalidDataException("课堂状态 Revision 已达到上限；未暂停课堂。");

            var devices = session.Devices.Select(device =>
            {
                var action = device.DesiredAction ?? ClassroomQuickControlDesiredState.ResolveCatchUpAction(device);
                var ownsWebsite = device.WebsiteRevision is not null || device.PendingWebsiteRevision is not null;
                var ownsInput = device.InputLeaseId is not null;
                var inFlight = device.Phase is ClassroomQuickControlPhase.WebsiteApplying or
                    ClassroomQuickControlPhase.Switching or ClassroomQuickControlPhase.InputLocking or
                    ClassroomQuickControlPhase.ReleasePending or ClassroomQuickControlPhase.WaitingForAgent;
                var inputActionNeedsManualReview = action is ClassroomQuickControlAction.LockInput or
                    ClassroomQuickControlAction.UnlockInput;
                var websiteNeedsReadback = ownsWebsite && action != ClassroomQuickControlAction.Release;
                if (action is null && !inFlight && !websiteNeedsReadback && !ownsInput)
                {
                    if (device.StateRevision == long.MaxValue)
                        throw new InvalidDataException("设备状态 Revision 已达到上限；未暂停课堂。");
                    return device with
                    {
                        StateRevision = device.StateRevision + 1,
                        ChangeSource = source,
                        TriggerReason = reason,
                        UpdatedUtc = pausedUtc
                    };
                }
                var latestDesiredRevision = NextTargetRevision(file.Sessions, device.TargetId);
                if (latestDesiredRevision == long.MaxValue || device.StateRevision == long.MaxValue)
                    throw new InvalidDataException("设备快捷管控 Revision 已达到上限；未暂停课堂。");

                var inputLeaseNeedsManualReview = ownsInput || inputActionNeedsManualReview;
                var keepReleaseDesired = action == ClassroomQuickControlAction.Release &&
                    (ownsWebsite || ownsInput);
                return device with
                {
                    DesiredRevision = latestDesiredRevision + 1,
                    StateRevision = device.StateRevision + 1,
                    TaskId = Guid.NewGuid(),
                    DesiredAction = inputActionNeedsManualReview ? null : action,
                    RetryFailureCount = 0,
                    RetryAfterUtc = null,
                    ChangeSource = source,
                    TriggerReason = inputLeaseNeedsManualReview
                        ? "教师端进程关闭；不自动续期或重放旧 InputGuard 命令，重连后读取签名状态。"
                        : reason,
                    Phase = keepReleaseDesired ? ClassroomQuickControlPhase.ReleasePending :
                        inputLeaseNeedsManualReview || websiteNeedsReadback
                            ? ClassroomQuickControlPhase.Unknown : device.Phase,
                    WebsitePhase = keepReleaseDesired && ownsWebsite
                        ? ClassroomQuickControlPhase.ReleasePending
                        : websiteNeedsReadback ? ClassroomQuickControlPhase.Unknown : device.WebsitePhase,
                    UpdatedUtc = pausedUtc,
                    Detail = inputLeaseNeedsManualReview
                        ? ownsWebsite
                            ? "教师端已关闭；InputGuard 不会续期或重放，同时保留网站策略待读取。"
                            : "教师端已关闭；InputGuard 不会续期或重放，重连后须重新读取签名状态。"
                        : websiteNeedsReadback
                            ? "教师端已关闭；网站策略保留，重连后须重新读取 Agent 签名状态。"
                        : "教师端已关闭；未创建恢复任务，重连后按新的任务版本继续核对当前期望状态。"
                };
            }).ToArray();
            var paused = session with
            {
                Active = false,
                StateRevision = session.StateRevision + 1,
                UpdatedUtc = pausedUtc,
                Devices = Array.AsReadOnly(devices)
            };
            ValidateSession(paused);
            ReplaceSession(file, paused);
            AppendStateChanges(session, paused);
            AppendSessionChange(session, paused, source, reason);
            return paused;
        }
    }

    public ClassroomQuickControlDeviceState UpdateRetry(Guid sessionId, Guid targetId,
        long desiredRevision, Guid taskId, DateTimeOffset? nowUtc = null)
    {
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var file = ReadFile();
            var session = file.Sessions.SingleOrDefault(item => item.SessionId == sessionId)
                          ?? throw new InvalidOperationException("待同步课堂状态已不存在。");
            var previous = session.Devices.SingleOrDefault(item => item.TargetId == targetId)
                           ?? throw new InvalidOperationException("待同步设备状态已不存在。");
            if (previous.DesiredRevision != desiredRevision || previous.TaskId != taskId)
                return previous;
            var failures = Math.Min(previous.RetryFailureCount + 1, 7);
            var seconds = Math.Min(60 * (1 << Math.Max(0, failures - 1)), 1800);
            var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            var next = previous with
            {
                StateRevision = checked(previous.StateRevision + 1),
                RetryFailureCount = failures,
                RetryAfterUtc = now.AddSeconds(seconds),
                UpdatedUtc = now,
                ChangeSource = "retry-backoff",
                TriggerReason = $"设备暂未取得可信状态；第 {failures} 次退避，{seconds} 秒后再检查同一任务。"
            };
            if (session.StateRevision == long.MaxValue)
                throw new InvalidDataException("课堂状态 Revision 已达到上限；待同步时间未更新。");
            var updated = session with { StateRevision = session.StateRevision + 1, UpdatedUtc = now,
                Devices = Array.AsReadOnly(session.Devices.Select(item =>
                    item.TargetId == targetId ? next : item).ToArray()) };
            ValidateSession(updated);
            ReplaceSession(file, updated);
            AppendStateChanges(session, updated);
            return next;
        }
    }

    public ClassroomQuickControlDeviceState ClearRetry(Guid sessionId, Guid targetId,
        long desiredRevision, Guid taskId)
    {
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var file = ReadFile();
            var session = file.Sessions.SingleOrDefault(item => item.SessionId == sessionId)
                          ?? throw new InvalidOperationException("课堂状态已不存在。");
            var previous = session.Devices.SingleOrDefault(item => item.TargetId == targetId)
                           ?? throw new InvalidOperationException("设备状态已不存在。");
            if (previous.DesiredRevision != desiredRevision || previous.TaskId != taskId ||
                previous.RetryFailureCount == 0 && previous.RetryAfterUtc is null)
                return previous;
            var next = previous with { StateRevision = checked(previous.StateRevision + 1),
                RetryFailureCount = 0, RetryAfterUtc = null,
                ChangeSource = "retry-complete", TriggerReason = "可信回执或新状态已确认；清除待同步退避。" };
            if (session.StateRevision == long.MaxValue)
                throw new InvalidDataException("课堂状态 Revision 已达到上限；待同步状态未更新。");
            var updated = session with { StateRevision = session.StateRevision + 1,
                UpdatedUtc = DateTimeOffset.UtcNow, Devices = Array.AsReadOnly(session.Devices.Select(item =>
                    item.TargetId == targetId ? next : item).ToArray()) };
            ReplaceSession(file, updated);
            AppendStateChanges(session, updated);
            return next;
        }
    }

    public void Remove(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂 ID 无效。");
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var current = ReadFile();
            var sessions = current.Sessions.Where(item => item.SessionId != sessionId).ToArray();
            if (sessions.Length != current.Sessions.Count)
                WriteFile(new StateFile(CurrentSchemaVersion, sessions));
        }
    }

    private StateFile ReadFile()
    {
        if (!File.Exists(_path)) return new StateFile(CurrentSchemaVersion, []);
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("快捷管控本机记录大小无效。");
        try
        {
            var bytes = File.ReadAllBytes(_path);
            PolicyJson.RejectDuplicateFields(bytes);
            var file = JsonSerializer.Deserialize<StateFile>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("快捷管控本机记录为空。");
            if (file.SchemaVersion is 1 or 2 or 3 or 4 or 5) file = UpgradeLegacySchema(file);
            ValidateFile(file);
            return file;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("快捷管控本机记录格式无效；原文件已保留。", exception);
        }
    }

    private void WriteFile(StateFile file)
    {
        ValidateFile(file);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
        if (bytes.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("快捷管控本机记录超过 4 MiB 限制。");
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("快捷管控本机记录路径无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        if (File.Exists(_path)) PathLinkSecurity.RejectLinks(_path);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static StateFile UpgradeLegacySchema(StateFile file)
    {
        if (file.SchemaVersion is not (1 or 2 or 3 or 4 or 5) || file.Sessions is null ||
            file.Sessions.Count > MaximumTrackedSessions ||
            file.Sessions.Select(item => item?.SessionId).Distinct().Count() != file.Sessions.Count ||
            file.Sessions.Any(item => item is null || item.SchemaVersion != file.SchemaVersion ||
                item.Devices is null || item.Devices.Any(device => device is null)))
            throw new InvalidDataException("旧版快捷管控记录版本无效。");
        var migratedSessions = file.Sessions.Select(session => session with
        {
            SchemaVersion = CurrentSchemaVersion,
            StateRevision = file.SchemaVersion >= 3 ? session.StateRevision : 0,
            Devices = Array.AsReadOnly(session.Devices.Select(device => device with
            {
                Phase = NeedsLegacyWebsiteReadback(file.SchemaVersion, device)
                    ? ClassroomQuickControlPhase.Unknown
                    : file.SchemaVersion == 1 && device.Mode == DeviceControlMode.None &&
                        device.WebsiteRevision is null && device.PendingWebsiteRevision is null &&
                        device.InputLeaseId is null && device.Phase == ClassroomQuickControlPhase.Unknown &&
                        device.Detail.StartsWith("尚未读取本堂课的快捷管控状态", StringComparison.Ordinal)
                    ? ClassroomQuickControlPhase.NotStarted : device.Phase,
                Detail = NeedsLegacyWebsiteReadback(file.SchemaVersion, device)
                    ? "旧版课堂状态已迁移；必须重新读取 Agent 签名策略版本后确认，迁移记录不代表策略仍生效。"
                    : file.SchemaVersion == 1 && device.Mode == DeviceControlMode.None &&
                         device.WebsiteRevision is null && device.PendingWebsiteRevision is null &&
                         device.InputLeaseId is null && device.Phase == ClassroomQuickControlPhase.Unknown &&
                         device.Detail.StartsWith("尚未读取本堂课的快捷管控状态", StringComparison.Ordinal)
                    ? "尚未执行网站限制或键鼠锁定操作；这不表示 Agent 离线或策略不可用。"
                    : device.Detail,
                WebsiteExpiresUtc = file.SchemaVersion == 1 &&
                                    (device.Mode == DeviceControlMode.WebsiteRestriction ||
                                     device.PendingWebsiteRevision is not null && device.InputLeaseId is null)
                    ? device.ExpiresUtc : device.WebsiteExpiresUtc,
                InputExpiresUtc = file.SchemaVersion == 1 && device.InputLeaseId is not null
                    ? device.ExpiresUtc : device.InputExpiresUtc,
                WebsitePhase = NeedsLegacyWebsiteReadback(file.SchemaVersion, device)
                    ? ClassroomQuickControlPhase.Unknown : device.WebsitePhase,
                DesiredAction = file.SchemaVersion >= 4 && device.DesiredAction is not null
                    ? device.DesiredAction : LegacyDesiredAction(device),
                DesiredRevision = file.SchemaVersion >= 4 || device.DesiredRevision > 0 ? device.DesiredRevision :
                    LegacyDesiredAction(device) is null ? 0 : 1,
                TaskId = file.SchemaVersion >= 4 && device.TaskId is not null
                    ? device.TaskId : device.TaskId ?? (LegacyDesiredAction(device) is null ? null : Guid.NewGuid()),
                StateRevision = file.SchemaVersion >= 5 ? device.StateRevision : 0,
                ChangeSource = file.SchemaVersion >= 4 ? device.ChangeSource : "schema-migration",
                TriggerReason = file.SchemaVersion >= 4 ? device.TriggerReason :
                    NeedsLegacyWebsiteReadback(file.SchemaVersion, device)
                        ? "旧版设备策略状态来源不可信；等待 Agent 签名读回。"
                        : "旧版课堂快捷管控记录迁移；不明任务不会自动重放。"
            }).ToArray())
        }).ToArray();
        var targetRevisions = new Dictionary<Guid, long>();
        var normalized = new Dictionary<Guid, ClassroomQuickControlSessionState>();
        foreach (var session in migratedSessions.OrderBy(item => item.Active)
                     .ThenBy(item => item.ClosedUtc ?? item.UpdatedUtc)
                     .ThenBy(item => item.StateRevision))
        {
            var devices = session.Devices.Select(device =>
            {
                if (device.TaskId is null && device.DesiredRevision == 0) return device;
                var revision = targetRevisions.GetValueOrDefault(device.TargetId);
                if (revision == long.MaxValue)
                    throw new InvalidDataException("旧版快捷管控记录的设备 Revision 已达到上限。");
                revision++;
                targetRevisions[device.TargetId] = revision;
                return device with { DesiredRevision = revision };
            }).ToArray();
            normalized.Add(session.SessionId, session with { Devices = Array.AsReadOnly(devices) });
        }
        var sessions = migratedSessions.Select(item => normalized[item.SessionId]).ToArray();
        return new StateFile(CurrentSchemaVersion, Array.AsReadOnly(sessions));
    }

    private static bool NeedsLegacyWebsiteReadback(int schemaVersion, ClassroomQuickControlDeviceState device) =>
        schemaVersion < CurrentSchemaVersion &&
        (device.WebsiteRevision is not null || device.PendingWebsiteRevision is not null) &&
        device.Phase is not (ClassroomQuickControlPhase.Conflict or ClassroomQuickControlPhase.Unsupported or
            ClassroomQuickControlPhase.ReleasePending);

    private static ClassroomQuickControlAction? LegacyDesiredAction(ClassroomQuickControlDeviceState device) =>
        device.DesiredAction ?? (device.Phase == ClassroomQuickControlPhase.ReleasePending
            ? ClassroomQuickControlAction.Release
            : device.Phase is ClassroomQuickControlPhase.WaitingForAgent or
                ClassroomQuickControlPhase.WebsiteApplying or ClassroomQuickControlPhase.Switching ||
                (device.WebsiteRevision is not null || device.PendingWebsiteRevision is not null) &&
                device.Phase is not (ClassroomQuickControlPhase.Conflict or ClassroomQuickControlPhase.Unsupported)
                ? ClassroomQuickControlAction.EnableWebsiteRestriction : null);

    private static ClassroomQuickControlSessionState MergeWithoutRegression(
        ClassroomQuickControlSessionState current, ClassroomQuickControlSessionState incoming,
        IReadOnlyDictionary<Guid, (Guid SessionId, ClassroomQuickControlDeviceState Device)> targetHeads)
    {
        var devices = current.Devices.ToDictionary(item => item.TargetId);
        foreach (var candidate in incoming.Devices)
        {
            if (candidate.DesiredRevision > 0 && targetHeads.TryGetValue(candidate.TargetId, out var targetHead) &&
                (targetHead.SessionId != current.SessionId ||
                 targetHead.Device.DesiredRevision != candidate.DesiredRevision ||
                 targetHead.Device.TaskId != candidate.TaskId))
                continue;
            if (!devices.TryGetValue(candidate.TargetId, out var existing))
            {
                devices[candidate.TargetId] = candidate with { StateRevision = Math.Max(1, candidate.StateRevision) };
                continue;
            }

            if (candidate == existing) continue;
            var hasNewOperation = candidate.DesiredRevision > existing.DesiredRevision;
            var isSameOperation = candidate.DesiredRevision == existing.DesiredRevision &&
                candidate.TaskId == existing.TaskId && candidate.UpdatedUtc >= existing.UpdatedUtc;
            var candidateIsBasedOnCurrentState = candidate.StateRevision == existing.StateRevision;
            var canAdvance = candidate.ChangeSource == "agent-status-poll"
                ? isSameOperation && candidateIsBasedOnCurrentState
                : (hasNewOperation || isSameOperation) && candidateIsBasedOnCurrentState;
            if (!canAdvance) continue;
            if (existing.StateRevision == long.MaxValue)
                throw new InvalidDataException("设备快捷管控状态 Revision 已达到上限；状态未覆盖旧记录。");
            devices[candidate.TargetId] = candidate with { StateRevision = existing.StateRevision + 1 };
        }
        var metadata = incoming.StateRevision >= current.StateRevision ? incoming : current;
        var closedUtc = current.ClosedUtc ?? incoming.ClosedUtc;
        return metadata with
        {
            SchemaVersion = CurrentSchemaVersion,
            StateRevision = Math.Max(current.StateRevision, incoming.StateRevision),
            Active = closedUtc is null && metadata.Active,
            ClosedUtc = closedUtc,
            Devices = Array.AsReadOnly(devices.Values.OrderBy(item => item.TargetId).ToArray())
        };
    }

    private static Dictionary<Guid, (Guid SessionId, ClassroomQuickControlDeviceState Device)> BuildTargetHeads(
        IEnumerable<ClassroomQuickControlSessionState> sessions) => sessions
        .SelectMany(session => session.Devices.Where(device => device.DesiredRevision > 0)
            .Select(device => (session.SessionId, session.Active, Device: device)))
        .GroupBy(item => item.Device.TargetId)
        .ToDictionary(group => group.Key, group =>
        {
            var head = group.OrderByDescending(item => item.Device.DesiredRevision)
                .ThenByDescending(item => item.Active).First();
            return (head.SessionId, head.Device);
        });

    private static long NextTargetRevision(IEnumerable<ClassroomQuickControlSessionState> sessions,
        Guid targetId) => sessions.SelectMany(item => item.Devices)
        .Where(item => item.TargetId == targetId)
        .Select(item => item.DesiredRevision).DefaultIfEmpty(0).Max();

    private static ClassroomQuickControlDeviceState AdoptPriorOwnership(
        ClassroomQuickControlDeviceState current, IEnumerable<ClassroomQuickControlDeviceState> priorStates)
    {
        var prior = priorStates.Where(HasTransferableState)
            .OrderByDescending(item => item.DesiredRevision)
            .ThenByDescending(item => item.UpdatedUtc).ToArray();
        if (prior.Length == 0) return current;
        var website = current.WebsiteRevision is not null || current.PendingWebsiteRevision is not null
            ? current : prior.FirstOrDefault(item => item.WebsiteRevision is not null ||
                item.PendingWebsiteRevision is not null);
        var input = current.InputLeaseId is not null
            ? current : prior.FirstOrDefault(item => item.InputLeaseId is not null);
        var websiteRevision = current.WebsiteRevision ?? website?.WebsiteRevision;
        var pendingWebsiteRevision = current.PendingWebsiteRevision ?? website?.PendingWebsiteRevision;
        var inputLeaseId = current.InputLeaseId ?? input?.InputLeaseId;
        var websiteExpires = current.WebsiteExpiresUtc ?? website?.WebsiteExpiresUtc;
        var inputExpires = current.InputExpiresUtc ?? input?.InputExpiresUtc;
        var ownsWebsite = websiteRevision is not null || pendingWebsiteRevision is not null;
        var ownsInput = inputLeaseId is not null;
        return current with
        {
            Mode = ToMode(ownsWebsite, ownsInput),
            StateRevision = Math.Max(current.StateRevision, prior.Max(item => item.StateRevision)),
            WebsiteRevision = websiteRevision,
            PendingWebsiteRevision = pendingWebsiteRevision,
            InputLeaseId = inputLeaseId,
            WebsiteExpiresUtc = websiteExpires,
            InputExpiresUtc = inputExpires,
            ExpiresUtc = inputExpires ?? websiteExpires ?? current.ExpiresUtc,
            WebsitePhase = current.WebsitePhase ?? website?.WebsitePhase
        };
    }

    private static bool HasTransferableState(ClassroomQuickControlDeviceState device) =>
        device.ChangeSource != "superseded-by-newer-device-task" &&
        (device.DesiredAction is not null ||
        device.Mode != DeviceControlMode.None || device.WebsiteRevision is not null ||
        device.PendingWebsiteRevision is not null || device.InputLeaseId is not null ||
        device.Phase is ClassroomQuickControlPhase.WaitingForAgent or ClassroomQuickControlPhase.WebsiteApplying or
            ClassroomQuickControlPhase.Switching or ClassroomQuickControlPhase.InputLocking or
            ClassroomQuickControlPhase.ReleasePending);

    private static DeviceControlMode ToMode(bool websiteOwned, bool inputOwned) =>
        (websiteOwned, inputOwned) switch
        {
            (true, true) => DeviceControlMode.WebsiteAndInputLocked,
            (true, false) => DeviceControlMode.WebsiteRestriction,
            (false, true) => DeviceControlMode.InputLocked,
            _ => DeviceControlMode.None
        };

    private static bool SessionContentChanged(ClassroomQuickControlSessionState previous,
        ClassroomQuickControlSessionState next) => previous.Active != next.Active ||
        previous.CampusId != next.CampusId || previous.RoomId != next.RoomId ||
        previous.ControlExpiresUtc != next.ControlExpiresUtc || previous.ClosedUtc != next.ClosedUtc ||
        previous.UpdatedUtc != next.UpdatedUtc || !previous.Devices.SequenceEqual(next.Devices);

    private static bool SessionMetadataChanged(ClassroomQuickControlSessionState previous,
        ClassroomQuickControlSessionState next) => previous.Active != next.Active ||
        previous.CampusId != next.CampusId || previous.RoomId != next.RoomId ||
        previous.ControlExpiresUtc != next.ControlExpiresUtc || previous.ClosedUtc != next.ClosedUtc;

    private void ReplaceSession(StateFile file, ClassroomQuickControlSessionState state)
    {
        var sessions = FitTrackedSessions(file.Sessions.Where(item => item.SessionId != state.SessionId).Append(state));
        if (sessions.Count(item => item.Active) > 1)
            throw new InvalidDataException("快捷管控仍有过多未完成课堂记录；请先核对待恢复项。");
        WriteFile(new StateFile(CurrentSchemaVersion, sessions));
    }

    private static ClassroomQuickControlSessionState[] FitTrackedSessions(
        IEnumerable<ClassroomQuickControlSessionState> source)
    {
        var sessions = source.Where(item => item.Active || item.ClosedUtc is not null ||
                item.Devices.Any(device => device.Mode != DeviceControlMode.None ||
                    device.Phase is not (ClassroomQuickControlPhase.NoneVerified or
                        ClassroomQuickControlPhase.NotStarted)))
            .OrderBy(item => item.UpdatedUtc).ToList();
        if (sessions.Count > MaximumTrackedSessions)
        {
            foreach (var ended in sessions.Where(CanPruneEndedSession)
                         .OrderBy(item => item.ClosedUtc).ToArray())
            {
                if (sessions.Count <= MaximumTrackedSessions) break;
                sessions.Remove(ended);
            }
        }
        if (sessions.Count > MaximumTrackedSessions)
            throw new InvalidDataException("快捷管控仍有过多未完成课堂记录；请先核对待恢复项。");
        return sessions.ToArray();
    }

    private static bool CanPruneEndedSession(ClassroomQuickControlSessionState session) =>
        !session.Active && session.ClosedUtc is not null && session.Devices.All(device =>
            device.Mode == DeviceControlMode.None && device.WebsiteRevision is null &&
            device.PendingWebsiteRevision is null && device.InputLeaseId is null &&
            (device.Phase is ClassroomQuickControlPhase.NoneVerified or ClassroomQuickControlPhase.NotStarted ||
             device.ChangeSource == "superseded-by-newer-device-task" &&
             device.Phase == ClassroomQuickControlPhase.Conflict) &&
            device.DesiredAction is null &&
            !ClassroomQuickControlDesiredState.NeedsCatchUp(device));

    private void AppendStateChanges(ClassroomQuickControlSessionState? previous,
        ClassroomQuickControlSessionState next)
    {
        var oldDevices = previous?.Devices.ToDictionary(item => item.TargetId) ?? [];
        foreach (var device in next.Devices)
        {
            oldDevices.TryGetValue(device.TargetId, out var before);
            if (before == device) continue;
            AppendChange(new ClassroomQuickControlStateChange(DateTimeOffset.UtcNow, next.SessionId,
                device.TargetId, device.TaskId, device.DesiredRevision,
                device.WebsiteRevision ?? device.PendingWebsiteRevision,
                Describe(before), Describe(device), device.ChangeSource,
                string.IsNullOrWhiteSpace(device.TriggerReason) ? device.Detail : device.TriggerReason, true)
            { StateRevision = next.StateRevision, DeviceStateRevision = device.StateRevision });
        }
    }

    private void AppendSessionChange(ClassroomQuickControlSessionState previous,
        ClassroomQuickControlSessionState next, string source, string reason)
    {
        AppendChange(new ClassroomQuickControlStateChange(DateTimeOffset.UtcNow, next.SessionId,
            Guid.Empty, null, 0, null, DescribeSession(previous), DescribeSession(next), source, reason, true)
        { StateRevision = next.StateRevision });
    }

    private static string DescribeSession(ClassroomQuickControlSessionState state) =>
        $"session/active={state.Active}/closed={state.ClosedUtc?.ToString("O") ?? "none"}" +
        $"/deadline={state.ControlExpiresUtc?.ToString("O") ?? "none"}" +
        $"/campus={state.CampusId}/room={state.RoomId:D}/stateRevision={state.StateRevision}";

    private void AppendRejectedStaleChanges(ClassroomQuickControlSessionState current,
        ClassroomQuickControlSessionState attempted,
        IReadOnlyDictionary<Guid, (Guid SessionId, ClassroomQuickControlDeviceState Device)> targetHeads)
    {
        var currentDevices = current.Devices.ToDictionary(item => item.TargetId);
        foreach (var candidate in attempted.Devices)
        {
            if (!currentDevices.TryGetValue(candidate.TargetId, out var existing) || candidate == existing) continue;
            var targetHeadMatches = candidate.DesiredRevision == 0 ||
                !targetHeads.TryGetValue(candidate.TargetId, out var targetHead) ||
                targetHead.SessionId == current.SessionId &&
                targetHead.Device.DesiredRevision == candidate.DesiredRevision &&
                targetHead.Device.TaskId == candidate.TaskId;
            var isSameOperation = candidate.DesiredRevision == existing.DesiredRevision &&
                candidate.TaskId == existing.TaskId && candidate.UpdatedUtc >= existing.UpdatedUtc;
            var candidateWouldBeAccepted = targetHeadMatches && candidate.StateRevision == existing.StateRevision &&
                (candidate.ChangeSource == "agent-status-poll"
                    ? isSameOperation
                    : candidate.DesiredRevision > existing.DesiredRevision || isSameOperation);
            if (candidateWouldBeAccepted) continue;
            AppendChange(new ClassroomQuickControlStateChange(DateTimeOffset.UtcNow, current.SessionId,
                candidate.TargetId, candidate.TaskId, candidate.DesiredRevision,
                candidate.WebsiteRevision ?? candidate.PendingWebsiteRevision,
                Describe(existing), Describe(candidate), "stale-task-rejected",
                string.IsNullOrWhiteSpace(candidate.TriggerReason) ? candidate.Detail : candidate.TriggerReason,
                false) { StateRevision = current.StateRevision, DeviceStateRevision = existing.StateRevision });
        }
    }

    private void AppendChange(ClassroomQuickControlStateChange change)
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("快捷管控记录路径无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        var logPath = _path + ".events.jsonl";
        var lockPath = logPath + ".lock";
        using var logLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.WriteThrough);
        if (File.Exists(logPath)) PathLinkSecurity.RejectLinks(logPath);
        if (File.Exists(logPath) && new FileInfo(logPath).Length > MaximumChangeLogBytes)
        {
            var bytes = File.ReadAllBytes(logPath);
            var start = Math.Max(0, bytes.Length - (int)(MaximumChangeLogBytes / 2));
            while (start < bytes.Length && bytes[start] != (byte)'\n') start++;
            if (start < bytes.Length) start++;
            File.WriteAllBytes(logPath, bytes[start..]);
        }
        var line = JsonSerializer.Serialize(change, ChangeLogJsonOptions) + "\n";
        var payload = System.Text.Encoding.UTF8.GetBytes(line);
        using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    private static string Describe(ClassroomQuickControlDeviceState? state) => state is null ? "(none)" :
        $"{state.Mode}/{state.Phase}/desired={state.DesiredAction?.ToString() ?? "none"}" +
        $"/desiredRevision={state.DesiredRevision}/policyRevision={state.WebsiteRevision ?? state.PendingWebsiteRevision}" +
        $"/task={state.TaskId?.ToString("D") ?? "none"}/deviceStateRevision={state.StateRevision}";

    private static ClassroomQuickControlDeviceState ClearUnownedState(ClassroomQuickControlDeviceState device,
        string detail) => device with
    {
        Mode = DeviceControlMode.None,
        Phase = ClassroomQuickControlPhase.NoneVerified,
        WebsiteRevision = null,
        PendingWebsiteRevision = null,
        InputLeaseId = null,
        ExpiresUtc = null,
        WebsiteExpiresUtc = null,
        InputExpiresUtc = null,
        WebsitePhase = ClassroomQuickControlPhase.NoneVerified,
        UpdatedUtc = DateTimeOffset.UtcNow,
        Detail = detail
    };

    private FileStream AcquireFileLock()
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("快捷管控本机记录路径无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        var lockPath = _path + ".lock";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        IOException? lastFailure = null;
        do
        {
            try
            {
                if (File.Exists(lockPath)) PathLinkSecurity.RejectLinks(lockPath);
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException exception)
            {
                lastFailure = exception;
                Thread.Sleep(25);
            }
        } while (timer.Elapsed < LockTimeout);
        throw new IOException("另一 TeacherConsole 实例正在更新快捷管控记录，请稍后重试。", lastFailure);
    }

    private static void ValidateFile(StateFile? file)
    {
        if (file is null || file.SchemaVersion != CurrentSchemaVersion || file.Sessions is null ||
            file.Sessions.Count > MaximumTrackedSessions)
            throw new InvalidDataException("快捷管控记录版本或数量无效。");
        var ids = new HashSet<Guid>();
        foreach (var session in file.Sessions)
        {
            ValidateSession(session);
            if (!ids.Add(session.SessionId))
                throw new InvalidDataException("快捷管控记录包含重复课堂 ID。");
        }
        if (file.Sessions.Count(item => item.Active) > 1)
            throw new InvalidDataException("快捷管控记录包含多个活动课堂。");
    }

    private static void ValidateSession(ClassroomQuickControlSessionState? state)
    {
        if (state is null || state.SchemaVersion != CurrentSchemaVersion || state.SessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(state.CampusId) || state.CampusId.Length > 128 ||
            state.CampusId != state.CampusId.Trim() || state.CampusId.Any(char.IsControl) ||
            state.RoomId == Guid.Empty || state.StateRevision < 0 || state.UpdatedUtc.Offset != TimeSpan.Zero ||
            state.ClosedUtc is { Offset: var closedOffset } && closedOffset != TimeSpan.Zero ||
            state.Active && state.ClosedUtc is not null || state.Devices is null ||
            state.ControlExpiresUtc is { } controlExpiry &&
                (controlExpiry.Offset != TimeSpan.Zero ||
                 controlExpiry - state.UpdatedUtc > WebsitePolicyCompiler.MaximumPolicyLifetime) ||
            state.Devices.Count > MaximumDevicesPerSession || state.Devices.Any(device => device is null) ||
            state.Devices.Select(device => device.TargetId).Distinct().Count() != state.Devices.Count)
            throw new InvalidDataException("快捷管控课堂记录字段无效。");
        foreach (var device in state.Devices)
        {
            var websiteOwned = device.WebsiteRevision is not null || device.PendingWebsiteRevision is not null;
            var inputOwned = device.InputLeaseId is not null;
            if (device.TargetId == Guid.Empty || !Enum.IsDefined(device.Mode) || !Enum.IsDefined(device.Phase) ||
                device.DesiredRevision < 0 || device.StateRevision < 0 ||
                device.DesiredRevision > 0 && device.TaskId is null ||
                device.TaskId == Guid.Empty || device.DesiredAction is { } desiredAction && !Enum.IsDefined(desiredAction) ||
                device.RetryFailureCount is < 0 or > 7 ||
                device.RetryAfterUtc is { Offset: var retryOffset } && retryOffset != TimeSpan.Zero ||
                (device.RetryFailureCount == 0) != (device.RetryAfterUtc is null) ||
                string.IsNullOrWhiteSpace(device.ChangeSource) || device.ChangeSource.Length > 64 ||
                device.ChangeSource.Any(char.IsControl) || device.TriggerReason is null ||
                device.TriggerReason.Length > 512 || device.TriggerReason.Any(char.IsControl) ||
                device.WebsitePhase is { } websitePhase && !Enum.IsDefined(websitePhase) ||
                device.UpdatedUtc.Offset != TimeSpan.Zero || device.Detail is null || device.Detail.Length > 512 ||
                device.Detail.Any(char.IsControl) || device.WebsiteRevision is < 1 ||
                device.PendingWebsiteRevision is < 1 ||
                device.InputLeaseId == Guid.Empty ||
                device.Mode == DeviceControlMode.WebsiteRestriction && (!websiteOwned || inputOwned) ||
                device.Mode == DeviceControlMode.InputLocked && (!inputOwned || websiteOwned) ||
                device.Mode == DeviceControlMode.WebsiteAndInputLocked && (!websiteOwned || !inputOwned) ||
                device.Mode == DeviceControlMode.None && (device.WebsiteRevision is not null || inputOwned) ||
                device.ExpiresUtc is { Offset: var expiryOffset } && expiryOffset != TimeSpan.Zero ||
                device.WebsiteExpiresUtc is { Offset: var websiteExpiryOffset } && websiteExpiryOffset != TimeSpan.Zero ||
                device.InputExpiresUtc is { Offset: var inputExpiryOffset } && inputExpiryOffset != TimeSpan.Zero ||
                websiteOwned && device.WebsiteExpiresUtc is null && device.ExpiresUtc is null &&
                    device.Phase != ClassroomQuickControlPhase.WaitingForAgent ||
                inputOwned && device.InputExpiresUtc is null && device.ExpiresUtc is null)
                throw new InvalidDataException("快捷管控设备所有权字段无效。");
        }
    }
}
