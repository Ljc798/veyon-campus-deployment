using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public partial class TeacherWindow : Window
{
    private readonly TeacherViewModel _model = new();
    private readonly TeacherMobileControlManager _mobileControl;
    private readonly DispatcherTimer _teacherHeartbeatTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly DispatcherTimer _classroomStatusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _classroomEventsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _classroomCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _classroomEventCursor;
    private Guid? _classroomEventSessionId;
    private bool _isShowingReleaseNotice;
    private bool _trayBackgroundModeEnabled;
    private bool _allowApplicationExit;
    private bool _isSeatCellPaintGestureActive;
    private bool _seatCellPaintTargetIsSeat;
    private bool _seatCellPaintSuppressNextClick;
    private readonly HashSet<(int Row, int Column)> _seatCellPaintVisited = [];

    public TeacherWindow()
    {
        _mobileControl = new TeacherMobileControlManager(() => _model.CampusId,
            action => Dispatcher.UIThread.Post(action), SetClassroomSessionFromMobileAsync);
        InitializeComponent();
        PointerReleased += (_, _) => EndSeatCellPaintGesture();
        Closing += (_, e) =>
        {
            if (!_trayBackgroundModeEnabled || _allowApplicationExit) return;
            e.Cancel = true;
            Hide();
        };
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
        DataContext = _model;
        _model.ReleaseNoticeAvailable += OnReleaseNoticeAvailable;
        _teacherHeartbeatTimer.Tick += RunHourlyBackgroundChecks;
        _classroomStatusTimer.Tick += RefreshClassroomStatus;
        _classroomEventsTimer.Tick += RefreshClassroomEvents;
        _classroomCountdownTimer.Tick += (_, _) =>
        {
            _model.RefreshClassroomCountdownDisplay();
            if (!_model.IsClassroomCountdownRunning) _classroomCountdownTimer.Stop();
        };
        _teacherHeartbeatTimer.Start();
        _classroomStatusTimer.Start();
        _classroomEventsTimer.Start();
        Opened += async (_, _) =>
        {
            FitWindowToWorkingArea();
            await _model.RefreshTeacherSetupVeyonStatusAsync();
            if (_model.IsClassroomExperiencePage)
                await _model.ReadClassroomLocationsAsync();
            else if (_model.IsClassroomPage && string.IsNullOrWhiteSpace(_model.WebsiteTargets))
                await _model.ReadWebsiteLocationsAsync();
            if (_model.IsClassroomCountdownRunning) _classroomCountdownTimer.Start();
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
            _classroomCountdownTimer.Stop();
            _model.ReleaseNoticeAvailable -= OnReleaseNoticeAvailable;
            try { await _mobileControl.DisposeAsync(); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                              System.Net.Sockets.SocketException)
            { /* Shutdown is best-effort after the main window closes. */ }
        };
    }

    public void EnableTrayBackgroundMode() => _trayBackgroundModeEnabled = true;

    public void AllowApplicationExit() => _allowApplicationExit = true;

    private void FitWindowToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen is null) return;

        var workArea = screen.WorkingArea;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        var availableWidth = Math.Max(1d, workArea.Width / scaling - 24d);
        var availableHeight = Math.Max(1d, workArea.Height / scaling - 64d);
        var needsFit = Width > availableWidth || Height > availableHeight;
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        MinWidth = Math.Min(1024d, availableWidth);
        MinHeight = Math.Min(700d, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);

        var pixelWidth = (int)Math.Round(Width * scaling);
        var pixelHeight = (int)Math.Round(Height * scaling) + 32;
        var margin = needsFit ? (int)Math.Round(12d * scaling) : 0;
        Position = new PixelPoint(
            workArea.X + (needsFit ? margin : Math.Max(0, (workArea.Width - pixelWidth) / 2)),
            workArea.Y + (needsFit ? margin : Math.Max(0, (workArea.Height - pixelHeight) / 2)));
    }

    private async void RunHourlyBackgroundChecks(object? sender, EventArgs e)
    {
        await _model.RunHourlyBackgroundChecksAsync();
        ShowPendingReleaseNotice();
    }

    private async void RefreshClassroomStatus(object? sender, EventArgs e)
    {
        await _model.RefreshActiveClassroomStatusAsync();
        await SyncClassroomEventChannelAsync();
    }

    [SupportedOSPlatform("windows")]
    private async void ToggleClassroomSession(object? sender, RoutedEventArgs e)
        => await ToggleClassroomSessionFromUiAsync();

    [SupportedOSPlatform("windows")]
    private async Task ToggleClassroomSessionFromUiAsync()
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

    private Task<MobileClassroomSessionActionResponse> SetClassroomSessionFromMobileAsync(bool desiredActive,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("手机课堂控制只支持 Windows 教师端。");
        var completion = new TaskCompletionSource<MobileClassroomSessionActionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("手机课堂控制只支持 Windows 教师端。");
                if (_model.HasActiveClassroomSession != desiredActive)
                {
                    if (desiredActive && !_model.CanToggleClassroomSession)
                        await _model.ReadClassroomLocationsAsync();
                    if (!_model.CanToggleClassroomSession)
                    {
                        var detail = _model.HasMultipleClassroomLocations
                            ? "Veyon 中配置了多个机房；请先在教师电脑选择本次上课机房。"
                            : !string.IsNullOrWhiteSpace(_model.ClassroomDirectoryError)
                            ? _model.ClassroomDirectoryError
                            : !string.IsNullOrWhiteSpace(_model.CampusDirectoryError)
                                ? _model.CampusDirectoryError
                                : "请先在教师电脑选择校区和机房，并完成学生配置。";
                        throw new InvalidOperationException(detail);
                    }
                    await ToggleClassroomSessionFromUiAsync();
                }
                else
                {
                    await SyncClassroomEventChannelAsync();
                }

                if (_model.HasActiveClassroomSession != desiredActive)
                    throw new InvalidOperationException(_model.ClassroomDeliveryStatus.Length > 0
                        ? _model.ClassroomDeliveryStatus
                        : "课堂状态没有按预期更新；请检查教师电脑上的机房和学生配置。");

                var context = _model.GetActiveClassroomEventContext();
                if (desiredActive && context is null)
                    throw new InvalidOperationException("课堂已开启，但无法确认课堂电脑范围；请检查教师电脑上的 Veyon 机房名单。");
                completion.TrySetResult(new MobileClassroomSessionActionResponse(desiredActive,
                    _model.ClassroomDeliveryStatus,
                    context?.Targets ?? Array.Empty<string>(), context?.SessionId));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or CryptographicException or
                                              PlatformNotSupportedException or SocketException or ArgumentException)
            {
                completion.TrySetException(new InvalidOperationException(
                    "课堂操作没有完成，手机控制状态未确认。" + exception.Message, exception));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, DispatcherPriority.Normal);
        return completion.Task.WaitAsync(cancellationToken);
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

    [SupportedOSPlatform("windows")]
    private async void RetryClassroomRestores(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanRetryClassroomRestores) return;
        _model.SetClassroomRestoreRetrying(true);
        _model.RefreshClassroomRestoreLedger("正在读取机房目录并重新核对 Agent 签名状态……");
        try
        {
            var response = await _mobileControl.RetryClassroomRestoresAsync();
            var details = response.Results.Take(24).Select(result =>
                $"{result.Target}：{(result.NeedsReview ? "待处理" : result.AgentAccepted ? "已确认" : "未执行")} · {result.Detail}")
                .ToArray();
            var status = response.Results.Count > details.Length
                ? response.Message + Environment.NewLine + string.Join(Environment.NewLine, details) +
                  Environment.NewLine + $"另有 {response.Results.Count - details.Length} 项结果。"
                : details.Length == 0 ? response.Message : response.Message + Environment.NewLine +
                    string.Join(Environment.NewLine, details);
            _model.RefreshClassroomRestoreLedger(status);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or SocketException)
        {
            _model.RefreshClassroomRestoreLedger("待恢复项没有全部处理；记录仍保留，可稍后重试。" + exception.Message);
        }
        finally
        {
            _model.SetClassroomRestoreRetrying(false);
            _model.RefreshClassroomRestoreLedger();
        }
    }

    private async Task SyncClassroomEventChannelAsync()
    {
        try
        {
            var context = _model.GetActiveClassroomEventContext();
            await _mobileControl.SyncClassroomSessionAsync(context);
            _model.SetClassroomEventStatus(_mobileControl.ClassroomEventStatus);
            _model.SyncClassroomChatTargets(context?.SessionId, _mobileControl.ReadClassroomChatTargets());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or CryptographicException or
                                          PlatformNotSupportedException or SocketException)
        {
            _model.SetClassroomEventStatus("课堂消息暂不可用；稍后会重试。");
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
            _model.SyncClassroomChatTargets(null, Array.Empty<string>());
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

        _model.SyncClassroomChatTargets(result.Value.SessionId, _mobileControl.ReadClassroomChatTargets());
        _model.ApplyClassroomEvents(result.Value.SessionId, result.Value.Page.Events);
        if (result.Value.Page.Events.Any(item =>
                item.Type == ClassroomEventType.ClassroomChatMessage &&
                string.Equals(item.Target, _model.SelectedClassroomChatThread?.Target,
                    StringComparison.OrdinalIgnoreCase)))
            Dispatcher.UIThread.Post(ScrollTeacherChatToLatest);
        _classroomEventCursor = result.Value.Page.Cursor;
    }

    private void ClassroomChatThreadSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(ScrollTeacherChatToLatest);

    private void ScrollTeacherChatToLatest() =>
        TeacherChatHistoryScroller.Offset = new Vector(0, TeacherChatHistoryScroller.Extent.Height);

    private void SendClassroomChatMessage(object? sender, RoutedEventArgs e)
    {
        SendCurrentClassroomChatMessage();
    }

    private void ClassroomChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        SendCurrentClassroomChatMessage();
    }

    private void SendCurrentClassroomChatMessage()
    {
        if (!_model.CanSendClassroomChat || _model.SelectedClassroomChatThread is not { } thread) return;
        try
        {
            var result = _mobileControl.SendClassroomChatMessage(thread.Target, _model.ClassroomChatDraft);
            if (!result.Accepted)
                throw new InvalidOperationException("教师端没有接收这条消息；请重试。");
            _model.ClassroomChatDraft = "";
            _model.SetClassroomChatSendStatus(result.Duplicate
                ? "消息已提交（重复请求已去重）；等待学生端接收。"
                : "消息已提交给教师端；正在等待学生端接收。学生端未确认前不会标记为已送达。");
            RefreshClassroomEvents(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or
                                          TeacherMobileControlService.MobileAuthorizationException or
                                          TeacherMobileControlService.MobileRateLimitException)
        {
            _model.SetClassroomChatSendStatus(exception.Message);
        }
    }

    private void RetryClassroomChatMessage(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not TeacherClassroomChatMessageItem item || !item.CanRetry) return;
        try
        {
            var result = _mobileControl.SendClassroomChatMessage(item.Target, item.Message, item.EventId);
            if (!result.Accepted)
                throw new InvalidOperationException("教师端没有接收这条消息；请重试。");
            _model.MarkClassroomChatRetrySubmitted(item.EventId, result.ExpiresUtc);
            _model.SetClassroomChatSendStatus(result.Duplicate
                ? "消息仍在有效投递队列中，正在等待学生端确认。"
                : "已重新发送未确认的消息；等待学生端确认。");
            RefreshClassroomEvents(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or
                                          TeacherMobileControlService.MobileAuthorizationException or
                                          TeacherMobileControlService.MobileRateLimitException)
        {
            _model.SetClassroomChatSendStatus(exception.Message);
        }
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
                $"已发送到本堂课目标（{response.TargetCount} 台）。学生端收到后会弹出课堂助手；通知两分钟后失效。当前不回传已读状态。");
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

    private async void AddRoomToVeyon(object? sender, RoutedEventArgs e) => await _model.AddRoomToVeyonAsync();
    private void TeacherSetupStep1(object? sender, RoutedEventArgs e) => _model.SetTeacherSetupWizardStep(1);
    private void TeacherSetupStep2(object? sender, RoutedEventArgs e) => _model.SetTeacherSetupWizardStep(2);
    private void TeacherSetupStep3(object? sender, RoutedEventArgs e) => _model.SetTeacherSetupWizardStep(3);
    private void TeacherSetupNext(object? sender, RoutedEventArgs e) => _model.NextTeacherSetupWizardStep();
    private void SkipTeacherSetupRoom(object? sender, RoutedEventArgs e) => _model.SkipTeacherSetupRoomForNow();
    private async void RefreshTeacherSetupVeyon(object? sender, RoutedEventArgs e) =>
        await _model.RefreshTeacherSetupVeyonStatusAsync();
    private void OpenRoomSetupFromTeacherWizard(object? sender, RoutedEventArgs e) =>
        _model.OpenRoomSetupFromTeacherWizard();
    private async void OpenMobileControl(object? sender, RoutedEventArgs e)
    {
        var window = new TeacherMobileControlWindow(_mobileControl, _model);
        await window.ShowDialog(this);
    }
    private void NewCampusProfile(object? sender, RoutedEventArgs e) => _model.NewCampusProfile();
    private void SaveCampusProfile(object? sender, RoutedEventArgs e) => _model.SaveCampusProfile();
    private async void DeleteCampusProfile(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmProfileActionAsync("删除校区档案",
                "将从本机删除所选校区档案及其机房档案。此操作不会修改 Veyon；删除后不能从本机恢复。", "确认删除"))
            _model.DeleteSelectedCampusProfile();
    }
    private void NewRoomProfile(object? sender, RoutedEventArgs e) => _model.NewRoomProfile();
    private void SaveRoomProfile(object? sender, RoutedEventArgs e) => _model.SaveRoomProfile();
    private void PrepareRoomSeatGrid(object? sender, RoutedEventArgs e) => _model.PrepareCurrentRoomSeatGrid();
    private void OpenVeyonConfigurator(object? sender, RoutedEventArgs e) => _model.OpenVeyonConfigurator();
    private void ToggleSeatMapEditing(object? sender, RoutedEventArgs e) => _model.ToggleSeatMapEditing();
    private void ToggleSeatAddRemoveMode(object? sender, RoutedEventArgs e) => _model.ToggleSeatAddRemoveMode();
    private async void ApplySeatGridDimensions(object? sender, RoutedEventArgs e)
    {
        var removeOutside = _model.HasSeatsOutsidePendingGrid;
        if (removeOutside && !await ConfirmProfileActionAsync("确认缩小座位图",
                $"新范围是 {(int)_model.SeatRowsDraft} 行 × {(int)_model.SeatColumnsDraft} 列。范围外有桌位及学生姓名，确认删除这些本机记录吗？Veyon 地点和电脑目录不会被更改。",
                "删除范围外座位")) return;
        _model.ApplySeatGridDimensions(removeOutside);
    }
    private async void PrintSeatChart(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
        {
            _model.ReportSeatChartExportStatus("当前环境不支持保存座位表文件。");
            return;
        }
        string? savedPath = null;
        try
        {
            var safeName = string.Concat(_model.SeatGridTitle.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出可打印机房座位表",
                SuggestedFileName = $"{safeName}-座位表.html",
                DefaultExtension = "html",
                FileTypeChoices = [new FilePickerFileType("HTML 座位表") { Patterns = ["*.html"] }],
                ShowOverwritePrompt = true
            });
            if (file is null) return;
            using (file)
            {
                var path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请选择本机文件夹保存座位表。");
                File.WriteAllText(path, _model.BuildPrintableSeatChartHtml(), new UTF8Encoding(false));
                savedPath = path;
            }
            try
            {
                var pathToOpen = savedPath ?? throw new InvalidOperationException("座位表尚未保存。");
                Process.Start(new ProcessStartInfo(pathToOpen) { UseShellExecute = true });
                _model.ReportSeatChartExportStatus("座位表已导出并在浏览器中打开；可点击页面上的“打印 / 另存为 PDF”。");
            }
            catch (Exception exception)
            {
                _model.ReportSeatChartExportStatus($"座位表已保存到 {savedPath}，但未能自动打开浏览器：{exception.Message}");
            }
        }
        catch (Exception exception)
        {
            _model.ReportSeatChartExportStatus("无法导出座位表：" + exception.Message);
        }
    }
    private async void SwapSelectedSeats(object? sender, RoutedEventArgs e)
    {
        await _model.ReadSeatVeyonTargetsAsync();
        await _model.PrepareSeatSwapPlanAsync();
        if (!_model.CanExecuteSeatSwapPlan ||
            !await ConfirmSeatSwapAsync(_model.SeatSwapPlanRows)) return;
        await _model.ExecuteSeatSwapPlanAsync();
    }
    private void StartClassroomCountdown(object? sender, RoutedEventArgs e)
    {
        _model.StartClassroomCountdown();
        if (_model.IsClassroomCountdownRunning) _classroomCountdownTimer.Start();
    }
    private void EndClassroomCountdown(object? sender, RoutedEventArgs e)
    {
        _model.EndClassroomCountdown();
        _classroomCountdownTimer.Stop();
    }
    private void AddClassroomTask(object? sender, RoutedEventArgs e) => _model.AddClassroomTask();
    private void ToggleClassroomTask(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TeacherClassroomTaskItem item })
            _model.ToggleClassroomTask(item.TaskId);
    }
    private void RemoveClassroomTask(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TeacherClassroomTaskItem item })
            _model.RemoveClassroomTask(item.TaskId);
    }
    private async void SeatCellClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TeacherSeatCell cell }) return;
        if (_seatCellPaintSuppressNextClick)
        {
            _seatCellPaintSuppressNextClick = false;
            return;
        }
        if (!cell.CanEdit) return;
        if (_model.SeatSwapMode)
        {
            _model.SelectSeatForSwap(cell.Row, cell.Column);
            return;
        }
        if (_model.IsSeatAddRemoveMode)
        {
            _model.ToggleSeatCell(cell.Row, cell.Column);
            return;
        }
        if (!cell.IsSeat) return;
        var studentName = await EditSeatCellAsync(cell);
        if (studentName is not null) _model.SaveSeatCell(cell.Row, cell.Column, studentName);
    }

    private void SeatCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button { DataContext: TeacherSeatCell cell } button ||
            e.Pointer.Type != PointerType.Mouse ||
            !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed ||
            !_model.IsSeatAddRemoveMode || !_model.CanEditSeatMap || !cell.CanEdit) return;

        e.Handled = true;
        e.Pointer.Capture(null);
        _isSeatCellPaintGestureActive = true;
        _seatCellPaintSuppressNextClick = true;
        _seatCellPaintVisited.Clear();
        _seatCellPaintTargetIsSeat = !cell.IsSeat;
        PaintSeatCell(cell);
    }

    private void SeatCellPointerEntered(object? sender, PointerEventArgs e)
    {
        if (!_isSeatCellPaintGestureActive || sender is not Button { DataContext: TeacherSeatCell cell } button)
            return;
        if (e.Pointer.Type != PointerType.Mouse ||
            !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            EndSeatCellPaintGesture();
            return;
        }
        PaintSeatCell(cell);
    }

    private void PaintSeatCell(TeacherSeatCell cell)
    {
        if (!cell.CanEdit || !_seatCellPaintVisited.Add((cell.Row, cell.Column))) return;
        if (cell.IsSeat != _seatCellPaintTargetIsSeat)
            _model.ToggleSeatCell(cell.Row, cell.Column);
    }

    private void EndSeatCellPaintGesture()
    {
        _isSeatCellPaintGestureActive = false;
        _seatCellPaintSuppressNextClick = false;
        _seatCellPaintVisited.Clear();
    }

    private async Task<string?> EditSeatCellAsync(TeacherSeatCell cell)
    {
        var dialog = new Window
        {
            Title = "编辑桌位姓名",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.White
        };
        var layout = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(24) };
        layout.Children.Add(new TextBlock
        {
            Text = $"桌位 {cell.SeatNumber} · 第 {cell.Row} 行 · 第 {cell.Column} 列",
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        var nameBox = new TextBox
        {
            Text = cell.StudentName,
            MaxLength = 100,
            Watermark = "学生姓名（留空只保留桌位）"
        };
        layout.Children.Add(new TextBlock { Text = "学生姓名" });
        layout.Children.Add(nameBox);
        layout.Children.Add(new TextBlock
        {
            Text = "填写姓名后会为该桌位生成学生电脑；姓名留空时只保留桌位。",
            Classes = { "muted" },
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 90 };
        cancel.Classes.Add("secondary");
        cancel.Click += (_, _) => dialog.Close(null);
        var save = new Button
        {
            Content = "保存姓名",
            IsDefault = true,
            MinWidth = 100
        };
        save.Classes.Add("primary");
        save.Click += (_, _) => dialog.Close(nameBox.Text ?? "");
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        layout.Children.Add(actions);
        dialog.Content = layout;
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task<bool> ConfirmSeatSwapAsync(IReadOnlyList<string> planRows)
    {
        var dialog = new Window
        {
            Title = "确认交换座位",
            Width = 660,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.White
        };
        var layout = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(24) };
        layout.Children.Add(new TextBlock
        {
            Text = "将交换这两名学生的座位，并同步更新 Veyon 中的电脑显示名；电脑连接地址保持不变。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        layout.Children.Add(new ListBox
        {
            ItemsSource = planRows,
            MinHeight = Math.Min(160, 48 + planRows.Count * 32),
            MaxHeight = 240
        });
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 90 };
        cancel.Classes.Add("secondary");
        var confirm = new Button { Content = "确认交换", IsDefault = true, MinWidth = 120 };
        confirm.Classes.Add("primary");
        confirm.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        layout.Children.Add(actions);
        dialog.Content = layout;
        return await dialog.ShowDialog<bool>(this);
    }

    private async void DeleteRoomProfile(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmProfileActionAsync("删除机房档案",
                "将从教师电脑删除所选机房档案、学生姓名和座位图。此操作不会删除或修改 Veyon 中的地点与电脑目录；删除后不能从本机恢复。", "删除本机机房"))
            _model.DeleteSelectedRoomProfile();
    }
    private void LoadRoomProfile(object? sender, RoutedEventArgs e) => _model.LoadSelectedRoomProfileIntoBuilder();

    private async Task<bool> ConfirmProfileActionAsync(string title, string message, string confirmText)
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
        cancel.Classes.Add("secondary");
        var confirm = new Button { Content = confirmText, MinWidth = 100 };
        confirm.Classes.Add("danger");
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
    private void SelectAllWebsitePolicyTargets(object? sender, RoutedEventArgs e) =>
        _model.SelectAllWebsitePolicyTargets(selected: true);
    private void ClearWebsitePolicyTargets(object? sender, RoutedEventArgs e) =>
        _model.SelectAllWebsitePolicyTargets(selected: false);
    private async void ReadClassroomLocations(object? sender, RoutedEventArgs e) => await _model.ReadClassroomLocationsAsync();
    private void UseSelectedClassroomLocation(object? sender, RoutedEventArgs e) => _model.UseSelectedClassroomLocation();
    private async void SaveCurrentWebsiteApplicationProfile(object? sender, RoutedEventArgs e) =>
        await _model.SaveCurrentWebsiteApplicationProfileAsync(message =>
            ConfirmProfileActionAsync("替换已有组合策略预设", message, "确认替换"));
    private async void PushWebsitePolicy(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmProfileActionAsync("确认推送网站限制",
                _model.WebsitePolicyConfirmationSummary, "确认推送")) return;
        await _model.PushWebsitePolicyAsync();
    }
    private async void DisableWebsitePolicy(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmProfileActionAsync("确认解除网站限制",
                _model.WebsitePolicyDisableConfirmationSummary, "确认解除")) return;
        await _model.DisableWebsitePolicyAsync();
    }
    private async void ReadWebsitePolicyStatus(object? sender, RoutedEventArgs e) => await _model.ReadWebsitePolicyStatusAsync();
    private async void MigrateEdgeRealtime(object? sender, RoutedEventArgs e) =>
        await _model.MigrateEdgeRealtimeAsync(message =>
            ConfirmProfileActionAsync("启用 Edge 实时同步", message, "确认启用"));
    private async void RestoreEdgeLegacy(object? sender, RoutedEventArgs e) =>
        await _model.RestoreEdgeLegacyAsync(message =>
            ConfirmProfileActionAsync("恢复 Edge 注册表策略", message, "确认恢复"));
    private void ImportCommonWebsiteBlacklist(object? sender, RoutedEventArgs e) => _model.ImportCommonWebsiteBlacklist();
    private void ImportCommonWebsiteAllowlist(object? sender, RoutedEventArgs e) => _model.ImportCommonWebsiteAllowlist();
    private void ImportCommonCombinedWebsitePolicy(object? sender, RoutedEventArgs e) => _model.ImportCommonCombinedWebsitePolicy();
    private void AddObservedWebsiteToBlacklist(object? sender, RoutedEventArgs e) => _model.AddObservedWebsiteToBlacklist();
    private void AddObservedWebsiteToAllowlist(object? sender, RoutedEventArgs e) => _model.AddObservedWebsiteToAllowlist();
    private void FillFailedWebsiteTargets(object? sender, RoutedEventArgs e) => _model.FillFailedWebsiteTargets();
    private async void PushApplicationPolicy(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmProfileActionAsync("确认推送应用策略",
                _model.WebsitePolicyDisableConfirmationSummary, "确认推送")) return;
        await _model.PushApplicationPolicyAsync();
    }
    private async void DisableApplicationPolicy(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmProfileActionAsync("确认解除应用限制",
                _model.WebsitePolicyDisableConfirmationSummary, "确认解除")) return;
        await _model.DisableApplicationPolicyAsync();
    }
    private async void ReadApplicationPolicyAudit(object? sender, RoutedEventArgs e) => await _model.ReadApplicationPolicyAuditAsync();
    private async void PushStudentSystemPolicy(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmProfileActionAsync("确认推送学生电脑设置",
                _model.WebsitePolicyDisableConfirmationSummary, "确认推送")) return;
        await _model.PushStudentSystemPolicyAsync();
    }
    private async void DisableStudentSystemPolicy(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmProfileActionAsync("确认解除学生电脑设置",
                _model.WebsitePolicyDisableConfirmationSummary, "确认解除")) return;
        await _model.DisableStudentSystemPolicyAsync();
    }
    private async void ReadApplicationInventory(object? sender, RoutedEventArgs e) => await _model.ReadApplicationInventoryAsync();
    private async void ReadStudentAccounts(object? sender, RoutedEventArgs e)
    {
        if (await TrustStudentAgentIdentitiesAsync())
            await _model.ReadStudentAccountsAsync();
    }
    private void SelectAllStudentAccounts(object? sender, RoutedEventArgs e) =>
        _model.SetStudentAccountSelection(selected: true);
    private void ClearStudentAccountSelection(object? sender, RoutedEventArgs e) =>
        _model.SetStudentAccountSelection(selected: false);
    private void AddSelectedApplicationRules(object? sender, RoutedEventArgs e) => _model.AddSelectedApplicationRules();
    private async void InstallTeacherVeyon(object? sender, RoutedEventArgs e) => await _model.InstallTeacherVeyonAsync();
    private async void CheckTeacherUpdate(object? sender, RoutedEventArgs e) => await _model.CheckTeacherUpdateAsync();
    private void OpenFeedbackContacts(object? sender, RoutedEventArgs e) => FeedbackContactsWindow.ShowFor(this);
    private void OpenFeedbackIssue(object? sender, RoutedEventArgs e) => FeedbackIssueLink.Open();
    private async void ClearAllConfiguration(object? sender, RoutedEventArgs e)
    {
        if (_model.IsExecuting)
        {
            await new ConfigurationClearConfirmationWindow("暂时无法清空配置",
                "当前有操作正在执行。请等操作结束后再清空配置。", confirmation: false).ShowDialog<bool>(this);
            return;
        }
        var confirmation = new ConfigurationClearConfirmationWindow("TeacherConsole");
        if (await confirmation.ShowDialog<bool>(this) != true) return;
        try
        {
            ConfigurationClearHandoff.Start("TeacherConsole");
            AllowApplicationExit();
            Close();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                          InvalidOperationException or ArgumentException)
        {
            await new ConfigurationClearConfirmationWindow("未启动配置清理",
                "没有执行清理。" + Environment.NewLine + exception.Message, confirmation: false)
                .ShowDialog<bool>(this);
        }
    }
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
        if (await _model.DownloadTeacherUpdateAsync()) ExitForUpdate();
    }

    private void ExitForUpdate()
    {
        if (Application.Current is App app)
        {
            app.ExitTeacherConsole();
            return;
        }
        AllowApplicationExit();
        Close();
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
        if (_model.InstallOfflineTeacherUpdate()) ExitForUpdate();
    }
    private async void TrustStudentAgentIdentities(object? sender, RoutedEventArgs e) =>
        await TrustStudentAgentIdentitiesAsync();

    private async Task<bool> TrustStudentAgentIdentitiesAsync()
    {
        var discoveries = await _model.DiscoverStudentAgentIdentitiesAsync();
        var candidates = discoveries.Where(item => item.Candidate is not null).ToArray();
        if (candidates.Length == 0) return false;
        if (candidates.All(item => item.MatchesPinnedKey))
        {
            _model.ConfirmStudentAgentIdentities(discoveries, approveChangedKeys: false);
            return true;
        }
        if (!await ConfirmStudentAgentIdentityTrustAsync(discoveries)) return false;
        try
        {
            _model.ConfirmStudentAgentIdentities(discoveries, approveChangedKeys: true);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException)
        {
            await ShowStudentAgentTrustErrorAsync(exception.Message);
            return false;
        }
    }

    private async Task<bool> ConfirmStudentAgentIdentityTrustAsync(
        IReadOnlyList<VeyonCampus.Core.StudentAgentIdentityDiscoveryResult> discoveries)
    {
        var dialog = new Window
        {
            Title = "确认学生电脑",
            Width = 720,
            Height = 520,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.White
        };
        var layout = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(20) };
        layout.Children.Add(new TextBlock
        {
            Text = "请把学生电脑显示的指纹与下方内容对上。提示只会在教师端首次信任或检测到指纹变化时出现；固定记录保存在当前 Windows 用户配置中，因此教师机和虚拟机可能显示不同。若逐台结果提示连接超时，Agent 没有回应，暂时没有身份可供确认；请检查虚拟机中的 Agent、网络和 TCP 39174 防火墙。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var rows = new StackPanel { Spacing = 8 };
        foreach (var item in discoveries)
        {
            var candidate = item.Candidate;
            rows.Children.Add(new TextBlock
            {
                Text = candidate is null
                    ? $"{_model.GetWebsitePolicyStudentName(item.Target)}：未取得可验证身份 — {item.Detail}"
                    : item.MatchesPinnedKey
                        ? $"{_model.GetWebsitePolicyStudentName(item.Target)} · 已固定\n{candidate.Fingerprint}"
                        : candidate.PreviouslyPinnedFingerprint is { } old
                            ? $"{_model.GetWebsitePolicyStudentName(item.Target)} · 身份密钥变化，请现场复核后轮换\n原：{old}\n新：{candidate.Fingerprint}"
                            : $"{_model.GetWebsitePolicyStudentName(item.Target)} · 首次信任\n{candidate.Fingerprint}",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontFamily = "Consolas"
            });
        }
        layout.Children.Add(new ScrollViewer { Content = rows, Height = 220 });
        var verified = new CheckBox
        {
            Content = "我确认这些指纹来自本次要管理的学生电脑。",
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
        cancel.Classes.Add("secondary");
        var confirm = new Button { Content = "固定已核对的身份", IsDefault = true, MinWidth = 150, IsEnabled = false };
        confirm.Classes.Add("primary");
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
