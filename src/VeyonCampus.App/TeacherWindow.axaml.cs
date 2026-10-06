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
    private bool _isShowingReleaseNotice;

    public TeacherWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
        DataContext = _model;
        _model.ReleaseNoticeAvailable += OnReleaseNoticeAvailable;
        _teacherHeartbeatTimer.Tick += CheckTeacherHeartbeat;
        _teacherHeartbeatTimer.Start();
        Opened += (_, _) => ShowPendingReleaseNotice();
        Closed += (_, _) =>
        {
            _teacherHeartbeatTimer.Stop();
            _model.ReleaseNoticeAvailable -= OnReleaseNoticeAvailable;
        };
    }

    private async void CheckTeacherHeartbeat(object? sender, EventArgs e)
    {
        await _model.SendTeacherCampusHeartbeatIfDueAsync();
        ShowPendingReleaseNotice();
    }

    private void OnReleaseNoticeAvailable(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(ShowPendingReleaseNotice);

    private async void ShowPendingReleaseNotice()
    {
        if (!IsVisible || _isShowingReleaseNotice) return;
        var releaseDetails = _model.TakePendingReleaseNotice();
        if (releaseDetails is null) return;
        _isShowingReleaseNotice = true;
        try
        {
            await new TeacherReleaseNoticeWindow(releaseDetails).ShowDialog(this);
        }
        catch (InvalidOperationException)
        {
            // The notice is informational; it must never interfere with the heartbeat or main window.
        }
        finally
        {
            _isShowingReleaseNotice = false;
        }
    }

    private async void PreviewRoom(object? sender, RoutedEventArgs e) => await _model.InspectRoomConflictsAsync();
    private async void AddRoomToVeyon(object? sender, RoutedEventArgs e) => await _model.AddRoomToVeyonAsync();
    private void OpenVeyonConfigurator(object? sender, RoutedEventArgs e) => _model.OpenVeyonConfigurator();
    private void NewCampusProfile(object? sender, RoutedEventArgs e) => _model.NewCampusProfile();
    private void SaveCampusProfile(object? sender, RoutedEventArgs e) => _model.SaveCampusProfile();
    private async void DeleteCampusProfile(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmProfileDeletionAsync("删除校区档案",
                "将从本机删除所选校区档案及其机房档案。此操作不会修改 Veyon；删除后不能从本机恢复。"))
            _model.DeleteSelectedCampusProfile();
    }
    private void NewRoomProfile(object? sender, RoutedEventArgs e) => _model.NewRoomProfile();
    private void SaveRoomProfile(object? sender, RoutedEventArgs e) => _model.SaveRoomProfile();
    private async void DeleteRoomProfile(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmProfileDeletionAsync("删除机房档案",
                "将从本机删除所选机房档案。此操作不会修改 Veyon；删除后不能从本机恢复。"))
            _model.DeleteSelectedRoomProfile();
    }
    private void LoadRoomProfile(object? sender, RoutedEventArgs e) => _model.LoadSelectedRoomProfileIntoBuilder();

    private async Task<bool> ConfirmProfileDeletionAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.White
        };
        var layout = new StackPanel { Spacing = 18, Margin = new Avalonia.Thickness(24) };
        layout.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 90 };
        var confirm = new Button { Content = "确认删除", MinWidth = 100 };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        layout.Children.Add(actions);
        dialog.Content = layout;
        return await dialog.ShowDialog<bool>(this);
    }

    private async void FillWebsiteTargets(object? sender, RoutedEventArgs e) => await _model.ReadWebsiteLocationsAsync();
    private void FillWebsiteTargetsFromRoom(object? sender, RoutedEventArgs e) => _model.FillWebsiteTargetsFromRoom();
    private void FillSelectedWebsiteLocation(object? sender, RoutedEventArgs e) => _model.FillWebsiteTargetsFromSelectedLocation();
    private async void PushWebsitePolicy(object? sender, RoutedEventArgs e) => await _model.PushWebsitePolicyAsync();
    private async void DisableWebsitePolicy(object? sender, RoutedEventArgs e) => await _model.DisableWebsitePolicyAsync();
    private void FillFailedWebsiteTargets(object? sender, RoutedEventArgs e) => _model.FillFailedWebsiteTargets();
    private async void PushApplicationPolicy(object? sender, RoutedEventArgs e) => await _model.PushApplicationPolicyAsync();
    private async void DisableApplicationPolicy(object? sender, RoutedEventArgs e) => await _model.DisableApplicationPolicyAsync();
    private async void ReadApplicationPolicyAudit(object? sender, RoutedEventArgs e) => await _model.ReadApplicationPolicyAuditAsync();
    private async void ReadApplicationInventory(object? sender, RoutedEventArgs e) => await _model.ReadApplicationInventoryAsync();
    private void AddSelectedApplicationRules(object? sender, RoutedEventArgs e) => _model.AddSelectedApplicationRules();
    private async void InstallTeacherVeyon(object? sender, RoutedEventArgs e) => await _model.InstallTeacherVeyonAsync();
    private async void CheckTeacherUpdate(object? sender, RoutedEventArgs e) => await _model.CheckTeacherUpdateAsync();
    private void OpenFeedbackIssue(object? sender, RoutedEventArgs e) => FeedbackIssueLink.Open();
    private void OpenFeedbackContacts(object? sender, RoutedEventArgs e) => FeedbackContactsWindow.ShowFor(this);
    private async void DownloadTeacherUpdate(object? sender, RoutedEventArgs e)
    {
        if (await _model.DownloadTeacherUpdateAsync()) Close();
    }
    private async void ExportOfflineTeacherUpdate(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择离线更新包导出目录（例如已授权的加密 U 盘）",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            using var folder = folders[0];
            var path = folder.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _model.ReportOfflineTeacherUpdateError("所选目录没有可访问的本地路径；没有导出文件。");
                return;
            }
            await _model.ExportOfflineTeacherUpdateAsync(path);
        }
        catch (Exception exception)
        {
            _model.ReportOfflineTeacherUpdateError("无法导出离线更新包；没有启动安装。" + exception.Message);
        }
    }
    private async void VerifyOfflineTeacherUpdate(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择离线教师安装器（需与 .release.json 清单放在同一文件夹）",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Veyon Campus 教师安装器")
                    {
                        Patterns = ["VeyonCampus-Teacher-Setup-*-win-x64.exe"]
                    }
                ]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _model.ReportOfflineTeacherUpdateError("无法读取所选文件的本地路径；尚未复制或安装文件。");
                return;
            }
            await _model.VerifyOfflineTeacherUpdateAsync(path);
        }
        catch (Exception exception)
        {
            _model.ReportOfflineTeacherUpdateError("无法选择或暂存离线安装器；没有启动安装。" + exception.Message);
        }
    }
    private void InstallOfflineTeacherUpdate(object? sender, RoutedEventArgs e)
    {
        if (_model.InstallOfflineTeacherUpdate()) Close();
    }
    private async void DeployStudentUpdate(object? sender, RoutedEventArgs e) => await _model.DeployStudentUpdateAsync();
    private async void GeneratePackage(object? sender, RoutedEventArgs e) => await _model.GenerateStudentPackageAsync();
    private void CancelPackageGeneration(object? sender, RoutedEventArgs e) => _model.CancelStudentPackageGeneration();
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
