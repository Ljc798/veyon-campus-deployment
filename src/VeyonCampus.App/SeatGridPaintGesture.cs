namespace VeyonCampus.App;

/// <summary>Tracks one click-and-drag gesture that adds seats to empty cells only.</summary>
internal sealed class SeatGridPaintGesture
{
    private readonly HashSet<(int Row, int Column)> _visitedCells = [];
    private (int Row, int Column)? _suppressedClickCell;
    private (int Row, int Column)? _lastPathCell;

    public bool IsActive { get; private set; }
    public bool PaintAsSeat { get; private set; }
    public bool HasSuppressedClick => _suppressedClickCell is not null;

    public void Begin(TeacherSeatCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        IsActive = !cell.IsSeat && cell.CanEdit;
        PaintAsSeat = IsActive;
        _visitedCells.Clear();
        _suppressedClickCell = IsActive ? (cell.Row, cell.Column) : null;
        _lastPathCell = IsActive ? (cell.Row, cell.Column) : null;
    }

    public IReadOnlyList<(int Row, int Column)> GetPathTo(int row, int column)
    {
        if (!IsActive) return Array.Empty<(int Row, int Column)>();
        var start = _lastPathCell ?? (Row: row, Column: column);
        _lastPathCell = (row, column);

        var result = new List<(int Row, int Column)>();
        var x0 = start.Column;
        var y0 = start.Row;
        var x1 = column;
        var y1 = row;
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            result.Add((y0, x0));
            if (x0 == x1 && y0 == y1) break;
            var doubledError = 2 * error;
            if (doubledError >= dy)
            {
                error += dy;
                x0 += sx;
            }
            if (doubledError <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
        return result;
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
        _lastPathCell = null;
    }

    public void ClearSuppressedClick() => _suppressedClickCell = null;
}
