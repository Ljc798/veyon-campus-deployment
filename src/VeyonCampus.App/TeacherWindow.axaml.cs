using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public partial class TeacherWindow : Window
{
    private readonly TeacherViewModel _model = new();
    private readonly TeacherMobileControlManager _mobileControl;
    private readonly DispatcherTimer _teacherHeartbeatTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly DispatcherTimer _classroomStatusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _classroomEventsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private long _classroomEventCursor;
    private Guid? _classroomEventSessionId;
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
        _classroomEventsTimer.Tick += RefreshClassroomEvents;
        _teacherHeartbeatTimer.Start();
        _classroomStatusTimer.Start();
        _classroomEventsTimer.Start();
        Opened += async (_, _) =>
        {
            if (_model.HasActiveClassroomSession)
            {
                await _model.RefreshActiveClassroomStatusAsync();
                await SyncClassroomEventChannelAsync();
                RefreshClassroomEvents(this, EventArgs.Empty);
            }
            ShowPendingReleaseNotice();
        };
        Closed += async (_, _) =>
        {
            _teacherHeartbeatTimer.Stop();
            _classroomStatusTimer.Stop();
            _classroomEventsTimer.Stop();
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

    private async void RefreshClassroomStatus(object? sender, EventArgs e)
    {
        await _model.RefreshActiveClassroomStatusAsync();
        await SyncClassroomEventChannelAsync();
    }

    [SupportedOSPlatform("windows")]
    private async void ToggleClassroomSession(object? sender, RoutedEventArgs e)
    {
        if (_model.HasActiveClassroomSession)
        {
            try
            {
                var restore = await _mobileControl.ApplyClassroomModeAsync(ClassroomMode.Normal);
                _model.SetClassroomModeStatus(FormatClassroomModeStatus(restore));
                await _model.RefreshActiveClassroomStatusAsync();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or
                                              CryptographicException or PlatformNotSupportedException or SocketException)
            {
                _model.SetClassroomModeStatus("下课前恢复检查未完成；未完成项仍保留在教师电脑。" + exception.Message);
            }
        }
        await _model.ToggleClassroomSessionAsync();
        await SyncClassroomEventChannelAsync();
    }

    [SupportedOSPlatform("windows")]
    private async void ToggleClassroomMode(object? sender, RoutedEventArgs e)
    {
        var nextMode = _model.CurrentClassroomMode == ClassroomMode.Practice
            ? ClassroomMode.Normal
            : ClassroomMode.Practice;
        await ApplyClassroomModeAsync(nextMode);
    }

    [SupportedOSPlatform("windows")]
    private async Task ApplyClassroomModeAsync(ClassroomMode mode, string? reviewToken = null)
    {
        try
        {
            var result = await _mobileControl.ApplyClassroomModeAsync(mode, reviewToken);
            if (result.RequiresReview && !string.IsNullOrWhiteSpace(result.ReviewToken))
            {
                var approved = await new ClassroomApplicationReviewWindow(result.ApplicationReview ?? [])
                    .ShowDialog<bool>(this);
                if (approved)
                    result = await _mobileControl.ApplyClassroomModeAsync(mode, result.ReviewToken);
                else
                {
                    result = await _mobileControl.ApplyClassroomModeAsync(ClassroomMode.Normal);
                    result = result with { Message = "已取消启用应用阻止；课堂恢复正常。" };
                }
            }
            _model.SetClassroomModeStatus(FormatClassroomModeStatus(result));
            await _model.RefreshActiveClassroomStatusAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or SocketException)
        {
            _model.SetClassroomModeStatus("课堂模式没有完成更改：" + exception.Message);
        }
    }

    private static string FormatClassroomModeStatus(MobilePolicyOperationResponse response)
    {
        var lines = response.Results.Select(result =>
            $"{result.Target}：{(result.NeedsReview ? "待核对" : result.AgentAccepted ? "已确认" : "未执行")} · {result.Detail}");
        return response.Results.Count == 0
            ? response.Message
            : response.Message + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private async Task SyncClassroomEventChannelAsync()
    {
        try
        {
            var context = _model.GetActiveClassroomEventContext();
            await _mobileControl.SyncClassroomSessionAsync(context);
            _model.SetClassroomEventStatus(_mobileControl.ClassroomEventStatus);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or CryptographicException or
                                          PlatformNotSupportedException or SocketException)
        {
            _model.SetClassroomEventStatus("课堂求助通道暂不可用；将在下次课堂刷新时重试。");
        }
    }

    private void RefreshClassroomEvents(object? sender, EventArgs e)
    {
        var result = _mobileControl.ReadClassroomEvents(_classroomEventCursor);
        if (result is null)
        {
            _classroomEventSessionId = null;
            _classroomEventCursor = 0;
            _model.ResetClassroomEventFeed(null);
            return;
        }

        if (_classroomEventSessionId != result.Value.SessionId)
        {
            _classroomEventSessionId = result.Value.SessionId;
            _classroomEventCursor = 0;
            _model.ResetClassroomEventFeed(_classroomEventSessionId);
            result = _mobileControl.ReadClassroomEvents(0);
            if (result is null) return;
        }

        _model.ApplyClassroomEvents(result.Value.SessionId, result.Value.Page.Events);
        _classroomEventCursor = result.Value.Page.Cursor;
    }

    private void ReplyToClassroomEvent(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not TeacherClassroomEventItem item || !item.CanReply) return;
        try
        {
            var result = _mobileControl.ReplyToClassroomEvent(item.EventId, item.ReplyMessage);
            if (!result.Duplicate) _model.MarkClassroomEventReply(item.EventId, item.ReplyMessage.Trim());
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        {
            _model.SetClassroomEventReplyError(item.EventId, exception.Message);
        }
    }

    private void SendClassroomNotice(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanSendClassroomNotice) return;
        try
        {
            var response = _mobileControl.SendClassroomNotice(_model.ClassroomNoticeDraft.Trim());
            _model.ClassroomNoticeDraft = "";
            _model.SetClassroomNoticeStatus(
                $"已提交至本堂课目标（{response.TargetCount} 台）；在线学生端会显示。学生未读状态不会回传。");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or
                                          TeacherMobileControlService.MobileAuthorizationException or
                                          TeacherMobileControlService.MobileRateLimitException)
        {
            _model.SetClassroomNoticeStatus(exception.Message);
        }
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
