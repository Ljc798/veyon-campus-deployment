using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class TeacherViewModel : INotifyPropertyChanged
{
    private readonly VeyonInstallerStore _installerStore;
    private readonly DeploymentPackagePublishingClient _packagePublisher;
    private readonly ITaskLease _lease;
    private IReadOnlyList<string> _roomNames = Array.Empty<string>();
    private IReadOnlyList<string> _roomPreviewRows = Array.Empty<string>();
    private IReadOnlyList<VeyonNetworkLocation> _websiteLocations = Array.Empty<VeyonNetworkLocation>();
    private IReadOnlyList<string> _lastFailedWebsiteTargets = Array.Empty<string>();
    private bool _isExecuting, _isReadingWebsiteLocations, _websiteLocationSelectionPending;
    private bool _canReplaceWebsiteSigningKey;
    private string _roomPrefix = "PC-", _roomStart = "1", _roomCount = "150", _roomError = "";
    private string _roomLocationName = "", _studentRoster = "", _roomCreateResult = "", _roomCreateError = "", _roomCreateStatus = "";
    private string _configuratorLaunchError = "";
    private string _campusId = "", _roomOutputDir = "", _packageOutput = "", _packageOutputError = "";
    private string _publishPackageDirectory = "", _publishCampusName = "智学前程-", _publisherTeacherName = "", _publisherMobileLast4 = "";
    private string _packagePublisherStatus = "", _packagePublisherError = "", _packagePublishResult = "", _myPublishedPackages = "";
    private string _websiteTargets = "", _websiteDomains = "", _websitePolicyResult = "", _websitePolicyError = "", _websitePolicyHistoryText = "";
    private string _websiteDirectoryStatus = "", _websiteDirectoryError = "";
    private string _installerStatus = "Veyon 安装器已内嵌在 App 中；无需联网下载。", _teacherInstallResult = "", _teacherInstallIssue = "";
    private int _websiteModeIndex = 0, _websiteDurationIndex = 1, _websiteLocationIndex = -1;
    private string _selectedPage = "classroom";

    public TeacherViewModel(VeyonInstallerStore? installerStore = null)
    {
        _installerStore = installerStore ?? new VeyonInstallerStore();
        _packagePublisher = new DeploymentPackagePublishingClient();
        _lease = OperatingSystem.IsWindows() ? new NamedPipeTaskLease() : new TaskLease();
        LoadLatestWebsitePolicyHistory();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExecuting { get => _isExecuting; private set { _isExecuting = value; Changed(); Changed(nameof(CanInstallTeacherVeyon)); Changed(nameof(CanGenerateStudentPackage)); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(CanDisableWebsitePolicy)); Changed(nameof(CanFillFailedWebsiteTargets)); Changed(nameof(CanReadWebsiteLocations)); Changed(nameof(CanApplyWebsiteLocation)); Changed(nameof(CanReplaceWebsiteSigningKey)); Changed(nameof(CanAddRoomToVeyon)); Changed(nameof(CanPublishStudentPackage)); Changed(nameof(CanFindMyPublishedPackages)); } }
    public bool IsClassroomPage { get => _selectedPage == "classroom"; set { if (value) SelectPage("classroom"); } }
    public bool IsRoomPage { get => _selectedPage == "rooms"; set { if (value) SelectPage("rooms"); } }
    public bool IsSetupPage { get => _selectedPage == "setup"; set { if (value) SelectPage("setup"); } }
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
    public bool CanGenerateStudentPackage => OperatingSystem.IsWindows() && !IsExecuting;
    public bool CanPublishStudentPackage => OperatingSystem.IsWindows() && !IsExecuting &&
        IsPublishCampusNameValid() && IsPublisherIdentityValid() && Directory.Exists(PublishPackageDirectory);
    public bool CanFindMyPublishedPackages => !IsExecuting && IsPublisherIdentityValid();
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
    public string PublisherTeacherName
    {
        get => _publisherTeacherName;
        set
        {
            _publisherTeacherName = value ?? "";
            Changed();
            Changed(nameof(CanPublishStudentPackage));
            Changed(nameof(CanFindMyPublishedPackages));
            MyPublishedPackages = "";
            PackagePublishResult = "";
        }
    }
    public string PublisherMobileLast4
    {
        get => _publisherMobileLast4;
        set
        {
            _publisherMobileLast4 = value ?? "";
            Changed();
            Changed(nameof(CanPublishStudentPackage));
            Changed(nameof(CanFindMyPublishedPackages));
            MyPublishedPackages = "";
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
    public string MyPublishedPackages { get => _myPublishedPackages; private set { _myPublishedPackages = value; Changed(); Changed(nameof(HasMyPublishedPackages)); } }
    public bool HasMyPublishedPackages => MyPublishedPackages.Length > 0;
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

    public async Task FindMyPublishedPackagesAsync()
    {
        if (!CanFindMyPublishedPackages || !TryBeginExclusiveTask()) return;
        PackagePublisherError = "";
        MyPublishedPackages = "正在查找匹配的发布记录……";
        try
        {
            var items = await _packagePublisher.FindMyPublishedPackagesAsync(
                PublisherTeacherName.Trim(), PublisherMobileLast4, CancellationToken.None);
            MyPublishedPackages = items.Count == 0
                ? "没有找到与姓名和手机号后四位匹配的已发布包。"
                : string.Join(Environment.NewLine, items.Select(item =>
                    $"{item.CampusName} · {item.ComputerPrefix} · {item.PublishedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {item.PackageId:D}"));
        }
        catch (Exception exception)
        {
            PackagePublisherError = "读取我的发布记录失败：" + exception.Message;
            MyPublishedPackages = "";
        }
        finally { EndExclusiveTask(); }
    }

    public async Task PublishStudentPackageAsync()
    {
        if (!CanPublishStudentPackage || !TryBeginExclusiveTask()) return;
        PackagePublisherError = "";
        PackagePublishResult = "";
        PackagePublisherStatus = "正在校验公开配置文件并上传（ZIP 不超过 64 KiB）……";
        try
        {
            var campusName = PublishCampusName.Trim();
            var teacherName = PublisherTeacherName.Trim();
            var package = await Task.Run(() => PackageManifest.Load(PublishPackageDirectory));
            if (!string.Equals(package.Campus, campusName, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"填写的校区名称为“{campusName}”，但配置包内校区名为“{package.Campus}”。请将完整校区名称填入上方字段并重新生成配置包，或选择名称相符的配置包文件夹。");

            var result = await _packagePublisher.PublishAsync(campusName, teacherName,
                PublisherMobileLast4, PublishPackageDirectory);
            PackagePublisherStatus = "云端目录已发布；学生端现在可以搜索并下载此配置包。";
            PackagePublishResult =
                $"发布成功：{result.CampusName} · {result.ComputerPrefix}\n文件：{result.FileName} · {result.SizeBytes:N0} 字节\n包编号：{result.PackageId:D}\n学生下载时需要由教师输入手机号后四位。";
        }
        catch (Exception exception)
        {
            PackagePublisherError = "发布失败：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    private bool IsPublishCampusNameValid()
    {
        var campusName = PublishCampusName.Trim();
        return campusName.Length is > 0 and <= 100 && campusName != "智学前程-" && !campusName.Any(char.IsControl);
    }

    private bool IsPublisherIdentityValid()
    {
        var name = PublisherTeacherName.Trim();
        return name.Length is > 0 and <= 32 && !name.Any(char.IsControl) &&
               PublisherMobileLast4.Length == 4 && PublisherMobileLast4.All(char.IsAsciiDigit);
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
                publicKeyExportPath, websiteSigningKey.PublicKeyPem));
            PublishPackageDirectory = built;
            PackageOutput = $"已生成学生校区配置包：{built}\n{keyResult.Step.Detail}\n教师签名私钥保留在当前 Windows 用户证书库；学生配置仅包含校区公钥。可在下方免登录发布到云端目录，也可使用 Windows 只读共享分发。学生部署工具本身仍从受信发布渠道获取。";
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
