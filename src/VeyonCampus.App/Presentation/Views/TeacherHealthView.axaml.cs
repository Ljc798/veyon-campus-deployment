using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VeyonCampus.App.Presentation.Views;

public partial class TeacherHealthView : UserControl
{
    public TeacherHealthView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? ReadStudentDeviceHealthRequested;
    public event EventHandler<RoutedEventArgs>? EnsureSelectedVeyonServicesRunningRequested;
    public event EventHandler<RoutedEventArgs>? RefreshSelectedLegacyEdgePoliciesRequested;
    public event EventHandler<RoutedEventArgs>? AcknowledgeDeviceHealthAlertRequested;
    public event EventHandler<RoutedEventArgs>? RecheckDeviceHealthAlertRequested;
    public event EventHandler<RoutedEventArgs>? FocusDeviceHealthAlertRequested;
    public event EventHandler<RoutedEventArgs>? RepairDeviceHealthAlertRequested;
    public event EventHandler<RoutedEventArgs>? EnsureVeyonServiceRunningRequested;
    public event EventHandler<RoutedEventArgs>? RefreshLegacyEdgePolicyRequested;
    public event EventHandler<RoutedEventArgs>? PreviousDeviceHealthPageRequested;
    public event EventHandler<RoutedEventArgs>? NextDeviceHealthPageRequested;

    private void RequestReadStudentDeviceHealth(object? sender, RoutedEventArgs e) =>
        ReadStudentDeviceHealthRequested?.Invoke(sender, e);

    private void RequestEnsureSelectedVeyonServicesRunning(object? sender, RoutedEventArgs e) =>
        EnsureSelectedVeyonServicesRunningRequested?.Invoke(sender, e);

    private void RequestRefreshSelectedLegacyEdgePolicies(object? sender, RoutedEventArgs e) =>
        RefreshSelectedLegacyEdgePoliciesRequested?.Invoke(sender, e);

    private void RequestAcknowledgeDeviceHealthAlert(object? sender, RoutedEventArgs e) =>
        AcknowledgeDeviceHealthAlertRequested?.Invoke(sender, e);

    private void RequestRecheckDeviceHealthAlert(object? sender, RoutedEventArgs e) =>
        RecheckDeviceHealthAlertRequested?.Invoke(sender, e);

    private void RequestFocusDeviceHealthAlert(object? sender, RoutedEventArgs e) =>
        FocusDeviceHealthAlertRequested?.Invoke(sender, e);

    private void RequestRepairDeviceHealthAlert(object? sender, RoutedEventArgs e) =>
        RepairDeviceHealthAlertRequested?.Invoke(sender, e);

    private void RequestEnsureVeyonServiceRunning(object? sender, RoutedEventArgs e) =>
        EnsureVeyonServiceRunningRequested?.Invoke(sender, e);

    private void RequestRefreshLegacyEdgePolicy(object? sender, RoutedEventArgs e) =>
        RefreshLegacyEdgePolicyRequested?.Invoke(sender, e);

    private void RequestPreviousDeviceHealthPage(object? sender, RoutedEventArgs e) =>
        PreviousDeviceHealthPageRequested?.Invoke(sender, e);

    private void RequestNextDeviceHealthPage(object? sender, RoutedEventArgs e) =>
        NextDeviceHealthPageRequested?.Invoke(sender, e);

}
