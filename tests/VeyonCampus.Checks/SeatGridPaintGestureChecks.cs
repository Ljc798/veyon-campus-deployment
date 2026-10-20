using VeyonCampus.App;

internal static class SeatGridPaintGestureChecks
{
    public static void Run()
    {
        var gesture = new SeatGridPaintGesture();
        var emptyStart = Cell(1, 1, isSeat: false);
        var emptyNext = Cell(1, 2, isSeat: false);
        var lockedCell = Cell(1, 3, isSeat: false, canEdit: false);

        gesture.Begin(emptyStart);
        Assert(gesture.IsActive && gesture.PaintAsSeat && gesture.ShouldPaint(emptyStart) &&
               gesture.ShouldPaint(emptyNext) && !gesture.ShouldPaint(emptyStart) &&
               !gesture.ShouldPaint(lockedCell), "从空格开始应连续添加一次，且跳过只读格。");
        gesture.End();
        Assert(!gesture.IsActive && gesture.HasSuppressedClick,
            "指针释放结束绘制，但点击抑制要留到同一输入事件中的 Button.Click 处理。");
        Assert(gesture.ConsumeSuppressedClick(emptyStart with { IsSeat = true }) &&
               !gesture.HasSuppressedClick && !gesture.ConsumeSuppressedClick(emptyStart),
            "起始格在指针释放后最多抑制一次 Button.Click，避免添加后立即撤销。");

        var occupiedStart = Cell(2, 1, isSeat: true);
        var occupiedNext = Cell(2, 2, isSeat: true);
        gesture.Begin(occupiedStart);
        Assert(!gesture.IsActive && !gesture.PaintAsSeat &&
               !gesture.ShouldPaint(occupiedStart) && !gesture.ShouldPaint(occupiedNext) &&
               !gesture.ShouldPaint(Cell(2, 3, isSeat: false)),
            "从已有座位开始不应启动拖动删除；移除已有座位需单击并确认。");
        gesture.End();
        gesture.ClearSuppressedClick();
        Assert(!gesture.HasSuppressedClick, "没有 Button.Click 时，延后清理应清除残留抑制状态。");

        static TeacherSeatCell Cell(int row, int column, bool isSeat, bool canEdit = true) =>
            new(row, column, isSeat ? 1 : null, "", "", isSeat, false, CanEdit: canEdit);

        static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
