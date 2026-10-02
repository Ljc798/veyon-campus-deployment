using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace VeyonCampus.App;

public partial class StudentSetupUpdateWindow : Window
{
    private readonly StudentSetupUpdateViewModel _model = new();

    public StudentSetupUpdateWindow()
    {
        InitializeComponent();
        DataContext = _model;
        Opened += (_, _) => _ = _model.CheckLatestAsync();
    }

    private async void CheckLatest(object? sender, RoutedEventArgs e) => await _model.CheckLatestAsync();

    private async void DownloadAndInstall(object? sender, RoutedEventArgs e)
    {
        if (await _model.DownloadAndInstallAsync()) Close(true);
    }

    private async void ExportOfflineUpdate(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择离线 StudentSetup 更新包的导出目录",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            using var folder = folders[0];
            var destinationDirectory = folder.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                _model.ReportOfflineUpdateError("无法读取所选目录的本地路径；尚未导出更新包。");
                return;
            }
            await _model.ExportOfflineUpdateAsync(destinationDirectory);
        }
        catch (Exception exception)
        {
            _model.ReportOfflineUpdateError("无法选择或导出离线更新包：" + exception.Message);
        }
    }

    private async void SelectOfflineUpdate(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 StudentSetup 离线更新安装器",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("StudentSetup 安装器")
                    {
                        Patterns = ["VeyonCampus-Student-Setup-*-win-x64.exe"]
                    }
                ]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            var installerPath = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(installerPath))
            {
                _model.ReportOfflineUpdateError("无法读取所选文件的本地路径；没有复制或安装文件。");
                return;
            }
            await _model.VerifyOfflineUpdateAsync(installerPath);
        }
        catch (Exception exception)
        {
            _model.ReportOfflineUpdateError("无法选择或暂存离线安装器；没有启动安装。" + exception.Message);
        }
    }

    private void InstallOfflineUpdate(object? sender, RoutedEventArgs e)
    {
        if (_model.InstallOfflineUpdate()) Close(true);
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);
}
