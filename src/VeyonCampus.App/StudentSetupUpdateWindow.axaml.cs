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
    private async void ExportUpdateDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
        {
            _model.ReportDiagnosticExportFailure();
            return;
        }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出本机更新诊断",
                SuggestedFileName = $"veyon-campus-update-diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON 诊断文件") { Patterns = ["*.json"] }],
                ShowOverwritePrompt = true
            });
            if (file is null) return;
            using (file)
            {
                var path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path)) _model.ReportDiagnosticExportFailure();
                else _model.ExportUpdateDiagnostics(path);
            }
        }
        catch (Exception)
        {
            _model.ReportDiagnosticExportFailure();
        }
    }

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
        catch (Exception)
        {
            _model.ReportOfflineUpdateError("无法选择或导出离线更新包；所选位置无法写入。");
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
        catch (Exception)
        {
            _model.ReportOfflineUpdateError("无法选择或暂存离线安装器；请重新选择完整的可信安装器和清单。");
        }
    }

    private void InstallOfflineUpdate(object? sender, RoutedEventArgs e)
    {
        if (_model.InstallOfflineUpdate()) Close(true);
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);
}
