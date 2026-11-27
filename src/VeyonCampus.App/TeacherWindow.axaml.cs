using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
using VeyonCampus.App.Presentation.Views;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;

namespace VeyonCampus.App;

public partial class TeacherWindow : Window
{
    private static bool SupportsTeacherClassroomPolicyState =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private readonly TeacherViewModel _model = new(roomSelectionStore: new TeacherRoomSelectionStore());
    private readonly TeacherMobileControlManager _mobileControl;
    private readonly ClassroomScreenWallViewModel _screenWall;
    private TeacherScreenDetailWindow? _screenDetailWindow;
    private bool _screenWallWasActive;
    private string _screenWallFingerprint = "";
    private DateTimeOffset _screenWallReadinessUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _screenWallEmptyRetryUtc = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _classroomModeGate = new(1, 1);
    private readonly DispatcherTimer _teacherHeartbeatTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly DispatcherTimer _classroomStatusTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _classroomEventsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _classroomCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _classroomQuickControlProgressTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _classroomQuickControlCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _deviceRuntimeSummaryTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private long _classroomEventCursor;
    private Guid? _classroomEventSessionId;
    private bool _isShowingReleaseNotice;
    private bool _trayBackgroundModeEnabled;
    private bool _allowApplicationExit;
    private bool _isRefreshingQuickControlSnapshot;
    private bool _isOpeningWindow = true;
    private bool _classroomScopeDirty;
    private bool _classroomScopeWaitingForAgentConnections;
    private bool _isReconcilingClassroomScope;
    private CancellationTokenSource? _classroomScopeSyncCancellation;
    private string? _observedClassroomScopeFingerprint;
    private string _lastClassroomQuickControlSummary = "";
    private ItemsControl? _seatMapGrid;
    private readonly Dictionary<(int Row, int Column), Button> _seatMapCellButtons = [];
    private Guid? _seatMapDragSeatId;
    private Point _seatMapDragStart;
    private bool _seatMapDragMoved;
    private (int Row, int Column)? _seatMapVisualDragTarget;

    public TeacherWindow()
    {
        var veyonControlPlane = TeacherVeyonControlPlaneFactory.CreateForCurrentPlatform();
        _mobileControl = new TeacherMobileControlManager(() => _model.CampusId,
            action => Dispatcher.UIThread.Post(action), SetClassroomSessionFromMobileAsync,
            _model.ApplyMobileWebsitePolicyLists, _model.ReadMobileRuntimeSummariesAsync,
            _model.AddStudentNamesToVeyonLocations,
            veyonControlPlane is null
                ? null
                : (target, token) => veyonControlPlane.CaptureScreenAsync(_model.CampusId, target, token));
        InitializeComponent();
        _screenWall = new ClassroomScreenWallViewModel(action => Dispatcher.UIThread.Post(action));
        _model.ClassroomScreenWall = _screenWall;
        SetUpdatesNavigationActive("status");
        ClassroomChatDraftInput.AddHandler(InputElement.KeyDownEvent, ClassroomChatInputKeyDown,
            RoutingStrategies.Tunnel);
        KeyDown += TeacherWindowKeyDown;
        Closing += (_, e) =>
        {
            if (!_trayBackgroundModeEnabled || _allowApplicationExit) return;
            e.Cancel = true;
            Hide();
            _deviceRuntimeSummaryTimer.Stop();
            SyncClassroomVisualTimers();
        };
        Closed += (_, _) =>
        {
            _screenDetailWindow?.Close();
            _screenWall.Dispose();
        };
        Activated += (_, _) =>
        {
            SyncDeviceRuntimePolling();
            SyncClassroomVisualTimers();
            if (OperatingSystem.IsMacOS()) _ = _model.RefreshMacTeacherLoginStartStatusAsync();
        };
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
        DataContext = _model;
        _observedClassroomScopeFingerprint = BuildClassroomScopeFingerprint();
        SyncTeacherPageTemplate();
        SyncContentScrollForClassroom();
        _model.ReleaseNoticeAvailable += OnReleaseNoticeAvailable;
        _model.PropertyChanged += TeacherModelPropertyChanged;
        _model.ActiveClassroomSeatGridChanged += OnActiveClassroomSeatGridChanged;
        _mobileControl.StudentAgentChannelConnected += OnStudentAgentChannelConnected;
        _mobileControl.MaintenanceServiceChannelConnected += OnMaintenanceServiceChannelConnected;
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
        _classroomQuickControlCountdownTimer.Tick += RefreshClassroomQuickControlCountdown;
        _deviceRuntimeSummaryTimer.Tick += PollDeviceRuntimeSummaries;
        _teacherHeartbeatTimer.Start();
        _classroomStatusTimer.Start();
        _classroomEventsTimer.Start();
        Opened += async (_, _) =>
        {
            FitWindowToWorkingArea();
            SyncDeviceRuntimePolling();
            SyncClassroomVisualTimers();
            if (SupportsTeacherClassroomPolicyState && _model.HasActiveClassroomSession)
            {
                try
                {
                    if (_model.GetActiveClassroomEventContext() is { } savedContext)
                    {
                        var savedSnapshot = await Task.Run(() =>
                            _mobileControl.ReadSavedClassroomQuickControlSnapshot(savedContext));
                        if (_model.ActiveClassroomSessionId == savedContext.SessionId)
                            _model.UpdateClassroomQuickControlSnapshot(savedSnapshot);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or InvalidOperationException or CryptographicException or
                    PlatformNotSupportedException)
                {
                    _model.SetClassroomQuickControlSessionNotice("无法恢复本机管控记录；请核对课堂状态。" + exception.Message);
                }
            }
            await _mobileControl.RestoreStartupStateAsync();
            await _model.RefreshMaintenanceOverviewAsync();
            if (OperatingSystem.IsMacOS())
            {
                await _model.RefreshMacTeacherLoginStartStatusAsync();
                await _model.RecoverMacVeyonHelperAtStartupAsync();
            }
            await _model.RefreshTeacherSetupVeyonStatusAsync();
            if (_model.IsClassroomExperiencePage)
                await _model.ReadClassroomLocationsAsync();
            else if (_model.IsClassroomPage && string.IsNullOrWhiteSpace(_model.WebsiteTargets))
                await _model.ReadWebsiteLocationsAsync();
            if (_model.HasActiveClassroomSession)
            {
                await SyncClassroomEventChannelAsync();
                if (_classroomScopeDirty || _model.HasPendingClassroomScopeChange)
                    await ReconcileClassroomScopeAsync();
                await _model.RefreshActiveClassroomStatusAsync();
                await SyncClassroomEventChannelAsync();
                RefreshClassroomEvents(this, EventArgs.Empty);
                var expectedSessionId = _model.ActiveClassroomSessionId;
                if (SupportsTeacherClassroomPolicyState && expectedSessionId is { } expectedSession)
                {
                    try
                    {
                        var snapshot = await Task.Run(() =>
                            _mobileControl.ReadClassroomQuickControlSnapshot(expectedSession));
                        if (_model.ActiveClassroomSessionId == expectedSession)
                            _model.UpdateClassroomQuickControlSnapshot(snapshot);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                        InvalidDataException or InvalidOperationException or CryptographicException or
                        PlatformNotSupportedException or SocketException)
                    {
                        _model.SetClassroomQuickControlSessionNotice(
                            "启动时无法确认课堂管控状态；课堂保持进行中，请稍后重新读取。" + exception.Message);
                    }
                }
            }
            _isOpeningWindow = false;
            _observedClassroomScopeFingerprint = BuildClassroomScopeFingerprint();
            ScheduleLaunchReleaseCheck();
            ShowPendingReleaseNotice();
        };
        Closed += async (_, _) =>
        {
            _teacherHeartbeatTimer.Stop();
            _classroomStatusTimer.Stop();
            _classroomEventsTimer.Stop();
            _classroomCountdownTimer.Stop();
            _classroomQuickControlProgressTimer.Stop();
            _classroomQuickControlCountdownTimer.Stop();
            _deviceRuntimeSummaryTimer.Stop();
            _model.ReleaseNoticeAvailable -= OnReleaseNoticeAvailable;
            _model.PropertyChanged -= TeacherModelPropertyChanged;
            _model.ActiveClassroomSeatGridChanged -= OnActiveClassroomSeatGridChanged;
            _mobileControl.StudentAgentChannelConnected -= OnStudentAgentChannelConnected;
            _mobileControl.MaintenanceServiceChannelConnected -= OnMaintenanceServiceChannelConnected;
            _classroomScopeSyncCancellation?.Cancel();
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
            nameof(TeacherViewModel.IsTeacherAboutPage) or
            nameof(TeacherViewModel.IsRoomPage) or
            nameof(TeacherViewModel.IsSetupPage))
        {
            SyncTeacherPageTemplate();
            // 课堂页所有操作一屏可见，仅学生列表内部滚动；禁用外层整页滚动。
            SyncContentScrollForClassroom();
        }
        if (e.PropertyName == nameof(TeacherViewModel.IsHealthCenterPage)) SyncDeviceRuntimePolling();
        if (e.PropertyName is nameof(TeacherViewModel.IsClassroomExperiencePage) or
            nameof(TeacherViewModel.HasActiveClassroomSession) or
            nameof(TeacherViewModel.IsClassroomScreenWallMode) or
            nameof(TeacherViewModel.IsClassroomSeatMapMode) or
            nameof(TeacherViewModel.IsClassroomListMode) or
            nameof(TeacherViewModel.HasCurrentClassroomQuickControlDeadline))
            SyncClassroomVisualTimers();
        if (e.PropertyName is nameof(TeacherViewModel.IsClassroomScreenWallMode) or
            nameof(TeacherViewModel.IsClassroomSeatMapMode) or nameof(TeacherViewModel.IsClassroomListMode))
            SyncContentScrollForClassroom();
        if (e.PropertyName is nameof(TeacherViewModel.SelectedCampusProfile) or
            nameof(TeacherViewModel.SelectedRoomProfile) or nameof(TeacherViewModel.RoomProfiles))
            ObserveClassroomScopeChange();
    }

    private void OnActiveClassroomSeatGridChanged(object? sender, EventArgs e)
    {
        try { _mobileControl.RefreshClassroomScreenPreviewBindings(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or CryptographicException or
            PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            _model.ReportClassroomScreenPreviewBindingRefreshFailure(exception.Message);
        }
        SyncClassroomScreenWall();
    }

    private void SyncTeacherPageTemplate()
    {
        var templateKey = _model.IsRoomPage ? "RoomPageTemplate"
            : _model.IsSetupPage ? "SetupPageTemplate"
            : _model.IsTeacherAboutPage ? "AboutPageTemplate"
            : _model.IsUpdatesPage ? "UpdatesPageTemplate"
            : _model.IsClassroomExperiencePage ? "ClassroomPageTemplate"
            : "PolicyPageTemplate";
        if (Resources[templateKey] is not IDataTemplate template ||
            ReferenceEquals(TeacherPageHost.ContentTemplate, template))
            return;

        _seatMapGrid = null;
        TeacherPageHost.LayoutUpdated -= TryAttachSeatMapPointerHandler;
        TeacherPageHost.ContentTemplate = template;
        if (_model.IsRoomPage)
            TeacherPageHost.LayoutUpdated += TryAttachSeatMapPointerHandler;
    }

    /// <summary>
    /// 课堂控制页改为左右两栏：所有操作在左栏一屏可见，学生明细只在右栏列表内部滚动。
    /// 因此进入课堂页时关闭外层整页滚动，离开课堂页时恢复，避免多滚动一套。
    /// </summary>
    private void SyncContentScrollForClassroom()
    {
        var scroll = this.FindControl<ScrollViewer>("TeacherContentScroll");
        if (scroll is null) return;
        if (_model.IsClassroomExperiencePage)
        {
            scroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
            scroll.Offset = new Vector(0, 0);
            // 让课堂页 Grid 拿到外层 Viewport 的完整高度，右栏学生列表才能用 * 撑满并内部滚动。
            TeacherPageHost.VerticalContentAlignment = VerticalAlignment.Stretch;
            ApplyClassroomViewportHeight();
        }
        else
        {
            scroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            TeacherPageHost.VerticalContentAlignment = VerticalAlignment.Top;
        }
    }

    /// <summary>把外层内容区的可见高度交给课堂页，让左右两栏按整屏高度布局而非堆叠到内容高度。</summary>
    private void ApplyClassroomViewportHeight()
    {
        if (!_model.IsClassroomExperiencePage) return;
        var scroll = this.FindControl<ScrollViewer>("TeacherContentScroll");
        if (scroll is null) return;
        var view = TeacherPageHost.GetVisualDescendants().OfType<TeacherClassroomView>().FirstOrDefault();
        if (view is null)
        {
            // 模板刚切换时视图尚未生成，等一次布局后再补设高度。
            EventHandler? retry = null;
            retry = (_, _) =>
            {
                TeacherPageHost.LayoutUpdated -= retry;
                ApplyClassroomViewportHeight();
            };
            TeacherPageHost.LayoutUpdated += retry;
            return;
        }
        view.SetViewportHeight(scroll.Viewport.Height);
    }

    private void TeacherContentScrollSizeChanged(object? sender, SizeChangedEventArgs e) =>
        ApplyClassroomViewportHeight();

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
        _seatMapGrid.AddHandler(InputElement.PointerPressedEvent, SeatCellPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _seatMapGrid.AddHandler(InputElement.PointerMovedEvent, SeatMapPointerMoved,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _seatMapGrid.AddHandler(InputElement.PointerReleasedEvent, SeatCellPointerReleased,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        TeacherPageHost.LayoutUpdated -= TryAttachSeatMapPointerHandler;
    }

    private bool IsClassroomExperienceVisible => IsVisible &&
        WindowState != Avalonia.Controls.WindowState.Minimized && _model.IsClassroomExperiencePage;

    private void SyncClassroomVisualTimers()
    {
        SyncClassroomScreenWall();
        if (!IsClassroomExperienceVisible)
        {
            _classroomQuickControlProgressTimer.Stop();
            _classroomQuickControlCountdownTimer.Stop();
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

        if (_model.HasActiveClassroomSession && _model.HasCurrentClassroomQuickControlDeadline)
        {
            if (!_classroomQuickControlCountdownTimer.IsEnabled)
            {
                _classroomQuickControlCountdownTimer.Start();
                _model.RefreshClassroomQuickControlCountdownDisplay();
            }
        }
        else
            _classroomQuickControlCountdownTimer.Stop();

        _model.RefreshClassroomCountdownDisplay();
        if (_model.IsClassroomCountdownRunning)
        {
            if (!_classroomCountdownTimer.IsEnabled) _classroomCountdownTimer.Start();
        }
        else
            _classroomCountdownTimer.Stop();
    }

    private void OpenHealthStudentRoster(object? sender, RoutedEventArgs e) => _model.IsRoomPage = true;

    private void RefreshClassroomQuickControlCountdown(object? sender, EventArgs e)
    {
        if (!IsClassroomExperienceVisible || !_model.HasActiveClassroomSession ||
            !_model.HasCurrentClassroomQuickControlDeadline)
        {
            _classroomQuickControlCountdownTimer.Stop();
            return;
        }

        _model.RefreshClassroomQuickControlCountdownDisplay();
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
        if (_classroomScopeWaitingForAgentConnections) return;
        if (_classroomScopeDirty || _model.HasPendingClassroomScopeChange)
            await ReconcileClassroomScopeAsync();
        await _model.RefreshActiveClassroomStatusAsync();
        await SyncClassroomEventChannelAsync();
    }

    private async void RefreshClassroomQuickControlProgress(object? sender, EventArgs e)
    {
        if (!IsClassroomExperienceVisible)
        {
            SyncClassroomVisualTimers();
            return;
        }
        if (!SupportsTeacherClassroomPolicyState ||
            _model.ActiveClassroomSessionId is not { } expectedSession) return;
        _model.RefreshDefaultQuickControlDeadline();
        _model.RefreshClassroomQuickControlDeadlineState();
        _screenWall.ApplyControlState(_model.ClassroomQuickControlDevices);
        SyncClassroomScreenWall();
        if (_isRefreshingQuickControlSnapshot) return;
        _isRefreshingQuickControlSnapshot = true;
        try
        {
            var snapshot = await Task.Run(() =>
                _mobileControl.ReadClassroomQuickControlSnapshot(expectedSession));
            if (_model.ActiveClassroomSessionId == expectedSession)
            {
                _model.UpdateClassroomQuickControlSnapshot(snapshot);
                _screenWall.ApplyControlState(_model.ClassroomQuickControlDevices);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        {
            // Keep the last verified per-device progress visible while a snapshot refresh is unavailable.
        }
        finally { _isRefreshingQuickControlSnapshot = false; }
    }

    private void ObserveClassroomScopeChange()
    {
        var next = BuildClassroomScopeFingerprint();
        if (string.Equals(next, _observedClassroomScopeFingerprint, StringComparison.Ordinal)) return;
        _observedClassroomScopeFingerprint = next;
        _classroomScopeDirty = true;
        _classroomScopeWaitingForAgentConnections = false;
        if (_isOpeningWindow) return;

        _classroomScopeSyncCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        _classroomScopeSyncCancellation = cancellation;
        Dispatcher.UIThread.Post(() => _ = ReconcileClassroomScopeAfterDelayAsync(cancellation, cancellationToken));
    }

    private void OnStudentAgentChannelConnected(string target)
    {
        ReconcileMaintenanceConfigurationAfterConnection(target);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_classroomScopeWaitingForAgentConnections ||
                !_classroomScopeDirty && !_model.HasPendingClassroomScopeChange)
                return;
            var context = _mobileControl.ReadClassroomSessionContext();
            if (context is null || !context.Targets.Contains(target, StringComparer.OrdinalIgnoreCase)) return;
            _classroomScopeWaitingForAgentConnections = false;
            _ = ReconcileClassroomScopeAfterStudentConnectedAsync();
        });
    }

    private void OnMaintenanceServiceChannelConnected(string target) =>
        ReconcileMaintenanceConfigurationAfterConnection(target);

    private void ReconcileMaintenanceConfigurationAfterConnection(string target) =>
        Dispatcher.UIThread.Post(() => _ = _model.ReconcilePendingMaintenanceConfigurationAsync(target));

    private async Task ReconcileClassroomScopeAfterStudentConnectedAsync()
    {
        try { await ReconcileClassroomScopeAsync(); }
        catch (Exception exception)
        {
            _model.SetClassroomQuickControlSessionNotice(
                "学生端已连接，但课堂范围恢复仍未完成；状态已保留。" + exception.Message);
        }
    }

    private async Task ReconcileClassroomScopeAfterDelayAsync(CancellationTokenSource cancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(180), cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                await ReconcileClassroomScopeAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _model.SetClassroomQuickControlSessionNotice("课堂范围更新未完成；请检查课堂控制状态后重试。" + exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_classroomScopeSyncCancellation, cancellation))
                _classroomScopeSyncCancellation = null;
            cancellation.Dispose();
        }
    }

    private string BuildClassroomScopeFingerprint()
    {
        var campusId = _model.SelectedCampusProfile?.ProfileId;
        var roomId = _model.SelectedRoomProfile?.RoomId;
        var campus = campusId is { } selectedCampusId
            ? _model.CampusProfiles.FirstOrDefault(item => item.ProfileId == selectedCampusId) : null;
        var room = campus?.Rooms.FirstOrDefault(item => item.RoomId == roomId);
        if (campus is null || room is null) return "unconfigured";
        return string.Join("\u001f", new[]
        {
            campus.ProfileId.ToString("N"), campus.DisplayName, room.RoomId.ToString("N"), room.DisplayName,
            room.Prefix, room.StartNumber.ToString(CultureInfo.InvariantCulture),
            room.ComputerCount.ToString(CultureInfo.InvariantCulture),
            string.Join(",", room.ComputerNumbers ?? [])
        });
    }

    private async Task ReconcileClassroomScopeAsync()
    {
        if (_isReconcilingClassroomScope || (!_classroomScopeDirty && !_model.HasPendingClassroomScopeChange)) return;
        _isReconcilingClassroomScope = true;
        await _classroomModeGate.WaitAsync();
        try
        {
            if (!_model.HasActiveClassroomSession)
            {
                _classroomScopeDirty = false;
                _classroomScopeWaitingForAgentConnections = false;
                _observedClassroomScopeFingerprint = BuildClassroomScopeFingerprint();
                return;
            }

            var context = _mobileControl.ReadClassroomSessionContext() ?? _model.GetActiveClassroomEventContext();
            if (context is not null && SupportsTeacherClassroomPolicyState)
            {
                var currentControls = await Task.Run(() =>
                    _mobileControl.ReadClassroomQuickControlSnapshot(context.SessionId));
                var targetIdsNeedingRelease = currentControls.Devices
                    .Where(item => item.Mode != DeviceControlMode.None ||
                                   item.Phase == ClassroomQuickControlPhase.ReleasePending)
                    .Select(item => item.TargetId).ToHashSet();
                if (targetIdsNeedingRelease.Count > 0)
                {
                    var connectedTargetIds = context.TargetIds
                        .Where(pair => targetIdsNeedingRelease.Contains(pair.Value) &&
                                        StudentAgentChannelHub.IsConnected(context.CampusId, pair.Key))
                        .Select(pair => pair.Value).Distinct().ToArray();
                    if (connectedTargetIds.Length == 0)
                    {
                        _classroomScopeWaitingForAgentConnections = true;
                        _model.SetClassroomQuickControlSessionNotice(
                            $"旧课堂范围有 {targetIdsNeedingRelease.Count} 台电脑需要确认恢复；任务已保留，等待学生端主动连接。连接后会自动继续。");
                        return;
                    }

                    _model.SetClassroomQuickControlBusy(true);
                    try
                    {
                        _ = await Task.Run(() => _mobileControl.ReleaseClassroomQuickControlsAsync(
                            selectedTargetIds: connectedTargetIds));
                        var released = await Task.Run(() =>
                            _mobileControl.ReadClassroomQuickControlSnapshot(context.SessionId));
                        var stillPending = released.Devices.Where(item =>
                            item.Mode != DeviceControlMode.None ||
                            item.Phase == ClassroomQuickControlPhase.ReleasePending).ToArray();
                        if (stillPending.Length > 0)
                        {
                            var offlinePendingCount = context.TargetIds
                                .Where(pair => stillPending.Any(item => item.TargetId == pair.Value) &&
                                               !StudentAgentChannelHub.IsConnected(context.CampusId, pair.Key))
                                .Select(pair => pair.Value).Distinct().Count();
                            _classroomScopeWaitingForAgentConnections = offlinePendingCount > 0;
                            _model.SetClassroomQuickControlSessionNotice(offlinePendingCount > 0
                                ? $"旧课堂范围还有 {offlinePendingCount} 台电脑未建立教师端长连接；恢复任务已保留，上线后会自动继续。"
                                : "旧课堂范围仍有快捷管控未确认；已保留状态，等待下一次可信回执后继续。");
                            return;
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                        InvalidDataException or InvalidOperationException or CryptographicException or
                        PlatformNotSupportedException or SocketException)
                    {
                        _classroomScopeWaitingForAgentConnections = context.TargetIds
                            .Where(pair => targetIdsNeedingRelease.Contains(pair.Value))
                            .Any(pair => !StudentAgentChannelHub.IsConnected(context.CampusId, pair.Key));
                        _model.SetClassroomQuickControlSessionNotice(
                            "课堂范围恢复未确认；状态已保留，学生端连接后会继续。" + exception.Message);
                        return;
                    }
                    finally { _model.SetClassroomQuickControlBusy(false); }
                }

                if (_model.HasPendingClassroomRestoreItems)
                {
                    try
                    {
                        var restore = await Task.Run(() => _mobileControl.RestoreClassroomOwnedPoliciesAsync());
                        await _model.RefreshClassroomRestoreLedgerAsync(restore.Message);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                        InvalidDataException or InvalidOperationException or CryptographicException or
                        PlatformNotSupportedException or SocketException)
                    {
                        await _model.RefreshClassroomRestoreLedgerAsync(
                            "原范围的旧版课堂策略恢复仍在等待处理；继续更新课堂范围。" + exception.Message);
                    }
                }
            }

            await _model.UpdateActiveClassroomScopeAsync(context);
            await SyncClassroomEventChannelAsync();
            RefreshClassroomEvents(this, EventArgs.Empty);
            _classroomScopeDirty = _model.HasPendingClassroomScopeChange;
            _classroomScopeWaitingForAgentConnections = false;
            if (!_classroomScopeDirty)
                _observedClassroomScopeFingerprint = BuildClassroomScopeFingerprint();
        }
        finally
        {
            _isReconcilingClassroomScope = false;
            _classroomModeGate.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private async void EnableClassroomWebsiteControl(object? sender, RoutedEventArgs e) =>
        await RunClassroomQuickControlAsync(ClassroomQuickControlAction.EnableWebsiteRestriction,
            "启用课堂网站限制", "将对当前课堂内所有学生电脑应用已保存的网站名单。每台电脑会单独核验；若设备已有其他策略，系统会保留原策略并报告冲突。");

    [SupportedOSPlatform("windows")]
    private async void RestoreClassroomWebsiteAccess(object? sender, RoutedEventArgs e) =>
        await RunClassroomQuickControlAsync(ClassroomQuickControlAction.ClearWebsiteRestrictions,
            "恢复全班网站访问", "只清除本工具在当前课堂设备上写入且能够核验归属的网站限制；其他来源的策略保持不变。");

    [SupportedOSPlatform("windows")]
    private async void LockClassroomInput(object? sender, RoutedEventArgs e)
    {
        if (_model.IsClassroomInputControlActive || _model.HasUnverifiedClassroomInputControl)
            await RunClassroomQuickControlAsync(ClassroomQuickControlAction.UnlockInput,
                "解除键鼠锁定", "只解除本工具持有且能通过签名状态核验的键鼠锁定；网站限制保持不变。");
        else
            await RunClassroomQuickControlAsync(ClassroomQuickControlAction.LockInput,
                "限时锁定键鼠", "将在当前课堂内的学生电脑申请短时键鼠锁定，并在所选截止时间自动解除。教师端关闭或断线后，锁定最多 20 秒自动释放。");
    }

    [SupportedOSPlatform("windows")]
    private async void LockDeviceInput(Guid targetId) =>
        await RunDeviceInputAsync(targetId, ClassroomQuickControlAction.LockInput);

    [SupportedOSPlatform("windows")]
    private async void UnlockDeviceInput(Guid targetId) =>
        await RunDeviceInputAsync(targetId, ClassroomQuickControlAction.UnlockInput);

    [SupportedOSPlatform("windows")]
    private void HandleClassroomDeviceAction(Guid targetId, ClassroomDeviceAction action)
    {
        var tile = _screenWall.Tiles.FirstOrDefault(item => item.TargetId == targetId);
        switch (action)
        {
            case ClassroomDeviceAction.ViewLiveScreen:
            case ClassroomDeviceAction.ShowWebsiteStatus:
            case ClassroomDeviceAction.ShowInputGuardStatus:
            case ClassroomDeviceAction.ShowDeviceInfo:
                if (tile is not null) OpenScreenDetail(tile);
                break;
            case ClassroomDeviceAction.LocateInSeatMap:
                _model.LocateClassroomTarget(targetId);
                SyncClassroomScreenWall();
                TeacherPageHost.GetVisualDescendants().OfType<TeacherClassroomView>()
                    .FirstOrDefault()?.ScrollToClassroomTarget(targetId);
                break;
            case ClassroomDeviceAction.PushWebsitePolicy:
                _ = RunDeviceWebsiteAsync(targetId, ClassroomQuickControlAction.EnableWebsiteRestriction);
                break;
            case ClassroomDeviceAction.RestoreWebsiteAccess:
                _ = RunDeviceWebsiteAsync(targetId, ClassroomQuickControlAction.ClearWebsiteRestrictions);
                break;
            case ClassroomDeviceAction.LockInput:
                LockDeviceInput(targetId);
                break;
            case ClassroomDeviceAction.UnlockInput:
                UnlockDeviceInput(targetId);
                break;
            case ClassroomDeviceAction.ShowDiagnostics:
                if (tile is not null) ShowClassroomDeviceDiagnostics(tile);
                break;
        }
    }

    private void ShowClassroomDeviceDiagnostics(ClassroomScreenTileViewModel tile)
    {
        var control = _model.ClassroomQuickControlDevices.FirstOrDefault(item => item.TargetId == tile.TargetId);
        var details = string.Join("\n", new[]
        {
            $"学生：{(string.IsNullOrWhiteSpace(tile.StudentName) ? "未关联" : tile.StudentName)}",
            $"电脑：{tile.ComputerName}",
            $"座位：{tile.SeatText}（第 {tile.Row} 行 · 第 {tile.Column} 列）",
            $"TargetId：{tile.TargetId:D}",
            $"Veyon Host：{tile.HostText}",
            $"屏幕连接：{tile.LinkStatusText}",
            $"最近画面：{tile.FrameInfoText}",
            $"连接详情：{tile.DetailText}",
            $"网站限制：{tile.WebsiteBadgeText}",
            $"键鼠限制：{tile.InputBadgeText}",
            $"最近管控记录：{control?.UpdatedText ?? "尚无本机记录"}",
            $"管控结果：{control?.Detail ?? "尚未收到该设备的课堂管控回执"}",
            $"WebAPI：{(_screenWall.WebApiWarning.Length == 0 ? "已就绪" : _screenWall.WebApiWarning)}",
            $"Veyon 凭据：{_screenWall.KeyHint}"
        });
        var window = new Window
        {
            Title = $"设备诊断 · {tile.ComputerName}",
            Width = 620,
            Height = 520,
            MinWidth = 480,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.White,
            Content = new ScrollViewer
            {
                Margin = new Avalonia.Thickness(20),
                Content = new TextBlock
                {
                    Text = details,
                    FontSize = 13,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    LineHeight = 22
                }
            }
        };
        window.Show(this);
    }

    /// <summary>
    /// Starts, stops and re-targets the screen wall with the classroom page. The wall is the default
    /// right-hand view, so this runs on every classroom visual sync; a fingerprint keeps it from
    /// rebuilding connections when nothing about the target set changed.
    /// </summary>
    private void SyncClassroomScreenWall()
    {
        var pageVisible = IsClassroomExperienceVisible;
        var captureActive = pageVisible && (_model.IsClassroomScreenWallMode || _screenDetailWindow is not null);
        if (captureActive != _screenWallWasActive)
        {
            _screenWallWasActive = captureActive;
            _screenWall.SetActive(captureActive);
        }

        if (!pageVisible)
        {
            _screenDetailWindow?.Close();
            return;
        }

        if (_model.ActiveClassroomSessionId is not { } sessionId)
        {
            if (_screenWallFingerprint == "inactive-classroom") return;
            _screenWallFingerprint = "inactive-classroom";
            _screenWallEmptyRetryUtc = DateTimeOffset.MinValue;
            _model.SynchronizeClassroomTargets([]);
            _ = ApplyScreenWallAsync(Array.Empty<ClassroomScreenTarget>());
            return;
        }

        var targets = _mobileControl.ReadClassroomScreenTargets(sessionId);
        var fingerprint = sessionId.ToString("N") + "|" + string.Join(";", targets.Select(target =>
            $"{target.TargetId:N}:{target.HostAddress}:{target.SeatNumber}:{target.Row}:{target.Column}:{target.StudentName}:" +
            $"{target.ComputerName}"));
        var emptyRetryDue = targets.Count == 0 && DateTimeOffset.UtcNow >= _screenWallEmptyRetryUtc;
        var readinessRetryDue = DateTimeOffset.UtcNow - _screenWallReadinessUtc > TimeSpan.FromSeconds(20);
        if (string.Equals(fingerprint, _screenWallFingerprint, StringComparison.Ordinal) &&
            !emptyRetryDue && !readinessRetryDue)
            return;
        _screenWallFingerprint = fingerprint;
        _screenWallEmptyRetryUtc = targets.Count == 0
            ? DateTimeOffset.UtcNow.AddSeconds(5)
            : DateTimeOffset.MinValue;
        _ = ApplyScreenWallAsync(targets);
    }

    private async Task ApplyScreenWallAsync(IReadOnlyList<ClassroomScreenTarget> targets)
    {
        try
        {
            if (DateTimeOffset.UtcNow - _screenWallReadinessUtc > TimeSpan.FromSeconds(20))
            {
                _screenWallReadinessUtc = DateTimeOffset.UtcNow;
                await _screenWall.RefreshReadinessAsync();
            }

            await _screenWall.SyncAsync(targets);
            _screenWall.ApplyControlState(_model.ClassroomQuickControlDevices);
            _model.SynchronizeClassroomTargets(targets.Select(target => target.TargetId));
            _model.RebuildClassroomSeatWorkspaceCells();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                           ObjectDisposedException or OperationCanceledException)
        {
            // The wall is auxiliary: a probe or sync failure must never take the classroom page down.
        }
    }

    /// <summary>Opens one card's live picture in its own window and raises that tile's cadence.</summary>
    private void OpenScreenDetail(ClassroomScreenTileViewModel tile)
    {
        if (!_screenWall.Tiles.Any(item => item.TargetId == tile.TargetId)) return;
        _screenDetailWindow?.Close();
        var window = new TeacherScreenDetailWindow(tile, LockOrUnlockDeviceInput, ApplyDeviceWebsitePolicy);
        _screenDetailWindow = window;
        SyncClassroomScreenWall();
        _screenWall.SetFocused(tile.TargetId);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_screenDetailWindow, window)) _screenDetailWindow = null;
            _screenWall.SetFocused(null);
            SyncClassroomScreenWall();
        };
        window.Show(this);
    }

    [SupportedOSPlatform("windows")]
    private void LockOrUnlockDeviceInput(Guid targetId)
    {
        var device = _model.ClassroomQuickControlDevices.FirstOrDefault(item => item.TargetId == targetId);
        if (device is { CanUnlockDeviceInput: true }) UnlockDeviceInput(targetId);
        else LockDeviceInput(targetId);
    }

    /// <summary>
    /// Applies or removes the saved website list on exactly one machine from its screen-wall card.
    /// The class-wide deadline is preserved: a single card must not move everyone's countdown.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void ApplyDeviceWebsitePolicy(Guid targetId)
    {
        var device = _model.ClassroomQuickControlDevices.FirstOrDefault(item => item.TargetId == targetId);
        var restricted = device?.WebsiteState is "applied" or "reported" or "applying";
        _ = RunDeviceWebsiteAsync(targetId, restricted
            ? ClassroomQuickControlAction.ClearWebsiteRestrictions
            : ClassroomQuickControlAction.EnableWebsiteRestriction);
    }

    [SupportedOSPlatform("windows")]
    private async Task RunDeviceWebsiteAsync(Guid targetId, ClassroomQuickControlAction action)
    {
        if (TeacherClassroomQuickControlBuildFeatures.PilotEnabled is false)
        {
            _model.SetClassroomQuickControlSessionNotice("当前教师端构建未启用课堂快捷管控；没有发送网站策略。");
            return;
        }

        if (_model.ActiveClassroomSessionId is not { } sessionId)
        {
            _model.SetClassroomQuickControlSessionNotice("当前没有活动课堂；请先在页面顶部开始课堂，再对单台设备应用网站策略。");
            return;
        }

        var device = _model.ClassroomQuickControlDevices.FirstOrDefault(item => item.TargetId == targetId);
        var name = string.IsNullOrWhiteSpace(device?.DisplayName) ? "该设备" : device!.DisplayName;
        var enabling = action == ClassroomQuickControlAction.EnableWebsiteRestriction;
        var policySummary = _model.ClassroomQuickControlPresetSummary;
        DateTimeOffset? deviceDeadline = null;
        if (enabling)
        {
            deviceDeadline = await PromptSingleDeviceDeadlineAsync("网站策略 · 选择截止时间",
                $"目标设备：{name}\n当前名单：{policySummary}", "只对这台电脑应用当前课堂已保存的网站名单，其他电脑不受影响，" +
                "也不会改动全班的自动解除时间。学生端需要在线；若该设备已有其他来源的策略，会保留并报告冲突。",
                TimeSpan.FromHours(24));
            if (deviceDeadline is null) return;
        }
        else if (!await ConfirmProfileActionAsync("恢复网站访问",
                     $"将解除 {name} 上由本工具写入的网站限制；其他设备保持不变。",
                     "确认恢复"))
            return;

        SetClassroomQuickControlBusy(true);
        try
        {
            var result = await Task.Run(() => _mobileControl.ExecuteClassroomQuickControlAsync(
                sessionId, action, controlExpiresUtc: null, selectedTargetIds: [targetId],
                preserveControlExpiry: true, targetExpiresUtc: deviceDeadline));
            _model.SetClassroomQuickControlSessionNotice(result.Message);
            var refreshed = await Task.Run(() => _mobileControl.ReadClassroomQuickControlSnapshot(sessionId));
            if (_model.ActiveClassroomSessionId == sessionId)
            {
                _model.UpdateClassroomQuickControlSnapshot(refreshed);
                _screenWall.ApplyControlState(_model.ClassroomQuickControlDevices);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or
                                          SocketException)
        {
            _model.SetClassroomQuickControlSessionNotice("网站策略没有完成。" + exception.Message);
        }
        finally
        {
            SetClassroomQuickControlBusy(false);
        }
    }

    private void ToggleAllInputLockSelection(bool selected) => _model.ToggleAllInputLockSelection(selected);

    [SupportedOSPlatform("windows")]
    private async void BatchLockInput(Guid[] targetIds) =>
        await RunInputLockAsync(targetIds, ClassroomQuickControlAction.LockInput);

    [SupportedOSPlatform("windows")]
    private async void BatchUnlockInput(Guid[] targetIds) =>
        await RunInputLockAsync(targetIds, ClassroomQuickControlAction.UnlockInput);

    /// <summary>
    /// Applies a keyboard/mouse lease to exactly one device. The classroom-level button still applies
    /// website restrictions to everyone, but an input lease freezes whoever holds it, so it is only
    /// ever requested for the machine the teacher picked.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task RunDeviceInputAsync(Guid targetId, ClassroomQuickControlAction action) =>
        await RunInputLockAsync([targetId], action);

    /// <summary>
    /// Applies a keyboard/mouse lease to one or many selected devices. A lock carries the custom
    /// deadline and a 2-minute teacher-offline grace; the student Agent persists and self-renews it,
    /// so it survives reboot and does not depend on the teacher's (stoppable) renewal loop. Unlock
    /// releases every selected device that this tool holds.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task RunInputLockAsync(Guid[] targetIds, ClassroomQuickControlAction action)
    {
        if (TeacherClassroomQuickControlBuildFeatures.PilotEnabled is false)
        {
            _model.SetClassroomQuickControlSessionNotice(
                "当前教师端构建未启用课堂快捷管控；没有发送键鼠命令。");
            return;
        }
        if (_model.ActiveClassroomSessionId is not { } sessionId)
        {
            _model.SetClassroomQuickControlSessionNotice(
                "当前没有活动课堂；请先在页面顶部开始课堂，再对设备锁定键鼠。");
            return;
        }
        if (targetIds.Length == 0)
        {
            _model.SetClassroomQuickControlSessionNotice("请先勾选要操作的学生电脑（可多选）。");
            return;
        }
        var locking = action == ClassroomQuickControlAction.LockInput;
        DateTimeOffset? releaseUtc = null;
        var names = targetIds
            .Select(id => _model.ClassroomQuickControlDevices.FirstOrDefault(item => item.TargetId == id)?.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        var displayNames = names.Length > 0 ? string.Join("、", names.Take(6)) +
            (names.Length > 6 ? $" 等 {names.Length} 台" : "") : $"{targetIds.Length} 台设备";
        if (locking)
        {
            if (targetIds.Length == 1)
            {
                // Single machine: its own clock-time, independent of the lesson-wide countdown.
                var single = await PromptSingleDeviceDeadlineAsync("锁定键鼠 · 选择截止时间",
                    $"目标设备：{displayNames}",
                    "锁定后由学生端自行维持，到点自动解除；学生重启会按 45 秒宽限自动恢复锁定。" +
                    "教师端可随时点“解除”立即释放。这一步不会改动整个课堂的自动解除时间。",
                    StudentInputGuardCommandCryptography.MaximumPersistedLockLifetime);
                if (single is null) return;
                releaseUtc = single;
            }
            else
            {
                if (!_model.TryResolveInputLockReleaseUtc(DateTimeOffset.UtcNow, out var resolved, out var error))
                {
                    _model.SetClassroomQuickControlSessionNotice(error);
                    return;
                }
                releaseUtc = resolved;
                if (!await ConfirmProfileActionAsync("锁定键鼠",
                        $"将锁定 {displayNames} 的键盘鼠标，其他学生电脑不受影响。\n\n" +
                        $"锁定持续到今天 {releaseUtc.Value.ToLocalTime():HH:mm}，由学生端自行维持；学生重启后按 45 秒宽限自动恢复锁定。\n" +
                        "教师端可随时点“解除”立即释放；到点也会自动解除。这一步不会改动整个课堂的自动解除时间。\n\n" +
                        "学生端需要交互桌面 Guard 在运行；若状态显示“无法核验”，锁定不会生效。",
                        "确认锁定")) return;
            }
        }
        else if (!await ConfirmProfileActionAsync("解除键鼠锁定",
                     $"将解除 {displayNames} 上由本工具持有的键鼠锁定；网站限制和其他来源的锁定保持不变。",
                     "确认解除")) return;

        SetClassroomQuickControlBusy(true);
        try
        {
            var result = await Task.Run(() => _mobileControl.ExecuteClassroomQuickControlAsync(
                sessionId, action, controlExpiresUtc: null,
                selectedTargetIds: targetIds, preserveControlExpiry: true,
                inputLockReleaseUtc: releaseUtc,
                inputLockGraceSeconds: locking ? TeacherViewModel.InputLockGraceSeconds : 0));
            _model.SetClassroomQuickControlSessionNotice(result.Message);
            var refreshed = await Task.Run(() => _mobileControl.ReadClassroomQuickControlSnapshot(sessionId));
            if (_model.ActiveClassroomSessionId == sessionId)
                _model.UpdateClassroomQuickControlSnapshot(refreshed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or SocketException)
        {
            _model.SetClassroomQuickControlSessionNotice("键鼠操作没有完成。" + exception.Message);
        }
        finally { SetClassroomQuickControlBusy(false); }
    }

    [SupportedOSPlatform("windows")]
    private async void ReleaseClassroomQuickControl(object? sender, RoutedEventArgs e) =>
        await RunClassroomQuickControlAsync(ClassroomQuickControlAction.Release,
            "一键恢复全部", "解除本工具设置的网站限制和键鼠锁定；其他来源的策略和锁定保持不变。");

    [SupportedOSPlatform("windows")]
    private async Task RunClassroomQuickControlAsync(ClassroomQuickControlAction action, string title,
        string description)
    {
        var startsSessionForPolicy = !_model.HasActiveClassroomSession;
        var startsPolicy = action is ClassroomQuickControlAction.EnableWebsiteRestriction or
            ClassroomQuickControlAction.LockInput;
        if (startsSessionForPolicy && (!startsPolicy || !TeacherClassroomQuickControlBuildFeatures.PilotEnabled))
            return;

        Guid? expectedSessionId = _model.ActiveClassroomSessionId;
        ClassroomQuickControlSnapshot? snapshot = null;
        if (!startsSessionForPolicy)
        {
            if (expectedSessionId is null)
            {
                // Returning silently here made the button look broken: no dialog, no status change,
                // nothing in the per-device list. Say what happened instead.
                _model.SetClassroomQuickControlSessionNotice(
                    "课堂会话已失效；请在页面顶部重新开始课堂后再下发。没有发送命令。");
                return;
            }
            try
            {
                snapshot = await Task.Run(() =>
                    _mobileControl.ReadClassroomQuickControlSnapshot(expectedSessionId.Value));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or CryptographicException or
                                              PlatformNotSupportedException)
            {
                _model.SetClassroomQuickControlSessionNotice("无法读取当前课堂管控状态；没有发送命令。" + exception.Message);
                return;
            }
            if (_model.ActiveClassroomSessionId != expectedSessionId)
            {
                _model.SetClassroomQuickControlSessionNotice(
                    "课堂会话已切换；请在当前课堂里重新点击，没有发送命令。");
                return;
            }
            if (snapshot.SessionId != expectedSessionId)
            {
                _model.SetClassroomQuickControlSessionNotice(
                    "本机课堂状态与当前课堂不一致；请结束课堂后重新开始，再下发管控。没有发送命令。");
                return;
            }
        }
        if (action is not (ClassroomQuickControlAction.Release or
            ClassroomQuickControlAction.ClearWebsiteRestrictions or ClassroomQuickControlAction.UnlockInput) &&
            !TeacherClassroomQuickControlBuildFeatures.PilotEnabled)
        {
            _model.SetClassroomQuickControlSessionNotice(
                "当前教师端构建未启用课堂快捷管控；请改用“策略与预设”单独下发。没有发送命令。");
            return;
        }

        var names = snapshot?.Devices.Select(item => item.DisplayName).Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(6).ToArray();
        var targetText = startsSessionForPolicy
            ? _model.ClassroomTargetSummary
            : names is not { Length: > 0 }
                ? $"当前课堂的 {snapshot!.Devices.Count} 台电脑"
                : string.Join("、", names) + (snapshot!.Devices.Count > names.Length
                    ? $" 等 {snapshot.Devices.Count} 台电脑" : "");
        var verb = action switch
        {
            ClassroomQuickControlAction.EnableWebsiteRestriction => "网站限制",
            ClassroomQuickControlAction.LockInput => "键鼠锁定",
            ClassroomQuickControlAction.UnlockInput => "解除键鼠锁定",
            ClassroomQuickControlAction.ClearWebsiteRestrictions => "清除每台电脑的黑名单和白名单",
            _ => "解除快捷管控"
        };
        DateTimeOffset? controlExpiresUtc = null;
        if (action is ClassroomQuickControlAction.EnableWebsiteRestriction or ClassroomQuickControlAction.LockInput)
        {
            try
            {
                controlExpiresUtc = _model.GetClassroomQuickControlDeadlineUtc(DateTimeOffset.UtcNow);
                description += "\n\n自动解除时间：" + controlExpiresUtc.Value.ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) + "。";
            }
            catch (InvalidDataException exception)
            {
                _model.SetClassroomQuickControlSessionNotice(exception.Message);
                return;
            }
        }
        if (action != ClassroomQuickControlAction.Release &&
            !await ConfirmProfileActionAsync(title,
                $"{description}\n\n目标：{targetText}\n执行内容：{verb}",
                action is ClassroomQuickControlAction.ClearWebsiteRestrictions or
                    ClassroomQuickControlAction.UnlockInput ? "确认恢复" : "确认执行")) return;

        SetClassroomQuickControlBusy(true);
        try
        {
            if (startsSessionForPolicy)
            {
                await _model.EnsureClassroomSessionAsync();
                if (!_model.HasActiveClassroomSession) return;
                await SyncClassroomEventChannelAsync();
                if (_model.ActiveClassroomSessionId is not { } activeSession) return;
                expectedSessionId = activeSession;
            }

            if (expectedSessionId is not { } targetSessionId ||
                _model.ActiveClassroomSessionId != targetSessionId) return;
            var result = await Task.Run(() => _mobileControl.ExecuteClassroomQuickControlAsync(
                targetSessionId, action, controlExpiresUtc));
            _lastClassroomQuickControlSummary = FormatClassroomQuickControlResult(result);
            var refreshedSnapshot = await Task.Run(() =>
                _mobileControl.ReadClassroomQuickControlSnapshot(result.SessionId));
            if (_model.ActiveClassroomSessionId == result.SessionId)
                _model.UpdateClassroomQuickControlSnapshot(refreshedSnapshot);
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
        return result.Message;
    }

    private static bool IsQuickControlLifecycleIdle(ClassroomQuickControlSnapshot snapshot,
        Guid expectedSessionId, int expectedTargetCount) => snapshot.SessionId == expectedSessionId &&
        snapshot.Devices.Count == expectedTargetCount && expectedTargetCount > 0 &&
        snapshot.ControlExpiresUtc is null && snapshot.Devices.All(device =>
            // A device this record still owns blocks the end: its policy may still be applied, and the
            // next lesson would inherit an untracked restriction. A device the record no longer owns is
            // a different case - the release was attempted, the readback stayed ambiguous, and the row
            // says 需核对. Keeping that blocking meant a whole classroom could never be ended, because
            // nothing on the teacher side can turn an ambiguous readback into a verified one.
            device.Mode == DeviceControlMode.None && device.Phase is
                ClassroomQuickControlPhase.NotStarted or ClassroomQuickControlPhase.NoneVerified or
                ClassroomQuickControlPhase.Unknown or ClassroomQuickControlPhase.Unsupported);

    private async void ToggleClassroomSession(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanToggleClassroomSession) return;
        try
        {
            if (_model.ActiveClassroomSessionId is { } sessionId)
            {
                // A finished countdown must not keep the lesson open; clear it before asking.
                _model.PrepareClassroomForEnd();

                if (SupportsTeacherClassroomPolicyState)
                {
                    var snapshot = await Task.Run(() => _mobileControl.ReadClassroomQuickControlSnapshot(sessionId));
                    if (_model.ActiveClassroomSessionId == sessionId)
                        _model.UpdateClassroomQuickControlSnapshot(snapshot);
                    if (!IsQuickControlLifecycleIdle(snapshot, sessionId, _model.ActiveClassroomTargetCount))
                    {
                        var busyNotice = "尚有快捷管控未确认解除；先恢复全部学生电脑，再结束课堂。";
                        _model.SetClassroomQuickControlSessionNotice(busyNotice);
                        await ShowNoticeAsync("无法结束课堂", busyNotice);
                        return;
                    }
                }

                if (!await _model.EndClassroomSessionAsync(sessionId))
                {
                    // This used to write into a property no view bound to, so the button looked dead.
                    var blocker = _model.DescribeClassroomEndBlocker();
                    _model.SetClassroomQuickControlSessionNotice(blocker);
                    await ShowNoticeAsync("无法结束课堂", blocker);
                    return;
                }
            }
            else
            {
                await _model.EnsureClassroomSessionAsync();
                if (!_model.HasActiveClassroomSession) return;
            }

            await SyncClassroomEventChannelAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or
                                          SocketException or ArgumentException)
        {
            _model.SetClassroomQuickControlSessionNotice(
                "课堂状态切换失败；请刷新设备和课堂状态后核对。" + exception.Message);
        }
        finally { SyncClassroomVisualTimers(); }
    }

    private Task<MobileClassroomSessionActionResponse> SetClassroomSessionFromMobileAsync(bool desiredActive,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<MobileClassroomSessionActionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (desiredActive)
                {
                    if (!_model.HasActiveClassroomSession)
                    {
                        if (!_model.CanToggleClassroomSession)
                            throw new InvalidOperationException("教师电脑当前无法切换课堂状态；请稍后刷新重试。");
                        await _model.EnsureClassroomSessionAsync();
                    }
                    if (!_model.HasActiveClassroomSession)
                        throw new InvalidOperationException("课堂范围尚未就绪；请在教师电脑选择地点并核对学生电脑名单。");
                }
                else if (_model.ActiveClassroomSessionId is { } sessionId)
                {
                    if (SupportsTeacherClassroomPolicyState)
                    {
                        var snapshot = await Task.Run(() =>
                            _mobileControl.ReadClassroomQuickControlSnapshot(sessionId), cancellationToken);
                        if (_model.ActiveClassroomSessionId == sessionId)
                            _model.UpdateClassroomQuickControlSnapshot(snapshot);
                        if (!IsQuickControlLifecycleIdle(snapshot, sessionId, _model.ActiveClassroomTargetCount))
                            throw new InvalidOperationException(
                                "仍有课堂快捷管控未确认解除；先恢复所有学生电脑，再结束课堂。");
                    }

                    if (!await _model.EndClassroomSessionAsync(sessionId))
                        throw new InvalidOperationException(
                            "课堂仍有未解除的策略、运行中的倒计时或未完成的课堂任务；请先处理后再结束。");
                }

                await SyncClassroomEventChannelAsync();

                var context = _model.GetActiveClassroomEventContext();
                if (desiredActive && context is null)
                    throw new InvalidOperationException("课堂已开启，但无法确认课堂电脑范围；请检查教师电脑上的 Veyon 机房名单。");
                completion.TrySetResult(new MobileClassroomSessionActionResponse(context is not null,
                    context is null ? "课堂已结束。" : _model.ClassroomDeliveryStatus,
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
        await _model.RefreshClassroomRestoreLedgerAsync("正在读取机房目录并重新核对 Agent 签名状态……");
        try
        {
            var response = await Task.Run(() => _mobileControl.RetryClassroomRestoresAsync());
            var details = response.Results.Take(24).Select(result =>
                $"{_model.GetStudentDisplayNameForTarget(result.Target)}：{(result.NeedsReview ? "待处理" : result.AgentAccepted ? "已确认" : "未执行")} · {result.Detail}")
                .ToArray();
            var status = response.Results.Count > details.Length
                ? response.Message + Environment.NewLine + string.Join(Environment.NewLine, details) +
                  Environment.NewLine + $"另有 {response.Results.Count - details.Length} 项结果。"
                : details.Length == 0 ? response.Message : response.Message + Environment.NewLine +
                    string.Join(Environment.NewLine, details);
            await _model.RefreshClassroomRestoreLedgerAsync(status);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or SocketException)
        {
            await _model.RefreshClassroomRestoreLedgerAsync(
                "待恢复项没有全部处理；记录仍保留，可稍后重试。" + exception.Message);
        }
        finally
        {
            _model.SetClassroomRestoreRetrying(false);
            await _model.RefreshClassroomRestoreLedgerAsync();
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
                    : "");
            }
            else if (SupportsTeacherClassroomPolicyState &&
                     _model.ActiveClassroomSessionId == context.SessionId)
            {
                _lastClassroomQuickControlSummary = "";
                var snapshot = await Task.Run(() =>
                    _mobileControl.ReadClassroomQuickControlSnapshot(context.SessionId));
                if (_model.ActiveClassroomSessionId == context.SessionId)
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
        if (_model.IsSeatMapEditing && e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _model.RedoSeatMapEdit();
            else _model.UndoSeatMapEdit();
            e.Handled = true;
            return;
        }
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

    /// <summary>
    /// Runs the launch update check a few seconds after the window settles, so the network call never
    /// delays first paint. This is what makes "open it every morning and it tells me" true even on
    /// days when the daily campus heartbeat already ran before the release was published.
    /// </summary>
    private void ScheduleLaunchReleaseCheck() =>
        DispatcherTimer.RunOnce(async () =>
        {
            try
            {
                await _model.CheckTeacherReleaseOnLaunchAsync();
            }
            finally
            {
                ShowPendingReleaseNotice();
            }
        }, TimeSpan.FromSeconds(6));

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

    private void TeacherContentScrollChanged(object? sender, ScrollChangedEventArgs e) =>
        SynchronizeUpdatesNavigationHighlight();

    private void NavigateDeviceStatusSection(object? sender, RoutedEventArgs e) => NavigateHealthSection("status");
    private void NavigateStudentMaintenanceSection(object? sender, RoutedEventArgs e) => NavigateUpdatesSection("student-maintenance");

    private void NavigateHealthSection(string section)
    {
        SetUpdatesNavigationActive(section);
        TeacherPageHost.GetVisualDescendants().OfType<TeacherHealthView>().FirstOrDefault()?.ScrollToSection(section);
    }

    private void NavigateUpdatesSection(string section)
    {
        SetUpdatesNavigationActive(section);
        TeacherPageHost.GetVisualDescendants().OfType<TeacherUpdatesView>().FirstOrDefault()?.ScrollToSection(section);
    }

    private void SynchronizeUpdatesNavigationHighlight()
    {
        if (!_model.IsUpdatesPage) return;
        var scroll = this.FindControl<ScrollViewer>("TeacherContentScroll");
        if (scroll is null) return;
        var health = TeacherPageHost.GetVisualDescendants().OfType<TeacherHealthView>().FirstOrDefault();
        var updates = TeacherPageHost.GetVisualDescendants().OfType<TeacherUpdatesView>().FirstOrDefault();
        if (health is null || updates is null) return;

        var threshold = Math.Clamp(scroll.Bounds.Height * 0.38, 120, 280);
        var active = "status";
        var sections = new (string Key, Control? Target)[]
        {
            ("status", health.GetSectionControl("status")),
            ("alerts", health.GetSectionControl("alerts")),
            ("students", health.GetSectionControl("students")),
            ("maintenance", health.GetSectionControl("maintenance")),
            ("teacher-update", updates.GetSectionControl("teacher-update")),
            ("student-maintenance", updates.GetSectionControl("student-maintenance"))
        };
        foreach (var (key, target) in sections)
        {
            if (target is null || !target.IsVisible) continue;
            var position = target.TranslatePoint(new Point(0, 0), scroll);
            if (position is { } point && point.Y <= threshold) active = key;
        }
        SetUpdatesNavigationActive(active == "student-maintenance" ? "student-maintenance" : "status");
    }

    private void SetUpdatesNavigationActive(string section)
    {
        var buttons = new (string Key, string Name)[]
        {
            ("status", "UpdatesNavStatus"),
            ("student-maintenance", "UpdatesNavStudent")
        };
        foreach (var (key, name) in buttons)
            this.FindControl<Button>(name)?.Classes.Set("active", key == section);
    }

    private async void AddRoomToVeyon(object? sender, RoutedEventArgs e) => await _model.AddRoomToVeyonAsync();
    private async void ReadSeatVeyonTargets(object? sender, RoutedEventArgs e) =>
        await _model.ReadSeatVeyonTargetsAsync();
    private async void SyncVeyonNamesToCampus(object? sender, RoutedEventArgs e)
    {
        var conflicts = _model.SeatVeyonNameSyncConflicts.ToArray();
        if (conflicts.Length == 0) return;
        var preview = string.Join("\n", conflicts.Select(item => item.DisplayLabel));
        if (!await ConfirmProfileActionAsync("确认按 Veyon Name 更新学生姓名",
                "将按每台电脑的 UUID，把 Veyon Name 同步到对应 Campus 学生姓名：\n" + preview +
                "\n\n若 Veyon Name 等于固定电脑名（默认显示名），同步后该座位学生姓名为空。此操作只更新姓名；Host、电脑编号、UUID 和座位设备绑定不变。",
                "同步学生姓名")) return;
        _model.SetSeatNameSyncStatus(await _model.ApplyVeyonNameConflictsToCampusAsync());
    }
    private async void BindSelectedSeatVeyonTarget(object? sender, RoutedEventArgs e) =>
        await _model.BindSelectedSeatVeyonTargetAsync();
    private void ClearSelectedSeatVeyonTarget(object? sender, RoutedEventArgs e) =>
        _model.ClearSelectedSeatVeyonTarget();
    private void TeacherSetupStep1(object? sender, RoutedEventArgs e) => _model.SetTeacherSetupWizardStep(1);
    private void TeacherSetupStep2(object? sender, RoutedEventArgs e) => _model.SetTeacherSetupWizardStep(2);
    private void TeacherSetupStep3(object? sender, RoutedEventArgs e) => _model.SetTeacherSetupWizardStep(3);
    private async void ChangeMacTeacherLoginStart(object? sender, RoutedEventArgs e) =>
        await _model.ChangeMacTeacherLoginStartAsync();
    private async void RefreshMacTeacherLoginStart(object? sender, RoutedEventArgs e) =>
        await _model.RefreshMacTeacherLoginStartStatusAsync();
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
        var selected = _model.SelectedCampusProfile;
        if (selected is null) return;
        IReadOnlyList<TeacherRoomProfile> rooms = selected.Rooms ?? new List<TeacherRoomProfile>();
        var preview = new string[Math.Min(rooms.Count, 10)];
        for (var index = 0; index < preview.Length; index++)
            preview[index] = rooms[index].DisplayName;
        var roomList = rooms.Count == 0
            ? "该档案下没有地点。"
            : "将同时删除其下 " + rooms.Count + " 个地点：" + string.Join("、", preview) +
              (rooms.Count > preview.Length ? " 等" : "") + "。";
        var message = "将从本机删除校区档案“" + selected.DisplayName + "”。" + roomList +
                      "\n此操作只删除教师电脑上的档案、学生姓名与座位图，不会修改或删除 Veyon 中的地点与电脑；删除后不能从本机恢复。" +
                      "\n如需保留，请先导出座位表或备份本机校区档案。";
        if (await ConfirmProfileActionAsync("删除校区档案", message, "确认删除"))
            _model.DeleteSelectedCampusProfile();
    }
    private void NewRoomProfile(object? sender, RoutedEventArgs e) => _model.NewRoomProfile();
    private async void PrepareRoomSeatGrid(object? sender, RoutedEventArgs e) =>
        await _model.PrepareCurrentRoomSeatGrid();
    private void ReturnToTeacherSetupWizard(object? sender, RoutedEventArgs e) =>
        _model.ReturnToTeacherSetupWizard();
    private void OpenVeyonConfigurator(object? sender, RoutedEventArgs e) => _model.OpenVeyonConfigurator();
    private async void ToggleSeatMapEditing(object? sender, RoutedEventArgs e)
    {
        if (!_model.IsSeatMapEditing)
        {
            _model.ToggleSeatMapEditing();
            return;
        }
        if (!_model.SaveSeatMapDraftLocally()) return;
        await RunSeatMapVeyonSyncAsync();
    }
    private void CancelSeatMapEditing(object? sender, RoutedEventArgs e) => _model.CancelSeatMapEditing();
    private void AddSeatGridRow(object? sender, RoutedEventArgs e) => _model.AddSeatGridRow();
    private void AddSeatGridColumn(object? sender, RoutedEventArgs e) => _model.AddSeatGridColumn();
    private async void DeleteSeatGridRow(object? sender, SeatGridAxisEventArgs e)
    {
        if (!_model.IsSeatMapEditing) return;
        var seats = _model.SeatMapCells.Count(cell => cell.IsSeat && cell.Row == e.Index);
        if (seats > 0)
        {
            await ShowSeatGridAxisRemovalNoticeAsync($"第 {e.Index} 行还有 {seats} 个座位。请先移动或移除这些座位，再删除此行。学生和电脑关联不会被删除。");
            return;
        }
        if (await ConfirmProfileActionAsync($"删除第 {e.Index} 行？",
                "此行为空。删除只会缩小本机座位布局，不会删除 Veyon 电脑。保存前可用 Ctrl+Z 撤销。", "删除此行"))
            _model.DeleteEmptySeatGridRow(e.Index);
    }

    private async void DeleteSeatGridColumn(object? sender, SeatGridAxisEventArgs e)
    {
        if (!_model.IsSeatMapEditing) return;
        var seats = _model.SeatMapCells.Count(cell => cell.IsSeat && cell.Column == e.Index);
        if (seats > 0)
        {
            await ShowSeatGridAxisRemovalNoticeAsync($"第 {e.Index} 列还有 {seats} 个座位。请先移动或移除这些座位，再删除此列。学生和电脑关联不会被删除。");
            return;
        }
        if (await ConfirmProfileActionAsync($"删除第 {e.Index} 列？",
                "此列为空。删除只会缩小本机座位布局，不会删除 Veyon 电脑。保存前可用 Ctrl+Z 撤销。", "删除此列"))
            _model.DeleteEmptySeatGridColumn(e.Index);
    }
    private async void DeleteSeatComputer(object? sender, SeatGridSeatEventArgs e)
    {
        var seat = e.Seat;
        var objectId = seat.VeyonObjectId ?? Guid.Empty;
        var label = string.IsNullOrWhiteSpace(seat.StudentName)
            ? (string.IsNullOrWhiteSpace(seat.ComputerLabel) ? "未命名座位" : seat.ComputerLabel)
            : seat.StudentName;
        var hasVeyonComputer = objectId != Guid.Empty;
        var message = hasVeyonComputer
            ? $"将从 Veyon 目录删除电脑“{label}”（按 UUID {objectId:D}），并移除本机对应座位。" +
              "此操作会修改 Veyon，不能自动撤销；删除前请确认已导出配置备份。"
            : $"座位“{label}”没有绑定 Veyon 电脑 UUID，将只从本机座位图移除该座位，Veyon 不会改动。\n" +
              "如果 Veyon 里确实有这台电脑，请先点「重试 Veyon 同步」按 Host 重新建立 UUID 关联。";
        if (!await ConfirmProfileActionAsync(hasVeyonComputer ? "删除学生电脑" : "删除本机座位",
                message, "确认删除")) return;
        // Always show the outcome: a discarded result used to make right-click look broken.
        var result = await _model.DeleteSeatComputerFromVeyonAsync(seat.Row, seat.Column);
        await ShowSeatActionNoticeAsync("删除学生电脑", result);
    }

    private async void DeleteSeat(object? sender, SeatGridSeatEventArgs e)
    {
        if (!_model.IsSeatMapEditing) return;
        var seat = e.Seat;
        var details = seat.HasVeyonTarget
            ? "将从本机座位图移除此座位及本地设备关联；Veyon 中的电脑对象和 Host 不会被删除。"
            : "将从本机座位图移除此座位、学生姓名和电脑编号。";
        if (await ConfirmProfileActionAsync("删除此座位", details + " 可在保存前用 Ctrl+Z 撤销。", "删除座位"))
            _model.RemoveSeatCell(seat.Row, seat.Column, unassignVeyonTarget: true);
    }

    private async void ClearSeatVeyonTarget(object? sender, SeatGridSeatEventArgs e)
    {
        if (!_model.IsSeatMapEditing || !e.Seat.HasVeyonTarget) return;
        var seat = e.Seat;
        if (await ConfirmProfileActionAsync("解除本机电脑关联",
                $"只解除第 {seat.Row} 行第 {seat.Column} 列座位与 Veyon 电脑的本机对应关系；保留学生姓名和电脑编号，也不会删除或修改 Veyon 中的电脑。点击保存后应用，可在保存前用 Ctrl+Z 撤销。",
                "解除本机关联"))
            _model.ClearSeatVeyonTarget(seat.Row, seat.Column);
    }

    private async void RetrySeatMapSync(object? sender, RoutedEventArgs e)
    {
        if (!_model.IsSeatMapEditing) _model.ToggleSeatMapEditing();
        if (!_model.SaveSeatMapDraftLocally()) return;
        await RunSeatMapVeyonSyncAsync();
    }

    private async void RetryInitialSeatGridImport(object? sender, RoutedEventArgs e)
    {
        await _model.RetryInitialSeatGridImportAsync();
    }

    private void OpenInitialSeatGridConflictLocation(object? sender, RoutedEventArgs e) =>
        _model.OpenInitialSeatGridConflictLocation();

    private async Task RunSeatMapVeyonSyncAsync()
    {
        _model.BeginSeatMapVeyonSync();
        var classroomPaused = false;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                _model.ReportSeatMapSyncFailure("当前系统不支持 Veyon 目录写回；本机资料已保存。请在 Windows 教师端重试。");
                return;
            }

            // A live lesson must not have its roster rewritten underneath it, and the write path
            // already refuses while a session is active. Probe the CLI first: if it cannot start,
            // interrupting the lesson buys nothing and the teacher would lose the session for a
            // write that cannot happen.
            if (_model.HasActiveClassroomSession && !VeyonNetworkObjectDirectory.IsCliWritable())
            {
                _model.ReportSeatMapSyncFailure(
                    "这台电脑的 Veyon 命令行（veyon-cli）无法启动，不能写入地点和电脑；课堂保持进行中，本机座位资料已保存。" +
                    "请先修复 Veyon 安装（或让安全软件不再干扰 veyon-cli），再点「重试 Veyon 同步」。");
                return;
            }

            if (_model.HasActiveClassroomSession)
            {
                var pause = await _model.PauseClassroomForVeyonWriteAsync();
                if (pause.Error is { Length: > 0 } pauseError)
                {
                    _model.ReportSeatMapSyncFailure(
                        "本机资料已保存；写入 Veyon 需要先暂停课堂，但课堂未能自动结束：" + pauseError);
                    return;
                }
                classroomPaused = pause.Paused;
            }

            var bindingError = await _model.ReconcileSeatVeyonBindingsByHostAsync();
            if (bindingError is not null)
            {
                await ReportSeatMapSyncFailureWithDirectoryRefreshAsync(
                    "设备身份核对未通过，未自动绑定或覆盖设备：" + bindingError);
                return;
            }

            if (_model.SeatGridUnboundComputerCount == 0)
            {
                var refreshed = await _model.RefreshDeviceListsAfterSeatMapSaveAsync();
                if (refreshed) _model.ReportSeatMapSyncSuccess();
                else _model.ReportSeatMapSyncFailure("本机资料已保存，但 Veyon 设备名单未能刷新。原因：" +
                    (_model.ClassroomDirectoryError.Length > 0
                        ? _model.ClassroomDirectoryError
                        : "请查看「地点与学生名单」页诊断后重试。"));
                return;
            }

            await _model.AddRoomToVeyonAsync();
            if (_model.RoomCreateError.Length == 0) _model.ReportSeatMapSyncSuccess();
            else await ReportSeatMapSyncFailureWithDirectoryRefreshAsync(_model.RoomCreateError);
        }
        catch (Exception exception)
        {
            await ReportSeatMapSyncFailureWithDirectoryRefreshAsync(
                "本机资料已保存；Veyon 自动同步失败。" + exception.Message);
        }
        finally
        {
            if (classroomPaused) await _model.ResumeClassroomAfterVeyonWriteAsync();
            _model.FinishSeatMapEditingAfterSave();
            _model.EndSeatMapVeyonSync();
        }
    }

    private async Task ReportSeatMapSyncFailureWithDirectoryRefreshAsync(string message)
    {
        // A verified write whose view refresh already failed carries the concrete reason from the
        // view model. Refreshing a second time only doubles the Veyon CLI work and prints the same
        // paragraph twice, so report the original message as-is.
        if (message.Contains("本机策略与课堂设备名单未能刷新", StringComparison.Ordinal))
        {
            _model.ReportSeatMapSyncFailure(message);
            return;
        }

        var refreshed = false;
        try { refreshed = await _model.RefreshDeviceListsAfterSeatMapSaveAsync(); }
        catch { /* Preserve the original sync error; the VM refresh method records its own read error. */ }
        _model.ReportSeatMapSyncFailure(refreshed
            ? message
            : message + "\n当前 Veyon 名单未能通过完整读取与身份核对；请查看「地点与学生名单」页诊断，修复后点「重试 Veyon 同步」。");
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
        if (!cell.CanEdit) return;
        var edit = await EditSeatCellAsync(cell);
        if (edit is null) return;
        _model.SaveSeatCell(cell.Row, cell.Column, edit.StudentName, edit.ComputerNumber,
            saveImmediately: true);
    }

    private void SeatCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ItemsControl grid || e.Pointer.Type != PointerType.Mouse) return;
        var pointerProperties = e.GetCurrentPoint(grid).Properties;
        if (!pointerProperties.IsLeftButtonPressed || !_model.IsSeatMapEditing || !_model.CanEditSeatMap)
            return;

        _model.ClearSeatGridDragFeedback();
        ClearSeatMapDragTargetVisual(clearIndex: true);
        var cell = GetSeatCellAtPoint(grid, e.GetPosition(grid));
        if (cell is null || !cell.CanEdit || !cell.IsSeat || cell.SeatId is not { } seatId) return;
        _seatMapDragSeatId = seatId;
        _seatMapDragStart = e.GetPosition(grid);
        _seatMapDragMoved = false;
        IndexSeatMapCellButtons();
    }

    private void SeatMapPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_seatMapDragSeatId is null || _seatMapGrid is null || !_model.CanEditSeatMap ||
            e.Pointer.Type != PointerType.Mouse) return;
        var point = e.GetPosition(_seatMapGrid);
        if (!_seatMapDragMoved && Math.Abs(point.X - _seatMapDragStart.X) + Math.Abs(point.Y - _seatMapDragStart.Y) >= 3)
        {
            _seatMapDragMoved = true;
            e.Pointer.Capture(_seatMapGrid);
        }
        if (_seatMapDragMoved)
        {
            var target = GetSeatCellAtPoint(_seatMapGrid, point);
            SetSeatMapDragTargetVisual(target);
            _model.ReportSeatGridDragTarget(target?.Row, target?.Column);
            e.Handled = true;
        }
    }

    private void SeatCellPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not ItemsControl grid || _seatMapDragSeatId is not { } sourceSeatId) return;
        if (_seatMapDragMoved)
        {
            var target = GetSeatCellAtPoint(grid, e.GetPosition(grid));
            ClearSeatMapDragTargetVisual(clearIndex: true);
            _model.ClearSeatGridDragFeedback();
            if (target is { CanEdit: true }) _model.MoveSeatDraft(sourceSeatId, target.Row, target.Column);
            else _model.ReportSeatGridDragCancelled();
            e.Handled = true;
            e.Pointer.Capture(null);
        }
        _seatMapDragSeatId = null;
        _seatMapDragMoved = false;
    }

    private void IndexSeatMapCellButtons()
    {
        _seatMapCellButtons.Clear();
        if (_seatMapGrid is null) return;
        foreach (var button in _seatMapGrid.GetVisualDescendants().OfType<Button>())
        {
            if (button.DataContext is TeacherSeatCell cell)
                _seatMapCellButtons[(cell.Row, cell.Column)] = button;
        }
    }

    private void SetSeatMapDragTargetVisual(TeacherSeatCell? target)
    {
        (int Row, int Column)? next = target is null ? null : (target.Row, target.Column);
        if (_seatMapVisualDragTarget == next) return;
        ClearSeatMapDragTargetVisual();
        _seatMapVisualDragTarget = next;
        if (next is { } coordinate && _seatMapCellButtons.TryGetValue(coordinate, out var button))
            button.Classes.Set("dragTarget", true);
    }

    private void ClearSeatMapDragTargetVisual(bool clearIndex = false)
    {
        if (_seatMapVisualDragTarget is { } coordinate &&
            _seatMapCellButtons.TryGetValue(coordinate, out var button))
            button.Classes.Set("dragTarget", false);
        _seatMapVisualDragTarget = null;
        if (clearIndex) _seatMapCellButtons.Clear();
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
            Text = cell.SeatNumber?.ToString(CultureInfo.InvariantCulture) ??
                   (!cell.IsSeat ? _model.SuggestNextSeatComputerNumber() : ""),
            MaxLength = 3,
            PlaceholderText = "1–150（填姓名后按现有最大编号续排）",
            IsEnabled = cell.CanEditComputerNumber
        };
        layout.Children.Add(new TextBlock { Text = "电脑编号" });
        layout.Children.Add(numberBox);
        layout.Children.Add(new TextBlock
        {
            Text = cell.CanEditComputerNumber
                ? "编号固定跟随这个座位，不受插入或移动座位影响。填写姓名且编号留空时，会从本机已登记地点的最大编号后续排；空姓名不编号。"
                : "此座位已绑定 Veyon 电脑。编号对应固定 Windows Hostname；修改学生姓名只保存到教师端，不会改变 Veyon Name、HostAddress 或 UUID。",
            Classes = { "muted" },
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var nameBox = new TextBox
        {
            Text = cell.StudentName,
            MaxLength = 100,
            PlaceholderText = "学生姓名（留空只保留桌位）"
        };
        layout.Children.Add(new TextBlock { Text = "学生姓名" });
        layout.Children.Add(nameBox);
        layout.Children.Add(new TextBlock { Text = "填写姓名后会为座位建议电脑编号；无姓名座位也可手动分配编号。学生姓名与 Veyon 电脑名称分别保存；改姓名不会修改 Veyon Name、Host 或 UUID。" , Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
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

    private async Task<SeatNameConflictAction> ChooseSeatNameConflictActionAsync(string details)
    {
        var dialog = new Window
        {
            Title = "选择要保留的学生姓名",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.White
        };
        var layout = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(24) };
        layout.Children.Add(new TextBlock
        {
            Text = details + "\n\n请选择姓名来源。此操作只同步显示名称；电脑 Host、UUID 和座位绑定不会变化。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = new Button { Content = "稍后处理", IsCancel = true, MinWidth = 90 };
        cancel.Classes.Add("secondary");
        cancel.Click += (_, _) => dialog.Close(SeatNameConflictAction.Cancel);
        var fromVeyon = new Button { Content = "采用 Veyon Name", MinWidth = 130 };
        fromVeyon.Click += (_, _) => dialog.Close(SeatNameConflictAction.UseVeyonName);
        var fromCampus = new Button { Content = "采用本机姓名", IsDefault = true, MinWidth = 130 };
        fromCampus.Classes.Add("primary");
        fromCampus.Click += (_, _) => dialog.Close(SeatNameConflictAction.UseCampusName);
        actions.Children.Add(cancel);
        actions.Children.Add(fromVeyon);
        actions.Children.Add(fromCampus);
        layout.Children.Add(actions);
        dialog.Content = layout;
        return await dialog.ShowDialog<SeatNameConflictAction>(this);
    }

    private enum SeatNameConflictAction { Cancel, UseVeyonName, UseCampusName }

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
            Text = "将交换所选座位的学生姓名（允许一个座位为空）。Veyon Name、电脑编号、连接地址、UUID 和座位绑定均保持不变。",
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

    /// <summary>Single-button notice. Used where a silent failure would look like a dead control.</summary>
    private async Task ShowNoticeAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
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
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        var ok = new Button { Content = "知道了", IsDefault = true, MinWidth = 96 };
        ok.Click += (_, _) => dialog.Close();
        actions.Children.Add(ok);
        layout.Children.Add(actions);
        dialog.Content = layout;
        await dialog.ShowDialog(this);
    }

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

    /// <summary>
    /// One card's deadline picker. The class-wide deadline lives on the classroom page and belongs to
    /// everyone; a single machine gets its own clock-time here so "this one until 10:50" does not move
    /// the countdown every other seat is watching. Returns null when the teacher cancels.
    /// </summary>
    private async Task<DateTimeOffset?> PromptSingleDeviceDeadlineAsync(string title, string summary,
        string description, TimeSpan maximum)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.White
        };
        var now = DateTimeOffset.Now;
        var defaultDeadline = now.AddMinutes(45);
        if (defaultDeadline.Date != now.Date) defaultDeadline = now.Date.AddDays(1).AddMinutes(-1);
        var layout = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(24) };
        layout.Children.Add(new TextBlock
        {
            Text = summary, FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        layout.Children.Add(new TextBlock
        {
            Text = description, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.DimGray
        });
        layout.Children.Add(new TextBlock { Text = "截止时间（今天，24 小时制 HH:mm）" });
        var timeBox = new TextBox
        {
            Text = defaultDeadline.ToString("HH:mm"), Width = 120,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        layout.Children.Add(timeBox);
        var presets = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var minutes in new[] { 15, 30, 45, 60, 90, 120 })
        {
            var preset = new Button
            {
                Content = $"{minutes} 分钟", Margin = new Avalonia.Thickness(0, 0, 8, 6),
                MinWidth = 0, Padding = new Avalonia.Thickness(12, 6)
            };
            preset.Classes.Add("tertiary");
            preset.Click += (_, _) =>
            {
                var candidate = DateTimeOffset.Now.AddMinutes(minutes);
                if (candidate.Date != DateTimeOffset.Now.Date)
                    candidate = DateTimeOffset.Now.Date.AddDays(1).AddMinutes(-1);
                timeBox.Text = candidate.ToString("HH:mm");
            };
            presets.Children.Add(preset);
        }
        layout.Children.Add(presets);
        var preview = new TextBlock
        {
            Text = "", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.DimGray
        };
        layout.Children.Add(preview);
        void RefreshPreview()
        {
            if (TryParseDeadline(timeBox.Text, DateTimeOffset.Now, maximum, out var parsed, out var error))
            {
                preview.Text = $"将在 {parsed.ToLocalTime():HH:mm} 自动解除。";
                preview.Foreground = Avalonia.Media.Brushes.DimGray;
            }
            else
            {
                preview.Text = error;
                preview.Foreground = Avalonia.Media.Brushes.Firebrick;
            }
        }
        timeBox.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 90 };
        cancel.Classes.Add("secondary");
        var confirm = new Button { Content = "确认", IsDefault = true, MinWidth = 100 };
        confirm.Classes.Add("primary");
        cancel.Click += (_, _) => dialog.Close(null);
        confirm.Click += (_, _) =>
        {
            if (!TryParseDeadline(timeBox.Text, DateTimeOffset.Now, maximum, out var parsed, out var error))
            {
                preview.Text = error;
                preview.Foreground = Avalonia.Media.Brushes.Firebrick;
                return;
            }
            dialog.Close(parsed);
        };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        layout.Children.Add(actions);
        dialog.Content = layout;
        return await dialog.ShowDialog<DateTimeOffset?>(this);
    }

    private static bool TryParseDeadline(string? text, DateTimeOffset now, TimeSpan maximum,
        out DateTimeOffset deadline, out string error)
    {
        deadline = default;
        error = "";
        if (!TimeSpan.TryParseExact((text ?? "").Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var time) &&
            !TimeSpan.TryParseExact((text ?? "").Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time))
        {
            error = "请输入 HH:mm 格式的截止时间，例如 10:50。";
            return false;
        }
        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            error = "截止时间需在今天之内。";
            return false;
        }
        var local = now.ToLocalTime();
        var candidate = new DateTimeOffset(local.Year, local.Month, local.Day,
            time.Hours, time.Minutes, 0, local.Offset);
        if (candidate <= now)
        {
            error = "截止时间必须晚于当前时间。";
            return false;
        }
        if (candidate - now > maximum)
        {
            error = maximum >= TimeSpan.FromHours(24)
                ? "截止时间不能超过 24 小时。"
                : $"截止时间不能超过 {maximum.TotalHours:0} 小时。";
            return false;
        }
        deadline = candidate.ToUniversalTime();
        return true;
    }

    private Task ShowSeatGridAxisRemovalNoticeAsync(string message) =>
        ShowSeatActionNoticeAsync("此行列暂时不能删除", message);
    private async Task ShowSeatActionNoticeAsync(string title, string message)
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
        var ok = new Button { Content = "知道了", IsDefault = true, MinWidth = 90,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.Close();
        layout.Children.Add(ok);
        dialog.Content = layout;
        await dialog.ShowDialog(this);
    }

    private async Task<bool> ConfirmWebsitePolicyPushAsync()
    {
        var domains = _model.WebsitePolicyConfirmationDomains;
        var screen = Screens.ScreenFromWindow(this);
        var scaling = screen is { Scaling: > 0 } ? screen.Scaling : 1d;
        var availableHeight = screen is null ? Math.Max(360d, Bounds.Height) : screen.WorkingArea.Height / scaling;
        var availableWidth = screen is null ? Math.Max(360d, Bounds.Width) : screen.WorkingArea.Width / scaling;
        var maximumHeight = Math.Max(1d, Math.Min(680d, availableHeight - 32d));
        var maximumWidth = Math.Max(1d, Math.Min(760d, availableWidth - 40d));
        var domainListHeight = Math.Max(24d, Math.Min(240d, maximumHeight - 280d));
        var dialog = new Window
        {
            Title = "确认推送网站限制",
            Width = Math.Min(620d, maximumWidth),
            MaxWidth = maximumWidth,
            MaxHeight = maximumHeight,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.White
        };

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = _model.WebsitePolicyConfirmationSummary,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });

        var showDomains = new Button
        {
            Content = $"查看完整名单（{domains.Count} 个域名）",
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = domains.Count > 0
        };
        var search = new TextBox { PlaceholderText = "搜索域名", IsVisible = false };
        var matches = new TextBlock { Classes = { "supporting-copy" }, IsVisible = false };
        var domainText = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = domainListHeight,
            IsVisible = false
        };
        void UpdateDomainText()
        {
            var query = search.Text?.Trim() ?? "";
            var filtered = domains.Where(domain => query.Length == 0 ||
                domain.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            domainText.Text = string.Join(Environment.NewLine, filtered);
            matches.Text = query.Length == 0
                ? $"共 {domains.Count} 个域名"
                : $"找到 {filtered.Length} / {domains.Count} 个域名";
        }

        showDomains.Click += (_, _) =>
        {
            var expanded = !search.IsVisible;
            search.IsVisible = expanded;
            matches.IsVisible = expanded;
            domainText.IsVisible = expanded;
            showDomains.Content = expanded
                ? "收起完整名单"
                : $"查看完整名单（{domains.Count} 个域名）";
            UpdateDomainText();
        };
        search.TextChanged += (_, _) => UpdateDomainText();
        content.Children.Add(showDomains);
        content.Children.Add(search);
        content.Children.Add(matches);
        content.Children.Add(domainText);

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Avalonia.Thickness(20)
        };
        layout.Children.Add(new ScrollViewer
        {
            Content = content,
            MaxHeight = Math.Max(1d, maximumHeight - 130d),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 14, 0, 0)
        };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 90 };
        cancel.Classes.Add("secondary");
        var confirm = new Button { Content = "确认推送", IsDefault = true, MinWidth = 100 };
        confirm.Classes.Add("primary");
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        Grid.SetRow(actions, 1);
        layout.Children.Add(actions);
        dialog.Content = layout;
        return await dialog.ShowDialog<bool>(this);
    }

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
        if (!await ConfirmWebsitePolicyPushAsync()) return;
        if (!await EnsureTeacherAgentChannelStartedAsync()) return;
        await _model.PushWebsitePolicyAsync();
    }
    private async void DisableWebsitePolicy(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmProfileActionAsync("确认解除网站限制",
                _model.WebsitePolicyDisableConfirmationSummary, "确认解除")) return;
        if (!await EnsureTeacherAgentChannelStartedAsync()) return;
        await _model.DisableWebsitePolicyAsync();
    }
    private async void ReadWebsitePolicyStatus(object? sender, RoutedEventArgs e)
    {
        if (await EnsureTeacherAgentChannelStartedAsync())
            await _model.ReadWebsitePolicyStatusAsync();
    }
    private async void ReadStudentDeviceHealth(object? sender, RoutedEventArgs e)
    {
        if (await EnsureTeacherAgentChannelStartedAsync())
            await _model.ReadStudentDeviceHealthAsync();
    }
    private async void PrepareMacVeyonHelper(object? sender, RoutedEventArgs e) =>
        await _model.PrepareMacVeyonHelperAsync();
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
            $"当前名单 {targetCount} 台电脑中，将向 {eligibleCount} 台最近有签名回执且 VeyonService 未运行的电脑发送固定修复请求；其余 {targetCount - eligibleCount} 台跳过。Agent 只会启动已停止的服务，不会停止正在运行的服务。更新事务冲突时会拒绝执行。是否继续？");
        if (confirmed) await _model.EnsureVeyonServiceRunningForSelectedAsync();
    }

    private async void RefreshSelectedLegacyEdgePolicies(object? sender, RoutedEventArgs e)
    {
        var targetCount = _model.DeviceHealthSelectedCount;
        var eligibleCount = _model.DeviceHealthEdgeEligibleCount;
        if (eligibleCount == 0) return;
        var confirmed = await ConfirmHealthRepairAsync("批量排入 Edge 策略刷新",
            $"当前名单 {targetCount} 台电脑中，将向 {eligibleCount} 台处于旧 Edge 注册表模式且具有当前有效策略的电脑排入刷新；其余 {targetCount - eligibleCount} 台跳过。回执只表示请求已排入队列，不代表浏览器已应用。是否继续？");
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
            TeacherPageHost.GetVisualDescendants().OfType<TeacherHealthView>()
                .FirstOrDefault()?.ScrollToRow(row);
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

    private async void ReviewDeviceHealthIdentity(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TeacherDeviceHealthRow row }) return;
        row.SetIdentityReviewStatus("正在重新读取签名身份……");
        var discoveries = await _model.DiscoverStudentAgentIdentityAsync(row.Target);
        var discovery = discoveries.SingleOrDefault();
        // A live answer is only needed to refresh the identity. Pinning itself is a local trust
        // decision about a signature the device already produced, so an offline machine still has an
        // identity that can be trusted - otherwise its alert could never be cleared.
        var candidate = discovery?.Candidate ?? row.CurrentIdentityCandidate;
        if (candidate is null)
        {
            row.SetIdentityReviewStatus((discovery?.Detail is { Length: > 0 } detail ? detail + " " : "") +
                "尚未取得可信任的身份；请先确认学生 Agent 在线并重新读取。");
            return;
        }

        var pinnedFingerprint = _model.GetPinnedStudentAgentFingerprint(row.Target);
        if (pinnedFingerprint is { Length: > 0 } pinned)
        {
            if (string.Equals(pinned, candidate.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                row.SetIdentityReviewStatus("身份与已固定身份一致，无需处理。");
                return;
            }
            candidate = candidate with { PreviouslyPinnedFingerprint = pinned };
        }

        row.SetIdentityCandidate(candidate);
        // Teachers cannot compare fingerprints, and the confirmation was always clicked through.
        // Trust the identity the machine just proved with its own signature and keep the signature
        // requirement; only the human comparison step is gone.
        try
        {
            _model.ConfirmDeviceIdentity(row, candidate);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        {
            row.SetIdentityReviewStatus("身份保存失败：" + exception.Message);
            await ShowStudentAgentTrustErrorAsync(exception.Message);
            return;
        }

        row.SetIdentityReviewStatus("已自动信任并更新该身份，无需再人工核对。");
        if (!await _model.ReadDeviceHealthRowAsync(row))
            row.SetIdentityReviewStatus("身份已更新；Agent 暂未应答，下次签名回执到达后会自动复核。");
    }

    private async void CopyDeviceHealthDiagnostic(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TeacherDeviceHealthRow row })
            await CopyDeviceHealthTextAsync(row, row.DiagnosticsSummary, "诊断信息已复制。");
    }

    private async void CopyDeviceHealthFingerprint(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TeacherDeviceHealthRow row } && row.HasIdentityCandidate)
            await CopyDeviceHealthTextAsync(row, row.FullIdentityFingerprints, "完整指纹已复制。");
    }

    private static void ToggleDeviceHealthFingerprints(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TeacherDeviceHealthRow row })
            row.ShowFullIdentityFingerprints = !row.ShowFullIdentityFingerprints;
    }

    private async Task CopyDeviceHealthTextAsync(TeacherDeviceHealthRow row, string text, string successMessage)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                row.SetCopyStatus("无法访问剪贴板；可选中文本手动复制。");
                return;
            }
            await clipboard.SetTextAsync(text);
            row.SetCopyStatus(successMessage);
        }
        catch (Exception)
        {
            row.SetCopyStatus("复制失败；可选中文本手动复制。");
        }
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
        if (!await EnsureTeacherAgentChannelStartedAsync()) return;
        await _model.PushApplicationPolicyAsync();
    }
    private async void DisableApplicationPolicy(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmProfileActionAsync("确认解除应用限制",
                _model.WebsitePolicyDisableConfirmationSummary, "确认解除")) return;
        if (!await EnsureTeacherAgentChannelStartedAsync()) return;
        await _model.DisableApplicationPolicyAsync();
    }
    private async void ReadApplicationPolicyAudit(object? sender, RoutedEventArgs e)
    {
        if (await EnsureTeacherAgentChannelStartedAsync())
            await _model.ReadApplicationPolicyAuditAsync();
    }
    private async void PushStudentSystemPolicy(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmProfileActionAsync("确认推送学生电脑设置",
                _model.WebsitePolicyDisableConfirmationSummary, "确认推送")) return;
        if (!await EnsureTeacherAgentChannelStartedAsync()) return;
        await _model.PushStudentSystemPolicyAsync();
    }
    private async void DisableStudentSystemPolicy(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmProfileActionAsync("确认解除学生电脑设置",
                _model.WebsitePolicyDisableConfirmationSummary, "确认解除")) return;
        if (!await EnsureTeacherAgentChannelStartedAsync()) return;
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
    private void OpenDocumentation(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Process.Start(new ProcessStartInfo(DocumentationLinkFailureWindow.DocumentationUrl)
            {
                UseShellExecute = true
            }) is null)
                _ = new DocumentationLinkFailureWindow().ShowDialog(this);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                          InvalidOperationException or NotSupportedException or
                                          ArgumentException or System.Security.SecurityException or IOException)
        {
            _ = new DocumentationLinkFailureWindow().ShowDialog(this);
        }
    }
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

    private async void ExportMaintenanceEventReport(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
        {
            _model.ReportMaintenanceEventReportExportFailure();
            return;
        }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出匿名 Maintenance 事件",
                SuggestedFileName = $"veyon-campus-maintenance-events-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON Maintenance 事件") { Patterns = ["*.json"] }],
                ShowOverwritePrompt = true
            });
            if (file is null) return;
            using (file)
            {
                var path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path))
                    _model.ReportMaintenanceEventReportExportFailure();
                else
                    await _model.ExportMaintenanceEventReportAsync(path);
            }
        }
        catch (Exception)
        {
            _model.ReportMaintenanceEventReportExportFailure();
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
    private void RefreshPolicyTrustRotationDraft(object? sender, RoutedEventArgs e) =>
        _model.RefreshPolicyTrustRotationStatus();

    private void CreatePolicyTrustRotationDraft(object? sender, RoutedEventArgs e) =>
        _model.CreatePolicyTrustRotationDraft();

    private async void ActivatePolicyTrustRotationDraft(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmProfileActionAsync("激活策略信任候选根",
                "将把草稿中的候选根固定为当前签名根。请确认学生端已全部收到含轮换授权的配置包并确认新根；旧根保持活动，草稿会被清理。此操作会影响策略签名，不能自动撤销。",
                "确认激活"))
            _model.ActivatePolicyTrustRotationDraft();
    }

    private async void ClearPolicyTrustRotationDraft(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmProfileActionAsync("清理策略根轮换草稿",
                "只删除本机草稿记录，不会更改当前固定信任根或已生成的配置包；候选证书仍保留在证书库中，可重新创建草稿。",
                "确认清理"))
            _model.ClearPolicyTrustRotationDraft();
    }

    private async void TrustStudentAgentIdentities(object? sender, RoutedEventArgs e) =>
        await TrustStudentAgentIdentitiesAsync();

    private async Task<bool> TrustStudentAgentIdentitiesAsync()
    {
        var discoveries = await _model.DiscoverStudentAgentIdentitiesAsync();
        var candidates = discoveries.Where(item => item.Candidate is not null).ToArray();
        if (candidates.Length == 0) return false;
        // No fingerprint comparison dialog: identity changes are trusted automatically. The device
        // still has to prove the key by signing, and the pin records the key that was proven.
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

    private async Task ShowTeacherAgentChannelErrorAsync(string message)
    {
        var dialog = new Window
        {
            Title = "教师端学生连接服务未启动",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = "教师端需要启动安全连接服务，学生电脑才能通过主动连接通道接收策略和更新。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button { Content = "关闭", IsDefault = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                }
            }
        };
        ((Button)((StackPanel)dialog.Content!).Children[2]!).Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async Task<bool> EnsureTeacherAgentChannelStartedAsync()
    {
        if (_mobileControl.IsRunning) return true;
        try
        {
            await _mobileControl.StartForAgentCommandsAsync();
            return true;
        }
        catch (Exception exception)
        {
            await ShowTeacherAgentChannelErrorAsync(exception.Message);
            return false;
        }
    }

    private async void DeployStudentUpdate(object? sender, RoutedEventArgs e)
    {
        if (await EnsureTeacherAgentChannelStartedAsync())
            await _model.DeployStudentUpdateAsync();
    }

    private async void UpdateMaintenanceService(object? sender, RoutedEventArgs e)
    {
        await EnsureTeacherAgentChannelStartedAsync();
        await _model.UpdateMaintenanceServiceAsync();
    }

    private async void ProbeStudentTrustedCampus(object? sender, RoutedEventArgs e)
    {
        // 学生端只认一个校区策略签名根；教师改过校区标识后，先找出学生端实际信任的那个。
        await EnsureTeacherAgentChannelStartedAsync();
        await _model.ProbeStudentTrustedCampusAsync();
    }

    private async void MaintainStudentDevices(object? sender, RoutedEventArgs e)
    {        // 一键静默更新：自动全选当前地点学生电脑、自动核对身份、执行统一维护事务。
        // 旧版学生 Agent 走受信任的 Legacy HTTP 通道，即使新的 WebSocket 监听未启动也继续。
        await EnsureTeacherAgentChannelStartedAsync();
        await _model.OneClickUpdateAllStudentDevicesAsync();
    }

    private async void RetryMaintenanceIssue(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TeacherMaintenanceIssueItem issue }) return;
        await EnsureTeacherAgentChannelStartedAsync();
        await _model.RetryMaintenanceIssueAsync(issue);
    }

    private async void RefreshMaintenanceOverview(object? sender, RoutedEventArgs e) =>
        await _model.RefreshMaintenanceOverviewAsync();

    private async void ApproveMaintenanceIdentityRotation(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PendingMaintenanceIdentityRotationItem item } || !item.CanAct)
            return;
        var rotationMode = await ConfirmMaintenanceIdentityRotationAsync(item);
        if (rotationMode is not { } selectedMode) return;
        await _model.ApproveMaintenanceIdentityRotationAsync(item, selectedMode);
    }

    private async void RejectMaintenanceIdentityRotation(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PendingMaintenanceIdentityRotationItem item } && item.CanAct)
            await _model.RejectMaintenanceIdentityRotationAsync(item);
    }

    private async Task<MaintenanceServiceKeyRotationMode?> ConfirmMaintenanceIdentityRotationAsync(
        PendingMaintenanceIdentityRotationItem item)
    {
        var dialog = new Window
        {
            Title = "核对 Maintenance 服务密钥轮换",
            Width = 720,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var verified = new CheckBox
        {
            Content = "我已根据现场变更记录核对目标设备及完整新旧指纹，并确认批准这次服务身份轮换。",
            IsChecked = false
        };
        var rotationMode = new ComboBox
        {
            ItemsSource = new[]
            {
                "紧急或怀疑泄露：立即撤销旧服务密钥",
                "已计划且旧密钥仍安全：保留 24 小时回退信任"
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 90 };
        cancel.Classes.Add("secondary");
        var approve = new Button { Content = "批准新服务密钥", IsDefault = true, MinWidth = 150, IsEnabled = false };
        approve.Classes.Add("primary");
        verified.IsCheckedChanged += (_, _) => approve.IsEnabled = verified.IsChecked == true;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, approve }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        approve.Click += (_, _) => dialog.Close(true);
        var layout = new StackPanel
        {
            Spacing = 12,
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock
                {
                    Text = $"设备：{item.DisplayName} · {item.Target}\n\n原固定 Maintenance 指纹：\n{item.PreviousFingerprint}\n\nAgent 签名登记的新 Maintenance 指纹：\n{item.CandidateFingerprint}\n\n新指纹已由此设备当前固定的 Agent 身份签名授权。紧急轮换会立即停用旧密钥；24 小时回退仅适用于确认旧密钥仍安全的计划轮换，过期后旧信任会自动清除。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    FontFamily = "Consolas"
                },
                new TextBlock { Text = "轮换方式", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                rotationMode,
                verified,
                actions
            }
        };
        dialog.Content = layout;
        if (!await dialog.ShowDialog<bool>(this)) return null;
        return rotationMode.SelectedIndex == 1
            ? MaintenanceServiceKeyRotationMode.TemporaryRollbackTrust
            : MaintenanceServiceKeyRotationMode.ImmediateRevocation;
    }

    private async void SelectLocalStudentTestInstaller(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择本机测试 StudentSetup 更新包",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("本机测试 StudentSetup")
                    {
                        Patterns = ["VeyonCampus-Student-Setup-*-win-x64.exe"]
                    }
                ]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _model.ReportLocalStudentTestReleaseSelectionError("所选文件没有可访问的本地路径。");
                return;
            }
            await _model.SelectLocalTestStudentSetupInstallerAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            _model.ReportLocalStudentTestReleaseSelectionError(exception.Message);
        }
    }

    private async void SelectLocalTestMaintenanceInstaller(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择本机测试 MaintenanceService 更新包",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("本机测试 MaintenanceService")
                    {
                        Patterns = ["VeyonCampus-Maintenance-Setup-*-win-x64.exe"]
                    }
                ]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _model.ReportLocalStudentTestReleaseSelectionError("所选 MaintenanceService 文件没有可访问的本地路径。");
                return;
            }
            await _model.SelectLocalTestMaintenanceServiceInstallerAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            _model.ReportLocalStudentTestReleaseSelectionError(exception.Message);
        }
    }

    private async void SyncStudentConfiguration(object? sender, RoutedEventArgs e)
    {
        if (await EnsureTeacherAgentChannelStartedAsync())
            await _model.SyncStudentConfigurationAsync();
    }

    private async void ReviewConfigurationIdentity(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StudentConfigurationSyncTargetProgressItem row } ||
            !row.CanReviewIdentity) return;
        row.BeginIdentityReview();
        await ReviewStudentAgentIdentityInlineAsync(row.Target, row.SetIdentityReviewStatus,
            row.CompleteIdentityReview);
    }

    private async void ReviewStudentUpdateIdentity(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StudentApplicationUpdateTargetProgressItem row } ||
            !row.CanReviewIdentity) return;
        row.BeginIdentityReview();
        await ReviewStudentAgentIdentityInlineAsync(row.Target, row.SetIdentityReviewStatus,
            row.CompleteIdentityReview);
    }

    private async Task ReviewStudentAgentIdentityInlineAsync(string target, Action<string> setStatus,
        Action complete)
    {
        var discoveries = await _model.DiscoverStudentAgentIdentityAsync(target);
        var discovery = discoveries.SingleOrDefault();
        if (discovery?.Candidate is not { } candidate)
        {
            setStatus(discovery?.Detail ?? "未取得可验证的 Agent 身份；信任记录没有更改。请检查学生机在线状态和 TCP 39174。" );
            return;
        }

        if (discovery.MatchesPinnedKey)
        {
            _model.ConfirmStudentAgentIdentities(discoveries, approveChangedKeys: false);
            complete();
            return;
        }

        setStatus(candidate.PreviouslyPinnedFingerprint is null
            ? "已收到签名身份，正在自动信任。"
            : "Agent 身份已更新，正在自动信任新身份。");
        // Automatic trust: no fingerprint comparison dialog. The signature is still verified, and
        // the teacher cannot meaningfully compare a 64-character fingerprint anyway.
        try
        {
            _model.ConfirmStudentAgentIdentities(discoveries, approveChangedKeys: true);
            complete();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException)
        {
            setStatus("身份固定失败：" + exception.Message);
            await ShowStudentAgentTrustErrorAsync(exception.Message);
        }
    }

    private async void RecheckStudentUpdateResult(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StudentApplicationUpdateTargetProgressItem row)
            await _model.RecheckStudentUpdateResultAsync(row);
    }
    private async void GeneratePackage(object? sender, RoutedEventArgs e) => await _model.GenerateStudentPackageAsync();
    private void CancelPackageGeneration(object? sender, RoutedEventArgs e) => _model.CancelStudentPackageGeneration();
    private async void ReplaceWebsiteSigningKey(object? sender, RoutedEventArgs e) =>
        await _model.GenerateStudentPackageAsync(replaceUnavailableSigningKey: true);
    private async void PublishStudentPackage(object? sender, RoutedEventArgs e) => await _model.PublishStudentPackageAsync();

    private async void StartLanSharing(object? sender, RoutedEventArgs e) =>
        await _model.LanSharing.StartAsync(_model.PublishPackageDirectory);

    private async void StopLanSharing(object? sender, RoutedEventArgs e) =>
        await _model.LanSharing.StopAsync();

    private async void ScanLanMachines(object? sender, RoutedEventArgs e) =>
        await _model.LanSharing.ScanAsync();
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

    private async void ChooseTrustMigrationSourcePackage(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择原 Windows 教师端生成的学生配置包",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            using var folder = folders[0];
            var path = folder.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _model.ReportStudentPackageGenerationError("所选原配置包没有可访问的本地路径；没有读取或生成文件。");
                return;
            }
            _model.TrustMigrationSourcePackageDirectory = path;
        }
        catch (Exception exception)
        {
            _model.ReportStudentPackageGenerationError("无法选择原学生配置包文件夹：" + exception.Message);
        }
    }

    private async void ChooseMacVeyonPrivateKey(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择从原教师机安全导出的 Veyon 私钥 PEM",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Veyon 私钥 PEM")
                    {
                        Patterns = ["*.pem", "*.key", "*.private"]
                    }
                ]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _model.ReportMacVeyonPrivateKeyImportError("所选文件没有可访问的本地路径；没有读取或导入私钥。");
                return;
            }
            await _model.ImportMacVeyonPrivateKeyAsync(path);
        }
        catch (Exception exception)
        {
            _model.ReportMacVeyonPrivateKeyImportError("无法选择或导入 Veyon 私钥：" + exception.Message);
        }
    }

    private async void ChooseStudentPackageOutputDirectory(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择学生配置包的输出位置",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            using var folder = folders[0];
            var path = folder.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _model.ReportStudentPackageGenerationError("所选输出位置没有可访问的本地路径；没有创建文件。");
                return;
            }
            _model.RoomOutputDir = Path.Combine(path, "Veyon-学生配置包");
        }
        catch (Exception exception)
        {
            _model.ReportStudentPackageGenerationError("无法选择学生配置包输出位置：" + exception.Message);
        }
    }
}
