using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VeyonCampus.App.Presentation.Views;

public partial class TeacherUpdatesView : UserControl
{
    public TeacherUpdatesView() => InitializeComponent();

    public void ScrollToSection(string sectionName)
    {
        GetSectionControl(sectionName)?.BringIntoView();
    }

    public Control? GetSectionControl(string sectionName) => sectionName switch
    {
        "teacher-update" => this.FindControl<Control>("TeacherUpdateSection"),
        "student-maintenance" or "student-update" => this.FindControl<Control>("StudentUpdateSection"),
        "configuration-sync" => this.FindControl<Control>("ConfigurationSyncSection"),
        _ => null
    };

    public event EventHandler<RoutedEventArgs>? CheckTeacherUpdateRequested;
    public event EventHandler<RoutedEventArgs>? DownloadTeacherUpdateRequested;
    public event EventHandler<RoutedEventArgs>? ExportOfflineTeacherUpdateRequested;
    public event EventHandler<RoutedEventArgs>? VerifyOfflineTeacherUpdateRequested;
    public event EventHandler<RoutedEventArgs>? InstallOfflineTeacherUpdateRequested;
    public event EventHandler<RoutedEventArgs>? TrustStudentAgentIdentitiesRequested;
    public event EventHandler<RoutedEventArgs>? MaintainStudentDevicesRequested;
    public event EventHandler<RoutedEventArgs>? DeployStudentUpdateRequested;
    public event EventHandler<RoutedEventArgs>? UpdateMaintenanceServiceRequested;
    public event EventHandler<RoutedEventArgs>? SelectLocalStudentTestInstallerRequested;
    public event EventHandler<RoutedEventArgs>? SelectLocalTestMaintenanceInstallerRequested;
    public event EventHandler<RoutedEventArgs>? SyncStudentConfigurationRequested;
    public event EventHandler<RoutedEventArgs>? ReviewConfigurationIdentityRequested;
    public event EventHandler<RoutedEventArgs>? ReviewStudentUpdateIdentityRequested;
    public event EventHandler<RoutedEventArgs>? ExportStudentUpdatePerformanceReportRequested;
    public event EventHandler<RoutedEventArgs>? ExportMaintenanceEventReportRequested;
    public event EventHandler<RoutedEventArgs>? RecheckStudentUpdateResultRequested;

    private void RequestCheckTeacherUpdate(object? sender, RoutedEventArgs e) =>
        CheckTeacherUpdateRequested?.Invoke(sender, e);

    private void RequestDownloadTeacherUpdate(object? sender, RoutedEventArgs e) =>
        DownloadTeacherUpdateRequested?.Invoke(sender, e);

    private void RequestExportOfflineTeacherUpdate(object? sender, RoutedEventArgs e) =>
        ExportOfflineTeacherUpdateRequested?.Invoke(sender, e);

    private void RequestVerifyOfflineTeacherUpdate(object? sender, RoutedEventArgs e) =>
        VerifyOfflineTeacherUpdateRequested?.Invoke(sender, e);

    private void RequestInstallOfflineTeacherUpdate(object? sender, RoutedEventArgs e) =>
        InstallOfflineTeacherUpdateRequested?.Invoke(sender, e);

    private void RequestTrustStudentAgentIdentities(object? sender, RoutedEventArgs e) =>
        TrustStudentAgentIdentitiesRequested?.Invoke(sender, e);

    private void RequestMaintainStudentDevices(object? sender, RoutedEventArgs e) =>
        MaintainStudentDevicesRequested?.Invoke(sender, e);

    private void RequestDeployStudentUpdate(object? sender, RoutedEventArgs e) =>
        DeployStudentUpdateRequested?.Invoke(sender, e);

    private void RequestUpdateMaintenanceService(object? sender, RoutedEventArgs e) =>
        UpdateMaintenanceServiceRequested?.Invoke(sender, e);

    private void RequestSelectLocalStudentTestInstaller(object? sender, RoutedEventArgs e) =>
        SelectLocalStudentTestInstallerRequested?.Invoke(sender, e);

    private void RequestSelectLocalTestMaintenanceInstaller(object? sender, RoutedEventArgs e) =>
        SelectLocalTestMaintenanceInstallerRequested?.Invoke(sender, e);

    private void RequestSyncStudentConfiguration(object? sender, RoutedEventArgs e) =>
        SyncStudentConfigurationRequested?.Invoke(sender, e);

    private void RequestReviewConfigurationIdentity(object? sender, RoutedEventArgs e) =>
        ReviewConfigurationIdentityRequested?.Invoke(sender, e);

    private void RequestReviewStudentUpdateIdentity(object? sender, RoutedEventArgs e) =>
        ReviewStudentUpdateIdentityRequested?.Invoke(sender, e);

    private void RequestExportStudentUpdatePerformanceReport(object? sender, RoutedEventArgs e) =>
        ExportStudentUpdatePerformanceReportRequested?.Invoke(sender, e);

    private void RequestExportMaintenanceEventReport(object? sender, RoutedEventArgs e) =>
        ExportMaintenanceEventReportRequested?.Invoke(sender, e);

    private void RequestRecheckStudentUpdateResult(object? sender, RoutedEventArgs e) =>
        RecheckStudentUpdateResultRequested?.Invoke(sender, e);
}
