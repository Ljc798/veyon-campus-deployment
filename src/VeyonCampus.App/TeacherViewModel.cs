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
    private sealed record PreparedSeatSwapPlan(Guid CampusProfileId, Guid RoomId, Guid FirstSeatId,
        Guid SecondSeatId, ClassroomSeatGridSeat FirstSeatSnapshot, ClassroomSeatGridSeat SecondSeatSnapshot,
        string ConfigFingerprint, VeyonComputerRenameTargetPlan[] Targets,
        string FirstStudentName, string SecondStudentName, Guid FirstComputerObjectId,
        Guid FirstLocationObjectId, string FirstComputerOldName, string FirstComputerHostAddress,
        string FirstComputerNewName,
        Guid SecondComputerObjectId, Guid SecondLocationObjectId, string SecondComputerOldName,
        string SecondComputerHostAddress, string SecondComputerNewName, string[] PreviewRows);
    private readonly VeyonInstallerStore _installerStore;
    private readonly TeacherCampusDirectoryStore _campusDirectoryStore;
    private readonly ClassroomSessionStore _classroomSessionStore;
    private readonly ClassroomSeatGridStore _classroomSeatGridStore;
    private readonly ClassroomCountdownStore _classroomCountdownStore;
    private readonly ClassroomTaskProgressStore _classroomTaskProgressStore;
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
    private string _configuratorLaunchError = "";
    private string _campusId = DefaultCampusNamePrefix, _roomOutputDir = "", _packageOutput = "", _packageOutputError = "";
    private string _campusProfileName = "", _roomProfileName = "", _roomProfilePrefix = "PC-", _roomProfileStart = "1", _roomProfileCount = "150", _roomProfileHostOverrides = "";
    private string _campusDirectoryStatus = "", _campusDirectoryError = "";
    private string _classroomDirectoryStatus = "", _classroomDirectoryError = "";
    private int _classroomLocationIndex = -1;
    private bool _classroomDirectorySelectionPending;
    private IReadOnlyList<TeacherCampusProfile> _campusProfiles = Array.Empty<TeacherCampusProfile>();
    private IReadOnlyList<TeacherRoomProfile> _roomProfiles = Array.Empty<TeacherRoomProfile>();
    private IReadOnlyList<TeacherSeatCell> _seatMapCells = Array.Empty<TeacherSeatCell>();
    private IReadOnlyList<TeacherVeyonTargetChoice> _seatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
    private ClassroomSeatGridLayout? _seatGrid;
    private string _seatMapStatus = "";
    private string _seatVeyonTargetStatus = "";
    private string _seatSwapStatus = "";
    private IReadOnlyList<string> _seatSwapPlanRows = Array.Empty<string>();
    private IReadOnlyList<string> _seatSwapTargetResults = Array.Empty<string>();
    private readonly List<Guid> _seatSwapSelection = [];
    private PreparedSeatSwapPlan? _preparedSeatSwapPlan;
    private decimal _seatRows = ClassroomSeatGridStore.DefaultRows;
    private decimal _seatColumns = ClassroomSeatGridStore.DefaultColumns;
    private bool _isLoadingSeatGrid, _isReadingSeatVeyonTargets, _isPreparingSeatSwapPlan, _seatSwapMode;
    private TeacherCampusProfile? _selectedCampusProfile;
    private TeacherRoomProfile? _selectedRoomProfile;
    private ClassroomSession? _activeClassroomSession;
    private ClassroomCountdown? _activeClassroomCountdown;
    private ClassroomTaskProgress? _activeClassroomTaskProgress;
    private decimal _classroomCountdownMinutes = 15;
    private string _classroomCountdownStatus = "";
    private string _classroomTaskProgressDraft = "";
    private string _classroomTaskProgressStatus = "";
    private ClassroomMode _classroomMode = ClassroomMode.Normal;
    private bool _isClassroomTransitioning;
    private int _classroomStatusRefreshInFlight;
    private string _classroomDeliveryStatus = "";
    private string _classroomModeStatus = "";
    private IReadOnlyList<ClassroomPendingRestore> _pendingClassroomRestores = Array.Empty<ClassroomPendingRestore>();
    private string _classroomRestoreStatus = "";
    private bool _isRetryingClassroomRestores;
    private string _classroomEventStatus = "";
    private string _classroomNoticeDraft = "";
    private string _classroomNoticeStatus = "";
    private Guid? _classroomEventFeedSessionId;
    private readonly Dictionary<Guid, TeacherClassroomEventItem> _classroomHelpRows = [];
    private readonly HashSet<Guid> _classroomEventIds = [];
    private readonly Queue<Guid> _classroomEventOrder = new();
    private string _publishPackageDirectory = "", _publisherName = "", _teacherPhoneLast4 = "";
    private string _packagePublisherStatus = "", _packagePublisherError = "", _packagePublishResult = "";
    private string _websiteTargets = "", _websiteBlockDomains = "", _websiteAllowDomains = "",
        _websiteObservedAddress = "", _websiteEntryStatus = "", _websitePolicyResult = "",
        _websitePolicyResultDetails = "", _websitePolicyError = "", _websitePolicyHistoryText = "";
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
    private string _combinedPolicyProfileName = "学生代理+应用";
    private string _combinedPolicyProfileStatus = "";
    private string _applicationInventorySearch = "", _applicationInventoryStatus = "",
        _applicationInventoryFailureDetails = "";
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
    private string _selectedPage = "setup";

    public TeacherViewModel(VeyonInstallerStore? installerStore = null,
        TeacherCampusDirectoryStore? campusDirectoryStore = null,
        UpdateDiagnosticsStore? updateDiagnostics = null,
        ClassroomSessionStore? classroomSessionStore = null,
        ClassroomModeStateStore? classroomModeStateStore = null,
        CampusOperationsTelemetryStore? operationsTelemetryStore = null,
        ClassroomSeatGridStore? classroomSeatGridStore = null,
        ClassroomCountdownStore? classroomCountdownStore = null,
        ClassroomTaskProgressStore? classroomTaskProgressStore = null)
    {
        _installerStore = installerStore ?? new VeyonInstallerStore();
        _campusDirectoryStore = campusDirectoryStore ?? new TeacherCampusDirectoryStore();
        _classroomSessionStore = classroomSessionStore ?? new ClassroomSessionStore();
        _classroomSeatGridStore = classroomSeatGridStore ?? new ClassroomSeatGridStore();
        _classroomCountdownStore = classroomCountdownStore ?? new ClassroomCountdownStore();
        _classroomTaskProgressStore = classroomTaskProgressStore ?? new ClassroomTaskProgressStore();
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

    public bool IsExecuting { get => _isExecuting; private set { _isExecuting = value; Changed(); Changed(nameof(CanEditRoomPlan)); Changed(nameof(CanInstallTeacherVeyon)); Changed(nameof(CanGenerateStudentPackage)); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(CanDisableWebsitePolicy)); Changed(nameof(CanImportCommonWebsiteBlacklist)); Changed(nameof(CanImportCommonWebsiteAllowlist)); Changed(nameof(CanImportCommonCombinedWebsitePolicy)); Changed(nameof(CanAddObservedWebsite)); Changed(nameof(CanPushApplicationPolicy)); Changed(nameof(CanDisableApplicationPolicy)); Changed(nameof(CanPushStudentSystemPolicy)); Changed(nameof(StudentSystemPolicyPushGuidance)); Changed(nameof(CanDisableStudentSystemPolicy)); Changed(nameof(CanReadApplicationPolicyAudit)); Changed(nameof(CanReadApplicationInventory)); Changed(nameof(CanAddSelectedApplicationRules)); Changed(nameof(CanFillFailedWebsiteTargets)); Changed(nameof(CanReadWebsiteLocations)); Changed(nameof(CanApplyWebsiteLocation)); Changed(nameof(CanUseSelectedClassroomLocation)); Changed(nameof(CanReplaceWebsiteSigningKey)); Changed(nameof(CanAddRoomToVeyon)); Changed(nameof(CanPrepareRoomSeatGrid)); Changed(nameof(CanCheckRoomConflicts)); Changed(nameof(CanOpenVeyonConfigurator)); Changed(nameof(CanPublishStudentPackage)); Changed(nameof(CanCheckTeacherUpdate)); Changed(nameof(CanDownloadTeacherUpdate)); Changed(nameof(CanExportOfflineTeacherUpdate)); Changed(nameof(CanVerifyOfflineTeacherUpdate)); Changed(nameof(CanInstallOfflineTeacherUpdate)); Changed(nameof(CanTrustStudentAgentIdentities)); Changed(nameof(CanDeployStudentUpdate)); Changed(nameof(CanToggleClassroomSession)); Changed(nameof(CanChangeClassroomMode)); Changed(nameof(CanRetryClassroomRestores)); Changed(nameof(CanReadSeatVeyonTargets)); Changed(nameof(CanPrepareSeatSwapPlan)); Changed(nameof(CanExecuteSeatSwapPlan)); Changed(nameof(CanEditSeatMap)); Changed(nameof(CanToggleSeatSwapMode)); RefreshSeatMapCells(); } }
    private int _classroomPolicyIndex;
    private int _applicationPolicyStepIndex;
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
    public string ClassroomSessionActionText => HasActiveClassroomSession ? "结束互动" : "开始互动";
    public string ClassroomSessionSummary => _activeClassroomSession is { } active
        ? $"{active.Targets.Length} 台学生电脑 · 互动已开启"
        : SelectedRoomProfile is { } room
            ? room.ComputerCount > 0
                ? $"{room.ComputerCount} 台学生电脑可用于互动"
                : "此地点还没有配置电脑；请先在座位表添加座位并添加到 Veyon。"
            : "请先选择学生电脑范围。";
    public ClassroomMode CurrentClassroomMode => _classroomMode;
    public string ClassroomModeLabel => _classroomMode == ClassroomMode.Practice ? "练习限制已开启" : "未开启练习限制";
    public string ClassroomModeActionText => _classroomMode == ClassroomMode.Practice ? "解除练习限制" : "应用练习限制";
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
                $"{item.RoomName} · {item.DeviceLabel} · {(item.Kind == ClassroomPolicyKind.Website ? "学生代理" : "应用")}")
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
    public string ClassroomInteractionNavigationLabel => PendingClassroomHelpCount is > 0
        ? $"学生互动 Demo · {PendingClassroomHelpCount} 条待回复"
        : "学生互动 Demo";
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
        Changed(nameof(ClassroomInteractionNavigationLabel));
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
        // Keep the teacher view useful during a long class. The event channel is
        // an audit stream; the UI only needs the most recent messages.
        while (ClassroomEventItems.Count > 40)
        {
            var oldest = ClassroomEventItems[^1];
            ClassroomEventItems.RemoveAt(ClassroomEventItems.Count - 1);
            _classroomHelpRows.Remove(oldest.EventId);
        }
        foreach (var row in ClassroomEventItems) row.RefreshExpiry();
        Changed(nameof(HasClassroomEventItems));
        Changed(nameof(PendingClassroomHelpCount));
        Changed(nameof(ClassroomInteractionNavigationLabel));
        Changed(nameof(ClassroomNavigationLabel));
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
    public bool IsRoomPage { get => _selectedPage == "rooms"; set { if (value) SelectPage("rooms"); } }
    public bool IsSetupPage { get => _selectedPage == "setup"; set { if (value) SelectPage("setup"); } }

    public async Task ToggleClassroomSessionAsync()
    {
        if (!CanToggleClassroomSession) return;
        _isClassroomTransitioning = true;
        var sessionUpdated = false;
        Changed(nameof(CanToggleClassroomSession));
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
                    ClassroomModeStatus = "本机策略恢复账本无法更新；原文件已保留，请手动检查。";
                }
                _classroomMode = ClassroomMode.Normal;
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
            Changed(nameof(CanAddRoomToVeyon));
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
            ClassroomMode? mode = activeSession is null ? null :
                _classroomModeStateStore.Read(activeSession.SessionId)?.Mode ?? _classroomMode;
            var command = ClassroomStatusCryptography.Create(campusId, activeSession, DateTimeOffset.UtcNow, mode);
            var results = await ClassroomStatusTransport.SendAsync(targets, command, signingKey.PrivateKey);
            var confirmed = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => result.NeedsReview);
            var failed = results.Count - confirmed - needsReview;
            ClassroomDeliveryStatus = results.Count == 0
                ? "没有学生电脑可通知。"
                : confirmed == results.Count
                    ? $"已通知 {confirmed} 台学生电脑。"
                    : $"已通知 {confirmed}/{results.Count} 台；{needsReview} 台需核对身份，{failed} 台未响应。";
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
                _classroomMode = _classroomModeStateStore.Read(_activeClassroomSession.SessionId)?.Mode ??
                                 ClassroomMode.Normal;
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
        Changed(nameof(HasActiveClassroomEventStatus));
        Changed(nameof(ClassroomSessionActionText));
        Changed(nameof(ClassroomSessionSummary));
        Changed(nameof(CurrentClassroomMode));
        Changed(nameof(ClassroomModeLabel));
        Changed(nameof(ClassroomModeActionText));
        Changed(nameof(CanChangeClassroomMode));
        Changed(nameof(CanToggleClassroomSession));
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
    public bool IsStudentUpdateProgressVisible => _isStudentUpdateProgressVisible;
    public double StudentUpdateProgressPercent => _studentUpdateProgressPercent;
    public bool StudentUpdateProgressIsIndeterminate => _studentUpdateProgressIsIndeterminate;
    public string StudentUpdateProgressText => _studentUpdateProgressText;
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
        !_websiteLocationSelectionPending && AreWebsitePolicyTargetsValid();
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
    public bool CanAddSelectedApplicationRules => !IsExecuting && _allApplicationInventoryChoices.Any(item => item.IsSelected && item.CanSelect);
    public bool CanCheckRoomConflicts => OperatingSystem.IsWindows() && !IsExecuting && !IsCheckingRoomConflicts;
    public bool CanEditRoomPlan => !IsExecuting;
    public bool CanAddRoomToVeyon => OperatingSystem.IsWindows() && !IsExecuting && !IsCheckingRoomConflicts &&
        !_isClassroomTransitioning && !HasActiveClassroomSession &&
        HasRoomPreview && RoomCampusName.Trim().Length > 0 && RoomLocationName.Trim().Length > 0 && RoomError.Length == 0 &&
        _roomConflictCheckCompleted && _roomConflictCheckRevision == _roomPlanRevision &&
        _roomConflictResults.Length == 0 && _roomPlannedNewComputerCount > 0;
    public bool CanOpenVeyonConfigurator => OperatingSystem.IsWindows() && !IsExecuting &&
        !IsCheckingRoomConflicts && !IsReadingWebsiteLocations;
    public string ConfiguratorLaunchError
    {
        get => _configuratorLaunchError;
        private set { _configuratorLaunchError = value; Changed(); Changed(nameof(HasConfiguratorLaunchError)); }
    }
    public bool HasConfiguratorLaunchError => ConfiguratorLaunchError.Length > 0;
    public bool CanReadWebsiteLocations => OperatingSystem.IsWindows() && !IsExecuting && !IsReadingWebsiteLocations;
    public bool CanUseSelectedClassroomLocation => !IsExecuting && !IsReadingWebsiteLocations &&
        _classroomDirectorySelectionPending && ClassroomLocationIndex >= 0 &&
        ClassroomLocationIndex < WebsiteLocations.Count;
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
            Changed(nameof(HasNoSeatMap));
            Changed(nameof(SeatGridSummary));
        }
    }
    public bool HasSeatMap => _seatGrid is not null;
    public bool HasNoSeatMap => !HasSeatMap;
    public int SeatGridSeatCount => _seatGrid?.Seats.Length ?? 0;
    public int SeatGridNamedCount => _seatGrid?.Seats.Count(seat => !string.IsNullOrWhiteSpace(seat.StudentName)) ?? 0;
    public string SeatGridSummary => $"已添加 {SeatGridSeatCount} 个座位 · 已填写 {SeatGridNamedCount} 个姓名";
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
                                           SelectedRoomProfile is not null;
    public bool IsReadingSeatVeyonTargets
    {
        get => _isReadingSeatVeyonTargets;
        private set
        {
            _isReadingSeatVeyonTargets = value;
            Changed();
            Changed(nameof(CanReadSeatVeyonTargets));
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
            Changed(nameof(CanEditSeatMap));
            Changed(nameof(CanToggleSeatSwapMode));
            RefreshSeatMapCells();
        }
    }
    public bool CanEditSeatMap => !IsExecuting && !IsPreparingSeatSwapPlan;
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
            _seatSwapSelection.Clear();
            _preparedSeatSwapPlan = null;
            SeatSwapPlanRows = Array.Empty<string>();
            SeatSwapTargetResults = Array.Empty<string>();
            SeatSwapStatus = value
                ? "换座模式已开启：依次点选两名学生的座位。"
                : "换座模式已关闭。";
            Changed();
            Changed(nameof(CanPrepareSeatSwapPlan));
            Changed(nameof(CanExecuteSeatSwapPlan));
            Changed(nameof(SeatSwapSelectionSummary));
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
    public bool CanPrepareSeatSwapPlan => SeatSwapMode && _seatSwapSelection.Count == 2 &&
                                          !IsExecuting && !IsPreparingSeatSwapPlan && _seatGrid is not null;
    public bool CanExecuteSeatSwapPlan => SeatSwapMode && _preparedSeatSwapPlan is not null &&
                                          !IsExecuting;
    public string SeatSwapSelectionSummary
    {
        get
        {
            if (_seatSwapSelection.Count == 0) return "尚未选择座位。";
            var names = _seatSwapSelection.Select(id => _seatGrid?.Seats.SingleOrDefault(seat => seat.SeatId == id))
                .Where(seat => seat is not null)
                .Select(seat => seat!.StudentName)
                .ToArray();
            return $"已选择 {_seatSwapSelection.Count}/2：{string.Join(" ↔ ", names)}";
        }
    }
    public string SeatGridContext => SelectedCampusProfile is { } campus && SelectedRoomProfile is { } room
        ? $"{campus.DisplayName} · {room.DisplayName}"
        : "尚未选择地点";
    public bool CanPrepareRoomSeatGrid => !IsExecuting && !IsCheckingRoomConflicts &&
                                         !string.IsNullOrWhiteSpace(RoomCampusName) &&
                                         !string.IsNullOrWhiteSpace(RoomLocationName);
    public string RoomCampusName
    {
        get => _roomCampusName;
        set
        {
            if (_roomCampusName == value) return;
            _roomCampusName = value ?? "";
            Changed();
            Changed(nameof(CanPrepareRoomSeatGrid));
            Changed(nameof(CanAddRoomToVeyon));
            Changed(nameof(RoomSummary));
            InvalidateRoomReview();
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            ClearRoomCreateFeedback();
        }
    }
    public decimal SeatRows
    {
        get => _seatRows;
        set
        {
            if (_seatRows == value) return;
            if (_isLoadingSeatGrid)
            {
                _seatRows = value;
                Changed();
                return;
            }
            var rows = decimal.ToInt32(decimal.Round(value, 0, MidpointRounding.AwayFromZero));
            SetSeatDimensions(rows, decimal.ToInt32(decimal.Round(_seatColumns, 0, MidpointRounding.AwayFromZero)));
        }
    }
    public decimal SeatColumns
    {
        get => _seatColumns;
        set
        {
            if (_seatColumns == value) return;
            if (_isLoadingSeatGrid)
            {
                _seatColumns = value;
                Changed();
                return;
            }
            var columns = decimal.ToInt32(decimal.Round(value, 0, MidpointRounding.AwayFromZero));
            SetSeatDimensions(decimal.ToInt32(decimal.Round(_seatRows, 0, MidpointRounding.AwayFromZero)), columns);
        }
    }
    public decimal SeatDimensionMinimum => 1;
    public decimal SeatDimensionMaximum => ClassroomSeatGridStore.MaximumRows;
    public int SeatGridColumns => Math.Clamp((int)_seatColumns, 1, ClassroomSeatGridStore.MaximumColumns);
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
            RoomCampusName = value?.DisplayName ?? "本机校区";
            CampusDirectoryError = "";
            Changed(nameof(ClassroomSessionSummary));
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
            Changed(nameof(ClassroomSessionSummary));
            Changed(nameof(CanToggleClassroomSession));
            Changed(nameof(CanReadSeatVeyonTargets));
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
        }
    }
    public bool HasSelectedRoomProfile => SelectedRoomProfile is not null;
    internal void SaveSeatCell(int row, int column, string studentName)
    {
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room) return;
        try
        {
            ApplySeatGrid(_classroomSeatGridStore.AddOrUpdateSeat(campus.ProfileId, room.RoomId,
                row, column, studentName));
            var cell = SeatMapCells.FirstOrDefault(item => item.Row == row && item.Column == column);
            SeatMapStatus = cell?.SeatNumber is { } seatNumber
                ? $"座位 {seatNumber} 已保存。"
                : "座位已保存。";
            if (RoomNames.Count > 0) UpdateRoomPreviewRows();
            InvalidateRoomReview();
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "无法保存座位表；原布局未更改。" + exception.Message;
        }
    }

    internal void RemoveSeatCell(int row, int column, bool unassignVeyonTarget)
    {
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room) return;
        try
        {
            ApplySeatGrid(_classroomSeatGridStore.RemoveSeat(campus.ProfileId, room.RoomId,
                row, column, unassignVeyonTarget));
            SeatMapStatus = "已将该格设为过道。";
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
        if (!CanReadSeatVeyonTargets || SelectedRoomProfile is not { } room) return;
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
            var choices = orderedCandidates.Where(item => !ambiguousIds.Contains(item.Computer.ObjectId))
                .Select(item => new TeacherVeyonTargetChoice(item.Computer.ObjectId, item.Computer.ParentObjectId,
                    item.Computer.Name, item.HostAddress, item.Computer.Location))
                .ToArray();
            var pendingCount = roomComputers.Length - choices.Length;
            SeatVeyonTargetChoices = Array.AsReadOnly(choices);
            SeatVeyonTargetStatus = choices.Length == 0
                ? $"Veyon 地点“{room.DisplayName}”没有可唯一关联的电脑（需要明确的电脑名称和主机地址）；{pendingCount} 台仍待处理。"
                : $"已读取“{room.DisplayName}”的 {choices.Length} 台可唯一关联电脑。" +
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
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room || _seatGrid is null)
            return;
        try
        {
            var choice = SeatVeyonTargetChoices.SingleOrDefault(item => item.ObjectId == objectId)
                         ?? throw new InvalidDataException("请先读取当前地点的 Veyon 电脑清单。");
            ApplySeatGrid(_classroomSeatGridStore.AssignVeyonTarget(campus.ProfileId, room.RoomId,
                row, column, choice.HostAddress, choice.ComputerName, choice.ObjectId,
                choice.LocationObjectId));
            SeatMapStatus = $"座位已关联 Veyon 电脑“{choice.ComputerName}”；主机地址保持为 {choice.HostAddress}。";
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
        if (!CanEditSeatMap || !SeatSwapMode || _seatGrid is null) return;
        var seat = _seatGrid.Seats.SingleOrDefault(item => item.Row == row && item.Column == column);
        if (seat is null)
        {
            SeatSwapStatus = "只能选择已有学生姓名的座位。";
            return;
        }
        if (string.IsNullOrWhiteSpace(seat.StudentName))
        {
            SeatSwapStatus = "请先给这个座位填写学生姓名。";
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
        else
        {
            _seatSwapSelection.Add(seat.SeatId);
            SeatSwapStatus = _seatSwapSelection.Count == 2
                ? "已选好两名学生；生成计划后可逐台核对电脑名称和保留地址。"
                : "已选择第一名学生，请再点选另一名学生。";
        }
        Changed(nameof(SeatSwapSelectionSummary));
        Changed(nameof(CanPrepareSeatSwapPlan));
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
        SeatSwapStatus = "正在核对两台电脑的 Veyon 对象、名称和地址……";
        SeatSwapPlanRows = Array.Empty<string>();
        _preparedSeatSwapPlan = null;
        try
        {
            var snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot);
            if (SelectedCampusProfile?.ProfileId != campus.ProfileId ||
                SelectedRoomProfile?.RoomId != room.RoomId || _seatGrid is null)
                throw new InvalidOperationException("当前校区或地点已切换；请重新选择座位。");
            var first = layout.Seats.SingleOrDefault(seat => seat.SeatId == selectedIds[0])
                        ?? throw new InvalidDataException("找不到第一个所选座位。");
            var second = layout.Seats.SingleOrDefault(seat => seat.SeatId == selectedIds[1])
                         ?? throw new InvalidDataException("找不到第二个所选座位。");
            var currentFirst = _seatGrid.Seats.SingleOrDefault(seat => seat.SeatId == first.SeatId);
            var currentSecond = _seatGrid.Seats.SingleOrDefault(seat => seat.SeatId == second.SeatId);
            if (currentFirst != first || currentSecond != second)
                throw new InvalidOperationException("座位资料已变化；请重新选择并生成换座计划。");
            var persistedLayout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId);
            if (persistedLayout.Seats.SingleOrDefault(seat => seat.SeatId == first.SeatId) != first ||
                persistedLayout.Seats.SingleOrDefault(seat => seat.SeatId == second.SeatId) != second)
                throw new InvalidOperationException("本机座位表已由其他窗口更新；请重新读取座位表后生成计划。");
            if (string.Equals(first.StudentName, second.StudentName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("两名学生姓名相同或仅大小写不同，无法生成有效改名计划。");

            VeyonNetworkObject ReadBoundComputer(ClassroomSeatGridSeat seat)
            {
                if (seat.VeyonObjectId is not { } objectId ||
                    seat.VeyonLocationObjectId is not { } locationId ||
                    string.IsNullOrWhiteSpace(seat.VeyonHostAddress) ||
                    string.IsNullOrWhiteSpace(seat.VeyonComputerName))
                    throw new InvalidDataException($"座位“{seat.StudentName}”尚未绑定稳定的 Veyon 电脑标识；请先关联电脑。");
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
                $"座位 {first.Row} 行 {first.Column} 列：{first.StudentName} → {firstNewName}；电脑 {firstComputer.Name} → {firstNewName}；保留地址 {firstComputer.Host}",
                $"座位 {second.Row} 行 {second.Column} 列：{second.StudentName} → {secondNewName}；电脑 {secondComputer.Name} → {secondNewName}；保留地址 {secondComputer.Host}"
            };
            _preparedSeatSwapPlan = new PreparedSeatSwapPlan(campus.ProfileId, room.RoomId,
                first.SeatId, second.SeatId, first, second, snapshot.ConfigFingerprint, targetPlans.ToArray(),
                first.StudentName, second.StudentName, firstComputer.ObjectId, firstComputer.ParentObjectId,
                firstComputer.Name,
                VeyonHostAddress.NormalizeOverride(firstComputer.Host), firstNewName,
                secondComputer.ObjectId, secondComputer.ParentObjectId, secondComputer.Name,
                VeyonHostAddress.NormalizeOverride(secondComputer.Host), secondNewName, rows);
            SeatSwapPlanRows = Array.AsReadOnly(rows);
            SeatSwapStatus = "计划已生成。确认前请逐台核对旧名称、新名称和保留地址。";
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
        SeatSwapStatus = "正在备份 Veyon 配置、执行改名并逐台读回核对……";
        SeatSwapTargetResults = Array.Empty<string>();
        try
        {
            var latestLayout = _classroomSeatGridStore.GetLayout(plan.CampusProfileId, plan.RoomId);
            if (latestLayout.Seats.SingleOrDefault(seat => seat.SeatId == plan.FirstSeatId) != plan.FirstSeatSnapshot ||
                latestLayout.Seats.SingleOrDefault(seat => seat.SeatId == plan.SecondSeatId) != plan.SecondSeatSnapshot)
                throw new InvalidOperationException("本机座位表自生成计划后已有变化；没有修改 Veyon。请重新读取并生成计划。");
            var response = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                (requestId, caller) => new PrivilegedWorkerRequest(
                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                    PrivilegedWorkerOperation.RenameVeyonComputers,
                    VeyonConfigFingerprint: plan.ConfigFingerprint,
                    VeyonComputerRenameTargets: plan.Targets));
            var targetResults = response.VeyonRenameResults ?? Array.Empty<VeyonComputerRenameTargetResult>();
            SeatSwapTargetResults = Array.AsReadOnly(targetResults.Select(target =>
                $"{target.OldName} → {target.NewName} · {target.HostAddress} · {target.Status} · {target.Detail}").ToArray());
            if (!response.Result.Ok || targetResults.Count != plan.Targets.Length ||
                targetResults.Any(target => target.Status != "succeeded") ||
                !plan.Targets.All(expected => targetResults.Any(actual => actual.ObjectId == expected.ObjectId)))
            {
                SeatSwapStatus = response.Result.Detail;
                SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
                SeatVeyonTargetStatus = "请重新读取当前地点的 Veyon 电脑清单后再处理座位关联。";
                _preparedSeatSwapPlan = null;
                Changed(nameof(CanExecuteSeatSwapPlan));
                return;
            }

            try
            {
                var updated = _classroomSeatGridStore.CommitStudentSwap(plan.CampusProfileId,
                    plan.RoomId, plan.FirstSeatId, plan.SecondSeatId,
                    plan.FirstStudentName, plan.SecondStudentName,
                    plan.FirstComputerObjectId, plan.FirstLocationObjectId, plan.FirstComputerOldName,
                    plan.FirstComputerHostAddress,
                    plan.FirstComputerNewName,
                    plan.SecondComputerObjectId, plan.SecondLocationObjectId, plan.SecondComputerOldName,
                    plan.SecondComputerHostAddress,
                    plan.SecondComputerNewName);
                ApplySeatGrid(updated);
                RefreshClassroomEventSeatLocations();
                SeatSwapStatus = response.Result.Detail + " 本机学生与座位记录已同步保存。";
                SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
                SeatVeyonTargetStatus = "换座已完成；如需重新关联电脑，请读取最新 Veyon 清单。";
                _preparedSeatSwapPlan = null;
                _seatSwapSelection.Clear();
                _seatSwapMode = false;
                Changed(nameof(SeatSwapMode));
                Changed(nameof(SeatSwapSelectionSummary));
                Changed(nameof(CanPrepareSeatSwapPlan));
                Changed(nameof(CanExecuteSeatSwapPlan));
                RefreshSeatMapCells();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or InvalidOperationException)
            {
                SeatSwapStatus = "Veyon 名称已逐台读回，但本机座位表未能保存；请先核对备份，再同步本机学生记录。" +
                                 exception.Message + " " + response.Result.Detail;
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
            SeatSwapStatus = "换座结果需要核对；本机学生座位记录没有提交。" + exception.Message;
            SeatVeyonTargetChoices = Array.Empty<TeacherVeyonTargetChoice>();
            SeatVeyonTargetStatus = "换座结果需要核对；请重新读取 Veyon 电脑清单。";
            _preparedSeatSwapPlan = null;
            Changed(nameof(CanExecuteSeatSwapPlan));
        }
        finally { EndExclusiveTask(); }
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
            SeatMapStatus = "地点已保存在教师电脑。座位图已打开；点空格添加座位，座位号会自动排列。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or FormatException or OverflowException)
        {
            SeatMapStatus = "无法建立座位表；本机资料未更改。" + exception.Message;
        }
    }

    private bool TrySaveCurrentRoomProfile(out string error, int? configuredComputerCount = null)
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
            if (configuredComputerCount is { } computerCount)
            {
                var names = MachineNaming.CreateRange(RoomPrefix, RoomStart,
                    computerCount.ToString(CultureInfo.InvariantCulture));
                var hosts = ParseHostOverrides(RoomComputerHosts, names.Count);
                room = new TeacherRoomProfile(roomIndex >= 0 ? rooms[roomIndex].RoomId : Guid.NewGuid(),
                    roomName, RoomPrefix.Trim(), int.Parse(RoomStart, CultureInfo.InvariantCulture),
                    names.Count, hosts.ToList());
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
            if (isNewRoom && configuredComputerCount is null)
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
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room)
        {
            _seatGrid = null;
            _seatRows = ClassroomSeatGridStore.DefaultRows;
            _seatColumns = ClassroomSeatGridStore.DefaultColumns;
            SeatMapCells = Array.Empty<TeacherSeatCell>();
            SeatMapStatus = "";
            Changed(nameof(SeatRows));
            Changed(nameof(SeatColumns));
            Changed(nameof(SeatGridColumns));
            Changed(nameof(SeatGridSeatCount));
            Changed(nameof(SeatGridNamedCount));
            return;
        }

        try
        {
            ApplySeatGrid(_classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId));
            SeatMapStatus = "点空格添加座位；点已有座位可编辑姓名或设为过道。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _seatGrid = null;
            SeatMapCells = Array.Empty<TeacherSeatCell>();
            SeatMapStatus = "无法读取座位表；原文件保留，未展示不完整布局。";
            Changed(nameof(HasSeatMap));
            Changed(nameof(SeatGridSeatCount));
            Changed(nameof(SeatGridNamedCount));
            Changed(nameof(SeatRows));
            Changed(nameof(SeatColumns));
            Changed(nameof(SeatGridColumns));
        }
    }

    private void SetSeatDimensions(int rows, int columns)
    {
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room || _seatGrid is null)
        {
            Changed(nameof(SeatRows));
            Changed(nameof(SeatColumns));
            return;
        }
        try
        {
            ApplySeatGrid(_classroomSeatGridStore.SetDimensions(campus.ProfileId, room.RoomId, rows, columns));
            SeatMapStatus = $"座位表已调整为 {rows} 行、{columns} 列。";
            RefreshClassroomEventSeatLocations();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SeatMapStatus = "无法调整座位表；原布局未更改。" + exception.Message;
            Changed(nameof(SeatRows));
            Changed(nameof(SeatColumns));
        }
    }

    private void ApplySeatGrid(ClassroomSeatGridLayout layout)
    {
        _preparedSeatSwapPlan = null;
        _seatSwapSelection.Clear();
        SeatSwapPlanRows = Array.Empty<string>();
        Changed(nameof(CanPrepareSeatSwapPlan));
        Changed(nameof(CanExecuteSeatSwapPlan));
        Changed(nameof(SeatSwapSelectionSummary));
        _seatGrid = layout with { Seats = layout.Seats.Select(seat => seat with { }).ToArray() };
        _isLoadingSeatGrid = true;
        _seatRows = layout.Rows;
        _seatColumns = layout.Columns;
        Changed(nameof(SeatRows));
        Changed(nameof(SeatColumns));
        _isLoadingSeatGrid = false;
        Changed(nameof(SeatGridColumns));
        Changed(nameof(HasSeatMap));
        Changed(nameof(SeatGridSeatCount));
        Changed(nameof(SeatGridNamedCount));
        Changed(nameof(RoomSummary));
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
            return new TeacherSeatCell(cell.Row, cell.Column, cell.SeatNumber, cell.StudentName ?? "",
                cell.VeyonComputerName ?? "", cell.IsSeat,
                cell.VeyonHostAddress is not null || cell.VeyonComputerName is not null,
                seat is not null && _seatSwapSelection.Contains(seat.SeatId),
                cell.VeyonObjectId, cell.VeyonHostAddress ?? "", CanEditSeatMap);
        }).ToArray();
    }

    private IReadOnlyDictionary<string, string> ReadActiveSeatLabels(Guid sessionId)
    {
        if (_activeClassroomSession is not { } active || active.SessionId != sessionId)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var targets = active.Targets.Select(target => target.DeviceLabel).ToArray();
            var layout = _classroomSeatGridStore.GetLayout(active.Room.CampusProfileId, active.Room.RoomId);
            return ClassroomSeatGridStore.GetLocations(layout)
                .Where(location => location.SeatNumber <= targets.Length)
                .ToDictionary(location => targets[location.SeatNumber - 1],
                    location => $"第 {location.Row} 排 · 第 {location.Column} 列", StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (SelectedRoomProfile?.RoomId == active.Room.RoomId)
                SeatMapStatus = "无法读取座位表；原文件保留，课堂消息仍显示电脑编号。";
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
    public string CombinedPolicyProfileName
    {
        get => _combinedPolicyProfileName;
        set { _combinedPolicyProfileName = value ?? ""; Changed(); }
    }
    public string CombinedPolicyProfileStatus
    {
        get => _combinedPolicyProfileStatus;
        private set
        {
            if (_combinedPolicyProfileStatus == value) return;
            _combinedPolicyProfileStatus = value;
            Changed();
            Changed(nameof(HasCombinedPolicyProfileStatus));
        }
    }
    public bool HasCombinedPolicyProfileStatus => !string.IsNullOrWhiteSpace(CombinedPolicyProfileStatus);
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
            Changed(nameof(CampusSetupHint));
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
            Changed(nameof(HasStudentAccountChoices));
            Changed(nameof(StudentAccountSelectionSummary));
            Changed(nameof(SelectedApplicationSummary));
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
            Changed(nameof(HasStudentAccountChoices));
            Changed(nameof(StudentAccountSelectionSummary));
            Changed(nameof(SelectedApplicationSummary));
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
    public string WebsiteBlockDomains { get => _websiteBlockDomains; set { _websiteBlockDomains = value ?? ""; Changed(); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(WebsiteModeGuidance)); } }
    public string WebsiteAllowDomains { get => _websiteAllowDomains; set { _websiteAllowDomains = value ?? ""; Changed(); Changed(nameof(CanPushWebsitePolicy)); } }
    public string WebsiteObservedAddress
    {
        get => _websiteObservedAddress;
        set { _websiteObservedAddress = value ?? ""; Changed(); Changed(nameof(CanAddObservedWebsite)); }
    }
    public string WebsiteEntryStatus { get => _websiteEntryStatus; private set { _websiteEntryStatus = value; Changed(); Changed(nameof(HasWebsiteEntryStatus)); } }
    public bool HasWebsiteEntryStatus => WebsiteEntryStatus.Length > 0;
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
        WebsitePolicyMode.Blocklist => "阻止黑名单中的网站；名单外网站照常访问。",
        WebsitePolicyMode.Allowlist => "阻止其他普通网页，只放行白名单中的网站。",
        WebsitePolicyMode.Combined when string.IsNullOrWhiteSpace(WebsiteBlockDomains) =>
            "综合模式下，白名单只用来放行黑名单中的例外；请先填黑名单，否则白名单不会改变访问范围。",
        WebsitePolicyMode.Combined => "黑名单命中时阻止，白名单作为例外放行；两份名单之外的网站照常访问。",
        _ => "选择网站策略模式。"
    };
    public bool CanImportCommonWebsiteBlacklist => !IsExecuting;
    public bool CanImportCommonWebsiteAllowlist => !IsExecuting;
    public bool CanImportCommonCombinedWebsitePolicy => !IsExecuting;
    public bool CanAddObservedWebsite => !IsExecuting && !string.IsNullOrWhiteSpace(WebsiteObservedAddress);
    public WebsitePolicyMode SelectedWebsiteMode => WebsiteModeIndex switch
    {
        0 => WebsitePolicyMode.Blocklist,
        1 => WebsitePolicyMode.Allowlist,
        _ => WebsitePolicyMode.Combined
    };

    public void ImportCommonWebsiteBlacklist()
    {
        if (!CanImportCommonWebsiteBlacklist) return;
        try
        {
            var merged = MergeWebsiteEntries(WebsiteBlockDomains, CommonWebsiteBlacklistDomains);
            if (WebsiteModeIndex != 2) WebsiteModeIndex = 0;
            WebsiteBlockDomains = merged;
            CompleteWebsiteEditorUpdate("已填入常见黑名单；检查名单后再应用。");
        }
        catch (InvalidDataException exception) { WebsitePolicyError = exception.Message; }
    }

    public void ImportCommonWebsiteAllowlist()
    {
        if (!CanImportCommonWebsiteAllowlist) return;
        try
        {
            var merged = MergeWebsiteEntries(WebsiteAllowDomains, CommonWebsiteAllowlistDomains);
            if (WebsiteModeIndex != 2) WebsiteModeIndex = 1;
            WebsiteAllowDomains = merged;
            CompleteWebsiteEditorUpdate("已填入常见学习网站白名单；检查名单后再应用。");
        }
        catch (InvalidDataException exception) { WebsitePolicyError = exception.Message; }
    }

    public void ImportCommonCombinedWebsitePolicy()
    {
        if (!CanImportCommonCombinedWebsitePolicy) return;
        try
        {
            var blacklist = MergeWebsiteEntries(WebsiteBlockDomains, CommonWebsiteBlacklistDomains);
            var allowlist = MergeWebsiteEntries(WebsiteAllowDomains, CommonWebsiteAllowlistDomains);
            WebsiteModeIndex = 2;
            WebsiteBlockDomains = blacklist;
            WebsiteAllowDomains = allowlist;
            CompleteWebsiteEditorUpdate("已填入综合预设（黑名单 + 白名单例外）；检查名单后再应用。");
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
            WebsiteModeIndex = 2;
            if (toBlacklist) WebsiteBlockDomains = updated;
            else WebsiteAllowDomains = updated;
            WebsiteObservedAddress = "";
            WebsiteEntryStatus = $"已加入综合策略{(toBlacklist ? "黑名单" : "白名单例外")}：{domain}。规则会匹配该域名及其子域名。";
            ResetWebsitePolicyEditorFeedback();
        }
        catch (InvalidDataException exception) { WebsitePolicyError = "网址未加入名单：" + exception.Message; }
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
                throw new InvalidDataException($"每份网站名单最多 {WebsitePolicyCompiler.MaximumEntries} 个域名；没有更改名单。");
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
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanOpenVeyonConfigurator));
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
            Changed(nameof(CanApplyWebsiteLocation));
            Changed(nameof(CanUseSelectedClassroomLocation));
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
        }
    }
    public IReadOnlyList<string> ClassroomLocationChoices => WebsiteLocations.Select(location => location.DisplayName).ToArray();
    public bool HasMultipleClassroomLocations => _classroomDirectorySelectionPending && WebsiteLocations.Count > 1;
    public string ClassroomDirectoryStatus
    {
        get => _classroomDirectoryStatus;
        private set { _classroomDirectoryStatus = value; Changed(); Changed(nameof(HasClassroomDirectoryStatus)); }
    }
    public bool HasClassroomDirectoryStatus => ClassroomDirectoryStatus.Length > 0;
    public string ClassroomDirectoryError
    {
        get => _classroomDirectoryError;
        private set { _classroomDirectoryError = value; Changed(); Changed(nameof(HasClassroomDirectoryError)); }
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
            return $"已选 {selected.Length} 个账户，覆盖 {coveredTargets}/{targetCount} 台目标电脑。每台电脑至少保留一个账户；手机预设会按电脑分别保存。";
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
            Changed(nameof(CanOpenVeyonConfigurator));
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
        if (page is not ("classroom" or "lesson" or "updates" or "rooms" or "setup") || _selectedPage == page) return;
        _selectedPage = page;
        Changed(nameof(IsClassroomPage)); Changed(nameof(IsClassroomExperiencePage));
        Changed(nameof(IsClassroomWorkspacePage)); Changed(nameof(IsUpdatesPage)); Changed(nameof(IsRoomPage)); Changed(nameof(IsSetupPage));
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
        CampusDirectoryStatus = "填写机房名称和电脑范围；保存后可重复使用。";
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
            var room = new TeacherRoomProfile(selectedId ?? Guid.NewGuid(), RoomProfileName.Trim(),
                RoomProfilePrefix, int.Parse(RoomProfileStart, CultureInfo.InvariantCulture), range.Count,
                hostOverrides.ToList());
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
        var campuses = CampusProfiles.Select(item => item.ProfileId == campus.ProfileId
            ? item with { Rooms = item.Rooms.Where(room => room.RoomId != selected.RoomId).ToList() }
            : item).ToArray();
        if (PersistCampusDirectory(campuses, "机房模板已删除。"))
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
        RoomComputerHosts = string.Join(Environment.NewLine, room.HostOverrides ?? []);
        CampusDirectoryStatus = "已载入地点设置。电脑数量将按座位表自动生成；尚未修改 Veyon。";
        CampusDirectoryError = "";
    }

    public async Task AddRoomToVeyonAsync()
    {
        if (!CanAddRoomToVeyon) return;
        if (RoomError.Length > 0 || !HasRoomPreview)
        {
            RoomCreateError = RoomError.Length > 0 ? RoomError : "请先在座位图中添加座位并检查电脑清单。";
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
            RoomCreateStatus = "正在写入地点和电脑清单并读回核对……";
            var response = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.TeacherConsole,
                (requestId, caller) => new PrivilegedWorkerRequest(
                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                    PrivilegedWorkerOperation.AddVeyonRoom,
                    LocationName: locationName, Computers: computers));
            if (response.Result.Ok)
            {
                if (!TrySaveCurrentRoomProfile(out var profileError, computers.Length))
                {
                    RoomCreateError = "Veyon 地点和电脑已写入并读回，但本机地点记录未保存；请重新打开本机权限后再试。" + profileError;
                    return;
                }
                var bindingError = await TryBindRoomComputersToSeatGridAsync(locationName, computers);
                RoomCreateResult = response.Result.Detail +
                    "\n此结果只确认本机静态目录写入及读回，不代表电脑在线或 Veyon 连接成功。";
                if (bindingError is null)
                    RoomCreateResult += $"\n已按座位顺序自动关联 {computers.Length} 台电脑；已关联座位可用于手机屏幕巡视。";
                else
                {
                    SeatVeyonTargetStatus = "自动关联未完成；本次没有应用部分关联。请读取 Veyon 电脑后手动关联。";
                    RoomCreateError = "Veyon 电脑已成功写入，但没有自动关联任何座位：" + bindingError;
                }
            }
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

    private async Task<string?> TryBindRoomComputersToSeatGridAsync(string locationName,
        IReadOnlyList<VeyonNetworkComputer> computers)
    {
        if (SelectedCampusProfile is not { } campus || SelectedRoomProfile is not { } room ||
            _seatGrid is null || room.ComputerCount != computers.Count ||
            !string.Equals(room.DisplayName, locationName, StringComparison.OrdinalIgnoreCase))
            return "本机地点或座位图与刚写入的地点不一致。";

        var campusId = campus.ProfileId;
        var roomId = room.RoomId;
        var seats = _seatGrid.Seats.OrderBy(seat => seat.Row).ThenBy(seat => seat.Column).ToArray();
        if (seats.Length != computers.Count)
            return $"当前有 {seats.Length} 个座位，电脑清单有 {computers.Count} 台。";

        VeyonDirectorySnapshot snapshot;
        try { snapshot = await Task.Run(VeyonNetworkObjectDirectory.ReadDirectorySnapshot); }
        catch (Exception exception)
        {
            return "无法重新读取 Veyon 电脑目录：" + exception.Message;
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
            SeatVeyonTargetStatus = $"已按座位顺序自动关联 {bindings.Count} 台 Veyon 电脑。";
            RefreshClassroomEventSeatLocations();
            return null;
        }
        catch (Exception exception)
        {
            return "本机座位关联未保存：" + exception.Message;
        }
    }

    public void OpenVeyonConfigurator()
    {
        RoomCreateError = "";
        ConfiguratorLaunchError = "";
        if (IsExecuting || IsCheckingRoomConflicts || IsReadingWebsiteLocations)
        {
            SetConfiguratorLaunchError("Veyon 正在由其他操作读取或更新；请等该操作完成后再打开设置。");
            return;
        }
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Veyon", "veyon-configurator.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Veyon", "veyon-configurator.exe")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            SetConfiguratorLaunchError("找不到 Veyon Configurator；请先安装固定版本 Veyon。");
            return;
        }
        try { ProcessRunner.LaunchVeyonConfigurator(path); }
        catch (Exception exception)
        {
            SetConfiguratorLaunchError("无法打开 Veyon Configurator：" + DescribeWindowsLaunchError(exception));
        }
    }

    private void SetConfiguratorLaunchError(string message)
    {
        ConfiguratorLaunchError = message;
        RoomCreateError = message;
    }

    private static string DescribeWindowsLaunchError(Exception exception)
    {
        if (exception is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 } ||
            exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 })
            return "Veyon Configurator 的程序文件正被其他进程占用；请关闭已有的 Veyon 设置窗口或安装程序，等 Veyon 操作结束后再重试。";
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
            ClassroomDirectoryStatus = $"Veyon 中有 {locations.Count} 个地点；请选择学生电脑范围。";
            Changed(nameof(HasMultipleClassroomLocations));
            Changed(nameof(CanUseSelectedClassroomLocation));
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
            ClassroomDirectoryStatus = $"已选中 {location.Name} · {location.Targets.Count} 台电脑。名单仅保存在本机，未改动 Veyon。";
            Changed(nameof(HasMultipleClassroomLocations));
            Changed(nameof(CanUseSelectedClassroomLocation));
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
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
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
            var displayNamesTask = ResolveTargetDisplayNamesAsync(targets);
            var results = await SendByTargetAsync(targets, t => ApplicationPolicyTransport.PushAsync([t], signedByTarget[t], campus));
            var displayNames = await displayNamesTask;
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
                $"{GetTargetDisplayName(result.Target, displayNames)}：{(result.Succeeded ? "已确认" : result.NeedsReview ? "需核对" : "失败")} — {result.Detail}"));
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

        foreach (var room in RoomProfiles)
        {
            IReadOnlyList<string> computerNames;
            try
            {
                computerNames = MachineNaming.CreateRange(room.Prefix,
                    room.StartNumber.ToString(CultureInfo.InvariantCulture),
                    room.ComputerCount.ToString(CultureInfo.InvariantCulture));
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

    private static string GetTargetDisplayName(string target,
        IReadOnlyDictionary<string, string> displayNames) =>
        displayNames.TryGetValue(NormalizeTargetKey(target), out var displayName) &&
        !string.IsNullOrWhiteSpace(displayName) ? displayName : target;

    private static string NormalizeTargetKey(string target)
    {
        try { return VeyonHostAddress.NormalizeOverride(target).ToLowerInvariant(); }
        catch (InvalidDataException) { return target.Trim().ToLowerInvariant(); }
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
                throw new PlatformNotSupportedException("请在 Windows 教师端设置学生电脑限制。");
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
            var displayNamesTask = ResolveTargetDisplayNamesAsync(targets);
            var results = await SendByTargetAsync(targets, t => StudentSystemPolicyTransport.PushAsync([t], signedByTarget[t], campus));
            var displayNames = await displayNamesTask;
            var succeeded = results.Count(result => result.Succeeded);
            var partiallyApplied = results.Count(result => result.Succeeded && result.Partial);
            var fullyApplied = succeeded - partiallyApplied;
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count(result => !result.Succeeded && !result.NeedsReview);
            var mode = disabled
                ? (succeeded == results.Count ? "系统限制已解除" : "解除结果")
                : partiallyApplied > 0 ? "系统限制部分应用"
                : (succeeded == results.Count ? "系统限制已应用" : "系统限制应用结果");
            StudentSystemPolicyResult = partiallyApplied > 0
                ? $"{mode} · 完整 {fullyApplied}/{results.Count} · 部分 {partiallyApplied} · 需核对 {needsReview} · 失败 {failed}"
                : $"{mode} · 成功 {succeeded}/{results.Count} · 需核对 {needsReview} · 失败 {failed}";
            StudentSystemPolicyDetails = string.Join(Environment.NewLine, results.Select(result =>
                $"{GetTargetDisplayName(result.Target, displayNames)}：{(result.Partial ? "部分应用" : result.Succeeded ? "已确认" : result.NeedsReview ? "需核对" : "失败")} — {result.Detail}"));
            if (succeeded != results.Count)
                StudentSystemPolicyError = "部分电脑尚未确认限制已生效；请查看结果后重试。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or PlatformNotSupportedException)
        {
            StudentSystemPolicyError = "系统限制未应用：" + exception.Message;
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
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            using var signingKey = ApplicationPolicySigningKeyStore.Open(campus);
            var displayNamesTask = ResolveTargetDisplayNamesAsync(targets);
            var results = await ApplicationPolicyTransport.ReadInventoryAsync(targets, campus, signingKey.PrivateKey);
            var displayNames = await displayNamesTask;
            if (campus != CampusId.Trim() || !targets.SequenceEqual(WebsitePolicyTransport.NormalizeTargets(
                    WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)), StringComparer.OrdinalIgnoreCase))
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
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            using var signingKey = ApplicationPolicySigningKeyStore.Open(campus);
            var displayNamesTask = ResolveTargetDisplayNamesAsync(targets);
            var results = await ApplicationPolicyTransport.ReadAuditAsync(targets, campus, signingKey.PrivateKey, 24);
            var displayNames = await displayNamesTask;
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
                    lines.Add($"{GetTargetDisplayName(result.Target, displayNames)}：{(result.NeedsReview ? "需核对" : "失败")} — {result.Detail}");
                    continue;
                }
                var response = result.Response;
                var wouldBlock = response.Results.Sum(item => item.WouldBlockCount);
                var blocked = response.Results.Sum(item => item.BlockedCount);
                lines.Add(response.IsSimulation
                    ? $"{GetTargetDisplayName(result.Target, displayNames)}：影响检查，预计阻止 {wouldBlock} 次启动（未实际拦截）"
                    : $"{GetTargetDisplayName(result.Target, displayNames)}：匹配 {wouldBlock} 次，已阻止 {blocked} 次启动");
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

    public MobilePolicyProfile SaveWebsiteApplicationMobileProfile(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("组合策略预设仅能从 Windows 教师端保存。");
        var campus = CampusId.Trim();
        WebsitePolicySigningKeyStore.ValidateCampusId(campus);
        var targets = WebsitePolicyTransport.NormalizeTargets(
            WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var lifetimeMinutes = ApplicationPolicyDurationIndex switch
        {
            0 => 45,
            1 => 60,
            2 => 90,
            3 => 120,
            4 => 1440,
            _ => throw new InvalidDataException("组合预设期限无效。")
        };
        var studentSidsByTarget = MobileStudentSidsByTarget(targets);
        var existing = MobilePolicyProfileStore.ReadAll()
            .FirstOrDefault(profile => profile.Kind == MobilePolicyProfileKind.WebsiteApplication &&
                                       profile.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                       profile.CampusId == campus);
        var profile = MobilePolicyProfileCompiler.Validate(new MobilePolicyProfile(
            existing?.Id ?? Guid.NewGuid(), name, campus, MobilePolicyProfileKind.WebsiteApplication,
            lifetimeMinutes, WebsiteMode: SelectedWebsiteMode,
            WebsiteDomains: (SelectedWebsiteMode == WebsitePolicyMode.Allowlist ? WebsiteAllowDomains : WebsiteBlockDomains)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            WebsiteAllowDomains: SelectedWebsiteMode == WebsitePolicyMode.Combined
                ? WebsiteAllowDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : null,
            ApplicationMode: SelectedApplicationPolicyMode, ApplicationRules: ParseApplicationRules(),
            UpdatedUtc: DateTimeOffset.UtcNow, StudentSidsByTarget: studentSidsByTarget));
        MobilePolicyProfileStore.Save(profile);
        return profile;
    }

    public void SaveCurrentWebsiteApplicationProfile()
    {
        try
        {
            var profile = SaveWebsiteApplicationMobileProfile(CombinedPolicyProfileName);
            CombinedPolicyProfileStatus = $"已保存“{profile.Name}”；手机端和教师端学生互动页可使用。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        { CombinedPolicyProfileStatus = "未保存组合策略：" + exception.Message; }
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
            StudentUpdateStatus = "学生端更新仅支持 Windows。";
            return;
        }
        if (!CanDeployStudentUpdate || !TryBeginExclusiveTask()) return;
        ClearStudentUpdateResults();
        StudentUpdateStatus = "正在检查学生端更新……";
        SetStudentUpdateProgress(true, true, 0, "正在查询签名发布信息……");
        string? targetVersion = null;
        try
        {
            var releaseClient = _releaseClient ?? throw new InvalidOperationException(
                _releaseClientError ?? "此版本暂不支持学生端更新。");
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(
                WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var latest = await releaseClient.CheckLatestAsync(ApplicationReleaseRole.StudentSetup, "0.0.0");
            var release = latest.Release ?? throw new InvalidOperationException("暂时没有可用的学生端更新。");
            targetVersion = release.Manifest.Version;
            ApplicationReleaseCompatibility.EnsureSupports(release.Manifest,
                applicationPolicyRequired: true, studentSystemPolicyRequired: true);
            var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "Updates");
            StudentUpdateStatus = $"正在准备学生端更新 {release.Manifest.Version}（{targets.Count} 台目标电脑）……";
            SetStudentUpdateProgress(true, false, 0,
                $"正在从 GitCode 下载并校验 StudentSetup（0 / {FormatMegabytes(release.Manifest.SizeBytes)} MB）");
            var installerPath = await releaseClient.DownloadAsync(release, ApplicationReleaseRole.StudentSetup,
                updateDirectory, progress: new Progress<InstallerResourceProgress>(value =>
                    SetStudentResourceProgress(value)));
            SetStudentUpdateProgress(true, true, 100,
                "下载完成并通过 SHA-256 校验，正在启动局域网分发……");
            var lanProgress = new Progress<StudentApplicationUpdateLanProgress>(value =>
            {
                var percent = value.TotalBytes > 0
                    ? Math.Clamp(value.BytesSent * 100d / value.TotalBytes, 0d, 100d)
                    : 0d;
                SetStudentUpdateProgress(true, false, percent,
                    $"正在通过局域网发送给 {value.Target}：{FormatMegabytes(value.BytesSent)} / " +
                    $"{FormatMegabytes(value.TotalBytes)} MB · {percent:0}%");
            });
            await using var lanServer = StudentApplicationUpdateLanServer.Start(installerPath, lanProgress);
            SetStudentUpdateProgress(true, false, 0,
                $"正在联系学生 Agent（0 / {targets.Count} 台完成）……");
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var developerPublicKey = ApplicationReleaseTrust.LoadPinnedPublicKeyPem();
            var transportProgress = new Progress<StudentApplicationUpdateProgress>(value =>
            {
                var percent = value.TotalTargets > 0
                    ? value.CompletedTargets * 100d / value.TotalTargets
                    : 100d;
                var target = value.Target.Length > 0 ? $"：{value.Target}" : "";
                var waitingForAgent = value.Stage.Contains("安装并重启", StringComparison.Ordinal) && percent < 100d;
                SetStudentUpdateProgress(true, waitingForAgent, percent,
                    $"{value.Stage}{target}（已完成 {value.CompletedTargets} / {value.TotalTargets} 台）");
            });
            var deliveries = await StudentApplicationUpdateTransport.PushAsync(targets, campus,
                release.Manifest.Version, signingKey.PrivateKey, async (target, token) =>
            {
                var downloadUri = await lanServer.GetDownloadUriAsync(target, token);
                var issuedUtc = DateTimeOffset.UtcNow;
                var command = new StudentApplicationUpdateCommand(1, campus, Guid.NewGuid(), issuedUtc,
                    issuedUtc + StudentApplicationUpdateCryptography.MaximumCommandLifetime, release,
                    downloadUri.AbsoluteUri);
                lock (signingKey.PrivateKey)
                    return (command.CommandId, StudentApplicationUpdateCryptography.Sign(command,
                        signingKey.PrivateKey, releaseClient.ApiBaseAddress, developerPublicKey));
            }, progress: transportProgress);
            var succeeded = deliveries.Count(result => result.Succeeded);
            var needsReview = deliveries.Count(result => result.NeedsReview);
            var failed = deliveries.Count - succeeded - needsReview;
            RecordUpdateSuccess(UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.StudentRollout,
                release.Manifest.Version, new UpdateDiagnosticCounts(succeeded, needsReview, failed));
            StudentUpdateStatus = $"学生端更新 {release.Manifest.Version}：成功 {succeeded} 台，待核对 {needsReview} 台，失败 {failed} 台。";
            StudentUpdateSucceededDetails = FormatStudentUpdateResults(deliveries.Where(result => result.Succeeded));
            StudentUpdateNeedsReviewDetails = FormatStudentUpdateResults(deliveries.Where(result => result.NeedsReview));
            StudentUpdateFailedDetails = FormatStudentUpdateResults(deliveries.Where(result =>
                !result.Succeeded && !result.NeedsReview));
        }
        catch (Exception exception)
        {
            var failure = RecordUpdateFailure(UpdateDiagnosticModule.TeacherConsole,
                UpdateDiagnosticOperation.StudentRollout, targetVersion, exception);
            StudentUpdateStatus = "学生端更新未完成，请查看结果后重试：" + failure.ToUserMessage();
            StudentUpdateFailedDetails = "更新准备或分发阶段：" + failure.ToUserMessage();
            SetStudentUpdateProgress(true, false, _studentUpdateProgressPercent,
                "更新未完成；请查看下方失败结果，修复后可重试。");
        }
        finally
        {
            if (StudentUpdateFailedDetails.Length == 0) SetStudentUpdateProgress(false, true, 0, "");
            EndExclusiveTask();
        }
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

    private static string FormatStudentUpdateResults(
        IEnumerable<StudentApplicationUpdatePushResult> results) =>
        string.Join(Environment.NewLine, results.Select(result =>
            string.IsNullOrWhiteSpace(result.Detail) ? result.Target : $"{result.Target}：{result.Detail}"));

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
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
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
            var lifetimeMinutes = GetWebsiteLifetimeMinutes();
            TimeSpan? duration = mode == WebsitePolicyMode.Disabled || lifetimeMinutes == 0
                ? null : TimeSpan.FromMinutes(lifetimeMinutes);
            DateTimeOffset? expiresUtc = duration is { } lifetime ? issuedUtc + lifetime : null;
            var revision = WebsitePolicyRevisionStore.Next(campus);
            var policy = WebsitePolicyCompiler.Create(campus, revision, mode, domains, issuedUtc, expiresUtc,
                allowedDomains);
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var signedPolicy = WebsitePolicyCryptography.Sign(policy, signingKey.PrivateKey);
            var displayNamesTask = ResolveTargetDisplayNamesAsync(targets);
            var results = await WebsitePolicyTransport.PushAsync(targets, signedPolicy, campus);
            var displayNames = await displayNamesTask;
            var succeeded = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count(result => !result.Succeeded && !result.NeedsReview);
            var expirySummary = expiresUtc is { } expiry
                ? $" · 自动解除 {expiry.ToLocalTime():yyyy-MM-dd HH:mm}"
                : mode == WebsitePolicyMode.Disabled ? "" : " · 需手动解除";
            var resultLabel = mode switch
            {
                WebsitePolicyMode.Disabled => succeeded == results.Count ? "学生代理限制已解除" : "解除结果",
                WebsitePolicyMode.Blocklist => succeeded == results.Count ? "黑名单已应用" : "黑名单应用结果",
                WebsitePolicyMode.Combined => succeeded == results.Count ? "综合黑白名单已应用" : "综合黑白名单应用结果",
                _ => succeeded == results.Count ? "白名单已应用" : "白名单应用结果"
            };
            var hasBrowserRestartRequest = results.Any(result => result.Succeeded &&
                result.Detail.Contains("Agent 已发布 Edge/Chrome 优雅重启请求", StringComparison.Ordinal));
            var hasLegacyAgent = results.Any(result => result.Succeeded &&
                !result.Detail.Contains("Agent 已发布 Edge/Chrome 优雅重启请求", StringComparison.Ordinal));
            var restartNotice = succeeded == 0 ? "" : hasBrowserRestartRequest && hasLegacyAgent
                ? " · 新 Agent 已向 Companion 请求 Edge/Chrome 重启；其他 Agent 请手动重启"
                : hasBrowserRestartRequest ? " · 已向 Companion 请求 Edge/Chrome 重启；若未重启请手动处理，Firefox 需手动重启"
                : " · 请手动重启浏览器后生效";
            WebsitePolicyResult = $"{resultLabel}{expirySummary} · 已确认 {succeeded}/{results.Count} · 待核对 {needsReview} · 失败 {failed}{restartNotice}";
            WebsitePolicyResultDetails = string.Join(Environment.NewLine,
                results.Select(result => $"{GetTargetDisplayName(result.Target, displayNames)}：{(result.StatusLabel)} — {result.Detail}"));
            ShowWebsitePolicyResultDetails = needsReview > 0 || failed > 0;
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
            if (WebsiteDurationIndex == 4 && WebsiteCustomDurationMinutes is < 10 or > 300) return false;
            WebsitePolicySigningKeyStore.ValidateCampusId(CampusId.Trim());
            WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
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
        catch (Exception exception) when (exception is InvalidDataException or PlatformNotSupportedException) { return false; }
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
            targets = WebsitePolicyTransport.NormalizeTargets(
                WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
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
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
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
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
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
            WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
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
            var targets = WebsitePolicyTransport.NormalizeTargets(
                WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
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
            StudentAgentIdentityDetails = string.Join(Environment.NewLine, results.Select(result =>
                result.Candidate is { } candidate
                    ? $"{result.Target}：{(result.MatchesPinnedKey ? "身份已确认" : "待确认")}" +
                      (candidate.PreviouslyPinnedFingerprint is { } old && old != candidate.Fingerprint
                          ? $"；原指纹 {old}，新指纹 {candidate.Fingerprint}"
                          : $"；指纹 {candidate.Fingerprint}")
                    : $"{result.Target}：无法读取 — {result.Detail}"));
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
        StudentAgentIdentityDetails = string.Join(Environment.NewLine, results.Select(result =>
            result.Candidate is { } candidate
                ? candidate.PreviouslyPinnedFingerprint is { } previous &&
                  !string.Equals(previous, candidate.Fingerprint, StringComparison.OrdinalIgnoreCase)
                    ? $"{result.Target}：身份已轮换；原指纹 {previous}，新指纹 {candidate.Fingerprint}"
                    : $"{result.Target}：身份已确认；指纹 {candidate.Fingerprint}"
                : $"{result.Target}：无法读取 — {result.Detail}"));
        ShowStudentAgentIdentityDetails = unverifiable > 0;
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
            else TeacherInstallIssue = "Veyon 安装完成，但状态未能确认。请打开 Veyon Configurator 检查。";
            InstallerStatus = repairMissingUninstaller ? "Veyon 修复完成。" : "Veyon 安装完成。";
        }
        catch (Exception exception) { TeacherInstallIssue = "安装未完成：" + DescribeWindowsLaunchError(exception); }
        finally { IsInstallingTeacherVeyon = false; EndExclusiveTask(); }
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
            var students = GetStudentNamesForCurrentRoom();
            if (students.Count == 0)
                throw new InvalidDataException("请先创建地点并在座位图中添加座位。");
            var names = MachineNaming.CreateRange(RoomPrefix, RoomStart,
                students.Count.ToString(CultureInfo.InvariantCulture));
            var hosts = ParseHostOverrides(RoomComputerHosts, names.Count);
            RoomNames = names;
            RoomPreviewRows = names.Select((name, index) =>
            {
                var student = students[index];
                var host = index < hosts.Count && hosts[index].Length > 0 ? hosts[index] : name;
                var displayName = student.Length == 0 ? "（未填写显示名）" : $"显示名：{student}";
                return $"{name}  →  {host}    {displayName}";
            }).ToArray();
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
            .Select(seat => seat.StudentName).ToArray();
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

    private VeyonNetworkComputer[] BuildRoomImportPlan()
    {
        var students = GetStudentNamesForCurrentRoom();
        if (students.Count == 0)
            throw new InvalidDataException("请先在座位图中添加座位。");
        var names = MachineNaming.CreateRange(RoomPrefix, RoomStart,
            students.Count.ToString(CultureInfo.InvariantCulture));
        var hosts = ParseHostOverrides(RoomComputerHosts, names.Count);
        return names.Select((name, index) =>
        {
            var host = index < hosts.Count && hosts[index].Length > 0 ? hosts[index] : name;
            var student = students[index];
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
            CampusDirectoryError = "保存本机地点清单失败；原文件未更改。" + exception.Message;
            return false;
        }
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record TeacherSeatCell(int Row, int Column, int? SeatNumber, string StudentName,
    string ComputerLabel, bool IsSeat, bool HasVeyonTarget, bool IsSwapSelected = false,
    Guid? VeyonObjectId = null, string VeyonHostAddress = "", bool CanEdit = true)
{
    public bool IsAisle => !IsSeat;
    public string PrimaryLabel => IsSeat
        ? $"座位 {SeatNumber} · {(!string.IsNullOrWhiteSpace(StudentName) ? StudentName : "未填写姓名")}"
        : "＋ 添加座位";
    public string SecondaryLabel => IsSeat
        ? (HasVeyonTarget ? "电脑：" + ComputerLabel : "尚未关联电脑")
        : $"第 {Row} 行 · 第 {Column} 列 · 过道";
}

public sealed record TeacherVeyonTargetChoice(Guid ObjectId, Guid LocationObjectId,
    string ComputerName, string HostAddress, string Location)
{
    public override string ToString() => $"{ComputerName}（{Location}）";
}

public sealed record TeacherClassroomTaskItem(Guid TaskId, string Title, bool IsCompleted);

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
        Event.Target == ClassroomEventCryptography.ClassroomNoticeTarget ? "全班通知" : $"学生通知 · {Event.Target}";
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
