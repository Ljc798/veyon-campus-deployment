using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VeyonCampus.App.Presentation.Views;

public partial class TeacherSetupView : UserControl
{
    public TeacherSetupView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? TeacherSetupStep1Requested;
    public event EventHandler<RoutedEventArgs>? TeacherSetupStep2Requested;
    public event EventHandler<RoutedEventArgs>? TeacherSetupStep3Requested;
    public event EventHandler<RoutedEventArgs>? InstallTeacherVeyonRequested;
    public event EventHandler<RoutedEventArgs>? RefreshTeacherSetupVeyonRequested;
    public event EventHandler<RoutedEventArgs>? TeacherSetupNextRequested;
    public event EventHandler<RoutedEventArgs>? OpenRoomSetupFromTeacherWizardRequested;
    public event EventHandler<RoutedEventArgs>? SkipTeacherSetupRoomRequested;

    private void RequestTeacherSetupStep1(object? sender, RoutedEventArgs e) =>
        TeacherSetupStep1Requested?.Invoke(sender, e);

    private void RequestTeacherSetupStep2(object? sender, RoutedEventArgs e) =>
        TeacherSetupStep2Requested?.Invoke(sender, e);

    private void RequestTeacherSetupStep3(object? sender, RoutedEventArgs e) =>
        TeacherSetupStep3Requested?.Invoke(sender, e);

    private void RequestInstallTeacherVeyon(object? sender, RoutedEventArgs e) =>
        InstallTeacherVeyonRequested?.Invoke(sender, e);

    private void RequestRefreshTeacherSetupVeyon(object? sender, RoutedEventArgs e) =>
        RefreshTeacherSetupVeyonRequested?.Invoke(sender, e);

    private void RequestTeacherSetupNext(object? sender, RoutedEventArgs e) =>
        TeacherSetupNextRequested?.Invoke(sender, e);

    private void RequestOpenRoomSetupFromTeacherWizard(object? sender, RoutedEventArgs e) =>
        OpenRoomSetupFromTeacherWizardRequested?.Invoke(sender, e);

    private void RequestSkipTeacherSetupRoom(object? sender, RoutedEventArgs e) =>
        SkipTeacherSetupRoomRequested?.Invoke(sender, e);
}
