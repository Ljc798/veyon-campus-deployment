using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;

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
    private static string? GetPackageDirectory(DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles()?.Take(2).ToArray();
        if (items is not { Length: 1 }) return null;
        var path = items[0].TryGetLocalPath();
        if (path is null) return null;
        if (Directory.Exists(path)) return path;
        return File.Exists(path) && string.Equals(Path.GetFileName(path), "campus.json", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(path) : null;
    }
    private void PackageDragOver(object? sender, DragEventArgs e)
    {
        var accepted = GetPackageDirectory(e) is not null;
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
            var directory = GetPackageDirectory(e);
            if (directory is null)
            {
                _model.ReportError("请每次拖入一个部署包文件夹或 campus.json 文件。");
                return;
            }
            _model.LoadPackage(directory);
            e.DragEffects = DragDropEffects.Copy;
        }
        catch (Exception ex) { _model.ReportError($"无法读取拖入的部署包：{ex.Message}"); }
    }
    private void ShowStudent(object? sender, RoutedEventArgs e) => _model.Navigate(true);
    private void ShowTeacher(object? sender, RoutedEventArgs e) => _model.Navigate(false);
    private void ResetForm(object? sender, RoutedEventArgs e) => _model.Reset();
    private void Preview(object? sender, RoutedEventArgs e) => _model.GeneratePreview();
    private async void SelectPackage(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!StorageProvider.CanPickFolder) { _model.ReportError("当前环境不支持文件夹选择。"); return; }
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择包含 campus.json 的学生部署包文件夹", AllowMultiple = false
            });
            if (folders.Count == 0) return;
            using var folder = folders[0];
            var path = folder.TryGetLocalPath();
            if (path is null) { _model.ReportError("请选择本机上的文件夹。"); return; }
            _model.LoadPackage(path);
        }
        catch (Exception ex) { _model.ReportError($"无法打开文件夹：{ex.Message}"); }
    }
}
