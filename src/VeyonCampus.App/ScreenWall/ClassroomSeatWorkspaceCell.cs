namespace VeyonCampus.App;

/// <summary>Read-only classroom projection of one real seat-grid cell and its active device, if any.</summary>
public sealed class ClassroomSeatWorkspaceCell
{
    public ClassroomSeatWorkspaceCell(TeacherSeatCell seat, ClassroomScreenTileViewModel? tile,
        string searchText, string statusFilter, Guid? locatedTargetId)
    {
        Seat = seat;
        Tile = tile;
        IsLocated = tile is not null && tile.TargetId == locatedTargetId;

        var search = searchText.Trim();
        var searchMatch = search.Length == 0 ||
            (tile?.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            seat.StudentName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            seat.ComputerLabel.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            seat.Row.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
            seat.Column.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
        var statusMatch = statusFilter switch
        {
            "在线" => tile?.ConnectionFilterKey == "在线",
            "异常" => tile?.ConnectionFilterKey == "异常" ||
                      (tile is null && seat.HasVeyonTarget),
            "网站受限" => tile?.IsWebsiteRestricted == true,
            "键鼠已锁" => tile?.IsInputLocked == true,
            "未排座位" => false,
            _ => true
        };
        IsDimmed = !searchMatch || !statusMatch;
    }

    public TeacherSeatCell Seat { get; }
    public ClassroomScreenTileViewModel? Tile { get; }
    public Guid? TargetId => Tile?.TargetId;
    public int Row => Seat.Row;
    public int Column => Seat.Column;
    public int? SeatNumber => Seat.SeatNumber;
    public bool IsSeat => Seat.IsSeat;
    public bool IsAisle => !Seat.IsSeat;
    public bool HasActiveDevice => Tile is not null;
    public bool IsEmptySeat => Seat.IsSeat && Tile is null && !Seat.HasVeyonTarget;
    public bool IsOfflineSeat => Seat.IsSeat && Tile is null && Seat.HasVeyonTarget;
    public bool IsUnboundSeat => Seat.IsSeat && !Seat.HasVeyonTarget;
    public bool IsSelected => Tile?.IsSelected == true;
    public bool IsLocated { get; }
    public bool IsDimmed { get; }
    public string SeatNumberText => Seat.SeatNumber is { } number && number > 0
        ? $"{number:00} 号位" : "座位待编号";
    public string StudentName => !string.IsNullOrWhiteSpace(Tile?.StudentName)
        ? Tile!.StudentName
        : !string.IsNullOrWhiteSpace(Seat.StudentName) ? Seat.StudentName : "未填写姓名";
    public string ComputerName => Tile?.ComputerName ?? Seat.ComputerLabel;
    public string DisplayName => Tile?.DisplayName ??
        (!string.IsNullOrWhiteSpace(Seat.StudentName) ? Seat.StudentName : Seat.ComputerLabel);
    public string CoordinateText => $"第 {Row} 行 · 第 {Column} 列";
    public string StatusText => Tile?.LinkStatusText ??
        (Seat.HasVeyonTarget ? "离线" : IsEmptySeat ? "空座位" : "未绑定");
    public string StatusSummary => Tile?.ConnectionSummary ??
        (Seat.HasVeyonTarget ? "座位已绑定设备，但设备不在当前活动课堂中" : "座位尚未绑定学生电脑");
    public string ToolTipText => $"{CoordinateText} · {StatusSummary}";
}
