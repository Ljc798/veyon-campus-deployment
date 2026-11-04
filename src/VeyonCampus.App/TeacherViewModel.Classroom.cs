using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;
using VeyonCampus.App.Maintenance;

namespace VeyonCampus.App;

public sealed partial class TeacherViewModel
{
    // ===== Classroom / events / chat / task members moved from TeacherViewModel.cs =====
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
        }
    }
    public bool HasRoomProfiles => RoomProfiles.Count > 0;
    public bool HasNoRoomProfiles => RoomProfiles.Count == 0;
    public bool HasSingleRoomProfile => RoomProfiles.Count == 1;
    public bool HasMultipleRoomProfiles => RoomProfiles.Count > 1;
    public string ClassroomRoomContextSummary => _activeClassroomSession is { } active
        ? $"课堂范围：{active.Room.RoomName} · {active.Targets.Length} 台学生电脑"
        : SelectedRoomProfile is { } room
            ? $"课堂范围：{room.DisplayName} · {room.ComputerCount} 台学生电脑"
            : "尚未配置教室";
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
    public int SeatGridNumberPendingCount => _seatGrid?.Seats.Count(seat =>
        !string.IsNullOrWhiteSpace(seat.StudentName) && seat.ComputerNumber is null) ?? 0;
    public string SeatGridSummary => $"已添加 {SeatGridSeatCount} 个桌位 · 已填写 {SeatGridNamedCount} 个姓名" +
        (SeatGridNumberPendingCount > 0 ? $" · 待编号 {SeatGridNumberPendingCount}" : "");
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
            Changed(nameof(CanEditSeatMap));
            Changed(nameof(CanClearSelectedSeatVeyonTarget));
            Changed(nameof(CanInteractWithSeatMap));
            Changed(nameof(CanToggleSeatMapEditing));
            Changed(nameof(CanToggleSeatAddRemoveMode));
            Changed(nameof(CanAssignMissingSeatComputerNumbers));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(CanApplySeatGridDimensions));
            if (!value) IsSeatAddRemoveMode = false;
            RefreshSeatMapCells();
        }
    }
    public string SeatMapEditButtonLabel => IsSeatMapEditing ? "完成编辑" : "编辑座位图";
    public bool CanToggleSeatMapEditing => HasSeatMap && !IsExecuting && !IsPreparingSeatSwapPlan && !SeatSwapMode;
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
        SeatGridComputerCount > 0 && SeatGridNumberPendingCount == 0 &&
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
    public bool CanEditSeatMap => !IsExecuting && !IsPreparingSeatSwapPlan && IsSeatMapEditing && !SeatSwapMode;
    public bool CanInteractWithSeatMap => !IsExecuting && !IsPreparingSeatSwapPlan &&
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
        ? $"{campus.DisplayName} · {room.DisplayName}"
        : "尚未选择地点";
    public void ToggleSeatMapEditing()
    {
        if (!CanToggleSeatMapEditing) return;
        if (IsSeatMapEditing)
        {
            CommitSeatMapEdit();
            IsSeatMapEditing = false;
        }
        else
            BeginSeatMapEdit();
        SeatMapStatus = IsSeatMapEditing ? "编辑中。" : "预览中。";
    }

    public void CancelSeatMapEditing()
    {
        if (!CanCancelSeatMapEditing || _seatMapEditSnapshot is not { } snapshot ||
            SelectedCampusProfile?.ProfileId != snapshot.CampusProfileId ||
            SelectedRoomProfile?.RoomId != snapshot.RoomId)
            return;
        try
        {
            ApplySeatGrid(_classroomSeatGridStore.RestoreLayout(snapshot));
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
        _seatMapEditSnapshot = _seatGrid with { Seats = _seatGrid.Seats.Select(seat => seat with { }).ToArray() };
        _seatMapEditOriginalSeatIds = _seatGrid.Seats.Select(seat => seat.SeatId).ToHashSet();
        IsSeatMapEditing = true;
    }

    private void CommitSeatMapEdit()
    {
        _seatMapEditSnapshot = null;
        _seatMapEditOriginalSeatIds.Clear();
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
                                         !string.IsNullOrWhiteSpace(RoomCampusName) &&
                                         !string.IsNullOrWhiteSpace(RoomLocationName);
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
        }
    }
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
            Changed(nameof(CanReadSeatVeyonTargets));
            Changed(nameof(CanDeleteRoomProfile));
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
        var location = MacTeacherNetworkDirectory.CreateLocation(campus, room);
        WebsiteTargets = string.Join(Environment.NewLine, location.Targets);
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
        if (!CanEditSeatMap) return false;
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room) return false;
        try
        {
            var currentSeat = _seatGrid?.Seats.SingleOrDefault(seat => seat.Row == row && seat.Column == column);
            int? computerNumber = null;
            if (!string.IsNullOrWhiteSpace(computerNumberText))
            {
                if (!int.TryParse(computerNumberText, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var parsedNumber) || parsedNumber is < 1 or > MachineNaming.MaximumNumber)
                    throw new InvalidDataException($"电脑编号请输入 1–{MachineNaming.MaximumNumber} 的整数。");
                computerNumber = parsedNumber;
            }
            var willAutoAssignNumber = !string.IsNullOrWhiteSpace(studentName) && computerNumber is null &&
                                       currentSeat?.ComputerNumber is null;
            var firstComputerNumber = willAutoAssignNumber
                ? GetFirstComputerNumberAfterCampusMaximum()
                : room.StartNumber;
            ApplySeatGrid(_classroomSeatGridStore.SaveSeat(campus.ProfileId, room.RoomId,
                row, column, studentName, computerNumber, firstComputerNumber));
            var cell = SeatMapCells.FirstOrDefault(item => item.Row == row && item.Column == column);
            SeatMapStatus = cell?.SeatNumber is { } seatNumber
                ? willAutoAssignNumber
                    ? $"已从现有最大编号后自动分配电脑编号 {seatNumber}；座位资料已保存。"
                    : $"电脑编号 {seatNumber} 的座位资料已保存。"
                : currentSeat?.ComputerNumber is not null
                    ? "无姓名座位资料已保存；由于已有电脑编号，该座位会进入电脑清单。"
                    : "桌位资料已保存；留空姓名且无电脑编号时只保留桌位。";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            RefreshClassroomEventSeatLocations();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "无法保存座位表；原布局未更改。" + exception.Message;
            return false;
        }
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
        if (!IsSeatAddRemoveMode || !CanEditSeatMap || _seatGrid is null || cells.Count == 0 ||
            SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room) return;
        try
        {
            ApplySeatGrid(_classroomSeatGridStore.SetSeatCells(campus.ProfileId, room.RoomId, cells, makeSeats));
            SeatMapStatus = "座位图已更新。";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "连续调整座位表失败；原布局未更改。" + exception.Message;
        }
    }

    internal void RemoveSeatCell(int row, int column, bool unassignVeyonTarget)
    {
        if (!CanEditSeatMap) return;
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room) return;
        try
        {
            ApplySeatGrid(_classroomSeatGridStore.RemoveSeat(campus.ProfileId, room.RoomId,
                row, column, unassignVeyonTarget));
            SeatMapStatus = "已移除桌位；该格现在是空桌位区域。";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "无法移除座位；原布局未更改。" + exception.Message;
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
                Uri.CheckHostName(host) != UriHostNameType.Dns || seat.ComputerNumber is not { } number ||
                !string.Equals(host, seat.VeyonHostAddress, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(host, MachineNaming.CreateName(room.Prefix,
                    number.ToString(CultureInfo.InvariantCulture)), StringComparison.OrdinalIgnoreCase))
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
                ? "座位已关联学生设备；映射已保存在本机。"
                : $"学生“{studentName}”已关联到该座位；设备映射已保存在本机。";
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
            if (boundSeat.ComputerNumber is not { } computerNumber ||
                !string.Equals(host, MachineNaming.CreateName(room.Prefix,
                    computerNumber.ToString(CultureInfo.InvariantCulture)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("所选设备的 Veyon Host 必须与此座位的固定 Windows 电脑名一致；不支持 IP 或 Host 映射。");

            if (!BindSeatVeyonTarget(seatChoice.Row, seatChoice.Column, current.ObjectId))
            {
                SeatVeyonTargetStatus = "人工关联没有应用；" + SeatMapStatus;
                return;
            }
            SeatVeyonTargetStatus = $"已由教师确认：座位“{seatChoice.DisplayLabel}”关联到 {targetChoice.DisplayLabel}。";
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
        if (!CanEditSeatMap) return;
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room || _seatGrid is null)
            return;
        try
        {
            var seat = _seatGrid.Seats.SingleOrDefault(item => item.Row == row && item.Column == column)
                       ?? throw new InvalidDataException("找不到要解除电脑关联的座位。");
            ApplySeatGrid(_classroomSeatGridStore.ClearVeyonTarget(campus.ProfileId, room.RoomId, seat.SeatId));
            SeatMapStatus = "已解除本机座位与 Veyon 电脑的关联；未修改 Veyon 目录。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException)
        {
            SeatMapStatus = "无法解除电脑关联；原座位表未更改。" + exception.Message;
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
                }
            }
        }

        var choices = targets.Select(target =>
        {
            var targetKey = NormalizeTargetKey(target);
            displayNames.TryGetValue(targetKey, out var computerName);
            var studentName = studentsByTarget.GetValueOrDefault(targetKey);
            if (string.IsNullOrWhiteSpace(studentName) && !ambiguousTargets.Contains(targetKey) &&
                !string.IsNullOrWhiteSpace(computerName))
                studentsByComputer.TryGetValue(NormalizeTargetKey(computerName), out studentName);
            if (string.IsNullOrWhiteSpace(studentName) && !ambiguousTargets.Contains(targetKey) &&
                !IsIpAddressDisplayName(target))
                studentsByComputer.TryGetValue(targetKey, out studentName);
            if (string.IsNullOrWhiteSpace(studentName) && !string.IsNullOrWhiteSpace(computerName) &&
                knownStudentNames.Contains(computerName.Trim()))
                studentName = computerName.Trim();
            if (string.IsNullOrWhiteSpace(studentName)) studentName = "未填写姓名";
            return new TeacherPolicyTargetChoice(target, studentName,
                previous.TryGetValue(targetKey, out var selected) && selected);
        }).ToArray();
        WebsitePolicyTargetChoices = Array.AsReadOnly(choices);
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


    // Restored members omitted by the in-progress partial-class extraction.
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
                    Uri.CheckHostName(actualHost) != UriHostNameType.Dns || seat.ComputerNumber is not { } number ||
                    !string.Equals(actualHost, MachineNaming.CreateName(room.Prefix,
                        number.ToString(CultureInfo.InvariantCulture)), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"座位“{seat.StudentName}”关联的 Host 或地点已变化，或 Host 不再是固定 Windows 电脑名；请先核对设备关联。");
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
}
