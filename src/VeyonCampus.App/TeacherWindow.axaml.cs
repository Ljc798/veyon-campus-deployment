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
}
