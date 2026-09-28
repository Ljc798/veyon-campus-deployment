using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _campus = "", _prefix = "PC-", _number = "", _studentAccount = "User", _adminAccount = "Administrator";
    private string _studentPassword = "", _studentPasswordConfirmation = "";
    private string _adminPassword = "", _adminPasswordConfirmation = "";
    private string _error = "", _packageError = "", _preview = "", _preflight = "", _packageStatus = "未选择校区配置包", _operationHelp = "", _execution = "";
#if !STUDENT_SETUP_APP
    private string _roomPrefix = "PC-", _roomStart = "1", _roomCount = "150", _roomError = "";
    private string _campusId = "", _roomOutputDir = "", _packageOutput = "", _packageOutputError = "";
    private string _websiteTargets = "", _websiteDomains = "", _websitePolicyResult = "", _websitePolicyError = "", _websitePolicyHistoryText = "";
    private int _websiteModeIndex = 1, _websiteDurationIndex = 1;
    private string _teacherInstallResult = "", _teacherInstallIssue = "";
    private IReadOnlyList<string> _roomNames = Array.Empty<string>();
    private IReadOnlyList<string> _lastFailedWebsiteTargets = Array.Empty<string>();
#endif
    private string _studentDeploymentVerificationText = "", _studentSetupCleanupText = "", _studentSetupCleanupAvailability = "";
    private string _installerStatus = "Veyon 安装器已内嵌在学生部署工具中；无需联网下载。";
    private string _veyonStatusText = "正在读取本机 Veyon 安装状态……", _preparationStatusText = "";
    private bool _installVeyon, _rename, _createStudent, _changeAdmin, _isExecuting = false;
    private bool _isDetectingVeyon, _isPreparingDeployment;
    private int _veyonProbeRequestId;
#if !STUDENT_SETUP_APP
    private bool _isStudent = true;
#endif
    private PackageContext? _package;
    private string? _deploymentInstallerPath;
    private PreflightReport? _preflightReport;
    private PlanInput? _preflightInput;
    private StudentDeploymentVerificationReport? _studentDeploymentVerification;
    private bool _studentSetupCleanupAvailable;
    private int _preflightRequestId;
    private WindowsVeyonAdapter? _adapter = new();
    private readonly VeyonInstallerStore _installerStore;
    private readonly ITaskLease _lease;
    private readonly IProcessLauncher _launcher = new DefaultProcessLauncher();

    public MainViewModel(VeyonInstallerStore? installerStore = null)
    {
        _installerStore = installerStore ?? new VeyonInstallerStore();
        // The Mac build is only a UI preview; Windows deployment uses the cross-process task lock.
        _lease = OperatingSystem.IsWindows() ? new NamedPipeTaskLease() : new TaskLease();
#if !STUDENT_SETUP_APP
        LoadLatestWebsitePolicyHistory();
#endif
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ClearStudentPasswordRequested;
    public event EventHandler? ClearAdminPasswordRequested;
    public string Campus
    {
        get => _campus;
        set { value ??= ""; if (_campus == value) return; _campus = value; Changed(); ClearLoadedPackage(); Invalidate(); }
    }
    public string Prefix
    {
        get => _prefix;
        set { value ??= ""; if (_prefix == value) return; _prefix = value; Changed(); Changed(nameof(ComputerName)); ClearLoadedPackage(); Invalidate(); }
    }
    public string Number { get => _number; set { value ??= ""; if (_number == value) return; _number = value; Changed(); Changed(nameof(ComputerName)); Invalidate(); } }
    public string StudentAccountName
    {
        get => _studentAccount;
        set { value ??= ""; if (_studentAccount == value) return; _studentAccount = value; Changed(); ClearStudentPassword(); Invalidate(); }
    }
    public string AdminAccountName
    {
        get => _adminAccount;
        set { value ??= ""; if (_adminAccount == value) return; _adminAccount = value; Changed(); ClearAdminPassword(); Invalidate(); }
    }
    public bool InstallVeyon { get => _installVeyon; set { _installVeyon = value; Changed(); Invalidate(); } }
    public bool RenameComputer { get => _rename; set { _rename = value; Changed(); Invalidate(); } }
    public bool CreateStudent
    {
        get => _createStudent;
        set { if (_createStudent == value) return; _createStudent = value; Changed(); if (!value) ClearStudentPassword(); Invalidate(); }
    }
    public bool ChangeAdminPassword
    {
        get => _changeAdmin;
        set { if (_changeAdmin == value) return; _changeAdmin = value; Changed(); if (!value) ClearAdminPassword(); Invalidate(); }
    }
    public void SetStudentPasswordInput(string password, string confirmation)
    {
        password ??= ""; confirmation ??= "";
        if (_studentPassword == password && _studentPasswordConfirmation == confirmation) return;
        _studentPassword = password; _studentPasswordConfirmation = confirmation;
        Error = "";
        NotifyExecutionAvailabilityChanged();
    }
    public void SetAdminPasswordInput(string password, string confirmation)
    {
        password ??= ""; confirmation ??= "";
        if (_adminPassword == password && _adminPasswordConfirmation == confirmation) return;
        _adminPassword = password; _adminPasswordConfirmation = confirmation;
        Error = "";
        NotifyExecutionAvailabilityChanged();
    }
    public PackageContext? LoadedPackage => _package;
    public string PackageStatus { get => _packageStatus; private set { _packageStatus = value; Changed(); } }
    public string Error { get => _error; private set { _error = value; Changed(); Changed(nameof(HasError)); Changed(nameof(HasGlobalError)); NotifyExecutionAvailabilityChanged(); } }
    public string PackageError { get => _packageError; private set { _packageError = value; Changed(); Changed(nameof(HasPackageError)); Changed(nameof(HasGlobalError)); NotifyExecutionAvailabilityChanged(); } }
    public string PreviewText
    {
        get => _preview;
        private set
        {
            _preview = value;
            Changed(); Changed(nameof(HasPreview)); NotifyExecutionAvailabilityChanged();
        }
    }
    public string PreflightText { get => _preflight; private set { _preflight = value; Changed(); Changed(nameof(HasPreflight)); } }
    public string ExecutionText { get => _execution; private set { _execution = value; Changed(); Changed(nameof(HasExecution)); } }
    public bool HasError => Error.Length > 0;
    public bool HasPackageError => PackageError.Length > 0;
    public bool HasGlobalError => HasError && (!HasPackageError || Error != PackageError);
    public bool HasPreview => PreviewText.Length > 0;
    public bool HasPreflight => PreflightText.Length > 0;
    public bool HasExecution => ExecutionText.Length > 0;
    public string StudentDeploymentVerificationText
    {
        get => _studentDeploymentVerificationText;
        private set { _studentDeploymentVerificationText = value; Changed(); Changed(nameof(HasStudentDeploymentVerification)); }
    }
    public bool HasStudentDeploymentVerification => StudentDeploymentVerificationText.Length > 0;
    public string StudentSetupCleanupText { get => _studentSetupCleanupText; private set { _studentSetupCleanupText = value; Changed(); Changed(nameof(HasStudentSetupCleanupText)); } }
    public bool HasStudentSetupCleanupText => StudentSetupCleanupText.Length > 0;
    public string StudentSetupCleanupAvailability { get => _studentSetupCleanupAvailability; private set { _studentSetupCleanupAvailability = value; Changed(); } }
    public bool CanVerifyStudentDeployment => OperatingSystem.IsWindows() && IsStudent && !IsExecuting && LoadedPackage is not null;
    public bool CanFinishStudentSetup => OperatingSystem.IsWindows() && IsStudent && !IsExecuting &&
        _studentSetupCleanupAvailable && _studentDeploymentVerification?.IsReadyToRemoveSetupTool == true;
    public bool IsExecuting => _isExecuting;
    public bool IsStudentControlsEnabled => IsStudent && !IsExecuting && !IsPreparingDeployment;
    public string VeyonStatusText { get => _veyonStatusText; private set { _veyonStatusText = value; Changed(); } }
    public bool IsDetectingVeyon { get => _isDetectingVeyon; private set { _isDetectingVeyon = value; Changed(); Changed(nameof(CanRefreshVeyonStatus)); } }
    public bool IsPreparingDeployment
    {
        get => _isPreparingDeployment;
        private set
        {
            if (_isPreparingDeployment == value) return;
            _isPreparingDeployment = value;
            Changed(); Changed(nameof(IsStudentControlsEnabled)); Changed(nameof(CanPrepareDeployment));
            Changed(nameof(CanRefreshVeyonStatus));
        }
    }
    public string PreparationStatusText { get => _preparationStatusText; private set { _preparationStatusText = value; Changed(); Changed(nameof(HasPreparationStatus)); } }
    public bool HasPreparationStatus => PreparationStatusText.Length > 0;
    public bool CanPrepareDeployment => !IsExecuting && !IsPreparingDeployment;
    public bool CanRefreshVeyonStatus => !IsExecuting && !IsPreparingDeployment && !IsDetectingVeyon;
    // ① 安装 Veyon：需要校区配置包和已校验的 App 内嵌安装器（与是否勾选无关）。
    public bool CanInstall => !IsExecuting && InstallVeyon && !RenameComputer && !CreateStudent && !ChangeAdminPassword &&
        HasPreview && LoadedPackage is not null && _deploymentInstallerPath is not null && !HasGlobalError &&
        HasCurrentExecutablePreflight();
    // ② 按冻结计划执行任意独立操作或组合。
    public bool CanStartDeployment => !IsExecuting &&
        (InstallVeyon || RenameComputer || CreateStudent || ChangeAdminPassword) &&
        HasPreview && !HasGlobalError && HasCurrentExecutablePreflight() && HasRequiredAccountCredentials() &&
        (InstallVeyon ? LoadedPackage is not null && _deploymentInstallerPath is not null : true);
    public string InstallAvailabilityText => $"仅安装 Veyon：{GetExecutionAvailabilityText("仅安装")}";
    public string DeploymentAvailabilityText => $"执行所选操作：{GetExecutionAvailabilityText("执行所选操作")}";
    public string OperationHelpText { get => _operationHelp; private set { _operationHelp = value; Changed(); Changed(nameof(HasOperationHelp)); } }
    public bool HasOperationHelp => OperationHelpText.Length > 0;
#if STUDENT_SETUP_APP
    public bool IsStudent => true;
    public bool IsTeacher => false;
    public string PageTitle => "学生端配置";
    public string PageDescription => "选择要独立执行或组合执行的操作；按需导入校区公钥、输入账户密码并完成只读检查。";
#else
    public bool IsStudent => _isStudent;
    public bool IsTeacher => !_isStudent;
    public string PageTitle => IsStudent ? "学生端配置" : "教师端准备";
    public string PageDescription => IsStudent
        ? "选择要独立执行或组合执行的操作；按需导入校区公钥、输入账户密码并完成只读检查。"
        : "可安装含 Master 的教师端 Veyon、生成学生校区包，并为学生端 Edge/Chrome 签名和推送网站黑白名单。";
#endif
    public string AppVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "版本未知";
    public string EnvironmentNote => OperatingSystem.IsWindows()
        ? $"App {AppVersion} · Veyon 执行功能为实验阶段；必须通过预检，结果未完整读回时显示需核对。"
        : $"App {AppVersion} · 当前为界面预览环境；真正的系统部署将仅支持 Windows。";
    public bool NeedsVeyonPackage => !InstallVeyon;
#if !STUDENT_SETUP_APP
    public bool CanInstallTeacherVeyon => OperatingSystem.IsWindows() && !IsExecuting;
    public bool CanGenerateStudentPackage => OperatingSystem.IsWindows() && !IsExecuting;
    public bool CanPushWebsitePolicy => OperatingSystem.IsWindows() && !IsExecuting && IsWebsitePolicyInputValid();
    public bool CanDisableWebsitePolicy => OperatingSystem.IsWindows() && !IsExecuting && AreWebsitePolicyTargetsValid();
    public bool CanFillFailedWebsiteTargets => !IsExecuting && _lastFailedWebsiteTargets.Count > 0;
    public string TeacherInstallPlanText =>
        $"目标计算机：{Environment.MachineName}\n操作：从 App 内嵌资源校验并安装官方 Veyon {VeyonInstallerTrust.Version} x64 教师组件（含 Master）。安装可能要求重启；检测到本机已有 Veyon 时会停止并提示不要重复安装。";
    public string TeacherInstallSafetyText =>
        "安装会添加 Veyon 系统服务并修改系统配置。开始前请暂时退出 360 等杀毒软件；安装完成后立即重新开启防护。";
#endif
    public string ComputerName
    {
        get { try { return MachineNaming.CreateName(Prefix, Number); } catch (InvalidDataException) { return "等待有效编号与前缀"; } }
    }

#if !STUDENT_SETUP_APP
    public string RoomPrefix { get => _roomPrefix; set { _roomPrefix = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string RoomStart { get => _roomStart; set { _roomStart = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string RoomCount { get => _roomCount; set { _roomCount = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string CampusId { get => _campusId; set { _campusId = value ?? ""; Changed(); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(CanDisableWebsitePolicy)); } }
    public string RoomOutputDir { get => _roomOutputDir; set { _roomOutputDir = value ?? ""; Changed(); } }
    public string WebsiteTargets { get => _websiteTargets; set { _websiteTargets = value ?? ""; Changed(); Changed(nameof(CanPushWebsitePolicy)); Changed(nameof(CanDisableWebsitePolicy)); } }
    public string WebsiteDomains { get => _websiteDomains; set { _websiteDomains = value ?? ""; Changed(); Changed(nameof(CanPushWebsitePolicy)); } }
    public int WebsiteModeIndex { get => _websiteModeIndex; set { _websiteModeIndex = Math.Clamp(value, 0, 2); Changed(); Changed(nameof(CanPushWebsitePolicy)); } }
    public int WebsiteDurationIndex { get => _websiteDurationIndex; set { _websiteDurationIndex = Math.Clamp(value, 0, 4); Changed(); } }
    public string WebsitePolicyResult { get => _websitePolicyResult; private set { _websitePolicyResult = value; Changed(); Changed(nameof(HasWebsitePolicyResult)); } }
    public bool HasWebsitePolicyResult => WebsitePolicyResult.Length > 0;
    public string WebsitePolicyError { get => _websitePolicyError; private set { _websitePolicyError = value; Changed(); Changed(nameof(HasWebsitePolicyError)); } }
    public bool HasWebsitePolicyError => WebsitePolicyError.Length > 0;
    public string WebsitePolicyHistoryText { get => _websitePolicyHistoryText; private set { _websitePolicyHistoryText = value; Changed(); Changed(nameof(HasWebsitePolicyHistory)); } }
    public bool HasWebsitePolicyHistory => WebsitePolicyHistoryText.Length > 0;
    public string TeacherInstallResult { get => _teacherInstallResult; private set { _teacherInstallResult = value; Changed(); Changed(nameof(HasTeacherInstallResult)); } }
    public bool HasTeacherInstallResult => TeacherInstallResult.Length > 0;
    public string TeacherInstallIssue { get => _teacherInstallIssue; private set { _teacherInstallIssue = value; Changed(); Changed(nameof(HasTeacherInstallIssue)); } }
    public bool HasTeacherInstallIssue => TeacherInstallIssue.Length > 0;
    public string PackageOutput { get => _packageOutput; private set { _packageOutput = value; Changed(); Changed(nameof(HasPackageOutput)); } }
    public string PackageOutputError { get => _packageOutputError; set { _packageOutputError = value ?? ""; Changed(); Changed(nameof(HasPackageOutputError)); } }
    public bool HasPackageOutput => PackageOutput.Length > 0;
    public bool HasPackageOutputError => PackageOutputError.Length > 0;
    public IReadOnlyList<string> RoomNames { get => _roomNames; private set { _roomNames = value; Changed(); Changed(nameof(HasRoomPreview)); } }
    public bool HasRoomPreview => RoomNames.Count > 0;
    public string RoomError { get => _roomError; private set { _roomError = value; Changed(); Changed(nameof(HasRoomError)); } }
    public bool HasRoomError => RoomError.Length > 0;
    public string RoomSummary => HasRoomPreview ? $"共 {RoomNames.Count} 台，首台 {RoomNames[0]}，末台 {RoomNames[^1]}" : "尚未生成清单";
#endif
    public string InstallerStatus { get => _installerStatus; private set { _installerStatus = value; Changed(); } }

#if !STUDENT_SETUP_APP
    public void Navigate(bool student)
    {
        if (_isStudent && !student) ClearAccountPasswords();
        _isStudent = student;
        Changed(nameof(IsStudent)); Changed(nameof(IsTeacher)); Changed(nameof(IsStudentControlsEnabled));
        Changed(nameof(CanVerifyStudentDeployment)); Changed(nameof(CanFinishStudentSetup));
        Changed(nameof(PageTitle)); Changed(nameof(PageDescription));
    }
#endif
    public void Reset()
    {
        _package = null;
        _deploymentInstallerPath = null;
        Changed(nameof(LoadedPackage));
        _campus = ""; Changed(nameof(Campus));
        _prefix = "PC-"; Changed(nameof(Prefix));
        Number = ""; StudentAccountName = "User"; AdminAccountName = "Administrator";
        ClearAccountPasswords();
        InstallVeyon = false; RenameComputer = false; CreateStudent = false; ChangeAdminPassword = false;
        PackageStatus = "未选择校区配置包";
        ExecutionText = "";
        Changed(nameof(ComputerName)); Invalidate();
    }
    public void ClearPackage()
    {
        _package = null;
        _deploymentInstallerPath = null;
        PackageStatus = "已清除校区配置包；其他表单输入和操作选择已保留。";
        PackageError = "";
        Changed(nameof(LoadedPackage));
        Invalidate();
    }
    public async Task LoadPackageAsync(string path)
    {
        ClearPackageSelection();
        var busy = false;
        try
        {
            var directory = PackageSource.Resolve(path);
            var loaded = PackageContext.Load(directory);
            var installerPath = loaded.InstallerPath;
            if (installerPath is null)
            {
                SetBusy(true);
                busy = true;
                PackageStatus = "校区公钥已读取，正在准备 App 内嵌的离线 Veyon 安装器……";
                InstallerStatus = "正在从 App 内嵌资源提取并校验 Veyon 安装器……";
                var acquired = await AcquireInstallerWithProgressAsync();
                installerPath = acquired.InstallerPath;
                InstallerStatus = acquired.ExtractedFromApp
                    ? $"已从 App 内嵌资源提取并校验 Veyon {VeyonInstallerTrust.Version}。"
                    : $"已复用并校验本机缓存中的 Veyon {VeyonInstallerTrust.Version}。";
            }
            _package = loaded;
            _deploymentInstallerPath = installerPath;
            Changed(nameof(LoadedPackage));
            _campus = loaded.Campus; Changed(nameof(Campus));
            _prefix = loaded.ComputerPrefix; Changed(nameof(Prefix)); Changed(nameof(ComputerName));
            var packageKind = loaded.SchemaVersion == 0 ? "旧版配置" : loaded.SchemaVersion == 1 ? "旧版含安装器部署包" : "新版轻量配置包";
            PackageStatus = $"已读取：{directory}\n校区：{loaded.Campus} · 电脑名前缀：{loaded.ComputerPrefix}\n{packageKind}，RSA 公钥指纹 {loaded.PublicKeyFingerprint[..12]}…；App 内嵌安装器已就绪；未读取 admin.txt。";
            Invalidate();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or
                                   System.Text.Json.JsonException or ArgumentException)
        {
            ReportPackageError($"无法读取校区配置包：{ex.Message}");
        }
        finally
        {
            if (busy) SetBusy(false);
        }
    }
    public void LoadPackage(string path) => LoadPackageAsync(path).GetAwaiter().GetResult();
    public void RejectPackage(string message)
    {
        ClearPackageSelection();
        ReportPackageError(message);
    }
    public void GeneratePreview()
    {
        Error = ""; PreviewText = "";
        try
        {
            var operations = new OperationSelection(InstallVeyon, RenameComputer, CreateStudent, ChangeAdminPassword);
            var plan = DeploymentPlan.Create(new PlanInput(Campus, Prefix, Number, StudentAccountName,
                AdminAccountName, operations, _package));
            var header = $"目标计算机：{Environment.MachineName}\n已选操作：{(InstallVeyon ? "Veyon " : "")}{(RenameComputer ? "改名 " : "")}" +
                         $"{(CreateStudent ? "创建学生账户 " : "")}{(ChangeAdminPassword ? "修改管理员密码" : "")}";
            if (plan.ComputerName is not null) header += $"\n目标电脑名：{plan.ComputerName}";
            if (plan.Campus is not null) header += $"\n校区：{plan.Campus}";
            var risks = new List<string>();
            if (InstallVeyon)
            {
                risks.Add($"安装固定版本 Veyon {VeyonInstallerTrust.Version} x64 学生组件；不安装 Master、拦截驱动或开始菜单项。");
                risks.Add("安装可能要求重启；App 不自动回滚，失败后已经完成的步骤可能保留。");
                risks.Add("“安装 Veyon”只执行安装；“配置并部署”会继续切换密钥认证、导入校区公钥并重启 VeyonService。");
            }
            if (CreateStudent) risks.Add("新建账户需要两次输入一致的初始密码；Windows 本机密码策略负责最终校验。已有合格普通账户会跳过，不会重置其密码。");
            if (ChangeAdminPassword) risks.Add("管理员密码不可读回或自动恢复；请确认目标本地账户，并保留可用的恢复管理员方式。");
            if (RenameComputer) risks.Add("改名可能需要重启；App 不自动改回原电脑名。");
            PreviewText = header + "\n\n" +
                string.Join("\n\n", plan.Steps.Select((step, i) => $"{i + 1}. {step.Description}")) +
                "\n\n执行影响与恢复限制\n" + string.Join("\n", risks.Select(risk => "• " + risk)) +
                "\n\n请核对目标与步骤，再使用对应的“确认计划并执行”按钮。";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or
                                   System.Text.Json.JsonException or ArgumentException)
        {
            Error = ex.Message;
        }
    }

    public async Task RefreshVeyonStatusAsync()
    {
        if (IsDetectingVeyon || IsExecuting || IsPreparingDeployment) return;
        var requestId = ++_veyonProbeRequestId;
        IsDetectingVeyon = true;
        VeyonStatusText = "正在只读检查本机 Veyon 安装状态……";
        try
        {
            var facts = await Task.Run(VeyonFacts.Probe);
            if (requestId != _veyonProbeRequestId) return;
            VeyonStatusText = facts.Status switch
            {
                VeyonFacts.NotApplicable => facts.AsText(),
                VeyonFacts.NotInstalled => $"未检测到 Veyon。准备部署时还会复核；若计划包含 Veyon 操作，将使用 App 内嵌的固定版本 {VeyonInstallerTrust.Version}。\n{facts.AsText()}",
                "installed" when VeyonFacts.IsSupportedVersionDetail(facts.VersionDetail) =>
                    $"已检测到兼容的 Veyon {VeyonInstallerTrust.Version}。部署会跳过安装步骤，并继续执行已选的配置操作。\n{facts.AsText()}",
                "installed" => $"检测到 Veyon，但版本与固定基线 {VeyonInstallerTrust.Version} 不一致或无法确认；只读检查会阻止 Veyon 操作，避免覆盖未知安装。\n{facts.AsText()}",
                _ => $"无法确认 Veyon 安装状态；只读检查会显示详情，不能确认前不会覆盖或重复安装。\n{facts.AsText()}"
            };
        }
        catch (Exception exception)
        {
            if (requestId == _veyonProbeRequestId)
                VeyonStatusText = $"读取 Veyon 状态失败；不能据此判定未安装。准备检查会再次尝试。\n{exception.Message}";
        }
        finally
        {
            if (requestId == _veyonProbeRequestId) IsDetectingVeyon = false;
        }
    }

    public async Task PrepareDeploymentAsync()
    {
        if (!CanPrepareDeployment) return;
        IsPreparingDeployment = true;
        PreparationStatusText = "正在生成计划并进行只读环境检查……此过程不会修改系统。";
        try
        {
            ClearPreflight();
            GeneratePreview();
            if (!HasPreview || HasGlobalError) return;
            await CheckEnvironmentAsync();
        }
        finally
        {
            IsPreparingDeployment = false;
            PreparationStatusText = "";
        }
    }

    private void ClearPreflight()
    {
        _preflightRequestId++;
        _preflightReport = null;
        _preflightInput = null;
        PreflightText = "";
        NotifyExecutionAvailabilityChanged();
    }
    public void CheckEnvironment()
    {
        BeginEnvironmentCheck();
        try
        {
            var input = CurrentPlanInput();
            var report = ReadOnlyPreflight.Check(input, _deploymentInstallerPath);
            DisplayPreflight(input, report);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or
                                   System.Text.Json.JsonException or ArgumentException)
        {
            Error = ex.Message;
        }
    }
    public async Task CheckEnvironmentAsync()
    {
        var requestId = BeginEnvironmentCheck();
        try
        {
            var input = CurrentPlanInput();
            var installerPath = _deploymentInstallerPath;
            var report = await Task.Run(() => ReadOnlyPreflight.Check(input, installerPath));
            if (requestId != _preflightRequestId || input != CurrentPlanInput()) return;
            DisplayPreflight(input, report);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or
                                   System.Text.Json.JsonException or ArgumentException)
        {
            if (requestId == _preflightRequestId) Error = ex.Message;
        }
    }

    public async Task VerifyStudentDeploymentAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        StudentDeploymentVerificationText = "";
        StudentSetupCleanupText = "";
        try
        {
            var package = LoadedPackage;
            if (package is null)
            {
                StudentDeploymentVerificationText = "请先重新载入本校区学生配置包，再运行部署后只读验证。";
                return;
            }
            var report = await Task.Run(() => StudentDeploymentVerification.Check(package));
            if (!ReferenceEquals(package, LoadedPackage))
            {
                StudentDeploymentVerificationText = "验证期间配置包发生变化；结果已丢弃，请重新载入后检查。";
                return;
            }
            _studentDeploymentVerification = report;
            StudentDeploymentVerificationText = $"检查时间：{report.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" +
                Environment.NewLine + Environment.NewLine + report.AsText();
            _studentSetupCleanupAvailable = StudentSetupBundleCleanup.CanSchedule(AppContext.BaseDirectory,
                out var cleanupDetail);
            StudentSetupCleanupAvailability = _studentSetupCleanupAvailable
                ? cleanupDetail
                : $"验证结果不会删除任何文件。{cleanupDetail}";
            if (report.IsReadyToRemoveSetupTool && _studentSetupCleanupAvailable)
                StudentDeploymentVerificationText += Environment.NewLine + Environment.NewLine +
                    "后台组件已读回确认。可关闭并清理便携式 GUI 部署工具；Veyon 与独立网站代理会继续运行。";
            else
                StudentDeploymentVerificationText += Environment.NewLine + Environment.NewLine +
                    "有后台组件未能确认；清理操作保持禁用。请先处理未通过项后重新检查。";
            Changed(nameof(CanFinishStudentSetup));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            _studentDeploymentVerification = null;
            _studentSetupCleanupAvailable = false;
            StudentSetupCleanupAvailability = "只读验证失败；没有删除文件。";
            StudentDeploymentVerificationText = "部署后只读验证未完成：" + exception.Message;
        }
        catch (Exception exception)
        {
            // Read-only verification must surface unexpected failures in the UI rather than
            // let an async button event terminate the setup application.
            _studentDeploymentVerification = null;
            _studentSetupCleanupAvailable = false;
            StudentSetupCleanupAvailability = "只读验证异常中止；没有删除文件。";
            StudentDeploymentVerificationText = "部署后只读验证遇到未预期错误；没有删除文件：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    public async Task<bool> FinishStudentSetupAsync()
    {
        if (!CanFinishStudentSetup)
        {
            Error = "只有在 Veyon 和适用的网站后台代理均通过只读验证后，才能清理便携 GUI 部署工具。";
            return false;
        }
        if (!TryBeginExclusiveTask()) return false;
        try
        {
            var process = Process.GetCurrentProcess();
            var result = await Task.Run(() => StudentSetupBundleCleanup.Schedule(AppContext.BaseDirectory,
                process.Id, process.StartTime.ToUniversalTime().Ticks));
            StudentSetupCleanupText = result.Detail;
            if (!result.Ok)
            {
                Error = result.Detail;
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or ArgumentException)
        {
            Error = "无法安排清理；没有删除部署工具：" + exception.Message;
            return false;
        }
        finally { EndExclusiveTask(); }
    }
    private int BeginEnvironmentCheck()
    {
        Error = ""; PreflightText = "";
        _preflightReport = null; _preflightInput = null;
        NotifyExecutionAvailabilityChanged();
        return ++_preflightRequestId;
    }
    private PlanInput CurrentPlanInput() => new(Campus, Prefix, Number, StudentAccountName, AdminAccountName,
        new OperationSelection(InstallVeyon, RenameComputer, CreateStudent, ChangeAdminPassword), _package);
    private void DisplayPreflight(PlanInput input, PreflightReport report)
    {
        _preflightReport = report;
        _preflightInput = input;
        NotifyExecutionAvailabilityChanged();
        PreflightText = $"检查时间：{report.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 计划摘要：{report.PlanSha256[..12]}…" +
            (report.PackageSha256 is null ? "" : $" · 校区配置摘要：{report.PackageSha256[..12]}…") + "\n\n" +
            string.Join("\n\n", report.Checks.Select(c =>
            $"{(c.Level == CheckLevel.Pass ? "✓" : c.Level == CheckLevel.Blocked ? "✗" : c.Level == CheckLevel.NotApplicable ? "—" : "?")} {c.Detail}"));
    }
#if !STUDENT_SETUP_APP
    public void GenerateRoomPreview()
    {
        RoomNames = Array.Empty<string>(); RoomError = "";
        try
        {
            RoomNames = MachineNaming.CreateRange(RoomPrefix, RoomStart, RoomCount);
            Changed(nameof(RoomSummary));
        }
        catch (InvalidDataException ex) { RoomError = ex.Message; }
    }
    public void CloseRoomPreview() => ClearRoomPreview();
    public void FillWebsiteTargetsFromRoom()
    {
        try
        {
            var names = RoomNames.Count > 0 ? RoomNames : MachineNaming.CreateRange(RoomPrefix, RoomStart, RoomCount);
            WebsiteTargets = string.Join(Environment.NewLine, names);
            RoomError = "";
        }
        catch (InvalidDataException exception) { RoomError = exception.Message; }
    }

    public async Task PushWebsitePolicyAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        WebsitePolicyResult = "";
        WebsitePolicyError = "";
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("网站策略推送仅支持 Windows 教师端。" );
            var campus = CampusId.Trim();
            WebsitePolicySigningKeyStore.ValidateCampusId(campus);
            var targets = WebsitePolicyTransport.NormalizeTargets(WebsiteTargets.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            var mode = WebsiteModeIndex switch
            {
                0 => WebsitePolicyMode.Disabled,
                1 => WebsitePolicyMode.Blocklist,
                2 => WebsitePolicyMode.Allowlist,
                _ => throw new InvalidDataException("网站策略模式无效。" )
            };
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
                _ => throw new InvalidDataException("网站限制时长无效。" )
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

    public Task DisableWebsitePolicyAsync()
    {
        WebsiteModeIndex = 0;
        return PushWebsitePolicyAsync();
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
            if (WebsiteModeIndex != 0)
                WebsitePolicyCompiler.NormalizeDomains(WebsiteDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            return WebsiteModeIndex == 0 || WebsiteDomains.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
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

    public async Task GenerateStudentPackageAsync()
    {
        if (!TryBeginExclusiveTask()) return;
        PackageOutput = ""; PackageOutputError = "";
        string? temporaryPublicKey = null;
        var teacherKeyCreated = false;
        var campus = CampusId.Trim();
        try { WebsitePolicySigningKeyStore.ValidateCampusId(campus); }
        catch (InvalidDataException exception)
        {
            PackageOutputError = exception.Message;
            return;
        }

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

        try
        {
            MachineNaming.CreateRange(RoomPrefix, "1", "150");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var outDir = string.IsNullOrWhiteSpace(RoomOutputDir)
                ? Path.Combine(desktop, "Veyon-学生配置包-" + campus)
                : Path.GetFullPath(RoomOutputDir);
            if (Directory.Exists(outDir) || File.Exists(outDir))
            {
                PackageOutputError = "输出目录已存在，为防止覆盖现有密钥，请改路径或先查清原目录内容。";
                return;
            }

            using var websiteSigningKey = WebsitePolicySigningKeyStore.GetOrCreate(campus);
            var publicKeyExportPath = Path.Combine(Path.GetTempPath(), "VeyonCampus-public-" + Guid.NewGuid().ToString("N") + ".pem");
            temporaryPublicKey = publicKeyExportPath;
            InstallerStatus = "正在检查 Veyon 密钥库并仅导出校区配置所需公钥……";
            var keyResult = await Task.Run(() => new VeyonTeacherKeyProvisioner()
                .ExportPublicKey(campus, publicKeyExportPath));
            teacherKeyCreated = keyResult.Created;
            if (!keyResult.Step.Ok)
            {
                PackageOutputError = keyResult.Step.Detail +
                    (teacherKeyCreated ? "\n本次已在 Veyon 密钥库创建密钥对，密钥保留在那里；没有导出教师私钥。" : "");
                return;
            }

            var built = await Task.Run(() => PackageBuilder.Build(outDir, campus, RoomPrefix,
                publicKeyExportPath, websiteSigningKey.PublicKeyPem));
            PackageOutput = $"已生成学生校区配置包：{built}\n{keyResult.Step.Detail}\nVeyon 教师私钥仍在 Veyon 受控密钥目录；网站策略签名私钥仅在当前教师 Windows 用户证书库内，学生包只含网站策略公钥。\nVeyon {VeyonInstallerTrust.Version} 安装器已内嵌在 VeyonCampus App 中，学生电脑无需联网下载。\n请将完整 VeyonCampus App 与此配置包一起分发。";
        }
        catch (Exception ex)
        {
            PackageOutputError = "生成失败：" + ex.Message;
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
            var adapter = _adapter ??= new WindowsVeyonAdapter();
            var installed = await Task.Run(VeyonFacts.Probe);
            if (installed.Status == "installed")
            {
                TeacherInstallIssue = $"检测到本机已安装 Veyon，请勿重复安装。\n{installed.AsText()}";
                return;
            }
            if (installed.Status != VeyonFacts.NotInstalled)
            {
                TeacherInstallIssue = $"无法确认本机 Veyon 安装状态；为避免覆盖或重复安装，已停止。\n{installed.AsText()}";
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
                TeacherInstallResult = $"{install.Detail}\n安装读回：{verification.Detail}\n\n教师端 Veyon 已安装。认证密钥、日常教师账户权限和机房电脑目录仍需后续配置。";
            else
                TeacherInstallIssue = $"{install.Detail}\n安装读回需人工核对：{verification.Detail}";
            InstallerStatus = acquired.ExtractedFromApp
                ? $"已从 App 内嵌资源提取并验证 Veyon {VeyonInstallerTrust.Version}；后续使用本机校验缓存。"
                : $"已复用并验证本机缓存中的 App 内嵌 Veyon {VeyonInstallerTrust.Version}。";
        }
        catch (Exception ex)
        {
            TeacherInstallIssue = "安装未完成：" + ex.Message;
        }
        finally
        {
            EndExclusiveTask();
        }
    }
#endif

    private Task<InstallerStoreResult> AcquireInstallerWithProgressAsync()
    {
        var progress = new Progress<InstallerResourceProgress>(value =>
        {
            var percent = value.TotalBytes > 0 ? Math.Clamp(value.BytesReceived * 100 / value.TotalBytes, 0, 100) : 0;
            InstallerStatus = $"正在释放 App 内嵌 Veyon {VeyonInstallerTrust.Version}：{percent}%（{value.BytesReceived / 1024 / 1024} / {value.TotalBytes / 1024 / 1024} MB）";
        });
        return _installerStore.EnsureAvailableAsync(progress);
    }

    private void SetBusy(bool busy)
    {
        _isExecuting = busy;
        Changed(nameof(IsExecuting));
        Changed(nameof(CanVerifyStudentDeployment)); Changed(nameof(CanFinishStudentSetup));
#if !STUDENT_SETUP_APP
        Changed(nameof(CanPushWebsitePolicy));
        Changed(nameof(CanDisableWebsitePolicy));
        Changed(nameof(CanFillFailedWebsiteTargets));
#endif
        NotifyExecutionAvailabilityChanged();
    }

    /// <summary>
    /// Synchronously takes the in-process task slot before the first await
    /// (AR-01); returns false when another entry already holds it.
    /// </summary>
    private bool TryBeginExclusiveTask()
    {
        if (!_lease.TryAcquire(out var denial))
        {
            Error = denial;
            return false;
        }
        SetBusy(true);
        return true;
    }

    private void EndExclusiveTask()
    {
        _lease.Dispose();
        SetBusy(false);
    }
    public void ToggleOperationHelp(string message) => OperationHelpText = OperationHelpText == message ? "" : message;
    public void ReportError(string message) { PreviewText = ""; PreflightText = ""; Error = message; }

    /// <summary>第一步：单独安装 Veyon（学生组件，不装 Master）。安装完成后
    /// 系统进入"已装 Veyon"状态，再进行第二步部署（配置公钥/账户/改名）。</summary>
    public async Task InstallVeyonOnlyAsync()
    {
        ExecutionText = ""; Error = "";
        if (!TryBeginExclusiveTask()) return;
        try
        {
            if (!InstallVeyon || RenameComputer || CreateStudent || ChangeAdminPassword)
            {
                Error = "安装入口只接受明确选择 Veyon 且未选择改名或账户操作的计划。";
                return;
            }
            var deploymentInstallerPath = _deploymentInstallerPath;
            if (LoadedPackage is null || deploymentInstallerPath is null)
            {
                Error = "请先载入校区公钥配置包，并确认 App 内嵌 Veyon 安装器已就绪。";
                return;
            }
            var frozenPlan = await FreezeAndValidateExecutionPlanAsync();
            if (frozenPlan?.Package is not { } frozenPackage) return;
            var adapter = _adapter ??= new WindowsVeyonAdapter();
            using var snapshot = PackageResourceSnapshot.Create(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VeyonCampus", "snapshots"), frozenPackage);
            snapshot.VerifyUnchanged();
            if (_deploymentInstallerPath != deploymentInstallerPath)
            {
                Error = "校区配置或安装资源在计划确认期间发生变化；未执行，请重新检查。";
                return;
            }
            var installFacts = await Task.Run(VeyonFacts.Probe);
            var installerResult = installFacts.Status == VeyonFacts.NotInstalled
                ? await Task.Run(() => adapter.InstallVeyonOnly(frozenPackage, deploymentInstallerPath, isTeacher: false))
                : installFacts.Status == "installed" && VeyonFacts.IsSupportedVersionDetail(installFacts.VersionDetail)
                    ? new StepResult("veyon-install", ExecutionPlan.Skipped,
                        $"已检测到固定版本 Veyon {VeyonInstallerTrust.Version}，跳过安装器。此入口仅负责安装，不会导入公钥；如需导入校区公钥，请点“确认并执行所选操作”。")
                    : new StepResult("veyon-install", ExecutionPlan.NeedsReview,
                        $"无法确认现有 Veyon 安装状态或版本；为避免覆盖或重复安装，已停止。{installFacts.AsText()}");
            var verification = await Task.Run(() => WindowsVeyonAdapter.Verify(frozenPackage));
            var stepResults = new List<StepResult> { installerResult };
            if (installerResult.Ok &&
                (verification.InstallState != "已安装" || !VeyonFacts.IsSupportedVersionDetail(verification.Version) ||
                 !verification.ServiceState.Contains("正在运行", StringComparison.Ordinal)))
                stepResults.Add(new("verify", ExecutionPlan.NeedsReview,
                    "安装器返回成功，但安装状态、固定版本或 VeyonService 运行状态未全部确认。"));
            var summary = ExecutionPlan.Summarize(stepResults);
            var lines = new List<string>
            {
                "安装结果",
                $"整体状态：{summary.Status}" + (summary.RebootRequired ? " · 需要重启" : ""),
                installerResult.Detail,
                "",
                "读回验证",
                $"安装状态：{verification.InstallState} · {verification.Version}",
                $"服务：{verification.ServiceState}"
            };
            if (installerResult.RebootRequired)
                lines.Add("安装器要求重启；当前不继续配置，请重启后重新检查。");
            else if (!installerResult.Ok)
                lines.Add("安装未成功；未继续配置步骤。请先解决安装问题。");
            else if (summary.Status != ExecutionPlan.Succeeded)
                lines.Add("安装器返回成功，但读回结果需人工核对；未继续配置步骤。");
            else lines.Add("安装步骤完成；请重新检查环境后，再运行公钥配置。");
            ExecutionText = string.Join("\n", lines);
            InvalidatePreflightAndPreview();
        }
        catch (Exception ex)
        {
            Error = $"安装执行失败：{ex.Message}";
        }
        finally
        {
            EndExclusiveTask();
            if (InstallVeyon) await RefreshVeyonStatusAsync();
        }
    }

    /// <summary>按冻结计划执行所选账户 → Veyon → 改名步骤。
    /// 各步骤在安全边界取消；失败或待重启停止后续，不自动回滚已完成修改。</summary>
    public async Task RunDeploymentAsync()
    {
        ExecutionText = ""; Error = "";
        if (!TryBeginExclusiveTask()) return;
        try
        {
            var operations = new OperationSelection(InstallVeyon, RenameComputer, CreateStudent, ChangeAdminPassword);
            if (!operations.Any)
            {
                Error = "请至少勾选一项操作再开始部署。";
                return;
            }
            if (!HasRequiredAccountCredentials())
            {
                Error = GetAccountPasswordValidationError();
                return;
            }
            var studentPassword = _studentPassword;
            var adminPassword = _adminPassword;
            var studentConfirmation = _studentPasswordConfirmation;
            var adminConfirmation = _adminPasswordConfirmation;

            var deploymentInstallerPath = _deploymentInstallerPath;
            if (operations.InstallVeyon && (LoadedPackage is null || deploymentInstallerPath is null))
            {
                Error = "Veyon 操作请先载入校区公钥配置包，并确认 App 内嵌 Veyon 安装器已就绪。";
                return;
            }
            var frozenPlan = await FreezeAndValidateExecutionPlanAsync();
            if (frozenPlan is null) return;
            if (_studentPassword != studentPassword || _studentPasswordConfirmation != studentConfirmation ||
                _adminPassword != adminPassword || _adminPasswordConfirmation != adminConfirmation)
            {
                Error = "密码输入在计划确认期间发生变化；未执行，请重新核对并检查。";
                return;
            }
            var frozenPackage = operations.InstallVeyon ? frozenPlan.Package : null;

            var adapter = _adapter ??= new WindowsVeyonAdapter();
            var accountAdapter = new WindowsAccountAdapter(_launcher);
            // The snapshot protects the public key bytes the Veyon CLI will
            // consume; only plans that include Veyon carry a package context.
            using var snapshot = frozenPackage is not null
                ? PackageResourceSnapshot.Create(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "VeyonCampus", "snapshots"), frozenPackage)
                : null;

            var runLog = DeploymentRunLog.Create(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VeyonCampus", "runs"), frozenPlan.PlanFingerprint);
            var renameAdapter = new WindowsRenameAdapter(_launcher);

            // Domain check gates rename before any modification (P5-03).
            if (frozenPlan.Input.Operations.RenameComputer)
            {
                var domain = await Task.Run(renameAdapter.CheckDomainMembership);
                if (!domain.Ok)
                {
                    runLog.Finish(domain.Status, false);
                    ExecutionText = $"部署结果\n整体状态：{domain.Status}\n{domain.Detail}\n改名被阻断；其他步骤未开始。\n执行记录：{runLog.LogPath}";
                    InvalidatePreflightAndPreview();
                    return;
                }
            }

            var executionSummary = await ExecutionCoordinator.RunAsync(frozenPlan, async step =>
            {
                try { snapshot?.VerifyUnchanged(); }
                catch (Exception ex)
                {
                    return new StepResult(step.Id, ExecutionPlan.NeedsReview,
                        $"执行资源在步骤开始前发生变化：{ex.Message}");
                }
                StepResult result;
                switch (step.Id)
                {
                    case "student-account":
                    {
                        var accountSnapshot = frozenPlan.Accounts;
                        result = accountSnapshot is not null &&
                                 accountSnapshot.StudentAccountName == frozenPlan.Input.StudentAccountName
                            ? await Task.Run(() => accountAdapter.CreateStudentAccount(
                                frozenPlan.Input.StudentAccountName,
                                studentPassword.Length == 0 ? null : studentPassword, accountSnapshot.StudentSid))
                            : new(step.Id, ExecutionPlan.NeedsReview,
                                "学生账户或预检 SID 快照缺失；未开始账户修改。");
                        break;
                    }
                    case "admin-password":
                    {
                        var accountSnapshot = frozenPlan.Accounts;
                        result = accountSnapshot is not null &&
                                 accountSnapshot.AdminAccountName == frozenPlan.Input.AdminAccountName &&
                                 !string.IsNullOrWhiteSpace(accountSnapshot.AdminSid) && adminPassword.Length > 0
                            ? await Task.Run(() => accountAdapter.ChangeAdminPassword(
                                frozenPlan.Input.AdminAccountName, adminPassword, accountSnapshot.AdminSid))
                            : new(step.Id, ExecutionPlan.NeedsReview,
                                "管理员账户、密码或预检 SID 快照缺失；未开始密码修改。");
                        break;
                    }
                    case "veyon-install":
                        if (frozenPackage is null || deploymentInstallerPath is null)
                            result = new(step.Id, ExecutionPlan.Failed, "缺少已校验的 Veyon 安装器；无法安装。");
                        else
                        {
                            var facts = await Task.Run(VeyonFacts.Probe);
                            if (facts.Status == "installed" && !VeyonFacts.IsSupportedVersionDetail(facts.VersionDetail))
                            {
                                result = new(step.Id, ExecutionPlan.NeedsReview,
                                    $"当前安装版本不是已验证的 Veyon {VeyonInstallerTrust.Version}，没有继续配置。{facts.VersionDetail}");
                                break;
                            }
                            if (facts.Status != VeyonFacts.NotInstalled && facts.Status != "installed")
                            {
                                result = new(step.Id, ExecutionPlan.NeedsReview,
                                    "无法确认现有 Veyon 安装状态；为避免覆盖未知安装，没有继续。");
                                break;
                            }
                            result = facts.Status == VeyonFacts.NotInstalled
                                ? await Task.Run(() => adapter.InstallVeyonOnly(frozenPackage, deploymentInstallerPath, isTeacher: false))
                                : new(step.Id, ExecutionPlan.Skipped,
                                    $"已安装固定版本 Veyon {VeyonInstallerTrust.Version}，跳过安装步骤。");
                        }
                        break;
                    case "veyon-key":
                        result = frozenPackage is null
                            ? new(step.Id, ExecutionPlan.Failed, "缺少校区配置包；无法配置公钥。")
                            : await Task.Run(() => adapter.ConfigureVeyonOnly(frozenPackage, snapshot!, isTeacher: false));
                        break;
                    case "website-agent":
                        result = frozenPackage is null || snapshot is null
                            ? new(step.Id, ExecutionPlan.Failed, "缺少学生校区配置包快照；无法安装网站策略代理。")
                            : OperatingSystem.IsWindows()
                                ? await Task.Run(() => WebsitePolicyAgentInstaller.Install(frozenPackage, snapshot))
                                : new(step.Id, ExecutionPlan.Failed, "学生网站策略代理仅支持 Windows。" );
                        break;
                    case "rename":
                        result = await Task.Run(() =>
                            renameAdapter.RequestRename(DeploymentPlan.ComputerNameFor(frozenPlan.Input)));
                        break;
                    default:
                        result = new(step.Id, ExecutionPlan.NeedsReview,
                            "当前执行器不支持冻结计划中的步骤；没有继续后续操作。");
                        break;
                }
                runLog.ReportEvent("step", step.Id, result, result.ExitCode);
                return result;
            });
            var verification = frozenPackage is not null
                ? await Task.Run(() => new WindowsVeyonVerificationService(_launcher)
                    .Verify(frozenPackage, isTeacher: false)) : null;
            if (verification is not null && executionSummary.Steps.Any(step =>
                    step.StepId == "veyon-key" && step.Status == ExecutionPlan.Succeeded))
            {
                var verified = verification.ToStepResult();
                runLog.ReportEvent("verification", verified.StepId, verified, verified.ExitCode);
                executionSummary = ExecutionPlan.Summarize(executionSummary.Steps.Append(verified));
            }
            runLog.Finish(executionSummary.Status, executionSummary.RebootRequired);
            var lines = new List<string>
            {
                "部署结果",
                $"整体状态：{executionSummary.Status}" + (executionSummary.RebootRequired ? " · 需要重启" : "")
            };
            lines.AddRange(executionSummary.Steps.Select(step => $"{step.StepId}：{step.Detail}"));
            if (verification is not null)
            {
                lines.Add("");
                lines.Add("读回验证");
                lines.Add(verification.Detail);
            }
            lines.Add("");
            lines.Add("执行记录：" + runLog.LogPath);
            ExecutionText = string.Join("\n", lines);
            InvalidatePreflightAndPreview();
        }
        catch (Exception ex)
        {
            Error = $"部署执行失败：{ex.Message}";
        }
        finally
        {
            ClearAccountPasswords();
            EndExclusiveTask();
            if (InstallVeyon) await RefreshVeyonStatusAsync();
        }
    }

    private bool HasCurrentExecutablePreflight()
    {
        var report = _preflightReport;
        var preflightInput = _preflightInput;
        if (!ReadOnlyPreflight.IsExecutable(report) || report is null || preflightInput is null ||
            DateTimeOffset.UtcNow - report.CheckedAt > TimeSpan.FromMinutes(5)) return false;
        return CurrentPlanInput() == preflightInput;
    }
    private async Task<ExecutionPlan?> FreezeAndValidateExecutionPlanAsync()
    {
        if (!HasPreview) { Error = "请先生成并核对当前操作计划预览。"; return null; }
        if (!OperatingSystem.IsWindows()) { Error = "仅支持在 Windows 上执行。"; return null; }
        if (_preflightReport?.HasBlocker == true)
        {
            Error = "执行前检查存在阻断项：" + string.Join("；", _preflightReport.Checks
                .Where(c => c.Level == CheckLevel.Blocked).Select(c => c.Detail));
            return null;
        }
        var confirmedReport = _preflightReport;
        var confirmedInput = _preflightInput;
        var confirmedInstallerPath = _deploymentInstallerPath;
        var currentInput = new PlanInput(Campus, Prefix, Number, StudentAccountName, AdminAccountName,
            new OperationSelection(InstallVeyon, RenameComputer, CreateStudent, ChangeAdminPassword), _package);
        if (confirmedReport is null || confirmedInput is null || !HasCurrentExecutablePreflight())
        { Error = "执行前检查缺失、已过期或与当前资料不一致。请重新检查。"; return null; }
        try
        {
            var result = await Task.Run(() =>
            {
                var report = ReadOnlyPreflight.Check(confirmedInput, confirmedInstallerPath);
                if (!ReadOnlyPreflight.IsExecutable(report))
                {
                    var privilege = report.Checks.FirstOrDefault(check => check.Id == "privilege");
                    var reason = privilege?.Level == CheckLevel.Pass
                        ? "执行前检查存在阻断项：" + string.Join("；", report.Checks
                            .Where(c => c.Level == CheckLevel.Blocked).Select(c => c.Detail))
                        : "未能确认当前进程具有管理员权限；本版本没有 UAC 执行器，未执行修改。";
                    return (Plan: (ExecutionPlan?)null, Error: reason);
                }
                if (!string.Equals(report.PlanSha256, confirmedReport.PlanSha256, StringComparison.Ordinal) ||
                    !string.Equals(report.PackageSha256, confirmedReport.PackageSha256, StringComparison.Ordinal) ||
                    !report.Checks.SequenceEqual(confirmedReport.Checks))
                    return (Plan: (ExecutionPlan?)null, Error: "执行前系统状态或部署资料与已确认预检不一致，请重新检查。");
                var plan = ExecutionPlan.Create(confirmedInput, confirmedInput.Package, report.Accounts);
                plan.Package?.VerifyUnchanged();
                return (Plan: (ExecutionPlan?)plan, Error: (string?)null);
            });
            if (currentInput != confirmedInput || _preflightReport != confirmedReport ||
                _deploymentInstallerPath != confirmedInstallerPath)
            { Error = "计划冻结期间表单或预检发生变化；未执行，请重新检查。"; return null; }
            if (result.Plan is null) { Error = result.Error!; return null; }
            return result.Plan;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        { Error = "无法冻结当前部署计划：" + ex.Message; return null; }
    }
    private void ReportPackageError(string message) { PackageError = message; ReportError(message); }
    private void InvalidatePreflightAndPreview() { _preflightRequestId++; PreviewText = ""; PreflightText = ""; _preflightReport = null; _preflightInput = null; Error = ""; }
    private void ClearLoadedPackage()
    {
        if (_package is null) return;
        _package = null;
        _deploymentInstallerPath = null;
        Changed(nameof(LoadedPackage));
        PackageStatus = "校区或前缀已修改；旧公钥资料已失效，请重新选择校区配置包。";
    }
    private void ClearPackageSelection()
    {
        _package = null;
        _deploymentInstallerPath = null;
        Changed(nameof(LoadedPackage));
        _campus = ""; Changed(nameof(Campus));
        _prefix = "PC-"; Changed(nameof(Prefix)); Changed(nameof(ComputerName));
        PackageStatus = "未选择校区配置包";
        PackageError = "";
        Invalidate();
    }
#if !STUDENT_SETUP_APP
    private void ClearRoomPreview() { RoomNames = Array.Empty<string>(); RoomError = ""; Changed(nameof(RoomSummary)); }
#endif
    private bool HasRequiredAccountCredentials() => GetAccountPasswordValidationError().Length == 0;
    private string GetAccountPasswordValidationError()
    {
        if (CreateStudent)
        {
            var existingStudent = _preflightReport?.Accounts is { } snapshot &&
                                  _preflightInput == CurrentPlanInput() &&
                                  string.Equals(snapshot.StudentAccountName, StudentAccountName, StringComparison.Ordinal) &&
                                  !string.IsNullOrWhiteSpace(snapshot.StudentSid);
            if (!existingStudent)
            {
                var error = _studentPassword.Length == 0 && _studentPasswordConfirmation.Length == 0
                    ? ""
                    : ValidatePasswordPair(_studentPassword, _studentPasswordConfirmation, "学生初始密码");
                if (error.Length > 0) return error;
            }
        }
        if (ChangeAdminPassword)
        {
            var error = ValidatePasswordPair(_adminPassword, _adminPasswordConfirmation, "管理员新密码");
            if (error.Length > 0) return error;
        }
        return "";
    }
    private static string ValidatePasswordPair(string password, string confirmation, string label)
    {
        if (password.Length == 0 || confirmation.Length == 0) return $"请两次输入{label}。";
        if (password.Length > 127 || confirmation.Length > 127) return $"{label}最多 127 个字符。";
        if (password.Any(char.IsControl) || confirmation.Any(char.IsControl))
            return $"{label}不能包含换行或控制字符；首尾空格会按原样保留。";
        return string.Equals(password, confirmation, StringComparison.Ordinal)
            ? "" : $"两次输入的{label}不一致。";
    }
    private void ClearStudentPassword()
    {
        _studentPassword = "";
        _studentPasswordConfirmation = "";
        ClearStudentPasswordRequested?.Invoke(this, EventArgs.Empty);
        NotifyExecutionAvailabilityChanged();
    }
    private void ClearAdminPassword()
    {
        _adminPassword = "";
        _adminPasswordConfirmation = "";
        ClearAdminPasswordRequested?.Invoke(this, EventArgs.Empty);
        NotifyExecutionAvailabilityChanged();
    }
    private void ClearAccountPasswords()
    {
        ClearStudentPassword();
        ClearAdminPassword();
    }
    private string GetExecutionAvailabilityText(string action)
    {
        if (action == "仅安装" ? CanInstall : CanStartDeployment)
            return $"可执行“{action}”；请再次核对计划和目标电脑。";
        if (IsExecuting) return "当前任务仍在执行，请等待结果。";
        if (!HasRequiredAccountCredentials()) return GetAccountPasswordValidationError();
        if (action == "仅安装" && (!InstallVeyon || RenameComputer))
            return "不可执行：仅安装入口要求只选择 Veyon 操作。";
        if (!InstallVeyon && !RenameComputer && !CreateStudent && !ChangeAdminPassword)
            return "不可执行：请至少选择一项操作。";
        if (HasGlobalError) return "不可执行：先处理上方错误，再重新生成计划并检查环境。";
        if (!HasPreview) return "不可执行：先生成并核对当前计划预览。";
        if (InstallVeyon && LoadedPackage is null)
            return "不可执行：请选择包含有效校区公钥的配置包。";
        if (InstallVeyon && _deploymentInstallerPath is null)
            return "不可执行：尚未准备 App 内嵌的固定版本 Veyon 安装器。";
        if (!HasCurrentExecutablePreflight()) return "不可执行：先完成当前计划的只读环境检查，并解决所有阻断项。";
        return "不可执行：当前状态未满足执行条件，请重新生成计划并检查环境。";
    }
    private void NotifyExecutionAvailabilityChanged()
    {
        Changed(nameof(CanInstall));
        Changed(nameof(CanStartDeployment));
        Changed(nameof(IsStudentControlsEnabled));
        Changed(nameof(CanPrepareDeployment));
        Changed(nameof(CanRefreshVeyonStatus));
        Changed(nameof(InstallAvailabilityText));
        Changed(nameof(DeploymentAvailabilityText));
#if !STUDENT_SETUP_APP
        Changed(nameof(CanInstallTeacherVeyon));
        Changed(nameof(CanGenerateStudentPackage));
#endif
        Changed(nameof(CanVerifyStudentDeployment)); Changed(nameof(CanFinishStudentSetup));
    }
    private void Invalidate()
    {
        _preflightRequestId++;
        PreviewText = ""; PreflightText = ""; _preflightReport = null; _preflightInput = null;
        _studentDeploymentVerification = null; _studentSetupCleanupAvailable = false;
        StudentDeploymentVerificationText = ""; StudentSetupCleanupText = ""; StudentSetupCleanupAvailability = "";
        Error = ""; ExecutionText = "";
        Changed(nameof(NeedsVeyonPackage));
        NotifyExecutionAvailabilityChanged();
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
