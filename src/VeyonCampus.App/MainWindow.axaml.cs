using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;
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
    private void PackageDrop(object? sender, DragEventArgs e)
    {
        PackageDropZone.Classes.Set("dragover", false);
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        try
        {
            var path = GetSinglePath(e);
            if (path is null)
            {
                _model.RejectPackage("请每次只拖入一个本机部署包文件夹或清单文件；不能同时拖入多个文件。");
                return;
            }
            _model.LoadPackage(path);
            if (_model.LoadedPackage is not null) e.DragEffects = DragDropEffects.Copy;
        }
        catch (Exception ex) { _model.RejectPackage($"无法读取拖入的部署包：{ex.Message}"); }
    }
    private void ShowStudent(object? sender, RoutedEventArgs e) => _model.Navigate(true);
    private void ShowTeacher(object? sender, RoutedEventArgs e) => _model.Navigate(false);
    private void ResetForm(object? sender, RoutedEventArgs e) => _model.Reset();
    private void Preview(object? sender, RoutedEventArgs e) => _model.GeneratePreview();
    private async void CheckEnvironment(object? sender, RoutedEventArgs e) => await _model.CheckEnvironmentAsync();
    private void PreviewRoom(object? sender, RoutedEventArgs e) => _model.GenerateRoomPreview();
    private void CloseRoomPreview(object? sender, RoutedEventArgs e) => _model.CloseRoomPreview();
    private async void StartDeployment(object? sender, RoutedEventArgs e) => await _model.RunDeploymentAsync();
    private async void InstallVeyon(object? sender, RoutedEventArgs e) => await _model.InstallVeyonOnlyAsync();
    private void GeneratePackage(object? sender, RoutedEventArgs e) => _model.GenerateStudentPackage();
    private async void PickInstaller(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"选择 Veyon {VeyonCampus.Core.VeyonInstallerTrust.Version} 安装程序",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Veyon installer") { Patterns = new[] { "*.exe" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                }
            });
            if (files.Count > 0)
            {
                using var file = files[0];
                var path = file.TryGetLocalPath();
                if (path is not null) _model.InstallerSource = path;
            }
        }
        catch (Exception ex) { _model.PackageOutputError = "无法打开文件选择器：" + ex.Message; }
    }
    private void ShowOperationHelp(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string message }) _model.ToggleOperationHelp(message);
    }
    private async void SelectPackage(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!StorageProvider.CanPickFolder) { _model.RejectPackage("当前环境不支持文件夹选择。"); return; }
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择包含 manifest.json 或 campus.json 的部署包文件夹", AllowMultiple = false
            });
            if (folders.Count == 0) return;
            using var folder = folders[0];
            var path = folder.TryGetLocalPath();
            if (path is null) { _model.RejectPackage("请选择本机上的文件夹。"); return; }
            _model.LoadPackage(path);
        }
        catch (Exception ex) { _model.RejectPackage($"无法打开文件夹：{ex.Message}"); }
    }
}
