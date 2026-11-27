using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VeyonCampus.App;

namespace VeyonCampus.App.Presentation.Views;

public delegate void DeviceInputRequestHandler(Guid targetId);
public delegate void BatchInputRequestHandler(Guid[] targetIds);
public delegate void ToggleAllSelectionHandler(bool selected);
public delegate void ScreenDetailRequestHandler(ClassroomScreenTileViewModel tile);

public partial class TeacherClassroomView : UserControl
{
    private ClassroomScreenTileViewModel? _contextMenuTile;

    public TeacherClassroomView()
    {
        InitializeComponent();
        ScreenWallHost.DeviceActionRequested += RaiseDeviceAction;
        ScreenWallHost.OpenDetailRequested += tile => OpenScreenDetailRequested?.Invoke(tile);
        ScreenWallHost.DeviceSelected += (tile, extend) =>
            _model?.ToggleClassroomDeviceSelection(tile.TargetId, extend);
    }

    /// <summary>The outer content area gives the classroom workspace its full available height.</summary>
    public void SetViewportHeight(double height)
    {
        if (height <= 0) return;
        ClassroomRoot.Height = height;
    }

    public void ScrollToClassroomTarget(Guid targetId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var target = ClassroomSeatMapGrid.GetVisualDescendants().OfType<Control>().FirstOrDefault(control =>
                control.DataContext is ClassroomSeatWorkspaceCell cell && cell.TargetId == targetId)
                ?? ClassroomUnseatedDevices.GetVisualDescendants().OfType<Control>().FirstOrDefault(control =>
                    control.DataContext is ClassroomScreenTileViewModel tile && tile.TargetId == targetId);
            target?.BringIntoView();
        });
    }

    public event EventHandler<RoutedEventArgs>? ReadClassroomLocationsRequested;
    public event EventHandler<RoutedEventArgs>? OpenQuickControlWebsitePolicyRequested;
    public event EventHandler<RoutedEventArgs>? EnableClassroomWebsiteControlRequested;
    public event EventHandler<RoutedEventArgs>? RestoreClassroomWebsiteAccessRequested;
    public event EventHandler<RoutedEventArgs>? LockClassroomInputRequested;
    public event EventHandler<RoutedEventArgs>? ReleaseClassroomQuickControlRequested;
    public event EventHandler<RoutedEventArgs>? ReplyToClassroomEventRequested;
    public event EventHandler<RoutedEventArgs>? RetryClassroomRestoresRequested;
    public event DeviceInputRequestHandler? LockDeviceInputRequested;
    public event DeviceInputRequestHandler? UnlockDeviceInputRequested;
    public event DeviceInputRequestHandler? WebsitePolicyRequested;
    public event ScreenDetailRequestHandler? OpenScreenDetailRequested;
    public event ClassroomDeviceActionRequestHandler? DeviceActionRequested;
    public event BatchInputRequestHandler? BatchLockInputRequested;
    public event BatchInputRequestHandler? BatchUnlockInputRequested;
    public event ToggleAllSelectionHandler? ToggleAllInputLockSelectionRequested;
    public event EventHandler<RoutedEventArgs>? BlockNetworkChangeRequested;
    public event EventHandler<RoutedEventArgs>? AllowNetworkChangeRequested;
    public event EventHandler<RoutedEventArgs>? WifiOffRequested;
    public event EventHandler<RoutedEventArgs>? WifiOnRequested;

    private void SwitchWorkspace(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } || !Enum.TryParse<ClassroomWorkspaceView>(value, out var view))
            return;
        _model?.SetClassroomWorkspaceView(view);
    }

    private void ToggleControlDrawer(object? sender, RoutedEventArgs e) => _model?.ToggleClassroomControlDrawer();

    private void ToggleTaskPanel(object? sender, RoutedEventArgs e)
    {
        if (_model is { } model) model.IsClassroomTaskPanelExpanded = !model.IsClassroomTaskPanelExpanded;
    }

    private void OpenSeatEditor(object? sender, RoutedEventArgs e)
    {
        if (_model is { } model) model.IsRoomPage = true;
    }

    private void SelectDevice(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ClassroomScreenTileViewModel tile } control) return;
        if (e.Source is CheckBox) return;
        var properties = e.GetCurrentPoint(control).Properties;
        if (properties.IsRightButtonPressed)
        {
            _contextMenuTile = tile;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        _model?.ToggleClassroomDeviceSelection(tile.TargetId,
            e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    private void ListDeviceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ClassroomScreenTileViewModel tile })
            OpenScreenDetailRequested?.Invoke(tile);
    }

    private void OpenListDeviceDetail(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClassroomScreenTileViewModel tile })
            OpenScreenDetailRequested?.Invoke(tile);
    }

    private void SeatCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ClassroomSeatWorkspaceCell { Tile: { } tile } cell } control)
            return;
        var properties = e.GetCurrentPoint(control).Properties;
        if (properties.IsRightButtonPressed)
        {
            _contextMenuTile = tile;
            return;
        }
        if (properties.IsLeftButtonPressed)
            _model?.ToggleClassroomDeviceSelection(tile.TargetId,
                e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    private void SeatCellDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ClassroomSeatWorkspaceCell { Tile: { } tile } })
            OpenScreenDetailRequested?.Invoke(tile);
    }

    private void DeviceContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            e.Cancel = true;
            return;
        }

        var placementContext = menu.PlacementTarget?.DataContext;
        var tile = menu.PlacementTarget is null
            ? _contextMenuTile
            : placementContext switch
            {
                ClassroomScreenTileViewModel target => target,
                ClassroomSeatWorkspaceCell { Tile: { } target } => target,
                _ => null
            };
        if (tile is null)
        {
            e.Cancel = true;
            return;
        }

        _contextMenuTile = tile;
        menu.DataContext = tile;
        foreach (var item in menu.Items.OfType<MenuItem>()) item.DataContext = tile;
    }

    private void RequestDeviceAction(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item ||
            !Enum.TryParse<ClassroomDeviceAction>(item.Tag?.ToString(), out var action)) return;
        var tile = item.DataContext as ClassroomScreenTileViewModel;
        if (tile is not null) DeviceActionRequested?.Invoke(tile.TargetId, action);
    }

    private void RaiseDeviceAction(Guid targetId, ClassroomDeviceAction action) =>
        DeviceActionRequested?.Invoke(targetId, action);

    private static ClassroomScreenTileViewModel? ResolveContextTile(object? sender)
    {
        if (sender is Control { DataContext: ClassroomScreenTileViewModel tile }) return tile;
        if (sender is Control { DataContext: ClassroomSeatWorkspaceCell { Tile: { } seatTile } }) return seatTile;
        if (sender is not MenuItem item) return null;
        if (item.DataContext is ClassroomScreenTileViewModel menuTile) return menuTile;
        var menu = item.Parent as ContextMenu ?? item.GetVisualAncestors().OfType<ContextMenu>().FirstOrDefault();
        return menu?.PlacementTarget?.DataContext switch
        {
            ClassroomScreenTileViewModel target => target,
            ClassroomSeatWorkspaceCell { Tile: { } target } => target,
            _ => null
        };
    }

    private void RequestReadClassroomLocations(object? sender, RoutedEventArgs e) =>
        ReadClassroomLocationsRequested?.Invoke(sender, e);
    private void RequestOpenQuickControlWebsitePolicy(object? sender, RoutedEventArgs e) =>
        OpenQuickControlWebsitePolicyRequested?.Invoke(sender, e);
    private void RequestEnableClassroomWebsiteControl(object? sender, RoutedEventArgs e) =>
        EnableClassroomWebsiteControlRequested?.Invoke(sender, e);
    private void RequestLockClassroomInput(object? sender, RoutedEventArgs e) =>
        LockClassroomInputRequested?.Invoke(sender, e);
    private void RequestReleaseClassroomQuickControl(object? sender, RoutedEventArgs e) =>
        ReleaseClassroomQuickControlRequested?.Invoke(sender, e);
    private void RequestReplyToClassroomEvent(object? sender, RoutedEventArgs e) =>
        ReplyToClassroomEventRequested?.Invoke(sender, e);
    private void RequestRetryClassroomRestores(object? sender, RoutedEventArgs e) =>
        RetryClassroomRestoresRequested?.Invoke(sender, e);

    private void RequestLockDeviceInput(object? sender, RoutedEventArgs e)
    {
        var tile = ResolveContextTile(sender);
        if (tile is not null) LockDeviceInputRequested?.Invoke(tile.TargetId);
    }

    private void RequestUnlockDeviceInput(object? sender, RoutedEventArgs e)
    {
        var tile = ResolveContextTile(sender);
        if (tile is not null) UnlockDeviceInputRequested?.Invoke(tile.TargetId);
    }

    private void RequestWebsitePolicy(object? sender, RoutedEventArgs e)
    {
        var tile = ResolveContextTile(sender);
        if (tile is not null) WebsitePolicyRequested?.Invoke(tile.TargetId);
    }

    private void ToggleAllInputLockSelection(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox check) ToggleAllInputLockSelectionRequested?.Invoke(check.IsChecked == true);
    }

    private void RestoreClassroomWebsiteAccess(object? sender, RoutedEventArgs e) =>
        RestoreClassroomWebsiteAccessRequested?.Invoke(sender, e);

    private void SelectionCheckBoxClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ClassroomScreenTileViewModel tile } check) return;
        _model?.SetClassroomDeviceSelected(tile.TargetId, check.IsChecked == true);
    }

    private void RequestBatchLockInput(object? sender, RoutedEventArgs e) =>
        BatchLockInputRequested?.Invoke(_model?.GetSelectedInputLockTargetIds() ?? []);

    private void RequestBatchUnlockInput(object? sender, RoutedEventArgs e) =>
        BatchUnlockInputRequested?.Invoke(_model?.GetSelectedInputLockTargetIds() ?? []);

    private void RequestBlockNetworkChange(object? sender, RoutedEventArgs e) =>
        BlockNetworkChangeRequested?.Invoke(sender, e);

    private void RequestAllowNetworkChange(object? sender, RoutedEventArgs e) =>
        AllowNetworkChangeRequested?.Invoke(sender, e);

    private void RequestWifiOff(object? sender, RoutedEventArgs e) =>
        WifiOffRequested?.Invoke(sender, e);

    private void RequestWifiOn(object? sender, RoutedEventArgs e) =>
        WifiOnRequested?.Invoke(sender, e);

    private TeacherViewModel? _model => DataContext as TeacherViewModel;
}
