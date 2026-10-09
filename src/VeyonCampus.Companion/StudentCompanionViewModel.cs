using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.Companion;

public enum StudentCompanionConnectionState
{
    Disconnected,
    ConnectedWithoutClass,
    ClassroomActive
}

public sealed record StudentCompanionStatusSnapshot(
    StudentCompanionConnectionState ConnectionState,
    string? RoomName = null,
    int TargetCount = 0,
    Guid? SessionId = null,
    ClassroomMode? Mode = null);

/// <summary>Maps a small, read-only classroom status snapshot to student-facing text.</summary>
public sealed class StudentCompanionViewModel : INotifyPropertyChanged
{
    private readonly Func<Guid, CancellationToken, Task<StudentAgentClassroomEventSubmission>>? _requestHelp;
    private readonly Func<Guid, Guid, CancellationToken, Task<StudentAgentClassroomEventSubmission>>? _resolveHelp;
    private StudentCompanionStatusSnapshot _snapshot =
        new(StudentCompanionConnectionState.Disconnected);
    private bool _isSubmittingHelp;
    private bool _isResolvingHelp;
    private Guid? _pendingHelpEventId;
    private bool _hasTeacherReply;
    private string _helpStatus = "";
    private string _classroomNoticeMessage = "";
    private Guid? _lastActiveSessionId;
    private readonly Queue<Guid> _handledTeacherEventOrder = new();
    private readonly HashSet<Guid> _handledTeacherEventIds = [];
    private readonly Dictionary<Guid, string> _earlyTeacherReplies = [];

    public StudentCompanionViewModel(
        Func<Guid, CancellationToken, Task<StudentAgentClassroomEventSubmission>>? requestHelp = null,
        Func<Guid, Guid, CancellationToken, Task<StudentAgentClassroomEventSubmission>>? resolveHelp = null)
    {
        _requestHelp = requestHelp;
        _resolveHelp = resolveHelp;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusTitle => _snapshot.ConnectionState switch
    {
        StudentCompanionConnectionState.Disconnected => "等待教师端连接",
        StudentCompanionConnectionState.ConnectedWithoutClass => "当前没有进行中的课堂",
        StudentCompanionConnectionState.ClassroomActive => "课堂进行中",
        _ => "课堂状态未知"
    };

    public string StatusDescription => _snapshot.ConnectionState switch
    {
        StudentCompanionConnectionState.Disconnected => "教师开始课堂后，这里会自动显示课堂状态。",
        StudentCompanionConnectionState.ConnectedWithoutClass => "教师开始课堂后，教室状态会自动显示。",
        StudentCompanionConnectionState.ClassroomActive => "你已连接到当前课堂。",
        _ => "暂时无法确认课堂状态。"
    };

    public string ClassroomSummary => _snapshot.ConnectionState switch
    {
        StudentCompanionConnectionState.ClassroomActive =>
            $"{_snapshot.RoomName} · {_snapshot.TargetCount} 台电脑" +
            (_snapshot.Mode is null ? "" : _snapshot.Mode == ClassroomMode.Practice ? " · 练习" : " · 正常课堂"),
        StudentCompanionConnectionState.ConnectedWithoutClass => "教师端已连接",
        _ => "未连接"
    };

    public bool HasClassroomSummary =>
        _snapshot.ConnectionState is StudentCompanionConnectionState.ConnectedWithoutClass or
            StudentCompanionConnectionState.ClassroomActive;
    public bool IsClassroomActive => _snapshot.ConnectionState == StudentCompanionConnectionState.ClassroomActive;
    public bool CanRequestHelp => IsClassroomActive && !_isSubmittingHelp && !_isResolvingHelp &&
                                  _pendingHelpEventId is null &&
                                  _requestHelp is not null;
    public bool CanResolveHelp => IsClassroomActive && !_isSubmittingHelp && !_isResolvingHelp &&
                                  _pendingHelpEventId is not null && _hasTeacherReply && _resolveHelp is not null;
    public bool HasHelpAction => CanRequestHelp || CanResolveHelp || _isSubmittingHelp || _isResolvingHelp;
    public bool CanActivateHelpAction => CanRequestHelp || CanResolveHelp;
    public string HelpActionText => _isSubmittingHelp ? "正在发送……" : _isResolvingHelp ? "正在确认……" :
        CanResolveHelp ? "标记已解决" : "需要老师帮助";
    public string HelpStatus => _classroomNoticeMessage.Length == 0
        ? _helpStatus
        : _helpStatus.Length == 0
            ? "课堂通知：" + _classroomNoticeMessage
            : _helpStatus + "\n课堂通知：" + _classroomNoticeMessage;
    public bool HasHelpStatus => HelpStatus.Length > 0;

    public void ApplyStatus(StudentCompanionStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        if (_snapshot == snapshot) return;
        var sessionChanged = snapshot.SessionId is { } newSessionId &&
                             _lastActiveSessionId is { } previousSessionId &&
                             newSessionId != previousSessionId;
        var classroomEnded = snapshot.ConnectionState == StudentCompanionConnectionState.ConnectedWithoutClass;
        if (snapshot.SessionId is { } activeSessionId) _lastActiveSessionId = activeSessionId;
        else if (classroomEnded) _lastActiveSessionId = null;
        _snapshot = snapshot;
        if (sessionChanged || classroomEnded)
        {
            _pendingHelpEventId = null;
            _hasTeacherReply = false;
            _helpStatus = "";
            _classroomNoticeMessage = "";
            _handledTeacherEventOrder.Clear();
            _handledTeacherEventIds.Clear();
            _earlyTeacherReplies.Clear();
            Changed(nameof(HelpStatus));
            Changed(nameof(HasHelpStatus));
        }
        Changed(nameof(StatusTitle));
        Changed(nameof(StatusDescription));
        Changed(nameof(ClassroomSummary));
        Changed(nameof(HasClassroomSummary));
        Changed(nameof(IsClassroomActive));
        Changed(nameof(CanRequestHelp));
        Changed(nameof(CanResolveHelp));
        Changed(nameof(HasHelpAction));
        Changed(nameof(CanActivateHelpAction));
        Changed(nameof(HelpActionText));
    }

    public async Task RequestHelpAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRequestHelp || _snapshot.SessionId is not { } sessionId || _requestHelp is null) return;
        _isSubmittingHelp = true;
        _helpStatus = "正在联系老师……";
        NotifyHelpActionChanged();
        Changed(nameof(HelpStatus));
        Changed(nameof(HasHelpStatus));
        try
        {
            var result = await _requestHelp(sessionId, cancellationToken);
            if (_snapshot.SessionId != sessionId) return;
            if (!result.Accepted || result.SessionId != sessionId || result.EventId == Guid.Empty ||
                result.CorrelationId is not null)
                throw new InvalidDataException("课堂求助回执与当前课堂不匹配。");
            if (_earlyTeacherReplies.Remove(result.EventId, out var earlyReply))
            {
                _pendingHelpEventId = result.EventId;
                _hasTeacherReply = true;
                _helpStatus = "老师回复：" + earlyReply;
            }
            else
            {
                _pendingHelpEventId = result.EventId;
                _hasTeacherReply = false;
                _helpStatus = "求助已发送给老师。";
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          InvalidDataException or IOException or CryptographicException)
        {
            _helpStatus = "暂时无法联系老师；请检查校园网连接后重试。";
        }
        finally
        {
            _isSubmittingHelp = false;
            NotifyHelpActionChanged();
            Changed(nameof(HelpStatus));
            Changed(nameof(HasHelpStatus));
        }
    }

    public Task ActivateHelpActionAsync(CancellationToken cancellationToken = default)
    {
        if (CanResolveHelp) return ResolveHelpAsync(cancellationToken);
        return RequestHelpAsync(cancellationToken);
    }

    public async Task ResolveHelpAsync(CancellationToken cancellationToken = default)
    {
        if (!CanResolveHelp || _snapshot.SessionId is not { } sessionId ||
            _pendingHelpEventId is not { } helpEventId || _resolveHelp is null) return;
        _isResolvingHelp = true;
        _helpStatus = "正在确认解决……";
        NotifyHelpActionChanged();
        Changed(nameof(HelpStatus));
        Changed(nameof(HasHelpStatus));
        try
        {
            var result = await _resolveHelp(sessionId, helpEventId, cancellationToken);
            if (_snapshot.SessionId != sessionId) return;
            if (!result.Accepted || result.SessionId != sessionId || result.EventId == Guid.Empty ||
                result.CorrelationId != helpEventId)
                throw new InvalidDataException("解决求助回执与当前求助不匹配。");
            _pendingHelpEventId = null;
            _hasTeacherReply = false;
            _helpStatus = "已标记为解决。";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          InvalidDataException or IOException or CryptographicException)
        {
            _helpStatus = "暂时无法确认解决；恢复校园网后可重试。";
        }
        finally
        {
            _isResolvingHelp = false;
            NotifyHelpActionChanged();
            Changed(nameof(HelpStatus));
            Changed(nameof(HasHelpStatus));
        }
    }

    public void ApplyTeacherEvent(ClassroomEvent classroomEvent)
    {
        ArgumentNullException.ThrowIfNull(classroomEvent);
        if (_lastActiveSessionId != classroomEvent.SessionId ||
            classroomEvent.Sender != ClassroomEventSender.Teacher) return;
        ClassroomEventCryptography.ValidateEvent(classroomEvent, classroomEvent.CampusId,
            classroomEvent.SessionId, classroomEvent.Target, ClassroomEventSender.Teacher, DateTimeOffset.UtcNow);
        if (!_handledTeacherEventIds.Add(classroomEvent.EventId)) return;
        _handledTeacherEventOrder.Enqueue(classroomEvent.EventId);
        while (_handledTeacherEventOrder.Count > ClassroomEventBuffer.MaximumEventsPerSession)
            _handledTeacherEventIds.Remove(_handledTeacherEventOrder.Dequeue());
        switch (classroomEvent.Type)
        {
            case ClassroomEventType.HelpAcknowledged:
                if (_pendingHelpEventId == classroomEvent.CorrelationId)
                    _helpStatus = "老师已看到你的求助。";
                break;
            case ClassroomEventType.TeacherReply:
                if (_pendingHelpEventId == classroomEvent.CorrelationId)
                {
                    _hasTeacherReply = true;
                    _helpStatus = "老师回复：" + classroomEvent.Message;
                    NotifyHelpActionChanged();
                }
                else if (classroomEvent.CorrelationId is { } earlyReplyId)
                {
                    if (_earlyTeacherReplies.Count >= ClassroomEventBuffer.MaximumEventsPerSession)
                        _earlyTeacherReplies.Remove(_earlyTeacherReplies.Keys.First());
                    _earlyTeacherReplies[earlyReplyId] = classroomEvent.Message ?? "";
                }
                break;
            case ClassroomEventType.ClassroomNotice:
                _classroomNoticeMessage = classroomEvent.Message ?? "收到一条课堂通知。";
                break;
        }
        Changed(nameof(HelpStatus));
        Changed(nameof(HasHelpStatus));
    }

    private void NotifyHelpActionChanged()
    {
        Changed(nameof(CanRequestHelp));
        Changed(nameof(CanResolveHelp));
        Changed(nameof(HasHelpAction));
        Changed(nameof(CanActivateHelpAction));
        Changed(nameof(HelpActionText));
    }

    private static void Validate(StudentCompanionStatusSnapshot snapshot)
    {
        switch (snapshot.ConnectionState)
        {
            case StudentCompanionConnectionState.Disconnected:
            case StudentCompanionConnectionState.ConnectedWithoutClass:
                if (snapshot.RoomName is not null || snapshot.TargetCount != 0 || snapshot.SessionId is not null ||
                    snapshot.Mode is not null)
                    throw new InvalidDataException("非课堂状态不能包含教室或目标电脑信息。");
                break;
            case StudentCompanionConnectionState.ClassroomActive:
                if (string.IsNullOrWhiteSpace(snapshot.RoomName) || snapshot.RoomName.Length > 100 ||
                    snapshot.RoomName != snapshot.RoomName.Trim() || snapshot.RoomName.Any(char.IsControl) ||
                    snapshot.TargetCount is < 1 or > 150)
                    throw new InvalidDataException("当前课堂状态无效。");
                if (snapshot.SessionId is null || snapshot.SessionId == Guid.Empty)
                    throw new InvalidDataException("当前课堂状态无效。");
                if (snapshot.Mode is { } mode && !Enum.IsDefined(mode))
                    throw new InvalidDataException("当前课堂模式无效。");
                break;
            default:
                throw new InvalidDataException("课堂连接状态不受支持。");
        }
    }

    private void Changed([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
