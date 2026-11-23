using Avalonia.Controls;
using Avalonia.Interactivity;
using VeyonCampus.App;

namespace VeyonCampus.App.Presentation.Views;

public delegate void DeviceInputRequestHandler(Guid targetId);
public delegate void BatchInputRequestHandler(Guid[] targetIds);
public delegate void ToggleAllSelectionHandler(bool selected);
public delegate void ScreenDetailRequestHandler(ClassroomScreenTileViewModel tile);

public partial class TeacherClassroomView : UserControl
{
    public TeacherClassroomView()
    {
        InitializeComponent();
        ScreenWallHost.ToggleInputLockRequested += tile =>
        {
            if (tile.IsInputLocked || tile.IsInputUnverified) UnlockDeviceInputRequested?.Invoke(tile.TargetId);
            else LockDeviceInputRequested?.Invoke(tile.TargetId);
        };
        ScreenWallHost.ToggleWebsitePolicyRequested += tile =>
            WebsitePolicyRequested?.Invoke(tile.TargetId);
        ScreenWallHost.OpenDetailRequested += tile => OpenScreenDetailRequested?.Invoke(tile);
    }

    /// <summary>
    /// 由外层内容区把可见高度传进来：左右两栏按整屏高度布局，
    /// 只有右栏学生列表内部滚动，避免页面下半部分大片留白。
    /// </summary>
    public void SetViewportHeight(double height)
    {
        if (height <= 0) return;
        ClassroomRoot.Height = height;
    }

    public event EventHandler<RoutedEventArgs>? ReadClassroomLocationsRequested;
    public event EventHandler<RoutedEventArgs>? OpenQuickControlWebsitePolicyRequested;
    public event EventHandler<RoutedEventArgs>? EnableClassroomWebsiteControlRequested;
    public event EventHandler<RoutedEventArgs>? LockClassroomInputRequested;
    public event EventHandler<RoutedEventArgs>? ReleaseClassroomQuickControlRequested;
    public event EventHandler<RoutedEventArgs>? ReplyToClassroomEventRequested;
    public event EventHandler<RoutedEventArgs>? RetryClassroomRestoresRequested;

    /// <summary>Raised from a single device row: lock only that machine's keyboard and mouse.</summary>
    public event DeviceInputRequestHandler? LockDeviceInputRequested;

    /// <summary>Raised from a single device row: release only that machine's input lease.</summary>
    public event DeviceInputRequestHandler? UnlockDeviceInputRequested;

    /// <summary>Raised from a screen-wall card: apply or remove the saved website list on one machine.</summary>
    public event DeviceInputRequestHandler? WebsitePolicyRequested;

    /// <summary>Raised from a screen-wall card: open that machine's live picture in its own window.</summary>
    public event ScreenDetailRequestHandler? OpenScreenDetailRequested;

    private void SwitchToScreenWall(object? sender, RoutedEventArgs e)
    {
        if (_model is { } model) model.IsClassroomScreenWallMode = true;
    }

    private void SwitchToList(object? sender, RoutedEventArgs e)
    {
        if (_model is { } model) model.IsClassroomScreenWallMode = false;
    }

    /// <summary>Raised from the batch bar: lock/release every checked machine in one action.</summary>
    public event BatchInputRequestHandler? BatchLockInputRequested;
    public event BatchInputRequestHandler? BatchUnlockInputRequested;
    public event ToggleAllSelectionHandler? ToggleAllInputLockSelectionRequested;

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
        if ((sender as Control)?.DataContext is TeacherClassroomQuickControlDeviceItem item)
            LockDeviceInputRequested?.Invoke(item.TargetId);
    }

    private void RequestUnlockDeviceInput(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TeacherClassroomQuickControlDeviceItem item)
            UnlockDeviceInputRequested?.Invoke(item.TargetId);
    }

    private void ToggleAllInputLockSelection(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox check) ToggleAllInputLockSelectionRequested?.Invoke(check.IsChecked == true);
    }

    private void RequestBatchLockInput(object? sender, RoutedEventArgs e) =>
        BatchLockInputRequested?.Invoke(_selectedIds());

    private void RequestBatchUnlockInput(object? sender, RoutedEventArgs e) =>
        BatchUnlockInputRequested?.Invoke(_selectedIds());

    private Guid[] _selectedIds() =>
        _model is null ? [] : _model.GetSelectedInputLockTargetIds();

    private TeacherViewModel? _model => DataContext as TeacherViewModel;
}
