using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;
using VeyonCampus.App.Maintenance;

namespace VeyonCampus.App;

public partial class TeacherViewModel
{
    // ===== Classroom / events / chat / task members moved from TeacherViewModel.cs =====
    public string ClassroomSessionSummary => _activeClassroomSession is { } active
        ? $"课堂范围：{active.Room.RoomName} · {active.Targets.Length} 台学生电脑"
        : SelectedRoomProfile is { } room
            ? room.ComputerCount > 0
                ? $"课堂范围：{room.DisplayName} · {room.ComputerCount} 台学生电脑可用于互动"
                : "此地点还没有配置电脑；请先在座位表添加座位并添加到 Veyon。"
            : "请先选择学生电脑范围。";
    public decimal ClassroomCountdownMinutes
    {
        get => _classroomCountdownMinutes;
        set
        {
            var next = Math.Clamp(decimal.Round(value, 0, MidpointRounding.AwayFromZero),
                ClassroomCountdown.MinimumMinutes, ClassroomCountdown.MaximumMinutes);
            if (_classroomCountdownMinutes == next) return;
            _classroomCountdownMinutes = next;
            Changed();
        }
    }
    public bool HasClassroomCountdown => _activeClassroomCountdown is not null;
    public bool IsClassroomCountdownRunning => _activeClassroomCountdown is { } countdown &&
        countdown.Remaining(DateTimeOffset.UtcNow) > TimeSpan.Zero;
    public bool CanStartClassroomCountdown => HasActiveClassroomSession && !HasClassroomCountdown &&
                                             !HasClassroomCountdownError;
    public bool CanEndClassroomCountdown => HasActiveClassroomSession && HasClassroomCountdown;
    public string ClassroomCountdownText
    {
        get
        {
            if (_activeClassroomCountdown is not { } countdown) return "";
            var seconds = (int)Math.Ceiling(countdown.Remaining(DateTimeOffset.UtcNow).TotalSeconds);
            if (seconds <= 0) return "时间到";
            var remaining = TimeSpan.FromSeconds(seconds);
            return remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
        }
    }
    public string ClassroomCountdownStatus
    {
        get => _classroomCountdownStatus;
        private set
        {
            if (_classroomCountdownStatus == value) return;
            _classroomCountdownStatus = value;
            Changed();
            Changed(nameof(HasClassroomCountdownStatus));
            Changed(nameof(HasClassroomCountdownError));
            Changed(nameof(HasClassroomCountdownInfo));
            Changed(nameof(CanStartClassroomCountdown));
        }
    }
    public bool HasClassroomCountdownStatus => !string.IsNullOrWhiteSpace(ClassroomCountdownStatus);
    public bool HasClassroomCountdownError => ClassroomCountdownStatus.StartsWith("无法", StringComparison.Ordinal) ||
                                              ClassroomCountdownStatus.StartsWith("课堂倒计时无法", StringComparison.Ordinal);
    public bool HasClassroomCountdownInfo => HasClassroomCountdownStatus && !HasClassroomCountdownError;
    public string ClassroomTaskProgressDraft
    {
        get => _classroomTaskProgressDraft;
        set
        {
            var next = value ?? "";
            if (_classroomTaskProgressDraft == next) return;
            _classroomTaskProgressDraft = next;
            Changed();
            Changed(nameof(CanAddClassroomTask));
        }
    }
    public int ClassroomTaskProgressCompletedCount => _activeClassroomTaskProgress?.CompletedCount ?? 0;
    public int ClassroomTaskProgressTotalCount => _activeClassroomTaskProgress?.Tasks.Length ?? 0;
    public bool HasClassroomTaskProgress => ClassroomTaskProgressTotalCount > 0;
    public string ClassroomTaskProgressSummary => HasClassroomTaskProgress
        ? $"{ClassroomTaskProgressCompletedCount}/{ClassroomTaskProgressTotalCount} 项已完成"
        : "";
    public bool CanAddClassroomTask => HasActiveClassroomSession && !HasClassroomTaskProgressError &&
        ClassroomTaskProgressTotalCount < ClassroomTaskProgress.MaximumTasks &&
        !string.IsNullOrWhiteSpace(ClassroomTaskProgressDraft);
    public string ClassroomTaskProgressStatus
    {
        get => _classroomTaskProgressStatus;
        private set
        {
            if (_classroomTaskProgressStatus == value) return;
            _classroomTaskProgressStatus = value;
            Changed();
            Changed(nameof(HasClassroomTaskProgressStatus));
            Changed(nameof(HasClassroomTaskProgressError));
            Changed(nameof(CanAddClassroomTask));
        }
    }
    public bool HasClassroomTaskProgressStatus => !string.IsNullOrWhiteSpace(ClassroomTaskProgressStatus);
    public bool HasClassroomTaskProgressError => ClassroomTaskProgressStatus.StartsWith("无法", StringComparison.Ordinal) ||
        ClassroomTaskProgressStatus.StartsWith("课堂任务进度无法", StringComparison.Ordinal);
    public bool HasClassroomRestores => _pendingClassroomRestores.Count > 0 ||
                                        !string.IsNullOrWhiteSpace(_classroomRestoreStatus);
    public bool HasPendingClassroomRestoreItems => _pendingClassroomRestores.Count > 0;
    public int PendingClassroomRestoreCount => _pendingClassroomRestores.Count;
    public string ClassroomRestoreSummary
    {
        get
        {
            IReadOnlyDictionary<Guid, ClassroomSession> sessions;
            try
            {
                sessions = _classroomSessionStore.ReadRecent().ToDictionary(item => item.SessionId);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                sessions = new Dictionary<Guid, ClassroomSession>();
            }

            var lines = _pendingClassroomRestores.Take(24).Select(item =>
                $"{item.RoomName} · {(sessions.TryGetValue(item.SessionId, out var session)
                    ? GetStudentNameForSessionTarget(session, item.DeviceLabel)
                    : "未填写姓名")} · {(item.Kind == ClassroomPolicyKind.Website ? "学生代理" : "应用")}")
                .ToList();
            if (_pendingClassroomRestores.Count > lines.Count)
                lines.Add($"还有 {_pendingClassroomRestores.Count - lines.Count} 项");
            var summary = _pendingClassroomRestores.Count > 0
                ? $"{_pendingClassroomRestores.Count} 项限制尚未恢复：\n" +
                  string.Join(Environment.NewLine, lines)
                : "";
            return string.Join(Environment.NewLine,
                new[] { summary, _classroomRestoreStatus }.Where(item => !string.IsNullOrWhiteSpace(item)));
        }
    }

    public string GetStudentDisplayNameForTarget(string computerLabel)
    {
        if (!string.IsNullOrWhiteSpace(computerLabel))
        {
            var key = NormalizeTargetKey(computerLabel);
            var choice = WebsitePolicyTargetChoices.FirstOrDefault(item =>
                string.Equals(NormalizeTargetKey(item.Target), key, StringComparison.OrdinalIgnoreCase));
            if (choice is not null) return choice.StudentName;
        }
        return GetStudentNameForComputerLabel(computerLabel);
    }

    private string GetStudentNameForComputerLabel(string computerLabel)
    {
        if (string.IsNullOrWhiteSpace(computerLabel)) return "未填写姓名";
        var key = NormalizeTargetKey(computerLabel);
        var matches = new Dictionary<(Guid CampusId, Guid RoomId, Guid SeatId), string>();
        foreach (var campus in CampusProfiles)
        foreach (var room in campus.Rooms)
        {
            ClassroomSeatGridLayout layout;
            try { layout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId, room.StartNumber); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            { continue; }
            IReadOnlyList<string> computerNames;
            IReadOnlyList<int> computerNumbers;
            try
            {
                computerNames = room.GetComputerNames();
                computerNumbers = room.GetComputerNumbers();
            }
            catch (InvalidDataException) { computerNames = Array.Empty<string>(); computerNumbers = Array.Empty<int>(); }
            foreach (var seat in layout.Seats.Where(item => !string.IsNullOrWhiteSpace(item.StudentName)))
            {
                if (string.Equals(seat.StudentName.Trim(), computerLabel.Trim(), StringComparison.OrdinalIgnoreCase))
                    matches[(campus.ProfileId, room.RoomId, seat.SeatId)] = seat.StudentName.Trim();
                if ((!string.IsNullOrWhiteSpace(seat.VeyonComputerName) &&
                     NormalizeTargetKey(seat.VeyonComputerName) == key) ||
                    (!string.IsNullOrWhiteSpace(seat.VeyonHostAddress) &&
                     NormalizeTargetKey(seat.VeyonHostAddress) == key))
                    matches[(campus.ProfileId, room.RoomId, seat.SeatId)] = seat.StudentName.Trim();
                if (seat.ComputerNumber is not { } number) continue;
                var index = -1;
                for (var candidate = 0; candidate < computerNumbers.Count; candidate++)
                    if (computerNumbers[candidate] == number) { index = candidate; break; }
                if (index < 0) continue;
                if (index < computerNames.Count && NormalizeTargetKey(computerNames[index]) == key)
                    matches[(campus.ProfileId, room.RoomId, seat.SeatId)] = seat.StudentName.Trim();
            }
        }
        return matches.Count == 1 ? matches.Values.Single() : "未填写姓名";
    }

    private string GetStudentNameForSessionTarget(ClassroomSession session, string deviceLabel)
    {
        IReadOnlyList<ClassroomSeatGridLocation> seats;
        try
        {
            var layout = ReadClassroomSeatGridForSession(session);
            seats = ClassroomSeatGridStore.GetLocations(layout);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return "未填写姓名";
        }

        var targetIndex = -1;
        for (var index = 0; index < session.Targets.Length; index++)
            if (string.Equals(session.Targets[index].DeviceLabel, deviceLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = index;
                break;
            }
        if (targetIndex < 0) return "未填写姓名";
        var seat = FindUniqueClassroomSeat(session, seats, deviceLabel, deviceLabel, targetIndex);
        return string.IsNullOrWhiteSpace(seat?.StudentName) ? "未填写姓名" : seat.StudentName.Trim();
    }
    public bool CanRetryClassroomRestores => PendingClassroomRestoreCount > 0 &&
        !_isRetryingClassroomRestores && !_isClassroomTransitioning && !IsExecuting;
    public string ClassroomDeliveryStatus
    {
        get => _classroomDeliveryStatus;
        private set
        {
            if (_classroomDeliveryStatus == value) return;
            _classroomDeliveryStatus = value;
            Changed();
            Changed(nameof(HasClassroomDeliveryStatus));
        }
    }
    public bool HasClassroomDeliveryStatus => !string.IsNullOrWhiteSpace(ClassroomDeliveryStatus);
    public string ClassroomEventStatus
    {
        get => _classroomEventStatus;
        private set
        {
            if (_classroomEventStatus == value) return;
            _classroomEventStatus = value;
            Changed();
            Changed(nameof(HasActiveClassroomEventStatus));
        }
    }
    public bool HasActiveClassroomEventStatus => HasActiveClassroomSession &&
        !string.IsNullOrWhiteSpace(ClassroomEventStatus);
    public string ClassroomNoticeDraft
    {
        get => _classroomNoticeDraft;
        set
        {
            if (_classroomNoticeDraft == value) return;
            _classroomNoticeDraft = value ?? "";
            Changed();
            Changed(nameof(CanSendClassroomNotice));
            ClassroomNoticeStatus = "";
        }
    }
    public string ClassroomNoticeTargetSummary =>
        $"将发送到本堂课的 {_activeClassroomSession?.Targets.Length ?? 0} 台学生电脑";
    public bool CanSendClassroomNotice => HasActiveClassroomSession && !_isClassroomTransitioning &&
        !string.IsNullOrWhiteSpace(_classroomNoticeDraft) && _classroomNoticeDraft.Trim().Length <=
        ClassroomEventCryptography.MaximumMessageCharacters &&
        !_classroomNoticeDraft.Any(character => char.IsControl(character) &&
            character is not '\r' and not '\n' and not '\t');
    public string ClassroomNoticeStatus
    {
        get => _classroomNoticeStatus;
        private set
        {
            if (_classroomNoticeStatus == value) return;
            _classroomNoticeStatus = value;
            Changed();
            Changed(nameof(HasClassroomNoticeStatus));
        }
    }
    public bool HasClassroomNoticeStatus => !string.IsNullOrWhiteSpace(_classroomNoticeStatus);
    public ObservableCollection<TeacherClassroomEventItem> ClassroomEventItems { get; } = [];
    public ObservableCollection<TeacherClassroomTaskItem> ClassroomTaskProgressItems { get; } = [];
    public bool HasClassroomEventItems => ClassroomEventItems.Count > 0;
    public int PendingClassroomHelpCount => ClassroomEventItems.Count(item => item.IsHelpRequest && item.CanReply);
    public ObservableCollection<TeacherClassroomChatThread> ClassroomChatThreads { get; } = [];
    public bool HasClassroomChatThreads => ClassroomChatThreads.Count > 0;
    public int UnreadClassroomChatCount => ClassroomChatThreads.Sum(item => item.UnreadCount);
    public string ClassroomChatUnreadSummary => $"未读 {UnreadClassroomChatCount} 条";
    public bool HasUnreadClassroomChatMessages => UnreadClassroomChatCount > 0;
    public string UnreadClassroomChatBadgeText => UnreadClassroomChatCount > 99 ? "99+" :
        UnreadClassroomChatCount.ToString(CultureInfo.CurrentCulture);
    public string ClassroomChatSearch
    {
        get => _classroomChatSearch;
        set
        {
            if (_classroomChatSearch == value) return;
            _classroomChatSearch = value ?? "";
            Changed();
            Changed(nameof(FilteredClassroomChatThreads));
            Changed(nameof(HasClassroomChatSearchResults));
            Changed(nameof(HasNoClassroomChatSearchResults));
        }
    }
    public IReadOnlyList<TeacherClassroomChatThread> FilteredClassroomChatThreads =>
        ClassroomChatThreads.Where(thread => string.IsNullOrWhiteSpace(ClassroomChatSearch) ||
            thread.Title.Contains(ClassroomChatSearch.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
            thread.Detail.Contains(ClassroomChatSearch.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
            thread.Target.Contains(ClassroomChatSearch.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    public bool HasClassroomChatSearchResults => FilteredClassroomChatThreads.Count > 0;
    public bool HasNoClassroomChatSearchResults => HasClassroomChatThreads && !HasClassroomChatSearchResults;
    public bool HasSelectedClassroomChatThread => SelectedClassroomChatThread is not null;
    public bool HasSelectedClassroomChatMessages =>
        SelectedClassroomChatThread is { } thread && thread.Messages.Count > 0;
    public bool HasEmptySelectedClassroomChatThread => HasSelectedClassroomChatThread &&
        !HasSelectedClassroomChatMessages;
    public bool HasNoSelectedClassroomChatThread => !HasSelectedClassroomChatThread;
    public bool IsClassroomChatDrawerOpen
    {
        get => _isClassroomChatDrawerOpen;
        set
        {
            if (_isClassroomChatDrawerOpen == value) return;
            _isClassroomChatDrawerOpen = value;
            if (value) SelectedClassroomChatThread?.MarkRead();
            Changed();
            Changed(nameof(UnreadClassroomChatCount));
            Changed(nameof(ClassroomChatUnreadSummary));
            Changed(nameof(HasUnreadClassroomChatMessages));
            Changed(nameof(UnreadClassroomChatBadgeText));
            Changed(nameof(ClassroomInteractionNavigationLabel));
        }
    }
    public TeacherClassroomChatThread? SelectedClassroomChatThread
    {
        get => _selectedClassroomChatThread;
        set
        {
            if (ReferenceEquals(_selectedClassroomChatThread, value)) return;
            _selectedClassroomChatThread?.SetDraft(ClassroomChatDraft);
            _selectedClassroomChatThread?.SetSelected(false);
            _selectedClassroomChatThread = value;
            _selectedClassroomChatThread?.SetSelected(true);
            if (_isClassroomChatDrawerOpen) _selectedClassroomChatThread?.MarkRead();
            _classroomChatDraft = _selectedClassroomChatThread?.Draft ?? "";
            ClassroomChatStatus = "";
            Changed();
            Changed(nameof(ClassroomChatDraft));
            Changed(nameof(UnreadClassroomChatCount));
            Changed(nameof(ClassroomChatUnreadSummary));
            Changed(nameof(HasUnreadClassroomChatMessages));
            Changed(nameof(UnreadClassroomChatBadgeText));
            Changed(nameof(ClassroomInteractionNavigationLabel));
            Changed(nameof(SelectedClassroomChatMessages));
            Changed(nameof(HasSelectedClassroomChatThread));
            Changed(nameof(HasSelectedClassroomChatMessages));
            Changed(nameof(HasEmptySelectedClassroomChatThread));
            Changed(nameof(HasNoSelectedClassroomChatThread));
            Changed(nameof(CanSendClassroomChat));
        }
    }
    public ObservableCollection<TeacherClassroomChatMessageItem> SelectedClassroomChatMessages =>
        SelectedClassroomChatThread?.Messages ?? EmptyClassroomChatMessages;
    private static ObservableCollection<TeacherClassroomChatMessageItem> EmptyClassroomChatMessages { get; } = [];
    public string ClassroomChatDraft
    {
        get => _classroomChatDraft;
        set
        {
            if (_classroomChatDraft == value) return;
            _classroomChatDraft = value ?? "";
            _selectedClassroomChatThread?.SetDraft(_classroomChatDraft);
            Changed();
            Changed(nameof(CanSendClassroomChat));
        }
    }
    public string ClassroomChatStatus
    {
        get => _classroomChatStatus;
        private set
        {
            if (_classroomChatStatus == value) return;
            _classroomChatStatus = value;
            Changed();
            Changed(nameof(HasClassroomChatStatus));
        }
    }
    public bool HasClassroomChatStatus => !string.IsNullOrWhiteSpace(ClassroomChatStatus);
    public bool CanSendClassroomChat => HasActiveClassroomSession && !_isClassroomTransitioning &&
        SelectedClassroomChatThread?.IsSupported == true && !string.IsNullOrWhiteSpace(ClassroomChatDraft) &&
        ClassroomEventCryptography.IsValidChatMessage(ClassroomChatDraft);
    public string ClassroomInteractionNavigationLabel
    {
        get
        {
            var unread = UnreadClassroomChatCount;
            var pending = PendingClassroomHelpCount;
            if (unread == 0 && pending == 0) return "课堂控制";
            var details = new List<string>(2);
            if (unread > 0) details.Add($"{unread} 条新消息");
            if (pending > 0) details.Add($"{pending} 条待回复");
            return "课堂控制 · " + string.Join(" · ", details);
        }
    }
    public string ClassroomNavigationLabel => "学生策略";
    internal TeacherClassroomEventContext? GetActiveClassroomEventContext()
    {
        if (_activeClassroomSession is not { } active) return null;
        var targets = ResolveClassroomNetworkTargets(active).ToArray();
        if (targets.Length != active.Targets.Length)
            throw new InvalidDataException("课堂目标与签名设备地址数量不匹配。");
        var targetIds = active.Targets.Select((target, index) => (targets[index], target.TargetId))
            .ToDictionary(item => item.Item1, item => item.TargetId, StringComparer.OrdinalIgnoreCase);
        return new TeacherClassroomEventContext(ResolveClassroomSigningCampus(active), active.SessionId,
            Array.AsReadOnly(targets), targetIds, active,
            ResolveClassroomStudentDisplayNames(active, targets));
    }

    private IReadOnlyDictionary<string, string> ResolveClassroomStudentDisplayNames(ClassroomSession session,
        IReadOnlyList<string> targets)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var room = CampusProfiles.FirstOrDefault(item => item.ProfileId == session.Room.CampusProfileId)?
            .Rooms.FirstOrDefault(item => item.RoomId == session.Room.RoomId);
        IReadOnlyList<ClassroomSeatGridLocation> seats = Array.Empty<ClassroomSeatGridLocation>();
        if (room is not null)
        {
            try
            {
                var layout = ReadClassroomSeatGridForSession(session);
                seats = ClassroomSeatGridStore.GetLocations(layout);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _ = exception;
            }
        }

        var computerNumbers = session.Room.ComputerNumbers ??
            Enumerable.Range(session.Room.StartNumber, session.Room.ComputerCount).ToArray();
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var deviceLabel = index < session.Targets.Length ? session.Targets[index].DeviceLabel : "";
            var seat = FindUniqueClassroomSeat(session, seats, target, deviceLabel, index,
                index < computerNumbers.Length ? computerNumbers[index] : null);

            var studentName = seat?.StudentName?.Trim();
            if ((string.IsNullOrWhiteSpace(studentName) || studentName == "未填写姓名") &&
                SelectedCampusProfile?.ProfileId == session.Room.CampusProfileId)
                studentName = FindConfiguredStudentName(target, deviceLabel);
            names[target] = string.IsNullOrWhiteSpace(studentName) ? "未填写姓名" : studentName;
            if (!string.IsNullOrWhiteSpace(deviceLabel)) names[deviceLabel] = names[target];
            if (!string.IsNullOrWhiteSpace(seat?.VeyonComputerName))
                names[seat.VeyonComputerName] = names[target];
            if (!string.IsNullOrWhiteSpace(seat?.VeyonHostAddress))
                names[seat.VeyonHostAddress] = names[target];
        }
        return names;
    }

    private ClassroomSeatGridLayout ReadClassroomSeatGridForSession(ClassroomSession session)
    {
        var layout = _classroomSeatGridStore.GetLayout(session.Room.CampusProfileId,
            session.Room.RoomId, session.Room.StartNumber);
        if (layout.Seats.Length > 0) return layout;

        var targets = session.Targets.Select(item => item.DeviceLabel).ToArray();
        var recoveredCampus = FindSavedSeatMapCampus(new VeyonNetworkLocation(
            session.Room.RoomName, Array.AsReadOnly(targets)), SelectedCampusProfile);
        if (recoveredCampus is null || recoveredCampus.ProfileId == session.Room.CampusProfileId) return layout;

        var targetSet = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchingRoom = recoveredCampus.Rooms.SingleOrDefault(room =>
            string.Equals(room.DisplayName, session.Room.RoomName, StringComparison.OrdinalIgnoreCase) &&
            targetSet.SetEquals(MacTeacherNetworkDirectory.CreateLocation(recoveredCampus, room).Targets));
        return matchingRoom is null ? layout : _classroomSeatGridStore.GetLayout(
            recoveredCampus.ProfileId, matchingRoom.RoomId, matchingRoom.StartNumber);
    }

    private static ClassroomSeatGridLocation? FindUniqueClassroomSeat(ClassroomSession session,
        IReadOnlyList<ClassroomSeatGridLocation> seats, string target, string deviceLabel, int index,
        int? computerNumberOverride = null)
    {
        var aliasMatches = seats.Where(item =>
            !string.IsNullOrWhiteSpace(item.VeyonHostAddress) &&
            string.Equals(NormalizeTargetKey(item.VeyonHostAddress), NormalizeTargetKey(target),
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(item.VeyonComputerName) &&
            (string.Equals(NormalizeTargetKey(item.VeyonComputerName), NormalizeTargetKey(deviceLabel),
                 StringComparison.OrdinalIgnoreCase) ||
             string.Equals(NormalizeTargetKey(item.VeyonComputerName), NormalizeTargetKey(target),
                 StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(item => item.SeatId).Take(2).ToArray();
        if (aliasMatches.Length > 0) return aliasMatches.Length == 1 ? aliasMatches[0] : null;

        var numbers = session.Room.ComputerNumbers ??
            Enumerable.Range(session.Room.StartNumber, session.Room.ComputerCount).ToArray();
        var computerNumber = computerNumberOverride ??
            (index >= 0 && index < numbers.Length ? numbers[index] : (int?)null);
        if (computerNumber is null) return null;
        var numberMatches = seats.Where(item => item.SeatNumber == computerNumber.Value)
            .DistinctBy(item => item.SeatId).Take(2).ToArray();
        return numberMatches.Length == 1 ? numberMatches[0] : null;
    }

    private string FindConfiguredStudentName(params string[] targetAliases)
    {
        var aliases = targetAliases.Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(NormalizeTargetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = WebsitePolicyTargetChoices.Where(item => aliases.Contains(NormalizeTargetKey(item.Target)))
            .Select(item => item.StudentName.Trim())
            .Where(item => item.Length > 0 && !string.Equals(item, "未填写姓名", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return names.Length == 1 ? names[0] : "未填写姓名";
    }
    internal void SetClassroomEventStatus(string status) => ClassroomEventStatus = status;
    internal void SetClassroomChatStatus(string status) => ClassroomChatStatus = status;
    public void StartClassroomCountdown()
    {
        if (!CanStartClassroomCountdown || _activeClassroomSession is not { } active) return;
        try
        {
            var duration = decimal.ToInt32(ClassroomCountdownMinutes);
            _activeClassroomCountdown = _classroomCountdownStore.Start(active.SessionId, duration,
                DateTimeOffset.UtcNow);
            ClassroomCountdownStatus = "课堂倒计时已启动。";
            RefreshClassroomCountdownDisplay();
            Changed(nameof(HasClassroomCountdown));
            Changed(nameof(IsClassroomCountdownRunning));
            Changed(nameof(CanStartClassroomCountdown));
            Changed(nameof(CanEndClassroomCountdown));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClassroomCountdownStatus = "课堂倒计时无法保存；原文件已保留。";
        }
    }

    public void EndClassroomCountdown()
    {
        if (!CanEndClassroomCountdown || _activeClassroomSession is not { } active) return;
        try
        {
            _ = _classroomCountdownStore.End(active.SessionId);
            _activeClassroomCountdown = null;
            ClassroomCountdownStatus = "已结束倒计时。";
            RefreshClassroomCountdownDisplay();
            Changed(nameof(HasClassroomCountdown));
            Changed(nameof(IsClassroomCountdownRunning));
            Changed(nameof(CanStartClassroomCountdown));
            Changed(nameof(CanEndClassroomCountdown));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClassroomCountdownStatus = "课堂倒计时无法结束；原文件已保留。";
        }
    }

    public void RefreshClassroomCountdownDisplay()
    {
        Changed(nameof(ClassroomCountdownText));
        Changed(nameof(IsClassroomCountdownRunning));
    }

    public void AddClassroomTask()
    {
        if (!CanAddClassroomTask || _activeClassroomSession is not { } active) return;
        try
        {
            _activeClassroomTaskProgress = _classroomTaskProgressStore.Add(active.SessionId,
                ClassroomTaskProgressDraft, DateTimeOffset.UtcNow);
            ClassroomTaskProgressDraft = "";
            ClassroomTaskProgressStatus = "";
            RefreshClassroomTaskProgressItems();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClassroomTaskProgressStatus = "课堂任务进度无法保存；原文件已保留。";
        }
    }

    public void ToggleClassroomTask(Guid taskId)
    {
        if (_activeClassroomSession is not { } active || _activeClassroomTaskProgress is not { } progress) return;
        var task = progress.Tasks.FirstOrDefault(item => item.TaskId == taskId);
        if (task is null) return;
        try
        {
            _activeClassroomTaskProgress = _classroomTaskProgressStore.SetCompleted(active.SessionId,
                taskId, !task.IsCompleted, DateTimeOffset.UtcNow);
            ClassroomTaskProgressStatus = "";
            RefreshClassroomTaskProgressItems();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClassroomTaskProgressStatus = "课堂任务进度无法更新；原文件已保留。";
        }
    }

    public void RemoveClassroomTask(Guid taskId)
    {
        if (_activeClassroomSession is not { } active || _activeClassroomTaskProgress is not { } progress ||
            progress.Tasks.All(item => item.TaskId != taskId)) return;
        try
        {
            _activeClassroomTaskProgress = _classroomTaskProgressStore.Remove(active.SessionId, taskId,
                DateTimeOffset.UtcNow);
            ClassroomTaskProgressStatus = "";
            RefreshClassroomTaskProgressItems();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClassroomTaskProgressStatus = "课堂任务进度无法更新；原文件已保留。";
        }
    }

    internal void SetClassroomRestoreRetrying(bool value)
    {
        if (_isRetryingClassroomRestores == value) return;
        _isRetryingClassroomRestores = value;
        Changed(nameof(CanRetryClassroomRestores));
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanSwitchClassroomScope));
    }

    internal void RefreshClassroomRestoreLedger(string? status = null)
    {
        try
        {
            _pendingClassroomRestores = ClassroomRestoreLedger.ListPending(_classroomModeStateStore.ReadAll(),
                _classroomSessionStore.ReadRecent());
            if (status is not null) _classroomRestoreStatus = status;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _classroomRestoreStatus = "无法读取本机待恢复记录；原记录已保留。";
        }
        Changed(nameof(HasClassroomRestores));
        Changed(nameof(HasPendingClassroomRestoreItems));
        Changed(nameof(PendingClassroomRestoreCount));
        Changed(nameof(ClassroomRestoreSummary));
        Changed(nameof(CanRetryClassroomRestores));
    }

    internal async Task RefreshClassroomRestoreLedgerAsync(string? status = null)
    {
        try
        {
            var pending = await Task.Run(() => ClassroomRestoreLedger.ListPending(
                _classroomModeStateStore.ReadAll(), _classroomSessionStore.ReadRecent()));
            _pendingClassroomRestores = pending;
            if (status is not null) _classroomRestoreStatus = status;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _classroomRestoreStatus = "无法读取本机待恢复记录；原记录已保留。";
        }
        Changed(nameof(HasClassroomRestores));
        Changed(nameof(HasPendingClassroomRestoreItems));
        Changed(nameof(PendingClassroomRestoreCount));
        Changed(nameof(ClassroomRestoreSummary));
        Changed(nameof(CanRetryClassroomRestores));
    }

    internal void SetClassroomNoticeStatus(string status) => ClassroomNoticeStatus = status;
    internal void ResetClassroomEventFeed(Guid? sessionId)
    {
        if (_classroomEventFeedSessionId == sessionId) return;
        _classroomEventFeedSessionId = sessionId;
        IsClassroomChatDrawerOpen = false;
        ClassroomChatSearch = "";
        ClassroomNoticeDraft = "";
        ClassroomNoticeStatus = "";
        ClassroomEventItems.Clear();
        _classroomHelpRows.Clear();
        _classroomChatThreadsByTarget.Clear();
        ClassroomChatThreads.Clear();
        SelectedClassroomChatThread = null;
        ClassroomChatDraft = "";
        ClassroomChatStatus = "";
        _classroomEventIds.Clear();
        _classroomEventOrder.Clear();
        Changed(nameof(HasClassroomEventItems));
        Changed(nameof(PendingClassroomHelpCount));
        Changed(nameof(UnreadClassroomChatCount));
        Changed(nameof(ClassroomChatUnreadSummary));
        Changed(nameof(HasUnreadClassroomChatMessages));
        Changed(nameof(UnreadClassroomChatBadgeText));
        Changed(nameof(HasClassroomChatThreads));
        Changed(nameof(FilteredClassroomChatThreads));
        Changed(nameof(HasClassroomChatSearchResults));
        Changed(nameof(HasNoClassroomChatSearchResults));
        Changed(nameof(HasSelectedClassroomChatThread));
        Changed(nameof(HasSelectedClassroomChatMessages));
        Changed(nameof(HasEmptySelectedClassroomChatThread));
        Changed(nameof(HasNoSelectedClassroomChatThread));
        Changed(nameof(CanSendClassroomChat));
        Changed(nameof(ClassroomInteractionNavigationLabel));
        Changed(nameof(ClassroomNavigationLabel));
    }

    internal void ApplyClassroomEvents(Guid sessionId, IEnumerable<ClassroomEvent> events)
    {
        ResetClassroomEventFeed(sessionId);
        var seatLabels = ReadActiveSeatLabels(sessionId);
        var studentNames = _activeClassroomSession is { } active && active.SessionId == sessionId
            ? ResolveClassroomStudentDisplayNames(active, ResolveClassroomNetworkTargets(active))
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ClassroomEventItems.Where(item => item.IsHelpRequest))
        {
            item.SetSeatLocation(seatLabels.GetValueOrDefault(item.Event.Target));
            item.SetStudentName(studentNames.GetValueOrDefault(item.Event.Target, "未填写姓名"));
        }
        foreach (var classroomEvent in events)
        {
            if (!_classroomEventIds.Add(classroomEvent.EventId))
            {
                if (classroomEvent.Type == ClassroomEventType.ClassroomChatMessage &&
                    classroomEvent.Sender == ClassroomEventSender.Teacher &&
                    _classroomChatThreadsByTarget.TryGetValue(classroomEvent.Target, out var retriedThread))
                    retriedThread.MarkRetrySubmitted(classroomEvent.EventId, classroomEvent.ExpiresUtc);
                continue;
            }
            _classroomEventOrder.Enqueue(classroomEvent.EventId);
            if (classroomEvent.Type == ClassroomEventType.HelpRequested &&
                classroomEvent.Sender == ClassroomEventSender.Student)
            {
                var row = new TeacherClassroomEventItem(classroomEvent,
                    seatLabels.GetValueOrDefault(classroomEvent.Target),
                    studentNames.GetValueOrDefault(classroomEvent.Target, "未填写姓名"));
                _classroomHelpRows[classroomEvent.EventId] = row;
                ClassroomEventItems.Insert(0, row);
            }
            else if (classroomEvent.Type == ClassroomEventType.TeacherReply &&
                     classroomEvent.Sender == ClassroomEventSender.Teacher &&
                     classroomEvent.CorrelationId is { } helpEventId &&
                     _classroomHelpRows.TryGetValue(helpEventId, out var helpRow))
            {
                helpRow.MarkReply(classroomEvent.Message ?? "");
            }
            else if (classroomEvent.Type == ClassroomEventType.HelpAcknowledged &&
                     classroomEvent.CorrelationId is { } acknowledgedHelpId &&
                     _classroomHelpRows.TryGetValue(acknowledgedHelpId, out var acknowledgedRow))
            {
                acknowledgedRow.MarkAcknowledged();
            }
            else if (classroomEvent.Type == ClassroomEventType.HelpResolved &&
                     classroomEvent.Sender == ClassroomEventSender.Student &&
                     classroomEvent.CorrelationId is { } resolvedHelpId &&
                     _classroomHelpRows.TryGetValue(resolvedHelpId, out var resolvedRow))
            {
                resolvedRow.MarkResolved();
            }
            else if (classroomEvent.Type == ClassroomEventType.ClassroomNotice)
            {
                ClassroomEventItems.Insert(0, new TeacherClassroomEventItem(classroomEvent,
                    displayName: studentNames.GetValueOrDefault(classroomEvent.Target, "未填写姓名")));
            }
            else if (classroomEvent.Type == ClassroomEventType.ClassroomChatMessage &&
                     _classroomChatThreadsByTarget.TryGetValue(classroomEvent.Target, out var chatThread))
            {
                chatThread.AddMessage(classroomEvent,
                    isIncoming: classroomEvent.Sender == ClassroomEventSender.Student &&
                        (!IsClassroomChatDrawerOpen ||
                         !ReferenceEquals(chatThread, SelectedClassroomChatThread)));
                Changed(nameof(UnreadClassroomChatCount));
                Changed(nameof(ClassroomChatUnreadSummary));
                Changed(nameof(HasUnreadClassroomChatMessages));
                Changed(nameof(UnreadClassroomChatBadgeText));
                Changed(nameof(ClassroomInteractionNavigationLabel));
                if (ReferenceEquals(chatThread, SelectedClassroomChatThread))
                {
                    Changed(nameof(HasSelectedClassroomChatMessages));
                    Changed(nameof(HasEmptySelectedClassroomChatThread));
                }
            }
            else if (classroomEvent.Type == ClassroomEventType.ClassroomChatDelivered &&
                     classroomEvent.CorrelationId is { } deliveredEventId &&
                     _classroomChatThreadsByTarget.TryGetValue(classroomEvent.Target, out var deliveredThread))
            {
                deliveredThread.MarkDelivered(deliveredEventId);
            }

            while (_classroomEventOrder.Count > ClassroomEventBuffer.MaximumEventsPerSession)
            {
                var expiredEventId = _classroomEventOrder.Dequeue();
                _classroomEventIds.Remove(expiredEventId);
                if (_classroomHelpRows.Remove(expiredEventId, out var expiredRow))
                    ClassroomEventItems.Remove(expiredRow);
            }
        }
        // Keep the teacher view useful during a long class. The event channel is
        // an audit stream; the UI only needs the most recent messages.
        while (ClassroomEventItems.Count > 40)
        {
            var oldest = ClassroomEventItems[^1];
            ClassroomEventItems.RemoveAt(ClassroomEventItems.Count - 1);
            _classroomHelpRows.Remove(oldest.EventId);
        }
        foreach (var row in ClassroomEventItems) row.RefreshExpiry();
        foreach (var thread in ClassroomChatThreads)
            thread.RefreshDeliveryStatuses(DateTimeOffset.UtcNow);
        Changed(nameof(HasClassroomEventItems));
        Changed(nameof(PendingClassroomHelpCount));
        Changed(nameof(HasSelectedClassroomChatMessages));
        Changed(nameof(HasEmptySelectedClassroomChatThread));
        Changed(nameof(ClassroomInteractionNavigationLabel));
        Changed(nameof(ClassroomNavigationLabel));
    }

    internal void SyncClassroomChatTargets(Guid? sessionId, IEnumerable<string> supportedTargets)
    {
        if (sessionId is null || _activeClassroomSession is not { } active || active.SessionId != sessionId)
        {
            if (_classroomChatThreadsByTarget.Count > 0)
            {
                _classroomChatThreadsByTarget.Clear();
                ClassroomChatThreads.Clear();
                SelectedClassroomChatThread = null;
                ClassroomChatStatus = "课堂已结束，聊天记录已清除。";
                Changed(nameof(HasClassroomChatThreads));
                Changed(nameof(FilteredClassroomChatThreads));
                Changed(nameof(HasClassroomChatSearchResults));
                Changed(nameof(HasNoClassroomChatSearchResults));
            }
            return;
        }

        var context = GetActiveClassroomEventContext();
        if (context is null || context.SessionId != sessionId) return;
        var supportedTargetList = supportedTargets.ToArray();
        IReadOnlyList<string> normalizedSupportedTargets = supportedTargetList.Length == 0 ?
            Array.Empty<string>() : WebsitePolicyTransport.NormalizeTargets(supportedTargetList);
        var supported = new HashSet<string>(normalizedSupportedTargets, StringComparer.OrdinalIgnoreCase);
        if (_classroomChatThreadsByTarget.Count == context.Targets.Count &&
            context.Targets.All(target => _classroomChatThreadsByTarget.TryGetValue(target, out var existing) &&
                existing.IsSupported == supported.Contains(target)))
            return;
        IReadOnlyList<ClassroomSeatGridLocation> seats = Array.Empty<ClassroomSeatGridLocation>();
        try
        {
            var layout = _classroomSeatGridStore.GetLayout(active.Room.CampusProfileId, active.Room.RoomId,
                active.Room.StartNumber);
            seats = ClassroomSeatGridStore.GetLocations(layout);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Keep chats addressable by computer label when the optional seating layout cannot be read.
        }

        var available = new HashSet<string>(context.Targets, StringComparer.OrdinalIgnoreCase);
        foreach (var thread in _classroomChatThreadsByTarget.Values.Where(item => !available.Contains(item.Target)).ToArray())
        {
            _classroomChatThreadsByTarget.Remove(thread.Target);
            ClassroomChatThreads.Remove(thread);
            if (ReferenceEquals(SelectedClassroomChatThread, thread)) SelectedClassroomChatThread = null;
        }

        for (var index = 0; index < context.Targets.Count; index++)
        {
            var target = context.Targets[index];
            var deviceLabel = index < active.Targets.Length ? active.Targets[index].DeviceLabel : "";
            var computerNumber = active.Room.ComputerNumbers is { } explicitNumbers &&
                                 index < explicitNumbers.Length ? explicitNumbers[index] : (int?)null;
            var seat = FindUniqueClassroomSeat(active, seats, target, deviceLabel, index, computerNumber);
            var studentName = context.TargetDisplayNames.GetValueOrDefault(target, "未填写姓名");
            var seatName = seat is null ? "" : $"第 {seat.Row} 排 · 第 {seat.Column} 列";
            var title = string.IsNullOrWhiteSpace(studentName) ? "未填写姓名" : studentName;
            var detail = string.Join(" · ", new[] { seatName }
                .Where(item => !string.IsNullOrWhiteSpace(item)));
            var isSupported = supported.Contains(target);
            if (!_classroomChatThreadsByTarget.TryGetValue(target, out var thread))
            {
                thread = new TeacherClassroomChatThread(target, title, detail, isSupported);
                _classroomChatThreadsByTarget.Add(target, thread);
                ClassroomChatThreads.Add(thread);
            }
            else
            {
                thread.UpdateDisplay(title, detail, isSupported);
            }
        }

        if (SelectedClassroomChatThread is null && ClassroomChatThreads.Count > 0)
            SelectedClassroomChatThread = ClassroomChatThreads.FirstOrDefault(item => item.IsSupported);
        Changed(nameof(HasClassroomChatThreads));
        Changed(nameof(FilteredClassroomChatThreads));
        Changed(nameof(HasClassroomChatSearchResults));
        Changed(nameof(HasNoClassroomChatSearchResults));
        Changed(nameof(UnreadClassroomChatCount));
        Changed(nameof(ClassroomChatUnreadSummary));
        Changed(nameof(HasUnreadClassroomChatMessages));
        Changed(nameof(UnreadClassroomChatBadgeText));
        Changed(nameof(ClassroomInteractionNavigationLabel));
        Changed(nameof(CanSendClassroomChat));
    }

    internal void SetClassroomChatSendStatus(string status) => ClassroomChatStatus = status;

    internal void MarkClassroomChatRetrySubmitted(Guid eventId, DateTimeOffset expiresUtc)
    {
        foreach (var thread in _classroomChatThreadsByTarget.Values)
            if (thread.MarkRetrySubmitted(eventId, expiresUtc)) return;
    }

    internal void MarkClassroomEventReply(Guid helpEventId, string message)
    {
        if (_classroomHelpRows.TryGetValue(helpEventId, out var row))
        {
            row.MarkReply(message);
            Changed(nameof(PendingClassroomHelpCount));
            Changed(nameof(ClassroomInteractionNavigationLabel));
            Changed(nameof(ClassroomNavigationLabel));
        }
    }

    internal void SetClassroomEventReplyError(Guid helpEventId, string message)
    {
        if (_classroomHelpRows.TryGetValue(helpEventId, out var row)) row.SetError(message);
    }
    public bool CanToggleClassroomSession => !IsExecuting &&
        !_isClassroomTransitioning && !_isRetryingClassroomRestores && (HasActiveClassroomSession ||
            !_classroomDirectorySelectionPending && !HasClassroomDirectoryError &&
            SelectedCampusProfile is not null &&
            SelectedRoomProfile is { ComputerCount: > 0 } &&
            WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal));

    private void SelectClassroomPolicy(int index)
    {
        _classroomPolicyIndex = index;
        if (!string.IsNullOrWhiteSpace(CampusId) && !string.IsNullOrWhiteSpace(WebsiteTargets))
            AreClassroomTargetsExpanded = false;
        if (index == 0) LoadWebsitePolicyLists(CampusId);
        Changed(nameof(IsWebsitePolicyPage)); Changed(nameof(IsApplicationPolicyPage)); Changed(nameof(IsSystemPolicyPage));
    }
    public bool IsClassroomPage
    {
        get => _selectedPage == "classroom";
        set
        {
            if (!value) return;
            SelectPage("classroom");
        }
    }
    public bool IsClassroomExperiencePage
    {
        get => _selectedPage == "lesson";
        set
        {
            if (!value) return;
            SelectPage("lesson");
            _ = ReadClassroomLocationsAsync();
        }
    }
    public bool IsClassroomWorkspacePage => IsClassroomPage || IsClassroomExperiencePage;
    {
        if (string.Equals(_macVeyonKeyImportStatus, status, StringComparison.Ordinal)) return;
        _macVeyonKeyImportStatus = status;
        Changed(nameof(MacVeyonKeyImportStatus));
    }

    public async Task EnsureClassroomSessionAsync()
    {
        if (HasActiveClassroomSession || !CanToggleClassroomSession) return;
        _isClassroomTransitioning = true;
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanSwitchClassroomScope));
        Changed(nameof(CanCheckRoomConflicts));
        Changed(nameof(CanAddRoomToVeyon));
        Changed(nameof(CanSendClassroomNotice));
        try
        {
            var campus = SelectedCampusProfile ?? throw new InvalidDataException("请先选择一个校区模板。");
            var room = SelectedRoomProfile ?? throw new InvalidDataException("请先选择一个地点模板。");
            var now = DateTimeOffset.UtcNow;
            var campusId = CampusId;
            var started = await Task.Run(() =>
            {
                var session = _classroomSessionStore.StartSession(campus, room.RoomId, now);
                string? ledgerWarning = null;
                try
                {
                    _classroomModeStateStore.Save(new ClassroomModeSessionState(
                        ClassroomModeStateStore.CurrentSchemaVersion, session.SessionId,
                        ClassroomMode.Normal, true, now, []));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  InvalidDataException)
                {
                    ledgerWarning = "课堂持续会话已保存；旧版策略账本暂不可用。" + exception.Message;
                }
                _classroomSigningContextStore.Save(session.SessionId, campusId);
                return (Session: session, LedgerWarning: ledgerWarning);
            }).ConfigureAwait(true);
            _activeClassroomSession = started.Session;
            if (started.LedgerWarning is { } ledgerWarning)
            {
                RefreshClassroomRestoreLedger(ledgerWarning);
            }
            await LoadClassroomSessionProgressAsync(started.Session.SessionId).ConfigureAwait(true);
            RefreshClassroomSessionProperties();
            ClassroomDeliveryStatus = "课堂保持进行中；正在通知当前学生电脑。";
            await PublishClassroomStatusAsync(started.Session, started.Session);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        {
            ClassroomDeliveryStatus = "课堂范围尚未就绪；请检查地点和学生电脑名单。";
        }
        finally
        {
            _isClassroomTransitioning = false;
            RefreshClassroomRestoreLedger();
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanSwitchClassroomScope));
            Changed(nameof(CanCheckRoomConflicts));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanSendClassroomNotice));
        }
    }

    private async Task LoadClassroomSessionProgressAsync(Guid sessionId)
    {
        var loaded = await Task.Run(() =>
        {
            ClassroomCountdown? countdown = null;
            var countdownStatus = "";
            try { countdown = _classroomCountdownStore.Read(sessionId); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException)
            {
                countdownStatus = "无法读取本机倒计时；原文件已保留。";
            }

            ClassroomTaskProgress? taskProgress = null;
            var taskStatus = "";
            try { taskProgress = _classroomTaskProgressStore.Read(sessionId); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException)
            {
                taskStatus = "课堂任务进度无法读取；原文件已保留。";
            }
            return (Countdown: countdown, CountdownStatus: countdownStatus,
                TaskProgress: taskProgress, TaskStatus: taskStatus);
        }).ConfigureAwait(true);
        if (_activeClassroomSession?.SessionId != sessionId) return;
        _activeClassroomCountdown = loaded.Countdown;
        ClassroomCountdownStatus = loaded.CountdownStatus;
        RefreshClassroomCountdownDisplay();
        Changed(nameof(HasClassroomCountdown));
        Changed(nameof(IsClassroomCountdownRunning));
        Changed(nameof(CanStartClassroomCountdown));
        Changed(nameof(CanEndClassroomCountdown));
        _activeClassroomTaskProgress = loaded.TaskProgress;
        ClassroomTaskProgressStatus = loaded.TaskStatus;
        RefreshClassroomTaskProgressItems();
    }

    public async Task<bool> EndClassroomSessionAsync(Guid expectedSessionId)
    {
        if (expectedSessionId == Guid.Empty || _activeClassroomSession is not { } active ||
            active.SessionId != expectedSessionId || _isClassroomTransitioning ||
            IsClassroomCountdownRunning ||
            ClassroomTaskProgressCompletedCount < ClassroomTaskProgressTotalCount)
            return false;

        _isClassroomTransitioning = true;
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanSwitchClassroomScope));
        try
        {
            var closeResult = await Task.Run(() =>
            {
                var modeState = _classroomModeStateStore.Read(expectedSessionId);
                if (modeState?.OwnedPolicies.Count > 0) return (Ended: false, LedgerWarning: (string?)null);
                var ended = _classroomSessionStore.EndSession(expectedSessionId, DateTimeOffset.UtcNow);
                if (ended is null) return (Ended: false, LedgerWarning: (string?)null);
                try
                {
                    _classroomModeStateStore.Save(modeState is null
                        ? new ClassroomModeSessionState(ClassroomModeStateStore.CurrentSchemaVersion,
                            expectedSessionId, ClassroomMode.Normal, false, DateTimeOffset.UtcNow, [])
                        : modeState with { Active = false, UpdatedUtc = DateTimeOffset.UtcNow });
                    return (Ended: true, LedgerWarning: (string?)null);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  InvalidDataException)
                {
                    return (Ended: true,
                        LedgerWarning: "课堂已关闭；旧版课堂恢复记录尚未更新。" + exception.Message);
                }
            }).ConfigureAwait(true);
            if (!closeResult.Ended) return false;

            _activeClassroomSession = null;
            if (closeResult.LedgerWarning is { } warning) RefreshClassroomRestoreLedger(warning);

            RefreshClassroomSessionProperties();
            try
            {
                await PublishClassroomStatusAsync(null, active);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or
                                              CryptographicException or PlatformNotSupportedException)
            {
                ClassroomDeliveryStatus = "课堂已在教师端关闭；部分学生电脑尚未收到结束通知，设备本地安全到期仍有效。";
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            ClassroomDeliveryStatus = "课堂状态未能关闭；请刷新后核对。" + exception.Message;
            return false;
        }
        finally
        {
            _isClassroomTransitioning = false;
            RefreshClassroomRestoreLedger();
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanSwitchClassroomScope));
        }
    }

    internal async Task UpdateActiveClassroomScopeAsync(TeacherClassroomEventContext? previousContext = null)
    {
        if (_activeClassroomSession is null)
        {
            await EnsureClassroomSessionAsync();
            return;
        }
        var selectedCampusId = SelectedCampusProfile?.ProfileId;
        var selectedRoomId = SelectedRoomProfile?.RoomId;
        var campus = selectedCampusId is { } campusId
            ? CampusProfiles.FirstOrDefault(item => item.ProfileId == campusId) : null;
        var room = campus?.Rooms.FirstOrDefault(item => item.RoomId == selectedRoomId);
        if (_isClassroomTransitioning || campus is null || room is not { ComputerCount: > 0 })
        {
            ClassroomDeliveryStatus = "课堂仍保持进行中；请选择一个已配置学生电脑的地点以更新范围。";
            return;
        }

        _isClassroomTransitioning = true;
        Changed(nameof(CanSwitchClassroomScope));
        Changed(nameof(CanCheckRoomConflicts));
        Changed(nameof(CanAddRoomToVeyon));
        Changed(nameof(CanSendClassroomNotice));
        try
        {
            var previous = _activeClassroomSession;
            var updated = await Task.Run(() =>
                _classroomSessionStore.UpdateActiveSession(previous.SessionId, campus, room.RoomId));
            _activeClassroomSession = updated;
            if (previousContext is not null)
            {
                var activeTargetIds = updated.Targets.Select(target => target.TargetId).ToHashSet();
                var removedTargetIds = previous.Targets.Where(target => !activeTargetIds.Contains(target.TargetId))
                    .Select(target => target.TargetId).ToHashSet();
                var previousTargetAddresses = previousContext.TargetIds
                    .Where(pair => removedTargetIds.Contains(pair.Value)).Select(pair => pair.Key).ToArray();
                if (previousTargetAddresses.Length > 0)
                {
                    try
                    {
                        await PublishClassroomStatusAsync(null, previous, explicitTargets: previousTargetAddresses);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                        InvalidDataException or InvalidOperationException or CryptographicException or
                        PlatformNotSupportedException)
                    {
                        ClassroomDeliveryStatus = "课堂范围已更新；移出范围的电脑暂未收到结束通知。";
                    }
                }
            }
            if (previous.Room.CampusProfileId != updated.Room.CampusProfileId)
            {
                try { await Task.Run(() => _classroomSigningContextStore.Clear(updated.SessionId)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                { ClassroomDeliveryStatus = "课堂地点已更新；签名校区上下文需要检查。" + exception.Message; }
            }
            SetClassroomQuickControlSessionNotice("课堂范围已更新，正在读取新范围的管控状态。", clearSnapshot: true);
            RefreshClassroomSessionProperties();
            ClassroomDeliveryStatus = "课堂控制范围已自动更新；正在通知新范围内的学生电脑。";
            await PublishClassroomStatusAsync(updated, updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        {
            ClassroomDeliveryStatus = "课堂仍在进行；新地点范围尚未确认。" + exception.Message;
        }
        finally
        {
            _isClassroomTransitioning = false;
            await RefreshClassroomRestoreLedgerAsync();
            Changed(nameof(CanSwitchClassroomScope));
            Changed(nameof(CanCheckRoomConflicts));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanSendClassroomNotice));
        }
    }

    public async Task RefreshActiveClassroomStatusAsync()
    {
        if (Interlocked.CompareExchange(ref _classroomStatusRefreshInFlight, 1, 0) != 0) return;
        try
        {
            if (_activeClassroomSession is not { } active) return;
            try { await PublishClassroomStatusAsync(active, active, requireCurrentSession: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or
                                              CryptographicException or PlatformNotSupportedException)
            {
                ClassroomDeliveryStatus = "学生互动仍在进行；设备状态暂时无法更新。";
            }
        }
        finally { Volatile.Write(ref _classroomStatusRefreshInFlight, 0); }
    }

    private async Task PublishClassroomStatusAsync(ClassroomSession? activeSession, ClassroomSession targetSession,
        bool requireCurrentSession = false, IReadOnlyList<string>? explicitTargets = null)
    {
        await _classroomStatusPushGate.WaitAsync();
        try
        {
            if (requireCurrentSession && _activeClassroomSession?.SessionId != activeSession?.SessionId) return;
            var campusId = ResolveClassroomSigningCampus(targetSession);
            var targets = explicitTargets is null
                ? ResolveClassroomNetworkTargets(targetSession)
                : WebsitePolicyTransport.NormalizeTargets(explicitTargets);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campusId);
            var command = ClassroomStatusCryptography.Create(campusId, activeSession, DateTimeOffset.UtcNow);
            var results = await ClassroomStatusTransport.SendAsync(targets, command, signingKey.PrivateKey);
            var confirmed = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => result.NeedsReview);
            var failed = results.Count - confirmed - needsReview;
            ClassroomDeliveryStatus = results.Count == 0
                ? "暂无学生电脑。"
                : confirmed == results.Count
                    ? $"课堂状态已确认 {confirmed}/{results.Count} 台。"
                    : $"课堂状态确认：{confirmed}/{results.Count} 台 · 待核对 {needsReview} · 未响应 {failed}";
        }
        finally { _classroomStatusPushGate.Release(); }
    }

    private string ResolveClassroomSigningCampus(ClassroomSession session)
    {
        ReadWebsiteSigningCampuses();
        var pinnedCampus = _classroomSigningContextStore.Read(session.SessionId);
        if (pinnedCampus is not null)
        {
            if (WebsiteSigningCampuses.Contains(pinnedCampus, StringComparer.Ordinal)) return pinnedCampus;
            throw new InvalidDataException("当前互动使用的校区设置不可用；互动记录已保留，学生状态同步已停止。");
        }
        var savedCampus = CampusProfiles.FirstOrDefault(item => item.ProfileId == session.Room.CampusProfileId);
        var profileCampus = savedCampus?.DisplayName ?? session.Room.CampusName;
        var inferredCampus = profileCampus is not null && WebsiteSigningCampuses.Contains(profileCampus, StringComparer.Ordinal)
            ? profileCampus
            : WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal)
                ? CampusId
                : WebsiteSigningCampuses.Count == 1 ? WebsiteSigningCampuses[0] : null;
        if (inferredCampus is not null)
        {
            SaveClassroomSigningContext(session.SessionId, inferredCampus);
            return inferredCampus;
        }
        throw new InvalidDataException(WebsiteSigningCampuses.Count == 0
            ? "请先生成学生校区配置包。"
            : "请选择与学生配置包相同的校区。");
    }

    private void RestoreActiveClassroomSigningContext()
    {
        if (_activeClassroomSession is not { } active) return;
        try
        {
            _ = ResolveClassroomSigningCampus(active);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException)
        {
            ClassroomDeliveryStatus = "无法确认当前互动校区；学生状态同步已暂停。";
        }
    }

    private void SaveClassroomSigningContext(Guid sessionId, string campusId) =>
        _classroomSigningContextStore.Save(sessionId, campusId);

    private IReadOnlyList<string> ResolveClassroomNetworkTargets(ClassroomSession session)
    {
        return WebsitePolicyTransport.NormalizeTargets(session.Targets.Select(target => target.DeviceLabel));
    }

    private void LoadActiveClassroomSession()
    {
        try
        {
            _activeClassroomSession = _classroomSessionStore.ReadActive();
            if (_activeClassroomSession is { } active)
            {
                var campus = CampusProfiles.FirstOrDefault(item => item.ProfileId == active.Room.CampusProfileId);
                if (campus is not null)
                {
                    SelectedCampusProfile = campus;
                    SelectedRoomProfile = RoomProfiles.FirstOrDefault(item => item.RoomId == active.Room.RoomId) ??
                                          RoomProfiles.FirstOrDefault(item => item.ComputerCount > 0);
                }
            }
            RefreshClassroomSessionProperties();
            if (_activeClassroomSession is not null)
            {
                ClassroomDeliveryStatus = "已恢复学生互动；正在等待电脑状态确认。";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _activeClassroomSession = null;
            ClassroomDeliveryStatus = "本机学生互动记录无法读取；原文件已保留。";
        }
        LoadActiveClassroomCountdown();
        LoadActiveClassroomTaskProgress();
    }

    private void LoadActiveClassroomCountdown()
    {
        _activeClassroomCountdown = null;
        if (_activeClassroomSession is not { } active)
        {
            ClassroomCountdownStatus = "";
        }
        else
        {
            try
            {
                _activeClassroomCountdown = _classroomCountdownStore.Read(active.SessionId);
                ClassroomCountdownStatus = "";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                ClassroomCountdownStatus = "无法读取本机倒计时；原文件已保留。";
            }
        }
        RefreshClassroomCountdownDisplay();
        Changed(nameof(HasClassroomCountdown));
        Changed(nameof(IsClassroomCountdownRunning));
        Changed(nameof(CanStartClassroomCountdown));
        Changed(nameof(CanEndClassroomCountdown));
    }

    private void LoadActiveClassroomTaskProgress()
    {
        _activeClassroomTaskProgress = null;
        if (_activeClassroomSession is not { } active)
        {
            ClassroomTaskProgressStatus = "";
        }
        else
        {
            try
            {
                _activeClassroomTaskProgress = _classroomTaskProgressStore.Read(active.SessionId);
                ClassroomTaskProgressStatus = "";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                ClassroomTaskProgressStatus = "课堂任务进度无法读取；原文件已保留。";
            }
        }
        RefreshClassroomTaskProgressItems();
    }

    private void RefreshClassroomTaskProgressItems()
    {
        ClassroomTaskProgressItems.Clear();
        if (_activeClassroomTaskProgress is { } progress)
            foreach (var task in progress.Tasks)
                ClassroomTaskProgressItems.Add(new TeacherClassroomTaskItem(task.TaskId, task.Title,
                    task.IsCompleted));
        Changed(nameof(ClassroomTaskProgressCompletedCount));
        Changed(nameof(ClassroomTaskProgressTotalCount));
        Changed(nameof(HasClassroomTaskProgress));
        Changed(nameof(ClassroomTaskProgressSummary));
        Changed(nameof(CanAddClassroomTask));
    }

    private void RefreshClassroomSessionProperties()
    {
        if (!HasActiveClassroomSession)
        {
            if (_classroomNoticeDraft.Length > 0) ClassroomNoticeDraft = "";
            if (_classroomTaskProgressDraft.Length > 0) ClassroomTaskProgressDraft = "";
        }
        Changed(nameof(HasActiveClassroomSession));
        Changed(nameof(HasNoActiveClassroomSession));
        Changed(nameof(HasPendingClassroomScopeChange));
        Changed(nameof(CanSwitchClassroomScope));
        Changed(nameof(ClassroomNoticeTargetSummary));
        Changed(nameof(ClassroomOverviewDeviceCount));
        Changed(nameof(ClassroomRoomContextSummary));
        Changed(nameof(CanStartClassroomQuickControl));
        Changed(nameof(IsClassroomQuickControlPilotDisabled));
        Changed(nameof(CanReleaseClassroomQuickControl));
        Changed(nameof(HasActiveClassroomEventStatus));
        Changed(nameof(ClassroomSessionActionText));
        Changed(nameof(ClassroomSessionToggleText));
        Changed(nameof(ClassroomSessionSummary));
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanSwitchClassroomScope));
        Changed(nameof(CanDeleteRoomProfile));
        Changed(nameof(CanCheckRoomConflicts));
        Changed(nameof(CanAddRoomToVeyon));
        Changed(nameof(CanSendClassroomNotice));
        Changed(nameof(CanRetryClassroomRestores));
        Changed(nameof(HasClassroomCountdown));
        Changed(nameof(ClassroomCountdownText));
        Changed(nameof(IsClassroomCountdownRunning));
        Changed(nameof(CanStartClassroomCountdown));
        Changed(nameof(CanEndClassroomCountdown));
        Changed(nameof(ClassroomTaskProgressCompletedCount));
        Changed(nameof(ClassroomTaskProgressTotalCount));
        Changed(nameof(HasClassroomTaskProgress));
        Changed(nameof(ClassroomTaskProgressSummary));
}
