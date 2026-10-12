Warning: truncated output (original token count: 61159)
Total output lines: 4720

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
        _studentSystemPolicySoftwareInstallReviewed;
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
    private string _roomPrefix = "PC-", _roomStart = "1", _roomCount = "150", _roomError = "";
    private string _roomLocationName = "", _studentRoster = "", _roomComputerHosts = "";
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
    private string _websiteTargets = "", _websiteDomains = "", _websitePolicyResult = "", _websitePolicyResultDetails = "", _websitePolicyError = "", _websitePolicyHistoryText = "";
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
    private string _offlineTeacherUpdateStatus = "安装器和配套清单需放在一起。";
    private string _studentUpdateStatus = "尚未向学生电脑发送更新。";
    private string _studentUpdateDetails = "";
    private bool _showStudentUpdateDetails;
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
        ClassroomCountdownStore? classroomCountdownStore = null,
        ClassroomTaskProgressStore? classroomTaskProgressStore = null)
    {
        _installerStore = installerStore ?? new VeyonInstallerStore();
        _campusDirectoryStore = campusDirectoryStore ?? new TeacherCampusDirectoryStore();
        _classroomSessionStore = classroomSessionStore ?? new ClassroomSessionStore();
        _classroomSeatLayoutStore = classroomSeatLayoutStore ?? new ClassroomSeatLayoutStore();
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

    public bool IsExecuting { get => _isExecuting; private set { _isExecuting = value; Changed(); Changed(nameof(CanInstallTeacherVeyon)); Changed(nameof(CanGenerateStudentPackage)); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(CanDisableWebsitePolicy)); Changed(nameof(CanPushApplicationPolicy)); Changed(nameof(CanDisableApplicationPolicy)); Changed(nameof(CanPushStudentSystemPolicy)); Changed(nameof(StudentSystemPolicyPushGuidance)); Changed(nameof(CanDisableStudentSystemPolicy)); Changed(nameof(CanReadApplicationPolicyAudit)); Changed(nameof(CanReadApplicationInventory)); Changed(nameof(CanAddSelectedApplicationRules)); Changed(nameof(CanFillFailedWebsiteTargets)); Changed(nameof(CanReadWebsiteLocations)); Changed(nameof(CanApplyWebsiteLocation)); Changed(nameof(CanUseSelectedClassroomLocation)); Changed(nameof(CanReplaceWebsiteSigningKey)); Changed(nameof(CanAddRoomToVeyon)); Changed(nameof(CanCheckRoomConflicts)); Changed(nameof(CanPublishStudentPackage)); Changed(nameof(CanCheckTeacherUpdate)); Changed(nameof(CanDownloadTeacherUpdate)); Changed(nameof(CanExportOfflineTeacherUpdate)); Changed(nameof(CanVerifyOfflineTeacherUpdate)); Changed(nameof(CanInstallOfflineTeacherUpdate)); Changed(nameof(CanTrustStudentAgentIdentities)); Changed(nameof(CanDeployStudentUpdate)); Changed(nameof(CanToggleClassroomSession)); Changed(nameof(CanChangeClassroomMode)); Changed(nameof(CanRetryClassroomRestores)); } }
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
            ? $"{room.ComputerCount} 台学生电脑可用于互动"
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
        ? $"学生互动 · {PendingClassroomHelpCount} 条待回复"
        : "学生互动";
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
        if (!CanEndClassroomCountdown |…46159 tokens truncated…AgentIdentityStatus));
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
            StudentAgentIdentityStatus = $"已读取 {countWithIdentity}/{results.Count} 台 · 待确认 {needsTrust} · 无法读取 {unverifiable}";
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
            if (installed.Status == "installed")
            {
                TeacherInstallIssue = "本机已安装 Veyon，已停止重复安装。";
                return;
            }
            if (installed.Status != VeyonFacts.NotInstalled)
            {
                TeacherInstallIssue = "无法确认 Veyon 状态，已停止安装。";
                return;
            }
            InstallerStatus = "正在准备 Veyon 安装文件……";
            var acquired = await AcquireInstallerWithProgressAsync();
            if (!acquired.Trust.IsAllowed || !acquired.Trust.AuthenticodeVerified)
            {
                TeacherInstallIssue = "安装文件验证失败，未运行安装程序。";
                return;
            }
            InstallerStatus = "正在安装教师端 Veyon……";
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
                TeacherInstallResult = "Veyon 已安装并完成认证设置。请重启 Veyon Master，确认连接后再生成学生配置包。";
            }
            else TeacherInstallIssue = "Veyon 安装完成，但状态未能确认。请打开 Veyon Configurator 检查。";
            InstallerStatus = "Veyon 安装完成。";
        }
        catch (Exception exception) { TeacherInstallIssue = "安装未完成：" + DescribeWindowsLaunchError(exception); }
        finally { EndExclusiveTask(); }
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
            CampusDirectoryError = "无法读取已保存的校区；原文件未更改。";
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
            CampusDirectoryError = "保存校区模板失败；原文件未更改。" + exception.Message;
            return false;
        }
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record TeacherSeatCell(int Index, string Target, string PositionLabel, bool IsSelected);

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
        Event.Target == ClassroomEventCryptography.ClassroomNoticeTarget ? "学生通知" : $"学生通知 · {Event.Target}";
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
