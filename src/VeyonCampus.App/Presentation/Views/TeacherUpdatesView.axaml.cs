using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VeyonCampus.App.Presentation.Views;

public partial class TeacherUpdatesView : UserControl
{
    public TeacherUpdatesView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? CheckTeacherUpdateRequested;
    public event EventHandler<RoutedEventArgs>? DownloadTeacherUpdateRequested;
    public event EventHandler<RoutedEventArgs>? ExportOfflineTeacherUpdateRequested;
    public event EventHandler<RoutedEventArgs>? VerifyOfflineTeacherUpdateRequested;
    public event EventHandler<RoutedEventArgs>? InstallOfflineTeacherUpdateRequested;
    public event EventHandler<RoutedEventArgs>? TrustStudentAgentIdentitiesRequested;
    public event EventHandler<RoutedEventArgs>? DeployStudentUpdateRequested;
    public event EventHandler<RoutedEventArgs>? ExportStudentUpdatePerformanceReportRequested;
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

    private void RequestDeployStudentUpdate(object? sender, RoutedEventArgs e) =>
        DeployStudentUpdateRequested?.Invoke(sender, e);

    private void RequestExportStudentUpdatePerformanceReport(object? sender, RoutedEventArgs e) =>
        ExportStudentUpdatePerformanceReportRequested?.Invoke(sender, e);

    private void RequestRecheckStudentUpdateResult(object? sender, RoutedEventArgs e) =>
        RecheckStudentUpdateResultRequested?.Invoke(sender, e);
}
