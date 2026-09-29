using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public partial class StudentSetupWindow : Window
{
    private readonly MainViewModel _model = new();

    public StudentSetupWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
        DataContext = _model;
        Opened += (_, _) => _ = _model.RefreshVeyonStatusAsync();
        foreach (var passwordInput in new[]
                 {
                     StudentInitialPasswordBox, StudentInitialPasswordConfirmationBox,
                     AdminPasswordBox, AdminPasswordConfirmationBox
                 })
        {
            passwordInput.IsUndoEnabled = false;
            passwordInput.CopyingToClipboard += (_, e) => e.Handled = true;
            passwordInput.CuttingToClipboard += (_, e) => e.Handled = true;
        }
        StudentInitialPasswordBox.TextChanged += (_, _) =>
            _model.SetStudentPasswordInput(StudentInitialPasswordBox.Text ?? "", StudentInitialPasswordConfirmationBox.Text ?? "");
        StudentInitialPasswordConfirmationBox.TextChanged += (_, _) =>
            _model.SetStudentPasswordInput(StudentInitialPasswordBox.Text ?? "", StudentInitialPasswordConfirmationBox.Text ?? "");
        AdminPasswordBox.TextChanged += (_, _) =>
            _model.SetAdminPasswordInput(AdminPasswordBox.Text ?? "", AdminPasswordConfirmationBox.Text ?? "");
        AdminPasswordConfirmationBox.TextChanged += (_, _) =>
            _model.SetAdminPasswordInput(AdminPasswordBox.Text ?? "", AdminPasswordConfirmationBox.Text ?? "");
        _model.ClearStudentPasswordRequested += (_, _) =>
        {
            StudentInitialPasswordBox.Text = "";
            StudentInitialPasswordConfirmationBox.Text = "";
        };
        _model.ClearAdminPasswordRequested += (_, _) =>
        {
            AdminPasswordBox.Text = "";
            AdminPasswordConfirmationBox.Text = "";
        };
        PackageDropZone.AddHandler(DragDrop.DragOverEvent, PackageDragOver);
        PackageDropZone.AddHandler(DragDrop.DragLeaveEvent, PackageDragLeave);
        PackageDropZone.AddHandler(DragDrop.DropEvent, PackageDrop);
    }

    private static string? GetSinglePath(DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles()?.Take(2).ToArray();
        if (items is not { Length: 1 }) return null;
        return items[0].TryGetLocalPath();
    }

    private void PackageDragOver(object? sender, DragEventArgs e)
    {
        bool accepted;
        try { accepted = PackageSource.IsCandidate(GetSinglePath(e)); }
        catch (Exception) { accepted = false; }
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        PackageDropZone.Classes.Set("dragover", accepted);
        e.Handled = true;
    }

    private void PackageDragLeave(object? sender, DragEventArgs e) => PackageDropZone.Classes.Set("dragover", false);

    private async void PackageDrop(object? sender, DragEventArgs e)
    {
        PackageDropZone.Classes.Set("dragover", false);
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        try
        {
            var path = GetSinglePath(e);
            if (path is null)
            {
                _model.RejectPackage("请每次只拖入一个本机校区配置包文件夹或清单文件；不能同时拖入多个文件。");
                return;
            }
            await _model.LoadPackageAsync(path);
            if (_model.LoadedPackage is not null) e.DragEffects = DragDropEffects.Copy;
        }
        catch (Exception ex) { _model.RejectPackage($"无法读取拖入的校区配置包：{ex.Message}"); }
    }

    private void ResetForm(object? sender, RoutedEventArgs e) => _model.Reset();
    private void ClearPackage(object? sender, RoutedEventArgs e) => _model.ClearPackage();
    private async void PrepareDeployment(object? sender, RoutedEventArgs e) => await _model.PrepareDeploymentAsync();
    private async void RefreshVeyonStatus(object? sender, RoutedEventArgs e) => await _model.RefreshVeyonStatusAsync();
    private async void VerifyStudentDeployment(object? sender, RoutedEventArgs e) => await _model.VerifyStudentDeploymentAsync();
    private async void InstallWebsitePolicyAgent(object? sender, RoutedEventArgs e) => await _model.InstallWebsitePolicyAgentAsync();
    private async void RemoveWebsitePolicyAgent(object? sender, RoutedEventArgs e)
    {
        var confirmation = new StudentWebsiteAgentRemovalConfirmationWindow();
        if (await confirmation.ShowDialog<bool>(this) == true)
            await _model.RemoveWebsitePolicyAgentAsync();
    }
    private async void FinishStudentSetup(object? sender, RoutedEventArgs e)
    {
        var confirmation = new StudentSetupCleanupConfirmationWindow();
        if (await confirmation.ShowDialog<bool>(this) != true) return;
        if (await _model.FinishStudentSetupAsync()) Close();
    }
    private async void StartDeployment(object? sender, RoutedEventArgs e) => await _model.RunDeploymentAsync();
    private async void InstallVeyon(object? sender, RoutedEventArgs e) => await _model.InstallVeyonOnlyAsync();
    private void ShowOperationHelp(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string message }) _model.ToggleOperationHelp(message);
    }

    private async void LoadSharedPackage(object? sender, RoutedEventArgs e)
    {
        var path = NetworkPackagePathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            SharedPackageStatus.Text = "请输入教师电脑提供的 Windows 共享文件夹路径。";
            SharedPackageStatus.IsVisible = true;
            return;
        }
        try
        {
            SharedPackageStatus.Text = "正在从共享文件夹读取配置……";
            SharedPackageStatus.IsVisible = true;
            await _model.LoadPackageAsync(path);
            SharedPackageStatus.Text = _model.LoadedPackage is null
                ? "载入失败：" + _model.PackageError
                : $"已载入校区“{_model.LoadedPackage.Campus}”配置。请继续核对部署操作和电脑编号。";
        }
        catch (Exception exception)
        {
            SharedPackageStatus.Text = "载入共享配置失败：" + exception.Message;
            SharedPackageStatus.IsVisible = true;
        }
    }

    private async void SearchCloudPackages(object? sender, RoutedEventArgs e) =>
        await _model.SearchCloudPackagesAsync();

    private async void LoadCloudPackage(object? sender, RoutedEventArgs e) =>
        await _model.LoadSelectedCloudPackageAsync();

    private async void SaveCloudPackage(object? sender, RoutedEventArgs e)
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

    private async void SelectPackage(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!StorageProvider.CanPickFolder) { _model.RejectPackage("当前环境不支持文件夹选择。"); return; }
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择包含校区公钥的配置包文件夹", AllowMultiple = false
            });
            if (folders.Count == 0) return;
            using var folder = folders[0];
            var path = folder.TryGetLocalPath();
            if (path is null) { _model.RejectPackage("请选择本机上的文件夹。"); return; }
            await _model.LoadPackageAsync(path);
        }
        catch (Exception ex) { _model.RejectPackage($"无法打开文件夹：{ex.Message}"); }
    }
}
