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
    private CancellationTokenSource? _lanPackageDownloadCancellation;
    private bool _closeAfterDownloadCancellation, _closingAfterDownloadCancellation;

    public StudentSetupWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
        DataContext = _model;
        Closing += HandleClosing;
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
        LanPairingCodeBox.IsUndoEnabled = false;
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

    private async void DownloadLanPackage(object? sender, RoutedEventArgs e)
    {
        if (_lanPackageDownloadCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        _lanPackageDownloadCancellation = cancellation;
        DownloadLanPackageButton.IsVisible = false;
        CancelLanPackageDownloadButton.IsVisible = true;
        LanPackageTransferStatus.Text = "正在准备局域网连接……";
        try
        {
            var progress = new Progress<string>(message => LanPackageTransferStatus.Text = message);
            var received = await StudentLanPackageTransfer.DownloadAsync(
                LanTeacherAddressBox.Text ?? "", LanPairingCodeBox.Text ?? "",
                LanCertificateCodeBox.Text ?? "", progress, cancellation.Token);
            await _model.LoadPackageAsync(received.DirectoryPath);
            LanPackageTransferStatus.Text = _model.LoadedPackage is null
                ? "文件已传输，但学生端未能载入配置：" + _model.PackageError
                : $"已安全接收校区“{received.Campus}”配置（清单 v{received.SchemaVersion}，{received.ArchiveBytes:N0} 字节）；当前学生部署工具 v{_model.AppVersion}。请继续核对部署操作和电脑编号。";
        }
        catch (OperationCanceledException)
        {
            LanPackageTransferStatus.Text = "下载已取消；未使用不完整文件。";
        }
        catch (Exception exception)
        {
            LanPackageTransferStatus.Text = "获取失败：" + exception.Message;
        }
        finally
        {
            LanPairingCodeBox.Text = "";
            _lanPackageDownloadCancellation = null;
            cancellation.Dispose();
            DownloadLanPackageButton.IsVisible = true;
            CancelLanPackageDownloadButton.IsVisible = false;
            if (_closeAfterDownloadCancellation)
            {
                _closeAfterDownloadCancellation = false;
                _closingAfterDownloadCancellation = true;
                Close();
            }
        }
    }

    private void CancelLanPackageDownload(object? sender, RoutedEventArgs e) =>
        _lanPackageDownloadCancellation?.Cancel();

    private void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingAfterDownloadCancellation || _lanPackageDownloadCancellation is null) return;
        e.Cancel = true;
        _closeAfterDownloadCancellation = true;
        _lanPackageDownloadCancellation.Cancel();
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
