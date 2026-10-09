using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Security.Cryptography;

namespace VeyonCampus.App;

public partial class TeacherWindow : Window
{
    private readonly TeacherViewModel _model = new();
    private readonly TeacherMobileControlManager _mobileControl;
    private readonly DispatcherTimer _teacherHeartbeatTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly DispatcherTimer _classroomStatusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _isShowingReleaseNotice;

    public TeacherWindow()
    {
        _mobileControl = new TeacherMobileControlManager(() => _model.CampusId,
            action => Dispatcher.UIThread.Post(action));
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
        DataContext = _model;
        _model.ReleaseNoticeAvailable += OnReleaseNoticeAvailable;
        _teacherHeartbeatTimer.Tick += CheckTeacherHeartbeat;
        _classroomStatusTimer.Tick += RefreshClassroomStatus;
        _teacherHeartbeatTimer.Start();
        _classroomStatusTimer.Start();
        Opened += (_, _) =>
        {
            if (_model.HasActiveClassroomSession) _ = _model.RefreshActiveClassroomStatusAsync();
            ShowPendingReleaseNotice();
        };
        Closed += async (_, _) =>
        {
            _teacherHeartbeatTimer.Stop();
            _classroomStatusTimer.Stop();
            _model.ReleaseNoticeAvailable -= OnReleaseNoticeAvailable;
            try { await _mobileControl.DisposeAsync(); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                              System.Net.Sockets.SocketException)
            { /* Shutdown is best-effort after the main window closes. */ }
        };
    }

    private async void CheckTeacherHeartbeat(object? sender, EventArgs e)
    {
        await _model.SendTeacherCampusHeartbeatIfDueAsync();
        ShowPendingReleaseNotice();
    }

    private async void RefreshClassroomStatus(object? sender, EventArgs e) =>
        await _model.RefreshActiveClassroomStatusAsync();

    private async void ToggleClassroomSession(object? sender, RoutedEventArgs e) =>
        await _model.ToggleClassroomSessionAsync();

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
            if (await new TeacherReleaseNoticeWindow(releaseDetails).ShowDialog<bool>(this))
            {
                _model.SelectPage("updates");
                await _model.CheckTeacherUpdateAsync();
            }
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

    private void PolicyCategoryChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true })
        {
            var scroll = this.FindControl<ScrollViewer>("TeacherContentScroll");
            if (scroll is not null) scroll.Offset = new Avalonia.Vector(0, 0);
        }
    }

    private async void PreviewRoom(object? sender, RoutedEventArgs e) => await _model.InspectRoomConflictsAsync();
    private async void AddRoomToVeyon(object? sender, RoutedEventArgs e) => await _model.AddRoomToVeyonAsync();
    private void OpenVeyonConfigurator(object? sender, RoutedEventArgs e) => _model.OpenVeyonConfigurator();
    private async void OpenMobileControl(object? sender, RoutedEventArgs e)
    {
        var window = new TeacherMobileControlWindow(_mobileControl, _model);
        await window.ShowDialog(this);
    }
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
    private async void PushStudentSystemPolicy(object? sender, RoutedEventArgs e) => await _model.PushStudentSystemPolicyAsync();
    private async void DisableStudentSystemPolicy(object? sender, RoutedEventArgs e) => await _model.DisableStudentSystemPolicyAsync();
    private async void ReadApplicationInventory(object? sender, RoutedEventArgs e) => await _model.ReadApplicationInventoryAsync();
    private async void ReadStudentAccounts(object? sender, RoutedEventArgs e) => await _model.ReadStudentAccountsAsync();
    private void AddSelectedApplicationRules(object? sender, RoutedEventArgs e) => _model.AddSelectedApplicationRules();
    private async void InstallTeacherVeyon(object? sender, RoutedEventArgs e) => await _model.InstallTeacherVeyonAsync();
    private async void CheckTeacherUpdate(object? sender, RoutedEventArgs e) => await _model.CheckTeacherUpdateAsync();
    private void OpenFeedbackIssue(object? sender, RoutedEventArgs e) => FeedbackIssueLink.Open();
    private void OpenFeedbackContacts(object? sender, RoutedEventArgs e) => FeedbackContactsWindow.ShowFor(this);
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
        catch (Exception)
        {
            _model.ReportOfflineTeacherUpdateError("无法导出离线更新包；所选位置无法写入。");
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
        catch (Exception)
        {
            _model.ReportOfflineTeacherUpdateError("无法选择或暂存离线安装器；请重新选择完整的可信安装器和清单。");
        }
    }
    private void InstallOfflineTeacherUpdate(object? sender, RoutedEventArgs e)
    {
        if (_model.InstallOfflineTeacherUpdate()) Close();
    }
    private async void TrustStudentAgentIdentities(object? sender, RoutedEventArgs e)
    {
        var discoveries = await _model.DiscoverStudentAgentIdentitiesAsync();
        var candidates = discoveries.Where(item => item.Candidate is not null).ToArray();
        if (candidates.Length == 0) return;
        if (candidates.All(item => item.MatchesPinnedKey))
        {
            _model.ConfirmStudentAgentIdentities(candidates, approveChangedKeys: false);
            return;
        }
        if (!await ConfirmStudentAgentIdentityTrustAsync(discoveries)) return;
        try { _model.ConfirmStudentAgentIdentities(candidates, approveChangedKeys: true); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException)
        { await ShowStudentAgentTrustErrorAsync(exception.Message); }
    }

    private async Task<bool> ConfirmStudentAgentIdentityTrustAsync(
        IReadOnlyList<VeyonCampus.Core.StudentAgentIdentityDiscoveryResult> discoveries)
    {
        var dialog = new Window
        {
            Title = "核对学生 Agent 身份指纹",
            Width = 760,
            Height = 600,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.White
        };
        var layout = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(20) };
        layout.Children.Add(new TextBlock
        {
            Text = "优先将每台电脑的完整指纹与该学生机部署工具显示的指纹逐台比对。若首次信任只能根据校园 LAN 上的签名回执完成，教师明确批准后会固定该密钥；首次网络信任无法识别同网段攻击者替换密钥，后续任何密钥变化都会触发拒绝并要求重新核对。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var rows = new StackPanel { Spacing = 8 };
        foreach (var item in discoveries)
        {
            var candidate = item.Candidate;
            rows.Children.Add(new TextBlock
            {
                Text = candidate is null
                    ? $"{item.Target}：未取得可验证身份 — {item.Detail}"
                    : item.MatchesPinnedKey
                        ? $"{item.Target} · 已固定\n{candidate.Fingerprint}"
                        : candidate.PreviouslyPinnedFingerprint is { } old
                            ? $"{item.Target} · 身份密钥变化，请现场复核后轮换\n原：{old}\n新：{candidate.Fingerprint}"
                            : $"{item.Target} · 首次信任\n{candidate.Fingerprint}",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontFamily = "Consolas"
            });
        }
        layout.Children.Add(new ScrollViewer { Content = rows, Height = 350 });
        var verified = new CheckBox
        {
            Content = "我已核对这些指纹来源，或明确批准对列出的密钥执行首次信任/轮换。",
            IsChecked = false
        };
        layout.Children.Add(verified);
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 90 };
        var confirm = new Button { Content = "固定已核对的身份", IsDefault = true, MinWidth = 150, IsEnabled = false };
        verified.IsCheckedChanged += (_, _) => confirm.IsEnabled = verified.IsChecked == true;
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        layout.Children.Add(actions);
        dialog.Content = layout;
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowStudentAgentTrustErrorAsync(string message)
    {
        var dialog = new Window
        {
            Title = "Agent 身份未固定",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button { Content = "关闭", IsDefault = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                }
            }
        };
        ((Button)((StackPanel)dialog.Content!).Children[1]!).Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
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
