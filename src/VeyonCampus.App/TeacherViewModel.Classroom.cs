using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;
using VeyonCampus.App.Maintenance;

namespace VeyonCampus.App;

public sealed partial class TeacherViewModel
{
    // ===== Classroom / events / chat / task members moved from TeacherViewModel.cs =====
    private bool _isImportingInitialSeatGridFromVeyon;
    private (int Row, int Column)? _seatGridDragTarget;
    private string _seatMapDragFeedback = "";
    private string _seatMapIdentityRecoverySummary = "";
    private IReadOnlyList<ClassroomSeatGridExistingBinding> _initialSeatGridConflictBindings =
        Array.Empty<ClassroomSeatGridExistingBinding>();

    public IReadOnlyList<TeacherRoomProfile> RoomProfiles
    {
        get => _roomProfiles;
        private set
        {
            _roomProfiles = value;
            Changed();
            if (_selectedRoomProfile is { } selectedRoom &&
                value.FirstOrDefault(item => item.RoomId == selectedRoom.RoomId) is { } refreshedRoom &&
                refreshedRoom != selectedRoom)
            {
                _selectedRoomProfile = refreshedRoom;
                Changed(nameof(SelectedRoomProfile));
                Changed(nameof(HasSelectedRoomProfile));
                Changed(nameof(SeatGridTitle));
                Changed(nameof(ClassroomSessionSummary));
                Changed(nameof(CanStartClassroomQuickControl));
                Changed(nameof(CanSendClassroomNotice));
                Changed(nameof(CanSendClassroomChat));
                Changed(nameof(CanStartClassroomCountdown));
                Changed(nameof(CanDeleteRoomProfile));
                Changed(nameof(CanSaveRoomProfile));
                Changed(nameof(CanEditRoomProfileNumbering));
                Changed(nameof(HasFixedRoomComputerNumbers));
                SyncStudentRosterFromSelectedRoom();
            }
            Changed(nameof(HasRoomProfiles));
            Changed(nameof(HasNoRoomProfiles));
            Changed(nameof(HasSingleRoomProfile));
            Changed(nameof(HasMultipleRoomProfiles));
            Changed(nameof(ShowRoomLocationSettings));
            Changed(nameof(ClassroomRoomContextSummary));
            Changed(nameof(HasPendingClassroomScopeChange));
            Changed(nameof(CanSwitchClassroomScope));
            Changed(nameof(CanStartClassroomQuickControl));
            Changed(nameof(CanSendClassroomNotice));
            Changed(nameof(CanSendClassroomChat));
            Changed(nameof(CanStartClassroomCountdown));
        }
    }
    public bool HasRoomProfiles => RoomProfiles.Count > 0;
    public bool HasNoRoomProfiles => RoomProfiles.Count == 0;
    public bool HasSingleRoomProfile => RoomProfiles.Count == 1;
    public bool HasMultipleRoomProfiles => RoomProfiles.Count > 1;
    private bool IsClassroomDirectoryScopeVerified => !OperatingSystem.IsWindows() ||
        (IsVeyonDeviceDirectoryCurrent && !_classroomDirectorySelectionPending &&
         !HasClassroomDirectoryError && !HasPendingClassroomScopeChange);

    public string ClassroomRoomContextSummary => !IsClassroomDirectoryScopeVerified
        ? _activeClassroomSession is not null
            ? "课堂范围：Veyon 当前名单待核对；旧会话已保留，课堂操作暂不可用。"
            : SelectedRoomProfile is { } unverifiedRoom
                ? $"课堂范围：{unverifiedRoom.DisplayName} · Veyon 当前名单待核对"
                : "Veyon 当前名单待核对。"
        : _activeClassroomSession is { } active
            ? $"课堂范围：{active.Room.RoomName} · {active.Targets.Length} 台学生电脑"
            : SelectedRoomProfile is { } directoryRoom && CurrentClassroomDirectoryTargetCount is { } targetCount
                ? $"课堂范围：{directoryRoom.DisplayName} · {targetCount} 台学生电脑"
                : SelectedRoomProfile is { } pendingRoom && OperatingSystem.IsWindows()
                    ? $"课堂范围：{pendingRoom.DisplayName} · 尚未读取 Veyon 设备名单"
                    : SelectedRoomProfile is { } fallbackRoom
                        ? $"课堂范围：{fallbackRoom.DisplayName} · {fallbackRoom.ComputerCount} 台学生电脑"
                        : "尚未配置教室";

    private int? CurrentClassroomDirectoryTargetCount => ClassroomLocationIndex >= 0 &&
        ClassroomLocationIndex < WebsiteLocations.Count
            ? WebsiteLocations[ClassroomLocationIndex].Targets.Count
            : null;
    public string RoomSelectionStatus
    {
        get => _roomSelectionStatus;
        private set
        {
            if (_roomSelectionStatus == value) return;
            _roomSelectionStatus = value;
            Changed();
            Changed(nameof(HasRoomSelectionStatus));
        }
    }
    public bool HasRoomSelectionStatus => !string.IsNullOrWhiteSpace(RoomSelectionStatus);
    public IReadOnlyList<TeacherSeatCell> SeatMapCells
    {
        get => _seatMapCells;
        private set
        {
            _seatMapCells = value;
            Changed();
            Changed(nameof(HasSeatMap));
            Changed(nameof(HasNoSeatMap));
            Changed(nameof(CanRetryInitialSeatGridImport));
            Changed(nameof(CanPrintSeatMap));
            Changed(nameof(SeatGridSummary));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanToggleSeatMapEditing));
            Changed(nameof(CanCancelSeatMapEditing));
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(HasSeatsOutsidePendingGrid));
            Changed(nameof(SeatDimensionRemovalSummary));
            NotifyTeacherSetupNavigationStateChanged();
        }
    }
    public bool HasSeatMap => _seatGrid is not null;
    public bool HasNoSeatMap => !HasSeatMap;
    public bool CanPrintSeatMap => HasSeatMap && SeatGridSeatCount > 0;
    public string SeatGridTitle => SelectedRoomProfile?.DisplayName ?? "机房座位图";
    public int SeatGridSeatCount => _seatGrid?.Seats.Length ?? 0;
    public int SeatGridNamedCount => _seatGrid?.Seats.Count(seat => !string.IsNullOrWhiteSpace(seat.StudentName)) ?? 0;
    public int SeatGridComputerCount => _seatGrid?.Seats.Count(seat => seat.ComputerNumber is not null) ?? 0;
    public bool IsImportingInitialSeatGridFromVeyon
    {
        get => _isImportingInitialSeatGridFromVeyon;
        private set
        {
            if (_isImportingInitialSeatGridFromVeyon == value) return;
            _isImportingInitialSeatGridFromVeyon = value;
            Changed();
            Changed(nameof(CanRetryInitialSeatGridImport));
            Changed(nameof(CanToggleSeatMapEditing));
            Changed(nameof(CanEditSeatMap));
            Changed(nameof(CanInteractWithSeatMap));
            Changed(nameof(CanAddSeatGridRow));
            Changed(nameof(CanAddSeatGridColumn));
            Changed(nameof(CanPrepareRoomSeatGrid));
            RefreshSeatMapCells();
        }
    }
    public bool CanRetryInitialSeatGridImport => OperatingSystem.IsWindows() && !IsExecuting &&
        !IsImportingInitialSeatGridFromVeyon && !IsSeatMapEditing && HasSeatMap && SeatGridSeatCount == 0 &&
        SeatMapStatus.StartsWith("无法从 Veyon 自动导入", StringComparison.Ordinal);
    public bool CanOpenInitialSeatGridConflictLocation => OperatingSystem.IsWindows() && !IsExecuting &&
        !IsImportingInitialSeatGridFromVeyon && !IsSeatMapEditing && _initialSeatGridConflictBindings.Count > 0 &&
        GetInitialSeatGridConflictLocation() is not null;
    public int SeatGridNumberPendingCount => _seatGrid?.Seats.Count(seat =>
        !string.IsNullOrWhiteSpace(seat.StudentName) && seat.ComputerNumber is null &&
        seat.VeyonObjectId is null) ?? 0;
    public string SeatGridSummary => $"已添加 {SeatGridSeatCount} 个桌位 · 已填写 {SeatGridNamedCount} 个姓名" +
        (SeatGridNumberPendingCount > 0 ? $" · 待编号 {SeatGridNumberPendingCount}" : "");
    public int SeatGridUnboundComputerCount => _seatGrid?.Seats.Count(seat =>
        seat.ComputerNumber is not null && seat.VeyonObjectId is null) ?? 0;
    public bool CanAssignMissingSeatComputerNumbers => IsSeatMapEditing && !IsExecuting &&
        SeatGridNumberPendingCount > 0;
    public string AssignMissingSeatComputerNumbersLabel => SeatGridNumberPendingCount > 0
        ? $"给待编号座位分配新号（{SeatGridNumberPendingCount}）" : "给待编号座位分配新号";
    public bool CanOpenVeyonConfigurator => OperatingSystem.IsWindows() && !IsExecuting;
    public bool CanDeleteRoomProfile => !IsExecuting && SelectedCampusProfile is { } selectedCampus &&
        SelectedRoomProfile is { } selectedRoom && (_activeClassroomSession?.Room.RoomId != selectedRoom.RoomId ||
        selectedCampus.Rooms.Any(room => room.RoomId != selectedRoom.RoomId && room.ComputerCount > 0));
    public bool IsSeatMapPreview => HasSeatMap && !IsSeatMapEditing && !SeatSwapMode;
    public bool IsSeatMapEditing
    {
        get => _isSeatMapEditing;
        private set
        {
            if (_isSeatMapEditing == value) return;
            _isSeatMapEditing = value;
            Changed();
            Changed(nameof(IsSeatMapPreview));
            Changed(nameof(SeatMapEditButtonLabel));
            Changed(nameof(CanRetryInitialSeatGridImport));
            Changed(nameof(CanEditSeatMap));
            Changed(nameof(CanClearSelectedSeatVeyonTarget));
            Changed(nameof(CanInteractWithSeatMap));
            Changed(nameof(CanToggleSeatMapEditing));
            Changed(nameof(CanToggleSeatAddRemoveMode));
            Changed(nameof(CanAssignMissingSeatComputerNumbers));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(CanAddSeatGridRow));
            Changed(nameof(CanAddSeatGridColumn));
            Changed(nameof(SeatGridRowHeaders));
            Changed(nameof(SeatGridColumnHeaders));
            Changed(nameof(CanUndoSeatMapEdit));
            Changed(nameof(CanRedoSeatMapEdit));
            if (!value)
            {
                _seatGridDragTarget = null;
                SeatMapDragFeedback = "";
                IsSeatAddRemoveMode = false;
            }
            RefreshSeatMapCells();
        }
    }
    public string SeatMapEditButtonLabel => IsSeatMapEditing ? "保存" : "编辑座位";
    public bool CanToggleSeatMapEditing => HasSeatMap && !IsExecuting && !_isSeatMapSyncing &&
        !IsImportingInitialSeatGridFromVeyon && !IsPreparingSeatSwapPlan && !SeatSwapMode;
    public bool CanRetrySeatMapSync => HasRoomCreateError && OperatingSystem.IsWindows() && HasSeatMap && !IsExecuting && !_isSeatMapSyncing;
    public bool CanCancelSeatMapEditing => CanEditSeatMap;
    public bool IsSeatAddRemoveMode
    {
        get => _isSeatAddRemoveMode;
        private set
        {
            if (_isSeatAddRemoveMode == value) return;
            _isSeatAddRemoveMode = value;
            Changed();
            Changed(nameof(SeatAddRemoveModeButtonLabel));
            Changed(nameof(CanToggleSeatAddRemoveMode));
            RefreshSeatMapCells();
        }
    }
    public string SeatAddRemoveModeButtonLabel => IsSeatAddRemoveMode ? "完成添加座位" : "添加座位";
    public bool CanToggleSeatAddRemoveMode => IsSeatMapEditing && !IsExecuting &&
        !IsPreparingSeatSwapPlan && !SeatSwapMode;
    public bool CanStartRoomCreation => OperatingSystem.IsWindows() && !IsExecuting &&
        !IsCheckingRoomConflicts && !IsPreparingSeatSwapPlan && HasSeatMap && IsSeatMapEditing && SeatGridSeatCount > 0 &&
        SeatGridUnboundComputerCount > 0 &&
        !string.IsNullOrWhiteSpace(RoomCampusName) &&
        !string.IsNullOrWhiteSpace(RoomLocationName);
    public IReadOnlyList<TeacherVeyonTargetChoice> SeatVeyonTargetChoices
    {
        get => _seatVeyonTargetChoices;
        private set
        {
            _seatVeyonTargetChoices = value;
            if (_selectedSeatAssociationTarget is { } previousTarget)
                SelectedSeatAssociationTarget = value.SingleOrDefault(choice => choice.ObjectId == previousTarget.ObjectId);
            Changed();
            Changed(nameof(CanBindSelectedSeatVeyonTarget));
            Changed(nameof(CanClearSelectedSeatVeyonTarget));
        }
    }
    public IReadOnlyList<TeacherSeatAssociationChoice> SeatAssociationSeatChoices
    {
        get => _seatAssociationSeatChoices;
        private set
        {
            _seatAssociationSeatChoices = value;
            if (_selectedSeatAssociationSeat is { } previousSeat)
                SelectedSeatAssociationSeat = value.SingleOrDefault(choice => choice.SeatId == previousSeat.SeatId);
            Changed();
            Changed(nameof(CanBindSelectedSeatVeyonTarget));
            Changed(nameof(CanClearSelectedSeatVeyonTarget));
        }
    }
    public TeacherSeatAssociationChoice? SelectedSeatAssociationSeat
    {
        get => _selectedSeatAssociationSeat;
        set
        {
            if (_selectedSeatAssociationSeat == value) return;
            _selectedSeatAssociationSeat = value;
            Changed();
            Changed(nameof(CanBindSelectedSeatVeyonTarget));
            Changed(nameof(CanClearSelectedSeatVeyonTarget));
        }
    }
    public TeacherVeyonTargetChoice? SelectedSeatAssociationTarget
    {
        get => _selectedSeatAssociationTarget;
        set
        {
            if (_selectedSeatAssociationTarget == value) return;
            _selectedSeatAssociationTarget = value;
            Changed();
            Changed(nameof(CanBindSelectedSeatVeyonTarget));
        }
    }
    public string SeatVeyonTargetStatus
    {
        get => _seatVeyonTargetStatus;
        private set { _seatVeyonTargetStatus = value; Changed(); Changed(nameof(HasSeatVeyonTargetStatus)); }
    }
    public bool HasSeatVeyonTargetStatus => SeatVeyonTargetStatus.Length > 0;
    public IReadOnlyList<TeacherVeyonNameSyncConflict> SeatVeyonNameSyncConflicts
    {
        get => _seatVeyonNameSyncConflicts;
        private set { _seatVeyonNameSyncConflicts = value; Changed(); Changed(nameof(HasSeatVeyonNameSyncConflicts)); }
    }
    public bool HasSeatVeyonNameSyncConflicts => SeatVeyonNameSyncConflicts.Count > 0;
    internal void SetSeatNameSyncStatus(string status) => SeatVeyonTargetStatus = status;
    public bool CanBindSelectedSeatVeyonTarget
    {
        get
        {
            if (!CanEditSeatMap || IsReadingSeatVeyonTargets || _seatGrid is null ||
                SelectedSeatAssociationSeat is not { } seat || SelectedSeatAssociationTarget is not { } target ||
                !SeatAssociationSeatChoices.Any(item => item.SeatId == seat.SeatId) ||
                !SeatVeyonTargetChoices.Any(item => item.ObjectId == target.ObjectId) ||
                _seatGrid.Seats.Any(item => item.SeatId != seat.SeatId && item.VeyonObjectId == target.ObjectId))
                return false;
            var current = _seatGrid.Seats.SingleOrDefault(item => item.SeatId == seat.SeatId);
            return current is not null && (current.VeyonObjectId is null || current.VeyonObjectId == target.ObjectId);
        }
    }
    public bool CanClearSelectedSeatVeyonTarget => CanEditSeatMap && !IsReadingSeatVeyonTargets &&
        SelectedSeatAssociationSeat is { VeyonObjectId: not null } seat &&
        SeatAssociationSeatChoices.Any(item => item.SeatId == seat.SeatId);
    public bool CanReadSeatVeyonTargets => OperatingSystem.IsWindows() && !IsExecuting &&
                                           !IsReadingWebsiteLocations && !_isReadingSeatVeyonTargets &&
                                           !IsPreparingSeatSwapPlan &&
                                           SelectedRoomProfile is not null;
    public bool IsReadingSeatVeyonTargets
    {
        get => _isReadingSeatVeyonTargets;
        private set
        {
            _isReadingSeatVeyonTargets = value;
            Changed();
            Changed(nameof(CanReadSeatVeyonTargets));
            Changed(nameof(CanBindSelectedSeatVeyonTarget));
            Changed(nameof(CanClearSelectedSeatVeyonTarget));
            Changed(nameof(CanSwapSeats));
            Changed(nameof(CanSyncSeatGridWithVeyon));
        }
    }
    public bool IsPreparingSeatSwapPlan
    {
        get => _isPreparingSeatSwapPlan;
        private set
        {
            if (_isPreparingSeatSwapPlan == value) return;
            _isPreparingSeatSwapPlan = value;
            Changed();
            Changed(nameof(CanPrepareSeatSwapPlan));
            Changed(nameof(CanSwapSeats));
            Changed(nameof(CanReadSeatVeyonTargets));
            Changed(nameof(CanEditSeatMap));
            Changed(nameof(CanClearSelectedSeatVeyonTarget));
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanInteractWithSeatMap));
            Changed(nameof(CanToggleSeatSwapMode));
            Changed(nameof(CanToggleSeatMapEditing));
            Changed(nameof(CanToggleSeatAddRemoveMode));
            Changed(nameof(CanSyncSeatGridWithVeyon));
            RefreshSeatMapCells();
        }
    }
    public bool CanEditSeatMap => !IsExecuting && !_isSeatMapSyncing && !IsImportingInitialSeatGridFromVeyon &&
        !IsPreparingSeatSwapPlan && IsSeatMapEditing && !SeatSwapMode;
    public bool CanInteractWithSeatMap => !IsExecuting && !_isSeatMapSyncing &&
                                          !IsImportingInitialSeatGridFromVeyon && !IsPreparingSeatSwapPlan &&
                                          (IsSeatMapEditing || SeatSwapMode);
    public bool CanToggleSeatSwapMode => !IsExecuting && !IsPreparingSeatSwapPlan;
    public bool SeatSwapMode
    {
        get => _seatSwapMode;
        set
        {
            if (!CanToggleSeatSwapMode && value != _seatSwapMode)
            {
                Changed();
                return;
            }
            if (_seatSwapMode == value) return;
            _seatSwapMode = value;
            if (value)
            {
                CommitSeatMapEdit();
                IsSeatMapEditing = false;
            }
            _seatSwapSelection.Clear();
            _preparedSeatSwapPlan = null;
            SeatSwapPlanRows = Array.Empty<string>();
            SeatSwapTargetResults = Array.Empty<string>();
            SeatSwapStatus = value
                ? "换座模式已开启：依次点选两个座位，其中一个可以是空座位。"
                : "换座模式已关闭。";
            Changed();
            Changed(nameof(CanPrepareSeatSwapPlan));
            Changed(nameof(CanSwapSeats));
            Changed(nameof(CanExecuteSeatSwapPlan));
            Changed(nameof(SeatSwapSelectionSummary));
            Changed(nameof(IsSeatMapPreview));
            Changed(nameof(CanEditSeatMap));
            Changed(nameof(CanClearSelectedSeatVeyonTarget));
            Changed(nameof(CanInteractWithSeatMap));
            Changed(nameof(CanToggleSeatMapEditing));
            Changed(nameof(CanToggleSeatAddRemoveMode));
            RefreshSeatMapCells();
        }
    }
    public string SeatSwapStatus
    {
        get => _seatSwapStatus;
        private set { _seatSwapStatus = value; Changed(); Changed(nameof(HasSeatSwapStatus)); }
    }
    public bool HasSeatSwapStatus => SeatSwapStatus.Length > 0;
    public IReadOnlyList<string> SeatSwapPlanRows
    {
        get => _seatSwapPlanRows;
        private set { _seatSwapPlanRows = value; Changed(); Changed(nameof(HasSeatSwapPlan)); }
    }
    public IReadOnlyList<string> SeatSwapTargetResults
    {
        get => _seatSwapTargetResults;
        private set { _seatSwapTargetResults = value; Changed(); Changed(nameof(HasSeatSwapTargetResults)); }
    }
    public bool HasSeatSwapPlan => SeatSwapPlanRows.Count > 0;
    public bool HasSeatSwapTargetResults => SeatSwapTargetResults.Count > 0;
    public bool CanPrepareSeatSwapPlan => SeatSwapMode && HasTwoSwapSeatsWithAStudent &&
                                          !IsExecuting && !IsPreparingSeatSwapPlan && _seatGrid is not null;
    public bool CanSwapSeats => SeatSwapMode && HasTwoSwapSeatsWithAStudent &&
                                !IsExecuting && !IsPreparingSeatSwapPlan && !IsReadingSeatVeyonTargets &&
                                _seatGrid is not null;
    private bool HasTwoSwapSeatsWithAStudent => _seatGrid is not null && _seatSwapSelection.Count == 2 &&
        _seatGrid.Seats.Any(seat => _seatSwapSelection.Contains(seat.SeatId) &&
                                    !string.IsNullOrWhiteSpace(seat.StudentName));
    public bool CanExecuteSeatSwapPlan => SeatSwapMode && _preparedSeatSwapPlan is not null &&
                                          !IsExecuting;
    public string SeatSwapSelectionSummary
    {
        get
        {
            if (_seatSwapSelection.Count == 0) return "尚未选择座位。";
            var seats = _seatSwapSelection.Select(id => _seatGrid?.Seats.SingleOrDefault(seat => seat.SeatId == id))
                .Where(seat => seat is not null)
                .Select(seat => string.IsNullOrWhiteSpace(seat!.StudentName)
                    ? $"空座位（第 {seat.Row} 行第 {seat.Column} 列）"
                    : $"{seat.StudentName}（第 {seat.Row} 行第 {seat.Column} 列）")
                .ToArray();
            return $"已选择 {_seatSwapSelection.Count}/2：{string.Join(" ↔ ", seats)}";
        }
    }
    public string SeatGridContext => SelectedCampusProfile is { } campus && SelectedRoomProfile is { } room
        ? string.Equals(campus.DisplayName?.Trim(), room.DisplayName?.Trim(), StringComparison.Ordinal)
            ? room.DisplayName
            : $"{campus.DisplayName} · {room.DisplayName}"
        : "尚未选择地点";

    /// <summary>
    /// Makes the local-room ↔ Veyon-location correspondence explicit. A room that merely has
    /// the same display name is NOT bound; only seat bindings carrying both UUIDs count.
    /// </summary>
    public string VeyonRoomBindingStatus
    {
        get
        {
            if (_seatGrid is null || SelectedRoomProfile is null) return "";
            var bound = _seatGrid.Seats.Where(seat =>
                seat.VeyonObjectId is { } objectId && objectId != Guid.Empty &&
                seat.VeyonLocationObjectId is { } locationId && locationId != Guid.Empty).ToArray();
            if (bound.Length == 0)
                return "本机地点尚未绑定 Veyon 地点 UUID；点「重试 Veyon 同步」后按 Host 建立对应。";
            var locationIds = bound.Select(seat => seat.VeyonLocationObjectId!.Value).Distinct().ToArray();
            if (locationIds.Length != 1)
                return $"本机座位绑定了 {locationIds.Length} 个不同 Veyon 地点 UUID；请先核对目录，不要直接同步。";
            return $"已对应 Veyon 地点「{SelectedRoomProfile.DisplayName}」· 已绑定 {bound.Length} 台电脑";
        }
    }
    public bool HasVeyonRoomBindingStatus => VeyonRoomBindingStatus.Length > 0;

    public void ToggleSeatMapEditing()
    {
        if (!CanToggleSeatMapEditing) return;
        if (!IsSeatMapEditing)
            BeginSeatMapEdit();
        SeatMapStatus = IsSeatMapEditing ? "编辑中；更改尚未保存。可用 Ctrl+Z 撤销。" : "预览中。";
    }

    /// <summary>Persists the current edit draft locally. Veyon synchronization runs afterwards.</summary>
    public bool SaveSeatMapDraftLocally()
    {
        if (!CanEditSeatMap || _seatGrid is null || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room) return false;
        try
        {
            var saved = _classroomSeatGridStore.RestoreLayout(_seatGrid);
            ApplySeatGrid(saved);
            _seatMapEditSnapshot = CloneSeatGrid(saved);
            _seatMapEditOriginalSeatIds = saved.Seats.Select(seat => seat.SeatId).ToHashSet();
            _seatMapUndo.Clear();
            _seatMapRedo.Clear();
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            SeatMapStatus = "本机座位和学生资料已保存；正在核对 Veyon 设备目录。";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "本机座位资料保存失败；没有开始 Veyon 同步。" + exception.Message;
            return false;
        }
    }

    public void FinishSeatMapEditingAfterSave()
    {
        if (!IsSeatMapEditing) return;
        CommitSeatMapEdit();
        IsSeatMapEditing = false;
    }

    public void BeginSeatMapVeyonSync()
    {
        _isSeatMapSyncing = true;
        Changed(nameof(CanEditSeatMap));
        Changed(nameof(CanInteractWithSeatMap));
        Changed(nameof(CanToggleSeatMapEditing));
        Changed(nameof(CanCancelSeatMapEditing));
        Changed(nameof(CanRetrySeatMapSync));
        Changed(nameof(CanAddSeatGridRow));
        Changed(nameof(CanAddSeatGridColumn));
        RefreshSeatMapCells();
    }

    public void EndSeatMapVeyonSync()
    {
        _isSeatMapSyncing = false;
        Changed(nameof(CanEditSeatMap));
        Changed(nameof(CanInteractWithSeatMap));
        Changed(nameof(CanToggleSeatMapEditing));
        Changed(nameof(CanCancelSeatMapEditing));
        Changed(nameof(CanRetrySeatMapSync));
        Changed(nameof(CanAddSeatGridRow));
        Changed(nameof(CanAddSeatGridColumn));
        RefreshSeatMapCells();
    }

    public bool UndoSeatMapEdit()
    {
        if (!CanEditSeatMap || _seatMapUndo.Count == 0) return false;
        _seatMapRedo.Push(CloneSeatGrid(_seatGrid!));
        ApplySeatGridDraft(_seatMapUndo.Pop());
        Changed(nameof(CanUndoSeatMapEdit));
        Changed(nameof(CanRedoSeatMapEdit));
        SeatMapStatus = "已撤销上一步编辑。";
        return true;
    }

    public bool RedoSeatMapEdit()
    {
        if (!CanEditSeatMap || _seatMapRedo.Count == 0) return false;
        _seatMapUndo.Push(CloneSeatGrid(_seatGrid!));
        ApplySeatGridDraft(_seatMapRedo.Pop());
        Changed(nameof(CanUndoSeatMapEdit));
        Changed(nameof(CanRedoSeatMapEdit));
        SeatMapStatus = "已重做上一步编辑。";
        return true;
    }

    private static ClassroomSeatGridLayout CloneSeatGrid(ClassroomSeatGridLayout layout) =>
        layout with { Seats = layout.Seats.Select(seat => seat with { }).ToArray() };

    private void ApplySeatGridDraft(ClassroomSeatGridLayout layout)
    {
        ApplySeatGrid(layout, publishActiveClassroom: false);
    }

    private void RecordSeatMapUndo()
    {
        if (_seatGrid is null) return;
        _seatMapUndo.Push(CloneSeatGrid(_seatGrid));
        _seatMapRedo.Clear();
        Changed(nameof(CanUndoSeatMapEdit));
        Changed(nameof(CanRedoSeatMapEdit));
    }

    public bool CanUndoSeatMapEdit => IsSeatMapEditing && _seatMapUndo.Count > 0;
    public bool CanRedoSeatMapEdit => IsSeatMapEditing && _seatMapRedo.Count > 0;

    public bool AddSeatGridRow()
    {
        if (!CanEditSeatMap || _seatGrid is null || _seatGrid.Rows >= ClassroomSeatGridStore.MaximumEditableRows)
            return false;
        RecordSeatMapUndo();
        ApplySeatGridDraft(_seatGrid with { Rows = _seatGrid.Rows + 1 });
        SeatMapStatus = $"已添加第 {_seatGrid.Rows} 行；尚未保存。";
        return true;
    }

    public bool AddSeatGridColumn()
    {
        if (!CanEditSeatMap || _seatGrid is null || _seatGrid.Columns >= ClassroomSeatGridStore.MaximumEditableColumns)
            return false;
        RecordSeatMapUndo();
        ApplySeatGridDraft(_seatGrid with { Columns = _seatGrid.Columns + 1 });
        SeatMapStatus = $"已添加第 {_seatGrid.Columns} 列；尚未保存。";
        return true;
    }

    public bool DeleteEmptySeatGridRow(int row)
    {
        if (!CanEditSeatMap || _seatGrid is null || row < 1 || row > _seatGrid.Rows) return false;
        if (_seatGrid.Seats.Any(seat => seat.Row == row))
        {
            SeatMapStatus = $"第 {row} 行仍有座位；请先移动或移除这些座位。";
            return false;
        }
        if (_seatGrid.Rows <= 1) { SeatMapStatus = "座位图至少保留一行。"; return false; }
        RecordSeatMapUndo();
        ApplySeatGridDraft(_seatGrid with
        {
            Rows = _seatGrid.Rows - 1,
            Seats = _seatGrid.Seats.Select(seat => seat.Row > row ? seat with { Row = seat.Row - 1 } : seat).ToArray()
        });
        SeatMapStatus = $"已删除空的第 {row} 行；尚未保存。";
        return true;
    }

    public bool DeleteEmptySeatGridColumn(int column)
    {
        if (!CanEditSeatMap || _seatGrid is null || column < 1 || column > _seatGrid.Columns) return false;
        if (_seatGrid.Seats.Any(seat => seat.Column == column))
        {
            SeatMapStatus = $"第 {column} 列仍有座位；请先移动或移除这些座位。";
            return false;
        }
        if (_seatGrid.Columns <= 1) { SeatMapStatus = "座位图至少保留一列。"; return false; }
        RecordSeatMapUndo();
        ApplySeatGridDraft(_seatGrid with
        {
            Columns = _seatGrid.Columns - 1,
            Seats = _seatGrid.Seats.Select(seat => seat.Column > column ? seat with { Column = seat.Column - 1 } : seat).ToArray()
        });
        SeatMapStatus = $"已删除空的第 {column} 列；尚未保存。";
        return true;
    }

    public bool MoveSeatDraft(Guid seatId, int targetRow, int targetColumn)
    {
        if (!CanEditSeatMap || _seatGrid is null || seatId == Guid.Empty || targetRow < 1 ||
            targetRow > _seatGrid.Rows || targetColumn < 1 || targetColumn > _seatGrid.Columns) return false;
        var source = _seatGrid.Seats.SingleOrDefault(seat => seat.SeatId == seatId);
        if (source is null || (source.Row == targetRow && source.Column == targetColumn)) return false;
        RecordSeatMapUndo();
        var target = _seatGrid.Seats.SingleOrDefault(seat => seat.Row == targetRow && seat.Column == targetColumn);
        var seats = _seatGrid.Seats.Select(seat => seat.SeatId == source.SeatId
            ? seat with { Row = targetRow, Column = targetColumn }
            : target is not null && seat.SeatId == target.SeatId
                ? seat with { Row = source.Row, Column = source.Column }
                : seat).ToArray();
        ApplySeatGridDraft(_seatGrid with { Seats = seats });
        SeatMapStatus = target is null ? "座位及其设备关联已移动；尚未保存。" : "两个座位及其设备关联已交换位置；尚未保存。";
        return true;
    }

    public void ReportSeatGridDragTarget(int? row, int? column)
    {
        (int Row, int Column)? next = null;
        if (CanEditSeatMap && row is { } targetRow && column is { } targetColumn &&
            _seatGrid is { } layout && targetRow >= 1 && targetRow <= layout.Rows &&
            targetColumn >= 1 && targetColumn <= layout.Columns)
            next = (targetRow, targetColumn);
        if (_seatGridDragTarget == next) return;
        _seatGridDragTarget = next;
        SeatMapDragFeedback = next is { } target
            ? $"目标座位：第 {target.Row} 行、第 {target.Column} 列"
            : "将鼠标移回座位网格以选择目标座位";
    }

    public void ClearSeatGridDragFeedback()
    {
        _seatGridDragTarget = null;
        SeatMapDragFeedback = "";
    }

    public void ReportSeatGridDragCancelled() => SeatMapStatus = "拖动未完成；座位未移动。";

    public void CancelSeatMapEditing()
    {
        if (!CanCancelSeatMapEditing || _seatMapEditSnapshot is not { } snapshot ||
            SelectedCampusProfile?.ProfileId != snapshot.CampusProfileId ||
            SelectedRoomProfile?.RoomId != snapshot.RoomId)
            return;
        try
        {
            ApplySeatGridDraft(snapshot);
            CommitSeatMapEdit();
            IsSeatMapEditing = false;
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            RefreshClassroomEventSeatLocations();
            SeatMapStatus = "已取消本次编辑；座位、姓名、编号和行列均已恢复到进入编辑前的状态。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "取消编辑失败；当前座位图仍保留，可重试取消。" + exception.Message;
        }
    }

    private void BeginSeatMapEdit()
    {
        if (_seatGrid is null) return;
        _seatMapEditSnapshot = CloneSeatGrid(_seatGrid);
        _seatMapEditOriginalSeatIds = _seatGrid.Seats.Select(seat => seat.SeatId).ToHashSet();
        _seatMapUndo.Clear();
        _seatMapRedo.Clear();
        IsSeatMapEditing = true;
    }

    private void CommitSeatMapEdit()
    {
        _seatMapEditSnapshot = null;
        _seatMapEditOriginalSeatIds.Clear();
        _seatMapUndo.Clear();
        _seatMapRedo.Clear();
        Changed(nameof(CanUndoSeatMapEdit));
        Changed(nameof(CanRedoSeatMapEdit));
        RefreshSeatMapCells();
    }

    public void ToggleSeatAddRemoveMode()
    {
        if (!CanToggleSeatAddRemoveMode) return;
        IsSeatAddRemoveMode = !IsSeatAddRemoveMode;
        SeatMapStatus = IsSeatAddRemoveMode
            ? "添加座位模式已开启；点击空格或按住左键拖动可连续新增座位；拖动只新增，已有座位请单击并确认移除。"
            : "添加座位模式已关闭；点击桌位可编辑学生姓名。";
    }

    public string BuildPrintableSeatChartHtml()
    {
        if (_seatGrid is null || SeatGridSeatCount == 0)
            throw new InvalidOperationException("当前没有可打印的座位表。");

        var orderedSeats = _seatGrid.Seats.OrderBy(seat => seat.Row).ThenBy(seat => seat.Column).ToArray();
        var namedSeats = orderedSeats.Where(seat => !string.IsNullOrWhiteSpace(seat.StudentName)).ToArray();
        var campusName = SelectedCampusProfile?.DisplayName ?? RoomCampusName.Trim();
        var roomName = SelectedRoomProfile?.DisplayName ?? RoomLocationName.Trim();
        var output = new StringBuilder(16_384);
        output.AppendLine("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        output.Append("<title>").Append(System.Net.WebUtility.HtmlEncode(roomName)).AppendLine(" 座位表</title>");
        output.AppendLine("<style>");
        output.AppendLine("@page{size:A3 landscape;margin:8mm}*{box-sizing:border-box}body{font-family:'Microsoft YaHei',sans-serif;color:#173447;margin:0}h1{font-size:20pt;margin:0 0 3mm}.meta{font-size:10pt;color:#526171;margin-bottom:5mm}.toolbar{margin:0 0 5mm}.toolbar button{padding:7px 14px;font-size:11pt}.map{display:grid;gap:1px;width:100%;grid-template-columns:9mm repeat(var(--cols),minmax(0,1fr));border:1px solid #bccbd5;background:#bccbd5}.axis{background:#edf2f6;text-align:center;font-size:7pt;padding:1mm 0;min-height:6mm}.seat{padding:1mm;min-height:8mm;overflow:hidden;display:flex;flex-direction:column;justify-content:center;line-height:1.12}.seat.without-computer{background:#fff;border:1px solid #9cb4c0}.seat.with-computer{background:#dff1ec;border:1px solid #66aa9a}.seat .number{font-size:7pt;font-weight:700;overflow-wrap:anywhere}.seat .name{font-size:7pt;overflow-wrap:anywhere}.aisle{background:#eef2f5;min-height:8mm}.legend{display:flex;flex-wrap:wrap;gap:12px;margin-top:3mm;font-size:8pt;color:#526171}.legend-item{display:inline-flex;align-items:center;gap:4px}.swatch{width:10px;height:10px;border:1px solid #9cb4c0;border-radius:3px;display:inline-block}.swatch.aisle{background:#eef2f5;border-color:#dce3e8}.swatch.without-computer{background:#fff}.swatch.with-computer{background:#dff1ec;border-color:#66aa9a}.note{font-size:8pt;color:#667784;margin-top:3mm}@media print{.toolbar{display:none}.map{break-inside:avoid}.seat,.aisle{min-height:7mm}}");
        output.AppendLine(".seat{background:#fff;border:1px solid #9cb4c0}.swatch.seat{background:#fff}");
        output.AppendLine(".seat .computer-id{font-size:6.5pt;color:#17645a;font-weight:700;overflow-wrap:anywhere}");
        output.AppendLine("</style></head><body>");
        output.AppendLine("<div class=\"toolbar\"><button onclick=\"window.print()\">打印 / 另存为 PDF</button></div>");
        output.Append("<h1>机房座位表</h1><div class=\"meta\">")
            .Append(System.Net.WebUtility.HtmlEncode(campusName)).Append(" · ")
            .Append(System.Net.WebUtility.HtmlEncode(roomName)).Append("　｜　学生 ")
            .Append(SeatGridNamedCount.ToString(CultureInfo.InvariantCulture)).Append(" / ")
            .Append(SeatGridSeatCount.ToString(CultureInfo.InvariantCulture)).Append(" 人　｜　")
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .AppendLine("</div>");
        output.Append("<div class=\"map\" style=\"--cols:")
            .Append(_seatGrid.Columns.ToString(CultureInfo.InvariantCulture)).AppendLine("\">");
        output.AppendLine("<div class=\"axis\">行 / 列</div>");
        for (var column = 1; column <= _seatGrid.Columns; column++)
            output.Append("<div class=\"axis\">").Append(column.ToString(CultureInfo.InvariantCulture)).AppendLine("</div>");

        var seatByPosition = orderedSeats.ToDictionary(seat => (seat.Row, seat.Column));
        for (var row = 1; row <= _seatGrid.Rows; row++)
        {
            output.Append("<div class=\"axis\">").Append(row.ToString(CultureInfo.InvariantCulture)).AppendLine("</div>");
            for (var column = 1; column <= _seatGrid.Columns; column++)
            {
                if (!seatByPosition.TryGetValue((row, column), out var item))
                {
                    output.AppendLine("<div class=\"aisle\"></div>");
                    continue;
                }

                var studentName = string.IsNullOrWhiteSpace(item.StudentName)
                    ? "未填写姓名"
                    : System.Net.WebUtility.HtmlEncode(item.StudentName);
                output.Append("<div class=\"seat\"><div class=\"number\">第 ")
                    .Append(row.ToString(CultureInfo.InvariantCulture)).Append(" 排 · 第 ")
                    .Append(column.ToString(CultureInfo.InvariantCulture)).Append(" 列</div>");
                output.Append("<div class=\"name\">").Append(studentName).AppendLine("</div></div>");
            }
        }
        output.AppendLine("</div><div class=\"legend\"><span class=\"legend-item\"><i class=\"swatch aisle\"></i>空桌位区域</span><span class=\"legend-item\"><i class=\"swatch seat\"></i>桌位</span></div><div class=\"note\">学生姓名保存在教师机。填写姓名且编号留空时自动分配电脑编号；无姓名座位手动分配编号后也会生成电脑。没有编号的座位不会生成电脑。</div></body></html>");
        return output.ToString();
    }

    public void ReportSeatChartExportStatus(string status) => SeatMapStatus = status;

    public bool CanPrepareRoomSeatGrid => !IsExecuting && !IsCheckingRoomConflicts &&
                                         !IsImportingInitialSeatGridFromVeyon &&
                                         !string.IsNullOrWhiteSpace(RoomCampusName) &&
                                         !string.IsNullOrWhiteSpace(RoomLocationName);

    private void TryStartInitialSeatGridImportFromVeyon()
    {
        if (!OperatingSystem.IsWindows() || !IsRoomPage || IsImportingInitialSeatGridFromVeyon ||
            IsSeatMapEditing || IsExecuting || _seatGrid is not { Seats.Length: 0 } ||
            SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room)
            return;

        _ = ImportInitialSeatGridFromVeyonAsync(campus, room);
    }

    public Task RetryInitialSeatGridImportAsync()
    {
        if (!CanRetryInitialSeatGridImport || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room)
            return Task.CompletedTask;
        return ImportInitialSeatGridFromVeyonAsync(campus, room);
    }

    private async Task ImportInitialSeatGridFromVeyonAsync(TeacherCampusProfile campus,
        TeacherRoomProfile room)
    {
        if (IsImportingInitialSeatGridFromVeyon || _seatGrid is not { Seats.Length: 0 } || IsSeatMapEditing)
            return;

        IsImportingInitialSeatGridFromVeyon = true;
        SeatMapStatus = "正在只读读取当前 Veyon 地点和学生姓名；不会修改 Veyon。";
        Guid? importedLocationObjectId = null;
        _initialSeatGridConflictBindings = Array.Empty<ClassroomSeatGridExistingBinding>();
        Changed(nameof(CanOpenInitialSeatGridConflictLocation));
        try
        {
            var snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            if (!IsCurrentSeatGridRoom(campus.ProfileId, room.RoomId)) return;

            var objects = snapshot.Objects;
            var matchingLocations = objects.Where(item =>
                item.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Name, room.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchingLocations.Length != 1 || matchingLocations[0].ObjectId == Guid.Empty)
                throw new InvalidDataException("找不到与当前地点名称唯一对应的 Veyon 地点；请核对地点名称和 Veyon 目录后重试。");

            var location = matchingLocations[0];
            importedLocationObjectId = location.ObjectId;
            if (objects.Count(item => item.ObjectId == location.ObjectId) != 1)
                throw new InvalidDataException("当前 Veyon 地点身份重复，无法安全导入；请先在 Veyon 中处理地点冲突。");

            var allComputers = objects.Where(item =>
                item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase)).ToArray();
            var roomComputers = allComputers.Where(item => item.ParentObjectId == location.ObjectId).ToArray();
            if (roomComputers.Length == 0)
                throw new InvalidDataException("当前 Veyon 地点没有电脑记录；请先在 Veyon 中配置电脑后重试。");
            if (roomComputers.Length > ClassroomSeatGridStore.MaximumSeats)
                throw new InvalidDataException($"当前地点超过 {ClassroomSeatGridStore.MaximumSeats} 台电脑，无法一次导入。");

            var allowedNumbers = room.GetComputerNumbers().ToHashSet();

            var hostOwners = new Dictionary<string, List<VeyonNetworkObject>>(StringComparer.OrdinalIgnoreCase);
            foreach (var computer in allComputers)
            {
                string host;
                try { host = VeyonHostAddress.NormalizeOverride(computer.Host); }
                catch (InvalidDataException)
                {
                    if (computer.ParentObjectId == location.ObjectId)
                        throw new InvalidDataException("当前地点有无效的电脑地址；没有座位资料被导入。");
                    continue;
                }
                if (host.Length == 0)
                {
                    if (computer.ParentObjectId == location.ObjectId)
                        throw new InvalidDataException("当前地点有空的电脑地址；没有座位资料被导入。");
                    continue;
                }
                if (!hostOwners.TryGetValue(host, out var owners))
                    hostOwners.Add(host, owners = []);
                owners.Add(computer);
            }

            var orderedComputers = new List<(VeyonNetworkObject Computer, string Host, int? Number, string StudentName)>(roomComputers.Length);
            var usedObjectIds = new HashSet<Guid>();
            var eligibleNumbers = roomComputers.Select(computer =>
            {
                string host;
                try { host = VeyonHostAddress.NormalizeOverride(computer.Host); }
                catch (InvalidDataException) { return (int?)null; }
                return MachineNaming.TryParseNumber(room.Prefix, host, out var parsedNumber) &&
                       allowedNumbers.Contains(parsedNumber) ? parsedNumber : (int?)null;
            }).Where(number => number is not null).GroupBy(number => number!.Value)
                .Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet();
            foreach (var computer in roomComputers)
            {
                if (computer.ObjectId == Guid.Empty || objects.Count(item => item.ObjectId == computer.ObjectId) != 1 ||
                    !usedObjectIds.Add(computer.ObjectId))
                    throw new InvalidDataException("当前地点有重复或缺失的设备身份；没有座位资料被导入。");
                if (string.IsNullOrWhiteSpace(computer.Name) || computer.Name != computer.Name.Trim() ||
                    computer.Name.Length > 128 || computer.Name.Any(char.IsControl))
                    throw new InvalidDataException("当前地点有无效的电脑显示名称；没有座位资料被导入。");

                var host = VeyonHostAddress.NormalizeOverride(computer.Host);
                if (Uri.CheckHostName(host) != UriHostNameType.Dns)
                    throw new InvalidDataException("当前地点有无效的电脑地址；没有座位资料被导入。");
                if (!hostOwners.TryGetValue(host, out var sameHostOwners) || sameHostOwners.Count != 1)
                    throw new InvalidDataException("发现重复的电脑地址；为避免误绑定，没有导入座位。");

                int? number = null;
                if (MachineNaming.TryParseNumber(room.Prefix, host, out var parsedNumber) &&
                    eligibleNumbers.Contains(parsedNumber))
                    number = parsedNumber;
                var studentName = string.Equals(computer.Name, host, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : computer.Name;
                if (studentName.Length > 100)
                    throw new InvalidDataException("Veyon 中有学生显示名称超过 100 个字符；没有座位资料被导入。");
                orderedComputers.Add((computer, host, number, studentName));
            }

            orderedComputers.Sort((first, second) =>
            {
                var numberOrder = first.Number is null
                    ? second.Number is null ? 0 : 1
                    : second.Number is null ? -1 : first.Number.Value.CompareTo(second.Number.Value);
                if (numberOrder != 0) return numberOrder;
                var nameOrder = StringComparer.OrdinalIgnoreCase.Compare(first.StudentName, second.StudentName);
                return nameOrder != 0 ? nameOrder : StringComparer.OrdinalIgnoreCase.Compare(first.Host, second.Host);
            });
            var currentLayout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId,
                room.StartNumber);
            if (!IsCurrentSeatGridRoom(campus.ProfileId, room.RoomId)) return;
            if (_seatGrid is not { Seats.Length: 0 } || IsSeatMapEditing || currentLayout.Seats.Length > 0)
            {
                if (currentLayout.Seats.Length > 0) ApplySeatGrid(currentLayout);
                SeatMapStatus = "本机座位资料在读取期间已变化；自动导入已取消，现有资料保持不变。";
                return;
            }

            var rows = currentLayout.Rows;
            var columns = currentLayout.Columns;
            if (orderedComputers.Count > rows * columns)
            {
                columns = Math.Max(columns,
                    (orderedComputers.Count + ClassroomSeatGridStore.MaximumEditableRows - 1) /
                    ClassroomSeatGridStore.MaximumEditableRows);
                rows = Math.Max(rows, (orderedComputers.Count + columns - 1) / columns);
                if (rows > ClassroomSeatGridStore.MaximumEditableRows ||
                    columns > ClassroomSeatGridStore.MaximumEditableColumns)
                    throw new InvalidDataException("当前网格无法容纳 Veyon 地点的全部电脑；请先调整座位图尺寸。");
            }

            var seats = orderedComputers.Select((item, index) => new ClassroomSeatGridSeat(
                Guid.NewGuid(), index / columns + 1, index % columns + 1, item.StudentName,
                item.Host, item.Computer.Name, item.Computer.ObjectId, location.ObjectId, item.Number)).ToArray();
            var importedLayout = currentLayout with
            {
                Rows = rows,
                Columns = columns,
                Seats = seats,
                ComputerNumbersInitialized = true
            };
            var saved = _classroomSeatGridStore.PopulateEmptyLayout(importedLayout);
            ApplySeatGrid(saved);
            SeatMapStatus = $"已从 Veyon 只读导入 {saved.Seats.Length} 台电脑和学生姓名；固定电脑编号与设备关联保持不变。可拖动座位调整布局后保存。";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
                                          not StackOverflowException and not AccessViolationException)
        {
            if (!IsCurrentSeatGridRoom(campus.ProfileId, room.RoomId)) return;
            try
            {
                var latest = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId, room.StartNumber);
                if (latest.Seats.Length > 0 && _seatGrid is { Seats.Length: 0 })
                {
                    ApplySeatGrid(latest);
                    SeatMapStatus = "本机座位资料在读取期间已变化；自动导入已取消，现有资料保持不变。";
                    return;
                }
            }
            catch (Exception readException) when (readException is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Keep the original import error as the visible diagnostic.
            }
            if (exception is ClassroomSeatGridBindingConflictException recoveryConflict)
            {
                _initialSeatGridConflictBindings = recoveryConflict.Bindings;
                Changed(nameof(CanOpenInitialSeatGridConflictLocation));
                if (importedLocationObjectId is { } locationObjectId &&
                    TryRestoreExistingSeatGridForVeyonLocation(campus, room, locationObjectId,
                        recoveryConflict.Bindings))
                    return;
            }
            var detail = exception is ClassroomSeatGridBindingConflictException bindingConflict
                ? FormatInitialSeatGridBindingConflicts(bindingConflict.Bindings)
                : exception.Message;
            SeatMapStatus = "无法从 Veyon 自动导入；本机座位表没有更改。请检查 Veyon 目录、地点身份和电脑权限后重试。" +
                            Environment.NewLine + detail;
        }
        finally
        {
            IsImportingInitialSeatGridFromVeyon = false;
            if (IsRoomPage && SelectedCampusProfile is { } currentCampus &&
                SelectedRoomProfile is { } currentRoom && _seatGrid is { Seats.Length: 0 } &&
                (currentCampus.ProfileId != campus.ProfileId || currentRoom.RoomId != room.RoomId))
                TryStartInitialSeatGridImportFromVeyon();
        }
    }

    private (TeacherCampusProfile Campus, TeacherRoomProfile Room)? GetInitialSeatGridConflictLocation()
    {
        var locations = _initialSeatGridConflictBindings
            .Select(binding => (binding.CampusProfileId, binding.RoomId)).Distinct().ToArray();
        if (locations.Length != 1) return null;
        var (campusId, roomId) = locations[0];
        var campus = CampusProfiles.FirstOrDefault(item => item.ProfileId == campusId);
        var room = campus?.Rooms.FirstOrDefault(item => item.RoomId == roomId);
        return campus is null || room is null ? null : (campus, room);
    }

    public void OpenInitialSeatGridConflictLocation()
    {
        if (!CanOpenInitialSeatGridConflictLocation || GetInitialSeatGridConflictLocation() is not { } location)
            return;
        SelectedCampusProfile = location.Campus;
        SelectedRoomProfile = location.Room;
        if (!IsCurrentSeatGridRoom(location.Campus.ProfileId, location.Room.RoomId) ||
            _seatGrid is not { Seats.Length: > 0 })
        {
            SeatMapStatus = "无法打开发生设备关联冲突的本机座位图；原有资料保持不变。请在校区和地点列表中选择冲突地点。";
            return;
        }

        _initialSeatGridConflictBindings = Array.Empty<ClassroomSeatGridExistingBinding>();
        Changed(nameof(CanOpenInitialSeatGridConflictLocation));
        SeatMapStatus = $"已打开已有的本机座位图“{location.Campus.DisplayName} / {location.Room.DisplayName}”；没有重复导入或更改设备。需要解除本机关联时，进入编辑模式后右键座位，选择“解除本机电脑关联”。";
    }

    private bool TryRestoreExistingSeatGridForVeyonLocation(TeacherCampusProfile currentCampus,
        TeacherRoomProfile currentRoom, Guid locationObjectId,
        IReadOnlyList<ClassroomSeatGridExistingBinding> bindings)
    {
        if (locationObjectId == Guid.Empty || bindings.Count == 0 ||
            bindings.Any(binding => binding.VeyonLocationObjectId != locationObjectId))
            return false;

        var localLocations = bindings.Select(binding => (binding.CampusProfileId, binding.RoomId))
            .Distinct().ToArray();
        if (localLocations.Length != 1) return false;
        var (campusProfileId, roomId) = localLocations[0];
        var campus = CampusProfiles.FirstOrDefault(item => item.ProfileId == campusProfileId);
        var room = campus?.Rooms.FirstOrDefault(item => item.RoomId == roomId);
        if (campus is null || room is null ||
            !string.Equals(campus.DisplayName, currentCampus.DisplayName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(room.DisplayName, currentRoom.DisplayName, StringComparison.OrdinalIgnoreCase))
            return false;

        ClassroomSeatGridLayout existing;
        try
        {
            existing = _classroomSeatGridStore.GetLayout(campusProfileId, roomId, room.StartNumber);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }

        if (existing.Seats.Length == 0 || bindings.Any(binding =>
                existing.Seats.SingleOrDefault(seat => seat.Row == binding.Row && seat.Column == binding.Column)
                    ?.VeyonLocationObjectId != locationObjectId))
            return false;

        SelectedCampusProfile = campus;
        SelectedRoomProfile = room;
        if (!IsCurrentSeatGridRoom(campusProfileId, roomId)) return false;
        ApplySeatGrid(existing);
        _initialSeatGridConflictBindings = Array.Empty<ClassroomSeatGridExistingBinding>();
        Changed(nameof(CanOpenInitialSeatGridConflictLocation));
        SeatMapStatus = $"检测到当前 Veyon 地点已对应另一份本机地点档案，已切换到保留原座位资料的“{campus.DisplayName} / {room.DisplayName}”。没有导入或更改座位数据。";
        if (RoomNames.Count > 0) UpdateRoomPreviewRows();
        InvalidateRoomReview();
        RefreshClassroomEventSeatLocations();
        return true;
    }

    private bool IsCurrentSeatGridRoom(Guid campusProfileId, Guid roomId) =>
        SelectedCampusProfile?.ProfileId == campusProfileId && SelectedRoomProfile?.RoomId == roomId;

    private string FormatInitialSeatGridBindingConflicts(
        IReadOnlyList<ClassroomSeatGridExistingBinding> bindings)
    {
        var locations = bindings.Select(binding =>
        {
            var campus = CampusProfiles.FirstOrDefault(item => item.ProfileId == binding.CampusProfileId);
            var room = campus?.Rooms.FirstOrDefault(item => item.RoomId == binding.RoomId);
            var location = campus is null || room is null
                ? "其他本机地点"
                : $"{campus.DisplayName} / {room.DisplayName}";
            return $"{location} 第 {binding.Row} 行第 {binding.Column} 列";
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var shownLocations = string.Join("；", locations.Take(3));
        var remainingCount = locations.Length - Math.Min(locations.Length, 3);
        if (remainingCount > 0) shownLocations += $"；另有 {remainingCount} 个座位";
        return $"发现导入设备已关联到其他本机座位：{shownLocations}。为避免重复绑定，本次没有导入。请先到原地点检查并解除旧关联，然后重试。";
    }

    public string RoomCampusName
    {
        get => _roomCampusName;
        set
        {
            if (_roomCampusName == value) return;
            var previousCampusName = _roomCampusName.Trim();
            _roomCampusName = value ?? "";
            Changed();
            if (!HasSeatMap && (string.IsNullOrWhiteSpace(RoomLocationName) ||
                                string.Equals(RoomLocationName.Trim(), previousCampusName,
                                    StringComparison.OrdinalIgnoreCase)))
                RoomLocationName = _roomCampusName.Trim();
            Changed(nameof(CanPrepareRoomSeatGrid));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(RoomSummary));
            InvalidateRoomReview();
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            ClearRoomCreateFeedback();
        }
    }
    public decimal SeatRows
    {
        get => _seatRows;
        private set { if (_seatRows == value) return; _seatRows = value; Changed(); }
    }
    public decimal SeatColumns
    {
        get => _seatColumns;
        private set { if (_seatColumns == value) return; _seatColumns = value; Changed(); }
    }
    public decimal SeatRowsDraft
    {
        get => _seatRowsDraft;
        set
        {
            if (_seatRowsDraft == value) return;
            _seatRowsDraft = value;
            Changed();
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(SeatDimensionRemovalSummary));
        }
    }
    public decimal SeatColumnsDraft
    {
        get => _seatColumnsDraft;
        set
        {
            if (_seatColumnsDraft == value) return;
            _seatColumnsDraft = value;
            Changed();
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(SeatDimensionRemovalSummary));
        }
    }
    public decimal SeatDimensionMinimum => 1;
    public decimal SeatDimensionMaximum => ClassroomSeatGridStore.MaximumEditableRows;
    public int SeatGridColumns => Math.Clamp((int)_seatColumns, 1, ClassroomSeatGridStore.MaximumColumns);
    public IReadOnlyList<TeacherSeatGridAxis> SeatGridRowHeaders => _seatGrid is null
        ? Array.Empty<TeacherSeatGridAxis>()
        : Enumerable.Range(1, _seatGrid.Rows).Select(index => new TeacherSeatGridAxis(index, $"{index}", true, CanEditSeatMap)).ToArray();
    public IReadOnlyList<TeacherSeatGridAxis> SeatGridColumnHeaders => _seatGrid is null
        ? Array.Empty<TeacherSeatGridAxis>()
        : Enumerable.Range(1, _seatGrid.Columns).Select(index => new TeacherSeatGridAxis(index, $"{index}", false, CanEditSeatMap)).ToArray();
    public bool CanAddSeatGridRow => CanEditSeatMap && _seatGrid is not null &&
                                    _seatGrid.Rows < ClassroomSeatGridStore.MaximumEditableRows;
    public bool CanAddSeatGridColumn => CanEditSeatMap && _seatGrid is not null &&
                                       _seatGrid.Columns < ClassroomSeatGridStore.MaximumEditableColumns;
    public bool CanApplySeatGridDimensions => CanEditSeatMap && _seatGrid is not null &&
        _seatRowsDraft == decimal.Truncate(_seatRowsDraft) &&
        _seatColumnsDraft == decimal.Truncate(_seatColumnsDraft) &&
        _seatRowsDraft is >= 1 and <= ClassroomSeatGridStore.MaximumEditableRows &&
        _seatColumnsDraft is >= 1 and <= ClassroomSeatGridStore.MaximumEditableColumns &&
        (_seatRowsDraft != _seatRows || _seatColumnsDraft != _seatColumns);
    public bool HasSeatsOutsidePendingGrid => _seatGrid?.Seats.Any(seat =>
        seat.Row > _seatRowsDraft || seat.Column > _seatColumnsDraft) == true;
    public string SeatDimensionRemovalSummary => HasSeatsOutsidePendingGrid
        ? $"将删除网格外的 {_seatGrid!.Seats.Count(seat => seat.Row > _seatRowsDraft || seat.Column > _seatColumnsDraft)} 个座位，确认后生效。"
        : "不会删除现有座位。";
    public string SeatMapStatus
    {
        get => _seatMapStatus;
        private set
        {
            if (_seatMapStatus == value) return;
            _seatMapStatus = value;
            Changed();
            Changed(nameof(HasSeatMapStatus));
            Changed(nameof(HasSeatMapError));
            Changed(nameof(HasSeatMapInfo));
            Changed(nameof(CanRetryInitialSeatGridImport));
        }
    }
    public string SeatMapDragFeedback
    {
        get => _seatMapDragFeedback;
        private set
        {
            if (_seatMapDragFeedback == value) return;
            _seatMapDragFeedback = value;
            Changed();
            Changed(nameof(HasSeatMapDragFeedback));
        }
    }
    public bool HasSeatMapDragFeedback => SeatMapDragFeedback.Length > 0;
    public bool HasSeatMapStatus => SeatMapStatus.Length > 0;
    public bool HasSeatMapError => SeatMapStatus.StartsWith("无法", StringComparison.Ordinal) ||
                                   SeatMapStatus.StartsWith("座位图", StringComparison.Ordinal);
    public bool HasSeatMapInfo => HasSeatMapStatus && !HasSeatMapError;
    public TeacherCampusProfile? SelectedCampusProfile
    {
        get => _selectedCampusProfile is { } selected
            ? CampusProfiles.FirstOrDefault(item => item.ProfileId == selected.ProfileId) ?? selected
            : null;
        set
        {
            if (_selectedCampusProfile?.ProfileId == value?.ProfileId) return;
            _selectedCampusProfile = value;
            Changed();
            Changed(nameof(HasSelectedCampusProfile));
            RoomProfiles = value?.Rooms.ToArray() ?? Array.Empty<TeacherRoomProfile>();
            var preferredRoomId = value is not null && _recentRoomSelections.TryGetValue(value.ProfileId, out var roomId)
                ? roomId
                : Guid.Empty;
            SelectedRoomProfile = RoomProfiles.FirstOrDefault(item => item.RoomId == preferredRoomId) ??
                                  RoomProfiles.FirstOrDefault();
            CampusProfileName = value?.DisplayName ?? "";
            RoomCampusName = value?.DisplayName ?? "本机校区";
            CampusDirectoryError = "";
            Changed(nameof(ClassroomSessionSummary));
            Changed(nameof(ClassroomSessionActionText));
            Changed(nameof(HasPendingClassroomScopeChange));
            Changed(nameof(ClassroomOverviewDeviceCount));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanSwitchClassroomScope));
            Changed(nameof(CanStartClassroomQuickControl));
            Changed(nameof(CanSendClassroomNotice));
            Changed(nameof(CanSendClassroomChat));
            Changed(nameof(CanStartClassroomCountdown));
            Changed(nameof(SeatGridContext));
            if (_seatGrid?.CampusProfileId != value?.ProfileId ||
                _seatGrid?.RoomId != SelectedRoomProfile?.RoomId)
                LoadSelectedSeatGrid();
        }
    }
    public bool HasSelectedCampusProfile => SelectedCampusProfile is not null;
    public bool CanSaveRoomProfile => !IsExecuting && SelectedCampusProfile is not null &&
                                     SelectedRoomProfile is not null;
    public bool CanEditRoomProfileNumbering => !IsExecuting && SelectedRoomProfile?.ComputerNumbers is null;
    public bool HasFixedRoomComputerNumbers => SelectedRoomProfile?.ComputerNumbers is not null;
    public TeacherRoomProfile? SelectedRoomProfile
    {
        get => _selectedRoomProfile;
        set
        {
            if (_selectedRoomProfile?.RoomId == value?.RoomId) return;
            _selectedRoomProfile = value;
            SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
            SeatVeyonNameSyncConflicts = Array.Empty<TeacherVeyonNameSyncConflict>();
            SeatVeyonTargetStatus = "";
            Changed();
            Changed(nameof(HasSelectedRoomProfile));
            Changed(nameof(CanSaveRoomProfile));
            Changed(nameof(CanEditRoomProfileNumbering));
            Changed(nameof(HasFixedRoomComputerNumbers));
            Changed(nameof(ClassroomRoomContextSummary));
            Changed(nameof(HasMultipleRoomProfiles));
            Changed(nameof(HasSingleRoomProfile));
            Changed(nameof(HasNoRoomProfiles));
            Changed(nameof(ClassroomSessionSummary));
            Changed(nameof(ClassroomSessionActionText));
            Changed(nameof(HasPendingClassroomScopeChange));
            Changed(nameof(ClassroomOverviewDeviceCount));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanSwitchClassroomScope));
            Changed(nameof(CanStartClassroomQuickControl));
            Changed(nameof(CanSendClassroomNotice));
            Changed(nameof(CanSendClassroomChat));
            Changed(nameof(CanStartClassroomCountdown));
            Changed(nameof(CanReadSeatVeyonTargets));
            Changed(nameof(CanDeleteRoomProfile));
            Changed(nameof(VeyonRoomBindingStatus));
            Changed(nameof(HasVeyonRoomBindingStatus));
            if (value is null)
            {
                RoomProfileName = "";
                RoomProfilePrefix = "PC-";
                RoomProfileStart = "1";
                RoomProfileCount = "150";
            }
            else
            {
                RoomProfileName = value.DisplayName;
                RoomProfilePrefix = value.Prefix;
                RoomProfileStart = value.StartNumber.ToString(CultureInfo.InvariantCulture);
                RoomProfileCount = value.ComputerCount.ToString(CultureInfo.InvariantCulture);
            }
            RoomCampusName = SelectedCampusProfile?.DisplayName ?? "本机校区";
            RoomLocationName = value?.DisplayName ?? "";
            RoomPrefix = value?.Prefix ?? "PC-";
            RoomStart = value?.StartNumber.ToString(CultureInfo.InvariantCulture) ?? "1";
            CampusDirectoryError = "";
            LoadSelectedSeatGrid();
            SyncStudentRosterFromSelectedRoom();
            Changed(nameof(SeatGridContext));
            SaveRecentRoomSelection(value);
        }
    }

    private void SyncStudentRosterFromSelectedRoom()
    {
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room)
        {
            WebsiteTargets = "";
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            var location = MacTeacherNetworkDirectory.CreateLocation(campus, room);
            WebsiteTargets = string.Join(Environment.NewLine, location.Targets);
            return;
        }

        var matches = WebsiteLocations.Select((location, index) => (location, index))
            .Where(item => string.Equals(item.location.Name, room.DisplayName,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1)
        {
            WebsiteLocationIndex = matches[0].index;
            FillWebsiteTargetsFromSelectedLocation();
        }
        else
        {
            // A local room profile is not evidence that these computers exist in Veyon.
            WebsiteTargets = "";
            WebsiteLocationIndex = -1;
        }
    }

    /// <summary>Validates UUID-bound devices and binds new fixed-number seats only by unique Host and location UUID.</summary>
    public async Task<string?> ReconcileSeatVeyonBindingsByHostAsync()
    {
        if (!OperatingSystem.IsWindows() || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room || _seatGrid is null)
            return "当前系统或地点不支持自动读取 Veyon 设备目录。";

        var campusId = campus.ProfileId;
        var roomId = room.RoomId;
        _seatMapIdentityRecoverySummary = "";
        try
        {
            var snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            if (SelectedCampusProfile?.ProfileId != campusId || SelectedRoomProfile?.RoomId != roomId)
                return "读取期间地点发生变化；没有应用设备关联。";

            var objects = snapshot.Objects;
            var locationMatches = objects.Where(item => item.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Name, room.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (locationMatches.Length > 1) return "Veyon 中存在多个同名地点；为避免误绑定，请先核对地点目录。";
            var locationId = locationMatches.Length == 1 ? locationMatches[0].ObjectId : Guid.Empty;
            var computers = objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase)).ToArray();
            var usedIds = new HashSet<Guid>();
            foreach (var boundSeat in _seatGrid.Seats.Where(seat => seat.VeyonObjectId is not null))
                if (!usedIds.Add(boundSeat.VeyonObjectId!.Value))
                    return "本机座位表中有重复的设备关联；没有改动任何关联。请在诊断中重新核对。";

            var pendingBindings = new List<ClassroomSeatGridTargetBinding>();
            var missingBindings = new List<ClassroomSeatGridStaleVeyonBinding>();
            var reboundByHost = 0;
            foreach (var seat in _seatGrid.Seats.Where(seat => seat.VeyonObjectId is not null))
            {
                var matches = computers.Where(item => item.ObjectId == seat.VeyonObjectId).ToArray();
                if (matches.Length == 0)
                {
                    if (seat.ComputerNumber is not { } number)
                        return "已有设备 UUID 已消失，且座位没有固定电脑编号；原绑定保持不变，请先核对设备关联。";
                    var expectedHost = MachineNaming.CreateName(room.Prefix,
                        number.ToString(CultureInfo.InvariantCulture));
                    string oldHost;
                    try { oldHost = VeyonHostAddress.NormalizeOverride(seat.VeyonHostAddress); }
                    catch (InvalidDataException) { return "已有设备 UUID 已消失，且座位 Host 无效；原绑定保持不变。"; }
                    if (!string.Equals(oldHost, expectedHost, StringComparison.OrdinalIgnoreCase))
                        return "已有设备 UUID 已消失，且保存的 Host 与固定电脑编号不一致；原绑定保持不变，请核对身份。";

                    var sameHostForRecovery = computers.Where(item =>
                    {
                        try { return string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), expectedHost,
                            StringComparison.OrdinalIgnoreCase); }
                        catch (InvalidDataException) { return false; }
                    }).ToArray();
                    if (sameHostForRecovery.Length > 1)
                        return "旧 UUID 已消失，但相同固定 Host 在 Veyon 中重复；原绑定保持不变，请先处理冲突。";
                    missingBindings.Add(new ClassroomSeatGridStaleVeyonBinding(seat.SeatId,
                        seat.VeyonObjectId!.Value, seat.VeyonLocationObjectId, seat.VeyonHostAddress ?? ""));
                    if (sameHostForRecovery.Length == 1)
                    {
                        if (locationId == Guid.Empty || sameHostForRecovery[0].ParentObjectId != locationId ||
                            sameHostForRecovery[0].ObjectId == Guid.Empty || !usedIds.Add(sameHostForRecovery[0].ObjectId))
                            return "旧 UUID 已消失，但固定 Host 对应的 Veyon 电脑不属于唯一当前地点或已被其他座位占用；原绑定保持不变。";
                        pendingBindings.Add(new ClassroomSeatGridTargetBinding(seat.SeatId, expectedHost,
                            sameHostForRecovery[0].Name, sameHostForRecovery[0].ObjectId, locationId, number));
                        reboundByHost++;
                    }
                    continue;
                }
                if (matches.Length != 1 || locationId == Guid.Empty || matches[0].ParentObjectId != locationId ||
                    matches[0].ParentObjectId != seat.VeyonLocationObjectId)
                    return "已有设备 UUID 重复或不再属于当前地点；原绑定保持不变，请核对设备关联。";
                string actualHost;
                try { actualHost = VeyonHostAddress.NormalizeOverride(matches[0].Host); }
                catch (InvalidDataException) { return "已有设备 Host 无效；原绑定保持不变，请核对设备关联。"; }
                if (Uri.CheckHostName(actualHost) != UriHostNameType.Dns ||
                    !string.Equals(actualHost, seat.VeyonHostAddress, StringComparison.OrdinalIgnoreCase))
                    return "已有设备 Host 已变化或无效；原绑定保持不变，请核对设备关联。";
                var sameHost = computers.Count(item =>
                {
                    try { return string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), actualHost,
                        StringComparison.OrdinalIgnoreCase); }
                    catch (InvalidDataException) { return false; }
                });
                if (sameHost != 1) return "已有设备 Host 在 Veyon 中不唯一；原绑定保持不变，请处理设备冲突。";
            }

            foreach (var seat in _seatGrid.Seats.Where(seat => seat.VeyonObjectId is null &&
                                                                  seat.ComputerNumber is not null))
            {
                var expectedHost = MachineNaming.CreateName(room.Prefix,
                    seat.ComputerNumber!.Value.ToString(CultureInfo.InvariantCulture));
                var matches = computers.Where(item =>
                {
                    try { return string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), expectedHost,
                        StringComparison.OrdinalIgnoreCase); }
                    catch (InvalidDataException) { return false; }
                }).ToArray();
                if (matches.Length == 0) continue;
                if (matches.Length != 1 || locationId == Guid.Empty || matches[0].ParentObjectId != locationId ||
                    matches[0].ObjectId == Guid.Empty)
                    return $"电脑编号 {seat.ComputerNumber} 已有 Veyon 记录，但身份或地点不唯一；没有自动绑定。";
                if (!usedIds.Add(matches[0].ObjectId))
                    return $"电脑编号 {seat.ComputerNumber} 对应的设备已绑定到其他座位；没有自动绑定。";
                pendingBindings.Add(new ClassroomSeatGridTargetBinding(seat.SeatId, expectedHost,
                    matches[0].Name, matches[0].ObjectId, locationId, seat.ComputerNumber));
            }

            if (missingBindings.Count > 0)
            {
                var missingOnDisk = missingBindings.Count - reboundByHost;
                _seatMapIdentityRecoverySummary = reboundByHost > 0 && missingOnDisk > 0
                    ? $"已按唯一固定电脑名恢复 {reboundByHost} 条本机设备关联；另有 {missingOnDisk} 台已不存在的电脑将按原电脑名重建。"
                    : reboundByHost > 0
                        ? $"已按唯一固定电脑名恢复 {reboundByHost} 条本机设备关联。"
                        : $"已确认 {missingBindings.Count} 台旧 Veyon 电脑对象不存在；保留固定 Host，将在本次同步中重新创建。";
            }

            if (pendingBindings.Count > 0)
            {
                if (missingBindings.Count > 0)
                {
                    var cleared = _classroomSeatGridStore.ClearMissingVeyonTargets(campusId, roomId,
                        missingBindings);
                    ApplySeatGrid(cleared);
                }
                var updated = _classroomSeatGridStore.AssignVeyonTargets(campusId, roomId, pendingBindings);
                ApplySeatGrid(updated);
            }
            else if (missingBindings.Count > 0)
            {
                var updated = _classroomSeatGridStore.ClearMissingVeyonTargets(campusId, roomId,
                    missingBindings);
                ApplySeatGrid(updated);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          InvalidOperationException or TimeoutException or PlatformNotSupportedException or
                                          System.ComponentModel.Win32Exception)
        {
            return "Veyon 设备目录读取或 UUID 对账失败：" + exception.Message;
        }
    }

    public bool HasSelectedRoomProfile => SelectedRoomProfile is not null;

    private void SaveRecentRoomSelection(TeacherRoomProfile? room)
    {
        if (room is null || SelectedCampusProfile is not { } campus || _roomSelectionStore is null) return;
        try
        {
            _roomSelectionStore.Save(campus.ProfileId, room.RoomId);
            _recentRoomSelections[campus.ProfileId] = room.RoomId;
            RoomSelectionStatus = "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RoomSelectionStatus = "无法保存最近教室；本次运行仍会使用当前选择。";
        }
    }

    internal bool SaveSeatCell(int row, int column, string studentName, string? computerNumberText = null)
    {
        if (!CanEditSeatMap || _seatGrid is null) return false;
        try
        {
            if (row < 1 || row > _seatGrid.Rows || column < 1 || column > _seatGrid.Columns)
                throw new InvalidDataException("所选格子已超出当前座位图范围。");
            studentName = studentName.Trim();
            if (studentName.Length > 100 || studentName.Any(char.IsControl))
                throw new InvalidDataException("学生姓名最多 100 个字符，且不能包含控制字符。");
            var currentSeat = _seatGrid.Seats.SingleOrDefault(seat => seat.Row == row && seat.Column == column);
            int? computerNumber = null;
            if (!string.IsNullOrWhiteSpace(computerNumberText))
            {
                if (!int.TryParse(computerNumberText, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var parsedNumber) || parsedNumber is < 1 or > MachineNaming.MaximumNumber)
                    throw new InvalidDataException($"电脑编号请输入 1–{MachineNaming.MaximumNumber} 的整数。");
                computerNumber = parsedNumber;
            }
            int? assignedNumber = computerNumber;
            if (assignedNumber is null)
            {
                if (currentSeat?.VeyonObjectId is not null)
                    assignedNumber = currentSeat.ComputerNumber;
                else if (!string.IsNullOrWhiteSpace(studentName) &&
                         (currentSeat is null || currentSeat.ComputerNumber is null))
                    assignedNumber = GetSuggestedSeatComputerNumber();
                else if (computerNumberText is null && currentSeat is not null)
                    assignedNumber = currentSeat.ComputerNumber;
            }
            if (currentSeat is { VeyonObjectId: not null } &&
                currentSeat.ComputerNumber != assignedNumber)
                throw new InvalidDataException("此座位已关联电脑，编号对应固定 Windows Hostname，不能直接改号。");
            if (assignedNumber is { } requestedNumber &&
                _seatGrid.Seats.Any(seat => seat.SeatId != currentSeat?.SeatId &&
                                            seat.ComputerNumber == requestedNumber))
                throw new InvalidDataException($"电脑编号 {assignedNumber} 已分配给其他座位。");
            if (currentSeat is null && _seatGrid.Seats.Length >= ClassroomSeatGridStore.MaximumSeats)
                throw new InvalidDataException($"一个教室最多可添加 {ClassroomSeatGridStore.MaximumSeats} 个座位。");
            RecordSeatMapUndo();
            var seats = currentSeat is null
                ? _seatGrid.Seats.Append(new ClassroomSeatGridSeat(Guid.NewGuid(), row, column,
                    studentName, ComputerNumber: assignedNumber)).ToArray()
                : _seatGrid.Seats.Select(seat => seat.SeatId == currentSeat.SeatId
                    ? seat with { StudentName = studentName, ComputerNumber = assignedNumber }
                    : seat).ToArray();
            ApplySeatGridDraft(_seatGrid with { Seats = seats });
            var cell = SeatMapCells.FirstOrDefault(item => item.Row == row && item.Column == column);
            SeatMapStatus = assignedNumber is null && currentSeat?.VeyonObjectId is null
                ? $"已{(currentSeat is null ? "添加" : "更新")}无编号空座位；它会保存在本机座位图中，不会新建 Veyon 电脑。点击“保存”提交布局。"
                : cell?.SeatNumber is { } seatNumber
                    ? $"座位 {seatNumber} 已更新；点击“保存”后写入本机并同步 Veyon。"
                    : "座位已更新；点击“保存”后写入本机并同步 Veyon。";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or OverflowException)
        {
            SeatMapStatus = "无法应用这项座位修改；当前草稿未更改。" + exception.Message;
            return false;
        }
    }

    public string SuggestNextSeatComputerNumber() =>
        GetSuggestedSeatComputerNumber().ToString(CultureInfo.InvariantCulture);

    private int GetSuggestedSeatComputerNumber()
    {
        var used = _seatGrid?.Seats.Where(seat => seat.ComputerNumber is not null)
            .Select(seat => seat.ComputerNumber!.Value).ToHashSet() ?? [];
        var candidate = GetFirstComputerNumberAfterCampusMaximum();
        while (candidate <= MachineNaming.MaximumNumber && used.Contains(candidate)) candidate++;
        if (candidate > MachineNaming.MaximumNumber)
        {
            candidate = 1;
            while (candidate <= MachineNaming.MaximumNumber && used.Contains(candidate)) candidate++;
        }
        if (candidate > MachineNaming.MaximumNumber)
            throw new InvalidDataException($"没有可用电脑编号；最大编号为 {MachineNaming.MaximumNumber}。");
        return candidate;
    }

    public void ToggleSeatCell(int row, int column)
    {
        if (!IsSeatAddRemoveMode || !CanEditSeatMap || _seatGrid is null) return;
        var cell = SeatMapCells.FirstOrDefault(item => item.Row == row && item.Column == column);
        if (cell is null) return;
        if (cell.IsSeat)
            RemoveSeatCell(row, column, unassignVeyonTarget: true);
        else
            SaveSeatCell(row, column, string.Empty, null);
    }

    internal void PaintSeatCells(IReadOnlyList<(int Row, int Column)> cells, bool makeSeats)
    {
        if (!CanEditSeatMap || _seatGrid is null || cells.Count == 0) return;
        try
        {
            foreach (var (row, column) in cells.Distinct())
            {
                if (makeSeats && !(_seatGrid.Seats.Any(seat => seat.Row == row && seat.Column == column)))
                    SaveSeatCell(row, column, "", null);
                else if (!makeSeats)
                    RemoveSeatCell(row, column, unassignVeyonTarget: true);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or OverflowException)
        {
            SeatMapStatus = "连续调整座位草稿失败。" + exception.Message;
        }
    }

    internal void RemoveSeatCell(int row, int column, bool unassignVeyonTarget)
    {
        if (!CanEditSeatMap || _seatGrid is null) return;
        var seat = _seatGrid.Seats.SingleOrDefault(item => item.Row == row && item.Column == column);
        if (seat is null) return;
        RecordSeatMapUndo();
        ApplySeatGridDraft(_seatGrid with { Seats = _seatGrid.Seats.Where(item => item.SeatId != seat.SeatId).ToArray() });
        SeatMapStatus = "座位已从本机草稿移除；Veyon 中的电脑不会删除。点击“保存”提交更改。";
        if (RoomNames.Count > 0) UpdateRoomPreviewRows();
        InvalidateRoomReview();
    }

    /// <summary>
    /// Deletes a seat's Veyon computer object by UUID when present, then removes the local seat
    /// and persists immediately: a Veyon deletion is irreversible and preview mode has no save
    /// button. Seats without a Veyon UUID are removed locally only, so right-click deletion is
    /// never a silent no-op.
    /// </summary>
    public async Task<string> DeleteSeatComputerFromVeyonAsync(int row, int column)
    {
        if (!OperatingSystem.IsWindows()) return "只有 Windows 教师端可以删除 Veyon 电脑。";
        var grid = _seatGrid;
        if (grid is null) return "当前没有打开座位图。";
        var seat = grid.Seats.SingleOrDefault(item => item.Row == row && item.Column == column);
        if (seat is null) return "找不到该座位；没有删除任何内容。";
        var objectId = seat.VeyonObjectId ?? Guid.Empty;
        var updated = grid with
        {
            Seats = grid.Seats.Where(item => item.SeatId != seat.SeatId).ToArray()
        };

        string resultText;
        try
        {
            if (objectId != Guid.Empty)
            {
                var result = await Task.Run(() => VeyonNetworkObjectDirectory.RemoveComputer(objectId));
                resultText = $"已删除 Veyon 电脑“{result.ComputerName}”（{result.HostAddress}），" +
                             $"剩余 {result.RemainingComputerCount} 台；本机对应座位已移除。";
            }
            else
            {
                resultText = "此座位没有绑定 Veyon 电脑 UUID；已仅从本机座位图移除该座位，Veyon 未改动。";
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
            IOException or UnauthorizedAccessException or PlatformNotSupportedException or
            System.ComponentModel.Win32Exception or TimeoutException)
        {
            var failure = "删除未完成：" + exception.Message + " 没有删除任何本机座位。";
            SeatMapStatus = failure;
            return failure;
        }

        try
        {
            var saved = _classroomSeatGridStore.RestoreLayout(updated);
            ApplySeatGrid(saved);
            _seatMapUndo.Clear();
            _seatMapRedo.Clear();
            Changed(nameof(CanUndoSeatMapEdit));
            Changed(nameof(CanRedoSeatMapEdit));
            SeatMapStatus = resultText;
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            if (objectId != Guid.Empty)
            {
                try { await ReadWebsiteLocationsAsync(); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    SeatMapStatus += " 名单刷新未完成，请点「重新读取目录」。";
                }
            }
            return resultText;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
            IOException or UnauthorizedAccessException)
        {
            var failure = objectId != Guid.Empty
                ? "Veyon 电脑已删除，但本机座位移除未能保存：" + exception.Message +
                  " 请进入「编辑座位」后手动移除该座位，不要重复删除 Veyon 对象。"
                : "本机座位移除未能保存：" + exception.Message + " 没有删除任何内容。";
            SeatMapStatus = failure;
            return failure;
        }
    }

    public async Task<string?> SyncSeatStudentNameToVeyonAsync(int row, int column,
        string? confirmedExternalName = null)
    {
        if (!OperatingSystem.IsWindows() || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room || _seatGrid is null) return null;
        var seat = _seatGrid.Seats.SingleOrDefault(item => item.Row == row && item.Column == column);
        if (seat?.VeyonObjectId is not { } objectId || seat.VeyonLocationObjectId is not { } locationId ||
            string.IsNullOrWhiteSpace(seat.VeyonHostAddress) || string.IsNullOrWhiteSpace(seat.VeyonComputerName))
            return null;

        try
        {
            var snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            if (SelectedCampusProfile?.ProfileId != campus.ProfileId || SelectedRoomProfile?.RoomId != room.RoomId)
                return "地点已切换；没有同步 Veyon 名称。";
            var matches = snapshot.Objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                item.ObjectId == objectId).ToArray();
            if (matches.Length != 1) return "绑定 UUID 不唯一或已不存在；学生姓名已保存在 Campus，请重新核对绑定。";
            var current = matches[0];
            var host = VeyonHostAddress.NormalizeOverride(current.Host);
            if (current.ParentObjectId != locationId ||
                Uri.CheckHostName(host) != UriHostNameType.Dns ||
                !string.Equals(host, seat.VeyonHostAddress, StringComparison.OrdinalIgnoreCase) ||
                snapshot.Objects.Count(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), host,
                        StringComparison.OrdinalIgnoreCase)) != 1)
                return "绑定设备的 Host 或地点已变化；学生姓名已保存在 Campus，未修改 Veyon。请先核对设备关联。";

            var desiredName = string.IsNullOrWhiteSpace(seat.StudentName) ? host : seat.StudentName.Trim();
            if (string.Equals(current.Name, desiredName, StringComparison.Ordinal))
            {
                if (!string.Equals(seat.VeyonComputerName, current.Name, StringComparison.Ordinal))
                    ApplySeatGrid(_classroomSeatGridStore.UpdateBoundVeyonComputerName(objectId, locationId,
                        seat.VeyonComputerName, current.Name));
                return null;
            }
            if (!string.Equals(current.Name, seat.VeyonComputerName, StringComparison.Ordinal) &&
                !string.Equals(current.Name, confirmedExternalName, StringComparison.Ordinal))
                return $"CONFIRM_NAME:{current.Name}|Veyon 中此 UUID 的 Name 已由“{seat.VeyonComputerName}”改为“{current.Name}”。是否以 Campus 学生姓名“{desiredName}”覆盖？";

            var target = new VeyonComputerRenameTargetPlan(objectId, locationId, current.Name, desiredName, host);
            var response = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                (requestId, caller) => new PrivilegedWorkerRequest(PrivilegedWorkerProtocol.CurrentVersion,
                    requestId, caller, PrivilegedWorkerOperation.RenameVeyonComputers,
                    VeyonConfigFingerprint: snapshot.ConfigFingerprint,
                    VeyonComputerRenameTargets: [target]));
            if (!response.Result.Ok || response.VeyonRenameResults?.Count != 1 ||
                response.VeyonRenameResults[0].ObjectId != objectId ||
                response.VeyonRenameResults[0].Status != "succeeded")
                return "Veyon 名称写入未确认；Host 和绑定未改。请刷新目录核对结果。";

            var readback = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            var verified = readback.Objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                item.ObjectId == objectId).ToArray();
            if (verified.Length != 1 || verified[0].ParentObjectId != locationId ||
                readback.Objects.Count(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), host,
                        StringComparison.OrdinalIgnoreCase)) != 1 ||
                !string.Equals(verified[0].Name, desiredName, StringComparison.Ordinal) ||
                !string.Equals(VeyonHostAddress.NormalizeOverride(verified[0].Host), host,
                    StringComparison.OrdinalIgnoreCase))
                return "Veyon 名称读回不一致；Campus 姓名已保留，需人工核对。";
            if (SelectedCampusProfile?.ProfileId != campus.ProfileId ||
                SelectedRoomProfile?.RoomId != room.RoomId || _seatGrid?.Seats.SingleOrDefault(item =>
                    item.SeatId == seat.SeatId) is not { } latestSeat ||
                latestSeat.VeyonObjectId != objectId || latestSeat.VeyonLocationObjectId != locationId ||
                !string.Equals(latestSeat.StudentName, seat.StudentName, StringComparison.Ordinal) ||
                !string.Equals(latestSeat.VeyonHostAddress, seat.VeyonHostAddress, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(latestSeat.VeyonComputerName, seat.VeyonComputerName, StringComparison.Ordinal))
                return "Veyon Name 已按本次读回更新，但 Campus 座位在操作期间变化；未覆盖新座位状态，请重新读取并同步。";
            ApplySeatGrid(_classroomSeatGridStore.UpdateBoundVeyonComputerName(objectId, locationId,
                seat.VeyonComputerName, desiredName));
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or TimeoutException or
            PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return "学生姓名已保存，但 Veyon 名称未同步成功；" + exception.Message;
        }
    }

    public async Task<string?> SyncSeatVeyonNameToStudentAsync(int row, int column,
        string expectedExternalName)
    {
        if (!OperatingSystem.IsWindows() || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room || _seatGrid is null)
            return "当前系统或地点不支持名称同步。";
        var seat = _seatGrid.Seats.SingleOrDefault(item => item.Row == row && item.Column == column);
        if (seat?.VeyonObjectId is not { } objectId || seat.VeyonLocationObjectId is not { } locationId ||
            string.IsNullOrWhiteSpace(seat.VeyonHostAddress) || string.IsNullOrWhiteSpace(seat.VeyonComputerName))
            return "座位没有可核对的 Veyon 绑定；未更改姓名。";
        try
        {
            var snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            if (SelectedCampusProfile?.ProfileId != campus.ProfileId || SelectedRoomProfile?.RoomId != room.RoomId)
                return "读取期间地点发生变化；没有更新本机姓名。";
            var matches = snapshot.Objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                item.ObjectId == objectId).ToArray();
            if (matches.Length != 1 || matches[0].ParentObjectId != locationId ||
                !string.Equals(matches[0].Name, expectedExternalName, StringComparison.Ordinal))
                return "Veyon 设备身份或 Name 在确认后发生变化；没有更新本机姓名。";
            var host = VeyonHostAddress.NormalizeOverride(matches[0].Host);
            if (Uri.CheckHostName(host) != UriHostNameType.Dns ||
                !string.Equals(host, seat.VeyonHostAddress, StringComparison.OrdinalIgnoreCase) ||
                snapshot.Objects.Count(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), host,
                        StringComparison.OrdinalIgnoreCase)) != 1)
                return "Veyon Host 已变化、无效或不唯一；没有更新本机姓名。";

            var studentName = string.Equals(matches[0].Name, host, StringComparison.OrdinalIgnoreCase)
                ? ""
                : matches[0].Name;
            if (studentName.Length > 100 || studentName.Any(char.IsControl))
                return "Veyon Name 超出学生姓名允许范围；没有更新本机姓名。";
            var update = new ClassroomSeatVeyonNameSync(seat.SeatId, objectId, locationId, host,
                seat.VeyonComputerName, seat.StudentName ?? "", studentName, matches[0].Name);
            var updated = _classroomSeatGridStore.ApplyConfirmedVeyonNameSync(campus.ProfileId,
                room.RoomId, [update]);
            ApplySeatGrid(updated);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or TimeoutException or
            PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return "Veyon 名称没有同步到本机；" + exception.Message;
        }
    }

    public async Task<string> ApplyVeyonNameConflictsToCampusAsync()
    {
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room ||
            _seatGrid is null || SeatVeyonNameSyncConflicts.Count == 0)
            return "没有待确认的 Veyon 名称差异。";
        var conflicts = SeatVeyonNameSyncConflicts.ToArray();
        try
        {
            var snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            if (SelectedCampusProfile?.ProfileId != campus.ProfileId || SelectedRoomProfile?.RoomId != room.RoomId)
                return "校区或地点已切换；没有同步学生姓名。";
            var updates = new List<ClassroomSeatVeyonNameSync>(conflicts.Length);
            foreach (var conflict in conflicts)
            {
                var seat = _seatGrid.Seats.SingleOrDefault(item => item.SeatId == conflict.SeatId)
                    ?? throw new InvalidDataException("待同步座位已不存在；请重新读取 Veyon 目录。");
                if (seat.VeyonObjectId != conflict.VeyonObjectId ||
                    seat.VeyonLocationObjectId != conflict.VeyonLocationObjectId ||
                    !string.Equals(seat.VeyonHostAddress, conflict.HostAddress, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(seat.VeyonComputerName, conflict.CachedVeyonName, StringComparison.Ordinal) ||
                    !string.Equals(seat.StudentName, conflict.CampusStudentName, StringComparison.Ordinal))
                    throw new InvalidDataException("Campus 座位资料已变化；没有应用本次批量同步，请重新读取。");
                var matches = snapshot.Objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                    item.ObjectId == conflict.VeyonObjectId).ToArray();
                if (matches.Length != 1 || matches[0].ParentObjectId != conflict.VeyonLocationObjectId ||
                    !string.Equals(matches[0].Name, conflict.ActualVeyonName, StringComparison.Ordinal) ||
                    !string.Equals(VeyonHostAddress.NormalizeOverride(matches[0].Host), conflict.HostAddress,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Veyon 的 UUID、Name、Host 或地点在确认后发生变化；没有应用名称同步。");
                var studentName = string.Equals(conflict.ActualVeyonName, conflict.HostAddress,
                    StringComparison.OrdinalIgnoreCase) ? "" : conflict.ActualVeyonName;
                updates.Add(new ClassroomSeatVeyonNameSync(conflict.SeatId, conflict.VeyonObjectId,
                    conflict.VeyonLocationObjectId, conflict.HostAddress, conflict.CachedVeyonName,
                    conflict.CampusStudentName ?? "", studentName, conflict.ActualVeyonName));
            }
            ApplySeatGrid(_classroomSeatGridStore.ApplyConfirmedVeyonNameSync(campus.ProfileId,
                room.RoomId, updates));
            SeatVeyonNameSyncConflicts = Array.Empty<TeacherVeyonNameSyncConflict>();
            return $"已按 UUID 同步 {updates.Count} 个 Veyon Name 到 Campus 学生姓名；Host、编号与座位设备绑定未更改。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or TimeoutException or
            PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return "没有应用 Veyon→Campus 名称同步；请重新读取目录后核对。" + exception.Message;
        }
    }

    private bool BindSeatVeyonTarget(int row, int column, Guid objectId)
    {
        if (!CanEditSeatMap) return false;
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room || _seatGrid is null)
            return false;
        try
        {
            var choice = SeatVeyonTargetChoices.SingleOrDefault(item => item.ObjectId == objectId)
                         ?? throw new InvalidDataException("请先读取当前地点的 Veyon 电脑清单。");
            ApplySeatGrid(_classroomSeatGridStore.AssignVeyonTarget(campus.ProfileId, room.RoomId,
                row, column, choice.HostAddress, choice.ComputerName, choice.ObjectId,
                choice.LocationObjectId));
            var studentName = _seatGrid?.Seats.FirstOrDefault(item => item.Row == row && item.Column == column)
                ?.StudentName?.Trim();
            SeatMapStatus = string.IsNullOrWhiteSpace(studentName)
                ? "座位与 Veyon 电脑的对应关系已保存在本机；还没有写入 Veyon。点击“保存”后才会同步并验证目录。"
                : $"学生“{studentName}”与 Veyon 电脑的对应关系已保存在本机；还没有写入 Veyon。点击“保存”后才会同步并验证目录。";
            RefreshClassroomEventSeatLocations();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException)
        {
            SeatMapStatus = "无法关联 Veyon 电脑；原座位表未更改。" + exception.Message;
            return false;
        }
    }

    public async Task BindSelectedSeatVeyonTargetAsync()
    {
        if (!CanBindSelectedSeatVeyonTarget || SelectedRoomProfile is not { } room ||
            SelectedCampusProfile is not { } campus || _seatGrid is null ||
            SelectedSeatAssociationSeat is not { } seatChoice ||
            SelectedSeatAssociationTarget is not { } targetChoice)
        {
            SeatVeyonTargetStatus = "请先读取当前地点目录，再明确选择一个姓名座位和一台 Veyon 电脑。";
            return;
        }

        var roomId = room.RoomId;
        var campusId = campus.ProfileId;
        var seatId = seatChoice.SeatId;
        IsReadingSeatVeyonTargets = true;
        SeatVeyonTargetStatus = "正在重新读取目录，核对所选 Veyon UUID、Host 和父地点……";
        try
        {
            var objects = await Task.Run(VeyonNetworkObjectDirectory.ReadNetworkObjects);
            if (SelectedCampusProfile?.ProfileId != campusId || SelectedRoomProfile?.RoomId != roomId ||
                _seatGrid?.Seats.All(seat => seat.SeatId != seatId) != false)
            {
                SeatVeyonTargetStatus = "读取期间地点或座位已变化；没有应用关联。";
                return;
            }

            var locations = objects.Where(item => item.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Name, room.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (locations.Length != 1 || locations[0].ObjectId != targetChoice.LocationObjectId)
                throw new InvalidDataException("所选电脑的地点 UUID 已变化或当前地点不唯一；请重新读取并选择。");
            var matches = objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                item.ObjectId == targetChoice.ObjectId).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("所选电脑 UUID 已消失或重复；请重新读取目录。");
            var current = matches[0];
            var host = VeyonHostAddress.NormalizeOverride(current.Host);
            if (current.ParentObjectId != targetChoice.LocationObjectId ||
                !string.Equals(current.Name, targetChoice.ComputerName, StringComparison.Ordinal) ||
                !string.Equals(host, targetChoice.HostAddress, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("所选电脑的 Name、Host 或父地点已变化；没有关联，请重新读取目录。");
            var sameHost = objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase))
                .Where(item =>
                {
                    try { return string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), host,
                        StringComparison.OrdinalIgnoreCase); }
                    catch (InvalidDataException) { return false; }
                }).ToArray();
            if (sameHost.Length != 1)
                throw new InvalidDataException("所选 Host 在 Veyon 多个对象中重复；为避免错绑，没有应用关联。");

            var boundSeat = _seatGrid.Seats.Single(seat => seat.SeatId == seatId);
            if (boundSeat.VeyonObjectId is { } existingObjectId && existingObjectId != current.ObjectId)
                throw new InvalidDataException("这个座位已关联另一台 Veyon 电脑；请先核对并明确解除旧关联。");
            if (boundSeat.ComputerNumber is { } computerNumber &&
                !string.Equals(host, MachineNaming.CreateName(room.Prefix,
                    computerNumber.ToString(CultureInfo.InvariantCulture)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("所选设备的 Veyon Host 必须与此座位的固定 Windows 电脑名一致；不支持 IP 或 Host 映射。");

            if (!BindSeatVeyonTarget(seatChoice.Row, seatChoice.Column, current.ObjectId))
            {
                SeatVeyonTargetStatus = "人工关联没有应用；" + SeatMapStatus;
                return;
            }
            SeatVeyonTargetStatus = $"已在本机将座位“{seatChoice.DisplayLabel}”对应到 {targetChoice.DisplayLabel}；点击“保存”后才会同步并验证 Veyon。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                           UnauthorizedAccessException or InvalidOperationException or
                                           TimeoutException or PlatformNotSupportedException or
                                           System.ComponentModel.Win32Exception)
        {
            SeatVeyonTargetStatus = "人工关联没有应用；原座位绑定保持不变。" + exception.Message;
        }
        finally { IsReadingSeatVeyonTargets = false; }
    }

    public void ClearSeatVeyonTarget(int row, int column)
    {
        if (!CanEditSeatMap || _seatGrid is null)
            return;
        try
        {
            var seat = _seatGrid.Seats.SingleOrDefault(item => item.Row == row && item.Column == column)
                       ?? throw new InvalidDataException("找不到要解除电脑关联的座位。");
            RecordSeatMapUndo();
            ApplySeatGridDraft(_seatGrid with
            {
                Seats = _seatGrid.Seats.Select(item => item.SeatId == seat.SeatId
                    ? item with
                    {
                        VeyonHostAddress = null,
                        VeyonComputerName = null,
                        VeyonObjectId = null,
                        VeyonLocationObjectId = null
                    }
                    : item).ToArray()
            });
            SeatMapStatus = "已在本次编辑中解除本机座位与 Veyon 电脑的关联；仍需点击“保存”确认。Veyon 电脑对象和 Host 不会被删除。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException)
        {
            SeatMapStatus = "无法解除电脑关联；当前编辑内容保持不变。" + exception.Message;
        }
    }

    public void ClearSelectedSeatVeyonTarget()
    {
        if (!CanClearSelectedSeatVeyonTarget || SelectedSeatAssociationSeat is not { } seat) return;
        ClearSeatVeyonTarget(seat.Row, seat.Column);
    }

    public async Task ReadSeatVeyonTargetsAsync()
    {
        if (!CanReadSeatVeyonTargets || SelectedRoomProfile is not { } room || SelectedCampusProfile is not { } campus) return;
        IsReadingSeatVeyonTargets = true;
        SeatVeyonNameSyncConflicts = Array.Empty<TeacherVeyonNameSyncConflict>();
        SeatVeyonTargetStatus = $"正在读取“{room.DisplayName}”的 Veyon 电脑……";
        try
        {
            var roomId = room.RoomId;
            var snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            var objects = snapshot.Objects;
            if (SelectedCampusProfile?.ProfileId != campus.ProfileId || SelectedRoomProfile?.RoomId != roomId)
            {
                SeatVeyonTargetStatus = "当前校区或地点已切换；本次电脑清单未应用。";
                return;
            }
            var roomLocations = objects.Where(item =>
                item.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Name, room.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (roomLocations.Length != 1 || roomLocations[0].ObjectId == Guid.Empty)
            {
                SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
                SeatVeyonTargetStatus = "Veyon 中找不到唯一、有 UUID 的当前地点；没有修改座位关联。";
                return;
            }
            var roomLocation = roomLocations[0];
            var allComputers = objects.Where(item =>
                item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase)).ToArray();
            var roomComputers = allComputers.Where(item => item.ParentObjectId == roomLocation.ObjectId).ToArray();
            var candidates = new List<(VeyonNetworkObject Computer, string HostAddress)>();
            foreach (var item in roomComputers)
            {
                if (item.ObjectId == Guid.Empty || item.ParentObjectId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(item.Name) || item.Name != item.Name.Trim() ||
                    item.Name.Length > 100 || item.Name.Any(char.IsControl))
                    continue;
                try
                {
                    var host = VeyonHostAddress.NormalizeOverride(item.Host);
                    if (host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Dns)
                        candidates.Add((item, host));
                }
                catch (InvalidDataException)
                {
                    // Keep malformed entries pending without blocking valid computers in this room.
                }
            }
            var orderedCandidates = candidates
                .OrderBy(item => item.Computer.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.HostAddress, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // Names are display labels and may legitimately match student names; UUID and Host
            // determine identity/connection uniqueness.
            var ambiguousIds = allComputers.GroupBy(item => item.ObjectId)
                .Where(group => group.Key == Guid.Empty || group.Count() > 1)
                .SelectMany(group => group).Select(item => item.ObjectId).ToHashSet();
            var allHostOwners = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in allComputers)
            {
                try
                {
                    var host = VeyonHostAddress.NormalizeOverride(item.Host);
                    if (!allHostOwners.TryGetValue(host, out var owners))
                        allHostOwners.Add(host, owners = []);
                    owners.Add(item.ObjectId);
                }
                catch (InvalidDataException) { }
            }
            foreach (var owners in allHostOwners.Values.Where(owners => owners.Count > 1))
                ambiguousIds.UnionWith(owners);
            var choices = orderedCandidates.Where(item => !ambiguousIds.Contains(item.Computer.ObjectId))
                .Select(item => new TeacherVeyonTargetChoice(item.Computer.ObjectId, item.Computer.ParentObjectId,
                    item.Computer.Name, item.HostAddress, item.Computer.Location,
                    _seatGrid?.Seats.SingleOrDefault(seat => seat.VeyonObjectId == item.Computer.ObjectId)
                        ?.StudentName ?? "未填写姓名"))
                .ToArray();
            var pendingCount = Math.Max(0, roomComputers.Length - choices.Length);
            SeatVeyonTargetChoices = Array.AsReadOnly(choices);
            var knownBindings = _seatGrid?.Seats.Where(seat => seat.VeyonObjectId is not null).ToArray() ?? [];
            var staleBindings = knownBindings.Count(seat => !objects.Any(item =>
                item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                item.ObjectId == seat.VeyonObjectId && item.ParentObjectId == seat.VeyonLocationObjectId &&
                string.Equals(item.Host, seat.VeyonHostAddress, StringComparison.OrdinalIgnoreCase)));
            SeatVeyonNameSyncConflicts = Array.AsReadOnly(knownBindings.Select(seat =>
            {
                var boundMatches = allComputers.Where(item => item.ObjectId == seat.VeyonObjectId).ToArray();
                if (boundMatches.Length != 1 || boundMatches[0].ParentObjectId != seat.VeyonLocationObjectId)
                    return null;
                try
                {
                    var actualHost = VeyonHostAddress.NormalizeOverride(boundMatches[0].Host);
                    if (!string.Equals(actualHost, seat.VeyonHostAddress, StringComparison.OrdinalIgnoreCase))
                        return null;
                    if (Uri.CheckHostName(actualHost) != UriHostNameType.Dns ||
                        allHostOwners.TryGetValue(actualHost, out var hostOwners) && hostOwners.Count != 1)
                        return null;
                }
                catch (InvalidDataException) { return null; }
                var expectedName = string.IsNullOrWhiteSpace(seat.StudentName)
                    ? seat.VeyonHostAddress! : seat.StudentName.Trim();
                return string.Equals(boundMatches[0].Name, expectedName, StringComparison.Ordinal)
                    ? null
                    : new TeacherVeyonNameSyncConflict(seat.SeatId, seat.Row, seat.Column,
                        seat.StudentName, seat.VeyonComputerName!, boundMatches[0].Name,
                        seat.VeyonHostAddress!, seat.VeyonObjectId!.Value,
                        seat.VeyonLocationObjectId!.Value);
            }).Where(item => item is not null).Select(item => item!).ToArray());
            SeatAssociationSeatChoices = BuildSeatAssociationSeatChoices(_seatGrid);
            SeatVeyonTargetStatus = choices.Length == 0
                ? $"Veyon 地点“{room.DisplayName}”没有可安全供教师选择的电脑；{pendingCount} 台有重复、Host 无效或资料不完整。"
                : $"已读取“{room.DisplayName}”的 {choices.Length} 台可唯一候选电脑；没有按姓名或编号自动绑定。" +
                  (staleBindings == 0 ? "" : $"另有 {staleBindings} 个原 UUID/Host/地点关联已变化，原绑定保留并需人工核对。");
            if (SeatVeyonNameSyncConflicts.Count > 0)
                SeatVeyonTargetStatus += $"检测到 {SeatVeyonNameSyncConflicts.Count} 个绑定 UUID 的 Name 与学生姓名不一致；可检查后按 Veyon Name 更新 Campus。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                           UnauthorizedAccessException or InvalidOperationException or
                                           TimeoutException or PlatformNotSupportedException or
                                           System.ComponentModel.Win32Exception)
        {
            SeatVeyonTargetStatus = "读取 Veyon 电脑失败；座位关联没有更改。" + exception.Message;
        }
        finally { IsReadingSeatVeyonTargets = false; }
    }

    /// <summary>Synchronizes the edited seat name to its already-bound Veyon UUID.</summary>

    public async Task ExecuteSeatSwapPlanAsync()
    {
        if (!CanExecuteSeatSwapPlan || _preparedSeatSwapPlan is not { } plan || !TryBeginExclusiveTask()) return;
        var veyonPlan = plan.VeyonPlan;
        SeatSwapStatus = veyonPlan is null
            ? "正在保存本机学生座位调整……"
            : "正在备份 Veyon 配置、执行改名并逐台读回核对……";
        SeatSwapTargetResults = Array.Empty<string>();
        try
        {
            var latestLayout = _classroomSeatGridStore.GetLayout(plan.CampusProfileId, plan.RoomId,
                SelectedRoomProfile?.RoomId == plan.RoomId ? SelectedRoomProfile.StartNumber : 1);
            if (latestLayout.Seats.SingleOrDefault(seat => seat.SeatId == plan.FirstSeatSnapshot.SeatId) !=
                    plan.FirstSeatSnapshot ||
                latestLayout.Seats.SingleOrDefault(seat => seat.SeatId == plan.SecondSeatSnapshot.SeatId) !=
                    plan.SecondSeatSnapshot)
                throw new InvalidOperationException("本机座位表自生成计划后已有变化；没有修改 Veyon。请重新读取并生成计划。");

            if (veyonPlan is null)
            {
                var updatedLocal = _classroomSeatGridStore.CommitStudentPositionSwap(plan.CampusProfileId,
                    plan.RoomId, plan.FirstSeatSnapshot, plan.SecondSeatSnapshot);
                CompleteSeatSwap(updatedLocal);
                SeatSwapStatus = "本机学生座位已保存；电脑编号、主机地址和 Veyon 关联保持在原座位。";
                return;
            }

            var response = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                (requestId, caller) => new PrivilegedWorkerRequest(
                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                    PrivilegedWorkerOperation.RenameVeyonComputers,
                    VeyonConfigFingerprint: veyonPlan.ConfigFingerprint,
                    VeyonComputerRenameTargets: veyonPlan.Targets));
            var targetResults = response.VeyonRenameResults ?? Array.Empty<VeyonComputerRenameTargetResult>();
            var namesByObjectId = veyonPlan.Bindings.ToDictionary(item => item.VeyonObjectId,
                item => item.NewName);
            SeatSwapTargetResults = Array.AsReadOnly(targetResults.Select(target =>
            {
                var displayName = namesByObjectId.GetValueOrDefault(target.ObjectId, "未填写姓名");
                var status = target.Status switch
                {
                    "succeeded" => "已确认",
                    "unchanged" => "未变更",
                    "rolled-back" => "已回滚",
                    "needs-review" => "待核对",
                    _ => "失败"
                };
                return $"{displayName}：{status}";
            }).ToArray());
            if (!response.Result.Ok || targetResults.Count != veyonPlan.Targets.Length ||
                targetResults.Any(target => target.Status != "succeeded") ||
                !veyonPlan.Targets.All(expected => targetResults.Any(actual => actual.ObjectId == expected.ObjectId)))
            {
                SeatSwapStatus = "设备名称同步未全部确认；教师端尚未提交新的座位姓名。请重新读取地点状态后再处理。";
                SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
                SeatVeyonTargetStatus = "请重新读取当前地点的 Veyon 电脑清单后再处理座位关联。";
                _preparedSeatSwapPlan = null;
                Changed(nameof(CanExecuteSeatSwapPlan));
                return;
            }

            try
            {
                var updated = _classroomSeatGridStore.CommitStudentPositionSwapAfterVeyonRename(
                    plan.CampusProfileId, plan.RoomId, plan.FirstSeatSnapshot,
                    plan.SecondSeatSnapshot, veyonPlan.Bindings);
                CompleteSeatSwap(updated);
                SeatSwapStatus = "换座完成；设备显示名和本机学生座位记录已同步保存。";
                SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
                SeatVeyonTargetStatus = "换座已完成；如需重新关联电脑，请读取最新 Veyon 清单。";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or InvalidOperationException)
            {
                SeatSwapStatus = "设备名称已逐台读回，但本机座位表未能保存；请先核对备份，再同步本机学生记录。";
                SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
                SeatVeyonTargetStatus = "改名结果需要核对；请重新读取 Veyon 电脑清单。";
                _preparedSeatSwapPlan = null;
                Changed(nameof(CanExecuteSeatSwapPlan));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException or
                                           TimeoutException or System.ComponentModel.Win32Exception)
        {
            SeatSwapStatus = veyonPlan is null
                ? "本机座位表没有提交；请重新读取并检查后再试。"
                : "换座结果需要核对；本机学生座位记录没有提交。请重新读取地点状态后再处理。";
            if (veyonPlan is not null)
            {
                SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
                SeatVeyonTargetStatus = "换座结果需要核对；请重新读取 Veyon 电脑清单。";
            }
            _preparedSeatSwapPlan = null;
            Changed(nameof(CanExecuteSeatSwapPlan));
        }
        finally { EndExclusiveTask(); }
    }

    private void NotifyStudentUpdateTargetSelectionChanged()
    {
        Changed(nameof(StudentUpdateTargetSelectionSummary));
        Changed(nameof(StudentUpdateSelectedTargetCount));
        Changed(nameof(CanDeployStudentUpdate));
        Changed(nameof(CanSyncStudentConfiguration));
        Changed(nameof(CanMaintainStudentDevices));
        Changed(nameof(CanTrustStudentAgentIdentities));
    }

    private void WebsitePolicyTargetChoiceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not TeacherPolicyTargetChoice choice || !_websitePolicyTargetChoices.Contains(choice) ||
            args.PropertyName is not (null or nameof(TeacherPolicyTargetChoice.IsSelected)) ||
            _isUpdatingWebsitePolicyTargetSelection) return;
        NotifyWebsitePolicyTargetSelectionChanged();
    }

    private void NotifyWebsitePolicyTargetSelectionChanged()
    {
        Changed(nameof(WebsitePolicyTargetSelectionSummary));
        Changed(nameof(WebsitePolicyNextStepGuidance));
        Changed(nameof(ClassroomTargetSummary));
        Changed(nameof(CanPushWebsitePolicy));
        Changed(nameof(CanDisableWebsitePolicy));
        Changed(nameof(CanReadWebsitePolicyStatus));
        Changed(nameof(CanReadDeviceHealth));
        NotifyDeviceHealthBatchProperties();
        Changed(nameof(CanTrustStudentAgentIdentities));
        Changed(nameof(CanMigrateEdgeRealtime));
        Changed(nameof(CanRestoreEdgeLegacy));
        Changed(nameof(WebsitePolicyConfirmationSummary));
        Changed(nameof(WebsitePolicyDisableConfirmationSummary));
        Changed(nameof(CanPushApplicationPolicy));
        Changed(nameof(CanDisableApplicationPolicy));
        Changed(nameof(CanPushStudentSystemPolicy));
        Changed(nameof(CanDisableStudentSystemPolicy));
        Changed(nameof(CanReadApplicationPolicyAudit));
        Changed(nameof(CanReadApplicationInventory));
        NotifyApplicationPolicyInputs();
        NotifyStudentUpdateTargetSelectionChanged();
    }

    private void InvalidateDeviceHealthSnapshot(string reason)
    {
        Interlocked.Increment(ref _deviceHealthSelectionRevision);
        if (!string.Equals(_deviceHealthRowsCampus, CampusId.Trim(), StringComparison.OrdinalIgnoreCase))
            _deviceRuntimeSummaryPolling.Clear();
        CancelDeviceHealthFreshnessUpdate();
        var hadSnapshot = _deviceHealthRows.Count > 0 || _deviceHealthAlerts.Count > 0;
        ReconcileDeviceHealthRows(CampusId.Trim(), GetDeviceHealthTargets());
        if (!hadSnapshot && _deviceHealthRows.Count == 0) return;
        ReconcileDeviceHealthAlerts();
        RefreshDeviceHealthView();
        DeviceHealthStatus = $"{reason}；已保留本机缓存并标记为待更新，正在重新读取。";
        DeviceRuntimeSummaryStatus = "学生名单已变化，正在更新状态。";
        DeviceRuntimeSummaryDiagnostics = "";
        DeviceHealthBatchStatus = IsRunningDeviceHealthBatch
            ? "校区或设备选择已变化；正在处理的批次仍针对开始时的电脑和校区，不会改投新选择。"
            : "校区或设备选择已变化；请先重新读取健康状态，再开始批量操作。";
        Changed(nameof(HasDeviceHealthAlerts));
        NotifyDeviceHealthBatchProperties();
    }

    private void ReconcileDeviceHealthRows(string campus, IEnumerable<string> selectedTargets)
    {
        var rawTargets = selectedTargets.Where(target => !string.IsNullOrWhiteSpace(target)).ToArray();
        string[] targets;
        try
        {
            targets = rawTargets.Length == 0
                ? Array.Empty<string>()
                : WebsitePolicyTransport.NormalizeTargets(rawTargets).ToArray();
        }
        catch (InvalidDataException)
        {
            targets = Array.Empty<string>();
        }
        if (!string.Equals(_deviceHealthRowsCampus, campus, StringComparison.OrdinalIgnoreCase))
        {
            _deviceHealthRows.Clear();
            if (_deviceHealthAlerts.Count > 0)
            {
                _deviceHealthAlerts.Clear();
                Changed(nameof(HasDeviceHealthAlerts));
                Changed(nameof(DeviceHealthAlertsHeader));
                Changed(nameof(DeviceHealthAlertsDescription));
            }
        }

        var selectedKeys = targets.Select(NormalizeTargetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _deviceHealthRows.Where(row => !selectedKeys.Contains(
                     NormalizeTargetKey(row.Target))).ToArray())
            _deviceHealthRows.Remove(row);

        foreach (var target in targets)
        {
            var key = NormalizeTargetKey(target);
            var existing = _deviceHealthRows.FirstOrDefault(row =>
                NormalizeTargetKey(row.Target) == key);
            var choice = WebsitePolicyTargetChoices.FirstOrDefault(item =>
                NormalizeTargetKey(item.Target) == key);
            var studentName = choice?.StudentName ?? GetWebsitePolicyStudentName(target);
            if (existing is not null)
            {
                existing.SetStudentName(studentName);
                continue;
            }

            var row = new TeacherDeviceHealthRow(target, studentName, "");
            if (_deviceHealthCache.TryGetValue(BuildDeviceHealthCacheKey(campus, target), out var cached))
                row.ApplyLocalCache(cached);
            _deviceHealthRows.Add(row);
        }

        _deviceHealthRowsCampus = campus;
        RefreshDeviceHealthView();
        RefreshRuntimeSummaryCounts();
        UpdateDeviceHealthCacheStatus(campus, targets);
        ReconcileDeviceHealthAlerts();
    }

    private static string BuildDeviceHealthCacheKey(string campus, string target) =>
        campus.Trim() + "\0" + NormalizeTargetKey(target);

    private void SaveDeviceHealthCache(string campus, IEnumerable<TeacherDeviceHealthRow>? additionalRows = null)
    {
        if (_deviceHealthCacheError.Length > 0) return;
        var savedUtc = DateTimeOffset.UtcNow;
        var rows = _deviceHealthRows.Concat(additionalRows ?? Array.Empty<TeacherDeviceHealthRow>())
            .DistinctBy(row => NormalizeTargetKey(row.Target), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = BuildDeviceHealthCacheKey(campus, row.Target);
            var entry = row.CreateLocalCacheEntry(campus, savedUtc);
            if (entry is null && _deviceHealthCache.TryGetValue(key, out var cached))
                entry = cached with
                {
                    StudentName = row.StudentName,
                    SavedUtc = savedUtc,
                    PreviousIssues = Array.AsReadOnly(row.CachedIssueSummaries.ToArray())
                };
            if (entry is null) continue;
            _deviceHealthCache[key] = entry;
        }
        try
        {
            _deviceHealthCacheStore.Save(_deviceHealthCache.Values);
            DeviceHealthCacheStatus = "查询摘要已保存在本机；缓存状态会标记为待更新，并自动重新读取。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          System.Text.Json.JsonException or ArgumentException)
        {
            _deviceHealthCacheError = exception.Message;
            DeviceHealthCacheStatus = "本机状态缓存写入失败；实时查询仍可用。";
        }
    }

    private void UpdateDeviceHealthCacheStatus(string campus, IReadOnlyCollection<string> targets)
    {
        if (_deviceHealthCacheError.Length > 0)
        {
            DeviceHealthCacheStatus = "本机状态缓存不可用；实时查询仍可用。";
            return;
        }
        var cachedCount = targets.Count(target => _deviceHealthCache.ContainsKey(
            BuildDeviceHealthCacheKey(campus, target)));
        DeviceHealthCacheStatus = cachedCount > 0
            ? $"本机缓存 {cachedCount}/{targets.Count} 台 · 仅供刷新前查看，状态待 Agent 确认。"
            : "查询摘要仅保存在本机；读取后会自动刷新。";
    }

    private IReadOnlyList<string> GetSelectedWebsitePolicyTargets()
    {
        if (!AreWebsiteTargetsTrustedForActions()) return Array.Empty<string>();
        var targets = WebsitePolicyTargetChoices.Where(item => item.IsSelected)
            .Select(item => item.Target).ToArray();
        return AreTargetsConflictFree(targets) ? targets : Array.Empty<string>();
    }

    private bool AreTargetsConflictFree(IEnumerable<string> targets)
    {
        if (!_websiteTargetsRequireDirectorySnapshot || !OperatingSystem.IsWindows()) return true;
        var snapshot = VeyonDeviceDirectorySnapshot;
        if (!_veyonDeviceDirectoryReadValid || snapshot is null) return false;
        var currentDevices = snapshot.Devices.Where(item =>
            item.State != VeyonDeviceDirectoryState.MissingInVeyon).ToArray();
        foreach (var target in targets)
        {
            var key = NormalizeTargetKey(target);
            var matches = currentDevices.Where(item =>
                string.Equals(NormalizeTargetKey(item.HostAddress), key,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].State is not
                    (VeyonDeviceDirectoryState.InSync or VeyonDeviceDirectoryState.Discovered))
                return false;
        }
        return true;
    }

    private bool AreWebsiteTargetsTrustedForActions() => !_websiteTargetsLoadedFromPartialRead &&
        (!_websiteTargetsRequireDirectorySnapshot ||
         (_veyonDeviceDirectoryReadValid && HasFreshVerifiedVeyonDirectoryRead &&
          !string.IsNullOrWhiteSpace(_websiteTargetsDirectoryFingerprint) &&
          string.Equals(_websiteTargetsDirectoryFingerprint,
              VeyonNetworkObjectDirectory.LastTrustedDirectoryFingerprint, StringComparison.Ordinal)));

    private static bool HasFreshVerifiedVeyonDirectoryRead
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return true;
            if (VeyonNetworkObjectDirectory.LastReadDiagnostic is not
                { FailureStage: VeyonDirectoryReadDiagnostic.None, LastSuccessfulReadUtc: { } lastRead })
                return false;
            var age = DateTimeOffset.UtcNow - lastRead;
            return age >= TimeSpan.FromSeconds(-30) && age <= TimeSpan.FromMinutes(3);
        }
    }

    private IReadOnlyList<string> GetSelectedStudentUpdateTargets() => GetSelectedWebsitePolicyTargets();

    private IReadOnlyList<TeacherDeviceHealthRow> GetSelectedHealthRows()
    {
        var selected = GetDeviceHealthTargets().Select(NormalizeTargetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _deviceHealthRows.Where(row => selected.Contains(NormalizeTargetKey(row.Target))).ToArray();
    }

    private void NotifyDeviceHealthBatchProperties()
    {
        Changed(nameof(DeviceHealthSelectedCount));
        Changed(nameof(DeviceHealthScopeSummary));
        Changed(nameof(HasDeviceHealthTargets));
        Changed(nameof(DeviceHealthRecentlyReachableCount));
        Changed(nameof(DeviceHealthVeyonEligibleCount));
        Changed(nameof(DeviceHealthEdgeEligibleCount));
        Changed(nameof(DeviceHealthVeyonSkippedCount));
        Changed(nameof(DeviceHealthEdgeSkippedCount));
        Changed(nameof(DeviceHealthBatchPreview));
        Changed(nameof(CanBatchEnsureVeyonServiceRunning));
        Changed(nameof(CanBatchRefreshLegacyEdgePolicy));
    }

    private void ScheduleNextDeviceHealthFreshnessUpdate()
    {
        CancelDeviceHealthFreshnessUpdate();
        var now = DateTimeOffset.UtcNow;
        var nextExpiry = _deviceHealthRows.Select(row => row.LastCollectedUtc)
            .Where(collected => collected is { } value && value.AddMinutes(2) > now)
            .Select(collected => collected!.Value.AddMinutes(2))
            .DefaultIfEmpty(DateTimeOffset.MinValue).Min();
        if (nextExpiry == DateTimeOffset.MinValue) return;
        var cancellation = new CancellationTokenSource();
        _healthSnapshotFreshnessCancellation = cancellation;
        _ = RefreshDeviceHealthReachabilityAtExpiryAsync(nextExpiry, cancellation.Token);
    }

    private async Task RefreshDeviceHealthReachabilityAtExpiryAsync(DateTimeOffset expiresUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = expiresUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in _deviceHealthRows) row.RefreshHealthFreshness();
            foreach (var alert in _deviceHealthAlerts) alert.RefreshRepairAvailability();
            NotifyDeviceHealthBatchProperties();
            ScheduleNextDeviceHealthFreshnessUpdate();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void CancelDeviceHealthFreshnessUpdate()
    {
        var cancellation = _healthSnapshotFreshnessCancellation;
        _healthSnapshotFreshnessCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private bool IsCampusIdValid()
    {
        try { WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim()); return true; }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException)
        { return false; }
    }
    public string WebsiteBlockDomains
    {
        get => _websiteBlockDomains;
        set
        {
            var next = value ?? "";
            if (_websiteBlockDomains == next) return;
            _websiteBlockDomains = next;
            if (!_loadingWebsitePolicyLists) PersistWebsitePolicyLists();
            Changed(); Changed(nameof(SelectedWebsiteMode)); Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(WebsiteModeGuidance)); Changed(nameof(WebsitePolicyNextStepGuidance));
            Changed(nameof(ClassroomQuickControlPresetSummary));
            Changed(nameof(WebsitePolicyConfirmationSummary));
        }
    }
    public string WebsiteAllowDomains
    {
        get => _websiteAllowDomains;
        set
        {
            var next = value ?? "";
            if (_websiteAllowDomains == next) return;
            _websiteAllowDomains = next;
            if (!_loadingWebsitePolicyLists) PersistWebsitePolicyLists();
            Changed(); Changed(nameof(SelectedWebsiteMode)); Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(WebsiteModeGuidance)); Changed(nameof(WebsitePolicyNextStepGuidance));
            Changed(nameof(ClassroomQuickControlPresetSummary));
            Changed(nameof(WebsitePolicyConfirmationSummary));
        }
    }
    public string WebsitePolicyDraftStatus
    {
        get => _websitePolicyDraftStatus;
        private set { if (_websitePolicyDraftStatus == value) return; _websitePolicyDraftStatus = value; Changed(); }
    }
    public string CommonWebsiteBlacklistPresetDomains
    {
        get => _commonWebsiteBlacklistPresetDomains;
        set
        {
            var next = value ?? "";
            if (_commonWebsiteBlacklistPresetDomains == next) return;
            _commonWebsiteBlacklistPresetDomains = next;
            Changed();
            Changed(nameof(WebsitePolicyPresetEditStatus));
        }
    }
    public string CommonWebsiteAllowlistPresetDomains
    {
        get => _commonWebsiteAllowlistPresetDomains;
        set
        {
            var next = value ?? "";
            if (_commonWebsiteAllowlistPresetDomains == next) return;
            _commonWebsiteAllowlistPresetDomains = next;
            Changed();
            Changed(nameof(WebsitePolicyPresetEditStatus));
        }
    }
    public string WebsitePolicyPresetStatus
    {
        get => _websitePolicyPresetStatus;
        private set { if (_websitePolicyPresetStatus == value) return; _websitePolicyPresetStatus = value; Changed(); }
    }
    public string WebsitePolicyPresetEditStatus =>
        CommonWebsiteBlacklistPresetDomains == _savedCommonWebsiteBlacklistPresetDomains &&
        CommonWebsiteAllowlistPresetDomains == _savedCommonWebsiteAllowlistPresetDomains
            ? ""
            : "常用名单有未保存的修改；保存后，之后导入才会使用新内容。";
    public bool HasWebsitePolicyPresetEditStatus => WebsitePolicyPresetEditStatus.Length > 0;
    public bool CanSaveWebsitePolicyPresets => !IsExecuting;
    public string WebsiteObservedAddress
    {
        get => _websiteObservedAddress;
        set
        {
            var next = value ?? "";
            if (_websiteObservedAddress == next) return;
            _websiteObservedAddress = next;
            PersistWebsiteObservedAddress(next);
            Changed();
            Changed(nameof(CanAddObservedWebsite));
        }
    }
    public string WebsiteEntryStatus { get => _websiteEntryStatus; private set { _websiteEntryStatus = value; Changed(); Changed(nameof(HasWebsiteEntryStatus)); } }
    public bool HasWebsiteEntryStatus => WebsiteEntryStatus.Length > 0;
    private void LoadWebsitePolicyLists(string campusId)
    {
        if (string.IsNullOrWhiteSpace(campusId)) return;
        try
        {
            var draft = WebsitePolicyListDraftStore.Read(campusId.Trim(), _websitePolicyDraftDirectory);
            _loadingWebsitePolicyLists = true;
            WebsiteBlockDomains = draft?.BlockedDomains ?? "";
            WebsiteAllowDomains = draft?.AllowedDomains ?? "";
            var selectedMode = WebsitePolicyModeSelection.Infer(WebsiteBlockDomains, WebsiteAllowDomains);
            string? modeLoadIssue = null;
            try
            {
                selectedMode = WebsitePolicyModeStore.Read(campusId.Trim(), _websitePolicyDraftDirectory) ?? selectedMode;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            {
                modeLoadIssue = exception.Message;
                WebsitePolicyError = "限制方式记录暂不可用；已根据当前名单选择方式。" + exception.Message;
            }
            WebsiteModeIndex = WebsitePolicyModeSelection.ToIndex(selectedMode);
            WebsitePolicyDraftStatus = modeLoadIssue is not null
                ? "名单已恢复；限制方式记录无法读取，当前方式按名单推断。"
                : draft is null
                    ? "当前校区还没有保存名单；输入内容后会自动保存在教师电脑，手机可刷新读取。"
                    : $"已恢复本机名单（{draft.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}）；修改后自动保存，手机可刷新读取。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          System.Text.Json.JsonException or ArgumentException)
        {
            _loadingWebsitePolicyLists = true;
            WebsiteBlockDomains = "";
            WebsiteAllowDomains = "";
            WebsiteModeIndex = 0;
            WebsitePolicyDraftStatus = "无法读取本机名单；原文件未更改。";
            WebsitePolicyError = "网站名单暂不可用：" + exception.Message;
        }
        finally { _loadingWebsitePolicyLists = false; }
    }

    private void LoadCommonWebsitePolicyPresets()
    {
        try
        {
            var presets = WebsitePolicyPresetStore.Read();
            CommonWebsiteBlacklistPresetDomains = presets.BlockedDomains;
            CommonWebsiteAllowlistPresetDomains = presets.AllowedDomains;
            _savedCommonWebsiteBlacklistPresetDomains = presets.BlockedDomains;
            _savedCommonWebsiteAllowlistPresetDomains = presets.AllowedDomains;
            Changed(nameof(WebsitePolicyPresetEditStatus));
            WebsitePolicyPresetStatus = presets.UpdatedUtc is { } updated
                ? $"已恢复本机常用名单（{updated.ToLocalTime():yyyy-MM-dd HH:mm}）；保存后只影响之后导入。"
                : "已载入内置常用名单；修改并保存后，只影响之后导入，不会自动推送。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            CommonWebsiteBlacklistPresetDomains = "";
            CommonWebsiteAllowlistPresetDomains = "";
            _savedCommonWebsiteBlacklistPresetDomains = "";
            _savedCommonWebsiteAllowlistPresetDomains = "";
            Changed(nameof(WebsitePolicyPresetEditStatus));
            WebsitePolicyPresetStatus = "无法读取本机常用名单；原文件未更改。请检查教师电脑应用数据目录。";
            WebsitePolicyError = "常用网站名单暂不可用：" + exception.Message;
        }
    }

    public void SaveCommonWebsitePolicyPresets()
    {
        if (!CanSaveWebsitePolicyPresets) return;
        try
        {
            var saved = WebsitePolicyPresetStore.Save(CommonWebsiteBlacklistPresetDomains,
                CommonWebsiteAllowlistPresetDomains);
            CommonWebsiteBlacklistPresetDomains = saved.BlockedDomains;
            CommonWebsiteAllowlistPresetDomains = saved.AllowedDomains;
            _savedCommonWebsiteBlacklistPresetDomains = saved.BlockedDomains;
            _savedCommonWebsiteAllowlistPresetDomains = saved.AllowedDomains;
            Changed(nameof(WebsitePolicyPresetEditStatus));
            WebsitePolicyPresetStatus = "已保存到教师电脑；只影响之后导入，不会自动推送到学生电脑。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            WebsitePolicyPresetStatus = "常用名单未保存；请检查域名格式和教师电脑应用数据目录。";
            WebsitePolicyError = "常用网站名单未能保存：" + exception.Message;
        }
    }

    public void AcknowledgeDeviceHealthAlert(TeacherDeviceHealthAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        alert.Acknowledge();
        if (alert.IssueCode == "boot-changed")
        {
            foreach (var row in alert.AffectedRows) row.AcknowledgeBootChange();
            ReconcileDeviceHealthAlerts();
            RefreshDeviceHealthView();
        }
    }

    private void AddNewerRelease(ApplicationReleaseEnvelope? release, ApplicationReleaseRole role,
        string displayName, string currentVersion, ICollection<string> newerVersions)
    {
        try
        {
            var result = _releaseClient!.EvaluateLatest(release, role, currentVersion);
            if (result.IsNewer && result.Release is not null)
                newerVersions.Add($"{displayName}：{currentVersion} → {result.Release.Manifest.Version}");
        }
        catch (InvalidDataException)
        {
            // Ignore untrusted release metadata; the signed heartbeat itself remains successful.
        }
    }

    public async Task ChangeMacTeacherLoginStartAsync()
    {
        if (!CanChangeMacTeacherLoginStart) return;
        _isChangingMacTeacherLoginStart = true;
        Changed(nameof(IsChangingMacTeacherLoginStart));
        Changed(nameof(CanChangeMacTeacherLoginStart));
        try
        {
            var enable = _macTeacherLoginStart.State != MacTeacherLoginStartState.Enabled;
            SetMacTeacherLoginStart(await Task.Run(() => _teacherLoginStartPreference.SetEnabled(enable)));
        }
        catch (Exception exception)
        {
            SetMacTeacherLoginStart(new MacTeacherLoginStartSnapshot(MacTeacherLoginStartState.Failed,
                "修改 Mac 登录启动失败：" + exception.Message, _teacherLoginStartPreference.IsPackagedApp));
        }
        finally
        {
            _isChangingMacTeacherLoginStart = false;
            Changed(nameof(IsChangingMacTeacherLoginStart));
            Changed(nameof(CanChangeMacTeacherLoginStart));
        }
    }

    public async Task CheckTeacherUpdateAsync()
    {
        if (_releaseClient is null)
        {
            TeacherUpdateLatestVersion = "不可用";
            TeacherUpdateStatus = _releaseClientError ?? "此版本暂不支持自动更新。";
            return;
        }
        _isCheckingTeacherUpdate = true;
        Changed(nameof(CanCheckTeacherUpdate));
        Changed(nameof(CanDownloadTeacherUpdate));
        Changed(nameof(CanExportOfflineTeacherUpdate));
        Changed(nameof(CanVerifyOfflineTeacherUpdate));
        Changed(nameof(CanInstallOfflineTeacherUpdate));
        TeacherUpdateLatestVersion = "检查中…";
        TeacherUpdateStatus = "正在检查更新……";
        try
        {
            var result = await _releaseClient.CheckLatestAsync(ApplicationReleaseRole.TeacherConsole, AppVersion);
            _teacherUpdateRelease = result.Release;
            TeacherUpdateLatestVersion = result.Release?.Manifest.Version ?? "暂无可用版本";
            _teacherUpdateAvailable = result.IsNewer && result.Release is not null &&
                                      HasRequiredPolicyCapabilities(result.Release.Manifest);
            TeacherUpdateStatus = result.Release is null
                ? "暂时没有可用更新。"
                : result.IsNewer && !HasRequiredPolicyCapabilities(result.Release.Manifest)
                    ? "新版本与当前功能不兼容，已停止更新。"
                : result.IsNewer
                    ? $"发现新版本 {result.Release.Manifest.Version}，可以更新。"
                    : "已是最新版本。";
            RecordUpdateSuccess(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.Check,
                result.Release?.Manifest.Version);
        }
        catch (Exception exception)
        {
            _teacherUpdateRelease = null;
            _teacherUpdateAvailable = false;
            TeacherUpdateLatestVersion = "获取失败";
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.Check, null, exception);
            TeacherUpdateStatus = "检查更新失败：" + failure.ToUserMessage();
        }
        finally
        {
            _isCheckingTeacherUpdate = false;
            Changed(nameof(CanCheckTeacherUpdate));
            Changed(nameof(CanDownloadTeacherUpdate));
            Changed(nameof(CanExportOfflineTeacherUpdate));
            Changed(nameof(CanVerifyOfflineTeacherUpdate));
            Changed(nameof(CanInstallOfflineTeacherUpdate));
            Changed(nameof(HasTeacherUpdate));
        }
    }

    public async Task<bool> DownloadTeacherUpdateAsync()
    {
        if (_releaseClient is null || !_teacherUpdateAvailable || _teacherUpdateRelease is null) return false;
        var release = _teacherUpdateRelease;
        _isDownloadingTeacherUpdate = true;
        Changed(nameof(CanCheckTeacherUpdate));
        Changed(nameof(CanDownloadTeacherUpdate));
        Changed(nameof(CanVerifyOfflineTeacherUpdate));
        Changed(nameof(CanInstallOfflineTeacherUpdate));
        SetTeacherUpdateProgress(true, false, 0, "正在从 GitCode 下载并校验教师端更新包……");
        TeacherUpdateStatus = "正在下载更新……";
        var failureStage = "下载并校验更新包";
        var handoffStarted = false;
        try
        {
            var updateDirectory = Path.Combine(VeyonCampusPaths.GetLocalApplicationDataDirectory(),
                "VeyonCampus", "Updates");
            var installerPath = await _releaseClient.DownloadAsync(release,
                ApplicationReleaseRole.TeacherConsole, updateDirectory,
                progress: new Progress<InstallerResourceProgress>(SetTeacherResourceProgress));
            failureStage = "复核安装包并启动更新助手";
            SetTeacherUpdateProgress(true, true, 100, "下载完成，正在复核安装包并启动更新助手……");
            var verificationProgress = new Progress<InstallerResourceProgress>(SetTeacherResourceProgress);
            await Task.Run(() => ApplicationReleaseUpdateHandoff.Start(installerPath,
                ApplicationReleaseRole.TeacherConsole, AppVersion, verificationProgress));
            handoffStarted = true;
            RecordUpdateHandoffStarted(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.DownloadAndInstall,
                release.Manifest.Version);
            TeacherUpdateStatus = "更新已启动，应用即将关闭。";
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.DownloadAndInstall, release.Manifest.Version, exception);
            TeacherUpdateStatus = $"更新失败（{failureStage}），当前版本未更改：{failure.ToUserMessage()}";
            SetTeacherUpdateProgress(true, false, _teacherUpdateProgressPercent,
                $"失败阶段：{failureStage}。教师端仍是 {AppVersion}；可以重试或导出问题报告。");
        }
        finally
        {
            if (handoffStarted) SetTeacherUpdateProgress(false, true, 0, "");
            _isDownloadingTeacherUpdate = false;
            Changed(nameof(CanCheckTeacherUpdate));
            Changed(nameof(CanDownloadTeacherUpdate));
            Changed(nameof(CanVerifyOfflineTeacherUpdate));
            Changed(nameof(CanInstallOfflineTeacherUpdate));
        }
        return handoffStarted;
    }

    public async Task<bool> ExportMaintenanceEventReportAsync(string destinationPath)
    {
        try
        {
            var journal = await _maintenanceRunStore.ReadAsync();
            var report = MaintenanceEventReportBuilder.Create(journal, DateTimeOffset.UtcNow);
            var bytes = MaintenanceEventReportBuilder.Serialize(report);
            await File.WriteAllBytesAsync(destinationPath, bytes);
            MaintenanceEventReportExportStatus = $"已导出 {report.Events.Count} 条匿名 Maintenance 事件；文件只保存在本机选择的位置，不会自动上传。";
            Changed(nameof(MaintenanceEventReportExportStatus));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException)
        {
            MaintenanceEventReportExportStatus = "无法读取本机维护记录或保存匿名事件文件。";
            Changed(nameof(MaintenanceEventReportExportStatus));
            return false;
        }
    }

    public async Task ExportOfflineTeacherUpdateAsync(string destinationDirectory)
    {
        if (!CanExportOfflineTeacherUpdate || _releaseClient is null || _teacherUpdateRelease is null) return;
        var targetVersion = _teacherUpdateRelease.Manifest.Version;
        _isDownloadingTeacherUpdate = true;
        Changed(nameof(CanCheckTeacherUpdate));
        Changed(nameof(CanDownloadTeacherUpdate));
        Changed(nameof(CanExportOfflineTeacherUpdate));
        Changed(nameof(CanVerifyOfflineTeacherUpdate));
        Changed(nameof(CanInstallOfflineTeacherUpdate));
            OfflineTeacherUpdateStatus = "正在准备离线更新包……";
        try
        {
            var release = _teacherUpdateRelease;
            var updateDirectory = Path.Combine(VeyonCampusPaths.GetLocalApplicationDataDirectory(),
                "VeyonCampus", "Updates");
            var downloadedPath = await _releaseClient.DownloadAsync(release,
                ApplicationReleaseRole.TeacherConsole, updateDirectory);
            var publicKeyPem = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
            var exportedPath = await Task.Run(() => ApplicationReleaseClient.StageVerifiedOfflineRelease(downloadedPath,
                destinationDirectory, ApplicationReleaseRole.TeacherConsole, _releaseClient.ApiBaseAddress, publicKeyPem));
            RecordUpdateSuccess(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.OfflineExport,
                release.Manifest.Version);
            OfflineTeacherUpdateStatus = $"离线更新包 {release.Manifest.Version} 已导出。";
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.OfflineExport, targetVersion, exception);
            OfflineTeacherUpdateStatus = "无法导出离线更新包：" + failure.ToUserMessage();
        }
        finally
        {
            _isDownloadingTeacherUpdate = false;
            Changed(nameof(CanCheckTeacherUpdate));
            Changed(nameof(CanDownloadTeacherUpdate));
            Changed(nameof(CanExportOfflineTeacherUpdate));
            Changed(nameof(CanVerifyOfflineTeacherUpdate));
            Changed(nameof(CanInstallOfflineTeacherUpdate));
        }
    }

    public bool ExportStudentUpdatePerformanceReport(string destinationPath)
    {
        if (_lastStudentUpdatePerformanceReport is null)
        {
            StudentUpdatePerformanceReportExportStatus = "本次更新尚无可导出的性能汇总。";
            Changed(nameof(StudentUpdatePerformanceReportExportStatus));
            return false;
        }
        try
        {
            var bytes = StudentUpdatePerformanceReportBuilder.Serialize(_lastStudentUpdatePerformanceReport);
            if (bytes.Length > StudentUpdatePerformanceReportBuilder.MaximumReportBytes)
                throw new InvalidDataException("性能汇总超过大小限制。");
            File.WriteAllBytes(destinationPath, bytes);
            StudentUpdatePerformanceReportExportStatus =
                "性能汇总已保存。文件不含电脑名、IP 或校区身份，也不会自动上传。";
            Changed(nameof(StudentUpdatePerformanceReportExportStatus));
            return true;
        }
        catch (Exception)
        {
            StudentUpdatePerformanceReportExportStatus = "无法保存性能汇总文件。";
            Changed(nameof(StudentUpdatePerformanceReportExportStatus));
            return false;
        }
    }

    public bool ExportUpdateDiagnostics(string destinationPath)
    {
        try
        {
            var count = _updateDiagnostics.ExportTo(destinationPath);
            UpdateDiagnosticsStatus = $"已导出 {count} 条诊断（不会自动上传）。";
            return true;
        }
        catch (Exception)
        {
            UpdateDiagnosticsStatus = "无法保存诊断文件。";
            return false;
        }
    }

    public TeacherDeviceHealthRow? FocusDeviceHealthAlert(TeacherDeviceHealthAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var row = alert.AffectedRows.FirstOrDefault();
        if (row is null) return null;
        DeviceHealthCategoryFilterIndex = 0;
        DeviceHealthSearchText = row.StudentName;
        RefreshDeviceHealthView();
        return _deviceHealthPageRows.FirstOrDefault(item =>
            string.Equals(NormalizeTargetKey(item.Target), NormalizeTargetKey(row.Target),
                StringComparison.OrdinalIgnoreCase));
    }

    private static string GetHealthRepairSkipReason(TeacherDeviceHealthRow row,
        StudentAgentHealthRepairAction action)
    {
        if (!row.IsRecentlyReachable) return "已跳过：最近两分钟内没有有效的签名 Agent 回执。";
        if (row.Category == TeacherDeviceHealthCategory.NeedsManualReview)
            return "已跳过：设备状态需要人工检查。";
        if (row.UpdateTransactionState is "inProgress" or "needsReview")
            return "已跳过：学生端更新事务正在进行或需要恢复。";
        if (action == StudentAgentHealthRepairAction.EnsureVeyonServiceRunning &&
            row.ServiceState == StudentVeyonServiceState.Running)
            return "已跳过：VeyonService 已处于运行状态。";
        if (action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy &&
            (row.WebsiteRevision is null or <= 0))
            return "已跳过：没有可刷新的活动网站策略版本。";
        return "已跳过：当前状态不满足此固定修复动作的前置条件。";
    }

    public async Task ImportMacVeyonPrivateKeyAsync(string privateKeyPath)
    {
        if (!CanImportMacVeyonPrivateKey) return;
        byte[]? privateKeyBytes = null;
        byte[]? publicKeyBytes = null;
        char[]? privateKeyCharacters = null;
        char[]? publicKeyCharacters = null;
        _isImportingMacVeyonKey = true;
        Changed(nameof(IsImportingMacVeyonKey));
        Changed(nameof(CanChooseTrustMigrationSourcePackage));
        Changed(nameof(CanImportMacVeyonPrivateKey));
        SetMacVeyonKeyImportStatus("正在验证私钥与原学生配置包的公钥……");
        try
        {
            var package = PackageContext.Load(TrustMigrationSourcePackageDirectory);
            package.VerifyUnchanged();
            var privateKeyFile = new FileInfo(Path.GetFullPath(privateKeyPath));
            if (!privateKeyFile.Exists || privateKeyFile.LinkTarget is not null ||
                (privateKeyFile.Attributes & FileAttributes.ReparsePoint) != 0 ||
                privateKeyFile.Length is <= 0 or > 64 * 1024)
                throw new InvalidDataException("所选 Veyon PEM 文件不存在、不是普通文件，或超过 64 KB；没有导入。");

            privateKeyBytes = await File.ReadAllBytesAsync(privateKeyFile.FullName);
            if (privateKeyBytes.Length is <= 0 or > 64 * 1024)
                throw new InvalidDataException("所选 Veyon PEM 文件超过安全读取上限；没有导入。");
            publicKeyBytes = await File.ReadAllBytesAsync(package.PublicKeyPath);
            var publicKeyHash = Convert.ToHexString(SHA256.HashData(publicKeyBytes));
            if (!string.Equals(publicKeyHash, package.PublicKeySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原学生配置包中的 Veyon 公钥已变化；没有导入私钥。");
            package.VerifyUnchanged();

            var utf8 = new UTF8Encoding(false, true);
            var privateKeyChars = utf8.GetChars(privateKeyBytes);
            var publicKeyChars = utf8.GetChars(publicKeyBytes);
            privateKeyCharacters = privateKeyChars;
            publicKeyCharacters = publicKeyChars;
            var created = await Task.Run(() => VeyonAuthenticationKeyStore.ImportPrivateKey(
                package.Campus, privateKeyChars, publicKeyChars));
            package.VerifyUnchanged();
            SetMacVeyonKeyImportStatus(created
                ? "已将匹配的 Veyon 私钥保存到当前用户钥匙串。原始 PEM 临时导出文件可从安全传输位置删除。"
                : "当前用户钥匙串中已有与此学生配置包公钥匹配的 Veyon 私钥；没有覆盖现有密钥。");

            try
            {
                await MacVeyonScreenshotCapture.RestoreVeyonKeyToHelperAsync(package.Campus);
                SetMacVeyonHelperStatus(await MacVeyonScreenshotCapture.GetHelperStatusAsync(package.Campus));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                UnauthorizedAccessException or InvalidOperationException or CryptographicException or
                PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                SetMacVeyonHelperStatus("Veyon 私钥已安全写入钥匙串，但 helper 尚未载入该私钥：" + exception.Message);
            }
        }
        catch (Exception exception)
        {
            SetMacVeyonKeyImportStatus("Veyon 私钥导入失败：" + exception.Message);
        }
        finally
        {
            if (privateKeyBytes is not null) CryptographicOperations.ZeroMemory(privateKeyBytes);
            if (publicKeyBytes is not null) CryptographicOperations.ZeroMemory(publicKeyBytes);
            if (privateKeyCharacters is not null) Array.Clear(privateKeyCharacters);
            if (publicKeyCharacters is not null) Array.Clear(publicKeyCharacters);
            _isImportingMacVeyonKey = false;
            Changed(nameof(IsImportingMacVeyonKey));
            Changed(nameof(CanChooseTrustMigrationSourcePackage));
            Changed(nameof(CanImportMacVeyonPrivateKey));
        }
    }

    public bool InstallOfflineTeacherUpdate()
    {
        if (!CanInstallOfflineTeacherUpdate || _releaseClient is null || _offlineTeacherInstallerPath is null ||
            _offlineTeacherUpdateRelease is null) return false;
        var targetVersion = _offlineTeacherUpdateRelease.Manifest.Version;
        try
        {
            var publicKeyPem = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
            var release = ApplicationReleaseClient.ReadVerifiedStagedRelease(_offlineTeacherInstallerPath,
                ApplicationReleaseRole.TeacherConsole, _releaseClient.ApiBaseAddress, publicKeyPem);
            if (release != _offlineTeacherUpdateRelease ||
                ApplicationReleaseClient.CompareVersions(release.Manifest.Version, AppVersion) <= 0 ||
                !HasRequiredPolicyCapabilities(release.Manifest))
                throw new InvalidDataException("暂存文件自上次校验后发生变化，或不再是高于当前版本的教师安装器。");
            ApplicationReleaseUpdateHandoff.Start(_offlineTeacherInstallerPath,
                ApplicationReleaseRole.TeacherConsole, AppVersion);
            RecordUpdateHandoffStarted(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.OfflineInstall,
                release.Manifest.Version);
            OfflineTeacherUpdateStatus = "更新已启动，应用即将关闭。";
            return true;
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.OfflineInstall, targetVersion, exception);
            _offlineTeacherUpdateRelease = null;
            _offlineTeacherInstallerPath = null;
            OfflineTeacherUpdateStatus = "离线安装未启动；暂存文件复核失败：" + failure.ToUserMessage();
            Changed(nameof(CanInstallOfflineTeacherUpdate));
            return false;
        }
    }

    public void NextDeviceHealthPage()
    {
        if (!CanNextDeviceHealthPage) return;
        _deviceHealthPageIndex++;
        RefreshDeviceHealthView();
    }

    private void NotifyTrustMigrationSourcePackageValidationChanged()
    {
        Changed(nameof(TrustMigrationSourcePackageValidationStatus));
        Changed(nameof(IsValidatingTrustMigrationSourcePackage));
        Changed(nameof(IsTrustMigrationSourcePackageReady));
        Changed(nameof(IsTrustMigrationSourcePackageNotReady));
        Changed(nameof(CanGenerateStudentPackage));
        Changed(nameof(CanChooseTrustMigrationSourcePackage));
        Changed(nameof(CanImportMacVeyonPrivateKey));
        Changed(nameof(MacVeyonPrivateKeyImportInstructions));
        Changed(nameof(TrustMigrationPackageGuidance));
    }

    public async Task PollDeviceRuntimeSummariesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsHealthCenterPage || IsExecuting ||
            IsReadingDeviceHealth || !IsCampusIdValid() ||
            Interlocked.CompareExchange(ref _deviceRuntimeSummaryPollInFlight, 1, 0) != 0) return;
        _isPollingDeviceRuntimeSummary = true;
        Changed(nameof(IsPollingDeviceRuntimeSummary));
        var pollTimer = Stopwatch.StartNew();
        try
        {
            var selectionRevision = Interlocked.Read(ref _deviceHealthSelectionRevision);
            var campus = CampusId.Trim();
            var selection = WebsitePolicyTargetChoices
                .Select(choice => choice.Target).ToArray();
            if (selection.Length == 0)
            {
                DeviceRuntimeSummaryStatus = "尚未配置学生电脑名单。";
                return;
            }
            var targets = WebsitePolicyTransport.NormalizeTargets(selection);
            foreach (var target in targets)
            {
                var key = NormalizeTargetKey(target);
                if (!_deviceRuntimeSummaryPolling.ContainsKey(key))
                    _deviceRuntimeSummaryPolling[key] = new DeviceRuntimeSummaryPollingState();
            }
            var previousRowCount = _deviceHealthRows.Count;
            ReconcileDeviceHealthRows(campus, targets);
            if (_deviceHealthRows.Count > previousRowCount) SaveDeviceHealthCache(campus);

            var now = DateTimeOffset.UtcNow;
            foreach (var row in _deviceHealthRows) row.RefreshRuntimeFreshness(now);
            RefreshDeviceHealthView();
            RefreshRuntimeSummaryCounts();
            var due = targets.Where(target => _deviceRuntimeSummaryPolling[NormalizeTargetKey(target)].NextPollUtc <= now)
                .ToArray();
            if (due.Length == 0)
            {
                RefreshDeviceHealthView();
                ReconcileDeviceHealthAlerts();
                RefreshRuntimeSummaryCounts();
                DeviceRuntimeSummaryStatus = $"设备状态是最新的 · {DateTime.Now:HH:mm:ss}";
                return;
            }

            // Submit every due target now. The transport owns a shared 12-request semaphore,
            // so larger rooms drain in bounded waves instead of waiting an extra UI timer tick
            // for each group of 12 devices.
            var selectedBatch = due;
            var queuedTargets = Math.Max(0,
                selectedBatch.Length - StudentAgentRuntimeSummaryTransport.MaximumConcurrency);
            DeviceRuntimeSummaryStatus = $"正在刷新 {selectedBatch.Length} 台设备状态…";
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var requestTimer = Stopwatch.StartNew();
            var results = await StudentAgentRuntimeSummaryTransport.ReadAsync(selectedBatch, campus,
                signingKey.PrivateKey, cancellationToken);
            var fallbackTargets = results.Where(result => result.Unsupported)
                .Select(result => result.Target).ToArray();
            var fallbackResults = fallbackTargets.Length == 0
                ? Array.Empty<StudentAgentStatusDeliveryResult>()
                : await WebsitePolicyStatusTransport.ReadAsync(fallbackTargets, campus,
                    signingKey.PrivateKey, cancellationToken, includeHealthSnapshot: true);
            requestTimer.Stop();
            var fallbackByTarget = fallbackResults.ToDictionary(result => NormalizeTargetKey(result.Target),
                StringComparer.OrdinalIgnoreCase);
            var selectedNow = GetDeviceHealthTargets().Select(NormalizeTargetKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Interlocked.Read(ref _deviceHealthSelectionRevision) != selectionRevision ||
                !string.Equals(CampusId.Trim(), campus, StringComparison.Ordinal) ||
                !selectedNow.SetEquals(targets.Select(NormalizeTargetKey)))
            {
                DeviceRuntimeSummaryStatus = "学生名单已变化，正在更新设备状态。";
                return;
            }

            var successful = 0;
            foreach (var result in results)
            {
                var key = NormalizeTargetKey(result.Target);
                var state = _deviceRuntimeSummaryPolling[key];
                var row = _deviceHealthRows.FirstOrDefault(item => NormalizeTargetKey(item.Target) == key);
                if (result.Unsupported && fallbackByTarget.TryGetValue(key, out var fallback))
                {
                    if (fallback.Succeeded && fallback.Status is not null)
                    {
                        state.ConsecutiveFailures = 0;
                        state.NextPollUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                        successful++;
                    }
                    else
                    {
                        state.ConsecutiveFailures++;
                        var retrySeconds = state.ConsecutiveFailures switch
                        {
                            1 => 60,
                            2 => 120,
                            3 => 300,
                            _ => 600
                        };
                        state.NextPollUtc = DateTimeOffset.UtcNow.AddSeconds(retrySeconds);
                    }
                    row?.Apply(fallback);
                    continue;
                }

                if (result.Succeeded)
                {
                    state.ConsecutiveFailures = 0;
                    state.NextPollUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                    successful++;
                }
                else
                {
                    state.ConsecutiveFailures++;
                    var retrySeconds = state.ConsecutiveFailures switch
                    {
                        1 => 60,
                        2 => 120,
                        3 => 300,
                        _ => 600
                    };
                    state.NextPollUtc = DateTimeOffset.UtcNow.AddSeconds(retrySeconds);
                }
                row?.ApplyRuntimeSummary(result, state.ConsecutiveFailures);
            }

            var timedOut = results.Count(result => !result.Succeeded &&
                    result.Detail.Contains("超时", StringComparison.Ordinal)) +
                fallbackResults.Count(result => !result.Succeeded &&
                    result.Detail.Contains("超时", StringComparison.Ordinal));
            var known = _deviceHealthRows.Count(row => row.IsRecentlyReachable);
            var viewModelUpdateTimer = Stopwatch.StartNew();
            RefreshDeviceHealthView();
            ReconcileDeviceHealthAlerts();
            RefreshRuntimeSummaryCounts();
            NotifyDeviceHealthBatchProperties();
            SaveDeviceHealthCache(campus);
            viewModelUpdateTimer.Stop();
            pollTimer.Stop();
            DeviceRuntimeSummaryStatus = successful == selectedBatch.Length
                ? $"设备状态已更新 · {DateTime.Now:HH:mm:ss}"
                : $"部分设备暂未确认 · {DateTime.Now:HH:mm:ss}";
            DeviceRuntimeSummaryDiagnostics = $"本轮 {pollTimer.ElapsedMilliseconds} ms · 通信 {requestTimer.ElapsedMilliseconds} ms · " +
                $"视图更新 {viewModelUpdateTimer.ElapsedMilliseconds} ms · 超时 {timedOut} · 排队 {queuedTargets} · " +
                $"当前有效 {known}/{targets.Count} · 并发上限 {StudentAgentRuntimeSummaryTransport.MaximumConcurrency}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or InvalidOperationException or CryptographicException or
            PlatformNotSupportedException)
        {
            DeviceRuntimeSummaryStatus = "状态暂未更新，将自动重试。";
            DeviceRuntimeSummaryDiagnostics = exception.Message;
        }
        finally
        {
            _isPollingDeviceRuntimeSummary = false;
            Changed(nameof(IsPollingDeviceRuntimeSummary));
            Interlocked.Exchange(ref _deviceRuntimeSummaryPollInFlight, 0);
        }
    }

    public async Task PrepareMacVeyonHelperAsync()
    {
        if (!CanPrepareMacVeyonHelper) return;
        _isPreparingMacVeyonHelper = true;
        Changed(nameof(IsPreparingMacVeyonHelper));
        Changed(nameof(CanPrepareMacVeyonHelper));
        SetMacVeyonHelperStatus("正在检查 Docker 并准备 Veyon 4.11.4 ARM64 helper……");
        try
        {
            SetMacVeyonHelperStatus(await MacVeyonScreenshotCapture.PrepareHelperAsync(CampusId));
        }
        catch (Exception exception)
        {
            SetMacVeyonHelperStatus("准备 Mac Veyon helper 失败：" + exception.Message);
        }
        finally
        {
            _isPreparingMacVeyonHelper = false;
            Changed(nameof(IsPreparingMacVeyonHelper));
            Changed(nameof(CanPrepareMacVeyonHelper));
        }
    }

    public async Task PrepareSeatSwapPlanAsync()
    {
        if (!CanPrepareSeatSwapPlan || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room || _seatGrid is null) return;
        IsPreparingSeatSwapPlan = true;
        var layout = _seatGrid with { Seats = _seatGrid.Seats.Select(seat => seat with { }).ToArray() };
        var selectedIds = _seatSwapSelection.ToArray();
        SeatSwapStatus = "正在核对所选座位……";
        SeatSwapPlanRows = Array.Empty<string>();
        _preparedSeatSwapPlan = null;
        try
        {
            var first = layout.Seats.SingleOrDefault(seat => seat.SeatId == selectedIds[0])
                        ?? throw new InvalidDataException("找不到第一个所选座位。");
            var second = layout.Seats.SingleOrDefault(seat => seat.SeatId == selectedIds[1])
                         ?? throw new InvalidDataException("找不到第二个所选座位。");
            void VerifySeatSnapshots()
            {
                if (SelectedCampusProfile?.ProfileId != campus.ProfileId ||
                    SelectedRoomProfile?.RoomId != room.RoomId || _seatGrid is null)
                    throw new InvalidOperationException("当前校区或地点已切换；请重新选择座位。");
                var currentFirst = _seatGrid.Seats.SingleOrDefault(seat => seat.SeatId == first.SeatId);
                var currentSecond = _seatGrid.Seats.SingleOrDefault(seat => seat.SeatId == second.SeatId);
                if (currentFirst != first || currentSecond != second)
                    throw new InvalidOperationException("座位资料已变化；请重新选择并生成换座计划。");
                var persistedLayout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId,
                    room.StartNumber);
                if (persistedLayout.Seats.SingleOrDefault(seat => seat.SeatId == first.SeatId) != first ||
                    persistedLayout.Seats.SingleOrDefault(seat => seat.SeatId == second.SeatId) != second)
                    throw new InvalidOperationException("本机座位表已由其他窗口更新；请重新读取座位表后生成计划。");
            }

            VerifySeatSnapshots();
            var firstHasStudent = !string.IsNullOrWhiteSpace(first.StudentName);
            var secondHasStudent = !string.IsNullOrWhiteSpace(second.StudentName);
            if (!firstHasStudent && !secondHasStudent)
                throw new InvalidDataException("至少要有一个座位已填写学生姓名。");
            if (firstHasStudent && secondHasStudent &&
                string.Equals(first.StudentName, second.StudentName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("两个座位上的学生姓名相同，无需换位。");

            static bool HasAnyVeyonBindingData(ClassroomSeatGridSeat seat) =>
                seat.VeyonObjectId is not null || seat.VeyonLocationObjectId is not null ||
                !string.IsNullOrWhiteSpace(seat.VeyonHostAddress) ||
                !string.IsNullOrWhiteSpace(seat.VeyonComputerName);
            static bool HasCompleteVeyonBinding(ClassroomSeatGridSeat seat) =>
                seat.VeyonObjectId is not null && seat.VeyonLocationObjectId is not null &&
                !string.IsNullOrWhiteSpace(seat.VeyonHostAddress) &&
                !string.IsNullOrWhiteSpace(seat.VeyonComputerName);
            static string OccupantLabel(ClassroomSeatGridSeat seat) =>
                string.IsNullOrWhiteSpace(seat.StudentName) ? "空座位" : seat.StudentName;

            var firstHasBinding = HasAnyVeyonBindingData(first);
            var secondHasBinding = HasAnyVeyonBindingData(second);
            var firstHasCompleteBinding = HasCompleteVeyonBinding(first);
            var secondHasCompleteBinding = HasCompleteVeyonBinding(second);
            if ((firstHasBinding && !firstHasCompleteBinding) ||
                (secondHasBinding && !secondHasCompleteBinding))
                throw new InvalidDataException("所选座位存在不完整的 Veyon 电脑关联；请先修复或解除该关联，再换座。");

            if (!firstHasCompleteBinding && !secondHasCompleteBinding)
            {
                var localRows = new[]
                {
                    $"座位 {first.Row} 行 {first.Column} 列：{OccupantLabel(first)} → {OccupantLabel(second)}",
                    $"座位 {second.Row} 行 {second.Column} 列：{OccupantLabel(second)} → {OccupantLabel(first)}",
                    "交换本机学生姓名；电脑编号、Host 与设备绑定保留在原座位。"
                };
                _preparedSeatSwapPlan = new PreparedSeatSwapPlan(campus.ProfileId, room.RoomId,
                    first, second, null, localRows);
                SeatSwapPlanRows = Array.AsReadOnly(localRows);
                SeatSwapStatus = "计划已生成；所选座位都没有 Veyon 绑定，只调整本机学生座位记录。";
                Changed(nameof(CanExecuteSeatSwapPlan));
                return;
            }

            var snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            VerifySeatSnapshots();

            VeyonNetworkObject ReadBoundComputer(ClassroomSeatGridSeat seat)
            {
                if (seat.VeyonObjectId is not { } objectId ||
                    seat.VeyonLocationObjectId is not { } locationId ||
                    string.IsNullOrWhiteSpace(seat.VeyonHostAddress) ||
                    string.IsNullOrWhiteSpace(seat.VeyonComputerName))
                    throw new InvalidDataException($"座位“{seat.StudentName}”无法唯一匹配到 Veyon 电脑。系统已按电脑名和地址尝试匹配；请检查对应电脑资料，或先在座位上关联电脑。");
                var matches = snapshot.Objects.Where(item => item.ObjectId == objectId &&
                    item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException($"座位“{seat.StudentName}”对应的 Veyon 电脑标识不存在或重复；请先核对关联。");
                var computer = matches[0];
                var actualHost = VeyonHostAddress.NormalizeOverride(computer.Host);
                if (computer.ParentObjectId != locationId ||
                    !computer.Location.Equals(room.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(actualHost, seat.VeyonHostAddress, StringComparison.OrdinalIgnoreCase) ||
                    Uri.CheckHostName(actualHost) != UriHostNameType.Dns ||
                    snapshot.Objects.Count(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), actualHost,
                            StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new InvalidDataException($"座位“{seat.StudentName}”关联的 Host 或地点已变化、无效或不唯一；请先核对设备关联。");
                if (!string.Equals(computer.Name, seat.VeyonComputerName, StringComparison.Ordinal))
                    throw new InvalidDataException($"座位“{seat.StudentName}”的 Veyon Name 已被外部修改；请先在座位图读取目录并确认同步方向，再换座。");
                return computer;
            }

            var bindings = new List<ClassroomSeatGridNameSwapBinding>(2);
            var targetPlans = new List<VeyonComputerRenameTargetPlan>();
            foreach (var seat in new[] { first, second })
            {
                if (!HasCompleteVeyonBinding(seat)) continue;
                var computer = ReadBoundComputer(seat);
                if (bindings.Any(item => item.VeyonObjectId == computer.ObjectId ||
                    string.Equals(item.HostAddress, computer.Host, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("两个座位关联到了同一台 Veyon 电脑；请先修正关联。");
                var incomingStudentName = seat.SeatId == first.SeatId ? second.StudentName : first.StudentName;
                var newName = string.IsNullOrWhiteSpace(incomingStudentName)
                    ? VeyonHostAddress.NormalizeOverride(computer.Host) : incomingStudentName.Trim();
                var host = VeyonHostAddress.NormalizeOverride(computer.Host);
                bindings.Add(new ClassroomSeatGridNameSwapBinding(seat.SeatId, computer.ObjectId,
                    computer.ParentObjectId, host, computer.Name, newName));
                if (!string.Equals(computer.Name, newName, StringComparison.Ordinal))
                    targetPlans.Add(new VeyonComputerRenameTargetPlan(computer.ObjectId,
                        computer.ParentObjectId, computer.Name, newName, host));
            }

            if (targetPlans.Count == 0)
            {
                var updated = _classroomSeatGridStore.CommitStudentPositionSwap(campus.ProfileId,
                    room.RoomId, first, second);
                CompleteSeatSwap(updated);
                SeatSwapStatus = "换座完成；Host、电脑编号与设备绑定保持在原座位。";
                Changed(nameof(CanExecuteSeatSwapPlan));
                return;
            }

            var rows = new[]
            {
                    $"第 {first.Row} 排第 {first.Column} 列：{OccupantLabel(first)} → {OccupantLabel(second)}",
                    $"第 {second.Row} 排第 {second.Column} 列：{OccupantLabel(second)} → {OccupantLabel(first)}",
                "已绑定设备按各自 UUID 同步 Name；Host、编号和座位绑定保留。"
            };
            _preparedSeatSwapPlan = new PreparedSeatSwapPlan(campus.ProfileId, room.RoomId,
                first, second, new PreparedSeatSwapVeyonPlan(snapshot.ConfigFingerprint,
                    targetPlans.ToArray(), bindings.ToArray()), rows);
            SeatSwapPlanRows = Array.AsReadOnly(rows);
            SeatSwapStatus = "电脑关联已核对；请在确认窗口核对学生和保留地址。";
            Changed(nameof(CanExecuteSeatSwapPlan));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException or
                                           TimeoutException or PlatformNotSupportedException or
                                           System.ComponentModel.Win32Exception)
        {
            SeatSwapStatus = "无法生成换座计划；学生姓名和 Veyon 目录没有更改。" + exception.Message;
            Changed(nameof(CanExecuteSeatSwapPlan));
        }
        finally { IsPreparingSeatSwapPlan = false; }
    }

    public void PreviousDeviceHealthPage()
    {
        if (!CanPreviousDeviceHealthPage) return;
        _deviceHealthPageIndex--;
        RefreshDeviceHealthView();
    }

    private void QueueNewerReleaseNotice(TeacherCampusLatestReleases releases)
    {
        if (_releaseClient is null) return;
        var newerVersions = new List<string>(2);
        AddNewerRelease(releases.TeacherConsole, ApplicationReleaseRole.TeacherConsole,
            "教师控制台", AppVersion, newerVersions);
        AddNewerRelease(releases.StudentSetup, ApplicationReleaseRole.StudentSetup,
            "学生端部署程序", AppVersion, newerVersions);
        if (newerVersions.Count == 0) return;

        var message = "发现可用的新版本：" + Environment.NewLine +
                      string.Join(Environment.NewLine, newerVersions);
        lock (_releaseNoticeGate)
        {
            if (_pendingReleaseNotice is null) _pendingReleaseNotice = message;
        }
        ReleaseNoticeAvailable?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<StudentAgentRuntimeSummaryDeliveryResult>> ReadMobileRuntimeSummariesAsync(
        IReadOnlyList<string> targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var campus = CampusId.Trim();
        WebsitePolicySigningKeyStore.ValidateCampusId(campus);
        using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
        return await StudentAgentRuntimeSummaryTransport.ReadAsync(targets, campus,
            signingKey.PrivateKey, cancellationToken);
    }

    public async Task ReadStudentDeviceHealthAsync()
    {
        if (!CanReadDeviceHealth || !TryBeginExclusiveTask()) return;
        // A lightweight poll already in flight must not overwrite this newer full diagnostic.
        Interlocked.Increment(ref _deviceHealthSelectionRevision);
        IsReadingDeviceHealth = true;
        DeviceHealthStatus = "正在读取所选电脑的 Agent、Veyon 服务、策略和更新状态……";
        try
        {
            var selectionRevision = Interlocked.Read(ref _deviceHealthSelectionRevision);
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(GetDeviceHealthTargets());
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var results = await WebsitePolicyStatusTransport.ReadAsync(targets, campus,
                signingKey.PrivateKey, includeHealthSnapshot: true);
            var displayNames = await ResolveTargetDisplayNamesAsync(targets);
            var selectedNow = GetDeviceHealthTargets().Select(NormalizeTargetKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Interlocked.Read(ref _deviceHealthSelectionRevision) != selectionRevision ||
                !string.Equals(CampusId.Trim(), campus, StringComparison.Ordinal) ||
                !selectedNow.SetEquals(targets.Select(NormalizeTargetKey)))
            {
                InvalidateDeviceHealthSnapshot("读取过程中校区或设备选择发生变化");
                DeviceHealthStatus = "校区或设备选择已变化；旧结果未显示，请重新读取。";
                return;
            }

            ReconcileDeviceHealthRows(campus, targets);
            var previousRows = _deviceHealthRows.ToDictionary(row => NormalizeTargetKey(row.Target),
                StringComparer.OrdinalIgnoreCase);
            foreach (var result in results)
            {
                var key = NormalizeTargetKey(result.Target);
                var resolvedName = GetWebsitePolicyStudentName(result.Target);
                if (string.IsNullOrWhiteSpace(resolvedName) || resolvedName == "未填写姓名")
                    resolvedName = displayNames.GetValueOrDefault(key, "未填写姓名");
                var row = previousRows.GetValueOrDefault(key) ?? new TeacherDeviceHealthRow(result.Target,
                    resolvedName, "");
                row.SetStudentName(resolvedName);
                row.Apply(result);
                if (!_deviceHealthRows.Contains(row)) _deviceHealthRows.Add(row);
            }

            var healthy = _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Healthy);
            var attention = _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Attention);
            var manual = _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NeedsManualReview);
            var noResponse = _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NoResponse);
            UpdateDeviceHealthSummary(healthy, attention, manual, noResponse);
            RefreshDeviceHealthView();
            ReconcileDeviceHealthAlerts();
            NotifyDeviceHealthBatchProperties();
            ScheduleNextDeviceHealthFreshnessUpdate();
            SaveDeviceHealthCache(campus);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            DeviceHealthStatus = "健康状态读取失败：" + exception.Message;
            NotifyDeviceHealthBatchProperties();
        }
        finally
        {
            IsReadingDeviceHealth = false;
            EndExclusiveTask();
        }
    }

    public Task RecheckDeviceHealthAlertAsync(TeacherDeviceHealthAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return ReadStudentDeviceHealthAsync();
    }

    private UpdateDiagnosticFailure RecordUpdateFailure(UpdateDiagnosticModule module,
        UpdateDiagnosticOperation operation, string? targetVersion, Exception exception)
    {
        var failure = UpdateDiagnosticCatalog.Classify(exception);
        TryAppendUpdateDiagnostic(UpdateDiagnosticEntry.Create(module, operation, AppVersion,
            targetVersion, exception));
        return failure;
    }

    public async Task RecoverMacVeyonHelperAtStartupAsync()
    {
        if (!IsMacTeacherHost) return;
        try
        {
            SetMacVeyonHelperStatus(await MacVeyonScreenshotCapture.RecoverExistingHelperAsync(CampusId));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            UnauthorizedAccessException or InvalidOperationException or CryptographicException or
            PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            SetMacVeyonHelperStatus("启动时恢复 Mac Veyon helper 失败：" + exception.Message);
        }
    }

    public async Task RefreshMacTeacherLoginStartStatusAsync()
    {
        if (!IsMacTeacherHost) return;
        try
        {
            SetMacTeacherLoginStart(await Task.Run(_teacherLoginStartPreference.ReadStatus));
        }
        catch (Exception exception)
        {
            SetMacTeacherLoginStart(new MacTeacherLoginStartSnapshot(MacTeacherLoginStartState.Failed,
                "读取 Mac 登录启动状态失败：" + exception.Message, _teacherLoginStartPreference.IsPackagedApp));
        }
    }

    public async Task RefreshMacVeyonHelperStatusAsync()
    {
        if (!IsMacTeacherHost) return;
        try
        {
            SetMacVeyonHelperStatus(await MacVeyonScreenshotCapture.GetHelperStatusAsync(CampusId));
        }
        catch (Exception exception)
        {
            SetMacVeyonHelperStatus("读取 Mac Veyon helper 状态失败：" + exception.Message);
        }
    }

    public Task RepairDeviceHealthAlertAsync(TeacherDeviceHealthAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (alert.RecommendedAction is not { } action) return Task.CompletedTask;
        return RunStudentHealthRepairBatchAsync(action, alert.AffectedRows);
    }

    public void ReportMaintenanceEventReportExportFailure()
    {
        MaintenanceEventReportExportStatus = "无法打开保存位置或保存匿名 Maintenance 事件文件。";
        Changed(nameof(MaintenanceEventReportExportStatus));
    }

    public void ReportStudentUpdatePerformanceReportExportFailure()
    {
        StudentUpdatePerformanceReportExportStatus = "无法打开保存位置或保存性能汇总文件。";
        Changed(nameof(StudentUpdatePerformanceReportExportStatus));
    }

    internal async Task RunHourlyBackgroundChecksAsync()
    {
        if (TeacherCampusHeartbeatStateStore.IsDue(_teacherHeartbeatState,
                TeacherCampusHeartbeatStateStore.GetHongKongDate()))
            await SendTeacherCampusHeartbeatAsync();
        await SendCampusOperationsReportsAsync();
    }

    private async Task RunStudentHealthRepairAsync(TeacherDeviceHealthRow row,
        StudentAgentHealthRepairAction action)
    {
        ArgumentNullException.ThrowIfNull(row);
        var allowed = action switch
        {
            StudentAgentHealthRepairAction.EnsureVeyonServiceRunning => row.CanEnsureVeyonServiceRunning,
            StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy => row.CanRefreshLegacyEdgePolicy,
            _ => false
        };
        if (!allowed || !TryBeginExclusiveTask()) return;

        row.IsRepairing = true;
        row.RepairStatus = "正在发送经过签名的固定修复请求……";
        try
        {
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var expectedRevision = action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy
                ? row.WebsiteRevision
                : null;
            var results = await StudentAgentHealthRepairTransport.ExecuteAsync([row.Target], campus,
                signingKey.PrivateKey, action, expectedRevision);
            var result = results.Single();
            row.SetRepairResult(result);

            var refreshed = await WebsitePolicyStatusTransport.ReadAsync([row.Target], campus,
                signingKey.PrivateKey, includeHealthSnapshot: true);
            if (refreshed.Count == 1) row.Apply(refreshed[0]);
            ReconcileDeviceHealthAlerts();
            NotifyDeviceHealthBatchProperties();
            ScheduleNextDeviceHealthFreshnessUpdate();
            UpdateDeviceHealthSummary(
                _deviceHealthRows.Count(item => item.Category == TeacherDeviceHealthCategory.Healthy),
                _deviceHealthRows.Count(item => item.Category == TeacherDeviceHealthCategory.Attention),
                _deviceHealthRows.Count(item => item.Category == TeacherDeviceHealthCategory.NeedsManualReview),
                _deviceHealthRows.Count(item => item.Category == TeacherDeviceHealthCategory.NoResponse));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            row.RepairStatus = "修复状态无法确认：" + exception.Message;
        }
        finally
        {
            row.IsRepairing = false;
            EndExclusiveTask();
        }
    }

    private async Task RunStudentHealthRepairBatchAsync(StudentAgentHealthRepairAction action,
        IReadOnlyList<TeacherDeviceHealthRow>? requestedRows = null)
    {
        var selectedTargets = GetDeviceHealthTargets();
        var selectedKeys = selectedTargets.Select(NormalizeTargetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedKeys = requestedRows is null
            ? selectedKeys
            : requestedRows.Select(row => NormalizeTargetKey(row.Target))
                .Where(selectedKeys.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopeTargets = selectedTargets.Where(target => requestedKeys.Contains(NormalizeTargetKey(target))).ToArray();
        var currentRows = _deviceHealthRows.ToDictionary(row => NormalizeTargetKey(row.Target),
            StringComparer.OrdinalIgnoreCase);
        var rows = scopeTargets.Select(target => currentRows.GetValueOrDefault(NormalizeTargetKey(target)))
            .Where(row => row is not null).Cast<TeacherDeviceHealthRow>().ToArray();
        var eligibleRows = rows.Where(row => row.IsRecentlyReachable && IsHealthRepairAllowed(row, action))
            .ToArray();
        if (eligibleRows.Length == 0 || !TryBeginExclusiveTask()) return;

        IsRunningDeviceHealthBatch = true;
        var campus = CampusId.Trim();
        var batchId = Guid.NewGuid();
        _activeDeviceHealthBatchId = batchId;
        _activeDeviceHealthBatchCampus = campus;
        _activeDeviceHealthBatchSelection = new HashSet<string>(selectedKeys, StringComparer.OrdinalIgnoreCase);
        var rowByTarget = rows.ToDictionary(row => NormalizeTargetKey(row.Target),
            StringComparer.OrdinalIgnoreCase);
        var reachable = rows.Count(row => row.IsRecentlyReachable);
        var skipped = Math.Max(0, scopeTargets.Length - eligibleRows.Length);
        foreach (var row in rows)
        {
            var allowed = row.IsRecentlyReachable && IsHealthRepairAllowed(row, action);
            var detail = allowed ? "等待发送；此状态仅保存在教师端内存，不会离线排队。"
                : GetHealthRepairSkipReason(row, action);
            row.BeginBatchOperation(batchId, action,
                allowed ? TeacherDeviceHealthBatchState.Queued : TeacherDeviceHealthBatchState.Skipped,
                detail);
        }
        DeviceHealthBatchStatus = $"批次 {batchId.ToString("N")[..8]} · 目标 {scopeTargets.Length} · 最近有签名回执 {reachable} · " +
                                  $"计划 {eligibleRows.Length} · 跳过 {skipped}。";
        NotifyDeviceHealthBatchProperties();

        try
        {
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var progress = new Progress<StudentAgentHealthRepairProgressUpdate>(update =>
            {
                if (update.BatchId != batchId || _activeDeviceHealthBatchId != batchId ||
                    !rowByTarget.TryGetValue(NormalizeTargetKey(update.Target), out var row)) return;
                row.SetBatchOperation(batchId, update.CommandId, action,
                    ToTeacherDeviceHealthBatchState(update.State), update.Detail);
                UpdateDeviceHealthBatchProgressSummary(batchId, scopeTargets.Length, reachable,
                    eligibleRows.Length, skipped, rows);
            });
            IReadOnlyDictionary<string, long?>? websiteRevisions = null;
            if (action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy)
                websiteRevisions = eligibleRows.ToDictionary(row => row.Target, row => row.WebsiteRevision,
                    StringComparer.OrdinalIgnoreCase);
            var results = await StudentAgentHealthRepairTransport.ExecuteAsync(eligibleRows.Select(row => row.Target),
                campus, signingKey.PrivateKey, action, cancellationToken: default, progress: progress,
                expectedWebsiteRevisions: websiteRevisions, batchIdOverride: batchId);

            foreach (var result in results)
            {
                if (!rowByTarget.TryGetValue(NormalizeTargetKey(result.Target), out var row)) continue;
                row.SetBatchOperation(batchId, result.CommandId, action,
                    ToTeacherDeviceHealthBatchState(result.State), result.Detail);
            }
            UpdateDeviceHealthBatchProgressSummary(batchId, scopeTargets.Length, reachable,
                eligibleRows.Length, skipped, rows);

            if (selectedTargets.Count > 0 && string.Equals(CampusId.Trim(), campus, StringComparison.Ordinal))
            {
                var currentSelection = GetDeviceHealthTargets().Select(NormalizeTargetKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (currentSelection.SetEquals(selectedKeys))
                {
                    var refreshed = await WebsitePolicyStatusTransport.ReadAsync(selectedTargets, campus,
                        signingKey.PrivateKey, includeHealthSnapshot: true);
                    foreach (var result in refreshed)
                    {
                        var currentRow = _deviceHealthRows.FirstOrDefault(row =>
                            NormalizeTargetKey(row.Target) == NormalizeTargetKey(result.Target));
                        currentRow?.Apply(result);
                    }
                    ReconcileDeviceHealthAlerts();
                    UpdateDeviceHealthSummary(
                        _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Healthy),
                        _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Attention),
                        _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NeedsManualReview),
                        _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NoResponse));
                    RefreshDeviceHealthView();
                    ScheduleNextDeviceHealthFreshnessUpdate();
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        {
            DeviceHealthBatchStatus = $"批次 {batchId.ToString("N")[..8]} 未能完成：{exception.Message}";
            foreach (var row in eligibleRows.Where(row => row.BatchOperationState is
                         TeacherDeviceHealthBatchState.Queued or TeacherDeviceHealthBatchState.Delivered or
                         TeacherDeviceHealthBatchState.Executing))
                row.SetBatchOperation(batchId, row.BatchOperationCommandId, action,
                    TeacherDeviceHealthBatchState.Failed, "教师端未能确认操作结果；请重新读取设备状态。不会自动重试。");
        }
        finally
        {
            var contextChanged = !string.Equals(CampusId.Trim(), _activeDeviceHealthBatchCampus,
                                    StringComparison.Ordinal) ||
                                 !GetDeviceHealthTargets().Select(NormalizeTargetKey)
                                     .ToHashSet(StringComparer.OrdinalIgnoreCase)
                                     .SetEquals(_activeDeviceHealthBatchSelection);
            if (contextChanged)
                DeviceHealthBatchStatus += " · 此批次仍对应开始时的校区和目标。";
            _activeDeviceHealthBatchId = null;
            _activeDeviceHealthBatchCampus = "";
            _activeDeviceHealthBatchSelection.Clear();
            IsRunningDeviceHealthBatch = false;
            EndExclusiveTask();
        }
    }

    public async Task SelectLocalTestMaintenanceServiceInstallerAsync(string installerPath)
    {
        if (!IsLocalTestStudentUpdateSourceAvailable)
            throw new InvalidOperationException("此教师端正式版已关闭本机测试更新入口；请使用本机测试教师端选择测试包。");
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        var path = Path.GetFullPath(installerPath);
        if (!File.Exists(path + ".release.json"))
            throw new InvalidDataException("所选 MaintenanceService 安装器旁没有同名 .exe.release.json 签名清单。");
        var releaseClient = _releaseClient ?? throw new InvalidOperationException(
            _releaseClientError ?? "此版本未嵌入 Developer Release 公钥，不能验证测试包。");
        var release = await Task.Run(() => ApplicationReleaseClient.ReadVerifiedStagedRelease(path,
            ApplicationReleaseRole.MaintenanceService, releaseClient.ApiBaseAddress,
            ApplicationReleaseTrust.LoadPinnedPublicKeyPem()));
        if (release.Manifest.ReleaseChannel != "local-test" ||
            release.Manifest.MaintenanceProtocolVersion != MaintenanceProtocolV1.CurrentVersion)
            throw new InvalidDataException("本机 MaintenanceService 包必须由已固定的 Developer Release 密钥签署，并声明当前受支持的维护协议。");
        _localTestMaintenanceInstallerPath = path;
        _localTestMaintenanceReleaseSummary =
            $"已验签：MaintenanceService {release.Manifest.Version} · local-test · 协议 v{release.Manifest.MaintenanceProtocolVersion}。";
        UseLocalTestStudentUpdateSource = true;
        Changed(nameof(LocalTestMaintenanceInstallerPath));
        Changed(nameof(StudentUpdateSourceSummary));
        Changed(nameof(CanUpdateMaintenanceService));
    }

    public async Task SelectLocalTestStudentSetupInstallerAsync(string installerPath)
    {
        if (!IsLocalTestStudentUpdateSourceAvailable)
            throw new InvalidOperationException("此教师端正式版已关闭本机测试更新入口；请使用本机测试教师端选择测试包。");
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        var path = Path.GetFullPath(installerPath);
        if (!File.Exists(path + ".release.json"))
        {
            var isBootstrap = Path.GetFileName(path).Contains("bootstrap", StringComparison.OrdinalIgnoreCase);
            throw new InvalidDataException(isBootstrap
                ? "所选文件是一次性引导安装包，只用于在快照学生机本地覆盖安装，不能作为教师端远程更新目标。请改选带同名 .exe.release.json 清单的本机测试更新目标包。"
                : "此安装器旁边没有签名清单。请改选带同名 .exe.release.json 清单的本机测试更新目标包。引导安装包不能用于教师端远程更新。");
        }
        var releaseClient = _releaseClient ?? throw new InvalidOperationException(
            _releaseClientError ?? "此版本未嵌入线上正式发行公钥，不能验证测试包。");
        var release = await Task.Run(() => ApplicationReleaseClient.ReadVerifiedStagedRelease(path,
            ApplicationReleaseRole.StudentSetup, releaseClient.ApiBaseAddress,
            ApplicationReleaseTrust.LoadPinnedPublicKeyPem()));
        if (release.Manifest.SchemaVersion != 3 || release.Manifest.ReleaseChannel is not null ||
            release.Manifest.StudentAgentVersion is not null)
            throw new InvalidDataException("本机更新包必须使用已安装学生 Agent 信任的 Developer Release 密钥签署，并提供 stable Schema 3 清单。");
        _localTestStudentSetupInstallerPath = path;
        _localTestStudentReleaseSummary =
            $"已验签：StudentSetup {release.Manifest.Version} · stable 签名。";
        UseLocalTestStudentUpdateSource = true;
        Changed(nameof(LocalTestStudentSetupInstallerPath));
        Changed(nameof(StudentUpdateSourceSummary));
        Changed(nameof(CanDeployStudentUpdate));
    }

    public void SelectSeatForSwap(int row, int column)
    {
        if (!CanInteractWithSeatMap || !SeatSwapMode || _seatGrid is null) return;
        var seat = _seatGrid.Seats.SingleOrDefault(item => item.Row == row && item.Column == column);
        if (seat is null)
        {
            SeatSwapStatus = "请选择已添加的座位；空白网格区域不是座位。";
            return;
        }
        _preparedSeatSwapPlan = null;
        SeatSwapPlanRows = Array.Empty<string>();
        SeatSwapTargetResults = Array.Empty<string>();
        if (_seatSwapSelection.Remove(seat.SeatId))
        {
            SeatSwapStatus = "已取消该座位选择。";
        }
        else if (_seatSwapSelection.Count >= 2)
        {
            SeatSwapStatus = "已选满两个座位；再次点击已选座位可取消。";
        }
        else if (_seatSwapSelection.Count == 1 && string.IsNullOrWhiteSpace(seat.StudentName) &&
                 _seatGrid.Seats.Where(item => _seatSwapSelection.Contains(item.SeatId))
                     .All(item => string.IsNullOrWhiteSpace(item.StudentName)))
        {
            SeatSwapStatus = "两个座位都是空座位；请至少选择一个有学生姓名的座位。";
        }
        else
        {
            _seatSwapSelection.Add(seat.SeatId);
            if (_seatSwapSelection.Count == 1)
                SeatSwapStatus = string.IsNullOrWhiteSpace(seat.StudentName)
                    ? "已选择一个空座位，请再选择一个有学生姓名的座位。"
                    : "已选择第一个座位；第二个座位可以为空。";
            else
            {
                var selectedSeats = _seatGrid.Seats.Where(item => _seatSwapSelection.Contains(item.SeatId)).ToArray();
                SeatSwapStatus = selectedSeats.Any(item => string.IsNullOrWhiteSpace(item.StudentName))
                    ? "已选好学生和空座位；点“交换所选座位”继续。"
                    : "已选好两个学生座位；点“交换所选座位”继续。";
            }
        }
        Changed(nameof(SeatSwapSelectionSummary));
        Changed(nameof(CanPrepareSeatSwapPlan));
        Changed(nameof(CanSwapSeats));
        Changed(nameof(CanExecuteSeatSwapPlan));
        RefreshSeatMapCells();
    }

    private void SetMacTeacherLoginStart(MacTeacherLoginStartSnapshot snapshot)
    {
        _macTeacherLoginStart = snapshot;
        Changed(nameof(MacTeacherLoginStartStatus));
        Changed(nameof(CanChangeMacTeacherLoginStart));
        Changed(nameof(CanRefreshMacTeacherLoginStart));
        Changed(nameof(MacTeacherLoginStartActionLabel));
    }

    private void SetMacVeyonHelperStatus(string status)
    {
        if (string.Equals(_macVeyonHelperStatus, status, StringComparison.Ordinal)) return;
        _macVeyonHelperStatus = status;
        Changed(nameof(MacVeyonHelperStatus));
        Changed(nameof(IsMacVeyonHelperReady));
    }

    public void SetTeacherSetupWizardStep(int step)
    {
        if (step is < 1 or > 3 || step == _teacherSetupWizardStep) return;
        if (step > _teacherSetupWizardStep &&
            (step == 2 && !CanProceedTeacherSetupToRooms ||
             step == 3 && !CanProceedTeacherSetupToPackage)) return;
        _teacherSetupWizardStep = step;
        Changed(nameof(TeacherSetupWizardStep));
        Changed(nameof(TeacherSetupWizardProgress));
        Changed(nameof(TeacherSetupWizardProgressText));
        Changed(nameof(IsTeacherSetupStep1));
        Changed(nameof(IsTeacherSetupStep2));
        Changed(nameof(IsTeacherSetupStep3));
        Changed(nameof(IsWindowsTeacherSetupStep));
        Changed(nameof(IsMacTeacherSetupStep));
        Changed(nameof(IsTeacherSetupPackageStepVisible));
        NotifyTeacherSetupNavigationStateChanged();
    }

    public void SkipTeacherSetupRoomForNow()
    {
        if (!CanSkipTeacherSetupRoom) return;
        _teacherSetupRoomSkippedForNow = true;
        NotifyTeacherSetupNavigationStateChanged();
        SetTeacherSetupWizardStep(3);
    }

    private void TryAppendUpdateDiagnostic(UpdateDiagnosticEntry entry)
    {
        try { _updateDiagnostics.Append(entry); }
        catch (Exception) { /* Local diagnostics are best-effort and must not change update behavior. */ }
    }

    private void UpdateDeviceHealthBatchProgressSummary(Guid batchId, int targetCount, int reachable,
        int sent, int skipped, IReadOnlyList<TeacherDeviceHealthRow> rows)
    {
        if (_activeDeviceHealthBatchId != batchId) return;
        var batchRows = rows.Where(row => row.BatchOperationId == batchId).ToArray();
        var succeeded = batchRows.Count(row => row.BatchOperationState == TeacherDeviceHealthBatchState.Succeeded);
        var failed = batchRows.Count(row => row.BatchOperationState == TeacherDeviceHealthBatchState.Failed);
        var timedOut = batchRows.Count(row => row.BatchOperationState == TeacherDeviceHealthBatchState.TimedOut);
        var contextChanged = !string.Equals(CampusId.Trim(), _activeDeviceHealthBatchCampus,
                                StringComparison.Ordinal) ||
                             !GetDeviceHealthTargets().Select(NormalizeTargetKey)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase)
                                 .SetEquals(_activeDeviceHealthBatchSelection);
        DeviceHealthBatchStatus = $"批次 {batchId.ToString("N")[..8]} · 目标 {targetCount} · 最近有回执 {reachable} · " +
                                  $"计划 {sent} · 成功 {succeeded} · 失败 {failed} · 超时 {timedOut} · 跳过 {skipped}" +
                                  (contextChanged ? " · 此批次仍对应开始时的校区和目标" : "");
    }

    private async Task ValidateTrustMigrationSourcePackageAsync(string path)
    {
        var generation = Interlocked.Increment(ref _trustMigrationSourcePackageValidationGeneration);
        _trustMigrationSourcePackageValidated = false;
        _isValidatingTrustMigrationSourcePackage = false;

        if (!IsMacTeacherHost)
        {
            _trustMigrationSourcePackageValidationStatus = "";
            _macVeyonPrivateKeyImportInstructions = "先选择原有学生配置包，再按提示从原教师端导出匹配的私钥。";
            NotifyTrustMigrationSourcePackageValidationChanged();
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _trustMigrationSourcePackageValidationStatus = "尚未选择原学生配置包。";
            _macVeyonPrivateKeyImportInstructions = "先选择原有学生配置包，再按提示从原教师端导出匹配的私钥。";
            NotifyTrustMigrationSourcePackageValidationChanged();
            return;
        }

        if (!Directory.Exists(path))
        {
            _trustMigrationSourcePackageValidationStatus = "所选文件夹不可访问；请重新选择完整的原学生配置包。";
            _macVeyonPrivateKeyImportInstructions = "原配置包不可访问；校验通过后才会显示匹配的私钥导出命令。";
            NotifyTrustMigrationSourcePackageValidationChanged();
            return;
        }

        _isValidatingTrustMigrationSourcePackage = true;
        _trustMigrationSourcePackageValidationStatus = "正在读取并校验原配置包清单和公钥……";
        _macVeyonPrivateKeyImportInstructions = "配置包校验完成后会显示匹配的 Veyon 私钥导出命令。";
        NotifyTrustMigrationSourcePackageValidationChanged();
        try
        {
            var result = await Task.Run(() =>
            {
                var package = PackageContext.Load(path);
                package.VerifyUnchanged();
                if (package.WebsitePolicyPublicKeyPath is null || package.SchemaVersion < 3)
                    throw new InvalidDataException("来源包没有网站策略信任公钥；请使用当前 Windows 教师端生成的完整学生配置包。");
                var keyId = VeyonAuthKeyId.ForCampus(package.Campus);
                return (package.Campus, package.ComputerPrefix, KeyId: keyId);
            });

            if (generation != Interlocked.Read(ref _trustMigrationSourcePackageValidationGeneration)) return;
            _trustMigrationSourcePackageValidated = true;
            _trustMigrationSourcePackageValidationStatus =
                $"已验证来源包：校区“{result.Campus}” · 电脑前缀“{result.ComputerPrefix}”。生成时会再次检查文件是否变化。";
            _macVeyonPrivateKeyImportInstructions =
                $"在原 Windows 教师机执行：veyon-cli authkeys export {result.KeyId}/private <临时 PEM 文件>。选择该 PEM 后，应用会核对它是否与所选配置包公钥匹配，并将私钥写入当前用户钥匙串。不要通过聊天或邮件传输 PEM。";
        }
        catch (Exception exception)
        {
            if (generation != Interlocked.Read(ref _trustMigrationSourcePackageValidationGeneration)) return;
            _trustMigrationSourcePackageValidated = false;
            _trustMigrationSourcePackageValidationStatus = "来源包校验失败：" + exception.Message;
            _macVeyonPrivateKeyImportInstructions =
                "原配置包未通过校验；不会读取或保存 Veyon 私钥。请重新选择完整的学生配置包。";
        }
        finally
        {
            if (generation == Interlocked.Read(ref _trustMigrationSourcePackageValidationGeneration))
            {
                _isValidatingTrustMigrationSourcePackage = false;
                NotifyTrustMigrationSourcePackageValidationChanged();
            }
        }
    }

    public async Task VerifyOfflineTeacherUpdateAsync(string installerPath)
    {
        if (!CanVerifyOfflineTeacherUpdate) return;
        _offlineTeacherUpdateRelease = null;
        _offlineTeacherInstallerPath = null;
        Changed(nameof(CanInstallOfflineTeacherUpdate));
        _isVerifyingOfflineTeacherUpdate = true;
        Changed(nameof(CanCheckTeacherUpdate));
        Changed(nameof(CanDownloadTeacherUpdate));
        Changed(nameof(CanExportOfflineTeacherUpdate));
        Changed(nameof(CanVerifyOfflineTeacherUpdate));
        Changed(nameof(CanInstallOfflineTeacherUpdate));
        OfflineTeacherUpdateStatus = "正在检查离线更新包……";
        try
        {
            var releaseClient = _releaseClient ?? throw new InvalidOperationException("此版本没有固定的 Developer Release 公钥。");
            var publicKeyPem = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
            var stagingDirectory = Path.Combine(VeyonCampusPaths.GetLocalApplicationDataDirectory(),
                "VeyonCampus", "Updates");
            var verified = await Task.Run(() =>
            {
                var stagedPath = ApplicationReleaseClient.StageVerifiedOfflineRelease(installerPath,
                    stagingDirectory, ApplicationReleaseRole.TeacherConsole, releaseClient.ApiBaseAddress, publicKeyPem);
                var stagedRelease = ApplicationReleaseClient.ReadVerifiedStagedRelease(stagedPath,
                    ApplicationReleaseRole.TeacherConsole, releaseClient.ApiBaseAddress, publicKeyPem);
                return (Path: stagedPath, Release: stagedRelease);
            });
            _offlineTeacherInstallerPath = verified.Path;
            _offlineTeacherUpdateRelease = verified.Release;
            var versionComparison = ApplicationReleaseClient.CompareVersions(verified.Release.Manifest.Version, AppVersion);
            OfflineTeacherUpdateStatus = versionComparison > 0 &&
                                         HasRequiredPolicyCapabilities(verified.Release.Manifest)
                ? $"已验证版本 {verified.Release.Manifest.Version}，可以安装。"
                : versionComparison > 0
                    ? "此更新与当前功能不兼容，无法安装。"
                : $"所选版本 {verified.Release.Manifest.Version} 不高于当前版本。";
            RecordUpdateSuccess(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.OfflineVerify,
                verified.Release.Manifest.Version);
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.OfflineVerify, null, exception);
            OfflineTeacherUpdateStatus = "离线安装器验证失败，未启动安装；" + failure.ToUserMessage();
        }
        finally
        {
            _isVerifyingOfflineTeacherUpdate = false;
            Changed(nameof(CanCheckTeacherUpdate));
            Changed(nameof(CanDownloadTeacherUpdate));
            Changed(nameof(CanExportOfflineTeacherUpdate));
            Changed(nameof(CanVerifyOfflineTeacherUpdate));
            Changed(nameof(CanInstallOfflineTeacherUpdate));
        }
    }

    private async Task WaitForInitialTeacherHeartbeatAsync(DateTimeOffset sendAtUtc)
    {
        try
        {
            var remaining = sendAtUtc - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            if (_teacherHeartbeatState is { Enabled: true, PackageId: not null } state &&
                state.FirstHeartbeatNotBeforeUtc is { } dueAt && dueAt <= DateTimeOffset.UtcNow)
                await SendTeacherCampusHeartbeatAsync();
        }
        finally { Volatile.Write(ref _initialTeacherHeartbeatWaitScheduled, 0); }
    }


    // Restored members omitted by the in-progress partial-class extraction.
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

    public Task EnsureVeyonServiceRunningAsync(TeacherDeviceHealthRow row) =>
        RunStudentHealthRepairAsync(row, StudentAgentHealthRepairAction.EnsureVeyonServiceRunning);

    public Task EnsureVeyonServiceRunningForSelectedAsync() =>
        RunStudentHealthRepairBatchAsync(StudentAgentHealthRepairAction.EnsureVeyonServiceRunning);

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

    public void NextTeacherSetupWizardStep() => SetTeacherSetupWizardStep(Math.Min(3, _teacherSetupWizardStep + 1));

    public void OpenRoomSetupFromTeacherWizard() => SelectPage("rooms");

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

    public void RefreshClassroomCountdownDisplay()
    {
        Changed(nameof(ClassroomCountdownText));
        Changed(nameof(IsClassroomCountdownRunning));
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

    public Task RefreshLegacyEdgePolicyAsync(TeacherDeviceHealthRow row) =>
        RunStudentHealthRepairAsync(row, StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy);

    public Task RefreshLegacyEdgePolicyForSelectedAsync() =>
        RunStudentHealthRepairBatchAsync(StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy);

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

    public void ReportDiagnosticExportFailure() =>
        UpdateDiagnosticsStatus = "无法保存诊断文件。";

    public void ReportLocalStudentTestReleaseSelectionError(string message) =>
        StudentUpdateStatus = "本机测试版未能选择：" + (string.IsNullOrWhiteSpace(message) ? "文件不可用。" : message);

    internal void ReportMacVeyonPrivateKeyImportError(string message) =>
        SetMacVeyonKeyImportStatus(message);

    public void ReportOfflineTeacherUpdateError(string message) =>
        OfflineTeacherUpdateStatus = message;

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

    internal void SetClassroomChatSendStatus(string status) => ClassroomChatStatus = status;

    internal void SetClassroomEventReplyError(Guid helpEventId, string message)
    {
        if (_classroomHelpRows.TryGetValue(helpEventId, out var row)) row.SetError(message);
    }

    internal void SetClassroomEventStatus(string status) => ClassroomEventStatus = status;

    internal void SetClassroomNoticeStatus(string status) => ClassroomNoticeStatus = status;

    internal void SetClassroomRestoreRetrying(bool value)
    {
        if (_isRetryingClassroomRestores == value) return;
        _isRetryingClassroomRestores = value;
        Changed(nameof(CanRetryClassroomRestores));
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanSwitchClassroomScope));
    }

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

    internal async Task UpdateActiveClassroomScopeAsync(TeacherClassroomEventContext? previousContext = null)
    {
        if (_activeClassroomSession is null)
        {
            await EnsureClassroomSessionAsync();
            return;
        }
        if (!IsClassroomDirectoryScopeVerified)
        {
            ClassroomDeliveryStatus = "Veyon 当前名单尚未通过完整核对；保留原课堂范围，暂不更新或向设备发送命令。";
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
}
