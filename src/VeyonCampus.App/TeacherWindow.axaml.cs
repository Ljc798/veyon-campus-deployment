using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace VeyonCampus.App;

public partial class TeacherWindow : Window
{
    private readonly TeacherViewModel _model = new();
    private readonly DispatcherTimer _teacherHeartbeatTimer = new() { Interval = TimeSpan.FromHours(1) };

    public TeacherWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
        DataContext = _model;
        _teacherHeartbeatTimer.Tick += CheckTeacherHeartbeat;
        _teacherHeartbeatTimer.Start();
        Closed += (_, _) => _teacherHeartbeatTimer.Stop();
    }

    private async void CheckTeacherHeartbeat(object? sender, EventArgs e) =>
        await _model.SendTeacherCampusHeartbeatIfDueAsync();

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
    private async void CheckTeacherUpdate(object? sender, RoutedEventArgs e) => await _model.CheckTeacherUpdateAsync();
    private async void DownloadTeacherUpdate(object? sender, RoutedEventArgs e)
    {
        if (await _model.DownloadTeacherUpdateAsync()) Close();
    }
    private async void DeployStudentUpdate(object? sender, RoutedEventArgs e) => await _model.DeployStudentUpdateAsync();
    private async void GeneratePackage(object? sender, RoutedEventArgs e) => await _model.GenerateStudentPackageAsync();
    private async void ReplaceWebsiteSigningKey(object? sender, RoutedEventArgs e) =>
        await _model.GenerateStudentPackageAsync(replaceUnavailableSigningKey: true);
    private async void PublishStudentPackage(object? sender, RoutedEventArgs e) => await _model.PublishStudentPackageAsync();
    private async void ChoosePublishPackageDirectory(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择学生校区配置包文件夹",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            using var folder = folders[0];
            var path = folder.TryGetLocalPath();
            if (path is not null) _model.PublishPackageDirectory = path;
        }
        catch (Exception exception)
        {
            _model.ReportPackagePublisherError("无法选择配置包文件夹：" + exception.Message);
        }
    }
}
