using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public partial class TeacherWindow : Window
{
    private readonly TeacherViewModel _model = new(roomSelectionStore: new TeacherRoomSelectionStore());
    private readonly TeacherMobileControlManager _mobileControl;
    private readonly DispatcherTimer _teacherHeartbeatTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly DispatcherTimer _classroomStatusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _classroomEventsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _classroomCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _classroomQuickControlProgressTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _deviceRuntimeSummaryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private long _classroomEventCursor;
    private Guid? _classroomEventSessionId;
    private bool _isShowingReleaseNotice;
    private bool _trayBackgroundModeEnabled;
    private bool _allowApplicationExit;
    private string _lastClassroomQuickControlSummary = "";
    private readonly SeatGridPaintGesture _seatCellPaintGesture = new();
    private ItemsControl? _seatMapGrid;

    public TeacherWindow()
    {
        _mobileControl = new TeacherMobileControlManager(() => _model.CampusId,
            action => Dispatcher.UIThread.Post(action), SetClassroomSessionFromMobileAsync,
            _model.ApplyMobileWebsitePolicyLists, _model.ReadMobileRuntimeSummariesAsync,
            _model.AddStudentNamesToVeyonLocations);
        InitializeComponent();
        ClassroomChatDraftInput.AddHandler(InputElement.KeyDownEvent, ClassroomChatInputKeyDown,
            RoutingStrategies.Tunnel);
        KeyDown += TeacherWindowKeyDown;
        PointerMoved += SeatMapPointerMoved;
        PointerCaptureLost += (_, _) =>
        {
            if (_seatCellPaintGesture.IsActive) EndSeatCellPaintGesture();
        };
        PointerReleased += (_, e) =>
        {
            if (_seatCellPaintGesture.IsActive) e.Pointer.Capture(null);
            EndSeatCellPaintGesture();
        };
        Closing += (_, e) =>
        {
            if (!_trayBackgroundModeEnabled || _allowApplicationExit) return;
            e.Cancel = true;
            Hide();
            _deviceRuntimeSummaryTimer.Stop();
            SyncClassroomVisualTimers();
        };
        Activated += (_, _) =>
        {
            SyncDeviceRuntimePolling();
            SyncClassroomVisualTimers();
        };
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
        DataContext = _model;
        SyncTeacherPageTemplate();
        _model.ReleaseNoticeAvailable += OnReleaseNoticeAvailable;
        _model.PropertyChanged += TeacherModelPropertyChanged;
        _teacherHeartbeatTimer.Tick += RunHourlyBackgroundChecks;
        _classroomStatusTimer.Tick += RefreshClassroomStatus;
        _classroomEventsTimer.Tick += RefreshClassroomEvents;
        _classroomCountdownTimer.Tick += (_, _) =>
        {
            if (!IsClassroomExperienceVisible)
            {
                SyncClassroomVisualTimers();
                return;
            }
            _model.RefreshClassroomCountdownDisplay();
            if (!_model.IsClassroomCountdownRunning) _classroomCountdownTimer.Stop();
        };
        _classroomQuickControlProgressTimer.Tick += RefreshClassroomQuickControlProgress;
        _deviceRuntimeSummaryTimer.Tick += PollDeviceRuntimeSummaries;
        _teacherHeartbeatTimer.Start();
        _classroomStatusTimer.Start();
        _classroomEventsTimer.Start();
        Opened += async (_, _) =>
        {
            FitWindowToWorkingArea();
            SyncDeviceRuntimePolling();
            SyncClassroomVisualTimers();
            await _mobileControl.RestoreStartupStateAsync();
            await _model.RefreshTeacherSetupVeyonStatusAsync();
            if (_model.IsClassroomExperiencePage)
                await _model.ReadClassroomLocationsAsync();
            else if (_model.IsClassroomPage && string.IsNullOrWhiteSpace(_model.WebsiteTargets))
                await _model.ReadWebsiteLocationsAsync();
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
            _classroomQuickControlProgressTimer.Stop();
            _deviceRuntimeSummaryTimer.Stop();
            _model.ReleaseNoticeAvailable -= OnReleaseNoticeAvailable;
            _model.PropertyChanged -= TeacherModelPropertyChanged;
            try { await _mobileControl.DisposeAsync(); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                              System.Net.Sockets.SocketException)
            { /* Shutdown is best-effort after the main window closes. */ }
        };
    }

    private void TeacherModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TeacherViewModel.IsClassroomPage) or
            nameof(TeacherViewModel.IsClassroomExperiencePage) or
            nameof(TeacherViewModel.IsHealthCenterPage) or
            nameof(TeacherViewModel.IsUpdatesPage) or
            nameof(TeacherViewModel.IsRoomPage) or
            nameof(TeacherViewModel.IsSetupPage))
            SyncTeacherPageTemplate();
        if (e.PropertyName == nameof(TeacherViewModel.IsHealthCenterPage)) SyncDeviceRuntimePolling();
        if (e.PropertyName is nameof(TeacherViewModel.IsClassroomExperiencePage) or
            nameof(TeacherViewModel.HasActiveClassroomSession))
            SyncClassroomVisualTimers();
    }

    private void SyncTeacherPageTemplate()
    {
        var templateKey = _model.IsRoomPage ? "RoomPageTemplate"
            : _model.IsSetupPage ? "SetupPageTemplate"
            : _model.IsUpdatesPage ? "UpdatesPageTemplate"
            : _model.IsHealthCenterPage ? "HealthPageTemplate"
            : _model.IsClassroomExperiencePage ? "ClassroomPageTemplate"
            : "PolicyPageTemplate";
        if (Resources[templateKey] is not IDataTemplate template ||
            ReferenceEquals(TeacherPageHost.ContentTemplate, template))
            return;

        if (_seatCellPaintGesture.IsActive) EndSeatCellPaintGesture();
        _seatMapGrid = null;
        TeacherPageHost.LayoutUpdated -= TryAttachSeatMapPointerHandler;
        TeacherPageHost.ContentTemplate = template;
        if (_model.IsRoomPage)
            TeacherPageHost.LayoutUpdated += TryAttachSeatMapPointerHandler;
    }

    private void TryAttachSeatMapPointerHandler(object? sender, EventArgs e)
    {
        if (!_model.IsRoomPage)
        {
            TeacherPageHost.LayoutUpdated -= TryAttachSeatMapPointerHandler;
            return;
        }
        if (_seatMapGrid is not null)
        {
            TeacherPageHost.LayoutUpdated -= TryAttachSeatMapPointerHandler;
            return;
        }
        _seatMapGrid = TeacherPageHost.GetVisualDescendants().OfType<ItemsControl>()
            .FirstOrDefault(control => string.Equals(control.Name, "SeatMapGrid", StringComparison.Ordinal));
        if (_seatMapGrid is null) return;
        _seatMapGrid?.AddHandler(InputElement.PointerPressedEvent, SeatCellPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        TeacherPageHost.LayoutUpdated -= TryAttachSeatMapPointerHandler;
    }

    private bool IsClassroomExperienceVisible => OperatingSystem.IsWindows() &&
        IsVisible && WindowState != Avalonia.Controls.WindowState.Minimized && _model.IsClassroomExperiencePage;

    private void SyncClassroomVisualTimers()
    {
        if (!IsClassroomExperienceVisible)
        {
            _classroomQuickControlProgressTimer.Stop();
            _classroomCountdownTimer.Stop();
            return;
        }

        if (_model.HasActiveClassroomSession)
        {
            if (!_classroomQuickControlProgressTimer.IsEnabled)
            {
                _classroomQuickControlProgressTimer.Start();
                RefreshClassroomQuickControlProgress(this, EventArgs.Empty);
            }
        }
        else
            _classroomQuickControlProgressTimer.Stop();

        _model.RefreshClassroomCountdownDisplay();
        if (_model.IsClassroomCountdownRunning)
        {
            if (!_classroomCountdownTimer.IsEnabled) _classroomCountdownTimer.Start();
        }
        else
            _classroomCountdownTimer.Stop();
    }

    private void SyncDeviceRuntimePolling()
    {
        if (!_model.IsHealthCenterPage || !IsVisible || WindowState == Avalonia.Controls.WindowState.Minimized)
        {
            _deviceRuntimeSummaryTimer.Stop();
            return;
        }
        if (!_deviceRuntimeSummaryTimer.IsEnabled) _deviceRuntimeSummaryTimer.Start();
        PollDeviceRuntimeSummaries(this, EventArgs.Empty);
    }

    private async void PollDeviceRuntimeSummaries(object? sender, EventArgs e)
    {
        if (!_model.IsHealthCenterPage || !IsVisible || WindowState == Avalonia.Controls.WindowState.Minimized)
        {
            _deviceRuntimeSummaryTimer.Stop();
            return;
        }
        await _model.PollDeviceRuntimeSummariesAsync();
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

    private void RefreshClassroomQuickControlProgress(object? sender, EventArgs e)
    {
        if (!IsClassroomExperienceVisible)
        {
            SyncClassroomVisualTimers();
            return;
        }
        if (!OperatingSystem.IsWindows() || !_model.HasActiveClassroomSession) return;
        try
        {
            _model.UpdateClassroomQuickControlSnapshot(_mobileControl.ReadClassroomQuickControlSnapshot());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        {
            // Keep the last verified per-device progress visible while a snapshot refresh is unavailable.
        }
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
                var quickControl = await _mobileControl.ReleaseClassroomQuickControlsAsync();
                _lastClassroomQuickControlSummary = FormatClassroomQuickControlResult(quickControl);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or
                                              CryptographicException or PlatformNotSupportedException or SocketException)
            {
                _lastClassroomQuickControlSummary = "下课时快捷管控解除未完成，请在课堂管控卡片中重试。" + exception.Message;
            }
            try
            {
                var restore = await _mobileControl.RestoreClassroomOwnedPoliciesAsync();
                _model.RefreshClassroomRestoreLedger(restore.Message);
                await _model.RefreshActiveClassroomStatusAsync();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or
                                              CryptographicException or PlatformNotSupportedException or SocketException)
            {
                _model.RefreshClassroomRestoreLedger("下课前旧版课堂策略恢复未完成；记录仍保留在教师电脑。" + exception.Message);
            }
        }
        await _model.ToggleClassroomSessionAsync();
        await SyncClassroomEventChannelAsync();
    }

    [SupportedOSPlatform("windows")]
    private async void EnableClassroomWebsiteControl(object? sender, RoutedEventArgs e) =>
        await RunClassroomQuickControlAsync(ClassroomQuickControlAction.EnableWebsiteRestriction,
            "启用课堂网站限制", "将对当前课堂内所有学生电脑应用已保存的网站名单。每台电脑会单独核验；若设备已有其他策略，系统会保留原策略并报告冲突。");

    [SupportedOSPlatform("windows")]
    private async void LockClassroomInput(object? sender, RoutedEventArgs e) =>
        await RunClassroomQuickControlAsync(ClassroomQuickControlAction.LockInput,
            "限时锁定键鼠", "将在当前课堂内的学生电脑申请短时键鼠锁定。教师端会每隔数秒续租；教师端关闭或断线后，锁定最多 20 秒自动解除。");

    [SupportedOSPlatform("windows")]
    private async void ReleaseClassroomQuickControl(object? sender, RoutedEventArgs e) =>
        await RunClassroomQuickControlAsync(ClassroomQuickControlAction.Release,
            "解除课堂管控", "只会解除本工具能够证明归属的网站限制或键鼠锁定；其他来源的策略和锁定会保留并显示为冲突。");

    [SupportedOSPlatform("windows")]
    private async Task RunClassroomQuickControlAsync(ClassroomQuickControlAction action, string title,
        string description)
    {
        if (!_model.HasActiveClassroomSession) return;
        ClassroomQuickControlSnapshot snapshot;
        try { snapshot = _mobileControl.ReadClassroomQuickControlSnapshot(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or CryptographicException or
                                          PlatformNotSupportedException)
        {
            _model.SetClassroomQuickControlSessionNotice("无法读取当前课堂管控状态；没有发送命令。" + exception.Message);
            return;
        }
        if (action != ClassroomQuickControlAction.Release && !snapshot.PilotFeatureEnabled)
        {
            return;
        }

        var names = snapshot.Devices.Select(item => item.DisplayName).Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(6).ToArray();
        var targetText = names.Length == 0 ? $"当前课堂的 {snapshot.Devices.Count} 台电脑" :
            string.Join("、", names) + (snapshot.Devices.Count > names.Length
                ? $" 等 {snapshot.Devices.Count} 台电脑" : "");
        var verb = action switch
        {
            ClassroomQuickControlAction.EnableWebsiteRestriction => "网站限制",
            ClassroomQuickControlAction.LockInput => "键鼠锁定",
            _ => "解除快捷管控"
        };
        if (!await ConfirmProfileActionAsync(title,
                $"{description}\n\n目标：{targetText}\n执行内容：{verb}",
                action == ClassroomQuickControlAction.Release ? "确认解除" : "确认执行")) return;

        SetClassroomQuickControlBusy(true);
        try
        {
            var result = await _mobileControl.ExecuteClassroomQuickControlAsync(action);
            _lastClassroomQuickControlSummary = FormatClassroomQuickControlResult(result);
            _model.UpdateClassroomQuickControlSnapshot(_mobileControl.ReadClassroomQuickControlSnapshot());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or SocketException)
        {
            _model.SetClassroomQuickControlSessionNotice("课堂快捷管控没有完成。" + exception.Message);
        }
        finally { SetClassroomQuickControlBusy(false); }
    }

    private void SetClassroomQuickControlBusy(bool busy)
    {
        _model.SetClassroomQuickControlBusy(busy);
    }

    private void OpenQuickControlWebsitePolicy(object? sender, RoutedEventArgs e) =>
        _model.IsClassroomPage = true;

    private static string FormatClassroomQuickControlResult(ClassroomQuickControlOperationResult result)
    {
        var details = result.Results.Take(16).Select(item =>
            $"{item.DisplayName}：{(item.Phase == ClassroomQuickControlPhase.WaitingForAgent ? "等待 Agent 上线" :
                item.Phase == ClassroomQuickControlPhase.ReleasePending ? "等待恢复" :
                item.Succeeded ? "已核对" : item.NeedsReview ? "待核对" : "失败")} · {item.Detail}")
            .ToArray();
        var extra = result.Results.Count > details.Length
            ? Environment.NewLine + $"另有 {result.Results.Count - details.Length} 台未展开。" : "";
        return details.Length == 0 ? result.Message : result.Message + Environment.NewLine +
            string.Join(Environment.NewLine, details) + extra;
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
    private async void RetryClassroomRestores(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanRetryClassroomRestores) return;
        _model.SetClassroomRestoreRetrying(true);
        _model.RefreshClassroomRestoreLedger("正在读取机房目录并重新核对 Agent 签名状态……");
        try
        {
            var response = await _mobileControl.RetryClassroomRestoresAsync();
            var details = response.Results.Take(24).Select(result =>
                $"{_model.GetStudentDisplayNameForTarget(result.Target)}：{(result.NeedsReview ? "待处理" : result.AgentAccepted ? "已确认" : "未执行")} · {result.Detail}")
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
            if (context is null)
            {
                _model.SetClassroomQuickControlSessionNotice(_lastClassroomQuickControlSummary.Length > 0
                    ? _lastClassroomQuickControlSummary
                    : "课堂已结束；未发送的网站策略不会在下堂课自动执行。");
            }
            else if (OperatingSystem.IsWindows())
            {
                _lastClassroomQuickControlSummary = "";
                var snapshot = _mobileControl.ReadClassroomQuickControlSnapshot();
                _model.UpdateClassroomQuickControlSnapshot(snapshot);
            }
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
        if (_model.IsClassroomChatDrawerOpen && result.Value.Page.Events.Any(item =>
                item.Type == ClassroomEventType.ClassroomChatMessage &&
                string.Equals(item.Target, _model.SelectedClassroomChatThread?.Target,
                    StringComparison.OrdinalIgnoreCase)))
            Dispatcher.UIThread.Post(ScrollTeacherChatToLatest);
        _classroomEventCursor = result.Value.Page.Cursor;
    }

    private void ScrollTeacherChatToLatest() =>
        TeacherChatHistoryScroller.Offset = new Vector(0, TeacherChatHistoryScroller.Extent.Height);

    private void ToggleClassroomChatDrawer(object? sender, RoutedEventArgs e)
    {
        if (!_model.HasActiveClassroomSession) return;
        _model.IsClassroomChatDrawerOpen = !_model.IsClassroomChatDrawerOpen;
        if (_model.IsClassroomChatDrawerOpen)
            Dispatcher.UIThread.Post(() => ClassroomChatSearchInput.Focus());
        else
            TeacherHeader.FocusClassroomChatToggle();
    }

    private void CloseClassroomChatDrawer(object? sender, RoutedEventArgs e)
    {
        _model.IsClassroomChatDrawerOpen = false;
        TeacherHeader.FocusClassroomChatToggle();
    }

    private void DismissClassroomChatDrawer(object? sender, PointerPressedEventArgs e)
    {
        _model.IsClassroomChatDrawerOpen = false;
        Dispatcher.UIThread.Post(() => TeacherHeader.FocusClassroomChatToggle());
    }

    private void TeacherWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_model.IsClassroomChatDrawerOpen) return;
        _model.IsClassroomChatDrawerOpen = false;
        e.Handled = true;
        Dispatcher.UIThread.Post(() => TeacherHeader.FocusClassroomChatToggle());
    }

    private void SelectClassroomChatThread(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TeacherClassroomChatThread thread) return;
        _model.SelectedClassroomChatThread = thread;
        Dispatcher.UIThread.Post(ScrollTeacherChatToLatest);
    }

    private async void OpenClassroomNoticeDialog(object? sender, RoutedEventArgs e)
    {
        if (!_model.HasActiveClassroomSession) return;
        var dialog = new TeacherClassroomNoticeWindow(_model, SendClassroomNotice);
        await dialog.ShowDialog(this);
    }

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
            _model.SetClassroomChatSendStatus("");
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
            _model.SetClassroomChatSendStatus("");
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
        => SendClassroomNotice();

    private void SendClassroomNotice()
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
        var window = new TeacherMobileControlWindow(_mobileControl);
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
    private void CancelSeatMapEditing(object? sender, RoutedEventArgs e) => _model.CancelSeatMapEditing();
    private void ToggleSeatAddRemoveMode(object? sender, RoutedEventArgs e) => _model.ToggleSeatAddRemoveMode();
    private void AssignMissingSeatComputerNumbers(object? sender, RoutedEventArgs e) =>
        _model.AssignMissingSeatComputerNumbers();
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
        SyncClassroomVisualTimers();
    }
    private void EndClassroomCountdown(object? sender, RoutedEventArgs e)
    {
        _model.EndClassroomCountdown();
        SyncClassroomVisualTimers();
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
        if (_seatCellPaintGesture.ConsumeSuppressedClick(cell)) return;
        if (!cell.CanEdit) return;
        if (_model.SeatSwapMode)
        {
            _model.SelectSeatForSwap(cell.Row, cell.Column);
            return;
        }
        if (_model.IsSeatAddRemoveMode)
        {
            if (cell.IsSeat && !cell.IsNewlyAdded &&
                !await ConfirmProfileActionAsync("移除已有座位",
                    "将从本机座位图移除此桌位及其姓名和电脑编号；拖动添加不会删除任何已建立座位，Veyon 目录不受影响。此操作可以通过“取消编辑”恢复。",
                    "移除座位"))
                return;
            _model.ToggleSeatCell(cell.Row, cell.Column);
            return;
        }
        if (!cell.IsSeat) return;
        var edit = await EditSeatCellAsync(cell);
        if (edit is not null)
            _model.SaveSeatCell(cell.Row, cell.Column, edit.StudentName, edit.ComputerNumber);
    }

    private void SeatCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ItemsControl grid ||
            e.Pointer.Type != PointerType.Mouse ||
            !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed ||
            !_model.IsSeatAddRemoveMode || !_model.CanEditSeatMap) return;

        var cell = GetSeatCellAtPoint(grid, e.GetPosition(grid));
        if (cell is null || !cell.CanEdit || cell.IsSeat) return;

        e.Handled = true;
        _seatCellPaintGesture.Begin(cell);
        e.Pointer.Capture(this);
        PaintSeatCell(cell);
    }

    private void SeatMapPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_seatCellPaintGesture.IsActive) return;
        if (e.Pointer.Type != PointerType.Mouse ||
            !_model.IsSeatAddRemoveMode || !_model.CanEditSeatMap)
        {
            e.Pointer.Capture(null);
            EndSeatCellPaintGesture();
            return;
        }

        if (_seatMapGrid is null) return;
        var cell = GetSeatCellAtPoint(_seatMapGrid, e.GetPosition(_seatMapGrid));
        if (cell is not null) PaintSeatCell(cell);
    }

    private TeacherSeatCell? GetSeatCellAtPoint(ItemsControl grid, Point point)
    {
        var columns = _model.SeatGridColumns;
        var rows = decimal.ToInt32(_model.SeatRows);
        var width = grid.Bounds.Width;
        var height = grid.Bounds.Height;
        if (columns < 1 || rows < 1 || width <= 0 || height <= 0 ||
            point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height)
            return null;

        var column = Math.Min(columns, (int)(point.X / width * columns) + 1);
        var row = Math.Min(rows, (int)(point.Y / height * rows) + 1);
        var index = (row - 1) * columns + column - 1;
        return index >= 0 && index < _model.SeatMapCells.Count
            ? _model.SeatMapCells[index]
            : null;
    }

    private void PaintSeatCell(TeacherSeatCell cell)
    {
        var columns = _model.SeatGridColumns;
        var cellsToPaint = new List<(int Row, int Column)>();
        foreach (var (row, column) in _seatCellPaintGesture.GetPathTo(cell.Row, cell.Column))
        {
            var index = (row - 1) * columns + column - 1;
            if (index >= 0 && index < _model.SeatMapCells.Count &&
                _seatCellPaintGesture.ShouldPaint(_model.SeatMapCells[index]))
                cellsToPaint.Add((row, column));
        }
        _model.PaintSeatCells(cellsToPaint, _seatCellPaintGesture.PaintAsSeat);
    }

    private void EndSeatCellPaintGesture()
    {
        var shouldClearSuppressedClick = _seatCellPaintGesture.IsActive &&
                                         _seatCellPaintGesture.HasSuppressedClick;
        _seatCellPaintGesture.End();
        if (shouldClearSuppressedClick)
            Dispatcher.UIThread.Post(_seatCellPaintGesture.ClearSuppressedClick);
    }

    private async Task<SeatCellEditResult?> EditSeatCellAsync(TeacherSeatCell cell)
    {
        var dialog = new Window
        {
            Title = "编辑座位和电脑编号",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.White
        };
        var layout = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(24) };
        layout.Children.Add(new TextBlock
        {
            Text = $"{(cell.SeatNumber is { } number ? $"电脑编号 {number}" : "新座位 · 待编号")} · 第 {cell.Row} 行 · 第 {cell.Column} 列",
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        var numberBox = new TextBox
        {
            Text = cell.SeatNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
            MaxLength = 3,
            Watermark = "1–150（填姓名后按现有最大编号续排）",
            IsEnabled = cell.CanEditComputerNumber
        };
        layout.Children.Add(new TextBlock { Text = "电脑编号" });
        layout.Children.Add(numberBox);
        layout.Children.Add(new TextBlock
        {
            Text = cell.CanEditComputerNumber
                ? "编号固定跟随这个座位，不受插入或移动座位影响。填写姓名且编号留空时，会从本机已登记地点的最大编号后续排；空姓名不编号。"
                : "此座位已绑定 Veyon 电脑。为避免电脑身份错位，编号已锁定；更改电脑名需单独操作。",
            Classes = { "muted" },
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var nameBox = new TextBox
        {
            Text = cell.StudentName,
            MaxLength = 100,
            Watermark = "学生姓名（留空只保留桌位）"
        };
        layout.Children.Add(new TextBlock { Text = "学生姓名" });
        layout.Children.Add(nameBox);
        layout.Children.Add(new TextBlock { Text = "填写姓名后会生成学生电脑；编号留空时按本机已登记地点的最大编号续排。留空姓名只保留桌位，不分配编号。" , Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
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
            Content = "保存",
            IsDefault = true,
            MinWidth = 100
        };
        save.Classes.Add("primary");
        save.Click += (_, _) => dialog.Close(new SeatCellEditResult(nameBox.Text ?? "", numberBox.Text ?? ""));
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        layout.Children.Add(actions);
        dialog.Content = layout;
        return await dialog.ShowDialog<SeatCellEditResult?>(this);
    }

    private sealed record SeatCellEditResult(string StudentName, string ComputerNumber);

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
            Text = "将交换所选座位中的学生姓名。两个座位都有学生且均关联到已核验的 Veyon 电脑时，会同步更新电脑显示名；否则只更新本机座位记录，电脑编号、连接地址和 Veyon 关联保留在原座位。",
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
    private async void ReadStudentDeviceHealth(object? sender, RoutedEventArgs e) =>
        await _model.ReadStudentDeviceHealthAsync();
    private void PreviousDeviceHealthPage(object? sender, RoutedEventArgs e) =>
        _model.PreviousDeviceHealthPage();
    private void NextDeviceHealthPage(object? sender, RoutedEventArgs e) =>
        _model.NextDeviceHealthPage();
    private async void EnsureSelectedVeyonServicesRunning(object? sender, RoutedEventArgs e)
    {
        var targetCount = _model.DeviceHealthSelectedCount;
        var eligibleCount = _model.DeviceHealthVeyonEligibleCount;
        if (eligibleCount == 0) return;
        var confirmed = await ConfirmHealthRepairAsync("批量确保 Veyon 服务运行",
            $"所选 {targetCount} 台电脑中，将向 {eligibleCount} 台最近有签名回执且 VeyonService 未运行的电脑发送固定修复请求；其余 {targetCount - eligibleCount} 台跳过。Agent 只会启动已停止的服务，不会停止正在运行的服务。更新事务冲突时会拒绝执行。是否继续？");
        if (confirmed) await _model.EnsureVeyonServiceRunningForSelectedAsync();
    }

    private async void RefreshSelectedLegacyEdgePolicies(object? sender, RoutedEventArgs e)
    {
        var targetCount = _model.DeviceHealthSelectedCount;
        var eligibleCount = _model.DeviceHealthEdgeEligibleCount;
        if (eligibleCount == 0) return;
        var confirmed = await ConfirmHealthRepairAsync("批量排入 Edge 策略刷新",
            $"所选 {targetCount} 台电脑中，将向 {eligibleCount} 台处于旧 Edge 注册表模式且具有当前有效策略的电脑排入刷新；其余 {targetCount - eligibleCount} 台跳过。回执只表示请求已排入队列，不代表浏览器已应用。是否继续？");
        if (confirmed) await _model.RefreshLegacyEdgePolicyForSelectedAsync();
    }

    private void AcknowledgeDeviceHealthAlert(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TeacherDeviceHealthAlert alert })
            _model.AcknowledgeDeviceHealthAlert(alert);
    }

    private async void RecheckDeviceHealthAlert(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TeacherDeviceHealthAlert alert })
            await _model.RecheckDeviceHealthAlertAsync(alert);
    }

    private void FocusDeviceHealthAlert(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TeacherDeviceHealthAlert alert } &&
            _model.FocusDeviceHealthAlert(alert) is { } row)
            TeacherPageHost.GetVisualDescendants().OfType<ListBox>()
                .FirstOrDefault(control => string.Equals(control.Name, "DeviceHealthRowsList",
                    StringComparison.Ordinal))?.ScrollIntoView(row);
    }

    private async void RepairDeviceHealthAlert(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TeacherDeviceHealthAlert alert } ||
            alert.RecommendedAction is not { } action)
            return;
        var actionTitle = action == StudentAgentHealthRepairAction.EnsureVeyonServiceRunning
            ? "确保 Veyon 服务运行"
            : "排入 Edge 策略刷新";
        var confirmed = await ConfirmHealthRepairAsync("尝试可用修复",
            $"异常“{alert.Title}”影响 {alert.AffectedCount} 台所选电脑。将只向满足该固定动作前置条件的设备发送签名命令，其余设备跳过；不会创建离线待执行命令。继续{actionTitle}？");
        if (confirmed) await _model.RepairDeviceHealthAlertAsync(alert);
    }
    private async void EnsureVeyonServiceRunning(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TeacherDeviceHealthRow row } ||
            !row.CanEnsureVeyonServiceRunning)
            return;
        var confirmed = await ConfirmHealthRepairAsync("启动学生机 Veyon 服务",
            $"将对“{row.StudentName}”发送固定修复请求。仅当服务已停止时才会启动；不会停止正在运行的服务。");
        if (confirmed) await _model.EnsureVeyonServiceRunningAsync(row);
    }

    private async void RefreshLegacyEdgePolicy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TeacherDeviceHealthRow row } ||
            !row.CanRefreshLegacyEdgePolicy)
            return;
        await _model.RefreshLegacyEdgePolicyAsync(row);
    }

    private async Task<bool> ConfirmHealthRepairAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children =
            {
                new Button { Content = "取消", IsCancel = true, MinWidth = 84 },
                new Button { Content = "确认发送", IsDefault = true, MinWidth = 96, Classes = { "primary" } }
            }
        };
        var layout = new StackPanel
        {
            Spacing = 16,
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                actions
            }
        };
        dialog.Content = layout;
        ((Button)actions.Children[0]!).Click += (_, _) => dialog.Close(false);
        ((Button)actions.Children[1]!).Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }
    private async void MigrateEdgeRealtime(object? sender, RoutedEventArgs e) =>
        await _model.MigrateEdgeRealtimeAsync(message =>
            ConfirmProfileActionAsync("启用 Edge 实时同步", message, "确认启用"));
    private async void RestoreEdgeLegacy(object? sender, RoutedEventArgs e) =>
        await _model.RestoreEdgeLegacyAsync(message =>
            ConfirmProfileActionAsync("恢复 Edge 注册表策略", message, "确认恢复"));
    private void ImportCommonWebsiteBlacklist(object? sender, RoutedEventArgs e) => _model.ImportCommonWebsiteBlacklist();
    private void ImportCommonWebsiteAllowlist(object? sender, RoutedEventArgs e) => _model.ImportCommonWebsiteAllowlist();
    private void ImportCommonCombinedWebsitePolicy(object? sender, RoutedEventArgs e) => _model.ImportCommonCombinedWebsitePolicy();
    private void SaveCommonWebsitePolicyPresets(object? sender, RoutedEventArgs e) => _model.SaveCommonWebsitePolicyPresets();
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
    private void SelectRecommendedStudentSystemRestrictions(object? sender, RoutedEventArgs e) =>
        _model.SelectRecommendedStudentSystemRestrictions();
    private void ClearStudentSystemRestrictionSelection(object? sender, RoutedEventArgs e) =>
        _model.ClearStudentSystemRestrictionSelection();
    private async void ReadApplicationInventory(object? sender, RoutedEventArgs e) => await _model.ReadApplicationInventoryAsync();
    private async void ReadApplicationCatalog(object? sender, RoutedEventArgs e) => await _model.ReadApplicationCatalogAsync();
    private async void GenerateApplicationCandidates(object? sender, RoutedEventArgs e) => await _model.ReadSelectedApplicationDetailsAsync();
    private void OpenLongTermSoftwareProtection(object? sender, RoutedEventArgs e) => _model.IsSystemPolicyPage = true;
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
    private void SelectCommonApplicationChoices(object? sender, RoutedEventArgs e) => _model.SelectCommonApplicationChoices();
    private void ClearApplicationChoiceSelection(object? sender, RoutedEventArgs e) => _model.ClearApplicationChoiceSelection();
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

    private async void ExportStudentUpdatePerformanceReport(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
        {
            _model.ReportStudentUpdatePerformanceReportExportFailure();
            return;
        }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出学生端更新匿名性能汇总",
                SuggestedFileName = $"veyon-campus-student-update-performance-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON 性能汇总") { Patterns = ["*.json"] }],
                ShowOverwritePrompt = true
            });
            if (file is null) return;
            using (file)
            {
                var path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path))
                    _model.ReportStudentUpdatePerformanceReportExportFailure();
                else
                    _model.ExportStudentUpdatePerformanceReport(path);
            }
        }
        catch (Exception)
        {
            _model.ReportStudentUpdatePerformanceReportExportFailure();
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
