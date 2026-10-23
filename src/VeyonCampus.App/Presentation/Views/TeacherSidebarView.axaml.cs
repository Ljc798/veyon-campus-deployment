using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VeyonCampus.App.Presentation.Views;

public partial class TeacherSidebarView : UserControl
{
    public TeacherSidebarView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? OpenMobileControlRequested;
    public event EventHandler<RoutedEventArgs>? ClearAllConfigurationRequested;
    public event EventHandler<RoutedEventArgs>? ExportUpdateDiagnosticsRequested;
    public event EventHandler<RoutedEventArgs>? OpenFeedbackIssueRequested;
    public event EventHandler<RoutedEventArgs>? OpenFeedbackContactsRequested;

    private void RequestOpenMobileControl(object? sender, RoutedEventArgs e) =>
        OpenMobileControlRequested?.Invoke(this, e);

    private void RequestClearAllConfiguration(object? sender, RoutedEventArgs e) =>
        ClearAllConfigurationRequested?.Invoke(this, e);

    private void RequestExportUpdateDiagnostics(object? sender, RoutedEventArgs e) =>
        ExportUpdateDiagnosticsRequested?.Invoke(this, e);

    private void RequestOpenFeedbackIssue(object? sender, RoutedEventArgs e) =>
        OpenFeedbackIssueRequested?.Invoke(this, e);

    private void RequestOpenFeedbackContacts(object? sender, RoutedEventArgs e) =>
        OpenFeedbackContactsRequested?.Invoke(this, e);

    private void ToggleSidebarHelp(object? sender, RoutedEventArgs e)
    {
        var expanded = SidebarHelpToggle.IsChecked == true;
        SidebarHelpContent.IsVisible = expanded;
        SidebarHelpExpandIcon.IsVisible = !expanded;
        SidebarHelpCollapseIcon.IsVisible = expanded;
    }
}
