using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VeyonCampus.App.Presentation.Views;

public partial class TeacherClassroomView : UserControl
{
    public TeacherClassroomView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? ReadClassroomLocationsRequested;
    public event EventHandler<RoutedEventArgs>? ToggleClassroomSessionRequested;
    public event EventHandler<RoutedEventArgs>? SwitchClassroomScopeRequested;
    public event EventHandler<RoutedEventArgs>? OpenQuickControlWebsitePolicyRequested;
    public event EventHandler<RoutedEventArgs>? EnableClassroomWebsiteControlRequested;
    public event EventHandler<RoutedEventArgs>? LockClassroomInputRequested;
    public event EventHandler<RoutedEventArgs>? ClearClassroomWebsiteRestrictionsRequested;
    public event EventHandler<RoutedEventArgs>? ReleaseClassroomQuickControlRequested;
    public event EventHandler<RoutedEventArgs>? ReplyToClassroomEventRequested;
    public event EventHandler<RoutedEventArgs>? RetryClassroomRestoresRequested;

    private void RequestReadClassroomLocations(object? sender, RoutedEventArgs e) =>
        ReadClassroomLocationsRequested?.Invoke(sender, e);

    private void RequestToggleClassroomSession(object? sender, RoutedEventArgs e) =>
        ToggleClassroomSessionRequested?.Invoke(sender, e);

    private void RequestSwitchClassroomScope(object? sender, RoutedEventArgs e) =>
        SwitchClassroomScopeRequested?.Invoke(sender, e);

    private void RequestOpenQuickControlWebsitePolicy(object? sender, RoutedEventArgs e) =>
        OpenQuickControlWebsitePolicyRequested?.Invoke(sender, e);

    private void RequestEnableClassroomWebsiteControl(object? sender, RoutedEventArgs e) =>
        EnableClassroomWebsiteControlRequested?.Invoke(sender, e);

    private void RequestLockClassroomInput(object? sender, RoutedEventArgs e) =>
        LockClassroomInputRequested?.Invoke(sender, e);

    private void RequestClearClassroomWebsiteRestrictions(object? sender, RoutedEventArgs e) =>
        ClearClassroomWebsiteRestrictionsRequested?.Invoke(sender, e);

    private void RequestReleaseClassroomQuickControl(object? sender, RoutedEventArgs e) =>
        ReleaseClassroomQuickControlRequested?.Invoke(sender, e);

    private void RequestReplyToClassroomEvent(object? sender, RoutedEventArgs e) =>
        ReplyToClassroomEventRequested?.Invoke(sender, e);

    private void RequestRetryClassroomRestores(object? sender, RoutedEventArgs e) =>
        RetryClassroomRestoresRequested?.Invoke(sender, e);
}
