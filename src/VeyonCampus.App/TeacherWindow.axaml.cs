using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;

namespace VeyonCampus.App;

public partial class TeacherWindow : Window
{
    private readonly TeacherViewModel _model = new();

    public TeacherWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
        DataContext = _model;
        Closing += HandleClosing;
    }

    private void PreviewRoom(object? sender, RoutedEventArgs e) => _model.GenerateRoomPreview();
    private async void AddRoomToVeyon(object? sender, RoutedEventArgs e) => await _model.AddRoomToVeyonAsync();
    private void OpenVeyonConfigurator(object? sender, RoutedEventArgs e) => _model.OpenVeyonConfigurator();
    private async void FillWebsiteTargets(object? sender, RoutedEventArgs e) => await _model.ReadWebsiteLocationsAsync();
    private void FillWebsiteTargetsFromRoom(object? sender, RoutedEventArgs e) => _model.FillWebsiteTargetsFromRoom();
    private void FillSelectedWebsiteLocation(object? sender, RoutedEventArgs e) => _model.FillWebsiteTargetsFromSelectedLocation();
    private async void PushWebsitePolicy(object? sender, RoutedEventArgs e) => await _model.PushWebsitePolicyAsync();
    private async void DisableWebsitePolicy(object? sender, RoutedEventArgs e) => await _model.DisableWebsitePolicyAsync();
    private void FillFailedWebsiteTargets(object? sender, RoutedEventArgs e) => _model.FillFailedWebsiteTargets();
    private async void InstallTeacherVeyon(object? sender, RoutedEventArgs e) => await _model.InstallTeacherVeyonAsync();
    private async void ConfigureTeacherAuthentication(object? sender, RoutedEventArgs e) => await _model.ConfigureTeacherAuthenticationAsync();
    private async void GeneratePackage(object? sender, RoutedEventArgs e) => await _model.GenerateStudentPackageAsync();
    private async void ReplaceWebsiteSigningKey(object? sender, RoutedEventArgs e) =>
        await _model.GenerateStudentPackageAsync(replaceUnavailableSigningKey: true);
    private async void StartLanDistribution(object? sender, RoutedEventArgs e) => await _model.StartLanDistributionAsync();
    private async void StopLanDistribution(object? sender, RoutedEventArgs e) => await _model.StopLanDistributionAsync();

    private async void SelectLanPackageDirectory(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder)
        {
            return;
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择已生成的校区配置包文件夹",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        using var folder = folders[0];
        if (folder.TryGetLocalPath() is { } path) _model.SetLanPackageDirectory(path);
    }

    private bool _closingAfterLanStop;
    private async void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingAfterLanStop) return;
        e.Cancel = true;
        _closingAfterLanStop = true;
        await _model.ShutdownLanDistributionAsync();
        Close();
    }
}
