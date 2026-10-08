using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace VeyonCampus.App;

public partial class StudentCloudPackageWindow : Window
{
    private readonly MainViewModel _model;

    public StudentCloudPackageWindow() : this(new MainViewModel()) { }

    public StudentCloudPackageWindow(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
    }

    private async void SearchPackages(object? sender, RoutedEventArgs e) =>
        await _model.SearchCloudPackagesAsync();

    private async void SearchQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        if (!_model.CanSearchCloudPackages) return;
        if (sender is TextBox query) _model.CloudPackageQuery = query.Text ?? "";
        await _model.SearchCloudPackagesAsync();
    }

    private async void LoadPackage(object? sender, RoutedEventArgs e)
    {
        await _model.LoadSelectedCloudPackageAsync();
        if (_model.LoadedPackage is not null) Close(true);
    }

    private async void SavePackage(object? sender, RoutedEventArgs e)
    {
        var selected = _model.SelectedCloudPackage;
        if (selected is null) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "下载校区配置 ZIP",
                SuggestedFileName = Path.GetFileName(selected.FileName),
                DefaultExtension = ".zip",
                FileTypeChoices =
                [
                    new FilePickerFileType("校区配置 ZIP")
                    {
                        Patterns = ["*.zip"],
                        MimeTypes = ["application/zip"]
                    }
                ]
            });
            var path = file?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) await _model.SaveSelectedCloudPackageAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _model.ReportCloudPackageError("无法保存 ZIP 文件：" + exception.Message);
        }
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);
}
