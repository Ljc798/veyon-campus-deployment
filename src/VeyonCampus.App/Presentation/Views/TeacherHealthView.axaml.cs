using Avalonia.Controls;
using Avalonia.Interactivity;
using VeyonCampus.App;

namespace VeyonCampus.App.Presentation.Views;

public partial class TeacherHealthView : UserControl
{
    public TeacherHealthView() => InitializeComponent();

    public void ScrollToRow(TeacherDeviceHealthRow row) => DeviceHealthRowsList.ScrollIntoView(row);

    public event EventHandler<RoutedEventArgs>? AcknowledgeDeviceHealthAlertRequested;
    public event EventHandler<RoutedEventArgs>? OpenStudentRosterRequested;
    public event EventHandler<RoutedEventArgs>? EnsureSelectedVeyonServicesRunningRequested;
    public event EventHandler<RoutedEventArgs>? EnsureVeyonServiceRunningRequested;
    public event EventHandler<RoutedEventArgs>? FocusDeviceHealthAlertRequested;
    public event EventHandler<RoutedEventArgs>? NextDeviceHealthPageRequested;
    public event EventHandler<RoutedEventArgs>? PreviousDeviceHealthPageRequested;
    public event EventHandler<RoutedEventArgs>? ReadStudentDeviceHealthRequested;
    public event EventHandler<RoutedEventArgs>? PrepareMacVeyonHelperRequested;
    public event EventHandler<RoutedEventArgs>? RecheckDeviceHealthAlertRequested;
    public event EventHandler<RoutedEventArgs>? RefreshLegacyEdgePolicyRequested;
    public event EventHandler<RoutedEventArgs>? RefreshSelectedLegacyEdgePoliciesRequested;
    public event EventHandler<RoutedEventArgs>? RepairDeviceHealthAlertRequested;

    private void RequestAcknowledgeDeviceHealthAlert(object? sender, RoutedEventArgs e) =>
        AcknowledgeDeviceHealthAlertRequested?.Invoke(sender, e);

    private void RequestOpenStudentRoster(object? sender, RoutedEventArgs e) =>
        OpenStudentRosterRequested?.Invoke(sender, e);

    private void RequestEnsureSelectedVeyonServicesRunning(object? sender, RoutedEventArgs e) =>
        EnsureSelectedVeyonServicesRunningRequested?.Invoke(sender, e);

    private void RequestEnsureVeyonServiceRunning(object? sender, RoutedEventArgs e) =>
        EnsureVeyonServiceRunningRequested?.Invoke(sender, e);

    private void RequestFocusDeviceHealthAlert(object? sender, RoutedEventArgs e) =>
        FocusDeviceHealthAlertRequested?.Invoke(sender, e);

    private void RequestNextDeviceHealthPage(object? sender, RoutedEventArgs e) =>
        NextDeviceHealthPageRequested?.Invoke(sender, e);

    private void RequestPreviousDeviceHealthPage(object? sender, RoutedEventArgs e) =>
        PreviousDeviceHealthPageRequested?.Invoke(sender, e);

    private void RequestReadStudentDeviceHealth(object? sender, RoutedEventArgs e) =>
        ReadStudentDeviceHealthRequested?.Invoke(sender, e);

    private void RequestPrepareMacVeyonHelper(object? sender, RoutedEventArgs e) =>
        PrepareMacVeyonHelperRequested?.Invoke(sender, e);

    private void RequestRecheckDeviceHealthAlert(object? sender, RoutedEventArgs e) =>
        RecheckDeviceHealthAlertRequested?.Invoke(sender, e);

    private void RequestRefreshLegacyEdgePolicy(object? sender, RoutedEventArgs e) =>
        RefreshLegacyEdgePolicyRequested?.Invoke(sender, e);

    private void RequestRefreshSelectedLegacyEdgePolicies(object? sender, RoutedEventArgs e) =>
        RefreshSelectedLegacyEdgePoliciesRequested?.Invoke(sender, e);

    private void RequestRepairDeviceHealthAlert(object? sender, RoutedEventArgs e) =>
        RepairDeviceHealthAlertRequested?.Invoke(sender, e);
}
