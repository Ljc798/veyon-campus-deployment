using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class TeacherViewModel : INotifyPropertyChanged
{
    private const string DefaultCampusNamePrefix = "智学前程-";
    private readonly VeyonInstallerStore _installerStore;
    private readonly TeacherCampusDirectoryStore _campusDirectoryStore;
    private readonly ClassroomSessionStore _classroomSessionStore;
    private readonly ClassroomSeatLayoutStore _classroomSeatLayoutStore;
    private readonly ClassroomCountdownStore _classroomCountdownStore;
    private readonly ClassroomModeStateStore _classroomModeStateStore;
    private readonly ClassroomSigningContextStore _classroomSigningContextStore;
    private readonly DeploymentPackagePublishingClient _packagePublisher;
    private readonly UpdateDiagnosticsStore _updateDiagnostics;
    private readonly CampusOperationsTelemetryStore _operationsTelemetryStore;
    private readonly ApplicationReleaseClient? _releaseClient;
    private readonly string? _releaseClientError;
    private readonly TeacherCampusHeartbeatClient? _teacherHeartbeatClient;
    private readonly string? _teacherHeartbeatClientError;
    private readonly CampusOperationsTelemetryClient? _operationsTelemetryClient;
    private readonly string? _operationsTelemetryClientError;
    private readonly ITaskLease _lease;
    private readonly object _releaseNoticeGate = new();
    private readonly object _operationsTelemetryGate = new();
    private readonly SemaphoreSlim _classroomStatusPushGate = new(1, 1);
    private IReadOnlyList<string> _roomNames = Array.Empty<string>();
    private IReadOnlyList<string> _roomPreviewRows = Array.Empty<string>();
    private IReadOnlyList<VeyonNetworkLocation> _websiteLocations = Array.Empty<VeyonNetworkLocation>();
    private IReadOnlyList<ApplicationInventoryChoice> _applicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
    private IReadOnlyList<ApplicationInventoryChoice> _allApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
    private IReadOnlyList<string> _lastFailedWebsiteTargets = Array.Empty<string>();
    private bool _isExecuting, _isReadingWebsiteLocations, _websiteLocationSelectionPending, _showWebsitePolicyResultDetails;
    private bool _applicationEnforcementReviewed, _showApplicationPolicyResultDetails;
    private bool _studentSystemPolicyLockWallpaper = true, _studentSystemPolicyProhibitTimeChanges = true,
        _studentSystemPolicyProhibitNetworkChanges = true, _studentSystemPolicyProhibitSoftwareInstallation = true,
        _studentSystemPolicyProhibitAccountManagement = true, _studentSystemPolicyProhibitControlPanel,
        _studentSystemPolicySoftwareInstallReviewed;
    private bool _hasMatchingApplicationAudit;
    private long? _lastApplicationAuditPolicyRevision;
    private string? _lastApplicationAuditFingerprint;
    private bool _canReplaceWebsiteSigningKey, _isGeneratingStudentPackage;
    private bool _isBuildingStudentPackage;
    private CancellationTokenSource? _studentPackageBuildCancellation;
    private bool _isCheckingRoomConflicts, _roomConflictCheckCompleted;
    private bool _recommendInstallVeyon = true, _recommendRenameComputer, _recommendCreateStudentAccount,
        _recommendChangeAdminPassword;
    private int _roomPlanRevision, _roomConflictCheckRevision = -1, _roomPlannedNewComputerCount;
    private string _roomPrefix = "PC-", _roomStart = "1", _roomCount = "150", _roomError = "";
    private string _roomLocationName = "", _studentRoster = "", _roomComputerHosts = "";
    private string _roomCreateResult = "", _roomCreateError = "", _roomCreateStatus = "", _roomConflictStatus = "", _roomConflictResults = "", _roomSkippedResults = "";
    private string _configuratorLaunchError = "";
    private string _campusId = DefaultCampusNamePrefix, _roomOutputDir = "", _packageOutput = "", _packageOutputError = "";
    private string _campusProfileName = "", _roomProfileName = "", _roomProfilePrefix = "PC-", _roomProfileStart = "1", _roomProfileCount = "150", _roomProfileHostOverrides = "";
    private string _campusDirectoryStatus = "", _campusDirectoryError = "";
    private IReadOnlyList<TeacherCampusProfile> _campusProfiles = Array.Empty<TeacherCampusProfile>();
    private IReadOnlyList<TeacherRoomProfile> _roomProfiles = Array.Empty<TeacherRoomProfile>();
    private IReadOnlyList<TeacherSeatCell> _seatMapCells = Array.Empty<TeacherSeatCell>();
    private IReadOnlyList<string> _seatMapTargets = Array.Empty<string>();
    private ClassroomSeatLayout? _seatLayout;
    private string _seatMapStatus = "";
    private int? _selectedSeatIndex;
    private decimal _seatColumns = 1;
    private bool _isLoadingSeatLayout;
    private TeacherCampusProfile? _selectedCampusProfile;
    private TeacherRoomProfile? _selectedRoomProfile;
    private ClassroomSession? _activeClassroomSession;
    private ClassroomCountdown? _activeClassroomCountdown;
    private decimal _classroomCountdownMinutes = 15;
    private string _classroomCountdownStatus = "";
    private ClassroomMode _classroomMode = ClassroomMode.Normal;
    private bool _isClassroomTransitioning;
    private int _classroomStatusRefreshInFlight;
    private string _classroomDeliveryStatus = "课堂状态尚未同步。";
    private string _classroomModeStatus = "";
    private IReadOnlyList<ClassroomPendingRestore> _pendingClassroomRestores = Array.Empty<ClassroomPendingRestore>();
    private string _classroomRestoreStatus = "";
    private bool _isRetryingClassroomRestores;
    private string _classroomEventStatus = "课堂求助通道尚未启动。";
    private string _classroomNoticeDraft = "";
    private string _classroomNoticeStatus = "";
    private Guid? _classroomEventFeedSessionId;
    private readonly Dictionary<Guid, TeacherClassroomEventItem> _classroomHelpRows = [];
    private readonly HashSet<Guid> _classroomEventIds = [];
    private readonly Queue<Guid> _classroomEventOrder = new();
    private string _publishPackageDirectory = "", _publisherName = "", _teacherPhoneLast4 = "";
    private string _packagePublisherStatus = "", _packagePublisherError = "", _packagePublishResult = "";
    private string _websiteTargets = "", _websiteDomains = "", _websitePolicyResult = "", _websitePolicyResultDetails = "", _websitePolicyError = "", _websitePolicyHistoryText = "";
    private string _applicationStudentSids = "", _applicationRules = "", _applicationPolicyResult = "", _applicationPolicyResultDetails = "", _applicationPolicyError = "", _applicationPolicyHistoryText = "", _applicationAuditResult = "";
    private string _studentSystemPolicyResult = "", _studentSystemPolicyDetails = "", _studentSystemPolicyError = "";
    private string _applicationInventorySearch = "", _applicationInventoryStatus = "";
    private string _websiteDirectoryStatus = "", _websiteDirectoryError = "";
    private string _installerStatus = "Veyon 安装器已内嵌在 App 中；无需联网下载。", _teacherInstallResult = "", _teacherInstallIssue = "";
    private string _teacherUpdateStatus = "尚未检查教师控制台更新。";
    private string _offlineTeacherUpdateStatus = "无网络时可选择安装器和配套 .release.json 清单；本机固定公钥会验证签名与 SHA-256。";
    private string _studentUpdateStatus = "尚未向学生电脑发送更新。";
    private string _updateDiagnosticsStatus = "更新诊断只保存在本机；需要时手动导出，不会自动上传。";
    private string _operationsTelemetryStatus = "匿名运维汇总已关闭；本机数据不会上传。";
    private bool _isOperationsTelemetryEnabled;
    private int _operationsTelemetryInFlight;
    private CancellationTokenSource? _operationsTelemetryCancellation;
    private string _packageGenerationStatus = "";
    private string _teacherHeartbeatStatus = "默认开启；发布校区配置包后发送每日汇总。";
    private TeacherCampusHeartbeatState? _teacherHeartbeatState;
    private string? _pendingReleaseNotice;
    private ApplicationReleaseEnvelope? _teacherUpdateRelease;
    private ApplicationReleaseEnvelope? _offlineTeacherUpdateRelease;
    private string? _offlineTeacherInstallerPath;
    private bool _teacherUpdateAvailable;
    private bool _isCheckingTeacherUpdate, _isDownloadingTeacherUpdate, _isVerifyingOfflineTeacherUpdate;
    private int _teacherHeartbeatInFlight;
    private int _initialTeacherHeartbeatWaitScheduled;
    private int _websiteModeIndex = 0, _websiteDurationIndex = 1, _websiteLocationIndex = -1;
    private int _applicationPolicyModeIndex, _applicationPolicyDurationIndex = 1;
    private string _selectedPage = "classroom";

    public TeacherViewModel(VeyonInstallerStore? installerStore = null,
        TeacherCampusDirectoryStore? campusDirectoryStore = null,
        UpdateDiagnosticsStore? updateDiagnostics = null,
        ClassroomSessionStore? classroomSessionStore = null,
        ClassroomModeStateStore? classroomModeStateStore = null,
        CampusOperationsTelemetryStore? operationsTelemetryStore = null,
        ClassroomSeatLayoutStore? classroomSeatLayoutStore = null,
        ClassroomCountdownStore? classroomCountdownStore = null)
    {
        _installerStore = installerStore ?? new VeyonInstallerStore();
        _campusDirectoryStore = campusDirectoryStore ?? new TeacherCampusDirectoryStore();
        _classroomSessionStore = classroomSessionStore ?? new ClassroomSessionStore();
        _classroomSeatLayoutStore = classroomSeatLayoutStore ?? new ClassroomSeatLayoutStore();
        _classroomCountdownStore = classroomCountdownStore ?? new ClassroomCountdownStore();
        _classroomModeStateStore = classroomModeStateStore ?? new ClassroomModeStateStore();
        _classroomSigningContextStore = new ClassroomSigningContextStore();
        _updateDiagnostics = updateDiagnostics ?? new UpdateDiagnosticsStore();
        _operationsTelemetryStore = operationsTelemetryStore ?? new CampusOperationsTelemetryStore();
        LoadCampusDirectory();
        _packagePublisher = new DeploymentPackagePublishingClient();
        try { _releaseClient = new ApplicationReleaseClient(); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            var failure = UpdateDiagnosticCatalog.Classify(exception);
            _releaseClientError = failure.ToUserMessage();
            _teacherUpdateStatus = "教师端更新不可用：" + failure.ToUserMessage();
            _studentUpdateStatus = failure.ToUserMessage();
        }
        try { _teacherHeartbeatClient = new TeacherCampusHeartbeatClient(); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            _teacherHeartbeatClientError = exception.Message;
        }
        try { _operationsTelemetryClient = new CampusOperationsTelemetryClient(); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            _operationsTelemetryClientError = exception.Message;
        }
        try
        {
            var preference = _operationsTelemetryStore.LoadPreference();
            _isOperationsTelemetryEnabled = preference.Enabled;
            _operationsTelemetryStatus = preference.Enabled
                ? "已开启；只在本机汇总更新结果和课堂次数，离线时稍后重试。"
                : "匿名运维汇总已关闭；本机数据不会上传。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _isOperationsTelemetryEnabled = false;
            _operationsTelemetryStatus = "无法读取上报设置；当前按关闭处理。";
        }
        if (File.Exists(TeacherCampusHeartbeatStateStore.DefaultPath))
        {
            try
            {
                _teacherHeartbeatState = TeacherCampusHeartbeatStateStore.LoadOrCreate();
                _teacherHeartbeatStatus = !_teacherHeartbeatState.Enabled
                    ? "已关闭校区每日汇总。"
                    : _teacherHeartbeatState.PackageId is null
                        ? "已开启；发布校区配置包后发送每日汇总。"
                        : _teacherHeartbeatState.FirstHeartbeatNotBeforeUtc is { } firstHeartbeatAt &&
                          firstHeartbeatAt > DateTimeOffset.UtcNow
                            ? "首次校区心跳已安排，将在配置包发布 1 小时后发送。"
                            : TeacherCampusHeartbeatStateStore.IsDue(_teacherHeartbeatState,
                                TeacherCampusHeartbeatStateStore.GetHongKongDate())
                            ? "已开启；正在检查今日心跳发送状态。"
                            : "今日校区心跳已成功发送；本机按 UTC+8 跳过重复请求。";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _teacherHeartbeatStatus = "无法读取本机 Teacher 心跳设置：" + exception.Message;
            }
        }
        _lease = OperatingSystem.IsWindows() ? new NamedPipeTaskLease() : new TaskLease();
        LoadActiveClassroomSession();
        RefreshClassroomRestoreLedger();
        try { ReadWebsiteSigningCampuses(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          CryptographicException)
        {
            ClassroomDeliveryStatus = "无法读取校区签名记录；课堂状态推送已停用。";
        }
        if (WebsiteSigningCampuses.Count == 0 && ClassroomDeliveryStatus == "课堂状态尚未同步。")
            ClassroomDeliveryStatus = "先生成校区配置包，建立学生端已信任的签名密钥。";
        RestoreActiveClassroomSigningContext();
        LoadLatestWebsitePolicyHistory();
        LoadLatestApplicationPolicyHistory();
        if (OperatingSystem.IsWindows() && _teacherHeartbeatState is { Enabled: true, PackageId: not null } state)
        {
            if (state.FirstHeartbeatNotBeforeUtc is { } notBefore && notBefore > DateTimeOffset.UtcNow)
                ScheduleInitialTeacherHeartbeat(notBefore);
            else
                _ = SendTeacherCampusHeartbeatAsync();
        }
        if (OperatingSystem.IsWindows() && _isOperationsTelemetryEnabled)
            _ = SendCampusOperationsReportsAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ReleaseNoticeAvailable;

    public string? TakePendingReleaseNotice()
    {
        lock (_releaseNoticeGate)
        {
            var notice = _pendingReleaseNotice;
            _pendingReleaseNotice = null;
            return notice;
        }
    }

    public bool IsExecuting { get => _isExecuting; private set { _isExecuting = value; Changed(); Changed(nameof(CanInstallTeacherVeyon)); Changed(nameof(CanGenerateStudentPackage)); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(CanDisableWebsitePolicy)); Changed(nameof(CanPushApplicationPolicy)); Changed(nameof(CanDisableApplicationPolicy)); Changed(nameof(CanPushStudentSystemPolicy)); Changed(nameof(CanDisableStudentSystemPolicy)); Changed(nameof(CanReadApplicationPolicyAudit)); Changed(nameof(CanReadApplicationInventory)); Changed(nameof(CanAddSelectedApplicationRules)); Changed(nameof(CanFillFailedWebsiteTargets)); Changed(nameof(CanReadWebsiteLocations)); Changed(nameof(CanApplyWebsiteLocation)); Changed(nameof(CanReplaceWebsiteSigningKey)); Changed(nameof(CanAddRoomToVeyon)); Changed(nameof(CanCheckRoomConflicts)); Changed(nameof(CanPublishStudentPackage)); Changed(nameof(CanCheckTeacherUpdate)); Changed(nameof(CanDownloadTeacherUpdate)); Changed(nameof(CanExportOfflineTeacherUpdate)); Changed(nameof(CanVerifyOfflineTeacherUpdate)); Changed(nameof(CanInstallOfflineTeacherUpdate)); Changed(nameof(CanTrustStudentAgentIdentities)); Changed(nameof(CanDeployStudentUpdate)); Changed(nameof(CanToggleClassroomSession)); Changed(nameof(CanChangeClassroomMode)); Changed(nameof(CanRetryClassroomRestores)); } }
    private int _classroomPolicyIndex;
    private bool _areClassroomTargetsExpanded = true;
    public bool AreClassroomTargetsExpanded
    {
        get => _areClassroomTargetsExpanded;
        set { _areClassroomTargetsExpanded = value; Changed(); }
    }
    public bool IsWebsitePolicyPage { get => _classroomPolicyIndex == 0; set { if (value) SelectClassroomPolicy(0); } }
    public bool IsApplicationPolicyPage { get => _classroomPolicyIndex == 1; set { if (value) SelectClassroomPolicy(1); } }
    public bool IsSystemPolicyPage { get => _classroomPolicyIndex == 2; set { if (value) SelectClassroomPolicy(2); } }
    public string ClassroomTargetSummary => $"策略目标 · { (string.IsNullOrWhiteSpace(CampusId) ? "未选择校区" : CampusId.Trim()) } · " +
        WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Count(s => !string.IsNullOrWhiteSpace(s)) + " 台电脑";
    public bool HasActiveClassroomSession => _activeClassroomSession is not null;
    public string ClassroomSessionActionText => HasActiveClassroomSession ? "下课" : "开始课堂";
    public string ClassroomSessionSummary => _activeClassroomSession is { } active
        ? $"{active.Room.RoomName} · {active.Targets.Length} 台电脑 · 课堂进行中"
        : SelectedRoomProfile is { } room
            ? $"{room.DisplayName} · {room.ComputerCount} 台电脑 · 尚未开始"
            : "请先在地点与学生名单中保存机房档案。";
    public ClassroomMode CurrentClassroomMode => _classroomMode;
    public string ClassroomModeLabel => _classroomMode == ClassroomMode.Practice ? "练习模式" : "正常课堂";
    public string ClassroomModeActionText => _classroomMode == ClassroomMode.Practice ? "恢复正常" : "开始练习";
    public decimal ClassroomCountdownMinutes
    {
        get => _classroomCountdownMinutes;
        set
        {
            var next = Math.Clamp(decimal.Round(value, 0, MidpointRounding.AwayFromZero),
                ClassroomCountdown.MinimumMinutes, ClassroomCountdown.MaximumMinutes);
            if (_classroomCountdownMinutes == next) return;
            _classroomCountdownMinutes = next;
            Changed();
        }
    }
    public bool HasClassroomCountdown => _activeClassroomCountdown is not null;
    public bool IsClassroomCountdownRunning => _activeClassroomCountdown is { } countdown &&
        countdown.Remaining(DateTimeOffset.UtcNow) > TimeSpan.Zero;
    public bool CanStartClassroomCountdown => HasActiveClassroomSession && !HasClassroomCountdown &&
                                             !HasClassroomCountdownError;
    public bool CanEndClassroomCountdown => HasActiveClassroomSession && HasClassroomCountdown;
    public string ClassroomCountdownText
    {
        get
        {
            if (_activeClassroomCountdown is not { } countdown) return "";
            var seconds = (int)Math.Ceiling(countdown.Remaining(DateTimeOffset.UtcNow).TotalSeconds);
            if (seconds <= 0) return "时间到";
            var remaining = TimeSpan.FromSeconds(seconds);
            return remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
        }
    }
    public string ClassroomCountdownStatus
    {
        get => _classroomCountdownStatus;
        private set
        {
            if (_classroomCountdownStatus == value) return;
            _classroomCountdownStatus = value;
            Changed();
            Changed(nameof(HasClassroomCountdownStatus));
            Changed(nameof(HasClassroomCountdownError));
            Changed(nameof(HasClassroomCountdownInfo));
            Changed(nameof(CanStartClassroomCountdown));
        }
    }
    public bool HasClassroomCountdownStatus => !string.IsNullOrWhiteSpace(ClassroomCountdownStatus);
    public bool HasClassroomCountdownError => ClassroomCountdownStatus.StartsWith("无法", StringComparison.Ordinal) ||
                                              ClassroomCountdownStatus.StartsWith("课堂倒计时无法", StringComparison.Ordinal);
    public bool HasClassroomCountdownInfo => HasClassroomCountdownStatus && !HasClassroomCountdownError;
    public bool CanChangeClassroomMode => HasActiveClassroomSession && !_isClassroomTransitioning &&
        !_isRetryingClassroomRestores && !IsExecuting;
    public string ClassroomModeStatus
    {
        get => _classroomModeStatus;
        private set
        {
            if (_classroomModeStatus == value) return;
            _classroomModeStatus = value;
            Changed();
            Changed(nameof(HasClassroomModeStatus));
        }
    }
    public bool HasClassroomModeStatus => !string.IsNullOrWhiteSpace(_classroomModeStatus);
    public bool HasClassroomRestores => _pendingClassroomRestores.Count > 0 ||
                                        !string.IsNullOrWhiteSpace(_classroomRestoreStatus);
    public bool HasPendingClassroomRestoreItems => _pendingClassroomRestores.Count > 0;
    public int PendingClassroomRestoreCount => _pendingClassroomRestores.Count;
    public string ClassroomRestoreSummary
    {
        get
        {
            var lines = _pendingClassroomRestores.Take(24).Select(item =>
                $"{item.RoomName} · {item.DeviceLabel} · {(item.Kind == ClassroomPolicyKind.Website ? "网站" : "应用")}")
                .ToList();
            if (_pendingClassroomRestores.Count > lines.Count)
                lines.Add($"还有 {_pendingClassroomRestores.Count - lines.Count} 项");
            var summary = _pendingClassroomRestores.Count > 0
                ? $"{_pendingClassroomRestores.Count} 项课堂策略待恢复。系统只会重试仍由原课堂拥有且能在当前机房唯一匹配的电脑。\n" +
                  string.Join(Environment.NewLine, lines)
                : "";
            return string.Join(Environment.NewLine,
                new[] { summary, _classroomRestoreStatus }.Where(item => !string.IsNullOrWhiteSpace(item)));
        }
    }
    public bool CanRetryClassroomRestores => OperatingSystem.IsWindows() && PendingClassroomRestoreCount > 0 &&
        !HasActiveClassroomSession && !_isRetryingClassroomRestores && !_isClassroomTransitioning && !IsExecuting;
    public string ClassroomDeliveryStatus
    {
        get => _classroomDeliveryStatus;
        private set { if (_classroomDeliveryStatus == value) return; _classroomDeliveryStatus = value; Changed(); }
    }
    public string ClassroomEventStatus
    {
        get => _classroomEventStatus;
        private set { if (_classroomEventStatus == value) return; _classroomEventStatus = value; Changed(); }
    }
    public string ClassroomNoticeDraft
    {
        get => _classroomNoticeDraft;
        set
        {
            if (_classroomNoticeDraft == value) return;
            _classroomNoticeDraft = value ?? "";
            Changed();
            Changed(nameof(CanSendClassroomNotice));
            ClassroomNoticeStatus = "";
        }
    }
    public bool CanSendClassroomNotice => HasActiveClassroomSession && !_isClassroomTransitioning &&
        !string.IsNullOrWhiteSpace(_classroomNoticeDraft) && _classroomNoticeDraft.Trim().Length <=
        ClassroomEventCryptography.MaximumMessageCharacters &&
        !_classroomNoticeDraft.Any(character => char.IsControl(character) &&
            character is not '\r' and not '\n' and not '\t');
    public string ClassroomNoticeStatus
    {
        get => _classroomNoticeStatus;
        private set
        {
            if (_classroomNoticeStatus == value) return;
            _classroomNoticeStatus = value;
            Changed();
            Changed(nameof(HasClassroomNoticeStatus));
        }
    }
    public bool HasClassroomNoticeStatus => !string.IsNullOrWhiteSpace(_classroomNoticeStatus);
    public ObservableCollection<TeacherClassroomEventItem> ClassroomEventItems { get; } = [];
    public bool HasClassroomEventItems => ClassroomEventItems.Count > 0;
    public int PendingClassroomHelpCount => ClassroomEventItems.Count(item => item.IsHelpRequest && item.CanReply);
    public string ClassroomNavigationLabel => PendingClassroomHelpCount is > 0
        ? $"课堂控制 · {PendingClassroomHelpCount} 个待回复"
        : "课堂控制";
    internal TeacherClassroomEventContext? GetActiveClassroomEventContext()
    {
        if (_activeClassroomSession is not { } active) return null;
        var targets = ResolveClassroomNetworkTargets(active).ToArray();
        if (targets.Length != active.Targets.Length)
            throw new InvalidDataException("课堂目标与签名设备地址数量不匹配。");
        var targetIds = active.Targets.Select((target, index) => (targets[index], target.TargetId))
            .ToDictionary(item => item.Item1, item => item.TargetId, StringComparer.OrdinalIgnoreCase);
        return new TeacherClassroomEventContext(ResolveClassroomSigningCampus(active), active.SessionId,
            Array.AsReadOnly(targets), targetIds, _classroomMode, active);
    }
    internal void SetClassroomEventStatus(string status) => ClassroomEventStatus = status;
    internal void SetClassroomModeStatus(string status) => ClassroomModeStatus = status;
    public void StartClassroomCountdown()
    {
        if (!CanStartClassroomCountdown || _activeClassroomSession is not { } active) return;
        try
        {
            var duration = decimal.ToInt32(ClassroomCountdownMinutes);
            _activeClassroomCountdown = _classroomCountdownStore.Start(active.SessionId, duration,
                DateTimeOffset.UtcNow);
            ClassroomCountdownStatus = "课堂倒计时已启动。";
            RefreshClassroomCountdownDisplay();
            Changed(nameof(HasClassroomCountdown));
            Changed(nameof(IsClassroomCountdownRunning));
            Changed(nameof(CanStartClassroomCountdown));
            Changed(nameof(CanEndClassroomCountdown));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClassroomCountdownStatus = "课堂倒计时无法保存；原文件已保留。";
        }
    }

    public void EndClassroomCountdown()
    {
        if (!CanEndClassroomCountdown || _activeClassroomSession is not { } active) return;
        try
        {
            _ = _classroomCountdownStore.End(active.SessionId);
            _activeClassroomCountdown = null;
            ClassroomCountdownStatus = "已结束倒计时。";
            RefreshClassroomCountdownDisplay();
            Changed(nameof(HasClassroomCountdown));
            Changed(nameof(IsClassroomCountdownRunning));
            Changed(nameof(CanStartClassroomCountdown));
            Changed(nameof(CanEndClassroomCountdown));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClassroomCountdownStatus = "课堂倒计时无法结束；原文件已保留。";
        }
    }

    public void RefreshClassroomCountdownDisplay()
    {
        Changed(nameof(ClassroomCountdownText));
        Changed(nameof(IsClassroomCountdownRunning));
    }

    internal void SetClassroomRestoreRetrying(bool value)
    {
        if (_isRetryingClassroomRestores == value) return;
        _isRetryingClassroomRestores = value;
        Changed(nameof(CanRetryClassroomRestores));
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanChangeClassroomMode));
    }

    internal void RefreshClassroomRestoreLedger(string? status = null)
    {
        try
        {
            _pendingClassroomRestores = ClassroomRestoreLedger.ListPending(_classroomModeStateStore.ReadAll(),
                _classroomSessionStore.ReadRecent());
            if (status is not null) _classroomRestoreStatus = status;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _classroomRestoreStatus = "无法读取本机待恢复记录；原记录已保留。";
        }
        Changed(nameof(HasClassroomRestores));
        Changed(nameof(HasPendingClassroomRestoreItems));
        Changed(nameof(PendingClassroomRestoreCount));
        Changed(nameof(ClassroomRestoreSummary));
        Changed(nameof(CanRetryClassroomRestores));
    }
    internal void SetClassroomNoticeStatus(string status) => ClassroomNoticeStatus = status;
    internal void ResetClassroomEventFeed(Guid? sessionId)
    {
        if (_classroomEventFeedSessionId == sessionId) return;
        _classroomEventFeedSessionId = sessionId;
        ClassroomNoticeDraft = "";
        ClassroomNoticeStatus = "";
        ClassroomEventItems.Clear();
        _classroomHelpRows.Clear();
        _classroomEventIds.Clear();
        _classroomEventOrder.Clear();
        Changed(nameof(HasClassroomEventItems));
        Changed(nameof(PendingClassroomHelpCount));
        Changed(nameof(ClassroomNavigationLabel));
    }

    internal void ApplyClassroomEvents(Guid sessionId, IEnumerable<ClassroomEvent> events)
    {
        ResetClassroomEventFeed(sessionId);
        var seatLabels = ReadActiveSeatLabels(sessionId);
        foreach (var item in ClassroomEventItems.Where(item => item.IsHelpRequest))
            item.SetSeatLocation(seatLabels.GetValueOrDefault(item.Event.Target));
        foreach (var classroomEvent in events)
        {
            if (!_classroomEventIds.Add(classroomEvent.EventId)) continue;
            _classroomEventOrder.Enqueue(classroomEvent.EventId);
            if (classroomEvent.Type == ClassroomEventType.HelpRequested &&
                classroomEvent.Sender == ClassroomEventSender.Student)
            {
                var row = new TeacherClassroomEventItem(classroomEvent,
                    seatLabels.GetValueOrDefault(classroomEvent.Target));
                _classroomHelpRows[classroomEvent.EventId] = row;
                ClassroomEventItems.Insert(0, row);
            }
            else if (classroomEvent.Type == ClassroomEventType.TeacherReply &&
                     classroomEvent.Sender == ClassroomEventSender.Teacher &&
                     classroomEvent.CorrelationId is { } helpEventId &&
                     _classroomHelpRows.TryGetValue(helpEventId, out var helpRow))
            {
                helpRow.MarkReply(classroomEvent.Message ?? "");
            }
            else if (classroomEvent.Type == ClassroomEventType.HelpAcknowledged &&
                     classroomEvent.CorrelationId is { } acknowledgedHelpId &&
                     _classroomHelpRows.TryGetValue(acknowledgedHelpId, out var acknowledgedRow))
            {
                acknowledgedRow.MarkAcknowledged();
            }
            else if (classroomEvent.Type == ClassroomEventType.HelpResolved &&
                     classroomEvent.Sender == ClassroomEventSender.Student &&
                     classroomEvent.CorrelationId is { } resolvedHelpId &&
                     _classroomHelpRows.TryGetValue(resolvedHelpId, out var resolvedRow))
            {
                resolvedRow.MarkResolved();
            }
            else if (classroomEvent.Type == ClassroomEventType.ClassroomNotice)
            {
                ClassroomEventItems.Insert(0, new TeacherClassroomEventItem(classroomEvent));
            }

            while (_classroomEventOrder.Count > ClassroomEventBuffer.MaximumEventsPerSession)
            {
                var expiredEventId = _classroomEventOrder.Dequeue();
                _classroomEventIds.Remove(expiredEventId);
                if (_classroomHelpRows.Remove(expiredEventId, out var expiredRow))
                    ClassroomEventItems.Remove(expiredRow);
            }
        }
        foreach (var row in ClassroomEventItems) row.RefreshExpiry();
        Changed(nameof(HasClassroomEventItems));
        Changed(nameof(PendingClassroomHelpCount));
        Changed(nameof(ClassroomNavigationLabel));
    }

    internal void MarkClassroomEventReply(Guid helpEventId, string message)
    {
        if (_classroomHelpRows.TryGetValue(helpEventId, out var row))
        {
            row.MarkReply(message);
            Changed(nameof(PendingClassroomHelpCount));
            Changed(nameof(ClassroomNavigationLabel));
        }
    }

    internal void SetClassroomEventReplyError(Guid helpEventId, string message)
    {
        if (_classroomHelpRows.TryGetValue(helpEventId, out var row)) row.SetError(message);
    }
    public bool CanToggleClassroomSession => OperatingSystem.IsWindows() && !IsExecuting &&
        !_isClassroomTransitioning && !_isRetryingClassroomRestores && (HasActiveClassroomSession ||
            SelectedCampusProfile is not null && SelectedRoomProfile is not null &&
            WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal));

    private void SelectClassroomPolicy(int index)
    {
        _classroomPolicyIndex = index;
        if (!string.IsNullOrWhiteSpace(CampusId) && !string.IsNullOrWhiteSpace(WebsiteTargets))
            AreClassroomTargetsExpanded = false;
        Changed(nameof(IsWebsitePolicyPage)); Changed(nameof(IsApplicationPolicyPage)); Changed(nameof(IsSystemPolicyPage));
    }
    public bool IsClassroomPage { get => _selectedPage == "classroom"; set { if (value) SelectPage("classroom"); } }
    public bool IsUpdatesPage { get => _selectedPage == "updates"; set { if (value) SelectPage("updates"); } }
    public bool IsRoomPage { get => _selectedPage == "rooms"; set { if (value) SelectPage("rooms"); } }
    public bool IsSetupPage { get => _selectedPage == "setup"; set { if (value) SelectPage("setup"); } }

    public async Task ToggleClassroomSessionAsync()
    {
        if (!CanToggleClassroomSession) return;
        _isClassroomTransitioning = true;
        var sessionUpdated = false;
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanSendClassroomNotice));
        Changed(nameof(CanRetryClassroomRestores));
        Changed(nameof(ClassroomSessionActionText));
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_activeClassroomSession is { } active)
            {
                var ended = _classroomSessionStore.EndSession(active.SessionId, now)
                            ?? throw new InvalidDataException("本机活动课堂记录已不存在。");
                sessionUpdated = true;
                try { _ = _classroomCountdownStore.End(active.SessionId); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    ClassroomCountdownStatus = "课堂倒计时无法清理；旧状态不会用于新课堂。";
                }
                _activeClassroomCountdown = null;
                _activeClassroomSession = null;
                try
                {
                    var modeState = _classroomModeStateStore.Read(active.SessionId);
                    if (modeState is { OwnedPolicies.Count: > 0 })
                        _classroomModeStateStore.Save(modeState with { Active = false, UpdatedUtc = now });
                    else _classroomModeStateStore.Remove(active.SessionId);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  InvalidDataException)
                {
                    ClassroomModeStatus = "本机策略恢复账本无法更新；原文件已保留，请手动检查。";
                }
                _classroomMode = ClassroomMode.Normal;
                RefreshClassroomSessionProperties();
                ClassroomDeliveryStatus = "已下课；正在通知学生电脑。";
                try { await PublishClassroomStatusAsync(null, ended); }
                finally
                {
                    try { _classroomSigningContextStore.Clear(ended.SessionId); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                      InvalidDataException)
                    {
                        ClassroomDeliveryStatus += " 本机签名上下文清理待处理。";
                    }
                }
            }
            else
            {
                _classroomRestoreStatus = "";
                var campus = SelectedCampusProfile
                             ?? throw new InvalidDataException("请先选择已保存的校区档案。");
                var room = SelectedRoomProfile
                           ?? throw new InvalidDataException("请先选择已保存的机房档案。");
                var started = _classroomSessionStore.StartSession(campus, room.RoomId, now);
                sessionUpdated = true;
                _activeClassroomSession = started;
                LoadActiveClassroomCountdown();
                _classroomMode = ClassroomMode.Normal;
                ClassroomModeStatus = "";
                try
                {
                    _classroomModeStateStore.Save(new ClassroomModeSessionState(
                        ClassroomModeStateStore.CurrentSchemaVersion, started.SessionId,
                        ClassroomMode.Normal, true, now, []));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  InvalidDataException)
                {
                    ClassroomModeStatus = "课堂仍可正常开始；本机恢复记录容量或访问受限，练习模式暂不可用。";
                }
                SaveClassroomSigningContext(started.SessionId, CampusId);
                RefreshClassroomSessionProperties();
                ClassroomDeliveryStatus = "课堂已开始；正在通知学生电脑。";
                await PublishClassroomStatusAsync(started, started);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        {
            ClassroomDeliveryStatus = sessionUpdated
                ? "本机课堂记录已更新，学生电脑状态同步未完成：" + exception.Message
                : "课堂操作未完成，课堂记录没有更改：" + exception.Message;
        }
        finally
        {
            _isClassroomTransitioning = false;
            RefreshClassroomRestoreLedger();
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanChangeClassroomMode));
            Changed(nameof(CanSendClassroomNotice));
            Changed(nameof(ClassroomSessionActionText));
            Changed(nameof(CanRetryClassroomRestores));
        }
    }

    public async Task RefreshActiveClassroomStatusAsync()
    {
        if (!OperatingSystem.IsWindows() ||
            Interlocked.CompareExchange(ref _classroomStatusRefreshInFlight, 1, 0) != 0) return;
        try
        {
            if (_activeClassroomSession is not { } active) return;
            var savedMode = _classroomModeStateStore.Read(active.SessionId)?.Mode ?? ClassroomMode.Normal;
            if (savedMode != _classroomMode)
            {
                _classroomMode = savedMode;
                Changed(nameof(CurrentClassroomMode));
                Changed(nameof(ClassroomModeLabel));
                Changed(nameof(ClassroomModeActionText));
            }
            try { await PublishClassroomStatusAsync(active, active, requireCurrentSession: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or
                                              CryptographicException or PlatformNotSupportedException)
            {
                ClassroomDeliveryStatus = "课堂仍在本机进行；学生状态刷新未完成：" + exception.Message;
            }
        }
        finally { Volatile.Write(ref _classroomStatusRefreshInFlight, 0); }
    }

    private async Task PublishClassroomStatusAsync(ClassroomSession? activeSession, ClassroomSession targetSession,
        bool requireCurrentSession = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("课堂状态推送仅支持 Windows 教师端。");
        await _classroomStatusPushGate.WaitAsync();
        try
        {
            if (requireCurrentSession && _activeClassroomSession?.SessionId != activeSession?.SessionId) return;
            var campusId = ResolveClassroomSigningCampus(targetSession);
            var targets = ResolveClassroomNetworkTargets(targetSession);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campusId);
            ClassroomMode? mode = activeSession is null ? null :
                _classroomModeStateStore.Read(activeSession.SessionId)?.Mode ?? _classroomMode;
            var command = ClassroomStatusCryptography.Create(campusId, activeSession, DateTimeOffset.UtcNow, mode);
            var results = await ClassroomStatusTransport.SendAsync(targets, command, signingKey.PrivateKey);
            var confirmed = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => result.NeedsReview);
            var failed = results.Count - confirmed - needsReview;
            ClassroomDeliveryStatus = $"目标 {results.Count} 台：{confirmed} 台身份已确认，" +
                                      $"{needsReview} 台需核对，{failed} 台未响应。";
        }
        finally { _classroomStatusPushGate.Release(); }
    }

    private string ResolveClassroomSigningCampus(ClassroomSession session)
    {
        ReadWebsiteSigningCampuses();
        var pinnedCampus = _classroomSigningContextStore.Read(session.SessionId);
        if (pinnedCampus is not null)
        {
            if (WebsiteSigningCampuses.Contains(pinnedCampus, StringComparer.Ordinal)) return pinnedCampus;
            throw new InvalidDataException("本堂课绑定的校区签名密钥不可用；课堂记录保留，状态同步已停止。");
        }
        var savedCampus = CampusProfiles.FirstOrDefault(item => item.ProfileId == session.Room.CampusProfileId);
        var profileCampus = savedCampus?.DisplayName ?? session.Room.CampusName;
        var inferredCampus = profileCampus is not null && WebsiteSigningCampuses.Contains(profileCampus, StringComparer.Ordinal)
            ? profileCampus
            : WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal)
                ? CampusId
                : WebsiteSigningCampuses.Count == 1 ? WebsiteSigningCampuses[0] : null;
        if (inferredCampus is not null)
        {
            SaveClassroomSigningContext(session.SessionId, inferredCampus);
            return inferredCampus;
        }
        throw new InvalidDataException(WebsiteSigningCampuses.Count == 0
            ? "找不到已配置的校区签名密钥；请先生成学生校区配置包。"
            : "请在课堂目标设置中选择与学生配置包一致的校区签名密钥。");
    }

    private void RestoreActiveClassroomSigningContext()
    {
        if (_activeClassroomSession is not { } active || !OperatingSystem.IsWindows()) return;
        try
        {
            _ = ResolveClassroomSigningCampus(active);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException)
        {
            ClassroomDeliveryStatus = "本堂课的签名校区无法确认；学生状态同步已停用。";
        }
    }

    private void SaveClassroomSigningContext(Guid sessionId, string campusId) =>
        _classroomSigningContextStore.Save(sessionId, campusId);

    private IReadOnlyList<string> ResolveClassroomNetworkTargets(ClassroomSession session)
    {
        var room = CampusProfiles.FirstOrDefault(item => item.ProfileId == session.Room.CampusProfileId)?
            .Rooms.FirstOrDefault(item => item.RoomId == session.Room.RoomId);
        var targets = session.Targets.Select((target, index) =>
        {
            var host = room?.HostOverrides is { } overrides && index < overrides.Count
                ? overrides[index]
                : null;
            return string.IsNullOrWhiteSpace(host) ? target.DeviceLabel : host.Trim();
        });
        return WebsitePolicyTransport.NormalizeTargets(targets);
    }

    private void LoadActiveClassroomSession()
    {
        try
        {
            _activeClassroomSession = _classroomSessionStore.ReadActive();
            if (_activeClassroomSession is { } active)
            {
                var campus = CampusProfiles.FirstOrDefault(item => item.ProfileId == active.Room.CampusProfileId);
                if (campus is not null)
                {
                    SelectedCampusProfile = campus;
                    SelectedRoomProfile = RoomProfiles.FirstOrDefault(item => item.RoomId == active.Room.RoomId);
                }
            }
            RefreshClassroomSessionProperties();
            if (_activeClassroomSession is not null)
            {
                _classroomMode = _classroomModeStateStore.Read(_activeClassroomSession.SessionId)?.Mode ??
                                 ClassroomMode.Normal;
                ClassroomDeliveryStatus = "已恢复本机活动课堂；正在等待学生电脑状态确认。";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _activeClassroomSession = null;
            ClassroomDeliveryStatus = "本机课堂记录无法读取；原文件已保留。";
        }
        LoadActiveClassroomCountdown();
    }

    private void LoadActiveClassroomCountdown()
    {
        _activeClassroomCountdown = null;
        if (_activeClassroomSession is not { } active)
        {
            ClassroomCountdownStatus = "";
        }
        else
        {
            try
            {
                _activeClassroomCountdown = _classroomCountdownStore.Read(active.SessionId);
                ClassroomCountdownStatus = "";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                ClassroomCountdownStatus = "无法读取本机倒计时；原文件已保留。";
            }
        }
        RefreshClassroomCountdownDisplay();
        Changed(nameof(HasClassroomCountdown));
        Changed(nameof(IsClassroomCountdownRunning));
        Changed(nameof(CanStartClassroomCountdown));
        Changed(nameof(CanEndClassroomCountdown));
    }

    private void RefreshClassroomSessionProperties()
    {
        if (!HasActiveClassroomSession && _classroomNoticeDraft.Length > 0)
            ClassroomNoticeDraft = "";
        Changed(nameof(HasActiveClassroomSession));
        Changed(nameof(ClassroomSessionActionText));
        Changed(nameof(ClassroomSessionSummary));
        Changed(nameof(CurrentClassroomMode));
        Changed(nameof(ClassroomModeLabel));
        Changed(nameof(ClassroomModeActionText));
        Changed(nameof(CanChangeClassroomMode));
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanSendClassroomNotice));
        Changed(nameof(CanRetryClassroomRestores));
        Changed(nameof(HasClassroomCountdown));
        Changed(nameof(ClassroomCountdownText));
        Changed(nameof(IsClassroomCountdownRunning));
        Changed(nameof(CanStartClassroomCountdown));
        Changed(nameof(CanEndClassroomCountdown));
    }
    public string TeacherHeartbeatStatus
    {
        get => _teacherHeartbeatStatus;
        private set
        {
            if (_teacherHeartbeatStatus == value) return;
            _teacherHeartbeatStatus = value;
            Changed();
            Changed(nameof(HasTeacherHeartbeatStatus));
        }
    }
    public bool HasTeacherHeartbeatStatus => _teacherHeartbeatState?.PackageId is not null;
    public string PageTitle => _selectedPage switch
    {
        "rooms" => "地点与学生名单",
        "setup" => "首次设置",
        "updates" => "更新",
        _ => "课堂控制"
    };
    public string PageDescription => _selectedPage switch
    {
        "rooms" => "创建机房地点和电脑名单。",
        "setup" => "首次安装、生成配置包或更换密钥。",
        "updates" => "检查教师端更新，或推送学生端更新。",
        _ => "选择网站、应用或系统策略，共用本次校区和电脑目标。"
    };
    public string AppVersion => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "版本未知";
    public bool CanInstallTeacherVeyon => OperatingSystem.IsWindows() && !IsExecuting;
    public bool CanCheckTeacherUpdate => OperatingSystem.IsWindows() && _releaseClient is not null && !IsExecuting && !_isCheckingTeacherUpdate && !_isDownloadingTeacherUpdate && !_isVerifyingOfflineTeacherUpdate;
    public bool CanDownloadTeacherUpdate => OperatingSystem.IsWindows() && !IsExecuting && !_isCheckingTeacherUpdate &&
        !_isDownloadingTeacherUpdate && !_isVerifyingOfflineTeacherUpdate && _teacherUpdateAvailable &&
        _teacherUpdateRelease is not null && HasRequiredPolicyCapabilities(_teacherUpdateRelease.Manifest);
    public bool CanExportOfflineTeacherUpdate => CanDownloadTeacherUpdate;
    public bool CanVerifyOfflineTeacherUpdate => OperatingSystem.IsWindows() && _releaseClient is not null && !IsExecuting &&
        !_isCheckingTeacherUpdate && !_isDownloadingTeacherUpdate && !_isVerifyingOfflineTeacherUpdate;
    public bool CanInstallOfflineTeacherUpdate => OperatingSystem.IsWindows() && _releaseClient is not null &&
        !IsExecuting && !_isCheckingTeacherUpdate && !_isDownloadingTeacherUpdate && !_isVerifyingOfflineTeacherUpdate &&
        _offlineTeacherUpdateRelease is not null && _offlineTeacherInstallerPath is not null &&
        HasRequiredPolicyCapabilities(_offlineTeacherUpdateRelease.Manifest) &&
        ApplicationReleaseClient.CompareVersions(_offlineTeacherUpdateRelease.Manifest.Version, AppVersion) > 0;
    public bool CanTrustStudentAgentIdentities => OperatingSystem.IsWindows() && !IsExecuting &&
        AreWebsitePolicyTargetsValid() && !string.IsNullOrWhiteSpace(CampusId);
    public bool CanDeployStudentUpdate => OperatingSystem.IsWindows() && !IsExecuting && _releaseClient is not null &&
        AreWebsitePolicyTargetsValid() && !string.IsNullOrWhiteSpace(CampusId);
    private static bool HasRequiredPolicyCapabilities(ApplicationReleaseManifest manifest) =>
        ApplicationReleaseCompatibility.SupportsApplicationPolicy(manifest) &&
        ApplicationReleaseCompatibility.SupportsStudentSystemPolicy(manifest);
    public string TeacherUpdateStatus
    {
        get => _teacherUpdateStatus;
        private set
        {
            if (_teacherUpdateStatus == value) return;
            _teacherUpdateStatus = value;
            Changed();
        }
    }
    public string OfflineTeacherUpdateStatus
    {
        get => _offlineTeacherUpdateStatus;
        private set { if (_offlineTeacherUpdateStatus == value) return; _offlineTeacherUpdateStatus = value; Changed(); }
    }
    public bool HasTeacherUpdate => _teacherUpdateAvailable && _teacherUpdateRelease is not null;
    public string StudentUpdateStatus
    {
        get => _studentUpdateStatus;
        private set { if (_studentUpdateStatus == value) return; _studentUpdateStatus = value; Changed(); }
    }
    public string UpdateDiagnosticsStatus
    {
        get => _updateDiagnosticsStatus;
        private set { if (_updateDiagnosticsStatus == value) return; _updateDiagnosticsStatus = value; Changed(); }
    }
    public bool IsOperationsTelemetryEnabled
    {
        get => _isOperationsTelemetryEnabled;
        set
        {
            if (_isOperationsTelemetryEnabled == value) return;
            if (!OperatingSystem.IsWindows())
            {
                OperationsTelemetryStatus = "匿名运维汇总仅在 Windows Teacher 上运行。";
                Changed();
                return;
            }

            _isOperationsTelemetryEnabled = value;
            Changed();
            try
            {
                var preference = _operationsTelemetryStore.SetEnabled(value);
                _isOperationsTelemetryEnabled = preference.Enabled;
                if (!value)
                {
                    lock (_operationsTelemetryGate)
                        _operationsTelemetryCancellation?.Cancel();
                    OperationsTelemetryStatus = "已关闭；本机待发汇总已删除，不再上传。";
                }
                else if (_operationsTelemetryClient is null)
                {
                    OperationsTelemetryStatus = _operationsTelemetryClientError ?? "上报 API 配置不可用；数据仍留在本机。";
                }
                else if (_teacherHeartbeatState?.PackageId is null)
                {
                    OperationsTelemetryStatus = "已开启；发布校区配置包后开始按日汇总。";
                }
                else
                {
                    OperationsTelemetryStatus = "已开启；只上传每日汇总，离线时会自动重试。";
                    _ = SendCampusOperationsReportsAsync();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _isOperationsTelemetryEnabled = false;
                Changed();
                OperationsTelemetryStatus = "无法保存上报设置；当前按关闭处理。";
            }
        }
    }
    public bool CanUseOperationsTelemetry => OperatingSystem.IsWindows();
    public string OperationsTelemetryStatus
    {
        get => _operationsTelemetryStatus;
        private set { if (_operationsTelemetryStatus == value) return; _operationsTelemetryStatus = value; Changed(); }
    }
    public bool CanGenerateStudentPackage => OperatingSystem.IsWindows() && !IsExecuting;
    public bool CanPublishStudentPackage => OperatingSystem.IsWindows() && !IsExecuting &&
        IsPackagePublisherDetailsValid() && Directory.Exists(PublishPackageDirectory);
    public bool CanPushWebsitePolicy => OperatingSystem.IsWindows() && !IsExecuting && !IsReadingWebsiteLocations &&
        !_websiteLocationSelectionPending && IsWebsitePolicyInputValid();
    public bool CanDisableWebsitePolicy => OperatingSystem.IsWindows() && !IsExecuting && !IsReadingWebsiteLocations &&
        !_websiteLocationSelectionPending && AreWebsitePolicyTargetsValid();
    public bool CanFillFailedWebsiteTargets => !IsExecuting && _lastFailedWebsiteTargets.Count > 0;
    public bool CanPushApplicationPolicy => OperatingSystem.IsWindows() && !IsExecuting &&
        IsApplicationPolicyInputValid() && (ApplicationPolicyModeIndex == 0 ||
            (ApplicationEnforcementReviewed && HasMatchingApplicationAudit));
    public bool CanDisableApplicationPolicy => OperatingSystem.IsWindows() && !IsExecuting && AreWebsitePolicyTargetsValid();
    public bool CanPushStudentSystemPolicy => OperatingSystem.IsWindows() && !IsExecuting &&
        IsStudentSystemPolicyInputValid() &&
        (!_studentSystemPolicyProhibitSoftwareInstallation || _studentSystemPolicySoftwareInstallReviewed);
    public bool CanDisableStudentSystemPolicy => OperatingSystem.IsWindows() && !IsExecuting && AreWebsitePolicyTargetsValid();
    public bool CanReadApplicationPolicyAudit => OperatingSystem.IsWindows() && !IsExecuting && AreWebsitePolicyTargetsValid();
    public bool CanReadApplicationInventory => OperatingSystem.IsWindows() && !IsExecuting && AreWebsitePolicyTargetsValid();
    public bool CanAddSelectedApplicationRules => !IsExecuting && _allApplicationInventoryChoices.Any(item => item.IsSelected && item.CanSelect);
    public bool CanCheckRoomConflicts => OperatingSystem.IsWindows() && !IsExecuting && !IsCheckingRoomConflicts;
    public bool CanAddRoomToVeyon => OperatingSystem.IsWindows() && !IsExecuting && !IsCheckingRoomConflicts &&
        HasRoomPreview && RoomLocationName.Trim().Length > 0 && RoomError.Length == 0 &&
        _roomConflictCheckCompleted && _roomConflictCheckRevision == _roomPlanRevision &&
        _roomConflictResults.Length == 0 && _roomPlannedNewComputerCount > 0;
    public bool CanOpenVeyonConfigurator => OperatingSystem.IsWindows() && !IsExecuting;
    public string ConfiguratorLaunchError
    {
        get => _configuratorLaunchError;
        private set { _configuratorLaunchError = value; Changed(); Changed(nameof(HasConfiguratorLaunchError)); }
    }
    public bool HasConfiguratorLaunchError => ConfiguratorLaunchError.Length > 0;
    public bool CanReadWebsiteLocations => OperatingSystem.IsWindows() && !IsExecuting && !IsReadingWebsiteLocations;
    public bool CanApplyWebsiteLocation => !IsExecuting && !IsReadingWebsiteLocations &&
        _websiteLocationIndex >= 0 && _websiteLocationIndex < WebsiteLocations.Count;
    public string TeacherInstallPlanText =>
        $"离线安装 Veyon {VeyonInstallerTrust.Version} 教师组件（含 Master）；已有安装会停止。";
    public string TeacherInstallSafetyText =>
        "安装完成可能需要重启。";

    public async Task CheckTeacherUpdateAsync()
    {
        if (_releaseClient is null)
        {
            TeacherUpdateStatus = _releaseClientError ?? "此版本没有固定的发布签名公钥，已安全停用更新。";
            return;
        }
        _isCheckingTeacherUpdate = true;
        Changed(nameof(CanCheckTeacherUpdate));
        Changed(nameof(CanDownloadTeacherUpdate));
        Changed(nameof(CanExportOfflineTeacherUpdate));
        Changed(nameof(CanVerifyOfflineTeacherUpdate));
        Changed(nameof(CanInstallOfflineTeacherUpdate));
        TeacherUpdateStatus = "正在检查已签名的教师控制台版本……";
        try
        {
            var result = await _releaseClient.CheckLatestAsync(ApplicationReleaseRole.TeacherConsole, AppVersion);
            _teacherUpdateRelease = result.Release;
            _teacherUpdateAvailable = result.IsNewer && result.Release is not null &&
                                      HasRequiredPolicyCapabilities(result.Release.Manifest);
            TeacherUpdateStatus = result.Release is null
                ? "目前没有已发布的教师控制台版本。"
                : result.IsNewer && !HasRequiredPolicyCapabilities(result.Release.Manifest)
                    ? $"发现新版本 {result.Release.Manifest.Version}，但发布未声明应用与系统策略兼容能力；已拒绝更新。"
                : result.IsNewer
                    ? $"发现新版本 {result.Release.Manifest.Version}；清单签名与目标信息已验证。"
                    : $"当前版本 {AppVersion} 已是最新版本（云端 {result.Release.Manifest.Version}）。";
            RecordUpdateSuccess(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.Check,
                result.Release?.Manifest.Version);
        }
        catch (Exception exception)
        {
            _teacherUpdateRelease = null;
            _teacherUpdateAvailable = false;
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.Check, null, exception);
            TeacherUpdateStatus = "检查更新失败：" + failure.ToUserMessage();
        }
        finally
        {
            _isCheckingTeacherUpdate = false;
            Changed(nameof(CanCheckTeacherUpdate));
            Changed(nameof(CanDownloadTeacherUpdate));
            Changed(nameof(CanExportOfflineTeacherUpdate));
            Changed(nameof(CanVerifyOfflineTeacherUpdate));
            Changed(nameof(CanInstallOfflineTeacherUpdate));
            Changed(nameof(HasTeacherUpdate));
        }
    }

    public async Task VerifyOfflineTeacherUpdateAsync(string installerPath)
    {
        if (!CanVerifyOfflineTeacherUpdate) return;
        _offlineTeacherUpdateRelease = null;
        _offlineTeacherInstallerPath = null;
        Changed(nameof(CanInstallOfflineTeacherUpdate));
        _isVerifyingOfflineTeacherUpdate = true;
        Changed(nameof(CanCheckTeacherUpdate));
        Changed(nameof(CanDownloadTeacherUpdate));
        Changed(nameof(CanExportOfflineTeacherUpdate));
        Changed(nameof(CanVerifyOfflineTeacherUpdate));
        Changed(nameof(CanInstallOfflineTeacherUpdate));
        OfflineTeacherUpdateStatus = "正在使用此版本内嵌的 Developer Release 公钥验证离线安装器……";
        try
        {
            var releaseClient = _releaseClient ?? throw new InvalidOperationException("此版本没有固定的 Developer Release 公钥。");
            var publicKeyPem = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
            var stagingDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "Updates");
            var verified = await Task.Run(() =>
            {
                var stagedPath = ApplicationReleaseClient.StageVerifiedOfflineRelease(installerPath,
                    stagingDirectory, ApplicationReleaseRole.TeacherConsole, releaseClient.ApiBaseAddress, publicKeyPem);
                var stagedRelease = ApplicationReleaseClient.ReadVerifiedStagedRelease(stagedPath,
                    ApplicationReleaseRole.TeacherConsole, releaseClient.ApiBaseAddress, publicKeyPem);
                return (Path: stagedPath, Release: stagedRelease);
            });
            _offlineTeacherInstallerPath = verified.Path;
            _offlineTeacherUpdateRelease = verified.Release;
            var versionComparison = ApplicationReleaseClient.CompareVersions(verified.Release.Manifest.Version, AppVersion);
            OfflineTeacherUpdateStatus = versionComparison > 0 &&
                                         HasRequiredPolicyCapabilities(verified.Release.Manifest)
                ? $"离线验签通过：教师控制台 {verified.Release.Manifest.Version}；大小 {verified.Release.Manifest.SizeBytes:N0} 字节；SHA-256 {verified.Release.Manifest.Sha256}。已安全暂存，可安装并重启。"
                : versionComparison > 0
                    ? $"发布签名有效，但版本 {verified.Release.Manifest.Version} 未声明应用与系统策略兼容能力；已拒绝更新。"
                : $"离线验签通过：版本 {verified.Release.Manifest.Version}，SHA-256 {verified.Release.Manifest.Sha256}；此版本不高于当前 {AppVersion}，不能作为更新安装。";
            RecordUpdateSuccess(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.OfflineVerify,
                verified.Release.Manifest.Version);
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.OfflineVerify, null, exception);
            OfflineTeacherUpdateStatus = "离线安装器验证失败，未启动安装；" + failure.ToUserMessage();
        }
        finally
        {
            _isVerifyingOfflineTeacherUpdate = false;
            Changed(nameof(CanCheckTeacherUpdate));
            Changed(nameof(CanDownloadTeacherUpdate));
            Changed(nameof(CanExportOfflineTeacherUpdate));
            Changed(nameof(CanVerifyOfflineTeacherUpdate));
            Changed(nameof(CanInstallOfflineTeacherUpdate));
        }
    }

    public bool InstallOfflineTeacherUpdate()
    {
        if (!CanInstallOfflineTeacherUpdate || _releaseClient is null || _offlineTeacherInstallerPath is null ||
            _offlineTeacherUpdateRelease is null) return false;
        var targetVersion = _offlineTeacherUpdateRelease.Manifest.Version;
        try
        {
            var publicKeyPem = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
            var release = ApplicationReleaseClient.ReadVerifiedStagedRelease(_offlineTeacherInstallerPath,
                ApplicationReleaseRole.TeacherConsole, _releaseClient.ApiBaseAddress, publicKeyPem);
            if (release != _offlineTeacherUpdateRelease ||
                ApplicationReleaseClient.CompareVersions(release.Manifest.Version, AppVersion) <= 0 ||
                !HasRequiredPolicyCapabilities(release.Manifest))
                throw new InvalidDataException("暂存文件自上次校验后发生变化，或不再是高于当前版本的教师安装器。");
            ApplicationReleaseUpdateHandoff.Start(_offlineTeacherInstallerPath,
                ApplicationReleaseRole.TeacherConsole, AppVersion);
            RecordUpdateHandoffStarted(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.OfflineInstall,
                release.Manifest.Version);
            OfflineTeacherUpdateStatus = $"已再次验签并启动 {release.Manifest.Version} 安装；应用将关闭，安装助手会在失败时尝试恢复旧版本。";
            return true;
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.OfflineInstall, targetVersion, exception);
            _offlineTeacherUpdateRelease = null;
            _offlineTeacherInstallerPath = null;
            OfflineTeacherUpdateStatus = "离线安装未启动；暂存文件复核失败：" + failure.ToUserMessage();
            Changed(nameof(CanInstallOfflineTeacherUpdate));
            return false;
        }
    }

    public async Task ExportOfflineTeacherUpdateAsync(string destinationDirectory)
    {
        if (!CanExportOfflineTeacherUpdate || _releaseClient is null || _teacherUpdateRelease is null) return;
        var targetVersion = _teacherUpdateRelease.Manifest.Version;
        _isDownloadingTeacherUpdate = true;
        Changed(nameof(CanCheckTeacherUpdate));
        Changed(nameof(CanDownloadTeacherUpdate));
        Changed(nameof(CanExportOfflineTeacherUpdate));
        Changed(nameof(CanVerifyOfflineTeacherUpdate));
        Changed(nameof(CanInstallOfflineTeacherUpdate));
        OfflineTeacherUpdateStatus = "正在下载并再次验签，然后复制安装器和签名清单到所选离线介质……";
        try
        {
            var release = _teacherUpdateRelease;
            var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "Updates");
            var downloadedPath = await _releaseClient.DownloadAsync(release,
                ApplicationReleaseRole.TeacherConsole, updateDirectory);
            var publicKeyPem = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
            var exportedPath = await Task.Run(() => ApplicationReleaseClient.StageVerifiedOfflineRelease(downloadedPath,
                destinationDirectory, ApplicationReleaseRole.TeacherConsole, _releaseClient.ApiBaseAddress, publicKeyPem));
            RecordUpdateSuccess(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.OfflineExport,
                release.Manifest.Version);
            OfflineTeacherUpdateStatus = $"离线更新包已验签并导出：{exportedPath}，旁边的 .release.json 文件也必须一并转移。版本 {release.Manifest.Version}，SHA-256 {release.Manifest.Sha256}。";
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.OfflineExport, targetVersion, exception);
            OfflineTeacherUpdateStatus = "离线更新包导出失败；未覆盖目标目录中的现有文件。" + failure.ToUserMessage();
        }
        finally
        {
            _isDownloadingTeacherUpdate = false;
            Changed(nameof(CanCheckTeacherUpdate));
            Changed(nameof(CanDownloadTeacherUpdate));
            Changed(nameof(CanExportOfflineTeacherUpdate));
            Changed(nameof(CanVerifyOfflineTeacherUpdate));
            Changed(nameof(CanInstallOfflineTeacherUpdate));
        }
    }

    public async Task<bool> DownloadTeacherUpdateAsync()
    {
        if (_releaseClient is null || !_teacherUpdateAvailable || _teacherUpdateRelease is null) return false;
        var release = _teacherUpdateRelease;
        _isDownloadingTeacherUpdate = true;
        Changed(nameof(CanCheckTeacherUpdate));
        Changed(nameof(CanDownloadTeacherUpdate));
        Changed(nameof(CanVerifyOfflineTeacherUpdate));
        Changed(nameof(CanInstallOfflineTeacherUpdate));
        TeacherUpdateStatus = "正在下载并验证安装器大小、SHA-256 与发布签名……";
        var handoffStarted = false;
        try
        {
            var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "Updates");
            var installerPath = await _releaseClient.DownloadAsync(release,
                ApplicationReleaseRole.TeacherConsole, updateDirectory);
            ApplicationReleaseUpdateHandoff.Start(installerPath, ApplicationReleaseRole.TeacherConsole, AppVersion);
            handoffStarted = true;
            RecordUpdateHandoffStarted(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.DownloadAndInstall,
                release.Manifest.Version);
            TeacherUpdateStatus = $"已验证并启动 {release.Manifest.Version} 安装；应用将关闭，安装成功后自动重启。";
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.DownloadAndInstall, release.Manifest.Version, exception);
            TeacherUpdateStatus = "下载或校验失败；当前安装未更改：" + failure.ToUserMessage();
        }
        finally
        {
            _isDownloadingTeacherUpdate = false;
            Changed(nameof(CanCheckTeacherUpdate));
            Changed(nameof(CanDownloadTeacherUpdate));
            Changed(nameof(CanVerifyOfflineTeacherUpdate));
            Changed(nameof(CanInstallOfflineTeacherUpdate));
        }
        return handoffStarted;
    }

    public void ReportOfflineTeacherUpdateError(string message) =>
        OfflineTeacherUpdateStatus = message;

    public bool ExportUpdateDiagnostics(string destinationPath)
    {
        try
        {
            var count = _updateDiagnostics.ExportTo(destinationPath);
            UpdateDiagnosticsStatus = $"已导出 {count} 条诊断；文件保存在所选位置，不会自动上传。";
            return true;
        }
        catch (Exception)
        {
            UpdateDiagnosticsStatus = "诊断导出失败；所选位置无法写入。";
            return false;
        }
    }

    public void ReportDiagnosticExportFailure() =>
        UpdateDiagnosticsStatus = "诊断导出失败；所选位置无法写入。";

    private void RecordUpdateSuccess(UpdateDiagnosticModule module, UpdateDiagnosticOperation operation,
        string? targetVersion, UpdateDiagnosticCounts? counts = null) =>
        TryAppendUpdateDiagnostic(UpdateDiagnosticEntry.Create(module, operation, AppVersion, targetVersion,
            counts: counts));

    private void RecordUpdateHandoffStarted(UpdateDiagnosticModule module, UpdateDiagnosticOperation operation,
        string? targetVersion) =>
        TryAppendUpdateDiagnostic(UpdateDiagnosticEntry.HandoffStarted(module, operation, AppVersion, targetVersion));

    private UpdateDiagnosticFailure RecordUpdateFailure(UpdateDiagnosticModule module,
        UpdateDiagnosticOperation operation, string? targetVersion, Exception exception)
    {
        var failure = UpdateDiagnosticCatalog.Classify(exception);
        TryAppendUpdateDiagnostic(UpdateDiagnosticEntry.Create(module, operation, AppVersion,
            targetVersion, exception));
        return failure;
    }

    private void TryAppendUpdateDiagnostic(UpdateDiagnosticEntry entry)
    {
        try { _updateDiagnostics.Append(entry); }
        catch (Exception) { /* Local diagnostics are best-effort and must not change update behavior. */ }
    }

    private async Task SendCampusOperationsReportsAsync()
    {
        if (!OperatingSystem.IsWindows() ||
            Interlocked.CompareExchange(ref _operationsTelemetryInFlight, 1, 0) != 0) return;

        var cancellation = new CancellationTokenSource();
        lock (_operationsTelemetryGate) _operationsTelemetryCancellation = cancellation;
        try
        {
            var preference = _operationsTelemetryStore.LoadPreference();
            if (!preference.Enabled || preference.EnabledAtUtc is not { } enabledAtUtc) return;
            if (_teacherHeartbeatState is not { PackageId: not null } identity)
            {
                OperationsTelemetryStatus = "已开启；发布校区配置包后开始按日汇总。";
                return;
            }
            if (_operationsTelemetryClient is null)
            {
                OperationsTelemetryStatus = _operationsTelemetryClientError ?? "上报 API 配置不可用；数据仍留在本机。";
                return;
            }

            var reports = CampusOperationsTelemetrySummaryBuilder.Build(
                _updateDiagnostics.ReadRecent(), _classroomSessionStore.ReadRecent(), enabledAtUtc,
                CampusOperationsTelemetrySummaryBuilder.HongKongDate(DateTimeOffset.UtcNow));
            _operationsTelemetryStore.QueueReports(reports);
            var pending = _operationsTelemetryStore.ReadPending();
            if (pending.Count == 0)
            {
                OperationsTelemetryStatus = "已开启；只汇总启用后的数据，目前没有待上报结果。";
                return;
            }

            foreach (var report in pending)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!_operationsTelemetryStore.LoadPreference().Enabled) return;
                await _operationsTelemetryClient.UploadAsync(identity, report, cancellation.Token)
                    .ConfigureAwait(false);
                _operationsTelemetryStore.MarkSent(report.ReportDate);
            }
            OperationsTelemetryStatus = $"已发送 {pending.Count} 天的匿名运维汇总。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // User disabled reporting while a bounded request was in progress.
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
                                          UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
                                          TaskCanceledException)
        {
            OperationsTelemetryStatus = "暂时无法上报；匿名汇总留在本机，联网后自动重试。";
        }
        finally
        {
            lock (_operationsTelemetryGate)
            {
                if (ReferenceEquals(_operationsTelemetryCancellation, cancellation))
                    _operationsTelemetryCancellation = null;
            }
            cancellation.Dispose();
            Volatile.Write(ref _operationsTelemetryInFlight, 0);
        }
    }

    private async Task SendTeacherCampusHeartbeatAsync()
    {
        if (!OperatingSystem.IsWindows() || Interlocked.CompareExchange(ref _teacherHeartbeatInFlight, 1, 0) != 0)
            return;
        try
        {
            if (_teacherHeartbeatState is not { Enabled: true, PackageId: not null } state) return;
            if (_teacherHeartbeatClient is null)
            {
                TeacherHeartbeatStatus = _teacherHeartbeatClientError ?? "Teacher 心跳 API 配置不可用。";
                return;
            }
            var now = DateTimeOffset.UtcNow;
            if (!TeacherCampusHeartbeatStateStore.IsDue(state,
                    TeacherCampusHeartbeatStateStore.GetHongKongDate(), now))
            {
                TeacherHeartbeatStatus = state.LastSentDay != TeacherCampusHeartbeatStateStore.GetHongKongDate() &&
                                         state.FirstHeartbeatNotBeforeUtc is { } notBefore && notBefore > now
                    ? "首次校区心跳已安排，将在配置包发布 1 小时后发送。"
                    : "服务已确认今日心跳发送；本机按 UTC+8 日期跳过重复请求。";
                return;
            }
            TeacherHeartbeatStatus = "正在读取 Veyon 机房电脑总数并发送匿名校区汇总……";
            var locations = await Task.Run(VeyonNetworkObjectDirectory.ReadLocations);
            var configuredComputerCount = locations.SelectMany(location => location.Targets)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var heartbeat = await _teacherHeartbeatClient.TrySendOnceDailyAsync(state, AppVersion,
                AppVersion, configuredComputerCount);
            _teacherHeartbeatState = TeacherCampusHeartbeatStateStore.LoadOrCreate();
            if (heartbeat is not null)
                QueueNewerReleaseNotice(heartbeat.LatestReleases);
            TeacherHeartbeatStatus = heartbeat is not null
                ? $"服务已确认今日心跳发送：电脑总数 {configuredComputerCount}，教师版本 {AppVersion}。"
                : "服务已确认今日心跳发送；本机按 UTC+8 日期跳过重复请求。";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or
                                          UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or PlatformNotSupportedException)
        {
            TeacherHeartbeatStatus = "校区心跳未发送；不会影响 Teacher 功能：" + exception.Message;
        }
        finally { Volatile.Write(ref _teacherHeartbeatInFlight, 0); }
    }

    private void QueueNewerReleaseNotice(TeacherCampusLatestReleases releases)
    {
        if (_releaseClient is null) return;
        var newerVersions = new List<string>(2);
        AddNewerRelease(releases.TeacherConsole, ApplicationReleaseRole.TeacherConsole,
            "教师控制台", AppVersion, newerVersions);
        AddNewerRelease(releases.StudentSetup, ApplicationReleaseRole.StudentSetup,
            "学生端部署程序", AppVersion, newerVersions);
        if (newerVersions.Count == 0) return;

        var message = "发现可用的新版本：" + Environment.NewLine +
                      string.Join(Environment.NewLine, newerVersions);
        lock (_releaseNoticeGate)
        {
            if (_pendingReleaseNotice is null) _pendingReleaseNotice = message;
        }
        ReleaseNoticeAvailable?.Invoke(this, EventArgs.Empty);
    }

    private void AddNewerRelease(ApplicationReleaseEnvelope? release, ApplicationReleaseRole role,
        string displayName, string currentVersion, ICollection<string> newerVersions)
    {
        try
        {
            var result = _releaseClient!.EvaluateLatest(release, role, currentVersion);
            if (result.IsNewer && result.Release is not null)
                newerVersions.Add($"{displayName}：{currentVersion} → {result.Release.Manifest.Version}");
        }
        catch (InvalidDataException)
        {
            // Ignore untrusted release metadata; the signed heartbeat itself remains successful.
        }
    }

    internal async Task RunHourlyBackgroundChecksAsync()
    {
        if (TeacherCampusHeartbeatStateStore.IsDue(_teacherHeartbeatState,
                TeacherCampusHeartbeatStateStore.GetHongKongDate()))
            await SendTeacherCampusHeartbeatAsync();
        await SendCampusOperationsReportsAsync();
    }

    private void ScheduleInitialTeacherHeartbeat(DateTimeOffset sendAtUtc)
    {
        if (!OperatingSystem.IsWindows() ||
            Interlocked.CompareExchange(ref _initialTeacherHeartbeatWaitScheduled, 1, 0) != 0)
            return;
        _ = WaitForInitialTeacherHeartbeatAsync(sendAtUtc);
    }

    private async Task WaitForInitialTeacherHeartbeatAsync(DateTimeOffset sendAtUtc)
    {
        try
        {
            var remaining = sendAtUtc - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            if (_teacherHeartbeatState is { Enabled: true, PackageId: not null } state &&
                state.FirstHeartbeatNotBeforeUtc is { } dueAt && dueAt <= DateTimeOffset.UtcNow)
                await SendTeacherCampusHeartbeatAsync();
        }
        finally { Volatile.Write(ref _initialTeacherHeartbeatWaitScheduled, 0); }
    }

    public string RoomPrefix { get => _roomPrefix; set { _roomPrefix = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string RoomStart { get => _roomStart; set { _roomStart = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string RoomCount { get => _roomCount; set { _roomCount = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string RoomLocationName
    {
        get => _roomLocationName;
        set
        {
            if (_roomLocationName == value) return;
            _roomLocationName = value ?? "";
            Changed();
            InvalidateRoomReview();
            ClearRoomCreateFeedback();
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(RoomSummary));
        }
    }
    public string StudentRoster
    {
        get => _studentRoster;
        set
        {
            _studentRoster = value ?? "";
            Changed();
            RoomError = "";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            ClearRoomCreateFeedback();
            Changed(nameof(CanAddRoomToVeyon));
        }
    }
    public string RoomComputerHosts
    {
        get => _roomComputerHosts;
        set
        {
            _roomComputerHosts = value ?? "";
            Changed();
            RoomError = "";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            ClearRoomCreateFeedback();
        }
    }
    public IReadOnlyList<TeacherCampusProfile> CampusProfiles
    {
        get => _campusProfiles;
        private set { _campusProfiles = value; Changed(); Changed(nameof(HasCampusProfiles)); }
    }
    public bool HasCampusProfiles => CampusProfiles.Count > 0;
    public IReadOnlyList<TeacherRoomProfile> RoomProfiles
    {
        get => _roomProfiles;
        private set { _roomProfiles = value; Changed(); Changed(nameof(HasRoomProfiles)); }
    }
    public bool HasRoomProfiles => RoomProfiles.Count > 0;
    public IReadOnlyList<TeacherSeatCell> SeatMapCells
    {
        get => _seatMapCells;
        private set
        {
            _seatMapCells = value;
            Changed();
            Changed(nameof(HasSeatMap));
        }
    }
    public bool HasSeatMap => SeatMapCells.Count > 0;
    public decimal SeatColumns
    {
        get => _seatColumns;
        set
        {
            if (_seatColumns == value) return;
            if (_isLoadingSeatLayout)
            {
                _seatColumns = value;
                Changed();
                return;
            }
            var columns = decimal.ToInt32(decimal.Round(value, 0, MidpointRounding.AwayFromZero));
            SetSeatColumns(columns);
        }
    }
    public decimal SeatColumnsMinimum => 1;
    public decimal SeatColumnsMaximum => Math.Max(1, _seatMapTargets.Count);
    public int SeatGridColumns => Math.Clamp((int)_seatColumns, 1, Math.Max(1, _seatMapTargets.Count));
    public string SeatMapStatus
    {
        get => _seatMapStatus;
        private set
        {
            if (_seatMapStatus == value) return;
            _seatMapStatus = value;
            Changed();
            Changed(nameof(HasSeatMapStatus));
            Changed(nameof(HasSeatMapError));
            Changed(nameof(HasSeatMapInfo));
        }
    }
    public bool HasSeatMapStatus => SeatMapStatus.Length > 0;
    public bool HasSeatMapError => SeatMapStatus.StartsWith("无法", StringComparison.Ordinal) ||
                                   SeatMapStatus.StartsWith("座位图", StringComparison.Ordinal);
    public bool HasSeatMapInfo => HasSeatMapStatus && !HasSeatMapError;
    public TeacherCampusProfile? SelectedCampusProfile
    {
        get => _selectedCampusProfile;
        set
        {
            if (_selectedCampusProfile?.ProfileId == value?.ProfileId) return;
            _selectedCampusProfile = value;
            Changed();
            Changed(nameof(HasSelectedCampusProfile));
            RoomProfiles = value?.Rooms.ToArray() ?? Array.Empty<TeacherRoomProfile>();
            SelectedRoomProfile = RoomProfiles.FirstOrDefault();
            CampusProfileName = value?.DisplayName ?? "";
            CampusDirectoryError = "";
            Changed(nameof(ClassroomSessionSummary));
            Changed(nameof(CanToggleClassroomSession));
            if (_seatLayout?.CampusProfileId != value?.ProfileId ||
                _seatLayout?.RoomId != SelectedRoomProfile?.RoomId)
                LoadSelectedSeatLayout();
        }
    }
    public bool HasSelectedCampusProfile => SelectedCampusProfile is not null;
    public TeacherRoomProfile? SelectedRoomProfile
    {
        get => _selectedRoomProfile;
        set
        {
            if (_selectedRoomProfile?.RoomId == value?.RoomId) return;
            _selectedRoomProfile = value;
            Changed();
            Changed(nameof(HasSelectedRoomProfile));
            Changed(nameof(ClassroomSessionSummary));
            Changed(nameof(CanToggleClassroomSession));
            if (value is null)
            {
                RoomProfileName = "";
                RoomProfilePrefix = "PC-";
                RoomProfileStart = "1";
                RoomProfileCount = "150";
                RoomProfileHostOverrides = "";
            }
            else
            {
                RoomProfileName = value.DisplayName;
                RoomProfilePrefix = value.Prefix;
                RoomProfileStart = value.StartNumber.ToString(CultureInfo.InvariantCulture);
                RoomProfileCount = value.ComputerCount.ToString(CultureInfo.InvariantCulture);
                RoomProfileHostOverrides = string.Join(Environment.NewLine, value.HostOverrides ?? []);
            }
            CampusDirectoryError = "";
            LoadSelectedSeatLayout();
        }
    }
    public bool HasSelectedRoomProfile => SelectedRoomProfile is not null;
    internal void SelectSeatCell(int index)
    {
        if (_seatLayout is null || index < 0 || index >= _seatLayout.TargetOrder.Length) return;
        if (_selectedSeatIndex is null)
        {
            _selectedSeatIndex = index;
            SeatMapStatus = $"已选中 {_seatLayout.TargetOrder[index]}；再点另一格交换座位。";
            RefreshSeatMapCells();
            return;
        }
        if (_selectedSeatIndex == index)
        {
            _selectedSeatIndex = null;
            SeatMapStatus = "点击两台电脑交换座位；改动自动保存在教师电脑。";
            RefreshSeatMapCells();
            return;
        }

        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room) return;
        var firstIndex = _selectedSeatIndex.Value;
        var firstTarget = _seatLayout.TargetOrder[firstIndex];
        var secondTarget = _seatLayout.TargetOrder[index];
        try
        {
            var layout = _classroomSeatLayoutStore.Swap(campus.ProfileId, room.RoomId,
                _seatMapTargets, firstIndex, index);
            _selectedSeatIndex = null;
            ApplySeatLayout(layout);
            SeatMapStatus = $"已交换 {firstTarget} 与 {secondTarget} 的座位，并保存在教师电脑。";
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _selectedSeatIndex = null;
            SeatMapStatus = "座位图无法保存；原布局已保留。";
            Changed(nameof(SeatColumns));
            RefreshSeatMapCells();
        }
    }

    private void LoadSelectedSeatLayout()
    {
        _selectedSeatIndex = null;
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room)
        {
            _seatLayout = null;
            _seatMapTargets = Array.Empty<string>();
            _seatColumns = 1;
            SeatMapCells = Array.Empty<TeacherSeatCell>();
            SeatMapStatus = "";
            Changed(nameof(SeatColumns));
            Changed(nameof(SeatColumnsMaximum));
            Changed(nameof(SeatGridColumns));
            return;
        }

        try
        {
            _seatMapTargets = MachineNaming.CreateRange(room.Prefix,
                room.StartNumber.ToString(CultureInfo.InvariantCulture),
                room.ComputerCount.ToString(CultureInfo.InvariantCulture));
            ApplySeatLayout(_classroomSeatLayoutStore.GetLayout(campus.ProfileId, room.RoomId, _seatMapTargets));
            SeatMapStatus = "点击两台电脑交换座位；改动自动保存在教师电脑。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _seatLayout = null;
            _seatMapTargets = Array.Empty<string>();
            _seatColumns = 1;
            SeatMapCells = Array.Empty<TeacherSeatCell>();
            SeatMapStatus = "无法读取座位图；原文件保留，未展示不完整布局。";
            Changed(nameof(SeatColumns));
            Changed(nameof(SeatColumnsMaximum));
            Changed(nameof(SeatGridColumns));
        }
    }

    private void SetSeatColumns(int columns)
    {
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room ||
            _seatMapTargets.Count == 0 || columns < 1 || columns > _seatMapTargets.Count)
        {
            Changed(nameof(SeatColumns));
            return;
        }
        try
        {
            ApplySeatLayout(_classroomSeatLayoutStore.SetColumns(campus.ProfileId, room.RoomId,
                _seatMapTargets, columns));
            SeatMapStatus = $"每排 {columns} 个座位；布局已保存在教师电脑。";
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "座位图无法保存；原布局已保留。";
            Changed(nameof(SeatColumns));
        }
    }

    private void ApplySeatLayout(ClassroomSeatLayout layout)
    {
        _seatLayout = layout with { TargetOrder = layout.TargetOrder.ToArray() };
        _isLoadingSeatLayout = true;
        _seatColumns = layout.Columns;
        Changed(nameof(SeatColumns));
        _isLoadingSeatLayout = false;
        Changed(nameof(SeatColumnsMaximum));
        Changed(nameof(SeatGridColumns));
        RefreshSeatMapCells();
    }

    private void RefreshSeatMapCells()
    {
        if (_seatLayout is null)
        {
            SeatMapCells = Array.Empty<TeacherSeatCell>();
            return;
        }
        var locations = ClassroomSeatLayoutStore.GetLocations(_seatLayout);
        SeatMapCells = locations.Select((location, index) => new TeacherSeatCell(index, location.Target,
            $"第 {location.Row} 排 · 第 {location.Column} 位", _selectedSeatIndex == index)).ToArray();
    }

    private IReadOnlyDictionary<string, string> ReadActiveSeatLabels(Guid sessionId)
    {
        if (_activeClassroomSession is not { } active || active.SessionId != sessionId)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var targets = active.Targets.Select(target => target.DeviceLabel).ToArray();
            var layout = _classroomSeatLayoutStore.GetLayout(active.Room.CampusProfileId, active.Room.RoomId, targets);
            return ClassroomSeatLayoutStore.GetLocations(layout).ToDictionary(location => location.Target,
                location => $"第 {location.Row} 排 · 第 {location.Column} 位", StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (SelectedRoomProfile?.RoomId == active.Room.RoomId)
                SeatMapStatus = "无法读取座位图；原文件保留，课堂消息仍显示电脑编号。";
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void RefreshClassroomEventSeatLocations()
    {
        if (_classroomEventFeedSessionId is not { } sessionId) return;
        var labels = ReadActiveSeatLabels(sessionId);
        foreach (var item in ClassroomEventItems.Where(item => item.IsHelpRequest))
            item.SetSeatLocation(labels.GetValueOrDefault(item.Event.Target));
    }

    public string CampusProfileName { get => _campusProfileName; set { _campusProfileName = value ?? ""; Changed(); } }
    public string RoomProfileName { get => _roomProfileName; set { _roomProfileName = value ?? ""; Changed(); } }
    public string RoomProfilePrefix { get => _roomProfilePrefix; set { _roomProfilePrefix = value ?? ""; Changed(); } }
    public string RoomProfileStart { get => _roomProfileStart; set { _roomProfileStart = value ?? ""; Changed(); } }
    public string RoomProfileCount { get => _roomProfileCount; set { _roomProfileCount = value ?? ""; Changed(); } }
    public string RoomProfileHostOverrides { get => _roomProfileHostOverrides; set { _roomProfileHostOverrides = value ?? ""; Changed(); } }
    public string CampusDirectoryStatus { get => _campusDirectoryStatus; private set { _campusDirectoryStatus = value; Changed(); Changed(nameof(HasCampusDirectoryStatus)); } }
    public bool HasCampusDirectoryStatus => CampusDirectoryStatus.Length > 0;
    public string CampusDirectoryError { get => _campusDirectoryError; private set { _campusDirectoryError = value; Changed(); Changed(nameof(HasCampusDirectoryError)); } }
    public bool HasCampusDirectoryError => CampusDirectoryError.Length > 0;
    public string CampusId
    {
        get => _campusId;
        set
        {
            var next = value ?? "";
            if (_campusId == next) return;
            _campusId = next;
            CanReplaceWebsiteSigningKey = false;
            Changed();
            Changed(nameof(PublishCampusName));
            Changed(nameof(SelectedWebsiteSigningCampus));
            Changed(nameof(CanPublishStudentPackage));
            PackagePublishResult = "";
            PackagePublisherError = "";
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanPushStudentSystemPolicy));
            Changed(nameof(CanDisableStudentSystemPolicy));
            Changed(nameof(CanTrustStudentAgentIdentities));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanDeployStudentUpdate));
            Changed(nameof(CanReadApplicationInventory));
            Changed(nameof(ClassroomTargetSummary));
            _studentAccountChoices = [];
            Changed(nameof(StudentAccountChoices));
            Changed(nameof(SelectedApplicationSummary));
            if (!UseManualApplicationInputs) ApplicationRules = "";
            if (_allApplicationInventoryChoices.Count > 0)
            {
                _allApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
                ApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
                ApplicationInventoryStatus = "校区已变化，请重新读取应用清单。";
            }
            NotifyApplicationPolicyInputs();
        }
    }
    public string RoomOutputDir { get => _roomOutputDir; set { _roomOutputDir = value ?? ""; Changed(); } }
    public bool RecommendInstallVeyon
    {
        get => _recommendInstallVeyon;
        set { if (_recommendInstallVeyon == value) return; _recommendInstallVeyon = value; Changed(); }
    }
    public bool RecommendRenameComputer
    {
        get => _recommendRenameComputer;
        set { if (_recommendRenameComputer == value) return; _recommendRenameComputer = value; Changed(); }
    }
    public bool RecommendCreateStudentAccount
    {
        get => _recommendCreateStudentAccount;
        set { if (_recommendCreateStudentAccount == value) return; _recommendCreateStudentAccount = value; Changed(); }
    }
    public bool RecommendChangeAdminPassword
    {
        get => _recommendChangeAdminPassword;
        set { if (_recommendChangeAdminPassword == value) return; _recommendChangeAdminPassword = value; Changed(); }
    }
    public string WebsiteTargets
    {
        get => _websiteTargets;
        set
        {
            value ??= "";
            if (_websiteTargets == value) return;
            _websiteTargets = value;
            Changed(nameof(ClassroomTargetSummary));
            if (_websiteLocationSelectionPending)
            {
                _websiteLocationSelectionPending = false;
                Changed(nameof(CanPushWebsitePolicy));
                Changed(nameof(CanDisableWebsitePolicy));
            }
            Changed();
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanPushStudentSystemPolicy));
            Changed(nameof(CanDisableStudentSystemPolicy));
            Changed(nameof(CanTrustStudentAgentIdentities));
            Changed(nameof(CanDeployStudentUpdate));
            Changed(nameof(CanReadApplicationInventory));
            _studentAccountChoices = [];
            Changed(nameof(StudentAccountChoices));
            Changed(nameof(SelectedApplicationSummary));
            if (!UseManualApplicationInputs) ApplicationRules = "";
            if (_allApplicationInventoryChoices.Count > 0)
            {
                _allApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
                ApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
                ApplicationInventoryStatus = "目标电脑已变化，请重新读取应用清单。";
            }
            NotifyApplicationPolicyInputs();
        }
    }
    public string WebsiteDomains { get => _websiteDomains; set { _websiteDomains = value ?? ""; Changed(); Changed(nameof(CanPushWebsitePolicy)); } }
    public int WebsiteModeIndex { get => _websiteModeIndex; set { _websiteModeIndex = Math.Clamp(value, 0, 1); Changed(); Changed(nameof(CanPushWebsitePolicy)); } }
    public WebsitePolicyMode SelectedWebsiteMode => WebsiteModeIndex == 0 ? WebsitePolicyMode.Blocklist : WebsitePolicyMode.Allowlist;
    public IReadOnlyList<string> WebsiteSigningCampuses { get; private set; } = Array.Empty<string>();
    public string? SelectedWebsiteSigningCampus
    {
        get => WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal) ? CampusId : null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) CampusId = value;
        }
    }

    private void ReadWebsiteSigningCampuses()
    {
        if (!OperatingSystem.IsWindows()) return;
        var candidates = WebsitePolicySigningKeyStore.ReadCampusIds().ToList();
        var latest = WebsitePolicyPushHistoryStore.ReadLatest();
        if (latest is not null) candidates.Add(latest.CampusId);
        WebsiteSigningCampuses = candidates.Where(campus => !ApplicationPolicySigningKeyStore.IsInternalNamespace(campus) &&
            !StudentSystemPolicySigningKeyStore.IsInternalNamespace(campus)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Changed(nameof(WebsiteSigningCampuses));
        Changed(nameof(SelectedWebsiteSigningCampus));
        Changed(nameof(CanToggleClassroomSession));
        if (!WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal))
        {
            var preferred = latest?.CampusId is { } lastUsed &&
                            WebsiteSigningCampuses.Contains(lastUsed, StringComparer.Ordinal)
                ? lastUsed
                : WebsiteSigningCampuses.FirstOrDefault();
            if (preferred is not null) CampusId = preferred;
        }
    }
    public int WebsiteDurationIndex { get => _websiteDurationIndex; set { _websiteDurationIndex = Math.Clamp(value, 0, 4); Changed(); } }
    public int WebsiteLocationIndex
    {
        get => _websiteLocationIndex;
        set
        {
            if (_websiteLocationIndex == value) return;
            _websiteLocationIndex = value;
            Changed();
            Changed(nameof(CanApplyWebsiteLocation));
        }
    }
    public bool IsReadingWebsiteLocations
    {
        get => _isReadingWebsiteLocations;
        private set
        {
            if (_isReadingWebsiteLocations == value) return;
            _isReadingWebsiteLocations = value;
            Changed();
            Changed(nameof(CanReadWebsiteLocations));
            Changed(nameof(CanApplyWebsiteLocation));
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
        }
    }
    public IReadOnlyList<VeyonNetworkLocation> WebsiteLocations
    {
        get => _websiteLocations;
        private set
        {
            _websiteLocations = value;
            Changed();
            Changed(nameof(WebsiteLocationChoices));
            Changed(nameof(HasMultipleWebsiteLocations));
            Changed(nameof(CanApplyWebsiteLocation));
        }
    }
    public IReadOnlyList<string> WebsiteLocationChoices => WebsiteLocations.Select(location => location.DisplayName).ToArray();
    public bool HasMultipleWebsiteLocations => WebsiteLocations.Count > 1;
    public string WebsiteDirectoryStatus { get => _websiteDirectoryStatus; private set { _websiteDirectoryStatus = value; Changed(); Changed(nameof(HasWebsiteDirectoryStatus)); } }
    public bool HasWebsiteDirectoryStatus => WebsiteDirectoryStatus.Length > 0;
    public string WebsiteDirectoryError { get => _websiteDirectoryError; private set { _websiteDirectoryError = value; Changed(); Changed(nameof(HasWebsiteDirectoryError)); } }
    public bool HasWebsiteDirectoryError => WebsiteDirectoryError.Length > 0;
    public string WebsitePolicyResult { get => _websitePolicyResult; private set { _websitePolicyResult = value; Changed(); Changed(nameof(HasWebsitePolicyResult)); } }
    public bool HasWebsitePolicyResult => WebsitePolicyResult.Length > 0;
    public string WebsitePolicyResultDetails
    {
        get => _websitePolicyResultDetails;
        private set { _websitePolicyResultDetails = value; Changed(); Changed(nameof(HasWebsitePolicyResultDetails)); }
    }
    public bool HasWebsitePolicyResultDetails => WebsitePolicyResultDetails.Length > 0;
    public bool ShowWebsitePolicyResultDetails
    {
        get => _showWebsitePolicyResultDetails;
        set { _showWebsitePolicyResultDetails = value; Changed(); Changed(nameof(WebsitePolicyDetailsToggleText)); }
    }
    public string WebsitePolicyDetailsToggleText => ShowWebsitePolicyResultDetails ? "收起逐台结果" : "查看逐台结果";
    public string WebsitePolicyError { get => _websitePolicyError; private set { _websitePolicyError = value; Changed(); Changed(nameof(HasWebsitePolicyError)); } }
    public bool HasWebsitePolicyError => WebsitePolicyError.Length > 0;
    public string WebsitePolicyHistoryText { get => _websitePolicyHistoryText; private set { _websitePolicyHistoryText = value; Changed(); Changed(nameof(HasWebsitePolicyHistory)); } }
    public bool HasWebsitePolicyHistory => WebsitePolicyHistoryText.Length > 0;
    public string ApplicationStudentSids
    {
        get => _applicationStudentSids;
        set { _applicationStudentSids = value ?? ""; Changed(); NotifyApplicationPolicyInputs(); Changed(nameof(CanPushStudentSystemPolicy)); Changed(nameof(StudentSystemPolicyPreview)); }
    }
    public bool StudentSystemPolicyLockWallpaper
    {
        get => _studentSystemPolicyLockWallpaper;
        set { if (_studentSystemPolicyLockWallpaper == value) return; _studentSystemPolicyLockWallpaper = value; Changed(); NotifyStudentSystemPolicyInputs(); }
    }
    public bool StudentSystemPolicyProhibitTimeChanges
    {
        get => _studentSystemPolicyProhibitTimeChanges;
        set { if (_studentSystemPolicyProhibitTimeChanges == value) return; _studentSystemPolicyProhibitTimeChanges = value; Changed(); NotifyStudentSystemPolicyInputs(); }
    }
    public bool StudentSystemPolicyProhibitNetworkChanges
    {
        get => _studentSystemPolicyProhibitNetworkChanges;
        set { if (_studentSystemPolicyProhibitNetworkChanges == value) return; _studentSystemPolicyProhibitNetworkChanges = value; Changed(); NotifyStudentSystemPolicyInputs(); }
    }
    public bool StudentSystemPolicyProhibitSoftwareInstallation
    {
        get => _studentSystemPolicyProhibitSoftwareInstallation;
        set
        {
            if (_studentSystemPolicyProhibitSoftwareInstallation == value) return;
            _studentSystemPolicyProhibitSoftwareInstallation = value;
            _studentSystemPolicySoftwareInstallReviewed = false;
            Changed();
            Changed(nameof(StudentSystemPolicySoftwareInstallReviewed));
            NotifyStudentSystemPolicyInputs();
        }
    }
    public bool StudentSystemPolicyProhibitAccountManagement
    {
        get => _studentSystemPolicyProhibitAccountManagement;
        set { if (_studentSystemPolicyProhibitAccountManagement == value) return; _studentSystemPolicyProhibitAccountManagement = value; Changed(); NotifyStudentSystemPolicyInputs(); }
    }
    public bool StudentSystemPolicyProhibitControlPanel
    {
        get => _studentSystemPolicyProhibitControlPanel;
        set { if (_studentSystemPolicyProhibitControlPanel == value) return; _studentSystemPolicyProhibitControlPanel = value; Changed(); NotifyStudentSystemPolicyInputs(); }
    }
    public bool StudentSystemPolicySoftwareInstallReviewed
    {
        get => _studentSystemPolicySoftwareInstallReviewed;
        set
        {
            if (_studentSystemPolicySoftwareInstallReviewed == value) return;
            _studentSystemPolicySoftwareInstallReviewed = value;
            Changed();
            Changed(nameof(CanPushStudentSystemPolicy));
            Changed(nameof(StudentSystemPolicyPreview));
        }
    }
    public string StudentSystemPolicyPreview => BuildStudentSystemPolicyPreview();
    public string StudentSystemPolicyResult
    {
        get => _studentSystemPolicyResult;
        private set { _studentSystemPolicyResult = value; Changed(); Changed(nameof(HasStudentSystemPolicyResult)); }
    }
    public bool HasStudentSystemPolicyResult => StudentSystemPolicyResult.Length > 0;
    public string StudentSystemPolicyDetails
    {
        get => _studentSystemPolicyDetails;
        private set { _studentSystemPolicyDetails = value; Changed(); Changed(nameof(HasStudentSystemPolicyDetails)); }
    }
    public bool HasStudentSystemPolicyDetails => StudentSystemPolicyDetails.Length > 0;
    public string StudentSystemPolicyError
    {
        get => _studentSystemPolicyError;
        private set { _studentSystemPolicyError = value; Changed(); Changed(nameof(HasStudentSystemPolicyError)); }
    }
    public bool HasStudentSystemPolicyError => StudentSystemPolicyError.Length > 0;
    public IReadOnlyList<ApplicationInventoryChoice> ApplicationInventoryChoices
    {
        get => _applicationInventoryChoices;
        private set { _applicationInventoryChoices = value; Changed(); Changed(nameof(HasApplicationInventoryItems)); Changed(nameof(CanAddSelectedApplicationRules)); }
    }
    public bool HasApplicationInventoryItems => ApplicationInventoryChoices.Count > 0;
    public string ApplicationInventorySearch
    {
        get => _applicationInventorySearch;
        set
        {
            _applicationInventorySearch = value ?? "";
            Changed();
            FilterApplicationInventory();
        }
    }
    public string ApplicationInventoryStatus
    {
        get => _applicationInventoryStatus;
        private set { _applicationInventoryStatus = value; Changed(); Changed(nameof(HasApplicationInventoryStatus)); }
    }
    public bool HasApplicationInventoryStatus => ApplicationInventoryStatus.Length > 0;
    private IReadOnlyList<StudentAccountChoice> _studentAccountChoices = [];
    private bool _useManualApplicationInputs;
    public IReadOnlyList<StudentAccountChoice> StudentAccountChoices => _studentAccountChoices;
    public bool UseManualApplicationInputs
    {
        get => _useManualApplicationInputs;
        set { _useManualApplicationInputs = value; Changed(); if (!value) SyncSelectedApplicationRules(); NotifyApplicationPolicyInputs(); }
    }
    public string SelectedApplicationSummary => string.Join("、", _allApplicationInventoryChoices
        .Where(item => item.IsSelected && item.CanSelect).Select(item => item.DisplayName).Distinct());

    public async Task ReadStudentAccountsAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        ApplicationInventoryStatus = "正在读取学生账户……";
        _studentAccountChoices = [];
        Changed(nameof(StudentAccountChoices));
        NotifyApplicationPolicyInputs();
        try
        {
            var campus = CampusId.Trim();
            using var key = ApplicationPolicySigningKeyStore.Open(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var results = await ApplicationPolicyTransport.ReadInventoryAsync(targets,
                campus, key.PrivateKey, accountsOnly: true);
            if (campus != CampusId.Trim() || !targets.SequenceEqual(WebsitePolicyTransport.NormalizeTargets(
                    WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)), StringComparer.OrdinalIgnoreCase))
            { ApplicationInventoryStatus = "校区或目标已变化，请重新读取账户。"; return; }
            SetStudentAccountChoices(results.Where(r => r.Succeeded).SelectMany(r =>
                (r.StudentAccounts ?? []).Select(a => new StudentAccountChoice(r.Target, a))).ToArray());
            ApplicationInventoryStatus = "请勾选实际用于上课的账户；管理员账户不在此列表中。" + Environment.NewLine +
                string.Join(Environment.NewLine, results.Select(r => r.Succeeded
                    ? $"{r.Target}：{r.StudentAccounts?.Count ?? 0} 个普通账户"
                    : $"{r.Target}：读取未完成 — {r.Detail}（旧 Agent 请安装新版学生包）"));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException or CryptographicException or PlatformNotSupportedException)
        { ApplicationInventoryStatus = "账户读取未完成：" + e.Message; }
        finally { NotifyApplicationPolicyInputs(); EndExclusiveTask(); }
    }

    internal void SetStudentAccountChoices(IReadOnlyList<StudentAccountChoice> choices)
    {
        _studentAccountChoices = choices;
        foreach (var choice in choices) choice.PropertyChanged += (_, _) => NotifyApplicationPolicyInputs();
        Changed(nameof(StudentAccountChoices));
        NotifyApplicationPolicyInputs();
    }

    internal void SetApplicationInventoryChoices(IReadOnlyList<ApplicationInventoryChoice> choices)
    {
        _allApplicationInventoryChoices = choices;
        foreach (var choice in choices) choice.PropertyChanged += (_, _) => SyncSelectedApplicationRules();
        SyncSelectedApplicationRules();
        FilterApplicationInventory();
    }

    private void SyncSelectedApplicationRules()
    {
        if (!UseManualApplicationInputs)
            ApplicationRules = string.Join(Environment.NewLine, _allApplicationInventoryChoices
                .Where(c => c.IsSelected && c.CanSelect).Select(c => c.RuleLine!).Distinct(StringComparer.Ordinal));
        Changed(nameof(CanAddSelectedApplicationRules));
        Changed(nameof(SelectedApplicationSummary));
    }

    internal Dictionary<string, string[]> StudentSidsByTarget(IReadOnlyList<string> targets, bool disabled)
    {
        if (disabled) return targets.ToDictionary(t => t, _ => Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (UseManualApplicationInputs) return targets.ToDictionary(t => t, _ => ParseStudentSids(), StringComparer.OrdinalIgnoreCase);
        var map = targets.ToDictionary(t => t, t => _studentAccountChoices.Where(c => c.IsSelected &&
            string.Equals(c.Target, t, StringComparison.OrdinalIgnoreCase)).Select(c => c.Account.Sid)
            .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
        if (map.Any(pair => pair.Value.Length == 0))
            throw new InvalidDataException("请先读取账户，并为每台目标电脑勾选实际学生账户。");
        return map;
    }

    private static async Task<IReadOnlyList<T>> SendByTargetAsync<T>(IReadOnlyList<string> targets,
        Func<string, Task<IReadOnlyList<T>>> send)
    {
        var results = new List<T>();
        foreach (var batch in targets.Chunk(16))
            foreach (var items in await Task.WhenAll(batch.Select(send))) results.AddRange(items);
        return results;
    }

    public string ApplicationRules
    {
        get => _applicationRules;
        set { _applicationRules = value ?? ""; Changed(); NotifyApplicationPolicyInputs(); }
    }
    public int ApplicationPolicyModeIndex
    {
        get => _applicationPolicyModeIndex;
        set
        {
            var next = Math.Clamp(value, 0, 1);
            if (_applicationPolicyModeIndex == next) return;
            _applicationPolicyModeIndex = next;
            _applicationEnforcementReviewed = false;
            Changed();
            Changed(nameof(ApplicationEnforcementReviewed));
            Changed(nameof(IsEnforceApplicationPolicy));
            NotifyApplicationPolicyInputs(invalidateAudit: false);
        }
    }
    public bool ApplicationEnforcementReviewed
    {
        get => _applicationEnforcementReviewed;
        set { _applicationEnforcementReviewed = value; Changed(); Changed(nameof(CanPushApplicationPolicy)); Changed(nameof(ApplicationPolicyPreview)); }
    }
    public bool IsEnforceApplicationPolicy => ApplicationPolicyModeIndex == 1;
    public bool HasMatchingApplicationAudit => _hasMatchingApplicationAudit;
    public int ApplicationPolicyDurationIndex
    {
        get => _applicationPolicyDurationIndex;
        set { _applicationPolicyDurationIndex = Math.Clamp(value, 0, 4); Changed(); NotifyApplicationPolicyInputs(); }
    }
    public string ApplicationPolicyPreview => BuildApplicationPolicyPreview();
    public string ApplicationPolicyResult
    {
        get => _applicationPolicyResult;
        private set { _applicationPolicyResult = value; Changed(); Changed(nameof(HasApplicationPolicyResult)); }
    }
    public bool HasApplicationPolicyResult => ApplicationPolicyResult.Length > 0;
    public string ApplicationPolicyResultDetails
    {
        get => _applicationPolicyResultDetails;
        private set { _applicationPolicyResultDetails = value; Changed(); Changed(nameof(HasApplicationPolicyResultDetails)); }
    }
    public bool HasApplicationPolicyResultDetails => ApplicationPolicyResultDetails.Length > 0;
    public bool ShowApplicationPolicyResultDetails
    {
        get => _showApplicationPolicyResultDetails;
        set { _showApplicationPolicyResultDetails = value; Changed(); Changed(nameof(ApplicationPolicyDetailsToggleText)); }
    }
    public string ApplicationPolicyDetailsToggleText => ShowApplicationPolicyResultDetails ? "收起逐台结果" : "查看逐台结果";
    public string ApplicationPolicyError
    {
        get => _applicationPolicyError;
        private set { _applicationPolicyError = value; Changed(); Changed(nameof(HasApplicationPolicyError)); }
    }
    public bool HasApplicationPolicyError => ApplicationPolicyError.Length > 0;
    public string ApplicationPolicyHistoryText
    {
        get => _applicationPolicyHistoryText;
        private set { _applicationPolicyHistoryText = value; Changed(); Changed(nameof(HasApplicationPolicyHistory)); }
    }
    public bool HasApplicationPolicyHistory => ApplicationPolicyHistoryText.Length > 0;
    public string ApplicationAuditResult
    {
        get => _applicationAuditResult;
        private set { _applicationAuditResult = value; Changed(); Changed(nameof(HasApplicationAuditResult)); }
    }
    public bool HasApplicationAuditResult => ApplicationAuditResult.Length > 0;
    public string InstallerStatus { get => _installerStatus; private set { _installerStatus = value; Changed(); } }
    public string TeacherInstallResult { get => _teacherInstallResult; private set { _teacherInstallResult = value; Changed(); Changed(nameof(HasTeacherInstallResult)); } }
    public bool HasTeacherInstallResult => TeacherInstallResult.Length > 0;
    public string TeacherInstallIssue { get => _teacherInstallIssue; private set { _teacherInstallIssue = value; Changed(); Changed(nameof(HasTeacherInstallIssue)); } }
    public bool HasTeacherInstallIssue => TeacherInstallIssue.Length > 0;
    public string PackageOutput { get => _packageOutput; private set { _packageOutput = value; Changed(); Changed(nameof(HasPackageOutput)); } }
    public string PackageOutputError { get => _packageOutputError; private set { _packageOutputError = value; Changed(); Changed(nameof(HasPackageOutputError)); } }
    public bool IsGeneratingStudentPackage
    {
        get => _isGeneratingStudentPackage;
        private set
        {
            if (_isGeneratingStudentPackage == value) return;
            _isGeneratingStudentPackage = value;
            Changed();
            Changed(nameof(CanCancelStudentPackageGeneration));
        }
    }
    public bool IsBuildingStudentPackage
    {
        get => _isBuildingStudentPackage;
        private set
        {
            if (_isBuildingStudentPackage == value) return;
            _isBuildingStudentPackage = value;
            Changed();
            Changed(nameof(CanCancelStudentPackageGeneration));
        }
    }
    public bool CanCancelStudentPackageGeneration => IsBuildingStudentPackage &&
        _studentPackageBuildCancellation is { IsCancellationRequested: false };
    public void CancelStudentPackageGeneration()
    {
        var cancellation = _studentPackageBuildCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested) return;
        cancellation.Cancel();
        PackageGenerationStatus = "正在取消生成并清理未发布的临时文件……";
        Changed(nameof(CanCancelStudentPackageGeneration));
    }
    public string PackageGenerationStatus
    {
        get => _packageGenerationStatus;
        private set
        {
            if (_packageGenerationStatus == value) return;
            _packageGenerationStatus = value;
            Changed();
        }
    }
    public bool HasPackageOutput => PackageOutput.Length > 0;
    public bool HasPackageOutputError => PackageOutputError.Length > 0;
    public string PublishPackageDirectory
    {
        get => _publishPackageDirectory;
        set
        {
            _publishPackageDirectory = value ?? "";
            Changed();
            Changed(nameof(CanPublishStudentPackage));
            PackagePublishResult = "";
        }
    }
    public string PublishCampusName
    {
        get => CampusId;
        set => CampusId = value;
    }
    public string PublisherName
    {
        get => _publisherName;
        set
        {
            _publisherName = value ?? "";
            Changed();
            Changed(nameof(CanPublishStudentPackage));
            PackagePublishResult = "";
        }
    }
    public string TeacherPhoneLast4
    {
        get => _teacherPhoneLast4;
        set
        {
            _teacherPhoneLast4 = value ?? "";
            Changed();
            Changed(nameof(CanPublishStudentPackage));
            PackagePublishResult = "";
        }
    }
    public string PackagePublisherStatus
    {
        get => _packagePublisherStatus;
        private set { _packagePublisherStatus = value; Changed(); Changed(nameof(HasPackagePublisherStatus)); }
    }
    public bool HasPackagePublisherStatus => PackagePublisherStatus.Length > 0;
    public string PackagePublisherError
    {
        get => _packagePublisherError;
        private set { _packagePublisherError = value; Changed(); Changed(nameof(HasPackagePublisherError)); }
    }
    public bool HasPackagePublisherError => PackagePublisherError.Length > 0;
    public string PackagePublishResult
    {
        get => _packagePublishResult;
        private set { _packagePublishResult = value; Changed(); Changed(nameof(HasPackagePublishResult)); }
    }
    public bool HasPackagePublishResult => PackagePublishResult.Length > 0;
    public bool CanReplaceWebsiteSigningKey
    {
        get => _canReplaceWebsiteSigningKey && !IsExecuting;
        private set { if (_canReplaceWebsiteSigningKey == value) return; _canReplaceWebsiteSigningKey = value; Changed(); }
    }
    public IReadOnlyList<string> RoomNames { get => _roomNames; private set { _roomNames = value; Changed(); Changed(nameof(HasRoomPreview)); Changed(nameof(RoomSummary)); Changed(nameof(CanAddRoomToVeyon)); } }
    public IReadOnlyList<string> RoomPreviewRows { get => _roomPreviewRows; private set { _roomPreviewRows = value; Changed(); Changed(nameof(RoomSummary)); } }
    public bool HasRoomPreview => RoomNames.Count > 0;
    public string RoomError { get => _roomError; private set { _roomError = value; Changed(); Changed(nameof(HasRoomError)); Changed(nameof(CanAddRoomToVeyon)); } }
    public bool HasRoomError => RoomError.Length > 0;
    public string RoomSummary => HasRoomPreview
        ? $"{RoomLocationName.Trim()} · 共 {RoomNames.Count} 台 · 已填写 {GetNamedStudentCount()} 个显示名"
        : "尚未生成清单";
    public bool IsCheckingRoomConflicts
    {
        get => _isCheckingRoomConflicts;
        private set
        {
            if (_isCheckingRoomConflicts == value) return;
            _isCheckingRoomConflicts = value;
            Changed();
            Changed(nameof(CanCheckRoomConflicts));
            Changed(nameof(CanAddRoomToVeyon));
        }
    }
    public string RoomConflictStatus
    {
        get => _roomConflictStatus;
        private set
        {
            if (_roomConflictStatus == value) return;
            _roomConflictStatus = value;
            Changed();
            Changed(nameof(HasRoomConflictStatus));
            Changed(nameof(CanAddRoomToVeyon));
        }
    }
    public bool HasRoomConflictStatus => RoomConflictStatus.Length > 0;
    public string RoomConflictResults
    {
        get => _roomConflictResults;
        private set
        {
            if (_roomConflictResults == value) return;
            _roomConflictResults = value;
            Changed();
            Changed(nameof(HasRoomConflictResults));
            Changed(nameof(CanAddRoomToVeyon));
        }
    }
    public bool HasRoomConflictResults => RoomConflictResults.Length > 0;
    public string RoomSkippedResults
    {
        get => _roomSkippedResults;
        private set
        {
            if (_roomSkippedResults == value) return;
            _roomSkippedResults = value;
            Changed();
            Changed(nameof(HasRoomSkippedResults));
        }
    }
    public bool HasRoomSkippedResults => RoomSkippedResults.Length > 0;
    public string RoomCreateResult { get => _roomCreateResult; private set { _roomCreateResult = value; Changed(); Changed(nameof(HasRoomCreateResult)); } }
    public bool HasRoomCreateResult => RoomCreateResult.Length > 0;
    public string RoomCreateError { get => _roomCreateError; private set { _roomCreateError = value; Changed(); Changed(nameof(HasRoomCreateError)); } }
    public bool HasRoomCreateError => RoomCreateError.Length > 0;
    public string RoomCreateStatus { get => _roomCreateStatus; private set { _roomCreateStatus = value; Changed(); Changed(nameof(HasRoomCreateStatus)); } }
    public bool HasRoomCreateStatus => RoomCreateStatus.Length > 0;

    public void SelectPage(string page)
    {
        if (page is not ("classroom" or "updates" or "rooms" or "setup") || _selectedPage == page) return;
        _selectedPage = page;
        Changed(nameof(IsClassroomPage)); Changed(nameof(IsUpdatesPage)); Changed(nameof(IsRoomPage)); Changed(nameof(IsSetupPage));
        Changed(nameof(PageTitle)); Changed(nameof(PageDescription));
    }

    public void GenerateRoomPreview()
    {
        InvalidateRoomReview();
        RoomNames = Array.Empty<string>();
        RoomPreviewRows = Array.Empty<string>();
        RoomError = "";
        try
        {
            RoomNames = MachineNaming.CreateRange(RoomPrefix, RoomStart, RoomCount);
            UpdateRoomPreviewRows();
        }
        catch (InvalidDataException exception) { RoomError = exception.Message; }
    }

    public async Task InspectRoomConflictsAsync()
    {
        if (IsCheckingRoomConflicts || IsExecuting) return;
        if (!HasRoomPreview) GenerateRoomPreview();
        if (RoomError.Length > 0 || !HasRoomPreview)
        {
            RoomConflictStatus = RoomError.Length > 0 ? RoomError : "请先生成有效的电脑清单。";
            return;
        }
        if (string.IsNullOrWhiteSpace(RoomLocationName))
        {
            RoomConflictStatus = "请填写 Veyon 地点名称后再检查。";
            return;
        }

        VeyonNetworkComputer[] computers;
        try { computers = BuildRoomImportPlan(); }
        catch (InvalidDataException exception)
        {
            RoomError = exception.Message;
            RoomConflictStatus = "清单有误，请修正后重新检查。";
            return;
        }

        var revision = _roomPlanRevision;
        var locationName = RoomLocationName.Trim();
        IsCheckingRoomConflicts = true;
        _roomConflictCheckCompleted = false;
        _roomConflictCheckRevision = -1;
        _roomPlannedNewComputerCount = 0;
        RoomConflictStatus = "正在只读检查本机 Veyon 目录……";
        RoomConflictResults = "";
        RoomSkippedResults = "";
        try
        {
            var preview = await Task.Run(() => VeyonNetworkObjectDirectory.ReadImportPreview(locationName, computers));
            if (revision != _roomPlanRevision) return;
            _roomConflictCheckCompleted = true;
            _roomConflictCheckRevision = revision;
            _roomPlannedNewComputerCount = preview.ComputersToAdd.Count;
            RoomSkippedResults = string.Join(Environment.NewLine,
                preview.SkippedComputers.Select(message => "• " + message));
            if (preview.Conflicts.Count == 0 && _roomPlannedNewComputerCount > 0)
                RoomConflictStatus = $"检查通过：将{(preview.LocationExists ? "复用已有地点" : "新建地点")}，新增 {_roomPlannedNewComputerCount} 台、保留并跳过 {preview.SkippedComputers.Count} 台。添加时仍会重新检查。";
            else if (preview.Conflicts.Count == 0)
                RoomConflictStatus = $"没有新增项：计划中的 {preview.SkippedComputers.Count} 台都已存在；保留现有配置，未安排写入。";
            else
            {
                RoomConflictResults = string.Join(Environment.NewLine, preview.Conflicts.Select(message => "• " + message));
                RoomConflictStatus = $"发现 {preview.Conflicts.Count} 项清单内部冲突；已阻止写入，请修正后重查。";
            }
        }
        catch (Exception exception)
        {
            if (revision != _roomPlanRevision) return;
            _roomConflictCheckCompleted = false;
            _roomConflictCheckRevision = -1;
            RoomConflictStatus = "无法读取 Veyon 目录，检查未通过；没有修改任何内容：" + exception.Message;
        }
        finally { IsCheckingRoomConflicts = false; }
    }

    public void NewCampusProfile()
    {
        SelectedCampusProfile = null;
        CampusProfileName = "";
        CampusDirectoryStatus = "新增校区档案：填写名称后保存，即会生成本机稳定 ID。";
        CampusDirectoryError = "";
    }

    public void SaveCampusProfile()
    {
        var name = CampusProfileName.Trim();
        var selectedId = SelectedCampusProfile?.ProfileId;
        var campuses = CampusProfiles.ToList();
        var index = selectedId is null ? -1 : campuses.FindIndex(item => item.ProfileId == selectedId);
        var profile = new TeacherCampusProfile(selectedId ?? Guid.NewGuid(), name,
            index >= 0 ? campuses[index].Rooms : new List<TeacherRoomProfile>());
        if (index >= 0) campuses[index] = profile;
        else campuses.Add(profile);
        if (PersistCampusDirectory(campuses, "校区档案已保存；本机 ID：" + profile.StableIdLabel + "。"))
            SelectedCampusProfile = CampusProfiles.FirstOrDefault(item => item.ProfileId == profile.ProfileId);
    }

    public void DeleteSelectedCampusProfile()
    {
        if (SelectedCampusProfile is not { } selected) return;
        var campuses = CampusProfiles.Where(item => item.ProfileId != selected.ProfileId).ToArray();
        if (PersistCampusDirectory(campuses, "校区档案已从本机删除。"))
        {
            SelectedCampusProfile = null;
            CampusProfileName = "";
            SelectedRoomProfile = null;
        }
    }

    public void NewRoomProfile()
    {
        if (SelectedCampusProfile is null)
        {
            CampusDirectoryError = "请先选择或保存一个校区档案。";
            return;
        }
        SelectedRoomProfile = null;
        CampusDirectoryStatus = "新增机房档案：保存仅记录名称和电脑编号规划，不会添加到 Veyon。";
        CampusDirectoryError = "";
    }

    public void SaveRoomProfile()
    {
        if (SelectedCampusProfile is not { } campus)
        {
            CampusDirectoryError = "请先选择或保存一个校区档案。";
            return;
        }
        var rooms = campus.Rooms.ToList();
        var selectedId = SelectedRoomProfile?.RoomId;
        var index = selectedId is null ? -1 : rooms.FindIndex(item => item.RoomId == selectedId);
        try
        {
            var range = MachineNaming.CreateRange(RoomProfilePrefix, RoomProfileStart, RoomProfileCount);
            var hostOverrides = ParseHostOverrides(RoomProfileHostOverrides, range.Count);
            var room = new TeacherRoomProfile(selectedId ?? Guid.NewGuid(), RoomProfileName.Trim(),
                RoomProfilePrefix, int.Parse(RoomProfileStart, CultureInfo.InvariantCulture), range.Count,
                hostOverrides.ToList());
            if (index >= 0) rooms[index] = room;
            else rooms.Add(room);
            var campuses = CampusProfiles.Select(item => item.ProfileId == campus.ProfileId
                ? item with { Rooms = rooms }
                : item).ToArray();
            if (PersistCampusDirectory(campuses, "机房档案已保存；本机 ID：" + room.StableIdLabel + "。"))
            {
                SelectedCampusProfile = CampusProfiles.FirstOrDefault(item => item.ProfileId == campus.ProfileId);
                SelectedRoomProfile = RoomProfiles.FirstOrDefault(item => item.RoomId == room.RoomId);
                LoadSelectedSeatLayout();
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or OverflowException)
        {
            CampusDirectoryError = "机房档案无效：" + exception.Message;
        }
    }

    public void DeleteSelectedRoomProfile()
    {
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } selected) return;
        var campuses = CampusProfiles.Select(item => item.ProfileId == campus.ProfileId
            ? item with { Rooms = item.Rooms.Where(room => room.RoomId != selected.RoomId).ToList() }
            : item).ToArray();
        if (PersistCampusDirectory(campuses, "机房档案已从本机删除。"))
        {
            SelectedCampusProfile = CampusProfiles.FirstOrDefault(item => item.ProfileId == campus.ProfileId);
            SelectedRoomProfile = null;
        }
    }

    public void LoadSelectedRoomProfileIntoBuilder()
    {
        if (SelectedRoomProfile is not { } room) return;
        RoomLocationName = room.DisplayName;
        RoomPrefix = room.Prefix;
        RoomStart = room.StartNumber.ToString(CultureInfo.InvariantCulture);
        RoomCount = room.ComputerCount.ToString(CultureInfo.InvariantCulture);
        StudentRoster = "";
        RoomComputerHosts = string.Join(Environment.NewLine, room.HostOverrides ?? []);
        CampusDirectoryStatus = "已载入机房名称、电脑编号和可选主机/IP 规划；姓名未保存，请检查后预览。此操作尚未修改 Veyon。";
        CampusDirectoryError = "";
    }

    public async Task AddRoomToVeyonAsync()
    {
        if (!CanAddRoomToVeyon) return;
        if (RoomError.Length > 0 || !HasRoomPreview)
        {
            RoomCreateError = RoomError.Length > 0 ? RoomError : "请先填写有效的电脑名前缀、起始编号和电脑数量。";
            return;
        }
        if (!TryBeginExclusiveTask()) return;
        RoomCreateResult = "";
        RoomCreateError = "";
        try
        {
            var locationName = RoomLocationName.Trim();
            var computers = BuildRoomImportPlan();
            RoomCreateStatus = "正在检查 Veyon 版本、权限和现有地点……";
            var veyon = await Task.Run(VeyonFacts.Probe);
            if (veyon.Status != "installed" || !VeyonFacts.IsSupportedVersionDetail(veyon.VersionDetail))
            {
                RoomCreateError = $"需要先安装并确认 Veyon {VeyonInstallerTrust.Version}。没有更改目录。\n{veyon.AsText()}";
                return;
            }
            RoomCreateStatus = "正在写入地点和电脑清单并读回核对……";
            var response = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                (requestId, caller) => new PrivilegedWorkerRequest(
                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                    PrivilegedWorkerOperation.AddVeyonRoom,
                    LocationName: locationName, Computers: computers));
            if (response.Result.Ok) RoomCreateResult = response.Result.Detail + "\n此结果只确认本机静态目录写入及读回，不代表电脑在线或 Veyon 连接成功。";
            else RoomCreateError = "添加地点未完成：" + response.Result.Detail;
        }
        catch (Exception exception)
        {
            RoomCreateError = "添加地点未完成：" + exception.Message;
        }
        finally
        {
            RoomCreateStatus = "";
            EndExclusiveTask();
        }
    }

    public void OpenVeyonConfigurator()
    {
        RoomCreateError = "";
        ConfiguratorLaunchError = "";
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Veyon", "veyon-configurator.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Veyon", "veyon-configurator.exe")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            ConfiguratorLaunchError = "找不到 Veyon Configurator；请先安装固定版本 Veyon。";
            RoomCreateError = ConfiguratorLaunchError;
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            ConfiguratorLaunchError = "无法打开 Veyon Configurator：" + DescribeWindowsLaunchError(exception);
            RoomCreateError = ConfiguratorLaunchError;
        }
    }

    private static string DescribeWindowsLaunchError(Exception exception)
    {
        if (exception is System.ComponentModel.Win32Exception { NativeErrorCode: 740 } ||
            exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 740 })
            return "此操作需要管理员权限。请确认 Windows 管理员权限提示后重试。";
        if (exception is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ||
            exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 })
            return "已取消管理员权限请求；程序没有启动。";
        return exception.Message;
    }

    public void FillWebsiteTargetsFromRoom()
    {
        try
        {
            var names = RoomNames.Count > 0 ? RoomNames : MachineNaming.CreateRange(RoomPrefix, RoomStart, RoomCount);
            WebsiteTargets = string.Join(Environment.NewLine, names);
            RoomError = "";
            WebsiteDirectoryStatus = $"已按电脑名前缀规则填入 {names.Count} 台目标；确认后再推送。";
            WebsiteDirectoryError = "";
        }
        catch (InvalidDataException exception) { RoomError = exception.Message; }
    }

    public async Task ReadWebsiteLocationsAsync()
    {
        if (IsReadingWebsiteLocations || IsExecuting) return;
        IsReadingWebsiteLocations = true;
        WebsiteLocations = Array.Empty<VeyonNetworkLocation>();
        WebsiteLocationIndex = -1;
        _websiteLocationSelectionPending = false;
        WebsiteDirectoryStatus = "正在读取 Veyon 当前机房目录……";
        WebsiteDirectoryError = "";
        try
        {
            ReadWebsiteSigningCampuses();
            var locations = await Task.Run(VeyonNetworkObjectDirectory.ReadLocations);
            WebsiteLocations = locations;
            if (locations.Count == 0)
            {
                WebsiteDirectoryStatus = "";
                WebsiteDirectoryError = "没有从 Veyon 内置电脑目录读到目标。请先在 Veyon Configurator 配置机房电脑，或手动填写目标；LDAP/动态目录暂不支持此读取方式。";
                return;
            }

            if (locations.Count == 1)
            {
                WebsiteLocationIndex = 0;
                FillWebsiteTargetsFromSelectedLocation();
                return;
            }

            WebsiteDirectoryStatus = $"已读取到 {locations.Count} 个机房；请选择要接收策略的机房，再填入目标。";
            _websiteLocationSelectionPending = true;
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or TimeoutException or PlatformNotSupportedException or
                                          System.ComponentModel.Win32Exception)
        {
            WebsiteDirectoryStatus = "";
            WebsiteDirectoryError = "读取 Veyon 机房目录失败：" + exception.Message;
        }
        finally { IsReadingWebsiteLocations = false; }
    }

    public void FillWebsiteTargetsFromSelectedLocation()
    {
        if (WebsiteLocationIndex < 0 || WebsiteLocationIndex >= WebsiteLocations.Count) return;
        var location = WebsiteLocations[WebsiteLocationIndex];
        var selectionWasPending = _websiteLocationSelectionPending;
        _websiteLocationSelectionPending = false;
        if (selectionWasPending)
        {
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
        }
        WebsiteTargets = string.Join(Environment.NewLine, location.Targets);
        var campusStatus = string.IsNullOrWhiteSpace(CampusId)
            ? "请选择签名密钥所属校区，或填写生成学生配置包时使用的校区名称 / ID。"
            : $"使用签名密钥所属校区“{CampusId.Trim()}”。";
        WebsiteDirectoryStatus = $"已填入“{location.Name}”的 {location.Targets.Count} 台电脑。{campusStatus}请核对后再推送。";
        WebsiteDirectoryError = "";
    }

    public Task PushWebsitePolicyAsync() => PushWebsitePolicyAsync(SelectedWebsiteMode);

    public Task PushApplicationPolicyAsync() => PushApplicationPolicyAsync(SelectedApplicationPolicyMode);

    public async Task PushApplicationPolicyAsync(ApplicationPolicyMode mode)
    {
        if (!TryBeginExclusiveTask()) return;
        ApplicationPolicyResult = "";
        ApplicationPolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("应用策略签发仅支持 Windows 教师端。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var now = DateTimeOffset.UtcNow;
            var rules = mode == ApplicationPolicyMode.Disabled ? Array.Empty<ApplicationDenyRule>() : ParseApplicationRules();
            var sids = mode == ApplicationPolicyMode.Disabled ? Array.Empty<string>() : ParseStudentSids();
            DateTimeOffset? expiry = mode == ApplicationPolicyMode.Disabled ? null : now + ApplicationPolicyLifetime();
            var revision = WebsitePolicyRevisionStore.Next(campus);
            var policy = ApplicationPolicyCompiler.Validate(new ApplicationPolicyDocument(1,
                ApplicationPolicyCompiler.Purpose, campus, revision, now, expiry, mode, sids, rules));
            if (mode == ApplicationPolicyMode.Enforce && !ApplicationEnforcementReviewed)
                throw new InvalidDataException("请先核对最近的审核事件，并勾选执行前确认。");
            using var signingKey = ApplicationPolicySigningKeyStore.Open(campus);
            var accounts = StudentSidsByTarget(targets, mode == ApplicationPolicyMode.Disabled);
            var signedByTarget = targets.ToDictionary(t => t, t => ApplicationPolicyCryptography.Sign(
                policy with { StudentSids = accounts[t] }, signingKey.PrivateKey));
            var results = await SendByTargetAsync(targets, t => ApplicationPolicyTransport.PushAsync([t], signedByTarget[t], campus));
            var succeeded = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count(result => !result.Succeeded && !result.NeedsReview);
            var expirySummary = expiry is { } until ? $" · 自动解除 {until.ToLocalTime():yyyy-MM-dd HH:mm}" : "";
            var modeText = mode switch
            {
                ApplicationPolicyMode.Audit => "审核（不拦截）",
                ApplicationPolicyMode.Enforce => "阻止",
                _ => "已解除"
            };
            ApplicationPolicyResult = $"版本 {revision} · {modeText}{expirySummary} · 已确认 {succeeded}/{results.Count} · 待核对 {needsReview} · 失败 {failed}";
            ApplicationPolicyResultDetails = string.Join(Environment.NewLine, results.Select(result =>
                $"{result.Target}：{(result.Succeeded ? "Agent 已确认" : result.NeedsReview ? "需核对" : "失败")} — {result.Detail}"));
            ShowApplicationPolicyResultDetails = false;
            _lastApplicationAuditPolicyRevision = mode == ApplicationPolicyMode.Audit && succeeded == results.Count ? revision : null;
            _lastApplicationAuditFingerprint = _lastApplicationAuditPolicyRevision is null ? null : GetApplicationPolicyFingerprint();
            _hasMatchingApplicationAudit = false;
            Changed(nameof(HasMatchingApplicationAudit));
            Changed(nameof(CanPushApplicationPolicy));
            Changed(nameof(ApplicationPolicyPreview));
            var history = new ApplicationPolicyPushHistoryEntry(DateTimeOffset.UtcNow, campus, revision, mode,
                rules.Length, sids.Length, results);
            UpdateApplicationPolicyHistory(history);
            var errors = new List<string>();
            try { ApplicationPolicyPushHistoryStore.Append(history); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                              ArgumentException or System.Text.Json.JsonException)
            { errors.Add("本次结果仍可查看，但本机历史记录未保存：" + exception.Message); }
            if (succeeded != results.Count)
                errors.Add("部分设备未确认策略状态；检查逐台结果后再重试。重试会使用新版本号。");
            if (errors.Count > 0) ApplicationPolicyError = string.Join(" ", errors);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            ApplicationPolicyError = "应用策略未推送：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    public Task DisableApplicationPolicyAsync() => PushApplicationPolicyAsync(ApplicationPolicyMode.Disabled);

    public Task PushStudentSystemPolicyAsync() => PushStudentSystemPolicyAsync(disabled: false);
    public Task DisableStudentSystemPolicyAsync() => PushStudentSystemPolicyAsync(disabled: true);

    private async Task PushStudentSystemPolicyAsync(bool disabled)
    {
        if (!TryBeginExclusiveTask()) return;
        StudentSystemPolicyResult = "";
        StudentSystemPolicyDetails = "";
        StudentSystemPolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("学生机系统策略签发仅支持 Windows 教师端。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(
                WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var settings = disabled ? new StudentSystemPolicySettings(false, false, false, false, false, false) :
                new StudentSystemPolicySettings(StudentSystemPolicyLockWallpaper,
                    StudentSystemPolicyProhibitTimeChanges, StudentSystemPolicyProhibitNetworkChanges,
                    StudentSystemPolicyProhibitSoftwareInstallation, StudentSystemPolicyProhibitAccountManagement,
                    StudentSystemPolicyProhibitControlPanel);
            if (!disabled && settings.IsEmpty)
                throw new InvalidDataException("至少选择一项学生机系统限制；如需撤销，请使用“解除”按钮。");
            if (!disabled && settings.ProhibitSoftwareInstallation && !_studentSystemPolicySoftwareInstallReviewed)
                throw new InvalidDataException("请先阅读软件安装限制的影响说明并勾选确认。");
            var sids = disabled ? Array.Empty<string>() : ParseStudentSids();
            var revision = StudentSystemPolicySigningKeyStore.NextRevision(campus);
            var policy = StudentSystemPolicyCompiler.Create(campus, revision, sids, settings);
            using var signingKey = StudentSystemPolicySigningKeyStore.Open(campus);
            var accounts = StudentSidsByTarget(targets, disabled);
            var signedByTarget = targets.ToDictionary(t => t, t => StudentSystemPolicyCryptography.Sign(
                StudentSystemPolicyCompiler.Create(campus, revision, accounts[t], settings), signingKey.PrivateKey));
            var results = await SendByTargetAsync(targets, t => StudentSystemPolicyTransport.PushAsync([t], signedByTarget[t], campus));
            var succeeded = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count(result => !result.Succeeded && !result.NeedsReview);
            var mode = disabled ? "已解除" : "长期系统基线";
            StudentSystemPolicyResult = $"版本 {revision} · {mode} · Agent 已确认 {succeeded}/{results.Count} · 待核对 {needsReview} · 失败 {failed}";
            StudentSystemPolicyDetails = string.Join(Environment.NewLine, results.Select(result =>
                $"{result.Target}：{(result.Succeeded ? "Agent 已读回接受" : result.NeedsReview ? "需现场核对" : "失败")} — {result.Detail}"));
            if (succeeded != results.Count)
                StudentSystemPolicyError = "部分电脑未确认。失败或待核对状态不表示限制已生效；检查逐台结果后再重试。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            StudentSystemPolicyError = "系统策略未推送：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    public async Task ReadApplicationInventoryAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        ApplicationInventoryStatus = "正在从所选学生电脑读取已安装程序清单……";
        ApplicationPolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("应用清单读取仅支持 Windows 教师端。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            using var signingKey = ApplicationPolicySigningKeyStore.Open(campus);
            var results = await ApplicationPolicyTransport.ReadInventoryAsync(targets, campus, signingKey.PrivateKey);
            if (campus != CampusId.Trim() || !targets.SequenceEqual(WebsitePolicyTransport.NormalizeTargets(
                    WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)), StringComparer.OrdinalIgnoreCase))
            { ApplicationInventoryStatus = "校区或目标已变化，请重新读取软件。"; return; }
            var rawChoices = results.Where(result => result.Succeeded)
                .SelectMany(result => result.Items.Select(item => new ApplicationInventoryChoice(result.Target, item)))
                .ToArray();
            var unsupportedCount = rawChoices.Count(item => !item.CanSelect);
            _allApplicationInventoryChoices = rawChoices.GroupBy(item => item.RuleLine is { } rule
                    ? "rule|" + rule
                    : "info|" + item.DisplayName + "|" + item.BinaryName + "|" + item.Publisher + "|" + item.Item.ProductName,
                    StringComparer.Ordinal)
                .Select(group =>
                {
                    var first = group.First();
                    var devices = group.Select(item => item.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var shownDevices = devices.Take(6).ToArray();
                    var targetSummary = string.Join(", ", shownDevices) + (devices.Length > shownDevices.Length ? " 等" : "");
                    return new ApplicationInventoryChoice(targetSummary, first.Item, devices.Length);
                })
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Target, StringComparer.OrdinalIgnoreCase).ToArray();
            SetApplicationInventoryChoices(_allApplicationInventoryChoices);
            var successful = results.Count(result => result.Succeeded);
            var itemCount = results.Sum(result => result.Items.Count);
            var errors = results.Where(result => !result.Succeeded).Select(result =>
                $"{result.Target}: {result.Detail}").ToArray();
            ApplicationInventoryStatus = $"已读取 {successful}/{results.Count} 台电脑，共 {itemCount} 个程序条目，归并为 {_allApplicationInventoryChoices.Count} 种程序；{unsupportedCount} 个条目没有可用规则条件。请勾选要禁用的程序。升级软件后需要重新读取和勾选；此列表不是当前禁用状态。" +
                                         (errors.Length == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException or
                                          System.ComponentModel.Win32Exception or TimeoutException)
        {
            _allApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
            ApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
            ApplicationInventoryStatus = "读取应用清单失败：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    public void AddSelectedApplicationRules()
    {
        if (!CanAddSelectedApplicationRules) return;
        var existing = ApplicationRules.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);
        var added = _allApplicationInventoryChoices.Where(item => item.IsSelected && item.RuleLine is not null)
            .Select(item => item.RuleLine!).Distinct(StringComparer.Ordinal).Where(line => existing.Add(line)).ToArray();
        if (added.Length == 0)
        {
            ApplicationInventoryStatus = "所选规则已存在于规则框中；没有重复添加。";
            return;
        }
        ApplicationRules = string.Join(Environment.NewLine,
            new[] { ApplicationRules.TrimEnd(), string.Join(Environment.NewLine, added) }.Where(text => text.Length > 0));
        ApplicationInventoryStatus = $"已加入 {added.Length} 条应用规则。发布者规则限定当前精确版本；确认影响后再扩展版本范围。";
    }

    public async Task ReadApplicationPolicyAuditAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        ApplicationAuditResult = "正在读取最近 24 小时的 AppLocker 审核事件……";
        ApplicationPolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("应用策略审计读取仅支持 Windows 教师端。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            using var signingKey = ApplicationPolicySigningKeyStore.Open(campus);
            var results = await ApplicationPolicyTransport.ReadAuditAsync(targets, campus, signingKey.PrivateKey, 24);
            var currentFingerprint = TryGetApplicationPolicyFingerprint();
            _hasMatchingApplicationAudit = currentFingerprint is not null &&
                _lastApplicationAuditFingerprint == currentFingerprint && _lastApplicationAuditPolicyRevision is not null &&
                results.Count == targets.Count && results.All(result => result.Succeeded && result.Response is
                    { Mode: ApplicationPolicyMode.Audit } response && response.PolicyRevision == _lastApplicationAuditPolicyRevision);
            Changed(nameof(HasMatchingApplicationAudit));
            Changed(nameof(CanPushApplicationPolicy));
            Changed(nameof(ApplicationPolicyPreview));
            var lines = new List<string>();
            foreach (var result in results)
            {
                if (!result.Succeeded || result.Response is null)
                {
                    lines.Add($"{result.Target}：{(result.NeedsReview ? "需核对" : "失败")} — {result.Detail}");
                    continue;
                }
                var response = result.Response;
                var wouldBlock = response.Results.Sum(item => item.WouldBlockCount);
                var blocked = response.Results.Sum(item => item.BlockedCount);
                lines.Add(response.IsSimulation
                    ? $"{result.Target}：策略 v{response.PolicyRevision?.ToString(CultureInfo.InvariantCulture) ?? "无"} · 影响模拟 · 预计规则命中 {wouldBlock}（启动日志/实际阻止 {blocked}）"
                    : $"{result.Target}：策略 v{response.PolicyRevision?.ToString(CultureInfo.InvariantCulture) ?? "无"} · {response.Mode?.ToString() ?? "无策略"} · 审核命中 {wouldBlock} · 已阻止 {blocked}");
                lines.AddRange(response.Results.Select(item => response.IsSimulation
                    ? $"  {item.DisplayName} · SID …{item.StudentSid[(item.StudentSid.LastIndexOf('-') + 1)..]} · 预计命中 {item.WouldBlockCount}"
                    : $"  {item.DisplayName} · SID …{item.StudentSid[(item.StudentSid.LastIndexOf('-') + 1)..]} · 审核 {item.WouldBlockCount} · 阻止 {item.BlockedCount}"));
                if (!string.IsNullOrWhiteSpace(response.CoverageNote)) lines.Add("  " + response.CoverageNote);
                if (response.Results.Count == 0)
                    lines.Add(response.IsSimulation
                        ? "  当前已登记程序清单中没有发现匹配规则的程序条目。"
                        : "  最近 24 小时没有匹配当前策略的 AppLocker 事件。");
            }
            ApplicationAuditResult = "应用策略审核/影响模拟结果（仅包括当前策略已知规则；成功回执已核对 Agent 身份签名和本次请求；仍需人工复核实际影响）：" +
                                     Environment.NewLine + string.Join(Environment.NewLine, lines);
            if (!_hasMatchingApplicationAudit)
                ApplicationAuditResult += Environment.NewLine + "本次回执未能确认所有目标仍运行刚推送的同一审核策略；执行模式保持锁定。请在审核模式下重新推送并读取全部设备。";
            else if (results.All(result => result.Response?.IsSimulation == true))
                ApplicationAuditResult += Environment.NewLine + "全部目标仍运行刚推送的同一策略版本。上方是已登记程序清单的影响模拟，不包含启动历史；核对后再勾选执行确认。";
            else
                ApplicationAuditResult += Environment.NewLine + "已核对：全部目标仍运行刚推送的审核策略。阅读上方命中后，再勾选执行确认。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            ApplicationAuditResult = "应用策略审计未读取：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    private ApplicationPolicyMode SelectedApplicationPolicyMode =>
        ApplicationPolicyModeIndex == 0 ? ApplicationPolicyMode.Audit : ApplicationPolicyMode.Enforce;

    public MobilePolicyProfile SaveWebsiteMobileProfile(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("手机策略预设仅能从 Windows 教师端保存。");
        var campus = CampusId.Trim();
        WebsitePolicySigningKeyStore.ValidateCampusId(campus);
        var lifetimeMinutes = WebsiteDurationIndex switch
        {
            0 => 45,
            1 => 60,
            2 => 90,
            3 => 120,
            4 => 0,
            _ => throw new InvalidDataException("网站预设期限无效。")
        };
        var existing = MobilePolicyProfileStore.ReadAll()
            .FirstOrDefault(profile => profile.Kind == MobilePolicyProfileKind.Website &&
                                       profile.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                       profile.CampusId == campus);
        var profile = MobilePolicyProfileCompiler.Validate(new MobilePolicyProfile(
            existing?.Id ?? Guid.NewGuid(), name, campus, MobilePolicyProfileKind.Website, lifetimeMinutes,
            WebsiteMode: SelectedWebsiteMode,
            WebsiteDomains: WebsiteDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            UpdatedUtc: DateTimeOffset.UtcNow));
        MobilePolicyProfileStore.Save(profile);
        return profile;
    }

    public MobilePolicyProfile SaveApplicationMobileProfile(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("手机策略预设仅能从 Windows 教师端保存。");
        var campus = CampusId.Trim();
        WebsitePolicySigningKeyStore.ValidateCampusId(campus);
        var lifetimeMinutes = ApplicationPolicyDurationIndex switch
        {
            0 => 45,
            1 => 60,
            2 => 90,
            3 => 120,
            4 => 1440,
            _ => throw new InvalidDataException("应用预设期限无效。")
        };
        var existing = MobilePolicyProfileStore.ReadAll()
            .FirstOrDefault(profile => profile.Kind == MobilePolicyProfileKind.Application &&
                                       profile.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                       profile.CampusId == campus);
        var profile = MobilePolicyProfileCompiler.Validate(new MobilePolicyProfile(
            existing?.Id ?? Guid.NewGuid(), name, campus, MobilePolicyProfileKind.Application, lifetimeMinutes,
            ApplicationMode: SelectedApplicationPolicyMode,
            StudentSids: ParseMobileStudentSids(), ApplicationRules: ParseApplicationRules(),
            UpdatedUtc: DateTimeOffset.UtcNow));
        MobilePolicyProfileStore.Save(profile);
        return profile;
    }

    public MobilePolicyProfile SaveStudentSystemMobileProfile(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("手机策略预设仅能从 Windows 教师端保存。");
        var campus = CampusId.Trim();
        WebsitePolicySigningKeyStore.ValidateCampusId(campus);
        var settings = new StudentSystemPolicySettings(StudentSystemPolicyLockWallpaper,
            StudentSystemPolicyProhibitTimeChanges, StudentSystemPolicyProhibitNetworkChanges,
            StudentSystemPolicyProhibitSoftwareInstallation, StudentSystemPolicyProhibitAccountManagement,
            StudentSystemPolicyProhibitControlPanel);
        if (settings.IsEmpty)
            throw new InvalidDataException("至少选择一项长期系统限制后再保存手机预设。");
        if (settings.ProhibitSoftwareInstallation && !_studentSystemPolicySoftwareInstallReviewed)
            throw new InvalidDataException("请先阅读软件安装限制的影响说明并勾选确认，再保存手机预设。");
        var studentSids = ParseMobileStudentSids();
        _ = StudentSystemPolicyCompiler.Create(campus, 1, studentSids, settings);
        var existing = MobilePolicyProfileStore.ReadAll()
            .FirstOrDefault(profile => profile.Kind == MobilePolicyProfileKind.System &&
                                       profile.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                       profile.CampusId == campus);
        var profile = MobilePolicyProfileCompiler.Validate(new MobilePolicyProfile(
            existing?.Id ?? Guid.NewGuid(), name, campus, MobilePolicyProfileKind.System, 0,
            StudentSids: studentSids, UpdatedUtc: DateTimeOffset.UtcNow,
            SystemSettings: settings,
            SoftwareInstallationImpactReviewed: settings.ProhibitSoftwareInstallation &&
                                                  _studentSystemPolicySoftwareInstallReviewed));
        MobilePolicyProfileStore.Save(profile);
        return profile;
    }

    public IReadOnlyList<MobilePolicyProfile> ReadMobilePolicyProfiles() => MobilePolicyProfileStore.ReadAll();

    public bool DeleteMobilePolicyProfile(Guid id) => MobilePolicyProfileStore.Remove(id);

    private TimeSpan ApplicationPolicyLifetime() => ApplicationPolicyDurationIndex switch
    {
        0 => TimeSpan.FromMinutes(45),
        1 => TimeSpan.FromHours(1),
        2 => TimeSpan.FromMinutes(90),
        3 => TimeSpan.FromHours(2),
        4 => TimeSpan.FromHours(24),
        _ => throw new InvalidDataException("应用限制有效时长无效。")
    };

    private string[] ParseMobileStudentSids()
    {
        if (!UseManualApplicationInputs && WebsitePolicyTransport.NormalizeTargets(
                WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Count > 1)
            throw new InvalidDataException("手机账户策略预设请先选择单台学生电脑，避免将不同电脑的账户混用。");
        return ParseStudentSids();
    }

    private string[] ParseStudentSids()
    {
        if (!UseManualApplicationInputs)
        {
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var values = StudentSidsByTarget(targets, disabled: false).Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length > ApplicationPolicyCompiler.MaximumStudents) throw new InvalidDataException("一次最多选择 150 个学生账户。");
            return values;
        }
        var sids = ApplicationStudentSids.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        if (sids.Length is 0 or > ApplicationPolicyCompiler.MaximumStudents ||
            sids.Distinct(StringComparer.Ordinal).Count() != sids.Length)
            throw new InvalidDataException("请填写 1–150 个不重复的学生账户 SID，每行一个。");
        return sids;
    }

    private ApplicationDenyRule[] ParseApplicationRules()
    {
        var rows = ApplicationRules.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (rows.Length is 0 or > ApplicationPolicyCompiler.MaximumRules)
            throw new InvalidDataException("请填写 1–200 条应用规则，每行一条。");
        var rules = new List<ApplicationDenyRule>(rows.Length);
        foreach (var row in rows)
        {
            var fields = row.Split('|').Select(field => field.Trim()).ToArray();
            if (fields[0] == "publisher" && fields.Length == 7)
                rules.Add(new ApplicationDenyRule(StableApplicationRuleId(row), ApplicationRuleKind.Publisher,
                    fields[1], fields[2], fields[3], fields[4], fields[5], fields[6]));
            else if (fields[0] == "hash" && fields.Length == 6 &&
                     long.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                rules.Add(new ApplicationDenyRule(StableApplicationRuleId(row), ApplicationRuleKind.Hash,
                    fields[1], SourceFileName: fields[2], FileSha256: fields[3],
                    AppLockerHashSha256: fields[4], SourceFileLength: length));
            else
                throw new InvalidDataException("规则格式无效。发布者格式：publisher|显示名称|发布者名|产品名|文件.exe|最低版本|最高版本；哈希格式：hash|显示名称|文件.exe|文件SHA256|AppLocker SHA256|文件长度。");
        }
        return rules.ToArray();
    }

    private static Guid StableApplicationRuleId(string value) =>
        new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    public async Task DeployStudentUpdateAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            StudentUpdateStatus = "学生端静默更新仅支持 Windows。";
            return;
        }
        if (!CanDeployStudentUpdate || !TryBeginExclusiveTask()) return;
        StudentUpdateStatus = "正在检查并验证最新 StudentSetup 发布……";
        string? targetVersion = null;
        try
        {
            var releaseClient = _releaseClient ?? throw new InvalidOperationException(
                _releaseClientError ?? "此版本没有固定的发布签名公钥，已安全停用学生更新。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(
                WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var latest = await releaseClient.CheckLatestAsync(ApplicationReleaseRole.StudentSetup, "0.0.0");
            var release = latest.Release ?? throw new InvalidOperationException("云端没有已发布的 StudentSetup 版本。");
            targetVersion = release.Manifest.Version;
            ApplicationReleaseCompatibility.EnsureSupports(release.Manifest,
                applicationPolicyRequired: true, studentSystemPolicyRequired: true);
            var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "Updates");
            StudentUpdateStatus = $"正在下载并校验 StudentSetup {release.Manifest.Version} 安装器……";
            var installerPath = await releaseClient.DownloadAsync(release, ApplicationReleaseRole.StudentSetup,
                updateDirectory);
            var verifiedRelease = ApplicationReleaseClient.ReadVerifiedStagedRelease(installerPath,
                ApplicationReleaseRole.StudentSetup, releaseClient.ApiBaseAddress,
                ApplicationReleaseTrust.LoadPinnedPublicKeyPem());
            await using var lanServer = StudentApplicationUpdateLanServer.Start(installerPath);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var developerPublicKey = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
            var deliveries = await StudentApplicationUpdateTransport.PushAsync(targets, campus,
                verifiedRelease.Manifest.Version, signingKey.PrivateKey, async (target, token) =>
            {
                var downloadUri = await lanServer.GetDownloadUriAsync(target, token);
                var issuedUtc = DateTimeOffset.UtcNow;
                var command = new StudentApplicationUpdateCommand(1, campus, Guid.NewGuid(), issuedUtc,
                    issuedUtc + StudentApplicationUpdateCryptography.MaximumCommandLifetime, verifiedRelease,
                    downloadUri.AbsoluteUri);
                lock (signingKey.PrivateKey)
                    return (command.CommandId, StudentApplicationUpdateCryptography.Sign(command,
                        signingKey.PrivateKey, releaseClient.ApiBaseAddress, developerPublicKey));
            });
            var succeeded = deliveries.Count(result => result.Succeeded);
            var needsReview = deliveries.Count(result => result.NeedsReview);
            var failed = deliveries.Count - succeeded - needsReview;
            RecordUpdateSuccess(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.StudentRollout,
                verifiedRelease.Manifest.Version, new UpdateDiagnosticCounts(succeeded, needsReview, failed));
            StudentUpdateStatus = $"StudentSetup {verifiedRelease.Manifest.Version} · 已确认 {succeeded}/{deliveries.Count} 台 · 需核对 {needsReview} 台" +
                                  Environment.NewLine + string.Join(Environment.NewLine,
                                      deliveries.Select(result =>
                                          $"{result.Target}：{(result.Succeeded ? "已读回安装版本" : result.NeedsReview ? "需核对" : "失败")} — {result.Detail}" +
                                          (result.IdentityCandidate is { } candidate ? $" · Agent 指纹 {candidate.Fingerprint}" : "")));
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.StudentRollout, targetVersion, exception);
            StudentUpdateStatus = "学生静默更新未完成；请核对逐台状态后再重试：" + failure.ToUserMessage();
        }
        finally { EndExclusiveTask(); }
    }

    private async Task PushWebsitePolicyAsync(WebsitePolicyMode mode)
    {
        if (!TryBeginExclusiveTask()) return;
        WebsitePolicyResult = "";
        WebsitePolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("网站策略推送仅支持 Windows 教师端。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var domains = mode == WebsitePolicyMode.Disabled
                ? Array.Empty<string>()
                : WebsiteDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var issuedUtc = DateTimeOffset.UtcNow;
            TimeSpan? duration = mode == WebsitePolicyMode.Disabled ? null : WebsiteDurationIndex switch
            {
                0 => TimeSpan.FromMinutes(45),
                1 => TimeSpan.FromHours(1),
                2 => TimeSpan.FromMinutes(90),
                3 => TimeSpan.FromHours(2),
                4 => null,
                _ => throw new InvalidDataException("网站限制时长无效。")
            };
            DateTimeOffset? expiresUtc = duration is { } lifetime ? issuedUtc + lifetime : null;
            var revision = WebsitePolicyRevisionStore.Next(campus);
            var policy = WebsitePolicyCompiler.Create(campus, revision, mode, domains, issuedUtc, expiresUtc);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var signedPolicy = WebsitePolicyCryptography.Sign(policy, signingKey.PrivateKey);
            var results = await WebsitePolicyTransport.PushAsync(targets, signedPolicy, campus);
            var succeeded = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count(result => !result.Succeeded && !result.NeedsReview);
            var expirySummary = expiresUtc is { } expiry
                ? $" · 自动解除 {expiry.ToLocalTime():yyyy-MM-dd HH:mm}"
                : mode == WebsitePolicyMode.Disabled ? "" : " · 不自动到期";
            var restartNotice = succeeded > 0 ? " · 请等待 15 秒后重启学生端浏览器，使策略生效。" : "";
            WebsitePolicyResult = $"版本 {revision} · {mode switch { WebsitePolicyMode.Disabled => "已解除", WebsitePolicyMode.Blocklist => "黑名单", _ => "白名单" }}{expirySummary} · 已确认 {succeeded}/{results.Count} · 报告已应用但身份待核对 {results.Count(r => r.ReportedApplied)} · 其他待核对 {needsReview - results.Count(r => r.ReportedApplied)} · 拒绝 {failed}{restartNotice}";
            WebsitePolicyResultDetails = string.Join(Environment.NewLine,
                results.Select(result => $"{result.Target}：{(result.StatusLabel)} — {result.Detail}"));
            ShowWebsitePolicyResultDetails = needsReview > 0 || failed > 0;
            var history = new WebsitePolicyPushHistoryEntry(DateTimeOffset.UtcNow, campus, revision, mode, expiresUtc, results);
            UpdateWebsitePolicyHistory(history);
            var errorMessages = new List<string>();
            try { WebsitePolicyPushHistoryStore.Append(history); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                              ArgumentException or System.Text.Json.JsonException)
            {
                errorMessages.Add("本次推送结果已显示，但本机历史记录未保存：" + exception.Message);
            }
            if (succeeded != results.Count)
                errorMessages.Add(string.Join(" ", new[]
                {
                    results.Any(r => r.ReportedApplied) ? "学生机报告已应用；请点击“核对学生电脑身份”后重试确认。身份核对完成前不计入已确认。" : "",
                    needsReview > results.Count(r => r.ReportedApplied) ? "其他目标未取得有效确认，请展开结果检查电脑名/IP、连接和回执。" : "",
                    failed > 0 ? "部分学生机明确拒绝或未应用策略。" : "",
                    "可检查逐台结果并重新推送；重试会生成新的策略版本。"
                }.Where(text => text.Length > 0)));
            if (errorMessages.Count > 0) WebsitePolicyError = string.Join(" ", errorMessages);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            WebsitePolicyError = "网站策略未推送：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    public Task DisableWebsitePolicyAsync() => PushWebsitePolicyAsync(WebsitePolicyMode.Disabled);

    public void FillFailedWebsiteTargets()
    {
        if (_lastFailedWebsiteTargets.Count == 0) return;
        WebsiteTargets = string.Join(Environment.NewLine, _lastFailedWebsiteTargets);
        WebsitePolicyHistoryText += Environment.NewLine + "已填入失败或需核对的设备；请核对当前网站规则和时长，再发起新版本推送。";
    }

    private void LoadLatestWebsitePolicyHistory()
    {
        try
        {
            var latest = WebsitePolicyPushHistoryStore.ReadLatest();
            if (latest is not null) UpdateWebsitePolicyHistory(latest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          ArgumentException or System.Text.Json.JsonException)
        {
            WebsitePolicyHistoryText = "无法读取本机上次推送记录；当前仍可编辑新策略。";
        }
    }

    private void UpdateWebsitePolicyHistory(WebsitePolicyPushHistoryEntry entry)
    {
        _lastFailedWebsiteTargets = Array.AsReadOnly(entry.Results.Where(result => !result.Succeeded)
            .Select(result => result.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var succeeded = entry.Results.Count(result => result.Succeeded);
        var needsReview = entry.Results.Count(result => !result.Succeeded && result.NeedsReview);
        var failed = entry.Results.Count(result => !result.Succeeded && !result.NeedsReview);
        WebsitePolicyHistoryText = $"上次推送 {entry.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {entry.CampusId} · v{entry.Revision} · 已确认 {succeeded}/{entry.Results.Count} · 报告已应用但身份待核对 {entry.Results.Count(r => r.ReportedApplied)} · 其他待核对 {needsReview - entry.Results.Count(r => r.ReportedApplied)} · 拒绝 {failed}";
        Changed(nameof(CanFillFailedWebsiteTargets));
    }

    private bool IsWebsitePolicyInputValid()
    {
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            WebsitePolicyCompiler.NormalizeDomains(WebsiteDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            return WebsiteDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(domain => !string.IsNullOrWhiteSpace(domain));
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException) { return false; }
    }

    private bool IsApplicationPolicyInputValid()
    {
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var now = DateTimeOffset.UtcNow;
            var policy = new ApplicationPolicyDocument(1, ApplicationPolicyCompiler.Purpose, CampusId.Trim(), 1,
                now, now + ApplicationPolicyLifetime(), SelectedApplicationPolicyMode, ParseStudentSids(), ParseApplicationRules());
            ApplicationPolicyCompiler.Validate(policy);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException or OverflowException)
        { return false; }
    }

    private bool IsStudentSystemPolicyInputValid()
    {
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var settings = CurrentStudentSystemPolicySettings();
            if (settings.IsEmpty) return false;
            _ = StudentSystemPolicyCompiler.Create(CampusId.Trim(), 1, ParseStudentSids(), settings);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException or OverflowException)
        { return false; }
    }

    private StudentSystemPolicySettings CurrentStudentSystemPolicySettings() => new(
        StudentSystemPolicyLockWallpaper, StudentSystemPolicyProhibitTimeChanges,
        StudentSystemPolicyProhibitNetworkChanges, StudentSystemPolicyProhibitSoftwareInstallation,
        StudentSystemPolicyProhibitAccountManagement, StudentSystemPolicyProhibitControlPanel);

    private string BuildStudentSystemPolicyPreview()
    {
        try
        {
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            var sids = ParseStudentSids();
            var settings = CurrentStudentSystemPolicySettings();
            var enabled = new List<string>();
            if (settings.LockWallpaper) enabled.Add("Windows 蓝色默认壁纸");
            if (settings.ProhibitTimeChanges) enabled.Add("日期/时间与时区修改");
            if (settings.ProhibitNetworkChanges) enabled.Add("网络连接属性与配置入口");
            if (settings.ProhibitSoftwareInstallation) enabled.Add("MSI、Store/Appx 安装入口及学生可写位置中的便携程序执行");
            if (settings.ProhibitAccountManagement) enabled.Add("学生账户管理和本人改密");
            if (settings.ProhibitControlPanel) enabled.Add("Control Panel/Settings");
            if (enabled.Count == 0) return "请选择至少一项系统限制；长期策略没有自动到期时间。";
            return $"预览：{targets.Count} 台电脑 · {sids.Length} 个本地学生账户 SID · 长期生效（无自动到期）。启用：{string.Join("、", enabled)}。" +
                   (settings.ProhibitSoftwareInstallation ? "\n长期 AppLocker allowlist 允许 Windows 与 Program Files 目录及维护账户；学生可写目录中的程序默认拒绝。若同时启用课堂应用审核，读取按钮显示登记程序影响模拟，不是启动日志。" : "");
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException or OverflowException)
        { return "预览待补充：" + exception.Message; }
    }

    private string BuildApplicationPolicyPreview()
    {
        try
        {
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            var sids = ParseStudentSids();
            var rules = ParseApplicationRules();
            var publishers = rules.Count(rule => rule.Kind == ApplicationRuleKind.Publisher);
            var hashes = rules.Length - publishers;
            var expires = DateTimeOffset.UtcNow + ApplicationPolicyLifetime();
            var mode = SelectedApplicationPolicyMode == ApplicationPolicyMode.Audit ? "审核模式（课堂规则不拦截；读取事件，或在长期 allowlist 下模拟登记程序影响）" : "执行模式（阻止后续启动）";
            var gate = SelectedApplicationPolicyMode == ApplicationPolicyMode.Enforce && !_hasMatchingApplicationAudit
                ? "先应用“检查影响”，在学生账户尝试启动勾选的软件，再点击“查看影响检查结果”。"
                : SelectedApplicationPolicyMode == ApplicationPolicyMode.Enforce && !ApplicationEnforcementReviewed
                    ? "检查结果已返回；请核对后勾选执行确认。"
                : "";
            return $"预览：{targets.Count} 台电脑 · {sids.Length} 个学生账户 SID · {rules.Length} 条规则（发布者 {publishers}，文件哈希 {hashes}）· {mode} · 自动解除 {expires.ToLocalTime():yyyy-MM-dd HH:mm}。\n只阻止后续启动。每次应用会替换本工具的课堂禁用清单；不再勾选的软件在新清单成功应用后解除课堂限制。{gate}";
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException or OverflowException)
        {
            return "预览待补充：" + exception.Message;
        }
    }

    private void NotifyApplicationPolicyInputs(bool invalidateAudit = true)
    {
        if (invalidateAudit)
        {
            _applicationEnforcementReviewed = false;
            Changed(nameof(ApplicationEnforcementReviewed));
            _hasMatchingApplicationAudit = false;
            _lastApplicationAuditFingerprint = null;
            _lastApplicationAuditPolicyRevision = null;
            Changed(nameof(HasMatchingApplicationAudit));
        }
        Changed(nameof(ApplicationPolicyPreview));
        Changed(nameof(CanPushApplicationPolicy));
        Changed(nameof(CanDisableApplicationPolicy));
        Changed(nameof(CanReadApplicationPolicyAudit));
        Changed(nameof(CanPushStudentSystemPolicy));
        Changed(nameof(StudentSystemPolicyPreview));
    }

    private void NotifyStudentSystemPolicyInputs()
    {
        _studentSystemPolicySoftwareInstallReviewed = false;
        Changed(nameof(StudentSystemPolicySoftwareInstallReviewed));
        Changed(nameof(StudentSystemPolicyPreview));
        Changed(nameof(CanPushStudentSystemPolicy));
    }

    private void FilterApplicationInventory()
    {
        var query = ApplicationInventorySearch.Trim();
        ApplicationInventoryChoices = query.Length == 0
            ? _allApplicationInventoryChoices
            : _allApplicationInventoryChoices.Where(item =>
                item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.BinaryName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.FilePath.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Target.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    private string GetApplicationPolicyFingerprint()
    {
        var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            Campus = CampusId.Trim(), Targets = targets, Students = StudentSidsByTarget(targets, false), Rules = ParseApplicationRules()
        });
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    private string? TryGetApplicationPolicyFingerprint()
    {
        try { return GetApplicationPolicyFingerprint(); }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException or OverflowException)
        { return null; }
    }

    private void LoadLatestApplicationPolicyHistory()
    {
        try
        {
            var latest = ApplicationPolicyPushHistoryStore.ReadLatest();
            if (latest is not null) UpdateApplicationPolicyHistory(latest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          ArgumentException or System.Text.Json.JsonException)
        {
            ApplicationPolicyHistoryText = "无法读取本机上次应用策略推送记录；当前仍可编辑新策略。";
        }
    }

    private void UpdateApplicationPolicyHistory(ApplicationPolicyPushHistoryEntry entry)
    {
        var succeeded = entry.Results.Count(result => result.Succeeded);
        var pending = entry.Results.Count(result => !result.Succeeded && result.NeedsReview);
        var failed = entry.Results.Count(result => !result.Succeeded && !result.NeedsReview);
        var mode = entry.Mode switch
        {
            ApplicationPolicyMode.Audit => "审核",
            ApplicationPolicyMode.Enforce => "阻止",
            _ => "解除"
        };
        ApplicationPolicyHistoryText = $"上次应用策略 {entry.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {entry.CampusId} · v{entry.Revision} · {mode} · 已确认 {succeeded}/{entry.Results.Count} · 待核对 {pending} · 失败 {failed}";
    }

    private bool AreWebsitePolicyTargetsValid()
    {
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException) { return false; }
    }

    public string StudentAgentIdentityStatus { get; private set; } = "";

    public async Task<IReadOnlyList<StudentAgentIdentityDiscoveryResult>> DiscoverStudentAgentIdentitiesAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            StudentAgentIdentityStatus = "学生 Agent 身份读取仅支持 Windows。";
            Changed(nameof(StudentAgentIdentityStatus));
            return Array.Empty<StudentAgentIdentityDiscoveryResult>();
        }
        if (!CanTrustStudentAgentIdentities || !TryBeginExclusiveTask())
        {
            StudentAgentIdentityStatus = "请先填写校区和学生电脑目标，并确保当前没有其他操作。";
            Changed(nameof(StudentAgentIdentityStatus));
            return Array.Empty<StudentAgentIdentityDiscoveryResult>();
        }
        try
        {
            var campus = CampusId.Trim();
            var targets = WebsitePolicyTransport.NormalizeTargets(
                WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            StudentAgentIdentityStatus = $"正在对 {targets.Count} 台电脑发送校区签名挑战并读取 Agent 身份……";
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var results = await WebsitePolicyStatusTransport.DiscoverIdentitiesAsync(targets, campus,
                signingKey.PrivateKey);
            var countWithIdentity = results.Count(result => result.Candidate is not null);
            StudentAgentIdentityStatus = $"身份读取完成：取得 {countWithIdentity}/{results.Count} 个有效签名身份。首次信任前请将完整指纹与对应学生机部署结果逐台核对。" +
                                  Environment.NewLine + string.Join(Environment.NewLine, results.Select(result =>
                                      result.Candidate is { } candidate
                                          ? $"{result.Target}：{(result.MatchesPinnedKey ? "身份已固定" : "待核对")}" +
                                            (candidate.PreviouslyPinnedFingerprint is { } old && old != candidate.Fingerprint
                                                ? $"；已固定 {old}，当前 {candidate.Fingerprint}"
                                                : $"；指纹 {candidate.Fingerprint}")
                                          : $"{result.Target}：无法验证身份 — {result.Detail}"));
            return results;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or HttpRequestException or
                                          SocketException or PlatformNotSupportedException)
        {
            StudentAgentIdentityStatus = "学生 Agent 身份读取失败：" + exception.Message;
            return Array.Empty<StudentAgentIdentityDiscoveryResult>();
        }
        finally { Changed(nameof(StudentAgentIdentityStatus)); EndExclusiveTask(); }
    }

    public void ConfirmStudentAgentIdentities(IEnumerable<StudentAgentIdentityDiscoveryResult> discoveries,
        bool approveChangedKeys)
    {
        ArgumentNullException.ThrowIfNull(discoveries);
        var results = discoveries.ToArray();
        if (!approveChangedKeys && results.Any(item => item.Candidate is not null && !item.MatchesPinnedKey))
            throw new InvalidOperationException("首次固定或轮换学生 Agent 身份必须先完成教师端现场指纹确认。");
        var candidates = results.Where(item => item.Candidate is not null).Select(item => item.Candidate!).ToArray();
        if (candidates.Length == 0)
        {
            StudentAgentIdentityStatus = "没有可固定的已签名学生 Agent 身份。";
            return;
        }
        var trustStore = new StudentAgentIdentityTrustStore();
        var pinned = new List<string>();
        foreach (var candidate in candidates)
        {
            var changed = candidate.PreviouslyPinnedFingerprint is { } old &&
                          !string.Equals(old, candidate.Fingerprint, StringComparison.OrdinalIgnoreCase);
            var item = trustStore.Pin(candidate, replaceChangedKey: changed && approveChangedKeys);
            pinned.Add($"{item.Target}：{item.Fingerprint}");
        }
        StudentAgentIdentityStatus = "已固定学生 Agent 身份。后续状态与更新回执必须匹配这些指纹：" +
                              Environment.NewLine + string.Join(Environment.NewLine, pinned);
        Changed(nameof(StudentAgentIdentityStatus));
        Changed(nameof(CanDeployStudentUpdate));
    }

    public void ReportPackagePublisherError(string message) => PackagePublisherError = message;

    public async Task PublishStudentPackageAsync()
    {
        if (!CanPublishStudentPackage || !TryBeginExclusiveTask()) return;
        PackagePublisherError = "";
        PackagePublishResult = "";
        PackagePublisherStatus = "正在校验配置包并发布……";
        try
        {
            var result = await _packagePublisher.PublishAsync(CampusId,
                PublisherName, TeacherPhoneLast4, PublishPackageDirectory);
            PackagePublisherStatus = "云端目录已发布；学生可按校区名称搜索。";
            PackagePublishResult =
                $"发布成功：{result.CampusName}\n发布教师：{PublisherName.Trim()} · 学生下载需输入手机号后四位。";
            try
            {
                var heartbeatState = _teacherHeartbeatState ?? TeacherCampusHeartbeatStateStore.LoadOrCreate();
                var isFirstSuccessfulPublish = heartbeatState.PackageId is null &&
                                               heartbeatState.LastSentDay is null &&
                                               heartbeatState.FirstHeartbeatNotBeforeUtc is null;
                var nextHeartbeatState = heartbeatState with { PackageId = result.PackageId };
                if (isFirstSuccessfulPublish)
                    nextHeartbeatState = nextHeartbeatState with
                    {
                        FirstHeartbeatNotBeforeUtc = DateTimeOffset.UtcNow +
                                                     TeacherCampusHeartbeatStateStore.InitialHeartbeatDelay
                    };
                _teacherHeartbeatState = nextHeartbeatState;
                Changed(nameof(HasTeacherHeartbeatStatus));
                TeacherCampusHeartbeatStateStore.Save(nextHeartbeatState);
                if (_isOperationsTelemetryEnabled)
                    _ = SendCampusOperationsReportsAsync();
                if (nextHeartbeatState.Enabled)
                {
                    if (nextHeartbeatState.FirstHeartbeatNotBeforeUtc is { } notBefore &&
                        notBefore > DateTimeOffset.UtcNow)
                    {
                        TeacherHeartbeatStatus = "首次校区心跳已安排，将在配置包发布 1 小时后发送。";
                        ScheduleInitialTeacherHeartbeat(notBefore);
                    }
                    else
                    {
                        _ = SendTeacherCampusHeartbeatAsync();
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                TeacherHeartbeatStatus = "配置包已发布，但 Teacher 心跳本机状态未保存：" + exception.Message;
            }
        }
        catch (Exception exception)
        {
            PackagePublisherError = "发布失败：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    private bool IsPackagePublisherDetailsValid()
    {
        var campus = PublishCampusName.Normalize(System.Text.NormalizationForm.FormKC).Trim();
        var publisher = PublisherName.Normalize(System.Text.NormalizationForm.FormKC).Trim();
        return campus.Length is >= 1 and <= 100 && campus != DefaultCampusNamePrefix && !campus.Any(char.IsControl) &&
               publisher.Length is >= 1 and <= 100 && !publisher.Any(char.IsControl) &&
               TeacherPhoneLast4.Length == 4 && TeacherPhoneLast4.All(char.IsAsciiDigit);
    }

    public async Task GenerateStudentPackageAsync(bool replaceUnavailableSigningKey = false)
    {
        if (!TryBeginExclusiveTask()) return;
        PackageOutput = "";
        PackageOutputError = "";
        CanReplaceWebsiteSigningKey = false;
        string? temporaryPublicKey = null;
        CancellationTokenSource? packageBuildCancellation = null;
        var teacherKeyCreated = false;
        var campus = CampusId.Trim();
        if (campus == DefaultCampusNamePrefix)
        {
            PackageOutputError = $"请在默认前缀“{DefaultCampusNamePrefix}”后填写校区名称。";
            EndExclusiveTask();
            return;
        }
        try { WebsitePolicySigningKeyStore.ValidateCampusId(campus); }
        catch (InvalidDataException exception)
        {
            PackageOutputError = exception.Message;
            EndExclusiveTask();
            return;
        }
        IsGeneratingStudentPackage = true;
        PackageGenerationStatus = "正在准备生成学生校区配置包……";
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                PackageOutputError = "校区公钥由 Veyon 受控密钥目录管理；请在已安装 Veyon 的 Windows 教师端生成配置包。";
                return;
            }
            PackageGenerationStatus = "正在检测 Veyon 安装状态……";
            var installed = await Task.Run(VeyonFacts.Probe);
            if (installed.Status != "installed" || !VeyonFacts.IsSupportedVersionDetail(installed.VersionDetail))
            {
                PackageOutputError = $"请先安装并确认 Veyon {VeyonInstallerTrust.Version}，再生成学生校区配置包。\n{installed.AsText()}";
                return;
            }
            MachineNaming.CreateRange(RoomPrefix, "1", "150");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var campusFolderName = SafeCampusDirectoryName(campus);
            var outDir = string.IsNullOrWhiteSpace(RoomOutputDir)
                ? Path.Combine(desktop, "Veyon-学生配置包-" + campusFolderName)
                : Path.GetFullPath(RoomOutputDir);
            if (Directory.Exists(outDir) || File.Exists(outDir))
            {
                PackageOutputError = "输出目录已存在，为防止覆盖现有密钥，请改路径或先查清原目录内容。";
                return;
            }
            PackageGenerationStatus = "正在准备校区签名密钥……";
            using var websiteSigningKey = await Task.Run(() =>
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("教师网站策略密钥仅支持 Windows 用户证书库。");
                return WebsitePolicySigningKeyStore.GetOrCreate(campus, replaceUnavailableSigningKey);
            });
            using var applicationSigningKey = await Task.Run(() => ApplicationPolicySigningKeyStore.GetOrCreate(campus));
            using var systemSigningKey = await Task.Run(() => StudentSystemPolicySigningKeyStore.GetOrCreate(campus));
            var publicKeyExportPath = Path.Combine(Path.GetTempPath(), "VeyonCampus-public-" + Guid.NewGuid().ToString("N") + ".pem");
            temporaryPublicKey = publicKeyExportPath;
            PackageGenerationStatus = "正在准备校区公钥，首次生成可能需要较长时间，请稍候……";
            InstallerStatus = "正在检查 Veyon 密钥库并仅导出校区配置所需公钥……";
            var keyResponse = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                (requestId, caller) => new PrivilegedWorkerRequest(
                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                    PrivilegedWorkerOperation.ExportTeacherPublicKey, CampusId: campus));
            teacherKeyCreated = keyResponse.TeacherKeyCreated == true;
            if (!keyResponse.Result.Ok || string.IsNullOrWhiteSpace(keyResponse.PublicKeyPem))
            {
                PackageOutputError = keyResponse.Result.Detail +
                    (teacherKeyCreated ? "\n本次已在 Veyon 密钥库创建密钥对，密钥保留在那里；没有导出教师私钥。" : "");
                return;
            }
            await File.WriteAllTextAsync(publicKeyExportPath, keyResponse.PublicKeyPem,
                new System.Text.UTF8Encoding(false));
            PackageGenerationStatus = "正在生成配置文件并压缩部署包，请稍候……";
            packageBuildCancellation = new CancellationTokenSource();
            _studentPackageBuildCancellation = packageBuildCancellation;
            IsBuildingStudentPackage = true;
            var token = packageBuildCancellation.Token;
            string built;
            try
            {
                built = await Task.Run(() => PackageBuilder.Build(outDir, campus, RoomPrefix,
                    publicKeyExportPath, websiteSigningKey.PublicKeyPem, enableAnonymousTelemetry: true,
                    cancellationToken: token, applicationPolicyPublicKeyPem: applicationSigningKey.PublicKeyPem,
                    compatibility: PackageCompatibility.ForSupportedProtocolVersions(AppVersion, VeyonInstallerTrust.Version,
                        WebsitePolicyAgentInstaller.BuildVersion),
                    studentSystemPolicyPublicKeyPem: systemSigningKey.PublicKeyPem,
                    recommendedOperations: new PackageSetupRecommendations(
                        RecommendInstallVeyon, RecommendRenameComputer,
                        RecommendCreateStudentAccount, RecommendChangeAdminPassword)), token);
            }
            finally
            {
                IsBuildingStudentPackage = false;
                _studentPackageBuildCancellation = null;
            }
            PublishPackageDirectory = built;
            PackageOutput = $"已生成学生校区配置包：{built}\n{keyResponse.Result.Detail}\n教师签名私钥保留在当前 Windows 用户证书库；学生配置仅包含校区公钥。";
        }
        catch (OperationCanceledException) when (packageBuildCancellation?.IsCancellationRequested == true)
        {
            PackageOutputError = "已取消配置包生成；未发布不完整目录。";
        }
        catch (WebsitePolicySigningKeyRecoveryRequiredException exception)
        {
            PackageOutputError = "生成失败：" + exception.Message;
            CanReplaceWebsiteSigningKey = true;
        }
        catch (Exception exception)
        {
            PackageOutputError = "生成失败：" + exception.Message;
            if (teacherKeyCreated)
                PackageOutputError += "\n本次已在 Veyon 密钥库创建密钥对，密钥保留在那里；没有导出教师私钥。";
        }
        finally
        {
            if (temporaryPublicKey is not null)
            {
                try { if (File.Exists(temporaryPublicKey)) File.Delete(temporaryPublicKey); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            IsGeneratingStudentPackage = false;
            IsBuildingStudentPackage = false;
            _studentPackageBuildCancellation = null;
            packageBuildCancellation?.Dispose();
            PackageGenerationStatus = "";
            EndExclusiveTask();
        }
    }

    private static string SafeCampusDirectoryName(string campus)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(campus.Select(character =>
            char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "校区" : safe;
    }

    public async Task InstallTeacherVeyonAsync()
    {
        if (!TryBeginExclusiveTask()) return;
            TeacherInstallResult = "";
        TeacherInstallIssue = "";
        if (!OperatingSystem.IsWindows())
        {
            TeacherInstallIssue = "教师端 Veyon 安装仅支持 Windows。";
            EndExclusiveTask();
            return;
        }
        InstallerStatus = "正在检查教师端安装状态与权限……";
        try
        {
            var installed = await Task.Run(VeyonFacts.Probe);
            if (installed.Status == "installed")
            {
                TeacherInstallIssue = $"检测到本机已安装 Veyon，请勿重复安装。\n{installed.AsText()}";
                return;
            }
            if (installed.Status != VeyonFacts.NotInstalled)
            {
                TeacherInstallIssue = $"无法确认本机 Veyon 安装状态；为避免覆盖未知安装，已停止。\n{installed.AsText()}";
                return;
            }
            InstallerStatus = "正在从 App 内嵌资源提取并校验 Veyon 安装程序……";
            var acquired = await AcquireInstallerWithProgressAsync();
            if (!acquired.Trust.IsAllowed || !acquired.Trust.AuthenticodeVerified)
            {
                TeacherInstallIssue = "安装器没有通过 Windows SHA-256 与 Authenticode 校验；没有运行安装程序。" + acquired.Trust.Detail;
                return;
            }
            InstallerStatus = "安装器已校验，正在安装教师组件（含 Veyon Master）……";
            var installResponse = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                (requestId, caller) => new PrivilegedWorkerRequest(
                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                    PrivilegedWorkerOperation.InstallVeyon,
                    InstallerPath: acquired.InstallerPath, IsTeacher: true));
            var install = installResponse.Result;
            if (install.RebootRequired || !install.Ok)
            {
                TeacherInstallIssue = install.Detail + (install.RebootRequired ? " 请重启后重新检查。" : "");
                return;
            }
            var verification = await Task.Run(() => new WindowsVeyonAdapter().VerifyTeacherInstall());
            if (verification.Ok)
            {
                TeacherInstallResult = $"Veyon {VeyonInstallerTrust.Version} 教师端已安装并验证，密钥认证已配置。\n请关闭并重新打开 Veyon Master，确认教师账户能读取对应私钥，再生成学生校区配置包。";
            }
            else TeacherInstallIssue = $"{install.Detail}\n安装读回需人工核对：{verification.Detail}";
            InstallerStatus = $"Veyon {VeyonInstallerTrust.Version} 安装器校验完成。";
        }
        catch (Exception exception) { TeacherInstallIssue = "安装未完成：" + DescribeWindowsLaunchError(exception); }
        finally { EndExclusiveTask(); }
    }

    private Task<InstallerStoreResult> AcquireInstallerWithProgressAsync()
    {
        var progress = new Progress<InstallerResourceProgress>(value =>
        {
            var percent = value.TotalBytes > 0 ? Math.Clamp(value.BytesReceived * 100 / value.TotalBytes, 0, 100) : 0;
            InstallerStatus = $"正在释放 App 内嵌 Veyon {VeyonInstallerTrust.Version}：{percent}%（{value.BytesReceived / 1024 / 1024} / {value.TotalBytes / 1024 / 1024} MB）";
        });
        return _installerStore.EnsureAvailableAsync(progress);
    }

    private void ClearRoomPreview()
    {
        RoomNames = Array.Empty<string>();
        RoomPreviewRows = Array.Empty<string>();
        RoomError = "";
        InvalidateRoomReview();
        ClearRoomCreateFeedback();
    }

    private void InvalidateRoomReview()
    {
        _roomPlanRevision++;
        _roomConflictCheckCompleted = false;
        _roomConflictCheckRevision = -1;
        _roomPlannedNewComputerCount = 0;
        RoomConflictStatus = "";
        RoomConflictResults = "";
        RoomSkippedResults = "";
        Changed(nameof(CanAddRoomToVeyon));
    }

    private void ClearRoomCreateFeedback()
    {
        RoomCreateResult = "";
        RoomCreateError = "";
    }

    private void UpdateRoomPreviewRows()
    {
        try
        {
            var students = ParseStudentRoster(RoomNames.Count);
            var hosts = ParseHostOverrides(RoomComputerHosts, RoomNames.Count);
            RoomPreviewRows = RoomNames.Select((name, index) =>
            {
                var student = index < students.Count ? students[index] : "";
                var host = index < hosts.Count && hosts[index].Length > 0 ? hosts[index] : name;
                var displayName = student.Length == 0 ? "（未填写显示名）" : $"显示名：{student}";
                return $"{name}  →  {host}    {displayName}";
            }).ToArray();
            RoomError = "";
        }
        catch (InvalidDataException exception)
        {
            RoomPreviewRows = Array.Empty<string>();
            RoomError = exception.Message;
        }
    }

    private IReadOnlyList<string> ParseStudentRoster(int computerCount)
    {
        if (string.IsNullOrEmpty(StudentRoster)) return Array.Empty<string>();
        var normalized = StudentRoster.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (normalized.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > computerCount)
            throw new InvalidDataException($"学生姓名有 {lines.Count} 行，但电脑清单只有 {computerCount} 台；请删减或调整电脑数量。");

        var students = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var name = line.Trim();
            if (name.Length > 100 || name.Any(char.IsControl))
                throw new InvalidDataException("显示名最多 100 个字符，不能包含控制字符。");
            students.Add(name);
        }
        return students.AsReadOnly();
    }

    private int GetNamedStudentCount()
    {
        try { return ParseStudentRoster(RoomNames.Count).Count(name => name.Length > 0); }
        catch (InvalidDataException) { return 0; }
    }

    private bool TryBeginExclusiveTask()
    {
        if (!_lease.TryAcquire(out var denial))
        {
            WebsitePolicyError = denial;
            ApplicationPolicyError = denial;
            PackageOutputError = denial;
            PackagePublisherError = denial;
            TeacherInstallIssue = denial;
            return false;
        }
        IsExecuting = true;
        return true;
    }

    private void EndExclusiveTask()
    {
        _lease.Dispose();
        IsExecuting = false;
    }

    private void LoadCampusDirectory()
    {
        var selectedCampusId = _selectedCampusProfile?.ProfileId;
        var selectedRoomId = _selectedRoomProfile?.RoomId;
        try
        {
            var loaded = _campusDirectoryStore.Load();
            CampusProfiles = loaded;
            _selectedCampusProfile = null;
            _selectedRoomProfile = null;
            RoomProfiles = Array.Empty<TeacherRoomProfile>();
            Changed(nameof(SelectedCampusProfile));
            Changed(nameof(SelectedRoomProfile));
            Changed(nameof(HasSelectedCampusProfile));
            Changed(nameof(HasSelectedRoomProfile));
            SelectedCampusProfile = selectedCampusId is { } campusId
                ? loaded.FirstOrDefault(item => item.ProfileId == campusId) ?? loaded.FirstOrDefault()
                : loaded.FirstOrDefault();
            if (selectedRoomId is { } roomId)
                SelectedRoomProfile = RoomProfiles.FirstOrDefault(item => item.RoomId == roomId) ?? RoomProfiles.FirstOrDefault();
            CampusDirectoryError = "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            CampusProfiles = Array.Empty<TeacherCampusProfile>();
            RoomProfiles = Array.Empty<TeacherRoomProfile>();
            _selectedCampusProfile = null;
            _selectedRoomProfile = null;
            Changed(nameof(SelectedCampusProfile));
            Changed(nameof(SelectedRoomProfile));
            Changed(nameof(HasSelectedCampusProfile));
            Changed(nameof(HasSelectedRoomProfile));
            CampusDirectoryError = "无法读取本机校区档案；原文件保留未覆盖。";
        }
    }

    private VeyonNetworkComputer[] BuildRoomImportPlan()
    {
        var students = ParseStudentRoster(RoomNames.Count);
        var hosts = ParseHostOverrides(RoomComputerHosts, RoomNames.Count);
        return RoomNames.Select((name, index) =>
        {
            var host = index < hosts.Count && hosts[index].Length > 0 ? hosts[index] : name;
            var student = index < students.Count ? students[index] : "";
            return new VeyonNetworkComputer(name, host, student);
        }).ToArray();
    }

    private static IReadOnlyList<string> ParseHostOverrides(string? value, int computerCount)
    {
        if (string.IsNullOrEmpty(value)) return Array.Empty<string>();
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (normalized.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > computerCount)
            throw new InvalidDataException($"主机名/IP 有 {lines.Count} 行，但电脑清单只有 {computerCount} 台；请删减或调整电脑数量。");

        var hosts = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            try { hosts.Add(VeyonHostAddress.NormalizeOverride(line)); }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException("主机名/IP 覆盖无效：" + exception.Message, exception);
            }
        }
        return hosts.AsReadOnly();
    }

    private bool PersistCampusDirectory(IEnumerable<TeacherCampusProfile> campuses, string successMessage)
    {
        try
        {
            _campusDirectoryStore.Save(campuses);
            LoadCampusDirectory();
            if (HasCampusDirectoryError) return false;
            CampusDirectoryStatus = successMessage;
            CampusDirectoryError = "";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            CampusDirectoryError = "保存本机校区档案失败；原文件保留。" + exception.Message;
            return false;
        }
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record TeacherSeatCell(int Index, string Target, string PositionLabel, bool IsSelected);

public sealed class TeacherClassroomEventItem(ClassroomEvent classroomEvent, string? seatLocation = null) : INotifyPropertyChanged
{
    private string _replyMessage = "";
    private string? _error;
    private bool _isAcknowledged;
    private bool _hasReply;
    private bool _isResolved;
    private bool _wasExpired = classroomEvent.ExpiresUtc <= DateTimeOffset.UtcNow;
    private string? _seatLocation = seatLocation;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ClassroomEvent Event { get; } = classroomEvent;
    public Guid EventId => Event.EventId;
    public bool IsHelpRequest => Event.Type == ClassroomEventType.HelpRequested;
    public string Title => IsHelpRequest ? $"{Event.Target} 需要帮助" :
        Event.Target == ClassroomEventCryptography.ClassroomNoticeTarget ? "全班通知" : $"课堂通知 · {Event.Target}";
    public string? SeatLocation => _seatLocation;
    public bool HasSeatLocation => !string.IsNullOrWhiteSpace(_seatLocation);
    public string Detail => Event.Message ?? "学生需要老师帮助。";
    public string EventTime => Event.IssuedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public bool CanReply => IsHelpRequest && !_hasReply && !_isResolved && Event.ExpiresUtc > DateTimeOffset.UtcNow;
    public string Status => _error ?? (_isResolved ? "已解决" : _hasReply ? "等待学生确认" : _isAcknowledged ? "老师已确认" :
        _wasExpired ? "求助已过期" : IsHelpRequest ? "等待回复" : "");

    public string ReplyMessage
    {
        get => _replyMessage;
        set
        {
            if (_replyMessage == value) return;
            _replyMessage = value;
            Changed();
        }
    }

    public void MarkReply(string message)
    {
        _hasReply = true;
        _replyMessage = message;
        _error = null;
        Changed(nameof(ReplyMessage));
        Changed(nameof(Status));
        Changed(nameof(CanReply));
    }

    public void MarkAcknowledged()
    {
        _isAcknowledged = true;
        Changed(nameof(Status));
    }

    public void MarkResolved()
    {
        _isResolved = true;
        _error = null;
        Changed(nameof(Status));
        Changed(nameof(CanReply));
    }

    public void SetSeatLocation(string? value)
    {
        if (_seatLocation == value) return;
        _seatLocation = value;
        Changed(nameof(SeatLocation));
        Changed(nameof(HasSeatLocation));
    }

    public void SetError(string message)
    {
        _error = message;
        Changed(nameof(Status));
    }

    public void RefreshExpiry()
    {
        var isExpired = Event.ExpiresUtc <= DateTimeOffset.UtcNow;
        if (_wasExpired == isExpired) return;
        _wasExpired = isExpired;
        Changed(nameof(Status));
        Changed(nameof(CanReply));
    }

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
