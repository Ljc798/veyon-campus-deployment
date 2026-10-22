using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class TeacherClassroomQuickControlDeviceItem : INotifyPropertyChanged
{
    private string _displayName = "";
    private string _statusText = "";
    private string _detail = "";
    private string _updatedText = "";
    private IBrush _statusBrush = NeutralBrush;
    private IBrush _statusBackground = NeutralSubtleBrush;
    private bool _isExpanded;
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#16803C"));
    private static readonly IBrush SuccessSubtleBrush = new SolidColorBrush(Color.Parse("#E5F3EA"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#1769AA"));
    private static readonly IBrush InfoSubtleBrush = new SolidColorBrush(Color.Parse("#E7F1FA"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#9A5B00"));
    private static readonly IBrush WarningSubtleBrush = new SolidColorBrush(Color.Parse("#FFF3D6"));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#B42318"));
    private static readonly IBrush DangerSubtleBrush = new SolidColorBrush(Color.Parse("#FDECEC"));
    private static readonly IBrush NeutralBrush = new SolidColorBrush(Color.Parse("#526674"));
    private static readonly IBrush NeutralSubtleBrush = new SolidColorBrush(Color.Parse("#E9EEF2"));

    public Guid TargetId { get; }
    public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string UpdatedText { get => _updatedText; private set => Set(ref _updatedText, value); }
    public IBrush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public IBrush StatusBackground { get => _statusBackground; private set => Set(ref _statusBackground, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TeacherClassroomQuickControlDeviceItem(ClassroomQuickControlTargetSnapshot snapshot)
    {
        TargetId = snapshot.TargetId;
        Update(snapshot);
    }

    public void Update(ClassroomQuickControlTargetSnapshot snapshot)
    {
        if (snapshot.TargetId != TargetId)
            throw new ArgumentException("管控进度不能更换目标身份。", nameof(snapshot));

        DisplayName = snapshot.DisplayName;
        Detail = snapshot.Detail;
        UpdatedText = "本机记录 · " + snapshot.UpdatedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        (StatusText, StatusBrush, StatusBackground) = snapshot.Phase switch
        {
            ClassroomQuickControlPhase.WaitingForAgent => Status("等待上线", WarningBrush, WarningSubtleBrush),
            ClassroomQuickControlPhase.WebsiteApplying or ClassroomQuickControlPhase.Switching or
                ClassroomQuickControlPhase.InputLocking => Status("执行中", InfoBrush, InfoSubtleBrush),
            ClassroomQuickControlPhase.WebsiteApplied => Status("策略已应用", SuccessBrush, SuccessSubtleBrush),
            ClassroomQuickControlPhase.WebsiteReported => Status("Agent 已回执", SuccessBrush, SuccessSubtleBrush),
            ClassroomQuickControlPhase.InputLockedVerified => Status("键鼠已核验", InfoBrush, InfoSubtleBrush),
            ClassroomQuickControlPhase.ReleasePending => Status("正在恢复", WarningBrush, WarningSubtleBrush),
            ClassroomQuickControlPhase.Unknown => Status("状态未知", WarningBrush, WarningSubtleBrush),
            ClassroomQuickControlPhase.Conflict => Status("存在冲突", DangerBrush, DangerSubtleBrush),
            ClassroomQuickControlPhase.Failed => Status("执行失败", DangerBrush, DangerSubtleBrush),
            ClassroomQuickControlPhase.Unsupported => Status("暂不支持", NeutralBrush, NeutralSubtleBrush),
            _ => Status("已解除", NeutralBrush, NeutralSubtleBrush)
        };
    }

    private static (string, IBrush, IBrush) Status(string label, IBrush foreground, IBrush background) =>
        (label, foreground, background);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class TeacherViewModel : INotifyPropertyChanged
{
    private const string DefaultCampusNamePrefix = "智学前程-";
    private sealed record PreparedSeatSwapVeyonPlan(string ConfigFingerprint,
        VeyonComputerRenameTargetPlan[] Targets, Guid FirstComputerObjectId,
        Guid FirstLocationObjectId, string FirstComputerOldName, string FirstComputerHostAddress,
        string FirstComputerNewName, Guid SecondComputerObjectId, Guid SecondLocationObjectId,
        string SecondComputerOldName, string SecondComputerHostAddress, string SecondComputerNewName);
    private sealed record PreparedSeatSwapPlan(Guid CampusProfileId, Guid RoomId,
        ClassroomSeatGridSeat FirstSeatSnapshot, ClassroomSeatGridSeat SecondSeatSnapshot,
        PreparedSeatSwapVeyonPlan? VeyonPlan, string[] PreviewRows);
    private sealed class DeviceRuntimeSummaryPollingState
    {
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset NextPollUtc { get; set; }
    }
    private sealed record CollectedStudentUpdateTimings(Guid CommandId,
        IReadOnlyList<StudentUpdatePhaseTiming> Phases, StudentUpdateEnvironmentCategories? Environment);
    private sealed class StudentUpdateProgressCollector : IProgress<StudentApplicationUpdateProgress>
    {
        private readonly IProgress<StudentApplicationUpdateProgress> _forward;
        private readonly ConcurrentDictionary<string, CollectedStudentUpdateTimings> _timings =
            new(StringComparer.OrdinalIgnoreCase);

        public StudentUpdateProgressCollector(IProgress<StudentApplicationUpdateProgress> forward) =>
            _forward = forward;

        public void Report(StudentApplicationUpdateProgress value)
        {
            if (!string.IsNullOrWhiteSpace(value.Target) && value.CommandId is { } commandId &&
                value.PhaseTimings is { Count: > 0 } phases)
            {
                var key = NormalizeTargetKey(value.Target);
                var captured = new CollectedStudentUpdateTimings(commandId, Array.AsReadOnly(phases.ToArray()),
                    value.Environment);
                _timings.AddOrUpdate(key, captured, (_, previous) =>
                    previous.CommandId == commandId
                        ? captured with { Environment = value.Environment ?? previous.Environment }
                        : captured);
            }
            _forward.Report(value);
        }

        public bool TryGet(string target, out CollectedStudentUpdateTimings? timings) =>
            _timings.TryGetValue(NormalizeTargetKey(target), out timings);
    }
    private readonly VeyonInstallerStore _installerStore;
    private readonly TeacherCampusDirectoryStore _campusDirectoryStore;
    private readonly ClassroomSessionStore _classroomSessionStore;
    private readonly ClassroomSeatGridStore _classroomSeatGridStore;
    private readonly ClassroomCountdownStore _classroomCountdownStore;
    private readonly ClassroomTaskProgressStore _classroomTaskProgressStore;
    private readonly TeacherRoomSelectionStore? _roomSelectionStore;
    private readonly Dictionary<Guid, Guid> _recentRoomSelections = [];
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
    private readonly TeacherDeviceHealthCacheStore _deviceHealthCacheStore = new();
    private readonly Dictionary<string, TeacherDeviceHealthCacheEntry> _deviceHealthCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly string WebsiteObservedAddressPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "website-observed-address.txt");
    private readonly Dictionary<string, StudentApplicationUpdateTargetProgressItem> _studentUpdateRowsByTarget =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _studentUpdateTargetsByAddress = new(StringComparer.OrdinalIgnoreCase);
    private StudentUpdatePerformanceReport? _lastStudentUpdatePerformanceReport;
    private readonly List<StudentUpdatePerformanceBatchObservation> _studentUpdatePerformanceBatchHistory = [];
    private IReadOnlyList<string> _roomNames = Array.Empty<string>();
    private IReadOnlyList<string> _roomPreviewRows = Array.Empty<string>();
    private IReadOnlyList<VeyonNetworkLocation> _websiteLocations = Array.Empty<VeyonNetworkLocation>();
    private IReadOnlyList<TeacherPolicyTargetChoice> _websitePolicyTargetChoices = Array.Empty<TeacherPolicyTargetChoice>();
    private readonly ObservableCollection<TeacherDeviceHealthRow> _deviceHealthRows = [];
    private readonly ObservableCollection<TeacherDeviceHealthAlert> _deviceHealthAlerts = [];
    private readonly ObservableCollection<TeacherClassroomQuickControlDeviceItem> _classroomQuickControlDevices = [];
    private readonly Dictionary<string, DeviceRuntimeSummaryPollingState> _deviceRuntimeSummaryPolling =
        new(StringComparer.OrdinalIgnoreCase);
    private const int DeviceHealthPageSize = 12;
    private string _deviceHealthSearchText = "";
    private int _deviceHealthCategoryFilterIndex;
    private int _deviceHealthPageIndex;
    private int _deviceHealthFilteredCount;
    private IReadOnlyList<TeacherDeviceHealthRow> _deviceHealthPageRows = Array.Empty<TeacherDeviceHealthRow>();
    private string _deviceHealthStatus = "尚未读取学生电脑健康状态。";
    private string _deviceHealthCacheStatus = "设备状态摘要仅保存在本机；读取后会自动刷新。";
    private string _deviceRuntimeSummaryStatus = "打开设备健康页后自动读取轻量状态。";
    private string _deviceHealthBatchStatus = "批量修复仅使用当前健康快照和固定白名单动作。";
    private bool _isReadingDeviceHealth;
    private bool _isPollingDeviceRuntimeSummary;
    private bool _isRunningDeviceHealthBatch;
    private bool _isClassroomQuickControlBusy;
    private ClassroomQuickControlSnapshot? _classroomQuickControlSnapshot;
    private string _classroomQuickControlSessionNotice = "";
    private Guid? _activeDeviceHealthBatchId;
    private string _activeDeviceHealthBatchCampus = "";
    private HashSet<string> _activeDeviceHealthBatchSelection = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _healthSnapshotFreshnessCancellation;
    private string _deviceHealthRowsCampus = "";
    private string _deviceHealthCacheError = "";
    private long _deviceHealthSelectionRevision;
    private IReadOnlyList<ApplicationCatalogProductChoice> _applicationCatalogChoices = Array.Empty<ApplicationCatalogProductChoice>();
    private IReadOnlyList<ApplicationCatalogProductChoice> _allApplicationCatalogChoices = Array.Empty<ApplicationCatalogProductChoice>();
    private bool _applicationCatalogAllTargetsRead;
    private IReadOnlyList<ApplicationInventoryChoice> _applicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
    private IReadOnlyList<ApplicationInventoryChoice> _allApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
    private IReadOnlyList<string> _lastFailedWebsiteTargets = Array.Empty<string>();
    private bool _isExecuting, _isReadingWebsiteLocations, _websiteLocationSelectionPending, _showWebsitePolicyResultDetails;
    private bool _isCheckingTeacherSetupVeyon, _teacherSetupVeyonReady, _teacherSetupRoomSkippedForNow;
    private string _teacherSetupVeyonStatus = "尚未检查教师端 Veyon。";
    private bool _applicationEnforcementReviewed, _showApplicationPolicyResultDetails;
    private bool _studentSystemPolicyLockWallpaper, _studentSystemPolicyProhibitTimeChanges,
        _studentSystemPolicyProhibitNetworkChanges, _studentSystemPolicyProhibitSoftwareInstallation,
        _studentSystemPolicyProhibitAccountManagement, _studentSystemPolicyProhibitControlPanel,
        _studentSystemPolicySoftwareInstallReviewed, _studentSystemPolicyImpactReviewed;
    private bool _hasMatchingApplicationAudit;
    private long? _lastApplicationAuditPolicyRevision;
    private string? _lastApplicationAuditFingerprint;
    private bool _canReplaceWebsiteSigningKey, _isGeneratingStudentPackage;
    private bool _isBuildingStudentPackage;
    private CancellationTokenSource? _studentPackageBuildCancellation;
    private bool _isCheckingRoomConflicts, _roomConflictCheckCompleted;
    private bool _recommendInstallVeyon = true, _recommendRenameComputer = true,
        _recommendCreateStudentAccount = true, _recommendChangeAdminPassword = true;
    private int _roomPlanRevision, _roomConflictCheckRevision = -1, _roomPlannedNewComputerCount;
    private string _roomPrefix = "PC-", _roomStart = "1", _roomError = "";
    private string _roomCampusName = "本机校区", _roomLocationName = "", _roomComputerHosts = "";
    private string _roomCreateResult = "", _roomCreateError = "", _roomCreateStatus = "", _roomConflictStatus = "", _roomConflictResults = "", _roomSkippedResults = "";
    private string _campusId = DefaultCampusNamePrefix, _roomOutputDir = "", _packageOutput = "", _packageOutputError = "";
    private string _campusProfileName = "", _roomProfileName = "", _roomProfilePrefix = "PC-", _roomProfileStart = "1", _roomProfileCount = "150", _roomProfileHostOverrides = "";
    private string _campusDirectoryStatus = "", _campusDirectoryError = "";
    private string _roomSelectionStatus = "";
    private string _classroomDirectoryStatus = "", _classroomDirectoryError = "";
    private int _classroomLocationIndex = -1;
    private bool _classroomDirectorySelectionPending;
    private IReadOnlyList<TeacherCampusProfile> _campusProfiles = Array.Empty<TeacherCampusProfile>();
    private IReadOnlyList<string> _websiteSigningKeyCampuses = Array.Empty<string>();
    private IReadOnlyList<TeacherRoomProfile> _roomProfiles = Array.Empty<TeacherRoomProfile>();
    private IReadOnlyList<TeacherSeatCell> _seatMapCells = Array.Empty<TeacherSeatCell>();
    private IReadOnlyList<TeacherVeyonTargetChoice> _seatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
    private ClassroomSeatGridLayout? _seatGrid;
    private ClassroomSeatGridLayout? _seatMapEditSnapshot;
    private HashSet<Guid> _seatMapEditOriginalSeatIds = [];
    private string _seatMapStatus = "";
    private string _seatVeyonTargetStatus = "";
    private string _seatSwapStatus = "";
    private IReadOnlyList<string> _seatSwapPlanRows = Array.Empty<string>();
    private IReadOnlyList<string> _seatSwapTargetResults = Array.Empty<string>();
    private readonly List<Guid> _seatSwapSelection = [];
    private PreparedSeatSwapPlan? _preparedSeatSwapPlan;
    private decimal _seatRows = ClassroomSeatGridStore.DefaultRows;
    private decimal _seatColumns = ClassroomSeatGridStore.DefaultColumns;
    private decimal _seatRowsDraft = ClassroomSeatGridStore.DefaultRows;
    private decimal _seatColumnsDraft = ClassroomSeatGridStore.DefaultColumns;
    private bool _isReadingSeatVeyonTargets, _isPreparingSeatSwapPlan, _seatSwapMode,
        _isSeatMapEditing, _isSeatAddRemoveMode;
    private TeacherCampusProfile? _selectedCampusProfile;
    private TeacherRoomProfile? _selectedRoomProfile;
    private ClassroomSession? _activeClassroomSession;
    private ClassroomCountdown? _activeClassroomCountdown;
    private ClassroomTaskProgress? _activeClassroomTaskProgress;
    private decimal _classroomCountdownMinutes = 15;
    private string _classroomCountdownStatus = "";
    private string _classroomTaskProgressDraft = "";
    private string _classroomTaskProgressStatus = "";
    private bool _isClassroomTransitioning;
    private int _classroomStatusRefreshInFlight;
    private string _classroomDeliveryStatus = "";
    private IReadOnlyList<ClassroomPendingRestore> _pendingClassroomRestores = Array.Empty<ClassroomPendingRestore>();
    private string _classroomRestoreStatus = "";
    private bool _isRetryingClassroomRestores;
    private string _classroomEventStatus = "";
    private string _classroomNoticeDraft = "";
    private string _classroomNoticeStatus = "";
    private string _classroomChatDraft = "";
    private string _classroomChatStatus = "";
    private TeacherClassroomChatThread? _selectedClassroomChatThread;
    private Guid? _classroomEventFeedSessionId;
    private readonly Dictionary<Guid, TeacherClassroomEventItem> _classroomHelpRows = [];
    private readonly Dictionary<string, TeacherClassroomChatThread> _classroomChatThreadsByTarget =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _classroomEventIds = [];
    private readonly Queue<Guid> _classroomEventOrder = new();
    private string _publishPackageDirectory = "", _publisherName = "", _teacherPhoneLast4 = "";
    private string _packagePublisherStatus = "", _packagePublisherError = "", _packagePublishResult = "";
    private string _websiteTargets = "", _websiteBlockDomains = "", _websiteAllowDomains = "",
        _websiteObservedAddress = "", _websiteEntryStatus = "", _websitePolicyResult = "",
        _websitePolicyResultDetails = "", _websitePolicyError = "", _websitePolicyHistoryText = "";
    private string _websitePolicyDraftStatus = "";
    private bool _loadingWebsitePolicyLists;
    private static readonly DateTime DefaultWebsiteDeadlineLocal = DateTime.Now.AddHours(1);
    private DateTimeOffset? _websiteDeadlineDate = new DateTimeOffset(DefaultWebsiteDeadlineLocal.Date);
    private TimeSpan? _websiteDeadlineTime = DefaultWebsiteDeadlineLocal.TimeOfDay;
    private static readonly string[] CommonWebsiteBlacklistDomains =
    [
        "douyin.com", "kuaishou.com", "bilibili.com", "b23.tv", "xiaohongshu.com", "xhslink.com",
        "weibo.com", "weibo.cn", "douyu.com", "huya.com", "4399.com", "7k7k.com"
    ];
    private static readonly string[] CommonWebsiteAllowlistDomains =
    [
        "smartedu.cn", "icourse163.org", "xuetangx.com"
    ];
    private string _applicationStudentSids = "", _applicationRules = "", _applicationPolicyResult = "", _applicationPolicyResultDetails = "", _applicationPolicyError = "", _applicationPolicyHistoryText = "", _applicationAuditResult = "";
    private string _studentSystemPolicyResult = "", _studentSystemPolicyDetails = "", _studentSystemPolicyError = "";
    private string _studentAccountsStatus = "";
    private string _applicationInventorySearch = "", _applicationInventoryStatus = "",
        _applicationInventoryFailureDetails = "";
    private string _applicationCatalogSearch = "", _applicationCatalogStatus = "",
        _applicationCatalogFailureDetails = "";
    private string _websiteDirectoryStatus = "", _websiteDirectoryError = "";
    private string _installerStatus = "", _teacherInstallResult = "", _teacherInstallIssue = "";
    private string _teacherUpdateStatus = "尚未检查更新。";
    private double _teacherUpdateProgressPercent, _studentUpdateProgressPercent;
    private bool _isTeacherUpdateProgressVisible, _teacherUpdateProgressIsIndeterminate = true;
    private bool _isStudentUpdateProgressVisible, _studentUpdateProgressIsIndeterminate = true;
    private string _teacherUpdateProgressText = "", _studentUpdateProgressText = "";
    private string _teacherUpdateLatestVersion = "尚未检查";
    private string _offlineTeacherUpdateStatus = "安装器和配套清单需放在一起。";
    private string _studentUpdateStatus = "尚未向学生电脑发送更新。";
    private string _studentUpdateSucceededDetails = "", _studentUpdateNeedsReviewDetails = "",
        _studentUpdateFailedDetails = "";
    private string _studentAgentIdentityDetails = "";
    private bool _showStudentAgentIdentityDetails;
    private string _updateDiagnosticsStatus = "";
    private string _operationsTelemetryStatus = "匿名运维汇总按安装说明默认开启。";
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
    private bool _isInstallingTeacherVeyon;
    private bool _isCheckingTeacherUpdate, _isDownloadingTeacherUpdate, _isVerifyingOfflineTeacherUpdate;
    private int _teacherHeartbeatInFlight;
    private int _initialTeacherHeartbeatWaitScheduled;
    private int _websiteModeIndex = 0, _websiteDurationIndex = 1, _websiteCustomDurationMinutes = 60,
        _websiteLocationIndex = -1;
    private int _applicationPolicyModeIndex, _applicationPolicyDurationIndex = 1;
    private string _selectedPage = "lesson";
    private int _teacherSetupWizardStep = 1;

    public TeacherViewModel(VeyonInstallerStore? installerStore = null,
        TeacherCampusDirectoryStore? campusDirectoryStore = null,
        UpdateDiagnosticsStore? updateDiagnostics = null,
        ClassroomSessionStore? classroomSessionStore = null,
        ClassroomModeStateStore? classroomModeStateStore = null,
        CampusOperationsTelemetryStore? operationsTelemetryStore = null,
        ClassroomSeatGridStore? classroomSeatGridStore = null,
        ClassroomCountdownStore? classroomCountdownStore = null,
        ClassroomTaskProgressStore? classroomTaskProgressStore = null,
        TeacherRoomSelectionStore? roomSelectionStore = null)
    {
        _installerStore = installerStore ?? new VeyonInstallerStore();
        _campusDirectoryStore = campusDirectoryStore ?? new TeacherCampusDirectoryStore();
        _classroomSessionStore = classroomSessionStore ?? new ClassroomSessionStore();
        _classroomSeatGridStore = classroomSeatGridStore ?? new ClassroomSeatGridStore();
        _classroomCountdownStore = classroomCountdownStore ?? new ClassroomCountdownStore();
        _classroomTaskProgressStore = classroomTaskProgressStore ?? new ClassroomTaskProgressStore();
        _roomSelectionStore = roomSelectionStore;
        if (_roomSelectionStore is not null)
        {
            try
            {
                foreach (var selection in _roomSelectionStore.Load())
                    _recentRoomSelections[selection.Key] = selection.Value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _roomSelectionStatus = "最近教室记录无法读取；将使用默认地点。";
            }
        }
        _classroomModeStateStore = classroomModeStateStore ?? new ClassroomModeStateStore();
        _classroomSigningContextStore = new ClassroomSigningContextStore();
        _updateDiagnostics = updateDiagnostics ?? new UpdateDiagnosticsStore();
        _operationsTelemetryStore = operationsTelemetryStore ?? new CampusOperationsTelemetryStore();
        try
        {
            foreach (var entry in _deviceHealthCacheStore.Load())
                _deviceHealthCache[BuildDeviceHealthCacheKey(entry.CampusId, entry.Target)] = entry;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          System.Text.Json.JsonException or ArgumentException)
        {
            _deviceHealthCacheError = exception.Message;
            _deviceHealthCacheStatus = "本机状态缓存无法读取；实时查询仍可使用。";
        }
        _websiteObservedAddress = LoadWebsiteObservedAddress();
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
                ? "已开启；只在本机汇总更新结果和学生互动次数，离线时稍后重试。"
                : "匿名运维汇总已关闭。";
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
                            : "今日校区状态已同步。";
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
            ClassroomDeliveryStatus = "无法读取校区设置；学生互动暂不可用。";
        }
        LoadWebsitePolicyLists(CampusId);
        if (WebsiteSigningCampuses.Count == 0 && !HasClassroomDeliveryStatus)
            ClassroomDeliveryStatus = "请先生成学生配置包，才能控制学生电脑。";
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

    public bool IsExecuting { get => _isExecuting; private set { _isExecuting = value; Changed(); Changed(nameof(CanReadDeviceHealth)); NotifyDeviceHealthBatchProperties(); foreach (var alert in _deviceHealthAlerts) alert.SetBusy(value); Changed(nameof(CanToggleSeatSwapMode)); Changed(nameof(CanEditRoomPlan)); Changed(nameof(CanInstallTeacherVeyon)); Changed(nameof(CanGenerateStudentPackage)); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(CanDisableWebsitePolicy)); Changed(nameof(CanReadWebsitePolicyStatus)); Changed(nameof(CanMigrateEdgeRealtime)); Changed(nameof(CanRestoreEdgeLegacy)); Changed(nameof(CanImportCommonWebsiteBlacklist)); Changed(nameof(CanImportCommonWebsiteAllowlist)); Changed(nameof(CanImportCommonCombinedWebsitePolicy)); Changed(nameof(CanAddObservedWebsite)); Changed(nameof(CanPushApplicationPolicy)); Changed(nameof(CanDisableApplicationPolicy)); Changed(nameof(CanPushStudentSystemPolicy)); Changed(nameof(StudentSystemPolicyPushGuidance)); Changed(nameof(CanDisableStudentSystemPolicy)); Changed(nameof(CanReadApplicationPolicyAudit)); Changed(nameof(CanReadApplicationInventory)); Changed(nameof(CanAddSelectedApplicationRules)); Changed(nameof(CanFillFailedWebsiteTargets)); Changed(nameof(CanReadWebsiteLocations)); Changed(nameof(CanApplyWebsiteLocation)); Changed(nameof(CanUseSelectedClassroomLocation)); Changed(nameof(CanReplaceWebsiteSigningKey)); Changed(nameof(CanAddRoomToVeyon)); Changed(nameof(CanPrepareRoomSeatGrid)); Changed(nameof(CanCheckRoomConflicts)); Changed(nameof(CanPublishStudentPackage)); Changed(nameof(CanCheckTeacherUpdate)); Changed(nameof(CanDownloadTeacherUpdate)); Changed(nameof(CanExportOfflineTeacherUpdate)); Changed(nameof(CanVerifyOfflineTeacherUpdate)); Changed(nameof(CanInstallOfflineTeacherUpdate)); Changed(nameof(CanTrustStudentAgentIdentities)); Changed(nameof(CanDeployStudentUpdate)); Changed(nameof(CanToggleClassroomSession)); Changed(nameof(CanRetryClassroomRestores)); Changed(nameof(CanReadSeatVeyonTargets)); Changed(nameof(CanPrepareSeatSwapPlan)); Changed(nameof(CanSwapSeats)); Changed(nameof(CanExecuteSeatSwapPlan)); Changed(nameof(CanEditSeatMap)); Changed(nameof(CanToggleSeatMapEditing)); Changed(nameof(CanToggleSeatAddRemoveMode)); Changed(nameof(CanAssignMissingSeatComputerNumbers)); Changed(nameof(CanStartRoomCreation)); Changed(nameof(CanSyncSeatGridWithVeyon)); Changed(nameof(CanApplySeatGridDimensions)); Changed(nameof(CanOpenVeyonConfigurator)); Changed(nameof(CanDeleteRoomProfile)); RefreshSeatMapCells(); } }
    private int _classroomPolicyIndex;
    private int _applicationPolicyStepIndex;
    private bool _areClassroomTargetsExpanded = true;

    public bool AreClassroomTargetsExpanded
    {
        get => _areClassroomTargetsExpanded;
        set
        {
            if (_areClassroomTargetsExpanded == value) return;
            _areClassroomTargetsExpanded = value;
            Changed();
        }
    }

    public bool IsWebsitePolicyPage { get => _classroomPolicyIndex == 0; set { if (value) SelectClassroomPolicy(0); } }
    public bool IsApplicationPolicyPage { get => _classroomPolicyIndex == 1; set { if (value) SelectClassroomPolicy(1); } }
    public bool IsSystemPolicyPage { get => _classroomPolicyIndex == 2; set { if (value) SelectClassroomPolicy(2); } }
    public string ClassroomTargetSummary => $"策略目标 · { (string.IsNullOrWhiteSpace(CampusId) ? "未选择校区" : CampusId.Trim()) } · " +
        $"已选 {WebsitePolicyTargetChoices.Count(item => item.IsSelected)}/{WebsitePolicyTargetChoices.Count} 名学生";
    public bool HasActiveClassroomSession => _activeClassroomSession is not null;
    public bool HasNoActiveClassroomSession => !HasActiveClassroomSession;
    public string ClassroomOverviewSessionValue => HasActiveClassroomSession ? "进行中" : "未开始";
    public string ClassroomOverviewDeviceCount => (_activeClassroomSession?.Targets.Length ??
        SelectedRoomProfile?.ComputerCount ?? 0).ToString(CultureInfo.CurrentCulture);
    public string ClassroomOverviewReceiptCount => (_classroomQuickControlSnapshot?.Devices.Count(item =>
        item.Phase is ClassroomQuickControlPhase.WebsiteReported or ClassroomQuickControlPhase.WebsiteApplied or
            ClassroomQuickControlPhase.InputLockedVerified) ?? 0).ToString(CultureInfo.CurrentCulture);
    public string ClassroomOverviewPendingCount => (_classroomQuickControlSnapshot?.Devices.Count(item =>
        item.Phase is ClassroomQuickControlPhase.WaitingForAgent or ClassroomQuickControlPhase.WebsiteApplying or
            ClassroomQuickControlPhase.Switching or ClassroomQuickControlPhase.InputLocking or
            ClassroomQuickControlPhase.ReleasePending or ClassroomQuickControlPhase.Unknown or
            ClassroomQuickControlPhase.Conflict or ClassroomQuickControlPhase.Failed or
            ClassroomQuickControlPhase.Unsupported) ?? 0).ToString(CultureInfo.CurrentCulture);
    public bool CanStartClassroomQuickControl => TeacherClassroomQuickControlBuildFeatures.PilotEnabled &&
        HasActiveClassroomSession && !_isClassroomQuickControlBusy;
    public bool CanReleaseClassroomQuickControl => HasActiveClassroomSession && !_isClassroomQuickControlBusy;
    public string ClassroomSessionActionText => HasActiveClassroomSession ? "结束互动" : "开始互动";

    public void SetClassroomQuickControlBusy(bool busy)
    {
        if (_isClassroomQuickControlBusy == busy) return;
        _isClassroomQuickControlBusy = busy;
        Changed(nameof(CanStartClassroomQuickControl));
        Changed(nameof(CanReleaseClassroomQuickControl));
    }
    public ObservableCollection<TeacherClassroomQuickControlDeviceItem> ClassroomQuickControlDevices =>
        _classroomQuickControlDevices;
    public bool HasClassroomQuickControlDevices => _classroomQuickControlDevices.Count > 0;
    public bool HasNoClassroomQuickControlDevices => !HasClassroomQuickControlDevices;
    public string ClassroomQuickControlProgressHeader => _classroomQuickControlSnapshot is { } snapshot
        ? $"逐台执行进度 · {snapshot.Devices.Count} 台"
        : "逐台执行进度";
    public string ClassroomQuickControlRoomSummary => _classroomQuickControlSnapshot is { } snapshot
        ? $"{(string.IsNullOrWhiteSpace(snapshot.RoomName) ? "当前教室" : snapshot.RoomName)} · 已选择 {snapshot.Devices.Count}/{Math.Max(snapshot.TargetCount, snapshot.Devices.Count)} 台学生电脑"
        : HasActiveClassroomSession ? ClassroomSessionSummary : "当前没有活动课堂";
    public string ClassroomQuickControlPresetSummary
    {
        get
        {
            try
            {
                var draft = string.IsNullOrWhiteSpace(CampusId) ? null : WebsitePolicyListDraftStore.Read(CampusId);
                if (draft is null) return "尚未保存网站规则";
                var blocked = draft.BlockedDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
                var allowed = draft.AllowedDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
                return $"禁止 {blocked} 条 · 允许 {allowed} 条";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return "当前网站预设暂不可读取 · " + exception.Message;
            }
        }
    }
    public string ClassroomQuickControlSelectionSummary => _classroomQuickControlSnapshot is not { } snapshot
        ? HasActiveClassroomSession ? "当前选择：尚未启用课堂快捷管控" : _classroomQuickControlSessionNotice
        : GetClassroomQuickControlSelectionSummary(snapshot);
    public string ClassroomQuickControlProgressSummary => _classroomQuickControlSnapshot is not { } snapshot
        ? _classroomQuickControlSessionNotice.Length > 0 ? _classroomQuickControlSessionNotice :
            HasActiveClassroomSession ? "尚未启用快捷管控。" : "开始互动后可查看逐台进度。"
        : GetClassroomQuickControlProgressSummary(snapshot);
    public string ClassroomQuickControlBadgeText => _classroomQuickControlSnapshot is not { } snapshot
        ? HasActiveClassroomSession ? "当前未启用" : _classroomQuickControlSessionNotice.Length > 0 ? "本堂课已结束" : "等待互动开始"
        : GetClassroomQuickControlBadge(snapshot);
    public IBrush ClassroomQuickControlBadgeBrush => _classroomQuickControlSnapshot is not { } snapshot
        ? new SolidColorBrush(Color.Parse("#617985"))
        : snapshot.Devices.Any(item => item.Phase is ClassroomQuickControlPhase.Conflict or
                ClassroomQuickControlPhase.Failed or ClassroomQuickControlPhase.Unknown or
                ClassroomQuickControlPhase.Unsupported or ClassroomQuickControlPhase.ReleasePending)
            ? new SolidColorBrush(Color.Parse("#B45309"))
            : snapshot.Devices.Any(item => item.Phase == ClassroomQuickControlPhase.WaitingForAgent)
                ? new SolidColorBrush(Color.Parse("#B45309"))
                : snapshot.Devices.Any(item => item.Mode != DeviceControlMode.None)
                    ? new SolidColorBrush(Color.Parse("#16803C"))
                    : new SolidColorBrush(Color.Parse("#617985"));

    public void UpdateClassroomQuickControlSnapshot(ClassroomQuickControlSnapshot snapshot)
    {
        if (_classroomQuickControlSnapshot is { } current &&
            current.SessionId == snapshot.SessionId && current.RoomName == snapshot.RoomName &&
            current.TargetCount == snapshot.TargetCount && current.Devices.SequenceEqual(snapshot.Devices))
            return;
        _classroomQuickControlSessionNotice = "";
        _classroomQuickControlSnapshot = snapshot;
        var ordered = snapshot.Devices
            .OrderBy(item => item.Phase == ClassroomQuickControlPhase.WaitingForAgent ? 0 :
                item.Phase is ClassroomQuickControlPhase.Unknown or ClassroomQuickControlPhase.Conflict or
                    ClassroomQuickControlPhase.Failed or ClassroomQuickControlPhase.ReleasePending ? 1 : 2)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var target = ordered[index];
            if (index < _classroomQuickControlDevices.Count &&
                _classroomQuickControlDevices[index].TargetId == target.TargetId)
            {
                _classroomQuickControlDevices[index].Update(target);
                continue;
            }

            var existingIndex = -1;
            for (var candidateIndex = index + 1; candidateIndex < _classroomQuickControlDevices.Count; candidateIndex++)
            {
                if (_classroomQuickControlDevices[candidateIndex].TargetId != target.TargetId) continue;
                existingIndex = candidateIndex;
                break;
            }
            if (existingIndex >= 0)
            {
                _classroomQuickControlDevices.Move(existingIndex, index);
                _classroomQuickControlDevices[index].Update(target);
            }
            else
            {
                _classroomQuickControlDevices.Insert(index, new TeacherClassroomQuickControlDeviceItem(target));
            }
        }
        while (_classroomQuickControlDevices.Count > ordered.Length)
            _classroomQuickControlDevices.RemoveAt(_classroomQuickControlDevices.Count - 1);
        Changed(nameof(HasClassroomQuickControlDevices));
        Changed(nameof(HasNoClassroomQuickControlDevices));
        Changed(nameof(ClassroomQuickControlProgressHeader));
        Changed(nameof(ClassroomQuickControlRoomSummary));
        Changed(nameof(ClassroomQuickControlPresetSummary));
        Changed(nameof(ClassroomQuickControlSelectionSummary));
        Changed(nameof(ClassroomQuickControlProgressSummary));
        Changed(nameof(ClassroomQuickControlBadgeText));
        Changed(nameof(ClassroomQuickControlBadgeBrush));
        Changed(nameof(ClassroomOverviewReceiptCount));
        Changed(nameof(ClassroomOverviewPendingCount));
    }

    public void SetClassroomQuickControlSessionNotice(string message)
    {
        if (_classroomQuickControlSnapshot is null && _classroomQuickControlSessionNotice == message) return;
        _classroomQuickControlSnapshot = null;
        _classroomQuickControlSessionNotice = message ?? "";
        _classroomQuickControlDevices.Clear();
        Changed(nameof(HasClassroomQuickControlDevices));
        Changed(nameof(HasNoClassroomQuickControlDevices));
        Changed(nameof(ClassroomQuickControlProgressHeader));
        Changed(nameof(ClassroomQuickControlRoomSummary));
        Changed(nameof(ClassroomQuickControlSelectionSummary));
        Changed(nameof(ClassroomQuickControlProgressSummary));
        Changed(nameof(ClassroomQuickControlBadgeText));
        Changed(nameof(ClassroomQuickControlBadgeBrush));
        Changed(nameof(ClassroomOverviewReceiptCount));
        Changed(nameof(ClassroomOverviewPendingCount));
    }

    private static string GetClassroomQuickControlSelectionSummary(ClassroomQuickControlSnapshot snapshot)
    {
        var active = snapshot.Devices.Where(item => item.Phase != ClassroomQuickControlPhase.WaitingForAgent &&
                                                     item.Mode != DeviceControlMode.None)
            .Select(item => item.Mode).Distinct().ToArray();
        var selection = active.Length switch
        {
            0 => "尚未启用快捷管控",
            1 when active[0] == DeviceControlMode.WebsiteRestriction => "网站限制",
            1 when active[0] == DeviceControlMode.InputLocked => "锁定键盘与鼠标",
            _ => "多种管控状态"
        };
        return selection;
    }

    private static string GetClassroomQuickControlProgressSummary(ClassroomQuickControlSnapshot snapshot)
    {
        var devices = snapshot.Devices;
        var receipts = devices.Count(item => item.Phase is ClassroomQuickControlPhase.WebsiteApplied or
            ClassroomQuickControlPhase.WebsiteReported or ClassroomQuickControlPhase.InputLockedVerified);
        var waiting = devices.Count(item => item.Phase == ClassroomQuickControlPhase.WaitingForAgent);
        var sending = devices.Count(item => item.Phase is ClassroomQuickControlPhase.WebsiteApplying or
            ClassroomQuickControlPhase.Switching or ClassroomQuickControlPhase.InputLocking);
        var review = devices.Count(item => item.Phase is ClassroomQuickControlPhase.Unknown or
            ClassroomQuickControlPhase.Conflict or ClassroomQuickControlPhase.Failed or
            ClassroomQuickControlPhase.Unsupported);
        var restoring = devices.Count(item => item.Phase == ClassroomQuickControlPhase.ReleasePending);
        if (receipts + waiting + sending + review + restoring == 0)
            return "尚未启用快捷管控。";
        return $"已回执 {receipts} · 执行中 {sending} · 等待上线 {waiting} · 需核对 {review} · 恢复中 {restoring}";
    }

    private static string GetClassroomQuickControlBadge(ClassroomQuickControlSnapshot snapshot)
    {
        if (snapshot.Devices.Any(item => item.Phase is ClassroomQuickControlPhase.Conflict or
                ClassroomQuickControlPhase.Failed or ClassroomQuickControlPhase.Unknown or
                ClassroomQuickControlPhase.Unsupported or ClassroomQuickControlPhase.ReleasePending))
            return "有设备需核对";
        if (snapshot.Devices.Any(item => item.Phase == ClassroomQuickControlPhase.WaitingForAgent))
            return "等待 Agent 上线";
        return snapshot.Devices.Any(item => item.Mode != DeviceControlMode.None)
            ? "管控已启用" : "当前未启用";
    }
    public string ClassroomSessionSummary => _activeClassroomSession is { } active
        ? $"本堂课 · {active.Targets.Length} 台电脑"
        : SelectedRoomProfile is { } room
            ? room.ComputerCount > 0
                ? $"{room.ComputerCount} 台学生电脑可用于互动"
                : "此地点还没有配置电脑；请先在座位表添加座位并添加到 Veyon。"
            : "请先选择学生电脑范围。";
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
    public string ClassroomTaskProgressDraft
    {
        get => _classroomTaskProgressDraft;
        set
        {
            var next = value ?? "";
            if (_classroomTaskProgressDraft == next) return;
            _classroomTaskProgressDraft = next;
            Changed();
            Changed(nameof(CanAddClassroomTask));
        }
    }
    public int ClassroomTaskProgressCompletedCount => _activeClassroomTaskProgress?.CompletedCount ?? 0;
    public int ClassroomTaskProgressTotalCount => _activeClassroomTaskProgress?.Tasks.Length ?? 0;
    public bool HasClassroomTaskProgress => ClassroomTaskProgressTotalCount > 0;
    public string ClassroomTaskProgressSummary => HasClassroomTaskProgress
        ? $"{ClassroomTaskProgressCompletedCount}/{ClassroomTaskProgressTotalCount} 项已完成"
        : "";
    public bool CanAddClassroomTask => HasActiveClassroomSession && !HasClassroomTaskProgressError &&
        ClassroomTaskProgressTotalCount < ClassroomTaskProgress.MaximumTasks &&
        !string.IsNullOrWhiteSpace(ClassroomTaskProgressDraft);
    public string ClassroomTaskProgressStatus
    {
        get => _classroomTaskProgressStatus;
        private set
        {
            if (_classroomTaskProgressStatus == value) return;
            _classroomTaskProgressStatus = value;
            Changed();
            Changed(nameof(HasClassroomTaskProgressStatus));
            Changed(nameof(HasClassroomTaskProgressError));
            Changed(nameof(CanAddClassroomTask));
        }
    }
    public bool HasClassroomTaskProgressStatus => !string.IsNullOrWhiteSpace(ClassroomTaskProgressStatus);
    public bool HasClassroomTaskProgressError => ClassroomTaskProgressStatus.StartsWith("无法", StringComparison.Ordinal) ||
        ClassroomTaskProgressStatus.StartsWith("课堂任务进度无法", StringComparison.Ordinal);
    public bool HasClassroomRestores => _pendingClassroomRestores.Count > 0 ||
                                        !string.IsNullOrWhiteSpace(_classroomRestoreStatus);
    public bool HasPendingClassroomRestoreItems => _pendingClassroomRestores.Count > 0;
    public int PendingClassroomRestoreCount => _pendingClassroomRestores.Count;
    public string ClassroomRestoreSummary
    {
        get
        {
            IReadOnlyDictionary<Guid, ClassroomSession> sessions;
            try
            {
                sessions = _classroomSessionStore.ReadRecent().ToDictionary(item => item.SessionId);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                sessions = new Dictionary<Guid, ClassroomSession>();
            }

            var lines = _pendingClassroomRestores.Take(24).Select(item =>
                $"{item.RoomName} · {(sessions.TryGetValue(item.SessionId, out var session)
                    ? GetStudentNameForSessionTarget(session, item.DeviceLabel)
                    : "未填写姓名")} · {(item.Kind == ClassroomPolicyKind.Website ? "学生代理" : "应用")}")
                .ToList();
            if (_pendingClassroomRestores.Count > lines.Count)
                lines.Add($"还有 {_pendingClassroomRestores.Count - lines.Count} 项");
            var summary = _pendingClassroomRestores.Count > 0
                ? $"{_pendingClassroomRestores.Count} 项限制尚未恢复：\n" +
                  string.Join(Environment.NewLine, lines)
                : "";
            return string.Join(Environment.NewLine,
                new[] { summary, _classroomRestoreStatus }.Where(item => !string.IsNullOrWhiteSpace(item)));
        }
    }

    public string GetStudentDisplayNameForTarget(string computerLabel)
    {
        if (!string.IsNullOrWhiteSpace(computerLabel))
        {
            var key = NormalizeTargetKey(computerLabel);
            var choice = WebsitePolicyTargetChoices.FirstOrDefault(item =>
                string.Equals(NormalizeTargetKey(item.Target), key, StringComparison.OrdinalIgnoreCase));
            if (choice is not null) return choice.StudentName;
        }
        return GetStudentNameForComputerLabel(computerLabel);
    }

    private string GetStudentNameForComputerLabel(string computerLabel)
    {
        if (string.IsNullOrWhiteSpace(computerLabel)) return "未填写姓名";
        var key = NormalizeTargetKey(computerLabel);
        var matches = new Dictionary<(Guid CampusId, Guid RoomId, Guid SeatId), string>();
        foreach (var campus in CampusProfiles)
        foreach (var room in campus.Rooms)
        {
            ClassroomSeatGridLayout layout;
            try { layout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId, room.StartNumber); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            { continue; }
            IReadOnlyList<string> computerNames;
            IReadOnlyList<int> computerNumbers;
            try
            {
                computerNames = room.GetComputerNames();
                computerNumbers = room.GetComputerNumbers();
            }
            catch (InvalidDataException) { computerNames = Array.Empty<string>(); computerNumbers = Array.Empty<int>(); }
            foreach (var seat in layout.Seats.Where(item => !string.IsNullOrWhiteSpace(item.StudentName)))
            {
                if (string.Equals(seat.StudentName.Trim(), computerLabel.Trim(), StringComparison.OrdinalIgnoreCase))
                    matches[(campus.ProfileId, room.RoomId, seat.SeatId)] = seat.StudentName.Trim();
                if ((!string.IsNullOrWhiteSpace(seat.VeyonComputerName) &&
                     NormalizeTargetKey(seat.VeyonComputerName) == key) ||
                    (!string.IsNullOrWhiteSpace(seat.VeyonHostAddress) &&
                     NormalizeTargetKey(seat.VeyonHostAddress) == key))
                    matches[(campus.ProfileId, room.RoomId, seat.SeatId)] = seat.StudentName.Trim();
                if (seat.ComputerNumber is not { } number) continue;
                var index = -1;
                for (var candidate = 0; candidate < computerNumbers.Count; candidate++)
                    if (computerNumbers[candidate] == number) { index = candidate; break; }
                if (index < 0) continue;
                if (index < computerNames.Count && NormalizeTargetKey(computerNames[index]) == key)
                    matches[(campus.ProfileId, room.RoomId, seat.SeatId)] = seat.StudentName.Trim();
                if (room.HostOverrides is { } hosts && index < hosts.Count &&
                    !string.IsNullOrWhiteSpace(hosts[index]) && NormalizeTargetKey(hosts[index]) == key)
                    matches[(campus.ProfileId, room.RoomId, seat.SeatId)] = seat.StudentName.Trim();
            }
        }
        return matches.Count == 1 ? matches.Values.Single() : "未填写姓名";
    }

    private string GetStudentNameForSessionTarget(ClassroomSession session, string deviceLabel)
    {
        var room = CampusProfiles.FirstOrDefault(item => item.ProfileId == session.Room.CampusProfileId)?
            .Rooms.FirstOrDefault(item => item.RoomId == session.Room.RoomId);
        if (room is null) return "未填写姓名";

        IReadOnlyList<ClassroomSeatGridLocation> seats;
        try
        {
            var layout = _classroomSeatGridStore.GetLayout(session.Room.CampusProfileId,
                session.Room.RoomId, session.Room.StartNumber);
            seats = ClassroomSeatGridStore.GetLocations(layout);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return "未填写姓名";
        }

        var targetIndex = -1;
        for (var index = 0; index < session.Targets.Length; index++)
            if (string.Equals(session.Targets[index].DeviceLabel, deviceLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = index;
                break;
            }
        if (targetIndex < 0) return "未填写姓名";
        var target = room.HostOverrides is { } hosts && targetIndex < hosts.Count &&
                     !string.IsNullOrWhiteSpace(hosts[targetIndex])
            ? hosts[targetIndex].Trim()
            : deviceLabel;
        var seat = FindUniqueClassroomSeat(session, seats, target, deviceLabel, targetIndex);
        return string.IsNullOrWhiteSpace(seat?.StudentName) ? "未填写姓名" : seat.StudentName.Trim();
    }
    public bool CanRetryClassroomRestores => OperatingSystem.IsWindows() && PendingClassroomRestoreCount > 0 &&
        !HasActiveClassroomSession && !_isRetryingClassroomRestores && !_isClassroomTransitioning && !IsExecuting;
    public string ClassroomDeliveryStatus
    {
        get => _classroomDeliveryStatus;
        private set
        {
            if (_classroomDeliveryStatus == value) return;
            _classroomDeliveryStatus = value;
            Changed();
            Changed(nameof(HasClassroomDeliveryStatus));
        }
    }
    public bool HasClassroomDeliveryStatus => !string.IsNullOrWhiteSpace(ClassroomDeliveryStatus);
    public string ClassroomEventStatus
    {
        get => _classroomEventStatus;
        private set
        {
            if (_classroomEventStatus == value) return;
            _classroomEventStatus = value;
            Changed();
            Changed(nameof(HasActiveClassroomEventStatus));
        }
    }
    public bool HasActiveClassroomEventStatus => HasActiveClassroomSession &&
        !string.IsNullOrWhiteSpace(ClassroomEventStatus);
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
    public ObservableCollection<TeacherClassroomTaskItem> ClassroomTaskProgressItems { get; } = [];
    public bool HasClassroomEventItems => ClassroomEventItems.Count > 0;
    public int PendingClassroomHelpCount => ClassroomEventItems.Count(item => item.IsHelpRequest && item.CanReply);
    public ObservableCollection<TeacherClassroomChatThread> ClassroomChatThreads { get; } = [];
    public bool HasClassroomChatThreads => ClassroomChatThreads.Count > 0;
    public int UnreadClassroomChatCount => ClassroomChatThreads.Sum(item => item.UnreadCount);
    public TeacherClassroomChatThread? SelectedClassroomChatThread
    {
        get => _selectedClassroomChatThread;
        set
        {
            if (ReferenceEquals(_selectedClassroomChatThread, value)) return;
            _selectedClassroomChatThread = value;
            _selectedClassroomChatThread?.MarkRead();
            Changed();
            Changed(nameof(UnreadClassroomChatCount));
            Changed(nameof(ClassroomInteractionNavigationLabel));
            Changed(nameof(SelectedClassroomChatMessages));
            Changed(nameof(CanSendClassroomChat));
            Changed(nameof(ClassroomChatDraft));
        }
    }
    public ObservableCollection<TeacherClassroomChatMessageItem> SelectedClassroomChatMessages =>
        SelectedClassroomChatThread?.Messages ?? EmptyClassroomChatMessages;
    private static ObservableCollection<TeacherClassroomChatMessageItem> EmptyClassroomChatMessages { get; } = [];
    public string ClassroomChatDraft
    {
        get => _classroomChatDraft;
        set
        {
            if (_classroomChatDraft == value) return;
            _classroomChatDraft = value ?? "";
            Changed();
            Changed(nameof(CanSendClassroomChat));
        }
    }
    public string ClassroomChatStatus
    {
        get => _classroomChatStatus;
        private set
        {
            if (_classroomChatStatus == value) return;
            _classroomChatStatus = value;
            Changed();
            Changed(nameof(HasClassroomChatStatus));
        }
    }
    public bool HasClassroomChatStatus => !string.IsNullOrWhiteSpace(ClassroomChatStatus);
    public bool CanSendClassroomChat => HasActiveClassroomSession && !_isClassroomTransitioning &&
        SelectedClassroomChatThread?.IsSupported == true && !string.IsNullOrWhiteSpace(ClassroomChatDraft) &&
        ClassroomEventCryptography.IsValidChatMessage(ClassroomChatDraft);
    public string ClassroomInteractionNavigationLabel
    {
        get
        {
            var unread = UnreadClassroomChatCount;
            var pending = PendingClassroomHelpCount;
            if (unread == 0 && pending == 0) return "课堂控制";
            var details = new List<string>(2);
            if (unread > 0) details.Add($"{unread} 条新消息");
            if (pending > 0) details.Add($"{pending} 条待回复");
            return "课堂控制 · " + string.Join(" · ", details);
        }
    }
    public string ClassroomNavigationLabel => "学生策略";
    internal TeacherClassroomEventContext? GetActiveClassroomEventContext()
    {
        if (_activeClassroomSession is not { } active) return null;
        var targets = ResolveClassroomNetworkTargets(active).ToArray();
        if (targets.Length != active.Targets.Length)
            throw new InvalidDataException("课堂目标与签名设备地址数量不匹配。");
        var targetIds = active.Targets.Select((target, index) => (targets[index], target.TargetId))
            .ToDictionary(item => item.Item1, item => item.TargetId, StringComparer.OrdinalIgnoreCase);
        return new TeacherClassroomEventContext(ResolveClassroomSigningCampus(active), active.SessionId,
            Array.AsReadOnly(targets), targetIds, active,
            ResolveClassroomStudentDisplayNames(active, targets));
    }

    private IReadOnlyDictionary<string, string> ResolveClassroomStudentDisplayNames(ClassroomSession session,
        IReadOnlyList<string> targets)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var room = CampusProfiles.FirstOrDefault(item => item.ProfileId == session.Room.CampusProfileId)?
            .Rooms.FirstOrDefault(item => item.RoomId == session.Room.RoomId);
        IReadOnlyList<ClassroomSeatGridLocation> seats = Array.Empty<ClassroomSeatGridLocation>();
        if (room is not null)
        {
            try
            {
                var layout = _classroomSeatGridStore.GetLayout(session.Room.CampusProfileId,
                    session.Room.RoomId, session.Room.StartNumber);
                seats = ClassroomSeatGridStore.GetLocations(layout);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _ = exception;
            }
        }

        var computerNumbers = session.Room.ComputerNumbers ??
            Enumerable.Range(session.Room.StartNumber, session.Room.ComputerCount).ToArray();
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var deviceLabel = index < session.Targets.Length ? session.Targets[index].DeviceLabel : "";
            var seat = FindUniqueClassroomSeat(session, seats, target, deviceLabel, index,
                index < computerNumbers.Length ? computerNumbers[index] : null);

            var studentName = seat?.StudentName?.Trim();
            if ((string.IsNullOrWhiteSpace(studentName) || studentName == "未填写姓名") &&
                SelectedCampusProfile?.ProfileId == session.Room.CampusProfileId)
                studentName = FindConfiguredStudentName(target, deviceLabel);
            names[target] = string.IsNullOrWhiteSpace(studentName) ? "未填写姓名" : studentName;
            if (!string.IsNullOrWhiteSpace(deviceLabel)) names[deviceLabel] = names[target];
            if (!string.IsNullOrWhiteSpace(seat?.VeyonComputerName))
                names[seat.VeyonComputerName] = names[target];
            if (!string.IsNullOrWhiteSpace(seat?.VeyonHostAddress))
                names[seat.VeyonHostAddress] = names[target];
        }
        return names;
    }

    private static ClassroomSeatGridLocation? FindUniqueClassroomSeat(ClassroomSession session,
        IReadOnlyList<ClassroomSeatGridLocation> seats, string target, string deviceLabel, int index,
        int? computerNumberOverride = null)
    {
        var aliasMatches = seats.Where(item =>
            !string.IsNullOrWhiteSpace(item.VeyonHostAddress) &&
            string.Equals(NormalizeTargetKey(item.VeyonHostAddress), NormalizeTargetKey(target),
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(item.VeyonComputerName) &&
            (string.Equals(NormalizeTargetKey(item.VeyonComputerName), NormalizeTargetKey(deviceLabel),
                 StringComparison.OrdinalIgnoreCase) ||
             string.Equals(NormalizeTargetKey(item.VeyonComputerName), NormalizeTargetKey(target),
                 StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(item => item.SeatId).Take(2).ToArray();
        if (aliasMatches.Length > 0) return aliasMatches.Length == 1 ? aliasMatches[0] : null;

        var numbers = session.Room.ComputerNumbers ??
            Enumerable.Range(session.Room.StartNumber, session.Room.ComputerCount).ToArray();
        var computerNumber = computerNumberOverride ??
            (index >= 0 && index < numbers.Length ? numbers[index] : (int?)null);
        if (computerNumber is null) return null;
        var numberMatches = seats.Where(item => item.SeatNumber == computerNumber.Value)
            .DistinctBy(item => item.SeatId).Take(2).ToArray();
        return numberMatches.Length == 1 ? numberMatches[0] : null;
    }

    private string FindConfiguredStudentName(params string[] targetAliases)
    {
        var aliases = targetAliases.Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(NormalizeTargetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = WebsitePolicyTargetChoices.Where(item => aliases.Contains(NormalizeTargetKey(item.Target)))
            .Select(item => item.StudentName.Trim())
            .Where(item => item.Length > 0 && !string.Equals(item, "未填写姓名", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return names.Length == 1 ? names[0] : "未填写姓名";
    }
    internal void SetClassroomEventStatus(string status) => ClassroomEventStatus = status;
    internal void SetClassroomChatStatus(string status) => ClassroomChatStatus = status;
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

    public void AddClassroomTask()
    {
        if (!CanAddClassroomTask || _activeClassroomSession is not { } active) return;
        try
        {
            _activeClassroomTaskProgress = _classroomTaskProgressStore.Add(active.SessionId,
                ClassroomTaskProgressDraft, DateTimeOffset.UtcNow);
            ClassroomTaskProgressDraft = "";
            ClassroomTaskProgressStatus = "";
            RefreshClassroomTaskProgressItems();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClassroomTaskProgressStatus = "课堂任务进度无法保存；原文件已保留。";
        }
    }

    public void ToggleClassroomTask(Guid taskId)
    {
        if (_activeClassroomSession is not { } active || _activeClassroomTaskProgress is not { } progress) return;
        var task = progress.Tasks.FirstOrDefault(item => item.TaskId == taskId);
        if (task is null) return;
        try
        {
            _activeClassroomTaskProgress = _classroomTaskProgressStore.SetCompleted(active.SessionId,
                taskId, !task.IsCompleted, DateTimeOffset.UtcNow);
            ClassroomTaskProgressStatus = "";
            RefreshClassroomTaskProgressItems();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClassroomTaskProgressStatus = "课堂任务进度无法更新；原文件已保留。";
        }
    }

    public void RemoveClassroomTask(Guid taskId)
    {
        if (_activeClassroomSession is not { } active || _activeClassroomTaskProgress is not { } progress ||
            progress.Tasks.All(item => item.TaskId != taskId)) return;
        try
        {
            _activeClassroomTaskProgress = _classroomTaskProgressStore.Remove(active.SessionId, taskId,
                DateTimeOffset.UtcNow);
            ClassroomTaskProgressStatus = "";
            RefreshClassroomTaskProgressItems();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClassroomTaskProgressStatus = "课堂任务进度无法更新；原文件已保留。";
        }
    }

    internal void SetClassroomRestoreRetrying(bool value)
    {
        if (_isRetryingClassroomRestores == value) return;
        _isRetryingClassroomRestores = value;
        Changed(nameof(CanRetryClassroomRestores));
        Changed(nameof(CanToggleClassroomSession));
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
        _classroomChatThreadsByTarget.Clear();
        ClassroomChatThreads.Clear();
        SelectedClassroomChatThread = null;
        ClassroomChatDraft = "";
        ClassroomChatStatus = "";
        _classroomEventIds.Clear();
        _classroomEventOrder.Clear();
        Changed(nameof(HasClassroomEventItems));
        Changed(nameof(PendingClassroomHelpCount));
        Changed(nameof(UnreadClassroomChatCount));
        Changed(nameof(HasClassroomChatThreads));
        Changed(nameof(CanSendClassroomChat));
        Changed(nameof(ClassroomInteractionNavigationLabel));
        Changed(nameof(ClassroomNavigationLabel));
    }

    internal void ApplyClassroomEvents(Guid sessionId, IEnumerable<ClassroomEvent> events)
    {
        ResetClassroomEventFeed(sessionId);
        var seatLabels = ReadActiveSeatLabels(sessionId);
        var studentNames = _activeClassroomSession is { } active && active.SessionId == sessionId
            ? ResolveClassroomStudentDisplayNames(active, ResolveClassroomNetworkTargets(active))
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ClassroomEventItems.Where(item => item.IsHelpRequest))
        {
            item.SetSeatLocation(seatLabels.GetValueOrDefault(item.Event.Target));
            item.SetStudentName(studentNames.GetValueOrDefault(item.Event.Target, "未填写姓名"));
        }
        foreach (var classroomEvent in events)
        {
            if (!_classroomEventIds.Add(classroomEvent.EventId))
            {
                if (classroomEvent.Type == ClassroomEventType.ClassroomChatMessage &&
                    classroomEvent.Sender == ClassroomEventSender.Teacher &&
                    _classroomChatThreadsByTarget.TryGetValue(classroomEvent.Target, out var retriedThread))
                    retriedThread.MarkRetrySubmitted(classroomEvent.EventId, classroomEvent.ExpiresUtc);
                continue;
            }
            _classroomEventOrder.Enqueue(classroomEvent.EventId);
            if (classroomEvent.Type == ClassroomEventType.HelpRequested &&
                classroomEvent.Sender == ClassroomEventSender.Student)
            {
                var row = new TeacherClassroomEventItem(classroomEvent,
                    seatLabels.GetValueOrDefault(classroomEvent.Target),
                    studentNames.GetValueOrDefault(classroomEvent.Target, "未填写姓名"));
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
                ClassroomEventItems.Insert(0, new TeacherClassroomEventItem(classroomEvent,
                    displayName: studentNames.GetValueOrDefault(classroomEvent.Target, "未填写姓名")));
            }
            else if (classroomEvent.Type == ClassroomEventType.ClassroomChatMessage &&
                     _classroomChatThreadsByTarget.TryGetValue(classroomEvent.Target, out var chatThread))
            {
                chatThread.AddMessage(classroomEvent,
                    isIncoming: classroomEvent.Sender == ClassroomEventSender.Student &&
                        !ReferenceEquals(chatThread, SelectedClassroomChatThread));
                Changed(nameof(UnreadClassroomChatCount));
                Changed(nameof(ClassroomInteractionNavigationLabel));
            }
            else if (classroomEvent.Type == ClassroomEventType.ClassroomChatDelivered &&
                     classroomEvent.CorrelationId is { } deliveredEventId &&
                     _classroomChatThreadsByTarget.TryGetValue(classroomEvent.Target, out var deliveredThread))
            {
                deliveredThread.MarkDelivered(deliveredEventId);
            }

            while (_classroomEventOrder.Count > ClassroomEventBuffer.MaximumEventsPerSession)
            {
                var expiredEventId = _classroomEventOrder.Dequeue();
                _classroomEventIds.Remove(expiredEventId);
                if (_classroomHelpRows.Remove(expiredEventId, out var expiredRow))
                    ClassroomEventItems.Remove(expiredRow);
            }
        }
        // Keep the teacher view useful during a long class. The event channel is
        // an audit stream; the UI only needs the most recent messages.
        while (ClassroomEventItems.Count > 40)
        {
            var oldest = ClassroomEventItems[^1];
            ClassroomEventItems.RemoveAt(ClassroomEventItems.Count - 1);
            _classroomHelpRows.Remove(oldest.EventId);
        }
        foreach (var row in ClassroomEventItems) row.RefreshExpiry();
        foreach (var thread in ClassroomChatThreads)
            thread.RefreshDeliveryStatuses(DateTimeOffset.UtcNow);
        Changed(nameof(HasClassroomEventItems));
        Changed(nameof(PendingClassroomHelpCount));
        Changed(nameof(ClassroomInteractionNavigationLabel));
        Changed(nameof(ClassroomNavigationLabel));
    }

    internal void SyncClassroomChatTargets(Guid? sessionId, IEnumerable<string> supportedTargets)
    {
        if (sessionId is null || _activeClassroomSession is not { } active || active.SessionId != sessionId)
        {
            if (_classroomChatThreadsByTarget.Count > 0)
            {
                _classroomChatThreadsByTarget.Clear();
                ClassroomChatThreads.Clear();
                SelectedClassroomChatThread = null;
                ClassroomChatStatus = "课堂已结束，聊天记录已清除。";
                Changed(nameof(HasClassroomChatThreads));
            }
            return;
        }

        var context = GetActiveClassroomEventContext();
        if (context is null || context.SessionId != sessionId) return;
        var supportedTargetList = supportedTargets.ToArray();
        IReadOnlyList<string> normalizedSupportedTargets = supportedTargetList.Length == 0 ?
            Array.Empty<string>() : WebsitePolicyTransport.NormalizeTargets(supportedTargetList);
        var supported = new HashSet<string>(normalizedSupportedTargets, StringComparer.OrdinalIgnoreCase);
        if (_classroomChatThreadsByTarget.Count == context.Targets.Count &&
            context.Targets.All(target => _classroomChatThreadsByTarget.TryGetValue(target, out var existing) &&
                existing.IsSupported == supported.Contains(target)))
            return;
        IReadOnlyList<ClassroomSeatGridLocation> seats = Array.Empty<ClassroomSeatGridLocation>();
        try
        {
            var layout = _classroomSeatGridStore.GetLayout(active.Room.CampusProfileId, active.Room.RoomId,
                active.Room.StartNumber);
            seats = ClassroomSeatGridStore.GetLocations(layout);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Keep chats addressable by computer label when the optional seating layout cannot be read.
        }

        var available = new HashSet<string>(context.Targets, StringComparer.OrdinalIgnoreCase);
        foreach (var thread in _classroomChatThreadsByTarget.Values.Where(item => !available.Contains(item.Target)).ToArray())
        {
            _classroomChatThreadsByTarget.Remove(thread.Target);
            ClassroomChatThreads.Remove(thread);
            if (ReferenceEquals(SelectedClassroomChatThread, thread)) SelectedClassroomChatThread = null;
        }

        for (var index = 0; index < context.Targets.Count; index++)
        {
            var target = context.Targets[index];
            var deviceLabel = index < active.Targets.Length ? active.Targets[index].DeviceLabel : "";
            var computerNumber = active.Room.ComputerNumbers is { } explicitNumbers &&
                                 index < explicitNumbers.Length ? explicitNumbers[index] : (int?)null;
            var seat = FindUniqueClassroomSeat(active, seats, target, deviceLabel, index, computerNumber);
            var studentName = context.TargetDisplayNames.GetValueOrDefault(target, "未填写姓名");
            var seatName = seat is null ? "" : $"第 {seat.Row} 排 · 第 {seat.Column} 列";
            var title = string.IsNullOrWhiteSpace(studentName) ? "未填写姓名" : studentName;
            var detail = string.Join(" · ", new[] { seatName }
                .Where(item => !string.IsNullOrWhiteSpace(item)));
            var isSupported = supported.Contains(target);
            if (!_classroomChatThreadsByTarget.TryGetValue(target, out var thread))
            {
                thread = new TeacherClassroomChatThread(target, title, detail, isSupported);
                _classroomChatThreadsByTarget.Add(target, thread);
                ClassroomChatThreads.Add(thread);
            }
            else
            {
                thread.UpdateDisplay(title, detail, isSupported);
            }
        }

        if (SelectedClassroomChatThread is null && ClassroomChatThreads.Count > 0)
            SelectedClassroomChatThread = ClassroomChatThreads.FirstOrDefault(item => item.IsSupported);
        Changed(nameof(HasClassroomChatThreads));
        Changed(nameof(UnreadClassroomChatCount));
        Changed(nameof(ClassroomInteractionNavigationLabel));
        Changed(nameof(CanSendClassroomChat));
    }

    internal void SetClassroomChatSendStatus(string status) => ClassroomChatStatus = status;

    internal void MarkClassroomChatRetrySubmitted(Guid eventId, DateTimeOffset expiresUtc)
    {
        foreach (var thread in _classroomChatThreadsByTarget.Values)
            if (thread.MarkRetrySubmitted(eventId, expiresUtc)) return;
    }

    internal void MarkClassroomEventReply(Guid helpEventId, string message)
    {
        if (_classroomHelpRows.TryGetValue(helpEventId, out var row))
        {
            row.MarkReply(message);
            Changed(nameof(PendingClassroomHelpCount));
            Changed(nameof(ClassroomInteractionNavigationLabel));
            Changed(nameof(ClassroomNavigationLabel));
        }
    }

    internal void SetClassroomEventReplyError(Guid helpEventId, string message)
    {
        if (_classroomHelpRows.TryGetValue(helpEventId, out var row)) row.SetError(message);
    }
    public bool CanToggleClassroomSession => OperatingSystem.IsWindows() && !IsExecuting &&
        !_isClassroomTransitioning && !_isRetryingClassroomRestores && (HasActiveClassroomSession ||
            SelectedCampusProfile is not null && SelectedRoomProfile is { ComputerCount: > 0 } &&
            WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal));

    private void SelectClassroomPolicy(int index)
    {
        _classroomPolicyIndex = index;
        if (!string.IsNullOrWhiteSpace(CampusId) && !string.IsNullOrWhiteSpace(WebsiteTargets))
            AreClassroomTargetsExpanded = false;
        if (index == 0) LoadWebsitePolicyLists(CampusId);
        Changed(nameof(IsWebsitePolicyPage)); Changed(nameof(IsApplicationPolicyPage)); Changed(nameof(IsSystemPolicyPage));
    }
    public bool IsClassroomPage
    {
        get => _selectedPage == "classroom";
        set
        {
            if (!value) return;
            SelectPage("classroom");
            if (string.IsNullOrWhiteSpace(WebsiteTargets)) _ = ReadWebsiteLocationsAsync();
        }
    }
    public bool IsClassroomExperiencePage
    {
        get => _selectedPage == "lesson";
        set
        {
            if (!value) return;
            SelectPage("lesson");
            _ = ReadClassroomLocationsAsync();
        }
    }
    public bool IsClassroomWorkspacePage => IsClassroomPage || IsClassroomExperiencePage;
    public bool IsApplicationAccountsStep { get => _applicationPolicyStepIndex == 0; set { if (value) SetApplicationPolicyStep(0); } }
    public bool IsApplicationSoftwareStep { get => _applicationPolicyStepIndex == 1; set { if (value) SetApplicationPolicyStep(1); } }
    public bool IsApplicationApplyStep { get => _applicationPolicyStepIndex == 2; set { if (value) SetApplicationPolicyStep(2); } }
    private void SetApplicationPolicyStep(int value)
    {
        _applicationPolicyStepIndex = value;
        Changed(nameof(IsApplicationAccountsStep));
        Changed(nameof(IsApplicationSoftwareStep));
        Changed(nameof(IsApplicationApplyStep));
    }
    public bool IsUpdatesPage { get => _selectedPage == "updates"; set { if (value) SelectPage("updates"); } }
    public bool IsHealthCenterPage { get => _selectedPage == "health"; set { if (value) SelectPage("health"); } }
    public ObservableCollection<TeacherDeviceHealthRow> DeviceHealthRows => _deviceHealthRows;
    public IReadOnlyList<TeacherDeviceHealthRow> DeviceHealthPageRows => _deviceHealthPageRows;
    public ObservableCollection<TeacherDeviceHealthAlert> DeviceHealthAlerts => _deviceHealthAlerts;
    public bool HasDeviceHealthAlerts => _deviceHealthAlerts.Count > 0;
    public bool HasDeviceHealthRows => _deviceHealthRows.Count > 0;
    public bool HasDeviceHealthFilteredRows => _deviceHealthFilteredCount > 0;
    public bool HasDeviceHealthFilterEmpty => HasDeviceHealthRows && !HasDeviceHealthFilteredRows;
    public int DeviceHealthAllCount => _deviceHealthRows.Count;
    public int DeviceHealthHealthyCount => _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Healthy);
    public int DeviceHealthAttentionCount => _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Attention);
    public int DeviceHealthManualReviewCount => _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NeedsManualReview);
    public int DeviceHealthNoResponseCount => _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NoResponse);
    public int DeviceHealthPendingCount => _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Pending);
    public string DeviceHealthListSummary => !HasDeviceHealthRows ? "等待监控设备状态" :
        DeviceHealthPendingCount > 0
            ? $"监控 {DeviceHealthAllCount} 台 · 等待读取 {DeviceHealthPendingCount} 台"
            : $"监控 {DeviceHealthAllCount} 台所选电脑";
    public IReadOnlyList<string> DeviceHealthCategoryFilters { get; } =
        ["全部状态", "正常", "需关注", "需人工处理", "未收到回执", "等待读取"];
    public string DeviceHealthSearchText
    {
        get => _deviceHealthSearchText;
        set
        {
            value ??= "";
            if (_deviceHealthSearchText == value) return;
            _deviceHealthSearchText = value;
            _deviceHealthPageIndex = 0;
            Changed();
            RefreshDeviceHealthView();
        }
    }
    public int DeviceHealthCategoryFilterIndex
    {
        get => _deviceHealthCategoryFilterIndex;
        set
        {
            var normalized = Math.Clamp(value, 0, DeviceHealthCategoryFilters.Count - 1);
            if (_deviceHealthCategoryFilterIndex == normalized) return;
            _deviceHealthCategoryFilterIndex = normalized;
            _deviceHealthPageIndex = 0;
            Changed();
            RefreshDeviceHealthView();
        }
    }
    public int DeviceHealthPageCount => Math.Max(1, (int)Math.Ceiling(_deviceHealthFilteredCount / (double)DeviceHealthPageSize));
    public string DeviceHealthPageLabel => _deviceHealthFilteredCount == 0
        ? "0 台"
        : $"第 {_deviceHealthPageIndex + 1} / {DeviceHealthPageCount} 页 · " +
          $"{_deviceHealthPageIndex * DeviceHealthPageSize + 1}–{Math.Min((_deviceHealthPageIndex + 1) * DeviceHealthPageSize, _deviceHealthFilteredCount)} / {_deviceHealthFilteredCount} 台";
    public bool CanPreviousDeviceHealthPage => _deviceHealthPageIndex > 0;
    public bool CanNextDeviceHealthPage => _deviceHealthPageIndex + 1 < DeviceHealthPageCount;
    public string DeviceHealthStatus
    {
        get => _deviceHealthStatus;
        private set { if (_deviceHealthStatus == value) return; _deviceHealthStatus = value; Changed(); }
    }
    public string DeviceHealthCacheStatus
    {
        get => _deviceHealthCacheStatus;
        private set { if (_deviceHealthCacheStatus == value) return; _deviceHealthCacheStatus = value; Changed(); }
    }
    public string DeviceRuntimeSummaryStatus
    {
        get => _deviceRuntimeSummaryStatus;
        private set { if (_deviceRuntimeSummaryStatus == value) return; _deviceRuntimeSummaryStatus = value; Changed(); }
    }
    public bool IsPollingDeviceRuntimeSummary => _isPollingDeviceRuntimeSummary;
    public bool IsReadingDeviceHealth
    {
        get => _isReadingDeviceHealth;
        private set
        {
            if (_isReadingDeviceHealth == value) return;
            _isReadingDeviceHealth = value;
            Changed();
            Changed(nameof(CanReadDeviceHealth));
            NotifyDeviceHealthBatchProperties();
        }
    }
    public bool IsRunningDeviceHealthBatch
    {
        get => _isRunningDeviceHealthBatch;
        private set
        {
            if (_isRunningDeviceHealthBatch == value) return;
            _isRunningDeviceHealthBatch = value;
            Changed();
            NotifyDeviceHealthBatchProperties();
        }
    }
    public string DeviceHealthBatchStatus
    {
        get => _deviceHealthBatchStatus;
        private set { if (_deviceHealthBatchStatus == value) return; _deviceHealthBatchStatus = value; Changed(); }
    }
    public int DeviceHealthSelectedCount => GetSelectedWebsitePolicyTargets().Count;
    public int DeviceHealthRecentlyReachableCount => GetSelectedHealthRows().Count(row => row.IsRecentlyReachable);
    public int DeviceHealthVeyonEligibleCount => GetSelectedHealthRows().Count(row =>
        row.IsRecentlyReachable && row.CanEnsureVeyonServiceRunning);
    public int DeviceHealthEdgeEligibleCount => GetSelectedHealthRows().Count(row =>
        row.IsRecentlyReachable && row.CanRefreshLegacyEdgePolicy);
    public int DeviceHealthVeyonSkippedCount => Math.Max(0, DeviceHealthSelectedCount - DeviceHealthVeyonEligibleCount);
    public int DeviceHealthEdgeSkippedCount => Math.Max(0, DeviceHealthSelectedCount - DeviceHealthEdgeEligibleCount);
    public string DeviceHealthBatchPreview =>
        $"所选 {DeviceHealthSelectedCount} 台 · 最近两分钟有签名回执 {DeviceHealthRecentlyReachableCount} 台 · " +
        $"Veyon 可修复 {DeviceHealthVeyonEligibleCount} 台（跳过 {DeviceHealthVeyonSkippedCount} 台） · " +
        $"Edge 可刷新 {DeviceHealthEdgeEligibleCount} 台（跳过 {DeviceHealthEdgeSkippedCount} 台）";
    public bool CanBatchEnsureVeyonServiceRunning => OperatingSystem.IsWindows() && !IsExecuting &&
        !IsReadingDeviceHealth && !IsRunningDeviceHealthBatch && DeviceHealthVeyonEligibleCount > 0 && IsCampusIdValid();
    public bool CanBatchRefreshLegacyEdgePolicy => OperatingSystem.IsWindows() && !IsExecuting &&
        !IsReadingDeviceHealth && !IsRunningDeviceHealthBatch && DeviceHealthEdgeEligibleCount > 0 && IsCampusIdValid();
    public bool CanReadDeviceHealth => OperatingSystem.IsWindows() && !IsExecuting &&
        !IsReadingDeviceHealth && GetSelectedWebsitePolicyTargets().Count > 0 && IsCampusIdValid();

    public async Task PollDeviceRuntimeSummariesAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !IsHealthCenterPage || IsExecuting ||
            IsReadingDeviceHealth || !IsCampusIdValid() ||
            Interlocked.CompareExchange(ref _deviceRuntimeSummaryPollInFlight, 1, 0) != 0) return;
        _isPollingDeviceRuntimeSummary = true;
        Changed(nameof(IsPollingDeviceRuntimeSummary));
        var pollTimer = Stopwatch.StartNew();
        try
        {
            var selectionRevision = Interlocked.Read(ref _deviceHealthSelectionRevision);
            var campus = CampusId.Trim();
            var selection = WebsitePolicyTargetChoices.Where(choice => choice.IsSelected)
                .Select(choice => choice.Target).ToArray();
            if (selection.Length == 0)
            {
                DeviceRuntimeSummaryStatus = "请选择地点中的学生电脑以查看实时状态。";
                return;
            }
            var targets = WebsitePolicyTransport.NormalizeTargets(selection);
            foreach (var target in targets)
            {
                var key = NormalizeTargetKey(target);
                if (!_deviceRuntimeSummaryPolling.ContainsKey(key))
                    _deviceRuntimeSummaryPolling[key] = new DeviceRuntimeSummaryPollingState();
            }
            var previousRowCount = _deviceHealthRows.Count;
            ReconcileDeviceHealthRows(campus, targets);
            if (_deviceHealthRows.Count > previousRowCount) SaveDeviceHealthCache(campus);

            var now = DateTimeOffset.UtcNow;
            foreach (var row in _deviceHealthRows) row.RefreshRuntimeFreshness(now);
            RefreshDeviceHealthView();
            RefreshRuntimeSummaryCounts();
            var due = targets.Where(target => _deviceRuntimeSummaryPolling[NormalizeTargetKey(target)].NextPollUtc <= now)
                .ToArray();
            if (due.Length == 0)
            {
                RefreshDeviceHealthView();
                ReconcileDeviceHealthAlerts();
                RefreshRuntimeSummaryCounts();
                return;
            }

            // Submit every due target now. The transport owns a shared 12-request semaphore,
            // so larger rooms drain in bounded waves instead of waiting an extra UI timer tick
            // for each group of 12 devices.
            var selectedBatch = due;
            var queuedTargets = Math.Max(0,
                selectedBatch.Length - StudentAgentRuntimeSummaryTransport.MaximumConcurrency);
            DeviceRuntimeSummaryStatus = $"读取 {selectedBatch.Length} 台 · 首波外排队目标 {queuedTargets} 台 · " +
                                         $"并发上限 {StudentAgentRuntimeSummaryTransport.MaximumConcurrency}";
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var requestTimer = Stopwatch.StartNew();
            var results = await StudentAgentRuntimeSummaryTransport.ReadAsync(selectedBatch, campus,
                signingKey.PrivateKey, cancellationToken);
            requestTimer.Stop();
            var selectedNow = GetSelectedWebsitePolicyTargets().Select(NormalizeTargetKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Interlocked.Read(ref _deviceHealthSelectionRevision) != selectionRevision ||
                !string.Equals(CampusId.Trim(), campus, StringComparison.Ordinal) ||
                !selectedNow.SetEquals(targets.Select(NormalizeTargetKey)))
            {
                DeviceRuntimeSummaryStatus = "校区或设备选择已变化；旧轻量状态未显示。";
                return;
            }

            var successful = 0;
            foreach (var result in results)
            {
                var key = NormalizeTargetKey(result.Target);
                var state = _deviceRuntimeSummaryPolling[key];
                if (result.Succeeded)
                {
                    state.ConsecutiveFailures = 0;
                    state.NextPollUtc = DateTimeOffset.UtcNow.AddSeconds(5);
                    successful++;
                }
                else
                {
                    state.ConsecutiveFailures++;
                    var retrySeconds = state.ConsecutiveFailures switch
                    {
                        1 => 5,
                        2 => 10,
                        3 => 20,
                        _ => 60
                    };
                    state.NextPollUtc = DateTimeOffset.UtcNow.AddSeconds(retrySeconds);
                }
                var row = _deviceHealthRows.FirstOrDefault(item => NormalizeTargetKey(item.Target) == key);
                row?.ApplyRuntimeSummary(result, state.ConsecutiveFailures);
            }

            var timedOut = results.Count(result => !result.Succeeded &&
                result.Detail.Contains("超时", StringComparison.Ordinal));
            var known = _deviceHealthRows.Count(row => row.RuntimeSummary is not null &&
                row.RuntimeFreshness == TeacherDeviceRuntimeFreshness.Current);
            var viewModelUpdateTimer = Stopwatch.StartNew();
            RefreshDeviceHealthView();
            ReconcileDeviceHealthAlerts();
            RefreshRuntimeSummaryCounts();
            NotifyDeviceHealthBatchProperties();
            SaveDeviceHealthCache(campus);
            viewModelUpdateTimer.Stop();
            pollTimer.Stop();
            DeviceRuntimeSummaryStatus = $"实时状态 · 本轮 {pollTimer.ElapsedMilliseconds} ms · 成功 {successful}/{selectedBatch.Length} 台 · 超时 {timedOut} · 首波外排队目标 {queuedTargets} 台 · 当前有效 {known}/{targets.Count} 台 · 通信 {requestTimer.ElapsedMilliseconds} ms · 视图模型更新 {viewModelUpdateTimer.ElapsedMilliseconds} ms · 并发上限 {StudentAgentRuntimeSummaryTransport.MaximumConcurrency} · {DateTimeOffset.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or InvalidOperationException or CryptographicException or
            PlatformNotSupportedException)
        {
            DeviceRuntimeSummaryStatus = "轻量状态读取失败：" + exception.Message;
        }
        finally
        {
            _isPollingDeviceRuntimeSummary = false;
            Changed(nameof(IsPollingDeviceRuntimeSummary));
            Interlocked.Exchange(ref _deviceRuntimeSummaryPollInFlight, 0);
        }
    }

    private int _deviceRuntimeSummaryPollInFlight;

    public async Task<IReadOnlyList<StudentAgentRuntimeSummaryDeliveryResult>> ReadMobileRuntimeSummariesAsync(
        IReadOnlyList<string> targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("学生电脑状态聚合只支持 Windows 教师端。");
        var campus = CampusId.Trim();
        WebsitePolicySigningKeyStore.ValidateCampusId(campus);
        using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
        return await StudentAgentRuntimeSummaryTransport.ReadAsync(targets, campus,
            signingKey.PrivateKey, cancellationToken);
    }

    private void RefreshRuntimeSummaryCounts()
    {
        Changed(nameof(DeviceHealthAllCount));
        Changed(nameof(DeviceHealthHealthyCount));
        Changed(nameof(DeviceHealthAttentionCount));
        Changed(nameof(DeviceHealthManualReviewCount));
        Changed(nameof(DeviceHealthNoResponseCount));
        Changed(nameof(DeviceHealthPendingCount));
        Changed(nameof(DeviceHealthListSummary));
    }

    private void UpdateRuntimeSummaryTargetSelection()
    {
        var selected = GetSelectedWebsitePolicyTargets().Select(NormalizeTargetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _deviceRuntimeSummaryPolling.Keys.Where(key => !selected.Contains(key)).ToArray())
            _deviceRuntimeSummaryPolling.Remove(key);
    }
    public bool IsRoomPage { get => _selectedPage == "rooms"; set { if (value) SelectPage("rooms"); } }
    public bool IsSetupPage { get => _selectedPage == "setup"; set { if (value) SelectPage("setup"); } }
    public int TeacherSetupWizardStep => _teacherSetupWizardStep;
    public bool IsTeacherSetupStep1 => _teacherSetupWizardStep == 1;
    public bool IsTeacherSetupStep2 => _teacherSetupWizardStep == 2;
    public bool IsTeacherSetupStep3 => _teacherSetupWizardStep == 3;
    public bool IsTeacherSetupPackageStepVisible => IsSetupPage && IsTeacherSetupStep3;
    public bool IsCheckingTeacherSetupVeyon => _isCheckingTeacherSetupVeyon;
    public string TeacherSetupVeyonStatus => _teacherSetupVeyonStatus;
    public bool HasTeacherSetupVeyonStatus => TeacherSetupVeyonStatus.Length > 0;
    public bool IsTeacherSetupVeyonReady => _teacherSetupVeyonReady;
    public bool CanRefreshTeacherSetupVeyon => !_isCheckingTeacherSetupVeyon;
    public bool IsTeacherSetupRoomComplete => HasSeatMap && SeatGridSeatCount > 0 && SeatGridNamedCount > 0;
    public bool IsTeacherSetupRoomSkippedForNow => _teacherSetupRoomSkippedForNow && !IsTeacherSetupRoomComplete;
    public bool CanOpenTeacherSetupStep2 => IsTeacherSetupStep2 || IsTeacherSetupStep3 || IsTeacherSetupVeyonReady;
    public bool CanOpenTeacherSetupStep3 => IsTeacherSetupStep3 || CanProceedTeacherSetupToPackage;
    public bool CanProceedTeacherSetupToRooms => IsTeacherSetupVeyonReady && !_isCheckingTeacherSetupVeyon;
    public bool CanProceedTeacherSetupToPackage => IsTeacherSetupVeyonReady &&
        (IsTeacherSetupRoomComplete || _teacherSetupRoomSkippedForNow) && !_isCheckingTeacherSetupVeyon;
    public bool CanSkipTeacherSetupRoom => IsTeacherSetupStep2 && IsTeacherSetupVeyonReady &&
        !IsTeacherSetupRoomComplete && !_teacherSetupRoomSkippedForNow;
    public string TeacherSetupStep1Label => IsTeacherSetupVeyonReady ? "1 · 教师端 ✓" : "1 · 教师端 · 待完成";
    public string TeacherSetupStep2Label => IsTeacherSetupRoomComplete
        ? "2 · 机房与座位 ✓"
        : IsTeacherSetupRoomSkippedForNow ? "2 · 机房与座位 · 稍后" : "2 · 机房与座位 · 待完成";
    public string TeacherSetupStep3Label => HasPackageOutput
        ? "3 · 学生配置包 ✓" : "3 · 学生配置包 · 待完成";
    public string TeacherSetupRoomSummary
    {
        get
        {
            if (!HasSeatMap)
                return "还没有保存机房座位图。可以先建立桌位并填写学生姓名；暂时没有机房时，也可以跳过此步骤。";

            var summary = $"已保存 {SeatGridSeatCount} 个桌位 · 已填写 {SeatGridNamedCount} 个姓名。";
            if (IsTeacherSetupRoomComplete)
                return summary + $"将为填写姓名的 {SeatGridNamedCount} 个桌位生成学生电脑；空姓名桌位只保留为桌子。";
            if (SeatGridSeatCount == 0)
                return summary + "请添加至少一个桌位，并至少填写一个学生姓名。";
            return summary + "至少填写一个学生姓名即可继续；未填写姓名的桌位不会生成学生电脑。";
        }
    }

    public void SetTeacherSetupWizardStep(int step)
    {
        if (step is < 1 or > 3 || step == _teacherSetupWizardStep) return;
        if (step > _teacherSetupWizardStep &&
            (step == 2 && !CanProceedTeacherSetupToRooms ||
             step == 3 && !CanProceedTeacherSetupToPackage)) return;
        _teacherSetupWizardStep = step;
        Changed(nameof(TeacherSetupWizardStep));
        Changed(nameof(IsTeacherSetupStep1));
        Changed(nameof(IsTeacherSetupStep2));
        Changed(nameof(IsTeacherSetupStep3));
        Changed(nameof(IsTeacherSetupPackageStepVisible));
        NotifyTeacherSetupNavigationStateChanged();
    }

    public void NextTeacherSetupWizardStep() => SetTeacherSetupWizardStep(Math.Min(3, _teacherSetupWizardStep + 1));
    public void PreviousTeacherSetupWizardStep() => SetTeacherSetupWizardStep(Math.Max(1, _teacherSetupWizardStep - 1));
    public void SkipTeacherSetupRoomForNow()
    {
        if (!CanSkipTeacherSetupRoom) return;
        _teacherSetupRoomSkippedForNow = true;
        NotifyTeacherSetupNavigationStateChanged();
        SetTeacherSetupWizardStep(3);
    }

    public async Task RefreshTeacherSetupVeyonStatusAsync()
    {
        if (_isCheckingTeacherSetupVeyon) return;
        if (!OperatingSystem.IsWindows())
        {
            _teacherSetupVeyonReady = false;
            _teacherSetupVeyonStatus = "教师端 Veyon 状态检查仅支持 Windows。";
            Changed(nameof(TeacherSetupVeyonStatus));
            NotifyTeacherSetupNavigationStateChanged();
            return;
        }

        _isCheckingTeacherSetupVeyon = true;
        Changed(nameof(IsCheckingTeacherSetupVeyon));
        Changed(nameof(CanRefreshTeacherSetupVeyon));
        NotifyTeacherSetupNavigationStateChanged();
        try
        {
            var facts = await Task.Run(VeyonFacts.Probe);
            _teacherSetupVeyonReady = facts.Status == "installed" &&
                VeyonFacts.IsSupportedVersionDetail(facts.VersionDetail);
            _teacherSetupVeyonStatus = _teacherSetupVeyonReady
                ? $"已检测到受支持的教师端 Veyon（{facts.VersionDetail}）。"
                : facts.Status == VeyonFacts.NotInstalled
                    ? "尚未检测到教师端 Veyon。请安装后重新检查。"
                    : facts.Status == "installed"
                        ? $"检测到 Veyon，但版本未能确认或不受当前控制台支持（要求 {VeyonInstallerTrust.Version}）。请先处理版本后重新检查。\n{facts.AsText()}"
                        : $"无法确认 Veyon 状态；请重新检查或查看安装问题。\n{facts.AsText()}";
            Changed(nameof(TeacherSetupVeyonStatus));
        }
        catch (Exception exception)
        {
            _teacherSetupVeyonReady = false;
            _teacherSetupVeyonStatus = "读取 Veyon 状态失败；没有更改系统。" + exception.Message;
            Changed(nameof(TeacherSetupVeyonStatus));
        }
        finally
        {
            _isCheckingTeacherSetupVeyon = false;
            Changed(nameof(IsCheckingTeacherSetupVeyon));
            Changed(nameof(CanRefreshTeacherSetupVeyon));
            NotifyTeacherSetupNavigationStateChanged();
        }
    }

    private void NotifyTeacherSetupNavigationStateChanged()
    {
        Changed(nameof(IsTeacherSetupVeyonReady));
        Changed(nameof(IsTeacherSetupRoomComplete));
        Changed(nameof(IsTeacherSetupRoomSkippedForNow));
        Changed(nameof(CanOpenTeacherSetupStep2));
        Changed(nameof(CanOpenTeacherSetupStep3));
        Changed(nameof(CanProceedTeacherSetupToRooms));
        Changed(nameof(CanProceedTeacherSetupToPackage));
        Changed(nameof(CanSkipTeacherSetupRoom));
        Changed(nameof(TeacherSetupStep1Label));
        Changed(nameof(TeacherSetupStep2Label));
        Changed(nameof(TeacherSetupStep3Label));
        Changed(nameof(TeacherSetupRoomSummary));
    }

    public void OpenRoomSetupFromTeacherWizard() => SelectPage("rooms");

    public async Task ToggleClassroomSessionAsync()
    {
        if (!CanToggleClassroomSession) return;
        _isClassroomTransitioning = true;
        var sessionUpdated = false;
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanCheckRoomConflicts));
        Changed(nameof(CanAddRoomToVeyon));
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
                try { _ = _classroomTaskProgressStore.Clear(active.SessionId); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    ClassroomTaskProgressStatus = "课堂任务进度无法清理；旧文件已保留。";
                }
                _activeClassroomCountdown = null;
                _activeClassroomTaskProgress = null;
                RefreshClassroomTaskProgressItems();
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
                    RefreshClassroomRestoreLedger("旧版课堂策略恢复账本无法更新；原文件已保留，请手动检查。");
                }
                RefreshClassroomSessionProperties();
                ClassroomDeliveryStatus = "学生互动已结束；正在通知学生电脑。";
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
                             ?? throw new InvalidDataException("请先选择一个校区模板。");
                var room = SelectedRoomProfile
                           ?? throw new InvalidDataException("请先选择一个地点模板。");
                var started = _classroomSessionStore.StartSession(campus, room.RoomId, now);
                sessionUpdated = true;
                _activeClassroomSession = started;
                LoadActiveClassroomCountdown();
                LoadActiveClassroomTaskProgress();
                try
                {
                    _classroomModeStateStore.Save(new ClassroomModeSessionState(
                        ClassroomModeStateStore.CurrentSchemaVersion, started.SessionId,
                        ClassroomMode.Normal, true, now, []));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  InvalidDataException)
                {
                    RefreshClassroomRestoreLedger("课堂仍可正常开始；旧版课堂策略恢复记录暂不可用。");
                }
                SaveClassroomSigningContext(started.SessionId, CampusId);
                RefreshClassroomSessionProperties();
                ClassroomDeliveryStatus = "学生互动已开始；正在通知学生电脑。";
                await PublishClassroomStatusAsync(started, started);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        {
            ClassroomDeliveryStatus = sessionUpdated
                ? "学生电脑范围已更新；设备状态尚未确认。"
                : "互动操作未完成；请重新检查学生电脑范围。";
        }
        finally
        {
            _isClassroomTransitioning = false;
            RefreshClassroomRestoreLedger();
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanCheckRoomConflicts));
            Changed(nameof(CanAddRoomToVeyon));
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
            try { await PublishClassroomStatusAsync(active, active, requireCurrentSession: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or
                                              CryptographicException or PlatformNotSupportedException)
            {
                ClassroomDeliveryStatus = "学生互动仍在进行；设备状态暂时无法更新。";
            }
        }
        finally { Volatile.Write(ref _classroomStatusRefreshInFlight, 0); }
    }

    private async Task PublishClassroomStatusAsync(ClassroomSession? activeSession, ClassroomSession targetSession,
        bool requireCurrentSession = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("请在 Windows 教师端管理学生互动。");
        await _classroomStatusPushGate.WaitAsync();
        try
        {
            if (requireCurrentSession && _activeClassroomSession?.SessionId != activeSession?.SessionId) return;
            var campusId = ResolveClassroomSigningCampus(targetSession);
            var targets = ResolveClassroomNetworkTargets(targetSession);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campusId);
            var command = ClassroomStatusCryptography.Create(campusId, activeSession, DateTimeOffset.UtcNow);
            var results = await ClassroomStatusTransport.SendAsync(targets, command, signingKey.PrivateKey);
            var confirmed = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => result.NeedsReview);
            var failed = results.Count - confirmed - needsReview;
            ClassroomDeliveryStatus = results.Count == 0
                ? "暂无学生电脑。"
                : confirmed == results.Count
                    ? $"已通知 {confirmed}/{results.Count} 台。"
                    : $"已通知 {confirmed}/{results.Count} · 核对 {needsReview} · 未响应 {failed}";
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
            throw new InvalidDataException("当前互动使用的校区设置不可用；互动记录已保留，学生状态同步已停止。");
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
            ? "请先生成学生校区配置包。"
            : "请选择与学生配置包相同的校区。");
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
            ClassroomDeliveryStatus = "无法确认当前互动校区；学生状态同步已暂停。";
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
                ClassroomDeliveryStatus = "已恢复学生互动；正在等待电脑状态确认。";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _activeClassroomSession = null;
            ClassroomDeliveryStatus = "本机学生互动记录无法读取；原文件已保留。";
        }
        LoadActiveClassroomCountdown();
        LoadActiveClassroomTaskProgress();
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

    private void LoadActiveClassroomTaskProgress()
    {
        _activeClassroomTaskProgress = null;
        if (_activeClassroomSession is not { } active)
        {
            ClassroomTaskProgressStatus = "";
        }
        else
        {
            try
            {
                _activeClassroomTaskProgress = _classroomTaskProgressStore.Read(active.SessionId);
                ClassroomTaskProgressStatus = "";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                ClassroomTaskProgressStatus = "课堂任务进度无法读取；原文件已保留。";
            }
        }
        RefreshClassroomTaskProgressItems();
    }

    private void RefreshClassroomTaskProgressItems()
    {
        ClassroomTaskProgressItems.Clear();
        if (_activeClassroomTaskProgress is { } progress)
            foreach (var task in progress.Tasks)
                ClassroomTaskProgressItems.Add(new TeacherClassroomTaskItem(task.TaskId, task.Title,
                    task.IsCompleted));
        Changed(nameof(ClassroomTaskProgressCompletedCount));
        Changed(nameof(ClassroomTaskProgressTotalCount));
        Changed(nameof(HasClassroomTaskProgress));
        Changed(nameof(ClassroomTaskProgressSummary));
        Changed(nameof(CanAddClassroomTask));
    }

    private void RefreshClassroomSessionProperties()
    {
        if (!HasActiveClassroomSession)
        {
            if (_classroomNoticeDraft.Length > 0) ClassroomNoticeDraft = "";
            if (_classroomTaskProgressDraft.Length > 0) ClassroomTaskProgressDraft = "";
        }
        Changed(nameof(HasActiveClassroomSession));
        Changed(nameof(HasNoActiveClassroomSession));
        Changed(nameof(ClassroomOverviewSessionValue));
        Changed(nameof(ClassroomOverviewDeviceCount));
        Changed(nameof(ClassroomRoomContextSummary));
        Changed(nameof(CanStartClassroomQuickControl));
        Changed(nameof(CanReleaseClassroomQuickControl));
        Changed(nameof(HasActiveClassroomEventStatus));
        Changed(nameof(ClassroomSessionActionText));
        Changed(nameof(ClassroomSessionSummary));
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanDeleteRoomProfile));
        Changed(nameof(CanCheckRoomConflicts));
        Changed(nameof(CanAddRoomToVeyon));
        Changed(nameof(CanSendClassroomNotice));
        Changed(nameof(CanRetryClassroomRestores));
        Changed(nameof(HasClassroomCountdown));
        Changed(nameof(ClassroomCountdownText));
        Changed(nameof(IsClassroomCountdownRunning));
        Changed(nameof(CanStartClassroomCountdown));
        Changed(nameof(CanEndClassroomCountdown));
        Changed(nameof(ClassroomTaskProgressCompletedCount));
        Changed(nameof(ClassroomTaskProgressTotalCount));
        Changed(nameof(HasClassroomTaskProgress));
        Changed(nameof(ClassroomTaskProgressSummary));
        Changed(nameof(CanAddClassroomTask));
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
    public string AppVersion => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "版本未知";
    public string TeacherUpdateVersionSummary => $"当前版本：{AppVersion} · 最新版本：{_teacherUpdateLatestVersion}";
    public bool IsInstallingTeacherVeyon
    {
        get => _isInstallingTeacherVeyon;
        private set { if (_isInstallingTeacherVeyon == value) return; _isInstallingTeacherVeyon = value; Changed(); }
    }
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
        GetSelectedWebsitePolicyTargets().Count > 0 && IsCampusIdValid();
    public async Task ReadStudentDeviceHealthAsync()
    {
        if (!OperatingSystem.IsWindows() || !CanReadDeviceHealth || !TryBeginExclusiveTask()) return;
        IsReadingDeviceHealth = true;
        DeviceHealthStatus = "正在读取所选电脑的 Agent、Veyon 服务、策略和更新状态……";
        try
        {
            var selectionRevision = Interlocked.Read(ref _deviceHealthSelectionRevision);
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var results = await WebsitePolicyStatusTransport.ReadAsync(targets, campus,
                signingKey.PrivateKey, includeHealthSnapshot: true);
            var displayNames = await ResolveTargetDisplayNamesAsync(targets);
            var selectedNow = GetSelectedWebsitePolicyTargets().Select(NormalizeTargetKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Interlocked.Read(ref _deviceHealthSelectionRevision) != selectionRevision ||
                !string.Equals(CampusId.Trim(), campus, StringComparison.Ordinal) ||
                !selectedNow.SetEquals(targets.Select(NormalizeTargetKey)))
            {
                InvalidateDeviceHealthSnapshot("读取过程中校区或设备选择发生变化");
                DeviceHealthStatus = "校区或设备选择已变化；旧结果未显示，请重新读取。";
                return;
            }

            var previousRows = _deviceHealthRows.ToDictionary(row => NormalizeTargetKey(row.Target),
                StringComparer.OrdinalIgnoreCase);
            _deviceHealthRows.Clear();
            foreach (var result in results)
            {
                var key = NormalizeTargetKey(result.Target);
                var resolvedName = GetWebsitePolicyStudentName(result.Target);
                if (string.IsNullOrWhiteSpace(resolvedName) || resolvedName == "未填写姓名")
                    resolvedName = displayNames.GetValueOrDefault(key, "未填写姓名");
                var row = previousRows.GetValueOrDefault(key) ?? new TeacherDeviceHealthRow(result.Target,
                    resolvedName, "");
                row.SetStudentName(resolvedName);
                row.Apply(result);
                _deviceHealthRows.Add(row);
            }

            var healthy = _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Healthy);
            var attention = _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Attention);
            var manual = _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NeedsManualReview);
            var noResponse = _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NoResponse);
            UpdateDeviceHealthSummary(healthy, attention, manual, noResponse);
            RefreshDeviceHealthView();
            ReconcileDeviceHealthAlerts();
            NotifyDeviceHealthBatchProperties();
            ScheduleNextDeviceHealthFreshnessUpdate();
            SaveDeviceHealthCache(campus);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            DeviceHealthStatus = "健康状态读取失败：" + exception.Message;
            NotifyDeviceHealthBatchProperties();
        }
        finally
        {
            IsReadingDeviceHealth = false;
            EndExclusiveTask();
        }
    }
    public Task EnsureVeyonServiceRunningAsync(TeacherDeviceHealthRow row) =>
        RunStudentHealthRepairAsync(row, StudentAgentHealthRepairAction.EnsureVeyonServiceRunning);

    public Task RefreshLegacyEdgePolicyAsync(TeacherDeviceHealthRow row) =>
        RunStudentHealthRepairAsync(row, StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy);

    private async Task RunStudentHealthRepairAsync(TeacherDeviceHealthRow row,
        StudentAgentHealthRepairAction action)
    {
        ArgumentNullException.ThrowIfNull(row);
        var allowed = action switch
        {
            StudentAgentHealthRepairAction.EnsureVeyonServiceRunning => row.CanEnsureVeyonServiceRunning,
            StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy => row.CanRefreshLegacyEdgePolicy,
            _ => false
        };
        if (!OperatingSystem.IsWindows() || !allowed || !TryBeginExclusiveTask()) return;

        row.IsRepairing = true;
        row.RepairStatus = "正在发送经过签名的固定修复请求……";
        try
        {
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var expectedRevision = action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy
                ? row.WebsiteRevision
                : null;
            var results = await StudentAgentHealthRepairTransport.ExecuteAsync([row.Target], campus,
                signingKey.PrivateKey, action, expectedRevision);
            var result = results.Single();
            row.SetRepairResult(result);

            var refreshed = await WebsitePolicyStatusTransport.ReadAsync([row.Target], campus,
                signingKey.PrivateKey, includeHealthSnapshot: true);
            if (refreshed.Count == 1) row.Apply(refreshed[0]);
            ReconcileDeviceHealthAlerts();
            NotifyDeviceHealthBatchProperties();
            ScheduleNextDeviceHealthFreshnessUpdate();
            UpdateDeviceHealthSummary(
                _deviceHealthRows.Count(item => item.Category == TeacherDeviceHealthCategory.Healthy),
                _deviceHealthRows.Count(item => item.Category == TeacherDeviceHealthCategory.Attention),
                _deviceHealthRows.Count(item => item.Category == TeacherDeviceHealthCategory.NeedsManualReview),
                _deviceHealthRows.Count(item => item.Category == TeacherDeviceHealthCategory.NoResponse));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            row.RepairStatus = "修复状态无法确认：" + exception.Message;
        }
        finally
        {
            row.IsRepairing = false;
            EndExclusiveTask();
        }
    }

    public Task EnsureVeyonServiceRunningForSelectedAsync() =>
        RunStudentHealthRepairBatchAsync(StudentAgentHealthRepairAction.EnsureVeyonServiceRunning);

    public Task RefreshLegacyEdgePolicyForSelectedAsync() =>
        RunStudentHealthRepairBatchAsync(StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy);

    public void AcknowledgeDeviceHealthAlert(TeacherDeviceHealthAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        alert.Acknowledge();
        if (alert.IssueCode == "boot-changed")
        {
            foreach (var row in alert.AffectedRows) row.AcknowledgeBootChange();
            ReconcileDeviceHealthAlerts();
            RefreshDeviceHealthView();
        }
    }

    public Task RecheckDeviceHealthAlertAsync(TeacherDeviceHealthAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return ReadStudentDeviceHealthAsync();
    }

    public Task RepairDeviceHealthAlertAsync(TeacherDeviceHealthAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (alert.RecommendedAction is not { } action) return Task.CompletedTask;
        return RunStudentHealthRepairBatchAsync(action, alert.AffectedRows);
    }

    private async Task RunStudentHealthRepairBatchAsync(StudentAgentHealthRepairAction action,
        IReadOnlyList<TeacherDeviceHealthRow>? requestedRows = null)
    {
        var selectedTargets = GetSelectedWebsitePolicyTargets();
        var selectedKeys = selectedTargets.Select(NormalizeTargetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedKeys = requestedRows is null
            ? selectedKeys
            : requestedRows.Select(row => NormalizeTargetKey(row.Target))
                .Where(selectedKeys.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopeTargets = selectedTargets.Where(target => requestedKeys.Contains(NormalizeTargetKey(target))).ToArray();
        var currentRows = _deviceHealthRows.ToDictionary(row => NormalizeTargetKey(row.Target),
            StringComparer.OrdinalIgnoreCase);
        var rows = scopeTargets.Select(target => currentRows.GetValueOrDefault(NormalizeTargetKey(target)))
            .Where(row => row is not null).Cast<TeacherDeviceHealthRow>().ToArray();
        var eligibleRows = rows.Where(row => row.IsRecentlyReachable && IsHealthRepairAllowed(row, action))
            .ToArray();
        if (!OperatingSystem.IsWindows() || eligibleRows.Length == 0 || !TryBeginExclusiveTask()) return;

        IsRunningDeviceHealthBatch = true;
        var campus = CampusId.Trim();
        var batchId = Guid.NewGuid();
        _activeDeviceHealthBatchId = batchId;
        _activeDeviceHealthBatchCampus = campus;
        _activeDeviceHealthBatchSelection = new HashSet<string>(selectedKeys, StringComparer.OrdinalIgnoreCase);
        var rowByTarget = rows.ToDictionary(row => NormalizeTargetKey(row.Target),
            StringComparer.OrdinalIgnoreCase);
        var reachable = rows.Count(row => row.IsRecentlyReachable);
        var skipped = Math.Max(0, scopeTargets.Length - eligibleRows.Length);
        foreach (var row in rows)
        {
            var allowed = row.IsRecentlyReachable && IsHealthRepairAllowed(row, action);
            var detail = allowed ? "等待发送；此状态仅保存在教师端内存，不会离线排队。"
                : GetHealthRepairSkipReason(row, action);
            row.BeginBatchOperation(batchId, action,
                allowed ? TeacherDeviceHealthBatchState.Queued : TeacherDeviceHealthBatchState.Skipped,
                detail);
        }
        DeviceHealthBatchStatus = $"批次 {batchId.ToString("N")[..8]} · 目标 {scopeTargets.Length} · 最近有签名回执 {reachable} · " +
                                  $"计划 {eligibleRows.Length} · 跳过 {skipped}。";
        NotifyDeviceHealthBatchProperties();

        try
        {
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var progress = new Progress<StudentAgentHealthRepairProgressUpdate>(update =>
            {
                if (update.BatchId != batchId || _activeDeviceHealthBatchId != batchId ||
                    !rowByTarget.TryGetValue(NormalizeTargetKey(update.Target), out var row)) return;
                row.SetBatchOperation(batchId, update.CommandId, action,
                    ToTeacherDeviceHealthBatchState(update.State), update.Detail);
                UpdateDeviceHealthBatchProgressSummary(batchId, scopeTargets.Length, reachable,
                    eligibleRows.Length, skipped, rows);
            });
            IReadOnlyDictionary<string, long?>? websiteRevisions = null;
            if (action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy)
                websiteRevisions = eligibleRows.ToDictionary(row => row.Target, row => row.WebsiteRevision,
                    StringComparer.OrdinalIgnoreCase);
            var results = await StudentAgentHealthRepairTransport.ExecuteAsync(eligibleRows.Select(row => row.Target),
                campus, signingKey.PrivateKey, action, cancellationToken: default, progress: progress,
                expectedWebsiteRevisions: websiteRevisions, batchIdOverride: batchId);

            foreach (var result in results)
            {
                if (!rowByTarget.TryGetValue(NormalizeTargetKey(result.Target), out var row)) continue;
                row.SetBatchOperation(batchId, result.CommandId, action,
                    ToTeacherDeviceHealthBatchState(result.State), result.Detail);
            }
            UpdateDeviceHealthBatchProgressSummary(batchId, scopeTargets.Length, reachable,
                eligibleRows.Length, skipped, rows);

            if (selectedTargets.Count > 0 && string.Equals(CampusId.Trim(), campus, StringComparison.Ordinal))
            {
                var currentSelection = GetSelectedWebsitePolicyTargets().Select(NormalizeTargetKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (currentSelection.SetEquals(selectedKeys))
                {
                    var refreshed = await WebsitePolicyStatusTransport.ReadAsync(selectedTargets, campus,
                        signingKey.PrivateKey, includeHealthSnapshot: true);
                    foreach (var result in refreshed)
                    {
                        var currentRow = _deviceHealthRows.FirstOrDefault(row =>
                            NormalizeTargetKey(row.Target) == NormalizeTargetKey(result.Target));
                        currentRow?.Apply(result);
                    }
                    ReconcileDeviceHealthAlerts();
                    UpdateDeviceHealthSummary(
                        _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Healthy),
                        _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Attention),
                        _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NeedsManualReview),
                        _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NoResponse));
                    RefreshDeviceHealthView();
                    ScheduleNextDeviceHealthFreshnessUpdate();
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        {
            DeviceHealthBatchStatus = $"批次 {batchId.ToString("N")[..8]} 未能完成：{exception.Message}";
            foreach (var row in eligibleRows.Where(row => row.BatchOperationState is
                         TeacherDeviceHealthBatchState.Queued or TeacherDeviceHealthBatchState.Delivered or
                         TeacherDeviceHealthBatchState.Executing))
                row.SetBatchOperation(batchId, row.BatchOperationCommandId, action,
                    TeacherDeviceHealthBatchState.Failed, "教师端未能确认操作结果；请重新读取设备状态。不会自动重试。");
        }
        finally
        {
            var contextChanged = !string.Equals(CampusId.Trim(), _activeDeviceHealthBatchCampus,
                                    StringComparison.Ordinal) ||
                                 !GetSelectedWebsitePolicyTargets().Select(NormalizeTargetKey)
                                     .ToHashSet(StringComparer.OrdinalIgnoreCase)
                                     .SetEquals(_activeDeviceHealthBatchSelection);
            if (contextChanged)
                DeviceHealthBatchStatus += " · 此批次仍对应开始时的校区和目标。";
            _activeDeviceHealthBatchId = null;
            _activeDeviceHealthBatchCampus = "";
            _activeDeviceHealthBatchSelection.Clear();
            IsRunningDeviceHealthBatch = false;
            EndExclusiveTask();
        }
    }

    private static bool IsHealthRepairAllowed(TeacherDeviceHealthRow row,
        StudentAgentHealthRepairAction action) => action switch
    {
        StudentAgentHealthRepairAction.EnsureVeyonServiceRunning => row.CanEnsureVeyonServiceRunning,
        StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy => row.CanRefreshLegacyEdgePolicy,
        _ => false
    };

    private static string GetHealthRepairSkipReason(TeacherDeviceHealthRow row,
        StudentAgentHealthRepairAction action)
    {
        if (!row.IsRecentlyReachable) return "已跳过：最近两分钟内没有有效的签名 Agent 回执。";
        if (row.Category == TeacherDeviceHealthCategory.NeedsManualReview)
            return "已跳过：设备状态需要人工检查。";
        if (row.UpdateTransactionState is "inProgress" or "needsReview")
            return "已跳过：学生端更新事务正在进行或需要恢复。";
        if (action == StudentAgentHealthRepairAction.EnsureVeyonServiceRunning &&
            row.ServiceState == StudentVeyonServiceState.Running)
            return "已跳过：VeyonService 已处于运行状态。";
        if (action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy &&
            (row.WebsiteRevision is null or <= 0))
            return "已跳过：没有可刷新的活动网站策略版本。";
        return "已跳过：当前状态不满足此固定修复动作的前置条件。";
    }

    private void UpdateDeviceHealthBatchProgressSummary(Guid batchId, int targetCount, int reachable,
        int sent, int skipped, IReadOnlyList<TeacherDeviceHealthRow> rows)
    {
        if (_activeDeviceHealthBatchId != batchId) return;
        var batchRows = rows.Where(row => row.BatchOperationId == batchId).ToArray();
        var succeeded = batchRows.Count(row => row.BatchOperationState == TeacherDeviceHealthBatchState.Succeeded);
        var failed = batchRows.Count(row => row.BatchOperationState == TeacherDeviceHealthBatchState.Failed);
        var timedOut = batchRows.Count(row => row.BatchOperationState == TeacherDeviceHealthBatchState.TimedOut);
        var contextChanged = !string.Equals(CampusId.Trim(), _activeDeviceHealthBatchCampus,
                                StringComparison.Ordinal) ||
                             !GetSelectedWebsitePolicyTargets().Select(NormalizeTargetKey)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase)
                                 .SetEquals(_activeDeviceHealthBatchSelection);
        DeviceHealthBatchStatus = $"批次 {batchId.ToString("N")[..8]} · 目标 {targetCount} · 最近有回执 {reachable} · " +
                                  $"计划 {sent} · 成功 {succeeded} · 失败 {failed} · 超时 {timedOut} · 跳过 {skipped}" +
                                  (contextChanged ? " · 此批次仍对应开始时的校区和目标" : "");
    }

    private void ReconcileDeviceHealthAlerts()
    {
        var hadAlerts = _deviceHealthAlerts.Count > 0;
        var groups = _deviceHealthRows.SelectMany(row => row.Issues.Select(issue => (Row: row, Issue: issue)))
            .GroupBy(pair => pair.Issue.Code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var existing = _deviceHealthAlerts.ToDictionary(alert => alert.IssueCode, StringComparer.Ordinal);
        foreach (var stale in _deviceHealthAlerts.Where(alert => !groups.ContainsKey(alert.IssueCode)).ToArray())
            _deviceHealthAlerts.Remove(stale);

        foreach (var (issueCode, items) in groups)
        {
            var rows = items.Select(item => item.Row).Distinct().ToArray();
            var severity = items.Any(item => item.Issue.Severity == TeacherDeviceHealthAlertSeverity.Critical)
                ? TeacherDeviceHealthAlertSeverity.Critical
                : items.Any(item => item.Issue.Severity == TeacherDeviceHealthAlertSeverity.Warning)
                    ? TeacherDeviceHealthAlertSeverity.Warning
                    : rows.All(row => row.Category == TeacherDeviceHealthCategory.NoResponse)
                    ? TeacherDeviceHealthAlertSeverity.Informational
                    : items.Min(item => item.Issue.Severity);
            if (!existing.TryGetValue(issueCode, out var alert))
            {
                alert = new TeacherDeviceHealthAlert(issueCode, items[0].Issue.Title, severity);
                _deviceHealthAlerts.Add(alert);
            }
            var isNewAlert = !existing.ContainsKey(issueCode);
            alert.Update(rows, isNewAlert, severity);
            alert.SetBusy(IsExecuting);
        }

        var ordered = _deviceHealthAlerts.OrderBy(alert => alert.Severity)
            .ThenByDescending(alert => alert.LastSeenUtc).ThenBy(alert => alert.Title, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var currentIndex = _deviceHealthAlerts.IndexOf(ordered[index]);
            if (currentIndex >= 0 && currentIndex != index)
                _deviceHealthAlerts.Move(currentIndex, index);
        }
        if (hadAlerts != (_deviceHealthAlerts.Count > 0)) Changed(nameof(HasDeviceHealthAlerts));
    }

    private static TeacherDeviceHealthBatchState ToTeacherDeviceHealthBatchState(
        StudentAgentHealthRepairProgressState state) => state switch
    {
        StudentAgentHealthRepairProgressState.Queued => TeacherDeviceHealthBatchState.Queued,
        StudentAgentHealthRepairProgressState.Delivered => TeacherDeviceHealthBatchState.Delivered,
        StudentAgentHealthRepairProgressState.Executing => TeacherDeviceHealthBatchState.Executing,
        StudentAgentHealthRepairProgressState.Succeeded => TeacherDeviceHealthBatchState.Succeeded,
        StudentAgentHealthRepairProgressState.TimedOut => TeacherDeviceHealthBatchState.TimedOut,
        _ => TeacherDeviceHealthBatchState.Failed
    };

    private void UpdateDeviceHealthSummary(int healthy, int attention, int manual, int noResponse) =>
        DeviceHealthStatus = $"已读取 {_deviceHealthRows.Count} 台：正常 {healthy} · 需关注 {attention} · 需人工处理 {manual} · 未收到回执 {noResponse}";

    public void PreviousDeviceHealthPage()
    {
        if (!CanPreviousDeviceHealthPage) return;
        _deviceHealthPageIndex--;
        RefreshDeviceHealthView();
    }

    public void NextDeviceHealthPage()
    {
        if (!CanNextDeviceHealthPage) return;
        _deviceHealthPageIndex++;
        RefreshDeviceHealthView();
    }

    public TeacherDeviceHealthRow? FocusDeviceHealthAlert(TeacherDeviceHealthAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var row = alert.AffectedRows.FirstOrDefault();
        if (row is null) return null;
        DeviceHealthCategoryFilterIndex = 0;
        DeviceHealthSearchText = row.StudentName;
        RefreshDeviceHealthView();
        return _deviceHealthPageRows.FirstOrDefault(item =>
            string.Equals(NormalizeTargetKey(item.Target), NormalizeTargetKey(row.Target),
                StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshDeviceHealthView()
    {
        var search = DeviceHealthSearchText.Trim();
        var filterIndex = DeviceHealthCategoryFilterIndex;
        var filtered = _deviceHealthRows
            .Where(row => filterIndex switch
            {
                1 => row.Category == TeacherDeviceHealthCategory.Healthy,
                2 => row.Category == TeacherDeviceHealthCategory.Attention,
                3 => row.Category == TeacherDeviceHealthCategory.NeedsManualReview,
                4 => row.Category == TeacherDeviceHealthCategory.NoResponse,
                5 => row.Category == TeacherDeviceHealthCategory.Pending,
                _ => true
            })
            .Where(row => search.Length == 0 ||
                ContainsHealthSearch(row.StudentName, search) ||
                ContainsHealthSearch(row.CategoryLabel, search) ||
                ContainsHealthSearch(row.AgentSummary, search) ||
                ContainsHealthSearch(row.ServiceSummary, search) ||
                ContainsHealthSearch(row.PolicySummary, search) ||
                ContainsHealthSearch(row.UpdateSummary, search) ||
                ContainsHealthSearch(row.Detail, search))
            .OrderBy(row => row.Category switch
            {
                TeacherDeviceHealthCategory.NeedsManualReview => 0,
                TeacherDeviceHealthCategory.Attention => 1,
                TeacherDeviceHealthCategory.NoResponse => 2,
                TeacherDeviceHealthCategory.Pending => 3,
                _ => 4
            })
            .ThenBy(row => row.StudentName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _deviceHealthFilteredCount = filtered.Length;
        var pageCount = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)DeviceHealthPageSize));
        _deviceHealthPageIndex = Math.Clamp(_deviceHealthPageIndex, 0, pageCount - 1);
        var page = filtered.Skip(_deviceHealthPageIndex * DeviceHealthPageSize)
            .Take(DeviceHealthPageSize).ToArray();
        if (_deviceHealthPageRows.Count != page.Length || !_deviceHealthPageRows.SequenceEqual(page))
        {
            _deviceHealthPageRows = Array.AsReadOnly(page);
            Changed(nameof(DeviceHealthPageRows));
        }
        Changed(nameof(HasDeviceHealthRows));
        Changed(nameof(HasDeviceHealthFilteredRows));
        Changed(nameof(HasDeviceHealthFilterEmpty));
        Changed(nameof(DeviceHealthAllCount));
        Changed(nameof(DeviceHealthListSummary));
        Changed(nameof(DeviceHealthHealthyCount));
        Changed(nameof(DeviceHealthAttentionCount));
        Changed(nameof(DeviceHealthManualReviewCount));
        Changed(nameof(DeviceHealthNoResponseCount));
        Changed(nameof(DeviceHealthPendingCount));
        Changed(nameof(DeviceHealthPageCount));
        Changed(nameof(DeviceHealthPageLabel));
        Changed(nameof(CanPreviousDeviceHealthPage));
        Changed(nameof(CanNextDeviceHealthPage));
    }

    private static bool ContainsHealthSearch(string value, string search) =>
        value.Contains(search, StringComparison.CurrentCultureIgnoreCase);

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
    public bool IsTeacherUpdateProgressVisible => _isTeacherUpdateProgressVisible;
    public double TeacherUpdateProgressPercent => _teacherUpdateProgressPercent;
    public bool TeacherUpdateProgressIsIndeterminate => _teacherUpdateProgressIsIndeterminate;
    public string TeacherUpdateProgressText => _teacherUpdateProgressText;
    public string TeacherUpdateLatestVersion
    {
        get => _teacherUpdateLatestVersion;
        private set
        {
            if (_teacherUpdateLatestVersion == value) return;
            _teacherUpdateLatestVersion = value;
            Changed();
            Changed(nameof(TeacherUpdateVersionSummary));
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
    public bool HasStudentUpdatePerformanceReport => _lastStudentUpdatePerformanceReport is not null;
    public string StudentUpdatePerformanceReportExportStatus { get; private set; } = "";
    public bool IsStudentUpdateProgressVisible => _isStudentUpdateProgressVisible;
    public double StudentUpdateProgressPercent => _studentUpdateProgressPercent;
    public bool StudentUpdateProgressIsIndeterminate => _studentUpdateProgressIsIndeterminate;
    public string StudentUpdateProgressText => _studentUpdateProgressText;
    public ObservableCollection<StudentApplicationUpdateTargetProgressItem> StudentUpdateTargetProgressItems { get; } = [];
    public bool HasStudentUpdateTargetProgressItems => StudentUpdateTargetProgressItems.Count > 0;
    public string StudentUpdateSucceededDetails
    {
        get => _studentUpdateSucceededDetails;
        private set
        {
            if (_studentUpdateSucceededDetails == value) return;
            _studentUpdateSucceededDetails = value;
            Changed();
            Changed(nameof(HasStudentUpdateSucceededDetails));
            Changed(nameof(HasStudentUpdateResults));
        }
    }
    public bool HasStudentUpdateSucceededDetails => StudentUpdateSucceededDetails.Length > 0;
    public string StudentUpdateNeedsReviewDetails
    {
        get => _studentUpdateNeedsReviewDetails;
        private set
        {
            if (_studentUpdateNeedsReviewDetails == value) return;
            _studentUpdateNeedsReviewDetails = value;
            Changed();
            Changed(nameof(HasStudentUpdateNeedsReviewDetails));
            Changed(nameof(HasStudentUpdateResults));
        }
    }
    public bool HasStudentUpdateNeedsReviewDetails => StudentUpdateNeedsReviewDetails.Length > 0;
    public string StudentUpdateFailedDetails
    {
        get => _studentUpdateFailedDetails;
        private set
        {
            if (_studentUpdateFailedDetails == value) return;
            _studentUpdateFailedDetails = value;
            Changed();
            Changed(nameof(HasStudentUpdateFailedDetails));
            Changed(nameof(HasStudentUpdateResults));
        }
    }
    public bool HasStudentUpdateFailedDetails => StudentUpdateFailedDetails.Length > 0;
    public bool HasStudentUpdateResults => HasStudentUpdateSucceededDetails ||
        HasStudentUpdateNeedsReviewDetails || HasStudentUpdateFailedDetails;
    public string UpdateDiagnosticsStatus
    {
        get => _updateDiagnosticsStatus;
        private set
        {
            if (_updateDiagnosticsStatus == value) return;
            _updateDiagnosticsStatus = value;
            Changed();
            Changed(nameof(HasUpdateDiagnosticsStatus));
        }
    }
    public bool HasUpdateDiagnosticsStatus => UpdateDiagnosticsStatus.Length > 0;
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
        !_websiteLocationSelectionPending && GetSelectedWebsitePolicyTargets().Count > 0 && IsCampusIdValid();
    public bool CanReadWebsitePolicyStatus => OperatingSystem.IsWindows() && !IsExecuting &&
        GetSelectedWebsitePolicyTargets().Count > 0 && IsCampusIdValid();
    public bool CanMigrateEdgeRealtime => OperatingSystem.IsWindows() && !IsExecuting &&
        ConfiguredEdgeExtensionId() is not null && GetSelectedWebsitePolicyTargets().Count > 0 && IsCampusIdValid();
    public bool CanRestoreEdgeLegacy => CanMigrateEdgeRealtime;
    public string EdgeRealtimeMigrationButtonLabel
    {
        get
        {
#if VEYONCAMPUS_EDGE_REALTIME_PILOT
            return "启用 Edge 实时同步（试用）";
#else
            return "启用 Edge 实时同步";
#endif
        }
    }
    public bool IsEdgeRealtimePilotBuild
    {
        get
        {
#if VEYONCAMPUS_EDGE_REALTIME_PILOT
            return true;
#else
            return false;
#endif
        }
    }
    public bool CanFillFailedWebsiteTargets => !IsExecuting && _lastFailedWebsiteTargets.Count > 0;
    public bool CanPushApplicationPolicy => OperatingSystem.IsWindows() && !IsExecuting &&
        IsApplicationPolicyInputValid() && (ApplicationPolicyModeIndex == 0 ||
            (ApplicationEnforcementReviewed && HasMatchingApplicationAudit));
    public bool CanDisableApplicationPolicy => OperatingSystem.IsWindows() && !IsExecuting && AreWebsitePolicyTargetsValid();
    public bool CanPushStudentSystemPolicy => OperatingSystem.IsWindows() && !IsExecuting &&
        IsStudentSystemPolicyInputValid() &&
        (!_studentSystemPolicyProhibitSoftwareInstallation || _studentSystemPolicySoftwareInstallReviewed) &&
        _studentSystemPolicyImpactReviewed;
    public string StudentSystemPolicyPushGuidance => GetStudentSystemPolicyPushGuidance();
    public bool CanDisableStudentSystemPolicy => OperatingSystem.IsWindows() && !IsExecuting && AreWebsitePolicyTargetsValid();
    public bool HasSelectedStudentSystemPolicy => !CurrentStudentSystemPolicySettings().IsEmpty;
    public bool CanReadApplicationPolicyAudit => OperatingSystem.IsWindows() && !IsExecuting && AreWebsitePolicyTargetsValid();
    public bool CanReadApplicationInventory => OperatingSystem.IsWindows() && !IsExecuting && AreWebsitePolicyTargetsValid();
    public bool CanGenerateApplicationCandidates => !IsExecuting && ApplicationCatalogAllTargetsRead &&
        _allApplicationCatalogChoices.Any(choice => choice.IsSelected) &&
        SelectedApplicationCatalogCandidatesByTarget().All(pair => pair.Value.Count <= ApplicationCatalogCryptography.MaximumSelectedItems);
    public bool CanAddSelectedApplicationRules => !IsExecuting && _allApplicationInventoryChoices.Any(item => item.IsSelected && item.CanSelect);
    public bool CanCheckRoomConflicts => OperatingSystem.IsWindows() && !IsExecuting && !IsCheckingRoomConflicts &&
        !_isClassroomTransitioning && !HasActiveClassroomSession;
    public bool CanEditRoomPlan => !IsExecuting;
    public bool CanAddRoomToVeyon => CanStartRoomCreation && HasRoomPreview && RoomError.Length == 0 &&
        SeatGridNamedCount > 0 && !_isClassroomTransitioning && !HasActiveClassroomSession &&
        _roomConflictCheckCompleted && _roomConflictCheckRevision == _roomPlanRevision &&
        _roomConflictResults.Length == 0;
    public bool CanReadWebsiteLocations => OperatingSystem.IsWindows() && !IsExecuting && !IsReadingWebsiteLocations;
    public bool CanUseSelectedClassroomLocation => !IsExecuting && !IsReadingWebsiteLocations &&
        ClassroomLocationIndex >= 0 && ClassroomLocationIndex < WebsiteLocations.Count &&
        (_classroomDirectorySelectionPending || !string.Equals(
            WebsiteLocations[ClassroomLocationIndex].Name, SelectedRoomProfile?.DisplayName,
            StringComparison.OrdinalIgnoreCase));
    public bool CanSyncSeatGridWithVeyon => OperatingSystem.IsWindows() && !IsExecuting &&
        !IsReadingWebsiteLocations && !_isReadingSeatVeyonTargets && !IsPreparingSeatSwapPlan &&
        (!_classroomDirectorySelectionPending || CanUseSelectedClassroomLocation);
    public string SeatGridVeyonSyncButtonLabel => _classroomDirectorySelectionPending
        ? "使用所选地点并读取电脑" : "同步 Veyon 地点和电脑";
    public bool CanApplyWebsiteLocation => !IsExecuting && !IsReadingWebsiteLocations &&
        _websiteLocationIndex >= 0 && _websiteLocationIndex < WebsiteLocations.Count;
    public string TeacherInstallPlanText =>
        "无需联网；已安装时不会重复安装。";
    public string TeacherInstallSafetyText =>
        "安装后可能需要重启。";

    public async Task CheckTeacherUpdateAsync()
    {
        if (_releaseClient is null)
        {
            TeacherUpdateLatestVersion = "不可用";
            TeacherUpdateStatus = _releaseClientError ?? "此版本暂不支持自动更新。";
            return;
        }
        _isCheckingTeacherUpdate = true;
        Changed(nameof(CanCheckTeacherUpdate));
        Changed(nameof(CanDownloadTeacherUpdate));
        Changed(nameof(CanExportOfflineTeacherUpdate));
        Changed(nameof(CanVerifyOfflineTeacherUpdate));
        Changed(nameof(CanInstallOfflineTeacherUpdate));
        TeacherUpdateLatestVersion = "检查中…";
        TeacherUpdateStatus = "正在检查更新……";
        try
        {
            var result = await _releaseClient.CheckLatestAsync(ApplicationReleaseRole.TeacherConsole, AppVersion);
            _teacherUpdateRelease = result.Release;
            TeacherUpdateLatestVersion = result.Release?.Manifest.Version ?? "暂无可用版本";
            _teacherUpdateAvailable = result.IsNewer && result.Release is not null &&
                                      HasRequiredPolicyCapabilities(result.Release.Manifest);
            TeacherUpdateStatus = result.Release is null
                ? "暂时没有可用更新。"
                : result.IsNewer && !HasRequiredPolicyCapabilities(result.Release.Manifest)
                    ? "新版本与当前功能不兼容，已停止更新。"
                : result.IsNewer
                    ? $"发现新版本 {result.Release.Manifest.Version}，可以更新。"
                    : "已是最新版本。";
            RecordUpdateSuccess(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.Check,
                result.Release?.Manifest.Version);
        }
        catch (Exception exception)
        {
            _teacherUpdateRelease = null;
            _teacherUpdateAvailable = false;
            TeacherUpdateLatestVersion = "获取失败";
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
        OfflineTeacherUpdateStatus = "正在检查离线更新包……";
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
                ? $"已验证版本 {verified.Release.Manifest.Version}，可以安装。"
                : versionComparison > 0
                    ? "此更新与当前功能不兼容，无法安装。"
                : $"所选版本 {verified.Release.Manifest.Version} 不高于当前版本。";
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
            OfflineTeacherUpdateStatus = "更新已启动，应用即将关闭。";
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
            OfflineTeacherUpdateStatus = "正在准备离线更新包……";
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
            OfflineTeacherUpdateStatus = $"离线更新包 {release.Manifest.Version} 已导出。";
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.OfflineExport, targetVersion, exception);
            OfflineTeacherUpdateStatus = "无法导出离线更新包：" + failure.ToUserMessage();
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
        SetTeacherUpdateProgress(true, false, 0, "正在从 GitCode 下载并校验教师端更新包……");
        TeacherUpdateStatus = "正在下载更新……";
        var failureStage = "下载并校验更新包";
        var handoffStarted = false;
        try
        {
            var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "Updates");
            var installerPath = await _releaseClient.DownloadAsync(release,
                ApplicationReleaseRole.TeacherConsole, updateDirectory,
                progress: new Progress<InstallerResourceProgress>(SetTeacherResourceProgress));
            failureStage = "复核安装包并启动更新助手";
            SetTeacherUpdateProgress(true, true, 100, "下载完成，正在复核安装包并启动更新助手……");
            var verificationProgress = new Progress<InstallerResourceProgress>(SetTeacherResourceProgress);
            await Task.Run(() => ApplicationReleaseUpdateHandoff.Start(installerPath,
                ApplicationReleaseRole.TeacherConsole, AppVersion, verificationProgress));
            handoffStarted = true;
            RecordUpdateHandoffStarted(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.DownloadAndInstall,
                release.Manifest.Version);
            TeacherUpdateStatus = "更新已启动，应用即将关闭。";
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.DownloadAndInstall, release.Manifest.Version, exception);
            TeacherUpdateStatus = $"更新失败（{failureStage}），当前版本未更改：{failure.ToUserMessage()}";
            SetTeacherUpdateProgress(true, false, _teacherUpdateProgressPercent,
                $"失败阶段：{failureStage}。教师端仍是 {AppVersion}；可以重试或导出问题报告。");
        }
        finally
        {
            if (handoffStarted) SetTeacherUpdateProgress(false, true, 0, "");
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
            UpdateDiagnosticsStatus = $"已导出 {count} 条诊断（不会自动上传）。";
            return true;
        }
        catch (Exception)
        {
            UpdateDiagnosticsStatus = "无法保存诊断文件。";
            return false;
        }
    }

    public bool ExportStudentUpdatePerformanceReport(string destinationPath)
    {
        if (_lastStudentUpdatePerformanceReport is null)
        {
            StudentUpdatePerformanceReportExportStatus = "本次更新尚无可导出的性能汇总。";
            Changed(nameof(StudentUpdatePerformanceReportExportStatus));
            return false;
        }
        try
        {
            var bytes = StudentUpdatePerformanceReportBuilder.Serialize(_lastStudentUpdatePerformanceReport);
            if (bytes.Length > StudentUpdatePerformanceReportBuilder.MaximumReportBytes)
                throw new InvalidDataException("性能汇总超过大小限制。");
            File.WriteAllBytes(destinationPath, bytes);
            StudentUpdatePerformanceReportExportStatus =
                "性能汇总已保存。文件不含电脑名、IP 或校区身份，也不会自动上传。";
            Changed(nameof(StudentUpdatePerformanceReportExportStatus));
            return true;
        }
        catch (Exception)
        {
            StudentUpdatePerformanceReportExportStatus = "无法保存性能汇总文件。";
            Changed(nameof(StudentUpdatePerformanceReportExportStatus));
            return false;
        }
    }

    public void ReportStudentUpdatePerformanceReportExportFailure()
    {
        StudentUpdatePerformanceReportExportStatus = "无法打开保存位置或保存性能汇总文件。";
        Changed(nameof(StudentUpdatePerformanceReportExportStatus));
    }

    public void ReportDiagnosticExportFailure() =>
        UpdateDiagnosticsStatus = "无法保存诊断文件。";

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
                    : "今日校区状态已同步。";
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
                : "今日校区状态已同步。";
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
            Changed(nameof(CanPrepareRoomSeatGrid));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(RoomSummary));
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
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
        private set
        {
            _roomProfiles = value;
            Changed();
            Changed(nameof(HasRoomProfiles));
            Changed(nameof(HasNoRoomProfiles));
            Changed(nameof(HasSingleRoomProfile));
            Changed(nameof(HasMultipleRoomProfiles));
            Changed(nameof(ClassroomRoomContextSummary));
        }
    }
    public bool HasRoomProfiles => RoomProfiles.Count > 0;
    public bool HasNoRoomProfiles => RoomProfiles.Count == 0;
    public bool HasSingleRoomProfile => RoomProfiles.Count == 1;
    public bool HasMultipleRoomProfiles => RoomProfiles.Count > 1;
    public string ClassroomRoomContextSummary => _activeClassroomSession is { } active
        ? $"本堂课：{active.Room.RoomName} · {active.Targets.Length} 台电脑"
        : SelectedRoomProfile is { } room
            ? $"{room.DisplayName} · {room.ComputerCount} 台电脑"
            : "尚未配置教室";
    public string RoomSelectionStatus
    {
        get => _roomSelectionStatus;
        private set
        {
            if (_roomSelectionStatus == value) return;
            _roomSelectionStatus = value;
            Changed();
            Changed(nameof(HasRoomSelectionStatus));
        }
    }
    public bool HasRoomSelectionStatus => !string.IsNullOrWhiteSpace(RoomSelectionStatus);
    public IReadOnlyList<TeacherSeatCell> SeatMapCells
    {
        get => _seatMapCells;
        private set
        {
            _seatMapCells = value;
            Changed();
            Changed(nameof(HasSeatMap));
            Changed(nameof(HasNoSeatMap));
            Changed(nameof(CanPrintSeatMap));
            Changed(nameof(SeatGridSummary));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanToggleSeatMapEditing));
            Changed(nameof(CanCancelSeatMapEditing));
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(HasSeatsOutsidePendingGrid));
            Changed(nameof(SeatDimensionRemovalSummary));
            NotifyTeacherSetupNavigationStateChanged();
        }
    }
    public bool HasSeatMap => _seatGrid is not null;
    public bool HasNoSeatMap => !HasSeatMap;
    public bool CanPrintSeatMap => HasSeatMap && SeatGridSeatCount > 0;
    public string SeatGridTitle => SelectedRoomProfile?.DisplayName ?? "机房座位图";
    public int SeatGridSeatCount => _seatGrid?.Seats.Length ?? 0;
    public int SeatGridNamedCount => _seatGrid?.Seats.Count(seat => !string.IsNullOrWhiteSpace(seat.StudentName)) ?? 0;
    public int SeatGridNumberPendingCount => _seatGrid?.Seats.Count(seat =>
        !string.IsNullOrWhiteSpace(seat.StudentName) && seat.ComputerNumber is null) ?? 0;
    public string SeatGridSummary => $"已添加 {SeatGridSeatCount} 个桌位 · 已填写 {SeatGridNamedCount} 个姓名" +
        (SeatGridNumberPendingCount > 0 ? $" · 待编号 {SeatGridNumberPendingCount}" : "");
    public bool CanAssignMissingSeatComputerNumbers => IsSeatMapEditing && !IsExecuting &&
        SeatGridNumberPendingCount > 0;
    public string AssignMissingSeatComputerNumbersLabel => SeatGridNumberPendingCount > 0
        ? $"给待编号座位分配新号（{SeatGridNumberPendingCount}）" : "给待编号座位分配新号";
    public bool CanOpenVeyonConfigurator => OperatingSystem.IsWindows() && !IsExecuting;
    public bool CanDeleteRoomProfile => !IsExecuting && SelectedCampusProfile is not null &&
        SelectedRoomProfile is not null && _activeClassroomSession?.Room.RoomId != SelectedRoomProfile.RoomId;
    public bool IsSeatMapPreview => HasSeatMap && !IsSeatMapEditing && !SeatSwapMode;
    public bool IsSeatMapEditing
    {
        get => _isSeatMapEditing;
        private set
        {
            if (_isSeatMapEditing == value) return;
            _isSeatMapEditing = value;
            Changed();
            Changed(nameof(IsSeatMapPreview));
            Changed(nameof(SeatMapEditButtonLabel));
            Changed(nameof(CanEditSeatMap));
            Changed(nameof(CanInteractWithSeatMap));
            Changed(nameof(CanToggleSeatMapEditing));
            Changed(nameof(CanToggleSeatAddRemoveMode));
            Changed(nameof(CanAssignMissingSeatComputerNumbers));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(CanApplySeatGridDimensions));
            if (!value) IsSeatAddRemoveMode = false;
            RefreshSeatMapCells();
        }
    }
    public string SeatMapEditButtonLabel => IsSeatMapEditing ? "完成编辑" : "编辑座位图";
    public bool CanToggleSeatMapEditing => HasSeatMap && !IsExecuting && !IsPreparingSeatSwapPlan && !SeatSwapMode;
    public bool CanCancelSeatMapEditing => CanEditSeatMap;
    public bool IsSeatAddRemoveMode
    {
        get => _isSeatAddRemoveMode;
        private set
        {
            if (_isSeatAddRemoveMode == value) return;
            _isSeatAddRemoveMode = value;
            Changed();
            Changed(nameof(SeatAddRemoveModeButtonLabel));
            Changed(nameof(CanToggleSeatAddRemoveMode));
            RefreshSeatMapCells();
        }
    }
    public string SeatAddRemoveModeButtonLabel => IsSeatAddRemoveMode ? "完成添加座位" : "添加座位";
    public bool CanToggleSeatAddRemoveMode => IsSeatMapEditing && !IsExecuting &&
        !IsPreparingSeatSwapPlan && !SeatSwapMode;
    public bool CanStartRoomCreation => OperatingSystem.IsWindows() && !IsExecuting &&
        !IsCheckingRoomConflicts && !IsPreparingSeatSwapPlan && HasSeatMap && IsSeatMapEditing && SeatGridSeatCount > 0 &&
        SeatGridNamedCount > 0 && SeatGridNumberPendingCount == 0 &&
        !string.IsNullOrWhiteSpace(RoomCampusName) &&
        !string.IsNullOrWhiteSpace(RoomLocationName);
    public IReadOnlyList<TeacherVeyonTargetChoice> SeatVeyonTargetChoices
    {
        get => _seatVeyonTargetChoices;
        private set { _seatVeyonTargetChoices = value; Changed(); }
    }
    public string SeatVeyonTargetStatus
    {
        get => _seatVeyonTargetStatus;
        private set { _seatVeyonTargetStatus = value; Changed(); Changed(nameof(HasSeatVeyonTargetStatus)); }
    }
    public bool HasSeatVeyonTargetStatus => SeatVeyonTargetStatus.Length > 0;
    public bool CanReadSeatVeyonTargets => OperatingSystem.IsWindows() && !IsExecuting &&
                                           !IsReadingWebsiteLocations && !_isReadingSeatVeyonTargets &&
                                           !IsPreparingSeatSwapPlan &&
                                           SelectedRoomProfile is not null;
    public bool IsReadingSeatVeyonTargets
    {
        get => _isReadingSeatVeyonTargets;
        private set
        {
            _isReadingSeatVeyonTargets = value;
            Changed();
            Changed(nameof(CanReadSeatVeyonTargets));
            Changed(nameof(CanSwapSeats));
            Changed(nameof(CanSyncSeatGridWithVeyon));
        }
    }
    public bool IsPreparingSeatSwapPlan
    {
        get => _isPreparingSeatSwapPlan;
        private set
        {
            if (_isPreparingSeatSwapPlan == value) return;
            _isPreparingSeatSwapPlan = value;
            Changed();
            Changed(nameof(CanPrepareSeatSwapPlan));
            Changed(nameof(CanSwapSeats));
            Changed(nameof(CanReadSeatVeyonTargets));
            Changed(nameof(CanEditSeatMap));
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanInteractWithSeatMap));
            Changed(nameof(CanToggleSeatSwapMode));
            Changed(nameof(CanToggleSeatMapEditing));
            Changed(nameof(CanToggleSeatAddRemoveMode));
            Changed(nameof(CanSyncSeatGridWithVeyon));
            RefreshSeatMapCells();
        }
    }
    public bool CanEditSeatMap => !IsExecuting && !IsPreparingSeatSwapPlan && IsSeatMapEditing && !SeatSwapMode;
    public bool CanInteractWithSeatMap => !IsExecuting && !IsPreparingSeatSwapPlan &&
                                          (IsSeatMapEditing || SeatSwapMode);
    public bool CanToggleSeatSwapMode => !IsExecuting && !IsPreparingSeatSwapPlan;
    public bool SeatSwapMode
    {
        get => _seatSwapMode;
        set
        {
            if (!CanToggleSeatSwapMode && value != _seatSwapMode)
            {
                Changed();
                return;
            }
            if (_seatSwapMode == value) return;
            _seatSwapMode = value;
            if (value)
            {
                CommitSeatMapEdit();
                IsSeatMapEditing = false;
            }
            _seatSwapSelection.Clear();
            _preparedSeatSwapPlan = null;
            SeatSwapPlanRows = Array.Empty<string>();
            SeatSwapTargetResults = Array.Empty<string>();
            SeatSwapStatus = value
                ? "换座模式已开启：依次点选两个座位，其中一个可以是空座位。"
                : "换座模式已关闭。";
            Changed();
            Changed(nameof(CanPrepareSeatSwapPlan));
            Changed(nameof(CanSwapSeats));
            Changed(nameof(CanExecuteSeatSwapPlan));
            Changed(nameof(SeatSwapSelectionSummary));
            Changed(nameof(IsSeatMapPreview));
            Changed(nameof(CanEditSeatMap));
            Changed(nameof(CanInteractWithSeatMap));
            Changed(nameof(CanToggleSeatMapEditing));
            Changed(nameof(CanToggleSeatAddRemoveMode));
            RefreshSeatMapCells();
        }
    }
    public string SeatSwapStatus
    {
        get => _seatSwapStatus;
        private set { _seatSwapStatus = value; Changed(); Changed(nameof(HasSeatSwapStatus)); }
    }
    public bool HasSeatSwapStatus => SeatSwapStatus.Length > 0;
    public IReadOnlyList<string> SeatSwapPlanRows
    {
        get => _seatSwapPlanRows;
        private set { _seatSwapPlanRows = value; Changed(); Changed(nameof(HasSeatSwapPlan)); }
    }
    public IReadOnlyList<string> SeatSwapTargetResults
    {
        get => _seatSwapTargetResults;
        private set { _seatSwapTargetResults = value; Changed(); Changed(nameof(HasSeatSwapTargetResults)); }
    }
    public bool HasSeatSwapPlan => SeatSwapPlanRows.Count > 0;
    public bool HasSeatSwapTargetResults => SeatSwapTargetResults.Count > 0;
    public bool CanPrepareSeatSwapPlan => SeatSwapMode && HasTwoSwapSeatsWithAStudent &&
                                          !IsExecuting && !IsPreparingSeatSwapPlan && _seatGrid is not null;
    public bool CanSwapSeats => SeatSwapMode && HasTwoSwapSeatsWithAStudent &&
                                !IsExecuting && !IsPreparingSeatSwapPlan && !IsReadingSeatVeyonTargets &&
                                _seatGrid is not null;
    private bool HasTwoSwapSeatsWithAStudent => _seatGrid is not null && _seatSwapSelection.Count == 2 &&
        _seatGrid.Seats.Any(seat => _seatSwapSelection.Contains(seat.SeatId) &&
                                    !string.IsNullOrWhiteSpace(seat.StudentName));
    public bool CanExecuteSeatSwapPlan => SeatSwapMode && _preparedSeatSwapPlan is not null &&
                                          !IsExecuting;
    public string SeatSwapSelectionSummary
    {
        get
        {
            if (_seatSwapSelection.Count == 0) return "尚未选择座位。";
            var seats = _seatSwapSelection.Select(id => _seatGrid?.Seats.SingleOrDefault(seat => seat.SeatId == id))
                .Where(seat => seat is not null)
                .Select(seat => string.IsNullOrWhiteSpace(seat!.StudentName)
                    ? $"空座位（第 {seat.Row} 行第 {seat.Column} 列）"
                    : $"{seat.StudentName}（第 {seat.Row} 行第 {seat.Column} 列）")
                .ToArray();
            return $"已选择 {_seatSwapSelection.Count}/2：{string.Join(" ↔ ", seats)}";
        }
    }
    public string SeatGridContext => SelectedCampusProfile is { } campus && SelectedRoomProfile is { } room
        ? $"{campus.DisplayName} · {room.DisplayName}"
        : "尚未选择地点";
    public void ToggleSeatMapEditing()
    {
        if (!CanToggleSeatMapEditing) return;
        if (IsSeatMapEditing)
        {
            CommitSeatMapEdit();
            IsSeatMapEditing = false;
        }
        else
            BeginSeatMapEdit();
        SeatMapStatus = IsSeatMapEditing ? "编辑中。" : "预览中。";
    }

    public void CancelSeatMapEditing()
    {
        if (!CanCancelSeatMapEditing || _seatMapEditSnapshot is not { } snapshot ||
            SelectedCampusProfile?.ProfileId != snapshot.CampusProfileId ||
            SelectedRoomProfile?.RoomId != snapshot.RoomId)
            return;
        try
        {
            ApplySeatGrid(_classroomSeatGridStore.RestoreLayout(snapshot));
            CommitSeatMapEdit();
            IsSeatMapEditing = false;
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            RefreshClassroomEventSeatLocations();
            SeatMapStatus = "已取消本次编辑；座位、姓名、编号和行列均已恢复到进入编辑前的状态。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "取消编辑失败；当前座位图仍保留，可重试取消。" + exception.Message;
        }
    }

    private void BeginSeatMapEdit()
    {
        if (_seatGrid is null) return;
        _seatMapEditSnapshot = _seatGrid with { Seats = _seatGrid.Seats.Select(seat => seat with { }).ToArray() };
        _seatMapEditOriginalSeatIds = _seatGrid.Seats.Select(seat => seat.SeatId).ToHashSet();
        IsSeatMapEditing = true;
    }

    private void CommitSeatMapEdit()
    {
        _seatMapEditSnapshot = null;
        _seatMapEditOriginalSeatIds.Clear();
        RefreshSeatMapCells();
    }

    public void ToggleSeatAddRemoveMode()
    {
        if (!CanToggleSeatAddRemoveMode) return;
        IsSeatAddRemoveMode = !IsSeatAddRemoveMode;
        SeatMapStatus = IsSeatAddRemoveMode
            ? "添加座位模式已开启；点击空格或按住左键拖动可连续新增座位；拖动只新增，已有座位请单击并确认移除。"
            : "添加座位模式已关闭；点击桌位可编辑学生姓名。";
    }

    public string BuildPrintableSeatChartHtml()
    {
        if (_seatGrid is null || SeatGridSeatCount == 0)
            throw new InvalidOperationException("当前没有可打印的座位表。");

        var orderedSeats = _seatGrid.Seats.OrderBy(seat => seat.Row).ThenBy(seat => seat.Column).ToArray();
        var namedSeats = orderedSeats.Where(seat => !string.IsNullOrWhiteSpace(seat.StudentName)).ToArray();
        var campusName = SelectedCampusProfile?.DisplayName ?? RoomCampusName.Trim();
        var roomName = SelectedRoomProfile?.DisplayName ?? RoomLocationName.Trim();
        var output = new StringBuilder(16_384);
        output.AppendLine("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        output.Append("<title>").Append(System.Net.WebUtility.HtmlEncode(roomName)).AppendLine(" 座位表</title>");
        output.AppendLine("<style>");
        output.AppendLine("@page{size:A3 landscape;margin:8mm}*{box-sizing:border-box}body{font-family:'Microsoft YaHei',sans-serif;color:#173447;margin:0}h1{font-size:20pt;margin:0 0 3mm}.meta{font-size:10pt;color:#526171;margin-bottom:5mm}.toolbar{margin:0 0 5mm}.toolbar button{padding:7px 14px;font-size:11pt}.map{display:grid;gap:1px;width:100%;grid-template-columns:9mm repeat(var(--cols),minmax(0,1fr));border:1px solid #bccbd5;background:#bccbd5}.axis{background:#edf2f6;text-align:center;font-size:7pt;padding:1mm 0;min-height:6mm}.seat{padding:1mm;min-height:8mm;overflow:hidden;display:flex;flex-direction:column;justify-content:center;line-height:1.12}.seat.without-computer{background:#fff;border:1px solid #9cb4c0}.seat.with-computer{background:#dff1ec;border:1px solid #66aa9a}.seat .number{font-size:7pt;font-weight:700;overflow-wrap:anywhere}.seat .name{font-size:7pt;overflow-wrap:anywhere}.aisle{background:#eef2f5;min-height:8mm}.legend{display:flex;flex-wrap:wrap;gap:12px;margin-top:3mm;font-size:8pt;color:#526171}.legend-item{display:inline-flex;align-items:center;gap:4px}.swatch{width:10px;height:10px;border:1px solid #9cb4c0;border-radius:3px;display:inline-block}.swatch.aisle{background:#eef2f5;border-color:#dce3e8}.swatch.without-computer{background:#fff}.swatch.with-computer{background:#dff1ec;border-color:#66aa9a}.note{font-size:8pt;color:#667784;margin-top:3mm}@media print{.toolbar{display:none}.map{break-inside:avoid}.seat,.aisle{min-height:7mm}}");
        output.AppendLine(".seat{background:#fff;border:1px solid #9cb4c0}.swatch.seat{background:#fff}");
        output.AppendLine(".seat .computer-id{font-size:6.5pt;color:#17645a;font-weight:700;overflow-wrap:anywhere}");
        output.AppendLine("</style></head><body>");
        output.AppendLine("<div class=\"toolbar\"><button onclick=\"window.print()\">打印 / 另存为 PDF</button></div>");
        output.Append("<h1>机房座位表</h1><div class=\"meta\">")
            .Append(System.Net.WebUtility.HtmlEncode(campusName)).Append(" · ")
            .Append(System.Net.WebUtility.HtmlEncode(roomName)).Append("　｜　学生 ")
            .Append(SeatGridNamedCount.ToString(CultureInfo.InvariantCulture)).Append(" / ")
            .Append(SeatGridSeatCount.ToString(CultureInfo.InvariantCulture)).Append(" 人　｜　")
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .AppendLine("</div>");
        output.Append("<div class=\"map\" style=\"--cols:")
            .Append(_seatGrid.Columns.ToString(CultureInfo.InvariantCulture)).AppendLine("\">");
        output.AppendLine("<div class=\"axis\">行 / 列</div>");
        for (var column = 1; column <= _seatGrid.Columns; column++)
            output.Append("<div class=\"axis\">").Append(column.ToString(CultureInfo.InvariantCulture)).AppendLine("</div>");

        var seatByPosition = orderedSeats.ToDictionary(seat => (seat.Row, seat.Column));
        for (var row = 1; row <= _seatGrid.Rows; row++)
        {
            output.Append("<div class=\"axis\">").Append(row.ToString(CultureInfo.InvariantCulture)).AppendLine("</div>");
            for (var column = 1; column <= _seatGrid.Columns; column++)
            {
                if (!seatByPosition.TryGetValue((row, column), out var item))
                {
                    output.AppendLine("<div class=\"aisle\"></div>");
                    continue;
                }

                var studentName = string.IsNullOrWhiteSpace(item.StudentName)
                    ? "未填写姓名"
                    : System.Net.WebUtility.HtmlEncode(item.StudentName);
                output.Append("<div class=\"seat\"><div class=\"number\">第 ")
                    .Append(row.ToString(CultureInfo.InvariantCulture)).Append(" 排 · 第 ")
                    .Append(column.ToString(CultureInfo.InvariantCulture)).Append(" 列</div>");
                output.Append("<div class=\"name\">").Append(studentName).AppendLine("</div></div>");
            }
        }
        output.AppendLine("</div><div class=\"legend\"><span class=\"legend-item\"><i class=\"swatch aisle\"></i>空桌位区域</span><span class=\"legend-item\"><i class=\"swatch seat\"></i>桌位</span></div><div class=\"note\">学生姓名保存在教师机。填写姓名时自动分配可用电脑编号并生成学生电脑；空姓名桌位只保留为桌位。</div></body></html>");
        return output.ToString();
    }

    public void ReportSeatChartExportStatus(string status) => SeatMapStatus = status;

    public bool CanPrepareRoomSeatGrid => !IsExecuting && !IsCheckingRoomConflicts &&
                                         !string.IsNullOrWhiteSpace(RoomCampusName) &&
                                         !string.IsNullOrWhiteSpace(RoomLocationName);
    public string RoomCampusName
    {
        get => _roomCampusName;
        set
        {
            if (_roomCampusName == value) return;
            var previousCampusName = _roomCampusName.Trim();
            _roomCampusName = value ?? "";
            Changed();
            if (!HasSeatMap && (string.IsNullOrWhiteSpace(RoomLocationName) ||
                                string.Equals(RoomLocationName.Trim(), previousCampusName,
                                    StringComparison.OrdinalIgnoreCase)))
                RoomLocationName = _roomCampusName.Trim();
            Changed(nameof(CanPrepareRoomSeatGrid));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(RoomSummary));
            InvalidateRoomReview();
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            ClearRoomCreateFeedback();
        }
    }
    public decimal SeatRows
    {
        get => _seatRows;
        private set { if (_seatRows == value) return; _seatRows = value; Changed(); }
    }
    public decimal SeatColumns
    {
        get => _seatColumns;
        private set { if (_seatColumns == value) return; _seatColumns = value; Changed(); }
    }
    public decimal SeatRowsDraft
    {
        get => _seatRowsDraft;
        set
        {
            if (_seatRowsDraft == value) return;
            _seatRowsDraft = value;
            Changed();
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(SeatDimensionRemovalSummary));
        }
    }
    public decimal SeatColumnsDraft
    {
        get => _seatColumnsDraft;
        set
        {
            if (_seatColumnsDraft == value) return;
            _seatColumnsDraft = value;
            Changed();
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(SeatDimensionRemovalSummary));
        }
    }
    public decimal SeatDimensionMinimum => 1;
    public decimal SeatDimensionMaximum => ClassroomSeatGridStore.MaximumEditableRows;
    public int SeatGridColumns => Math.Clamp((int)_seatColumns, 1, ClassroomSeatGridStore.MaximumColumns);
    public bool CanApplySeatGridDimensions => CanEditSeatMap && _seatGrid is not null &&
        _seatRowsDraft == decimal.Truncate(_seatRowsDraft) &&
        _seatColumnsDraft == decimal.Truncate(_seatColumnsDraft) &&
        _seatRowsDraft is >= 1 and <= ClassroomSeatGridStore.MaximumEditableRows &&
        _seatColumnsDraft is >= 1 and <= ClassroomSeatGridStore.MaximumEditableColumns &&
        (_seatRowsDraft != _seatRows || _seatColumnsDraft != _seatColumns);
    public bool HasSeatsOutsidePendingGrid => _seatGrid?.Seats.Any(seat =>
        seat.Row > _seatRowsDraft || seat.Column > _seatColumnsDraft) == true;
    public string SeatDimensionRemovalSummary => HasSeatsOutsidePendingGrid
        ? $"将删除网格外的 {_seatGrid!.Seats.Count(seat => seat.Row > _seatRowsDraft || seat.Column > _seatColumnsDraft)} 个座位，确认后生效。"
        : "不会删除现有座位。";
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
            var preferredRoomId = value is not null && _recentRoomSelections.TryGetValue(value.ProfileId, out var roomId)
                ? roomId
                : Guid.Empty;
            SelectedRoomProfile = RoomProfiles.FirstOrDefault(item => item.RoomId == preferredRoomId) ??
                                  RoomProfiles.FirstOrDefault();
            CampusProfileName = value?.DisplayName ?? "";
            RoomCampusName = value?.DisplayName ?? "本机校区";
            CampusDirectoryError = "";
            Changed(nameof(ClassroomSessionSummary));
            Changed(nameof(ClassroomOverviewDeviceCount));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(SeatGridContext));
            if (_seatGrid?.CampusProfileId != value?.ProfileId ||
                _seatGrid?.RoomId != SelectedRoomProfile?.RoomId)
                LoadSelectedSeatGrid();
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
            SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
            SeatVeyonTargetStatus = "";
            Changed();
            Changed(nameof(HasSelectedRoomProfile));
            Changed(nameof(ClassroomRoomContextSummary));
            Changed(nameof(HasMultipleRoomProfiles));
            Changed(nameof(HasSingleRoomProfile));
            Changed(nameof(HasNoRoomProfiles));
            Changed(nameof(ClassroomSessionSummary));
            Changed(nameof(ClassroomOverviewDeviceCount));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanReadSeatVeyonTargets));
            Changed(nameof(CanDeleteRoomProfile));
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
            RoomCampusName = SelectedCampusProfile?.DisplayName ?? "本机校区";
            RoomLocationName = value?.DisplayName ?? "";
            RoomPrefix = value?.Prefix ?? "PC-";
            RoomStart = value?.StartNumber.ToString(CultureInfo.InvariantCulture) ?? "1";
            RoomComputerHosts = string.Join(Environment.NewLine, value?.HostOverrides ?? []);
            CampusDirectoryError = "";
            LoadSelectedSeatGrid();
            Changed(nameof(SeatGridContext));
            SaveRecentRoomSelection(value);
        }
    }
    public bool HasSelectedRoomProfile => SelectedRoomProfile is not null;

    private void SaveRecentRoomSelection(TeacherRoomProfile? room)
    {
        if (room is null || SelectedCampusProfile is not { } campus || _roomSelectionStore is null) return;
        try
        {
            _roomSelectionStore.Save(campus.ProfileId, room.RoomId);
            _recentRoomSelections[campus.ProfileId] = room.RoomId;
            RoomSelectionStatus = "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RoomSelectionStatus = "无法保存最近教室；本次运行仍会使用当前选择。";
        }
    }

    internal void SaveSeatCell(int row, int column, string studentName, string? computerNumberText = null)
    {
        if (!CanEditSeatMap) return;
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room) return;
        try
        {
            var currentSeat = _seatGrid?.Seats.SingleOrDefault(seat => seat.Row == row && seat.Column == column);
            int? computerNumber = null;
            if (!string.IsNullOrWhiteSpace(computerNumberText))
            {
                if (!int.TryParse(computerNumberText, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var parsedNumber) || parsedNumber is < 1 or > MachineNaming.MaximumNumber)
                    throw new InvalidDataException($"电脑编号请输入 1–{MachineNaming.MaximumNumber} 的整数。");
                computerNumber = parsedNumber;
            }
            var willAutoAssignNumber = !string.IsNullOrWhiteSpace(studentName) && computerNumber is null &&
                                       currentSeat?.ComputerNumber is null;
            var firstComputerNumber = willAutoAssignNumber
                ? GetFirstComputerNumberAfterCampusMaximum()
                : room.StartNumber;
            ApplySeatGrid(_classroomSeatGridStore.SaveSeat(campus.ProfileId, room.RoomId,
                row, column, studentName, computerNumber, firstComputerNumber));
            var cell = SeatMapCells.FirstOrDefault(item => item.Row == row && item.Column == column);
            SeatMapStatus = cell?.SeatNumber is { } seatNumber
                ? willAutoAssignNumber
                    ? $"已从现有最大编号后自动分配电脑编号 {seatNumber}；座位资料已保存。"
                    : $"电脑编号 {seatNumber} 的座位资料已保存。"
                : "桌位资料已保存；留空姓名只保留桌位，不生成学生电脑。";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "无法保存座位表；原布局未更改。" + exception.Message;
        }
    }

    public void ToggleSeatCell(int row, int column)
    {
        if (!IsSeatAddRemoveMode || !CanEditSeatMap || _seatGrid is null) return;
        var cell = SeatMapCells.FirstOrDefault(item => item.Row == row && item.Column == column);
        if (cell is null) return;
        if (cell.IsSeat)
            RemoveSeatCell(row, column, unassignVeyonTarget: true);
        else
            SaveSeatCell(row, column, string.Empty, null);
    }

    internal void PaintSeatCells(IReadOnlyList<(int Row, int Column)> cells, bool makeSeats)
    {
        if (!IsSeatAddRemoveMode || !CanEditSeatMap || _seatGrid is null || cells.Count == 0 ||
            SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room) return;
        try
        {
            ApplySeatGrid(_classroomSeatGridStore.SetSeatCells(campus.ProfileId, room.RoomId, cells, makeSeats));
            SeatMapStatus = "座位图已更新。";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "连续调整座位表失败；原布局未更改。" + exception.Message;
        }
    }

    internal void RemoveSeatCell(int row, int column, bool unassignVeyonTarget)
    {
        if (!CanEditSeatMap) return;
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room) return;
        try
        {
            ApplySeatGrid(_classroomSeatGridStore.RemoveSeat(campus.ProfileId, room.RoomId,
                row, column, unassignVeyonTarget));
            SeatMapStatus = "已移除桌位；该格现在是空桌位区域。";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "无法移除座位；原布局未更改。" + exception.Message;
        }
    }

    public async Task ReadSeatVeyonTargetsAsync()
    {
        if (!CanReadSeatVeyonTargets || SelectedRoomProfile is not { } room || SelectedCampusProfile is not { } campus) return;
        IsReadingSeatVeyonTargets = true;
        SeatVeyonTargetStatus = $"正在读取“{room.DisplayName}”的 Veyon 电脑……";
        try
        {
            var roomId = room.RoomId;
            var snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            if (SelectedRoomProfile?.RoomId != roomId)
            {
                SeatVeyonTargetStatus = "当前地点已切换；本次电脑清单未应用。";
                return;
            }
            var roomComputers = snapshot.Objects
                .Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                               item.Location.Equals(room.DisplayName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var candidates = new List<(VeyonNetworkObject Computer, string HostAddress)>();
            foreach (var item in roomComputers)
            {
                if (item.ObjectId == Guid.Empty || item.ParentObjectId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(item.Name) || item.Name != item.Name.Trim() ||
                    item.Name.Length > 100 || item.Name.Any(char.IsControl))
                    continue;
                try
                {
                    var host = VeyonHostAddress.NormalizeOverride(item.Host);
                    if (host.Length > 0) candidates.Add((item, host));
                }
                catch (InvalidDataException)
                {
                    // Keep malformed entries pending without blocking valid computers in this room.
                }
            }
            var orderedCandidates = candidates
                .OrderBy(item => item.Computer.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.HostAddress, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var ambiguousIds = orderedCandidates.GroupBy(item => item.Computer.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).SelectMany(group => group).Select(item => item.Computer.ObjectId)
                .Concat(orderedCandidates.GroupBy(item => item.HostAddress,
                        StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1).SelectMany(group => group).Select(item => item.Computer.ObjectId))
                .ToHashSet();
            var studentNamesByAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ambiguousStudentAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_seatGrid is { } currentLayout)
            {
                IReadOnlyList<int> roomNumbers;
                IReadOnlyList<string> roomComputerNames;
                try { roomNumbers = room.GetComputerNumbers(); roomComputerNames = room.GetComputerNames(); }
                catch (InvalidDataException)
                { roomNumbers = Array.Empty<int>(); roomComputerNames = Array.Empty<string>(); }
                foreach (var seat in currentLayout.Seats.Where(item => !string.IsNullOrWhiteSpace(item.StudentName)))
                {
                    var studentName = seat.StudentName.Trim();
                    if (!string.IsNullOrWhiteSpace(seat.VeyonComputerName))
                        AddUnambiguousStudentName(studentNamesByAlias, ambiguousStudentAliases,
                            seat.VeyonComputerName, studentName);
                    if (!string.IsNullOrWhiteSpace(seat.VeyonHostAddress))
                        AddUnambiguousStudentName(studentNamesByAlias, ambiguousStudentAliases,
                            seat.VeyonHostAddress, studentName);
                    if (seat.ComputerNumber is not { } number) continue;
                    var seatIndex = -1;
                    for (var candidate = 0; candidate < roomNumbers.Count; candidate++)
                        if (roomNumbers[candidate] == number) { seatIndex = candidate; break; }
                    if (seatIndex < 0) continue;
                    if (seatIndex < roomComputerNames.Count)
                        AddUnambiguousStudentName(studentNamesByAlias, ambiguousStudentAliases,
                            roomComputerNames[seatIndex], studentName);
                    if (room.HostOverrides is { } hosts && seatIndex < hosts.Count &&
                        !string.IsNullOrWhiteSpace(hosts[seatIndex]))
                        AddUnambiguousStudentName(studentNamesByAlias, ambiguousStudentAliases,
                            hosts[seatIndex], studentName);
                }
            }
            var choices = orderedCandidates.Where(item => !ambiguousIds.Contains(item.Computer.ObjectId))
                .Select(item => new TeacherVeyonTargetChoice(item.Computer.ObjectId, item.Computer.ParentObjectId,
                    item.Computer.Name, item.HostAddress, item.Computer.Location,
                    studentNamesByAlias.GetValueOrDefault(NormalizeTargetKey(item.Computer.Name),
                        studentNamesByAlias.GetValueOrDefault(NormalizeTargetKey(item.HostAddress), "未填写姓名"))))
                .ToArray();
            var pendingCount = roomComputers.Length - choices.Length;
            SeatVeyonTargetChoices = Array.AsReadOnly(choices);
            var automaticallyBound = 0;
            var namedSeatCount = _seatGrid?.Seats.Count(item => !string.IsNullOrWhiteSpace(item.StudentName)) ?? 0;
            if (_seatGrid is { } layout)
            {
                var namedSeats = layout.Seats.Where(item => !string.IsNullOrWhiteSpace(item.StudentName)).ToArray();
                var bindings = new List<ClassroomSeatGridTargetBinding>();
                var assignedObjects = new HashSet<Guid>();
                int? ReadComputerNumber(TeacherVeyonTargetChoice choice)
                {
                    var hostMatches = MachineNaming.TryParseNumber(room.Prefix, choice.HostAddress, out var hostNumber);
                    var nameMatches = MachineNaming.TryParseNumber(room.Prefix, choice.ComputerName, out var nameNumber);
                    if (hostMatches && nameMatches && hostNumber != nameNumber) return null;
                    if (hostMatches) return hostNumber;
                    return nameMatches ? nameNumber : null;
                }

                foreach (var seat in namedSeats)
                {
                    var studentName = seat.StudentName.Trim();
                    var uniqueSeatName = namedSeats.Count(item => string.Equals(item.StudentName.Trim(), studentName,
                        StringComparison.OrdinalIgnoreCase)) == 1;
                    var nameMatch = uniqueSeatName
                        ? choices.Where(item => string.Equals(item.ComputerName, studentName,
                            StringComparison.OrdinalIgnoreCase)).ToArray()
                        : Array.Empty<TeacherVeyonTargetChoice>();
                    var explicitMatch = choices.Where(item =>
                        !string.IsNullOrWhiteSpace(seat.VeyonComputerName) &&
                        !string.IsNullOrWhiteSpace(seat.VeyonHostAddress) &&
                        string.Equals(item.ComputerName, seat.VeyonComputerName,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.HostAddress, seat.VeyonHostAddress,
                            StringComparison.OrdinalIgnoreCase)).ToArray();
                    var numberMatch = seat.ComputerNumber is { } seatNumber
                        ? choices.Where(item => ReadComputerNumber(item) == seatNumber).ToArray()
                        : Array.Empty<TeacherVeyonTargetChoice>();
                    var candidatesForSeat = nameMatch.Length == 1 ? nameMatch
                        : explicitMatch.Length == 1 ? explicitMatch
                        : numberMatch.Length == 1 ? numberMatch
                        : Array.Empty<TeacherVeyonTargetChoice>();
                    var match = candidatesForSeat.Where(item => !assignedObjects.Contains(item.ObjectId)).ToArray();
                    if (match.Length != 1) continue;
                    var choice = match[0];
                    assignedObjects.Add(choice.ObjectId);
                    bindings.Add(new ClassroomSeatGridTargetBinding(seat.SeatId, choice.HostAddress,
                        choice.ComputerName, choice.ObjectId, choice.LocationObjectId));
                }

                HashSet<Guid> boundSeatIds;
                while (true)
                {
                    boundSeatIds = bindings.Select(binding => binding.SeatId).ToHashSet();
                    var reservedHosts = layout.Seats.Where(seat => !boundSeatIds.Contains(seat.SeatId))
                        .Select(seat => seat.VeyonHostAddress)
                        .Where(host => !string.IsNullOrWhiteSpace(host))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var reservedObjectIds = layout.Seats.Where(seat => !boundSeatIds.Contains(seat.SeatId))
                        .Where(seat => seat.VeyonObjectId is not null)
                        .Select(seat => seat.VeyonObjectId!.Value).ToHashSet();
                    var remainingBindings = bindings.Where(binding => !reservedHosts.Contains(binding.HostAddress) &&
                                                                       !reservedObjectIds.Contains(binding.ObjectId))
                        .ToList();
                    if (remainingBindings.Count == bindings.Count) break;
                    bindings = remainingBindings;
                }
                boundSeatIds = bindings.Select(binding => binding.SeatId).ToHashSet();

                var renumbered = 0;
                if (bindings.Count == namedSeats.Length)
                {
                    var targetNumbers = bindings.Select(binding =>
                    {
                        var choice = choices.Single(item => item.ObjectId == binding.ObjectId);
                        return ReadComputerNumber(choice) ?? 0;
                    }).ToArray();
                    var untouchedNumbers = layout.Seats.Where(seat => !boundSeatIds.Contains(seat.SeatId))
                        .Where(seat => seat.ComputerNumber is not null)
                        .Select(seat => seat.ComputerNumber!.Value).ToHashSet();
                    if (targetNumbers.All(number => number > 0) &&
                        targetNumbers.Distinct().Count() == targetNumbers.Length &&
                        !targetNumbers.Any(untouchedNumbers.Contains))
                    {
                        var oldNumbers = layout.Seats.ToDictionary(seat => seat.SeatId,
                            seat => seat.ComputerNumber);
                        bindings = bindings.Select((binding, index) => binding with
                            { ComputerNumber = targetNumbers[index] }).ToList();
                        renumbered = bindings.Count(binding => oldNumbers.GetValueOrDefault(binding.SeatId) !=
                                                               binding.ComputerNumber);
                    }
                }

                if (bindings.Count > 0)
                {
                    try
                    {
                        layout = _classroomSeatGridStore.AssignVeyonTargets(campus.ProfileId, room.RoomId, bindings);
                        ApplySeatGrid(layout, preserveSeatSwapSelection: true);
                        automaticallyBound = bindings.Count;
                        if (renumbered > 0)
                            SeatMapStatus = $"已按学生姓名核对 Veyon 电脑，并将 {renumbered} 个座位电脑编号同步为主机名编号。";
                        RefreshClassroomEventSeatLocations();
                    }
                    catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException)
                    {
                        // Do not apply a partial Veyon association if the current seat map changed.
                    }
                }
            }
            var automaticStatus = automaticallyBound > 0
                ? $"已按学生姓名匹配 Veyon 显示名，并确认 {automaticallyBound} 个座位及电脑地址。" : "";
            var unmatchedSeatCount = Math.Max(0, namedSeatCount - automaticallyBound);
            if (unmatchedSeatCount > 0)
                automaticStatus += $"另有 {unmatchedSeatCount} 个姓名座位未能唯一匹配 Veyon 显示名，原关联保持不变。";
            SeatVeyonTargetStatus = choices.Length == 0
                ? $"Veyon 地点“{room.DisplayName}”没有可唯一关联的电脑（需要明确的电脑名称和主机地址）；{pendingCount} 台仍待处理。"
                : $"已读取“{room.DisplayName}”的 {choices.Length} 台可唯一关联电脑。{automaticStatus}" +
                  (pendingCount == 0 ? "" : $"另有 {pendingCount} 台存在重名、重复地址或资料不完整，保留为待处理状态。");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                           UnauthorizedAccessException or InvalidOperationException or
                                           TimeoutException or PlatformNotSupportedException or
                                           System.ComponentModel.Win32Exception)
        {
            SeatVeyonTargetStatus = "读取 Veyon 电脑失败；座位关联没有更改。" + exception.Message;
        }
        finally { IsReadingSeatVeyonTargets = false; }
    }

    public void BindSeatVeyonTarget(int row, int column, Guid objectId)
    {
        if (!CanEditSeatMap) return;
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room || _seatGrid is null)
            return;
        try
        {
            var choice = SeatVeyonTargetChoices.SingleOrDefault(item => item.ObjectId == objectId)
                         ?? throw new InvalidDataException("请先读取当前地点的 Veyon 电脑清单。");
            ApplySeatGrid(_classroomSeatGridStore.AssignVeyonTarget(campus.ProfileId, room.RoomId,
                row, column, choice.HostAddress, choice.ComputerName, choice.ObjectId,
                choice.LocationObjectId));
            var studentName = _seatGrid?.Seats.FirstOrDefault(item => item.Row == row && item.Column == column)
                ?.StudentName?.Trim();
            SeatMapStatus = string.IsNullOrWhiteSpace(studentName)
                ? "座位已关联学生设备；映射已保存在本机。"
                : $"学生“{studentName}”已关联到该座位；设备映射已保存在本机。";
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException)
        {
            SeatMapStatus = "无法关联 Veyon 电脑；原座位表未更改。" + exception.Message;
        }
    }

    public void ClearSeatVeyonTarget(int row, int column)
    {
        if (!CanEditSeatMap) return;
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room || _seatGrid is null)
            return;
        try
        {
            var seat = _seatGrid.Seats.SingleOrDefault(item => item.Row == row && item.Column == column)
                       ?? throw new InvalidDataException("找不到要解除电脑关联的座位。");
            ApplySeatGrid(_classroomSeatGridStore.ClearVeyonTarget(campus.ProfileId, room.RoomId, seat.SeatId));
            SeatMapStatus = "已解除本机座位与 Veyon 电脑的关联；未修改 Veyon 目录。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException)
        {
            SeatMapStatus = "无法解除电脑关联；原座位表未更改。" + exception.Message;
        }
    }

    public void SelectSeatForSwap(int row, int column)
    {
        if (!CanInteractWithSeatMap || !SeatSwapMode || _seatGrid is null) return;
        var seat = _seatGrid.Seats.SingleOrDefault(item => item.Row == row && item.Column == column);
        if (seat is null)
        {
            SeatSwapStatus = "请选择已添加的座位；空白网格区域不是座位。";
            return;
        }
        _preparedSeatSwapPlan = null;
        SeatSwapPlanRows = Array.Empty<string>();
        SeatSwapTargetResults = Array.Empty<string>();
        if (_seatSwapSelection.Remove(seat.SeatId))
        {
            SeatSwapStatus = "已取消该座位选择。";
        }
        else if (_seatSwapSelection.Count >= 2)
        {
            SeatSwapStatus = "已选满两个座位；再次点击已选座位可取消。";
        }
        else if (_seatSwapSelection.Count == 1 && string.IsNullOrWhiteSpace(seat.StudentName) &&
                 _seatGrid.Seats.Where(item => _seatSwapSelection.Contains(item.SeatId))
                     .All(item => string.IsNullOrWhiteSpace(item.StudentName)))
        {
            SeatSwapStatus = "两个座位都是空座位；请至少选择一个有学生姓名的座位。";
        }
        else
        {
            _seatSwapSelection.Add(seat.SeatId);
            if (_seatSwapSelection.Count == 1)
                SeatSwapStatus = string.IsNullOrWhiteSpace(seat.StudentName)
                    ? "已选择一个空座位，请再选择一个有学生姓名的座位。"
                    : "已选择第一个座位；第二个座位可以为空。";
            else
            {
                var selectedSeats = _seatGrid.Seats.Where(item => _seatSwapSelection.Contains(item.SeatId)).ToArray();
                SeatSwapStatus = selectedSeats.Any(item => string.IsNullOrWhiteSpace(item.StudentName))
                    ? "已选好学生和空座位；点“交换所选座位”继续。"
                    : "已选好两个学生座位；点“交换所选座位”继续。";
            }
        }
        Changed(nameof(SeatSwapSelectionSummary));
        Changed(nameof(CanPrepareSeatSwapPlan));
        Changed(nameof(CanSwapSeats));
        Changed(nameof(CanExecuteSeatSwapPlan));
        RefreshSeatMapCells();
    }

    public async Task PrepareSeatSwapPlanAsync()
    {
        if (!CanPrepareSeatSwapPlan || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room || _seatGrid is null) return;
        IsPreparingSeatSwapPlan = true;
        var layout = _seatGrid with { Seats = _seatGrid.Seats.Select(seat => seat with { }).ToArray() };
        var selectedIds = _seatSwapSelection.ToArray();
        SeatSwapStatus = "正在核对所选座位……";
        SeatSwapPlanRows = Array.Empty<string>();
        _preparedSeatSwapPlan = null;
        try
        {
            var first = layout.Seats.SingleOrDefault(seat => seat.SeatId == selectedIds[0])
                        ?? throw new InvalidDataException("找不到第一个所选座位。");
            var second = layout.Seats.SingleOrDefault(seat => seat.SeatId == selectedIds[1])
                         ?? throw new InvalidDataException("找不到第二个所选座位。");
            void VerifySeatSnapshots()
            {
                if (SelectedCampusProfile?.ProfileId != campus.ProfileId ||
                    SelectedRoomProfile?.RoomId != room.RoomId || _seatGrid is null)
                    throw new InvalidOperationException("当前校区或地点已切换；请重新选择座位。");
                var currentFirst = _seatGrid.Seats.SingleOrDefault(seat => seat.SeatId == first.SeatId);
                var currentSecond = _seatGrid.Seats.SingleOrDefault(seat => seat.SeatId == second.SeatId);
                if (currentFirst != first || currentSecond != second)
                    throw new InvalidOperationException("座位资料已变化；请重新选择并生成换座计划。");
                var persistedLayout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId,
                    room.StartNumber);
                if (persistedLayout.Seats.SingleOrDefault(seat => seat.SeatId == first.SeatId) != first ||
                    persistedLayout.Seats.SingleOrDefault(seat => seat.SeatId == second.SeatId) != second)
                    throw new InvalidOperationException("本机座位表已由其他窗口更新；请重新读取座位表后生成计划。");
            }

            VerifySeatSnapshots();
            var firstHasStudent = !string.IsNullOrWhiteSpace(first.StudentName);
            var secondHasStudent = !string.IsNullOrWhiteSpace(second.StudentName);
            if (!firstHasStudent && !secondHasStudent)
                throw new InvalidDataException("至少要有一个座位已填写学生姓名。");
            if (firstHasStudent && secondHasStudent &&
                string.Equals(first.StudentName, second.StudentName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("两个座位上的学生姓名相同，无需换位。");

            static bool HasAnyVeyonBindingData(ClassroomSeatGridSeat seat) =>
                seat.VeyonObjectId is not null || seat.VeyonLocationObjectId is not null ||
                !string.IsNullOrWhiteSpace(seat.VeyonHostAddress) ||
                !string.IsNullOrWhiteSpace(seat.VeyonComputerName);
            static bool HasCompleteVeyonBinding(ClassroomSeatGridSeat seat) =>
                seat.VeyonObjectId is not null && seat.VeyonLocationObjectId is not null &&
                !string.IsNullOrWhiteSpace(seat.VeyonHostAddress) &&
                !string.IsNullOrWhiteSpace(seat.VeyonComputerName);
            static string OccupantLabel(ClassroomSeatGridSeat seat) =>
                string.IsNullOrWhiteSpace(seat.StudentName) ? "空座位" : seat.StudentName;

            var firstHasBinding = HasAnyVeyonBindingData(first);
            var secondHasBinding = HasAnyVeyonBindingData(second);
            var firstHasCompleteBinding = HasCompleteVeyonBinding(first);
            var secondHasCompleteBinding = HasCompleteVeyonBinding(second);
            if ((firstHasBinding && !firstHasCompleteBinding) ||
                (secondHasBinding && !secondHasCompleteBinding))
                throw new InvalidDataException("所选座位存在不完整的 Veyon 电脑关联；请先修复或解除该关联，再换座。");

            if (!firstHasStudent || !secondHasStudent || !firstHasCompleteBinding ||
                !secondHasCompleteBinding)
            {
                var localRows = new[]
                {
                    $"座位 {first.Row} 行 {first.Column} 列：{OccupantLabel(first)} → {OccupantLabel(second)}",
                    $"座位 {second.Row} 行 {second.Column} 列：{OccupantLabel(second)} → {OccupantLabel(first)}",
                    "仅交换本机座位表中的学生姓名；电脑编号、主机地址和 Veyon 电脑关联保留在原座位。"
                };
                _preparedSeatSwapPlan = new PreparedSeatSwapPlan(campus.ProfileId, room.RoomId,
                    first, second, null, localRows);
                SeatSwapPlanRows = Array.AsReadOnly(localRows);
                SeatSwapStatus = "计划已生成；至少一个座位为空或未关联电脑，因此只调整本机学生座位记录。";
                Changed(nameof(CanExecuteSeatSwapPlan));
                return;
            }

            var snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            VerifySeatSnapshots();

            VeyonNetworkObject ReadBoundComputer(ClassroomSeatGridSeat seat)
            {
                if (seat.VeyonObjectId is not { } objectId ||
                    seat.VeyonLocationObjectId is not { } locationId ||
                    string.IsNullOrWhiteSpace(seat.VeyonHostAddress) ||
                    string.IsNullOrWhiteSpace(seat.VeyonComputerName))
                    throw new InvalidDataException($"座位“{seat.StudentName}”无法唯一匹配到 Veyon 电脑。系统已按电脑名和地址尝试匹配；请检查对应电脑资料，或先在座位上关联电脑。");
                var matches = snapshot.Objects.Where(item => item.ObjectId == objectId &&
                    item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException($"座位“{seat.StudentName}”对应的 Veyon 电脑标识不存在或重复；请先核对关联。");
                var computer = matches[0];
                if (computer.ParentObjectId != locationId ||
                    !computer.Location.Equals(room.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(computer.Name, seat.VeyonComputerName, StringComparison.Ordinal) ||
                    !string.Equals(VeyonHostAddress.NormalizeOverride(computer.Host),
                        seat.VeyonHostAddress, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"座位“{seat.StudentName}”关联的 Veyon 名称、地点或地址已变化；请重新读取并关联电脑。");
                return computer;
            }

            var firstComputer = ReadBoundComputer(first);
            var secondComputer = ReadBoundComputer(second);
            if (firstComputer.ObjectId == secondComputer.ObjectId ||
                string.Equals(firstComputer.Host, secondComputer.Host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("两个座位关联到了同一台 Veyon 电脑；请先修正关联。");

            var firstNewName = second.StudentName;
            var secondNewName = first.StudentName;
            var targetPlans = new List<VeyonComputerRenameTargetPlan>();
            if (!string.Equals(firstComputer.Name, firstNewName, StringComparison.Ordinal))
                targetPlans.Add(new VeyonComputerRenameTargetPlan(firstComputer.ObjectId,
                    firstComputer.ParentObjectId, firstComputer.Name, firstNewName,
                    VeyonHostAddress.NormalizeOverride(firstComputer.Host)));
            if (!string.Equals(secondComputer.Name, secondNewName, StringComparison.Ordinal))
                targetPlans.Add(new VeyonComputerRenameTargetPlan(secondComputer.ObjectId,
                    secondComputer.ParentObjectId, secondComputer.Name, secondNewName,
                    VeyonHostAddress.NormalizeOverride(secondComputer.Host)));
            if (targetPlans.Count == 0)
                throw new InvalidDataException("交换后的学生姓名与当前 Veyon 名称相同；没有改名需要执行。");

            var targetIds = targetPlans.Select(target => target.ObjectId).ToHashSet();
            var targetSeatIds = new HashSet<Guid> { first.SeatId, second.SeatId };
            foreach (var target in targetPlans)
            {
                if (snapshot.Objects.Any(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) &&
                        !targetIds.Contains(item.ObjectId) &&
                        string.Equals(item.Name, target.NewName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"新名称“{target.NewName}”已被其他 Veyon 电脑使用；请先处理重名。");
                if (layout.Seats.Any(seat => !targetSeatIds.Contains(seat.SeatId) &&
                        string.Equals(seat.VeyonComputerName, target.NewName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"新名称“{target.NewName}”仍记录在其他座位关联中；请先核对该座位。");
            }

            var rows = new[]
            {
                $"第 {first.Row} 排第 {first.Column} 列：{first.StudentName} → {firstNewName}",
                $"第 {second.Row} 排第 {second.Column} 列：{second.StudentName} → {secondNewName}",
                "设备关联由教师端核对并保留。"
            };
            _preparedSeatSwapPlan = new PreparedSeatSwapPlan(campus.ProfileId, room.RoomId,
                first, second, new PreparedSeatSwapVeyonPlan(snapshot.ConfigFingerprint, targetPlans.ToArray(),
                    firstComputer.ObjectId, firstComputer.ParentObjectId, firstComputer.Name,
                    VeyonHostAddress.NormalizeOverride(firstComputer.Host), firstNewName,
                    secondComputer.ObjectId, secondComputer.ParentObjectId, secondComputer.Name,
                    VeyonHostAddress.NormalizeOverride(secondComputer.Host), secondNewName), rows);
            SeatSwapPlanRows = Array.AsReadOnly(rows);
            SeatSwapStatus = "电脑关联已核对；请在确认窗口核对学生和保留地址。";
            Changed(nameof(CanExecuteSeatSwapPlan));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException or
                                           TimeoutException or PlatformNotSupportedException or
                                           System.ComponentModel.Win32Exception)
        {
            SeatSwapStatus = "无法生成换座计划；学生姓名和 Veyon 目录没有更改。" + exception.Message;
            Changed(nameof(CanExecuteSeatSwapPlan));
        }
        finally { IsPreparingSeatSwapPlan = false; }
    }

    public async Task ExecuteSeatSwapPlanAsync()
    {
        if (!CanExecuteSeatSwapPlan || _preparedSeatSwapPlan is not { } plan || !TryBeginExclusiveTask()) return;
        var veyonPlan = plan.VeyonPlan;
        SeatSwapStatus = veyonPlan is null
            ? "正在保存本机学生座位调整……"
            : "正在备份 Veyon 配置、执行改名并逐台读回核对……";
        SeatSwapTargetResults = Array.Empty<string>();
        try
        {
            var latestLayout = _classroomSeatGridStore.GetLayout(plan.CampusProfileId, plan.RoomId,
                SelectedRoomProfile?.RoomId == plan.RoomId ? SelectedRoomProfile.StartNumber : 1);
            if (latestLayout.Seats.SingleOrDefault(seat => seat.SeatId == plan.FirstSeatSnapshot.SeatId) !=
                    plan.FirstSeatSnapshot ||
                latestLayout.Seats.SingleOrDefault(seat => seat.SeatId == plan.SecondSeatSnapshot.SeatId) !=
                    plan.SecondSeatSnapshot)
                throw new InvalidOperationException("本机座位表自生成计划后已有变化；没有修改 Veyon。请重新读取并生成计划。");

            if (veyonPlan is null)
            {
                var updatedLocal = _classroomSeatGridStore.CommitStudentPositionSwap(plan.CampusProfileId,
                    plan.RoomId, plan.FirstSeatSnapshot, plan.SecondSeatSnapshot);
                CompleteSeatSwap(updatedLocal);
                SeatSwapStatus = "本机学生座位已保存；电脑编号、主机地址和 Veyon 关联保持在原座位。";
                return;
            }

            var response = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                (requestId, caller) => new PrivilegedWorkerRequest(
                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                    PrivilegedWorkerOperation.RenameVeyonComputers,
                    VeyonConfigFingerprint: veyonPlan.ConfigFingerprint,
                    VeyonComputerRenameTargets: veyonPlan.Targets));
            var targetResults = response.VeyonRenameResults ?? Array.Empty<VeyonComputerRenameTargetResult>();
            var studentNamesByObjectId = new Dictionary<Guid, string>
            {
                [veyonPlan.FirstComputerObjectId] = plan.SecondSeatSnapshot.StudentName.Trim(),
                [veyonPlan.SecondComputerObjectId] = plan.FirstSeatSnapshot.StudentName.Trim()
            };
            SeatSwapTargetResults = Array.AsReadOnly(targetResults.Select(target =>
            {
                var studentName = studentNamesByObjectId.GetValueOrDefault(target.ObjectId, "未填写姓名");
                var status = target.Status switch
                {
                    "succeeded" => "已确认",
                    "unchanged" => "未变更",
                    "rolled-back" => "已回滚",
                    "needs-review" => "待核对",
                    _ => "失败"
                };
                return $"{studentName}：{status}";
            }).ToArray());
            if (!response.Result.Ok || targetResults.Count != veyonPlan.Targets.Length ||
                targetResults.Any(target => target.Status != "succeeded") ||
                !veyonPlan.Targets.All(expected => targetResults.Any(actual => actual.ObjectId == expected.ObjectId)))
            {
                SeatSwapStatus = "设备名称同步未全部确认；教师端尚未提交新的座位姓名。请重新读取地点状态后再处理。";
                SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
                SeatVeyonTargetStatus = "请重新读取当前地点的 Veyon 电脑清单后再处理座位关联。";
                _preparedSeatSwapPlan = null;
                Changed(nameof(CanExecuteSeatSwapPlan));
                return;
            }

            try
            {
                var updated = _classroomSeatGridStore.CommitStudentSwap(plan.CampusProfileId,
                    plan.RoomId, plan.FirstSeatSnapshot.SeatId, plan.SecondSeatSnapshot.SeatId,
                    plan.FirstSeatSnapshot.StudentName, plan.SecondSeatSnapshot.StudentName,
                    veyonPlan.FirstComputerObjectId, veyonPlan.FirstLocationObjectId,
                    veyonPlan.FirstComputerOldName, veyonPlan.FirstComputerHostAddress,
                    veyonPlan.FirstComputerNewName, veyonPlan.SecondComputerObjectId,
                    veyonPlan.SecondLocationObjectId, veyonPlan.SecondComputerOldName,
                    veyonPlan.SecondComputerHostAddress, veyonPlan.SecondComputerNewName);
                CompleteSeatSwap(updated);
                SeatSwapStatus = "换座完成；设备显示名和本机学生座位记录已同步保存。";
                SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
                SeatVeyonTargetStatus = "换座已完成；如需重新关联电脑，请读取最新 Veyon 清单。";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or InvalidOperationException)
            {
                SeatSwapStatus = "设备名称已逐台读回，但本机座位表未能保存；请先核对备份，再同步本机学生记录。";
                SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
                SeatVeyonTargetStatus = "改名结果需要核对；请重新读取 Veyon 电脑清单。";
                _preparedSeatSwapPlan = null;
                Changed(nameof(CanExecuteSeatSwapPlan));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException or
                                           TimeoutException or System.ComponentModel.Win32Exception)
        {
            SeatSwapStatus = veyonPlan is null
                ? "本机座位表没有提交；请重新读取并检查后再试。"
                : "换座结果需要核对；本机学生座位记录没有提交。请重新读取地点状态后再处理。";
            if (veyonPlan is not null)
            {
                SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
                SeatVeyonTargetStatus = "换座结果需要核对；请重新读取 Veyon 电脑清单。";
            }
            _preparedSeatSwapPlan = null;
            Changed(nameof(CanExecuteSeatSwapPlan));
        }
        finally { EndExclusiveTask(); }
    }

    private void CompleteSeatSwap(ClassroomSeatGridLayout updated)
    {
        ApplySeatGrid(updated);
        RefreshClassroomEventSeatLocations();
        _preparedSeatSwapPlan = null;
        _seatSwapSelection.Clear();
        if (_seatSwapMode)
        {
            _seatSwapMode = false;
            Changed(nameof(SeatSwapMode));
        }
        Changed(nameof(SeatSwapSelectionSummary));
        Changed(nameof(CanPrepareSeatSwapPlan));
        Changed(nameof(CanExecuteSeatSwapPlan));
        RefreshSeatMapCells();
    }

    public void PrepareCurrentRoomSeatGrid()
    {
        if (!CanPrepareRoomSeatGrid)
        {
            SeatMapStatus = "请先填写校区和地点名称。";
            return;
        }
        try
        {
            if (!TrySaveCurrentRoomProfile(out var error))
            {
                SeatMapStatus = "无法保存地点信息；座位表尚未建立。" + error;
                return;
            }
            BeginSeatMapEdit();
            SeatMapStatus = "机房已保存在教师电脑；已有电脑编号保持固定，新座位默认待编号。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or FormatException or OverflowException)
        {
            SeatMapStatus = "无法建立座位表；本机资料未更改。" + exception.Message;
        }
    }

    public void AssignMissingSeatComputerNumbers()
    {
        if (!CanAssignMissingSeatComputerNumbers || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room) return;
        try
        {
            var pending = SeatGridNumberPendingCount;
            ApplySeatGrid(_classroomSeatGridStore.AssignNextComputerNumbers(campus.ProfileId, room.RoomId,
                GetFirstComputerNumberAfterCampusMaximum()));
            SeatMapStatus = $"已从现有最大编号后为 {pending} 个待编号学生座位分配电脑编号；已有编号未更改。";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "无法分配新电脑编号；现有编号未更改。" + exception.Message;
        }
    }

    private int GetFirstComputerNumberAfterCampusMaximum()
    {
        var usedNumbers = new HashSet<int>();
        foreach (var campus in CampusProfiles)
        foreach (var room in campus.Rooms)
        {
            usedNumbers.UnionWith(room.GetComputerNumbers());
            var layout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId, room.StartNumber);
            usedNumbers.UnionWith(layout.Seats.Where(seat => seat.ComputerNumber is not null)
                .Select(seat => seat.ComputerNumber!.Value));
        }

        var nextNumber = usedNumbers.DefaultIfEmpty(0).Max() + 1;
        if (nextNumber > MachineNaming.MaximumNumber)
            throw new InvalidDataException($"没有可分配的新电脑编号；当前最大编号为 {MachineNaming.MaximumNumber}。");
        return nextNumber;
    }

    private bool TrySaveCurrentRoomProfile(out string error,
        IReadOnlyList<VeyonNetworkComputer>? configuredComputers = null)
    {
        error = "";
        var campusName = RoomCampusName.Trim();
        var roomName = RoomLocationName.Trim();
        var enteredPrefix = RoomPrefix;
        var enteredStart = RoomStart;
        var enteredHosts = RoomComputerHosts;
        if (campusName.Length == 0 || roomName.Length == 0)
        {
            error = "校区和地点名称不能为空。";
            return false;
        }

        try
        {
            var campus = CampusProfiles.FirstOrDefault(item =>
                string.Equals(item.DisplayName, campusName, StringComparison.OrdinalIgnoreCase));
            var rooms = campus?.Rooms.ToList() ?? [];
            var roomIndex = rooms.FindIndex(item =>
                string.Equals(item.DisplayName, roomName, StringComparison.OrdinalIgnoreCase));
            var isNewRoom = roomIndex < 0;
            TeacherRoomProfile room;
            if (configuredComputers is { } computers)
            {
                if (computers.Count is < 1 or > ClassroomSession.MaximumTargets)
                    throw new InvalidDataException("机房学生电脑数量必须在允许范围内。");
                var numbers = computers.Select(computer => ParseComputerNumber(RoomPrefix, computer.ComputerName))
                    .ToList();
                var hosts = computers.Select(computer => VeyonHostAddress.NormalizeOverride(computer.Host)).ToList();
                room = new TeacherRoomProfile(roomIndex >= 0 ? rooms[roomIndex].RoomId : Guid.NewGuid(),
                    roomName, RoomPrefix.Trim(), numbers.Min(), computers.Count, hosts, numbers);
                if (roomIndex >= 0) rooms[roomIndex] = room;
                else rooms.Add(room);
            }
            else if (roomIndex >= 0)
            {
                // Editing the seating map must not change an already configured Veyon room.
                room = rooms[roomIndex];
            }
            else
            {
                var prefix = "PC-";
                var startNumber = 1;
                try
                {
                    _ = MachineNaming.CreateRange(RoomPrefix, RoomStart, "1");
                    prefix = RoomPrefix.Trim();
                    startNumber = int.Parse(RoomStart, CultureInfo.InvariantCulture);
                }
                catch (Exception exception) when (exception is InvalidDataException or FormatException or OverflowException)
                {
                    // Naming is optional until computers are added; keep the room and empty grid usable.
                }
                room = new TeacherRoomProfile(Guid.NewGuid(), roomName, prefix, startNumber, 0, []);
                rooms.Add(room);
            }
            var updatedCampus = new TeacherCampusProfile(campus?.ProfileId ?? Guid.NewGuid(), campusName, rooms);
            var campuses = CampusProfiles.Where(item => item.ProfileId != updatedCampus.ProfileId)
                .Append(updatedCampus).ToArray();
            if (!PersistCampusDirectory(campuses, "地点信息已保存在教师电脑。"))
            {
                error = CampusDirectoryError;
                return false;
            }

            SelectedCampusProfile = CampusProfiles.FirstOrDefault(item => item.ProfileId == updatedCampus.ProfileId);
            SelectedRoomProfile = RoomProfiles.FirstOrDefault(item => item.RoomId == room.RoomId);
            if (isNewRoom && configuredComputers is null)
            {
                RoomPrefix = enteredPrefix;
                RoomStart = enteredStart;
                RoomComputerHosts = enteredHosts;
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or OverflowException)
        {
            error = exception.Message;
            return false;
        }
    }

    private void LoadSelectedSeatGrid()
    {
        CommitSeatMapEdit();
        IsSeatMapEditing = false;
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room)
        {
            _seatGrid = null;
            _seatRows = ClassroomSeatGridStore.DefaultRows;
            _seatColumns = ClassroomSeatGridStore.DefaultColumns;
            _seatRowsDraft = _seatRows;
            _seatColumnsDraft = _seatColumns;
            SeatMapCells = Array.Empty<TeacherSeatCell>();
            SeatMapStatus = "";
            Changed(nameof(SeatRows));
            Changed(nameof(SeatColumns));
            Changed(nameof(SeatRowsDraft));
            Changed(nameof(SeatColumnsDraft));
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(SeatDimensionRemovalSummary));
            Changed(nameof(SeatGridColumns));
            Changed(nameof(SeatGridSeatCount));
            Changed(nameof(SeatGridNamedCount));
            Changed(nameof(SeatGridNumberPendingCount));
            Changed(nameof(SeatGridSummary));
            Changed(nameof(CanAssignMissingSeatComputerNumbers));
            Changed(nameof(AssignMissingSeatComputerNumbersLabel));
            Changed(nameof(SeatGridTitle));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(CanSwapSeats));
            NotifyTeacherSetupNavigationStateChanged();
            return;
        }

        try
        {
            var layout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId, room.StartNumber);
            var pendingNamedSeats = layout.Seats.Count(seat => seat.ComputerNumber is null &&
                !string.IsNullOrWhiteSpace(seat.StudentName));
            if (pendingNamedSeats > 0)
            {
                try
                {
                    layout = _classroomSeatGridStore.AssignNextComputerNumbers(campus.ProfileId, room.RoomId,
                        GetFirstComputerNumberAfterCampusMaximum());
                    SeatMapStatus = $"已从现有最大编号后为 {pendingNamedSeats} 个姓名座位分配新电脑编号；请核对后继续。";
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    SeatMapStatus = $"有 {pendingNamedSeats} 个姓名座位缺少电脑编号，自动修复未完成。" + exception.Message;
                }
            }
            else SeatMapStatus = "";
            ApplySeatGrid(layout);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _seatGrid = null;
            SeatMapCells = Array.Empty<TeacherSeatCell>();
            SeatMapStatus = "无法读取座位表；原文件保留，未展示不完整布局。";
            Changed(nameof(HasSeatMap));
            Changed(nameof(SeatGridSeatCount));
            Changed(nameof(SeatGridNamedCount));
            Changed(nameof(SeatGridNumberPendingCount));
            Changed(nameof(SeatGridSummary));
            Changed(nameof(CanAssignMissingSeatComputerNumbers));
            Changed(nameof(AssignMissingSeatComputerNumbersLabel));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(SeatRows));
            Changed(nameof(SeatColumns));
            Changed(nameof(SeatRowsDraft));
            Changed(nameof(SeatColumnsDraft));
            Changed(nameof(SeatGridColumns));
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(SeatDimensionRemovalSummary));
        }
    }

    public void ApplySeatGridDimensions(bool removeSeatsOutsideGrid)
    {
        if (!CanApplySeatGridDimensions || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room || _seatGrid is null) return;
        var rows = decimal.ToInt32(_seatRowsDraft);
        var columns = decimal.ToInt32(_seatColumnsDraft);
        var removedCount = _seatGrid.Seats.Count(seat => seat.Row > rows || seat.Column > columns);
        if (removedCount > 0 && !removeSeatsOutsideGrid)
        {
            SeatMapStatus = "请确认删除座位图范围外的座位后再应用。";
            return;
        }
        try
        {
            ApplySeatGrid(_classroomSeatGridStore.SetDimensions(campus.ProfileId, room.RoomId,
                rows, columns, removeSeatsOutsideGrid));
            SeatMapStatus = removedCount > 0
                ? $"座位表已调整为 {rows} 行、{columns} 列；已删除范围外的 {removedCount} 个本机座位记录，Veyon 目录未更改。"
                : $"座位表已调整为 {rows} 行、{columns} 列。";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "无法调整座位表；原布局未更改。" + exception.Message;
            SeatRowsDraft = _seatRows;
            SeatColumnsDraft = _seatColumns;
        }
    }

    private void ApplySeatGrid(ClassroomSeatGridLayout layout, bool preserveSeatSwapSelection = false)
    {
        var selectedSeatIds = preserveSeatSwapSelection ? _seatSwapSelection.ToArray() : Array.Empty<Guid>();
        _preparedSeatSwapPlan = null;
        _seatSwapSelection.Clear();
        SeatSwapPlanRows = Array.Empty<string>();
        Changed(nameof(CanPrepareSeatSwapPlan));
        Changed(nameof(CanExecuteSeatSwapPlan));
        Changed(nameof(SeatSwapSelectionSummary));
        _seatGrid = layout with { Seats = layout.Seats.Select(seat => seat with { }).ToArray() };
        if (preserveSeatSwapSelection)
            foreach (var seatId in selectedSeatIds)
                if (_seatGrid.Seats.Any(seat => seat.SeatId == seatId))
                    _seatSwapSelection.Add(seatId);
        _seatRows = layout.Rows;
        _seatColumns = layout.Columns;
        _seatRowsDraft = layout.Rows;
        _seatColumnsDraft = layout.Columns;
        Changed(nameof(SeatRows));
        Changed(nameof(SeatColumns));
        Changed(nameof(SeatRowsDraft));
        Changed(nameof(SeatColumnsDraft));
        Changed(nameof(CanApplySeatGridDimensions));
        Changed(nameof(SeatDimensionRemovalSummary));
        Changed(nameof(SeatGridColumns));
        Changed(nameof(HasSeatMap));
        Changed(nameof(SeatGridSeatCount));
        Changed(nameof(SeatGridNamedCount));
        Changed(nameof(SeatGridNumberPendingCount));
        Changed(nameof(SeatGridSummary));
        Changed(nameof(CanAssignMissingSeatComputerNumbers));
        Changed(nameof(AssignMissingSeatComputerNumbersLabel));
        Changed(nameof(RoomSummary));
        Changed(nameof(SeatGridTitle));
        Changed(nameof(CanAddRoomToVeyon));
        Changed(nameof(CanApplySeatGridDimensions));
        Changed(nameof(SeatDimensionRemovalSummary));
        NotifyTeacherSetupNavigationStateChanged();
        Changed(nameof(CanSwapSeats));
        Changed(nameof(CanPrepareSeatSwapPlan));
        Changed(nameof(CanStartRoomCreation));
        RebuildWebsitePolicyTargetChoices();
        RefreshSeatMapCells();
    }

    private void RefreshSeatMapCells()
    {
        if (_seatGrid is null)
        {
            SeatMapCells = Array.Empty<TeacherSeatCell>();
            return;
        }
        SeatMapCells = ClassroomSeatGridStore.GetCells(_seatGrid).Select(cell =>
        {
            var seat = cell.IsSeat
                ? _seatGrid.Seats.SingleOrDefault(item => item.Row == cell.Row && item.Column == cell.Column)
                : null;
            var computerLabel = !string.IsNullOrWhiteSpace(cell.VeyonHostAddress)
                ? cell.VeyonHostAddress
                : cell.SeatNumber is { } number && SelectedRoomProfile is { } room
                    ? MachineNaming.CreateName(room.Prefix, number.ToString(CultureInfo.InvariantCulture))
                    : "";
            return new TeacherSeatCell(cell.Row, cell.Column, cell.SeatNumber, cell.StudentName ?? "",
                computerLabel, cell.IsSeat,
                cell.VeyonObjectId is not null || !string.IsNullOrWhiteSpace(cell.VeyonHostAddress),
                seat is not null && _seatSwapSelection.Contains(seat.SeatId),
                cell.VeyonObjectId, cell.VeyonHostAddress ?? "",
                CanInteractWithSeatMap && (IsSeatAddRemoveMode || cell.IsSeat),
                IsSeatAddRemoveMode,
                IsSeatMapEditing && seat is not null && !_seatMapEditOriginalSeatIds.Contains(seat.SeatId),
                cell.VeyonComputerName ?? "");
        }).ToArray();
    }

    private IReadOnlyDictionary<string, string> ReadActiveSeatLabels(Guid sessionId)
    {
        if (_activeClassroomSession is not { } active || active.SessionId != sessionId)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var targets = active.Targets.Select(target => target.DeviceLabel).ToArray();
            var layout = _classroomSeatGridStore.GetLayout(active.Room.CampusProfileId, active.Room.RoomId,
                active.Room.StartNumber);
            var targetByComputerNumber = (active.Room.ComputerNumbers ??
                    Enumerable.Range(active.Room.StartNumber, active.Room.ComputerCount).ToArray())
                .Select((number, index) => (number, index))
                .Where(item => item.index < targets.Length)
                .ToDictionary(item => item.number, item => targets[item.index]);
            return ClassroomSeatGridStore.GetLocations(layout)
                .Where(location => location.SeatNumber is { } number && targetByComputerNumber.ContainsKey(number))
                .ToDictionary(location => targetByComputerNumber[location.SeatNumber!.Value],
                    location => $"第 {location.Row} 排 · 第 {location.Column} 列", StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (SelectedRoomProfile?.RoomId == active.Room.RoomId)
                SeatMapStatus = "无法读取座位表；原文件保留，课堂消息按学生姓名显示。";
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal IReadOnlyList<VeyonNetworkLocation> AddStudentNamesToVeyonLocations(
        IReadOnlyList<VeyonNetworkLocation> locations)
    {
        var namesByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var namesByComputer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousComputers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var campus in CampusProfiles)
        foreach (var room in campus.Rooms)
        {
            ClassroomSeatGridLayout layout;
            try { layout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId, room.StartNumber); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            { continue; }
            IReadOnlyList<int> roomNumbers;
            IReadOnlyList<string> roomComputerNames;
            try { roomNumbers = room.GetComputerNumbers(); roomComputerNames = room.GetComputerNames(); }
            catch (InvalidDataException) { roomNumbers = Array.Empty<int>(); roomComputerNames = Array.Empty<string>(); }
            foreach (var seat in layout.Seats.Where(item => !string.IsNullOrWhiteSpace(item.StudentName)))
            {
                var studentName = seat.StudentName.Trim();
                if (!string.IsNullOrWhiteSpace(seat.VeyonHostAddress))
                    AddUnambiguousStudentName(namesByHost, ambiguousHosts, seat.VeyonHostAddress, studentName);
                if (!string.IsNullOrWhiteSpace(seat.VeyonComputerName))
                    AddUnambiguousStudentName(namesByComputer, ambiguousComputers,
                        seat.VeyonComputerName, studentName);
                if (seat.ComputerNumber is not { } number) continue;
                var seatIndex = -1;
                for (var candidate = 0; candidate < roomNumbers.Count; candidate++)
                    if (roomNumbers[candidate] == number) { seatIndex = candidate; break; }
                if (seatIndex < 0) continue;
                if (seatIndex < roomComputerNames.Count)
                    AddUnambiguousStudentName(namesByComputer, ambiguousComputers,
                        roomComputerNames[seatIndex], studentName);
                if (room.HostOverrides is { } hosts && seatIndex < hosts.Count &&
                    !string.IsNullOrWhiteSpace(hosts[seatIndex]))
                    AddUnambiguousStudentName(namesByHost, ambiguousHosts, hosts[seatIndex], studentName);
            }
        }

        return locations.Select(location =>
        {
            var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in location.TargetDisplayNames ?? new Dictionary<string, string>())
                displayNames[pair.Key] = pair.Value;
            foreach (var target in location.Targets)
            {
                var targetKey = NormalizeTargetKey(target);
                if (!namesByHost.TryGetValue(targetKey, out var studentName) &&
                    !ambiguousHosts.Contains(targetKey) &&
                    displayNames.TryGetValue(target, out var computerName))
                    namesByComputer.TryGetValue(NormalizeTargetKey(computerName), out studentName);
                displayNames[target] = string.IsNullOrWhiteSpace(studentName) ? "未填写姓名" : studentName;
            }
            return location with { TargetDisplayNames = displayNames };
        }).ToArray();
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
            InvalidateDeviceHealthSnapshot("校区发生变化");
            CanReplaceWebsiteSigningKey = false;
            Changed();
            Changed(nameof(ClassroomQuickControlPresetSummary));
            Changed(nameof(CampusSetupHint));
            Changed(nameof(PublishCampusName));
            Changed(nameof(SelectedWebsiteSigningCampus));
            Changed(nameof(CanPublishStudentPackage));
            PackagePublishResult = "";
            PackagePublisherError = "";
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanReadWebsitePolicyStatus));
            Changed(nameof(CanReadDeviceHealth));
            NotifyDeviceHealthBatchProperties();
            Changed(nameof(CanMigrateEdgeRealtime));
            Changed(nameof(CanRestoreEdgeLegacy));
            Changed(nameof(CanPushStudentSystemPolicy));
            Changed(nameof(CanDisableStudentSystemPolicy));
            Changed(nameof(CanTrustStudentAgentIdentities));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanDeployStudentUpdate));
            Changed(nameof(CanReadApplicationInventory));
            Changed(nameof(ClassroomTargetSummary));
            _studentAccountChoices = [];
            Changed(nameof(StudentAccountChoices));
            Changed(nameof(HasStudentAccountChoices));
            Changed(nameof(StudentAccountSelectionSummary));
            Changed(nameof(SelectedApplicationSummary));
            ApplicationCatalogAllTargetsRead = false;
            if (_allApplicationCatalogChoices.Count > 0 || HasApplicationCatalogStatus)
            {
                SetApplicationCatalogChoices(Array.Empty<ApplicationCatalogProductChoice>());
                ApplicationCatalogFailureDetails = "";
                ApplicationCatalogStatus = "校区已变化，请重新读取软件目录。";
            }
            if (!UseManualApplicationInputs) ApplicationRules = "";
            if (_allApplicationInventoryChoices.Count > 0 || HasApplicationInventoryStatus)
            {
                _allApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
                ApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
                ApplicationInventoryFailureDetails = "";
                ApplicationInventoryStatus = "校区已变化，请重新读取软件。";
            }
            NotifyApplicationPolicyInputs();
        }
    }
    public string CampusSetupHint => WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal)
        ? $"当前值来自这台电脑保存的校区资料（{CampusId}）；更新会保留它。"
        : "填写校区名称；这台电脑保存的校区资料会在更新后保留。";
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
            RebuildWebsitePolicyTargetChoices();
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
            Changed(nameof(CanReadWebsitePolicyStatus));
            Changed(nameof(CanMigrateEdgeRealtime));
            Changed(nameof(CanRestoreEdgeLegacy));
            Changed(nameof(CanPushStudentSystemPolicy));
            Changed(nameof(CanDisableStudentSystemPolicy));
            Changed(nameof(CanTrustStudentAgentIdentities));
            Changed(nameof(CanDeployStudentUpdate));
            Changed(nameof(CanReadApplicationInventory));
            _studentAccountChoices = [];
            Changed(nameof(StudentAccountChoices));
            Changed(nameof(HasStudentAccountChoices));
            Changed(nameof(StudentAccountSelectionSummary));
            Changed(nameof(SelectedApplicationSummary));
            ApplicationCatalogAllTargetsRead = false;
            if (_allApplicationCatalogChoices.Count > 0 || HasApplicationCatalogStatus)
            {
                SetApplicationCatalogChoices(Array.Empty<ApplicationCatalogProductChoice>());
                ApplicationCatalogFailureDetails = "";
                ApplicationCatalogStatus = "电脑名单已变化，请重新读取软件目录。";
            }
            if (!UseManualApplicationInputs) ApplicationRules = "";
            if (_allApplicationInventoryChoices.Count > 0 || HasApplicationInventoryStatus)
            {
                _allApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
                ApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
                ApplicationInventoryFailureDetails = "";
                ApplicationInventoryStatus = "电脑名单已变化，请重新读取软件。";
            }
            NotifyApplicationPolicyInputs();
        }
    }
    public IReadOnlyList<TeacherPolicyTargetChoice> WebsitePolicyTargetChoices
    {
        get => _websitePolicyTargetChoices;
        private set
        {
            var previousSelection = _websitePolicyTargetChoices.Where(item => item.IsSelected)
                .Select(item => NormalizeTargetKey(item.Target))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextSelection = value.Where(item => item.IsSelected)
                .Select(item => NormalizeTargetKey(item.Target))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!previousSelection.SetEquals(nextSelection))
                InvalidateDeviceHealthSnapshot("设备名单或勾选范围发生变化");
            foreach (var choice in _websitePolicyTargetChoices)
                choice.PropertyChanged -= WebsitePolicyTargetChoiceChanged;
            _websitePolicyTargetChoices = value;
            foreach (var choice in value)
                choice.PropertyChanged += WebsitePolicyTargetChoiceChanged;
            UpdateRuntimeSummaryTargetSelection();
            foreach (var choice in value)
            {
                var row = _deviceHealthRows.FirstOrDefault(item =>
                    NormalizeTargetKey(item.Target) == NormalizeTargetKey(choice.Target));
                row?.SetStudentName(choice.StudentName);
            }
            ReconcileDeviceHealthRows(CampusId.Trim(), value.Where(item => item.IsSelected)
                .Select(item => item.Target).ToArray());
            RefreshDeviceHealthView();
            RefreshRuntimeSummaryCounts();
            Changed();
            Changed(nameof(WebsitePolicyTargetSelectionSummary));
            Changed(nameof(ClassroomTargetSummary));
            Changed(nameof(CanReadDeviceHealth));
            NotifyDeviceHealthBatchProperties();
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanReadWebsitePolicyStatus));
            Changed(nameof(CanTrustStudentAgentIdentities));
            Changed(nameof(CanMigrateEdgeRealtime));
            Changed(nameof(CanRestoreEdgeLegacy));
            Changed(nameof(WebsitePolicyConfirmationSummary));
            Changed(nameof(WebsitePolicyDisableConfirmationSummary));
        }
    }
    public string WebsitePolicyTargetSelectionSummary => WebsitePolicyTargetChoices.Count == 0
        ? "请先从 Veyon 读取电脑名单。"
        : $"已选择 {WebsitePolicyTargetChoices.Count(item => item.IsSelected)}/{WebsitePolicyTargetChoices.Count} 名学生";
    public IReadOnlyList<string> SelectedWebsitePolicyStudentNames => WebsitePolicyTargetChoices
        .Where(item => item.IsSelected).Select(item => item.StudentName).ToArray();
    public string WebsitePolicyConfirmationSummary => BuildWebsitePolicyTargetSummary(includePolicy: true);
    public string WebsitePolicyDisableConfirmationSummary => BuildWebsitePolicyTargetSummary(includePolicy: false);

    private string BuildWebsitePolicyTargetSummary(bool includePolicy)
    {
        var selected = WebsitePolicyTargetChoices.Where(item => item.IsSelected).ToArray();
        var lines = new List<string> { "将对以下学生电脑操作：" };
        lines.AddRange(selected.Select(item => $"• {item.StudentName}"));
        if (!includePolicy) return string.Join(Environment.NewLine, lines);
        var cutoff = GetWebsiteDeadlineUtc(DateTimeOffset.UtcNow).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        lines.Add("要阻止的网站：" + FormatWebsiteDomainList(WebsiteBlockDomains));
        lines.Add("允许的网站：" + FormatWebsiteDomainList(WebsiteAllowDomains));
        lines.Add("截止时间：" + cutoff);
        return string.Join(Environment.NewLine, lines);
    }

    public string GetWebsitePolicyStudentName(string target) => WebsitePolicyTargetChoices
        .FirstOrDefault(item => string.Equals(NormalizeTargetKey(item.Target), NormalizeTargetKey(target),
            StringComparison.OrdinalIgnoreCase))?.StudentName ??
        "未填写姓名";
    public string GetWebsitePolicyTargetLabel(string target) => WebsitePolicyTargetChoices
        .FirstOrDefault(item => string.Equals(NormalizeTargetKey(item.Target), NormalizeTargetKey(target),
            StringComparison.OrdinalIgnoreCase))?.StudentName ?? GetWebsitePolicyStudentName(target);

    public void SelectAllWebsitePolicyTargets(bool selected)
    {
        foreach (var choice in WebsitePolicyTargetChoices) choice.IsSelected = selected;
    }

    private void RebuildWebsitePolicyTargetChoices()
    {
        var previous = WebsitePolicyTargetChoices.ToDictionary(item => NormalizeTargetKey(item.Target),
            item => item.IsSelected, StringComparer.OrdinalIgnoreCase);
        var targets = WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(target => target.Trim()).Where(target => target.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var studentsByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var studentsByComputer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousComputers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownStudentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in WebsiteLocations)
        foreach (var pair in location.TargetDisplayNames ?? new Dictionary<string, string>())
            AddTargetDisplayName(displayNames, pair.Key, pair.Value);

        foreach (var campus in CampusProfiles)
        {
            foreach (var room in campus.Rooms)
            {
                ClassroomSeatGridLayout layout;
                try { layout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId, room.StartNumber); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                { continue; }
                IReadOnlyList<int> roomNumbers;
                IReadOnlyList<string> roomComputerNames;
                try { roomNumbers = room.GetComputerNumbers(); roomComputerNames = room.GetComputerNames(); }
                catch (InvalidDataException) { roomNumbers = Array.Empty<int>(); roomComputerNames = Array.Empty<string>(); }
                foreach (var seat in layout.Seats.Where(item => !string.IsNullOrWhiteSpace(item.StudentName)))
                {
                    var studentName = seat.StudentName.Trim();
                    knownStudentNames.Add(studentName);
                    if (!string.IsNullOrWhiteSpace(seat.VeyonHostAddress))
                        AddUnambiguousStudentName(studentsByTarget, ambiguousTargets,
                            seat.VeyonHostAddress, studentName);
                    if (!string.IsNullOrWhiteSpace(seat.VeyonComputerName))
                        AddUnambiguousStudentName(studentsByComputer, ambiguousComputers,
                            seat.VeyonComputerName, studentName);
                    if (seat.ComputerNumber is not { } number) continue;
                    var seatIndex = -1;
                    for (var candidate = 0; candidate < roomNumbers.Count; candidate++)
                        if (roomNumbers[candidate] == number) { seatIndex = candidate; break; }
                    if (seatIndex < 0) continue;
                    if (seatIndex < roomComputerNames.Count)
                        AddUnambiguousStudentName(studentsByComputer, ambiguousComputers,
                            roomComputerNames[seatIndex], studentName);
                    if (room.HostOverrides is { } hosts && seatIndex < hosts.Count &&
                        !string.IsNullOrWhiteSpace(hosts[seatIndex]))
                        AddUnambiguousStudentName(studentsByTarget, ambiguousTargets,
                            hosts[seatIndex], studentName);
                }
            }
        }

        var choices = targets.Select(target =>
        {
            var targetKey = NormalizeTargetKey(target);
            displayNames.TryGetValue(targetKey, out var computerName);
            var studentName = studentsByTarget.GetValueOrDefault(targetKey);
            if (string.IsNullOrWhiteSpace(studentName) && !ambiguousTargets.Contains(targetKey) &&
                !string.IsNullOrWhiteSpace(computerName))
                studentsByComputer.TryGetValue(NormalizeTargetKey(computerName), out studentName);
            if (string.IsNullOrWhiteSpace(studentName) && !ambiguousTargets.Contains(targetKey) &&
                !IsIpAddressDisplayName(target))
                studentsByComputer.TryGetValue(targetKey, out studentName);
            if (string.IsNullOrWhiteSpace(studentName) && !string.IsNullOrWhiteSpace(computerName) &&
                knownStudentNames.Contains(computerName.Trim()))
                studentName = computerName.Trim();
            if (string.IsNullOrWhiteSpace(studentName)) studentName = "未填写姓名";
            return new TeacherPolicyTargetChoice(target, studentName,
                !previous.TryGetValue(targetKey, out var selected) || selected);
        }).ToArray();
        WebsitePolicyTargetChoices = Array.AsReadOnly(choices);
    }

    private void WebsitePolicyTargetChoiceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not TeacherPolicyTargetChoice choice || !_websitePolicyTargetChoices.Contains(choice) ||
            args.PropertyName is not (null or nameof(TeacherPolicyTargetChoice.IsSelected))) return;
        UpdateRuntimeSummaryTargetSelection();
        InvalidateDeviceHealthSnapshot("学生电脑勾选发生变化");
        Changed(nameof(WebsitePolicyTargetSelectionSummary));
        Changed(nameof(ClassroomTargetSummary));
        Changed(nameof(CanPushWebsitePolicy));
        Changed(nameof(CanDisableWebsitePolicy));
        Changed(nameof(CanReadWebsitePolicyStatus));
        Changed(nameof(CanReadDeviceHealth));
        NotifyDeviceHealthBatchProperties();
        Changed(nameof(CanTrustStudentAgentIdentities));
        Changed(nameof(CanMigrateEdgeRealtime));
        Changed(nameof(CanRestoreEdgeLegacy));
        Changed(nameof(WebsitePolicyConfirmationSummary));
        Changed(nameof(WebsitePolicyDisableConfirmationSummary));
        Changed(nameof(CanPushApplicationPolicy));
        Changed(nameof(CanDisableApplicationPolicy));
        Changed(nameof(CanPushStudentSystemPolicy));
        Changed(nameof(CanDisableStudentSystemPolicy));
        Changed(nameof(CanReadApplicationPolicyAudit));
        Changed(nameof(CanReadApplicationInventory));
        NotifyApplicationPolicyInputs();
    }

    private void InvalidateDeviceHealthSnapshot(string reason)
    {
        Interlocked.Increment(ref _deviceHealthSelectionRevision);
        CancelDeviceHealthFreshnessUpdate();
        var hadSnapshot = _deviceHealthRows.Count > 0 || _deviceHealthAlerts.Count > 0;
        _deviceHealthAlerts.Clear();
        ReconcileDeviceHealthRows(CampusId.Trim(), GetSelectedWebsitePolicyTargets());
        if (!hadSnapshot && _deviceHealthRows.Count == 0) return;
        RefreshDeviceHealthView();
        DeviceHealthStatus = $"{reason}；已保留本机缓存并标记为待更新，正在重新读取。";
        DeviceHealthBatchStatus = IsRunningDeviceHealthBatch
            ? "校区或设备选择已变化；正在处理的批次仍针对开始时的电脑和校区，不会改投新选择。"
            : "校区或设备选择已变化；请先重新读取健康状态，再开始批量操作。";
        Changed(nameof(HasDeviceHealthAlerts));
        NotifyDeviceHealthBatchProperties();
    }

    private void ReconcileDeviceHealthRows(string campus, IEnumerable<string> selectedTargets)
    {
        var rawTargets = selectedTargets.Where(target => !string.IsNullOrWhiteSpace(target)).ToArray();
        string[] targets;
        try
        {
            targets = rawTargets.Length == 0
                ? Array.Empty<string>()
                : WebsitePolicyTransport.NormalizeTargets(rawTargets).ToArray();
        }
        catch (InvalidDataException)
        {
            targets = Array.Empty<string>();
        }
        if (!string.Equals(_deviceHealthRowsCampus, campus, StringComparison.OrdinalIgnoreCase))
            _deviceHealthRows.Clear();

        var selectedKeys = targets.Select(NormalizeTargetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _deviceHealthRows.Where(row => !selectedKeys.Contains(
                     NormalizeTargetKey(row.Target))).ToArray())
            _deviceHealthRows.Remove(row);

        foreach (var target in targets)
        {
            var key = NormalizeTargetKey(target);
            var existing = _deviceHealthRows.FirstOrDefault(row =>
                NormalizeTargetKey(row.Target) == key);
            var choice = WebsitePolicyTargetChoices.FirstOrDefault(item =>
                NormalizeTargetKey(item.Target) == key);
            var studentName = choice?.StudentName ?? GetWebsitePolicyStudentName(target);
            if (existing is not null)
            {
                existing.SetStudentName(studentName);
                continue;
            }

            var row = new TeacherDeviceHealthRow(target, studentName, "");
            if (_deviceHealthCache.TryGetValue(BuildDeviceHealthCacheKey(campus, target), out var cached))
                row.ApplyLocalCache(cached);
            _deviceHealthRows.Add(row);
        }

        _deviceHealthRowsCampus = campus;
        RefreshDeviceHealthView();
        RefreshRuntimeSummaryCounts();
        UpdateDeviceHealthCacheStatus(campus, targets);
    }

    private static string BuildDeviceHealthCacheKey(string campus, string target) =>
        campus.Trim() + "\0" + NormalizeTargetKey(target);

    private void SaveDeviceHealthCache(string campus)
    {
        if (_deviceHealthCacheError.Length > 0) return;
        var savedUtc = DateTimeOffset.UtcNow;
        foreach (var row in _deviceHealthRows)
        {
            var entry = row.CreateLocalCacheEntry(campus, savedUtc);
            if (entry is null) continue;
            _deviceHealthCache[BuildDeviceHealthCacheKey(campus, row.Target)] = entry;
        }
        try
        {
            _deviceHealthCacheStore.Save(_deviceHealthCache.Values);
            DeviceHealthCacheStatus = "查询摘要已保存在本机；缓存状态会标记为待更新，并自动重新读取。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          System.Text.Json.JsonException or ArgumentException)
        {
            _deviceHealthCacheError = exception.Message;
            DeviceHealthCacheStatus = "本机状态缓存写入失败；实时查询仍可用。";
        }
    }

    private void UpdateDeviceHealthCacheStatus(string campus, IReadOnlyCollection<string> targets)
    {
        if (_deviceHealthCacheError.Length > 0)
        {
            DeviceHealthCacheStatus = "本机状态缓存不可用；实时查询仍可用。";
            return;
        }
        var cachedCount = targets.Count(target => _deviceHealthCache.ContainsKey(
            BuildDeviceHealthCacheKey(campus, target)));
        DeviceHealthCacheStatus = cachedCount > 0
            ? $"本机缓存 {cachedCount}/{targets.Count} 台 · 仅供刷新前查看，状态待 Agent 确认。"
            : "查询摘要仅保存在本机；读取后会自动刷新。";
    }

    private IReadOnlyList<string> GetSelectedWebsitePolicyTargets() => WebsitePolicyTargetChoices
        .Where(item => item.IsSelected).Select(item => item.Target).ToArray();

    private IReadOnlyList<TeacherDeviceHealthRow> GetSelectedHealthRows()
    {
        var selected = GetSelectedWebsitePolicyTargets().Select(NormalizeTargetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _deviceHealthRows.Where(row => selected.Contains(NormalizeTargetKey(row.Target))).ToArray();
    }

    private void NotifyDeviceHealthBatchProperties()
    {
        Changed(nameof(DeviceHealthSelectedCount));
        Changed(nameof(DeviceHealthRecentlyReachableCount));
        Changed(nameof(DeviceHealthVeyonEligibleCount));
        Changed(nameof(DeviceHealthEdgeEligibleCount));
        Changed(nameof(DeviceHealthVeyonSkippedCount));
        Changed(nameof(DeviceHealthEdgeSkippedCount));
        Changed(nameof(DeviceHealthBatchPreview));
        Changed(nameof(CanBatchEnsureVeyonServiceRunning));
        Changed(nameof(CanBatchRefreshLegacyEdgePolicy));
    }

    private void ScheduleNextDeviceHealthFreshnessUpdate()
    {
        CancelDeviceHealthFreshnessUpdate();
        var now = DateTimeOffset.UtcNow;
        var nextExpiry = _deviceHealthRows.Select(row => row.LastCollectedUtc)
            .Where(collected => collected is { } value && value.AddMinutes(2) > now)
            .Select(collected => collected!.Value.AddMinutes(2))
            .DefaultIfEmpty(DateTimeOffset.MinValue).Min();
        if (nextExpiry == DateTimeOffset.MinValue) return;
        var cancellation = new CancellationTokenSource();
        _healthSnapshotFreshnessCancellation = cancellation;
        _ = RefreshDeviceHealthReachabilityAtExpiryAsync(nextExpiry, cancellation.Token);
    }

    private async Task RefreshDeviceHealthReachabilityAtExpiryAsync(DateTimeOffset expiresUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = expiresUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in _deviceHealthRows) row.RefreshHealthFreshness();
            foreach (var alert in _deviceHealthAlerts) alert.RefreshRepairAvailability();
            NotifyDeviceHealthBatchProperties();
            ScheduleNextDeviceHealthFreshnessUpdate();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void CancelDeviceHealthFreshnessUpdate()
    {
        var cancellation = _healthSnapshotFreshnessCancellation;
        _healthSnapshotFreshnessCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private bool IsCampusIdValid()
    {
        try { WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim()); return true; }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException)
        { return false; }
    }
    public string WebsiteBlockDomains
    {
        get => _websiteBlockDomains;
        set
        {
            var next = value ?? "";
            if (_websiteBlockDomains == next) return;
            _websiteBlockDomains = next;
            if (!_loadingWebsitePolicyLists) PersistWebsitePolicyLists();
            Changed(); Changed(nameof(SelectedWebsiteMode)); Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(WebsiteModeGuidance)); Changed(nameof(WebsitePolicyConfirmationSummary));
        }
    }
    public string WebsiteAllowDomains
    {
        get => _websiteAllowDomains;
        set
        {
            var next = value ?? "";
            if (_websiteAllowDomains == next) return;
            _websiteAllowDomains = next;
            if (!_loadingWebsitePolicyLists) PersistWebsitePolicyLists();
            Changed(); Changed(nameof(SelectedWebsiteMode)); Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(WebsiteModeGuidance)); Changed(nameof(WebsitePolicyConfirmationSummary));
        }
    }
    public string WebsitePolicyDraftStatus
    {
        get => _websitePolicyDraftStatus;
        private set { if (_websitePolicyDraftStatus == value) return; _websitePolicyDraftStatus = value; Changed(); }
    }
    public string WebsiteObservedAddress
    {
        get => _websiteObservedAddress;
        set
        {
            var next = value ?? "";
            if (_websiteObservedAddress == next) return;
            _websiteObservedAddress = next;
            PersistWebsiteObservedAddress(next);
            Changed();
            Changed(nameof(CanAddObservedWebsite));
        }
    }
    public string WebsiteEntryStatus { get => _websiteEntryStatus; private set { _websiteEntryStatus = value; Changed(); Changed(nameof(HasWebsiteEntryStatus)); } }
    public bool HasWebsiteEntryStatus => WebsiteEntryStatus.Length > 0;
    private void LoadWebsitePolicyLists(string campusId)
    {
        if (string.IsNullOrWhiteSpace(campusId)) return;
        try
        {
            var draft = WebsitePolicyListDraftStore.Read(campusId.Trim());
            _loadingWebsitePolicyLists = true;
            WebsiteBlockDomains = draft?.BlockedDomains ?? "";
            WebsiteAllowDomains = draft?.AllowedDomains ?? "";
            WebsitePolicyDraftStatus = draft is null
                ? "当前校区还没有保存名单；输入内容后会自动保存在教师电脑，手机可刷新读取。"
                : $"已恢复本机名单（{draft.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}）；修改后自动保存，手机可刷新读取。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          System.Text.Json.JsonException or ArgumentException)
        {
            _loadingWebsitePolicyLists = true;
            WebsiteBlockDomains = "";
            WebsiteAllowDomains = "";
            WebsitePolicyDraftStatus = "无法读取本机名单；原文件未更改。";
            WebsitePolicyError = "网站名单暂不可用：" + exception.Message;
        }
        finally { _loadingWebsitePolicyLists = false; }
    }

    private void PersistWebsitePolicyLists()
    {
        var campusId = CampusId.Trim();
        if (campusId.Length == 0) return;
        try
        {
            WebsitePolicyListDraftStore.Save(campusId, WebsiteBlockDomains, WebsiteAllowDomains);
            WebsitePolicyDraftStatus = "已自动保存在教师电脑；手机刷新后可读取这份当前名单。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          System.Text.Json.JsonException or ArgumentException)
        {
            WebsitePolicyDraftStatus = "无法保存本机名单；请检查教师电脑应用数据目录权限。";
            WebsitePolicyError = "网站名单未能保存：" + exception.Message;
        }
    }

    public void ApplyMobileWebsitePolicyLists(string campusId, string blockedDomains, string allowedDomains)
    {
        if (!string.Equals(CampusId.Trim(), campusId, StringComparison.Ordinal)) return;
        _loadingWebsitePolicyLists = true;
        try
        {
            WebsiteBlockDomains = blockedDomains;
            WebsiteAllowDomains = allowedDomains;
            WebsitePolicyDraftStatus = "手机已保存当前校区名单；可继续在此编辑，修改会自动保存。";
        }
        finally { _loadingWebsitePolicyLists = false; }
    }

    private static string LoadWebsiteObservedAddress()
    {
        try { return File.Exists(WebsiteObservedAddressPath) ? File.ReadAllText(WebsiteObservedAddressPath) : ""; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return ""; }
    }

    private bool PersistWebsiteObservedAddress(string address)
    {
        var temporaryPath = WebsiteObservedAddressPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WebsiteObservedAddressPath)!);
            File.WriteAllText(temporaryPath, address);
            File.Move(temporaryPath, WebsiteObservedAddressPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WebsiteEntryStatus = "无法将详细网址保存在此电脑；请检查当前 Windows 用户的应用数据目录权限。";
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            { _ = cleanupException; }
            return false;
        }
    }

    public int WebsiteModeIndex
    {
        get => _websiteModeIndex;
        set
        {
            _websiteModeIndex = Math.Clamp(value, 0, 2);
            Changed();
            Changed(nameof(SelectedWebsiteMode));
            Changed(nameof(IsWebsiteBlacklistEditorVisible));
            Changed(nameof(IsWebsiteAllowlistEditorVisible));
            Changed(nameof(WebsiteModeGuidance));
            Changed(nameof(CanPushWebsitePolicy));
        }
    }
    public bool IsWebsiteBlacklistEditorVisible => SelectedWebsiteMode is WebsitePolicyMode.Blocklist or WebsitePolicyMode.Combined;
    public bool IsWebsiteAllowlistEditorVisible => SelectedWebsiteMode is WebsitePolicyMode.Allowlist or WebsitePolicyMode.Combined;
    public string WebsiteModeGuidance => SelectedWebsiteMode switch
    {
        WebsitePolicyMode.Blocklist => "只阻止填入的网站；其他网站可访问。",
        WebsitePolicyMode.Allowlist => "只允许填入的网站；其他网站会被阻止。",
        WebsitePolicyMode.Combined => "阻止填入的网站；允许的网站作为例外。",
        _ => "添加要阻止或允许的网站后再应用。"
    };
    public bool CanImportCommonWebsiteBlacklist => !IsExecuting;
    public bool CanImportCommonWebsiteAllowlist => !IsExecuting;
    public bool CanImportCommonCombinedWebsitePolicy => !IsExecuting;
    public bool CanAddObservedWebsite => !IsExecuting && !string.IsNullOrWhiteSpace(WebsiteObservedAddress);
    public WebsitePolicyMode SelectedWebsiteMode
    {
        get
        {
            var hasBlocked = WebsiteBlockDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length > 0;
            var hasAllowed = WebsiteAllowDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length > 0;
            return (hasBlocked, hasAllowed) switch
            {
                (true, true) => WebsitePolicyMode.Combined,
                (true, false) => WebsitePolicyMode.Blocklist,
                (false, true) => WebsitePolicyMode.Allowlist,
                _ => WebsitePolicyMode.Disabled
            };
        }
    }

    public void ImportCommonWebsiteBlacklist()
    {
        if (!CanImportCommonWebsiteBlacklist) return;
        try
        {
            var merged = MergeWebsiteEntries(WebsiteBlockDomains, CommonWebsiteBlacklistDomains);
            WebsiteBlockDomains = merged;
            CompleteWebsiteEditorUpdate("已填入常见娱乐网站；核对后再应用。");
        }
        catch (InvalidDataException exception) { WebsitePolicyError = exception.Message; }
    }

    public void ImportCommonWebsiteAllowlist()
    {
        if (!CanImportCommonWebsiteAllowlist) return;
        try
        {
            var merged = MergeWebsiteEntries(WebsiteAllowDomains, CommonWebsiteAllowlistDomains);
            WebsiteAllowDomains = merged;
            CompleteWebsiteEditorUpdate("已填入常见学习网站；核对后再应用。");
        }
        catch (InvalidDataException exception) { WebsitePolicyError = exception.Message; }
    }

    public void ImportCommonCombinedWebsitePolicy()
    {
        if (!CanImportCommonCombinedWebsitePolicy) return;
        try
        {
            var blocked = MergeWebsiteEntries(WebsiteBlockDomains, CommonWebsiteBlacklistDomains);
            var allowed = MergeWebsiteEntries(WebsiteAllowDomains, CommonWebsiteAllowlistDomains);
            WebsiteBlockDomains = blocked;
            WebsiteAllowDomains = allowed;
            CompleteWebsiteEditorUpdate("已填入娱乐网站和学习网站例外；核对后再应用。");
        }
        catch (InvalidDataException exception) { WebsitePolicyError = exception.Message; }
    }

    public void AddObservedWebsiteToBlacklist() => AddObservedWebsite(toBlacklist: true);
    public void AddObservedWebsiteToAllowlist() => AddObservedWebsite(toBlacklist: false);

    private void AddObservedWebsite(bool toBlacklist)
    {
        if (!CanAddObservedWebsite) return;
        try
        {
            var domain = WebsitePolicyCompiler.NormalizeObservedWebsiteDomain(WebsiteObservedAddress);
            var updated = toBlacklist
                ? MergeWebsiteEntries(WebsiteBlockDomains, [domain])
                : MergeWebsiteEntries(WebsiteAllowDomains, [domain]);
            if (toBlacklist) WebsiteBlockDomains = updated;
            else WebsiteAllowDomains = updated;
            var addressSaved = PersistWebsiteObservedAddress(WebsiteObservedAddress);
            WebsiteEntryStatus = addressSaved
                ? $"已加入{(toBlacklist ? "阻止列表" : "允许列表")}：{domain}。详细网址已保存在此电脑；当前规则匹配该域名及其子域名。"
                : $"已加入{(toBlacklist ? "阻止列表" : "允许列表")}：{domain}；详细网址未能保存在此电脑。";
            ResetWebsitePolicyEditorFeedback();
        }
        catch (InvalidDataException exception) { WebsitePolicyError = "未能添加网址：" + exception.Message; }
    }

    private static string MergeWebsiteEntries(string existing, IEnumerable<string> additions)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in existing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                     .Concat(additions))
        {
            var entry = raw?.Trim() ?? "";
            if (entry.Length == 0) continue;
            string key;
            try { key = WebsitePolicyCompiler.NormalizeDomains([entry]).Single(); }
            catch (InvalidDataException) { key = entry; }
            if (seen.Add(key)) merged.Add(entry);
            if (merged.Count > WebsitePolicyCompiler.MaximumEntries)
                throw new InvalidDataException($"每个列表最多 {WebsitePolicyCompiler.MaximumEntries} 个域名，内容未更改。");
        }
        return string.Join(Environment.NewLine, merged);
    }

    private void CompleteWebsiteEditorUpdate(string status)
    {
        WebsiteEntryStatus = status;
        ResetWebsitePolicyEditorFeedback();
    }

    private void ResetWebsitePolicyEditorFeedback()
    {
        WebsitePolicyError = "";
        WebsitePolicyResult = "";
        WebsitePolicyResultDetails = "";
        ShowWebsitePolicyResultDetails = false;
    }
    public IReadOnlyList<string> WebsiteSigningCampuses { get; private set; } = Array.Empty<string>();
    public string? SelectedWebsiteSigningCampus
    {
        get => WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal) ? CampusId : null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                CampusId = value;
                LoadWebsitePolicyLists(value);
            }
        }
    }

    private void ReadWebsiteSigningCampuses()
    {
        if (!OperatingSystem.IsWindows()) return;
        _websiteSigningKeyCampuses = WebsitePolicySigningKeyStore.ReadCampusIds()
            .Where(campus => !ApplicationPolicySigningKeyStore.IsInternalNamespace(campus) &&
                             !StudentSystemPolicySigningKeyStore.IsInternalNamespace(campus))
            .Distinct(StringComparer.Ordinal).ToArray();
        var candidates = _websiteSigningKeyCampuses.ToList();
        var latest = WebsitePolicyPushHistoryStore.ReadLatest();
        if (latest is not null) candidates.Add(latest.CampusId);
        WebsiteSigningCampuses = candidates.Where(campus => !ApplicationPolicySigningKeyStore.IsInternalNamespace(campus) &&
            !StudentSystemPolicySigningKeyStore.IsInternalNamespace(campus)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Changed(nameof(WebsiteSigningCampuses));
        Changed(nameof(CampusSetupHint));
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
    public int WebsiteDurationIndex
    {
        get => _websiteDurationIndex;
        set
        {
            _websiteDurationIndex = Math.Clamp(value, 0, 5);
            Changed();
            Changed(nameof(WebsiteDurationSummary));
            Changed(nameof(IsWebsiteCustomDuration));
            Changed(nameof(CanPushWebsitePolicy));
        }
    }
    public int WebsiteCustomDurationMinutes
    {
        get => _websiteCustomDurationMinutes;
        set
        {
            var next = Math.Clamp(value, 10, 300);
            if (_websiteCustomDurationMinutes == next) return;
            _websiteCustomDurationMinutes = next;
            Changed();
            Changed(nameof(WebsiteDurationSummary));
            Changed(nameof(CanPushWebsitePolicy));
        }
    }
    public bool IsWebsiteCustomDuration => WebsiteDurationIndex == 4;
    public string WebsiteDurationSummary => WebsiteDurationIndex switch
    {
        0 => "限制时长：45 分钟",
        1 => "限制时长：60 分钟",
        2 => "限制时长：90 分钟",
        3 => "限制时长：120 分钟",
        4 => $"限制时长：{WebsiteCustomDurationMinutes} 分钟",
        _ => "限制时长：不自动到期"
    };
    public DateTimeOffset? WebsiteDeadlineDate
    {
        get => _websiteDeadlineDate;
        set
        {
            if (_websiteDeadlineDate == value) return;
            _websiteDeadlineDate = value is { } date ? new DateTimeOffset(date.Date, date.Offset) : null;
            Changed();
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(WebsitePolicyConfirmationSummary));
        }
    }
    public TimeSpan? WebsiteDeadlineTime
    {
        get => _websiteDeadlineTime;
        set
        {
            if (_websiteDeadlineTime == value) return;
            _websiteDeadlineTime = value;
            Changed();
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(WebsitePolicyConfirmationSummary));
        }
    }
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
            Changed(nameof(CanUseSelectedClassroomLocation));
            Changed(nameof(CanReadSeatVeyonTargets));
            Changed(nameof(CanSyncSeatGridWithVeyon));
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
            Changed(nameof(ClassroomLocationChoices));
            Changed(nameof(HasMultipleClassroomLocations));
            Changed(nameof(ShowClassroomLocationPicker));
            Changed(nameof(CanApplyWebsiteLocation));
            Changed(nameof(CanUseSelectedClassroomLocation));
            Changed(nameof(CanSyncSeatGridWithVeyon));
            RebuildWebsitePolicyTargetChoices();
        }
    }
    public IReadOnlyList<string> WebsiteLocationChoices => WebsiteLocations.Select(location => location.DisplayName).ToArray();
    public bool HasMultipleWebsiteLocations => WebsiteLocations.Count > 1;
    public int ClassroomLocationIndex
    {
        get => _classroomLocationIndex;
        set
        {
            if (_classroomLocationIndex == value) return;
            _classroomLocationIndex = value;
            Changed();
            Changed(nameof(CanUseSelectedClassroomLocation));
            Changed(nameof(CanSyncSeatGridWithVeyon));
        }
    }
    public IReadOnlyList<string> ClassroomLocationChoices => WebsiteLocations.Select(location => location.DisplayName).ToArray();
    public bool HasMultipleClassroomLocations => WebsiteLocations.Count > 1;
    public bool ShowClassroomLocationPicker => HasMultipleClassroomLocations || HasClassroomDirectoryError;
    public string ClassroomDirectoryStatus
    {
        get => _classroomDirectoryStatus;
        private set { _classroomDirectoryStatus = value; Changed(); Changed(nameof(HasClassroomDirectoryStatus)); }
    }
    public bool HasClassroomDirectoryStatus => ClassroomDirectoryStatus.Length > 0;
    public string ClassroomDirectoryError
    {
        get => _classroomDirectoryError;
        private set
        {
            _classroomDirectoryError = value;
            Changed();
            Changed(nameof(HasClassroomDirectoryError));
            Changed(nameof(ShowClassroomLocationPicker));
        }
    }
    public bool HasClassroomDirectoryError => ClassroomDirectoryError.Length > 0;
    public string WebsiteDirectoryStatus { get => _websiteDirectoryStatus; private set { _websiteDirectoryStatus = value; Changed(); Changed(nameof(HasWebsiteDirectoryStatus)); } }
    public bool HasWebsiteDirectoryStatus => WebsiteDirectoryStatus.Length > 0;
    public string WebsiteDirectoryError { get => _websiteDirectoryError; private set { _websiteDirectoryError = value; Changed(); Changed(nameof(HasWebsiteDirectoryError)); } }
    public bool HasWebsiteDirectoryError => WebsiteDirectoryError.Length > 0;
    public string WebsitePolicyResult { get => _websitePolicyResult; private set { _websitePolicyResult = value; Changed(); Changed(nameof(HasWebsitePolicyResult)); Changed(nameof(HasWebsitePolicyHistory)); } }
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
    public bool HasWebsitePolicyHistory => WebsitePolicyHistoryText.Length > 0 && !HasWebsitePolicyResult;
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
            Changed(nameof(StudentSystemPolicyPushGuidance));
        }
    }
    public bool StudentSystemPolicyImpactReviewed
    {
        get => _studentSystemPolicyImpactReviewed;
        set
        {
            if (_studentSystemPolicyImpactReviewed == value) return;
            _studentSystemPolicyImpactReviewed = value;
            Changed();
            Changed(nameof(CanPushStudentSystemPolicy));
            Changed(nameof(StudentSystemPolicyPreview));
            Changed(nameof(StudentSystemPolicyPushGuidance));
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
    public IReadOnlyList<ApplicationCatalogProductChoice> ApplicationCatalogChoices => _applicationCatalogChoices;
    public bool HasApplicationCatalogChoices => ApplicationCatalogChoices.Count > 0;
    public bool ApplicationCatalogAllTargetsRead
    {
        get => _applicationCatalogAllTargetsRead;
        private set
        {
            if (_applicationCatalogAllTargetsRead == value) return;
            _applicationCatalogAllTargetsRead = value;
            Changed();
            Changed(nameof(CanGenerateApplicationCandidates));
            Changed(nameof(ApplicationCatalogSelectionSummary));
        }
    }
    public string ApplicationCatalogSelectionSummary
    {
        get
        {
            if (!ApplicationCatalogAllTargetsRead)
                return "请先成功读取全部所选电脑的软件目录；有设备未读取时，不能生成封禁候选。";
            var selected = _allApplicationCatalogChoices.Where(choice => choice.IsSelected).ToArray();
            if (selected.Length == 0) return "先按产品勾选要精确限制的软件；选中后才会读取发布者信息并计算哈希。";
            var byTarget = SelectedApplicationCatalogCandidatesByTarget();
            var overflow = byTarget.Where(pair => pair.Value.Count > ApplicationCatalogCryptography.MaximumSelectedItems)
                .Select(pair => $"{GetWebsitePolicyTargetLabel(pair.Key)}：{pair.Value.Count} 项")
                .ToArray();
            return overflow.Length == 0
                ? $"已选 {selected.Length} 个产品、{byTarget.Values.Sum(items => items.Count)} 个电脑条目；每台最多 80 项。"
                : "每台最多生成 80 个候选，请减少选择：" + string.Join("；", overflow);
        }
    }
    public string ApplicationCatalogSearch
    {
        get => _applicationCatalogSearch;
        set
        {
            _applicationCatalogSearch = value ?? "";
            Changed();
            FilterApplicationCatalog();
        }
    }
    public string ApplicationCatalogStatus
    {
        get => _applicationCatalogStatus;
        private set { _applicationCatalogStatus = value; Changed(); Changed(nameof(HasApplicationCatalogStatus)); }
    }
    public bool HasApplicationCatalogStatus => ApplicationCatalogStatus.Length > 0;
    public string ApplicationCatalogFailureDetails
    {
        get => _applicationCatalogFailureDetails;
        private set
        {
            if (_applicationCatalogFailureDetails == value) return;
            _applicationCatalogFailureDetails = value;
            Changed();
            Changed(nameof(HasApplicationCatalogFailureDetails));
        }
    }
    public bool HasApplicationCatalogFailureDetails => ApplicationCatalogFailureDetails.Length > 0;
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
    public string ApplicationInventoryFailureDetails
    {
        get => _applicationInventoryFailureDetails;
        private set
        {
            if (_applicationInventoryFailureDetails == value) return;
            _applicationInventoryFailureDetails = value;
            Changed();
            Changed(nameof(HasApplicationInventoryFailureDetails));
        }
    }
    public bool HasApplicationInventoryFailureDetails => ApplicationInventoryFailureDetails.Length > 0;
    public string StudentAccountsStatus
    {
        get => _studentAccountsStatus;
        private set { _studentAccountsStatus = value; Changed(); Changed(nameof(HasStudentAccountsStatus)); }
    }
    public bool HasStudentAccountsStatus => StudentAccountsStatus.Length > 0;
    private IReadOnlyList<StudentAccountChoice> _studentAccountChoices = [];
    private bool _useManualApplicationInputs;
    public IReadOnlyList<StudentAccountChoice> StudentAccountChoices => _studentAccountChoices;
    public bool HasStudentAccountChoices => _studentAccountChoices.Count > 0;
    public string StudentAccountSelectionSummary
    {
        get
        {
            if (_studentAccountChoices.Count == 0) return "尚未读取账户。";
            var selected = _studentAccountChoices.Where(choice => choice.IsSelected).ToArray();
            var coveredTargets = selected.Select(choice => choice.Target)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int targetCount;
            try
            {
                targetCount = WebsitePolicyTransport.NormalizeTargets(
                    WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Count;
            }
            catch (InvalidDataException)
            {
                return "电脑名单有误，无法确认账户覆盖范围。";
            }
            return $"已选 {selected.Length} 个账户，覆盖 {coveredTargets}/{targetCount} 台目标电脑。每台电脑至少保留一个账户。";
        }
    }
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
        StudentAccountsStatus = "正在读取学生账户（只读）……";
        _studentAccountChoices = [];
        Changed(nameof(StudentAccountChoices));
        Changed(nameof(HasStudentAccountChoices));
        Changed(nameof(StudentAccountSelectionSummary));
        NotifyApplicationPolicyInputs();
        try
        {
            var campus = CampusId.Trim();
            using var key = ApplicationPolicySigningKeyStore.Open(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var displayNamesTask = ResolveTargetDisplayNamesAsync(targets);
            var results = await ApplicationPolicyTransport.ReadInventoryAsync(targets,
                campus, key.PrivateKey, accountsOnly: true);
            var displayNames = await displayNamesTask;
            if (campus != CampusId.Trim() || !targets.SequenceEqual(WebsitePolicyTransport.NormalizeTargets(
                    WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)), StringComparer.OrdinalIgnoreCase))
            { StudentAccountsStatus = "校区或电脑名单已变化，请重新读取账户。"; return; }
            SetStudentAccountChoices(results.Where(r => r.Succeeded).SelectMany(r =>
                (r.StudentAccounts ?? []).Select(a => new StudentAccountChoice(r.Target, a,
                    GetTargetDisplayName(r.Target, displayNames)))).ToArray());
            StudentAccountsStatus = string.Join(Environment.NewLine, results.Select(r => r.Succeeded
                    ? $"{GetTargetDisplayName(r.Target, displayNames)}：{r.StudentAccounts?.Count ?? 0} 个普通账户"
                    : $"{GetTargetDisplayName(r.Target, displayNames)}：读取失败 — {r.Detail}"));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException or CryptographicException or PlatformNotSupportedException)
        { StudentAccountsStatus = "账户读取失败；没有修改学生电脑：" + e.Message; }
        finally { NotifyApplicationPolicyInputs(); EndExclusiveTask(); }
    }

    internal void SetStudentAccountChoices(IReadOnlyList<StudentAccountChoice> choices)
    {
        _studentAccountChoices = choices
            .OrderBy(choice => choice.DisplayTarget, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Account.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var choice in _studentAccountChoices) choice.PropertyChanged += (_, _) =>
        {
            Changed(nameof(StudentAccountSelectionSummary));
            NotifyApplicationPolicyInputs();
        };
        Changed(nameof(StudentAccountChoices));
        Changed(nameof(HasStudentAccountChoices));
        Changed(nameof(StudentAccountSelectionSummary));
        NotifyApplicationPolicyInputs();
    }

    public void SetStudentAccountSelection(bool selected)
    {
        foreach (var choice in _studentAccountChoices) choice.IsSelected = selected;
    }

    internal void SetApplicationInventoryChoices(IReadOnlyList<ApplicationInventoryChoice> choices)
    {
        _allApplicationInventoryChoices = choices;
        foreach (var choice in choices) choice.PropertyChanged += (_, _) => SyncSelectedApplicationRules();
        SyncSelectedApplicationRules();
        FilterApplicationInventory();
    }

    internal void SetApplicationCatalogChoices(IReadOnlyList<ApplicationCatalogProductChoice> choices)
    {
        _allApplicationCatalogChoices = choices;
        foreach (var choice in choices)
            choice.PropertyChanged += (_, _) =>
            {
                Changed(nameof(ApplicationCatalogSelectionSummary));
                Changed(nameof(CanGenerateApplicationCandidates));
            };
        FilterApplicationCatalog();
    }

    private Dictionary<string, IReadOnlyList<string>> SelectedApplicationCatalogCandidatesByTarget()
    {
        return _allApplicationCatalogChoices.Where(choice => choice.IsSelected)
            .SelectMany(choice => choice.Candidates)
            .GroupBy(candidate => candidate.Target, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<string>)Array.AsReadOnly(group.Select(candidate => candidate.Id)
                    .Distinct(StringComparer.Ordinal).ToArray()), StringComparer.OrdinalIgnoreCase);
    }

    private void FilterApplicationCatalog()
    {
        var query = NormalizeApplicationIdentity(ApplicationCatalogSearch.Trim());
        _applicationCatalogChoices = query.Length == 0
            ? _allApplicationCatalogChoices
            : _allApplicationCatalogChoices.Where(choice =>
                NormalizeApplicationIdentity(choice.ProductName + " " + choice.CompanyName + " " +
                    choice.DisplayName + " " + choice.Details).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Changed(nameof(ApplicationCatalogChoices));
        Changed(nameof(HasApplicationCatalogChoices));
        Changed(nameof(ApplicationCatalogSelectionSummary));
        Changed(nameof(CanGenerateApplicationCandidates));
    }

    private void SyncSelectedApplicationRules()
    {
        if (!UseManualApplicationInputs)
            ApplicationRules = string.Join(Environment.NewLine, _allApplicationInventoryChoices
                .Where(c => c.IsSelected && c.CanSelect).Select(c => c.RuleLine!).Distinct(StringComparer.Ordinal));
        Changed(nameof(CanAddSelectedApplicationRules));
        Changed(nameof(SelectedApplicationSummary));
    }

    internal Dictionary<string, string[]> StudentSidsByTarget(IReadOnlyList<string> targets, bool disabled,
        bool useManualInputs = true)
    {
        if (disabled) return targets.ToDictionary(t => t, _ => Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (useManualInputs && UseManualApplicationInputs)
            return targets.ToDictionary(t => t, _ => ParseStudentSids(), StringComparer.OrdinalIgnoreCase);
        var map = targets.ToDictionary(t => t, t => _studentAccountChoices.Where(c => c.IsSelected &&
            string.Equals(c.Target, t, StringComparison.OrdinalIgnoreCase)).Select(c => c.Account.Sid)
            .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
        if (map.Any(pair => pair.Value.Length == 0))
            throw new InvalidDataException("请先为每台目标电脑读取并选择学生账户。");
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
        private set { _applicationPolicyResult = value; Changed(); Changed(nameof(HasApplicationPolicyResult)); Changed(nameof(HasApplicationPolicyHistory)); }
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
    public bool HasApplicationPolicyHistory => ApplicationPolicyHistoryText.Length > 0 && !HasApplicationPolicyResult;
    public string ApplicationAuditResult
    {
        get => _applicationAuditResult;
        private set { _applicationAuditResult = value; Changed(); Changed(nameof(HasApplicationAuditResult)); }
    }
    public bool HasApplicationAuditResult => ApplicationAuditResult.Length > 0;
    public string InstallerStatus
    {
        get => _installerStatus;
        private set
        {
            _installerStatus = value;
            Changed();
            Changed(nameof(HasInstallerStatus));
        }
    }
    public bool HasInstallerStatus => InstallerStatus.Length > 0;
    public string TeacherInstallResult { get => _teacherInstallResult; private set { _teacherInstallResult = value; Changed(); Changed(nameof(HasTeacherInstallResult)); } }
    public bool HasTeacherInstallResult => TeacherInstallResult.Length > 0;
    public string TeacherInstallIssue { get => _teacherInstallIssue; private set { _teacherInstallIssue = value; Changed(); Changed(nameof(HasTeacherInstallIssue)); } }
    public bool HasTeacherInstallIssue => TeacherInstallIssue.Length > 0;
    public string PackageOutput
    {
        get => _packageOutput;
        private set
        {
            if (_packageOutput == value) return;
            _packageOutput = value;
            Changed();
            Changed(nameof(HasPackageOutput));
            NotifyTeacherSetupNavigationStateChanged();
        }
    }
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
        ? $"{RoomLocationName.Trim()} · 共 {RoomNames.Count} 台 · 座位 {GetCurrentBuilderSeatCount()} 个 · 姓名 {GetNamedStudentCount()} 个"
        : "添加座位后自动生成电脑清单";
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
            Changed(nameof(CanPrepareRoomSeatGrid));
            Changed(nameof(CanStartRoomCreation));
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
        if (page is not ("classroom" or "lesson" or "updates" or "health" or "rooms" or "setup") || _selectedPage == page) return;
        _selectedPage = page;
        Changed(nameof(IsClassroomPage)); Changed(nameof(IsClassroomExperiencePage));
        Changed(nameof(IsClassroomWorkspacePage)); Changed(nameof(IsUpdatesPage)); Changed(nameof(IsHealthCenterPage));
        Changed(nameof(IsRoomPage)); Changed(nameof(IsSetupPage));
        Changed(nameof(IsTeacherSetupPackageStepVisible));
        Changed(nameof(ClassroomQuickControlPresetSummary));
    }

    public void GenerateRoomPreview()
    {
        InvalidateRoomReview();
        RoomNames = Array.Empty<string>();
        RoomPreviewRows = Array.Empty<string>();
        RoomError = "";
        UpdateRoomPreviewRows();
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
                RoomConflictStatus = $"电脑目录已有 {preview.SkippedComputers.Count} 台匹配项；保留现有记录，并在完成后自动核对座位关联。";
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
        CampusDirectoryStatus = "填写校区名称后保存。";
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
        if (PersistCampusDirectory(campuses, "校区模板已保存。"))
            SelectedCampusProfile = CampusProfiles.FirstOrDefault(item => item.ProfileId == profile.ProfileId);
    }

    public void DeleteSelectedCampusProfile()
    {
        if (SelectedCampusProfile is not { } selected) return;
        var campuses = CampusProfiles.Where(item => item.ProfileId != selected.ProfileId).ToArray();
        if (PersistCampusDirectory(campuses, "校区模板已删除。"))
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
            CampusDirectoryError = "请先选择或保存一个校区模板。";
            return;
        }
        SelectedRoomProfile = null;
        RoomLocationName = SelectedCampusProfile.DisplayName;
        CampusDirectoryStatus = "机房名称已默认使用当前配置包名称；创建座位图后可直接添加学生座位。";
        CampusDirectoryError = "";
    }

    public void SaveRoomProfile()
    {
        if (SelectedCampusProfile is not { } campus)
        {
            CampusDirectoryError = "请先选择或保存一个校区模板。";
            return;
        }
        var rooms = campus.Rooms.ToList();
        var selectedId = SelectedRoomProfile?.RoomId;
        var index = selectedId is null ? -1 : rooms.FindIndex(item => item.RoomId == selectedId);
        try
        {
            var range = MachineNaming.CreateRange(RoomProfilePrefix, RoomProfileStart, RoomProfileCount);
            var hostOverrides = ParseHostOverrides(RoomProfileHostOverrides, range.Count);
            var current = index >= 0 ? rooms[index] : null;
            TeacherRoomProfile room;
            if (current?.ComputerNumbers is { } fixedNumbers)
            {
                if (!string.Equals(RoomProfilePrefix, current.Prefix, StringComparison.Ordinal) ||
                    !int.TryParse(RoomProfileStart, NumberStyles.None, CultureInfo.InvariantCulture, out var fixedStart) ||
                    fixedStart != current.StartNumber || range.Count != current.ComputerCount)
                    throw new InvalidDataException("此机房使用固定的自定义电脑编号；请在座位图中管理编号，不能用连续范围覆盖，以免电脑和学生错位。");
                _ = MachineNaming.CreateNames(RoomProfilePrefix, fixedNumbers);
                room = current with
                {
                    DisplayName = RoomProfileName.Trim(),
                    HostOverrides = hostOverrides.ToList(),
                    ComputerNumbers = fixedNumbers.ToList()
                };
            }
            else
            {
                room = new TeacherRoomProfile(selectedId ?? Guid.NewGuid(), RoomProfileName.Trim(),
                    RoomProfilePrefix, int.Parse(RoomProfileStart, CultureInfo.InvariantCulture), range.Count,
                    hostOverrides.ToList());
            }
            if (index >= 0) rooms[index] = room;
            else rooms.Add(room);
            var campuses = CampusProfiles.Select(item => item.ProfileId == campus.ProfileId
                ? item with { Rooms = rooms }
                : item).ToArray();
            if (PersistCampusDirectory(campuses, "机房模板已保存。"))
            {
                SelectedCampusProfile = CampusProfiles.FirstOrDefault(item => item.ProfileId == campus.ProfileId);
                SelectedRoomProfile = RoomProfiles.FirstOrDefault(item => item.RoomId == room.RoomId);
                LoadSelectedSeatGrid();
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or OverflowException)
        {
            CampusDirectoryError = "机房模板无效：" + exception.Message;
        }
    }

    public void DeleteSelectedRoomProfile()
    {
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } selected) return;
        if (_activeClassroomSession?.Room.RoomId == selected.RoomId)
        {
            RoomCreateError = "当前机房正在上课；结束课堂后再删除本机机房档案。";
            return;
        }
        var campuses = CampusProfiles.Select(item => item.ProfileId == campus.ProfileId
            ? item with { Rooms = item.Rooms.Where(room => room.RoomId != selected.RoomId).ToList() }
            : item).ToArray();
        if (PersistCampusDirectory(campuses, "机房模板已删除。"))
        {
            string? cleanupError = null;
            try
            {
                _classroomSeatGridStore.DeleteLayout(campus.ProfileId, selected.RoomId);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                cleanupError = "本机机房档案已删除，但旧座位图文件未能清理；它不会继续显示。" + exception.Message;
            }
            if (cleanupError is null)
            {
                RoomCreateError = "";
                SeatMapStatus = "本机机房档案、学生姓名和座位图已删除；Veyon 地点与电脑目录未更改。";
            }
            else RoomCreateError = cleanupError;
        }
    }

    public void OpenVeyonConfigurator()
    {
        if (!CanOpenVeyonConfigurator) return;
        var path = WindowsVeyonAdapter.ResolveVeyonConfiguratorPath();
        if (path is null)
        {
            RoomCreateError = "没有找到 Veyon 配置器。请确认已安装 Veyon，默认路径应为 Program Files\\Veyon\\veyon-configurator.exe。";
            return;
        }
        try
        {
            ProcessRunner.LaunchVeyonConfigurator(path);
            RoomCreateError = "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            RoomCreateError = "无法打开 Veyon 配置器：" + exception.Message;
        }
    }

    public void LoadSelectedRoomProfileIntoBuilder()
    {
        if (SelectedRoomProfile is not { } room) return;
        RoomLocationName = room.DisplayName;
        RoomPrefix = room.Prefix;
        RoomStart = room.StartNumber.ToString(CultureInfo.InvariantCulture);
        RoomComputerHosts = string.Join(Environment.NewLine, room.HostOverrides ?? []);
        CampusDirectoryStatus = "已载入地点设置。电脑数量将按座位表自动生成；尚未修改 Veyon。";
        CampusDirectoryError = "";
    }

    public async Task AddRoomToVeyonAsync()
    {
        if (SeatGridNumberPendingCount > 0)
        {
            RoomCreateError = $"有 {SeatGridNumberPendingCount} 个已填写姓名的座位还没有电脑编号。请在座位图中给它们设置自定义编号，或点击“给待编号座位分配新号”；现有编号不会改变。";
            return;
        }
        if (!CanStartRoomCreation)
        {
            RoomCreateError = "请先进入座位图编辑状态并至少填写一个学生姓名，再写入地点和电脑。";
            return;
        }
        if (SeatGridNamedCount == 0)
        {
            RoomCreateError = "请至少填写一个学生姓名。空姓名桌位只保留为桌子，不会生成电脑。";
            return;
        }
        if (_isClassroomTransitioning || HasActiveClassroomSession)
        {
            RoomConflictStatus = "请先结束课堂，再添加地点和电脑。";
            RoomCreateError = "当前课堂正在使用电脑清单；结束课堂后即可一键写入地点和电脑。";
            return;
        }
        if (!CanCheckRoomConflicts)
        {
            RoomCreateError = "当前有 Veyon 目录操作正在进行；完成后再试一次。";
            return;
        }
        RoomCreateResult = "";
        RoomCreateError = "";
        await InspectRoomConflictsAsync();
        if (!CanAddRoomToVeyon) return;
        if (RoomError.Length > 0 || !HasRoomPreview)
        {
            RoomCreateError = RoomError.Length > 0 ? RoomError : "请先填写至少一个学生姓名。";
            return;
        }
        VeyonNetworkComputer[] computers;
        try { computers = BuildRoomImportPlan(); }
        catch (InvalidDataException exception)
        {
            RoomCreateError = "地点清单无效；没有修改 Veyon。" + exception.Message;
            return;
        }
        if (!TryBeginExclusiveTask()) return;
        RoomCreateResult = "";
        RoomCreateError = "";
        var renamedExistingComputerCount = 0;
        try
        {
            var locationName = RoomLocationName.Trim();
            RoomCreateStatus = "正在检查 Veyon 版本、权限和现有地点……";
            var veyon = await Task.Run(VeyonFacts.Probe);
            if (veyon.Status != "installed" || !VeyonFacts.IsSupportedVersionDetail(veyon.VersionDetail))
            {
                RoomCreateError = $"需要先安装并确认 Veyon {VeyonInstallerTrust.Version}。没有更改目录。\n{veyon.AsText()}";
                return;
            }
            var directory = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            VeyonComputerRenameTargetPlan[] renameTargets;
            try { renameTargets = BuildRoomComputerRenamePlan(locationName, computers, directory); }
            catch (InvalidDataException exception)
            {
                RoomCreateError = "现有 Veyon 电脑与本次座位编号无法安全对应；没有修改目录。" + exception.Message;
                return;
            }
            if (renameTargets.Length > 0)
            {
                RoomCreateStatus = $"正在同步 {renameTargets.Length} 台现有电脑的学生显示名……";
                var renameResponse = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                    (requestId, caller) => new PrivilegedWorkerRequest(
                        PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                        PrivilegedWorkerOperation.RenameVeyonComputers,
                        VeyonConfigFingerprint: directory.ConfigFingerprint,
                        VeyonComputerRenameTargets: renameTargets));
                if (!renameResponse.Result.Ok)
                {
                    RoomCreateError = "现有电脑名称同步未完成；没有继续创建地点和电脑。" + renameResponse.Result.Detail;
                    return;
                }
                renamedExistingComputerCount = renameTargets.Length;
            }
            RoomCreateStatus = "正在写入地点和电脑清单并读回核对……";
            var response = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                (requestId, caller) => new PrivilegedWorkerRequest(
                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                    PrivilegedWorkerOperation.AddVeyonRoom,
                    LocationName: locationName, Computers: computers));
            if (response.Result.Ok)
            {
                if (!TrySaveCurrentRoomProfile(out var profileError, computers))
                {
                    RoomCreateError = "Veyon 地点和电脑已写入并读回，但本机地点记录未保存；请重新打开本机权限后再试。" + profileError;
                    return;
                }
            var bindingError = await TryBindRoomComputersToSeatGridAsync(locationName, computers);
                RoomCreateResult = $"Veyon 地点和 {computers.Length} 台学生电脑已写入，并完成写入后自动核对。";
                if (bindingError is null)
                {
                    CommitSeatMapEdit();
                    IsSeatMapEditing = false;
                    RoomCreateResult = $"已完成：{computers.Length} 台学生电脑已自动对应姓名桌位；空姓名桌位只保留为桌子。";
                }
                else
                    RoomCreateError = $"Veyon 地点和 {computers.Length} 台学生电脑已成功写入。程序已在写入后自动核对，但未能确认教师机上的座位对应关系；已写入的 Veyon 电脑不受影响。原因：" + bindingError;
            }
            else RoomCreateError = (renamedExistingComputerCount > 0
                ? $"已有 {renamedExistingComputerCount} 台电脑名称已同步，但地点写入未完成。"
                : "") + "添加地点未完成：" + response.Result.Detail;
        }
        catch (Exception exception)
        {
            RoomCreateError = (renamedExistingComputerCount > 0
                ? $"已有 {renamedExistingComputerCount} 台电脑名称已同步；"
                : "") + "添加地点未完成：" + exception.Message;
        }
        finally
        {
            RoomCreateStatus = "";
            EndExclusiveTask();
        }
    }

    private async Task<string?> TryBindRoomComputersToSeatGridAsync(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers)
    {
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room ||
            _seatGrid is null || room.ComputerCount != computers.Count ||
            !string.Equals(room.DisplayName, locationName, StringComparison.OrdinalIgnoreCase))
            return "本机地点或座位图与刚写入的地点不一致。";

        var campusId = campus.ProfileId;
        var roomId = room.RoomId;
        var seats = _seatGrid.Seats.Where(seat => !string.IsNullOrWhiteSpace(seat.StudentName))
            .OrderBy(seat => seat.Row).ThenBy(seat => seat.Column).ToArray();
        if (seats.Length != computers.Count)
            return $"当前有 {seats.Length} 个座位，电脑清单有 {computers.Count} 台。";

        VeyonDirectorySnapshot snapshot;
        try { snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot); }
        catch (Exception exception)
        {
            return "写入后的自动核对无法读取 Veyon 电脑目录：" + exception.Message;
        }

        if (SelectedCampusProfile?.ProfileId != campusId || SelectedRoomProfile?.RoomId != roomId)
            return "读取期间当前地点已切换。";

        var locationObjects = snapshot.Objects.Where(item =>
            item.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
            item.ObjectId != Guid.Empty &&
            string.Equals(item.Name, locationName, StringComparison.Ordinal)).ToArray();
        if (locationObjects.Length != 1)
            return "Veyon 中找不到唯一对应的地点对象。";
        var locationObject = locationObjects[0];

        var bindings = new List<ClassroomSeatGridTargetBinding>(computers.Count);
        var usedObjectIds = new HashSet<Guid>();
        for (var index = 0; index < computers.Count; index++)
        {
            string expectedHost;
            try { expectedHost = VeyonHostAddress.NormalizeOverride(computers[index].Host); }
            catch (InvalidDataException exception) { return "电脑地址无效：" + exception.Message; }

            var matches = snapshot.Objects.Where(item =>
            {
                if (!item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase) ||
                    item.ObjectId == Guid.Empty || item.ParentObjectId != locationObject.ObjectId ||
                    !string.Equals(item.Name, computers[index].DisplayName, StringComparison.Ordinal))
                    return false;
                try
                {
                    return string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), expectedHost,
                        StringComparison.OrdinalIgnoreCase);
                }
                catch (InvalidDataException) { return false; }
            }).ToArray();
            if (matches.Length != 1 || !usedObjectIds.Add(matches[0].ObjectId))
                return $"第 {index + 1} 个座位的电脑名和地址不能唯一匹配 Veyon 读回结果。";

            bindings.Add(new ClassroomSeatGridTargetBinding(seats[index].SeatId, expectedHost,
                matches[0].Name, matches[0].ObjectId, locationObject.ObjectId));
        }

        if (SelectedCampusProfile?.ProfileId != campusId || SelectedRoomProfile?.RoomId != roomId)
            return "应用期间当前地点已切换。";

        try
        {
            var updated = _classroomSeatGridStore.AssignVeyonTargets(campusId, roomId, bindings);
            ApplySeatGrid(updated);
            SeatVeyonTargetStatus = $"已关联 {bindings.Count} 台电脑。";
            RefreshClassroomEventSeatLocations();
            return null;
        }
        catch (Exception exception)
        {
            return "本机座位关联未保存：" + exception.Message;
        }
    }

    private static string DescribeWindowsLaunchError(Exception exception)
    {
        if (exception is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 } ||
            exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 })
            return "相关程序文件正被其他进程占用；请关闭正在运行的安装或更新程序后重试。";
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
            if (RoomNames.Count == 0) GenerateRoomPreview();
            if (RoomError.Length > 0 || RoomNames.Count == 0)
                throw new InvalidDataException(RoomError.Length > 0 ? RoomError : "请先在座位图中添加座位。");
            var names = RoomNames;
            WebsiteTargets = string.Join(Environment.NewLine, names);
            RoomError = "";
            WebsiteDirectoryStatus = $"已填入 {names.Count} 台电脑。请检查后应用限制。";
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
                WebsiteDirectoryError = "未找到 Veyon 机房电脑。请先配置机房，或手动填写电脑名单。";
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

    public async Task ReadClassroomLocationsAsync(bool forceSelection = false)
    {
        if (IsReadingWebsiteLocations || IsExecuting) return;
        IsReadingWebsiteLocations = true;
            ClassroomDirectoryStatus = "正在读取 Veyon 地点……";
        ClassroomDirectoryError = "";
        _classroomDirectorySelectionPending = false;
        Changed(nameof(HasMultipleClassroomLocations));
        Changed(nameof(CanSyncSeatGridWithVeyon));
        Changed(nameof(SeatGridVeyonSyncButtonLabel));
        ClassroomLocationIndex = -1;
        try
        {
            ReadWebsiteSigningCampuses();
            var locations = await Task.Run(VeyonNetworkObjectDirectory.ReadLocations);
            WebsiteLocations = locations;
            if (locations.Count == 0)
            {
                ClassroomDirectoryStatus = "";
                ClassroomDirectoryError = "没有找到学生电脑。请先在“地点与学生名单”配置 Veyon 地点。";
                return;
            }

            if (locations.Count == 1)
            {
                ClassroomLocationIndex = 0;
                ApplyClassroomVeyonLocation(locations[0]);
                return;
            }

            var selectedRoomIndex = forceSelection ? -1 : Array.FindIndex(locations.ToArray(), location =>
                string.Equals(location.Name, SelectedRoomProfile?.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (selectedRoomIndex >= 0)
            {
                ClassroomLocationIndex = selectedRoomIndex;
                ApplyClassroomVeyonLocation(locations[selectedRoomIndex]);
                return;
            }

            _classroomDirectorySelectionPending = true;
            ClassroomDirectoryStatus = $"Veyon 中有 {locations.Count} 个地点，暂时无法自动确认课堂范围。请先到“地点与学生名单”页选择对应地点。";
            Changed(nameof(HasMultipleClassroomLocations));
            Changed(nameof(CanUseSelectedClassroomLocation));
            Changed(nameof(CanSyncSeatGridWithVeyon));
            Changed(nameof(SeatGridVeyonSyncButtonLabel));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or TimeoutException or PlatformNotSupportedException or
                                          System.ComponentModel.Win32Exception)
        {
            ClassroomDirectoryStatus = "";
            ClassroomDirectoryError = "读取 Veyon 地点失败；本机学生电脑名单没有更改。" + exception.Message;
        }
        finally { IsReadingWebsiteLocations = false; }
    }

    public void UseSelectedClassroomLocation()
    {
        if (!CanUseSelectedClassroomLocation) return;
        ApplyClassroomVeyonLocation(WebsiteLocations[ClassroomLocationIndex]);
    }

    public async Task SyncClassroomSeatGridWithVeyonAsync()
    {
        if (!CanSyncSeatGridWithVeyon) return;
        if (_classroomDirectorySelectionPending)
        {
            UseSelectedClassroomLocation();
            if (_classroomDirectorySelectionPending || HasClassroomDirectoryError) return;
            await ReadSeatVeyonTargetsAsync();
            return;
        }

        await ReadClassroomLocationsAsync();
        if (_classroomDirectorySelectionPending || HasClassroomDirectoryError || SelectedRoomProfile is null) return;
        await ReadSeatVeyonTargetsAsync();
    }

    private void ApplyClassroomVeyonLocation(VeyonNetworkLocation location)
    {
        try
        {
            var campus = SelectedCampusProfile;
            if (campus is null && CampusProfiles.Count == 1) campus = CampusProfiles[0];
            var roomCampus = string.IsNullOrWhiteSpace(RoomCampusName) || RoomCampusName == "本机校区"
                ? null : RoomCampusName.Trim();
            var configuredCampus = string.IsNullOrWhiteSpace(CampusId) || CampusId == DefaultCampusNamePrefix
                ? null : CampusId.Trim();
            var campusName = campus?.DisplayName ?? roomCampus ??
                (WebsiteSigningCampuses.Count == 1 ? WebsiteSigningCampuses[0] : configuredCampus ?? "本机校区");
            var mergedCampus = TeacherCampusProfileFromVeyon.MergeLocation(campus, campusName, location);
            var selectedRoom = mergedCampus.Rooms.Single(room =>
                string.Equals(room.DisplayName, location.Name, StringComparison.OrdinalIgnoreCase));
            var campuses = CampusProfiles.Where(item => item.ProfileId != mergedCampus.ProfileId).ToList();
            campuses.Add(mergedCampus);
            if (!PersistCampusDirectory(campuses,
                    $"已读取 Veyon 地点“{location.Name}”并更新学生电脑名单，共 {location.Targets.Count} 台电脑；没有修改 Veyon。"))
            {
                ClassroomDirectoryStatus = "";
                ClassroomDirectoryError = CampusDirectoryError;
                return;
            }

            SelectedCampusProfile = CampusProfiles.FirstOrDefault(item => item.ProfileId == mergedCampus.ProfileId);
            SelectedRoomProfile = RoomProfiles.FirstOrDefault(item => item.RoomId == selectedRoom.RoomId);
            _classroomDirectorySelectionPending = false;
            ClassroomDirectoryError = "";
            ClassroomDirectoryStatus = "";
            Changed(nameof(HasMultipleClassroomLocations));
            Changed(nameof(CanUseSelectedClassroomLocation));
            Changed(nameof(CanSyncSeatGridWithVeyon));
            Changed(nameof(SeatGridVeyonSyncButtonLabel));
            Changed(nameof(CanToggleClassroomSession));
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or
                                          IOException or UnauthorizedAccessException or ArgumentException)
        {
            ClassroomDirectoryStatus = "";
            ClassroomDirectoryError = "此 Veyon 地点名单无法用于学生互动；本机电脑名单没有更改。" + exception.Message;
        }
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
            ? "请选择与学生配置包相同的校区。"
            : $"校区：{CampusId.Trim()}。";
        WebsiteDirectoryStatus = $"已填入“{location.Name}”的 {location.Targets.Count} 台电脑。{campusStatus}请检查后应用限制。";
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
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            var now = DateTimeOffset.UtcNow;
            var rules = mode == ApplicationPolicyMode.Disabled ? Array.Empty<ApplicationDenyRule>() : ParseApplicationRules();
            var sids = mode == ApplicationPolicyMode.Disabled ? Array.Empty<string>() : ParseStudentSids();
            DateTimeOffset? expiry = mode == ApplicationPolicyMode.Disabled ? null : now + ApplicationPolicyLifetime();
            var revision = WebsitePolicyRevisionStore.Next(campus);
            var policy = ApplicationPolicyCompiler.Validate(new ApplicationPolicyDocument(1,
                ApplicationPolicyCompiler.Purpose, campus, revision, now, expiry, mode, sids, rules));
            if (mode == ApplicationPolicyMode.Enforce && !ApplicationEnforcementReviewed)
                throw new InvalidDataException("请先查看影响结果并勾选确认。");
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
                ApplicationPolicyMode.Audit => "影响检查",
                ApplicationPolicyMode.Enforce => "应用限制",
                _ => succeeded == results.Count ? "限制已解除" : "解除结果"
            };
            ApplicationPolicyResult = $"{modeText}{expirySummary} · 成功 {succeeded}/{results.Count} · 需核对 {needsReview} · 失败 {failed}";
            ApplicationPolicyResultDetails = string.Join(Environment.NewLine, results.Select(result =>
                $"{GetWebsitePolicyTargetLabel(result.Target)}：{(result.Succeeded ? "已确认" : result.NeedsReview ? "需核对" : "失败")} — {result.Detail}"));
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
            { errors.Add("结果已显示，但无法保存历史记录：" + exception.Message); }
            if (succeeded != results.Count)
                errors.Add("部分电脑未确认状态；请查看结果后重试。");
            if (errors.Count > 0) ApplicationPolicyError = string.Join(" ", errors);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            ApplicationPolicyError = "应用限制未完成：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveTargetDisplayNamesAsync(
        IReadOnlyList<string> targets)
    {
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddLocationDisplayNames(displayNames, WebsiteLocations, replace: false);

        foreach (var campus in CampusProfiles)
        foreach (var room in campus.Rooms)
        {
            IReadOnlyList<string> computerNames;
            try
            {
                computerNames = room.GetComputerNames();
            }
            catch (InvalidDataException) { continue; }

            var hostOverrides = room.HostOverrides ?? [];
            for (var index = 0; index < Math.Min(computerNames.Count, hostOverrides.Count); index++)
            {
                if (!string.IsNullOrWhiteSpace(hostOverrides[index]))
                    AddTargetDisplayName(displayNames, hostOverrides[index], computerNames[index]);
            }
        }

        var hasUnmappedIp = targets.Any(target =>
            (Uri.CheckHostName(target) is UriHostNameType.IPv4 or UriHostNameType.IPv6) &&
            !displayNames.ContainsKey(NormalizeTargetKey(target)));
        if (hasUnmappedIp)
        {
            try
            {
                var currentLocations = await Task.Run(() =>
                    VeyonNetworkObjectDirectory.ReadLocations(TimeSpan.FromSeconds(5)));
                AddLocationDisplayNames(displayNames, currentLocations, replace: true);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or
                UnauthorizedAccessException or InvalidOperationException or TimeoutException or
                PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                _ = exception;
            }
        }

        var studentsByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var studentsByComputer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousComputers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownStudentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var campus in CampusProfiles)
        foreach (var room in campus.Rooms)
        {
            ClassroomSeatGridLayout layout;
            try { layout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId, room.StartNumber); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            { continue; }
            IReadOnlyList<int> roomNumbers;
            IReadOnlyList<string> roomComputerNames;
            try { roomNumbers = room.GetComputerNumbers(); roomComputerNames = room.GetComputerNames(); }
            catch (InvalidDataException) { roomNumbers = Array.Empty<int>(); roomComputerNames = Array.Empty<string>(); }
            foreach (var seat in layout.Seats.Where(item => !string.IsNullOrWhiteSpace(item.StudentName)))
            {
                var studentName = seat.StudentName.Trim();
                knownStudentNames.Add(studentName);
                if (!string.IsNullOrWhiteSpace(seat.VeyonHostAddress))
                    AddUnambiguousStudentName(studentsByTarget, ambiguousTargets,
                        seat.VeyonHostAddress, studentName);
                if (!string.IsNullOrWhiteSpace(seat.VeyonComputerName))
                    AddUnambiguousStudentName(studentsByComputer, ambiguousComputers,
                        seat.VeyonComputerName, studentName);
                if (seat.ComputerNumber is not { } number) continue;
                var seatIndex = -1;
                for (var candidate = 0; candidate < roomNumbers.Count; candidate++)
                    if (roomNumbers[candidate] == number) { seatIndex = candidate; break; }
                if (seatIndex < 0) continue;
                if (seatIndex < roomComputerNames.Count)
                    AddUnambiguousStudentName(studentsByComputer, ambiguousComputers,
                        roomComputerNames[seatIndex], studentName);
                if (room.HostOverrides is { } hosts && seatIndex < hosts.Count &&
                    !string.IsNullOrWhiteSpace(hosts[seatIndex]))
                    AddUnambiguousStudentName(studentsByTarget, ambiguousTargets,
                        hosts[seatIndex], studentName);
            }
        }

        foreach (var target in targets)
        {
            var targetKey = NormalizeTargetKey(target);
            displayNames.TryGetValue(targetKey, out var computerName);
            var studentName = ambiguousTargets.Contains(targetKey)
                ? null
                : studentsByTarget.GetValueOrDefault(targetKey);
            if (string.IsNullOrWhiteSpace(studentName) && !ambiguousTargets.Contains(targetKey) &&
                !string.IsNullOrWhiteSpace(computerName))
                studentName = studentsByComputer.GetValueOrDefault(NormalizeTargetKey(computerName));
            if (string.IsNullOrWhiteSpace(studentName) && !ambiguousTargets.Contains(targetKey) &&
                !IsIpAddressDisplayName(target))
                studentName = studentsByComputer.GetValueOrDefault(targetKey);
            if (string.IsNullOrWhiteSpace(studentName) && !string.IsNullOrWhiteSpace(computerName) &&
                knownStudentNames.Contains(computerName.Trim()))
                studentName = computerName.Trim();
            if (string.IsNullOrWhiteSpace(studentName)) studentName = "未填写姓名";
            displayNames[targetKey] = studentName;
            if (!string.IsNullOrWhiteSpace(computerName) && !IsIpAddressDisplayName(computerName))
            {
                var computerKey = NormalizeTargetKey(computerName);
                if (ambiguousComputers.Contains(computerKey)) displayNames.Remove(computerKey);
                else displayNames[computerKey] = studentName;
            }
        }

        return displayNames;
    }

    private static void AddLocationDisplayNames(Dictionary<string, string> displayNames,
        IReadOnlyList<VeyonNetworkLocation> locations, bool replace)
    {
        foreach (var location in locations)
        {
            if (location.TargetDisplayNames is null) continue;
            foreach (var pair in location.TargetDisplayNames)
                AddTargetDisplayName(displayNames, pair.Key, pair.Value, replace);
        }
    }

    private static void AddTargetDisplayName(Dictionary<string, string> displayNames,
        string target, string displayName, bool replace = false)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(displayName)) return;
        var key = NormalizeTargetKey(target);
        if (replace || !displayNames.ContainsKey(key)) displayNames[key] = displayName.Trim();
    }

    private static void AddUnambiguousStudentName(Dictionary<string, string> studentNames,
        HashSet<string> ambiguousAliases, string alias, string studentName)
    {
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(studentName)) return;
        var key = NormalizeTargetKey(alias);
        if (ambiguousAliases.Contains(key)) return;
        if (studentNames.TryGetValue(key, out var existing) &&
            !string.Equals(existing, studentName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            studentNames.Remove(key);
            ambiguousAliases.Add(key);
            return;
        }
        studentNames[key] = studentName.Trim();
    }

    private static string GetTargetDisplayName(string target,
        IReadOnlyDictionary<string, string> displayNames) =>
        displayNames.TryGetValue(NormalizeTargetKey(target), out var displayName) &&
        !string.IsNullOrWhiteSpace(displayName) ? displayName :
        "未填写姓名";

    private static bool IsIpAddressDisplayName(string value) =>
        Uri.CheckHostName(value.Trim()) is UriHostNameType.IPv4 or UriHostNameType.IPv6;

    private static string NormalizeTargetKey(string target)
    {
        try { return VeyonHostAddress.NormalizeOverride(target).ToLowerInvariant(); }
        catch (InvalidDataException) { return target.Trim().ToLowerInvariant(); }
    }

    public Task DisableApplicationPolicyAsync() => PushApplicationPolicyAsync(ApplicationPolicyMode.Disabled);

    public void SelectRecommendedStudentSystemRestrictions()
    {
        StudentSystemPolicyLockWallpaper = false;
        StudentSystemPolicyProhibitTimeChanges = true;
        StudentSystemPolicyProhibitNetworkChanges = true;
        StudentSystemPolicyProhibitSoftwareInstallation = false;
        StudentSystemPolicyProhibitAccountManagement = true;
        StudentSystemPolicyProhibitControlPanel = false;
    }

    public void ClearStudentSystemRestrictionSelection()
    {
        StudentSystemPolicyLockWallpaper = false;
        StudentSystemPolicyProhibitTimeChanges = false;
        StudentSystemPolicyProhibitNetworkChanges = false;
        StudentSystemPolicyProhibitSoftwareInstallation = false;
        StudentSystemPolicyProhibitAccountManagement = false;
        StudentSystemPolicyProhibitControlPanel = false;
    }

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
                throw new PlatformNotSupportedException("请在 Windows 教师端设置学生电脑限制。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(
                GetSelectedWebsitePolicyTargets());
            var settings = disabled ? new StudentSystemPolicySettings(false, false, false, false, false, false) :
                new StudentSystemPolicySettings(StudentSystemPolicyLockWallpaper,
                    StudentSystemPolicyProhibitTimeChanges, StudentSystemPolicyProhibitNetworkChanges,
                    StudentSystemPolicyProhibitSoftwareInstallation, StudentSystemPolicyProhibitAccountManagement,
                    StudentSystemPolicyProhibitControlPanel);
            if (!disabled && settings.IsEmpty)
                throw new InvalidDataException("至少选择一项学生机系统限制；如需撤销，请使用“解除”按钮。");
            if (!disabled && settings.ProhibitSoftwareInstallation && !_studentSystemPolicySoftwareInstallReviewed)
                throw new InvalidDataException("请先勾选软件安装影响确认。");
            if (!disabled && !_studentSystemPolicyImpactReviewed)
                throw new InvalidDataException("请先阅读系统限制影响说明和目标预览，并勾选确认。");
            var sids = disabled ? Array.Empty<string>() : ParseStudentSids(allowManualInput: false);
            var revision = StudentSystemPolicySigningKeyStore.NextRevision(campus);
            var policy = StudentSystemPolicyCompiler.Create(campus, revision, sids, settings);
            using var signingKey = StudentSystemPolicySigningKeyStore.Open(campus);
            var accounts = StudentSidsByTarget(targets, disabled, useManualInputs: false);
            var signedByTarget = targets.ToDictionary(t => t, t => StudentSystemPolicyCryptography.Sign(
                StudentSystemPolicyCompiler.Create(campus, revision, accounts[t], settings), signingKey.PrivateKey));
            var results = await SendByTargetAsync(targets, t => StudentSystemPolicyTransport.PushAsync([t], signedByTarget[t], campus));
            var succeeded = results.Count(result => result.Succeeded);
            var partiallyApplied = results.Count(result => result.Succeeded && result.Partial);
            var fullyApplied = succeeded - partiallyApplied;
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count(result => !result.Succeeded && !result.NeedsReview);
            var mode = disabled
                ? "Agent 解除回执"
                : partiallyApplied > 0 ? "Agent 部分策略回执"
                : succeeded == results.Count ? "Agent 策略回执" : "Agent 回执结果";
            StudentSystemPolicyResult = partiallyApplied > 0
                ? $"{mode} · 完整 {fullyApplied}/{results.Count} · 部分 {partiallyApplied} · 需核对 {needsReview} · 失败 {failed} · Windows 实际效果待现场核验"
                : $"{mode} · 已回执 {succeeded}/{results.Count} · 需核对 {needsReview} · 失败 {failed} · Windows 实际效果待现场核验";
            StudentSystemPolicyDetails = string.Join(Environment.NewLine, results.Select(result =>
                $"{GetWebsitePolicyTargetLabel(result.Target)}：{(result.Partial ? "Agent 部分读回" : result.Succeeded ? disabled ? "Agent 已读回解除状态" : "Agent 已读回策略" : result.NeedsReview ? "需核对" : "失败")} — {result.Detail}"));
            if (succeeded != results.Count)
                StudentSystemPolicyError = "部分电脑未收到有效回执；请检查逐台结果和连接状态。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            StudentSystemPolicyError = "系统限制未应用：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    public async Task ReadApplicationCatalogAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        ApplicationCatalogStatus = "正在快速读取软件目录；不会计算发布者签名或文件哈希……";
        ApplicationCatalogFailureDetails = "";
        ApplicationCatalogAllTargetsRead = false;
        SetApplicationCatalogChoices(Array.Empty<ApplicationCatalogProductChoice>());
        SetApplicationInventoryChoices(Array.Empty<ApplicationInventoryChoice>());
        ApplicationInventoryStatus = "";
        ApplicationInventoryFailureDetails = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("软件目录读取仅支持 Windows 教师端。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            using var signingKey = ApplicationPolicySigningKeyStore.Open(campus);
            var displayNamesTask = ResolveTargetDisplayNamesAsync(targets);
            var results = await ApplicationPolicyTransport.ReadApplicationCatalogAsync(targets, campus,
                signingKey.PrivateKey);
            var displayNames = await displayNamesTask;
            if (campus != CampusId.Trim() || !targets.SequenceEqual(WebsitePolicyTransport.NormalizeTargets(
                    GetSelectedWebsitePolicyTargets()), StringComparer.OrdinalIgnoreCase))
            { ApplicationCatalogStatus = "校区或电脑名单已变化，请重新读取软件目录。"; return; }

            var entries = results.Where(result => result.Succeeded)
                .SelectMany(result => result.Items.Select(item => (result.Target,
                    DisplayTarget: GetTargetDisplayName(result.Target, displayNames), Item: item))).ToArray();
            var products = entries.GroupBy(entry =>
                NormalizeApplicationIdentity(entry.Item.ProductName ?? entry.Item.DisplayName) + "|" +
                NormalizeApplicationIdentity(entry.Item.CompanyName ?? ""), StringComparer.Ordinal)
                .Select(group =>
                {
                    var first = group.First().Item;
                    var productName = first.ProductName ?? first.DisplayName;
                    var company = first.CompanyName ?? "";
                    var binaries = group.Select(entry => entry.Item.BinaryName)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                    var devices = group.Select(entry => entry.DisplayTarget)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                    var targetRefs = group.GroupBy(entry => entry.Target, StringComparer.OrdinalIgnoreCase)
                        .SelectMany(device => device.Select(entry => new ApplicationCatalogCandidateReference(
                            entry.Target, entry.Item.Id)))
                        .GroupBy(candidate => candidate.Target + "|" + candidate.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(candidates => candidates.First()).ToArray();
                    var shownBinaries = string.Join("、", binaries.Take(5));
                    if (binaries.Length > 5) shownBinaries += " 等";
                    var shownDevices = string.Join("、", devices.Take(6));
                    if (devices.Length > 6) shownDevices += " 等";
                    return new ApplicationCatalogProductChoice(productName, company, first.DisplayName,
                        $"{productName} · {binaries.Length} 个程序 · {devices.Length} 台电脑",
                        $"程序：{shownBinaries}{Environment.NewLine}文件属性中的公司信息：{(company.Length == 0 ? "未提供" : company)}（尚未做签名核验）{Environment.NewLine}电脑：{shownDevices}",
                        targetRefs);
                })
                .OrderBy(choice => choice.ProductName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(choice => choice.CompanyName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            SetApplicationCatalogChoices(products);
            var failed = results.Where(result => !result.Succeeded).ToArray();
            ApplicationCatalogAllTargetsRead = failed.Length == 0;
            ApplicationCatalogFailureDetails = string.Join(Environment.NewLine, failed.Take(6)
                .Select(result => $"{GetTargetDisplayName(result.Target, displayNames)}：{result.Detail}"));
            ApplicationCatalogStatus = $"已快速读取 {results.Count - failed.Length}/{results.Count} 台 · {products.Length} 个软件产品 · 尚未计算签名和哈希" +
                                       (failed.Length == 0 ? "" : $" · {failed.Length} 台未读取");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException or
                                          System.ComponentModel.Win32Exception or TimeoutException)
        {
            ApplicationCatalogAllTargetsRead = false;
            SetApplicationCatalogChoices(Array.Empty<ApplicationCatalogProductChoice>());
            ApplicationCatalogFailureDetails = exception.Message;
            ApplicationCatalogStatus = "软件目录读取失败；没有修改学生电脑。";
        }
        finally { EndExclusiveTask(); }
    }

    public async Task ReadSelectedApplicationDetailsAsync()
    {
        if (!CanGenerateApplicationCandidates)
        {
            ApplicationCatalogStatus = ApplicationCatalogSelectionSummary;
            return;
        }
        if (!TryBeginExclusiveTask()) return;
        ApplicationCatalogStatus = "正在为选中的软件读取发布者信息并计算哈希……";
        ApplicationCatalogFailureDetails = "";
        try
        {
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var idsByTarget = SelectedApplicationCatalogCandidatesByTarget();
            var targets = WebsitePolicyTransport.NormalizeTargets(idsByTarget.Keys);
            using var signingKey = ApplicationPolicySigningKeyStore.Open(campus);
            var displayNamesTask = ResolveTargetDisplayNamesAsync(targets);
            var results = await ApplicationPolicyTransport.ReadSelectedApplicationDetailsAsync(idsByTarget,
                campus, signingKey.PrivateKey);
            var displayNames = await displayNamesTask;
            var currentTargets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            if (campus != CampusId.Trim() || targets.Except(currentTargets, StringComparer.OrdinalIgnoreCase).Any())
            { ApplicationCatalogStatus = "校区或目标电脑已变化，请重新读取软件目录。"; return; }

            var failed = results.Where(result => !result.Succeeded).ToArray();
            if (failed.Length > 0)
            {
                ApplicationCatalogAllTargetsRead = false;
                SetApplicationInventoryChoices(Array.Empty<ApplicationInventoryChoice>());
                ApplicationInventoryFailureDetails = string.Join(Environment.NewLine, failed.Take(6)
                    .Select(result => $"{GetTargetDisplayName(result.Target, displayNames)}：{result.Detail}"));
                ApplicationInventoryStatus = "部分电脑未生成精确候选；已清除本次候选，请先处理设备问题并重新读取目录。";
                ApplicationCatalogStatus = ApplicationInventoryStatus;
                return;
            }

            var rawChoices = results.Where(result => result.Succeeded)
                .SelectMany(result => (result.Details ?? Array.Empty<ApplicationInventoryItem>()).Select(item =>
                    new ApplicationInventoryChoice(result.Target, item,
                        displayTarget: GetTargetDisplayName(result.Target, displayNames))))
                .ToArray();
            var unsupportedCount = rawChoices.Count(item => !item.CanSelect);
            var grouped = rawChoices.GroupBy(item => item.RuleLine is { } rule
                    ? "rule|" + rule
                    : "info|" + item.DisplayName + "|" + item.BinaryName + "|" + item.Publisher + "|" + item.Item.ProductName,
                    StringComparer.Ordinal)
                .Select(group =>
                {
                    var first = group.First();
                    var devices = group.Select(item => item.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var displayedDevices = group.GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
                        .Select(device => device.First().DisplayTarget).Take(6).ToArray();
                    var suffix = devices.Length > displayedDevices.Length ? " 等" : "";
                    return new ApplicationInventoryChoice(string.Join(", ", devices.Take(6)) + suffix,
                        first.Item, devices.Length, string.Join("、", displayedDevices) + suffix);
                })
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Target, StringComparer.OrdinalIgnoreCase).ToArray();
            SetApplicationInventoryChoices(grouped);
            ApplicationInventoryFailureDetails = "";
            ApplicationInventoryStatus = $"已为 {grouped.Length} 个选中软件生成精确规则候选" +
                                         (unsupportedCount == 0 ? "" : $" · {unsupportedCount} 项不支持生成规则");
            ApplicationCatalogStatus = ApplicationInventoryStatus;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException or
                                          System.ComponentModel.Win32Exception or TimeoutException)
        {
            ApplicationCatalogFailureDetails = exception.Message;
            ApplicationCatalogStatus = "精确规则候选生成失败；没有应用软件限制。";
        }
        finally { EndExclusiveTask(); }
    }

    public async Task ReadApplicationInventoryAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        ApplicationInventoryStatus = "正在读取软件……";
        ApplicationInventoryFailureDetails = "";
        ApplicationPolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("应用清单读取仅支持 Windows 教师端。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            using var signingKey = ApplicationPolicySigningKeyStore.Open(campus);
            var displayNamesTask = ResolveTargetDisplayNamesAsync(targets);
            var results = await ApplicationPolicyTransport.ReadInventoryAsync(targets, campus, signingKey.PrivateKey);
            var displayNames = await displayNamesTask;
            if (campus != CampusId.Trim() || !targets.SequenceEqual(WebsitePolicyTransport.NormalizeTargets(
                    GetSelectedWebsitePolicyTargets()), StringComparer.OrdinalIgnoreCase))
            { ApplicationInventoryStatus = "设置已变化，请重新读取软件。"; return; }
            var rawChoices = results.Where(result => result.Succeeded)
                .SelectMany(result => result.Items.Select(item => new ApplicationInventoryChoice(result.Target, item,
                    displayTarget: GetTargetDisplayName(result.Target, displayNames))))
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
                    var displayedDevices = group.GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
                        .Select(device => device.First().DisplayTarget).Take(6).ToArray();
                    var omittedSuffix = devices.Length > displayedDevices.Length ? " 等" : "";
                    var targetSummary = string.Join(", ", devices.Take(6)) + omittedSuffix;
                    var displayTargetSummary = string.Join(", ", displayedDevices) + omittedSuffix;
                    return new ApplicationInventoryChoice(targetSummary, first.Item, devices.Length, displayTargetSummary);
                })
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Target, StringComparer.OrdinalIgnoreCase).ToArray();
            SetApplicationInventoryChoices(_allApplicationInventoryChoices);
            var successful = results.Count(result => result.Succeeded);
            var failedResults = results.Where(result => !result.Succeeded).ToArray();
            var failureDetails = failedResults.Take(4)
                .Select(result => $"{GetTargetDisplayName(result.Target, displayNames)}：{result.Detail}").ToList();
            if (failedResults.Length > failureDetails.Count)
                failureDetails.Add($"另有 {failedResults.Length - failureDetails.Count} 台未显示。");
            ApplicationInventoryFailureDetails = string.Join(Environment.NewLine, failureDetails);
            ApplicationInventoryStatus = $"已读取 {successful}/{results.Count} 台 · {_allApplicationInventoryChoices.Count} 种软件" +
                                         (unsupportedCount == 0 ? "" : $" · {unsupportedCount} 项暂不支持限制") +
                                         (failedResults.Length == 0 ? "" : $" · {failedResults.Length} 台未读取");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException or
                                          System.ComponentModel.Win32Exception or TimeoutException)
        {
            _allApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
            ApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
            ApplicationInventoryFailureDetails = "";
            ApplicationInventoryStatus = "读取失败，请检查校区和电脑连接后重试。";
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
            ApplicationInventoryStatus = $"已添加 {added.Length} 个软件。";
    }

    public void SelectCommonApplicationChoices()
    {
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "QQ", "TencentQQ", "腾讯QQ", "微信", "WeChat", "TencentWeChat", "Weixin",
            "Steam", "WeGame", "EpicGames", "EpicGamesLauncher", "BattleNet", "BlizzardBattleNet",
            "RiotClient", "RiotClientServices", "LeagueOfLegends", "英雄联盟",
            "Bilibili", "哔哩哔哩", "Douyin", "抖音"
        };
        var normalizedNames = knownNames.Select(NormalizeApplicationIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = _allApplicationInventoryChoices.Where(choice => choice.CanSelect)
            .Where(choice =>
            {
                var binaryName = choice.BinaryName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? choice.BinaryName[..^4] : choice.BinaryName;
                return new[] { choice.DisplayName, choice.Item.ProductName ?? "", binaryName }
                    .Select(NormalizeApplicationIdentity).Any(normalizedNames.Contains);
            }).ToArray();

        foreach (var choice in matched) choice.IsSelected = true;
        ApplicationInventoryStatus = matched.Length == 0
            ? "未找到支持限制的常见应用；可搜索后逐项选择。"
            : $"已勾选 {matched.Length} 项常见应用；请核对名称和电脑后继续。";
    }

    public void ClearApplicationChoiceSelection()
    {
        foreach (var choice in _allApplicationInventoryChoices) choice.IsSelected = false;
        ApplicationInventoryStatus = "已清空软件选择。";
    }

    private static string NormalizeApplicationIdentity(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public async Task ReadApplicationPolicyAuditAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        ApplicationAuditResult = "正在检查最近 24 小时的软件限制记录……";
        ApplicationPolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("请在 Windows 教师端查看应用影响。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
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
                    lines.Add($"{GetWebsitePolicyTargetLabel(result.Target)}：{(result.NeedsReview ? "需核对" : "失败")} — {result.Detail}");
                    continue;
                }
                var response = result.Response;
                var wouldBlock = response.Results.Sum(item => item.WouldBlockCount);
                var blocked = response.Results.Sum(item => item.BlockedCount);
                lines.Add(response.IsSimulation
                    ? $"{GetWebsitePolicyTargetLabel(result.Target)}：影响检查，预计阻止 {wouldBlock} 次启动（未实际拦截）"
                    : $"{GetWebsitePolicyTargetLabel(result.Target)}：匹配 {wouldBlock} 次，已阻止 {blocked} 次启动");
                lines.AddRange(response.Results.Select(item => response.IsSimulation
                    ? $"  {item.DisplayName}：预计阻止 {item.WouldBlockCount} 次"
                    : $"  {item.DisplayName}：匹配 {item.WouldBlockCount} 次，阻止 {item.BlockedCount} 次"));
                if (!string.IsNullOrWhiteSpace(response.CoverageNote)) lines.Add("  " + response.CoverageNote);
                if (response.Results.Count == 0)
                    lines.Add(response.IsSimulation
                        ? "  没有发现可能被限制的软件。"
                        : "  最近 24 小时没有相关记录。");
            }
            ApplicationAuditResult = "影响检查结果：" +
                                     Environment.NewLine + string.Join(Environment.NewLine, lines);
            if (!_hasMatchingApplicationAudit)
                ApplicationAuditResult += Environment.NewLine + "部分电脑未确认当前规则；请重新检查后再启用限制。";
            else if (results.All(result => result.Response?.IsSimulation == true))
                ApplicationAuditResult += Environment.NewLine + "所有电脑仍使用当前检查结果。上方是软件影响估算，不会实际拦截；检查后再确认启用限制。";
            else
                ApplicationAuditResult += Environment.NewLine + "所有电脑均已确认。查看上方影响后，再确认启用限制。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            ApplicationAuditResult = "无法读取应用影响结果：" + exception.Message;
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
        var lifetimeMinutes = GetWebsiteLifetimeMinutes();
        var existing = MobilePolicyProfileStore.ReadAll()
            .FirstOrDefault(profile => profile.Kind == MobilePolicyProfileKind.Website &&
                                       profile.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                       profile.CampusId == campus);
        var profile = MobilePolicyProfileCompiler.Validate(new MobilePolicyProfile(
            existing?.Id ?? Guid.NewGuid(), name, campus, MobilePolicyProfileKind.Website, lifetimeMinutes,
            WebsiteMode: SelectedWebsiteMode,
            WebsiteDomains: (SelectedWebsiteMode == WebsitePolicyMode.Allowlist ? WebsiteAllowDomains : WebsiteBlockDomains)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            WebsiteAllowDomains: SelectedWebsiteMode == WebsitePolicyMode.Combined
                ? WebsiteAllowDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : null,
            UpdatedUtc: DateTimeOffset.UtcNow));
        MobilePolicyProfileStore.Save(profile);
        return profile;
    }

    public MobilePolicyProfile SaveApplicationMobileProfile(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("手机策略预设仅能从 Windows 教师端保存。");
        var campus = CampusId.Trim();
        WebsitePolicySigningKeyStore.ValidateCampusId(campus);
        var targets = WebsitePolicyTransport.NormalizeTargets(
            WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var studentSidsByTarget = MobileStudentSidsByTarget(targets);
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
            StudentSids: [], ApplicationRules: ParseApplicationRules(),
            UpdatedUtc: DateTimeOffset.UtcNow, StudentSidsByTarget: studentSidsByTarget));
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
        var targets = WebsitePolicyTransport.NormalizeTargets(
            WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var studentSidsByTarget = MobileStudentSidsByTarget(targets, allowManualInput: false);
        var existing = MobilePolicyProfileStore.ReadAll()
            .FirstOrDefault(profile => profile.Kind == MobilePolicyProfileKind.System &&
                                       profile.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                       profile.CampusId == campus);
        var profile = MobilePolicyProfileCompiler.Validate(new MobilePolicyProfile(
            existing?.Id ?? Guid.NewGuid(), name, campus, MobilePolicyProfileKind.System, 0,
            StudentSids: [], StudentSidsByTarget: studentSidsByTarget, UpdatedUtc: DateTimeOffset.UtcNow,
            SystemSettings: settings,
            SoftwareInstallationImpactReviewed: settings.ProhibitSoftwareInstallation &&
                                                  _studentSystemPolicySoftwareInstallReviewed));
        MobilePolicyProfileStore.Save(profile);
        return profile;
    }

    public IReadOnlyList<MobilePolicyProfile> ReadMobilePolicyProfiles() => MobilePolicyProfileStore.ReadAll();

    public bool DeleteMobilePolicyProfile(Guid id) => MobilePolicyProfileStore.Remove(id);

    internal string MobilePolicyProfileAccountSummary
    {
        get
        {
            if (!UseManualApplicationInputs) return StudentAccountSelectionSummary;
            int targetCount;
            try
            {
                targetCount = WebsitePolicyTransport.NormalizeTargets(
                    WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Count;
            }
            catch (InvalidDataException)
            {
                return "电脑名单无效。应用预设使用高级手动 SID；系统预设仍按逐电脑账户清单。";
            }
            var applicationSummary = targetCount > 1
                ? "应用预设已启用手动 SID；多台电脑的应用预设不能保存。"
                : "应用预设会使用高级手动 SID。";
            return $"{applicationSummary} 系统预设仍使用逐电脑账户清单：{StudentAccountSelectionSummary}";
        }
    }

    internal IReadOnlyList<string> MobilePolicyProfileSelectedAccountNames => Array.AsReadOnly(
        _studentAccountChoices.Where(choice => choice.IsSelected)
            .Select(choice => choice.Summary).ToArray());

    internal async Task<(string Summary, IReadOnlyList<string> ComputerNames)> GetMobilePolicyProfileTargetSummaryAsync()
    {
        IReadOnlyList<string> targets;
        try
        {
            targets = WebsitePolicyTransport.NormalizeTargets(
                WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        }
        catch (InvalidDataException)
        {
            return ("电脑名单无效，请关闭此窗口并先检查目标电脑。", Array.Empty<string>());
        }

        if (targets.Count == 0)
            return ("当前没有目标电脑。请先在教师端选择电脑后再保存预设。", Array.Empty<string>());

        IReadOnlyDictionary<string, string> displayNames;
        try
        {
            displayNames = await ResolveTargetDisplayNamesAsync(targets);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          PlatformNotSupportedException or TimeoutException or
                                          System.ComponentModel.Win32Exception)
        {
            displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        var labels = targets.Select(target => GetTargetDisplayName(target, displayNames)).ToArray();
        const int visibleNameLimit = 5;
        var visibleNames = string.Join("、", labels.Take(visibleNameLimit));
        var remainingCount = labels.Length - visibleNameLimit;
        var summary = remainingCount > 0
            ? $"学生名单：{targets.Count} 名（{visibleNames} 等另 {remainingCount} 名）"
            : $"学生名单：{targets.Count} 名（{visibleNames}）";
        return (summary, Array.AsReadOnly(labels));
    }

    private TimeSpan ApplicationPolicyLifetime() => ApplicationPolicyDurationIndex switch
    {
        0 => TimeSpan.FromMinutes(45),
        1 => TimeSpan.FromHours(1),
        2 => TimeSpan.FromMinutes(90),
        3 => TimeSpan.FromHours(2),
        4 => TimeSpan.FromHours(24),
        _ => throw new InvalidDataException("应用限制有效时长无效。")
    };

    private IReadOnlyDictionary<string, IReadOnlyList<string>> MobileStudentSidsByTarget(
        IReadOnlyList<string> targets, bool allowManualInput = true)
    {
        if (targets.Count == 0)
            throw new InvalidDataException("请先选择目标电脑，再为每台电脑读取并选择学生账户。");
        if (allowManualInput && UseManualApplicationInputs && targets.Count > 1)
            throw new InvalidDataException("多台电脑请从电脑列表逐台读取并选择学生账户；手动输入的 SID 只适用于单台电脑。");
        return StudentSidsByTarget(targets, disabled: false, useManualInputs: allowManualInput)
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    private string[] ParseStudentSids(bool allowManualInput = true)
    {
        if (!allowManualInput || !UseManualApplicationInputs)
        {
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var values = StudentSidsByTarget(targets, disabled: false, useManualInputs: false)
                .Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length > ApplicationPolicyCompiler.MaximumStudents) throw new InvalidDataException("一次最多选择 150 个学生账户。");
            return values;
        }
        var sids = ApplicationStudentSids.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        if (sids.Length is 0 or > ApplicationPolicyCompiler.MaximumStudents ||
            sids.Distinct(StringComparer.Ordinal).Count() != sids.Length)
            throw new InvalidDataException("学生账户 SID 无效：每行一个，最多 150 个，不能重复。");
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
                throw new InvalidDataException("规则格式有误；请展开“查看手动输入格式”。");
        }
        return rules.ToArray();
    }

    private static Guid StableApplicationRuleId(string value) =>
        new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    public async Task DeployStudentUpdateAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            ClearStudentUpdateResults();
            ClearStudentUpdateTargetRows();
            StudentUpdateStatus = "学生端更新仅支持 Windows。";
            return;
        }
        if (!CanDeployStudentUpdate || !TryBeginExclusiveTask()) return;
        ClearStudentUpdateResults();
        ClearStudentUpdateTargetRows();
        _lastStudentUpdatePerformanceReport = null;
        Changed(nameof(HasStudentUpdatePerformanceReport));
        StudentUpdatePerformanceReportExportStatus = "";
        Changed(nameof(StudentUpdatePerformanceReportExportStatus));
        var rolloutId = Guid.NewGuid();
        var rolloutStartedUtc = DateTimeOffset.UtcNow;
        var rolloutTimer = Stopwatch.StartNew();
        StudentUpdateStatus = "正在检查学生端版本……";
        SetStudentUpdateProgress(true, true, 0, "正在查询签名发布信息……");
        string? targetVersion = null;
        string? preparationSummary = null;
        long? releaseLookupMilliseconds = null;
        long? teacherCacheReadyMilliseconds = null;
        bool? packageCacheHit = null;
        var teacherFetchedBytes = new long[1];
        long? teacherFetchMilliseconds = null;
        long? artifactSizeBytes = null;
        var selectedTargetCount = 0;
        var alreadyCurrentCount = 0;
        var rolloutTargetsToUpdate = new List<string>();
        var preflightResults = new List<StudentApplicationUpdatePushResult>();
        var deliveryResults = new List<StudentApplicationUpdatePushResult>();
        var updateSourceVersions = new Dictionary<string, StudentApplicationUpdateSourceVersions>(
            StringComparer.OrdinalIgnoreCase);
        StudentUpdateProgressCollector? progressCollector = null;
        var studentUpdateDispatchStarted = false;
        try
        {
            var releaseClient = _releaseClient ?? throw new InvalidOperationException(
                _releaseClientError ?? "此版本暂不支持学生端更新。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(
                GetSelectedWebsitePolicyTargets());
            selectedTargetCount = targets.Count;
            var displayNames = await ResolveTargetDisplayNamesAsync(targets);
            await PrepareStudentUpdateTargetRowsAsync(targets, displayNames);
            ApplicationReleaseCheckResult latest;
            var releaseLookupTimer = Stopwatch.StartNew();
            try
            {
                latest = await releaseClient.CheckLatestAsync(ApplicationReleaseRole.StudentSetup, "0.0.0");
            }
            finally
            {
                releaseLookupTimer.Stop();
                releaseLookupMilliseconds = releaseLookupTimer.ElapsedMilliseconds;
                preparationSummary = $"发布查询 {releaseLookupMilliseconds.Value / 1000d:0.0} 秒";
            }
            var release = latest.Release ?? throw new InvalidOperationException("暂时没有可用的学生端更新。");
            targetVersion = release.Manifest.Version;
            artifactSizeBytes = release.Manifest.SizeBytes;
            ApplicationReleaseCompatibility.EnsureSupports(release.Manifest,
                applicationPolicyRequired: true, studentSystemPolicyRequired: true);

            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            StudentUpdateStatus = $"正在读取 {targets.Count} 台电脑的 StudentSetup 与 SYSTEM Agent 版本……";
            SetStudentUpdateProgress(true, true, 0, "正在核对每台学生电脑的已安装版本……");
            var preflight = await WebsitePolicyStatusTransport.ReadAsync(targets, campus,
                signingKey.PrivateKey);
            foreach (var target in targets)
            {
                var row = FindStudentUpdateTargetRow(target);
                var observed = preflight.FirstOrDefault(item =>
                    string.Equals(NormalizeTargetKey(item.Target), NormalizeTargetKey(target),
                        StringComparison.OrdinalIgnoreCase));
                if (observed is not { Succeeded: true, Status: not null })
                {
                    var detail = observed?.Detail ?? "没有收到此电脑的版本状态。";
                    RemoveStudentUpdateTargetRow(target);
                    preflightResults.Add(new StudentApplicationUpdatePushResult(target, false, detail,
                        NeedsReview: true, IdentityCandidate: observed?.IdentityCandidate));
                    continue;
                }

                ShowStudentUpdateTargetRow(row!);

                var status = observed.Status;
                var agentComparison = ApplicationReleaseClient.CompareVersions(status.AgentVersion,
                    release.Manifest.Version);
                if (status.StudentSetupVersion is null)
                {
                    var detail = $"已读回 SYSTEM Agent {status.AgentVersion}，但此电脑没有可核对的 StudentSetup 版本与原子事务回执，已停止远程更新。请从官网下载当前正式版完整 StudentSetup 安装包，在这台电脑上覆盖安装一次；无需先卸载，安装器询问是否清理本机配置时请选择保留。完成后教师端才能安全接管后续远程更新，这不是等待后会自动恢复的问题。";
                    row?.SetResult(succeeded: false, needsReview: true, detail);
                    preflightResults.Add(new StudentApplicationUpdatePushResult(target, false, detail,
                        NeedsReview: true, IdentityCandidate: observed.IdentityCandidate));
                    continue;
                }

                var setupComparison = ApplicationReleaseClient.CompareVersions(status.StudentSetupVersion,
                    release.Manifest.Version);
                if (setupComparison >= 0 && agentComparison >= 0)
                {
                    var detail = $"StudentSetup {status.StudentSetupVersion}；SYSTEM Agent {status.AgentVersion}。两者都已达到目标版本 {release.Manifest.Version}，已跳过更新。";
                    row?.SetCurrent(detail);
                    alreadyCurrentCount++;
                    continue;
                }
                if (setupComparison > 0 && agentComparison < 0)
                {
                    var detail = $"StudentSetup {status.StudentSetupVersion} 高于可用版本 {release.Manifest.Version}，但 SYSTEM Agent 为 {status.AgentVersion}。不会降级 StudentSetup；请先发布匹配的学生端版本。";
                    row?.SetResult(succeeded: false, needsReview: true, detail);
                    preflightResults.Add(new StudentApplicationUpdatePushResult(target, false, detail,
                        NeedsReview: true, IdentityCandidate: observed.IdentityCandidate));
                    continue;
                }

                if (status.StudentUpdateProtocolVersion !=
                    WebsitePolicyStatusCryptography.CurrentStudentUpdateProtocolVersion)
                {
                    var detail = $"此学生端缺少当前版本所需的原子更新协议回执（当前 Agent {status.AgentVersion}），已停止远程更新。请从官网下载当前正式版完整 StudentSetup 安装包，在这台电脑上覆盖安装一次；无需先卸载，安装器询问是否清理本机配置时请选择保留。完成后教师端才能安全接管后续远程更新，这不是等待后会自动恢复的问题。";
                    row?.SetResult(succeeded: false, needsReview: true, detail);
                    preflightResults.Add(new StudentApplicationUpdatePushResult(target, false, detail,
                        NeedsReview: true, IdentityCandidate: observed.IdentityCandidate));
                    continue;
                }

                row?.SetCheckingVersions($"当前 StudentSetup {status.StudentSetupVersion} · SYSTEM Agent {status.AgentVersion}；需要更新到 {release.Manifest.Version}。");
                rolloutTargetsToUpdate.Add(target);
                updateSourceVersions[target] = new StudentApplicationUpdateSourceVersions(
                    status.StudentSetupVersion, status.AgentVersion, status.StudentUpdateProtocolVersion.Value);
            }

            if (rolloutTargetsToUpdate.Count == 0)
            {
                var needsReviewCount = preflightResults.Count(result => result.NeedsReview);
                var failedCount = preflightResults.Count(result => !result.NeedsReview);
                RecordUpdateSuccess(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.StudentRollout,
                    release.Manifest.Version, new UpdateDiagnosticCounts(alreadyCurrentCount, needsReviewCount, failedCount));
                StudentUpdateStatus = needsReviewCount == 0 && failedCount == 0
                    ? $"学生端 {release.Manifest.Version} · 已核对 {alreadyCurrentCount}/{targets.Count} 台，均已是最新版本，无需更新。"
                    : $"无需下发安装包 · 已是最新 {alreadyCurrentCount} · 待核对 {needsReviewCount} · 失败 {failedCount}";
                StudentUpdateStatus += $" · {preparationSummary}";
                SetStudentUpdateProgress(false, true, 0, "");
                foreach (var result in preflightResults)
                {
                    if (!_studentUpdateRowsByTarget.TryGetValue(NormalizeTargetKey(result.Target), out var row)) continue;
                    row.SetResult(result.Succeeded, result.NeedsReview, result.Detail, result.RolledBack);
                }
                SortStudentUpdateTargetRows();
                return;
            }

            var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "Updates");
            StudentUpdateStatus = $"正在准备学生端更新 {release.Manifest.Version}（{rolloutTargetsToUpdate.Count} 台需要更新）……";
            SetStudentUpdateProgress(true, false, 0,
                $"正在从 GitCode 下载并校验 StudentSetup（0 / {FormatMegabytes(release.Manifest.SizeBytes)} MB）");
            var teacherFetchTimer = Stopwatch.StartNew();
            string installerPath;
            try
            {
                installerPath = await releaseClient.DownloadAsync(release, ApplicationReleaseRole.StudentSetup,
                    updateDirectory, progress: new Progress<InstallerResourceProgress>(value =>
                        SetStudentResourceProgress(value)),
                    cacheStatus: cacheHit => packageCacheHit = cacheHit,
                    networkBytesProgress: bytes => Interlocked.Exchange(ref teacherFetchedBytes[0], bytes));
            }
            catch
            {
                teacherFetchTimer.Stop();
                teacherFetchMilliseconds = teacherFetchTimer.ElapsedMilliseconds;
                var failedDownloadMegabytes = Interlocked.Read(ref teacherFetchedBytes[0]) / (1024d * 1024d);
                preparationSummary = $"发布查询 {releaseLookupMilliseconds.GetValueOrDefault() / 1000d:0.0} 秒 · " +
                                     $"教师下载/校验失败（已接收 {failedDownloadMegabytes:0.0} MB，" +
                                     $"耗时 {teacherFetchTimer.Elapsed.TotalSeconds:0.0} 秒）";
                throw;
            }
            teacherFetchTimer.Stop();
            teacherFetchMilliseconds = teacherFetchTimer.ElapsedMilliseconds;
            teacherCacheReadyMilliseconds = rolloutTimer.ElapsedMilliseconds;
            var teacherFetchSeconds = Math.Max(teacherFetchTimer.Elapsed.TotalSeconds, 0.001);
            var teacherFetchMegabytes = release.Manifest.SizeBytes / (1024d * 1024d);
            var downloadedMegabytes = Interlocked.Read(ref teacherFetchedBytes[0]) / (1024d * 1024d);
            var teacherFetchSummary = packageCacheHit == true
                ? $"本机缓存命中 {teacherFetchMegabytes:0.0} MB · 校验 {teacherFetchSeconds:0.0} 秒"
                : $"教师下载并校验 {downloadedMegabytes:0.0} MB / {teacherFetchSeconds:0.0} 秒" +
                  $"（{downloadedMegabytes / teacherFetchSeconds:0.0} MB/s）";
            preparationSummary = $"发布查询 {releaseLookupMilliseconds.GetValueOrDefault() / 1000d:0.0} 秒 · {teacherFetchSummary}";
            SetStudentUpdateProgress(false, true, 100, "");
            StudentUpdateStatus = $"安装包 {release.Manifest.Version} 已校验（{preparationSummary}）· 正在发送给 {rolloutTargetsToUpdate.Count} 台需要更新的电脑";
            var lanProgress = new Progress<StudentApplicationUpdateLanProgress>(value =>
            {
                var percent = value.TotalBytes > 0
                    ? Math.Clamp(value.BytesSent * 100d / value.TotalBytes, 0d, 100d)
                    : 0d;
                FindStudentUpdateTargetRow(value.Target)?.SetTransferProgress(value.BytesSent,
                    value.TotalBytes, percent);
            });
            await using var lanServer = StudentApplicationUpdateLanServer.Start(installerPath,
                release.Manifest.Sha256, release.Manifest.SizeBytes, lanProgress);
            var developerPublicKey = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
            var reportedCompletedTargets = 0;
            var uiTransportProgress = new Progress<StudentApplicationUpdateProgress>(value =>
            {
                reportedCompletedTargets = Math.Max(reportedCompletedTargets, value.CompletedTargets);
                if (!string.IsNullOrWhiteSpace(value.Target))
                {
                    var row = FindStudentUpdateTargetRow(value.Target);
                    row?.SetTransportProgress(value.Stage);
                    if (value.PhaseTimings is { Count: > 0 } timings)
                        row?.SetPhaseTimings(timings);
                }
                StudentUpdateStatus = $"学生端更新 {release.Manifest.Version} · 已处理 {reportedCompletedTargets}/{value.TotalTargets} 台需要更新的电脑";
            });
            progressCollector = new StudentUpdateProgressCollector(uiTransportProgress);
            studentUpdateDispatchStarted = true;
            var deliveries = await StudentApplicationUpdateTransport.PushAsync(rolloutTargetsToUpdate, campus,
                release.Manifest.Version, signingKey.PrivateKey,
                async (target, pinnedAgentPublicKey, downloadRequestProtocolVersion, token) =>
            {
                var issuedUtc = DateTimeOffset.UtcNow;
                var commandId = Guid.NewGuid();
                var expiresUtc = issuedUtc + StudentApplicationUpdateCryptography.MaximumCommandLifetime;
                var downloadRequestKey = downloadRequestProtocolVersion ==
                                         StudentApplicationUpdateCryptography.DownloadRequestProtocolVersion
                    ? pinnedAgentPublicKey
                    : null;
                var downloadUri = await lanServer.GetDownloadUriAsync(target, campus, commandId, expiresUtc,
                    downloadRequestKey, token);
                var command = new StudentApplicationUpdateCommand(1, campus, commandId, issuedUtc,
                    expiresUtc, release,
                    downloadUri.AbsoluteUri);
                lock (signingKey.PrivateKey)
                    return (command.CommandId, StudentApplicationUpdateCryptography.Sign(command,
                        signingKey.PrivateKey, releaseClient.ApiBaseAddress, developerPublicKey));
            }, progress: progressCollector, sourceVersions: updateSourceVersions);
            deliveryResults.AddRange(deliveries);
            var succeeded = deliveries.Count(result => result.Succeeded);
            var needsReview = deliveries.Count(result => result.NeedsReview) +
                              preflightResults.Count(result => result.NeedsReview);
            var failed = deliveries.Count - succeeded - deliveries.Count(result => result.NeedsReview) +
                         preflightResults.Count(result => !result.Succeeded && !result.NeedsReview);
            RecordUpdateSuccess(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.StudentRollout,
                release.Manifest.Version, new UpdateDiagnosticCounts(succeeded + alreadyCurrentCount, needsReview, failed));
            StudentUpdateStatus = $"学生端 {release.Manifest.Version} · 已是最新 {alreadyCurrentCount} · 更新成功 {succeeded} · {needsReview} 待核对 · {failed} 失败" +
                                  (preparationSummary is null ? "" : $" · {preparationSummary}");
            foreach (var result in preflightResults)
            {
                if (!_studentUpdateRowsByTarget.TryGetValue(NormalizeTargetKey(result.Target), out var row)) continue;
                row.SetResult(result.Succeeded, result.NeedsReview, result.Detail, result.RolledBack);
            }
            foreach (var delivery in deliveries)
            {
                if (!_studentUpdateRowsByTarget.TryGetValue(NormalizeTargetKey(delivery.Target), out var row)) continue;
                var detail = SanitizeStudentUpdateDetail(delivery.Detail, delivery.Target, row.DisplayName);
                row.SetResult(delivery.Succeeded, delivery.NeedsReview, detail, delivery.RolledBack);
            }
            SortStudentUpdateTargetRows();
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.StudentRollout, targetVersion, exception);
            StudentUpdateStatus = "更新未完成 · " + failure.ToUserMessage() +
                                  (preparationSummary is null ? "" : $" · {preparationSummary}");
            if (StudentUpdateTargetProgressItems.Count == 0)
                StudentUpdateFailedDetails = failure.ToUserMessage();
            SetStudentUpdateProgress(false, true, _studentUpdateProgressPercent, "");
        }
        finally
        {
            SetStudentUpdateProgress(false, true, 0, "");
            rolloutTimer.Stop();
            try
            {
                var samples = BuildStudentUpdatePerformanceSamples(preflightResults, deliveryResults,
                    rolloutTargetsToUpdate, updateSourceVersions, progressCollector, studentUpdateDispatchStarted);
                var completedTargetKeys = new HashSet<string>(deliveryResults.Select(result =>
                    NormalizeTargetKey(result.Target)), StringComparer.OrdinalIgnoreCase);
                var unresolvedCount = studentUpdateDispatchStarted
                    ? rolloutTargetsToUpdate.Count(target => !completedTargetKeys.Contains(NormalizeTargetKey(target)))
                    : 0;
                var notStartedCount = Math.Max(0, selectedTargetCount - alreadyCurrentCount - preflightResults.Count -
                                                   deliveryResults.Count - unresolvedCount);
                var report = StudentUpdatePerformanceReportBuilder.Create(rolloutId,
                    rolloutStartedUtc, DateTimeOffset.UtcNow, rolloutTimer.ElapsedMilliseconds, targetVersion,
                    selectedTargetCount, alreadyCurrentCount, preflightResults.Count, notStartedCount,
                    unresolvedCount, rolloutTargetsToUpdate.Count > 0,
                    new StudentUpdatePerformancePreparation(releaseLookupMilliseconds,
                        teacherCacheReadyMilliseconds, packageCacheHit, teacherFetchMilliseconds,
                        teacherFetchMilliseconds is null ? null : Interlocked.Read(ref teacherFetchedBytes[0]),
                        artifactSizeBytes), samples, _studentUpdatePerformanceBatchHistory.ToArray());
                _lastStudentUpdatePerformanceReport = report;
                if (selectedTargetCount > 0)
                {
                    var batchFailed = report.Counts.FailedCount > 0 || report.Counts.RolledBackCount > 0 ||
                                      report.Counts.NeedsReviewCount > 0 || report.Counts.NotStartedCount > 0 ||
                                      report.Counts.UnresolvedCount > 0;
                    _studentUpdatePerformanceBatchHistory.Add(new StudentUpdatePerformanceBatchObservation(
                        report.BatchSizeBand, rolloutTargetsToUpdate.Count > 0, teacherCacheReadyMilliseconds,
                        rolloutTimer.ElapsedMilliseconds, batchFailed));
                    if (_studentUpdatePerformanceBatchHistory.Count >
                        StudentUpdatePerformanceReportBuilder.MaximumBatchObservations)
                        _studentUpdatePerformanceBatchHistory.RemoveAt(0);
                }
                Changed(nameof(HasStudentUpdatePerformanceReport));
            }
            catch (Exception)
            {
                _lastStudentUpdatePerformanceReport = null;
                Changed(nameof(HasStudentUpdatePerformanceReport));
                StudentUpdatePerformanceReportExportStatus = "本次性能汇总暂不可用；更新结果不受影响。";
                Changed(nameof(StudentUpdatePerformanceReportExportStatus));
            }
            EndExclusiveTask();
        }
    }

    private static IReadOnlyList<StudentUpdatePerformanceSample> BuildStudentUpdatePerformanceSamples(
        IReadOnlyList<StudentApplicationUpdatePushResult> preflightResults,
        IReadOnlyList<StudentApplicationUpdatePushResult> deliveryResults,
        IReadOnlyList<string> targetsToUpdate,
        IReadOnlyDictionary<string, StudentApplicationUpdateSourceVersions> sourceVersions,
        StudentUpdateProgressCollector? progressCollector, bool dispatchStarted)
    {
        var samples = new List<StudentUpdatePerformanceSample>(preflightResults.Count + targetsToUpdate.Count);
        foreach (var result in preflightResults)
            samples.Add(new StudentUpdatePerformanceSample(GetPerformanceOutcome(result), Attempted: false,
                null, null, StudentUpdateEnvironmentCategories.Unknown,
                Array.Empty<StudentUpdatePhaseTiming>()));

        var sourcesByTarget = new Dictionary<string, StudentApplicationUpdateSourceVersions>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in sourceVersions)
            sourcesByTarget[NormalizeTargetKey(pair.Key)] = pair.Value;
        var deliveredTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in deliveryResults)
        {
            var targetKey = NormalizeTargetKey(result.Target);
            deliveredTargets.Add(targetKey);
            sourcesByTarget.TryGetValue(targetKey, out var source);
            CollectedStudentUpdateTimings? timing = null;
            progressCollector?.TryGet(result.Target, out timing);
            samples.Add(new StudentUpdatePerformanceSample(GetPerformanceOutcome(result), Attempted: true,
                source?.StudentSetupVersion, source?.AgentVersion,
                timing?.Environment ?? StudentUpdateEnvironmentCategories.Unknown,
                timing?.Phases ?? Array.Empty<StudentUpdatePhaseTiming>()));
        }

        if (dispatchStarted)
        {
            foreach (var target in targetsToUpdate)
            {
                if (deliveredTargets.Contains(NormalizeTargetKey(target))) continue;
                sourcesByTarget.TryGetValue(NormalizeTargetKey(target), out var source);
                CollectedStudentUpdateTimings? timing = null;
                progressCollector?.TryGet(target, out timing);
                samples.Add(new StudentUpdatePerformanceSample("needsReview", Attempted: true,
                    source?.StudentSetupVersion, source?.AgentVersion,
                    timing?.Environment ?? StudentUpdateEnvironmentCategories.Unknown,
                    timing?.Phases ?? Array.Empty<StudentUpdatePhaseTiming>()));
            }
        }
        return Array.AsReadOnly(samples.ToArray());
    }

    private static string GetPerformanceOutcome(StudentApplicationUpdatePushResult result) =>
        result.Succeeded ? "succeeded" : result.RolledBack ? "rolledBack" :
        result.NeedsReview ? "needsReview" : "failed";

    private async Task PrepareStudentUpdateTargetRowsAsync(IReadOnlyList<string> targets,
        IReadOnlyDictionary<string, string> displayNames)
    {
        _studentUpdateRowsByTarget.Clear();
        _studentUpdateTargetsByAddress.Clear();
        StudentUpdateTargetProgressItems.Clear();
        foreach (var target in targets)
        {
            var row = new StudentApplicationUpdateTargetProgressItem(GetStudentUpdateDisplayName(target, displayNames));
            _studentUpdateRowsByTarget[NormalizeTargetKey(target)] = row;
            AddStudentUpdateAddressAlias(target, target);
            if (Uri.CheckHostName(target) is UriHostNameType.IPv4 or UriHostNameType.IPv6) continue;
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(target);
                foreach (var address in addresses) AddStudentUpdateAddressAlias(address.ToString(), target);
            }
            catch (Exception exception) when (exception is System.Net.Sockets.SocketException or
                                               InvalidOperationException or ArgumentException)
            {
                _ = exception;
            }
        }
    }

    private void ShowStudentUpdateTargetRow(StudentApplicationUpdateTargetProgressItem row)
    {
        if (StudentUpdateTargetProgressItems.Contains(row)) return;
        StudentUpdateTargetProgressItems.Add(row);
        Changed(nameof(HasStudentUpdateTargetProgressItems));
    }

    private void RemoveStudentUpdateTargetRow(string target)
    {
        var key = NormalizeTargetKey(target);
        if (_studentUpdateRowsByTarget.Remove(key, out var row))
        {
            StudentUpdateTargetProgressItems.Remove(row);
            Changed(nameof(HasStudentUpdateTargetProgressItems));
        }
        foreach (var alias in _studentUpdateTargetsByAddress.Where(pair =>
                     string.Equals(NormalizeTargetKey(pair.Value), key, StringComparison.OrdinalIgnoreCase))
                 .Select(pair => pair.Key).ToArray())
            _studentUpdateTargetsByAddress.Remove(alias);
    }

    private string GetStudentUpdateDisplayName(string target,
        IReadOnlyDictionary<string, string> displayNames)
    {
        var choice = WebsitePolicyTargetChoices.FirstOrDefault(item =>
            string.Equals(NormalizeTargetKey(item.Target), NormalizeTargetKey(target), StringComparison.OrdinalIgnoreCase));
        return choice?.StudentName ?? GetTargetDisplayName(target, displayNames);
    }

    private void AddStudentUpdateAddressAlias(string address, string target)
    {
        if (Uri.CheckHostName(address) is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            _studentUpdateTargetsByAddress[NormalizeTargetKey(address)] = target;
    }

    private StudentApplicationUpdateTargetProgressItem? FindStudentUpdateTargetRow(string address)
    {
        var key = NormalizeTargetKey(address);
        if (_studentUpdateRowsByTarget.TryGetValue(key, out var exact)) return exact;
        return _studentUpdateTargetsByAddress.TryGetValue(key, out var target) &&
               _studentUpdateRowsByTarget.TryGetValue(NormalizeTargetKey(target), out var byAddress)
            ? byAddress
            : _studentUpdateRowsByTarget.Count == 1 ? _studentUpdateRowsByTarget.Values.FirstOrDefault() : null;
    }

    private static string SanitizeStudentUpdateDetail(string detail, string target, string displayName) =>
        string.IsNullOrWhiteSpace(detail) ? "" : detail.Replace(target, displayName, StringComparison.OrdinalIgnoreCase);

    private void SortStudentUpdateTargetRows()
    {
        var ordered = StudentUpdateTargetProgressItems.OrderBy(item => item.Priority)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var currentIndex = StudentUpdateTargetProgressItems.IndexOf(ordered[index]);
            if (currentIndex != index) StudentUpdateTargetProgressItems.Move(currentIndex, index);
        }
    }

    private void ClearStudentUpdateTargetRows()
    {
        _studentUpdateRowsByTarget.Clear();
        _studentUpdateTargetsByAddress.Clear();
        StudentUpdateTargetProgressItems.Clear();
        Changed(nameof(HasStudentUpdateTargetProgressItems));
    }

    private void SetTeacherResourceProgress(InstallerResourceProgress value)
    {
        var percent = value.TotalBytes > 0
            ? Math.Clamp(value.BytesReceived * 100d / value.TotalBytes, 0d, 100d)
            : 0d;
        var stage = value.Stage == "verify" ? "正在复核已下载的教师端安装包" : "正在从 GitCode 下载教师端安装包";
        SetTeacherUpdateProgress(true, false, percent,
            value.TotalBytes > 0
                ? $"{stage}：{FormatMegabytes(value.BytesReceived)} / {FormatMegabytes(value.TotalBytes)} MB · {percent:0}%"
                : $"{stage}：{FormatMegabytes(value.BytesReceived)} MB");
    }

    private void SetTeacherUpdateProgress(bool visible, bool indeterminate, double percent, string text)
    {
        if (_isTeacherUpdateProgressVisible != visible)
        {
            _isTeacherUpdateProgressVisible = visible;
            Changed(nameof(IsTeacherUpdateProgressVisible));
        }
        if (_teacherUpdateProgressIsIndeterminate != indeterminate)
        {
            _teacherUpdateProgressIsIndeterminate = indeterminate;
            Changed(nameof(TeacherUpdateProgressIsIndeterminate));
        }
        percent = Math.Clamp(percent, 0d, 100d);
        if (Math.Abs(_teacherUpdateProgressPercent - percent) > 0.01)
        {
            _teacherUpdateProgressPercent = percent;
            Changed(nameof(TeacherUpdateProgressPercent));
        }
        if (_teacherUpdateProgressText != text)
        {
            _teacherUpdateProgressText = text;
            Changed(nameof(TeacherUpdateProgressText));
        }
    }

    private void SetStudentResourceProgress(InstallerResourceProgress value)
    {
        var percent = value.TotalBytes > 0
            ? Math.Clamp(value.BytesReceived * 100d / value.TotalBytes, 0d, 100d)
            : 0d;
        var stage = value.Stage == "verify"
            ? "正在复核已下载的 StudentSetup 安装包"
            : "正在从 GitCode 下载 StudentSetup";
        SetStudentUpdateProgress(true, false, percent,
            value.TotalBytes > 0
                ? $"{stage}：{FormatMegabytes(value.BytesReceived)} / {FormatMegabytes(value.TotalBytes)} MB · {percent:0}%"
                : $"{stage}：{FormatMegabytes(value.BytesReceived)} MB");
    }

    private void ClearStudentUpdateResults()
    {
        StudentUpdateSucceededDetails = "";
        StudentUpdateNeedsReviewDetails = "";
        StudentUpdateFailedDetails = "";
    }

    private string FormatStudentUpdateResults(
        IEnumerable<StudentApplicationUpdatePushResult> results) =>
        string.Join(Environment.NewLine, results.Select(result =>
        {
            var studentName = GetStudentDisplayNameForTarget(result.Target);
            var detail = SanitizeStudentUpdateDetail(result.Detail, result.Target, studentName);
            return detail.Length == 0 ? studentName : $"{studentName}：{detail}";
        }));

    private void SetStudentUpdateProgress(bool visible, bool indeterminate, double percent, string text)
    {
        if (_isStudentUpdateProgressVisible != visible)
        {
            _isStudentUpdateProgressVisible = visible;
            Changed(nameof(IsStudentUpdateProgressVisible));
        }
        if (_studentUpdateProgressIsIndeterminate != indeterminate)
        {
            _studentUpdateProgressIsIndeterminate = indeterminate;
            Changed(nameof(StudentUpdateProgressIsIndeterminate));
        }
        percent = Math.Clamp(percent, 0d, 100d);
        if (Math.Abs(_studentUpdateProgressPercent - percent) > 0.01)
        {
            _studentUpdateProgressPercent = percent;
            Changed(nameof(StudentUpdateProgressPercent));
        }
        if (_studentUpdateProgressText != text)
        {
            _studentUpdateProgressText = text;
            Changed(nameof(StudentUpdateProgressText));
        }
    }

    private static string FormatMegabytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.CurrentCulture);

    private static string FormatWebsiteDomainList(string raw)
    {
        var entries = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        return entries.Length == 0 ? "未设置" : string.Join("、", entries);
    }

    private async Task PushWebsitePolicyAsync(WebsitePolicyMode mode)
    {
        if (!TryBeginExclusiveTask()) return;
        WebsitePolicyResult = "";
        WebsitePolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("请在 Windows 教师端设置学生代理限制。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            var domains = mode switch
            {
                WebsitePolicyMode.Disabled => Array.Empty<string>(),
                WebsitePolicyMode.Allowlist => WebsiteAllowDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
                _ => WebsiteBlockDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            };
            var allowedDomains = mode == WebsitePolicyMode.Combined
                ? WebsiteAllowDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                : null;
            var issuedUtc = DateTimeOffset.UtcNow;
            DateTimeOffset? expiresUtc = mode == WebsitePolicyMode.Disabled
                ? null : GetWebsiteDeadlineUtc(issuedUtc);
            var revision = WebsitePolicyRevisionStore.Next(campus);
            var policy = WebsitePolicyCompiler.Create(campus, revision, mode, domains, issuedUtc, expiresUtc,
                allowedDomains);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var signedPolicy = WebsitePolicyCryptography.Sign(policy, signingKey.PrivateKey);
            var displayNamesTask = ResolveTargetDisplayNamesAsync(targets);
            var results = await WebsitePolicyTransport.PushAsync(targets, signedPolicy, campus);
            var displayNames = await displayNamesTask;
            var succeeded = results.Count(result => result.Succeeded);
            var realtimeAccepted = results.Count(result => result.Succeeded && result.RealtimeAccepted);
            var browserRefreshPending = results.Any(result => result.Succeeded && !result.RealtimeAccepted);
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count(result => !result.Succeeded && !result.NeedsReview);
            var expirySummary = expiresUtc is { } expiry
                ? $" · 自动解除 {expiry.ToLocalTime():yyyy-MM-dd HH:mm}"
                : mode == WebsitePolicyMode.Disabled ? "" : " · 需手动解除";
            var resultLabel = realtimeAccepted > 0 && realtimeAccepted == results.Count
                ? "限制已写入，Edge 正在同步"
                : realtimeAccepted > 0 && realtimeAccepted == succeeded ? "部分设备已提交，Edge 正在同步"
                : realtimeAccepted > 0 || browserRefreshPending ? "策略已写入，浏览器刷新待核对"
                : mode switch
            {
                WebsitePolicyMode.Disabled => succeeded == results.Count ? "学生代理限制已解除" : "解除结果",
                WebsitePolicyMode.Blocklist or WebsitePolicyMode.Combined or WebsitePolicyMode.Allowlist =>
                    succeeded == results.Count ? "网站限制已应用" : "网站限制结果",
                _ => "网站限制结果"
            };
            WebsitePolicyResult = $"{resultLabel}{expirySummary} · Agent 已确认 {succeeded}/{results.Count} · 待核对 {needsReview} · 失败 {failed}";
            var policyDetails = mode == WebsitePolicyMode.Disabled ? Array.Empty<string>() : new[]
            {
                "要阻止的网站：" + FormatWebsiteDomainList(WebsiteBlockDomains),
                "允许的网站：" + FormatWebsiteDomainList(WebsiteAllowDomains),
                "截止时间：" + expiresUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            };
            WebsitePolicyResultDetails = string.Join(Environment.NewLine, policyDetails.Concat(results.Select(result =>
                $"{GetWebsitePolicyStudentName(result.Target)}：{(result.StatusLabel)} — {result.Detail}")));
            ShowWebsitePolicyResultDetails = realtimeAccepted > 0 || needsReview > 0 || failed > 0;
            var history = new WebsitePolicyPushHistoryEntry(DateTimeOffset.UtcNow, campus, revision, mode, expiresUtc, results);
            UpdateWebsitePolicyHistory(history);
            var errorMessages = new List<string>();
            try { WebsitePolicyPushHistoryStore.Append(history); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                              ArgumentException or System.Text.Json.JsonException)
            {
                errorMessages.Add("结果已显示，但本机未保存历史：" + exception.Message);
            }
            if (succeeded != results.Count)
                errorMessages.Add(string.Join(" ", new[]
                {
                    results.Any(r => r.ReportedApplied) ? "学生机报告已应用；请点击“核对学生电脑身份”后重试确认。身份核对完成前不计入已确认。" : "",
                    needsReview > results.Count(r => r.ReportedApplied) ? "其他目标未取得有效确认，请展开结果检查电脑名/IP、连接和回执。" : "",
                    failed > 0 ? "部分学生机明确拒绝或未应用策略。" : "",
                    "查看失败电脑后，可重新应用。"
                }.Where(text => text.Length > 0)));
            if (errorMessages.Count > 0) WebsitePolicyError = string.Join(" ", errorMessages);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            WebsitePolicyError = "学生代理限制未应用：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    public async Task ReadWebsitePolicyStatusAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        WebsitePolicyError = "";
        WebsitePolicyResult = "正在读取学生端签名状态和 Edge 扩展回执…";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("请在 Windows 教师端读取学生代理状态。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var results = await WebsitePolicyStatusTransport.ReadAsync(targets, campus,
                signingKey.PrivateKey);
            var displayNames = await ResolveTargetDisplayNamesAsync(targets);
            var verified = results.Count(result => result.Succeeded && result.Status is not null);
            var appliedProfiles = results.Where(result => result.Status?.EdgeWebsite is not null)
                .Sum(result => result.Status!.EdgeWebsite!.Profiles.Count(profile => profile.Status == "applied"));
            var degradedProfiles = results.Where(result => result.Status?.EdgeWebsite is not null)
                .Sum(result => result.Status!.EdgeWebsite!.Profiles.Count(profile => profile.Status == "degraded"));
            var browserDiagnostics = results.Count(result => result.Status?.EdgeBrowserDiagnostics is not null);
            WebsitePolicyResult = $"签名状态已读取 {verified}/{results.Count} 名学生 · 浏览器诊断 {browserDiagnostics}/{verified} 台 · Edge 扩展已应用 {appliedProfiles} 个 Profile · 降级 {degradedProfiles} 个";
            WebsitePolicyResultDetails = string.Join(Environment.NewLine, results.Select(result =>
            {
                var name = GetWebsitePolicyStudentName(result.Target);
                if (!result.Succeeded || result.Status is not { } status)
                    return $"{name}：状态未知 — {result.Detail}";
                var browserDetails = FormatEdgeBrowserDiagnostics(status.EdgeBrowserDiagnostics, status.AgentVersion);
                var edge = status.EdgeWebsite;
                var websiteMode = status.Website.Mode switch
                {
                    WebsitePolicyMode.Disabled => "已解除",
                    WebsitePolicyMode.Blocklist => "阻止指定网站",
                    WebsitePolicyMode.Allowlist => "只允许指定网站",
                    WebsitePolicyMode.Combined => "阻止指定网站，允许网站作为例外",
                    _ => "状态未知"
                };
                var website = $"网站限制：{websiteMode}" +
                    (status.Website.ExpiresUtc is { } expiry ? $" · 截止 {expiry.ToLocalTime():yyyy-MM-dd HH:mm}" : "");
                var domainSummary = status.Website.BlockedDomains is null || status.Website.AllowedDomains is null
                    ? "\n当前学生端版本不支持读取网站明细，请更新学生端。"
                    : $"\n要阻止的网站：{FormatWebsiteDomainList(string.Join(Environment.NewLine, status.Website.BlockedDomains))}" +
                      $"\n允许的网站：{FormatWebsiteDomainList(string.Join(Environment.NewLine, status.Website.AllowedDomains))}";
                if (edge is null)
                    return $"{name}：{website}{domainSummary}\nAgent {status.AgentVersion} 未提供 Edge 扩展状态{browserDetails}";
                var mode = edge.ExecutionMode == EdgeWebsitePolicyExecutionMode.ExtensionRealtime
                    ? "实时扩展模式" : "旧注册表模式";
                var profiles = edge.Profiles.Count == 0
                    ? "尚未收到当前策略的 Edge Profile 回执；请确认扩展已安装并在此 Edge 配置中运行"
                    :
                    string.Join("；", edge.Profiles.Select(profile =>
                        $"Profile {profile.ProfileId[..8]}：{(profile.Status == "applied" ? "已读回应用" : "降级")}，规则 {profile.RuleCount}，{profile.ErrorCode ?? "无错误"}"));
                return $"{name}：{website} · r{status.Website.Revision}{domainSummary}\nEdge {mode} · {profiles}{browserDetails}";
            }));
            ShowWebsitePolicyResultDetails = true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            WebsitePolicyError = "读取学生端状态失败：" + exception.Message;
            WebsitePolicyResult = "";
        }
        finally { EndExclusiveTask(); }
    }

    private static string FormatEdgeBrowserDiagnostics(EdgeBrowserDiagnosticsSnapshot? diagnostics, string agentVersion)
    {
        if (diagnostics is null)
            return "\n浏览器诊断：该学生 Agent 版本暂不支持，请更新学生端后重试。";

        var lines = new List<string>
        {
            $"浏览器诊断（Agent {agentVersion}；采集于 {diagnostics.CollectedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}）"
        };
        lines.Add("已安装 Edge：");
        if (diagnostics.InstalledBrowsers.Count == 0)
            lines.Add("  未发现已知位置的 Edge 安装");
        else
            lines.AddRange(diagnostics.InstalledBrowsers.Select(browser =>
                $"  · {ChannelLabel(browser.Channel)} {browser.ImageFileVersion ?? "版本未知"} / {browser.Architecture} / {browser.ExecutablePath}" +
                (browser.RefreshSupported ? "（刷新参数已验证）" : "（刷新参数未验证）")));

        lines.Add("Edge 运行进程：");
        if (diagnostics.RunningProcesses.Count == 0)
        {
            lines.Add("  未发现 msedge.exe");
        }
        else
        {
            lines.AddRange(diagnostics.RunningProcesses.Select(process =>
                $"  · PID {process.ProcessId} / Session {process.SessionId?.ToString() ?? "未知"} / SID {ShortSid(process.UserSid)} / " +
                $"{ChannelLabel(process.Channel)} {process.ImageFileVersion ?? "版本未知"} / {process.Architecture} / 完整性 {IntegrityLabel(process.IntegrityLevelRid)} / " +
                $"{process.ExecutablePath ?? "路径不可读"} / {(process.RefreshSupported ? "刷新参数已验证" : "刷新参数未验证")}" +
                (process.ProcessMayPrecedeImageUpdate == true ? " / 进程可能早于磁盘映像更新时间" : "") +
                (process.ErrorCode is null ? "" : $" / {process.ErrorCode}")));
        }

        lines.Add("Edge 更新组件：");
        lines.AddRange(diagnostics.UpdateServices.Select(service =>
            $"{service.Name}：{ServiceInstallationStateLabel(service.Installed, service.State)} / 启动 {ServiceStartTypeLabel(service.StartType)}" +
            (service.ImageFileVersion is null ? "" : $" / {service.ImageFileVersion}") +
            (service.ErrorCode is null ? "" : $" / {service.ErrorCode}")));

        if (diagnostics.RefreshRequestId == Guid.Empty)
            lines.Add($"Agent 策略 Revision：{diagnostics.PolicyRevision}；当前没有新的 Edge 刷新请求");
        else
            lines.Add($"Agent 策略 Revision：{diagnostics.PolicyRevision}；最近刷新请求 Revision：{diagnostics.RefreshRevision}，创建于 {diagnostics.RefreshRequestedUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        lines.Add("当前判断：" + SummarizeEdgeRefreshState(diagnostics));

        lines.Add(diagnostics.ExecutorReports.Count == 0
            ? "会话执行器：没有可读的本地执行器报告"
            : "会话执行器本地报告（仅作诊断，不证明 Edge 已应用）：");
        if (diagnostics.ExecutorReports.Count > 0)
            lines.AddRange(diagnostics.ExecutorReports.Select(report =>
                $"  · SID {ShortSid(report.UserSid)} / Session {report.SessionId?.ToString() ?? "未知"}：{RefreshStatusLabel(report.Status)}，r{report.Revision}，尝试 {report.AttemptCount}" +
                (report.BrowserVersion is null ? "" : $"，Edge {report.BrowserVersion}") +
                (report.ErrorCode is null ? "" : $"，{report.ErrorCode}")));

        if (diagnostics.Warnings.Count > 0)
            lines.Add("诊断提示：" + string.Join("；", diagnostics.Warnings.Select(DiagnosticWarningLabel)));
        lines.Add("进程映像文件版本不等于 Edge 实际已读入内存的代码版本；只有策略页面读回和重新导航结果能确认浏览器是否生效。");
        return Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static string SummarizeEdgeRefreshState(EdgeBrowserDiagnosticsSnapshot diagnostics)
    {
        var policy = diagnostics.PolicyRevision > 0
            ? $"Agent 已读回注册表策略 r{diagnostics.PolicyRevision}"
            : "Agent 当前未读到非零策略 Revision";
        var stableInstallations = diagnostics.InstalledBrowsers
            .Where(browser => browser.Channel == "stable").ToArray();
        var stableProcesses = diagnostics.RunningProcesses
            .Where(process => process.Channel == "stable").ToArray();
        if (diagnostics.RunningProcesses.Count == 0)
        {
            if (stableInstallations.Length == 0)
                return $"{policy}；未发现 Stable Edge 安装或运行进程，待人工确认";
            var unsupportedInstall = stableInstallations.FirstOrDefault(browser =>
                !EdgePolicyRefreshCompatibility.IsSupportedVersion(browser.ImageFileVersion));
            if (unsupportedInstall is not null)
                return $"{policy}；需要升级或验证兼容性：已安装 Edge {unsupportedInstall.ImageFileVersion ?? "版本未知"}";
            if (stableInstallations.Any(browser => !browser.RefreshSupported))
                return $"{policy}；待人工检查：已安装 Edge 版本在名单中，但安装路径不符合自动刷新条件";
            return $"{policy}；浏览器未运行，Agent 不会主动启动 Edge";
        }
        if (stableProcesses.Length == 0)
            return $"{policy}；待人工检查：未发现可刷新策略的 Stable Edge 进程";
        var supportedProcesses = stableProcesses.Where(process => process.RefreshSupported).ToArray();
        var unsupported = stableProcesses.Where(process => !process.RefreshSupported).ToArray();
        if (supportedProcesses.Length == 0)
        {
            var unsupportedVersion = unsupported.Any(process =>
                !EdgePolicyRefreshCompatibility.IsSupportedVersion(process.ImageFileVersion));
            if (!unsupportedVersion)
                return $"{policy}；待人工检查：Edge 版本在已验证名单中，但安装路径不符合自动刷新条件";
            var versions = string.Join("、", unsupported.Select(process => process.ImageFileVersion ?? "版本未知")
                .Distinct(StringComparer.OrdinalIgnoreCase));
            return $"{policy}；需要升级或验证兼容性：当前运行 Edge {versions}";
        }
        if (diagnostics.RefreshRequestId == Guid.Empty)
            return $"{policy}；没有新的刷新请求，请通过 edge://policy 和新导航核验策略效果";

        var requestedUtc = diagnostics.RefreshRequestedUtc;
        var reports = diagnostics.ExecutorReports.Where(report =>
                report.RequestId == diagnostics.RefreshRequestId &&
                report.Revision == diagnostics.RefreshRevision &&
                (requestedUtc is null || report.UpdatedUtc >= requestedUtc.Value))
            .OrderByDescending(report => report.UpdatedUtc).ToArray();
        if (reports.Length == 0)
            return $"{policy}；待人工检查：没有与最近刷新请求匹配的会话回执";
        var matchingReport = reports.FirstOrDefault(report => supportedProcesses.Any(process =>
            process.SessionId == report.SessionId && process.SessionId is not null &&
            string.Equals(process.UserSid, report.UserSid, StringComparison.OrdinalIgnoreCase)));
        if (matchingReport is null)
        {
            if (unsupported.Length > 0)
                return $"{policy}；待人工检查：检测到多个用户会话或版本不同的 Edge，执行器回执未能对应到受支持进程";
            return $"{policy}；待人工检查：执行器回执与当前 Edge 用户或 Session 不匹配";
        }

        var result = matchingReport.Status switch
        {
            EdgePolicyRefreshStatus.RegistryCommitted => "策略已写入，等待会话执行器处理",
            EdgePolicyRefreshStatus.BrowserNotRunning => "执行器最近检查时浏览器未运行；请稍后重新读取状态",
            EdgePolicyRefreshStatus.PendingSession => "等待匹配的学生用户会话",
            EdgePolicyRefreshStatus.UnsupportedBrowserVersion => "需要升级或验证当前 Edge 版本兼容性",
            EdgePolicyRefreshStatus.Dispatching => "执行器正在发送刷新请求",
            EdgePolicyRefreshStatus.DispatchedUnverified => "刷新请求已发出，但浏览器结果仍未核验",
            EdgePolicyRefreshStatus.VerifiedApplied => "执行器报告标记为已核验，仍需检查策略页和新导航",
            EdgePolicyRefreshStatus.RetryableFailure => "刷新请求失败，可在排除原因后重试",
            EdgePolicyRefreshStatus.NeedsReview => "执行器报告需要人工检查",
            _ => "待人工检查：执行器状态未知"
        };
        return $"{policy}；{result}";
    }

    private static string ChannelLabel(string channel) => channel switch
    {
        "stable" => "Stable",
        "beta" => "Beta",
        "dev" => "Dev",
        "canary" => "Canary",
        _ => "未知通道"
    };

    private static string ShortSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return "不可读";
        var last = sid.LastIndexOf('-');
        return last > 0 ? sid[..Math.Min(last, 12)] + "…-" + sid[(last + 1)..] : "已脱敏";
    }

    private static string IntegrityLabel(int? rid) => rid switch
    {
        0x1000 => "低",
        0x2000 => "中",
        0x3000 => "高",
        0x4000 => "系统",
        null => "不可读",
        _ => $"RID {rid}"
    };

    private static string ServiceStateLabel(string state) => state switch
    {
        "running" => "运行中",
        "stopped" => "已停止",
        "startPending" => "正在启动",
        "stopPending" => "正在停止",
        "continuePending" => "正在继续",
        "pausePending" => "正在暂停",
        "paused" => "已暂停",
        "unavailable" => "状态不可读",
        _ => "未知"
    };

    private static string ServiceStartTypeLabel(string startType) => startType switch
    {
        "automatic" => "自动",
        "manual" => "手动",
        "disabled" => "已禁用",
        "boot" => "启动时",
        "system" => "系统启动时",
        _ => "未知"
    };

    private static string ServiceInstallationStateLabel(bool? installed, string state) => installed switch
    {
        true => ServiceStateLabel(state),
        false => "未安装",
        _ => "安装状态不可读"
    };

    private static string RefreshStatusLabel(EdgePolicyRefreshStatus status) => status switch
    {
        EdgePolicyRefreshStatus.RegistryCommitted => "策略已写入",
        EdgePolicyRefreshStatus.BrowserNotRunning => "浏览器未运行",
        EdgePolicyRefreshStatus.PendingSession => "等待用户会话",
        EdgePolicyRefreshStatus.UnsupportedBrowserVersion => "Edge 版本未验证",
        EdgePolicyRefreshStatus.Dispatching => "正在发送刷新请求",
        EdgePolicyRefreshStatus.DispatchedUnverified => "请求已发出，未核验",
        EdgePolicyRefreshStatus.VerifiedApplied => "报告标记已核验",
        EdgePolicyRefreshStatus.RetryableFailure => "可重试失败",
        EdgePolicyRefreshStatus.NeedsReview => "需要检查",
        _ => "未知状态"
    };

    private static string DiagnosticWarningLabel(string warning) => warning switch
    {
        "EDGE_PROCESS_ENUMERATION_FAILED" => "无法枚举 Edge 进程",
        "EDGE_PROCESS_IDENTITY_PARTIAL" => "部分进程身份字段不可读",
        "EDGE_PROCESS_LIST_TRUNCATED" => "进程过多，清单已截断",
        "EDGE_INSTALLED_BROWSER_LIST_TRUNCATED" => "安装通道较多，清单已截断",
        "EDGE_UPDATE_SERVICE_QUERY_FAILED" => "Edge 更新服务状态不可读",
        "EDGE_PROFILE_LIST_UNAVAILABLE" => "Windows 用户配置清单不可读",
        "EDGE_EXECUTOR_STATE_UNREADABLE" => "部分执行器本地报告不可读",
        "EDGE_EXECUTOR_PROFILE_LIST_TRUNCATED" => "用户配置较多，执行器报告已截断",
        _ => "部分浏览器信息不可用"
    };

#if VEYONCAMPUS_EDGE_REALTIME_GATE_PASSED || VEYONCAMPUS_EDGE_REALTIME_PILOT
    public async Task MigrateEdgeRealtimeAsync(Func<string, Task<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        if (!CanMigrateEdgeRealtime || !TryBeginExclusiveTask()) return;
        WebsitePolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Edge 实时策略迁移需要在 Windows 教师端执行。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var extensionId = ConfiguredEdgeExtensionId()
                              ?? throw new InvalidDataException("教师端没有配置已发布的 Edge 扩展 ID。");
            var targets = WebsitePolicyTransport.NormalizeTargets(
                GetSelectedWebsitePolicyTargets());
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var statuses = await WebsitePolicyStatusTransport.ReadAsync(targets, campus,
                signingKey.PrivateKey);
            var revisions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var profilesByTarget = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var displayNames = await ResolveTargetDisplayNamesAsync(targets);
            var alreadyRealtime = 0;
            foreach (var result in statuses)
            {
                if (!result.Succeeded || result.Status?.EdgeWebsite is not { } edge)
                    throw new InvalidDataException($"{GetTargetDisplayName(result.Target, displayNames)} 未提供可验证的 Edge Profile 回执；未开始迁移。请先更新学生端并读取状态。");
                if (edge.ExecutionMode == EdgeWebsitePolicyExecutionMode.ExtensionRealtime)
                {
                    if (edge.ExtensionId != extensionId)
                        throw new InvalidDataException($"{GetTargetDisplayName(result.Target, displayNames)} 已绑定其他 Edge 扩展 ID；未开始迁移。");
                    alreadyRealtime++;
                    continue;
                }
                if (edge.Profiles.Count == 0)
                {
#if VEYONCAMPUS_EDGE_REALTIME_PILOT
                    throw new InvalidDataException($"{GetTargetDisplayName(result.Target, displayNames)} 尚未收到当前策略的 Edge Profile 回执；未开始迁移。此试用学生包不会自动加载扩展。请在学生机每个实际使用的 Edge 配置中打开 edge://extensions，启用开发人员模式并加载 C:\\Program Files\\Veyon Campus\\Student\\EdgeExtension；保持 Edge 打开约 2 秒，再点“读取逐台状态”，看到每个 Profile 均为“已读回应用”后重试。");
#else
                    throw new InvalidDataException($"{GetTargetDisplayName(result.Target, displayNames)} 尚未收到当前策略的 Edge Profile 回执；未开始迁移。请确认 Edge 扩展已在每个实际使用的 Edge 配置中安装并启用、Edge 保持打开，然后点“读取逐台状态”确认回执。");
#endif
                }
                var unappliedProfiles = edge.Profiles.Where(profile => profile.Status != "applied").ToArray();
                if (unappliedProfiles.Length > 0)
                {
                    var profileErrors = string.Join("；", unappliedProfiles.Select(profile =>
                        $"{profile.ProfileId[..8]}：{profile.Status}{(profile.ErrorCode is null ? "" : " / " + profile.ErrorCode)}"));
                    throw new InvalidDataException($"{GetTargetDisplayName(result.Target, displayNames)} 有 Edge Profile 尚未读回应用当前策略（{profileErrors}）；未开始迁移。请确认扩展已启用且 Edge 正在运行，修复后点“读取逐台状态”再试。");
                }
                revisions.Add(result.Target, result.Status!.Website.Revision);
                profilesByTarget.Add(result.Target, Array.AsReadOnly(edge.Profiles
                    .Select(profile => profile.ProfileId).OrderBy(value => value, StringComparer.Ordinal).ToArray()));
            }

            if (profilesByTarget.Count == 0)
            {
                WebsitePolicyResult = $"所选设备已处于该 Edge 实时模式（{alreadyRealtime} 台）；没有修改。";
                WebsitePolicyResultDetails = "";
                ShowWebsitePolicyResultDetails = false;
                return;
            }
            var summaryLines = profilesByTarget.Select(pair =>
                $"{GetTargetDisplayName(pair.Key, displayNames)}：已读回 {pair.Value.Count} 个 Edge Profile 的当前规则");
            var confirmation =
#if VEYONCAMPUS_EDGE_REALTIME_PILOT
                "这是 Edge 手动试用模式。请先确认已在目标电脑的 Edge 中手动加载 Veyon Campus 扩展，并且所列 Profile 覆盖该电脑实际使用的 Edge 配置。" +
                Environment.NewLine + "启用后会清除 Veyon Campus 自己管理的 Edge URLBlocklist/URLAllowlist；策略将动态同步到之后新打开或重新加载的网页，当前网页需重新加载后生效。" +
#else
                "仅在已完成非 AD Windows 11 P0 分发验证，并确认所列 Profile 覆盖该电脑全部实际 Edge 用户后继续。" +
                Environment.NewLine + "启用后会清除 Veyon Campus 自己管理的 Edge URLBlocklist/URLAllowlist，保留学校外部策略，并开始实时同步；当前网页需重新加载后生效。" +
#endif
                Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, summaryLines);
            if (!await confirm(confirmation).ConfigureAwait(true))
            {
                WebsitePolicyResult = "已取消 Edge 实时模式迁移；旧策略保持不变。";
                return;
            }

            var results = await EdgeWebsitePolicyModeTransport.MigrateAsync(targets.Where(profilesByTarget.ContainsKey),
                campus, signingKey.PrivateKey, extensionId, revisions, profilesByTarget);
            var succeeded = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count - succeeded - needsReview;
            WebsitePolicyResult = $"Edge 实时模式迁移：已确认 {succeeded}/{results.Count} · 待核对 {needsReview} · 失败 {failed}";
            WebsitePolicyResultDetails = string.Join(Environment.NewLine, results.Select(result =>
                $"{GetTargetDisplayName(result.Target, displayNames)}：{(result.Succeeded ? "已启用实时同步" : result.NeedsReview ? "待核对" : "失败")} — {result.Detail}"));
            ShowWebsitePolicyResultDetails = true;
            if (succeeded != results.Count) WebsitePolicyError = "部分电脑未完成迁移；旧 Edge 策略保留或需管理员核对。请读取状态后处理。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            WebsitePolicyError = "Edge 实时模式迁移未完成：" + exception.Message;
            WebsitePolicyResult = "";
        }
        finally { EndExclusiveTask(); }
    }

    public async Task RestoreEdgeLegacyAsync(Func<string, Task<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        if (!CanRestoreEdgeLegacy || !TryBeginExclusiveTask()) return;
        WebsitePolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Edge 注册表策略恢复需要在 Windows 教师端执行。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var extensionId = ConfiguredEdgeExtensionId()
                              ?? throw new InvalidDataException("教师端没有配置 Edge 扩展 ID。");
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var statuses = await WebsitePolicyStatusTransport.ReadAsync(targets, campus,
                signingKey.PrivateKey);
            var displayNames = await ResolveTargetDisplayNamesAsync(targets);
            var restoreTargets = new List<string>();
            var revisions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in statuses)
            {
                if (!result.Succeeded || result.Status?.EdgeWebsite is not { } edge)
                    throw new InvalidDataException($"{GetTargetDisplayName(result.Target, displayNames)} 的 Edge 模式未知；没有开始恢复。请先读取逐台状态并检查连接。 ");
                if (edge.ExecutionMode == EdgeWebsitePolicyExecutionMode.LegacyRegistry) continue;
                if (edge.ExtensionId != extensionId)
                    throw new InvalidDataException($"{GetTargetDisplayName(result.Target, displayNames)} 绑定了其他 Edge 扩展 ID；没有开始恢复。");
                restoreTargets.Add(result.Target);
                revisions.Add(result.Target, result.Status!.Website.Revision);
            }

            if (restoreTargets.Count == 0)
            {
                WebsitePolicyResult = "所选电脑已处于 Edge 旧注册表模式；没有修改。";
                WebsitePolicyResultDetails = "";
                ShowWebsitePolicyResultDetails = false;
                return;
            }

            var summaryLines = restoreTargets.Select(GetWebsitePolicyStudentName);
            var confirmation =
                "恢复后会把最近已签收的网站策略写回 Edge 注册表，并让扩展清除它管理的动态规则。正在打开的网页需要重新加载，Edge 也可能需要一段时间读取新策略。" +
                Environment.NewLine + Environment.NewLine + "请确认这些学生电脑：" + Environment.NewLine +
                string.Join(Environment.NewLine, summaryLines);
            if (!await confirm(confirmation).ConfigureAwait(true))
            {
                WebsitePolicyResult = "已取消恢复；Edge 实时策略保持不变。";
                return;
            }

            var results = await EdgeWebsitePolicyModeTransport.RestoreLegacyAsync(restoreTargets,
                campus, signingKey.PrivateKey, extensionId, revisions);
            var succeeded = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count - succeeded - needsReview;
            WebsitePolicyResult = $"Edge 注册表策略恢复：已确认 {succeeded}/{results.Count} · 待核对 {needsReview} · 失败 {failed}";
            WebsitePolicyResultDetails = string.Join(Environment.NewLine, results.Select(result =>
                $"{GetWebsitePolicyStudentName(result.Target)}：" +
                $"{(result.Succeeded ? "已恢复" : result.NeedsReview ? "待核对" : "失败")} — {result.Detail}"));
            ShowWebsitePolicyResultDetails = true;
            if (succeeded != results.Count) WebsitePolicyError = "部分电脑未完成 Edge 策略恢复；请读取逐台状态并检查 Agent 回执。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            WebsitePolicyError = "Edge 注册表策略恢复未完成：" + exception.Message;
            WebsitePolicyResult = "";
        }
        finally { EndExclusiveTask(); }
    }
#else
    public Task MigrateEdgeRealtimeAsync(Func<string, Task<bool>> confirm) => Task.CompletedTask;
    public Task RestoreEdgeLegacyAsync(Func<string, Task<bool>> confirm) => Task.CompletedTask;
#endif

    private static string? ConfiguredEdgeExtensionId()
    {
#if VEYONCAMPUS_EDGE_REALTIME_GATE_PASSED || VEYONCAMPUS_EDGE_REALTIME_PILOT
        var value = typeof(TeacherViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "VeyonCampusEdgeExtensionId")?.Value;
        try
        {
            EdgeRealtimeWebsitePolicy.ValidateExtensionId(value);
            return value;
        }
        catch (InvalidDataException) { return null; }
#else
        return null;
#endif
    }

    public Task DisableWebsitePolicyAsync() => PushWebsitePolicyAsync(WebsitePolicyMode.Disabled);

    public void FillFailedWebsiteTargets()
    {
        if (_lastFailedWebsiteTargets.Count == 0) return;
        WebsiteTargets = string.Join(Environment.NewLine, _lastFailedWebsiteTargets);
        WebsitePolicyHistoryText += Environment.NewLine + "已填入失败电脑。检查学生代理规则和时长后，可重新应用。";
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
            WebsitePolicyHistoryText = "无法读取上次记录；仍可编辑学生代理限制。";
        }
    }

    private void UpdateWebsitePolicyHistory(WebsitePolicyPushHistoryEntry entry)
    {
        _lastFailedWebsiteTargets = Array.AsReadOnly(entry.Results.Where(result => !result.Succeeded)
            .Select(result => result.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var succeeded = entry.Results.Count(result => result.Succeeded);
        var needsReview = entry.Results.Count(result => !result.Succeeded && result.NeedsReview);
        var failed = entry.Results.Count(result => !result.Succeeded && !result.NeedsReview);
        WebsitePolicyHistoryText = $"上次应用 {entry.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 成功 {succeeded}/{entry.Results.Count} · 待核对 {needsReview} · 失败 {failed}";
        Changed(nameof(CanFillFailedWebsiteTargets));
    }

    private bool IsWebsitePolicyInputValid()
    {
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            if (targets.Count == 0) return false;
            _ = GetWebsiteDeadlineUtc(DateTimeOffset.UtcNow);
            var blocks = WebsiteBlockDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var allows = WebsiteAllowDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return SelectedWebsiteMode switch
            {
                WebsitePolicyMode.Blocklist => WebsitePolicyCompiler.NormalizeDomains(blocks).Count > 0,
                WebsitePolicyMode.Allowlist => WebsitePolicyCompiler.NormalizeDomains(allows).Count > 0,
                WebsitePolicyMode.Combined => WebsitePolicyCompiler.NormalizeDomains(blocks).Count > 0 ||
                    WebsitePolicyCompiler.NormalizeDomains(allows).Count > 0,
                _ => false
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException or ArgumentException) { return false; }
    }

    private int GetWebsiteLifetimeMinutes() => WebsiteDurationIndex switch
    {
        0 => 45,
        1 => 60,
        2 => 90,
        3 => 120,
        4 when WebsiteCustomDurationMinutes is >= 10 and <= 300 => WebsiteCustomDurationMinutes,
        5 => 0,
        _ => throw new InvalidDataException("学生代理限制时长无效。")
    };

    private DateTimeOffset GetWebsiteDeadlineUtc(DateTimeOffset issuedUtc)
    {
        if (WebsiteDeadlineDate is not { } selectedDate || WebsiteDeadlineTime is not { } selectedTime ||
            selectedTime < TimeSpan.Zero || selectedTime >= TimeSpan.FromDays(1))
            throw new InvalidDataException("请选择有效的限制截止日期和时间。");
        var localTime = DateTime.SpecifyKind(selectedDate.Date.Add(selectedTime), DateTimeKind.Unspecified);
        var expiry = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime)).ToUniversalTime();
        var issued = issuedUtc.ToUniversalTime();
        if (expiry <= issued)
            throw new InvalidDataException("截止时间必须晚于当前时间。");
        if (expiry - issued > WebsitePolicyCompiler.MaximumPolicyLifetime)
            throw new InvalidDataException("网站限制的截止时间最多可设为当前时间之后 24 小时。");
        return expiry;
    }

    private bool IsApplicationPolicyInputValid()
    {
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
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
            WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            var settings = CurrentStudentSystemPolicySettings();
            if (settings.IsEmpty) return false;
            _ = StudentSystemPolicyCompiler.Create(CampusId.Trim(), 1,
                ParseStudentSids(allowManualInput: false), settings);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException or OverflowException)
        { return false; }
    }

    private string GetStudentSystemPolicyPushGuidance()
    {
        if (!OperatingSystem.IsWindows()) return "请在 Windows 教师端设置学生电脑限制。";
        if (IsExecuting) return "正在处理，请稍候。";
        if (CurrentStudentSystemPolicySettings().IsEmpty) return "请至少选择一项限制。";
        IReadOnlyList<string> targets;
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException)
        { return "请填写校区和学生电脑。"; }

        if (targets.Any(target => !_studentAccountChoices.Any(choice =>
                choice.IsSelected && string.Equals(choice.Target, target, StringComparison.OrdinalIgnoreCase))))
            return "请先为每台目标电脑读取并选择学生账户。";
        if (_studentSystemPolicyProhibitSoftwareInstallation && !_studentSystemPolicySoftwareInstallReviewed)
            return "请先勾选软件安装影响确认。";
        if (!_studentSystemPolicyImpactReviewed)
            return "请阅读系统限制影响说明和目标预览，并勾选确认。";
        return "";
    }

    private StudentSystemPolicySettings CurrentStudentSystemPolicySettings() => new(
        StudentSystemPolicyLockWallpaper, StudentSystemPolicyProhibitTimeChanges,
        StudentSystemPolicyProhibitNetworkChanges, StudentSystemPolicyProhibitSoftwareInstallation,
        StudentSystemPolicyProhibitAccountManagement, StudentSystemPolicyProhibitControlPanel);

    private string BuildStudentSystemPolicyPreview()
    {
        try
        {
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            var sids = ParseStudentSids(allowManualInput: false);
            var settings = CurrentStudentSystemPolicySettings();
            var enabled = new List<string>();
            if (settings.LockWallpaper) enabled.Add("Windows 蓝色默认壁纸");
            if (settings.ProhibitTimeChanges) enabled.Add("日期/时间与时区修改");
            if (settings.ProhibitNetworkChanges) enabled.Add("网络设置");
            if (settings.ProhibitSoftwareInstallation) enabled.Add("软件安装和便携程序");
            if (settings.ProhibitAccountManagement) enabled.Add("学生账户管理和本人改密");
            if (settings.ProhibitControlPanel) enabled.Add("控制面板和设置");
            if (enabled.Count == 0) return "请至少选择一项系统限制。";
            var review = _studentSystemPolicyImpactReviewed ? "" : " 请阅读以上影响和目标预览，并勾选确认后应用。";
            return $"{targets.Count} 台电脑 · {sids.Length} 个账户 · {string.Join("、", enabled)}。{review}";
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException or OverflowException)
        { return "请检查校区、电脑和账户设置。"; }
    }

    private string BuildApplicationPolicyPreview()
    {
        try
        {
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            var sids = ParseStudentSids();
            var rules = ParseApplicationRules();
            var expires = DateTimeOffset.UtcNow + ApplicationPolicyLifetime();
            var mode = SelectedApplicationPolicyMode == ApplicationPolicyMode.Audit ? "只检查，不限制" : "限制已选应用";
            var gate = SelectedApplicationPolicyMode == ApplicationPolicyMode.Enforce && !_hasMatchingApplicationAudit
                ? "先查看影响，再应用限制。"
                : SelectedApplicationPolicyMode == ApplicationPolicyMode.Enforce && !ApplicationEnforcementReviewed
                    ? "请查看影响结果并确认。"
                : "";
            var effect = SelectedApplicationPolicyMode == ApplicationPolicyMode.Audit
                ? ""
                : "\n已运行的应用不会关闭；移除规则后，应用会恢复。";
            return $"{targets.Count} 台电脑 · {sids.Length} 个账户 · {rules.Length} 个应用 · {mode} · 到期 {expires.ToLocalTime():yyyy-MM-dd HH:mm}。{effect}" +
                   (gate.Length > 0 ? "\n" + gate : "");
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException or OverflowException)
        {
            return "请检查校区、电脑、账户和软件设置。";
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
        _studentSystemPolicyImpactReviewed = false;
        Changed(nameof(StudentSystemPolicyImpactReviewed));
        _studentSystemPolicySoftwareInstallReviewed = false;
        Changed(nameof(StudentSystemPolicySoftwareInstallReviewed));
        Changed(nameof(CanPushStudentSystemPolicy));
        Changed(nameof(StudentSystemPolicyPreview));
        Changed(nameof(StudentSystemPolicyPushGuidance));
    }

    private void NotifyStudentSystemPolicyInputs()
    {
        _studentSystemPolicyImpactReviewed = false;
        Changed(nameof(StudentSystemPolicyImpactReviewed));
        Changed(nameof(HasSelectedStudentSystemPolicy));
        _studentSystemPolicySoftwareInstallReviewed = false;
        Changed(nameof(StudentSystemPolicySoftwareInstallReviewed));
        Changed(nameof(StudentSystemPolicyPreview));
        Changed(nameof(CanPushStudentSystemPolicy));
        Changed(nameof(StudentSystemPolicyPushGuidance));
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
                item.DisplayTarget.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Target.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    private string GetApplicationPolicyFingerprint()
    {
        var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
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
            ApplicationPolicyHistoryText = "无法读取上次记录；仍可编辑应用限制。";
        }
    }

    private void UpdateApplicationPolicyHistory(ApplicationPolicyPushHistoryEntry entry)
    {
        var succeeded = entry.Results.Count(result => result.Succeeded);
        var pending = entry.Results.Count(result => !result.Succeeded && result.NeedsReview);
        var failed = entry.Results.Count(result => !result.Succeeded && !result.NeedsReview);
        var mode = entry.Mode switch
        {
            ApplicationPolicyMode.Audit => "影响检查",
            ApplicationPolicyMode.Enforce => "阻止",
            _ => "解除"
        };
        ApplicationPolicyHistoryText = $"上次应用 {entry.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {mode} · 成功 {succeeded}/{entry.Results.Count} · 待核对 {pending} · 失败 {failed}";
    }

    private bool AreWebsitePolicyTargetsValid()
    {
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException) { return false; }
    }

    public string StudentAgentIdentityStatus { get; private set; } = "";
    public string StudentAgentIdentityDetails
    {
        get => _studentAgentIdentityDetails;
        private set
        {
            if (_studentAgentIdentityDetails == value) return;
            _studentAgentIdentityDetails = value;
            Changed();
            Changed(nameof(HasStudentAgentIdentityDetails));
        }
    }
    public bool HasStudentAgentIdentityDetails => StudentAgentIdentityDetails.Length > 0;
    public bool ShowStudentAgentIdentityDetails
    {
        get => _showStudentAgentIdentityDetails;
        set
        {
            if (_showStudentAgentIdentityDetails == value) return;
            _showStudentAgentIdentityDetails = value;
            Changed();
            Changed(nameof(StudentAgentIdentityDetailsToggleText));
        }
    }
    public string StudentAgentIdentityDetailsToggleText =>
        ShowStudentAgentIdentityDetails ? "收起逐台结果" : "查看逐台结果";

    public async Task<IReadOnlyList<StudentAgentIdentityDiscoveryResult>> DiscoverStudentAgentIdentitiesAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            StudentAgentIdentityDetails = "";
            ShowStudentAgentIdentityDetails = false;
            StudentAgentIdentityStatus = "仅支持 Windows 教师端。";
            Changed(nameof(StudentAgentIdentityStatus));
            return Array.Empty<StudentAgentIdentityDiscoveryResult>();
        }
        if (!CanTrustStudentAgentIdentities || !TryBeginExclusiveTask())
        {
            StudentAgentIdentityDetails = "";
            ShowStudentAgentIdentityDetails = false;
            StudentAgentIdentityStatus = "请填写校区和学生电脑，并等待当前操作完成。";
            Changed(nameof(StudentAgentIdentityStatus));
            return Array.Empty<StudentAgentIdentityDiscoveryResult>();
        }
        try
        {
            var campus = CampusId.Trim();
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            StudentAgentIdentityDetails = "";
            ShowStudentAgentIdentityDetails = false;
            StudentAgentIdentityStatus = $"正在读取 {targets.Count} 台学生电脑身份……";
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var results = await WebsitePolicyStatusTransport.DiscoverIdentitiesAsync(targets, campus,
                signingKey.PrivateKey);
            var countWithIdentity = results.Count(result => result.Candidate is not null);
            var needsTrust = results.Count(result => result.Candidate is not null && !result.MatchesPinnedKey);
            var unverifiable = results.Count - countWithIdentity;
            var alreadyTrusted = countWithIdentity - needsTrust;
            StudentAgentIdentityStatus = $"已读取 {countWithIdentity}/{results.Count} 台 · 已固定 {alreadyTrusted}（无需重复确认） · 待确认 {needsTrust} · 无法读取 {unverifiable}";
            StudentAgentIdentityDetails = FormatStudentAgentIdentityDetails(results);
            ShowStudentAgentIdentityDetails = needsTrust > 0 || unverifiable > 0;
            return results;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or HttpRequestException or
                                          SocketException or PlatformNotSupportedException)
        {
            StudentAgentIdentityDetails = "";
            ShowStudentAgentIdentityDetails = false;
            StudentAgentIdentityStatus = "学生电脑身份读取失败：" + exception.Message;
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
            StudentAgentIdentityStatus = "未找到可验证的学生电脑身份。";
            return;
        }
        var trustStore = new StudentAgentIdentityTrustStore();
        foreach (var candidate in candidates)
        {
            var changed = candidate.PreviouslyPinnedFingerprint is { } old &&
                          !string.Equals(old, candidate.Fingerprint, StringComparison.OrdinalIgnoreCase);
            trustStore.Pin(candidate, replaceChangedKey: changed && approveChangedKeys);
        }
        var unverifiable = results.Length - candidates.Length;
        StudentAgentIdentityStatus = unverifiable == 0
            ? $"已确认 {candidates.Length} 台学生电脑。后续更新会自动核对。"
            : $"已确认 {candidates.Length}/{results.Length} 台 · 未读取 {unverifiable}";
        StudentAgentIdentityDetails = FormatStudentAgentIdentityDetails(results, candidatesConfirmed: true);
        ShowStudentAgentIdentityDetails = unverifiable > 0;
        Changed(nameof(StudentAgentIdentityStatus));
        Changed(nameof(CanDeployStudentUpdate));
    }

    private string FormatStudentAgentIdentityDetails(
        IReadOnlyList<StudentAgentIdentityDiscoveryResult> results, bool candidatesConfirmed = false)
    {
        var confirmed = results.Where(result => result.Candidate is not null &&
            (candidatesConfirmed || result.MatchesPinnedKey)).ToArray();
        var needsAttention = results.Except(confirmed).ToArray();
        var sections = new List<string>(2);
        if (confirmed.Length > 0)
        {
            var lines = confirmed.Select(result =>
            {
                var candidate = result.Candidate!;
                var rotated = candidate.PreviouslyPinnedFingerprint is { } old &&
                              !string.Equals(old, candidate.Fingerprint, StringComparison.OrdinalIgnoreCase);
                return candidatesConfirmed && rotated && candidate.PreviouslyPinnedFingerprint is { } previous
                    ? $"{GetWebsitePolicyTargetLabel(result.Target)}：已确认轮换；旧指纹 {previous}，新指纹 {candidate.Fingerprint}"
                    : $"{GetWebsitePolicyTargetLabel(result.Target)}：已确认；指纹 {candidate.Fingerprint}";
            });
            sections.Add($"正确 / 已确认（{confirmed.Length}）{Environment.NewLine}" +
                         string.Join(Environment.NewLine, lines));
        }
        if (needsAttention.Length > 0)
        {
            var lines = needsAttention.Select(result =>
            {
                if (result.Candidate is not { } candidate)
                    return $"{GetWebsitePolicyTargetLabel(result.Target)}：无法读取 — {result.Detail}";
                return candidate.PreviouslyPinnedFingerprint is { } old &&
                       !string.Equals(old, candidate.Fingerprint, StringComparison.OrdinalIgnoreCase)
                    ? $"{GetWebsitePolicyTargetLabel(result.Target)}：身份变化待确认；旧指纹 {old}，新指纹 {candidate.Fingerprint}"
                    : $"{GetWebsitePolicyTargetLabel(result.Target)}：待确认；指纹 {candidate.Fingerprint}";
            });
            sections.Add($"需处理 / 错误（{needsAttention.Length}）{Environment.NewLine}" +
                         string.Join(Environment.NewLine, lines));
        }
        return string.Join(Environment.NewLine + Environment.NewLine, sections);
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
                $"已发布：{result.CampusName}\n学生可搜索校区名称，并输入教师手机号后四位。";
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
        PackageGenerationStatus = "正在生成配置包……";
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                PackageOutputError = "请在 Windows 教师电脑上生成配置包。";
                return;
            }
            PackageGenerationStatus = "正在检查 Veyon……";
            var installed = await Task.Run(VeyonFacts.Probe);
            if (installed.Status != "installed" || !VeyonFacts.IsSupportedVersionDetail(installed.VersionDetail))
            {
                PackageOutputError = "请先安装教师端 Veyon，再生成配置包。";
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
                PackageOutputError = "输出文件夹已存在。请选择空文件夹，避免覆盖旧密钥。";
                return;
            }
            PackageGenerationStatus = "正在准备校区密钥……";
            using var websiteSigningKey = await Task.Run(() =>
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("学生代理签名密钥仅支持 Windows 用户证书库。");
                return WebsitePolicySigningKeyStore.GetOrCreate(campus, replaceUnavailableSigningKey);
            });
            using var applicationSigningKey = await Task.Run(() => ApplicationPolicySigningKeyStore.GetOrCreate(campus));
            using var systemSigningKey = await Task.Run(() => StudentSystemPolicySigningKeyStore.GetOrCreate(campus));
            var publicKeyExportPath = Path.Combine(Path.GetTempPath(), "VeyonCampus-public-" + Guid.NewGuid().ToString("N") + ".pem");
            temporaryPublicKey = publicKeyExportPath;
            PackageGenerationStatus = "正在准备校区配置，请稍候……";
                InstallerStatus = "正在读取教师安全密钥……";
            var keyResponse = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                (requestId, caller) => new PrivilegedWorkerRequest(
                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                    PrivilegedWorkerOperation.ExportTeacherPublicKey, CampusId: campus));
            teacherKeyCreated = keyResponse.TeacherKeyCreated == true;
            if (!keyResponse.Result.Ok || string.IsNullOrWhiteSpace(keyResponse.PublicKeyPem))
            {
                PackageOutputError = keyResponse.Result.Detail +
                    (teacherKeyCreated ? "\n教师密钥已安全保存在 Windows 中，没有导出。" : "");
                return;
            }
            await File.WriteAllTextAsync(publicKeyExportPath, keyResponse.PublicKeyPem,
                new System.Text.UTF8Encoding(false));
            PackageGenerationStatus = "正在生成配置包……";
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
            PackageOutput = $"已生成学生配置包：{built}\n请将此文件夹提供给学生部署工具。";
            ReadWebsiteSigningCampuses();
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
                PackageOutputError += "\n教师密钥已安全保存在 Windows 中，没有导出。";
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
            var repairMissingUninstaller = installed.CanRepairMissingUninstaller &&
                VeyonFacts.IsSupportedVersionDetail(installed.VersionDetail);
            if (installed.Status == "installed")
            {
                TeacherInstallIssue = "本机已安装 Veyon，已停止重复安装。";
                return;
            }
            if (installed.Status != VeyonFacts.NotInstalled && !repairMissingUninstaller)
            {
                TeacherInstallIssue = $"无法确认 Veyon 状态，已停止安装。\n{installed.AsText()}";
                return;
            }
            InstallerStatus = repairMissingUninstaller
                ? "检测到 Veyon 卸载入口损坏，正在准备修复……"
                : "正在准备 Veyon 安装文件……";
            var acquired = await AcquireInstallerWithProgressAsync();
            if (!acquired.Trust.IsAllowed || !acquired.Trust.AuthenticodeVerified)
            {
                TeacherInstallIssue = "安装文件验证失败，未运行安装程序。";
                return;
            }
            InstallerStatus = repairMissingUninstaller
                ? "正在修复教师端 Veyon 安装登记和卸载程序……"
                : "正在安装教师端 Veyon……";
            IsInstallingTeacherVeyon = true;
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
                TeacherInstallResult = repairMissingUninstaller
                    ? "Veyon 安装已修复并通过核验；现有配置已保留。现在可以从 Windows“已安装的应用”中卸载 Veyon。"
                    : "Veyon 已安装并完成认证设置。请重启 Veyon Master，确认连接后再生成学生配置包。";
            }
            else TeacherInstallIssue = "Veyon 安装完成，但教师控制台未能自动确认状态；请重新执行本页的安装/修复，再查看检查结果。";
            InstallerStatus = repairMissingUninstaller ? "Veyon 修复完成。" : "Veyon 安装完成。";
        }
        catch (Exception exception) { TeacherInstallIssue = "安装未完成：" + DescribeWindowsLaunchError(exception); }
        finally
        {
            IsInstallingTeacherVeyon = false;
            EndExclusiveTask();
            await RefreshTeacherSetupVeyonStatusAsync();
        }
    }

    private Task<InstallerStoreResult> AcquireInstallerWithProgressAsync()
    {
        var progress = new Progress<InstallerResourceProgress>(value =>
        {
            var percent = value.TotalBytes > 0 ? Math.Clamp(value.BytesReceived * 100 / value.TotalBytes, 0, 100) : 0;
            InstallerStatus = $"正在准备安装文件… {percent}%";
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
            if (GetStudentNamesForCurrentRoom().Count == 0)
            {
                RoomNames = Array.Empty<string>();
                RoomPreviewRows = Array.Empty<string>();
                RoomError = "";
                return;
            }
            var computers = BuildRoomImportPlan();
            RoomNames = computers.Select(computer => computer.ComputerName).ToArray();
            RoomPreviewRows = computers.Select(computer =>
                $"{(string.IsNullOrWhiteSpace(computer.StudentName) ? "未填写姓名" : computer.StudentName)}  →  {computer.Host}")
                .ToArray();
            RoomError = "";
        }
        catch (InvalidDataException exception)
        {
            RoomNames = Array.Empty<string>();
            RoomPreviewRows = Array.Empty<string>();
            RoomError = exception.Message;
        }
    }

    private IReadOnlyList<string> GetStudentNamesForCurrentRoom()
    {
        if (_seatGrid is null || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room ||
            !string.Equals(campus.DisplayName, RoomCampusName.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(room.DisplayName, RoomLocationName.Trim(), StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();
        return _seatGrid.Seats.OrderBy(seat => seat.Row).ThenBy(seat => seat.Column)
            .Where(seat => !string.IsNullOrWhiteSpace(seat.StudentName))
            .Select(seat => seat.StudentName.Trim()).ToArray();
    }

    private int GetCurrentBuilderSeatCount() => GetStudentNamesForCurrentRoom().Count;

    private int GetNamedStudentCount()
    {
        return GetStudentNamesForCurrentRoom().Count(name => !string.IsNullOrWhiteSpace(name));
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
            CampusDirectoryError = "无法读取本机地点清单；原文件未更改。";
        }
    }

    private static VeyonComputerRenameTargetPlan[] BuildRoomComputerRenamePlan(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers, VeyonDirectorySnapshot snapshot)
    {
        var locations = snapshot.Objects.Where(item =>
            item.Type.Equals("location", StringComparison.OrdinalIgnoreCase) &&
            item.Name.Equals(locationName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (locations.Length > 1)
            throw new InvalidDataException($"Veyon 中有多个同名地点“{locationName}”；请先在教师控制台处理重复地点。");

        var existingComputers = snapshot.Objects.Where(item =>
            item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase)).ToArray();
        var locationId = locations.Length == 1 ? locations[0].ObjectId : (Guid?)null;
        if (locationId is null && existingComputers.Any(item =>
                item.Location.Equals(locationName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"地点“{locationName}”有电脑记录，但没有唯一地点对象；控制台未改动目录。");

        var matchedByHost = new Dictionary<string, VeyonNetworkObject>(StringComparer.OrdinalIgnoreCase);
        var renameTargets = new List<VeyonComputerRenameTargetPlan>();
        foreach (var computer in computers)
        {
            var host = VeyonHostAddress.NormalizeOverride(computer.Host);
            var matches = existingComputers.Where(item =>
            {
                try
                {
                    return string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), host,
                        StringComparison.OrdinalIgnoreCase);
                }
                catch (InvalidDataException) { return false; }
            }).ToArray();
            if (matches.Length > 1)
                throw new InvalidDataException($"主机“{host}”对应多个 Veyon 电脑对象；无法自动关联。");
            if (matches.Length == 0) continue;

            var existing = matches[0];
            if (locationId is null || existing.ParentObjectId != locationId.Value)
                throw new InvalidDataException($"主机“{host}”已经属于其他地点；为避免重复关联，没有修改目录。");
            matchedByHost.Add(host, existing);
            if (!string.Equals(existing.Name, computer.DisplayName, StringComparison.Ordinal))
                renameTargets.Add(new VeyonComputerRenameTargetPlan(existing.ObjectId,
                    existing.ParentObjectId, existing.Name, computer.DisplayName, host));
        }

        var renamingIds = renameTargets.Select(target => target.ObjectId).ToHashSet();
        foreach (var computer in computers)
        {
            var desiredName = computer.DisplayName;
            var ownHost = VeyonHostAddress.NormalizeOverride(computer.Host);
            matchedByHost.TryGetValue(ownHost, out var ownObject);
            var nameMatches = existingComputers.Where(item =>
                item.Name.Equals(desiredName, StringComparison.OrdinalIgnoreCase));
            foreach (var match in nameMatches)
            {
                if (match.ObjectId == ownObject?.ObjectId) continue;
                if (renamingIds.Contains(match.ObjectId)) continue;
                throw new InvalidDataException($"学生姓名“{desiredName}”已被其他 Veyon 电脑使用；无法安全重命名。");
            }
        }

        return renameTargets.ToArray();
    }

    private VeyonNetworkComputer[] BuildRoomImportPlan()
    {
        if (_seatGrid is null || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room ||
            !string.Equals(campus.DisplayName, RoomCampusName.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(room.DisplayName, RoomLocationName.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请先选择并保存当前校区和机房座位图。");
        var seats = _seatGrid.Seats.OrderBy(seat => seat.Row).ThenBy(seat => seat.Column)
            .Where(seat => !string.IsNullOrWhiteSpace(seat.StudentName)).ToArray();
        if (seats.Length == 0)
            throw new InvalidDataException("请至少为一个桌位填写学生姓名；空桌位不会生成学生电脑。");
        if (seats.Any(seat => string.IsNullOrWhiteSpace(seat.StudentName)))
            throw new InvalidDataException("学生姓名无效；空姓名桌位应从学生电脑清单中排除。");
        var unnumbered = seats.Where(seat => seat.ComputerNumber is null).ToArray();
        if (unnumbered.Length > 0)
            throw new InvalidDataException($"有 {unnumbered.Length} 个已填写姓名的座位尚未分配电脑编号。请编辑座位填写编号，或点“给待编号座位分配新号”；已有编号不会改变。");

        var hostOverrides = ParseHostOverrides(RoomComputerHosts, MachineNaming.MaximumNumber);
        return seats.Select(seat =>
        {
            var number = seat.ComputerNumber!.Value;
            var name = MachineNaming.CreateName(RoomPrefix, number.ToString(CultureInfo.InvariantCulture));
            var hostOverride = seat.VeyonHostAddress ?? ResolveRoomHostOverride(number, hostOverrides);
            var host = string.IsNullOrWhiteSpace(hostOverride) ? name : hostOverride;
            return new VeyonNetworkComputer(name, host, seat.StudentName.Trim());
        }).ToArray();
    }

    private string? ResolveRoomHostOverride(int computerNumber, IReadOnlyList<string> hostOverrides)
    {
        if (SelectedRoomProfile?.ComputerNumbers is { } assignedNumbers)
        {
            var index = assignedNumbers.IndexOf(computerNumber);
            return index >= 0 && index < hostOverrides.Count && hostOverrides[index].Length > 0
                ? hostOverrides[index] : null;
        }

        var startNumber = int.TryParse(RoomStart, NumberStyles.None, CultureInfo.InvariantCulture,
            out var configuredStart) ? configuredStart : SelectedRoomProfile?.StartNumber ?? 1;
        var hostIndex = computerNumber - startNumber;
        return hostIndex >= 0 && hostIndex < hostOverrides.Count && hostOverrides[hostIndex].Length > 0
            ? hostOverrides[hostIndex] : null;
    }

    private static int ParseComputerNumber(string prefix, string computerName)
    {
        if (!computerName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(computerName.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                out var number) || number is < 1 or > MachineNaming.MaximumNumber ||
            !string.Equals(MachineNaming.CreateName(prefix, number.ToString(CultureInfo.InvariantCulture)),
                computerName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"电脑名称“{computerName}”无法解析为有效编号。");
        return number;
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
            CampusDirectoryError = "保存本机地点清单失败；原文件未更改。" + exception.Message;
            return false;
        }
    }

    private void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(IsExecuting))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanGenerateApplicationCandidates)));
    }
}

public sealed record TeacherSeatCell(int Row, int Column, int? SeatNumber, string StudentName,
    string ComputerLabel, bool IsSeat, bool HasVeyonTarget, bool IsSwapSelected = false,
    Guid? VeyonObjectId = null, string VeyonHostAddress = "", bool CanEdit = true,
    bool IsSeatAddRemoveMode = false, bool IsNewlyAdded = false, string VeyonComputerName = "")
{
    public bool IsAisle => !IsSeat;
    public bool HasStudentName => IsSeat && !string.IsNullOrWhiteSpace(StudentName);
    public bool CanEditComputerNumber => IsSeat && !HasVeyonTarget;
    public bool IsSeatWithoutVeyonTarget => IsSeat && !HasVeyonTarget;
    public string ComputerStatusLabel => "填写姓名后自动关联电脑编号";
    public string PrimaryLabel => IsSeat
        ? $"{(SeatNumber is { } number ? $"电脑编号 {number}" : "电脑编号待设置")} · {(!string.IsNullOrWhiteSpace(StudentName) ? StudentName : "未填写姓名")}"
        : "＋ 添加座位";
    public string SecondaryLabel => IsSeat ? "桌位" : $"第 {Row} 行 · 第 {Column} 列 · 空桌位区域";
    public string GridLabel => !IsSeat ? "" : SeatNumber?.ToString(CultureInfo.InvariantCulture) ??
        (HasStudentName ? "!" : "—");
    public bool NeedsComputerNumber => HasStudentName && SeatNumber is null;
    public string TooltipText => IsSeat
        ? (IsNewlyAdded ? "本次新增座位\n" : "") +
          $"座位 {(SeatNumber is { } number ? number.ToString(CultureInfo.InvariantCulture) : "待编号")} · {(!string.IsNullOrWhiteSpace(StudentName) ? StudentName : "未填写姓名")}" +
          (HasVeyonTarget ? "\n已关联 Veyon 设备" :
              HasStudentName ? "\n电脑编号待分配" : "\n空姓名桌位不会生成电脑") +
          (CanEdit
              ? IsSeatAddRemoveMode ? IsNewlyAdded ? "\n单击可移除此本次新增座位；拖动仅新增" : "\n单击并确认可移除此桌位；拖动仅新增"
                  : "\n点击可编辑姓名"
              : "")
        : $"第 {Row} 行 · 第 {Column} 列\n添加座位模式下点击或拖动可添加桌位";
}

public sealed class TeacherPolicyTargetChoice(string target, string studentName, bool isSelected) : INotifyPropertyChanged
{
    private bool _isSelected = isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Target { get; } = target;
    public string StudentName { get; } = studentName;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

public enum TeacherDeviceHealthCategory
{
    Healthy,
    Attention,
    NeedsManualReview,
    NoResponse,
    Pending
}

public enum TeacherDeviceHealthBatchState
{
    None,
    Queued,
    Delivered,
    Executing,
    Succeeded,
    Failed,
    TimedOut,
    Skipped
}

public enum TeacherDeviceRuntimeFreshness
{
    Unknown,
    Current,
    Stale,
    IdentityConflict
}

public sealed record TeacherDeviceHealthIssue(string Code, string Title,
    TeacherDeviceHealthAlertSeverity Severity, string Detail);

public sealed class TeacherDeviceHealthRow(string target, string studentName, string computerLabel) :
    INotifyPropertyChanged
{
    private static readonly IBrush HealthyForegroundBrush = Brush.Parse("#18794E");
    private static readonly IBrush AttentionForegroundBrush = Brush.Parse("#9A6700");
    private static readonly IBrush ManualReviewForegroundBrush = Brush.Parse("#B42318");
    private static readonly IBrush NoResponseForegroundBrush = Brush.Parse("#536477");
    private static readonly IBrush PendingForegroundBrush = Brush.Parse("#64748B");
    private static readonly IBrush InputGuardActiveForegroundBrush = Brush.Parse("#175CD3");
    private static readonly IBrush InputGuardHealthyForegroundBrush = Brush.Parse("#18794E");
    private static readonly IBrush InputGuardAttentionForegroundBrush = Brush.Parse("#9A6700");
    private static readonly IBrush InputGuardUnknownForegroundBrush = Brush.Parse("#536477");
    private static readonly IBrush InputGuardErrorForegroundBrush = Brush.Parse("#B42318");
    private static readonly IBrush HealthyBackgroundBrush = Brush.Parse("#E8F5EC");
    private static readonly IBrush AttentionBackgroundBrush = Brush.Parse("#FFF4D6");
    private static readonly IBrush ManualReviewBackgroundBrush = Brush.Parse("#FDECEC");
    private static readonly IBrush NoResponseBackgroundBrush = Brush.Parse("#EDF1F5");
    private static readonly IBrush PendingBackgroundBrush = Brush.Parse("#F1F5F9");
    private TeacherDeviceHealthCategory _category = TeacherDeviceHealthCategory.Pending;
    private string _categoryLabel = "等待读取";
    private string _agentSummary = "尚未读取";
    private string _serviceSummary = "尚未读取";
    private string _policySummary = "尚未读取";
    private string _updateSummary = "尚未读取";
    private string _collectedText = "";
    private string _detail = "";
    private string _repairStatus = "";
    private string _issueCode = "";
    private string _issueTitle = "";
    private IReadOnlyList<TeacherDeviceHealthIssue> _issues = Array.Empty<TeacherDeviceHealthIssue>();
    private StudentAgentRuntimeSummary? _runtimeSummary;
    private TeacherDeviceRuntimeFreshness _runtimeFreshness;
    private int _runtimeFailureCount;
    private Guid? _batchOperationId;
    private Guid _batchOperationCommandId;
    private StudentAgentHealthRepairAction? _batchOperationAction;
    private TeacherDeviceHealthBatchState _batchOperationState;
    private string _batchOperationStatus = "";
    private bool _isRepairing;
    private bool _isLocalCache;
    private StudentAgentStatusResponse? _status;
    private string _studentName = studentName;
    private string? _lastObservedBootId;
    private bool _bootChangePendingAcknowledgement;
    private bool _bootChangedAfterActiveInputLock;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Target { get; } = target;
    public string StudentName => _studentName;
    public string ComputerLabel { get; } = computerLabel;
    public string IdentitySummary => $"学生：{StudentName}";
    public string CompactSummary => _runtimeSummary is { } runtime
        ? $"Agent {runtime.AgentVersion} · {ServiceSummary} · {RuntimeFreshnessLabel}"
        : _status is { } status ? $"Agent {status.AgentVersion} · {ServiceSummary}" : AgentSummary;
    public TeacherDeviceHealthCategory Category
    {
        get => _category;
        private set
        {
            if (!Set(ref _category, value)) return;
            Raise(nameof(CategoryForeground));
            Raise(nameof(CategoryBackground));
        }
    }
    public string CategoryLabel { get => _categoryLabel; private set => Set(ref _categoryLabel, value); }
    public IBrush CategoryForeground => Category switch
    {
        TeacherDeviceHealthCategory.Healthy => HealthyForegroundBrush,
        TeacherDeviceHealthCategory.Attention => AttentionForegroundBrush,
        TeacherDeviceHealthCategory.NeedsManualReview => ManualReviewForegroundBrush,
        TeacherDeviceHealthCategory.Pending => PendingForegroundBrush,
        _ => NoResponseForegroundBrush
    };
    public IBrush CategoryBackground => Category switch
    {
        TeacherDeviceHealthCategory.Healthy => HealthyBackgroundBrush,
        TeacherDeviceHealthCategory.Attention => AttentionBackgroundBrush,
        TeacherDeviceHealthCategory.NeedsManualReview => ManualReviewBackgroundBrush,
        TeacherDeviceHealthCategory.Pending => PendingBackgroundBrush,
        _ => NoResponseBackgroundBrush
    };
    public string AgentSummary { get => _agentSummary; private set => Set(ref _agentSummary, value); }
    public string ServiceSummary { get => _serviceSummary; private set => Set(ref _serviceSummary, value); }
    public string PolicySummary { get => _policySummary; private set => Set(ref _policySummary, value); }
    public string InputGuardSummary => FormatInputGuardSummary(_runtimeSummary?.InputGuard,
        _runtimeSummary?.SchemaVersion ?? 0);
    public IBrush InputGuardForeground => _runtimeSummary?.InputGuard?.Phase switch
    {
        StudentInputGuardPhase.Locked => InputGuardActiveForegroundBrush,
        StudentInputGuardPhase.Unlocked or StudentInputGuardPhase.Expired => InputGuardHealthyForegroundBrush,
        StudentInputGuardPhase.GuardExited => InputGuardErrorForegroundBrush,
        StudentInputGuardPhase.Starting or StudentInputGuardPhase.Unlocking => InputGuardAttentionForegroundBrush,
        _ => InputGuardUnknownForegroundBrush
    };
    public string UpdateSummary { get => _updateSummary; private set => Set(ref _updateSummary, value); }
    public string CollectedText { get => _collectedText; private set => Set(ref _collectedText, value); }
    public bool IsLocalCache => _isLocalCache;
    public string Detail { get => _detail; private set { if (Set(ref _detail, value)) Raise(nameof(HasDetail)); } }
    public bool HasDetail => Detail.Length > 0;
    public string IssueCode { get => _issueCode; private set => Set(ref _issueCode, value); }
    public string IssueTitle { get => _issueTitle; private set => Set(ref _issueTitle, value); }
    public IReadOnlyList<TeacherDeviceHealthIssue> Issues => _issues;
    public StudentAgentRuntimeSummary? RuntimeSummary => _runtimeSummary;
    public TeacherDeviceRuntimeFreshness RuntimeFreshness => _runtimeFreshness;
    public string RuntimeFreshnessLabel => RuntimeFreshness switch
    {
        TeacherDeviceRuntimeFreshness.Current => $"最近读回 {(_runtimeSummary?.CollectedUtc.ToLocalTime().ToString("HH:mm:ss") ?? "")}",
        TeacherDeviceRuntimeFreshness.Stale => "状态已过期",
        TeacherDeviceRuntimeFreshness.IdentityConflict => "身份待核对",
        _ => "状态未知"
    };
    public DateTimeOffset? LastCollectedUtc => _runtimeSummary?.CollectedUtc ?? _status?.CollectedUtc;
    public bool IsRecentlyReachable => LastCollectedUtc is { } collected &&
        collected >= DateTimeOffset.UtcNow.AddMinutes(-2) && collected <= DateTimeOffset.UtcNow.AddSeconds(30);
    public StudentVeyonServiceState ServiceState => _runtimeSummary?.VeyonServiceState ?? _status?.Health?.VeyonServiceState ??
        StudentVeyonServiceState.Unknown;
    public string? UpdateTransactionState => _runtimeSummary?.UpdateTransactionState ?? _status?.Health?.UpdateTransactionState;
    public Guid? BatchOperationId => _batchOperationId;
    public Guid BatchOperationCommandId => _batchOperationCommandId;
    public StudentAgentHealthRepairAction? BatchOperationAction => _batchOperationAction;
    public TeacherDeviceHealthBatchState BatchOperationState => _batchOperationState;
    public string BatchOperationStatus { get => _batchOperationStatus; private set
        { if (Set(ref _batchOperationStatus, value)) Raise(nameof(HasBatchOperationStatus)); } }
    public bool HasBatchOperationStatus => BatchOperationStatus.Length > 0;
    public string RepairStatus
    {
        get => _repairStatus;
        set { if (Set(ref _repairStatus, value)) Raise(nameof(HasRepairStatus)); }
    }
    public bool HasRepairStatus => RepairStatus.Length > 0;
    public bool IsRepairing
    {
        get => _isRepairing;
        set
        {
            if (!Set(ref _isRepairing, value)) return;
            Raise(nameof(CanEnsureVeyonServiceRunning));
            Raise(nameof(CanRefreshLegacyEdgePolicy));
        }
    }
    public long? WebsiteRevision => _runtimeSummary?.WebsiteRevision ?? _status?.Website.Revision;
    public bool CanEnsureVeyonServiceRunning
    {
        get
        {
            var health = _status?.Health;
            return IsRecentlyReachable && !IsRepairing && health is not null && health.RunningAsSystem == true &&
                health.ManagedTaskMatchesAgent == true && health.ManagedTaskAclRestricted == true &&
                health.UpdateTransactionState is not ("inProgress" or "needsReview") &&
                health.VeyonServiceState is StudentVeyonServiceState.Stopped or
                    StudentVeyonServiceState.StartPending or StudentVeyonServiceState.StopPending;
        }
    }
    public bool CanRefreshLegacyEdgePolicy
    {
        get
        {
            var status = _status;
            var health = status?.Health;
            return IsRecentlyReachable && !IsRepairing && status is not null && health is not null &&
                health.RunningAsSystem == true && health.ManagedTaskMatchesAgent == true &&
                health.ManagedTaskAclRestricted == true &&
                health.UpdateTransactionState is not ("inProgress" or "needsReview") &&
                status.Website.Revision > 0 && status.Website.Mode != WebsitePolicyMode.Disabled &&
                status.Website.ExpiredUtc is null &&
                (status.Website.ExpiresUtc is null || status.Website.ExpiresUtc > DateTimeOffset.UtcNow) &&
                status.EdgeWebsite?.Revision == status.Website.Revision &&
                status.EdgeWebsite?.ExecutionMode == EdgeWebsitePolicyExecutionMode.LegacyRegistry;
        }
    }

    internal void ApplyLocalCache(TeacherDeviceHealthCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _runtimeSummary = null;
        _status = null;
        _runtimeFreshness = TeacherDeviceRuntimeFreshness.Unknown;
        _isLocalCache = true;
        Category = TeacherDeviceHealthCategory.Pending;
        CategoryLabel = "本地缓存 · 待更新";
        AgentSummary = string.IsNullOrWhiteSpace(entry.AgentSummary) ? "尚无有效签名状态" : entry.AgentSummary;
        ServiceSummary = string.IsNullOrWhiteSpace(entry.ServiceSummary) ? "尚未读取" : entry.ServiceSummary;
        PolicySummary = string.IsNullOrWhiteSpace(entry.PolicySummary) ? "尚未读取" : entry.PolicySummary;
        UpdateSummary = string.IsNullOrWhiteSpace(entry.UpdateSummary) ? "尚未读取" : entry.UpdateSummary;
        CollectedText = entry.LastCollectedUtc is { } collected
            ? $"本机缓存 · 上次设备回执 {collected.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : $"本机缓存 · 上次查询 {entry.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        Detail = $"上次记录为“{entry.PreviousStatus}”；这是本机缓存，当前状态尚未重新确认。";
        _issues = Array.Empty<TeacherDeviceHealthIssue>();
        Raise(nameof(Issues));
        Raise(nameof(RuntimeSummary));
        Raise(nameof(RuntimeFreshness));
        Raise(nameof(RuntimeFreshnessLabel));
        Raise(nameof(LastCollectedUtc));
        Raise(nameof(IsRecentlyReachable));
        Raise(nameof(ServiceState));
        Raise(nameof(UpdateTransactionState));
        Raise(nameof(WebsiteRevision));
        Raise(nameof(InputGuardSummary));
        Raise(nameof(InputGuardForeground));
        Raise(nameof(CompactSummary));
        Raise(nameof(IsLocalCache));
        NotifyRepairActionAvailability();
    }

    internal TeacherDeviceHealthCacheEntry? CreateLocalCacheEntry(string campus, DateTimeOffset savedUtc)
    {
        if (_isLocalCache && _runtimeSummary is null && _status is null) return null;
        return new TeacherDeviceHealthCacheEntry(campus, Target, StudentName, CategoryLabel, AgentSummary,
            ServiceSummary, PolicySummary, UpdateSummary, LastCollectedUtc, savedUtc);
    }

    public void ApplyRuntimeSummary(StudentAgentRuntimeSummaryDeliveryResult result, int consecutiveFailures)
    {
        ArgumentNullException.ThrowIfNull(result);
        _runtimeFailureCount = consecutiveFailures;
        if (!result.Succeeded || result.Summary is not { } summary)
        {
            if (result.IdentityCandidate is not null || result.NeedsReview &&
                (result.Detail.Contains("身份", StringComparison.Ordinal) ||
                 result.Detail.Contains("签名", StringComparison.Ordinal) ||
                 result.Detail.Contains("随机数", StringComparison.Ordinal) ||
                 result.Detail.Contains("字段无效", StringComparison.Ordinal) ||
                 result.Detail.Contains("编码无效", StringComparison.Ordinal)))
            {
                SetRuntimeFreshness(TeacherDeviceRuntimeFreshness.IdentityConflict);
                SetIssues([new TeacherDeviceHealthIssue("agent-identity", "Agent 身份需要核对",
                    TeacherDeviceHealthAlertSeverity.Critical,
                    result.Detail + "；在核对完成前不会把设备标记为正常。")]);
                Detail = result.Detail;
                return;
            }

            RefreshRuntimeFreshness(DateTimeOffset.UtcNow);
            if (_runtimeSummary is null && _status is null && consecutiveFailures < 3)
            {
                SetUnknownPending("尚未收到可信的轻量状态摘要；等待后续自动重试。未知状态不会标记为正常。");
                Detail = result.Detail;
            }
            else if (_runtimeSummary is null && _status is null ||
                     _runtimeFreshness is TeacherDeviceRuntimeFreshness.Stale or TeacherDeviceRuntimeFreshness.Unknown)
            {
                var detail = consecutiveFailures >= 3
                    ? $"连续 {consecutiveFailures} 次未收到签名摘要；设备状态未知，不代表电脑已关机。{result.Detail}"
                    : "尚未收到可信的轻量状态摘要；当前状态未知。";
                SetIssues([new TeacherDeviceHealthIssue("agent-no-response", "Agent 状态未知",
                    TeacherDeviceHealthAlertSeverity.Informational, detail)]);
                Detail = detail;
            }
            return;
        }

        var previousSummary = _runtimeSummary;
        var previousFreshness = _runtimeFreshness;
        if (summary.BootId is { } bootId)
        {
            if (_lastObservedBootId is { } previousBootId &&
                !string.Equals(previousBootId, bootId, StringComparison.OrdinalIgnoreCase))
            {
                var previousLockWasActive = previousSummary is { } previous &&
                    previous.InputGuard is
                    {
                        Phase: StudentInputGuardPhase.Locked,
                        LeaseExpiresUtc: { } leaseExpiresUtc
                    } &&
                    leaseExpiresUtc > previous.CollectedUtc;
                _bootChangePendingAcknowledgement = true;
                _bootChangedAfterActiveInputLock = previousFreshness == TeacherDeviceRuntimeFreshness.Current &&
                    previousLockWasActive;
            }
            _lastObservedBootId = bootId;
        }

        _runtimeSummary = summary;
        if (_isLocalCache)
        {
            _isLocalCache = false;
            Raise(nameof(IsLocalCache));
        }
        SetRuntimeFreshness(TeacherDeviceRuntimeFreshness.Current);
        Raise(nameof(RuntimeSummary));
        Raise(nameof(LastCollectedUtc));
        Raise(nameof(IsRecentlyReachable));
        Raise(nameof(ServiceState));
        Raise(nameof(UpdateTransactionState));
        Raise(nameof(WebsiteRevision));
        Raise(nameof(InputGuardSummary));
        Raise(nameof(InputGuardForeground));
        AgentSummary = $"Agent {summary.AgentVersion} 已签名响应" +
            (summary.StudentSetupVersion is { Length: > 0 } setup ? $" · Setup {setup}" : "");
        ServiceSummary = summary.VeyonServiceState switch
        {
            StudentVeyonServiceState.Running => "VeyonService 正在运行",
            StudentVeyonServiceState.Stopped => "VeyonService 已停止",
            StudentVeyonServiceState.StartPending => "VeyonService 正在启动",
            StudentVeyonServiceState.StopPending => "VeyonService 正在停止",
            StudentVeyonServiceState.NotInstalled => "未安装 VeyonService",
            _ => "VeyonService 状态未知"
        };
        var websiteMode = summary.WebsiteMode switch
        {
            WebsitePolicyMode.Disabled => "网站限制已解除",
            WebsitePolicyMode.Blocklist => "阻止指定网站",
            WebsitePolicyMode.Allowlist => "仅允许指定网站",
            WebsitePolicyMode.Combined => "阻止并设置例外",
            _ => "网站策略未知"
        };
        PolicySummary = $"网站 r{summary.WebsiteRevision}：{websiteMode}" +
            (summary.ApplicationSupported ? $" · 应用 {summary.ApplicationMode} r{summary.ApplicationRevision}" : " · 应用策略未启用") +
            (summary.SystemPolicySupported ? summary.SystemPolicyPending == true ? " · Windows 策略待处理" : " · Agent 已报告 Windows 策略（效果待核验）" : " · Windows 策略未启用") +
            (summary.EdgeProfileCount is { } edgeCount
                ? $" · Edge Profile 降级 {summary.EdgeDegradedProfileCount}/{edgeCount}"
                : " · Edge 策略回执未提供");
        UpdateSummary = FormatUpdateState(summary.UpdateTransactionState);
        CollectedText = $"签名轻量状态采集于 {summary.CollectedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        Raise(nameof(CompactSummary));
        Detail = "";

        var issues = new List<TeacherDeviceHealthIssue>();
        var detailedHealth = _status?.Health;
        if (detailedHealth is not null &&
            (detailedHealth.RunningAsSystem != true || detailedHealth.ManagedTaskMatchesAgent != true ||
             detailedHealth.ManagedTaskAclRestricted != true))
            issues.Add(new("agent-integrity", "Agent 身份或计划任务异常",
                TeacherDeviceHealthAlertSeverity.Critical,
                "最近一次详细诊断未能确认 Agent 的 SYSTEM 身份和受保护计划任务；重新读取详细诊断确认。"));
        if (summary.UpdateTransactionState == "needsReview")
            issues.Add(new("update-needs-review", "更新事务需要恢复",
                TeacherDeviceHealthAlertSeverity.Critical, "学生端更新事务需要人工恢复；确认恢复前不要继续修复。"));
        else if (summary.UpdateTransactionState == "inProgress")
            issues.Add(new("update-in-progress", "学生端更新事务进行中",
                TeacherDeviceHealthAlertSeverity.Warning, "更新事务尚未结束；请等待完成并重新读取状态。"));
        if (summary.VeyonServiceState is StudentVeyonServiceState.Stopped or
            StudentVeyonServiceState.StartPending or StudentVeyonServiceState.StopPending or
            StudentVeyonServiceState.NotInstalled)
            issues.Add(new("veyon-service", "Veyon 服务异常",
                TeacherDeviceHealthAlertSeverity.Warning, "Veyon 服务未确认处于运行状态；可读取详细诊断后查看安全操作。"));
        else if (summary.VeyonServiceState == StudentVeyonServiceState.Unknown)
            issues.Add(new("veyon-service-unknown", "Veyon 服务状态未知",
                TeacherDeviceHealthAlertSeverity.Informational, "Agent 没有读回 Veyon 服务状态；未知状态不会按正常处理。"));
        if (summary.SystemPolicyPending == true)
            issues.Add(new("system-policy-pending", "Windows 系统策略待处理",
                TeacherDeviceHealthAlertSeverity.Warning, "Windows 系统策略仍待处理；展开详细诊断查看状态。"));
        if (summary.InputGuard?.Phase is StudentInputGuardPhase.GuardExited)
            issues.Add(new("input-guard-exited", "InputGuard 已退出",
                TeacherDeviceHealthAlertSeverity.Warning,
                "交互桌面 Guard 已退出；当前键鼠状态需重新读取，不能据此判断设备仍被锁定。"));
        else if (summary.InputGuard?.Phase is StudentInputGuardPhase.Unknown)
            issues.Add(new("input-guard-unknown", "键鼠状态未知",
                TeacherDeviceHealthAlertSeverity.Informational,
                "Agent 未能从交互桌面 Guard 读回状态；未知状态不会按已解锁或已锁定处理。"));
        else if (summary.InputGuard?.Phase is StudentInputGuardPhase.Starting or StudentInputGuardPhase.Unlocking)
            issues.Add(new("input-guard-transition", "键鼠状态正在切换",
                TeacherDeviceHealthAlertSeverity.Warning,
                "Guard 正在启动或解除；请等待下一次签名状态刷新。"));
        if (_bootChangePendingAcknowledgement)
            issues.Add(new("boot-changed", "学生电脑已重启",
                _bootChangedAfterActiveInputLock
                    ? TeacherDeviceHealthAlertSeverity.Warning
                    : TeacherDeviceHealthAlertSeverity.Informational,
                _bootChangedAfterActiveInputLock
                    ? "检测到 Windows 已重启；此前的键鼠锁定不会自动恢复。请核对设备状态，需要时再手动启用。"
                    : "检测到 Windows 已重启；重启前的课堂状态不会自动恢复，需要时请重新核对。"));
        if (summary.EdgeDegradedProfileCount is > 0)
            issues.Add(new("edge-policy-degraded", "Edge 策略执行器降级",
                TeacherDeviceHealthAlertSeverity.Warning,
                $"Edge 有 {summary.EdgeDegradedProfileCount}/{summary.EdgeProfileCount} 个 Profile 未确认正常应用；请读取详细诊断。"));
        SetIssues(issues);
    }

    public void AcknowledgeBootChange()
    {
        if (!_bootChangePendingAcknowledgement) return;
        _bootChangePendingAcknowledgement = false;
        _bootChangedAfterActiveInputLock = false;
        SetIssues(_issues.Where(issue => issue.Code != "boot-changed"));
        if (_issues.Count == 0) Detail = "";
    }

    public void RefreshRuntimeFreshness(DateTimeOffset nowUtc)
    {
        if (_runtimeFreshness == TeacherDeviceRuntimeFreshness.IdentityConflict) return;
        var collected = _runtimeSummary?.CollectedUtc ?? _status?.CollectedUtc;
        var next = collected is null
            ? TeacherDeviceRuntimeFreshness.Unknown
            : collected.Value >= nowUtc.AddSeconds(-45)
                ? TeacherDeviceRuntimeFreshness.Current
                : TeacherDeviceRuntimeFreshness.Stale;
        if (next == _runtimeFreshness)
        {
            if (next == TeacherDeviceRuntimeFreshness.Unknown && _runtimeSummary is null && _status is null)
            {
                if (_runtimeFailureCount >= 3 && !_issues.Any(issue => issue.Code == "agent-no-response"))
                    SetIssues([new TeacherDeviceHealthIssue("agent-no-response", "Agent 状态未知",
                        TeacherDeviceHealthAlertSeverity.Informational,
                        $"连续 {_runtimeFailureCount} 次未收到可信摘要；设备状态未知，不代表电脑已关机。")]);
                else if (_runtimeFailureCount < 3 && _issues.Any(issue => issue.Code == "agent-no-response"))
                    SetUnknownPending("尚未收到可信的轻量状态摘要；等待后续自动重试。未知状态不会标记为正常。");
            }
            return;
        }
        SetRuntimeFreshness(next);
        if (next == TeacherDeviceRuntimeFreshness.Stale)
        {
            var detail = $"最后一次签名状态于 {collected!.Value.ToLocalTime():HH:mm:ss}；超过 45 秒未能更新，设备状态视为未知。";
            SetIssues([new TeacherDeviceHealthIssue("agent-no-response", "Agent 状态已过期",
                TeacherDeviceHealthAlertSeverity.Informational, detail)]);
            Detail = detail;
        }
        else if (next == TeacherDeviceRuntimeFreshness.Unknown && _runtimeSummary is null && _status is null &&
                 _runtimeFailureCount >= 3)
        {
            SetIssues([new TeacherDeviceHealthIssue("agent-no-response", "Agent 状态未知",
                TeacherDeviceHealthAlertSeverity.Informational,
                "尚未收到可信的轻量状态摘要；未知状态不会标记为正常。")]);
        }
        else if (next == TeacherDeviceRuntimeFreshness.Unknown && _runtimeSummary is null && _status is null)
            SetUnknownPending("尚未读取到可信的轻量状态摘要；未知状态不会标记为正常。");
    }

    public void SetStudentName(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "未填写姓名" : value.Trim();
        if (!Set(ref _studentName, normalized, nameof(StudentName))) return;
        Raise(nameof(IdentitySummary));
    }

    public void Apply(StudentAgentStatusDeliveryResult result)
    {
        if (!result.Succeeded || result.Status is not { } status)
        {
            _status = null;
            NotifyRepairActionAvailability();
            var identityProblem = result.IdentityCandidate is not null ||
                (result.Detail.Contains("签名", StringComparison.Ordinal) &&
                 !result.Detail.Contains("超时", StringComparison.Ordinal) &&
                 !result.Detail.Contains("无法连接", StringComparison.Ordinal));
            Category = identityProblem
                ? TeacherDeviceHealthCategory.NeedsManualReview
                : TeacherDeviceHealthCategory.NoResponse;
            CategoryLabel = identityProblem ? "需人工核对身份" : "未收到有效回执";
            IssueCode = identityProblem ? "agent-identity" : "agent-no-response";
            IssueTitle = identityProblem ? "Agent 身份需要核对" : "Agent 未收到回执";
            SetIssues([new TeacherDeviceHealthIssue(IssueCode, IssueTitle,
                identityProblem ? TeacherDeviceHealthAlertSeverity.Critical :
                    TeacherDeviceHealthAlertSeverity.Informational,
                identityProblem ? "身份回执需要教师核对；暂不执行修复。" :
                    "未收到有效回执；这不能说明电脑已关机。确认地址、Agent 和网络后重新读取。")]);
            AgentSummary = identityProblem ? "身份回执需要教师核对" : "Agent 未取得有效签名状态";
            Raise(nameof(CompactSummary));
            ServiceSummary = "未读取";
            PolicySummary = "未读取";
            UpdateSummary = "未读取";
            if (CollectedText.Length == 0) CollectedText = "尚无成功读取记录";
            Detail = identityProblem
                ? "Agent 身份或签名需要现场核对；暂不执行修复。"
                : "未收到有效回执；这不能说明电脑已关机。确认电脑地址、Agent 和网络后重新读取。";
            return;
        }

        _status = status;
        if (_isLocalCache)
        {
            _isLocalCache = false;
            Raise(nameof(IsLocalCache));
        }
        Raise(nameof(LastCollectedUtc));
        Raise(nameof(IsRecentlyReachable));
        Raise(nameof(ServiceState));
        Raise(nameof(UpdateTransactionState));
        var health = status.Health;
        AgentSummary = health is null
            ? $"Agent {status.AgentVersion} 已响应；此版本未提供完整健康快照"
            : $"Agent {status.AgentVersion} · {(health.RunningAsSystem == true ? "SYSTEM 身份已确认" : "SYSTEM 身份未确认")} · " +
              (health.ManagedTaskMatchesAgent == true ? "计划任务路径匹配" : "计划任务路径未确认") + " · " +
              (health.ManagedTaskAclRestricted == true ? "任务权限受限" : "任务权限未确认");
        ServiceSummary = health?.VeyonServiceState switch
        {
            StudentVeyonServiceState.Running => "VeyonService 正在运行",
            StudentVeyonServiceState.Stopped => "VeyonService 已停止",
            StudentVeyonServiceState.StartPending => "VeyonService 正在启动",
            StudentVeyonServiceState.StopPending => "VeyonService 正在停止",
            StudentVeyonServiceState.NotInstalled => "未安装 VeyonService",
            _ => "VeyonService 状态未知"
        };
        Raise(nameof(CompactSummary));
        PolicySummary = BuildPolicySummary(status);
        UpdateSummary = FormatUpdateState(health?.UpdateTransactionState);
        CollectedText = $"签名状态采集于 {status.CollectedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        Detail = "";

        if (health is null)
        {
            SetCategory(TeacherDeviceHealthCategory.Attention,
                "Agent 尚未提供 SYSTEM 任务和 Veyon 服务健康读回；请更新学生端后重新诊断。",
                "agent-health-unsupported", "Agent 健康快照不完整");
        }
        else if (health.UpdateTransactionState == "needsReview")
        {
            SetCategory(TeacherDeviceHealthCategory.NeedsManualReview,
                "更新事务需要人工恢复；在恢复完成前不运行修复操作。",
                "update-needs-review", "更新事务需要恢复");
        }
        else if (health.RunningAsSystem != true || health.ManagedTaskMatchesAgent != true ||
                 health.ManagedTaskAclRestricted != true)
        {
            SetCategory(TeacherDeviceHealthCategory.NeedsManualReview,
                "Agent 运行身份或受保护计划任务需要人工检查。",
                "agent-integrity", "Agent 身份或计划任务异常");
        }
        else if (health.UpdateTransactionState == "inProgress")
        {
            SetCategory(TeacherDeviceHealthCategory.Attention,
                "学生端更新事务仍在进行；完成或恢复前不运行修复操作。",
                "update-in-progress", "学生端更新事务仍在进行");
        }
        else if (health.VeyonServiceState != StudentVeyonServiceState.Running)
        {
            SetCategory(TeacherDeviceHealthCategory.Attention,
                "Veyon 服务没有确认处于运行状态。",
                "veyon-service", "Veyon 服务异常");
        }
        else if (status.SystemPolicy?.Pending == true)
        {
            SetCategory(TeacherDeviceHealthCategory.Attention,
                "Windows 系统策略仍待处理；读取回执确认是否已应用。",
                "system-policy-pending", "Windows 系统策略待处理");
        }
        else if (status.EdgeWebsite?.Profiles.Any(profile => profile.Status == "degraded") == true)
        {
            SetCategory(TeacherDeviceHealthCategory.Attention,
                "Edge 策略执行器报告降级；展开策略详情并重新读取回执。",
                "edge-policy-degraded", "Edge 策略执行器降级");
        }
        else
        {
            SetCategory(TeacherDeviceHealthCategory.Healthy,
                "Agent、受保护任务和 Veyon 服务均已读回；没有发现待恢复更新事务。", "", "");
        }
        NotifyRepairActionAvailability();
    }

    public void SetRepairResult(StudentAgentHealthRepairDeliveryResult result)
    {
        RepairStatus = result.Detail;
        NotifyRepairActionAvailability();
    }

    public void RefreshHealthFreshness()
    {
        Raise(nameof(IsRecentlyReachable));
        Raise(nameof(CanEnsureVeyonServiceRunning));
        Raise(nameof(CanRefreshLegacyEdgePolicy));
    }

    public void BeginBatchOperation(Guid batchId, StudentAgentHealthRepairAction action,
        TeacherDeviceHealthBatchState state, string detail) =>
        SetBatchOperation(batchId, Guid.Empty, action, state, detail);

    public void SetBatchOperation(Guid batchId, Guid commandId, StudentAgentHealthRepairAction action,
        TeacherDeviceHealthBatchState state, string detail)
    {
        if (batchId == Guid.Empty) return;
        if (_batchOperationId != batchId)
        {
            _batchOperationId = batchId;
            _batchOperationState = TeacherDeviceHealthBatchState.None;
            _batchOperationCommandId = Guid.Empty;
        }
        if (IsTerminalBatchState(_batchOperationState) && state != _batchOperationState) return;
        if (GetBatchStateRank(state) < GetBatchStateRank(_batchOperationState)) return;
        _batchOperationCommandId = commandId == Guid.Empty ? _batchOperationCommandId : commandId;
        _batchOperationAction = action;
        _batchOperationState = state;
        IsRepairing = state is TeacherDeviceHealthBatchState.Queued or
            TeacherDeviceHealthBatchState.Delivered or TeacherDeviceHealthBatchState.Executing;
        BatchOperationStatus = $"批次 {batchId.ToString("N")[..8]} · {FormatBatchState(state)} · {detail}";
        Raise(nameof(BatchOperationId));
        Raise(nameof(BatchOperationCommandId));
        Raise(nameof(BatchOperationAction));
        Raise(nameof(BatchOperationState));
    }

    private static int GetBatchStateRank(TeacherDeviceHealthBatchState state) => state switch
    {
        TeacherDeviceHealthBatchState.None => -1,
        TeacherDeviceHealthBatchState.Queued => 0,
        TeacherDeviceHealthBatchState.Delivered => 1,
        TeacherDeviceHealthBatchState.Executing => 2,
        _ => 3
    };

    private static bool IsTerminalBatchState(TeacherDeviceHealthBatchState state) => state is
        TeacherDeviceHealthBatchState.Succeeded or TeacherDeviceHealthBatchState.Failed or
        TeacherDeviceHealthBatchState.TimedOut or TeacherDeviceHealthBatchState.Skipped;

    private static string FormatBatchState(TeacherDeviceHealthBatchState state) => state switch
    {
        TeacherDeviceHealthBatchState.Queued => "queued · 等待发送",
        TeacherDeviceHealthBatchState.Delivered => "delivered · Agent 已签收",
        TeacherDeviceHealthBatchState.Executing => "executing · Agent 正在执行",
        TeacherDeviceHealthBatchState.Succeeded => "succeeded · 已完成",
        TeacherDeviceHealthBatchState.Failed => "failed · 未完成",
        TeacherDeviceHealthBatchState.TimedOut => "timed_out · 等待回执超时",
        TeacherDeviceHealthBatchState.Skipped => "skipped · 本次跳过",
        _ => "尚无批量操作"
    };

    private void NotifyRepairActionAvailability()
    {
        Raise(nameof(WebsiteRevision));
        Raise(nameof(LastCollectedUtc));
        Raise(nameof(IsRecentlyReachable));
        Raise(nameof(ServiceState));
        Raise(nameof(UpdateTransactionState));
        Raise(nameof(CanEnsureVeyonServiceRunning));
        Raise(nameof(CanRefreshLegacyEdgePolicy));
    }

    private void SetCategory(TeacherDeviceHealthCategory category, string detail,
        string issueCode, string issueTitle)
    {
        Category = category;
        CategoryLabel = category switch
        {
            TeacherDeviceHealthCategory.Healthy => "正常",
            TeacherDeviceHealthCategory.Attention => "需关注",
            TeacherDeviceHealthCategory.NeedsManualReview => "需人工处理",
            TeacherDeviceHealthCategory.Pending => "等待读取",
            _ => "未收到回执"
        };
        IssueCode = issueCode;
        IssueTitle = issueTitle;
        Detail = detail;
        var severity = category switch
        {
            TeacherDeviceHealthCategory.NeedsManualReview => TeacherDeviceHealthAlertSeverity.Critical,
            TeacherDeviceHealthCategory.Attention => TeacherDeviceHealthAlertSeverity.Warning,
            TeacherDeviceHealthCategory.NoResponse => TeacherDeviceHealthAlertSeverity.Informational,
            _ => TeacherDeviceHealthAlertSeverity.Informational
        };
        SetIssues(issueCode.Length == 0 ? Array.Empty<TeacherDeviceHealthIssue>() :
            [new TeacherDeviceHealthIssue(issueCode, issueTitle, severity, detail)]);
    }

    private void SetIssues(IEnumerable<TeacherDeviceHealthIssue> issues)
    {
        var next = Array.AsReadOnly(issues.GroupBy(issue => issue.Code, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray());
        _issues = next;
        Raise(nameof(Issues));
        var first = next.FirstOrDefault();
        IssueCode = first?.Code ?? "";
        IssueTitle = first?.Title ?? "";
        var category = next.Any(issue => issue.Severity == TeacherDeviceHealthAlertSeverity.Critical)
            ? TeacherDeviceHealthCategory.NeedsManualReview
            : next.Any(issue => issue.Severity == TeacherDeviceHealthAlertSeverity.Warning)
                ? TeacherDeviceHealthCategory.Attention
                : next.Any(issue => issue.Code == "agent-no-response")
                    ? TeacherDeviceHealthCategory.NoResponse
                    : next.Count == 0 ? TeacherDeviceHealthCategory.Healthy : TeacherDeviceHealthCategory.Attention;
        Category = category;
        CategoryLabel = category switch
        {
            TeacherDeviceHealthCategory.Healthy => "正常",
            TeacherDeviceHealthCategory.Attention => "需关注",
            TeacherDeviceHealthCategory.NeedsManualReview => "需人工处理",
            _ => "状态未知"
        };
        if (next.Count > 0) Detail = string.Join(Environment.NewLine, next.Select(issue => issue.Detail));
    }

    private void SetRuntimeFreshness(TeacherDeviceRuntimeFreshness value)
    {
        if (_runtimeFreshness == value) return;
        _runtimeFreshness = value;
        Raise(nameof(RuntimeFreshness));
        Raise(nameof(RuntimeFreshnessLabel));
        Raise(nameof(CompactSummary));
        Raise(nameof(IsRecentlyReachable));
        Raise(nameof(CanEnsureVeyonServiceRunning));
        Raise(nameof(CanRefreshLegacyEdgePolicy));
    }

    private void SetUnknownPending(string detail)
    {
        SetRuntimeFreshness(TeacherDeviceRuntimeFreshness.Unknown);
        Category = TeacherDeviceHealthCategory.Pending;
        CategoryLabel = _isLocalCache ? "本地缓存 · 等待确认" : "状态待确认";
        IssueCode = "";
        IssueTitle = "";
        if (_issues.Count != 0)
        {
            _issues = Array.Empty<TeacherDeviceHealthIssue>();
            Raise(nameof(Issues));
        }
        if (!_isLocalCache) AgentSummary = "尚未收到可信的签名状态";
        Raise(nameof(CompactSummary));
        Detail = _isLocalCache
            ? $"本机上次记录尚未过期保存；本轮未收到可信状态。{detail}"
            : detail;
    }

    private static string BuildPolicySummary(StudentAgentStatusResponse status)
    {
        var websiteMode = status.Website.Mode switch
        {
            WebsitePolicyMode.Disabled => "网站限制已解除",
            WebsitePolicyMode.Blocklist => "阻止指定网站",
            WebsitePolicyMode.Allowlist => "仅允许指定网站",
            WebsitePolicyMode.Combined => "阻止并设置例外",
            _ => "网站策略未知"
        };
        var application = status.Application is { Supported: true } app
            ? $"应用 {app.Mode?.ToString() ?? "已读回"} r{app.Revision?.ToString() ?? "?"}"
            : "应用策略未启用";
        var system = status.SystemPolicy is { Supported: true } systemState
            ? systemState.Pending ? "Windows 策略待处理" : "Agent 已报告 Windows 策略；效果待核验"
            : "Windows 策略未启用";
        var edge = status.EdgeWebsite is { ExecutionMode: EdgeWebsitePolicyExecutionMode.LegacyRegistry }
            ? "Edge 使用旧注册表执行模式"
            : status.EdgeWebsite is { } edgeState
            ? $"Edge 扩展已读回 {edgeState.Profiles.Count(profile => profile.Status == "applied")} / {edgeState.Profiles.Count} 个 Profile"
            : "Edge 扩展回执未提供";
        return $"网站 r{status.Website.Revision}：{websiteMode} · {application} · {system} · {edge}";
    }

    private static string FormatInputGuardSummary(StudentInputGuardRuntimeSnapshot? guard, int schemaVersion)
    {
        if (guard is null)
            return schemaVersion == StudentAgentRuntimeSummaryCryptography.LegacySchemaVersion
                ? "键鼠状态：此 Agent 版本未提供，请更新学生 Agent"
                : "键鼠状态：尚未读取";
        if (!guard.Supported) return "键鼠状态：Agent 不支持或未安装 Guard";
        return guard.Phase switch
        {
            StudentInputGuardPhase.Unlocked => "键鼠状态：未锁定",
            StudentInputGuardPhase.Starting => "键鼠状态：正在启动 Guard",
            StudentInputGuardPhase.Locked => guard.LeaseExpiresUtc is { } expiry
                ? $"键鼠状态：已锁定 · 租约至 {expiry.ToLocalTime():HH:mm:ss}"
                : "键鼠状态：已锁定",
            StudentInputGuardPhase.Unlocking => "键鼠状态：正在解除",
            StudentInputGuardPhase.Expired => "键鼠状态：租约已到期",
            StudentInputGuardPhase.GuardExited => "键鼠状态：Guard 已退出，需复核",
            _ => "键鼠状态：未知"
        };
    }

    private static string FormatUpdateState(string? state) => state switch
    {
        "notFound" or null => "当前没有未结更新事务",
        "inProgress" => "更新事务进行中",
        "committed" => "最近事务已提交",
        "rolledBack" => "最近事务已回滚",
        "needsReview" => "更新事务需要人工恢复",
        _ => "更新事务状态未知"
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum TeacherDeviceHealthAlertSeverity
{
    Critical,
    Warning,
    Informational
}

public sealed class TeacherDeviceHealthAlert(string issueCode, string title,
    TeacherDeviceHealthAlertSeverity severity) : INotifyPropertyChanged
{
    private static readonly IBrush CriticalForegroundBrush = Brush.Parse("#B42318");
    private static readonly IBrush WarningForegroundBrush = Brush.Parse("#9A6700");
    private static readonly IBrush InformationalForegroundBrush = Brush.Parse("#536477");
    private static readonly IBrush CriticalBackgroundBrush = Brush.Parse("#FDECEC");
    private static readonly IBrush WarningBackgroundBrush = Brush.Parse("#FFF4D6");
    private static readonly IBrush InformationalBackgroundBrush = Brush.Parse("#EDF1F5");
    private TeacherDeviceHealthAlertSeverity _severity = severity;
    private IReadOnlyList<TeacherDeviceHealthRow> _affectedRows = Array.Empty<TeacherDeviceHealthRow>();
    private string _diagnostics = "";
    private string _title = title;
    private string _affectedTargets = "";
    private int _occurrenceCount;
    private DateTimeOffset _lastSeenUtc;
    private bool _isAcknowledged;
    private bool _isBusy;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string IssueCode { get; } = issueCode;
    public string Title { get => _title; private set => Set(ref _title, value); }
    public TeacherDeviceHealthAlertSeverity Severity
    {
        get => _severity;
        private set
        {
            if (!Set(ref _severity, value)) return;
            Raise(nameof(SeverityLabel));
            Raise(nameof(SeverityForeground));
            Raise(nameof(SeverityBackground));
        }
    }
    public DateTimeOffset FirstSeenUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get => _lastSeenUtc; private set => Set(ref _lastSeenUtc, value); }
    public int OccurrenceCount { get => _occurrenceCount; private set => Set(ref _occurrenceCount, value); }
    public IReadOnlyList<TeacherDeviceHealthRow> AffectedRows
    {
        get => _affectedRows;
        private set
        {
            if (!Set(ref _affectedRows, value)) return;
            Raise(nameof(AffectedCount));
            Raise(nameof(AffectedCountSummary));
            Raise(nameof(CanRecheck));
            Raise(nameof(RecommendedAction));
            Raise(nameof(CanAttemptRepair));
        }
    }
    public int AffectedCount => AffectedRows.Count;
    public string AffectedCountSummary => $"{AffectedCount} 台受影响";
    public string AffectedTargets { get => _affectedTargets; private set => Set(ref _affectedTargets, value); }
    public string Diagnostics { get => _diagnostics; private set => Set(ref _diagnostics, value); }
    public string SeenSummary => $"首次 {FirstSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 最近 {LastSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 读取到 {OccurrenceCount} 次";
    public bool IsAcknowledged
    {
        get => _isAcknowledged;
        private set
        {
            if (!Set(ref _isAcknowledged, value)) return;
            Raise(nameof(AcknowledgementLabel));
            Raise(nameof(CanAcknowledge));
        }
    }
    public string AcknowledgementLabel => IsAcknowledged ? "已知悉" : "标记已知悉";
    public bool CanAcknowledge => !IsAcknowledged;
    public bool CanRecheck => AffectedRows.Count > 0 && !_isBusy;
    public StudentAgentHealthRepairAction? RecommendedAction => IssueCode switch
    {
        "veyon-service" when AffectedRows.Any(row => row.IsRecentlyReachable && row.CanEnsureVeyonServiceRunning) =>
            StudentAgentHealthRepairAction.EnsureVeyonServiceRunning,
        "edge-policy-degraded" when AffectedRows.Any(row => row.IsRecentlyReachable && row.CanRefreshLegacyEdgePolicy) =>
            StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy,
        _ => null
    };
    public bool CanAttemptRepair => RecommendedAction is not null && !_isBusy;
    public string SeverityLabel => Severity switch
    {
        TeacherDeviceHealthAlertSeverity.Critical => "需要人工处理",
        TeacherDeviceHealthAlertSeverity.Warning => "需关注",
        TeacherDeviceHealthAlertSeverity.Informational when IssueCode == "boot-changed" => "已重启",
        _ => "未收到回执"
    };
    public IBrush SeverityForeground => Severity switch
    {
        TeacherDeviceHealthAlertSeverity.Critical => CriticalForegroundBrush,
        TeacherDeviceHealthAlertSeverity.Warning => WarningForegroundBrush,
        _ => InformationalForegroundBrush
    };
    public IBrush SeverityBackground => Severity switch
    {
        TeacherDeviceHealthAlertSeverity.Critical => CriticalBackgroundBrush,
        TeacherDeviceHealthAlertSeverity.Warning => WarningBackgroundBrush,
        _ => InformationalBackgroundBrush
    };

    public void Update(IReadOnlyList<TeacherDeviceHealthRow> affectedRows, bool isNewOccurrence = true,
        TeacherDeviceHealthAlertSeverity? severity = null)
    {
        ArgumentNullException.ThrowIfNull(affectedRows);
        var next = Array.AsReadOnly(affectedRows.ToArray());
        var previousTargets = AffectedRows.Select(row => row.Target)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasNewTargets = next.Any(row => !previousTargets.Contains(row.Target));
        var sameRows = AffectedRows.Count == next.Count && AffectedRows.Zip(next)
            .All(pair => ReferenceEquals(pair.First, pair.Second));
        if (hasNewTargets) IsAcknowledged = false;
        if (severity is { } nextSeverity) Severity = nextSeverity;
        if (next.SelectMany(row => row.Issues).FirstOrDefault(issue => issue.Code == IssueCode) is { } issue)
            Title = issue.Title;
        if (!sameRows) AffectedRows = next;
        if (isNewOccurrence || hasNewTargets) OccurrenceCount++;
        LastSeenUtc = DateTimeOffset.UtcNow;
        AffectedTargets = string.Join("、", next.Take(4).Select(row => row.StudentName)) +
            (next.Count > 4 ? $"等 {next.Count} 台" : "");
        Diagnostics = string.Join(Environment.NewLine, next.Take(8).Select(row =>
            $"学生：{row.StudentName}\n" +
            (row.Issues.FirstOrDefault(issue => issue.Code == IssueCode)?.Detail ?? row.Detail)));
        Raise(nameof(SeenSummary));
        Raise(nameof(SeverityLabel));
    }

    public void Acknowledge() => IsAcknowledged = true;

    public void SetBusy(bool value)
    {
        if (_isBusy == value) return;
        _isBusy = value;
        RefreshRepairAvailability();
    }

    public void RefreshRepairAvailability()
    {
        Raise(nameof(CanRecheck));
        Raise(nameof(RecommendedAction));
        Raise(nameof(CanAttemptRepair));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class StudentApplicationUpdateTargetProgressItem(string displayName) : INotifyPropertyChanged
{
    private string _statusText = "等待发送";
    private string _detail = "";
    private double _progressPercent;
    private bool _isIndeterminate;
    private int _phase;
    private int _priority = 2;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<StudentUpdatePhaseTimingDisplayItem> PhaseTimings { get; } = [];
    public IReadOnlyList<StudentUpdatePhaseTiming> RawPhaseTimings { get; private set; } =
        Array.Empty<StudentUpdatePhaseTiming>();
    public string DisplayName { get; } = displayName;
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string Detail { get => _detail; private set { if (Set(ref _detail, value)) Raise(nameof(HasDetail)); } }
    public bool HasDetail => Detail.Length > 0;
    public bool HasPhaseTimings => PhaseTimings.Count > 0;
    public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }
    public int Priority { get => _priority; private set => Set(ref _priority, value); }

    public void SetTransferProgress(long bytesSent, long totalBytes, double percent)
    {
        if (_phase >= 3) return;
        _phase = 2;
        ProgressPercent = Math.Max(ProgressPercent, Math.Clamp(percent, 0, 100));
        IsIndeterminate = ProgressPercent >= 99.95;
        StatusText = IsIndeterminate
            ? "安装包已接收，正在验证并安装"
            : $"正在接收更新 · {ProgressPercent:0}%";
        Detail = totalBytes > 0
            ? IsIndeterminate
                ? $"已接收 {Format(bytesSent)} MB，等待安装程序完成"
                : $"{Format(bytesSent)} / {Format(totalBytes)} MB"
            : $"已接收 {Format(bytesSent)} MB";
    }

    public void SetCheckingVersions(string detail)
    {
        if (_phase >= 3) return;
        _phase = 1;
        IsIndeterminate = false;
        StatusText = "已核对版本";
        Detail = detail;
    }

    public void SetCurrent(string detail)
    {
        _phase = 5;
        _priority = 3;
        Raise(nameof(Priority));
        IsIndeterminate = false;
        ProgressPercent = 100;
        StatusText = "已是最新";
        Detail = detail;
    }

    public void SetTransportProgress(string stage)
    {
        var (phase, status) = stage.Contains("已完成此电脑处理", StringComparison.Ordinal)
            ? (4, "正在核对更新结果")
            : stage.Contains("安装并重启", StringComparison.Ordinal)
                ? (3, "正在安装，等待学生 Agent 回执")
                : (1, "正在连接学生 Agent");
        if (phase < _phase) return;
        _phase = phase;
        IsIndeterminate = phase is 1 or 3 or 4;
        StatusText = status;
        if (phase == 1 && ProgressPercent == 0) Detail = "";
    }

    public void SetPhaseTimings(IReadOnlyList<StudentUpdatePhaseTiming> timings)
    {
        RawPhaseTimings = Array.AsReadOnly(timings.ToArray());
        Raise(nameof(RawPhaseTimings));
        PhaseTimings.Clear();
        foreach (var timing in timings.OrderBy(item => item.StartedUtc))
        {
            var elapsed = timing.ElapsedMilliseconds is { } duration
                ? $"{duration / 1000d:0.0} 秒"
                : "进行中";
            if (timing.Phase == StudentUpdatePhase.LanTransfer && timing.Bytes is { } bytes)
            {
                var megabytes = bytes / (1024d * 1024d);
                var seconds = timing.ElapsedMilliseconds.GetValueOrDefault() / 1000d;
                elapsed += $" · {megabytes:0.0} MB";
                if (seconds > 0) elapsed += $" · {megabytes / seconds:0.0} MB/s";
            }
            if (timing.Phase == StudentUpdatePhase.LanTransfer && timing.RetryCount is { } retryCount)
                elapsed += $" · 重试 {retryCount} 次";
            if (timing.IntegrityHashMilliseconds is { } hashDuration)
                elapsed += $"（摘要计算 {hashDuration / 1000d:0.0} 秒）";
            var outcome = timing.Outcome switch
            {
                "succeeded" => "完成",
                "failed" => "失败",
                _ => "进行中"
            };
            PhaseTimings.Add(new StudentUpdatePhaseTimingDisplayItem(
                StudentUpdatePhaseDisplayNames.Get(timing.Phase), elapsed, outcome));
        }
        Raise(nameof(HasPhaseTimings));
    }

    public void SetResult(bool succeeded, bool needsReview, string detail, bool rolledBack = false)
    {
        _phase = 5;
        _priority = succeeded ? 3 : rolledBack ? 2 : needsReview ? 1 : 0;
        Raise(nameof(Priority));
        IsIndeterminate = false;
        if (succeeded || rolledBack) ProgressPercent = 100;
        StatusText = succeeded ? "成功" : rolledBack ? "已回滚" : needsReview ? "待核对" : "失败";
        Detail = detail;
    }

    private static string Format(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.CurrentCulture);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record StudentUpdatePhaseTimingDisplayItem(string Phase, string Duration, string Outcome);

internal static class StudentUpdatePhaseDisplayNames
{
    public static string Get(string phase) => phase switch
    {
        StudentUpdatePhase.TeacherOffer => "命令接收与预检",
        StudentUpdatePhase.LanTransfer => "局域网传输",
        StudentUpdatePhase.IntegrityVerify => "签名/摘要校验 CPU累计",
        StudentUpdatePhase.PrepareAndBackup => "更新准备与旧版备份",
        StudentUpdatePhase.SetupInstall => "StudentSetup 安装",
        StudentUpdatePhase.AgentStage => "Agent 文件暂存",
        StudentUpdatePhase.AgentActivate => "Agent 切换",
        StudentUpdatePhase.HealthAndCommit => "健康检查与提交",
        StudentUpdatePhase.Rollback => "回滚与恢复",
        StudentUpdatePhase.TeacherReceipt => "教师端终态签名确认",
        _ => "更新阶段"
    };
}

public sealed record TeacherVeyonTargetChoice(Guid ObjectId, Guid LocationObjectId,
    string ComputerName, string HostAddress, string Location, string StudentName = "未填写姓名")
{
    public override string ToString() => StudentName;
}

public sealed record TeacherClassroomTaskItem(Guid TaskId, string Title, bool IsCompleted);

public sealed class TeacherClassroomEventItem(ClassroomEvent classroomEvent, string? seatLocation = null,
    string displayName = "未填写姓名") : INotifyPropertyChanged
{
    private string _replyMessage = "";
    private string? _error;
    private bool _isAcknowledged;
    private bool _hasReply;
    private bool _isResolved;
    private bool _wasExpired = classroomEvent.ExpiresUtc <= DateTimeOffset.UtcNow;
    private string? _seatLocation = seatLocation;
    private string _displayName = displayName;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ClassroomEvent Event { get; } = classroomEvent;
    public Guid EventId => Event.EventId;
    public bool IsHelpRequest => Event.Type == ClassroomEventType.HelpRequested;
    public string Title => IsHelpRequest ? $"{_displayName} 需要帮助" :
        Event.Target == ClassroomEventCryptography.ClassroomNoticeTarget ? "全班通知" : $"学生通知 · {_displayName}";
    public string? SeatLocation => _seatLocation;
    public bool HasSeatLocation => !string.IsNullOrWhiteSpace(_seatLocation);
    public string Detail => Event.Message ?? "学生需要老师帮助。";
    public string EventTime => Event.IssuedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public bool CanReply => IsHelpRequest && !_hasReply && !_isResolved && Event.ExpiresUtc > DateTimeOffset.UtcNow;
    public string Status => _error ?? (_isResolved ? "已解决" : _hasReply ? "等待学生确认" : _isAcknowledged ? "老师已确认" :
        _wasExpired ? (IsHelpRequest ? "求助已过期" : "通知已过期") : IsHelpRequest ? "等待回复" : "");

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

    public void SetStudentName(string value)
    {
        var next = string.IsNullOrWhiteSpace(value) ? "未填写姓名" : value.Trim();
        if (_displayName == next) return;
        _displayName = next;
        Changed(nameof(Title));
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

public sealed class TeacherClassroomChatThread(string target, string title, string detail, bool isSupported) : INotifyPropertyChanged
{
    private string _title = title;
    private string _detail = detail;
    private bool _isSupported = isSupported;
    private int _unreadCount;
    private readonly HashSet<Guid> _eventIds = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Target { get; } = target;
    public string Title => _title;
    public string Detail => _detail;
    public bool HasDetail => _detail.Length > 0;
    public bool IsSupported => _isSupported;
    public string CapabilityStatus => IsSupported ? "可聊天" : "等待学生端连接";
    public int UnreadCount => _unreadCount;
    public string DisplayTitle => _unreadCount > 0 ? $"{_title} · {_unreadCount} 条新消息" : _title;
    public ObservableCollection<TeacherClassroomChatMessageItem> Messages { get; } = [];

    public void UpdateDisplay(string title, string detail, bool isSupported)
    {
        var titleChanged = _title != title;
        Set(ref _title, title, nameof(Title));
        Changed(nameof(DisplayTitle));
        if (titleChanged)
            foreach (var message in Messages) message.UpdateStudentName(title);
        Set(ref _detail, detail, nameof(Detail));
        Changed(nameof(HasDetail));
        if (_isSupported != isSupported)
        {
            _isSupported = isSupported;
            Changed(nameof(IsSupported));
            Changed(nameof(CapabilityStatus));
        }
    }

    public void AddMessage(ClassroomEvent classroomEvent, bool isIncoming)
    {
        if (!_eventIds.Add(classroomEvent.EventId)) return;
        Messages.Add(new TeacherClassroomChatMessageItem(classroomEvent, _title));
        while (Messages.Count > 200)
        {
            var expired = Messages[0];
            Messages.RemoveAt(0);
            _eventIds.Remove(expired.EventId);
        }
        if (isIncoming)
        {
            _unreadCount++;
            Changed(nameof(UnreadCount));
            Changed(nameof(DisplayTitle));
        }
    }

    public void MarkRead()
    {
        if (_unreadCount == 0) return;
        _unreadCount = 0;
        Changed(nameof(UnreadCount));
        Changed(nameof(DisplayTitle));
    }

    public void MarkDelivered(Guid eventId)
    {
        var message = Messages.FirstOrDefault(item => item.EventId == eventId);
        message?.MarkDelivered();
    }

    public void RefreshDeliveryStatuses(DateTimeOffset nowUtc)
    {
        foreach (var message in Messages) message.RefreshDeliveryStatus(nowUtc);
    }

    public bool MarkRetrySubmitted(Guid eventId, DateTimeOffset expiresUtc)
    {
        var message = Messages.FirstOrDefault(item => item.EventId == eventId);
        if (message is null) return false;
        message.MarkRetrySubmitted(expiresUtc);
        return true;
    }

    private void Set(ref string field, string value, string propertyName)
    {
        if (field == value) return;
        field = value;
        Changed(propertyName);
    }

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class TeacherClassroomChatMessageItem(ClassroomEvent classroomEvent, string studentName) : INotifyPropertyChanged
{
    private bool _isDelivered;
    private bool _canRetry;
    private string _studentName = studentName;
    private DateTimeOffset _deliveryExpiresUtc = classroomEvent.ExpiresUtc;

    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid EventId { get; } = classroomEvent.EventId;
    public string Target { get; } = classroomEvent.Target;
    public string Speaker => classroomEvent.Sender == ClassroomEventSender.Teacher ? "老师" : _studentName;
    public string Message => classroomEvent.Message ?? "";
    public string EventTime => classroomEvent.IssuedUtc.ToLocalTime()
        .ToString("HH:mm", CultureInfo.CurrentCulture);
    public bool HasDeliveryStatus => classroomEvent.Sender == ClassroomEventSender.Teacher;
    public bool CanRetry => _canRetry;
    public string DeliveryStatus => classroomEvent.Sender == ClassroomEventSender.Student
        ? "学生消息"
        : _isDelivered ? "学生端已收到" : _canRetry ? "未收到回执" : "等待学生端接收";

    public void UpdateStudentName(string value)
    {
        if (_studentName == value) return;
        _studentName = value;
        if (classroomEvent.Sender == ClassroomEventSender.Student)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Speaker)));
    }

    public void RefreshDeliveryStatus(DateTimeOffset nowUtc)
    {
        if (classroomEvent.Sender != ClassroomEventSender.Teacher || _isDelivered || _canRetry ||
            _deliveryExpiresUtc > nowUtc.ToUniversalTime()) return;
        _canRetry = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRetry)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeliveryStatus)));
    }

    public void MarkRetrySubmitted(DateTimeOffset expiresUtc)
    {
        var nextExpiry = expiresUtc.ToUniversalTime();
        if (_isDelivered || nextExpiry <= _deliveryExpiresUtc) return;
        _deliveryExpiresUtc = nextExpiry;
        _canRetry = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRetry)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeliveryStatus)));
    }

    public void MarkDelivered()
    {
        if (_isDelivered) return;
        _isDelivered = true;
        _canRetry = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRetry)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeliveryStatus)));
    }
}
