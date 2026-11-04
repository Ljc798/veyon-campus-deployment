using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using System.ComponentModel;

namespace VeyonCampus.App.Presentation.Views;

public partial class TeacherRoomView : UserControl
{
    public TeacherRoomView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? PrepareRoomSeatGridRequested;
    public event EventHandler<RoutedEventArgs>? SaveRoomProfileRequested;
    public event EventHandler<RoutedEventArgs>? ToggleSeatMapEditingRequested;
    public event EventHandler<RoutedEventArgs>? CancelSeatMapEditingRequested;
    public event EventHandler<RoutedEventArgs>? PrintSeatChartRequested;
    public event EventHandler<RoutedEventArgs>? OpenVeyonConfiguratorRequested;
    public event EventHandler<RoutedEventArgs>? DeleteRoomProfileRequested;
    public event EventHandler<RoutedEventArgs>? SeatCellClickedRequested;
    public event EventHandler<RoutedEventArgs>? AddSeatGridRowRequested;
    public event EventHandler<RoutedEventArgs>? AddSeatGridColumnRequested;
    public event EventHandler<RoutedEventArgs>? RetrySeatMapSyncRequested;
    public event EventHandler<RoutedEventArgs>? RetryInitialSeatGridImportRequested;
    public event EventHandler<SeatGridAxisEventArgs>? DeleteSeatGridRowRequested;
    public event EventHandler<SeatGridAxisEventArgs>? DeleteSeatGridColumnRequested;
    public event EventHandler<SeatGridSeatEventArgs>? DeleteSeatRequested;
    public event EventHandler<SeatGridSeatEventArgs>? ClearSeatVeyonTargetRequested;
    public event EventHandler<RoutedEventArgs>? OpenInitialSeatGridConflictLocationRequested;

    private void RequestPrepareRoomSeatGrid(object? sender, RoutedEventArgs e) =>
        PrepareRoomSeatGridRequested?.Invoke(sender, e);

    private void RequestSaveRoomProfile(object? sender, RoutedEventArgs e) =>
        SaveRoomProfileRequested?.Invoke(sender, e);

    private void RequestToggleSeatMapEditing(object? sender, RoutedEventArgs e) =>
        ToggleSeatMapEditingRequested?.Invoke(sender, e);

    private void RequestCancelSeatMapEditing(object? sender, RoutedEventArgs e) =>
        CancelSeatMapEditingRequested?.Invoke(sender, e);

    private void RequestPrintSeatChart(object? sender, RoutedEventArgs e) =>
        PrintSeatChartRequested?.Invoke(sender, e);

    private void RequestOpenVeyonConfigurator(object? sender, RoutedEventArgs e) =>
        OpenVeyonConfiguratorRequested?.Invoke(sender, e);

    private void RequestDeleteRoomProfile(object? sender, RoutedEventArgs e) =>
        DeleteRoomProfileRequested?.Invoke(sender, e);

    private void RequestSeatCellClicked(object? sender, RoutedEventArgs e) =>
        SeatCellClickedRequested?.Invoke(sender, e);

    private void RequestAddSeatGridRow(object? sender, RoutedEventArgs e) =>
        AddSeatGridRowRequested?.Invoke(sender, e);

    private void RequestAddSeatGridColumn(object? sender, RoutedEventArgs e) =>
        AddSeatGridColumnRequested?.Invoke(sender, e);

    private void RequestRetrySeatMapSync(object? sender, RoutedEventArgs e) =>
        RetrySeatMapSyncRequested?.Invoke(sender, e);

    private void RequestRetryInitialSeatGridImport(object? sender, RoutedEventArgs e) =>
        RetryInitialSeatGridImportRequested?.Invoke(sender, e);

    private void RequestOpenInitialSeatGridConflictLocation(object? sender, RoutedEventArgs e) =>
        OpenInitialSeatGridConflictLocationRequested?.Invoke(this, e);

    private void AxisHeaderPointerEntered(object? sender, PointerEventArgs e) => SetAxisDeleteMark(sender, true);

    private void AxisHeaderPointerExited(object? sender, PointerEventArgs e) => SetAxisDeleteMark(sender, false);

    private static void SetAxisDeleteMark(object? sender, bool showDeleteMark)
    {
        if (sender is not Button { DataContext: TeacherSeatGridAxis axis, Content: Grid content } ||
            content.Children.Count < 2) return;
        var show = showDeleteMark && axis.CanEdit;
        content.Children[0].IsVisible = !show;
        content.Children[1].IsVisible = show;
    }

    private void RequestDeleteSeatAxis(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TeacherSeatGridAxis { CanEdit: true } axis }) return;
        var request = new SeatGridAxisEventArgs(axis.Index);
        if (axis.IsRow) DeleteSeatGridRowRequested?.Invoke(this, request);
        else DeleteSeatGridColumnRequested?.Invoke(this, request);
    }

    private void RequestDeleteSeat(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TeacherViewModel { IsSeatMapEditing: true }) return;
        var cell = sender is MenuItem { DataContext: TeacherSeatCell { IsSeat: true } menuCell }
            ? menuCell
            : GetContextPlacementTarget(sender)?.DataContext as TeacherSeatCell;
        if (cell is { IsSeat: true })
            DeleteSeatRequested?.Invoke(this, new SeatGridSeatEventArgs(cell));
    }

    private void RequestClearSeatVeyonTarget(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TeacherViewModel { IsSeatMapEditing: true }) return;
        var cell = sender is MenuItem { DataContext: TeacherSeatCell { IsSeat: true } menuCell }
            ? menuCell
            : GetContextPlacementTarget(sender)?.DataContext as TeacherSeatCell;
        if (cell is { IsSeat: true, HasVeyonTarget: true })
            ClearSeatVeyonTargetRequested?.Invoke(this, new SeatGridSeatEventArgs(cell));
    }

    private void SeatCellContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu ||
            DataContext is not TeacherViewModel { IsSeatMapEditing: true } ||
            menu.PlacementTarget?.DataContext is not TeacherSeatCell { IsSeat: true } cell)
        {
            e.Cancel = true;
            return;
        }

        // Context menus live in a separate popup tree, so pass the clicked seat explicitly.
        menu.DataContext = cell;
        foreach (var item in menu.Items.OfType<MenuItem>()) item.DataContext = cell;
    }

    private static Control? GetContextPlacementTarget(object? sender)
    {
        if (sender is not MenuItem item) return null;
        var menu = item.Parent as ContextMenu ?? item.GetVisualAncestors().OfType<ContextMenu>().FirstOrDefault();
        return menu?.PlacementTarget;
    }

}

public sealed class SeatGridAxisEventArgs(int index) : EventArgs
{
    public int Index { get; } = index;
}

public sealed class SeatGridSeatEventArgs(TeacherSeatCell seat) : EventArgs
{
    public TeacherSeatCell Seat { get; } = seat;
}
