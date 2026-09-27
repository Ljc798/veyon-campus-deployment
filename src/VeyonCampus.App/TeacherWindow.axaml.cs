using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

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
    }

    private void PreviewRoom(object? sender, RoutedEventArgs e) => _model.GenerateRoomPreview();
    private void CloseRoomPreview(object? sender, RoutedEventArgs e) => _model.CloseRoomPreview();
    private void FillWebsiteTargets(object? sender, RoutedEventArgs e) => _model.FillWebsiteTargetsFromRoom();
    private async void PushWebsitePolicy(object? sender, RoutedEventArgs e) => await _model.PushWebsitePolicyAsync();
    private async void DisableWebsitePolicy(object? sender, RoutedEventArgs e) => await _model.DisableWebsitePolicyAsync();
    private void FillFailedWebsiteTargets(object? sender, RoutedEventArgs e) => _model.FillFailedWebsiteTargets();
    private async void InstallTeacherVeyon(object? sender, RoutedEventArgs e) => await _model.InstallTeacherVeyonAsync();
    private async void GeneratePackage(object? sender, RoutedEventArgs e) => await _model.GenerateStudentPackageAsync();
}
