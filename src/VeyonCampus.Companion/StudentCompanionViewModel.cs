using System.ComponentModel;
using System.Collections.ObjectModel;
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
    private readonly Func<Guid, Guid, string, CancellationToken, Task<StudentAgentClassroomEventSubmission>>? _sendChatMessage;
    private StudentCompanionStatusSnapshot _snapshot =
        new(StudentCompanionConnectionState.Disconnected);
    private bool _isSubmittingHelp;
    private bool _isResolvingHelp;
    private Guid? _pendingHelpEventId;
    private bool _hasTeacherReply;
    private string _helpStatus = "";
    private string _classroomNoticeMessage = "";
    private DateTimeOffset? _classroomNoticeExpiresUtc;
    private Uri? _classroomNoticeLink;
    private string _classroomNoticeOpenStatus = "";
    private Guid? _lastActiveSessionId;
    private int _consecutiveDisconnectedSnapshots;
    private readonly Queue<Guid> _handledTeacherEventOrder = new();
    private readonly HashSet<Guid> _handledTeacherEventIds = [];
    private readonly Dictionary<Guid, string> _earlyTeacherReplies = [];
    private readonly HashSet<Guid> _communicationEventIds = [];
    private readonly Queue<Guid> _communicationEventOrder = new();
    private bool _isSendingChatMessage;
    private string _chatDraft = "";
    private string _communicationStatus = "";

    public StudentCompanionViewModel(
        Func<Guid, CancellationToken, Task<StudentAgentClassroomEventSubmission>>? requestHelp = null,
        Func<Guid, Guid, CancellationToken, Task<StudentAgentClassroomEventSubmission>>? resolveHelp = null,
        Func<Guid, Guid, string, CancellationToken, Task<StudentAgentClassroomEventSubmission>>? sendChatMessage = null)
    {
        _requestHelp = requestHelp;
        _resolveHelp = resolveHelp;
        _sendChatMessage = sendChatMessage;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusTitle => _snapshot.ConnectionState switch
    {
        StudentCompanionConnectionState.Disconnected => "等待教师端连接",
        StudentCompanionConnectionState.ConnectedWithoutClass => "当前没有进行中的课堂",
        StudentCompanionConnectionState.ClassroomActive => "课堂进行中",
        _ => "课堂状态未知"
    };

    public string ClassroomSummary => _snapshot.ConnectionState switch
    {
        StudentCompanionConnectionState.ClassroomActive =>
            $"{_snapshot.RoomName} · {_snapshot.TargetCount} 台电脑" +
            (_snapshot.Mode == ClassroomMode.Practice ? " · 练习" : ""),
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
    public string HelpStatus
    {
        get
        {
            var parts = new List<string>(2);
            if (_helpStatus.Length > 0) parts.Add(_helpStatus);
            if (HasActiveClassroomNotice(DateTimeOffset.UtcNow))
                parts.Add("课堂通知：" + _classroomNoticeMessage);
            return string.Join("\n", parts);
        }
    }
    public bool HasHelpStatus => HelpStatus.Length > 0;
    public DateTimeOffset? ClassroomNoticeExpiresUtc => _classroomNoticeExpiresUtc;
    public bool HasClassroomNoticeLink => HasActiveClassroomNotice(DateTimeOffset.UtcNow) &&
                                          _classroomNoticeLink is not null;
    public string ClassroomNoticeOpenStatus => _classroomNoticeOpenStatus;
    public bool HasClassroomNoticeOpenStatus => _classroomNoticeOpenStatus.Length > 0;
    public ObservableCollection<StudentCompanionCommunicationItem> CommunicationItems { get; } = [];
    public bool HasCommunicationItems => CommunicationItems.Count > 0;
    public string ChatDraft
    {
        get => _chatDraft;
        set
        {
            if (_chatDraft == value) return;
            _chatDraft = value ?? "";
            Changed();
            Changed(nameof(CanSendChatMessage));
        }
    }
    public string CommunicationStatus
    {
        get => _communicationStatus;
        private set
        {
            if (_communicationStatus == value) return;
            _communicationStatus = value;
            Changed();
            Changed(nameof(HasCommunicationStatus));
        }
    }
    public bool HasCommunicationStatus => _communicationStatus.Length > 0;
    public bool CanSendChatMessage => IsClassroomActive && !_isSendingChatMessage &&
        _sendChatMessage is not null && !string.IsNullOrWhiteSpace(ChatDraft) &&
        ClassroomEventCryptography.IsValidChatMessage(ChatDraft);

    public Uri? GetActiveClassroomNoticeLink(DateTimeOffset nowUtc)
    {
        RefreshClassroomNoticeExpiry(nowUtc);
        return HasActiveClassroomNotice(nowUtc.ToUniversalTime()) ? _classroomNoticeLink : null;
    }

    public void RefreshClassroomNoticeExpiry(DateTimeOffset nowUtc)
    {
        if (_classroomNoticeExpiresUtc is not { } expiresUtc || expiresUtc > nowUtc.ToUniversalTime()) return;
        if (!ClearClassroomNotice()) return;
        Changed(nameof(HelpStatus));
        Changed(nameof(HasHelpStatus));
    }

    public void SetClassroomNoticeOpenFailed()
    {
        const string message = "无法打开课堂网址，请检查默认浏览器或手动复制通知内容。";
        if (_classroomNoticeOpenStatus == message) return;
        _classroomNoticeOpenStatus = message;
        Changed(nameof(ClassroomNoticeOpenStatus));
        Changed(nameof(HasClassroomNoticeOpenStatus));
    }

    public void ApplyStatus(StudentCompanionStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        if (_snapshot == snapshot)
        {
            if (snapshot.ConnectionState == StudentCompanionConnectionState.Disconnected &&
                _lastActiveSessionId is not null && ++_consecutiveDisconnectedSnapshots >= 3)
            {
                _lastActiveSessionId = null;
                ClearActiveClassroomConversation();
            }
            return;
        }
        var sessionChanged = snapshot.SessionId is { } newSessionId &&
                             _lastActiveSessionId is { } previousSessionId &&
                             newSessionId != previousSessionId;
        var classroomEnded = false;
        if (snapshot.ConnectionState == StudentCompanionConnectionState.ClassroomActive)
            _consecutiveDisconnectedSnapshots = 0;
        else if (snapshot.ConnectionState == StudentCompanionConnectionState.ConnectedWithoutClass)
        {
            classroomEnded = _lastActiveSessionId is not null;
            _consecutiveDisconnectedSnapshots = 0;
        }
        else
        {
            _consecutiveDisconnectedSnapshots++;
            classroomEnded = _lastActiveSessionId is not null && _consecutiveDisconnectedSnapshots >= 3;
        }
        if (snapshot.SessionId is { } activeSessionId) _lastActiveSessionId = activeSessionId;
        else if (classroomEnded) _lastActiveSessionId = null;
        _snapshot = snapshot;
        if (sessionChanged || classroomEnded)
        {
            ClearActiveClassroomConversation();
        }
        Changed(nameof(StatusTitle));
        Changed(nameof(ClassroomSummary));
        Changed(nameof(HasClassroomSummary));
        Changed(nameof(IsClassroomActive));
        Changed(nameof(CanRequestHelp));
        Changed(nameof(CanResolveHelp));
        Changed(nameof(HasHelpAction));
        Changed(nameof(CanActivateHelpAction));
        Changed(nameof(HelpActionText));
        Changed(nameof(CanSendChatMessage));
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
            AppendCommunication(new StudentCompanionCommunicationItem(result.EventId, "你", "需要老师帮助",
                DateTimeOffset.UtcNow, "求助"));
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

    public bool ApplyTeacherEvent(ClassroomEvent classroomEvent)
    {
        ArgumentNullException.ThrowIfNull(classroomEvent);
        if (_lastActiveSessionId != classroomEvent.SessionId ||
            classroomEvent.Sender != ClassroomEventSender.Teacher) return false;
        ClassroomEventCryptography.ValidateEvent(classroomEvent, classroomEvent.CampusId,
            classroomEvent.SessionId, classroomEvent.Target, ClassroomEventSender.Teacher, DateTimeOffset.UtcNow);
        if (!_handledTeacherEventIds.Add(classroomEvent.EventId)) return false;
        _handledTeacherEventOrder.Enqueue(classroomEvent.EventId);
        while (_handledTeacherEventOrder.Count > ClassroomEventBuffer.MaximumEventsPerSession)
            _handledTeacherEventIds.Remove(_handledTeacherEventOrder.Dequeue());
        switch (classroomEvent.Type)
        {
            case ClassroomEventType.HelpAcknowledged:
                if (_pendingHelpEventId == classroomEvent.CorrelationId)
                    _helpStatus = "老师已看到你的求助。";
                AppendCommunication(new StudentCompanionCommunicationItem(classroomEvent.EventId, "老师",
                    "老师已看到你的求助。", classroomEvent.IssuedUtc, "求助"));
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
                AppendCommunication(CreateTeacherCommunicationItem(classroomEvent, "老师回复"));
                break;
            case ClassroomEventType.ClassroomNotice:
                _classroomNoticeMessage = classroomEvent.Message ?? "收到一条课堂通知。";
                _classroomNoticeExpiresUtc = classroomEvent.ExpiresUtc;
                _classroomNoticeLink = ClassroomNoticeLinkParser.ExtractSingleHttpLink(_classroomNoticeMessage);
                _classroomNoticeOpenStatus = "";
                Changed(nameof(ClassroomNoticeExpiresUtc));
                Changed(nameof(HasClassroomNoticeLink));
                Changed(nameof(ClassroomNoticeOpenStatus));
                Changed(nameof(HasClassroomNoticeOpenStatus));
                AppendCommunication(CreateTeacherCommunicationItem(classroomEvent, "全班通知"));
                break;
            case ClassroomEventType.ClassroomChatMessage:
                AppendCommunication(CreateTeacherCommunicationItem(classroomEvent, "老师"));
                break;
        }
        Changed(nameof(HelpStatus));
        Changed(nameof(HasHelpStatus));
        return true;
    }

    private bool HasActiveClassroomNotice(DateTimeOffset nowUtc) =>
        _classroomNoticeMessage.Length > 0 && _classroomNoticeExpiresUtc is { } expiresUtc &&
        expiresUtc > nowUtc.ToUniversalTime();

    public async Task SendClassroomChatMessageAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSendChatMessage || _snapshot.SessionId is not { } sessionId || _sendChatMessage is null) return;
        var message = ClassroomEventCryptography.NormalizeChatMessage(ChatDraft);
        var eventId = Guid.NewGuid();
        _isSendingChatMessage = true;
        CommunicationStatus = "正在提交消息……";
        Changed(nameof(CanSendChatMessage));
        var item = new StudentCompanionCommunicationItem(eventId, "你", message, DateTimeOffset.UtcNow,
            "正在提交", canRetry: true);
        item.MarkSubmitting();
        AppendCommunication(item);
        try
        {
            var result = await _sendChatMessage(sessionId, eventId, message, cancellationToken);
            if (_snapshot.SessionId != sessionId) return;
            if (!result.Accepted || result.SessionId != sessionId || result.EventId != eventId ||
                result.CorrelationId is null || result.CorrelationId == Guid.Empty)
                throw new InvalidDataException("聊天回执与当前课堂或对话不匹配。");
            item.MarkSubmitted();
            ChatDraft = "";
            CommunicationStatus = "消息已提交给教师端。";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          InvalidDataException or IOException or CryptographicException)
        {
            item.MarkFailed();
            CommunicationStatus = exception is HttpRequestException
                ? "消息未送出；请检查校园网连接后重试。"
                : "消息未送出；" + exception.Message;
        }
        finally
        {
            _isSendingChatMessage = false;
            Changed(nameof(CanSendChatMessage));
        }
    }

    public async Task RetryClassroomChatMessageAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        if (_snapshot.SessionId is not { } sessionId || _sendChatMessage is null || _isSendingChatMessage) return;
        var item = CommunicationItems.FirstOrDefault(candidate => candidate.EventId == eventId && candidate.CanRetry);
        if (item is null) return;
        _isSendingChatMessage = true;
        item.MarkSubmitting();
        CommunicationStatus = "正在重试消息……";
        Changed(nameof(CanSendChatMessage));
        try
        {
            var result = await _sendChatMessage(sessionId, item.EventId, item.Message, cancellationToken);
            if (_snapshot.SessionId != sessionId) return;
            if (!result.Accepted || result.SessionId != sessionId || result.EventId != item.EventId ||
                result.CorrelationId is null || result.CorrelationId == Guid.Empty)
                throw new InvalidDataException("聊天回执与当前课堂或对话不匹配。");
            item.MarkSubmitted();
            CommunicationStatus = "消息已提交给教师端。";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          InvalidDataException or IOException or CryptographicException)
        {
            item.MarkFailed();
            CommunicationStatus = "重试未成功；" + exception.Message;
        }
        finally
        {
            _isSendingChatMessage = false;
            Changed(nameof(CanSendChatMessage));
        }
    }

    private void AppendCommunication(StudentCompanionCommunicationItem item)
    {
        if (!_communicationEventIds.Add(item.EventId)) return;
        _communicationEventOrder.Enqueue(item.EventId);
        CommunicationItems.Add(item);
        while (_communicationEventOrder.Count > 200)
        {
            var expired = _communicationEventOrder.Dequeue();
            _communicationEventIds.Remove(expired);
            var oldItem = CommunicationItems.FirstOrDefault(candidate => candidate.EventId == expired);
            if (oldItem is not null) CommunicationItems.Remove(oldItem);
        }
        Changed(nameof(HasCommunicationItems));
    }

    private void ClearActiveClassroomConversation()
    {
        _pendingHelpEventId = null;
        _hasTeacherReply = false;
        _helpStatus = "";
        ClearClassroomNotice();
        _handledTeacherEventOrder.Clear();
        _handledTeacherEventIds.Clear();
        _earlyTeacherReplies.Clear();
        CommunicationItems.Clear();
        _communicationEventIds.Clear();
        _communicationEventOrder.Clear();
        CommunicationStatus = "";
        ChatDraft = "";
        Changed(nameof(HasCommunicationItems));
        Changed(nameof(HelpStatus));
        Changed(nameof(HasHelpStatus));
        Changed(nameof(CanRequestHelp));
        Changed(nameof(CanResolveHelp));
        Changed(nameof(HasHelpAction));
        Changed(nameof(CanActivateHelpAction));
        Changed(nameof(HelpActionText));
        Changed(nameof(CanSendChatMessage));
    }

    private static StudentCompanionCommunicationItem CreateTeacherCommunicationItem(ClassroomEvent classroomEvent,
        string speaker) => new(classroomEvent.EventId, speaker, classroomEvent.Message ?? "",
        classroomEvent.IssuedUtc, speaker == "全班通知" ? "通知" : "课堂沟通");

    private bool ClearClassroomNotice()
    {
        if (_classroomNoticeMessage.Length == 0 && _classroomNoticeExpiresUtc is null &&
            _classroomNoticeLink is null && _classroomNoticeOpenStatus.Length == 0)
            return false;
        _classroomNoticeMessage = "";
        _classroomNoticeExpiresUtc = null;
        _classroomNoticeLink = null;
        _classroomNoticeOpenStatus = "";
        Changed(nameof(ClassroomNoticeExpiresUtc));
        Changed(nameof(HasClassroomNoticeLink));
        Changed(nameof(ClassroomNoticeOpenStatus));
        Changed(nameof(HasClassroomNoticeOpenStatus));
        return true;
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

public sealed class StudentCompanionCommunicationItem(Guid eventId, string speaker, string message,
    DateTimeOffset issuedUtc, string kind, bool canRetry = false) : INotifyPropertyChanged
{
    private string _deliveryStatus = kind;
    private bool _canRetry = canRetry;

    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid EventId { get; } = eventId;
    public string Speaker { get; } = speaker;
    public string Message { get; } = message;
    public string EventTime { get; } = issuedUtc.ToLocalTime().ToString("HH:mm");
    public string DeliveryStatus => _deliveryStatus;
    public bool CanRetry => _canRetry;

    public void MarkSubmitting()
    {
        _canRetry = false;
        _deliveryStatus = "正在提交";
        Changed(nameof(CanRetry));
        Changed(nameof(DeliveryStatus));
    }

    public void MarkSubmitted()
    {
        _canRetry = false;
        _deliveryStatus = "已提交给教师端";
        Changed(nameof(CanRetry));
        Changed(nameof(DeliveryStatus));
    }

    public void MarkFailed()
    {
        _canRetry = true;
        _deliveryStatus = "发送失败";
        Changed(nameof(CanRetry));
        Changed(nameof(DeliveryStatus));
    }

    private void Changed(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
