using Avalonia.Controls;
using Avalonia.Interactivity;

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
    public event EventHandler<RoutedEventArgs>? ApplySeatGridDimensionsRequested;
    public event EventHandler<RoutedEventArgs>? SwapSelectedSeatsRequested;
    public event EventHandler<RoutedEventArgs>? ToggleSeatAddRemoveModeRequested;
    public event EventHandler<RoutedEventArgs>? AssignMissingSeatComputerNumbersRequested;
    public event EventHandler<RoutedEventArgs>? AddRoomToVeyonRequested;
    public event EventHandler<RoutedEventArgs>? SeatCellClickedRequested;

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

    private void RequestApplySeatGridDimensions(object? sender, RoutedEventArgs e) =>
        ApplySeatGridDimensionsRequested?.Invoke(sender, e);

    private void RequestSwapSelectedSeats(object? sender, RoutedEventArgs e) =>
        SwapSelectedSeatsRequested?.Invoke(sender, e);

    private void RequestToggleSeatAddRemoveMode(object? sender, RoutedEventArgs e) =>
        ToggleSeatAddRemoveModeRequested?.Invoke(sender, e);

    private void RequestAssignMissingSeatComputerNumbers(object? sender, RoutedEventArgs e) =>
        AssignMissingSeatComputerNumbersRequested?.Invoke(sender, e);

    private void RequestAddRoomToVeyon(object? sender, RoutedEventArgs e) =>
        AddRoomToVeyonRequested?.Invoke(sender, e);

    private void RequestSeatCellClicked(object? sender, RoutedEventArgs e) =>
        SeatCellClickedRequested?.Invoke(sender, e);
}
