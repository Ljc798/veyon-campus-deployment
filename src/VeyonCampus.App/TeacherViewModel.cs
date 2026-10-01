using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class TeacherViewModel : INotifyPropertyChanged
{
    private readonly VeyonInstallerStore _installerStore;
    private readonly DeploymentPackagePublishingClient _packagePublisher;
    private readonly ApplicationReleaseClient? _releaseClient;
    private readonly string? _releaseClientError;
    private readonly TeacherCampusHeartbeatClient? _teacherHeartbeatClient;
    private readonly string? _teacherHeartbeatClientError;
    private readonly ITaskLease _lease;
    private IReadOnlyList<string> _roomNames = Array.Empty<string>();
    private IReadOnlyList<string> _roomPreviewRows = Array.Empty<string>();
    private IReadOnlyList<VeyonNetworkLocation> _websiteLocations = Array.Empty<VeyonNetworkLocation>();
    private IReadOnlyList<string> _lastFailedWebsiteTargets = Array.Empty<string>();
    private bool _isExecuting, _isReadingWebsiteLocations, _websiteLocationSelectionPending;
    private bool _canReplaceWebsiteSigningKey;
    private bool _enableAnonymousTelemetry;
    private string _roomPrefix = "PC-", _roomStart = "1", _roomCount = "150", _roomError = "";
    private string _roomLocationName = "", _studentRoster = "", _roomCreateResult = "", _roomCreateError = "", _roomCreateStatus = "";
    private string _configuratorLaunchError = "";
    private string _campusId = "", _roomOutputDir = "", _packageOutput = "", _packageOutputError = "";
    private string _publishPackageDirectory = "", _publishCampusName = "", _publisherName = "", _teacherPhoneLast4 = "";
    private string _packagePublisherStatus = "", _packagePublisherError = "", _packagePublishResult = "";
    private string _websiteTargets = "", _websiteDomains = "", _websitePolicyResult = "", _websitePolicyError = "", _websitePolicyHistoryText = "";
    private string _websiteDirectoryStatus = "", _websiteDirectoryError = "";
    private string _installerStatus = "Veyon 安装器已内嵌在 App 中；无需联网下载。", _teacherInstallResult = "", _teacherInstallIssue = "";
    private string _teacherUpdateStatus = "尚未检查教师控制台更新。";
    private string _studentUpdateStatus = "尚未向学生电脑发送更新。";
    private string _teacherHeartbeatStatus = "尚未启用校区每日心跳。";
    private TeacherCampusHeartbeatState? _teacherHeartbeatState;
    private ApplicationReleaseEnvelope? _teacherUpdateRelease;
    private bool _teacherUpdateAvailable;
    private bool _isCheckingTeacherUpdate, _isDownloadingTeacherUpdate;
    private int _teacherHeartbeatInFlight;
    private int _websiteModeIndex = 0, _websiteDurationIndex = 1, _websiteLocationIndex = -1;
    private string _selectedPage = "classroom";

    public TeacherViewModel(VeyonInstallerStore? installerStore = null)
    {
        _installerStore = installerStore ?? new VeyonInstallerStore();
        _packagePublisher = new DeploymentPackagePublishingClient();
        try { _releaseClient = new ApplicationReleaseClient(); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            _releaseClientError = exception.Message;
            _studentUpdateStatus = exception.Message;
        }
        try { _teacherHeartbeatClient = new TeacherCampusHeartbeatClient(); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            _teacherHeartbeatClientError = exception.Message;
        }
        if (File.Exists(TeacherCampusHeartbeatStateStore.DefaultPath))
        {
            try { _teacherHeartbeatState = TeacherCampusHeartbeatStateStore.LoadOrCreate(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _teacherHeartbeatStatus = "无法读取本机 Teacher 心跳设置：" + exception.Message;
            }
        }
        _lease = OperatingSystem.IsWindows() ? new NamedPipeTaskLease() : new TaskLease();
        LoadLatestWebsitePolicyHistory();
        if (OperatingSystem.IsWindows() && _teacherHeartbeatState is { Enabled: true, PackageId: not null })
            _ = SendTeacherCampusHeartbeatAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExecuting { get => _isExecuting; private set { _isExecuting = value; Changed(); Changed(nameof(CanInstallTeacherVeyon)); Changed(nameof(CanGenerateStudentPackage)); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(CanDisableWebsitePolicy)); Changed(nameof(CanFillFailedWebsiteTargets)); Changed(nameof(CanReadWebsiteLocations)); Changed(nameof(CanApplyWebsiteLocation)); Changed(nameof(CanReplaceWebsiteSigningKey)); Changed(nameof(CanAddRoomToVeyon)); Changed(nameof(CanPublishStudentPackage)); Changed(nameof(CanCheckTeacherUpdate)); Changed(nameof(CanDownloadTeacherUpdate)); Changed(nameof(CanDeployStudentUpdate)); } }
    public bool IsClassroomPage { get => _selectedPage == "classroom"; set { if (value) SelectPage("classroom"); } }
    public bool IsRoomPage { get => _selectedPage == "rooms"; set { if (value) SelectPage("rooms"); } }
    public bool IsSetupPage { get => _selectedPage == "setup"; set { if (value) SelectPage("setup"); } }
    public bool EnableAnonymousTelemetry
    {
        get => _enableAnonymousTelemetry;
        set { if (_enableAnonymousTelemetry == value) return; _enableAnonymousTelemetry = value; Changed(); }
    }
    public bool EnableTeacherCampusHeartbeat
    {
        get => _teacherHeartbeatState?.Enabled ?? false;
        set
        {
            if (EnableTeacherCampusHeartbeat == value) return;
            try
            {
                var state = _teacherHeartbeatState ?? TeacherCampusHeartbeatStateStore.LoadOrCreate();
                var nextState = state with { Enabled = value };
                _teacherHeartbeatState = nextState;
                TeacherCampusHeartbeatStateStore.Save(nextState);
                Changed();
                TeacherHeartbeatStatus = value
                    ? "已启用；仅上报校区包编号、版本和电脑总数。"
                    : "已停用校区每日心跳。";
                if (value) _ = SendTeacherCampusHeartbeatAsync();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                TeacherHeartbeatStatus = "无法保存心跳设置：" + exception.Message;
            }
        }
    }
    public string TeacherHeartbeatStatus
    {
        get => _teacherHeartbeatStatus;
        private set
        {
            if (_teacherHeartbeatStatus == value) return;
            _teacherHeartbeatStatus = value;
            Changed();
        }
    }
    public string PageTitle => _selectedPage switch
    {
        "rooms" => "地点与学生名单",
        "setup" => "首次设置",
        _ => "课堂控制"
    };
    public string PageDescription => _selectedPage switch
    {
        "rooms" => "一次建立 Veyon 地点和电脑清单；学生姓名只用于教师端显示。",
        "setup" => "只在首次配置或更换密钥时使用。",
        _ => "选择机房，设置课堂网站规则并查看逐台结果。"
    };
    public string AppVersion => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "版本未知";
    public string EnvironmentNote => OperatingSystem.IsWindows()
        ? $"教师控制台 {AppVersion} · 签名私钥只在当前教师 Windows 用户证书库中使用。"
        : $"教师控制台 {AppVersion} · 当前为界面预览环境；教师部署和策略签名只支持 Windows。";

    public bool CanInstallTeacherVeyon => OperatingSystem.IsWindows() && !IsExecuting;
    public bool CanCheckTeacherUpdate => OperatingSystem.IsWindows() && !IsExecuting && !_isCheckingTeacherUpdate && !_isDownloadingTeacherUpdate;
    public bool CanDownloadTeacherUpdate => OperatingSystem.IsWindows() && !IsExecuting && !_isCheckingTeacherUpdate &&
        !_isDownloadingTeacherUpdate && _teacherUpdateAvailable && _teacherUpdateRelease is not null;
    public bool CanDeployStudentUpdate => OperatingSystem.IsWindows() && !IsExecuting && _releaseClient is not null &&
        AreWebsitePolicyTargetsValid() && !string.IsNullOrWhiteSpace(CampusId);
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
    public bool HasTeacherUpdate => _teacherUpdateAvailable && _teacherUpdateRelease is not null;
    public string StudentUpdateStatus
    {
        get => _studentUpdateStatus;
        private set { if (_studentUpdateStatus == value) return; _studentUpdateStatus = value; Changed(); }
    }
    public bool CanGenerateStudentPackage => OperatingSystem.IsWindows() && !IsExecuting;
    public bool CanPublishStudentPackage => OperatingSystem.IsWindows() && !IsExecuting &&
        IsPackagePublisherDetailsValid() && Directory.Exists(PublishPackageDirectory);
    public bool CanPushWebsitePolicy => OperatingSystem.IsWindows() && !IsExecuting && !IsReadingWebsiteLocations &&
        !_websiteLocationSelectionPending && IsWebsitePolicyInputValid();
    public bool CanDisableWebsitePolicy => OperatingSystem.IsWindows() && !IsExecuting && !IsReadingWebsiteLocations &&
        !_websiteLocationSelectionPending && AreWebsitePolicyTargetsValid();
    public bool CanFillFailedWebsiteTargets => !IsExecuting && _lastFailedWebsiteTargets.Count > 0;
    public bool CanAddRoomToVeyon => OperatingSystem.IsWindows() && !IsExecuting &&
        RoomLocationName.Trim().Length > 0 && RoomError.Length == 0;
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
        $"目标计算机：{Environment.MachineName}\n操作：从 App 内嵌资源校验并安装官方 Veyon {VeyonInstallerTrust.Version} x64 教师组件（含 Master）。安装可能要求重启；检测到本机已有 Veyon 时会停止并提示不要重复安装。";
    public string TeacherInstallSafetyText =>
        "安装会添加 Veyon 系统服务并修改系统配置。开始前请暂时退出 360 等杀毒软件；安装完成后立即重新开启防护。";

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
        TeacherUpdateStatus = "正在检查已签名的教师控制台版本……";
        try
        {
            var result = await _releaseClient.CheckLatestAsync(ApplicationReleaseRole.TeacherConsole, AppVersion);
            _teacherUpdateRelease = result.Release;
            _teacherUpdateAvailable = result.IsNewer;
            TeacherUpdateStatus = result.Release is null
                ? "目前没有已发布的教师控制台版本。"
                : result.IsNewer
                    ? $"发现新版本 {result.Release.Manifest.Version}；清单签名与目标信息已验证。"
                    : $"当前版本 {AppVersion} 已是最新版本（云端 {result.Release.Manifest.Version}）。";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
        {
            _teacherUpdateRelease = null;
            _teacherUpdateAvailable = false;
            TeacherUpdateStatus = "检查更新失败：" + exception.Message;
        }
        finally
        {
            _isCheckingTeacherUpdate = false;
            Changed(nameof(CanCheckTeacherUpdate));
            Changed(nameof(CanDownloadTeacherUpdate));
            Changed(nameof(HasTeacherUpdate));
        }
    }

    public async Task<bool> DownloadTeacherUpdateAsync()
    {
        if (_releaseClient is null || !_teacherUpdateAvailable || _teacherUpdateRelease is null) return false;
        _isDownloadingTeacherUpdate = true;
        Changed(nameof(CanCheckTeacherUpdate));
        Changed(nameof(CanDownloadTeacherUpdate));
        TeacherUpdateStatus = "正在下载并验证安装器大小、SHA-256 与发布签名……";
        var handoffStarted = false;
        try
        {
            var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "Updates");
            var installerPath = await _releaseClient.DownloadAsync(_teacherUpdateRelease,
                ApplicationReleaseRole.TeacherConsole, updateDirectory);
            ApplicationReleaseUpdateHandoff.Start(installerPath, ApplicationReleaseRole.TeacherConsole, AppVersion);
            handoffStarted = true;
            TeacherUpdateStatus = $"已验证并启动 { _teacherUpdateRelease.Manifest.Version } 安装；应用将关闭，安装成功后自动重启。";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or
                                          InvalidOperationException or UnauthorizedAccessException or
                                          System.ComponentModel.Win32Exception or CryptographicException)
        {
            TeacherUpdateStatus = "下载或校验失败；当前安装未更改：" + exception.Message;
        }
        finally
        {
            _isDownloadingTeacherUpdate = false;
            Changed(nameof(CanCheckTeacherUpdate));
            Changed(nameof(CanDownloadTeacherUpdate));
        }
        return handoffStarted;
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
            if (!TeacherCampusHeartbeatStateStore.IsDue(state, TeacherCampusHeartbeatStateStore.GetHongKongDate()))
            {
                TeacherHeartbeatStatus = "今日校区心跳已发送；本机按 UTC+8 日期跳过重复请求。";
                return;
            }
            TeacherHeartbeatStatus = "正在读取 Veyon 机房电脑总数并发送匿名校区汇总……";
            var locations = await Task.Run(VeyonNetworkObjectDirectory.ReadLocations);
            var configuredComputerCount = locations.SelectMany(location => location.Targets)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var studentVersion = AppVersion;
            if (_releaseClient is not null)
            {
                try
                {
                    var studentRelease = await _releaseClient.CheckLatestAsync(
                        ApplicationReleaseRole.StudentSetup, AppVersion);
                    if (studentRelease.Release is not null)
                        studentVersion = studentRelease.Release.Manifest.Version;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or
                                                  TaskCanceledException)
                {
                }
            }
            var sent = await _teacherHeartbeatClient.TrySendOnceDailyAsync(state, AppVersion,
                studentVersion, configuredComputerCount);
            _teacherHeartbeatState = TeacherCampusHeartbeatStateStore.LoadOrCreate();
            TeacherHeartbeatStatus = sent
                ? $"今日校区心跳已发送：电脑总数 {configuredComputerCount}，教师版本 {AppVersion}，目标学生版本 {studentVersion}。"
                : "今日校区心跳已发送；本机按 UTC+8 日期跳过重复请求。";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or
                                          UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or PlatformNotSupportedException)
        {
            TeacherHeartbeatStatus = "校区心跳未发送；不会影响 Teacher 功能：" + exception.Message;
        }
        finally { Volatile.Write(ref _teacherHeartbeatInFlight, 0); }
    }

    internal Task SendTeacherCampusHeartbeatIfDueAsync()
    {
        return TeacherCampusHeartbeatStateStore.IsDue(_teacherHeartbeatState,
            TeacherCampusHeartbeatStateStore.GetHongKongDate())
            ? SendTeacherCampusHeartbeatAsync()
            : Task.CompletedTask;
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
            ClearRoomCreateFeedback();
            Changed(nameof(CanAddRoomToVeyon));
        }
    }
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
            Changed(nameof(SelectedWebsiteSigningCampus));
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanDeployStudentUpdate));
        }
    }
    public string RoomOutputDir { get => _roomOutputDir; set { _roomOutputDir = value ?? ""; Changed(); } }
    public string WebsiteTargets
    {
        get => _websiteTargets;
        set
        {
            value ??= "";
            if (_websiteTargets == value) return;
            _websiteTargets = value;
            if (_websiteLocationSelectionPending)
            {
                _websiteLocationSelectionPending = false;
                Changed(nameof(CanPushWebsitePolicy));
                Changed(nameof(CanDisableWebsitePolicy));
            }
            Changed();
            Changed(nameof(CanPushWebsitePolicy));
            Changed(nameof(CanDisableWebsitePolicy));
            Changed(nameof(CanDeployStudentUpdate));
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
        WebsiteSigningCampuses = candidates.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Changed(nameof(WebsiteSigningCampuses));
        Changed(nameof(SelectedWebsiteSigningCampus));
        if (string.IsNullOrWhiteSpace(CampusId) && WebsiteSigningCampuses.Count == 1)
            CampusId = WebsiteSigningCampuses[0];
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
    public string WebsitePolicyError { get => _websitePolicyError; private set { _websitePolicyError = value; Changed(); Changed(nameof(HasWebsitePolicyError)); } }
    public bool HasWebsitePolicyError => WebsitePolicyError.Length > 0;
    public string WebsitePolicyHistoryText { get => _websitePolicyHistoryText; private set { _websitePolicyHistoryText = value; Changed(); Changed(nameof(HasWebsitePolicyHistory)); } }
    public bool HasWebsitePolicyHistory => WebsitePolicyHistoryText.Length > 0;
    public string InstallerStatus { get => _installerStatus; private set { _installerStatus = value; Changed(); } }
    public string TeacherInstallResult { get => _teacherInstallResult; private set { _teacherInstallResult = value; Changed(); Changed(nameof(HasTeacherInstallResult)); } }
    public bool HasTeacherInstallResult => TeacherInstallResult.Length > 0;
    public string TeacherInstallIssue { get => _teacherInstallIssue; private set { _teacherInstallIssue = value; Changed(); Changed(nameof(HasTeacherInstallIssue)); } }
    public bool HasTeacherInstallIssue => TeacherInstallIssue.Length > 0;
    public string PackageOutput { get => _packageOutput; private set { _packageOutput = value; Changed(); Changed(nameof(HasPackageOutput)); } }
    public string PackageOutputError { get => _packageOutputError; private set { _packageOutputError = value; Changed(); Changed(nameof(HasPackageOutputError)); } }
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
        get => _publishCampusName;
        set
        {
            _publishCampusName = value ?? "";
            Changed();
            Changed(nameof(CanPublishStudentPackage));
            PackagePublishResult = "";
        }
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
        ? $"{RoomLocationName.Trim()} · 共 {RoomNames.Count} 台 · 已填写 {GetNamedStudentCount()} 个姓名"
        : "尚未生成清单";
    public string RoomCreateResult { get => _roomCreateResult; private set { _roomCreateResult = value; Changed(); Changed(nameof(HasRoomCreateResult)); } }
    public bool HasRoomCreateResult => RoomCreateResult.Length > 0;
    public string RoomCreateError { get => _roomCreateError; private set { _roomCreateError = value; Changed(); Changed(nameof(HasRoomCreateError)); } }
    public bool HasRoomCreateError => RoomCreateError.Length > 0;
    public string RoomCreateStatus { get => _roomCreateStatus; private set { _roomCreateStatus = value; Changed(); Changed(nameof(HasRoomCreateStatus)); } }
    public bool HasRoomCreateStatus => RoomCreateStatus.Length > 0;

    public void SelectPage(string page)
    {
        if (page is not ("classroom" or "rooms" or "setup") || _selectedPage == page) return;
        _selectedPage = page;
        Changed(nameof(IsClassroomPage)); Changed(nameof(IsRoomPage)); Changed(nameof(IsSetupPage));
        Changed(nameof(PageTitle)); Changed(nameof(PageDescription));
    }

    public void GenerateRoomPreview()
    {
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

    public async Task AddRoomToVeyonAsync()
    {
        if (!CanAddRoomToVeyon) return;
        if (!HasRoomPreview) GenerateRoomPreview();
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
            var studentNames = ParseStudentRoster(RoomNames.Count);
            var computers = RoomNames.Select((name, index) => new VeyonNetworkComputer(name, name,
                index < studentNames.Count ? studentNames[index] : "")).ToArray();
            RoomCreateStatus = "正在检查 Veyon 版本、权限和现有地点……";
            var platform = await Task.Run(PlatformFacts.Collect);
            if (platform.IsElevated != true)
            {
                RoomCreateError = "写入 Veyon 电脑目录需要管理员权限；请以管理员身份重新打开教师端。";
                return;
            }
            var veyon = platform.Veyon ?? await Task.Run(VeyonFacts.Probe);
            if (veyon.Status != "installed" || !VeyonFacts.IsSupportedVersionDetail(veyon.VersionDetail))
            {
                RoomCreateError = $"需要先安装并确认 Veyon {VeyonInstallerTrust.Version}。没有更改目录。\n{veyon.AsText()}";
                return;
            }
            RoomCreateStatus = "正在写入地点和电脑清单并读回核对……";
            var result = await Task.Run(() => VeyonNetworkObjectDirectory.AddLocation(locationName, computers));
            RoomCreateResult = $"已在本机 Veyon 内置目录创建地点“{result.LocationName}”，添加 {result.ComputerCount} 台电脑，其中 {result.NamedStudentCount} 台显示学生姓名。已填写姓名的电脑只显示姓名；电脑编号保留在主机名中，用于连接。若要手动修改，可打开 Veyon Configurator → Locations & computers。姓名保存在教师端电脑目录，不写入学生校区配置包。";
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
            ConfiguratorLaunchError = "无法打开 Veyon Configurator：" + exception.Message;
            RoomCreateError = ConfiguratorLaunchError;
        }
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

    public async Task DeployStudentUpdateAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            StudentUpdateStatus = "学生端静默更新仅支持 Windows。";
            return;
        }
        if (!CanDeployStudentUpdate || !TryBeginExclusiveTask()) return;
        StudentUpdateStatus = "正在检查并验证最新 StudentSetup 发布……";
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
            var deliveries = await StudentApplicationUpdateTransport.PushAsync(targets,
                verifiedRelease.Manifest.Version, async (target, token) =>
            {
                var downloadUri = await lanServer.GetDownloadUriAsync(target, token);
                var issuedUtc = DateTimeOffset.UtcNow;
                var command = new StudentApplicationUpdateCommand(1, campus, Guid.NewGuid(), issuedUtc,
                    issuedUtc + StudentApplicationUpdateCryptography.MaximumCommandLifetime, verifiedRelease,
                    downloadUri.AbsoluteUri);
                lock (signingKey.PrivateKey)
                    return StudentApplicationUpdateCryptography.Sign(command, signingKey.PrivateKey,
                        releaseClient.ApiBaseAddress, developerPublicKey);
            });
            var succeeded = deliveries.Count(result => result.Succeeded);
            var needsReview = deliveries.Count(result => result.NeedsReview);
            StudentUpdateStatus = $"StudentSetup {verifiedRelease.Manifest.Version} · 已确认 {succeeded}/{deliveries.Count} 台 · 需核对 {needsReview} 台" +
                                  Environment.NewLine + string.Join(Environment.NewLine,
                                      deliveries.Select(result =>
                                          $"{result.Target}：{(result.Succeeded ? "已读回安装版本" : result.NeedsReview ? "需核对" : "失败")} — {result.Detail}"));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or CryptographicException or HttpRequestException or
                                          System.ComponentModel.Win32Exception or SocketException or PlatformNotSupportedException)
        {
            StudentUpdateStatus = "学生静默更新未完成；请核对逐台状态后再重试：" + exception.Message;
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
            var results = await WebsitePolicyTransport.PushAsync(targets, signedPolicy);
            var succeeded = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count(result => !result.Succeeded && !result.NeedsReview);
            var expirySummary = expiresUtc is { } expiry
                ? $" · 自动解除 {expiry.ToLocalTime():yyyy-MM-dd HH:mm}"
                : mode == WebsitePolicyMode.Disabled ? "" : " · 不自动到期";
            var heading = $"策略版本 {revision} · {mode switch { WebsitePolicyMode.Disabled => "已停用", WebsitePolicyMode.Blocklist => "黑名单", _ => "白名单" }}{expirySummary} · 代理确认 {succeeded}/{results.Count} 台 · 需核对 {needsReview} · 失败 {failed}";
            WebsitePolicyResult = heading + Environment.NewLine + string.Join(Environment.NewLine,
                results.Select(result => $"{result.Target}：{(result.Succeeded ? "代理已确认" : result.NeedsReview ? "需核对" : "失败")} — {result.Detail}"));
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
                    needsReview > 0 ? "部分学生机没有返回代理确认；这些目标状态不明。" : "",
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

    public async Task ConfigureTeacherAuthenticationAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        try
        {
            TeacherInstallIssue = "";
            TeacherInstallResult = "";
            var result = await Task.Run(VeyonTeacherAuthentication.Configure);
            if (result.Ok) TeacherInstallResult = result.Detail;
            else TeacherInstallIssue = result.Detail;
        }
        finally { EndExclusiveTask(); }
    }

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
        WebsitePolicyHistoryText = $"最近一次推送：{entry.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 校区 {entry.CampusId} · 版本 {entry.Revision} · 代理确认 {succeeded}/{entry.Results.Count} · 需核对 {needsReview} · 失败 {failed}。本机仅保存设备与结果，不保存域名清单或签名内容；最多保留 {WebsitePolicyPushHistoryStore.MaximumRuns} 次。";
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

    public void ReportPackagePublisherError(string message) => PackagePublisherError = message;

    public async Task PublishStudentPackageAsync()
    {
        if (!CanPublishStudentPackage || !TryBeginExclusiveTask()) return;
        PackagePublisherError = "";
        PackagePublishResult = "";
        PackagePublisherStatus = "正在校验配置文件并匿名发布到私有存储（ZIP 不超过 64 KiB）……";
        try
        {
            var result = await _packagePublisher.PublishAsync(PublishCampusName,
                PublisherName, TeacherPhoneLast4, PublishPackageDirectory);
            PackagePublisherStatus = "云端目录已发布；学生可按校区名称或电脑名前缀搜索。";
            PackagePublishResult =
                $"发布成功：{result.CampusName} · {result.ComputerPrefix}\n发布教师：{PublisherName.Trim()}\n文件：{result.FileName} · {result.SizeBytes:N0} 字节\n包编号：{result.PackageId:D}\n学生下载时输入教师手机号后四位。";
            try
            {
                var heartbeatState = _teacherHeartbeatState ?? TeacherCampusHeartbeatStateStore.LoadOrCreate();
                var nextHeartbeatState = heartbeatState with { PackageId = result.PackageId };
                _teacherHeartbeatState = nextHeartbeatState;
                TeacherCampusHeartbeatStateStore.Save(nextHeartbeatState);
                Changed(nameof(EnableTeacherCampusHeartbeat));
                if (nextHeartbeatState.Enabled) _ = SendTeacherCampusHeartbeatAsync();
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
        return campus.Length is >= 1 and <= 100 && !campus.Any(char.IsControl) &&
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
        var teacherKeyCreated = false;
        var campus = CampusId.Trim();
        try { WebsitePolicySigningKeyStore.ValidateCampusId(campus); }
        catch (InvalidDataException exception)
        {
            PackageOutputError = exception.Message;
            EndExclusiveTask();
            return;
        }
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                PackageOutputError = "校区公钥由 Veyon 受控密钥目录管理；请在已安装 Veyon 的 Windows 教师端生成配置包。";
                return;
            }
            var platform = await Task.Run(PlatformFacts.Collect);
            if (platform.IsElevated != true)
            {
                PackageOutputError = "生成学生包需要管理员权限来访问 Veyon 密钥目录；请以管理员身份重新启动 App。";
                return;
            }
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
            using var websiteSigningKey = await Task.Run(() =>
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("教师网站策略密钥仅支持 Windows 用户证书库。");
                return WebsitePolicySigningKeyStore.GetOrCreate(campus, replaceUnavailableSigningKey);
            });
            var publicKeyExportPath = Path.Combine(Path.GetTempPath(), "VeyonCampus-public-" + Guid.NewGuid().ToString("N") + ".pem");
            temporaryPublicKey = publicKeyExportPath;
            InstallerStatus = "正在检查 Veyon 密钥库并仅导出校区配置所需公钥……";
            var keyResult = await Task.Run(() => new VeyonTeacherKeyProvisioner().ExportPublicKey(campus, publicKeyExportPath));
            teacherKeyCreated = keyResult.Created;
            if (!keyResult.Step.Ok)
            {
                PackageOutputError = keyResult.Step.Detail +
                    (teacherKeyCreated ? "\n本次已在 Veyon 密钥库创建密钥对，密钥保留在那里；没有导出教师私钥。" : "");
                return;
            }
            var built = await Task.Run(() => PackageBuilder.Build(outDir, campus, RoomPrefix,
                publicKeyExportPath, websiteSigningKey.PublicKeyPem, EnableAnonymousTelemetry));
            PublishPackageDirectory = built;
            var telemetryStatus = EnableAnonymousTelemetry
                ? "已启用匿名每日统计；学生工具会发送工具版本和配置包编号，并按 UTC+8 日期去重。"
                : "未启用匿名每日统计。";
            PackageOutput = $"已生成学生校区配置包：{built}\n{keyResult.Step.Detail}\n教师签名私钥保留在当前 Windows 用户证书库；学生配置仅包含校区公钥。可在下方填写校区名称、教师姓名和手机号后四位，直接发布到 CloudBase；断网维护时可将配置包保存到本机磁盘，再由 StudentSetup 手动导入。学生部署工具本身仍从受信发布渠道获取。\n{telemetryStatus}";
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
            var adapter = new WindowsVeyonAdapter();
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
            var platform = await Task.Run(PlatformFacts.Collect);
            if (platform.IsElevated != true)
            {
                TeacherInstallIssue = "尚未安装：请退出 App，再右键应用选择“以管理员身份运行”，然后重新点击此按钮。";
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
            var install = await Task.Run(() => adapter.InstallVeyonOnly(acquired.InstallerPath, isTeacher: true));
            if (install.RebootRequired || !install.Ok)
            {
                TeacherInstallIssue = install.Detail + (install.RebootRequired ? " 请重启后重新检查。" : "");
                return;
            }
            var verification = await Task.Run(adapter.VerifyTeacherInstall);
            if (verification.Ok)
            {
                var authentication = await Task.Run(VeyonTeacherAuthentication.Configure);
                TeacherInstallResult = $"{install.Detail}\n安装读回：{verification.Detail}\n{authentication.Detail}\n请继续生成校区密钥和学生配置包，并配置机房电脑目录。";
                if (!authentication.Ok) TeacherInstallIssue = authentication.Detail;
            }
            else TeacherInstallIssue = $"{install.Detail}\n安装读回需人工核对：{verification.Detail}";
            InstallerStatus = acquired.ExtractedFromApp
                ? $"已从 App 内嵌资源提取并验证 Veyon {VeyonInstallerTrust.Version}；后续使用本机校验缓存。"
                : $"已复用并验证本机缓存中的 App 内嵌 Veyon {VeyonInstallerTrust.Version}。";
        }
        catch (Exception exception) { TeacherInstallIssue = "安装未完成：" + exception.Message; }
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
        ClearRoomCreateFeedback();
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
            RoomPreviewRows = RoomNames.Select((name, index) =>
            {
                var student = index < students.Count ? students[index] : "";
                return student.Length == 0 ? $"{name}    （未填写姓名）" : student;
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
            if (name.Length > 100 || name.Any(character => char.IsControl(character) || character is ';' or '"'))
                throw new InvalidDataException("学生姓名最多 100 个字符，不能包含分号、双引号或控制字符。");
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

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
