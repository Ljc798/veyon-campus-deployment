namespace VeyonCampus.App;

/// <summary>Tracks one click-and-drag gesture that paints seats to the starting cell's opposite state.</summary>
internal sealed class SeatGridPaintGesture
{
    private readonly HashSet<(int Row, int Column)> _visitedCells = [];
    private (int Row, int Column)? _suppressedClickCell;

    public bool IsActive { get; private set; }
    public bool PaintAsSeat { get; private set; }
    public bool HasSuppressedClick => _suppressedClickCell is not null;

    public void Begin(TeacherSeatCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        IsActive = true;
        PaintAsSeat = !cell.IsSeat;
        _visitedCells.Clear();
        _suppressedClickCell = (cell.Row, cell.Column);
    }

    public bool ShouldPaint(TeacherSeatCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (!IsActive || !cell.CanEdit || !_visitedCells.Add((cell.Row, cell.Column))) return false;
        return cell.IsSeat != PaintAsSeat;
    }

    public bool ConsumeSuppressedClick(TeacherSeatCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (_suppressedClickCell != (cell.Row, cell.Column)) return false;
        _suppressedClickCell = null;
        return true;
    }

    public void End()
    {
        IsActive = false;
        _visitedCells.Clear();
    }

    public void ClearSuppressedClick() => _suppressedClickCell = null;
}
