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
    public event EventHandler<RoutedEventArgs>? ChangeMacTeacherLoginStartRequested;
    public event EventHandler<RoutedEventArgs>? RefreshMacTeacherLoginStartRequested;
    public event EventHandler<RoutedEventArgs>? PrepareMacVeyonHelperRequested;
    public event EventHandler<RoutedEventArgs>? OpenRoomSetupFromTeacherWizardRequested;
    public event EventHandler<RoutedEventArgs>? SkipTeacherSetupRoomRequested;
    public event EventHandler<RoutedEventArgs>? GenerateStudentPackageRequested;
    public event EventHandler<RoutedEventArgs>? CancelStudentPackageGenerationRequested;
    public event EventHandler<RoutedEventArgs>? ReplaceWebsiteSigningKeyRequested;
    public event EventHandler<RoutedEventArgs>? ChooseTrustMigrationSourceRequested;
    public event EventHandler<RoutedEventArgs>? ImportMacVeyonPrivateKeyRequested;
    public event EventHandler<RoutedEventArgs>? ChooseStudentPackageOutputDirectoryRequested;

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

    private void RequestChangeMacTeacherLoginStart(object? sender, RoutedEventArgs e) =>
        ChangeMacTeacherLoginStartRequested?.Invoke(sender, e);

    private void RequestRefreshMacTeacherLoginStart(object? sender, RoutedEventArgs e) =>
        RefreshMacTeacherLoginStartRequested?.Invoke(sender, e);

    private void RequestPrepareMacVeyonHelper(object? sender, RoutedEventArgs e) =>
        PrepareMacVeyonHelperRequested?.Invoke(sender, e);

    private void RequestOpenRoomSetupFromTeacherWizard(object? sender, RoutedEventArgs e) =>
        OpenRoomSetupFromTeacherWizardRequested?.Invoke(sender, e);

    private void RequestSkipTeacherSetupRoom(object? sender, RoutedEventArgs e) =>
        SkipTeacherSetupRoomRequested?.Invoke(sender, e);

    private void RequestGenerateStudentPackage(object? sender, RoutedEventArgs e) =>
        GenerateStudentPackageRequested?.Invoke(sender, e);

    private void RequestCancelStudentPackageGeneration(object? sender, RoutedEventArgs e) =>
        CancelStudentPackageGenerationRequested?.Invoke(sender, e);

    private void RequestReplaceWebsiteSigningKey(object? sender, RoutedEventArgs e) =>
        ReplaceWebsiteSigningKeyRequested?.Invoke(sender, e);

    private void RequestChooseTrustMigrationSource(object? sender, RoutedEventArgs e) =>
        ChooseTrustMigrationSourceRequested?.Invoke(sender, e);

    private void RequestImportMacVeyonPrivateKey(object? sender, RoutedEventArgs e) =>
        ImportMacVeyonPrivateKeyRequested?.Invoke(sender, e);

    private void RequestChooseStudentPackageOutputDirectory(object? sender, RoutedEventArgs e) =>
        ChooseStudentPackageOutputDirectoryRequested?.Invoke(sender, e);
}
