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
using VeyonCampus.Core.Maintenance;
using VeyonCampus.App.Maintenance;

namespace VeyonCampus.App;

public sealed class TeacherClassroomQuickControlDeviceItem : INotifyPropertyChanged
{
    private string _displayName = "";
    private string _statusText = "";
    private string _detail = "";
    private string _updatedText = "";
    private string _summary = "";
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
    private static readonly IBrush NeedsReviewBrush = new SolidColorBrush(Color.Parse("#9A5B00"));
    private static readonly IBrush NeedsReviewSubtleBrush = new SolidColorBrush(Color.Parse("#FFF3D6"));

    public Guid TargetId { get; }
    public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string UpdatedText { get => _updatedText; private set => Set(ref _updatedText, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public IBrush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public IBrush StatusBackground { get => _statusBackground; private set => Set(ref _statusBackground, value); }
    public string WebsiteControlStatus { get; private set; } = "网站状态未知";
    public IBrush WebsiteControlStatusBrush { get; private set; } = NeutralBrush;
    public IBrush WebsiteControlStatusBackground { get; private set; } = NeutralSubtleBrush;
    public string InputControlStatus { get; private set; } = "键鼠状态未知";
    public IBrush InputControlStatusBrush { get; private set; } = NeutralBrush;
    public IBrush InputControlStatusBackground { get; private set; } = NeutralSubtleBrush;
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
        UpdatedText = "记录 " + snapshot.UpdatedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        (WebsiteControlStatus, WebsiteControlStatusBrush, WebsiteControlStatusBackground) =
            snapshot.WebsiteState switch
            {
                "applied" => ("网站策略已应用", SuccessBrush, SuccessSubtleBrush),
                "reported" => ("网站名单已写入 · 待核对", WarningBrush, WarningSubtleBrush),
                "applying" => ("网站策略执行中", InfoBrush, InfoSubtleBrush),
                "waitingForAgent" => ("等待学生端上线", WarningBrush, WarningSubtleBrush),
                "releasing" => ("网站限制正在解除", WarningBrush, WarningSubtleBrush),
                "expired" => ("网站限制已到期", NeutralBrush, NeutralSubtleBrush),
                "needsReview" => ("网站状态需核对", DangerBrush, DangerSubtleBrush),
                "notEnabled" => ("网站限制未启用", NeutralBrush, NeutralSubtleBrush),
                _ => ("网站状态未知", NeutralBrush, NeutralSubtleBrush)
            };
        (InputControlStatus, InputControlStatusBrush, InputControlStatusBackground) =
            snapshot.InputState switch
            {
                "locked" => ("键鼠锁定已核验", InfoBrush, InfoSubtleBrush),
                "waitingForAgent" => ("等待学生端上线", WarningBrush, WarningSubtleBrush),
                "needsReview" => ("键鼠状态需核对", NeedsReviewBrush, NeedsReviewSubtleBrush),
                "notEnabled" => ("本工具未锁键鼠", NeutralBrush, NeutralSubtleBrush),
                _ => ("键鼠状态未知", NeutralBrush, NeutralSubtleBrush)
            };
        Raise(nameof(WebsiteControlStatus));
        Raise(nameof(WebsiteControlStatusBrush));
        Raise(nameof(WebsiteControlStatusBackground));
        Raise(nameof(InputControlStatus));
        Raise(nameof(InputControlStatusBrush));
        Raise(nameof(InputControlStatusBackground));
        Summary = snapshot.Phase switch
        {
            ClassroomQuickControlPhase.WaitingForAgent => "尚未收到学生端回执",
            ClassroomQuickControlPhase.WebsiteReported => "名单已写入，浏览器效果待核对",
            ClassroomQuickControlPhase.WebsiteApplied => "浏览器已报告应用当前名单",
            ClassroomQuickControlPhase.InputLockedVerified => "键鼠锁定已核验",
            ClassroomQuickControlPhase.WebsiteApplying or ClassroomQuickControlPhase.Switching or
                ClassroomQuickControlPhase.InputLocking => "正在发送并确认管控结果",
            ClassroomQuickControlPhase.ReleasePending => "正在解除本工具的管控",
            ClassroomQuickControlPhase.NotStarted => "尚未发送管控",
            ClassroomQuickControlPhase.Unknown => "尚未取得足够的可信状态",
            ClassroomQuickControlPhase.Conflict => "存在策略冲突，请查看详情",
            ClassroomQuickControlPhase.Failed => "操作未完成，请查看原因",
            ClassroomQuickControlPhase.Unsupported => "当前设备暂不支持此操作",
            _ => "管控已解除"
        };
        (StatusText, StatusBrush, StatusBackground) = snapshot.Phase switch
        {
            ClassroomQuickControlPhase.NotStarted => Status("尚未操作", NeutralBrush, NeutralSubtleBrush),
            ClassroomQuickControlPhase.WaitingForAgent => Status("等待上线", WarningBrush, WarningSubtleBrush),
            ClassroomQuickControlPhase.WebsiteApplying or ClassroomQuickControlPhase.Switching or
                ClassroomQuickControlPhase.InputLocking => Status("执行中", InfoBrush, InfoSubtleBrush),
            ClassroomQuickControlPhase.WebsiteApplied => Status("策略已应用", SuccessBrush, SuccessSubtleBrush),
            ClassroomQuickControlPhase.WebsiteReported => Status("已写入 · 待核对", WarningBrush, WarningSubtleBrush),
            ClassroomQuickControlPhase.InputLockedVerified => Status("键鼠已核验", InfoBrush, InfoSubtleBrush),
            ClassroomQuickControlPhase.ReleasePending => Status("正在恢复", WarningBrush, WarningSubtleBrush),
            ClassroomQuickControlPhase.Unknown => Status(
                snapshot.Detail.Contains("InputGuard", StringComparison.OrdinalIgnoreCase) ||
                snapshot.Detail.Contains("键鼠", StringComparison.OrdinalIgnoreCase)
                    ? "键鼠需核对" : "需核对", WarningBrush, WarningSubtleBrush),
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

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed partial class TeacherViewModel : INotifyPropertyChanged
{
    private readonly ConcurrentDictionary<string, byte> _pendingMaintenanceSyncInFlight =
        new(StringComparer.OrdinalIgnoreCase);
    private const string DefaultCampusNamePrefix = "智学前程-";
    private static readonly IBrush QuickControlNeutralBadgeBrush =
        new SolidColorBrush(Color.Parse("#617985"));
    private static readonly IBrush QuickControlWarningBadgeBrush =
        new SolidColorBrush(Color.Parse("#B45309"));
    private static readonly IBrush QuickControlSuccessBadgeBrush =
        new SolidColorBrush(Color.Parse("#16803C"));
    private sealed record PreparedSeatSwapVeyonPlan(string ConfigFingerprint,
        VeyonComputerRenameTargetPlan[] Targets, ClassroomSeatGridNameSwapBinding[] Bindings);
    private sealed record PreparedSeatSwapPlan(Guid CampusProfileId, Guid RoomId,
        ClassroomSeatGridSeat FirstSeatSnapshot, ClassroomSeatGridSeat SecondSeatSnapshot,
        PreparedSeatSwapVeyonPlan? VeyonPlan, string[] PreviewRows);
    private sealed class DeviceRuntimeSummaryPollingState
    {
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset NextPollUtc { get; set; }
    }
    private readonly VeyonInstallerStore _installerStore;
    private readonly string? _websitePolicyDraftDirectory;
    private readonly TeacherCampusDirectoryStore _campusDirectoryStore;
    private readonly ITeacherNetworkDirectory _networkDirectory;
    private readonly ITeacherLoginStartPreference _teacherLoginStartPreference;
    private readonly ClassroomSessionStore _classroomSessionStore;
    private readonly ClassroomSeatGridStore _classroomSeatGridStore;
    private readonly VeyonDeviceDirectoryStore _veyonDeviceDirectoryStore = new();
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
    private readonly ITeacherMaintenanceWorkflow? _maintenanceWorkflow;
    private readonly MaintenanceRunStore _maintenanceRunStore = new();
    private readonly MaintenanceServiceTrustStore _maintenanceIdentityTrustStore = new();
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
        VeyonCampusPaths.GetLocalApplicationDataDirectory(),
        "VeyonCampus", "website-observed-address.txt");
    private readonly Dictionary<string, StudentApplicationUpdateTargetProgressItem> _studentUpdateRowsByTarget =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _studentUpdateTargetsByAddress = new(StringComparer.OrdinalIgnoreCase);
    private string _studentMaintenanceStatus = "检查软件版本并安全同步教师连接配置。";
    private StudentUpdatePerformanceReport? _lastStudentUpdatePerformanceReport;
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
    private string _deviceRuntimeSummaryStatus = "打开设备与更新页后自动刷新学生电脑状态。";
    private string _deviceRuntimeSummaryDiagnostics = "";
    private string _deviceHealthBatchStatus = "批量修复仅使用当前健康快照和固定白名单动作。";
    private bool _isReadingDeviceHealth;
    private bool _isPollingDeviceRuntimeSummary;
    private bool _isRunningDeviceHealthBatch;
    private bool _isClassroomQuickControlBusy;
    private ClassroomQuickControlSnapshot? _classroomQuickControlSnapshot;
    private Guid? _classroomQuickControlRevisionSessionId;
    private long _classroomQuickControlStateRevision = -1;
    private string _classroomQuickControlSessionNotice = "";
    private int _classroomQuickControlCountdownHours;
    private int _classroomQuickControlCountdownMinutes;
    private int _classroomQuickControlCountdownSeconds;
    private Guid? _activeDeviceHealthBatchId;
    private string _activeDeviceHealthBatchCampus = "";
    private HashSet<string> _activeDeviceHealthBatchSelection = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _healthSnapshotFreshnessCancellation;
    private string _deviceHealthRowsCampus = "";
    private string _deviceHealthCacheError = "";
    private string _veyonDeviceDirectorySummary = "尚未读取已验证 Veyon 目录。";
    private VeyonDeviceDirectorySnapshot? _veyonDeviceDirectorySnapshot;
    private long _deviceHealthSelectionRevision;
    private IReadOnlyList<ApplicationCatalogProductChoice> _applicationCatalogChoices = Array.Empty<ApplicationCatalogProductChoice>();
    private IReadOnlyList<ApplicationCatalogProductChoice> _allApplicationCatalogChoices = Array.Empty<ApplicationCatalogProductChoice>();
    private bool _applicationCatalogAllTargetsRead;
    private IReadOnlyList<ApplicationInventoryChoice> _applicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
    private IReadOnlyList<ApplicationInventoryChoice> _allApplicationInventoryChoices = Array.Empty<ApplicationInventoryChoice>();
    private IReadOnlyList<string> _lastFailedWebsiteTargets = Array.Empty<string>();
    private string _lastFailedWebsiteTargetsCampus = "";
    private bool _isExecuting, _isReadingWebsiteLocations, _websiteLocationSelectionPending, _showWebsitePolicyResultDetails;
    private bool _settingWebsiteTargetsFromDirectory;
    private bool _websiteTargetsRequireDirectorySnapshot;
    private bool _veyonDeviceDirectoryReadValid;
    private bool _websiteTargetsLoadedFromPartialRead;
    private string? _websiteTargetsDirectoryFingerprint;
    private bool _isCheckingTeacherSetupVeyon, _teacherSetupVeyonReady, _teacherSetupRoomSkippedForNow;
    private string _teacherSetupVeyonStatus = "尚未检查教师端 Veyon。";
    private string _macVeyonHelperStatus = OperatingSystem.IsMacOS() ? "尚未检查 Mac Veyon helper。" : "";
    private bool _isPreparingMacVeyonHelper;
    private MacTeacherLoginStartSnapshot _macTeacherLoginStart = new(
        MacTeacherLoginStartState.Unavailable, "尚未读取 Mac 登录启动状态。", IsPackagedApp: false);
    private bool _isChangingMacTeacherLoginStart;
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
    private string _roomCampusName = "本机校区", _roomLocationName = "";
    private string _roomCreateResult = "", _roomCreateError = "", _roomCreateStatus = "", _roomConflictStatus = "", _roomConflictResults = "", _roomSkippedResults = "";
    private string _campusId = DefaultCampusNamePrefix, _roomOutputDir = "", _packageOutput = "", _packageOutputError = "";
    private string _trustMigrationSourcePackageDirectory = "";
    private string _trustMigrationSourcePackageValidationStatus = "尚未选择原学生配置包。";
    private bool _isValidatingTrustMigrationSourcePackage, _trustMigrationSourcePackageValidated;
    private long _trustMigrationSourcePackageValidationGeneration;
    private string _macVeyonKeyImportStatus = "尚未导入 Veyon 教师私钥。";
    private string _macVeyonPrivateKeyImportInstructions = "先选择原有学生配置包，再按提示从原教师端导出匹配的私钥。";
    private bool _isImportingMacVeyonKey;
    private string _campusProfileName = "", _roomProfileName = "", _roomProfilePrefix = "PC-", _roomProfileStart = "1", _roomProfileCount = "150";
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
    private IReadOnlyList<TeacherVeyonNameSyncConflict> _seatVeyonNameSyncConflicts =
        Array.Empty<TeacherVeyonNameSyncConflict>();
    private IReadOnlyList<TeacherSeatAssociationChoice> _seatAssociationSeatChoices =
        Array.Empty<TeacherSeatAssociationChoice>();
    private TeacherSeatAssociationChoice? _selectedSeatAssociationSeat;
    private TeacherVeyonTargetChoice? _selectedSeatAssociationTarget;
    private ClassroomSeatGridLayout? _seatGrid;
    private ClassroomSeatGridLayout? _seatMapEditSnapshot;
    private HashSet<Guid> _seatMapEditOriginalSeatIds = [];
    private readonly Stack<ClassroomSeatGridLayout> _seatMapUndo = new();
    private readonly Stack<ClassroomSeatGridLayout> _seatMapRedo = new();
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
        _isSeatMapEditing, _isSeatAddRemoveMode, _isSeatMapSyncing;
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
    private string _classroomChatSearch = "";
    private string _classroomChatStatus = "";
    private bool _isClassroomChatDrawerOpen;
    private TeacherClassroomChatThread? _selectedClassroomChatThread;
    private Guid? _classroomEventFeedSessionId;
    private readonly Dictionary<Guid, TeacherClassroomEventItem> _classroomHelpRows = [];
    private readonly Dictionary<string, TeacherClassroomChatThread> _classroomChatThreadsByTarget =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _classroomEventIds = [];
    private readonly Queue<Guid> _classroomEventOrder = new();
    private string _publishPackageDirectory = "", _publisherName = "", _teacherPhoneLast4 = "";
    private string _packagePublisherStatus = "", _packagePublisherError = "", _packagePublishResult = "";
    private string _studentConfigurationSyncStatus = "准备好后可同步当前教师连接到已选学生机。";
    private bool _useLocalTestStudentUpdateSource;
    private string? _localTestStudentSetupInstallerPath;
    private string? _localTestMaintenanceInstallerPath;
    private string? _localTestStudentReleaseSummary;
    private string? _localTestMaintenanceReleaseSummary;
    private readonly ObservableCollection<StudentConfigurationSyncTargetProgressItem> _studentConfigurationSyncTargetProgressItems = [];
    private bool _isStudentConfigurationSyncProgressVisible;
    private double _studentConfigurationSyncProgressPercent;
    private string _studentConfigurationSyncProgressText = "";
    private string _websiteTargets = "", _websiteBlockDomains = "", _websiteAllowDomains = "",
        _websiteObservedAddress = "", _websiteEntryStatus = "", _websitePolicyResult = "",
        _websitePolicyResultDetails = "", _websitePolicyError = "", _websitePolicyHistoryText = "";
    private string _websitePolicyDraftStatus = "";
    private string _commonWebsiteBlacklistPresetDomains = "", _commonWebsiteAllowlistPresetDomains = "";
    private string _savedCommonWebsiteBlacklistPresetDomains = "", _savedCommonWebsiteAllowlistPresetDomains = "";
    private string _websitePolicyPresetStatus = "";
    private bool _loadingWebsitePolicyLists;
    private static readonly DateTime DefaultWebsiteDeadlineLocal = DateTime.Now.AddHours(1);
    private DateTimeOffset? _websiteDeadlineDate = new DateTimeOffset(DefaultWebsiteDeadlineLocal.Date);
    private TimeSpan? _websiteDeadlineTime = DefaultWebsiteDeadlineLocal.TimeOfDay;
    private TimeSpan? _quickControlDeadlineTime = CreateDefaultQuickControlDeadlineTime();
    private bool _lastQuickControlDeadlineValid = true;
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
        TeacherRoomSelectionStore? roomSelectionStore = null,
        string? websitePolicyDraftDirectory = null,
        ITeacherMaintenanceWorkflow? maintenanceWorkflow = null)
        : this(installerStore, campusDirectoryStore, updateDiagnostics, classroomSessionStore,
            classroomModeStateStore, operationsTelemetryStore, classroomSeatGridStore, classroomCountdownStore,
            classroomTaskProgressStore, roomSelectionStore, websitePolicyDraftDirectory,
            TeacherLoginStartPreferenceFactory.CreateForCurrentPlatform(), maintenanceWorkflow)
    {
    }

    internal TeacherViewModel(VeyonInstallerStore? installerStore,
        TeacherCampusDirectoryStore? campusDirectoryStore,
        UpdateDiagnosticsStore? updateDiagnostics,
        ClassroomSessionStore? classroomSessionStore,
        ClassroomModeStateStore? classroomModeStateStore,
        CampusOperationsTelemetryStore? operationsTelemetryStore,
        ClassroomSeatGridStore? classroomSeatGridStore,
        ClassroomCountdownStore? classroomCountdownStore,
        ClassroomTaskProgressStore? classroomTaskProgressStore,
        TeacherRoomSelectionStore? roomSelectionStore,
        string? websitePolicyDraftDirectory,
        ITeacherLoginStartPreference teacherLoginStartPreference,
        ITeacherMaintenanceWorkflow? maintenanceWorkflow = null)
    {
        _teacherLoginStartPreference = teacherLoginStartPreference ??
            throw new ArgumentNullException(nameof(teacherLoginStartPreference));
        _websitePolicyDraftDirectory = websitePolicyDraftDirectory;
        _installerStore = installerStore ?? new VeyonInstallerStore();
        _campusDirectoryStore = campusDirectoryStore ?? new TeacherCampusDirectoryStore();
        _networkDirectory = TeacherNetworkDirectoryFactory.CreateForCurrentPlatform(() => CampusProfiles);
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
        if (maintenanceWorkflow is not null)
        {
            _maintenanceWorkflow = maintenanceWorkflow;
        }
        else if (_releaseClient is not null)
        {
            var artifactProvider = new TeacherMaintenanceArtifactProvider(_releaseClient,
                () => _localTestStudentSetupInstallerPath, () => _localTestMaintenanceInstallerPath);
            var coordinator = new TeacherMaintenanceCoordinator(_maintenanceRunStore,
                new LegacyAgentMaintenanceAdapter(artifactProvider));
            _maintenanceWorkflow = new TeacherMaintenanceWorkflow(coordinator, artifactProvider);
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
        try { ReadWebsiteSigningCampuses(selectPreferredCampusWhenUnset: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          CryptographicException)
        {
            ClassroomDeliveryStatus = "无法读取校区设置；学生互动暂不可用。";
        }
        LoadWebsitePolicyLists(CampusId);
        LoadCommonWebsitePolicyPresets();
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
    public event EventHandler? ActiveClassroomSeatGridChanged;

    public string? TakePendingReleaseNotice()
    {
        lock (_releaseNoticeGate)
        {
            var notice = _pendingReleaseNotice;
            _pendingReleaseNotice = null;
            return notice;
        }
    }

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            _isExecuting = value;
            Changed();
            Changed(nameof(CanReadDeviceHealth));
            Changed(nameof(CanSelectWebsitePolicyTargets));
            NotifyDeviceHealthBatchProperties();
            foreach (var alert in _deviceHealthAlerts) alert.SetBusy(value);
            Changed(nameof(CanToggleSeatSwapMode));
            Changed(nameof(CanEditRoomPlan));
            Changed(nameof(CanSaveRoomProfile));
            Changed(nameof(CanEditRoomProfileNumbering));
            Changed(nameof(CanInstallTeacherVeyon));
            Changed(nameof(CanGenerateStudentPackage));
            Changed(nameof(CanChooseTrustMigrationSourcePackage));
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(WebsitePolicyNextStepGuidance));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanReadWebsitePolicyStatus));
            Changed(nameof(CanMigrateEdgeRealtime));
            Changed(nameof(CanRestoreEdgeLegacy));
            Changed(nameof(CanImportCommonWebsiteBlacklist));
            Changed(nameof(CanImportCommonWebsiteAllowlist));
            Changed(nameof(CanImportCommonCombinedWebsitePolicy));
            Changed(nameof(CanSaveWebsitePolicyPresets));
            Changed(nameof(CanAddObservedWebsite));
            Changed(nameof(CanPushApplicationPolicy));
            Changed(nameof(CanDisableApplicationPolicy));
            Changed(nameof(CanPushStudentSystemPolicy));
            Changed(nameof(StudentSystemPolicyPushGuidance));
            Changed(nameof(CanDisableStudentSystemPolicy));
            Changed(nameof(CanReadApplicationPolicyAudit));
            Changed(nameof(CanReadApplicationInventory));
            Changed(nameof(CanAddSelectedApplicationRules));
            Changed(nameof(CanFillFailedWebsiteTargets));
            Changed(nameof(CanReadWebsiteLocations));
            Changed(nameof(CanApplyWebsiteLocation));
            Changed(nameof(CanUseSelectedClassroomLocation));
            Changed(nameof(CanReplaceWebsiteSigningKey));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(CanRetrySeatMapSync));
            Changed(nameof(CanRetryInitialSeatGridImport));
            Changed(nameof(CanPrepareRoomSeatGrid));
            Changed(nameof(CanCheckRoomConflicts));
            Changed(nameof(CanPublishStudentPackage));
            Changed(nameof(CanCheckTeacherUpdate));
            Changed(nameof(CanDownloadTeacherUpdate));
            Changed(nameof(CanExportOfflineTeacherUpdate));
            Changed(nameof(CanVerifyOfflineTeacherUpdate));
            Changed(nameof(CanInstallOfflineTeacherUpdate));
            Changed(nameof(CanTrustStudentAgentIdentities));
            Changed(nameof(CanDeployStudentUpdate));
            Changed(nameof(CanSyncStudentConfiguration));
            Changed(nameof(CanMaintainStudentDevices));
            Changed(nameof(CanOneClickUpdateAllStudentDevices));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanRetryClassroomRestores));
            Changed(nameof(CanReadSeatVeyonTargets));
            Changed(nameof(CanPrepareSeatSwapPlan));
            Changed(nameof(CanSwapSeats));
            Changed(nameof(CanExecuteSeatSwapPlan));
            Changed(nameof(CanEditSeatMap));
            Changed(nameof(CanClearSelectedSeatVeyonTarget));
            Changed(nameof(CanToggleSeatMapEditing));
            Changed(nameof(CanToggleSeatAddRemoveMode));
            Changed(nameof(CanAssignMissingSeatComputerNumbers));
            Changed(nameof(CanStartRoomCreation));
            Changed(nameof(CanSyncSeatGridWithVeyon));
            Changed(nameof(CanApplySeatGridDimensions));
            Changed(nameof(CanAddSeatGridRow));
            Changed(nameof(CanAddSeatGridColumn));
            Changed(nameof(CanOpenVeyonConfigurator));
            Changed(nameof(CanDeleteRoomProfile));
            RefreshSeatMapCells();
        }
    }
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
    public Guid? ActiveClassroomSessionId => _activeClassroomSession?.SessionId;
    public bool HasNoActiveClassroomSession => !HasActiveClassroomSession;
    public bool HasPendingClassroomScopeChange => _activeClassroomSession is { } active &&
        (SelectedRoomProfile is not { } room || SelectedCampusProfile is not { } campus ||
         room.RoomId != active.Room.RoomId || campus.ProfileId != active.Room.CampusProfileId ||
         !IsActiveClassroomTargetSetCurrent);
    public bool CanSwitchClassroomScope => HasPendingClassroomScopeChange &&
        !IsExecuting && !_isClassroomTransitioning && !_isRetryingClassroomRestores &&
        !IsReadingWebsiteLocations && !_classroomDirectorySelectionPending && !HasClassroomDirectoryError &&
        SelectedCampusProfile is not null &&
        (!OperatingSystem.IsWindows() || IsVeyonDeviceDirectoryCurrent) &&
        (OperatingSystem.IsWindows() ? CurrentClassroomDirectoryTargetCount is > 0 :
            SelectedRoomProfile is { ComputerCount: > 0 }) &&
        WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal);
    public string ClassroomOverviewDeviceCount => !IsClassroomDirectoryScopeVerified
        ? "待核对"
        : (_activeClassroomSession?.Targets.Length ??
            (OperatingSystem.IsWindows() ? CurrentClassroomDirectoryTargetCount ?? 0 :
                SelectedRoomProfile?.ComputerCount ?? 0)).ToString(CultureInfo.CurrentCulture);
    public int ActiveClassroomTargetCount => _activeClassroomSession?.Targets.Length ?? 0;
    public string ClassroomOverviewReceiptCount => (_classroomQuickControlSnapshot?.Devices.Count(item =>
        item.Phase is ClassroomQuickControlPhase.WebsiteReported or ClassroomQuickControlPhase.WebsiteApplied or
            ClassroomQuickControlPhase.InputLockedVerified) ?? 0).ToString(CultureInfo.CurrentCulture);
    public string ClassroomOverviewPendingCount => (_classroomQuickControlSnapshot?.Devices.Count(item =>
        item.Phase is ClassroomQuickControlPhase.WebsiteApplying or
            ClassroomQuickControlPhase.Switching or ClassroomQuickControlPhase.InputLocking or
            ClassroomQuickControlPhase.ReleasePending or ClassroomQuickControlPhase.Unknown or
            ClassroomQuickControlPhase.Conflict or ClassroomQuickControlPhase.Failed or
            ClassroomQuickControlPhase.Unsupported) ?? 0).ToString(CultureInfo.CurrentCulture);
    public bool CanStartClassroomQuickControl => TeacherClassroomQuickControlBuildFeatures.PilotEnabled &&
        IsClassroomDirectoryScopeVerified && !HasPendingClassroomScopeChange &&
        (HasActiveClassroomSession || CanToggleClassroomSession) && !_isClassroomQuickControlBusy &&
        HasValidClassroomQuickControlDeadline;
    public bool IsClassroomQuickControlPilotDisabled => HasActiveClassroomSession &&
        !TeacherClassroomQuickControlBuildFeatures.PilotEnabled;
    public bool CanStartClassroomWebsiteControl => CanStartClassroomQuickControl;
    public bool CanStartClassroomInputLock => CanStartClassroomQuickControl &&
        !(_classroomQuickControlSnapshot?.Devices.Any(item =>
            item.Phase == ClassroomQuickControlPhase.Unknown &&
            (item.Detail.Contains("InputGuard", StringComparison.OrdinalIgnoreCase) ||
             item.Detail.Contains("键鼠", StringComparison.OrdinalIgnoreCase))) ?? false);
    private int ActiveClassroomWebsiteControlCount => _classroomQuickControlSnapshot?.Devices.Count(item =>
        item.WebsiteState is "applied" or "reported") ?? 0;
    private int PendingClassroomWebsiteControlCount => _classroomQuickControlSnapshot?.Devices.Count(item =>
        item.WebsiteState is "applying" or "waitingForAgent") ?? 0;
    private int ActiveClassroomInputLockCount => _classroomQuickControlSnapshot?.Devices.Count(item =>
        item.InputState == "locked") ?? 0;
    private bool HasUnverifiedClassroomInputLease => _classroomQuickControlSnapshot?.Devices.Any(item =>
        item.InputLeaseId is not null && item.InputState != "locked") ?? false;
    public bool IsClassroomWebsiteControlActive => ActiveClassroomWebsiteControlCount > 0;
    public string ClassroomWebsiteControlTitle => ActiveClassroomWebsiteControlCount > 0
        ? "网站限制已启用"
        : PendingClassroomWebsiteControlCount > 0 ? "网站限制正在处理"
        : _classroomQuickControlSnapshot?.Devices.Any(item => item.WebsiteState == "needsReview") == true
            ? "网站状态需核对" : "启用网站限制";
    public string ClassroomWebsiteControlSubtitle => ActiveClassroomWebsiteControlCount > 0
        ? $"{ActiveClassroomWebsiteControlCount} 台已写入 · 点击重推"
        : PendingClassroomWebsiteControlCount > 0
            ? "未上线设备会自动推送"
            : "使用已保存的网站名单";
    public bool IsClassroomInputControlActive => ActiveClassroomInputLockCount > 0;
    public bool HasUnverifiedClassroomInputControl => HasUnverifiedClassroomInputLease;
    public string ClassroomInputControlTitle => ActiveClassroomInputLockCount > 0
        ? "键鼠已锁定 · 点击解除"
        : HasUnverifiedClassroomInputLease ? "键鼠待核对 · 点击处理" : "锁定键盘与鼠标";
    public string ClassroomInputControlSubtitle => ActiveClassroomInputLockCount > 0
        ? $"已锁定 {ActiveClassroomInputLockCount} 台 · 点击解除"
        : HasUnverifiedClassroomInputLease ? "重新读取状态后安全解除"
        : "断线后自动释放";
    public bool CanToggleClassroomInputControl => !_isClassroomQuickControlBusy &&
        (HasActiveClassroomSession
            ? IsClassroomInputControlActive || HasUnverifiedClassroomInputLease || CanStartClassroomInputLock
            : CanStartClassroomInputLock);
    public bool CanReleaseClassroomQuickControl => HasActiveClassroomSession && !_isClassroomQuickControlBusy;
    public bool IsClassroomQuickControlBusy => _isClassroomQuickControlBusy;
    public string ClassroomQuickControlActivityText => _isClassroomQuickControlBusy
        ? "正在发送管控命令…"
        : "";
    public TimeSpan? ClassroomQuickControlDeadlineTime
    {
        get => _quickControlDeadlineTime;
        set
        {
            if (_quickControlDeadlineTime == value) return;
            _quickControlDeadlineTime = value;
            NotifyQuickControlDeadlineChanged();
        }
    }
    public bool HasValidClassroomQuickControlDeadline => TryGetClassroomQuickControlDeadlineUtc(
        DateTimeOffset.UtcNow, out _, out _);
    public bool HasScheduledClassroomQuickControlDeadline =>
        _classroomQuickControlSnapshot?.ControlExpiresUtc is not null;
    public bool IsClassroomQuickControlDeadlinePending => HasScheduledClassroomQuickControlDeadline &&
        !HasCurrentClassroomQuickControlDeadline;
    public bool HasCurrentClassroomQuickControlDeadline
    {
        get
        {
            if (_classroomQuickControlSnapshot is not { } snapshot ||
                snapshot.ControlExpiresUtc is not { } deadline || deadline <= DateTimeOffset.UtcNow ||
                snapshot.Devices.Count == 0)
                return false;

            // The persisted deadline keeps advancing while devices reconnect or need review.
            return true;
        }
    }
    public bool CanEditClassroomQuickControlDeadline => !_isClassroomQuickControlBusy &&
        !HasScheduledClassroomQuickControlDeadline;
    public string ClassroomQuickControlDeadlineSummary
    {
        get
        {
            if (HasCurrentClassroomQuickControlDeadline) return "";
            if (_classroomQuickControlSnapshot?.ControlExpiresUtc is { } deadline)
                return deadline <= DateTimeOffset.UtcNow
                    ? "已到截止时间，正在恢复。"
                    : "已恢复原截止时间，设备状态仍待核对。";
            return TryGetClassroomQuickControlDeadlineUtc(DateTimeOffset.UtcNow, out _, out _)
                ? ""
                : "请选择今天内的未来时间。";
        }
    }
    public string CurrentClassroomQuickControlDeadlineText
    {
        get
        {
            if (_classroomQuickControlSnapshot?.ControlExpiresUtc is not { } deadline) return "";
            var localDeadline = deadline.ToLocalTime();
            var dayLabel = localDeadline.Date == DateTime.Today
                ? "今天"
                : localDeadline.ToString("M月d日", CultureInfo.CurrentCulture);
            return $"{dayLabel} {localDeadline.ToString("HH:mm", CultureInfo.CurrentCulture)} 解除";
        }
    }
    public decimal CurrentClassroomQuickControlCountdownHours => _classroomQuickControlCountdownHours;
    public decimal CurrentClassroomQuickControlCountdownMinutes => _classroomQuickControlCountdownMinutes;
    public decimal CurrentClassroomQuickControlCountdownSeconds => _classroomQuickControlCountdownSeconds;
    public string ClassroomSessionActionText => HasActiveClassroomSession ? "课堂模式已开启" :
        SelectedRoomProfile is { ComputerCount: > 0 } ? "课堂已关闭" : "等待教室配置";
    public string ClassroomSessionToggleText => HasActiveClassroomSession ? "结束课堂" : "开始课堂";

    private long GetClassroomQuickControlRemainingSeconds()
    {
        if (_classroomQuickControlSnapshot?.ControlExpiresUtc is not { } deadline) return 0;
        var remaining = (deadline - DateTimeOffset.UtcNow).TotalSeconds;
        return remaining <= 0 ? 0 : (long)Math.Ceiling(remaining);
    }

    internal void RefreshClassroomQuickControlCountdownDisplay()
    {
        var totalSeconds = GetClassroomQuickControlRemainingSeconds();
        var hours = Math.Min(99, (int)(totalSeconds / 3600));
        var minutes = (int)((totalSeconds / 60) % 60);
        var seconds = (int)(totalSeconds % 60);
        if (_classroomQuickControlCountdownHours != hours)
        {
            _classroomQuickControlCountdownHours = hours;
            Changed(nameof(CurrentClassroomQuickControlCountdownHours));
        }
        if (_classroomQuickControlCountdownMinutes != minutes)
        {
            _classroomQuickControlCountdownMinutes = minutes;
            Changed(nameof(CurrentClassroomQuickControlCountdownMinutes));
        }
        if (_classroomQuickControlCountdownSeconds != seconds)
        {
            _classroomQuickControlCountdownSeconds = seconds;
            Changed(nameof(CurrentClassroomQuickControlCountdownSeconds));
        }
    }

    public void SetClassroomQuickControlBusy(bool busy)
    {
        if (_isClassroomQuickControlBusy == busy) return;
        _isClassroomQuickControlBusy = busy;
        Changed(nameof(CanStartClassroomQuickControl));
        Changed(nameof(CanStartClassroomWebsiteControl));
        Changed(nameof(CanStartClassroomInputLock));
        Changed(nameof(CanToggleClassroomInputControl));
        Changed(nameof(IsClassroomQuickControlPilotDisabled));
        Changed(nameof(CanReleaseClassroomQuickControl));
        Changed(nameof(CanEditClassroomQuickControlDeadline));
        Changed(nameof(IsClassroomQuickControlBusy));
        Changed(nameof(ClassroomQuickControlActivityText));
    }

    internal void RefreshClassroomQuickControlDeadlineState()
    {
        var isValid = HasValidClassroomQuickControlDeadline;
        if (_lastQuickControlDeadlineValid == isValid) return;
        _lastQuickControlDeadlineValid = isValid;
        Changed(nameof(HasValidClassroomQuickControlDeadline));
        Changed(nameof(ClassroomQuickControlDeadlineSummary));
        Changed(nameof(CanStartClassroomQuickControl));
        Changed(nameof(CanStartClassroomWebsiteControl));
        Changed(nameof(CanStartClassroomInputLock));
        Changed(nameof(CanToggleClassroomInputControl));
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
                var draft = string.IsNullOrWhiteSpace(CampusId) ? null :
                    WebsitePolicyListDraftStore.Read(CampusId, _websitePolicyDraftDirectory);
                if (draft is null) return "尚未保存网站规则";
                var blocked = draft.BlockedDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
                var allowed = draft.AllowedDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
                var mode = WebsitePolicyModeStore.Read(CampusId, _websitePolicyDraftDirectory) ??
                    WebsitePolicyModeSelection.Infer(draft.BlockedDomains, draft.AllowedDomains);
                return mode switch
                {
                    WebsitePolicyMode.Allowlist => $"只允许名单内网站 · {allowed} 条",
                    WebsitePolicyMode.Combined => $"阻止 {blocked} 条 · 放行例外 {allowed} 条",
                    _ => $"阻止 {blocked} 条 · 其他网站可访问"
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            {
                return "当前网站预设暂不可读取 · " + exception.Message;
            }
        }
    }
    public string ClassroomQuickControlSelectionSummary => _classroomQuickControlSnapshot is not { } snapshot
        ? HasActiveClassroomSession ? "当前选择：尚未启用课堂快捷管控" :
            _classroomQuickControlSessionNotice.Length > 0 ? "课堂状态需核对" : "课堂快捷管控"
        : GetClassroomQuickControlSelectionSummary(snapshot);
    public string ClassroomQuickControlProgressSummary => _classroomQuickControlSnapshot is not { } snapshot
        ? _classroomQuickControlSessionNotice.Length > 0 ? _classroomQuickControlSessionNotice :
            HasActiveClassroomSession ? "尚未启用快捷管控。" : "确认教室范围后可查看逐台进度。"
        : GetClassroomQuickControlProgressSummary(snapshot) +
          (_classroomQuickControlSessionNotice.Length > 0 ? " · " + _classroomQuickControlSessionNotice : "");
    public string ClassroomQuickControlBadgeText => _classroomQuickControlSnapshot is not { } snapshot
        ? HasActiveClassroomSession ? "当前未启用" : _classroomQuickControlSessionNotice.Length > 0 ? "状态需核对" : "等待教室配置"
        : GetClassroomQuickControlBadge(snapshot);
    public IBrush ClassroomQuickControlBadgeBrush => _classroomQuickControlSnapshot is not { } snapshot
        ? QuickControlNeutralBadgeBrush
        : snapshot.Devices.Any(item => item.Phase is ClassroomQuickControlPhase.Conflict or
                ClassroomQuickControlPhase.Failed or ClassroomQuickControlPhase.Unknown or
                ClassroomQuickControlPhase.Unsupported or ClassroomQuickControlPhase.ReleasePending)
            ? QuickControlWarningBadgeBrush
            : snapshot.Devices.Any(item => item.Phase == ClassroomQuickControlPhase.WaitingForAgent)
                ? QuickControlWarningBadgeBrush
                : snapshot.Devices.Any(item => item.Mode != DeviceControlMode.None)
                    ? QuickControlSuccessBadgeBrush
                    : QuickControlNeutralBadgeBrush;

    public void UpdateClassroomQuickControlSnapshot(ClassroomQuickControlSnapshot snapshot)
    {
        if (_activeClassroomSession is { } activeSession && activeSession.SessionId != snapshot.SessionId)
            return;
        if (_classroomQuickControlRevisionSessionId == snapshot.SessionId &&
            snapshot.StateRevision < _classroomQuickControlStateRevision)
            return;
        if (_classroomQuickControlRevisionSessionId != snapshot.SessionId)
        {
            _classroomQuickControlRevisionSessionId = snapshot.SessionId;
            _classroomQuickControlStateRevision = -1;
        }
        _classroomQuickControlStateRevision = Math.Max(_classroomQuickControlStateRevision,
            snapshot.StateRevision);
        if (_classroomQuickControlSnapshot is { } current &&
            current.SessionId == snapshot.SessionId && current.RoomName == snapshot.RoomName &&
            current.TargetCount == snapshot.TargetCount && current.ControlExpiresUtc == snapshot.ControlExpiresUtc &&
            current.Devices.SequenceEqual(snapshot.Devices))
        {
            if (_classroomQuickControlSessionNotice.Length > 0)
            {
                _classroomQuickControlSessionNotice = "";
                Changed(nameof(ClassroomQuickControlProgressSummary));
            }
            return;
        }
        _classroomQuickControlSessionNotice = "";
        _classroomQuickControlSnapshot = snapshot;
        // Keep students in place while progress changes, including while a row is expanded.
        var previousOrder = _classroomQuickControlDevices.Select((item, index) => (item.TargetId, index))
            .ToDictionary(item => item.TargetId, item => item.index);
        var ordered = snapshot.Devices
            .OrderBy(item => previousOrder.GetValueOrDefault(item.TargetId, int.MaxValue)).ToArray();
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
        Changed(nameof(HasScheduledClassroomQuickControlDeadline));
        Changed(nameof(HasCurrentClassroomQuickControlDeadline));
        Changed(nameof(IsClassroomQuickControlDeadlinePending));
        Changed(nameof(CurrentClassroomQuickControlDeadlineText));
        RefreshClassroomQuickControlCountdownDisplay();
        Changed(nameof(ClassroomQuickControlDeadlineSummary));
        Changed(nameof(CanEditClassroomQuickControlDeadline));
        Changed(nameof(ClassroomQuickControlPresetSummary));
        Changed(nameof(IsClassroomWebsiteControlActive));
        Changed(nameof(ClassroomWebsiteControlTitle));
        Changed(nameof(ClassroomWebsiteControlSubtitle));
        Changed(nameof(IsClassroomInputControlActive));
        Changed(nameof(HasUnverifiedClassroomInputControl));
        Changed(nameof(ClassroomInputControlTitle));
        Changed(nameof(ClassroomInputControlSubtitle));
        Changed(nameof(CanToggleClassroomInputControl));
        Changed(nameof(ClassroomQuickControlSelectionSummary));
        Changed(nameof(ClassroomQuickControlProgressSummary));
        Changed(nameof(ClassroomQuickControlBadgeText));
        Changed(nameof(ClassroomQuickControlBadgeBrush));
        Changed(nameof(ClassroomOverviewReceiptCount));
        Changed(nameof(ClassroomOverviewPendingCount));
        Changed(nameof(CanStartClassroomInputLock));
    }

    public void SetClassroomQuickControlSessionNotice(string message, bool clearSnapshot = false)
    {
        if (!clearSnapshot && _classroomQuickControlSnapshot is { } snapshot &&
            snapshot.SessionId == ActiveClassroomSessionId)
        {
            _classroomQuickControlSessionNotice = message ?? "";
            Changed(nameof(ClassroomQuickControlProgressSummary));
            return;
        }
        if (_classroomQuickControlSnapshot is null && _classroomQuickControlSessionNotice == message) return;
        _classroomQuickControlSnapshot = null;
        _classroomQuickControlSessionNotice = message ?? "";
        _classroomQuickControlDevices.Clear();
        Changed(nameof(HasClassroomQuickControlDevices));
        Changed(nameof(HasNoClassroomQuickControlDevices));
        Changed(nameof(ClassroomQuickControlProgressHeader));
        Changed(nameof(ClassroomQuickControlRoomSummary));
        Changed(nameof(HasScheduledClassroomQuickControlDeadline));
        Changed(nameof(HasCurrentClassroomQuickControlDeadline));
        Changed(nameof(IsClassroomQuickControlDeadlinePending));
        Changed(nameof(CurrentClassroomQuickControlDeadlineText));
        RefreshClassroomQuickControlCountdownDisplay();
        Changed(nameof(ClassroomQuickControlDeadlineSummary));
        Changed(nameof(CanEditClassroomQuickControlDeadline));
        Changed(nameof(ClassroomQuickControlSelectionSummary));
        Changed(nameof(ClassroomQuickControlProgressSummary));
        Changed(nameof(ClassroomQuickControlBadgeText));
        Changed(nameof(ClassroomQuickControlBadgeBrush));
        Changed(nameof(IsClassroomWebsiteControlActive));
        Changed(nameof(ClassroomWebsiteControlTitle));
        Changed(nameof(ClassroomWebsiteControlSubtitle));
        Changed(nameof(IsClassroomInputControlActive));
        Changed(nameof(HasUnverifiedClassroomInputControl));
        Changed(nameof(ClassroomInputControlTitle));
        Changed(nameof(ClassroomInputControlSubtitle));
        Changed(nameof(CanToggleClassroomInputControl));
        Changed(nameof(ClassroomOverviewReceiptCount));
        Changed(nameof(ClassroomOverviewPendingCount));
        Changed(nameof(CanStartClassroomInputLock));
    }

    private static string GetClassroomQuickControlSelectionSummary(ClassroomQuickControlSnapshot snapshot)
    {
        var active = snapshot.Devices.Where(item => item.Mode != DeviceControlMode.None)
            .Select(item => item.Mode).Distinct().ToArray();
        var selection = active.Length switch
        {
            0 => "尚未启用快捷管控",
            1 when active[0] == DeviceControlMode.WebsiteRestriction => "网站限制",
            1 when active[0] == DeviceControlMode.InputLocked => "锁定键盘与鼠标",
            1 when active[0] == DeviceControlMode.WebsiteAndInputLocked => "网站限制 + 键鼠锁定",
            _ => "多种管控状态"
        };
        return selection;
    }

    private static string GetClassroomQuickControlProgressSummary(ClassroomQuickControlSnapshot snapshot)
    {
        var retryPending = snapshot.Devices.Count(item => item.Phase == ClassroomQuickControlPhase.WaitingForAgent);
        var devices = snapshot.Devices.Where(item =>
            item.Phase != ClassroomQuickControlPhase.WaitingForAgent).ToArray();
        var receipts = devices.Count(item => item.Phase is ClassroomQuickControlPhase.WebsiteApplied or
            ClassroomQuickControlPhase.WebsiteReported or ClassroomQuickControlPhase.InputLockedVerified);
        var sending = devices.Count(item => item.Phase is ClassroomQuickControlPhase.WebsiteApplying or
            ClassroomQuickControlPhase.Switching or ClassroomQuickControlPhase.InputLocking);
        var review = devices.Count(item => item.Phase is ClassroomQuickControlPhase.Unknown or
            ClassroomQuickControlPhase.Conflict or ClassroomQuickControlPhase.Failed or
            ClassroomQuickControlPhase.Unsupported);
        var restoring = devices.Count(item => item.Phase == ClassroomQuickControlPhase.ReleasePending);
        if (devices.Length == 0 && snapshot.Devices.Count > 0)
            return retryPending > 0 ? $"已保存期望状态 · 待同步 {retryPending} 台" : "离线设备上线后自动推送。";
        if (receipts + sending + review + restoring + retryPending == 0)
            return "尚未启用快捷管控。";
        return $"已回执 {receipts} · 执行中 {sending} · 需核对 {review} · 恢复中 {restoring}" +
               (retryPending > 0 ? $" · 待同步 {retryPending}" : "");
    }

    private static string GetClassroomQuickControlBadge(ClassroomQuickControlSnapshot snapshot)
    {
        if (snapshot.Devices.Any(item => item.Phase is ClassroomQuickControlPhase.Conflict or
                ClassroomQuickControlPhase.Failed or ClassroomQuickControlPhase.Unknown or
                ClassroomQuickControlPhase.Unsupported))
            return "有设备需核对";
        if (snapshot.Devices.Any(item => item.Phase == ClassroomQuickControlPhase.WaitingForAgent))
            return "待设备同步";
        if (snapshot.Devices.Any(item => item.Phase is ClassroomQuickControlPhase.WebsiteApplying or
                ClassroomQuickControlPhase.Switching or ClassroomQuickControlPhase.InputLocking or
                ClassroomQuickControlPhase.ReleasePending))
            return "正在执行";
        if (snapshot.Devices.Any(item => item.Mode != DeviceControlMode.None))
            return "管控已启用";
        return "当前未启用";
    }
    public string ClassroomSessionSummary => !IsClassroomDirectoryScopeVerified
        ? _activeClassroomSession is not null
            ? "课堂范围：Veyon 当前名单待核对；旧会话已保留，课堂操作暂不可用。"
            : SelectedRoomProfile is { } unverifiedRoom
                ? $"课堂范围：{unverifiedRoom.DisplayName} · Veyon 当前名单待核对"
                : "Veyon 当前名单待核对。"
        : _activeClassroomSession is { } active
            ? $"课堂范围：{active.Room.RoomName} · {active.Targets.Length} 台学生电脑"
            : SelectedRoomProfile is { } room
            ? OperatingSystem.IsWindows()
                ? CurrentClassroomDirectoryTargetCount is { } count
                    ? count > 0
                        ? $"课堂范围：{room.DisplayName} · Veyon 当前有 {count} 台学生电脑可用于互动"
                        : $"Veyon 当前地点“{room.DisplayName}”没有学生电脑；请先保存座位图并同步 Veyon。"
                    : $"课堂范围：{room.DisplayName} · 尚未读取 Veyon 设备名单"
                : room.ComputerCount > 0
                    ? $"课堂范围：{room.DisplayName} · {room.ComputerCount} 台学生电脑可用于互动"
                    : "此地点还没有配置电脑；请先在座位表添加座位并添加到 Veyon。"
            : "尚未选择地点。";
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
    public bool IsHealthCenterPage { get => _selectedPage == "updates"; set { if (value) SelectPage("updates"); } }
    public bool IsTeacherAboutPage { get => _selectedPage == "about"; set { if (value) SelectPage("about"); } }
    public ObservableCollection<TeacherDeviceHealthRow> DeviceHealthRows => _deviceHealthRows;
    public IReadOnlyList<TeacherDeviceHealthRow> DeviceHealthPageRows => _deviceHealthPageRows;
    public ObservableCollection<TeacherDeviceHealthAlert> DeviceHealthAlerts => _deviceHealthAlerts;
    public bool HasDeviceHealthAlerts => _deviceHealthAlerts.Count > 0;
    public string DeviceHealthAlertsHeader => _deviceHealthAlerts.Any(alert => !alert.IsCached)
        ? "需要处理"
        : _deviceHealthAlerts.Any(alert => !alert.IsResolved)
            ? "待确认"
        : _deviceHealthAlerts.Any(alert => alert.IsResolved)
            ? "已解决记录"
            : "历史记录";
    public string DeviceHealthAlertsDescription => _deviceHealthAlerts.Any(alert => !alert.IsCached)
        ? "展开问题可查看原因和可执行操作；学生端更新问题可在本页下方处理。"
        : _deviceHealthAlerts.Any(alert => !alert.IsResolved)
            ? "上次的问题还未确认；先刷新设备状态，再决定是否处理。"
        : _deviceHealthAlerts.Any(alert => alert.IsResolved)
            ? "设备问题已恢复；展开可查看处理记录。"
            : "这是上次保存的问题记录；刷新设备状态后再确认。";
    public bool HasDeviceHealthRows => _deviceHealthRows.Count > 0;
    public bool HasDeviceHealthFilteredRows => _deviceHealthFilteredCount > 0;
    public bool HasDeviceHealthFilterEmpty => HasDeviceHealthRows && !HasDeviceHealthFilteredRows;
    public int DeviceHealthAllCount => _deviceHealthRows.Count;
    public int DeviceHealthHealthyCount => _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Healthy);
    public int DeviceHealthAttentionCount => _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Attention);
    public int DeviceHealthManualReviewCount => _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NeedsManualReview);
    public int DeviceHealthNoResponseCount => _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.NoResponse);
    public int DeviceHealthPendingCount => _deviceHealthRows.Count(row => row.Category == TeacherDeviceHealthCategory.Pending);
    public int DeviceHealthNeedsActionCount => DeviceHealthAttentionCount + DeviceHealthManualReviewCount;
    public int DeviceHealthUnconfirmedCount => DeviceHealthNoResponseCount + DeviceHealthPendingCount;
    public string DeviceHealthListSummary => !HasDeviceHealthRows ? "等待监控设备状态" :
        DeviceHealthUnconfirmedCount > 0
            ? $"学生电脑 {DeviceHealthAllCount} 台 · 待确认 {DeviceHealthUnconfirmedCount} 台"
            : $"学生电脑 {DeviceHealthAllCount} 台";
    public IReadOnlyList<string> DeviceHealthCategoryFilters { get; } =
        ["全部设备", "正常", "需处理", "待确认"];
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
    public string DeviceRuntimeSummaryDiagnostics
    {
        get => _deviceRuntimeSummaryDiagnostics;
        private set
        {
            if (_deviceRuntimeSummaryDiagnostics == value) return;
            _deviceRuntimeSummaryDiagnostics = value;
            Changed();
            Changed(nameof(HasDeviceRuntimeSummaryDiagnostics));
        }
    }
    public bool HasDeviceRuntimeSummaryDiagnostics => DeviceRuntimeSummaryDiagnostics.Length > 0;
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
    public int DeviceHealthSelectedCount => GetDeviceHealthTargets().Count;
    public string DeviceHealthScopeSummary => $"当前名单 · {DeviceHealthSelectedCount} 名学生";
    public bool HasDeviceHealthTargets => DeviceHealthSelectedCount > 0;
    public bool HasMultipleDeviceHealthPages => DeviceHealthPageCount > 1;
    private IReadOnlyList<string> GetDeviceHealthTargets()
    {
        if (!AreWebsiteTargetsTrustedForActions()) return Array.Empty<string>();
        var targets = WebsitePolicyTargetChoices.Select(item => item.Target).ToArray();
        return AreTargetsConflictFree(targets) ? targets : Array.Empty<string>();
    }
    public int DeviceHealthRecentlyReachableCount => GetSelectedHealthRows().Count(row => row.IsRecentlyReachable);
    public int DeviceHealthVeyonEligibleCount => GetSelectedHealthRows().Count(row =>
        row.IsRecentlyReachable && row.CanEnsureVeyonServiceRunning);
    public int DeviceHealthEdgeEligibleCount => GetSelectedHealthRows().Count(row =>
        row.IsRecentlyReachable && row.CanRefreshLegacyEdgePolicy);
    public int DeviceHealthVeyonSkippedCount => Math.Max(0, DeviceHealthSelectedCount - DeviceHealthVeyonEligibleCount);
    public int DeviceHealthEdgeSkippedCount => Math.Max(0, DeviceHealthSelectedCount - DeviceHealthEdgeEligibleCount);
    public string DeviceHealthBatchPreview =>
        $"当前名单 {DeviceHealthSelectedCount} 台 · 最近两分钟有签名回执 {DeviceHealthRecentlyReachableCount} 台 · " +
        $"Veyon 可修复 {DeviceHealthVeyonEligibleCount} 台（跳过 {DeviceHealthVeyonSkippedCount} 台） · " +
        $"Edge 可刷新 {DeviceHealthEdgeEligibleCount} 台（跳过 {DeviceHealthEdgeSkippedCount} 台）";
    public bool CanBatchEnsureVeyonServiceRunning => !IsExecuting &&
        !IsReadingDeviceHealth && !IsRunningDeviceHealthBatch && DeviceHealthVeyonEligibleCount > 0 && IsCampusIdValid();
    public bool CanBatchRefreshLegacyEdgePolicy => !IsExecuting &&
        !IsReadingDeviceHealth && !IsRunningDeviceHealthBatch && DeviceHealthEdgeEligibleCount > 0 && IsCampusIdValid();
    public bool CanReadDeviceHealth => !IsExecuting &&
        !IsReadingDeviceHealth && GetDeviceHealthTargets().Count > 0 && IsCampusIdValid();

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
                var numbers = (roomIndex >= 0 ? rooms[roomIndex].GetComputerNumbers() : Array.Empty<int>())
                    .Concat(computers.Select(computer => ParseComputerNumber(RoomPrefix, computer.ComputerName)))
                    .Distinct().OrderBy(number => number).ToList();
                room = new TeacherRoomProfile(roomIndex >= 0 ? rooms[roomIndex].RoomId : Guid.NewGuid(),
                    roomName, RoomPrefix.Trim(), numbers.Min(), numbers.Count,
                    HostOverrides: null, ComputerNumbers: numbers);
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
            Changed(nameof(SeatGridComputerCount));
            Changed(nameof(SeatGridUnboundComputerCount));
            Changed(nameof(SeatGridNumberPendingCount));
            Changed(nameof(SeatGridSummary));
            Changed(nameof(IsTeacherSetupRoomComplete));
            Changed(nameof(IsTeacherSetupRoomSkippedForNow));
            Changed(nameof(TeacherSetupRoomSummary));
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
                seat.VeyonObjectId is null && !string.IsNullOrWhiteSpace(seat.StudentName));
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
            if (layout.Seats.Length == 0 && IsRoomPage)
                TryStartInitialSeatGridImportFromVeyon();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _seatGrid = null;
            SeatMapCells = Array.Empty<TeacherSeatCell>();
            SeatMapStatus = "无法读取座位表；原文件保留，未展示不完整布局。";
            Changed(nameof(HasSeatMap));
            Changed(nameof(SeatGridSeatCount));
            Changed(nameof(SeatGridNamedCount));
            Changed(nameof(SeatGridComputerCount));
            Changed(nameof(SeatGridUnboundComputerCount));
            Changed(nameof(SeatGridNumberPendingCount));
            Changed(nameof(SeatGridSummary));
            Changed(nameof(IsTeacherSetupRoomComplete));
            Changed(nameof(IsTeacherSetupRoomSkippedForNow));
            Changed(nameof(TeacherSetupRoomSummary));
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

    private void ApplySeatGrid(ClassroomSeatGridLayout layout, bool preserveSeatSwapSelection = false,
        bool publishActiveClassroom = true)
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
        Changed(nameof(SeatGridComputerCount));
        Changed(nameof(SeatGridUnboundComputerCount));
        Changed(nameof(SeatGridNumberPendingCount));
        Changed(nameof(SeatGridSummary));
        Changed(nameof(IsTeacherSetupRoomComplete));
        Changed(nameof(IsTeacherSetupRoomSkippedForNow));
        Changed(nameof(TeacherSetupRoomSummary));
        Changed(nameof(CanAssignMissingSeatComputerNumbers));
        Changed(nameof(AssignMissingSeatComputerNumbersLabel));
        Changed(nameof(RoomSummary));
        Changed(nameof(SeatGridTitle));
        Changed(nameof(VeyonRoomBindingStatus));
        Changed(nameof(HasVeyonRoomBindingStatus));
        Changed(nameof(CanAddRoomToVeyon));
        Changed(nameof(CanApplySeatGridDimensions));
        Changed(nameof(SeatDimensionRemovalSummary));
        Changed(nameof(CanAddSeatGridRow));
        Changed(nameof(CanAddSeatGridColumn));
        NotifyTeacherSetupNavigationStateChanged();
        Changed(nameof(CanSwapSeats));
        Changed(nameof(CanPrepareSeatSwapPlan));
        Changed(nameof(CanStartRoomCreation));
        SeatAssociationSeatChoices = BuildSeatAssociationSeatChoices(_seatGrid);
        RebuildWebsitePolicyTargetChoices();
        RefreshSeatMapCells();
        if (publishActiveClassroom && _activeClassroomSession is { } active &&
            active.Room.CampusProfileId == layout.CampusProfileId && active.Room.RoomId == layout.RoomId)
            ActiveClassroomSeatGridChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshSeatMapCells()
    {
        if (_seatGrid is null)
        {
            SeatMapCells = Array.Empty<TeacherSeatCell>();
            Changed(nameof(SeatGridRowHeaders));
            Changed(nameof(SeatGridColumnHeaders));
            Changed(nameof(CanUndoSeatMapEdit));
            Changed(nameof(CanRedoSeatMapEdit));
            return;
        }
        Changed(nameof(SeatGridRowHeaders));
        Changed(nameof(SeatGridColumnHeaders));
        Changed(nameof(CanUndoSeatMapEdit));
        Changed(nameof(CanRedoSeatMapEdit));
        SeatMapCells = ClassroomSeatGridStore.GetCells(_seatGrid).Select(cell =>
        {
            var seat = cell.IsSeat
                ? _seatGrid.Seats.SingleOrDefault(item => item.Row == cell.Row && item.Column == cell.Column)
                : null;
            var computerLabel = cell.SeatNumber is { } number && SelectedRoomProfile is { } room
                ? MachineNaming.CreateName(room.Prefix, number.ToString(CultureInfo.InvariantCulture))
                : cell.VeyonHostAddress ?? "";
            return new TeacherSeatCell(cell.Row, cell.Column, cell.SeatNumber, cell.StudentName ?? "",
                computerLabel, cell.IsSeat,
                cell.VeyonObjectId is not null || !string.IsNullOrWhiteSpace(cell.VeyonHostAddress),
                seat is not null && _seatSwapSelection.Contains(seat.SeatId),
                cell.VeyonObjectId, cell.VeyonHostAddress ?? "",
                CanInteractWithSeatMap && (IsSeatMapEditing || cell.IsSeat),
                IsSeatAddRemoveMode,
                IsSeatMapEditing && seat is not null && !_seatMapEditOriginalSeatIds.Contains(seat.SeatId),
                cell.VeyonComputerName ?? "", seat?.SeatId, IsSeatMapEditing && cell.IsSeat,
                IsSeatMapEditing && cell.IsSeat &&
                    (cell.VeyonObjectId is not null || !string.IsNullOrWhiteSpace(cell.VeyonHostAddress)));
        }).ToArray();
    }

    private static IReadOnlyList<TeacherSeatAssociationChoice> BuildSeatAssociationSeatChoices(
        ClassroomSeatGridLayout? layout)
    {
        if (layout is null) return Array.Empty<TeacherSeatAssociationChoice>();
        return Array.AsReadOnly(layout.Seats
            .OrderBy(seat => seat.Row).ThenBy(seat => seat.Column)
            .Select(seat => new TeacherSeatAssociationChoice(seat.SeatId, seat.Row, seat.Column,
                seat.ComputerNumber, seat.StudentName ?? "未填写姓名", seat.VeyonObjectId))
            .ToArray());
    }

    private IReadOnlyDictionary<string, string> ReadActiveSeatLabels(Guid sessionId)
    {
        if (_activeClassroomSession is not { } active || active.SessionId != sessionId)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var layout = _classroomSeatGridStore.GetLayout(active.Room.CampusProfileId, active.Room.RoomId,
                active.Room.StartNumber);
            var locationsByDeviceId = ClassroomSeatGridStore.GetLocations(layout)
                .Where(location => location.VeyonObjectId is not null)
                .ToDictionary(location => location.VeyonObjectId!.Value);
            return active.Targets
                .Where(target => locationsByDeviceId.ContainsKey(target.TargetId))
                .ToDictionary(target => target.DeviceLabel, target =>
                {
                    var location = locationsByDeviceId[target.TargetId];
                    return $"第 {location.Row} 排 · 第 {location.Column} 列";
                }, StringComparer.OrdinalIgnoreCase);
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

    internal void ReportClassroomScreenPreviewBindingRefreshFailure(string detail) =>
        SeatMapStatus = "座位已保存；旧手机巡视授权暂未能主动清理，后续画面请求仍会核对绑定。" + detail;

    public string CampusProfileName { get => _campusProfileName; set { _campusProfileName = value ?? ""; Changed(); } }
    public string RoomProfileName { get => _roomProfileName; set { _roomProfileName = value ?? ""; Changed(); } }
    public string RoomProfilePrefix { get => _roomProfilePrefix; set { _roomProfilePrefix = value ?? ""; Changed(); } }
    public string RoomProfileStart { get => _roomProfileStart; set { _roomProfileStart = value ?? ""; Changed(); } }
    public string RoomProfileCount { get => _roomProfileCount; set { _roomProfileCount = value ?? ""; Changed(); } }
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
            Changed(nameof(CanFillFailedWebsiteTargets));
            Changed(nameof(CanPublishStudentPackage));
            PackagePublishResult = "";
            PackagePublisherError = "";
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanSwitchClassroomScope));
            Changed(nameof(CanReadWebsitePolicyStatus));
            Changed(nameof(CanReadDeviceHealth));
            NotifyDeviceHealthBatchProperties();
            Changed(nameof(CanMigrateEdgeRealtime));
            Changed(nameof(CanRestoreEdgeLegacy));
            Changed(nameof(CanPushStudentSystemPolicy));
            Changed(nameof(CanDisableStudentSystemPolicy));
            Changed(nameof(CanTrustStudentAgentIdentities));
            Changed(nameof(CanOneClickUpdateAllStudentDevices));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanDeployStudentUpdate));
            Changed(nameof(CanSyncStudentConfiguration));
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
    public string CampusSetupHint => WebsiteSigningCampuses.Contains(CampusId.Trim(), StringComparer.Ordinal)
        ? $"正在使用已保存校区“{CampusId.Trim()}”的密钥。为现有学生电脑生成配置包时请保持名称不变；改名会创建新的校区身份。"
        : "输入新校区名称会创建独立身份；已部署的学生电脑不会自动迁移到新名称。";
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
            if (!_settingWebsiteTargetsFromDirectory)
            {
                _websiteTargetsRequireDirectorySnapshot = false;
                _websiteTargetsLoadedFromPartialRead = false;
                _websiteTargetsDirectoryFingerprint = null;
            }
            RebuildWebsitePolicyTargetChoices();
            Changed(nameof(ClassroomTargetSummary));
            if (_websiteLocationSelectionPending)
            {
                _websiteLocationSelectionPending = false;
                Changed(nameof(CanPushWebsitePolicy));
                Changed(nameof(CanDisableWebsitePolicy));
                Changed(nameof(WebsitePolicyNextStepGuidance));
            }
            Changed();
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(WebsitePolicyNextStepGuidance));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanReadWebsitePolicyStatus));
            Changed(nameof(CanMigrateEdgeRealtime));
            Changed(nameof(CanRestoreEdgeLegacy));
            Changed(nameof(CanPushStudentSystemPolicy));
            Changed(nameof(CanDisableStudentSystemPolicy));
            Changed(nameof(CanTrustStudentAgentIdentities));
            Changed(nameof(CanOneClickUpdateAllStudentDevices));
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
            var previousSelection = _websitePolicyTargetChoices
                .Select(item => NormalizeTargetKey(item.Target))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextSelection = value
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
            ReconcileDeviceHealthRows(CampusId.Trim(), value.Select(item => item.Target).ToArray());
            RefreshDeviceHealthView();
            RefreshRuntimeSummaryCounts();
            Changed();
            Changed(nameof(WebsitePolicyTargetSelectionSummary));
            Changed(nameof(HasWebsitePolicyTargetChoices));
            Changed(nameof(CanSelectWebsitePolicyTargets));
            Changed(nameof(StudentUpdateTargetChoices));
            NotifyStudentUpdateTargetSelectionChanged();
            Changed(nameof(CanFillFailedWebsiteTargets));
            Changed(nameof(WebsitePolicyNextStepGuidance));
            Changed(nameof(ClassroomTargetSummary));
            Changed(nameof(CanReadDeviceHealth));
            NotifyDeviceHealthBatchProperties();
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanReadWebsitePolicyStatus));
            Changed(nameof(CanTrustStudentAgentIdentities));
            Changed(nameof(CanOneClickUpdateAllStudentDevices));
            Changed(nameof(CanMigrateEdgeRealtime));
            Changed(nameof(CanRestoreEdgeLegacy));
            Changed(nameof(WebsitePolicyConfirmationSummary));
            Changed(nameof(WebsitePolicyDisableConfirmationSummary));
        }
    }
    public string WebsitePolicyTargetSelectionSummary => !AreWebsiteTargetsTrustedForActions()
        ? "目录尚未完成可信读取；策略目标暂不可用。请重新读取 Veyon 电脑名单。"
        : WebsitePolicyTargetChoices.Count == 0
            ? "请先从 Veyon 读取电脑名单。"
            : $"已选择 {WebsitePolicyTargetChoices.Count(item => item.IsSelected)}/{WebsitePolicyTargetChoices.Count} 名学生";
    public bool HasWebsitePolicyTargetChoices => WebsitePolicyTargetChoices.Count > 0;
    public bool CanSelectWebsitePolicyTargets => !IsExecuting && AreWebsiteTargetsTrustedForActions() &&
                                                  HasWebsitePolicyTargetChoices;
    public IReadOnlyList<TeacherPolicyTargetChoice> StudentUpdateTargetChoices => WebsitePolicyTargetChoices;
    public int StudentUpdateSelectedTargetCount => WebsitePolicyTargetChoices.Count(item => item.IsSelected);
    public string StudentUpdateTargetSelectionSummary => WebsitePolicyTargetChoices.Count == 0
        ? "请先在“地点与学生名单”配置学生电脑。"
        : $"维护范围与设备概览共用勾选：已选 {StudentUpdateSelectedTargetCount}/{WebsitePolicyTargetChoices.Count} 台学生电脑";
    public IReadOnlyList<string> SelectedWebsitePolicyStudentNames => WebsitePolicyTargetChoices
        .Where(item => item.IsSelected).Select(item => item.StudentName).ToArray();
    public string WebsitePolicyConfirmationSummary => BuildWebsitePolicyTargetSummary(includePolicy: true);
    public string WebsitePolicyDisableConfirmationSummary => BuildWebsitePolicyTargetSummary(includePolicy: false);
    public IReadOnlyList<string> WebsitePolicyConfirmationDomains => BuildWebsitePolicyConfirmationDomains();

    private string BuildWebsitePolicyTargetSummary(bool includePolicy)
    {
        var selected = WebsitePolicyTargetChoices.Where(item => item.IsSelected).ToArray();
        var lines = new List<string> { $"将对已选择的 {selected.Length} 台学生电脑操作。" };
        if (!includePolicy) return string.Join(Environment.NewLine, lines);
        var cutoff = GetWebsiteDeadlineUtc(DateTimeOffset.UtcNow).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        var blockedCount = CountWebsiteDomains(WebsiteBlockDomains);
        var allowedCount = CountWebsiteDomains(WebsiteAllowDomains);
        lines.Add("访问方式：" + GetWebsitePolicyModeLabel(SelectedWebsiteMode));
        lines.Add($"名单数量：阻止 {blockedCount} 个 · 允许 {allowedCount} 个");
        lines.Add("截止时间：" + cutoff);
        return string.Join(Environment.NewLine, lines);
    }

    private IReadOnlyList<string> BuildWebsitePolicyConfirmationDomains()
    {
        var mode = SelectedWebsiteMode;
        var domains = new List<string>();
        if (mode is WebsitePolicyMode.Blocklist or WebsitePolicyMode.Combined)
            domains.AddRange(ReadWebsiteDomainLines(WebsiteBlockDomains).Select(domain => "阻止 · " + domain));
        if (mode is WebsitePolicyMode.Allowlist or WebsitePolicyMode.Combined)
            domains.AddRange(ReadWebsiteDomainLines(WebsiteAllowDomains).Select(domain => "允许 · " + domain));
        return Array.AsReadOnly(domains.ToArray());
    }

    private static int CountWebsiteDomains(string raw) => ReadWebsiteDomainLines(raw).Count;

    private static IReadOnlyList<string> ReadWebsiteDomainLines(string raw) => Array.AsReadOnly(
        raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    private static string GetWebsitePolicyModeLabel(WebsitePolicyMode mode) => mode switch
    {
        WebsitePolicyMode.Blocklist => "阻止指定网站",
        WebsitePolicyMode.Allowlist => "只允许名单中的网站",
        WebsitePolicyMode.Combined => "阻止指定网站并放行例外",
        _ => "解除网站限制"
    };

    public string GetWebsitePolicyStudentName(string target) => WebsitePolicyTargetChoices
        .FirstOrDefault(item => string.Equals(NormalizeTargetKey(item.Target), NormalizeTargetKey(target),
            StringComparison.OrdinalIgnoreCase))?.StudentName ??
        "未填写姓名";
    public string GetWebsitePolicyTargetLabel(string target) => WebsitePolicyTargetChoices
        .FirstOrDefault(item => string.Equals(NormalizeTargetKey(item.Target), NormalizeTargetKey(target),
            StringComparison.OrdinalIgnoreCase))?.StudentName ?? GetWebsitePolicyStudentName(target);

    private bool _isUpdatingWebsitePolicyTargetSelection;

    public void SelectAllWebsitePolicyTargets(bool selected) =>
        SetWebsitePolicyTargetSelection(_ => selected);

    private void SetWebsitePolicyTargetSelection(Func<TeacherPolicyTargetChoice, bool> isSelected)
    {
        var changed = false;
        _isUpdatingWebsitePolicyTargetSelection = true;
        try
        {
            foreach (var choice in WebsitePolicyTargetChoices)
            {
                var selected = isSelected(choice);
                if (choice.IsSelected == selected) continue;
                choice.IsSelected = selected;
                changed = true;
            }
        }
        finally { _isUpdatingWebsitePolicyTargetSelection = false; }
        if (changed) NotifyWebsitePolicyTargetSelectionChanged();
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
                previous.TryGetValue(targetKey, out var selected) && selected);
        }).ToArray();
        WebsitePolicyTargetChoices = Array.AsReadOnly(choices);
    }

    private void PersistWebsitePolicyLists()
    {
        var campusId = CampusId.Trim();
        if (campusId.Length == 0) return;
        try
        {
            WebsitePolicyListDraftStore.Save(campusId, WebsiteBlockDomains, WebsiteAllowDomains,
                SelectedWebsiteMode, _websitePolicyDraftDirectory);
            WebsitePolicyDraftStatus = "已自动保存在教师电脑；手机刷新后可读取这份当前名单。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          System.Text.Json.JsonException or ArgumentException)
        {
            WebsitePolicyDraftStatus = "无法保存本机名单；请检查教师电脑应用数据目录权限。";
            WebsitePolicyError = "网站名单未能保存：" + exception.Message;
        }
    }

    private void PersistWebsitePolicyMode()
    {
        var campusId = CampusId.Trim();
        if (campusId.Length == 0) return;
        try
        {
            // Bump the existing list draft timestamp too, so a phone editing an older snapshot
            // cannot overwrite a teacher's newer mode selection.
            WebsitePolicyListDraftStore.Save(campusId, WebsiteBlockDomains, WebsiteAllowDomains,
                SelectedWebsiteMode, _websitePolicyDraftDirectory);
            WebsitePolicyModeStore.Save(campusId, SelectedWebsiteMode, _websitePolicyDraftDirectory);
            WebsitePolicyDraftStatus = "当前访问方式已保存在教师电脑；手机刷新后会同步读取。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            WebsitePolicyDraftStatus = "限制方式尚未保存；请检查教师电脑应用数据目录。";
            WebsitePolicyError = "限制方式未能保存：" + exception.Message;
        }
    }

    public void ApplyMobileWebsitePolicyLists(string campusId, string blockedDomains, string allowedDomains,
        WebsitePolicyMode selectedMode)
    {
        if (!string.Equals(CampusId.Trim(), campusId, StringComparison.Ordinal)) return;
        var mode = SelectedWebsiteMode;
        try { mode = WebsitePolicyListDraftStore.Read(campusId, _websitePolicyDraftDirectory)?.Mode ?? mode; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException or ArgumentException) { }
        _loadingWebsitePolicyLists = true;
        try
        {
            WebsiteBlockDomains = blockedDomains;
            WebsiteAllowDomains = allowedDomains;
            WebsiteModeIndex = WebsitePolicyModeSelection.ToIndex(selectedMode);
            WebsitePolicyDraftStatus = "手机已保存当前校区名单；可继续在此编辑，修改会自动保存。";
        }
        finally { _loadingWebsitePolicyLists = false; }
    }

    private static int InferWebsiteMode(string blockedDomains, string allowedDomains)
    {
        var hasBlocked = !string.IsNullOrWhiteSpace(blockedDomains);
        var hasAllowed = !string.IsNullOrWhiteSpace(allowedDomains);
        return (hasBlocked, hasAllowed) switch
        {
            (true, true) => 2,
            (false, true) => 1,
            _ => 0
        };
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
            var next = Math.Clamp(value, 0, 2);
            if (_websiteModeIndex == next) return;
            _websiteModeIndex = next;
            Changed();
            Changed(nameof(SelectedWebsiteMode));
            Changed(nameof(IsWebsiteBlocklistMode));
            Changed(nameof(IsWebsiteAllowlistMode));
            Changed(nameof(IsWebsiteCombinedMode));
            Changed(nameof(IsWebsiteBlacklistEditorVisible));
            Changed(nameof(IsWebsiteAllowlistEditorVisible));
            Changed(nameof(WebsiteModeGuidance));
            Changed(nameof(WebsitePolicyNextStepGuidance));
            Changed(nameof(WebsitePolicyActionText));
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(ClassroomQuickControlPresetSummary));
            Changed(nameof(WebsitePolicyConfirmationSummary));
            if (!_loadingWebsitePolicyLists) PersistWebsitePolicyMode();
        }
    }
    public bool IsWebsiteBlocklistMode
    {
        get => WebsiteModeIndex == 0;
        set { if (value) WebsiteModeIndex = 0; }
    }
    public bool IsWebsiteAllowlistMode
    {
        get => WebsiteModeIndex == 1;
        set { if (value) WebsiteModeIndex = 1; }
    }
    public bool IsWebsiteCombinedMode
    {
        get => WebsiteModeIndex == 2;
        set { if (value) WebsiteModeIndex = 2; }
    }
    public bool IsWebsiteBlacklistEditorVisible => SelectedWebsiteMode is WebsitePolicyMode.Blocklist or WebsitePolicyMode.Combined;
    public bool IsWebsiteAllowlistEditorVisible => SelectedWebsiteMode is WebsitePolicyMode.Allowlist or WebsitePolicyMode.Combined;
    public string WebsiteModeGuidance => SelectedWebsiteMode switch
    {
        WebsitePolicyMode.Blocklist => "只阻止下面列出的网站；其他网站仍可访问。",
        WebsitePolicyMode.Allowlist => "只允许下面列出的网站及其子域名；其他网站都会被阻止。",
        WebsitePolicyMode.Combined => "先阻止名单中的网站，再放行允许名单中的例外；名单外网站仍可访问。",
        _ => "添加要阻止或允许的网站后再应用。"
    };
    public string WebsitePolicyActionText => SelectedWebsiteMode switch
    {
        WebsitePolicyMode.Allowlist => "应用白名单限制",
        WebsitePolicyMode.Combined => "应用阻止名单和例外",
        _ => "应用网站限制"
    };
    public string WebsitePolicyNextStepGuidance
    {
        get
        {
            if (IsExecuting)
                return "下一步：等待当前操作完成，再查看下方逐台结果。";
            if (IsReadingWebsiteLocations || _websiteLocationSelectionPending)
                return "下一步：等待校区电脑名单读取完成，再选择要限制的学生电脑。";
            if (WebsitePolicyTargetChoices.Count == 0)
                return "下一步：读取 Veyon 学生电脑名单，再选择要限制的电脑。";
            if (GetSelectedWebsitePolicyTargets().Count == 0)
                return "下一步：至少选择一台学生电脑作为限制目标。";

            var missingListGuidance = SelectedWebsiteMode switch
            {
                WebsitePolicyMode.Blocklist when !HasWebsiteEntries(WebsiteBlockDomains) =>
                    "下一步：填写至少一个要阻止的网站，或导入常见娱乐网站。",
                WebsitePolicyMode.Allowlist when !HasWebsiteEntries(WebsiteAllowDomains) =>
                    "下一步：填写至少一个允许的网站；名单外网站会被阻止。",
                WebsitePolicyMode.Combined when !HasWebsiteEntries(WebsiteBlockDomains) && !HasWebsiteEntries(WebsiteAllowDomains) =>
                    "下一步：填写要阻止的网站，或填写允许访问的例外网站。",
                _ => null
            };
            if (missingListGuidance is not null) return missingListGuidance;

            if (TryGetWebsitePolicyDomainError(out var listName, out var error))
                return $"下一步：修正{listName}中的域名格式：{error}";

            try { _ = GetWebsiteDeadlineUtc(DateTimeOffset.UtcNow); }
            catch (InvalidDataException exception)
            { return $"下一步：修正限制截止时间。{exception.Message}"; }

            return "下一步：核对学生电脑和限制截止时间，再应用当前限制方式。";
        }
    }
    public bool CanImportCommonWebsiteBlacklist => !IsExecuting;
    public bool CanImportCommonWebsiteAllowlist => !IsExecuting;
    public bool CanImportCommonCombinedWebsitePolicy => !IsExecuting;
    public bool CanAddObservedWebsite => !IsExecuting && !string.IsNullOrWhiteSpace(WebsiteObservedAddress);
    public WebsitePolicyMode SelectedWebsiteMode
    {
        get => WebsitePolicyModeSelection.FromIndex(WebsiteModeIndex);
    }

    public void ImportCommonWebsiteBlacklist()
    {
        if (!CanImportCommonWebsiteBlacklist) return;
        try
        {
            var merged = MergeWebsiteEntries(WebsiteBlockDomains,
                _savedCommonWebsiteBlacklistPresetDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
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
            var merged = MergeWebsiteEntries(WebsiteAllowDomains,
                _savedCommonWebsiteAllowlistPresetDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
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
            var blocked = MergeWebsiteEntries(WebsiteBlockDomains,
                _savedCommonWebsiteBlacklistPresetDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var allowed = MergeWebsiteEntries(WebsiteAllowDomains,
                _savedCommonWebsiteAllowlistPresetDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            WebsiteBlockDomains = blocked;
            WebsiteAllowDomains = allowed;
            CompleteWebsiteEditorUpdate("已填入娱乐网站和学习网站例外；核对后再应用。");
        }
        catch (InvalidDataException exception) { WebsitePolicyError = exception.Message; }
    }

    public void AddObservedWebsiteToBlacklist() => AddObservedWebsite(toBlacklist: true);
    public void AddObservedWebsiteToAllowlist() => AddObservedWebsite(toBlacklist: false);

    private static bool HasWebsiteEntries(string raw) =>
        raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => !string.IsNullOrWhiteSpace(entry));

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
    public bool HasWebsiteSigningCampuses => WebsiteSigningCampuses.Count > 0;
    public bool HasMultipleWebsiteSigningCampuses => WebsiteSigningCampuses.Count > 1;
    public bool HasSingleWebsiteSigningCampus => WebsiteSigningCampuses.Count == 1;
    public bool ShowWebsiteSigningCampusSelector => WebsiteSigningCampuses.Count != 1;
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

    private void ReadWebsiteSigningCampuses(bool selectPreferredCampusWhenUnset = false)
    {
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
        Changed(nameof(HasWebsiteSigningCampuses));
        Changed(nameof(HasMultipleWebsiteSigningCampuses));
        Changed(nameof(HasSingleWebsiteSigningCampus));
        Changed(nameof(ShowWebsiteSigningCampusSelector));
        Changed(nameof(CampusSetupHint));
        Changed(nameof(SelectedWebsiteSigningCampus));
        Changed(nameof(CanToggleClassroomSession));
        if (selectPreferredCampusWhenUnset &&
            (string.IsNullOrWhiteSpace(CampusId) ||
             string.Equals(CampusId.Trim(), DefaultCampusNamePrefix, StringComparison.Ordinal)) &&
            !WebsiteSigningCampuses.Contains(CampusId, StringComparer.Ordinal))
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
            Changed(nameof(WebsitePolicyNextStepGuidance));
            Changed(nameof(WebsitePolicyConfirmationSummary));
        }
    }
    public DateTimeOffset GetClassroomQuickControlDeadlineUtc(DateTimeOffset requestedUtc)
    {
        if (!TryGetClassroomQuickControlDeadlineUtc(requestedUtc, out var deadline, out var error))
            throw new InvalidDataException(error);
        return deadline;
    }

    private bool TryGetClassroomQuickControlDeadlineUtc(DateTimeOffset requestedUtc,
        out DateTimeOffset deadlineUtc, out string error)
    {
        deadlineUtc = default;
        if (ClassroomQuickControlDeadlineTime is not { } selectedTime || selectedTime < TimeSpan.Zero ||
            selectedTime >= TimeSpan.FromDays(1))
        {
            error = "请选择今天内有效的管控截止时间。";
            return false;
        }

        var localNow = requestedUtc.ToLocalTime();
        var selectedLocalTime = localNow.Date.Add(selectedTime);
        if (selectedTime.Hours == 23 && selectedTime.Minutes == 59)
            selectedLocalTime = localNow.Date.AddDays(1).AddTicks(-1);
        var localTime = DateTime.SpecifyKind(selectedLocalTime, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localTime))
        {
            error = "所选时间在本机时区无效，请调整截止时间。";
            return false;
        }
        if (localTime.Date != localNow.Date)
        {
            error = "快捷管控截止时间只能设置在今天，不能跨天。";
            return false;
        }
        deadlineUtc = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime)).ToUniversalTime();
        var now = requestedUtc.ToUniversalTime();
        if (deadlineUtc <= now)
        {
            error = "请选择今天剩余时间内的未来时间。";
            return false;
        }
        if (deadlineUtc - now > WebsitePolicyCompiler.MaximumPolicyLifetime)
        {
            error = "快捷管控截止时间不能超过 24 小时。";
            return false;
        }
        error = "";
        return true;
    }

    private void NotifyQuickControlDeadlineChanged()
    {
        _lastQuickControlDeadlineValid = HasValidClassroomQuickControlDeadline;
        Changed(nameof(ClassroomQuickControlDeadlineTime));
        Changed(nameof(HasValidClassroomQuickControlDeadline));
        Changed(nameof(ClassroomQuickControlDeadlineSummary));
        Changed(nameof(CanStartClassroomQuickControl));
        Changed(nameof(CanStartClassroomWebsiteControl));
        Changed(nameof(CanStartClassroomInputLock));
    }

    private static TimeSpan CreateDefaultQuickControlDeadlineTime()
    {
        var now = DateTime.Now;
        var deadline = now.AddHours(1);
        if (deadline.Date != now.Date)
            deadline = now.Date.AddDays(1).AddMinutes(-1);
        return new TimeSpan(deadline.Hour, deadline.Minute, 0);
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
            Changed(nameof(WebsitePolicyNextStepGuidance));
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
            Changed(nameof(CanSwitchClassroomScope));
            Changed(nameof(CanReadWebsiteLocations));
            Changed(nameof(CanApplyWebsiteLocation));
            Changed(nameof(CanUseSelectedClassroomLocation));
            Changed(nameof(CanReadSeatVeyonTargets));
            Changed(nameof(CanSyncSeatGridWithVeyon));
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(WebsitePolicyNextStepGuidance));
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
            Changed(nameof(ShowRoomLocationSettings));
            Changed(nameof(CanApplyWebsiteLocation));
            Changed(nameof(CanUseSelectedClassroomLocation));
            Changed(nameof(CanSyncSeatGridWithVeyon));
            Changed(nameof(ClassroomOverviewDeviceCount));
            Changed(nameof(ClassroomSessionSummary));
            Changed(nameof(ClassroomRoomContextSummary));
            Changed(nameof(CanStartClassroomQuickControl));
            Changed(nameof(CanSendClassroomNotice));
            Changed(nameof(CanSendClassroomChat));
            Changed(nameof(CanStartClassroomCountdown));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanSwitchClassroomScope));
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
            Changed(nameof(ClassroomOverviewDeviceCount));
            Changed(nameof(ClassroomSessionSummary));
            Changed(nameof(ClassroomRoomContextSummary));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanSwitchClassroomScope));
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
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanSwitchClassroomScope));
            Changed(nameof(ClassroomOverviewDeviceCount));
            Changed(nameof(ClassroomSessionSummary));
            Changed(nameof(ClassroomRoomContextSummary));
            Changed(nameof(CanStartClassroomQuickControl));
            Changed(nameof(CanSendClassroomNotice));
            Changed(nameof(CanSendClassroomChat));
            Changed(nameof(CanStartClassroomCountdown));
        }
    }
    public bool HasClassroomDirectoryError => ClassroomDirectoryError.Length > 0;
    public string WebsiteDirectoryStatus { get => _websiteDirectoryStatus; private set { _websiteDirectoryStatus = value; Changed(); Changed(nameof(HasWebsiteDirectoryStatus)); } }
    public bool HasWebsiteDirectoryStatus => WebsiteDirectoryStatus.Length > 0;
    public string WebsiteDirectoryError { get => _websiteDirectoryError; private set { _websiteDirectoryError = value; Changed(); Changed(nameof(HasWebsiteDirectoryError)); } }
    public bool HasWebsiteDirectoryError => WebsiteDirectoryError.Length > 0;
    public string VeyonDeviceDirectorySummary
    {
        get => _veyonDeviceDirectorySummary;
        private set
        {
            if (_veyonDeviceDirectorySummary == value) return;
            _veyonDeviceDirectorySummary = value;
            Changed();
            Changed(nameof(HasVeyonDeviceDirectorySummary));
        }
    }
    public VeyonDeviceDirectorySnapshot? VeyonDeviceDirectorySnapshot
    {
        get => _veyonDeviceDirectorySnapshot;
        private set
        {
            _veyonDeviceDirectorySnapshot = value;
            Changed();
            Changed(nameof(HasVeyonDeviceDirectorySnapshot));
        }
    }
    public bool HasVeyonDeviceDirectorySnapshot => VeyonDeviceDirectorySnapshot is not null;
    public bool IsVeyonDeviceDirectoryCurrent => _veyonDeviceDirectoryReadValid;
    public bool HasVeyonDeviceDirectorySummary => VeyonDeviceDirectorySummary.Length > 0;
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
    public void ReportStudentPackageGenerationError(string message) =>
        PackageOutputError = string.IsNullOrWhiteSpace(message) ? "操作未完成。" : message;
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
    public string RoomCreateError { get => _roomCreateError; private set { _roomCreateError = value; Changed(); Changed(nameof(HasRoomCreateError)); Changed(nameof(CanRetrySeatMapSync)); } }
    public bool HasRoomCreateError => RoomCreateError.Length > 0;

    public void ReportSeatMapSyncFailure(string message)
    {
        RoomCreateResult = "";
        RoomCreateError = string.IsNullOrWhiteSpace(_seatMapIdentityRecoverySummary)
            ? message
            : message + "\n" + _seatMapIdentityRecoverySummary;
        SeatMapStatus = "本机座位资料已保存；Veyon 同步未完成。可查看原因并重试。" +
            (string.IsNullOrWhiteSpace(_seatMapIdentityRecoverySummary)
                ? ""
                : " " + _seatMapIdentityRecoverySummary);
    }

    public void ReportSeatMapSyncSuccess()
    {
        RoomCreateError = "";
        SeatMapStatus = "本机座位资料和 Veyon 目录均已核对；策略、预设与课堂控制名单已自动刷新。" +
            (string.IsNullOrWhiteSpace(_seatMapIdentityRecoverySummary)
                ? ""
                : " " + _seatMapIdentityRecoverySummary);
    }
    public string RoomCreateStatus { get => _roomCreateStatus; private set { _roomCreateStatus = value; Changed(); Changed(nameof(HasRoomCreateStatus)); } }
    public bool HasRoomCreateStatus => RoomCreateStatus.Length > 0;

    public void SelectPage(string page)
    {
        if (page == "health") page = "updates";
        if (page is not ("classroom" or "lesson" or "updates" or "rooms" or "setup" or "about") ||
            _selectedPage == page) return;
        _selectedPage = page;
        Changed(nameof(IsClassroomPage)); Changed(nameof(IsClassroomExperiencePage));
        Changed(nameof(IsClassroomWorkspacePage)); Changed(nameof(IsUpdatesPage)); Changed(nameof(IsHealthCenterPage));
        Changed(nameof(IsTeacherAboutPage));
        Changed(nameof(IsRoomPage)); Changed(nameof(IsSetupPage));
        Changed(nameof(IsTeacherSetupPackageStepVisible));
        Changed(nameof(ClassroomQuickControlPresetSummary));
        if (page is "classroom" or "updates")
            _ = ReadClassroomLocationsAsync(forceSelection: false);
        else if (page == "rooms")
            _ = ReadRoomPageDirectoryAsync();
    }

    private async Task ReadRoomPageDirectoryAsync()
    {
        await ReadWebsiteLocationsAsync();
        TryStartInitialSeatGridImportFromVeyon();
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
        var roomCount = selected.Rooms?.Count ?? 0;
        var message = $"已删除校区档案「{selected.DisplayName}」及其 {roomCount} 个地点；Veyon 中的地点与电脑未更改。";
        message += campuses.Length > 0
            ? $"本机还剩 {campuses.Length} 份校区档案；请重新读取 Veyon 目录后再确认课堂范围。"
            : "本机已无校区档案；下次读取 Veyon 目录时会按首次安装创建一份。";
        if (PersistCampusDirectory(campuses, message))
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
                    HostOverrides = null,
                    ComputerNumbers = fixedNumbers.ToList()
                };
            }
            else
            {
                room = new TeacherRoomProfile(selectedId ?? Guid.NewGuid(), RoomProfileName.Trim(),
                    RoomProfilePrefix, int.Parse(RoomProfileStart, CultureInfo.InvariantCulture), range.Count,
                    HostOverrides: null);
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
        if (_activeClassroomSession?.Room.RoomId == selected.RoomId &&
            !campus.Rooms.Any(room => room.RoomId != selected.RoomId && room.ComputerCount > 0))
        {
            RoomCreateError = "当前范围是唯一已配置地点；请先创建另一个有电脑的地点，再删除此地点。";
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
        CampusDirectoryStatus = "已载入地点设置。电脑数量将按座位表自动生成；尚未修改 Veyon。";
        CampusDirectoryError = "";
    }

    public async Task AddRoomToVeyonAsync(bool keepSeatMapEditing = false)
    {
        if (SeatGridNumberPendingCount > 0)
        {
            // A computer number is only the local label used when this app creates a Veyon
            // computer; Veyon itself identifies devices by Host + UUID. Named seats without a
            // number are therefore numbered automatically here instead of blocking the write.
            var pending = SeatGridNumberPendingCount;
            AssignMissingSeatComputerNumbers();
            if (SeatGridNumberPendingCount > 0)
            {
                RoomCreateError =
                    $"有 {SeatGridNumberPendingCount} 个已填写姓名的座位还没有电脑编号，自动编号也未成功；请在座位图中手动设置编号后再写入。";
                return;
            }
            RoomCreateResult = $"已自动为 {pending} 个待编号座位分配电脑编号；编号只是本机标签，Veyon 以 Host 和 UUID 识别设备。";
        }
        if (!CanStartRoomCreation)
        {
            RoomCreateError = "请先进入座位图编辑状态，为至少一个电脑座位分配编号，再写入地点和电脑。";
            return;
        }
        if (_isClassroomTransitioning)
        {
            RoomConflictStatus = "课堂范围正在同步；请稍后再写入地点和电脑。";
            RoomCreateError = "课堂控制范围正在更新；稍后即可继续写入地点和电脑。";
            return;
        }
        if (_activeClassroomSession is not null)
        {
            RoomCreateError = "课堂正在进行；座位信息已保存在教师电脑，但没有改动 Veyon 目录。请下课后重新添加电脑。";
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
        if (!TryBeginExclusiveTask())
        {
            RoomCreateError = "另一项教师端操作正在运行；座位已保存在本机，但没有开始 Veyon 写入。完成后请重新核对并补齐电脑。";
            return;
        }
        RoomCreateResult = "";
        RoomCreateError = "";
        var veyonWriteVerified = false;
        var seatBindingsVerified = false;
        string? locationNameForRefresh = null;
        try
        {
            var locationName = RoomLocationName.Trim();
            locationNameForRefresh = locationName;
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
            if (response.Result.Ok)
            {
                veyonWriteVerified = true;
                if (!TrySaveCurrentRoomProfile(out var profileError, computers))
                {
                    if (keepSeatMapEditing) BeginSeatMapEdit();
                    RoomCreateError = "Veyon 地点和电脑已写入并读回，但本机地点记录未保存；请重新打开本机权限后再试。" +
                                      profileError;
                    return;
                }
                var bindingError = await TryBindRoomComputersToSeatGridAsync(locationName, computers,
                    response.VeyonAddedComputerTargets);
                RoomCreateResult = $"Veyon 地点和 {computers.Length} 台学生电脑已写入，并完成写入后自动核对。";
                if (bindingError is null)
                {
                    seatBindingsVerified = true;
                    if (keepSeatMapEditing)
                        BeginSeatMapEdit();
                    else
                    {
                        CommitSeatMapEdit();
                        IsSeatMapEditing = false;
                    }
                    RoomCreateResult = $"已完成：{computers.Length} 台电脑已对应编号座位；空姓名座位也已纳入电脑目录。";
                }
                else
                {
                    if (keepSeatMapEditing) BeginSeatMapEdit();
                    else
                    {
                        CommitSeatMapEdit();
                        IsSeatMapEditing = false;
                    }
                    RoomCreateError = $"Veyon 地点和 {computers.Length} 台电脑已成功写入。程序已在写入后自动核对，但未能确认教师机上的座位对应关系；已写入的 Veyon 电脑不受影响。原因：" +
                                      bindingError;
                }
            }
            else
            {
                if (response.Result.Status == ExecutionPlan.NeedsReview)
                {
                    if (keepSeatMapEditing) BeginSeatMapEdit();
                    else
                    {
                        CommitSeatMapEdit();
                        IsSeatMapEditing = false;
                    }
                }
                RoomCreateError = "Veyon 同步未完成：" + response.Result.Detail;
            }
        }
        catch (Exception exception)
        {
            RoomCreateError = "Veyon 同步未完成：" + exception.Message;
        }
        finally
        {
            RoomCreateStatus = "";
            try
            {
                if (veyonWriteVerified && locationNameForRefresh is { } writtenLocationName)
                {
                    var viewsRefreshed = await RefreshDirectoryViewsAfterVeyonWriteAsync(writtenLocationName);
                    if (!viewsRefreshed && RoomCreateError.Length == 0)
                        RoomCreateError = "Veyon 写入已读回确认，但本机策略与课堂设备名单未能刷新；本机座位资料已保存。原因：" +
                                          (ClassroomDirectoryError.Length > 0
                                              ? ClassroomDirectoryError
                                              : WebsiteDirectoryError.Length > 0
                                                  ? WebsiteDirectoryError
                                                  : VeyonDeviceDirectorySummary);
                }
            }
            finally { EndExclusiveTask(); }
            if (seatBindingsVerified && SelectedRoomProfile is { } selectedRoom &&
                string.Equals(selectedRoom.DisplayName, locationNameForRefresh,
                    StringComparison.OrdinalIgnoreCase))
                await ReadSeatVeyonTargetsAsync();
        }
    }

    public Task<bool> RefreshDeviceListsAfterSeatMapSaveAsync() =>
        SelectedRoomProfile is { } room
            ? RefreshDirectoryViewsAfterVeyonWriteAsync(room.DisplayName)
            : Task.FromResult(false);

    private async Task<bool> RefreshDirectoryViewsAfterVeyonWriteAsync(string locationName)
    {
        if (OperatingSystem.IsWindows()) SetVeyonDeviceDirectoryReadValid(false);
        var oldLocations = WebsiteLocations;
        var oldTargets = WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(target => NormalizeTargetKey(target.Trim()))
            .Where(target => target.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedWebsiteLocation = WebsiteLocationIndex >= 0 &&
                                      WebsiteLocationIndex < oldLocations.Count
            ? oldLocations[WebsiteLocationIndex].Name
            : null;
        var selectedClassroomLocation = ClassroomLocationIndex >= 0 &&
                                        ClassroomLocationIndex < oldLocations.Count
            ? oldLocations[ClassroomLocationIndex].Name
            : null;
        var classroomFollowedLocation = string.Equals(selectedClassroomLocation, locationName,
                                          StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(SelectedRoomProfile?.DisplayName, locationName,
                                          StringComparison.OrdinalIgnoreCase);
        var websiteTargetsFollowedLocation = selectedWebsiteLocation is not null &&
                                             selectedWebsiteLocation.Equals(locationName,
                                                 StringComparison.OrdinalIgnoreCase) &&
                                             oldTargets.SetEquals(oldLocations[WebsiteLocationIndex].Targets
                                                 .Select(NormalizeTargetKey));
        var websiteTargetsShouldFollowLocation = websiteTargetsFollowedLocation ||
            string.Equals(selectedWebsiteLocation, locationName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(SelectedRoomProfile?.DisplayName, locationName, StringComparison.OrdinalIgnoreCase);
        WebsiteDirectoryError = "";
        ClassroomDirectoryError = "";
        try
        {
            var locations = await Task.Run(_networkDirectory.ReadLocations);
            await RefreshVeyonDeviceDirectorySummaryAsync(locations);
            if (RejectUnverifiedWindowsDirectoryRoster()) return false;
            WebsiteLocations = locations;
            WebsiteLocationIndex = FindLocationIndex(locations, selectedWebsiteLocation);
            ClassroomLocationIndex = FindLocationIndex(locations, selectedClassroomLocation);

            var matchingLocation = locations.Where(location =>
                string.Equals(location.Name, locationName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (websiteTargetsShouldFollowLocation && matchingLocation.Length == 1)
            {
                WebsiteLocationIndex = Array.IndexOf(locations.ToArray(), matchingLocation[0]);
                FillWebsiteTargetsFromSelectedLocation();
                WebsiteDirectoryStatus = $"“{locationName}”已同步到策略名单；新电脑默认未勾选。";
            }
            else if (websiteTargetsShouldFollowLocation)
            {
                WebsiteLocationIndex = -1;
                WebsiteTargets = "";
                WebsiteDirectoryStatus = "Veyon 当前没有找到所选地点；已清除旧设备名单，避免继续显示过期电脑。";
            }
            else
            {
                WebsiteDirectoryStatus = "Veyon 地点名单已自动更新；保留了手工编辑的策略目标文本。";
            }

            if (classroomFollowedLocation && matchingLocation.Length == 1)
            {
                ClassroomLocationIndex = Array.IndexOf(locations.ToArray(), matchingLocation[0]);
                ApplyClassroomLocation(matchingLocation[0]);
                if (HasClassroomDirectoryError) return false;
                ClassroomDirectoryStatus = $"课堂控制范围已同步“{locationName}”的 {matchingLocation[0].Targets.Count} 台电脑。";
            }
            else if (classroomFollowedLocation)
            {
                ClassroomLocationIndex = -1;
                ClassroomDirectoryStatus = "";
                ClassroomDirectoryError = $"Veyon 当前没有“{locationName}”地点；课堂控制名单已清除，未使用旧本机电脑清单。";
                return false;
            }

            if (OperatingSystem.IsWindows() && !IsVeyonDeviceDirectoryCurrent)
            {
                var partialReadMessage = GetVeyonDirectoryReadNotice();
                WebsiteDirectoryError = partialReadMessage.Length == 0
                    ? "Veyon 地点名单未通过完整身份核对；可查看诊断并重新读取。"
                    : partialReadMessage;
                ClassroomDirectoryError = "Veyon 地点名单未通过完整身份核对；课堂控制暂不可用。";
            }

            // The full notice is already written to WebsiteDirectoryError above; do not render
            // the same paragraph a second time in the status line.
            return !OperatingSystem.IsWindows() || IsVeyonDeviceDirectoryCurrent;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
                                          not StackOverflowException and not AccessViolationException)
        {
            WebsiteLocations = Array.Empty<VeyonNetworkLocation>();
            WebsiteLocationIndex = -1;
            ClassroomLocationIndex = -1;
            WebsiteTargets = "";
            var failure = "无法读取当前 Veyon 设备名单；已清除旧名单，避免显示过期电脑。" + exception.Message;
            WebsiteDirectoryStatus = "";
            WebsiteDirectoryError = failure;
            ClassroomDirectoryStatus = "";
            ClassroomDirectoryError = failure;
            return false;
        }

        static int FindLocationIndex(IReadOnlyList<VeyonNetworkLocation> locations, string? name)
        {
            if (name is null) return -1;
            var matches = locations.Select((location, index) => (location, index))
                .Where(item => string.Equals(item.location.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return matches.Length == 1 ? matches[0].index : -1;
        }
    }

    private async Task<string?> TryBindRoomComputersToSeatGridAsync(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers,
        IReadOnlyList<VeyonAddedComputerTarget>? verifiedAddedTargets)
    {
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room ||
            _seatGrid is null || computers.Count == 0 ||
            !string.Equals(room.DisplayName, locationName, StringComparison.OrdinalIgnoreCase))
            return "本机地点或座位图与刚写入的地点不一致。";

        var campusId = campus.ProfileId;
        var roomId = room.RoomId;
        var plannedSeats = new List<(VeyonNetworkComputer Computer, ClassroomSeatGridSeat Seat, int Number,
            string Host)>(computers.Count);
        foreach (var computer in computers)
        {
            int number;
            string host;
            try
            {
                number = ParseComputerNumber(room.Prefix, computer.ComputerName);
                host = VeyonHostAddress.NormalizeOverride(computer.Host);
            }
            catch (InvalidDataException exception) { return "新增电脑清单无效：" + exception.Message; }
            if (!string.Equals(host, MachineNaming.CreateName(room.Prefix,
                    number.ToString(CultureInfo.InvariantCulture)), StringComparison.OrdinalIgnoreCase))
                return $"新增电脑 {computer.ComputerName} 的 Host 不符合本地固定编号；没有应用座位关联。";
            var seats = _seatGrid.Seats.Where(seat => seat.ComputerNumber == number &&
                                                      seat.VeyonObjectId is null).ToArray();
            if (seats.Length != 1)
                return $"新增电脑 {computer.ComputerName} 没有唯一、未绑定的对应座位；没有应用座位关联。";
            plannedSeats.Add((computer, seats[0], number, host));
        }
        if (plannedSeats.Select(item => item.Number).Distinct().Count() != plannedSeats.Count ||
            plannedSeats.Select(item => item.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plannedSeats.Count)
            return "新增电脑清单中存在重复编号或 Host；没有应用座位关联。";

        IReadOnlyList<VeyonNetworkObject> objects;
        try { objects = await Task.Run(VeyonNetworkObjectDirectory.ReadNetworkObjects); }
        catch (Exception exception)
        {
            return "写入后的自动核对无法读取 Veyon 电脑目录：" + exception.Message;
        }

        if (SelectedCampusProfile?.ProfileId != campusId || SelectedRoomProfile?.RoomId != roomId)
            return "读取期间当前地点已切换。";

        // A verified write already told us the parent location UUID; prefer it over the display
        // name so a second same-named Veyon location cannot capture the seat bindings (R21).
        var verifiedLocationIds = (verifiedAddedTargets ?? [])
            .Select(target => target.ParentObjectId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var locationObject = verifiedLocationIds.Length == 1
            ? VeyonNetworkObjectDirectory.SelectLocationObject(objects, verifiedLocationIds[0], locationName)
            : VeyonNetworkObjectDirectory.SelectLocationObject(objects, null, locationName);
        if (locationObject is null)
            return "Veyon 中找不到唯一对应的地点对象。";

        var bindings = new List<ClassroomSeatGridTargetBinding>(plannedSeats.Count);
        var currentById = objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.ObjectId).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var planned in plannedSeats)
        {
            var hostMatches = objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase))
                .Where(item =>
                {
                    try { return string.Equals(VeyonHostAddress.NormalizeOverride(item.Host), planned.Host,
                        StringComparison.OrdinalIgnoreCase); }
                    catch (InvalidDataException) { return false; }
                }).ToArray();
            if (hostMatches.Length != 1 || hostMatches[0].ParentObjectId != locationObject.ObjectId ||
                hostMatches[0].ObjectId == Guid.Empty)
                return $"新增电脑 {planned.Computer.ComputerName} 写入后未找到唯一的同地点设备；未应用座位关联。";
            var selectedObject = hostMatches[0];
            if (!currentById.TryGetValue(selectedObject.ObjectId, out var readBack) || readBack.Length != 1 ||
                readBack[0].ParentObjectId != locationObject.ObjectId ||
                !string.Equals(readBack[0].Name, planned.Computer.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(VeyonHostAddress.NormalizeOverride(readBack[0].Host), planned.Host,
                    StringComparison.OrdinalIgnoreCase))
                return $"新增电脑 {planned.Computer.ComputerName} 的 UUID、Host 或 Name 读回不一致；未应用座位关联。";

            var returnedAdds = (verifiedAddedTargets ?? []).Where(target => target.ObjectId == selectedObject.ObjectId)
                .ToArray();
            if (returnedAdds.Length > 1 || returnedAdds.Length == 1 &&
                (returnedAdds[0].ParentObjectId != locationObject.ObjectId ||
                 !string.Equals(returnedAdds[0].ComputerName, planned.Computer.DisplayName, StringComparison.Ordinal) ||
                 !string.Equals(VeyonHostAddress.NormalizeOverride(returnedAdds[0].Host), planned.Host,
                     StringComparison.OrdinalIgnoreCase)))
                return $"新增电脑 {planned.Computer.ComputerName} 的提权写入回执与读回目录不一致；未应用座位关联。";

            bindings.Add(new ClassroomSeatGridTargetBinding(planned.Seat.SeatId, planned.Host,
                selectedObject.Name, selectedObject.ObjectId, locationObject.ObjectId, planned.Number));
        }

        if (bindings.Count != plannedSeats.Count)
            return "并非全部座位都通过 UUID 核对；本机座位关联没有更改。";

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

    private const string VeyonReadNoticeMarker = "目录未核对";
    private const string VeyonNoticePointer = "详情见「地点与学生名单」页";
    private const string VeyonDuplicateNoticeMarker = "重复对象";

    /// <summary>
    /// Builds a single, actionable notice for the read-only config-export fallback. The raw
    /// diagnostic.Message already embeds the process detail, so it is intentionally not repeated
    /// here; the structured fields below are the parts a teacher can actually act on.
    /// </summary>
    private static string GetVeyonDirectoryReadNotice()
    {
        if (!OperatingSystem.IsWindows() ||
            VeyonNetworkObjectDirectory.LastReadDiagnostic is not
                { FailureStage: VeyonDirectoryReadDiagnostic.PartialRead } diagnostic)
            return "";
        var detail = (diagnostic.Detail ?? "").Replace('\r', ' ').Replace('\n', ' ');
        var reason = detail.Contains("重复 UUID", StringComparison.Ordinal) ||
                     detail.Contains("重复对象", StringComparison.Ordinal)
            ? "Veyon 目录里有重复对象"
            : "Veyon 目录读取失败";
        return VeyonReadNoticeMarker + "：" + reason + "，已隐藏未验证电脑；请点「重新读取目录」。";
    }

    /// <summary>
    /// Non-blocking notice for byte-identical duplicate objects that Veyon persisted twice in
    /// JsonStoreArray. A de-duplicated read is safe, but the operator still needs to know the
    /// source directory is dirty; the teacher app never deletes or merges Veyon objects.
    /// </summary>
    private static string GetVeyonDuplicateObjectNotice()
    {
        var count = VeyonNetworkObjectDirectory.LastIdenticalDuplicateObjectCount;
        if (!OperatingSystem.IsWindows() || count <= 0) return "";
        return VeyonDuplicateNoticeMarker +
               $"：发现 {count} 条重复对象，已按 UUID 去重；建议导出配置备份后清理。";
    }

    private void AppendVeyonDuplicateObjectNotice(bool classroomPage)
    {
        var notice = GetVeyonDuplicateObjectNotice();
        if (notice.Length == 0) return;
        if (classroomPage)
        {
            if (ClassroomDirectoryStatus.Contains(VeyonDuplicateNoticeMarker, StringComparison.Ordinal) ||
                ClassroomDirectoryError.Contains(VeyonDuplicateNoticeMarker, StringComparison.Ordinal)) return;
            if (ClassroomDirectoryError.Length > 0) ClassroomDirectoryError += "\n" + notice;
            else ClassroomDirectoryStatus = ClassroomDirectoryStatus.Length > 0
                ? ClassroomDirectoryStatus + "\n" + notice : notice;
        }
        else
        {
            if (WebsiteDirectoryStatus.Contains(VeyonDuplicateNoticeMarker, StringComparison.Ordinal) ||
                WebsiteDirectoryError.Contains(VeyonDuplicateNoticeMarker, StringComparison.Ordinal)) return;
            if (WebsiteDirectoryError.Length > 0) WebsiteDirectoryError += "\n" + notice;
            else WebsiteDirectoryStatus = WebsiteDirectoryStatus.Length > 0
                ? WebsiteDirectoryStatus + "\n" + notice : notice;
        }
    }

    private bool RejectUnverifiedWindowsDirectoryRoster()
    {
        if (!OperatingSystem.IsWindows() || IsVeyonDeviceDirectoryCurrent) return false;

        var notice = GetVeyonDirectoryReadNotice();
        var message = notice.Length > 0
            ? notice
            : VeyonReadNoticeMarker + "：已隐藏未验证电脑，请点「重新读取目录」。";
        WebsiteLocations = Array.Empty<VeyonNetworkLocation>();
        WebsiteLocationIndex = -1;
        ClassroomLocationIndex = -1;
        WebsiteTargets = "";
        WebsiteDirectoryStatus = "";
        ClassroomDirectoryStatus = "";
        // Only the location page carries the full diagnostic; the policy page shows a short
        // pointer so the same long paragraph is not rendered three times on one screen.
        WebsiteDirectoryError = "名单待核对；" + VeyonNoticePointer + "。";
        ClassroomDirectoryError = message;
        _websiteLocationSelectionPending = false;
        _classroomDirectorySelectionPending = false;
        Changed(nameof(CanUseSelectedClassroomLocation));
        Changed(nameof(CanApplyWebsiteLocation));
        return true;
    }

    /// <summary>Returns the first Veyon location name that appears more than once.</summary>
    private static string? FindDuplicateVeyonLocationName(IReadOnlyList<VeyonNetworkLocation> locations)
    {
        for (var index = 0; index < locations.Count; index++)
        for (var other = index + 1; other < locations.Count; other++)
            if (string.Equals(locations[index].Name, locations[other].Name, StringComparison.OrdinalIgnoreCase))
                return locations[index].Name;
        return null;
    }

    /// <summary>
    /// Two Veyon locations with the same display name cannot be told apart by the local profile
    /// store, which merges rooms by name. Refuse to auto-apply anything until the operator has
    /// cleaned up the Veyon directory; never silently merge or overwrite a saved seat map.
    /// </summary>
    private bool RejectDuplicateVeyonLocations(IReadOnlyList<VeyonNetworkLocation> locations,
        out string duplicateName)
    {
        duplicateName = "";
        if (OperatingSystem.IsMacOS()) return false;
        var duplicate = FindDuplicateVeyonLocationName(locations);
        if (duplicate is null) return false;
        duplicateName = duplicate;
        WebsiteLocations = Array.Empty<VeyonNetworkLocation>();
        WebsiteLocationIndex = -1;
        ClassroomLocationIndex = -1;
        WebsiteTargets = "";
        WebsiteDirectoryStatus = "";
        ClassroomDirectoryStatus = "";
        WebsiteDirectoryError = "存在同名地点；" + VeyonNoticePointer + "。";
        ClassroomDirectoryError =
            $"Veyon 中存在多个同名地点「{duplicate}」，教师端只按地点名称匹配本机档案，无法安全判断应对应哪一份座位图。" +
            "如果两个地点里的电脑信息完全一样、删掉一个会两个一起消失，通常是同一个 Veyon 对象被重复引用，而不是两份独立数据。" +
            "已停止自动应用，未修改任何本机档案、学生姓名或 Veyon 配置。" +
            "\n请先导出并备份 Veyon 配置，在 Veyon 配置器中核对重复地点对象的 UUID，确认后再清理或重命名；不要在未备份时直接删除。" +
            "\n处理完成后点击「重新读取目录」。";
        _websiteLocationSelectionPending = false;
        _classroomDirectorySelectionPending = true;
        Changed(nameof(CanUseSelectedClassroomLocation));
        Changed(nameof(CanApplyWebsiteLocation));
        Changed(nameof(HasMultipleClassroomLocations));
        return true;
    }

    private async Task RefreshVeyonDeviceDirectorySummaryAsync(
        IReadOnlyList<VeyonNetworkLocation> locations)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetVeyonDeviceDirectoryReadValid(true);
            VeyonDeviceDirectorySummary = "此目录投影由 Windows 教师端维护。";
            return;
        }
        if (VeyonNetworkObjectDirectory.LastReadDiagnostic?.FailureStage ==
            VeyonDirectoryReadDiagnostic.PartialRead)
        {
            SetVeyonDeviceDirectoryReadValid(false);
            VeyonDeviceDirectorySummary = "当前名单来自只读回退；保留上次设备身份缓存，未把回退结果写入可信目录。";
            return;
        }

        try
        {
            var objects = await Task.Run(VeyonNetworkObjectDirectory.ReadNetworkObjects);
            // The CSV projection merges same-named locations, so the duplicate guard cannot see
            // two distinct UUIDs. Check the UUID snapshot before writing any derived state.
            var identityLocations = VeyonNetworkObjectDirectory.BuildLocationsById(objects);
            if (RejectDuplicateVeyonLocations(identityLocations, out _)) return;
            // Capture the fingerprint from this read's payload. The directory helper also exposes
            // a process-wide last-read value; another page can refresh it while this task resumes.
            var fingerprint = VeyonNetworkObjectDirectory.FingerprintObjects(objects);
            var objectLocations = VeyonNetworkObjectDirectory.ParseLocationsFromObjects(objects);
            var seatBindings = new Dictionary<Guid, VeyonDeviceSeatBinding>();
            foreach (var campus in CampusProfiles)
            foreach (var room in campus.Rooms)
            {
                var layout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId,
                    room.StartNumber);
                foreach (var seat in layout.Seats.Where(item => item.VeyonObjectId is not null))
                {
                    var objectId = seat.VeyonObjectId!.Value;
                    var binding = new VeyonDeviceSeatBinding(seat.SeatId, seat.ComputerNumber,
                        seat.StudentName, seat.VeyonLocationObjectId ?? Guid.Empty,
                        seat.VeyonHostAddress ?? "");
                    if (!seatBindings.TryAdd(objectId, binding))
                        throw new InvalidDataException("同一 Veyon UUID 被多个座位绑定；设备目录快照未更新。");
                }
            }
            var snapshot = await Task.Run(() => _veyonDeviceDirectoryStore.Reconcile(objects, fingerprint,
                seatBindings: seatBindings));
            VeyonDeviceDirectorySnapshot = snapshot;
            var current = snapshot.Devices.Where(item => item.State != VeyonDeviceDirectoryState.MissingInVeyon)
                .ToArray();
            var discovered = current.Count(item => item.State == VeyonDeviceDirectoryState.Discovered);
            var changed = current.Count(item => item.State is VeyonDeviceDirectoryState.Renamed or
                VeyonDeviceDirectoryState.HostChanged or VeyonDeviceDirectoryState.Moved);
            var conflicts = current.Count(item => item.State == VeyonDeviceDirectoryState.Conflict);
            var missing = snapshot.Devices.Count(item => item.State == VeyonDeviceDirectoryState.MissingInVeyon);
            var bound = current.Count(item => item.SeatId is not null);
            var mismatch = !string.Equals(VeyonNetworkObjectDirectory.FingerprintLocations(locations),
                VeyonNetworkObjectDirectory.FingerprintLocations(objectLocations), StringComparison.Ordinal);
            SetVeyonDeviceDirectoryReadValid(!mismatch);
            var mismatchNotice = mismatch
                ? "读取期间目录有变化；名单操作暂时锁定，请重新刷新。"
                : "同一地点名单与 UUID 设备目录已核对。";
            VeyonDeviceDirectorySummary =
                $"UUID 设备目录：{current.Length} 台 · 座位绑定 {bound} · 新发现 {discovered} · 名称/Host/地点变化 {changed} · 冲突 {conflicts} · Veyon 缺失 {missing}；{mismatchNotice}";
            if (VeyonNetworkObjectDirectory.LastRegistrySnapshotDetail is { Length: > 0 })
                VeyonDeviceDirectorySummary +=
                    " 当前从 Veyon 注册表快照读取（veyon-cli 无法启动）；新增地点或电脑仍需可用的 veyon-cli。";
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanReadWebsitePolicyStatus));
            Changed(nameof(CanMigrateEdgeRealtime));
            Changed(nameof(CanRestoreEdgeLegacy));
            Changed(nameof(CanPushApplicationPolicy));
            Changed(nameof(CanDisableApplicationPolicy));
            Changed(nameof(CanReadApplicationPolicyAudit));
            Changed(nameof(CanReadApplicationInventory));
            Changed(nameof(CanPushStudentSystemPolicy));
            Changed(nameof(CanDisableStudentSystemPolicy));
            Changed(nameof(CanDeployStudentUpdate));
            Changed(nameof(CanReadDeviceHealth));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException or
                                           TimeoutException or PlatformNotSupportedException or
                                           System.ComponentModel.Win32Exception)
        {
            SetVeyonDeviceDirectoryReadValid(false);
            VeyonDeviceDirectorySummary = "Veyon 地点名单可显示，但 UUID 设备目录未能读回；策略和维护目标已要求重新刷新。" +
                                           exception.Message;
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanReadWebsitePolicyStatus));
            Changed(nameof(CanPushApplicationPolicy));
            Changed(nameof(CanDisableApplicationPolicy));
            Changed(nameof(CanPushStudentSystemPolicy));
            Changed(nameof(CanDisableStudentSystemPolicy));
            Changed(nameof(CanDeployStudentUpdate));
        }
    }

    private void SetVeyonDeviceDirectoryReadValid(bool value)
    {
        if (_veyonDeviceDirectoryReadValid == value) return;
        _veyonDeviceDirectoryReadValid = value;
        Changed(nameof(IsVeyonDeviceDirectoryCurrent));
        Changed(nameof(CanSelectWebsitePolicyTargets));
        Changed(nameof(CanPushWebsitePolicy));
        Changed(nameof(CanDisableWebsitePolicy));
        Changed(nameof(CanPushApplicationPolicy));
        Changed(nameof(CanDisableApplicationPolicy));
        Changed(nameof(CanPushStudentSystemPolicy));
        Changed(nameof(CanDisableStudentSystemPolicy));
        Changed(nameof(CanDeployStudentUpdate));
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanSwitchClassroomScope));
        Changed(nameof(ClassroomOverviewDeviceCount));
        Changed(nameof(ClassroomRoomContextSummary));
        Changed(nameof(ClassroomSessionSummary));
        Changed(nameof(CanStartClassroomQuickControl));
        Changed(nameof(CanStartClassroomWebsiteControl));
        Changed(nameof(CanStartClassroomInputLock));
        Changed(nameof(CanSendClassroomNotice));
        Changed(nameof(CanSendClassroomChat));
        Changed(nameof(CanStartClassroomCountdown));
    }

    public async Task ReadWebsiteLocationsAsync()
    {
        if (IsReadingWebsiteLocations || IsExecuting) return;
        if (OperatingSystem.IsWindows()) SetVeyonDeviceDirectoryReadValid(false);
        IsReadingWebsiteLocations = true;
        WebsiteLocations = Array.Empty<VeyonNetworkLocation>();
        WebsiteLocationIndex = -1;
        WebsiteTargets = "";
        _websiteLocationSelectionPending = false;
        WebsiteDirectoryStatus = OperatingSystem.IsMacOS()
            ? "正在读取教师端保存的地点和电脑清单……"
            : "正在读取 Veyon 当前机房目录……";
        WebsiteDirectoryError = "";
        try
        {
            ReadWebsiteSigningCampuses();
            var locations = await Task.Run(_networkDirectory.ReadLocations);
            await RefreshVeyonDeviceDirectorySummaryAsync(locations);
            if (RejectUnverifiedWindowsDirectoryRoster()) return;
            if (RejectDuplicateVeyonLocations(locations, out _)) return;
            WebsiteLocations = locations;
            if (locations.Count == 0)
            {
                WebsiteDirectoryStatus = "";
                WebsiteDirectoryError = OperatingSystem.IsMacOS()
                    ? "本机地点档案中还没有配置学生电脑；请先在地点与座位页保存地点和电脑地址。"
                    : "未找到 Veyon 机房电脑。请先配置机房，或手动填写电脑名单。";
                return;
            }

            if (locations.Count == 1)
            {
                WebsiteLocationIndex = 0;
                FillWebsiteTargetsFromSelectedLocation();
                WebsiteDirectoryStatus =
                    $"已读取到 1 个机房、{locations[0].Targets.Count} 台电脑；地点与 UUID 目录已核对。";
                return;
            }

            var selectedRoomIndex = Array.FindIndex(locations.ToArray(), location =>
                OperatingSystem.IsMacOS()
                    ? SelectedCampusProfile is { } campus && SelectedRoomProfile is { } selectedRoom &&
                      MacTeacherNetworkDirectory.HasSameIdentity(location, campus, selectedRoom)
                    : string.Equals(location.Name, SelectedRoomProfile?.DisplayName,
                        StringComparison.OrdinalIgnoreCase));
            if (selectedRoomIndex >= 0)
            {
                WebsiteLocationIndex = selectedRoomIndex;
                FillWebsiteTargetsFromSelectedLocation();
                return;
            }

            WebsiteDirectoryStatus = OperatingSystem.IsMacOS()
                ? $"已读取到本机保存的 {locations.Count} 个地点；请选择要接收策略的地点，再填入目标。"
                : $"已读取到 {locations.Count} 个机房；请选择要接收策略的机房，再填入目标。";
            _websiteLocationSelectionPending = true;
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or TimeoutException or PlatformNotSupportedException or
                                          System.ComponentModel.Win32Exception)
        {
            WebsiteTargets = "";
            WebsiteDirectoryStatus = "";
            WebsiteDirectoryError = (OperatingSystem.IsMacOS()
                ? "读取本机地点档案失败："
                : "读取 Veyon 机房目录失败：") + exception.Message;
        }
        finally
        {
            IsReadingWebsiteLocations = false;
            var readNotice = GetVeyonDirectoryReadNotice();
            if (readNotice.Length > 0 &&
                !WebsiteDirectoryStatus.Contains(VeyonReadNoticeMarker, StringComparison.Ordinal) &&
                !WebsiteDirectoryError.Contains(VeyonReadNoticeMarker, StringComparison.Ordinal) &&
                !WebsiteDirectoryError.Contains(VeyonNoticePointer, StringComparison.Ordinal))
            {
                if (WebsiteDirectoryStatus.Length > 0)
                    WebsiteDirectoryStatus += "\n" + readNotice;
                else if (WebsiteDirectoryError.Length > 0)
                    WebsiteDirectoryError += "\n" + readNotice;
            }
            AppendVeyonDuplicateObjectNotice(classroomPage: false);
        }
    }

    public async Task ReadClassroomLocationsAsync(bool forceSelection = false)
    {
        if (IsReadingWebsiteLocations || IsExecuting) return;
        if (OperatingSystem.IsWindows()) SetVeyonDeviceDirectoryReadValid(false);
        IsReadingWebsiteLocations = true;
            ClassroomDirectoryStatus = OperatingSystem.IsMacOS()
                ? "正在读取教师端保存的地点……"
                : "正在读取 Veyon 地点……";
        ClassroomDirectoryError = "";
        _classroomDirectorySelectionPending = false;
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanSwitchClassroomScope));
        Changed(nameof(HasMultipleClassroomLocations));
        Changed(nameof(CanSyncSeatGridWithVeyon));
        Changed(nameof(SeatGridVeyonSyncButtonLabel));
        WebsiteLocations = Array.Empty<VeyonNetworkLocation>();
        WebsiteLocationIndex = -1;
        ClassroomLocationIndex = -1;
        if (OperatingSystem.IsWindows()) WebsiteTargets = "";
        try
        {
            ReadWebsiteSigningCampuses();
            var locations = await Task.Run(_networkDirectory.ReadLocations);
            await RefreshVeyonDeviceDirectorySummaryAsync(locations);
            if (RejectUnverifiedWindowsDirectoryRoster()) return;
            if (RejectDuplicateVeyonLocations(locations, out _)) return;
            WebsiteLocations = locations;
            if (locations.Count == 0)
            {
                ClassroomDirectoryStatus = "";
                ClassroomDirectoryError = OperatingSystem.IsMacOS()
                    ? "本机地点档案中没有已配置的学生电脑。请在“地点与学生名单”保存电脑数量和主机地址。"
                    : "没有找到学生电脑。请先在“地点与学生名单”配置 Veyon 地点。";
                return;
            }

            if (locations.Count == 1)
            {
                ClassroomLocationIndex = 0;
                WebsiteLocationIndex = 0;
                ApplyClassroomLocation(locations[0]);
                if (!HasClassroomDirectoryError) FillWebsiteTargetsFromSelectedLocation();
                WebsiteDirectoryStatus =
                    $"已读取到 1 个机房、{locations[0].Targets.Count} 台电脑；地点与 UUID 目录已核对。";
                return;
            }

            var selectedRoomIndex = forceSelection ? -1 : Array.FindIndex(locations.ToArray(), location =>
                OperatingSystem.IsMacOS()
                    ? SelectedCampusProfile is { } campus && SelectedRoomProfile is { } selectedRoom &&
                      MacTeacherNetworkDirectory.HasSameIdentity(location, campus, selectedRoom)
                    : string.Equals(location.Name, SelectedRoomProfile?.DisplayName,
                        StringComparison.OrdinalIgnoreCase));
            if (selectedRoomIndex >= 0)
            {
                ClassroomLocationIndex = selectedRoomIndex;
                WebsiteLocationIndex = selectedRoomIndex;
                ApplyClassroomLocation(locations[selectedRoomIndex]);
                if (!HasClassroomDirectoryError) FillWebsiteTargetsFromSelectedLocation();
                return;
            }

            if (OperatingSystem.IsWindows()) WebsiteTargets = "";

            _classroomDirectorySelectionPending = true;
            ClassroomDirectoryStatus = OperatingSystem.IsMacOS()
                ? $"本机保存了 {locations.Count} 个地点，暂时无法自动确认课堂范围。请先选择对应地点。"
                : $"Veyon 中有 {locations.Count} 个地点，暂时无法自动确认课堂范围。请先到“地点与学生名单”页选择对应地点。";
            Changed(nameof(ClassroomOverviewDeviceCount));
            Changed(nameof(ClassroomSessionSummary));
            Changed(nameof(ClassroomRoomContextSummary));
            Changed(nameof(CanStartClassroomQuickControl));
            Changed(nameof(CanSendClassroomNotice));
            Changed(nameof(CanSendClassroomChat));
            Changed(nameof(CanStartClassroomCountdown));
            Changed(nameof(HasMultipleClassroomLocations));
            Changed(nameof(CanUseSelectedClassroomLocation));
            Changed(nameof(CanSyncSeatGridWithVeyon));
            Changed(nameof(SeatGridVeyonSyncButtonLabel));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanSwitchClassroomScope));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or TimeoutException or PlatformNotSupportedException or
                                          System.ComponentModel.Win32Exception)
        {
            if (OperatingSystem.IsWindows()) WebsiteTargets = "";
            WebsiteLocations = Array.Empty<VeyonNetworkLocation>();
            WebsiteLocationIndex = -1;
            ClassroomLocationIndex = -1;
            ClassroomDirectoryStatus = "";
            ClassroomDirectoryError = (OperatingSystem.IsMacOS()
                ? "读取本机地点失败；保存的学生电脑名单没有更改。"
                : "读取 Veyon 地点失败；本机学生电脑名单没有更改。") + exception.Message;
        }
        finally
        {
            IsReadingWebsiteLocations = false;
            var readNotice = GetVeyonDirectoryReadNotice();
            if (readNotice.Length > 0 &&
                !ClassroomDirectoryStatus.Contains(VeyonReadNoticeMarker, StringComparison.Ordinal) &&
                !ClassroomDirectoryError.Contains(VeyonReadNoticeMarker, StringComparison.Ordinal))
            {
                if (ClassroomDirectoryStatus.Length > 0)
                    ClassroomDirectoryStatus += "\n" + readNotice;
                else if (ClassroomDirectoryError.Length > 0)
                    ClassroomDirectoryError += "\n" + readNotice;
            }
            AppendVeyonDuplicateObjectNotice(classroomPage: true);
        }
    }

    public void UseSelectedClassroomLocation()
    {
        if (!CanUseSelectedClassroomLocation) return;
        ApplyClassroomLocation(WebsiteLocations[ClassroomLocationIndex]);
    }

    private void ApplyClassroomLocation(VeyonNetworkLocation location)
    {
        if (!OperatingSystem.IsMacOS())
        {
            ApplyClassroomVeyonLocation(location);
            return;
        }

        var matches = CampusProfiles.SelectMany(campus => campus.Rooms.Select(room => (Campus: campus, Room: room)))
            .Where(item => MacTeacherNetworkDirectory.MatchesCurrentRoom(location, item.Campus, item.Room))
            .ToArray();
        if (matches.Length != 1)
        {
            ClassroomDirectoryError = "本机地点身份或电脑地址已变化，无法安全确认课堂范围；课堂范围没有更改。请重新读取地点。";
            return;
        }

        SelectedCampusProfile = matches[0].Campus;
        SelectedRoomProfile = matches[0].Room;
        if (SelectedRoomProfile is null)
        {
            ClassroomDirectoryError = "本机地点档案不完整；课堂范围没有更改。";
            return;
        }
        _classroomDirectorySelectionPending = false;
        ClassroomDirectoryError = "";
        ClassroomDirectoryStatus = $"已使用本机地点“{matches[0].Campus.DisplayName} / {matches[0].Room.DisplayName}”，共 {location.Targets.Count} 台电脑。";
        Changed(nameof(HasMultipleClassroomLocations));
        Changed(nameof(CanUseSelectedClassroomLocation));
        Changed(nameof(CanSyncSeatGridWithVeyon));
        Changed(nameof(SeatGridVeyonSyncButtonLabel));
        Changed(nameof(CanToggleClassroomSession));
        Changed(nameof(CanSwitchClassroomScope));
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

    internal void ApplyClassroomVeyonLocation(VeyonNetworkLocation location)
    {
        try
        {
            var campus = SelectedCampusProfile;
            if (campus is null && CampusProfiles.Count == 1) campus = CampusProfiles[0];
            campus = FindSavedSeatMapCampus(location, campus) ?? campus;
            var roomCampus = string.IsNullOrWhiteSpace(RoomCampusName) || RoomCampusName == "本机校区"
                ? null : RoomCampusName.Trim();
            var configuredCampus = string.IsNullOrWhiteSpace(CampusId) || CampusId == DefaultCampusNamePrefix
                ? null : CampusId.Trim();
            var campusName = campus?.DisplayName ?? roomCampus ??
                (WebsiteSigningCampuses.Count == 1 ? WebsiteSigningCampuses[0] : configuredCampus ?? "本机校区");

            // Reading a Veyon location must never silently create another local campus profile.
            // Only a genuine first install (no profiles at all) may create one, and it is reported.
            if (campus is null && CampusProfiles.Count > 0)
            {
                ClassroomDirectoryStatus = "";
                ClassroomDirectoryError =
                    $"已读到 Veyon 地点「{location.Name}」（{location.Targets.Count} 台电脑），但本机已有 {CampusProfiles.Count} 份校区档案，无法确认它属于哪一份。" +
                    "为避免凭空多出校区，本次只读取、不写入本机档案；Veyon 也未修改。" +
                    "\n请在「地点与学生名单」选择对应校区档案，或先清理不需要的档案，再点击「重新读取目录」。";
                _classroomDirectorySelectionPending = true;
                Changed(nameof(CanUseSelectedClassroomLocation));
                Changed(nameof(CanToggleClassroomSession));
                Changed(nameof(HasMultipleClassroomLocations));
                return;
            }
            // A room that already exists owns its computer numbering: the local seat map is the
            // source of truth and Veyon only supplies the live target set. Re-deriving numbers
            // from host names here rejects an already-verified location as soon as one computer
            // does not follow the PC-nn plan, and a student computer that keeps its factory
            // Windows name (`desktop-fgvqhia`) is the normal case. That made every post-write
            // refresh report a fake read failure even though Veyon had been written and read back
            // successfully.
            var existingRoom = campus?.Rooms.FirstOrDefault(room =>
                string.Equals(room.DisplayName, location.Name, StringComparison.OrdinalIgnoreCase));
            if (existingRoom is not null)
            {
                if (SelectedCampusProfile?.ProfileId != campus?.ProfileId)
                {
                    SelectedCampusProfile = campus;
                    SelectedRoomProfile = existingRoom;
                }
                var boundSeats = _seatGrid?.Seats.Count(seat => seat.VeyonObjectId is not null) ?? 0;
                _classroomDirectorySelectionPending = false;
                _seatMapProfileSelectionRequired = false;
                ClassroomDirectoryError = "";
                ClassroomDirectoryStatus = boundSeats == location.Targets.Count
                    ? $"已核对 Veyon 地点「{location.Name}」：{location.Targets.Count} 台电脑与座位图一致；没有修改本机档案或 Veyon。"
                    : $"已核对 Veyon 地点「{location.Name}」：Veyon {location.Targets.Count} 台，座位图已绑定 {boundSeats} 台；需要时请重新同步。";
                Changed(nameof(HasMultipleClassroomLocations));
                Changed(nameof(CanUseSelectedClassroomLocation));
                Changed(nameof(CanSyncSeatGridWithVeyon));
                Changed(nameof(SeatGridVeyonSyncButtonLabel));
                Changed(nameof(CanToggleClassroomSession));
                Changed(nameof(CanSwitchClassroomScope));
                Changed(nameof(ShowRoomLocationSettings));
                return;
            }

            var firstInstall = campus is null;
            var mergedCampus = TeacherCampusProfileFromVeyon.MergeLocation(campus, campusName, location);
            var selectedRoom = mergedCampus.Rooms.Single(room =>
                string.Equals(room.DisplayName, location.Name, StringComparison.OrdinalIgnoreCase));
            var campuses = CampusProfiles.Where(item => item.ProfileId != mergedCampus.ProfileId).ToList();
            campuses.Add(mergedCampus);
            var successMessage = firstInstall
                ? $"首次读取：已为本机创建校区档案「{campusName}」并导入 Veyon 地点「{location.Name}」的 {location.Targets.Count} 台电脑；没有修改 Veyon。"
                : $"已读取 Veyon 地点「{location.Name}」并更新学生电脑名单，共 {location.Targets.Count} 台电脑；没有修改 Veyon。";
            if (!PersistCampusDirectory(campuses, successMessage))
            {
                ClassroomDirectoryStatus = "";
                ClassroomDirectoryError = CampusDirectoryError;
                return;
            }

            SelectedCampusProfile = CampusProfiles.FirstOrDefault(item => item.ProfileId == mergedCampus.ProfileId);
            SelectedRoomProfile = RoomProfiles.FirstOrDefault(item => item.RoomId == selectedRoom.RoomId);
            _seatMapProfileSelectionRequired = false;
            Changed(nameof(ShowRoomLocationSettings));
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

    private TeacherCampusProfile? FindSavedSeatMapCampus(VeyonNetworkLocation location,
        TeacherCampusProfile? selectedCampus)
    {
        var targets = location.Targets.Select(VeyonHostAddress.NormalizeOverride)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = new List<TeacherCampusProfile>();
        foreach (var campus in CampusProfiles)
        foreach (var room in campus.Rooms)
        {
            if (!string.Equals(room.DisplayName, location.Name, StringComparison.OrdinalIgnoreCase) ||
                !targets.SetEquals(MacTeacherNetworkDirectory.CreateLocation(campus, room).Targets)) continue;
            var layout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId, room.StartNumber);
            if (layout.Seats.Length == 0) continue;
            // Keep a deliberately selected populated map. An empty duplicate must not hide a saved map.
            if (campus.ProfileId == selectedCampus?.ProfileId) return campus;
            matches.Add(campus);
        }
        if (matches.Count <= 1) return matches.SingleOrDefault();
        _seatMapProfileSelectionRequired = true;
        Changed(nameof(ShowRoomLocationSettings));
        throw new InvalidDataException("此地点对应多份已保存的座位图，无法自动确认；请在本机校区档案中选择原档案。");
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
        _settingWebsiteTargetsFromDirectory = true;
        try { WebsiteTargets = string.Join(Environment.NewLine, location.Targets); }
        finally { _settingWebsiteTargetsFromDirectory = false; }
        _websiteTargetsRequireDirectorySnapshot = OperatingSystem.IsWindows();
        _websiteTargetsLoadedFromPartialRead = OperatingSystem.IsWindows() &&
            VeyonNetworkObjectDirectory.LastReadDiagnostic?.FailureStage ==
            VeyonDirectoryReadDiagnostic.PartialRead;
        _websiteTargetsDirectoryFingerprint = _websiteTargetsLoadedFromPartialRead
            ? null
            : VeyonNetworkObjectDirectory.FingerprintLocations(WebsiteLocations);
        Changed(nameof(CanPushWebsitePolicy));
        Changed(nameof(CanDisableWebsitePolicy));
        Changed(nameof(CanReadWebsitePolicyStatus));
        Changed(nameof(CanMigrateEdgeRealtime));
        Changed(nameof(CanRestoreEdgeLegacy));
        Changed(nameof(CanPushStudentSystemPolicy));
        Changed(nameof(CanDisableStudentSystemPolicy));
        Changed(nameof(CanDeployStudentUpdate));
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
                ApplicationPolicyMode.Audit => "影响检查回执",
                ApplicationPolicyMode.Enforce => "应用限制回执",
                _ => succeeded == results.Count ? "解除回执" : "解除结果"
            };
            ApplicationPolicyResult = $"{modeText}{expirySummary} · Agent 签名确认 {succeeded}/{results.Count} · 需核对 {needsReview} · 失败 {failed} · Windows 实际效果待现场核验";
            ApplicationPolicyResultDetails = string.Join(Environment.NewLine, results.Select(result =>
                $"{GetWebsitePolicyTargetLabel(result.Target)}：{(result.Succeeded ? "Agent 签名回执已确认" : result.NeedsReview ? "需核对" : "失败")} — {result.Detail}"));
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
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
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

    public async Task SyncStudentConfigurationAsync(IReadOnlyCollection<string>? targetOverride = null)
    {
        await MaintainStudentDevicesAsync(MaintenanceOperationKind.ConfigurationSync, targetOverride);
    }

    public async Task ReconcilePendingMaintenanceConfigurationAsync(string target)
    {
        var workflow = _maintenanceWorkflow;
        var campusId = CampusId.Trim();
        if (workflow is null || string.IsNullOrWhiteSpace(campusId) || string.IsNullOrWhiteSpace(target)) return;
        var key = campusId + "\n" + target.Trim().ToLowerInvariant();
        if (!_pendingMaintenanceSyncInFlight.TryAdd(key, 0)) return;
        try
        {
            await workflow.SynchronizePendingTeacherConnectivityAsync(campusId, target).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or
                                          InvalidOperationException or ArgumentException or
                                          System.Net.Http.HttpRequestException)
        {
            // The durable Desired entry stays queued; a later authenticated reconnect can retry it.
        }
        finally { _pendingMaintenanceSyncInFlight.TryRemove(key, out _); }
    }

    public async Task RefreshMaintenanceOverviewAsync()
    {
        try
        {
            var campusId = CampusId.Trim();
            if (string.IsNullOrWhiteSpace(campusId))
            {
                PendingMaintenanceIdentityRotations.Clear();
                Changed(nameof(HasPendingMaintenanceIdentityRotations));
                MaintenanceConfigurationItems.Clear();
                Changed(nameof(HasMaintenanceConfigurationItems));
                MaintenanceIssueItems.Clear();
                Changed(nameof(HasMaintenanceIssueItems));
                MaintenanceOverviewSummary = "尚未设置当前校区；没有读取维护记录。";
                Changed(nameof(MaintenanceOverviewSummary));
                MaintenanceConfigurationOverviewSummary = "尚未设置当前校区；没有读取配置对账记录。";
                Changed(nameof(MaintenanceConfigurationOverviewSummary));
                return;
            }
            var journal = await _maintenanceRunStore.ReadAsync();
            var retentionCutoff = DateTimeOffset.UtcNow - MaintenanceJournal.TerminalRunRetention;
            var latestRuns = journal.Runs.Where(run => string.Equals(run.Device.CampusId, campusId,
                    StringComparison.OrdinalIgnoreCase) && (!run.IsTerminal || run.UpdatedUtc >= retentionCutoff))
                .GroupBy(run => run.Device.Key)
                .Select(group => group.OrderByDescending(run => run.UpdatedUtc).First())
                .ToArray();
            var knownMaintenance = latestRuns.Count(run => run.State is not
                    (MaintenanceRunState.ManualRequired or MaintenanceRunState.RecoveryRequired) &&
                run.Capabilities?.Names.Contains("maintenance-service.v1", StringComparer.Ordinal) == true);
            var knownLegacy = latestRuns.Count(run => run.State is not
                    (MaintenanceRunState.ManualRequired or MaintenanceRunState.RecoveryRequired) &&
                run.Capabilities is not null && !run.Capabilities.Names.Contains("maintenance-service.v1",
                    StringComparer.Ordinal));
            var needsField = latestRuns.Count(run => run.State is MaintenanceRunState.ManualRequired or
                MaintenanceRunState.RecoveryRequired);
            var unknown = latestRuns.Length - knownMaintenance - knownLegacy - needsField;
            MaintenanceOverviewSummary = $"本校区本机记录 {latestRuns.Length} 台 · Maintenance {knownMaintenance} · Legacy {knownLegacy} · 需现场维护 {needsField}" +
                (unknown > 0 ? $" · 能力未知/待核对 {unknown}" : "") + "。按每台最近一次维护事务统计。";
            Changed(nameof(MaintenanceOverviewSummary));
            MaintenanceConfigurationOverviewSummary = BuildMaintenanceConfigurationOverview(journal, latestRuns);
            Changed(nameof(MaintenanceConfigurationOverviewSummary));

            MaintenanceIssueItems.Clear();
            var currentTargets = WebsitePolicyTargetChoices.GroupBy(item => NormalizeTargetKey(item.Target),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().StudentName,
                    StringComparer.OrdinalIgnoreCase);
            var agentIdentityTrust = new StudentAgentIdentityTrustStore();
            MaintenanceConfigurationItems.Clear();
            foreach (var run in latestRuns)
            {
                foreach (var module in Enum.GetValues<MaintenanceConfigurationModule>())
                {
                    var status = journal.GetConfigurationStatus(run.Device.PinnedIdentityFingerprint, module);
                    if (status.State == MaintenanceConfigurationReconciliationState.NoDesiredConfiguration)
                        continue;
                    var displayName = currentTargets.GetValueOrDefault(NormalizeTargetKey(run.Device.RouteAddress),
                        run.Device.RouteAddress);
                    var configurationRun = FindRelatedConfigurationRun(journal, run, status);
                    MaintenanceConfigurationItems.Add(new TeacherMaintenanceConfigurationItem(displayName,
                        run.Device.RouteAddress, module, status, configurationRun));
                }
            }
            Changed(nameof(HasMaintenanceConfigurationItems));
            PendingMaintenanceIdentityRotations.Clear();
            foreach (var rotation in _maintenanceIdentityTrustStore.ListPendingRotations(campusId)
                         .OrderByDescending(item => item.RequestedUtc))
            {
                var displayName = currentTargets.GetValueOrDefault(NormalizeTargetKey(rotation.Target),
                    rotation.Target);
                PendingMaintenanceIdentityRotations.Add(new PendingMaintenanceIdentityRotationItem(rotation,
                    displayName));
            }
            Changed(nameof(HasPendingMaintenanceIdentityRotations));
            foreach (var run in latestRuns.Where(run => run.State is not
                         (MaintenanceRunState.Completed or MaintenanceRunState.NoChanges)))
            {
                var target = run.Device.RouteAddress;
                var displayName = currentTargets.GetValueOrDefault(NormalizeTargetKey(target), target);
                var detail = run.Transitions.LastOrDefault()?.Reason ?? "该设备的最近维护事务尚未完成。";
                var versions = run.VerifiedCurrentVersions ?? run.CurrentVersions;
                var identityIsCurrentlyPinned = false;
                try
                {
                    var currentPinnedKey = agentIdentityTrust.FindTrustedPublicKey(campusId, target);
                    identityIsCurrentlyPinned = currentPinnedKey is not null &&
                        string.Equals(StudentAgentResponseCryptography.GetFingerprint(currentPinnedKey),
                            run.Device.PinnedIdentityFingerprint, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  InvalidDataException or System.Security.SecurityException or
                                                  CryptographicException or ArgumentException or
                                                  System.Text.Json.JsonException)
                {
                    // An unreadable trust store must appear as unverified; it must not hide other journal rows.
                }
                MaintenanceIssueItems.Add(new TeacherMaintenanceIssueItem(target, displayName, run.State,
                    detail, run.UpdatedUtc, versions, run.Capabilities is not null, run.IsTerminal,
                    currentTargets.ContainsKey(NormalizeTargetKey(target)), identityIsCurrentlyPinned,
                    run.OperationId, run.AgentCommandId, run.VerifiedUpdateTransactionState,
                    run.VerifiedStatusUtc));
            }
            Changed(nameof(HasMaintenanceIssueItems));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or CryptographicException or ArgumentException or
            System.Security.SecurityException or System.Text.Json.JsonException)
        {
            PendingMaintenanceIdentityRotations.Clear();
            Changed(nameof(HasPendingMaintenanceIdentityRotations));
            MaintenanceConfigurationItems.Clear();
            Changed(nameof(HasMaintenanceConfigurationItems));
            MaintenanceIssueItems.Clear();
            Changed(nameof(HasMaintenanceIssueItems));
            MaintenanceOverviewSummary = "无法读取本机 Maintenance 概览；原维护记录未修改。";
            Changed(nameof(MaintenanceOverviewSummary));
            MaintenanceConfigurationOverviewSummary = "无法读取本机配置对账记录；原维护记录未修改。";
            Changed(nameof(MaintenanceConfigurationOverviewSummary));
        }
    }

    /// <summary>
    /// CM-05: associate every configuration module with the relevant configuration transaction,
    /// not just TeacherConnectivity. A Desired fingerprint match is authoritative; modules
    /// without a per-module fingerprint in MaintenancePlan fall back to the most recent
    /// unfinished configuration transaction for the same fixed device, so the failure stage
    /// is never silently attached to an unrelated completed run.
    /// </summary>
    private static MaintenanceRun? FindRelatedConfigurationRun(MaintenanceJournal journal,
        MaintenanceRun run, MaintenanceConfigurationReconciliationStatus status)
    {
        var candidates = journal.Runs.Where(candidate =>
            candidate.Device.Key == run.Device.Key &&
            candidate.Plan is { SynchronizeConfiguration: true }).ToArray();
        if (candidates.Length == 0) return null;
        if (!string.IsNullOrWhiteSpace(status.DesiredFingerprint))
        {
            var matched = candidates.Where(candidate => candidate.Plan is { } plan &&
                string.Equals(plan.ConfigurationSha256, status.DesiredFingerprint,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matched.Length > 0)
                return matched.OrderByDescending(candidate => candidate.UpdatedUtc).First();
        }
        var latest = candidates.OrderByDescending(candidate => candidate.UpdatedUtc).First();
        return latest.State is MaintenanceRunState.Failed or MaintenanceRunState.RolledBack or
            MaintenanceRunState.RecoveryRequired or MaintenanceRunState.ManualRequired or
            MaintenanceRunState.PendingVerification ? latest : null;
    }

    private static string BuildMaintenanceConfigurationOverview(MaintenanceJournal journal,
        IReadOnlyList<MaintenanceRun> latestRuns)
    {
        var deviceFingerprints = latestRuns.Select(run => run.Device.PinnedIdentityFingerprint)
            .Distinct(StringComparer.Ordinal).ToArray();
        var modules = Enum.GetValues<MaintenanceConfigurationModule>();
        var summaries = new List<string>();
        foreach (var module in modules)
        {
            var statuses = deviceFingerprints.Select(fingerprint => journal.GetConfigurationStatus(fingerprint, module))
                .Where(status => status.State != MaintenanceConfigurationReconciliationState.NoDesiredConfiguration)
                .ToArray();
            if (statuses.Length == 0) continue;
            var queued = statuses.Count(status => status.State == MaintenanceConfigurationReconciliationState.QueuedForDelivery);
            var waiting = statuses.Count(status => status.State == MaintenanceConfigurationReconciliationState.AwaitingReport);
            var pending = statuses.Count(status => status.State == MaintenanceConfigurationReconciliationState.PendingApplication);
            var rejected = statuses.Count(status => status.State == MaintenanceConfigurationReconciliationState.Rejected);
            var drifted = statuses.Count(status => status.State == MaintenanceConfigurationReconciliationState.Drifted);
            var inSync = statuses.Count(status => status.State == MaintenanceConfigurationReconciliationState.InSync);
            var label = module switch
            {
                MaintenanceConfigurationModule.CampusIdentity => "校区身份",
                MaintenanceConfigurationModule.TeacherConnectivity => "教师连接",
                MaintenanceConfigurationModule.PolicyTrust => "策略信任",
                MaintenanceConfigurationModule.SystemConfiguration => "系统配置",
                MaintenanceConfigurationModule.RuntimeSettings => "运行参数",
                _ => module.ToString()
            };
            summaries.Add($"{label}：排队 {queued}、待回报 {waiting}、应用中 {pending}、拒绝 {rejected}、漂移 {drifted}、同步 {inSync}");
        }
        return summaries.Count == 0
            ? "本机当前校区记录中尚无 Desired/Reported 配置对账状态。"
            : "配置 Desired/Reported 对账（" + string.Join("；", summaries) + "）。统计仅覆盖本机 journal 中有事务记录的固定身份设备，不代表全校实时状态。";
    }

    public string PolicyTrustRotationStatus { get; private set; } = "点「查看草稿状态」读取本机策略根轮换草稿。";

    public void RefreshPolicyTrustRotationStatus()
    {
        try
        {
            PolicyTrustRotationStatus = OperatingSystem.IsWindows()
                ? PolicyTrustRotationDraftStore.Inspect(CampusId.Trim()) ??
                  "当前没有策略根轮换草稿；生成配置包不会包含轮换授权。"
                : "策略根轮换草稿保存在 Windows 当前用户证书库，当前平台不支持。";
        }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException or
            IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            PolicyTrustRotationStatus = "轮换草稿无法读取：" + exception.Message + "；未修改任何信任根。";
        }
        Changed(nameof(PolicyTrustRotationStatus));
    }

    public void CreatePolicyTrustRotationDraft()
    {
        if (!OperatingSystem.IsWindows())
        {
            PolicyTrustRotationStatus = "策略根轮换草稿仅支持 Windows 当前用户证书库。";
            Changed(nameof(PolicyTrustRotationStatus));
            return;
        }
        try
        {
            var draft = PolicyTrustRotationDraftStore.Create(CampusId.Trim(),
                Enum.GetValues<PolicyTrustKeyRole>(), DateTimeOffset.UtcNow.AddDays(30));
            PolicyTrustRotationStatus =
                $"已创建 {draft.Entries.Count} 项轮换草稿；旧根保持活动，退休时间 {draft.RetirePreviousRootUtc.ToLocalTime():yyyy-MM-dd HH:mm}。" +
                "请先生成含授权的配置包并分发，全部设备确认新根后再激活候选根。";
        }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException or
            IOException or UnauthorizedAccessException or InvalidOperationException or
            PlatformNotSupportedException or ArgumentException)
        {
            PolicyTrustRotationStatus = "创建轮换草稿失败：" + exception.Message + "；没有更改当前信任根。";
        }
        Changed(nameof(PolicyTrustRotationStatus));
    }

    public void ActivatePolicyTrustRotationDraft()
    {
        if (!OperatingSystem.IsWindows())
        {
            PolicyTrustRotationStatus = "策略根轮换草稿仅支持 Windows 当前用户证书库。";
            Changed(nameof(PolicyTrustRotationStatus));
            return;
        }
        try
        {
            var draft = PolicyTrustRotationDraftStore.Activate(CampusId.Trim());
            PolicyTrustRotationStatus =
                $"已固定 {draft.Entries.Count} 个候选根并清理草稿；旧根仍保持活动，请确认全部设备后再按退休时间退休。";
        }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException or
            IOException or UnauthorizedAccessException or InvalidOperationException or
            PlatformNotSupportedException or ArgumentException)
        {
            PolicyTrustRotationStatus = "激活失败：" + exception.Message;
        }
        Changed(nameof(PolicyTrustRotationStatus));
    }

    public void ClearPolicyTrustRotationDraft()
    {
        if (!OperatingSystem.IsWindows())
        {
            PolicyTrustRotationStatus = "策略根轮换草稿仅支持 Windows 当前用户证书库。";
            Changed(nameof(PolicyTrustRotationStatus));
            return;
        }
        try
        {
            PolicyTrustRotationDraftStore.Clear(CampusId.Trim());
            PolicyTrustRotationStatus = "已清理轮换草稿；当前固定信任根未更改。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            PolicyTrustRotationStatus = "清理草稿失败：" + exception.Message;
        }
        Changed(nameof(PolicyTrustRotationStatus));
    }

    public async Task ApproveMaintenanceIdentityRotationAsync(PendingMaintenanceIdentityRotationItem item,
        MaintenanceServiceKeyRotationMode rotationMode = MaintenanceServiceKeyRotationMode.ImmediateRevocation)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.CanAct) return;
        item.SetBusy(true);
        try
        {
            _ = _maintenanceIdentityTrustStore.ApproveKeyRotation(item.Request,
                item.PreviousFingerprint, item.CandidateFingerprint, rotationMode: rotationMode);
            MaintenanceIdentityRotationStatus = rotationMode == MaintenanceServiceKeyRotationMode.TemporaryRollbackTrust
                ? "已批准常规密钥轮换；新旧服务身份并行信任 24 小时，之后旧身份会自动退役。"
                : "已批准紧急密钥轮换；旧服务身份已立即撤销，服务重新连接后会继续身份核验。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or ArgumentException)
        {
            MaintenanceIdentityRotationStatus = "Maintenance 密钥轮换未批准：" + exception.Message;
        }
        finally
        {
            item.SetBusy(false);
            await RefreshMaintenanceOverviewAsync();
        }
        Changed(nameof(MaintenanceIdentityRotationStatus));
        Changed(nameof(HasMaintenanceIdentityRotationStatus));
    }

    public async Task RejectMaintenanceIdentityRotationAsync(PendingMaintenanceIdentityRotationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.CanAct) return;
        item.SetBusy(true);
        try
        {
            _ = _maintenanceIdentityTrustStore.RejectKeyRotation(item.Request);
            MaintenanceIdentityRotationStatus = "已拒绝该 Maintenance 服务密钥轮换请求；原固定密钥保持不变。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MaintenanceIdentityRotationStatus = "无法清除 Maintenance 密钥轮换请求：" + exception.Message;
        }
        finally
        {
            item.SetBusy(false);
            await RefreshMaintenanceOverviewAsync();
        }
        Changed(nameof(MaintenanceIdentityRotationStatus));
        Changed(nameof(HasMaintenanceIdentityRotationStatus));
    }

    public async Task RetryMaintenanceIssueAsync(TeacherMaintenanceIssueItem issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (!issue.CanAct) return;
        issue.SetBusy(true);
        try
        {
            await MaintainStudentDevicesAsync(null, [issue.Target]);
        }
        finally { issue.SetBusy(false); }
    }

    public async Task MaintainStudentDevicesAsync()
    {
        await MaintainStudentDevicesAsync(null, null);
    }

    /// <summary>教师端一键：对当前地点全部学生电脑自动核对身份并执行静默维护/更新。</summary>
    public async Task OneClickUpdateAllStudentDevicesAsync()
    {
        if (!CanOneClickUpdateAllStudentDevices)
        {
            StudentMaintenanceStatus = "暂无可更新的学生电脑，或当前操作尚未结束。";
            return;
        }
        StudentMaintenanceStatus = "正在自动核对全部学生电脑身份……";
        SelectAllWebsitePolicyTargets(true);
        await AutoApproveMaintenanceKeyRotationsAsync();
        await MaintainStudentDevicesAsync();
    }

    /// <summary>Maintenance 服务密钥变化也由一键流程批准，避免教师再去找隐藏的审批入口。</summary>
    private async Task AutoApproveMaintenanceKeyRotationsAsync()
    {
        try
        {
            await RefreshMaintenanceOverviewAsync();
            foreach (var rotation in PendingMaintenanceIdentityRotations.Where(item => item.CanAct).ToArray())
                await ApproveMaintenanceIdentityRotationAsync(rotation,
                    MaintenanceServiceKeyRotationMode.TemporaryRollbackTrust);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        {
            StudentMaintenanceStatus = "自动批准 Maintenance 密钥变化未完成：" + exception.Message;
        }
    }

    /// <summary>
    /// 教师无法人工比对指纹：首次身份由核心层的首次信任策略自动固定，
    /// 这里对教师明确选中的设备额外自动批准密钥变化，并给出可见结果。
    /// </summary>
    private async Task<(int Confirmed, int Rotated, int Unreachable)> AutoTrustMaintenanceTargetsAsync(
        string campus, IReadOnlyList<string> targets)
    {
        if (targets.Count == 0) return (0, 0, 0);
        try
        {
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var discoveries = await WebsitePolicyStatusTransport.DiscoverIdentitiesAsync(
                targets, campus, signingKey.PrivateKey).ConfigureAwait(false);
            var confirmed = 0;
            var rotated = 0;
            foreach (var item in discoveries)
            {
                if (item.Candidate is null) continue;
                confirmed++;
                if (item.MatchesPinnedKey) continue;
                if (StudentAgentIdentityAutoTrust.TryApproveRotation(campus, item.Target,
                        item.Candidate.PublicKeyPem, item.Candidate.Fingerprint, out var changed) && changed)
                    rotated++;
            }
            return (confirmed, rotated, discoveries.Count - confirmed);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or InvalidOperationException or CryptographicException or
            HttpRequestException or SocketException or PlatformNotSupportedException or ArgumentException)
        {
            return (0, 0, targets.Count);
        }
    }

    private async Task MaintainStudentDevicesAsync(MaintenanceOperationKind? requestedOperation,
        IReadOnlyCollection<string>? targetOverride)
    {
        var canStart = requestedOperation switch
        {
            MaintenanceOperationKind.SoftwareUpdate => CanDeployStudentUpdate,
            MaintenanceOperationKind.ConfigurationSync => CanSyncStudentConfiguration,
            MaintenanceOperationKind.MaintenanceServiceUpdate => CanUpdateMaintenanceService,
            _ => targetOverride is { Count: > 0 }
                ? CanMaintainTargetOverride(targetOverride)
                : CanMaintainStudentDevices
        };
        if (!canStart || !TryBeginExclusiveTask()) return;
        var performanceStartedUtc = DateTimeOffset.UtcNow;
        var performanceStopwatch = Stopwatch.StartNew();
        _lastStudentUpdatePerformanceReport = null;
        StudentUpdatePerformanceReportExportStatus = "";
        Changed(nameof(HasStudentUpdatePerformanceReport));
        Changed(nameof(StudentUpdatePerformanceReportExportStatus));
        IReadOnlyList<string> selectedTargets = Array.Empty<string>();
        var workflow = _maintenanceWorkflow;
        StudentMaintenanceStatus = "正在读取已固定身份、签名版本和未完成事务……";
        SetStudentUpdateProgress(true, true, 0, "正在为所选学生电脑建立维护操作记录……");
        try
        {
            selectedTargets = WebsitePolicyTransport.NormalizeTargets(targetOverride ??
                GetSelectedStudentUpdateTargets());
            ClearStudentUpdateTargetRows();
            var displayNames = await ResolveTargetDisplayNamesAsync(selectedTargets);
            await PrepareStudentUpdateTargetRowsAsync(selectedTargets, displayNames);
            var trust = await AutoTrustMaintenanceTargetsAsync(CampusId.Trim(), selectedTargets);
            StudentMaintenanceStatus = $"身份已自动核对 {trust.Confirmed}/{selectedTargets.Count} 台" +
                (trust.Rotated > 0 ? $" · 密钥变化已批准 {trust.Rotated} 台" : "") +
                (trust.Unreachable > 0 ? $" · 未响应 {trust.Unreachable} 台" : "");
            StudentAgentIdentityStatus = StudentMaintenanceStatus;
            Changed(nameof(StudentAgentIdentityStatus));
            if (requestedOperation == MaintenanceOperationKind.ConfigurationSync)
            {
                _studentConfigurationSyncTargetProgressItems.Clear();
                foreach (var target in selectedTargets)
                {
                    var displayName = GetTargetDisplayName(target, displayNames);
                    _studentConfigurationSyncTargetProgressItems.Add(
                        new StudentConfigurationSyncTargetProgressItem(target, displayName));
                }
                Changed(nameof(HasStudentConfigurationSyncTargetProgressItems));
                StudentConfigurationSyncStatus = $"正在核验并同步 {selectedTargets.Count} 台学生电脑的教师连接配置……";
                SetStudentConfigurationSyncProgress(true, 0, "正在读取签名身份、配置指纹和更新事务。");
            }
            if (workflow is null)
            {
                var detail = _releaseClientError ?? "学生维护服务当前不可用。";
                StudentMaintenanceStatus = "维护未启动：" + detail;
                StudentUpdateStatus = "没有发送学生端命令：" + detail;
                foreach (var target in selectedTargets)
                {
                    var row = FindStudentUpdateTargetRow(target);
                    row?.SetResult(false, true, detail);
                }
                if (requestedOperation == MaintenanceOperationKind.ConfigurationSync)
                {
                    StudentConfigurationSyncStatus = "没有发送配置：" + detail;
                    foreach (var row in _studentConfigurationSyncTargetProgressItems)
                        row.SetMaintenanceResult(MaintenanceRunState.ManualRequired, detail);
                }
                return;
            }

            var channel = _useLocalTestStudentUpdateSource
                ? MaintenanceReleaseChannel.LocalTest
                : MaintenanceReleaseChannel.Stable;
            StudentUpdateStatus = requestedOperation == MaintenanceOperationKind.ConfigurationSync
                ? "正在通过统一维护事务核验并同步教师连接配置……"
                : requestedOperation == MaintenanceOperationKind.SoftwareUpdate
                    ? channel == MaintenanceReleaseChannel.LocalTest
                        ? "正在验签所选本机 StudentSetup 测试包……"
                        : "正在查询并验签 StudentSetup stable 发布目标……"
                    : channel == MaintenanceReleaseChannel.LocalTest
                        ? "正在验签所选本机 StudentSetup 测试包……"
                        : "正在查询并验签 StudentSetup stable 发布目标……";
            var progress = new Progress<MaintenanceCoordinatorProgress>(value =>
            {
                var row = FindStudentUpdateTargetRow(value.Target ?? string.Empty);
                row?.SetMaintenanceOperation(value.OperationId);
                row?.SetTransportProgress(value.Detail, value.CommandId);
                row?.SetEnvironment(value.Environment);
                if (value.PhaseTimings is { Count: > 0 } phaseTimings)
                    row?.SetPhaseTimings(phaseTimings);
                if (requestedOperation == MaintenanceOperationKind.ConfigurationSync)
                {
                    var configurationRow = _studentConfigurationSyncTargetProgressItems.FirstOrDefault(item =>
                        string.Equals(NormalizeTargetKey(item.Target), NormalizeTargetKey(value.Target ?? ""),
                            StringComparison.OrdinalIgnoreCase));
                    configurationRow?.SetMaintenanceProgress(value.Detail);
                    var finished = _studentConfigurationSyncTargetProgressItems.Count(item => item.IsCompleted);
                    var percent = selectedTargets.Count == 0 ? 0 : finished * 100d / selectedTargets.Count;
                    SetStudentConfigurationSyncProgress(true, percent,
                        $"已核对 {finished}/{selectedTargets.Count} 台学生电脑。");
                }
                StudentMaintenanceStatus = $"维护事务 {value.Completed}/{value.Total} · {value.Detail}";
                StudentUpdateStatus = value.Detail;
            });
            var results = await workflow.MaintainAsync(CampusId.Trim(), selectedTargets, channel, progress,
                requestedOperation: requestedOperation);
            foreach (var outcome in results)
            {
                var row = FindStudentUpdateTargetRow(outcome.Target);
                if (row is null) continue;
                var detail = SanitizeStudentUpdateDetail(outcome.Summary, outcome.Target, row.DisplayName);
                if (outcome.OperationId is { } operationId)
                    row.SetTransportProgress("维护事务 " + operationId.ToString("D"),
                        outcome.Result?.AgentCommandId);
                switch (outcome.State)
                {
                    case MaintenanceRunState.Completed:
                        row.SetResult(true, false, detail);
                        break;
                    case MaintenanceRunState.RolledBack:
                        row.SetResult(false, true, detail, rolledBack: true);
                        break;
                    case MaintenanceRunState.PendingVerification:
                    case MaintenanceRunState.UpdateOffered:
                    case MaintenanceRunState.Updating:
                    case MaintenanceRunState.WaitingForCommit:
                    case MaintenanceRunState.ConfigurationSyncing:
                        row.SetPendingVerification(detail);
                        break;
                    case MaintenanceRunState.RecoveryRequired:
                        row.SetRecoveryRequired(detail);
                        break;
                    case MaintenanceRunState.ManualRequired:
                        row.SetManualRequired(detail);
                        break;
                    case MaintenanceRunState.Failed:
                        row.SetResult(false, false, detail);
                        break;
                    default:
                        row.SetCheckingVersions(detail);
                        break;
                }
                row.SetMaintenanceFacts(outcome.Result?.VerifiedCurrentVersions ?? outcome.CurrentVersions,
                    outcome.TargetVersions,
                    outcome.Result?.VerifiedUpdateTransactionState ?? outcome.UpdateTransactionState,
                    outcome.AuthenticatedChannelConnected,
                    outcome.Result?.VerifiedStatusUtc ?? outcome.Capabilities?.VerifiedUtc,
                    outcome.Capabilities?.VerifiedUtc, outcome.AuthenticatedChannelCheckedUtc,
                    outcome.Result?.VerifiedStatusUtc is not null,
                    outcome.OperationId, outcome.Result?.AgentCommandId,
                    outcome.OperationKind, outcome.Channel, outcome.CurrentVersions is not null ||
                    outcome.TargetVersions is not null || outcome.Capabilities is not null ||
                    outcome.UpdateTransactionState is not null || outcome.OperationId is not null,
                    outcome.State is MaintenanceRunState.UpdateOffered or MaintenanceRunState.Updating or
                        MaintenanceRunState.WaitingForCommit or MaintenanceRunState.PendingVerification or
                        MaintenanceRunState.RecoveryRequired or MaintenanceRunState.ConfigurationSyncing or
                        MaintenanceRunState.MaintenanceServiceHealthVerified,
                    outcome.TeacherChannelDiagnostics, outcome.Capabilities?.ProtocolVersion);
                if (requestedOperation == MaintenanceOperationKind.ConfigurationSync)
                {
                    var configurationRow = _studentConfigurationSyncTargetProgressItems.FirstOrDefault(item =>
                        string.Equals(NormalizeTargetKey(item.Target), NormalizeTargetKey(outcome.Target),
                            StringComparison.OrdinalIgnoreCase));
                    configurationRow?.SetMaintenanceResult(outcome.State, detail);
                }
            }
            // The anonymous rollout report is produced after the unified workflow returns, not
            // by the removed ViewModel batch path. Include an explicitly requested
            // FullMaintenance as well; configuration-only, service-only and reconcile runs
            // are not student software rollouts and must not create a report.
            var isStudentRollout = requestedOperation is null ||
                requestedOperation == MaintenanceOperationKind.FullMaintenance ||
                requestedOperation == MaintenanceOperationKind.SoftwareUpdate;
            var hasRolloutResults = results.Any(item => item.OperationKind is
                MaintenanceOperationKind.SoftwareUpdate or MaintenanceOperationKind.FullMaintenance);
            if (isStudentRollout &&
                (requestedOperation == MaintenanceOperationKind.SoftwareUpdate || hasRolloutResults))
                CaptureStudentUpdatePerformanceReport(performanceStartedUtc, performanceStopwatch.ElapsedMilliseconds,
                    selectedTargets, results);
            SortStudentUpdateTargetRows();
            var completed = results.Count(item => item.State == MaintenanceRunState.Completed);
            var pending = results.Count(item => item.State is MaintenanceRunState.PendingVerification or
                MaintenanceRunState.Updating or MaintenanceRunState.WaitingForCommit or
                MaintenanceRunState.ConfigurationSyncing or MaintenanceRunState.MaintenanceServiceHealthVerified);
            var manual = results.Count(item => item.State is MaintenanceRunState.ManualRequired or
                MaintenanceRunState.RecoveryRequired);
            var failed = results.Count(item => item.State == MaintenanceRunState.Failed);
            var uniqueResults = results.GroupBy(item => NormalizeTargetKey(item.Target),
                StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).ToArray();
            static bool RequiresFieldMaintenance(TeacherMaintenanceTargetResult item) => item.State is
                MaintenanceRunState.ManualRequired or MaintenanceRunState.RecoveryRequired;
            var fieldMaintenance = uniqueResults.Count(RequiresFieldMaintenance);
            var maintenanceAccepted = uniqueResults.Count(item => !RequiresFieldMaintenance(item) &&
                item.Capabilities?.Names.Contains(
                "maintenance-service.v1", StringComparer.Ordinal) == true);
            var legacy = uniqueResults.Count(item => !RequiresFieldMaintenance(item) &&
                item.Capabilities is not null &&
                item.Capabilities.Names.Contains("maintenance-service.v1",
                    StringComparer.Ordinal) == false);
            var unclassified = uniqueResults.Length - maintenanceAccepted - legacy - fieldMaintenance;
            StudentMaintenanceStatus = $"本批设备：Maintenance 已接管 {maintenanceAccepted} · Legacy {legacy} · 需现场维护 {fieldMaintenance}" +
                (unclassified > 0 ? $" · 结果未分类 {unclassified}" : "") +
                $"。事务：已记录 {results.Count} · 完成 {completed} · 待验证 {pending} · 人工核对 {manual} · 失败 {failed}。";
            StudentUpdateStatus = results.Count == 0
                ? "没有可执行的维护目标。"
                : $"维护操作已持久化 · 完成 {completed} · 待验证 {pending} · 人工核对 {manual} · 失败 {failed}";
            if (requestedOperation == MaintenanceOperationKind.ConfigurationSync)
            {
                StudentConfigurationSyncStatus = $"连接配置维护已记录 {results.Count} 台 · 完成 {completed} · 待验证 {pending} · 人工核对 {manual} · 失败 {failed}";
                SetStudentConfigurationSyncProgress(true, 100,
                    $"已处理 {results.Count}/{selectedTargets.Count} 台学生电脑。");
            }
        }
        catch (Exception exception)
        {
            StudentMaintenanceStatus = "维护未完成：" + exception.Message;
            StudentUpdateStatus = "维护事务已保留；请重新检查签名状态后恢复。";
            foreach (var target in selectedTargets)
            {
                var row = FindStudentUpdateTargetRow(target);
                if (row?.MaintenanceOperationId is not null)
                    row.SetPendingVerification("结果暂时没有返回；请重新查询本机 journal 中的原维护事务。" );
                else
                    row?.SetResult(false, true, "维护没有完成；请核对身份和签名状态后重试。" );
            }
            if (requestedOperation == MaintenanceOperationKind.ConfigurationSync)
            {
                StudentConfigurationSyncStatus = "配置同步结果待核对：" + exception.Message;
                foreach (var configurationRow in _studentConfigurationSyncTargetProgressItems.Where(item =>
                             !item.IsCompleted))
                {
                    var maintenanceRow = FindStudentUpdateTargetRow(configurationRow.Target);
                    configurationRow.SetMaintenanceResult(maintenanceRow?.MaintenanceOperationId is not null
                            ? MaintenanceRunState.PendingVerification : MaintenanceRunState.ManualRequired,
                        maintenanceRow?.MaintenanceOperationId is not null
                            ? "事务日志已保留；" + exception.Message
                            : "没有确认学生端命令；" + exception.Message);
                }
            }
        }
        finally
        {
            SetStudentUpdateProgress(false, true, 0, "");
            if (requestedOperation == MaintenanceOperationKind.ConfigurationSync)
            {
                Changed(nameof(HasStudentConfigurationSyncTargetProgressItems));
                SetStudentConfigurationSyncProgress(false, _studentConfigurationSyncProgressPercent, "");
            }
            EndExclusiveTask();
            Changed(nameof(CanMaintainStudentDevices));
            Changed(nameof(CanOneClickUpdateAllStudentDevices));
            await RefreshMaintenanceOverviewAsync();
        }
    }

    private void CaptureStudentUpdatePerformanceReport(DateTimeOffset startedUtc,
        long rolloutDurationMilliseconds, IReadOnlyCollection<string> selectedTargets,
        IReadOnlyList<TeacherMaintenanceTargetResult> results)
    {
        // The report is built in the Maintenance layer (P1-03 thin-ViewModel boundary);
        // this method only wires UI row state into the factory and publishes the result.
        try
        {
            _lastStudentUpdatePerformanceReport = TeacherMaintenancePerformanceReportFactory.Create(
                startedUtc, rolloutDurationMilliseconds, selectedTargets, results,
                NormalizeTargetKey,
                target => FindStudentUpdateTargetRow(target) is { } row
                    ? new TeacherMaintenanceReportRow(row.Environment, row.RawPhaseTimings)
                    : null);
        }
        catch (Exception)
        {
            _lastStudentUpdatePerformanceReport = null;
            StudentUpdatePerformanceReportExportStatus = "本次结果没有生成匿名性能汇总。";
        }
        Changed(nameof(HasStudentUpdatePerformanceReport));
        Changed(nameof(StudentUpdatePerformanceReportExportStatus));
    }

    private void SetStudentConfigurationSyncProgress(bool visible, double percent, string text)
    {
        if (_isStudentConfigurationSyncProgressVisible != visible)
        {
            _isStudentConfigurationSyncProgressVisible = visible;
            Changed(nameof(IsStudentConfigurationSyncProgressVisible));
        }
        var bounded = Math.Clamp(percent, 0, 100);
        if (Math.Abs(_studentConfigurationSyncProgressPercent - bounded) > 0.01)
        {
            _studentConfigurationSyncProgressPercent = bounded;
            Changed(nameof(StudentConfigurationSyncProgressPercent));
        }
        if (_studentConfigurationSyncProgressText != text)
        {
            _studentConfigurationSyncProgressText = text;
            Changed(nameof(StudentConfigurationSyncProgressText));
        }
    }

    public async Task DeployStudentUpdateAsync()
    {
        await MaintainStudentDevicesAsync(MaintenanceOperationKind.SoftwareUpdate, null);
    }

    public async Task UpdateMaintenanceServiceAsync()
    {
        if (!CanUpdateMaintenanceService) return;
        await MaintainStudentDevicesAsync(MaintenanceOperationKind.MaintenanceServiceUpdate, null);
    }

    public async Task RecheckStudentUpdateResultAsync(StudentApplicationUpdateTargetProgressItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanRequery || !TryBeginExclusiveTask()) return;
        row.BeginRequery();
        try
        {
            if (row.MaintenanceOperationId is { } operationId)
            {
                var workflow = _maintenanceWorkflow ?? throw new InvalidOperationException(
                    "维护恢复服务当前不可用；原 operationId 已保留。" );
                var outcome = await workflow.ResumeAsync(operationId);
                var detail = SanitizeStudentUpdateDetail(outcome.Summary, outcome.Target, row.DisplayName);
                switch (outcome.State)
                {
                    case MaintenanceRunState.Completed:
                        row.SetResult(true, false, detail);
                        break;
                    case MaintenanceRunState.RolledBack:
                        row.SetResult(false, true, detail, rolledBack: true);
                        break;
                    case MaintenanceRunState.PendingVerification:
                    case MaintenanceRunState.UpdateOffered:
                    case MaintenanceRunState.Updating:
                    case MaintenanceRunState.WaitingForCommit:
                    case MaintenanceRunState.ConfigurationSyncing:
                        row.SetPendingVerification(detail);
                        break;
                    case MaintenanceRunState.RecoveryRequired:
                        row.SetRecoveryRequired(detail);
                        break;
                    case MaintenanceRunState.ManualRequired:
                        row.SetManualRequired(detail);
                        break;
                    case MaintenanceRunState.Failed:
                        row.SetResult(false, false, detail);
                        break;
                    default:
                        row.SetCheckingVersions(detail);
                        break;
                }
                row.SetMaintenanceFacts(outcome.Result?.VerifiedCurrentVersions ?? outcome.CurrentVersions,
                    outcome.TargetVersions,
                    outcome.Result?.VerifiedUpdateTransactionState ?? outcome.UpdateTransactionState,
                    outcome.AuthenticatedChannelConnected,
                    outcome.Result?.VerifiedStatusUtc ?? outcome.Capabilities?.VerifiedUtc,
                    outcome.Capabilities?.VerifiedUtc, outcome.AuthenticatedChannelCheckedUtc,
                    outcome.Result?.VerifiedStatusUtc is not null,
                    outcome.OperationId, outcome.Result?.AgentCommandId, outcome.OperationKind, outcome.Channel,
                    outcome.CurrentVersions is not null || outcome.OperationId is not null,
                    outcome.State is MaintenanceRunState.UpdateOffered or MaintenanceRunState.Updating or
                        MaintenanceRunState.WaitingForCommit or MaintenanceRunState.PendingVerification or
                        MaintenanceRunState.RecoveryRequired or MaintenanceRunState.ConfigurationSyncing or
                        MaintenanceRunState.MaintenanceServiceHealthVerified,
                    outcome.TeacherChannelDiagnostics, outcome.Capabilities?.ProtocolVersion);
                StudentMaintenanceStatus = "已重新核对维护事务 " + operationId.ToString("D") + "：" + outcome.Summary;
                return;
            }
            row.SetResult(false, true, "缺少可恢复的 Maintenance journal operationId；不能用教师进程内存状态判断更新结果。");
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or HttpRequestException or
                                          TaskCanceledException or UriFormatException or ArgumentException or
                                          FormatException)
        {
            row.SetResult(false, true, "重新查询失败，更新结果仍待核对：" + exception.Message);
        }
        finally { EndExclusiveTask(); }
    }

    private async Task PrepareStudentUpdateTargetRowsAsync(IReadOnlyList<string> targets,
        IReadOnlyDictionary<string, string> displayNames)
    {
        _studentUpdateRowsByTarget.Clear();
        _studentUpdateTargetsByAddress.Clear();
        StudentUpdateTargetProgressItems.Clear();
        foreach (var target in targets)
        {
            var row = new StudentApplicationUpdateTargetProgressItem(target,
                GetStudentUpdateDisplayName(target, displayNames));
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
            WebsitePolicyResultDetails = string.Join(Environment.NewLine, results.Select(result =>
                $"{GetWebsitePolicyStudentName(result.Target)}：{result.StatusLabel} — {result.Detail}"));
            ShowWebsitePolicyResultDetails = false;
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
                errorMessages.Add($"未全部确认：{succeeded}/{results.Count} 台已确认、{needsReview} 台待核对、{failed} 台拒绝。未确认设备不计为已应用；展开逐台结果查看原因。");
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
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedWebsitePolicyTargets());
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var results = await WebsitePolicyStatusTransport.ReadAsync(targets, campus,
                signingKey.PrivateKey, allowLegacyDirectFallback: true);
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
        if (!CanFillFailedWebsiteTargets) return;
        var failedTargets = _lastFailedWebsiteTargets.Select(NormalizeTargetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SetWebsitePolicyTargetSelection(choice => failedTargets.Contains(NormalizeTargetKey(choice.Target)));
        WebsitePolicyHistoryText = $"已选中 {WebsitePolicyTargetChoices.Count(choice => choice.IsSelected)} 台待重试设备，点击“应用限制”重试。";
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
        _lastFailedWebsiteTargetsCampus = entry.CampusId;
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

    private bool TryGetWebsitePolicyDomainError(out string listName, out string error)
    {
        var lists = SelectedWebsiteMode switch
        {
            WebsitePolicyMode.Blocklist => new[] { (Name: "要阻止的网站名单", Value: WebsiteBlockDomains) },
            WebsitePolicyMode.Allowlist => new[] { (Name: "允许的网站名单", Value: WebsiteAllowDomains) },
            WebsitePolicyMode.Combined => new[]
            {
                (Name: "要阻止的网站名单", Value: WebsiteBlockDomains),
                (Name: "允许的网站名单", Value: WebsiteAllowDomains)
            },
            _ => Array.Empty<(string Name, string Value)>()
        };

        foreach (var (name, value) in lists)
        {
            if (!HasWebsiteEntries(value)) continue;
            try
            {
                _ = WebsitePolicyCompiler.NormalizeDomains(value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            }
            catch (InvalidDataException exception)
            {
                listName = name;
                error = exception.Message;
                return true;
            }
        }

        listName = "";
        error = "";
        return false;
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

    private bool AreStudentUpdateTargetsValid()
    {
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            return WebsitePolicyTransport.NormalizeTargets(GetSelectedStudentUpdateTargets()).Count > 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException)
        { return false; }
    }

    private bool CanMaintainTargetOverride(IReadOnlyCollection<string> targets)
    {
        if (!OperatingSystem.IsWindows() || IsExecuting) return false;
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            var normalized = WebsitePolicyTransport.NormalizeTargets(targets);
            if (normalized.Count != 1) return false;
            return WebsitePolicyTargetChoices.Any(choice => string.Equals(
                NormalizeTargetKey(choice.Target), NormalizeTargetKey(normalized[0]),
                StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException)
        { return false; }
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

    public async Task<IReadOnlyList<StudentAgentIdentityDiscoveryResult>>
        DiscoverStudentAgentIdentityAsync(string target)
    {
        if (!IsCampusIdValid() || !TryBeginExclusiveTask())
        {
            StudentAgentIdentityStatus = "请填写有效校区，并等待当前操作完成。";
            Changed(nameof(StudentAgentIdentityStatus));
            return Array.Empty<StudentAgentIdentityDiscoveryResult>();
        }
        try
        {
            var campus = CampusId.Trim();
            var targets = WebsitePolicyTransport.NormalizeTargets([target]);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var discoveries = await WebsitePolicyStatusTransport.DiscoverIdentitiesAsync(targets, campus,
                signingKey.PrivateKey);
            StudentAgentIdentityStatus = discoveries.SingleOrDefault() is { } result
                ? result.Candidate is null ? $"{target}：{result.Detail}" :
                    result.MatchesPinnedKey ? $"{target}：身份与已固定指纹一致。" :
                    $"{target}：发现待核对的 Agent 身份。"
                : "未取得学生电脑身份。";
            Changed(nameof(StudentAgentIdentityStatus));
            return discoveries;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or InvalidOperationException or CryptographicException or
            HttpRequestException or SocketException or PlatformNotSupportedException or ArgumentException)
        {
            StudentAgentIdentityStatus = "学生电脑身份读取失败：" + exception.Message;
            Changed(nameof(StudentAgentIdentityStatus));
            return Array.Empty<StudentAgentIdentityDiscoveryResult>();
        }
        finally { EndExclusiveTask(); }
    }

    public async Task<bool> ReadDeviceHealthRowAsync(TeacherDeviceHealthRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!IsCampusIdValid() || !TryBeginExclusiveTask()) return false;
        try
        {
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets([row.Target]);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var results = await WebsitePolicyStatusTransport.ReadAsync(targets, campus,
                signingKey.PrivateKey, includeHealthSnapshot: true);
            if (results.Count != 1) return false;
            row.Apply(results[0]);
            row.SetIdentityReviewStatus(results[0].Succeeded
                ? "已读取最新签名状态。"
                : "最新状态读取未完成：" + results[0].Detail);
            ReconcileDeviceHealthAlerts();
            RefreshDeviceHealthView();
            NotifyDeviceHealthBatchProperties();
            ScheduleNextDeviceHealthFreshnessUpdate();
            SaveDeviceHealthCache(campus);
            return results[0].Succeeded && results[0].Status is not null;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or InvalidOperationException or CryptographicException or
            HttpRequestException or SocketException or PlatformNotSupportedException or ArgumentException or
            System.Text.Json.JsonException)
        {
            row.SetIdentityReviewStatus("最新状态读取失败：" + exception.Message);
            return false;
        }
        finally { EndExclusiveTask(); }
    }

    public async Task<IReadOnlyList<StudentAgentIdentityDiscoveryResult>> DiscoverStudentAgentIdentitiesAsync()
    {
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
            var targets = WebsitePolicyTransport.NormalizeTargets(GetSelectedStudentUpdateTargets());
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
        if (!CanGenerateStudentPackage || !TryBeginExclusiveTask()) return;
        PackageOutput = "";
        PackageOutputError = "";
        CanReplaceWebsiteSigningKey = false;
        string? temporaryPublicKey = null;
        CancellationTokenSource? packageBuildCancellation = null;
        var teacherKeyCreated = false;
        var isWindows = OperatingSystem.IsWindows();
        var isMac = OperatingSystem.IsMacOS();
        IsGeneratingStudentPackage = true;
        PackageGenerationStatus = "正在生成配置包……";
        try
        {
            if (!isWindows && !isMac)
            {
                PackageOutputError = "配置包生成目前支持 Windows 首次部署，或 Mac 基于现有学生包更新策略密钥。";
                return;
            }

            PackageContext? trustSourcePackage = null;
            if (isMac)
            {
                if (!HasTrustMigrationSourcePackageDirectory)
                    throw new InvalidDataException("请先选择最初由 Windows 教师端生成的学生配置包文件夹。Mac 不会在没有旧 Veyon 公钥时生成替代身份。");
                trustSourcePackage = PackageContext.Load(TrustMigrationSourcePackageDirectory);
                trustSourcePackage.VerifyUnchanged();
                if (trustSourcePackage.WebsitePolicyPublicKeyPath is null || trustSourcePackage.SchemaVersion < 3)
                    throw new InvalidDataException("所选来源包没有网站策略信任公钥；请选用当前 Windows 教师端生成的完整学生配置包。");
                if (CampusId.Trim() is not ("" or DefaultCampusNamePrefix) &&
                    !string.Equals(CampusId.Trim(), trustSourcePackage.Campus, StringComparison.Ordinal))
                    throw new InvalidDataException($"当前校区“{CampusId.Trim()}”与来源包校区“{trustSourcePackage.Campus}”不一致；没有生成配置包。");
            }

            var campus = trustSourcePackage?.Campus ?? CampusId.Trim();
            if (campus == DefaultCampusNamePrefix)
                throw new InvalidDataException($"请在默认前缀“{DefaultCampusNamePrefix}”后填写校区名称。");
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var packagePrefix = trustSourcePackage?.ComputerPrefix ?? RoomPrefix;
            MachineNaming.CreateRange(packagePrefix, "1", "150");

            if (isWindows)
            {
                PackageGenerationStatus = "正在检查 Veyon……";
                var installed = await Task.Run(VeyonFacts.Probe);
                if (installed.Status != "installed" || !VeyonFacts.IsSupportedVersionDetail(installed.VersionDetail))
                    throw new InvalidDataException("请先安装教师端 Veyon，再生成首次部署配置包。" + Environment.NewLine + installed.AsText());
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var campusFolderName = SafeCampusDirectoryName(campus);
            var outDir = string.IsNullOrWhiteSpace(RoomOutputDir)
                ? Path.Combine(desktop, "Veyon-学生配置包-" + campusFolderName)
                : Path.GetFullPath(RoomOutputDir);
            if (Directory.Exists(outDir) || File.Exists(outDir))
                throw new IOException("输出文件夹已存在。请选择空文件夹，避免覆盖旧密钥。");

            PackageGenerationStatus = "正在准备校区密钥……";
            using var websiteSigningKey = await Task.Run(() =>
                WebsitePolicySigningKeyStore.GetOrCreate(campus,
                    replaceUnavailableSigningKey && isWindows));
            using var applicationSigningKey = await Task.Run(() => ApplicationPolicySigningKeyStore.GetOrCreate(campus));
            using var systemSigningKey = await Task.Run(() => StudentSystemPolicySigningKeyStore.GetOrCreate(campus));

            string publicKeySourcePath;
            if (trustSourcePackage is not null)
            {
                trustSourcePackage.VerifyUnchanged();
                var originalPublicKeyBytes = await File.ReadAllBytesAsync(trustSourcePackage.PublicKeyPath);
                var originalPublicKeyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalPublicKeyBytes));
                if (!string.Equals(originalPublicKeyHash, trustSourcePackage.PublicKeySha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("原配置包中的 Veyon 公钥在读取时发生变化；没有生成更新包。");
                trustSourcePackage.VerifyUnchanged();
                var snapshotPath = Path.Combine(Path.GetTempPath(),
                    "VeyonCampus-source-public-" + Guid.NewGuid().ToString("N") + ".pem");
                temporaryPublicKey = snapshotPath;
                await File.WriteAllBytesAsync(snapshotPath, originalPublicKeyBytes);
                publicKeySourcePath = snapshotPath;
            }
            else
            {
                var publicKeyExportPath = Path.Combine(Path.GetTempPath(), "VeyonCampus-public-" + Guid.NewGuid().ToString("N") + ".pem");
                temporaryPublicKey = publicKeyExportPath;
                PackageGenerationStatus = "正在读取教师安全密钥……";
                var keyResponse = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                    (requestId, caller) => new PrivilegedWorkerRequest(
                        PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                        PrivilegedWorkerOperation.ExportTeacherPublicKey, CampusId: campus));
                teacherKeyCreated = keyResponse.TeacherKeyCreated == true;
                if (!keyResponse.Result.Ok || string.IsNullOrWhiteSpace(keyResponse.PublicKeyPem))
                    throw new IOException(keyResponse.Result.Detail +
                        (teacherKeyCreated ? "\n教师密钥已安全保存在 Windows 中，没有导出。" : ""));
                await File.WriteAllTextAsync(publicKeyExportPath, keyResponse.PublicKeyPem,
                    new System.Text.UTF8Encoding(false));
                publicKeySourcePath = publicKeyExportPath;
            }

            PackageGenerationStatus = "正在准备校区配置，请稍候……";
            var compatibility = PackageCompatibility.ForSupportedProtocolVersions(AppVersion,
                trustSourcePackage?.Compatibility?.Veyon.MinInclusive ?? VeyonInstallerTrust.Version,
                WebsitePolicyAgentInstaller.BuildVersion);
            if (trustSourcePackage?.Compatibility is { } sourceCompatibility)
                compatibility = compatibility with { Veyon = sourceCompatibility.Veyon };
            var recommendations = trustSourcePackage is not null
                ? trustSourcePackage.RecommendedOperations
                : new PackageSetupRecommendations(RecommendInstallVeyon, RecommendRenameComputer,
                    RecommendCreateStudentAccount, RecommendChangeAdminPassword);
            var enableTelemetry = trustSourcePackage is null ||
                                  !string.IsNullOrWhiteSpace(trustSourcePackage.TelemetryEndpoint);
            IReadOnlyList<string>? teacherChannelEndpoints = null;
            byte[]? teacherChannelRootCertificate = null;
            if (isWindows)
            {
                var identityProvider = TeacherMobileControlIdentityProviderFactory.CreateForCurrentPlatform();
                using var teacherIdentity = await Task.Run(() =>
                    identityProvider.CreateIdentity());
                var preferredAddress = identityProvider.SelectPreferredLanAddress(teacherIdentity.Addresses);
                var orderedAddresses = new[] { preferredAddress }
                    .Concat(teacherIdentity.Addresses.Where(address => !address.Equals(preferredAddress)));
                teacherChannelEndpoints = Array.AsReadOnly(orderedAddresses
                    .Select(address => ClassroomEventCryptography.CreateTeacherEndpoint(address))
                    .Distinct(StringComparer.Ordinal).ToArray());
                teacherChannelRootCertificate = teacherIdentity.RootCertificateBytes.ToArray();
            }
            else if (trustSourcePackage?.TeacherChannelEndpoints is { Count: > 0 } sourceEndpoints &&
                     trustSourcePackage.TeacherChannelRootCertificatePath is { } sourceRootPath)
            {
                teacherChannelEndpoints = Array.AsReadOnly(sourceEndpoints.ToArray());
                teacherChannelRootCertificate = await File.ReadAllBytesAsync(sourceRootPath);
            }
            IReadOnlyList<string>? rotationAuthorizations = null;
            if (OperatingSystem.IsWindows())
            {
                var rotationDraft = PolicyTrustRotationDraftStore.Read(campus);
                if (rotationDraft is not null)
                    rotationAuthorizations = Array.AsReadOnly(rotationDraft.Entries
                        .Select(entry => entry.AuthorizationJson).ToArray());
            }
            packageBuildCancellation = new CancellationTokenSource();
            _studentPackageBuildCancellation = packageBuildCancellation;
            IsBuildingStudentPackage = true;
            var token = packageBuildCancellation.Token;
            string built;
            try
            {
                trustSourcePackage?.VerifyUnchanged();
                built = await Task.Run(() => PackageBuilder.Build(outDir, campus, packagePrefix,
                    publicKeySourcePath, websiteSigningKey.PublicKeyPem,
                    enableAnonymousTelemetry: enableTelemetry,
                    cancellationToken: token, applicationPolicyPublicKeyPem: applicationSigningKey.PublicKeyPem,
                    compatibility: compatibility,
                    studentSystemPolicyPublicKeyPem: systemSigningKey.PublicKeyPem,
                    recommendedOperations: recommendations,
                    teacherChannelEndpoints: teacherChannelEndpoints,
                    teacherChannelRootCertificate: teacherChannelRootCertificate,
                    policyTrustRotationAuthorizations: rotationAuthorizations), token);
            }
            finally
            {
                IsBuildingStudentPackage = false;
                _studentPackageBuildCancellation = null;
            }
            PublishPackageDirectory = built;
            PackageOutput = trustSourcePackage is null
                ? $"已生成学生配置包：{built}\n请将此文件夹提供给最新 StudentSetup。"
                : $"已生成策略密钥更新包：{built}\n保留了原包 Veyon 公钥；请先更新 StudentSetup，再在每台学生机以管理员身份重新应用此包。学生端完成更新前，Mac 上的状态和策略仍会显示未信任。";
            if (rotationAuthorizations is { Count: > 0 })
                PackageOutput += "\n本包包含策略信任根轮换授权（schemaVersion=8）；学生端全部确认新根前不要退休旧根。";
            ReadWebsiteSigningCampuses();
        }
        catch (OperationCanceledException) when (packageBuildCancellation?.IsCancellationRequested == true)
        {
            PackageOutputError = "已取消配置包生成；未发布不完整目录。";
        }
        catch (WebsitePolicySigningKeyRecoveryRequiredException exception)
        {
            PackageOutputError = "生成失败：" + exception.Message;
            CanReplaceWebsiteSigningKey = isWindows;
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

    private int GetCurrentBuilderSeatCount() => _seatGrid?.Seats.Length ?? 0;

    private int GetNamedStudentCount()
    {
        return _seatGrid?.Seats.Count(seat => !string.IsNullOrWhiteSpace(seat.StudentName)) ?? 0;
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

    private VeyonNetworkComputer[] BuildRoomImportPlan()
    {
        if (_seatGrid is null || SelectedCampusProfile is not { } campus ||
            SelectedRoomProfile is not { } room ||
            !string.Equals(campus.DisplayName, RoomCampusName.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(room.DisplayName, RoomLocationName.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请先选择并保存当前校区和机房座位图。");
        var seats = _seatGrid.Seats.OrderBy(seat => seat.Row).ThenBy(seat => seat.Column)
            .Where(seat => seat.ComputerNumber is not null && seat.VeyonObjectId is null).ToArray();
        if (seats.Length == 0)
            throw new InvalidDataException("请至少为一个电脑座位分配电脑编号；没有编号的空桌位不会生成学生电脑。");

        return seats.Select(seat =>
        {
            var number = seat.ComputerNumber!.Value;
            var name = MachineNaming.CreateName(RoomPrefix, number.ToString(CultureInfo.InvariantCulture));
            var studentName = string.IsNullOrWhiteSpace(seat.StudentName) ? null : seat.StudentName.Trim();
            return new VeyonNetworkComputer(name, name, studentName);
        }).ToArray();
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
    bool IsSeatAddRemoveMode = false, bool IsNewlyAdded = false, string VeyonComputerName = "",
    Guid? SeatId = null, bool CanDelete = false, bool CanClearVeyonTarget = false)
{
    public bool IsAisle => !IsSeat;
    public bool HasStudentName => IsSeat && !string.IsNullOrWhiteSpace(StudentName);
    public bool IsStudentNameMissing => IsSeat && !HasStudentName;
    public string ComputerIdLabel => !IsSeat ? "" :
        !string.IsNullOrWhiteSpace(ComputerLabel) ? ComputerLabel : "待编号";
    public string StudentNameLabel => HasStudentName ? StudentName.Trim() : "未填写";
    public bool CanEditComputerNumber => !HasVeyonTarget;
    public bool IsSeatWithoutVeyonTarget => IsSeat && !HasVeyonTarget;
    public bool HasVeyonComputerObject => IsSeat && VeyonObjectId is { } id && id != Guid.Empty;
    // Exactly one delete entry per seat:
    // - bound: "删除学生电脑（含 Veyon）" in both preview and edit mode;
    // - unbound in edit mode: the existing draft "删除此座位" (undoable before save);
    // - unbound in preview: a persisted local-only "删除此座位".
    public bool ShowDeleteLocalSeat => IsSeat && CanDelete && !HasVeyonComputerObject;
    public bool CanDeleteSeatComputer => IsSeat && (HasVeyonComputerObject || !CanDelete);
    public string DeleteSeatComputerMenuLabel => HasVeyonComputerObject
        ? "删除学生电脑（含 Veyon）" : "删除此座位";
    public string ComputerStatusLabel => "填写姓名后自动关联电脑编号";
    public string PrimaryLabel => IsSeat
        ? $"{(SeatNumber is { } number ? $"电脑编号 {number}" : "电脑编号待设置")} · {(!string.IsNullOrWhiteSpace(StudentName) ? StudentName : "未填写姓名")}"
        : "＋ 添加座位";
    public string SecondaryLabel => IsSeat ? "桌位" : $"第 {Row} 行 · 第 {Column} 列 · 空桌位区域";
    public string GridLabel => ComputerIdLabel;
    public bool NeedsComputerNumber => HasStudentName && SeatNumber is null;
    public string TooltipText => IsSeat
        ? (IsNewlyAdded ? "本次新增座位\n" : "") +
          $"座位 {(SeatNumber is { } number ? number.ToString(CultureInfo.InvariantCulture) : "待编号")} · {(!string.IsNullOrWhiteSpace(StudentName) ? StudentName : "未填写姓名")}" +
          (HasVeyonTarget ? "\n已关联 Veyon 设备" :
              HasStudentName ? "\n电脑编号待分配" : "") +
          (CanEdit
              ? IsSeatAddRemoveMode ? IsNewlyAdded ? "\n单击可移除此本次新增座位；拖动仅新增" : "\n单击并确认可移除此桌位；拖动仅新增"
                  : "\n点击可编辑姓名"
              : "")
        : $"第 {Row} 行 · 第 {Column} 列\n添加座位模式下点击或拖动可添加桌位";
}

public sealed record TeacherSeatGridAxis(int Index, string Label, bool IsRow, bool CanEdit);

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
    private string _copyStatus = "";
    private string _issueCode = "";
    private string _issueTitle = "";
    private IReadOnlyList<TeacherDeviceHealthIssue> _issues = Array.Empty<TeacherDeviceHealthIssue>();
    private IReadOnlyList<TeacherDeviceHealthIssue> _detailedIssues = Array.Empty<TeacherDeviceHealthIssue>();
    private IReadOnlyList<TeacherDeviceHealthIssue> _runtimeIssues = Array.Empty<TeacherDeviceHealthIssue>();
    private IReadOnlyList<TeacherDeviceHealthCachedIssue> _cachedIssueSummaries =
        Array.Empty<TeacherDeviceHealthCachedIssue>();
    private DateTimeOffset _cacheSavedUtc;
    private string _cachePreviousStatus = "";
    private readonly Dictionary<string, (DateTimeOffset CollectedUtc, int Count)> _issueResolutionConfirmations =
        new(StringComparer.Ordinal);
    private StudentAgentRuntimeSummary? _runtimeSummary;
    private StudentAgentIdentityTrustCandidate? _identityCandidate;
    private TeacherDeviceRuntimeFreshness _runtimeFreshness;
    private int _runtimeFailureCount;
    private Guid? _batchOperationId;
    private Guid _batchOperationCommandId;
    private StudentAgentHealthRepairAction? _batchOperationAction;
    private TeacherDeviceHealthBatchState _batchOperationState;
    private string _batchOperationStatus = "";
    private bool _isRepairing;
    private bool _isLocalCache;
    private bool _isDetailsExpanded;
    private bool _showFullIdentityFingerprints;
    private string _identityReviewStatus = "";
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
    public string DeviceListIdentifier => ComputerLabel.Length > 0 ? ComputerLabel : Target;
    public string DeviceIdentifierSummary => string.Equals(DeviceListIdentifier, Target, StringComparison.OrdinalIgnoreCase)
        ? $"设备编号：{DeviceListIdentifier}"
        : $"设备编号：{DeviceListIdentifier} · 地址：{Target}";
    public string LastSeenSummary => LastCollectedUtc is { } collected
        ? $"最后在线：{collected.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
        : _isLocalCache ? "最后在线：仅有本机历史记录" : "最后在线：尚无可信回执";
    public string AgentVersionSummary => _status?.AgentVersion is { Length: > 0 } statusAgent
        ? $"Agent 版本：{statusAgent}"
        : _runtimeSummary?.AgentVersion is { Length: > 0 } summaryAgent
            ? $"Agent 版本：{summaryAgent}"
            : "Agent 版本：尚未获取";
    public string StudentSetupVersionSummary => _status?.StudentSetupVersion is { Length: > 0 } statusSetup
        ? $"StudentSetup 版本：{statusSetup}"
        : _runtimeSummary?.StudentSetupVersion is { Length: > 0 } summarySetup
            ? $"StudentSetup 版本：{summarySetup}"
            : _status is not null || _runtimeSummary is not null
                ? "StudentSetup 版本：此 Agent 未提供"
                : "StudentSetup 版本：尚未获取";
    public string WebsitePolicyDetailSummary => _status is { } status
        ? $"网站策略：{PolicyModeLabel(status.Website.Mode)} · 已读回 r{status.Website.Revision}"
        : _runtimeSummary is { } summary
            ? $"网站策略：{PolicyModeLabel(summary.WebsiteMode)} · 已读回 r{summary.WebsiteRevision}"
            : "网站策略：尚未读取";
    public string ApplicationPolicyDetailSummary => _status?.Application is { Supported: true } application
        ? $"应用策略：{application.Mode?.ToString() ?? "已读回"} · r{application.Revision?.ToString() ?? "?"}"
        : _runtimeSummary is { ApplicationSupported: true } runtimeApplication
            ? $"应用策略：{runtimeApplication.ApplicationMode?.ToString() ?? "已读回"} · r{runtimeApplication.ApplicationRevision?.ToString() ?? "?"}"
            : "应用策略：未启用或尚未读取";
    public string PolicyRevisionSummary => _status is { } status
        ? $"Agent 已读回版本：网站 r{status.Website.Revision}" +
          (status.Application is { Supported: true } application ? $" · 应用 r{application.Revision?.ToString() ?? "?"}" : "")
        : _runtimeSummary is { } summary
            ? $"Agent 已读回版本：网站 r{summary.WebsiteRevision}" +
              (summary.ApplicationSupported ? $" · 应用 r{summary.ApplicationRevision?.ToString() ?? "?"}" : "")
            : "Agent 已读回版本：尚未读取";
    public string LastAcknowledgementSummary => LastCollectedUtc is { } collected
        ? $"最后回执：{collected.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
        : "最后回执：无可用记录";
    public string IdentityVerificationSummary => _status is not null || _runtimeSummary is not null
        ? _identityCandidate is { } candidate
            ? IdentityMatchesPinnedKey(candidate)
                ? "Agent 签名：有效 · 身份已固定"
                : candidate.PreviouslyPinnedFingerprint is null
                    ? "Agent 签名：有效 · 身份等待教师核对"
                    : "Agent 签名：有效 · 指纹变化，等待教师核对"
            : "Agent 签名：状态回执已验证 · 未提供身份指纹"
        : _identityCandidate is not null
            ? "本轮未取得有效回执；指纹为最近一次有效签名记录"
            : _isLocalCache ? "Agent 签名：当前未验证（仅有本机缓存）" : "Agent 签名：尚未获取";
    public string CurrentFingerprintSummary => _identityCandidate is { } candidate
        ? "当前提交指纹：" + ShortFingerprint(candidate.Fingerprint)
        : "当前提交指纹：尚未获取";
    public string PinnedFingerprintSummary => _identityCandidate?.PreviouslyPinnedFingerprint is { } fingerprint
        ? "已固定指纹：" + ShortFingerprint(fingerprint)
        : "已固定指纹：尚未固定";
    public string FullIdentityFingerprints => _identityCandidate is { } candidate
        ? "当前提交指纹：" + candidate.Fingerprint + Environment.NewLine +
          "已固定指纹：" + (candidate.PreviouslyPinnedFingerprint ?? "尚未固定")
        : "";
    public bool HasIdentityCandidate => _identityCandidate is not null;
    public bool CanReviewIdentity => _identityCandidate is { } candidate && !IdentityMatchesPinnedKey(candidate);
    public bool ShowFullIdentityFingerprints
    {
        get => _showFullIdentityFingerprints;
        set
        {
            if (!Set(ref _showFullIdentityFingerprints, value)) return;
            Raise(nameof(FullIdentityFingerprints));
            Raise(nameof(IdentityFingerprintToggleText));
        }
    }
    public string IdentityFingerprintToggleText => ShowFullIdentityFingerprints
        ? "收起完整指纹" : "查看完整指纹";
    public string IdentityReviewStatus { get => _identityReviewStatus; private set
        { if (Set(ref _identityReviewStatus, value)) Raise(nameof(HasIdentityReviewStatus)); } }
    public bool HasIdentityReviewStatus => IdentityReviewStatus.Length > 0;
    public void SetIdentityReviewStatus(string message) => IdentityReviewStatus = message;
    public string DiagnosticsSummary => BuildDiagnosticsSummary();
    public bool HasDiagnosticsSummary => DiagnosticsSummary.Length > 0;
    public string PolicyReadbackSummary => _status is { } status
        ? $"策略回执：网站 r{status.Website.Revision}" +
          (status.Application is { Supported: true } application ? $" · 应用 r{application.Revision?.ToString() ?? "?"}" : "")
        : _runtimeSummary is { } summary
            ? $"策略回执：网站 r{summary.WebsiteRevision}" +
              (summary.ApplicationSupported ? $" · 应用 r{summary.ApplicationRevision?.ToString() ?? "?"}" : "")
            : "策略回执：尚未读取";
    public string CompactSummary =>
        $"{(IsRecentlyReachable ? "Agent 在线" : LastCollectedUtc is null ? "等待 Agent 回执" : "Agent 未确认在线")} · " +
        PolicyReadbackSummary.Replace("策略回执：", "", StringComparison.Ordinal);
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
    public string CategoryLabel
    {
        get => _categoryLabel;
        private set
        {
            if (!Set(ref _categoryLabel, value)) return;
            Raise(nameof(DiagnosticsSummary));
        }
    }
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
    public string CollectedText
    {
        get => _collectedText;
        private set
        {
            if (!Set(ref _collectedText, value)) return;
            Raise(nameof(DiagnosticsSummary));
        }
    }
    public bool IsLocalCache => _isLocalCache;
    public string Detail
    {
        get => _detail;
        private set
        {
            if (!Set(ref _detail, value)) return;
            Raise(nameof(HasDetail));
            Raise(nameof(DiagnosticsSummary));
            Raise(nameof(HasDiagnosticsSummary));
        }
    }
    public bool HasDetail => Detail.Length > 0;
    public string IssueCode { get => _issueCode; private set
        { if (Set(ref _issueCode, value)) Raise(nameof(DiagnosticsSummary)); } }
    public string IssueTitle { get => _issueTitle; private set
        { if (Set(ref _issueTitle, value)) Raise(nameof(DiagnosticsSummary)); } }
    public IReadOnlyList<TeacherDeviceHealthIssue> Issues => _issues;
    internal IReadOnlyList<TeacherDeviceHealthCachedIssue> CachedIssueSummaries => _cachedIssueSummaries;
    public DateTimeOffset CacheSavedUtc => _cacheSavedUtc;
    public string CachePreviousStatus => _cachePreviousStatus;
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
        set
        {
            if (!Set(ref _repairStatus, value)) return;
            Raise(nameof(HasRepairStatus));
            Raise(nameof(DiagnosticsSummary));
        }
    }
    public bool HasRepairStatus => RepairStatus.Length > 0;
    public string CopyStatus { get => _copyStatus; private set
        { if (Set(ref _copyStatus, value)) Raise(nameof(HasCopyStatus)); } }
    public bool HasCopyStatus => CopyStatus.Length > 0;
    public void SetCopyStatus(string message) => CopyStatus = message;
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
    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        set => Set(ref _isDetailsExpanded, value);
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

    internal bool PreserveAlertForLocalReview(TeacherDeviceHealthAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (_cachedIssueSummaries.Any(issue => issue.Code == alert.IssueCode)) return false;
        var severity = alert.Severity switch
        {
            TeacherDeviceHealthAlertSeverity.Critical => TeacherDeviceHealthCacheIssueSeverity.Critical,
            TeacherDeviceHealthAlertSeverity.Warning => TeacherDeviceHealthCacheIssueSeverity.Warning,
            _ => TeacherDeviceHealthCacheIssueSeverity.Informational
        };
        var detail = string.IsNullOrWhiteSpace(alert.Diagnostics)
            ? "本机保留了此前的异常摘要；当前状态仍需 Agent 确认。"
            : alert.Diagnostics;
        if (detail.Length > 512) detail = detail[..512];
        var preserved = new TeacherDeviceHealthCachedIssue(alert.IssueCode, alert.IssueTitle, severity,
            detail, alert.LastSeenUtc == default ? DateTimeOffset.UtcNow : alert.LastSeenUtc);
        SetCachedIssueSummaries(_cachedIssueSummaries.Append(preserved));
        return true;
    }

    internal void ApplyLocalCache(TeacherDeviceHealthCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _runtimeSummary = null;
        _status = null;
        _detailedIssues = Array.Empty<TeacherDeviceHealthIssue>();
        _runtimeIssues = Array.Empty<TeacherDeviceHealthIssue>();
        _cacheSavedUtc = entry.SavedUtc;
        _cachePreviousStatus = entry.PreviousStatus;
        SetCachedIssueSummaries(GetCachedIssueSummaries(entry));
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
        Raise(nameof(CachedIssueSummaries));
        Raise(nameof(CacheSavedUtc));
        Raise(nameof(CachePreviousStatus));
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
        RaiseHealthDetailProperties();
        NotifyRepairActionAvailability();
    }

    private static IReadOnlyList<TeacherDeviceHealthCachedIssue> GetCachedIssueSummaries(
        TeacherDeviceHealthCacheEntry entry)
    {
        if (entry.PreviousIssues is { Count: > 0 })
            return Array.AsReadOnly(entry.PreviousIssues.Take(8).ToArray());

        var previousStatus = entry.PreviousStatus.Trim();
        var (code, title, severity) = previousStatus switch
        {
            "需关注" => ("cached-status-attention", "上次状态需关注",
                TeacherDeviceHealthCacheIssueSeverity.Warning),
            "需人工处理" or "需人工核对身份" => ("cached-status-manual-review", "上次状态需人工处理",
                TeacherDeviceHealthCacheIssueSeverity.Critical),
            "未收到有效回执" or "未收到回执" => ("cached-status-no-response", "上次未收到回执",
                TeacherDeviceHealthCacheIssueSeverity.Informational),
            _ => ("", "", TeacherDeviceHealthCacheIssueSeverity.Informational)
        };
        return code.Length == 0
            ? Array.Empty<TeacherDeviceHealthCachedIssue>()
            : Array.AsReadOnly(new[]
            {
                new TeacherDeviceHealthCachedIssue(code, title, severity,
                    $"上次记录状态为“{previousStatus}”；当前状态尚未由 Agent 确认。")
            });
    }

    private void MarkLiveStatusReceived()
    {
        var wasLocalCache = _isLocalCache;
        _isLocalCache = false;
        if (wasLocalCache) Raise(nameof(IsLocalCache));
        if (_cachedIssueSummaries.Count == 0) ClearCachedIssueMetadata();
    }

    private void SetCachedIssueSummaries(IEnumerable<TeacherDeviceHealthCachedIssue> issues)
    {
        var next = Array.AsReadOnly(issues.GroupBy(issue => issue.Code, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(issue => issue.LastSeenUtc ?? DateTimeOffset.MinValue)
                .First())
            .OrderByDescending(issue => issue.LastSeenUtc ?? DateTimeOffset.MinValue)
            .Take(8).ToArray());
        if (_cachedIssueSummaries.SequenceEqual(next)) return;
        _cachedIssueSummaries = next;
        Raise(nameof(CachedIssueSummaries));
        if (next.Count != 0) return;
        ClearCachedIssueMetadata();
    }

    private void ClearCachedIssueMetadata()
    {
        if (_cacheSavedUtc == default && _cachePreviousStatus.Length == 0) return;
        _cacheSavedUtc = default;
        _cachePreviousStatus = "";
        Raise(nameof(CacheSavedUtc));
        Raise(nameof(CachePreviousStatus));
    }

    private void ResolveCachedIssuesFromDetailedStatus(StudentAgentStatusResponse status)
    {
        if (_cachedIssueSummaries.Count == 0) return;
        var liveCodes = _detailedIssues.Select(issue => issue.Code).ToHashSet(StringComparer.Ordinal);
        SetCachedIssueSummaries(_cachedIssueSummaries.Select(issue => liveCodes.Contains(issue.Code)
            ? issue with { IsResolved = false, ResolvedUtc = null }
            : DetailedStatusConfirmsResolution(issue.Code, status)
                ? MarkCachedIssueResolved(issue, DateTimeOffset.UtcNow)
                : issue));
    }

    private static bool DetailedStatusConfirmsResolution(string issueCode, StudentAgentStatusResponse status)
    {
        var health = status.Health;
        return issueCode switch
        {
            "cached-status-attention" or "cached-status-manual-review" or "cached-status-no-response" => true,
            "agent-no-response" or "agent-identity" => true,
            "agent-health-unsupported" => health is not null,
            "agent-integrity" => health is
                { RunningAsSystem: true, ManagedTaskMatchesAgent: true, ManagedTaskAclRestricted: true },
            "veyon-service" => health?.VeyonServiceState == StudentVeyonServiceState.Running,
            "veyon-service-unknown" => health?.VeyonServiceState is { } service &&
                                        service != StudentVeyonServiceState.Unknown,
            "update-needs-review" => health?.UpdateTransactionState is { Length: > 0 } update &&
                                     update != "needsReview",
            "update-in-progress" => health?.UpdateTransactionState is { Length: > 0 } update &&
                                     update != "inProgress",
            "system-policy-pending" => status.SystemPolicy?.Pending == false,
            "edge-policy-degraded" => status.EdgeWebsite is { } edge &&
                                      edge.Profiles.All(profile => profile.Status != "degraded"),
            _ => false
        };
    }

    private IReadOnlyList<TeacherDeviceHealthCachedIssue> RetainCachedIssuesUntilConfirmed(
        IEnumerable<TeacherDeviceHealthIssue> currentIssues, StudentAgentRuntimeSummary summary)
    {
        if (_cachedIssueSummaries.Count == 0) return Array.Empty<TeacherDeviceHealthCachedIssue>();
        var currentCodes = currentIssues.Select(issue => issue.Code).ToHashSet(StringComparer.Ordinal);
        var retained = new List<TeacherDeviceHealthCachedIssue>();
        foreach (var issue in _cachedIssueSummaries)
        {
            if (issue.IsResolved)
            {
                retained.Add(issue);
                continue;
            }
            if (currentCodes.Contains(issue.Code))
            {
                retained.Add(issue);
                continue;
            }
            if (RuntimeSummaryOmitsResolutionEvidence(issue.Code, summary))
            {
                _issueResolutionConfirmations.Remove(issue.Code);
                retained.Add(issue);
                continue;
            }

            if (!RuntimeSummaryConfirmsResolution(issue.Code, summary) ||
                !HasConfirmedIssueResolution(issue.Code, summary.CollectedUtc))
                retained.Add(issue);
            else
                retained.Add(MarkCachedIssueResolved(issue, DateTimeOffset.UtcNow));
        }
        return Array.AsReadOnly(retained.ToArray());
    }

    private static TeacherDeviceHealthCachedIssue MarkCachedIssueResolved(
        TeacherDeviceHealthCachedIssue issue, DateTimeOffset resolvedUtc)
    {
        if (issue.IsResolved) return issue;
        var detail = issue.Detail.Contains("连续签名状态已确认恢复", StringComparison.Ordinal)
            ? issue.Detail
            : (issue.Detail.Length == 0 ? "" : issue.Detail + Environment.NewLine) +
              "连续签名状态已确认恢复；本机保留异常记录。";
        if (detail.Length > 512) detail = detail[..512];
        return issue with
        {
            Detail = detail,
            IsResolved = true,
            ResolvedUtc = resolvedUtc
        };
    }

    internal TeacherDeviceHealthCacheEntry? CreateLocalCacheEntry(string campus, DateTimeOffset savedUtc)
    {
        if (_isLocalCache && _runtimeSummary is null && _status is null) return null;
        var status = _cachedIssueSummaries.Any(issue => !issue.IsResolved) && _issues.Count == 0 &&
                     _cachePreviousStatus.Length > 0
            ? _cachePreviousStatus
            : CategoryLabel;
        var issues = _cachedIssueSummaries.Take(8).ToArray();
        return new TeacherDeviceHealthCacheEntry(campus, Target, StudentName, status, AgentSummary,
            ServiceSummary, PolicySummary, UpdateSummary, LastCollectedUtc, savedUtc,
            Array.AsReadOnly(issues));
    }

    private static TeacherDeviceHealthCacheIssueSeverity ToCacheSeverity(
        TeacherDeviceHealthAlertSeverity severity) => severity switch
    {
        TeacherDeviceHealthAlertSeverity.Critical => TeacherDeviceHealthCacheIssueSeverity.Critical,
        TeacherDeviceHealthAlertSeverity.Warning => TeacherDeviceHealthCacheIssueSeverity.Warning,
        _ => TeacherDeviceHealthCacheIssueSeverity.Informational
    };

    public void ApplyRuntimeSummary(StudentAgentRuntimeSummaryDeliveryResult result, int consecutiveFailures)
    {
        ArgumentNullException.ThrowIfNull(result);
        SetIdentityCandidate(result.IdentityCandidate);
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
                SetRuntimeIssues([new TeacherDeviceHealthIssue("agent-identity", "Agent 身份需要核对",
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
                SetRuntimeIssues([new TeacherDeviceHealthIssue("agent-no-response", "Agent 状态未知",
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
        SetIdentityCandidate(result.IdentityCandidate);
        ResolveDetailedIssuesFromRuntimeSummary(summary);
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
                "Agent 已返回签名状态，但没有从交互桌面 Guard 读回键鼠状态；这不表示电脑或 Agent 离线。键鼠相关操作会等状态可核验后再执行。"));
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
        RememberIssueSummaries(issues);
        if (issues.Count == 0 && _detailedIssues.Count == 0)
        {
            Category = TeacherDeviceHealthCategory.Healthy;
            CategoryLabel = "正常";
            Detail = "Agent 轻量状态正常。";
        }
        SetRuntimeIssues(RetainRuntimeIssuesWhenSummaryIsIncomplete(issues, summary));
        SetCachedIssueSummaries(RetainCachedIssuesUntilConfirmed(issues, summary));
        MarkLiveStatusReceived();
        RaiseHealthDetailProperties();
    }

    public void AcknowledgeBootChange()
    {
        if (!_bootChangePendingAcknowledgement) return;
        _bootChangePendingAcknowledgement = false;
        _bootChangedAfterActiveInputLock = false;
        SetRuntimeIssues(_runtimeIssues.Where(issue => issue.Code != "boot-changed"));
        if (_issues.Count == 0)
        {
            Category = _runtimeFreshness == TeacherDeviceRuntimeFreshness.Current
                ? TeacherDeviceHealthCategory.Healthy
                : _runtimeFreshness == TeacherDeviceRuntimeFreshness.Stale
                    ? TeacherDeviceHealthCategory.NoResponse
                    : TeacherDeviceHealthCategory.Pending;
            CategoryLabel = Category switch
            {
                TeacherDeviceHealthCategory.Healthy => "正常",
                TeacherDeviceHealthCategory.NoResponse => "未收到回执",
                _ => "等待读取"
            };
            IssueCode = "";
            IssueTitle = "";
            Detail = "";
        }
    }

    public void RefreshRuntimeFreshness(DateTimeOffset nowUtc)
    {
        if (_runtimeFreshness == TeacherDeviceRuntimeFreshness.IdentityConflict) return;
        var collected = _runtimeSummary?.CollectedUtc ?? _status?.CollectedUtc;
        var next = collected is null
            ? TeacherDeviceRuntimeFreshness.Unknown
            : collected.Value >= nowUtc.AddSeconds(-90)
                ? TeacherDeviceRuntimeFreshness.Current
                : TeacherDeviceRuntimeFreshness.Stale;
        if (next == _runtimeFreshness)
        {
            if (next == TeacherDeviceRuntimeFreshness.Unknown && _runtimeSummary is null && _status is null)
            {
                if (_runtimeFailureCount >= 3 && !_issues.Any(issue => issue.Code == "agent-no-response"))
                    SetRuntimeIssues([new TeacherDeviceHealthIssue("agent-no-response", "Agent 状态未知",
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
            var detail = $"最后一次签名状态于 {collected!.Value.ToLocalTime():HH:mm:ss}；超过 90 秒未能更新，设备状态视为未知。";
            SetRuntimeIssues([new TeacherDeviceHealthIssue("agent-no-response", "Agent 状态已过期",
                TeacherDeviceHealthAlertSeverity.Informational, detail)]);
            Detail = detail;
        }
        else if (next == TeacherDeviceRuntimeFreshness.Unknown && _runtimeSummary is null && _status is null &&
                 _runtimeFailureCount >= 3)
        {
            SetRuntimeIssues([new TeacherDeviceHealthIssue("agent-no-response", "Agent 状态未知",
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
            SetIdentityCandidate(result.IdentityCandidate);
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
            RaiseHealthDetailProperties();
            return;
        }

        _status = status;
        SetIdentityCandidate(result.IdentityCandidate);
        // A complete status response does not contain every field in the lightweight runtime
        // summary (for example, InputGuard). Keep those issues until signed summaries confirm
        // resolution; their reconciliation already requires two distinct confirmations.
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
        ResolveCachedIssuesFromDetailedStatus(status);
        MarkLiveStatusReceived();
        NotifyRepairActionAvailability();
        RaiseHealthDetailProperties();
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
        var next = issues.GroupBy(issue => issue.Code, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        RememberIssueSummaries(next);
        _detailedIssues = Array.AsReadOnly(next);
        RefreshCombinedIssues();
        Raise(nameof(DiagnosticsSummary));
        Raise(nameof(HasDiagnosticsSummary));
    }

    private void SetRuntimeIssues(IEnumerable<TeacherDeviceHealthIssue> issues)
    {
        var next = issues.GroupBy(issue => issue.Code, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        var previous = _runtimeIssues.ToDictionary(issue => issue.Code, StringComparer.Ordinal);
        RememberIssueSummaries(next.Where(issue => !previous.TryGetValue(issue.Code, out var old) ||
            !string.Equals(old.Title, issue.Title, StringComparison.Ordinal) ||
            old.Severity != issue.Severity || !string.Equals(old.Detail, issue.Detail, StringComparison.Ordinal)));
        _runtimeIssues = Array.AsReadOnly(next);
        RefreshCombinedIssues();
        Raise(nameof(DiagnosticsSummary));
        Raise(nameof(HasDiagnosticsSummary));
    }

    private void RememberIssueSummaries(IEnumerable<TeacherDeviceHealthIssue> issues)
    {
        var summaries = _cachedIssueSummaries.ToDictionary(issue => issue.Code, StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        foreach (var issue in issues)
        {
            var detail = issue.Detail.Length <= 512 ? issue.Detail : issue.Detail[..512];
            summaries[issue.Code] = new TeacherDeviceHealthCachedIssue(issue.Code, issue.Title,
                ToCacheSeverity(issue.Severity), detail, now, IsResolved: false);
        }

        SetCachedIssueSummaries(summaries.Values.OrderByDescending(issue => issue.LastSeenUtc)
            .Take(8));
    }

    private IReadOnlyList<TeacherDeviceHealthIssue> RetainRuntimeIssuesWhenSummaryIsIncomplete(
        IEnumerable<TeacherDeviceHealthIssue> currentIssues, StudentAgentRuntimeSummary summary)
    {
        var next = currentIssues.GroupBy(issue => issue.Code, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        var currentCodes = next.Select(issue => issue.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var issueCode in currentCodes)
            _issueResolutionConfirmations.Remove(issueCode);

        var retained = new List<TeacherDeviceHealthIssue>();
        foreach (var issue in _runtimeIssues)
        {
            if (currentCodes.Contains(issue.Code)) continue;
            if (RuntimeSummaryOmitsResolutionEvidence(issue.Code, summary))
            {
                _issueResolutionConfirmations.Remove(issue.Code);
                retained.Add(issue);
                continue;
            }

            if (RuntimeSummaryConfirmsResolution(issue.Code, summary) &&
                !HasConfirmedIssueResolution(issue.Code, summary.CollectedUtc))
                retained.Add(issue);
        }
        return Array.AsReadOnly(retained.Concat(next).ToArray());
    }

    private static bool RuntimeSummaryOmitsResolutionEvidence(string issueCode,
        StudentAgentRuntimeSummary summary) => issueCode switch
    {
        "veyon-service" or "veyon-service-unknown" =>
            summary.VeyonServiceState == StudentVeyonServiceState.Unknown,
        "update-needs-review" or "update-in-progress" =>
            string.IsNullOrWhiteSpace(summary.UpdateTransactionState),
        "system-policy-pending" => summary.SystemPolicyPending is null,
        "edge-policy-degraded" => summary.EdgeProfileCount is null ||
                                    summary.EdgeDegradedProfileCount is null,
        "input-guard-exited" or "input-guard-unknown" or "input-guard-transition" =>
            summary.InputGuard is null ||
            summary.InputGuard.Phase == StudentInputGuardPhase.Unknown,
        _ => false
    };

    private static bool RuntimeSummaryConfirmsResolution(string issueCode,
        StudentAgentRuntimeSummary summary) => issueCode switch
    {
        "cached-status-attention" or "cached-status-manual-review" or "cached-status-no-response" => true,
        "agent-no-response" or "agent-identity" => true,
        "veyon-service" => summary.VeyonServiceState == StudentVeyonServiceState.Running,
        "veyon-service-unknown" => summary.VeyonServiceState != StudentVeyonServiceState.Unknown,
        "update-needs-review" => !string.IsNullOrWhiteSpace(summary.UpdateTransactionState) &&
                                  summary.UpdateTransactionState != "needsReview",
        "update-in-progress" => !string.IsNullOrWhiteSpace(summary.UpdateTransactionState) &&
                                 summary.UpdateTransactionState != "inProgress",
        "system-policy-pending" => summary.SystemPolicyPending == false,
        "edge-policy-degraded" => summary.EdgeProfileCount is not null &&
                                   summary.EdgeDegradedProfileCount == 0,
        "input-guard-exited" => summary.InputGuard is { Phase: not (
            StudentInputGuardPhase.Unknown or StudentInputGuardPhase.GuardExited) },
        "input-guard-unknown" => summary.InputGuard is { Phase: not StudentInputGuardPhase.Unknown },
        "input-guard-transition" => summary.InputGuard is { Phase: not (
            StudentInputGuardPhase.Unknown or StudentInputGuardPhase.Starting or
                StudentInputGuardPhase.Unlocking) },
        _ => false
    };

    private bool HasConfirmedIssueResolution(string issueCode, DateTimeOffset collectedUtc)
    {
        if (_issueResolutionConfirmations.TryGetValue(issueCode, out var previous))
        {
            if (collectedUtc <= previous.CollectedUtc) return previous.Count >= 2;
            var count = previous.Count + 1;
            _issueResolutionConfirmations[issueCode] = (collectedUtc, count);
            return count >= 2;
        }

        _issueResolutionConfirmations[issueCode] = (collectedUtc, 1);
        return false;
    }

    private void ResolveDetailedIssuesFromRuntimeSummary(StudentAgentRuntimeSummary summary)
    {
        if (_detailedIssues.Count == 0) return;
        var retained = new List<TeacherDeviceHealthIssue>();
        foreach (var issue in _detailedIssues)
        {
            if (RuntimeSummaryOmitsResolutionEvidence(issue.Code, summary))
            {
                _issueResolutionConfirmations.Remove(issue.Code);
                retained.Add(issue);
            }
            else if (!RuntimeSummaryConfirmsResolution(issue.Code, summary))
            {
                _issueResolutionConfirmations.Remove(issue.Code);
                retained.Add(issue);
            }
            else if (!HasConfirmedIssueResolution(issue.Code, summary.CollectedUtc))
                retained.Add(issue);
        }
        _detailedIssues = Array.AsReadOnly(retained.ToArray());
    }

    private void RefreshCombinedIssues()
    {
        var merged = _detailedIssues.Concat(_runtimeIssues)
            .GroupBy(issue => issue.Code, StringComparer.Ordinal)
            .Select(group => group.Last()).ToArray();
        if (_issues.SequenceEqual(merged)) return;
        var next = Array.AsReadOnly(merged);
        _issues = next;
        Raise(nameof(Issues));
        var first = next.FirstOrDefault();
        IssueCode = first?.Code ?? "";
        IssueTitle = first?.Title ?? "";
        if (next.Count == 0) return;
        var category = next.Any(issue => issue.Severity == TeacherDeviceHealthAlertSeverity.Critical)
            ? TeacherDeviceHealthCategory.NeedsManualReview
            : next.Any(issue => issue.Severity == TeacherDeviceHealthAlertSeverity.Warning)
                ? TeacherDeviceHealthCategory.Attention
                : next.Any(issue => issue.Code == "agent-no-response")
                    ? TeacherDeviceHealthCategory.NoResponse
                    : TeacherDeviceHealthCategory.Attention;
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
        SetRuntimeIssues(Array.Empty<TeacherDeviceHealthIssue>());
        if (_detailedIssues.Count == 0)
        {
            Category = TeacherDeviceHealthCategory.Pending;
            CategoryLabel = _isLocalCache ? "本地缓存 · 等待确认" : "状态待确认";
            IssueCode = "";
            IssueTitle = "";
            Detail = _isLocalCache
                ? $"本机上次记录尚未过期保存；本轮未收到可信状态。{detail}"
                : detail;
        }
        if (!_isLocalCache) AgentSummary = "尚未收到可信的签名状态";
        Raise(nameof(CompactSummary));
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

    private static string PolicyModeLabel(WebsitePolicyMode mode) => mode switch
    {
        WebsitePolicyMode.Disabled => "已解除",
        WebsitePolicyMode.Blocklist => "阻止名单",
        WebsitePolicyMode.Allowlist => "允许名单",
        WebsitePolicyMode.Combined => "阻止名单与例外",
        _ => "未知"
    };

    private static string ShortFingerprint(string fingerprint) => fingerprint.Length <= 24
        ? fingerprint
        : $"{fingerprint[..12]}…{fingerprint[^8..]}";

    private static bool IdentityMatchesPinnedKey(StudentAgentIdentityTrustCandidate candidate) =>
        candidate.PreviouslyPinnedFingerprint is { } pinned &&
        string.Equals(candidate.Fingerprint, pinned, StringComparison.OrdinalIgnoreCase);

    private string BuildDiagnosticsSummary()
    {
        var lines = new List<string>
        {
            $"学生：{StudentName}",
            $"设备编号：{Target}",
            $"当前状态：{CategoryLabel}",
            "Agent 健康：" + AgentSummary,
            "Veyon 服务：" + ServiceSummary,
            "策略回执：" + PolicySummary,
            "更新状态：" + UpdateSummary,
            InputGuardSummary
        };
        if (!string.IsNullOrWhiteSpace(CollectedText)) lines.Add(CollectedText);
        if (!string.IsNullOrWhiteSpace(IssueCode)) lines.Add($"错误代码：{IssueCode}");
        if (!string.IsNullOrWhiteSpace(IssueTitle)) lines.Add($"问题：{IssueTitle}");
        foreach (var issue in Issues.Take(8))
            lines.Add($"{issue.Code} · {issue.Title}：{issue.Detail}");
        if (!string.IsNullOrWhiteSpace(Detail) && !lines.Any(line => line.EndsWith(Detail, StringComparison.Ordinal)))
            lines.Add("详细信息：" + Detail);
        if (!string.IsNullOrWhiteSpace(RepairStatus)) lines.Add("最近修复：" + RepairStatus);
        var text = string.Join(Environment.NewLine, lines);
        return text.Length <= 16_000 ? text : text[..16_000] + Environment.NewLine + "（诊断内容已截断）";
    }

    public void SetIdentityCandidate(StudentAgentIdentityTrustCandidate? candidate)
    {
        if (candidate is null || _identityCandidate == candidate) return;
        _identityCandidate = candidate;
        Raise(nameof(IdentityVerificationSummary));
        Raise(nameof(CurrentFingerprintSummary));
        Raise(nameof(PinnedFingerprintSummary));
        Raise(nameof(FullIdentityFingerprints));
        Raise(nameof(HasIdentityCandidate));
        Raise(nameof(CanReviewIdentity));
        Raise(nameof(IdentityFingerprintToggleText));
    }

    private void RaiseHealthDetailProperties()
    {
        Raise(nameof(LastSeenSummary));
        Raise(nameof(PolicyReadbackSummary));
        Raise(nameof(CompactSummary));
        Raise(nameof(AgentVersionSummary));
        Raise(nameof(StudentSetupVersionSummary));
        Raise(nameof(WebsitePolicyDetailSummary));
        Raise(nameof(ApplicationPolicyDetailSummary));
        Raise(nameof(PolicyRevisionSummary));
        Raise(nameof(LastAcknowledgementSummary));
        Raise(nameof(IdentityVerificationSummary));
        Raise(nameof(CurrentFingerprintSummary));
        Raise(nameof(PinnedFingerprintSummary));
        Raise(nameof(FullIdentityFingerprints));
        Raise(nameof(HasIdentityCandidate));
        Raise(nameof(CanReviewIdentity));
        Raise(nameof(DiagnosticsSummary));
        Raise(nameof(HasDiagnosticsSummary));
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
        "superseded" => "旧事务已由较新版本安全接管",
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
    private static readonly IBrush RecoveredForegroundBrush = Brush.Parse("#18794E");
    private static readonly IBrush CriticalBackgroundBrush = Brush.Parse("#FDECEC");
    private static readonly IBrush WarningBackgroundBrush = Brush.Parse("#FFF4D6");
    private static readonly IBrush InformationalBackgroundBrush = Brush.Parse("#EDF1F5");
    private static readonly IBrush RecoveredBackgroundBrush = Brush.Parse("#E8F5EC");
    private TeacherDeviceHealthAlertSeverity _severity = severity;
    private IReadOnlyList<TeacherDeviceHealthRow> _affectedRows = Array.Empty<TeacherDeviceHealthRow>();
    private string _diagnostics = "";
    private string _title = title;
    private string _issueTitle = title;
    private string _affectedTargets = "";
    private int _occurrenceCount;
    private DateTimeOffset _lastSeenUtc;
    private bool _isAcknowledged;
    private bool _isBusy;
    private bool _isCached;
    private bool _isResolved;
    private int _cachedPendingReviewCount;
    private int _cachedResolvedCount;
    private DateTimeOffset? _resolutionConfirmedUtc;
    private bool _isDiagnosticsExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string IssueCode { get; } = issueCode;
    internal string IssueTitle => _issueTitle;
    public string Title
    {
        get => _title;
        private set { if (Set(ref _title, value)) Raise(nameof(DisplayTitle)); }
    }
    public string DisplayTitle => Title.Replace("Agent", "学生端", StringComparison.Ordinal);
    public bool IsCached
    {
        get => _isCached;
        private set
        {
            if (!Set(ref _isCached, value)) return;
            Raise(nameof(AffectedCountSummary));
            Raise(nameof(SeenSummary));
            Raise(nameof(AcknowledgementLabel));
            Raise(nameof(CanAcknowledge));
            Raise(nameof(CanAttemptRepair));
            Raise(nameof(SeverityLabel));
            Raise(nameof(SeverityForeground));
            Raise(nameof(SeverityBackground));
        }
    }
    public bool IsResolved
    {
        get => _isResolved;
        private set
        {
            if (!Set(ref _isResolved, value)) return;
            Raise(nameof(AffectedCountSummary));
            Raise(nameof(SeenSummary));
            Raise(nameof(SeverityLabel));
            Raise(nameof(SeverityForeground));
            Raise(nameof(SeverityBackground));
        }
    }
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
    public string AffectedCountSummary => IsCached
        ? _cachedPendingReviewCount > 0 && _cachedResolvedCount > 0
            ? $"{_cachedPendingReviewCount} 台待核对 · {_cachedResolvedCount} 台已恢复"
            : IsResolved ? $"{AffectedCount} 台已恢复" : $"{AffectedCount} 台待核对"
        : $"{AffectedCount} 台受影响";
    public string AffectedTargets { get => _affectedTargets; private set => Set(ref _affectedTargets, value); }
    public string Diagnostics { get => _diagnostics; private set => Set(ref _diagnostics, value); }
    public string SeenSummary => IsCached
        ? IsResolved
            ? $"异常记录于 {LastSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · Agent 确认恢复于 {_resolutionConfirmedUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : $"本机缓存保存于 {LastSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 当前状态待 Agent 确认"
        : $"首次 {FirstSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 最近 {LastSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 读取到 {OccurrenceCount} 次";
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
    public string AcknowledgementLabel => IsCached ? "本地记录只读" : IsAcknowledged ? "已知悉" : "标记已知悉";
    public bool CanAcknowledge => !IsCached && !IsAcknowledged;
    public bool CanRecheck => AffectedRows.Count > 0 && !_isBusy;
    public StudentAgentHealthRepairAction? RecommendedAction => IssueCode switch
    {
        "veyon-service" when AffectedRows.Any(row => row.IsRecentlyReachable && row.CanEnsureVeyonServiceRunning) =>
            StudentAgentHealthRepairAction.EnsureVeyonServiceRunning,
        "edge-policy-degraded" when AffectedRows.Any(row => row.IsRecentlyReachable && row.CanRefreshLegacyEdgePolicy) =>
            StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy,
        _ => null
    };
    public bool CanAttemptRepair => !IsCached && RecommendedAction is not null && !_isBusy;
    public bool IsDiagnosticsExpanded
    {
        get => _isDiagnosticsExpanded;
        set => Set(ref _isDiagnosticsExpanded, value);
    }
    public string SeverityLabel => IsCached
        ? IsResolved ? "已恢复 · 本地记录"
            : _cachedResolvedCount > 0 ? "部分已恢复 · 本地记录" : "缓存待确认"
        : Severity switch
    {
        TeacherDeviceHealthAlertSeverity.Critical => "需要人工处理",
        TeacherDeviceHealthAlertSeverity.Warning => "需关注",
        TeacherDeviceHealthAlertSeverity.Informational when IssueCode == "boot-changed" => "已重启",
        TeacherDeviceHealthAlertSeverity.Informational when IssueCode == "input-guard-unknown" => "Guard 状态未知",
        _ => "未收到回执"
    };
    public IBrush SeverityForeground => IsCached
        ? IsResolved ? RecoveredForegroundBrush : InformationalForegroundBrush
        : Severity switch
    {
        TeacherDeviceHealthAlertSeverity.Critical => CriticalForegroundBrush,
        TeacherDeviceHealthAlertSeverity.Warning => WarningForegroundBrush,
        _ => InformationalForegroundBrush
    };
    public IBrush SeverityBackground => IsCached
        ? IsResolved ? RecoveredBackgroundBrush : InformationalBackgroundBrush
        : Severity switch
    {
        TeacherDeviceHealthAlertSeverity.Critical => CriticalBackgroundBrush,
        TeacherDeviceHealthAlertSeverity.Warning => WarningBackgroundBrush,
        _ => InformationalBackgroundBrush
    };

    public void Update(IReadOnlyList<TeacherDeviceHealthRow> affectedRows, bool isNewOccurrence = true,
        TeacherDeviceHealthAlertSeverity? severity = null)
    {
        ArgumentNullException.ThrowIfNull(affectedRows);
        _cachedPendingReviewCount = 0;
        _cachedResolvedCount = 0;
        IsCached = false;
        IsResolved = false;
        _resolutionConfirmedUtc = null;
        var next = Array.AsReadOnly(affectedRows.ToArray());
        var previousTargets = AffectedRows.Select(row => row.Target)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasNewTargets = next.Any(row => !previousTargets.Contains(row.Target));
        var sameRows = AffectedRows.Count == next.Count && AffectedRows.Zip(next)
            .All(pair => ReferenceEquals(pair.First, pair.Second));
        if (hasNewTargets) IsAcknowledged = false;
        if (severity is { } nextSeverity) Severity = nextSeverity;
        if (next.SelectMany(row => row.Issues).FirstOrDefault(issue => issue.Code == IssueCode) is { } issue)
        {
            _issueTitle = issue.Title;
            Title = issue.Title;
        }
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

    internal void UpdateFromCache(IReadOnlyList<TeacherDeviceHealthRow> affectedRows,
        TeacherDeviceHealthCachedIssue issue, DateTimeOffset savedUtc)
    {
        ArgumentNullException.ThrowIfNull(affectedRows);
        ArgumentNullException.ThrowIfNull(issue);
        var next = Array.AsReadOnly(affectedRows.ToArray());
        var cachedStates = next.Select(row => row.CachedIssueSummaries
            .FirstOrDefault(item => item.Code == issue.Code) ?? issue).ToArray();
        _cachedPendingReviewCount = cachedStates.Count(item => !item.IsResolved);
        _cachedResolvedCount = cachedStates.Count(item => item.IsResolved);
        IsCached = true;
        IsResolved = cachedStates.Length > 0 && _cachedPendingReviewCount == 0;
        _resolutionConfirmedUtc = IsResolved
            ? cachedStates.Select(item => item.ResolvedUtc).OfType<DateTimeOffset>()
                .DefaultIfEmpty(issue.ResolvedUtc ?? savedUtc).Max()
            : null;
        Severity = TeacherDeviceHealthAlertSeverity.Informational;
        _issueTitle = issue.Title;
        Title = issue.IsResolved ? $"已恢复：{issue.Title}" : $"上次记录：{issue.Title}";
        if (AffectedRows.Count != next.Count || !AffectedRows.Zip(next)
                .All(pair => ReferenceEquals(pair.First, pair.Second)))
            AffectedRows = next;
        LastSeenUtc = issue.LastSeenUtc ?? savedUtc;
        AffectedTargets = string.Join("、", next.Take(4).Select(row => row.StudentName)) +
            (next.Count > 4 ? $"等 {next.Count} 台" : "");
        Diagnostics = string.Join(Environment.NewLine, next.Take(8).Select(row =>
        {
            var savedIssue = row.CachedIssueSummaries.FirstOrDefault(item => item.Code == issue.Code) ?? issue;
            var cacheSaved = row.CacheSavedUtc == default
                ? savedUtc
                : row.CacheSavedUtc;
            return $"学生：{row.StudentName}\n异常摘要保存于 {cacheSaved.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {savedIssue.Title}\n{savedIssue.Detail}";
        }));
        Raise(nameof(SeenSummary));
        Raise(nameof(AffectedCountSummary));
        Raise(nameof(SeverityLabel));
        RefreshRepairAvailability();
    }

    internal bool MarkPendingReview(IReadOnlyList<TeacherDeviceHealthRow> affectedRows)
    {
        ArgumentNullException.ThrowIfNull(affectedRows);
        if (affectedRows.Count == 0) return false;
        var nextRows = Array.AsReadOnly(affectedRows.ToArray());
        var changed = AffectedRows.Count != nextRows.Count || !AffectedRows.Zip(nextRows)
            .All(pair => ReferenceEquals(pair.First, pair.Second));
        if (changed) AffectedRows = nextRows;
        if (IsCached && !IsResolved) return changed;
        _cachedPendingReviewCount = nextRows.Count;
        _cachedResolvedCount = 0;
        _resolutionConfirmedUtc = null;
        IsCached = true;
        IsResolved = false;
        Severity = TeacherDeviceHealthAlertSeverity.Informational;
        Title = $"待核对：{_issueTitle}";
        Diagnostics = "本轮没有取得所选设备的新状态，已保留此前异常；当前状态待 Agent 确认。" +
                      (Diagnostics.Length > 0 ? Environment.NewLine + Diagnostics : "");
        Raise(nameof(AffectedCountSummary));
        Raise(nameof(SeenSummary));
        RefreshRepairAvailability();
        return true;
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

public sealed class StudentConfigurationSyncTargetProgressItem(string target, string displayName) : INotifyPropertyChanged
{
    private static readonly IBrush SuccessForeground = Brush.Parse("#18794E");
    private static readonly IBrush SuccessBackground = Brush.Parse("#E8F5EC");
    private static readonly IBrush InfoForeground = Brush.Parse("#175EA8");
    private static readonly IBrush InfoBackground = Brush.Parse("#EAF2FB");
    private static readonly IBrush WarningForeground = Brush.Parse("#9A6700");
    private static readonly IBrush WarningBackground = Brush.Parse("#FFF4D6");
    private static readonly IBrush DangerForeground = Brush.Parse("#B42318");
    private static readonly IBrush DangerBackground = Brush.Parse("#FDECEC");
    private static readonly IBrush NeutralForeground = Brush.Parse("#536477");
    private static readonly IBrush NeutralBackground = Brush.Parse("#EDF1F5");
    private string _statusText = "等待发送";
    private string _detail = "";
    private string _identityReviewStatus = "";
    private double _progressPercent;
    private bool _isIndeterminate;
    private bool _isCompleted;
    private bool _canReviewIdentity;
    private bool _identityReviewActive;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Target { get; } = target;
    public string DisplayName { get; } = displayName;
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (!Set(ref _statusText, value)) return;
            Raise(nameof(StatusForeground));
            Raise(nameof(StatusBackground));
        }
    }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }
    public bool IsCompleted { get => _isCompleted; private set => Set(ref _isCompleted, value); }
    public bool CanReviewIdentity => _canReviewIdentity && !_identityReviewActive;
    public string IdentityReviewStatus { get => _identityReviewStatus; private set => Set(ref _identityReviewStatus, value); }
    public bool HasIdentityReviewStatus => !string.IsNullOrWhiteSpace(IdentityReviewStatus);
    public IBrush StatusForeground => StatusText switch
    {
        "已同步" or "已核验" => SuccessForeground,
        "待核对" => WarningForeground,
        "失败" => DangerForeground,
        "等待发送" => NeutralForeground,
        _ => InfoForeground
    };
    public IBrush StatusBackground => StatusText switch
    {
        "已同步" or "已核验" => SuccessBackground,
        "待核对" => WarningBackground,
        "失败" => DangerBackground,
        "等待发送" => NeutralBackground,
        _ => InfoBackground
    };

    public void Update(StudentAgentConfigurationSyncProgress progress)
    {
        if (progress.Completed)
        {
            SetResult(progress.Succeeded, progress.NeedsReview, progress.Detail);
            return;
        }
        IsIndeterminate = true;
        StatusText = "正在同步";
        Detail = progress.Detail;
    }

    public void SetMaintenanceProgress(string detail)
    {
        IsCompleted = false;
        IsIndeterminate = true;
        StatusText = "正在核验";
        Detail = detail;
    }

    public void SetMaintenanceResult(MaintenanceRunState state, string detail)
    {
        IsCompleted = true;
        IsIndeterminate = false;
        ProgressPercent = 100;
        StatusText = state switch
        {
            MaintenanceRunState.Completed => "已核验",
            MaintenanceRunState.Failed => "失败",
            _ => "待核对"
        };
        Detail = detail;
        SetCanReviewIdentity(state != MaintenanceRunState.Completed && IsIdentityReviewDetail(detail));
        if (state == MaintenanceRunState.Completed) SetIdentityReviewStatus("");
    }

    public void SetResult(bool succeeded, bool needsReview, string detail)
    {
        IsCompleted = true;
        IsIndeterminate = false;
        ProgressPercent = 100;
        StatusText = succeeded ? "已同步" : needsReview ? "待核对" : "失败";
        Detail = detail;
        SetCanReviewIdentity(!succeeded && IsIdentityReviewDetail(detail));
        if (succeeded) SetIdentityReviewStatus("");
    }

    public void BeginIdentityReview()
    {
        _identityReviewActive = true;
        Raise(nameof(CanReviewIdentity));
        IdentityReviewStatus = "正在重新读取此电脑的签名身份……";
        Raise(nameof(HasIdentityReviewStatus));
    }

    public void SetIdentityReviewStatus(string status)
    {
        IdentityReviewStatus = status;
        Raise(nameof(HasIdentityReviewStatus));
        _identityReviewActive = false;
        Raise(nameof(CanReviewIdentity));
    }

    public void CompleteIdentityReview()
    {
        SetCanReviewIdentity(false);
        SetIdentityReviewStatus("身份已核对。请再次运行上方的统一维护操作。");
    }

    private void SetCanReviewIdentity(bool value)
    {
        if (_canReviewIdentity == value) return;
        _canReviewIdentity = value;
        Raise(nameof(CanReviewIdentity));
    }

    private static bool IsIdentityReviewDetail(string detail) =>
        detail.Contains("身份尚未", StringComparison.Ordinal) ||
        detail.Contains("身份需先核对", StringComparison.Ordinal) ||
        detail.Contains("身份指纹已变化", StringComparison.Ordinal) ||
        detail.Contains("身份密钥变化", StringComparison.Ordinal);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class TeacherMaintenanceIssueItem(string target, string displayName, MaintenanceRunState state,
    string detail, DateTimeOffset updatedUtc, MaintenanceComponentVersions? versions,
    bool hasVerifiedCapabilities, bool isTerminal, bool targetIsCurrent, bool identityIsCurrentlyPinned,
    Guid operationId, Guid? agentCommandId, string? updateTransactionState,
    DateTimeOffset? verifiedStatusUtc) : INotifyPropertyChanged
{
    private bool _isBusy;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Target { get; } = target;
    public string DisplayName { get; } = displayName;
    public MaintenanceRunState State { get; } = state;
    public string Detail { get; } = detail;
    public string IdentityTrustText => identityIsCurrentlyPinned
        ? "Agent 身份：与当前固定指纹一致"
        : "Agent 身份：未能与当前固定指纹核对";
    public string OperationText => "维护操作 " + operationId.ToString("N")[..8] +
        (agentCommandId is { } commandId ? " · 命令 " + commandId.ToString("N")[..8] : "");
    public string TransactionStageText => updateTransactionState switch
    {
        "inProgress" => "Agent 更新事务：正在执行",
        "needsReview" => "Agent 更新事务：需要核对",
        "committed" => "Agent 更新事务：已提交",
        "rolledBack" => "Agent 更新事务：已回滚",
        "superseded" => "Agent 更新事务：已被后续事务替代",
        _ => "Agent 更新事务：没有签名读回"
    };
    public string SignedStatusReadbackText => verifiedStatusUtc is { } checkedUtc
        ? "签名状态读回 " + checkedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
        : "没有当前签名版本读回";
    public bool IsTargetUnavailable => !targetIsCurrent;
    public DateTimeOffset UpdatedUtc { get; } = updatedUtc;
    public string UpdatedText => "最近记录 " + UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss",
        CultureInfo.CurrentCulture);
    public string StateText => State switch
    {
        MaintenanceRunState.PendingVerification => "待验证",
        MaintenanceRunState.RecoveryRequired => "需要恢复",
        MaintenanceRunState.ManualRequired => "需现场维护",
        MaintenanceRunState.RolledBack => "已回滚",
        MaintenanceRunState.Failed => "失败",
        MaintenanceRunState.Discovered => "已发现设备",
        MaintenanceRunState.IdentityVerified => "身份已核验",
        MaintenanceRunState.CapabilitiesVerified => "能力已核验",
        MaintenanceRunState.PlanReady => "维护计划就绪",
        MaintenanceRunState.UpdateOffered => "已发送更新，待核对",
        MaintenanceRunState.Updating => "更新中",
        MaintenanceRunState.WaitingForCommit => "等待设备提交",
        MaintenanceRunState.AgentHealthVerified => "Agent 已核验",
        MaintenanceRunState.ConfigurationSyncing => "配置同步中",
        MaintenanceRunState.MaintenanceServiceHealthVerified => "Maintenance 已核验，待继续软件阶段",
        MaintenanceRunState.NoChanges => "没有变化",
        MaintenanceRunState.Completed => "已完成",
        _ => "状态未知"
    };
    public string RecoveryAdviceText => State switch
    {
        MaintenanceRunState.PendingVerification =>
            "建议：使用原 operation/commandId 继续核对签名事务；不要为同一设备另发更新。",
        MaintenanceRunState.RecoveryRequired =>
            "建议：暂停该设备的新命令，先核对原事务和恢复材料；本机自动读回仍不一致时转现场恢复。",
        MaintenanceRunState.ManualRequired =>
            "建议：暂停自动处理，先核对设备身份、签名发行与通道能力，再决定人工修复方式。",
        MaintenanceRunState.RolledBack =>
            "建议：确认回滚版本和设备健康后，修复原失败原因，再单独重试此设备。",
        MaintenanceRunState.Failed =>
            "建议：先根据失败原因和签名版本读回排除前置问题，再单独重试此设备。",
        MaintenanceRunState.MaintenanceServiceHealthVerified =>
            "建议：继续同一维护 operation；服务阶段已核验，后续软件阶段尚未完成。",
        MaintenanceRunState.UpdateOffered or MaintenanceRunState.Updating or
            MaintenanceRunState.WaitingForCommit or MaintenanceRunState.ConfigurationSyncing =>
            "建议：等待或重新核对原事务；未收到签名终态前不要重复下发。",
        _ => "建议：先核对签名状态与组件版本，再选择恢复或现场维护。"
    };
    public string VersionsText
    {
        get
        {
            if (versions is null) return "没有可显示的签名版本读回";
            var values = new List<string>();
            if (versions.StudentSetupVersion is not null) values.Add("Setup " + versions.StudentSetupVersion);
            if (versions.AgentRuntimeVersion is not null) values.Add("Agent " + versions.AgentRuntimeVersion);
            if (versions.MaintenanceVersion is not null) values.Add("Maintenance " + versions.MaintenanceVersion);
            return values.Count == 0 ? "没有可显示的签名版本读回" : string.Join(" · ", values);
        }
    }
    private bool CanRetry => hasVerifiedCapabilities && isTerminal &&
        (State is MaintenanceRunState.Failed or MaintenanceRunState.RolledBack);
    private bool CanRecheck => !isTerminal;
    public bool CanAct => !_isBusy && targetIsCurrent && (CanRetry || CanRecheck);
    public string ActionText => CanRetry ? "重试此设备" : State switch
    {
        MaintenanceRunState.RecoveryRequired => "核对恢复状态",
        MaintenanceRunState.PendingVerification => "继续核对原事务",
        MaintenanceRunState.ManualRequired => "需现场处理",
        _ => "继续核对事务"
    };
    public bool IsBusy => _isBusy;

    public void SetBusy(bool busy)
    {
        if (_isBusy == busy) return;
        _isBusy = busy;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAct)));
    }
}

public sealed class PendingMaintenanceIdentityRotationItem(
    PendingMaintenanceServiceIdentityRotation request, string displayName) : INotifyPropertyChanged
{
    private bool _isBusy;
    public event PropertyChangedEventHandler? PropertyChanged;
    public PendingMaintenanceServiceIdentityRotation Request { get; } = request;
    public string Target => Request.Target;
    public string DisplayName { get; } = displayName;
    public string PreviousFingerprint => Request.PreviousMaintenanceIdentityFingerprint;
    public string CandidateFingerprint => Request.CandidateMaintenanceIdentityFingerprint;
    public string PreviousFingerprintText => "旧公钥：" + PreviousFingerprint;
    public string CandidateFingerprintText => "新公钥：" + CandidateFingerprint;
    public string RequestedText => "请求时间 " + Request.RequestedUtc.ToLocalTime().ToString(
        "yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string AuthorizationText =>
        "新公钥已由此设备当前固定的 Agent 身份签名授权；教师批准前不会接受新服务密钥。";
    public bool IsBusy => _isBusy;
    public bool CanAct => !_isBusy;

    public void SetBusy(bool busy)
    {
        if (_isBusy == busy) return;
        _isBusy = busy;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAct)));
    }
}

public sealed class TeacherMaintenanceConfigurationItem(string displayName, string target,
    MaintenanceConfigurationModule module, MaintenanceConfigurationReconciliationStatus status,
    MaintenanceRun? configurationRun)
{
    public string DisplayName { get; } = displayName;
    public string Target { get; } = target;
    public string ModuleText { get; } = module switch
    {
        MaintenanceConfigurationModule.CampusIdentity => "校区身份",
        MaintenanceConfigurationModule.TeacherConnectivity => "教师连接",
        MaintenanceConfigurationModule.PolicyTrust => "策略信任",
        MaintenanceConfigurationModule.SystemConfiguration => "系统配置",
        MaintenanceConfigurationModule.RuntimeSettings => "运行参数",
        _ => module.ToString()
    };
    private bool HasFailedConfigurationRun => status.State != MaintenanceConfigurationReconciliationState.InSync &&
        configurationRun?.State is MaintenanceRunState.PendingVerification or MaintenanceRunState.Failed or
            MaintenanceRunState.RolledBack or MaintenanceRunState.RecoveryRequired or MaintenanceRunState.ManualRequired;
    public string StateText => HasFailedConfigurationRun && status.State != MaintenanceConfigurationReconciliationState.InSync
        ? configurationRun!.State switch
        {
            MaintenanceRunState.Failed => "配置操作失败",
            MaintenanceRunState.RolledBack => "配置操作已回滚",
            MaintenanceRunState.RecoveryRequired => "配置操作需恢复",
            MaintenanceRunState.ManualRequired => "配置操作需现场处理",
            MaintenanceRunState.PendingVerification => "配置操作待核对",
            _ => "配置操作待核对"
        }
        : status.State switch
    {
        MaintenanceConfigurationReconciliationState.QueuedForDelivery => "等待发送",
        MaintenanceConfigurationReconciliationState.AwaitingReport => "等待回报",
        MaintenanceConfigurationReconciliationState.PendingApplication => "应用中",
        MaintenanceConfigurationReconciliationState.Rejected => "学生端拒绝",
        MaintenanceConfigurationReconciliationState.Drifted => "配置漂移",
        MaintenanceConfigurationReconciliationState.InSync => "已同步",
        _ => status.State.ToString()
    };
    public bool HasConfigurationFailureDetail => HasFailedConfigurationRun &&
        configurationRun?.Transitions.LastOrDefault()?.Reason is { Length: > 0 };
    public string FailureStageText
    {
        get
        {
            var reason = configurationRun?.Transitions.LastOrDefault()?.Reason;
            if (reason is not null && reason.StartsWith("阶段：", StringComparison.Ordinal))
            {
                var separator = reason.IndexOf('。');
                return separator > 3 ? reason[..separator] : "操作阶段：配置同步结果核对";
            }
            return configurationRun?.State switch
            {
                MaintenanceRunState.Failed => "操作阶段：配置事务失败",
                MaintenanceRunState.RolledBack => "操作阶段：配置事务回滚",
                MaintenanceRunState.RecoveryRequired => "操作阶段：等待恢复核对",
                MaintenanceRunState.ManualRequired => "操作阶段：需要人工处理",
                _ => "操作阶段：配置同步结果核对"
            };
        }
    }
    public string FailureDetailText
    {
        get
        {
            var reason = configurationRun?.Transitions.LastOrDefault()?.Reason ?? "";
            if (!reason.StartsWith("阶段：", StringComparison.Ordinal)) return reason;
            var separator = reason.IndexOf('。');
            return separator >= 0 ? reason[(separator + 1)..].Trim() : reason;
        }
    }
    public string RevisionText => $"Desired r{status.DesiredRevision?.ToString() ?? "—"} / Reported r{status.ReportedRevision?.ToString() ?? "—"}";
    public string LastReportedText => status.LastReportedUtc is { } reported
        ? (status.State == MaintenanceConfigurationReconciliationState.InSync ? "最后同步 " : "最后回报 ") +
          reported.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
        : "尚无学生端回报";
    public string AdviceText => status.State switch
    {
        _ when HasFailedConfigurationRun && configurationRun?.State == MaintenanceRunState.RecoveryRequired =>
            "建议：先核对同一配置事务与恢复记录，恢复完成前不要重发命令。",
        _ when HasFailedConfigurationRun && configurationRun?.State == MaintenanceRunState.ManualRequired =>
            "建议：暂停自动重试，按失败原因核对设备身份、配置兼容性和签名通道后现场处理。",
        _ when HasFailedConfigurationRun && configurationRun?.State == MaintenanceRunState.RolledBack =>
            "建议：确认回滚后设备状态，再修复失败原因并创建新的配置修订。",
        _ when HasFailedConfigurationRun && configurationRun?.State == MaintenanceRunState.PendingVerification =>
            "建议：继续核对同一配置事务与学生端签名读回；结果明确前不要再次下发。",
        _ when HasFailedConfigurationRun =>
            "建议：先处理最近配置事务的失败阶段；核对完成后再创建新修订，不要重发未决 commandId。",
        MaintenanceConfigurationReconciliationState.QueuedForDelivery =>
            "建议：等待该固定身份设备重新认证连接；离线队列保留期内会尝试对账。",
        MaintenanceConfigurationReconciliationState.AwaitingReport =>
            "建议：核对学生端连接和签名回报；收到回报前不要把期望配置视为已应用。",
        MaintenanceConfigurationReconciliationState.PendingApplication =>
            "建议：等待学生端应用完成后刷新状态；不要重复发送仍在处理的修订。",
        MaintenanceConfigurationReconciliationState.Rejected =>
            "建议：查看学生端拒绝原因，修复配置或兼容问题后再签发新修订。",
        MaintenanceConfigurationReconciliationState.Drifted =>
            "建议：Desired 与 Reported 不一致；核对设备当前配置后再决定是否重新同步。",
        MaintenanceConfigurationReconciliationState.InSync => "期望配置与学生端签名读回一致。",
        _ => "建议：刷新维护记录并核对配置状态。"
    };
}

public sealed class StudentApplicationUpdateTargetProgressItem(string target, string displayName) : INotifyPropertyChanged
{
    private static readonly IBrush SuccessForeground = Brush.Parse("#18794E");
    private static readonly IBrush SuccessBackground = Brush.Parse("#E8F5EC");
    private static readonly IBrush InfoForeground = Brush.Parse("#175EA8");
    private static readonly IBrush InfoBackground = Brush.Parse("#EAF2FB");
    private static readonly IBrush WarningForeground = Brush.Parse("#9A6700");
    private static readonly IBrush WarningBackground = Brush.Parse("#FFF4D6");
    private static readonly IBrush DangerForeground = Brush.Parse("#B42318");
    private static readonly IBrush DangerBackground = Brush.Parse("#FDECEC");
    private static readonly IBrush NeutralForeground = Brush.Parse("#536477");
    private static readonly IBrush NeutralBackground = Brush.Parse("#EDF1F5");
    private string _statusText = "等待发送";
    private string _detail = "";
    private string _identityReviewStatus = "";
    private double _progressPercent;
    private bool _isIndeterminate;
    private int _phase;
    private int _priority = 2;
    private Guid? _commandId;
    private StudentUpdateEnvironmentCategories? _environment;
    private bool _isRechecking;
    private bool _canReviewIdentity;
    private bool _identityReviewActive;
    private Guid? _maintenanceOperationId;
    private bool _maintenanceCanResume;
    private string _maintenanceVersions = "";
    private string _maintenanceTransaction = "";
    private string _maintenanceConnection = "";
    private string _maintenanceDiagnostics = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<StudentUpdatePhaseTimingDisplayItem> PhaseTimings { get; } = [];
    public IReadOnlyList<StudentUpdatePhaseTiming> RawPhaseTimings { get; private set; } =
        Array.Empty<StudentUpdatePhaseTiming>();
    public StudentUpdateEnvironmentCategories? Environment => _environment;
    public string Target { get; } = target;
    public string DisplayName { get; } = displayName;
    public Guid? CommandId => _commandId;
    public Guid? MaintenanceOperationId => _maintenanceOperationId;
    public bool CanRequery => MaintenanceOperationId is not null && _maintenanceCanResume &&
        (StatusText is "待核对" or "待验证" or "需要恢复") && !_isRechecking;
    public string RequeryButtonText => MaintenanceOperationId is null ? "重新查询更新结果" : "重新核对维护事务";
    public bool CanReviewIdentity => _canReviewIdentity && !_identityReviewActive;
    public string IdentityReviewStatus { get => _identityReviewStatus; private set => Set(ref _identityReviewStatus, value); }
    public bool HasIdentityReviewStatus => !string.IsNullOrWhiteSpace(IdentityReviewStatus);
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (!Set(ref _statusText, value)) return;
            Raise(nameof(StatusForeground));
            Raise(nameof(StatusBackground));
            Raise(nameof(CanRequery));
        }
    }
    public IBrush StatusForeground => StatusText switch
    {
        "成功" or "已是最新" or "已核对版本" => SuccessForeground,
        "失败" => DangerForeground,
        "待核对" or "待验证" or "需要恢复" or "需现场维护" or "已回滚" => WarningForeground,
        "等待发送" => NeutralForeground,
        _ => InfoForeground
    };
    public IBrush StatusBackground => StatusText switch
    {
        "成功" or "已是最新" or "已核对版本" => SuccessBackground,
        "失败" => DangerBackground,
        "待核对" or "待验证" or "需要恢复" or "需现场维护" or "已回滚" => WarningBackground,
        "等待发送" => NeutralBackground,
        _ => InfoBackground
    };
    public string Detail { get => _detail; private set { if (Set(ref _detail, value)) Raise(nameof(HasDetail)); } }
    public bool HasDetail => Detail.Length > 0;
    public bool HasPhaseTimings => PhaseTimings.Count > 0;
    public string MaintenanceVersions { get => _maintenanceVersions; private set { if (Set(ref _maintenanceVersions, value)) Raise(nameof(HasMaintenanceFacts)); } }
    public string MaintenanceTransaction { get => _maintenanceTransaction; private set { if (Set(ref _maintenanceTransaction, value)) Raise(nameof(HasMaintenanceFacts)); } }
    public string MaintenanceConnection { get => _maintenanceConnection; private set { if (Set(ref _maintenanceConnection, value)) Raise(nameof(HasMaintenanceFacts)); } }
    public string MaintenanceDiagnostics { get => _maintenanceDiagnostics; private set { if (Set(ref _maintenanceDiagnostics, value)) Raise(nameof(HasMaintenanceFacts)); } }
    public bool HasMaintenanceFacts => MaintenanceVersions.Length > 0 || MaintenanceTransaction.Length > 0 ||
                                       MaintenanceConnection.Length > 0 || MaintenanceDiagnostics.Length > 0;
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

    public void SetTransportProgress(string stage, Guid? commandId = null)
    {
        if (commandId is { } id && _commandId != id)
        {
            _commandId = id;
            Raise(nameof(CommandId));
            Raise(nameof(CanRequery));
        }
        var (phase, status) = stage.Contains("已完成此电脑处理", StringComparison.Ordinal)
            ? (4, "正在核对更新结果")
            : stage.Contains("更新处理中", StringComparison.Ordinal)
                ? (3, "学生端更新中，正在读取签名阶段")
            : stage.Contains("安装并重启", StringComparison.Ordinal)
                ? (3, "学生端更新中，等待签名回执")
                : stage.Contains("核对提交或回滚状态", StringComparison.Ordinal) ||
                  stage.Contains("核对教师端签名回执", StringComparison.Ordinal)
                    ? (3, "学生端更新中，正在核对签名状态")
                    : (1, "正在连接学生 Agent");
        if (phase < _phase) return;
        _phase = phase;
        IsIndeterminate = phase is 1 or 3 or 4;
        StatusText = status;
        if (phase == 1 && ProgressPercent == 0) Detail = "";
    }

    public void SetMaintenanceFacts(MaintenanceComponentVersions? current,
        MaintenanceComponentVersions? target, string? updateTransactionState,
        bool? authenticatedChannelConnected, DateTimeOffset? verifiedUtc,
        DateTimeOffset? capabilityVerifiedUtc, DateTimeOffset? connectionCheckedUtc,
        bool finalStatusReadback, Guid? operationId,
        Guid? agentCommandId, MaintenanceOperationKind? operationKind,
        MaintenanceReleaseChannel? channel, bool hasSnapshot, bool canResume,
        StudentAgentTeacherChannelDiagnosticsSnapshot? channelDiagnostics, int? statusProtocolVersion)
    {
        if (!hasSnapshot) return;
        if (_maintenanceOperationId != operationId || _maintenanceCanResume != canResume)
        {
            _maintenanceOperationId = operationId;
            _maintenanceCanResume = canResume;
            Raise(nameof(MaintenanceOperationId));
            Raise(nameof(CanRequery));
            Raise(nameof(RequeryButtonText));
        }
        MaintenanceVersions = (finalStatusReadback ? "签名状态读回：" : "检查时：") +
                              FormatVersions(current) + "；目标：" + FormatVersions(target) +
                              (operationKind is { } kind ? "；操作：" + FormatOperation(kind) : "") +
                              (channel is { } releaseChannel ? "；通道：" + FormatChannel(releaseChannel) : "");
        var verification = verifiedUtc is { } time
            ? finalStatusReadback
                ? $"；签名状态读回 {time.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : $"；能力核验 {time.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "";
        var command = agentCommandId is { } id ? $"；Agent commandId {id:D}" : "";
        var operation = operationId is { } operationIdValue ? $"operationId {operationIdValue:D}" : "operationId 未创建";
        MaintenanceTransaction = $"维护操作：{operation}；状态：{StatusText}" +
                                 (operationKind == MaintenanceOperationKind.ConfigurationSync &&
                                  updateTransactionState is null
                                     ? "；学生端更新事务：本次配置同步不产生软件更新事务"
                                     : updateTransactionState is { Length: > 0 } state
                                     ? $"；学生端更新事务：{FormatTransactionState(state)}"
                                     : "；学生端更新事务：未读到终态状态") + command + verification;
        var connectionTime = connectionCheckedUtc is { } connectedUtc
            ? $" · 签名状态核验 {connectedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "";
        MaintenanceConnection = authenticatedChannelConnected switch
        {
            true => "检查时课堂连接：已认证长连接在线" + connectionTime,
            false => "检查时课堂连接：没有已认证长连接（签名状态接口已核验）" + connectionTime,
            _ => "检查时课堂连接：未知（本次未取得可信状态）" + connectionTime
        };
        MaintenanceDiagnostics = channelDiagnostics is { } diagnostics
            ? FormatChannelDiagnostics(diagnostics)
            : statusProtocolVersion is < 5
                ? "教师连接诊断：此学生 Agent 的状态协议较旧，未提供 DNS/TCP/TLS/认证错误阶段读回。"
                : "教师连接诊断：本次未取得可信的诊断状态。";
    }

    private static string FormatChannelDiagnostics(StudentAgentTeacherChannelDiagnosticsSnapshot diagnostics)
    {
        var state = diagnostics.State switch
        {
            "connected" => "最近一次连接通过 TLS 与身份认证",
            "connecting" => "最近一次上报时正在连接",
            "unconfigured" => "缺少连接配置",
            "disconnected" => "最近一次上报时连接失败或已断开",
            _ => "未知"
        };
        var details = new List<string> { $"最近签名上报：{state}" };
        if (diagnostics.FailureCode is { } code) details.Add(code);
        if (!string.IsNullOrWhiteSpace(diagnostics.Detail)) details.Add(diagnostics.Detail);
        if (diagnostics.LastAttemptUtc is { } attempt)
            details.Add("最近尝试 " + attempt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        if (diagnostics.LastSuccessUtc is { } success)
            details.Add("最近认证成功 " + success.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        if (diagnostics.LastFailureUtc is { } failure)
            details.Add("最近失败 " + failure.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        if (diagnostics.LastConfigurationChangeUtc is { } changed)
            details.Add("连接配置变更 " + changed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        if (diagnostics.ConfigurationFingerprint is { Length: 64 } fingerprint)
            details.Add("教师连接指纹 " + fingerprint[..12]);
        if (diagnostics.FailureCode is { } failureCode && FailureNextStep(failureCode) is { } nextStep)
            details.Add("下一步：" + nextStep);
        return string.Join(" · ", details);
    }

    private static string? FailureNextStep(string failureCode) => failureCode switch
    {
        "MissingEndpoint" or "MissingRootCertificate" or "InvalidConfiguration" =>
            "在教师端重新同步校区连接配置。",
        "DnsFailure" => "核对教师地址以及学生网络的名称解析。",
        "TcpConnectFailure" => "确认教师连接服务正在监听，并检查学生到教师的网络可达性。",
        "TlsValidationFailure" => "检查教师证书有效期、证书链和学生端 TLS 根证书是否匹配。",
        "AuthenticationRejected" => "在教师端重新核对并固定此 Agent 身份。",
        "ProtocolMismatch" => "升级教师端或学生 Agent 到兼容版本。",
        "TeacherNotListening" => "启动教师连接服务，并检查相应防火墙规则。",
        "NetworkFailure" => "检查学生机网络以及教师连接服务日志。",
        "ConfigurationChanged" => "等待 Agent 重试；仍失败时重新同步连接配置。",
        _ => null
    };

    private static string FormatVersions(MaintenanceComponentVersions? versions)
    {
        if (versions is null) return "未知";
        return $"StudentSetup {FormatVersion(versions.StudentSetupVersion)} · Agent 运行 {FormatVersion(versions.AgentRuntimeVersion)} · Agent 程序集 {FormatVersion(versions.AgentAssemblyVersion)} · Maintenance {FormatVersion(versions.MaintenanceVersion)}";
    }

    private static string FormatVersion(string? version) => string.IsNullOrWhiteSpace(version) ? "未知" : version;
    private static string FormatTransactionState(string state) => state switch
    {
        "notFound" => "无活动事务",
        "inProgress" => "进行中",
        "committed" => "已提交",
        "rolledBack" => "已回滚",
        "needsReview" => "需要恢复核对",
        "superseded" => "已被新事务取代",
        _ => "未知状态 (" + state + ")"
    };
    private static string FormatOperation(MaintenanceOperationKind kind) => kind switch
    {
        MaintenanceOperationKind.FullMaintenance => "软件与连接配置",
        MaintenanceOperationKind.SoftwareUpdate => "软件更新",
        MaintenanceOperationKind.ConfigurationSync => "连接配置同步",
        MaintenanceOperationKind.MaintenanceServiceUpdate => "Maintenance 服务更新",
        MaintenanceOperationKind.Reconcile => "事务核对",
        _ => kind.ToString()
    };
    private static string FormatChannel(MaintenanceReleaseChannel channel) => channel switch
    {
        MaintenanceReleaseChannel.Stable => "stable",
        MaintenanceReleaseChannel.LocalTest => "本机测试",
        _ => channel.ToString()
    };

    public void BeginRequery()
    {
        if (!CanRequery) return;
        _isRechecking = true;
        _phase = 4;
        IsIndeterminate = true;
        StatusText = "正在重新核对终态";
        Detail = "正在读取此命令对应的签名更新事务。";
        Raise(nameof(CanRequery));
    }

    public void SetPendingVerification(string detail)
    {
        var canResume = MaintenanceOperationId is not null;
        if (_maintenanceCanResume != canResume)
        {
            _maintenanceCanResume = canResume;
            Raise(nameof(CanRequery));
        }
        _isRechecking = false;
        _phase = 5;
        _priority = 1;
        Raise(nameof(Priority));
        IsIndeterminate = false;
        StatusText = "待验证";
        Detail = detail;
        Raise(nameof(CanRequery));
        SetCanReviewIdentity(IsIdentityReviewDetail(detail));
    }

    public void SetRecoveryRequired(string detail)
    {
        var canResume = MaintenanceOperationId is not null;
        if (_maintenanceCanResume != canResume)
        {
            _maintenanceCanResume = canResume;
            Raise(nameof(CanRequery));
        }
        _isRechecking = false;
        _phase = 5;
        _priority = 1;
        Raise(nameof(Priority));
        IsIndeterminate = false;
        StatusText = "需要恢复";
        Detail = detail;
        Raise(nameof(CanRequery));
        SetCanReviewIdentity(IsIdentityReviewDetail(detail));
    }

    public void SetManualRequired(string detail)
    {
        if (_maintenanceCanResume)
        {
            _maintenanceCanResume = false;
            Raise(nameof(CanRequery));
        }
        _isRechecking = false;
        _phase = 5;
        _priority = 1;
        Raise(nameof(Priority));
        IsIndeterminate = false;
        StatusText = "需现场维护";
        Detail = detail;
        Raise(nameof(CanRequery));
        SetCanReviewIdentity(IsIdentityReviewDetail(detail));
    }

    public void SetMaintenanceOperation(Guid operationId)
    {
        if (operationId == Guid.Empty) return;
        if (_maintenanceOperationId == operationId) return;
        _maintenanceOperationId = operationId;
        _maintenanceCanResume = false;
        Raise(nameof(MaintenanceOperationId));
        Raise(nameof(CanRequery));
        Raise(nameof(RequeryButtonText));
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

    public void SetEnvironment(StudentUpdateEnvironmentCategories? environment)
    {
        _environment = StudentUpdateEnvironmentCategories.IsValid(environment)
            ? environment
            : StudentUpdateEnvironmentCategories.Unknown;
    }

    public void SetResult(bool succeeded, bool needsReview, string detail, bool rolledBack = false)
    {
        _isRechecking = false;
        _phase = 5;
        _priority = succeeded ? 3 : rolledBack ? 2 : needsReview ? 1 : 0;
        Raise(nameof(Priority));
        IsIndeterminate = false;
        if (succeeded || rolledBack) ProgressPercent = 100;
        StatusText = succeeded ? "成功" : rolledBack ? "已回滚" : needsReview ? "待核对" : "失败";
        Detail = detail;
        Raise(nameof(CanRequery));
        SetCanReviewIdentity(!succeeded && !rolledBack && IsIdentityReviewDetail(detail));
        if (succeeded || rolledBack) SetIdentityReviewStatus("");
    }

    public void BeginIdentityReview()
    {
        _identityReviewActive = true;
        Raise(nameof(CanReviewIdentity));
        IdentityReviewStatus = "正在重新读取此电脑的签名身份……";
        Raise(nameof(HasIdentityReviewStatus));
    }

    public void SetIdentityReviewStatus(string status)
    {
        IdentityReviewStatus = status;
        Raise(nameof(HasIdentityReviewStatus));
        _identityReviewActive = false;
        Raise(nameof(CanReviewIdentity));
    }

    public void CompleteIdentityReview()
    {
        SetCanReviewIdentity(false);
        SetIdentityReviewStatus("身份已核对。请再次运行上方的统一维护操作。");
    }

    private void SetCanReviewIdentity(bool value)
    {
        if (_canReviewIdentity == value) return;
        _canReviewIdentity = value;
        Raise(nameof(CanReviewIdentity));
    }

    private static bool IsIdentityReviewDetail(string detail) =>
        detail.Contains("身份尚未", StringComparison.Ordinal) ||
        detail.Contains("身份需先核对", StringComparison.Ordinal) ||
        detail.Contains("身份指纹已变化", StringComparison.Ordinal) ||
        detail.Contains("身份密钥变化", StringComparison.Ordinal);

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
    public string DisplayLabel => $"{ComputerName} · {HostAddress} · UUID {ObjectId:D}";
    public override string ToString() => DisplayLabel;
}

public sealed record TeacherVeyonNameSyncConflict(Guid SeatId, int Row, int Column,
    string? CampusStudentName, string CachedVeyonName, string ActualVeyonName,
    string HostAddress, Guid VeyonObjectId, Guid VeyonLocationObjectId)
{
    public string DisplayLabel =>
        $"第 {Row} 行第 {Column} 列：Campus“{(string.IsNullOrWhiteSpace(CampusStudentName) ? "（空）" : CampusStudentName)}” ↔ Veyon“{ActualVeyonName}” · {HostAddress}";
}

public sealed record TeacherSeatAssociationChoice(Guid SeatId, int Row, int Column,
    int? ComputerNumber, string StudentName, Guid? VeyonObjectId)
{
    public string DisplayLabel =>
        $"第 {Row} 行第 {Column} 列 · {StudentName.Trim()} · 编号 {ComputerNumber?.ToString(CultureInfo.InvariantCulture) ?? "未编号"}" +
        (VeyonObjectId is { } objectId ? $" · 已绑 UUID {objectId:D}" : " · 未绑定");
    public override string ToString() => DisplayLabel;
}

public sealed record TeacherVeyonComputerRenamePreview(
    Guid CampusProfileId, Guid RoomId, string LocationName, VeyonComputerRenameTargetPlan Target)
{
    public string ConfirmationSummary =>
        $"只修改 Veyon 电脑显示名：\n地点：{LocationName}\n电脑 UUID：{Target.ObjectId:D}\n" +
        $"Name：{Target.OldName} → {Target.NewName}\nHost：{Target.HostAddress}（保持不变）\n" +
        "学生姓名、座位、Windows 主机名和 Agent 信任保持原值。系统会先备份配置，再核对并读回。";
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
    private string _draft = "";
    private bool _isSupported = isSupported;
    private bool _isSelected;
    private int _unreadCount;
    private readonly HashSet<Guid> _eventIds = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Target { get; } = target;
    public string Title => _title;
    public string Detail => _detail;
    public string Draft => _draft;
    public bool HasDetail => _detail.Length > 0;
    public bool IsSupported => _isSupported;
    public bool IsSelected => _isSelected;
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

    internal void SetDraft(string draft) => _draft = draft;

    internal void SetSelected(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        Changed(nameof(IsSelected));
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
