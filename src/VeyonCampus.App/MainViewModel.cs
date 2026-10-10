using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed record StudentPreflightItem(string Name, string Hint, string StatusCode, string Status, string Detail)
{
    public bool IsPass => StatusCode == CheckLevel.Pass.ToString();
    public bool IsWarning => StatusCode == CheckLevel.Warning.ToString();
    public bool IsBlocked => StatusCode == CheckLevel.Blocked.ToString();
    public bool IsUnknown => StatusCode == CheckLevel.Unknown.ToString();
    public bool IsNotApplicable => StatusCode == CheckLevel.NotApplicable.ToString();
}

public sealed record StudentExecutionStepStatus(string StepId, string Name, string StatusCode, string Status, string Detail)
{
    public string DisplayName => StepId switch
    {
        "veyon-install" => "安装 Veyon",
        "veyon-key" => "配置 Veyon",
        "website-agent" => "更新学生代理",
        "rename" => "修改电脑名称",
        "student-account" => "创建学生账户",
        "admin-password" => "更新管理员密码",
        _ => Name.Length > 24 ? Name[..24] + "…" : Name
    };
    public bool IsSuccess => StatusCode == ExecutionPlan.Succeeded;
    public bool IsRunning => StatusCode == ExecutionPlan.Running;
    public bool IsError => StatusCode == ExecutionPlan.Failed;
    public bool IsWarning => StatusCode is ExecutionPlan.RequiresReboot or ExecutionPlan.PartiallyCompleted or ExecutionPlan.NeedsReview;
    public bool IsNeutral => StatusCode is ExecutionPlan.NotStarted or ExecutionPlan.Skipped or ExecutionPlan.Cancelled;
    public bool ShowDetail => (StepId == "website-agent" && !string.IsNullOrWhiteSpace(Detail)) || IsError || IsWarning;
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _campus = "", _prefix = "PC-", _number = "", _studentAccount = "User", _adminAccount = "Administrator";
    private string _studentPassword = "", _studentPasswordConfirmation = "";
    private string _adminPassword = "", _adminPasswordConfirmation = "";
    private string _error = "", _packageError = "", _preview = "", _preflight = "", _packageStatus = "未选择校区配置包", _operationHelp = "", _execution = "";
    private string _packageRecommendationNotice = "";
    private bool _applyingPackageRecommendations, _operationSelectionTouched;
#if !STUDENT_SETUP_APP
    private string _roomPrefix = "PC-", _roomStart = "1", _roomCount = "150", _roomError = "";
    private string _campusId = "", _roomOutputDir = "", _packageOutput = "", _packageOutputError = "";
    private string _websiteTargets = "", _websiteDomains = "", _websitePolicyResult = "", _websitePolicyError = "", _websitePolicyHistoryText = "";
    private int _websiteModeIndex = 1, _websiteDurationIndex = 1;
    private string _teacherInstallResult = "", _teacherInstallIssue = "";
    private IReadOnlyList<string> _roomNames = Array.Empty<string>();
    private IReadOnlyList<string> _lastFailedWebsiteTargets = Array.Empty<string>();
#endif
    private string _studentDeploymentVerificationText = "", _websiteAgentRemovalStatus = "", _websiteAgentInstallStatus = "";
    private string _cloudPackageQuery = "", _cloudPackageTeacherPhoneLast4 = "", _cloudPackageStatus = "输入校区名称，搜索云端已发布的配置包。";
    private bool _isSearchingCloudPackages, _isDownloadingCloudPackage;
    private DeploymentPackageCatalogEntry? _selectedCloudPackage;
    private string _installerStatus = "Veyon 安装器已内嵌在学生部署工具中；无需联网下载。";
    private string _veyonStatusText = "正在读取本机 Veyon 安装状态……", _veyonStatusSummary = "正在检查 Veyon…", _preparationStatusText = "";
    private string _preflightSummaryText = "", _executionOverallStatus = ExecutionPlan.NotStarted;
    private bool _installVeyon, _rename, _createStudent, _changeAdmin, _isExecuting = false;
    private bool _isDetectingVeyon, _isPreparingDeployment;
    private CancellationTokenSource? _stopAfterCurrentStep;
    private bool _stopAfterCurrentStepRequested;
    private int _wizardPage;
    private int _wizardReturnPage = -1;
    private int _highestCompletedWizardStep = -1;
    private int _veyonProbeRequestId;
    private bool _showingPreviousExecution;
    private bool _needsPreviousRunReview;
    private DeploymentRunHistory? _latestExecutionHistory;
    private string? _latestStateBackupPath;
    private string _stateBackupReviewText = "尚未读取快照。";
    private string _stateBackupComparisonText = "尚未比对。";
    private string _stateBackupSystemComparisonText = "尚未检查。";
    private string _stateBackupExportText = "";
    private bool _isReviewingStateBackup, _isComparingStateBackup, _isExportingStateBackup;
    private bool _stateBackupReviewSucceeded, _stateBackupHasVeyonConfig;
#if !STUDENT_SETUP_APP
    private bool _isStudent = true;
#endif
    private PackageContext? _package;
    private string _packageSourceLabel = "未选择";
    private string? _deploymentInstallerPath;
    private PreflightReport? _preflightReport;
    private PlanInput? _preflightInput;
    private PlanInput? _executionInput;
    private StudentDeploymentVerificationReport? _studentDeploymentVerification;
    private int _preflightRequestId;
    private WindowsVeyonAdapter? _adapter = new();
    private readonly VeyonInstallerStore _installerStore;
    private readonly ITaskLease _lease;
    private readonly IProcessLauncher _launcher = new DefaultProcessLauncher();
    private readonly DeploymentPackageCatalogClient _deploymentPackageCatalog = new();

    public MainViewModel(VeyonInstallerStore? installerStore = null)
    {
        _installerStore = installerStore ?? new VeyonInstallerStore();
        // The Mac build is only a UI preview; Windows deployment uses the cross-process task lock.
        _lease = OperatingSystem.IsWindows() ? new NamedPipeTaskLease() : new TaskLease();
        DeploymentStateBackup.CleanupStaleTemporaryExports(DeploymentRunLog.GetDefaultRoot());
        LoadLatestExecutionHistory();
#if !STUDENT_SETUP_APP
        LoadLatestWebsitePolicyHistory();
#endif
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ClearStudentPasswordRequested;
    public event EventHandler? ClearAdminPasswordRequested;
    public ObservableCollection<StudentPreflightItem> PreflightItems { get; } = [];
    public ObservableCollection<StudentExecutionStepStatus> ExecutionStepStatuses { get; } = [];
    public bool HasPreflightItems => PreflightItems.Count > 0;
    public string DeviceComputerName => Environment.MachineName;
    public bool HasSavedExecutionHistory => _latestExecutionHistory is not null;
    public bool HasRecoverableStateBackup
    {
        get
        {
            if (_latestStateBackupPath is null) return false;
            try
            {
                var file = new FileInfo(_latestStateBackupPath);
                return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }
    }
    public string StateBackupReviewText { get => _stateBackupReviewText; private set { _stateBackupReviewText = value; Changed(); } }
    public string StateBackupComparisonText { get => _stateBackupComparisonText; private set { _stateBackupComparisonText = value; Changed(); } }
    public string StateBackupSystemComparisonText { get => _stateBackupSystemComparisonText; private set { _stateBackupSystemComparisonText = value; Changed(); } }
    public string StateBackupExportText { get => _stateBackupExportText; private set { _stateBackupExportText = value; Changed(); Changed(nameof(HasStateBackupExportText)); } }
    public bool HasStateBackupExportText => StateBackupExportText.Length > 0;
    public bool IsStateBackupBusy => _isReviewingStateBackup || _isComparingStateBackup || _isExportingStateBackup;
    public bool CanCompareCurrentSystemBackup => HasRecoverableStateBackup && _stateBackupReviewSucceeded && !IsStateBackupBusy;
    public bool CanCompareCurrentStateBackup => HasRecoverableStateBackup && _stateBackupReviewSucceeded &&
        _stateBackupHasVeyonConfig && !IsStateBackupBusy;
    public bool CanExportStateBackup => HasRecoverableStateBackup && _stateBackupReviewSucceeded &&
        _stateBackupHasVeyonConfig && !IsStateBackupBusy;
    public bool NeedsPreviousRunReview => _needsPreviousRunReview;
    public bool CanAcknowledgePreviousRun => IsCompletePage && NeedsPreviousRunReview;
    public string ExecutionSummaryHeading => _showingPreviousExecution ? "上次执行结果" : "本次执行结果";
    public string ExecutionSummaryDescription => HasPendingComputerRenameRestart
        ? "保存工作并重启，再检查电脑名称。"
        : !_showingPreviousExecution
        ? "执行完成。"
        : _latestExecutionHistory is { } history
            ? history.WasInterrupted
                ? "上次运行未完整结束。请检查标记步骤和本机状态，再重新开始。"
                : "请检查标记步骤和本机状态，再重新开始。"
            : "没有可读取的上次运行记录。";
    public string ExecutionResultsHeading => ExecutionSummaryHeading;
    public bool CheckAllPassed => HasPreflight && !IsPreparingDeployment && CanProceedToDeploy &&
        PreflightItems.All(item => item.IsPass || item.IsNotApplicable);
    public bool CheckHasBlocker => HasPreflight && !IsPreparingDeployment && PreflightItems.Any(item => item.IsBlocked);
    public bool CheckNeedsAttention => HasPreflight && !IsPreparingDeployment && !CheckAllPassed && !CheckHasBlocker;
    public string CheckConclusionTitle => IsPreparingDeployment ? "正在检查" : !HasPreflight ? "等待检查" :
        CheckHasBlocker ? "暂时无法部署" : NeedsPreviousRunReview ? "请检查上次结果" :
        CheckAllPassed ? "检查全部通过" : "有项目需要注意";
    public string CheckConclusionHint => IsPreparingDeployment ? "请稍候…" : !HasPreflight ? "点击重新检查。" :
        CheckHasBlocker ? "查看未通过项并重新检查。" : NeedsPreviousRunReview
            ? "检查上次结果和当前电脑状态，再重新开始部署。"
            : CheckAllPassed ? "可以继续下一步。" : CanProceedToDeploy ? "请查看提示后继续。" : "请核对详情后重新检查。";
    public bool HasExecutionStepStatuses => ExecutionStepStatuses.Count > 0;
    public bool HasPendingComputerRenameRestart => HasCurrentExecution && _executionInput is not null &&
        ExecutionStepStatuses.Any(step =>
            step.StepId == "rename" && step.StatusCode == ExecutionPlan.RequiresReboot);
    public int WizardPage => _wizardPage;
    public bool IsSourcePage => WizardPage == 0;
    public bool IsNotSourcePage => !IsSourcePage;
    public bool IsContentPage => WizardPage == 1;
    public bool IsCheckPage => WizardPage == 2;
    public bool IsDeployPage => WizardPage == 3;
    public bool IsCompletePage => WizardPage == 4;
    public bool IsNotCompletePage => !IsCompletePage;
    public string WizardPageTitle => WizardPage switch
    {
        0 => "选择校区配置",
        1 => "设置部署内容",
        2 => "检查电脑环境",
        3 => "执行部署",
        _ => "部署结果"
    };
    public string WizardPageDescription => WizardPage switch
    {
        0 => "选择这台电脑使用的校区配置。",
        1 => "勾选操作，核对右侧预览。",
        2 => "检查通过后即可继续。",
        3 => "正在执行所选操作，进度实时更新。",
        _ => _showingPreviousExecution
            ? "上次部署结果；请查看标记步骤。"
            : "查看本次执行结果。"
    };
    public bool CanNavigateWizard => !IsExecuting && !IsPreparingDeployment;
    public bool CanGoPreviousWizardPage => CanNavigateWizard && WizardPage > 0;
    public bool CanGoNextWizardPage => CanNavigateWizardPage(WizardPage + 1);
    public bool CanProceedToContent => CanNavigateWizard;
    public bool HasSelectedOperation => InstallVeyon || RenameComputer || CreateStudent || ChangeAdminPassword;
    public bool HasVeyonPackageRequirement => InstallVeyon && LoadedPackage is null;
    public bool HasLoadedPackage => LoadedPackage is not null;
    public bool HasNoLoadedPackage => !HasLoadedPackage;
    public bool CanProceedToCheck => CanNavigateWizard && HasSelectedOperation && !HasVeyonPackageRequirement && !HasGlobalError;
    public bool CanProceedToDeploy => CanNavigateWizard && (CanStartDeployment || CanInstall);
    public bool CanProceedToComplete => CanNavigateWizard && CanReviewExecutionResult;
    public bool CanExitSetup => CanNavigateWizard;
    public bool IsAdministrator => PlatformFacts.IsCurrentProcessElevated;
    public bool CanOpenMaintenance => CanNavigateWizard && IsAdministrator;
    public string MaintenanceAccessHint => IsAdministrator
        ? "查看和修复本机部署"
        : "请关闭程序，右键选择“以管理员身份运行”后进入维护。";
    public string WizardFooterStatus => IsExecuting
        ? IsStopAfterCurrentStepRequested
            ? "当前步骤完成后将停止。"
            : "正在执行；当前步骤完成后可停止。"
        : IsPreparingDeployment
            ? "正在检查……"
            : NeedsPreviousRunReview
                ? "上次部署需要检查"
            : IsCheckPage && !CanProceedToDeploy
                ? DeploymentAvailabilityText
            : _showingPreviousExecution
                ? $"上次部署状态：{ExecutionOverallStatusText}"
                : HasExecution
                ? $"最近执行状态：{ExecutionOverallStatusText}"
                : IsContentPage && !HasSelectedOperation
                    ? "请至少选择一项操作。"
                    : IsContentPage && HasVeyonPackageRequirement
                        ? "请返回上一步导入校区配置。"
                        : "请核对后继续。";

    public bool CanNavigateWizardPage(int page)
    {
        if (!CanNavigateWizard || page is < 0 or > 4) return false;
        if (page <= WizardPage) return true;
        if (page != WizardPage + 1 || (page == 3 && WizardPage < 3)) return false;
        return WizardPage switch
        {
            0 => CanProceedToContent,
            1 => CanProceedToCheck,
            2 => CanProceedToDeploy,
            3 => CanProceedToComplete,
            _ => false
        };
    }

    public bool IsWizardStepComplete(int step) => step switch
    {
        0 => _highestCompletedWizardStep >= 0,
        1 => _highestCompletedWizardStep >= 1,
        2 => _highestCompletedWizardStep >= 2,
        3 => HasCurrentExecution && _highestCompletedWizardStep >= 3 &&
             ExecutionOverallStatus is ExecutionPlan.Succeeded or ExecutionPlan.RequiresReboot,
        4 => HasCurrentExecution && ExecutionOverallStatus is ExecutionPlan.Succeeded or ExecutionPlan.RequiresReboot,
        _ => false
    };

    public bool IsWizardStepError(int step) => step switch
    {
        0 => HasPackageError,
        1 => IsContentPage && HasError,
        2 => IsCheckPage && HasError || _preflightReport?.HasBlocker == true,
        3 => HasCurrentExecution && ExecutionOverallStatus is ExecutionPlan.Failed or ExecutionPlan.PartiallyCompleted or
            ExecutionPlan.NeedsReview or ExecutionPlan.Cancelled,
        4 => HasCurrentExecution && ExecutionOverallStatus != ExecutionPlan.Succeeded,
        _ => false
    };

    public string PreflightSummaryText
    {
        get => _preflightSummaryText;
        private set { _preflightSummaryText = value; Changed(); Changed(nameof(HasPreflightItems)); }
    }
    public string ExecutionOverallStatus
    {
        get => _executionOverallStatus;
        private set
        {
            _executionOverallStatus = value;
            Changed(); Changed(nameof(ExecutionOverallStatusText));
            Changed(nameof(IsExecutionSuccessful)); Changed(nameof(IsExecutionFailed)); Changed(nameof(IsExecutionWarning));
            Changed(nameof(IsExecutionRequiresReboot)); Changed(nameof(CanReviewExecutionResult));
            Changed(nameof(CanProceedToComplete)); Changed(nameof(CanGoNextWizardPage));
            Changed(nameof(ExecutionStatusSymbol)); Changed(nameof(WizardFooterStatus));
        }
    }
    public string ExecutionOverallStatusText => GetExecutionStatusLabel(ExecutionOverallStatus);
    public bool IsExecutionSuccessful => HasExecution && ExecutionOverallStatus == ExecutionPlan.Succeeded;
    public bool IsExecutionRequiresReboot => HasExecution && ExecutionOverallStatus == ExecutionPlan.RequiresReboot;
    public bool CanReviewExecutionResult => HasCurrentExecution &&
        ExecutionOverallStatus is ExecutionPlan.Succeeded or ExecutionPlan.RequiresReboot;
    public bool IsExecutionFailed => HasExecution && ExecutionOverallStatus == ExecutionPlan.Failed;
    public bool IsExecutionWarning => HasExecution && !IsExecutionSuccessful && !IsExecutionFailed;
    public string ExecutionStatusSymbol => !HasExecution ? "—" : IsExecutionSuccessful ? "✓" : IsExecutionFailed ? "✕" : "!";
    public int ExecutionProgressPercent
    {
        get
        {
            if (ExecutionStepStatuses.Count == 0) return 0;
            var settled = ExecutionStepStatuses.Count(step => step.StatusCode is not ExecutionPlan.NotStarted and not ExecutionPlan.Running);
            var running = ExecutionStepStatuses.Any(step => step.StatusCode == ExecutionPlan.Running) ? 0.5 : 0;
            return (int)Math.Round((settled + running) * 100d / ExecutionStepStatuses.Count);
        }
    }
    public string ExecutionProgressText
    {
        get
        {
            var settled = ExecutionStepStatuses.Count(step => step.StatusCode is not ExecutionPlan.NotStarted and not ExecutionPlan.Running);
            var running = ExecutionStepStatuses.Any(step => step.StatusCode == ExecutionPlan.Running);
            var current = Math.Min(ExecutionStepStatuses.Count, settled + (running ? 1 : 0));
            return $"{current} / {ExecutionStepStatuses.Count} 项 · {ExecutionProgressPercent}%";
        }
    }
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
    public bool InstallVeyon { get => _installVeyon; set { MarkOperationSelectionTouched(); _installVeyon = value; Changed(); Invalidate(); } }
    public bool RenameComputer { get => _rename; set { MarkOperationSelectionTouched(); _rename = value; Changed(); Invalidate(); } }
    public bool CreateStudent
    {
        get => _createStudent;
        set { MarkOperationSelectionTouched(); if (_createStudent == value) return; _createStudent = value; Changed(); if (!value) ClearStudentPassword(); Invalidate(); }
    }
    public bool ChangeAdminPassword
    {
        get => _changeAdmin;
        set { MarkOperationSelectionTouched(); if (_changeAdmin == value) return; _changeAdmin = value; Changed(); Changed(nameof(AdminPasswordRecommendationWarning)); if (!value) ClearAdminPassword(); Invalidate(); }
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
    public string PackageRecommendationNotice => _packageRecommendationNotice;
    public bool HasPackageRecommendationNotice => !string.IsNullOrWhiteSpace(PackageRecommendationNotice);
    public bool HasAdminPasswordRecommendation => LoadedPackage?.RecommendedOperations?.ChangeAdminPassword == true;
    public string AdminPasswordRecommendationWarning => ChangeAdminPassword
        ? "先确认账户，并妥善保存新密码。"
        : "此项默认关闭；确认账户后再手动启用。";
    private string PackageRecommendationsPreview
    {
        get
        {
            if (LoadedPackage?.RecommendedOperations is not { } recommendation) return "";
            var items = new List<string>();
            if (recommendation.InstallVeyon) items.Add("安装/配置 Veyon");
            if (recommendation.RenameComputer) items.Add("修改电脑名称");
            if (recommendation.CreateStudentAccount) items.Add("创建学生账户");
            if (recommendation.ChangeAdminPassword) items.Add("修改管理员密码（需手动选择）");
            return "默认操作建议\n" + (items.Count == 0 ? "无" : string.Join("、", items));
        }
    }
    public string LoadedPackageInlineSummary => LoadedPackage is null
        ? "尚未选择校区配置"
        : $"{LoadedPackage.Campus}    ·    电脑名前缀：{LoadedPackage.ComputerPrefix}    ·    来源：{PackageSourceLabel}";
    public string DeploymentSelectionSummary => string.Join("\n\n", new[]
    {
        PackageRecommendationsPreview,
        InstallVeyon ? "Veyon\n连接教师机" : null,
        RenameComputer ? $"电脑名称\n{ComputerName}" : null,
        CreateStudent ? $"学生账户\n{StudentAccountName}" : null,
        ChangeAdminPassword ? $"管理员账户\n{AdminAccountName}（更新密码）" : null,
        !HasSelectedOperation ? "尚未选择操作。" : null
    }.Where(item => item is not null));
    public string PackageSourceLabel => _packageSourceLabel;
    public string PackageStatus { get => _packageStatus; private set { _packageStatus = value; Changed(); } }
    private void NotifyLoadedPackageChanged()
    {
        Changed(nameof(LoadedPackage));
        Changed(nameof(HasLoadedPackage));
        Changed(nameof(HasNoLoadedPackage));
        Changed(nameof(LoadedPackageSummary));
        Changed(nameof(LoadedPackageInlineSummary));
        Changed(nameof(PackageRecommendationNotice));
        Changed(nameof(HasPackageRecommendationNotice));
        Changed(nameof(HasAdminPasswordRecommendation));
        Changed(nameof(AdminPasswordRecommendationWarning));
        Changed(nameof(DeploymentSelectionSummary));
        NotifyExecutionAvailabilityChanged();
    }
    public string LoadedPackageSummary => LoadedPackage is null
        ? "尚未选择校区配置"
        : $"{LoadedPackage.Campus}\n电脑名前缀：{LoadedPackage.ComputerPrefix} · 来源：{PackageSourceLabel}";
    public ObservableCollection<DeploymentPackageCatalogEntry> CloudPackages { get; } = [];
    public string CloudPackageQuery { get => _cloudPackageQuery; set { value ??= ""; if (_cloudPackageQuery == value) return; _cloudPackageQuery = value; Changed(); } }
    public string CloudPackageTeacherPhoneLast4
    {
        get => _cloudPackageTeacherPhoneLast4;
        set
        {
            _cloudPackageTeacherPhoneLast4 = value ?? "";
            Changed();
            Changed(nameof(CanLoadSelectedCloudPackage));
            Changed(nameof(CanSaveSelectedCloudPackage));
        }
    }
    public bool IsCloudPackageTeacherPhoneLast4Required => SelectedCloudPackage is not null;
    public string CloudPackageStatus { get => _cloudPackageStatus; private set { _cloudPackageStatus = value; Changed(); } }
    public DeploymentPackageCatalogEntry? SelectedCloudPackage
    {
        get => _selectedCloudPackage;
        set
        {
            if (_selectedCloudPackage == value) return;
            _selectedCloudPackage = value;
            CloudPackageTeacherPhoneLast4 = "";
            Changed(); Changed(nameof(IsCloudPackageTeacherPhoneLast4Required)); Changed(nameof(CanLoadSelectedCloudPackage)); Changed(nameof(CanSaveSelectedCloudPackage));
        }
    }
    public bool IsSearchingCloudPackages
    {
        get => _isSearchingCloudPackages;
        private set { if (_isSearchingCloudPackages == value) return; _isSearchingCloudPackages = value; Changed(); Changed(nameof(CanSearchCloudPackages)); Changed(nameof(CanLoadSelectedCloudPackage)); Changed(nameof(CanSaveSelectedCloudPackage)); }
    }
    public bool IsDownloadingCloudPackage
    {
        get => _isDownloadingCloudPackage;
        private set { if (_isDownloadingCloudPackage == value) return; _isDownloadingCloudPackage = value; Changed(); Changed(nameof(CanSearchCloudPackages)); Changed(nameof(CanLoadSelectedCloudPackage)); Changed(nameof(CanSaveSelectedCloudPackage)); }
    }
    public bool CanSearchCloudPackages => IsStudentControlsEnabled && !IsSearchingCloudPackages && !IsDownloadingCloudPackage;
    public bool CanLoadSelectedCloudPackage => IsStudentControlsEnabled && !IsSearchingCloudPackages && !IsDownloadingCloudPackage &&
        SelectedCloudPackage is not null && IsCloudPackageTeacherPhoneLast4Ready();
    public bool CanSaveSelectedCloudPackage => CanLoadSelectedCloudPackage;
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
    public string PreflightText { get => _preflight; private set { _preflight = value; Changed(); Changed(nameof(HasPreflight)); Changed(nameof(HasNoPreflight)); } }
    public string ExecutionText
    {
        get => _execution;
        private set
        {
            _execution = value;
            if (value.Length > 0 && IsDeployPage)
                _highestCompletedWizardStep = Math.Max(_highestCompletedWizardStep, 3);
            Changed(); Changed(nameof(HasExecution)); Changed(nameof(HasNoExecution));
            Changed(nameof(HasCurrentExecution));
            Changed(nameof(HasPendingComputerRenameRestart));
            Changed(nameof(IsExecutionSuccessful)); Changed(nameof(IsExecutionFailed)); Changed(nameof(IsExecutionWarning));
            Changed(nameof(IsExecutionRequiresReboot)); Changed(nameof(CanReviewExecutionResult));
            Changed(nameof(ExecutionStatusSymbol));
            Changed(nameof(ExecutionInputNote));
            Changed(nameof(ExecutionSummaryHeading)); Changed(nameof(ExecutionResultsHeading));
            Changed(nameof(ExecutionSummaryDescription));
            NotifyWizardNavigationChanged();
        }
    }
    public bool HasError => Error.Length > 0;
    public bool HasPackageError => PackageError.Length > 0;
    public bool HasGlobalError => HasError && (!HasPackageError || Error != PackageError);
    public bool HasPreview => PreviewText.Length > 0;
    public bool HasPreflight => PreflightText.Length > 0;
    public bool HasNoPreflight => !HasPreflight;
    public bool HasExecution => ExecutionText.Length > 0;
    public bool HasNoExecution => !HasExecution;
    public bool HasCurrentExecution => HasExecution && !_showingPreviousExecution;
    public string ExecutionInputNote => !HasExecution
        ? ""
        : _showingPreviousExecution
            ? "这是保存在本机的最近一次运行摘要；它不能证明当前系统状态，请先重新检测。"
            : _executionInput is null
                ? ""
                : _executionInput == CurrentPlanInput()
                    ? "以下是最近一次执行结果。系统状态可能已变化，请在维护页重新读回核对。"
                    : "当前配置已变化；以下是最近一次执行记录，不能视为当前配置已执行。请重新检查并部署，或在维护页读回当前系统状态。";
    public string StudentDeploymentVerificationText
    {
        get => _studentDeploymentVerificationText;
        private set { _studentDeploymentVerificationText = value; Changed(); Changed(nameof(HasStudentDeploymentVerification)); }
    }
    public bool HasStudentDeploymentVerification => StudentDeploymentVerificationText.Length > 0;
    public string WebsiteAgentRemovalStatus
    {
        get => _websiteAgentRemovalStatus;
        private set { _websiteAgentRemovalStatus = value; Changed(); Changed(nameof(HasWebsiteAgentRemovalStatus)); }
    }
    public bool HasWebsiteAgentRemovalStatus => WebsiteAgentRemovalStatus.Length > 0;
    public string WebsiteAgentInstallStatus
    {
        get => _websiteAgentInstallStatus;
        private set { _websiteAgentInstallStatus = value; Changed(); Changed(nameof(HasWebsiteAgentInstallStatus)); }
    }
    public bool HasWebsiteAgentInstallStatus => WebsiteAgentInstallStatus.Length > 0;
    public bool CanInstallWebsitePolicyAgent => CanOpenMaintenance && OperatingSystem.IsWindows() && IsStudent && !IsExecuting &&
        LoadedPackage?.WebsitePolicyPublicKeyPath is not null;
    public bool CanRemoveWebsitePolicyAgent => CanOpenMaintenance && OperatingSystem.IsWindows() && IsStudent && !IsExecuting;
    public bool CanVerifyStudentDeployment => OperatingSystem.IsWindows() && IsStudent && !IsExecuting && LoadedPackage is not null;
    public bool IsExecuting => _isExecuting;
    public bool CanRequestStopAfterCurrentStep => IsExecuting &&
        _stopAfterCurrentStep is { IsCancellationRequested: false } &&
        ExecutionStepStatuses.Any(step => step.StatusCode == ExecutionPlan.NotStarted &&
            step.StepId is not ("pre-change-backup" or "verify"));
    public bool IsStopAfterCurrentStepRequested => _stopAfterCurrentStepRequested;
    public string StopAfterCurrentStepButtonText => IsStopAfterCurrentStepRequested
        ? "已请求停止" : "当前步骤结束后停止";
    public bool IsStudentControlsEnabled => IsStudent && !IsExecuting && !IsPreparingDeployment;
    public string VeyonStatusText { get => _veyonStatusText; private set { _veyonStatusText = value; Changed(); } }
    public string VeyonStatusSummary { get => _veyonStatusSummary; private set { _veyonStatusSummary = value; Changed(); } }
    public bool IsDetectingVeyon { get => _isDetectingVeyon; private set { _isDetectingVeyon = value; Changed(); Changed(nameof(CanRefreshVeyonStatus)); } }
    public bool IsPreparingDeployment
    {
        get => _isPreparingDeployment;
        private set
        {
            if (_isPreparingDeployment == value) return;
            _isPreparingDeployment = value;
            Changed();
            NotifyExecutionAvailabilityChanged();
        }
    }
    public string PreparationStatusText { get => _preparationStatusText; private set { _preparationStatusText = value; Changed(); Changed(nameof(HasPreparationStatus)); } }
    public bool HasPreparationStatus => PreparationStatusText.Length > 0;
    public bool CanPrepareDeployment => !IsExecuting && !IsPreparingDeployment;
    public bool CanRefreshVeyonStatus => !IsExecuting && !IsPreparingDeployment && !IsDetectingVeyon;
    // ① 安装 Veyon：需要校区配置包和已校验的 App 内嵌安装器（与是否勾选无关）。
    public bool CanInstall => !IsExecuting && InstallVeyon && !RenameComputer && !CreateStudent && !ChangeAdminPassword &&
        !NeedsPreviousRunReview && HasPreview && LoadedPackage is not null && _deploymentInstallerPath is not null && !HasGlobalError &&
        HasCurrentExecutablePreflight();
    // ② 按冻结计划执行任意独立操作或组合。
    public bool CanStartDeployment => !IsExecuting &&
        !NeedsPreviousRunReview &&
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
    public string PageDescription => "按步骤完成设置与检查。";
#else
    public bool IsStudent => _isStudent;
    public bool IsTeacher => !_isStudent;
    public string PageTitle => IsStudent ? "学生端配置" : "教师端准备";
    public string PageDescription => IsStudent
        ? "按步骤完成设置与检查。"
        : "安装教师端 Veyon，生成学生配置包并推送网站规则。";
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
        Changed(nameof(CanVerifyStudentDeployment)); Changed(nameof(CanRemoveWebsitePolicyAgent)); Changed(nameof(CanInstallWebsitePolicyAgent));
        Changed(nameof(PageTitle)); Changed(nameof(PageDescription));
    }
#endif
    public void Reset()
    {
        _highestCompletedWizardStep = -1;
        _wizardReturnPage = -1;
        _package = null;
        _packageSourceLabel = "未选择";
        _deploymentInstallerPath = null;
        _operationSelectionTouched = false;
        _applyingPackageRecommendations = true;
        try
        {
            InstallVeyon = false; RenameComputer = false; CreateStudent = false; ChangeAdminPassword = false;
        }
        finally { _applyingPackageRecommendations = false; }
        SetPackageRecommendationNotice("");
        NotifyLoadedPackageChanged();
        _campus = ""; Changed(nameof(Campus));
        _prefix = "PC-"; Changed(nameof(Prefix));
        Number = ""; StudentAccountName = "User"; AdminAccountName = "Administrator";
        ClearAccountPasswords();
        PackageStatus = "未选择校区配置包";
        ClearExecutionSteps(); ExecutionText = "";
        NavigateWizardPage(0);
        Changed(nameof(ComputerName)); Invalidate();
    }

    public void NavigateWizardPage(int page)
    {
        if (!CanNavigateWizardPage(page) || _wizardPage == page) return;
        if (page == _wizardPage + 1)
            _highestCompletedWizardStep = Math.Max(_highestCompletedWizardStep, _wizardPage);
        _wizardReturnPage = -1;
        _wizardPage = page;
        NotifyWizardNavigationChanged();
    }

    public void OpenMaintenancePage()
    {
        if (!CanOpenMaintenance || WizardPage == 4) return;
        _wizardReturnPage = WizardPage;
        _wizardPage = 4;
        NotifyWizardNavigationChanged();
    }

    public void PreviousWizardPage()
    {
        if (WizardPage == 4 && _wizardReturnPage >= 0)
        {
            var returnPage = _wizardReturnPage;
            _wizardReturnPage = -1;
            _wizardPage = returnPage;
            NotifyWizardNavigationChanged();
            return;
        }
        NavigateWizardPage(WizardPage - 1);
    }

    public void NextWizardPage()
    {
        if (CanGoNextWizardPage) NavigateWizardPage(WizardPage + 1);
    }

    public bool BeginDeploymentFromCheckPage()
    {
        if (!IsCheckPage || !CanProceedToDeploy) return false;
        _highestCompletedWizardStep = Math.Max(_highestCompletedWizardStep, WizardPage);
        _wizardReturnPage = -1;
        _wizardPage = 3;
        NotifyWizardNavigationChanged();
        return true;
    }

    private void NotifyWizardNavigationChanged()
    {
        Changed(nameof(WizardPage));
        Changed(nameof(IsSourcePage)); Changed(nameof(IsNotSourcePage)); Changed(nameof(IsContentPage)); Changed(nameof(IsCheckPage));
        Changed(nameof(IsDeployPage)); Changed(nameof(IsCompletePage)); Changed(nameof(IsNotCompletePage));
        Changed(nameof(WizardPageTitle)); Changed(nameof(WizardPageDescription));
        Changed(nameof(CanNavigateWizard)); Changed(nameof(CanGoPreviousWizardPage));
        Changed(nameof(CanGoNextWizardPage)); Changed(nameof(CanProceedToContent)); Changed(nameof(CanProceedToCheck));
        Changed(nameof(CanProceedToDeploy)); Changed(nameof(CanProceedToComplete));
        Changed(nameof(CanExitSetup)); Changed(nameof(CanOpenMaintenance)); Changed(nameof(CanAcknowledgePreviousRun));
        Changed(nameof(WizardFooterStatus));
    }
    public void ClearPackage()
    {
        _package = null;
        _packageSourceLabel = "未选择";
        _deploymentInstallerPath = null;
        PackageStatus = "已清除校区配置包；其他表单输入和操作选择已保留。";
        SetPackageRecommendationNotice("已清除配置包；当前操作选择保持不变。重新载入其他包时，请核对其建议来源。");
        PackageError = "";
        NotifyLoadedPackageChanged();
        Invalidate();
    }
    public async Task LoadPackageAsync(string path, string sourceLabel = "本机导入")
    {
        var previousPackageFingerprint = LoadedPackage?.PackageFingerprint;
        var hadPreviousPackage = LoadedPackage is not null;
        ClearPackageSelection();

        var busy = false;
        try
        {
            var directory = PackageSource.Resolve(path);
            var isNetworkSource = string.Equals(sourceLabel, "本机导入", StringComparison.Ordinal) &&
                                  IsNetworkPackagePath(directory);
            PackageContext loaded;
            if (isNetworkSource)
            {
                SetBusy(true);
                busy = true;
                PackageStatus = "正在将共享文件夹中的配置复制到本机并核对内容……";
                var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VeyonCampus", "deployment-packages", "local-imports");
                loaded = PackageSource.CreateLocalSnapshot(directory, store);
                directory = loaded.Root;
                sourceLabel = "共享文件夹（已安全暂存到本机）";
            }
            else loaded = PackageContext.Load(directory);
            loaded.Compatibility?.EnsureReadable(loaded.SchemaVersion, AppVersion, VeyonInstallerTrust.Version,
                WebsitePolicyAgentInstaller.BuildVersion);
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
            _packageSourceLabel = sourceLabel;
            _deploymentInstallerPath = installerPath;
            ApplyPackageRecommendations(loaded, hadPreviousPackage,
                !string.Equals(previousPackageFingerprint, loaded.PackageFingerprint, StringComparison.Ordinal));
            NotifyLoadedPackageChanged();
            _campus = loaded.Campus; Changed(nameof(Campus));
            _prefix = loaded.ComputerPrefix; Changed(nameof(Prefix)); Changed(nameof(ComputerName));
            var packageKind = loaded.SchemaVersion == 0 ? "旧版配置" : loaded.SchemaVersion == 1 ? "旧版含安装器部署包" : "新版轻量配置包";
            PackageStatus = $"已读取：{directory}\n校区：{loaded.Campus} · 电脑名前缀：{loaded.ComputerPrefix}\n{packageKind}，RSA 公钥指纹 {loaded.PublicKeyFingerprint[..12]}…；App 内嵌安装器已就绪；未读取 admin.txt。" +
                            (isNetworkSource ? "\n共享目录中的配置已复制并核验到本机，之后不依赖共享目录连接。" : "");
            if (loaded.Compatibility is { } compatibility)
                PackageStatus += $"\n兼容范围：Student App [{compatibility.StudentApp.MinInclusive}, {compatibility.StudentApp.MaxExclusive})；Veyon [{compatibility.Veyon.MinInclusive}, {compatibility.Veyon.MaxExclusive})。";
            if (loaded.RecommendedOperations is not null)
                PackageStatus += "\n此 schema v6 配置包还包含首次部署建议；它们不会授权或自动执行系统更改。";
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

    private static bool IsNetworkPackagePath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return true;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false;
            var drive = DriveInfo.GetDrives().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, root, StringComparison.OrdinalIgnoreCase));
            return drive?.DriveType == DriveType.Network;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public async Task SearchCloudPackagesAsync()
    {
        if (!CanSearchCloudPackages) return;
        IsSearchingCloudPackages = true;
        CloudPackageStatus = "正在搜索已发布的校区配置包……";
        CloudPackages.Clear();
        SelectedCloudPackage = null;
        try
        {
            var result = await _deploymentPackageCatalog.SearchAsync(CloudPackageQuery.Trim());
            foreach (var item in result.Items) CloudPackages.Add(item);
            CloudPackageStatus = result.Items.Count == 0
                ? "没有找到已发布的配置包。请检查校区名称后重试。"
                : $"找到 {result.Items.Count} 个配置包。下载后会继续校验清单、校区公钥和网站策略公钥。" +
                  (result.HasMore ? " 当前最多显示 20 项，请缩小搜索范围。" : "");
            SelectedCloudPackage = CloudPackages.FirstOrDefault();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or InvalidOperationException)
        {
            CloudPackageStatus = "云端目录暂不可用：" + exception.Message + " 可选择本地配置文件夹离线导入，或稍后重试云端搜索。";
        }
        finally { IsSearchingCloudPackages = false; }
    }

    public void ReportCloudPackageError(string message) => CloudPackageStatus = message;

    public async Task LoadSelectedCloudPackageAsync()
    {
        var selected = SelectedCloudPackage;
        if (!CanLoadSelectedCloudPackage || selected is null) return;
        IsDownloadingCloudPackage = true;
        CloudPackageStatus = $"正在下载“{selected.CampusName}”配置……";
        try
        {
            var archive = await _deploymentPackageCatalog.DownloadAsync(selected.PackageId, GetCloudPackageTeacherPhoneLast4());
            var storageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "deployment-packages");
            var context = CampusConfigurationArchive.ExtractToStore(archive, storageRoot);
            await LoadPackageAsync(context.Root, "云端目录");
            CloudPackageStatus = LoadedPackage is null
                ? "下载完成，但配置包未能载入：" + PackageError
                : $"已下载并载入校区“{LoadedPackage.Campus}”配置。请继续核对部署操作和电脑编号。";
            CloudPackageTeacherPhoneLast4 = "";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CloudPackageStatus = "下载或校验失败：" + exception.Message;
        }
        finally { IsDownloadingCloudPackage = false; }
    }

    public async Task SaveSelectedCloudPackageAsync(string path)
    {
        var selected = SelectedCloudPackage;
        if (!CanSaveSelectedCloudPackage || selected is null) return;
        IsDownloadingCloudPackage = true;
        CloudPackageStatus = $"正在下载并校验“{selected.CampusName}”配置……";
        try
        {
            var archive = await _deploymentPackageCatalog.DownloadAsync(selected.PackageId, GetCloudPackageTeacherPhoneLast4());
            var verifyRoot = Path.Combine(Path.GetTempPath(), "VeyonCampus-package-verify-" + Guid.NewGuid().ToString("N"));
            try { _ = CampusConfigurationArchive.ExtractToStore(archive, verifyRoot); }
            finally
            {
                try { if (Directory.Exists(verifyRoot)) Directory.Delete(verifyRoot, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            await File.WriteAllBytesAsync(path, archive);
            CloudPackageStatus = $"已验证并下载配置包：{path}";
            CloudPackageTeacherPhoneLast4 = "";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CloudPackageStatus = "下载或校验失败：" + exception.Message;
        }
        finally { IsDownloadingCloudPackage = false; }
    }

    private bool IsCloudPackageTeacherPhoneLast4Ready() => CloudPackageTeacherPhoneLast4.Length == 4 &&
        CloudPackageTeacherPhoneLast4.All(char.IsAsciiDigit);

    private string GetCloudPackageTeacherPhoneLast4() => CloudPackageTeacherPhoneLast4;

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
            if (PackageRecommendationsPreview.Length > 0)
                header += "\n\n" + PackageRecommendationsPreview;
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
        VeyonStatusSummary = "正在检查 Veyon…";
        VeyonStatusText = "正在只读检查本机 Veyon 安装状态……";
        try
        {
            var facts = await Task.Run(VeyonFacts.Probe);
            if (requestId != _veyonProbeRequestId) return;
            VeyonStatusSummary = facts.Status switch
            {
                VeyonFacts.NotApplicable => "Windows 功能不适用",
                VeyonFacts.NotInstalled => "尚未安装",
                "installed" when VeyonFacts.IsSupportedVersionDetail(facts.VersionDetail) => "已安装 · 服务状态待核对",
                "installed" => "已安装 · 版本需要核对",
                _ => "状态需要核对"
            };
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
            {
                VeyonStatusSummary = "状态无法确认";
                VeyonStatusText = $"读取 Veyon 状态失败；不能据此判定未安装。准备检查会再次尝试。\n{exception.Message}";
            }
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
        PreflightItems.Clear();
        PreflightSummaryText = "";
        Changed(nameof(HasPreflightItems));
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
            StudentDeploymentVerificationText = $"检查时间：{report.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
                Environment.NewLine + Environment.NewLine + report.AsText();
            if (report.IsReadyToRemoveSetupTool)
                StudentDeploymentVerificationText += Environment.NewLine + Environment.NewLine +
                    "适用的本机组件已通过只读核对。你可以在此查看状态、修复网站策略 Agent，或卸载本工具拥有的策略。";
            else
                StudentDeploymentVerificationText += Environment.NewLine + Environment.NewLine +
                    "有组件状态未能确认。请先处理未通过项，再重新运行只读检查。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            _studentDeploymentVerification = null;
            StudentDeploymentVerificationText = "部署后只读验证未完成：" + exception.Message;
        }
        catch (Exception exception)
        {
            // Read-only verification must surface unexpected failures in the UI rather than
            // let an async button event terminate the setup application.
            _studentDeploymentVerification = null;
            StudentDeploymentVerificationText = "部署后只读验证遇到未预期错误：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    public async Task RemoveWebsitePolicyAgentAsync()
    {
        if (!CanRemoveWebsitePolicyAgent) return;
        if (!TryBeginExclusiveTask()) return;
        WebsiteAgentRemovalStatus = "正在核对并卸载本机 VeyonCampus 网站策略代理……";
        StudentDeploymentVerificationText = "";
        _studentDeploymentVerification = null;
        try
        {
            var response = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.StudentSetup,
                (requestId, caller) => new PrivilegedWorkerRequest(
                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                    PrivilegedWorkerOperation.UninstallWebsitePolicyAgent));
            var result = response.Result;
            WebsiteAgentRemovalStatus = result.Status == ExecutionPlan.Succeeded
                ? result.Detail + " 请在学生电脑上手动重启 Edge、Chrome 和 Firefox，使已清除的策略生效。"
                : "卸载未完成，未清理无法确认归属的项目：" + result.Detail;
        }
        catch (Exception exception)
        {
            WebsiteAgentRemovalStatus = "卸载未完成；请核对本机 Agent、计划任务、策略值和防火墙规则后重试：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    public async Task InstallWebsitePolicyAgentAsync()
    {
        if (!CanInstallWebsitePolicyAgent) return;
        if (!TryBeginExclusiveTask()) return;
        WebsiteAgentInstallStatus = "正在更新网站策略代理…";
        StudentDeploymentVerificationText = "";
        _studentDeploymentVerification = null;
        try
        {
            var package = LoadedPackage;
            if (package?.WebsitePolicyPublicKeyPath is null)
            {
                WebsiteAgentInstallStatus = "当前没有包含网站策略公钥的学生配置包；请重新载入教师端生成的最新配置包。";
                return;
            }
            package.VerifyUnchanged();
            var response = await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.StudentSetup,
                (requestId, caller) => new PrivilegedWorkerRequest(
                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                    PrivilegedWorkerOperation.InstallWebsitePolicyAgent, PackageRoot: package.Root));
            var result = response.Result;
            WebsiteAgentInstallStatus = result.Status == ExecutionPlan.Succeeded
                ? "网站策略代理已更新。"
                : "网站策略 Agent 安装/修复未完成：" + result.Detail;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or ArgumentException)
        {
            WebsiteAgentInstallStatus = "网站策略代理未完成：" + exception.Message;
        }
        catch (Exception exception)
        {
            WebsiteAgentInstallStatus = "网站策略 Agent 安装/修复遇到未预期错误；请核对本机状态：" + exception.Message;
        }
        finally { EndExclusiveTask(); }
    }

    private int BeginEnvironmentCheck()
    {
        Error = ""; PreflightText = "";
        _preflightReport = null; _preflightInput = null;
        PreflightItems.Clear();
        PreflightSummaryText = "";
        Changed(nameof(HasPreflightItems));
        NotifyExecutionAvailabilityChanged();
        return ++_preflightRequestId;
    }
    private PlanInput CurrentPlanInput() => new(Campus, Prefix, Number, StudentAccountName, AdminAccountName,
        new OperationSelection(InstallVeyon, RenameComputer, CreateStudent, ChangeAdminPassword), _package);
    private void DisplayPreflight(PlanInput input, PreflightReport report)
    {
        _preflightReport = report;
        _preflightInput = input;
        PreflightItems.Clear();
        foreach (var check in report.ChecksByPriority)
            PreflightItems.Add(new StudentPreflightItem(
                GetPreflightCheckName(check.Id), GetPreflightCheckHint(check.Id), check.Level.ToString(),
                GetPreflightStatusLabel(check.Level), check.Detail));
        var passed = report.Checks.Count(check => check.Level == CheckLevel.Pass);
        var warnings = report.Checks.Count(check => check.Level == CheckLevel.Warning);
        var blocked = report.Checks.Count(check => check.Level == CheckLevel.Blocked);
        var unknown = report.Checks.Count(check => check.Level == CheckLevel.Unknown);
        PreflightSummaryText = $"{passed} 项通过 · {warnings} 项提示 · {blocked} 项未通过 · {unknown} 项待检查";
        Changed(nameof(HasPreflightItems));
        PreflightText = $"检查时间：{report.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n\n" +
            string.Join("\n\n", report.ChecksByPriority.Select(c =>
            $"{(c.Level == CheckLevel.Pass ? "✓" : c.Level == CheckLevel.Blocked ? "✗" : c.Level == CheckLevel.NotApplicable ? "—" : "?")} {GetPreflightCheckName(c.Id)}\n{c.Detail}"));
        NotifyExecutionAvailabilityChanged();
    }

    private static string GetPreflightCheckName(string id) => id switch
    {
        "platform" => "运行平台",
        "windows-system" => "Windows 系统",
        "os" => "Windows 版本",
        "architecture" => "系统架构",
        "computer" => "当前电脑",
        "domain-membership" => "域或工作组",
        "restore-environment" => "还原保护状态",
        "privilege" => "管理员权限",
        "reboot" => "待重启状态",
        "disk" => "磁盘空间",
        "processes" => "Veyon 进程状态",
        "veyon" => "Veyon 状态",
        "veyon-version" => "Veyon 版本",
        "public-key" => "校区公钥",
        "installer" => "Veyon 安装资源",
        "installer-trust" => "安装资源签名",
        "rename" => "电脑名修改条件",
        "student-account" => "学生账户",
        "admin-account" => "管理员账户",
        "accounts" => "账户操作",
        _ => id
    };

    private static string GetPreflightCheckHint(string id) => id switch
    {
        "platform" => "当前工具需在受支持的 Windows 电脑上运行。",
        "windows-system" => "请使用受支持的 Windows 系统完成部署。",
        "os" => "系统版本需满足本次部署要求。",
        "architecture" => "安装资源需要与系统架构匹配。",
        "computer" => "电脑名称需符合校区的编号规则。",
        "domain-membership" => "显示本机是否加入域或处于工作组。",
        "restore-environment" => "提示重启后可能影响配置保留的还原保护。",
        "privilege" => "安装与系统设置需要管理员权限。",
        "reboot" => "如系统等待重启，请先完成重启。",
        "disk" => "请为 Veyon 和配置文件留足空间。",
        "processes" => "确认是否有 Veyon 管理程序仍在运行。",
        "veyon" => "此项会检查现有 Veyon 安装状态。",
        "veyon-version" => "Veyon 版本需符合校区配置要求。",
        "public-key" => "校区公钥用于验证教师发布的配置。",
        "installer" => "部署需要校区指定的 Veyon 安装资源。",
        "installer-trust" => "签名用于确认安装资源来源可信。",
        "rename" => "修改电脑名需满足规则并获系统允许。",
        "student-account" => "此项会检查学生本地账户的设置条件。",
        "admin-account" => "此项会检查本机管理员账户的设置条件。",
        "accounts" => "账户检查仅支持 Windows 电脑。",
        _ => "请查看详情确认此项是否符合部署要求。"
    };

    private static string GetPreflightStatusLabel(CheckLevel level) => level switch
    {
        CheckLevel.Pass => "通过",
        CheckLevel.Warning => "提示",
        CheckLevel.Blocked => "阻断",
        CheckLevel.NotApplicable => "不适用",
        _ => "请检查"
    };
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
            var results = await WebsitePolicyTransport.PushAsync(targets, signedPolicy, campus);
            var succeeded = results.Count(result => result.Succeeded);
            var needsReview = results.Count(result => !result.Succeeded && result.NeedsReview);
            var failed = results.Count(result => !result.Succeeded && !result.NeedsReview);
            var expirySummary = expiresUtc is { } expiry
                ? $" · 自动解除 {expiry.ToLocalTime():yyyy-MM-dd HH:mm}"
                : mode == WebsitePolicyMode.Disabled ? "" : " · 不自动到期";
            var heading = $"{mode switch { WebsitePolicyMode.Disabled => "已解除", WebsitePolicyMode.Blocklist => "黑名单已推送", _ => "白名单已推送" }}{expirySummary} · 已确认 {succeeded}/{results.Count} · 待核对 {needsReview} · 失败 {failed}";
            WebsitePolicyResult = heading + Environment.NewLine + Environment.NewLine +
                "请在收到代理确认的学生电脑上手动重启 Edge、Chrome 和 Firefox，再检查阻止或恢复效果。" + Environment.NewLine +
                string.Join(Environment.NewLine,
                results.Select(result => $"{result.Target}：{(result.StatusLabel)} — {result.Detail}"));
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
                    needsReview > 0 ? "部分学生机未取得可信确认；报告已应用但身份待核对的电脑，请在教师端固定身份后重试。" : "",
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
        WebsitePolicyHistoryText = $"最近推送 {entry.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 成功 {succeeded}/{entry.Results.Count} · 待核对 {needsReview} · 失败 {failed}";
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
            using var applicationSigningKey = ApplicationPolicySigningKeyStore.GetOrCreate(campus);
            var publicKeyExportPath = Path.Combine(Path.GetTempPath(), "VeyonCampus-public-" + Guid.NewGuid().ToString("N") + ".pem");
            temporaryPublicKey = publicKeyExportPath;
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

            var built = await Task.Run(() => PackageBuilder.Build(outDir, campus, RoomPrefix,
                publicKeyExportPath, websiteSigningKey.PublicKeyPem,
                applicationPolicyPublicKeyPem: applicationSigningKey.PublicKeyPem,
                compatibility: PackageCompatibility.ForSupportedProtocolVersions(AppVersion, VeyonInstallerTrust.Version)));
            PackageOutput = $"已生成学生校区配置包：{built}\n{keyResponse.Result.Detail}\nVeyon 教师私钥仍在 Veyon 受控密钥目录；网站与应用策略签名私钥仅在当前教师 Windows 用户证书库内，学生包只含公钥。\n该包可供同一兼容协议的后续补丁版本使用；Veyon 保持 {VeyonInstallerTrust.Version}。\n请将完整 VeyonCampus App 与此配置包一起分发。";
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
        Changed(nameof(CanRequestStopAfterCurrentStep));
        Changed(nameof(CanVerifyStudentDeployment)); Changed(nameof(CanRemoveWebsitePolicyAgent)); Changed(nameof(CanInstallWebsitePolicyAgent));
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
        Error = "";
        if (!TryBeginExclusiveTask()) return;
        CancellationTokenSource? stopAfterCurrentStep = null;
        DeploymentRunLog? runLog = null;
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
            ExecutionText = "";
            ClearExecutionSteps();
            _executionInput = frozenPlan.Input;
            Changed(nameof(ExecutionInputNote));
            ExecutionStepStatuses.Add(new StudentExecutionStepStatus(
                "pre-change-backup", GetExecutionStepName("pre-change-backup"), ExecutionPlan.Running,
                GetExecutionStatusLabel(ExecutionPlan.Running), "正在保存现有 Veyon 配置并验证加密快照。"));
            ExecutionStepStatuses.Add(new StudentExecutionStepStatus(
                "veyon-install", GetExecutionStepName("veyon-install"), ExecutionPlan.NotStarted,
                GetExecutionStatusLabel(ExecutionPlan.NotStarted), "等待执行前快照完成。"));
            Changed(nameof(HasExecutionStepStatuses));
            ExecutionOverallStatus = ExecutionPlan.Running;
            using var snapshot = PackageResourceSnapshot.Create(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VeyonCampus", "snapshots"), frozenPackage);
            snapshot.VerifyUnchanged();
            if (_deploymentInstallerPath != deploymentInstallerPath)
            {
                Error = "校区配置或安装资源在计划确认期间发生变化；未执行，请重新检查。";
                return;
            }
            runLog = DeploymentRunLog.Create(
                DeploymentRunLog.GetDefaultRoot(), frozenPlan.PlanFingerprint);
            stopAfterCurrentStep = EnableStopAfterCurrentStepRequests();
            runLog.ReportEvent("step", "pre-change-backup",
                new StepResult("pre-change-backup", ExecutionPlan.Running, "正在生成并验证加密快照。"), null);
            var stateBackup = await Task.Run(() => DeploymentStateBackup.Capture(
                frozenPlan, runLog.RunDirectory, _launcher));
            var backupStep = new StepResult("pre-change-backup",
                stateBackup.Succeeded ? ExecutionPlan.Succeeded : ExecutionPlan.NeedsReview,
                stateBackup.Detail);
            runLog.ReportEvent("step", backupStep.StepId, backupStep, null);
            UpdateExecutionStep(backupStep.StepId, backupStep.Status, backupStep.Detail);
            if (!stateBackup.Succeeded)
            {
                var blockedInstall = new StepResult("veyon-install", ExecutionPlan.Skipped,
                    "执行前状态快照未完成；安装器没有启动。");
                var blockedVerify = new StepResult("verify", ExecutionPlan.Skipped,
                    "安装未开始；没有进行安装结果验证。");
                runLog.ReportEvent("step", blockedInstall.StepId, blockedInstall, null);
                runLog.ReportEvent("verification", blockedVerify.StepId, blockedVerify, null);
                UpdateExecutionStep(blockedInstall.StepId, blockedInstall.Status, blockedInstall.Detail);
                UpdateExecutionStep(blockedVerify.StepId, blockedVerify.Status, blockedVerify.Detail);
                runLog.Finish(ExecutionPlan.NeedsReview, false);
                RefreshLatestExecutionHistory();
                ExecutionOverallStatus = ExecutionPlan.NeedsReview;
                ExecutionText = string.Join("\n", [
                    "安装结果",
                    "整体状态：请检查",
                    backupStep.Detail,
                    "本次运行没有启动 Veyon 安装器。",
                    "执行记录：" + runLog.LogPath
                ]);
                InvalidatePreflightAndPreview();
                return;
            }
            if (stopAfterCurrentStep.IsCancellationRequested)
            {
                CompleteVeyonInstallCancellation(runLog, backupStep.Detail);
                return;
            }
            var installFacts = await Task.Run(VeyonFacts.Probe);
            if (stopAfterCurrentStep.IsCancellationRequested)
            {
                CompleteVeyonInstallCancellation(runLog, backupStep.Detail);
                return;
            }
            var installStarted = new StepResult("veyon-install", ExecutionPlan.Running,
                "Veyon 安装步骤已开始；重启后仍需按实际系统状态核对。");
            runLog.ReportEvent("step", installStarted.StepId, installStarted, null);
            UpdateExecutionStep(installStarted.StepId, installStarted.Status, installStarted.Detail);
            var installerResult = installFacts.Status == VeyonFacts.NotInstalled
                ? (await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.StudentSetup,
                    (requestId, caller) => new PrivilegedWorkerRequest(
                        PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                        PrivilegedWorkerOperation.InstallVeyon, PackageRoot: frozenPackage.Root,
                        InstallerPath: deploymentInstallerPath, IsTeacher: false))).Result
                : installFacts.Status == "installed" && VeyonFacts.IsSupportedVersionDetail(installFacts.VersionDetail)
                    ? new StepResult("veyon-install", ExecutionPlan.Skipped,
                        $"已检测到固定版本 Veyon {VeyonInstallerTrust.Version}，跳过安装器。此入口仅负责安装，不会导入公钥；如需导入校区公钥，请点“确认并执行所选操作”。")
                    : new StepResult("veyon-install", ExecutionPlan.NeedsReview,
                        $"无法确认现有 Veyon 安装状态或版本；为避免覆盖或重复安装，已停止。{installFacts.AsText()}");
            var verification = await Task.Run(() => WindowsVeyonAdapter.Verify(frozenPackage));
            var stepResults = new List<StepResult> { installerResult };
            var verificationOk = verification.InstallState == "已安装" &&
                VeyonFacts.IsSupportedVersionDetail(verification.Version) &&
                verification.ServiceState.Contains("正在运行", StringComparison.Ordinal);
            var verificationDetail = verificationOk
                ? $"已确认 Veyon {verification.Version} 安装状态和服务运行状态。"
                : "安装状态、固定版本或 VeyonService 运行状态未全部确认。";
            runLog.ReportEvent("step", installerResult.StepId, installerResult, installerResult.ExitCode);
            runLog.ReportEvent("verification", "verify", new StepResult(
                "verify", verificationOk ? ExecutionPlan.Succeeded : ExecutionPlan.NeedsReview,
                verificationDetail), null);
            if (installerResult.Ok)
                stepResults.Add(new("verify", verificationOk ? ExecutionPlan.Succeeded : ExecutionPlan.NeedsReview,
                    verificationDetail));
            UpdateExecutionStep("veyon-install", installerResult.Status, installerResult.Detail);
            UpdateExecutionStep("verify", verificationOk ? ExecutionPlan.Succeeded : ExecutionPlan.NeedsReview,
                verificationDetail);
            var summary = ExecutionPlan.Summarize(stepResults);
            ExecutionOverallStatus = summary.Status;
            runLog.Finish(summary.Status, summary.RebootRequired);
            RefreshLatestExecutionHistory();
            var lines = new List<string>
            {
                "安装结果",
                $"整体状态：{summary.Status}" + (summary.RebootRequired ? " · 需要重启" : ""),
                backupStep.Detail,
                "执行前快照：" + stateBackup.Path,
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
            if (ExecutionStepStatuses.Count > 0)
            {
                MarkRunningStepsNeedsReview("安装流程遇到异常；实际系统状态需要重新检查。" + Environment.NewLine + ex.Message);
                ExecutionOverallStatus = ExecutionPlan.NeedsReview;
                ExecutionText = $"安装流程遇到异常；实际系统状态需要重新检查。{Environment.NewLine}{ex.Message}";
                MarkCurrentRunNeedsReview(runLog);
            }
            else Error = $"安装执行失败：{ex.Message}";
        }
        finally
        {
            ClearStopAfterCurrentStep(stopAfterCurrentStep);
            stopAfterCurrentStep?.Dispose();
            EndExclusiveTask();
            if (InstallVeyon) await RefreshVeyonStatusAsync();
        }
    }

    /// <summary>按冻结计划执行所选账户 → Veyon → 改名步骤。
    /// 各步骤在安全边界取消；失败或待重启停止后续，不自动回滚已完成修改。</summary>
    public async Task RunDeploymentAsync()
    {
        var uiContext = SynchronizationContext.Current;
        Error = "";
        if (!TryBeginExclusiveTask()) return;
        CancellationTokenSource? stopAfterCurrentStep = null;
        DeploymentRunLog? runLog = null;
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
            ExecutionText = "";
            ClearExecutionSteps();
            _executionInput = frozenPlan.Input;
            Changed(nameof(ExecutionInputNote));
            BeginExecutionSteps(frozenPlan.Steps, includeBackup: true);
            stopAfterCurrentStep = EnableStopAfterCurrentStepRequests();
            var frozenPackage = operations.InstallVeyon ? frozenPlan.Package : null;

            // The snapshot protects the public key bytes the Veyon CLI will
            // consume; only plans that include Veyon carry a package context.
            using var snapshot = frozenPackage is not null
                ? PackageResourceSnapshot.Create(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "VeyonCampus", "snapshots"), frozenPackage)
                : null;

            runLog = DeploymentRunLog.Create(
                DeploymentRunLog.GetDefaultRoot(), frozenPlan.PlanFingerprint);
            var renameAdapter = new WindowsRenameAdapter(_launcher);

            runLog.ReportEvent("step", "pre-change-backup",
                new StepResult("pre-change-backup", ExecutionPlan.Running, "正在生成并验证加密快照。"), null);
            UpdateExecutionStep("pre-change-backup", ExecutionPlan.Running,
                "正在保存 Veyon 配置、电脑名称和所选账户的非密码状态……");
            var stateBackup = await Task.Run(() => DeploymentStateBackup.Capture(
                frozenPlan, runLog.RunDirectory, _launcher));
            var backupStep = new StepResult("pre-change-backup",
                stateBackup.Succeeded ? ExecutionPlan.Succeeded : ExecutionPlan.NeedsReview,
                stateBackup.Detail);
            runLog.ReportEvent("step", backupStep.StepId, backupStep, null);
            UpdateExecutionStep(backupStep.StepId, backupStep.Status, backupStep.Detail);
            if (!stateBackup.Succeeded)
            {
                foreach (var step in frozenPlan.Steps)
                {
                    var blockedStep = new StepResult(step.Id, ExecutionPlan.Skipped,
                        "执行前状态快照未完成；此步骤没有开始。");
                    runLog.ReportEvent("step", blockedStep.StepId, blockedStep, null);
                    UpdateExecutionStep(blockedStep.StepId, blockedStep.Status, blockedStep.Detail);
                }
                runLog.Finish(ExecutionPlan.NeedsReview, false);
                RefreshLatestExecutionHistory();
                ExecutionOverallStatus = ExecutionPlan.NeedsReview;
                ExecutionText = string.Join("\n", [
                    "部署结果",
                    "整体状态：请检查",
                    backupStep.Detail,
                    "本次运行没有开始任何系统修改。",
                    "执行记录：" + runLog.LogPath
                ]);
                InvalidatePreflightAndPreview();
                return;
            }

            // Domain check gates rename before any modification (P5-03).
            if (!stopAfterCurrentStep.IsCancellationRequested && frozenPlan.Input.Operations.RenameComputer)
            {
                var domain = await Task.Run(renameAdapter.CheckDomainMembership);
                if (!domain.Ok)
                {
                    runLog.ReportEvent("step", "rename", new StepResult("rename", domain.Status, domain.Detail), null);
                    runLog.Finish(domain.Status, false);
                    RefreshLatestExecutionHistory();
                    UpdateExecutionStep("rename", domain.Status,
                        domain.Detail + " 改名被阻断；其他步骤未开始。");
                    ExecutionOverallStatus = domain.Status;
                    ExecutionText = $"部署结果\n整体状态：{domain.Status}\n{domain.Detail}\n改名被阻断；其他步骤未开始。\n执行记录：{runLog.LogPath}";
                    InvalidatePreflightAndPreview();
                    return;
                }
            }

            var executionSummary = await ExecutionCoordinator.RunAsync(frozenPlan, async step =>
            {
                runLog.ReportEvent("step", step.Id,
                    new StepResult(step.Id, ExecutionPlan.Running, "步骤已开始。"), null);
                await InvokeOnSynchronizationContextAsync(uiContext, () =>
                    UpdateExecutionStep(step.Id, ExecutionPlan.Running, "正在执行当前步骤……"));
                try { snapshot?.VerifyUnchanged(); }
                catch (Exception ex)
                {
                    var review = new StepResult(step.Id, ExecutionPlan.NeedsReview,
                        $"执行资源在步骤开始前发生变化：{ex.Message}");
                    await InvokeOnSynchronizationContextAsync(uiContext, () =>
                        UpdateExecutionStep(step.Id, review.Status, review.Detail));
                    return review;
                }
                StepResult result;
                switch (step.Id)
                {
                    case "student-account":
                    {
                        var accountSnapshot = frozenPlan.Accounts;
                        if (accountSnapshot is null || accountSnapshot.StudentAccountName != frozenPlan.Input.StudentAccountName)
                            result = new(step.Id, ExecutionPlan.NeedsReview,
                                "学生账户或预检 SID 快照缺失；未开始账户修改。");
                        else if (accountSnapshot.StudentSid is { } expectedStudentSid)
                        {
                            var current = await Task.Run(() => new WindowsAccountAdapter(_launcher)
                                .ReadLocalAccountFacts(accountSnapshot.StudentAccountName));
                            result = current is { Exists: true } &&
                                     string.Equals(current.Sid, expectedStudentSid, StringComparison.OrdinalIgnoreCase) &&
                                     current.PrincipalSource == "Local" && WindowsAccountAdapter.IsStandardEnabledUser(current)
                                ? new(step.Id, ExecutionPlan.Skipped,
                                    $"普通账户 {accountSnapshot.StudentAccountName}（SID {expectedStudentSid}）已存在；保留原密码与权限，没有请求管理员操作。")
                                : new(step.Id, ExecutionPlan.NeedsReview,
                                    "现有学生账户状态与预检不一致或无法读取；没有修改账户。");
                        }
                        else
                            result = (await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.StudentSetup,
                                (requestId, caller) => new PrivilegedWorkerRequest(
                                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                                    PrivilegedWorkerOperation.CreateStudentAccount,
                                    AccountName: frozenPlan.Input.StudentAccountName,
                                    SecretUtf8: studentPassword.Length == 0 ? null :
                                        System.Text.Encoding.UTF8.GetBytes(studentPassword)))).Result;
                        break;
                    }
                    case "admin-password":
                    {
                        var accountSnapshot = frozenPlan.Accounts;
                        result = accountSnapshot is not null &&
                                 accountSnapshot.AdminAccountName == frozenPlan.Input.AdminAccountName &&
                                 !string.IsNullOrWhiteSpace(accountSnapshot.AdminSid) && adminPassword.Length > 0
                            ? (await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.StudentSetup,
                                (requestId, caller) => new PrivilegedWorkerRequest(
                                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                                    PrivilegedWorkerOperation.ChangeAdminPassword,
                                    AccountName: frozenPlan.Input.AdminAccountName,
                                    ExpectedSid: accountSnapshot.AdminSid,
                                    SecretUtf8: System.Text.Encoding.UTF8.GetBytes(adminPassword)))).Result
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
                                ? (await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.StudentSetup,
                                    (requestId, caller) => new PrivilegedWorkerRequest(
                                        PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                                        PrivilegedWorkerOperation.InstallVeyon,
                                        PackageRoot: frozenPackage.Root, InstallerPath: deploymentInstallerPath,
                                        IsTeacher: false))).Result
                                : new(step.Id, ExecutionPlan.Skipped,
                                    $"已安装固定版本 Veyon {VeyonInstallerTrust.Version}，跳过安装步骤。");
                        }
                        break;
                    case "veyon-key":
                        result = frozenPackage is null
                            ? new(step.Id, ExecutionPlan.Failed, "缺少校区配置包；无法配置公钥。")
                            : (await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.StudentSetup,
                                (requestId, caller) => new PrivilegedWorkerRequest(
                                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                                    PrivilegedWorkerOperation.ConfigureVeyon,
                                    PackageRoot: frozenPackage.Root, IsTeacher: false))).Result;
                        break;
                    case "website-agent":
                        result = frozenPackage is null || snapshot is null
                            ? new(step.Id, ExecutionPlan.Failed, "缺少学生校区配置包快照；无法安装网站策略代理。")
                            : OperatingSystem.IsWindows()
                            ? (await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.StudentSetup,
                                (requestId, caller) => new PrivilegedWorkerRequest(
                                    PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                                    PrivilegedWorkerOperation.InstallWebsitePolicyAgent,
                                    PackageRoot: frozenPackage.Root))).Result
                                : new(step.Id, ExecutionPlan.Failed, "学生网站策略代理仅支持 Windows。" );
                        break;
                    case "rename":
                        result = (await ElevatedWorkerClient.ExecuteAsync(VeyonCampusRole.StudentSetup,
                            (requestId, caller) => new PrivilegedWorkerRequest(
                                PrivilegedWorkerProtocol.CurrentVersion, requestId, caller,
                                PrivilegedWorkerOperation.RenameComputer,
                                ComputerName: DeploymentPlan.ComputerNameFor(frozenPlan.Input)))).Result;
                        break;
                    default:
                        result = new(step.Id, ExecutionPlan.NeedsReview,
                            "当前执行器不支持冻结计划中的步骤；没有继续后续操作。");
                        break;
                }
                runLog.ReportEvent("step", step.Id, result, result.ExitCode);
                await InvokeOnSynchronizationContextAsync(uiContext, () =>
                    UpdateExecutionStep(step.Id, result.Status, result.Detail));
                return result;
            }, stopAfterCurrentStep.Token);
            // These terminal outcomes are generated at safe boundaries, so
            // they did not pass through the per-process callback above.
            foreach (var result in executionSummary.Steps)
                if (result.Status is ExecutionPlan.Cancelled or ExecutionPlan.Skipped)
                    runLog.ReportEvent("step", result.StepId, result, result.ExitCode);
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
            ApplyExecutionResults(executionSummary.Steps);
            ExecutionOverallStatus = executionSummary.Status;
            runLog.Finish(executionSummary.Status, executionSummary.RebootRequired);
            RefreshLatestExecutionHistory();
            var lines = new List<string>
            {
                "部署结果",
                $"整体状态：{executionSummary.Status}" + (executionSummary.RebootRequired ? " · 需要重启" : ""),
                backupStep.Detail,
                "执行前快照：" + stateBackup.Path
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
            if (ExecutionStepStatuses.Count > 0)
            {
                MarkRunningStepsNeedsReview("部署流程遇到异常；实际系统状态需要重新检查。" + Environment.NewLine + ex.Message);
                ExecutionOverallStatus = ExecutionPlan.NeedsReview;
                ExecutionText = $"部署流程遇到异常；实际系统状态需要重新检查。{Environment.NewLine}{ex.Message}";
                MarkCurrentRunNeedsReview(runLog);
            }
            else Error = $"部署执行失败：{ex.Message}";
        }
        finally
        {
            ClearStopAfterCurrentStep(stopAfterCurrentStep);
            stopAfterCurrentStep?.Dispose();
            ClearAccountPasswords();
            EndExclusiveTask();
            if (InstallVeyon) await RefreshVeyonStatusAsync();
        }
    }

    /// <summary>Requests a stop at the next plan boundary; never kills an installer or account command.</summary>
    public void RequestStopAfterCurrentStep()
    {
        var cancellation = _stopAfterCurrentStep;
        if (cancellation is null || cancellation.IsCancellationRequested || !CanRequestStopAfterCurrentStep) return;
        _stopAfterCurrentStepRequested = true;
        cancellation.Cancel();
        Changed(nameof(CanRequestStopAfterCurrentStep));
        Changed(nameof(IsStopAfterCurrentStepRequested));
        Changed(nameof(StopAfterCurrentStepButtonText));
        Changed(nameof(WizardFooterStatus));
    }

    private CancellationTokenSource EnableStopAfterCurrentStepRequests()
    {
        var cancellation = new CancellationTokenSource();
        _stopAfterCurrentStep = cancellation;
        _stopAfterCurrentStepRequested = false;
        Changed(nameof(CanRequestStopAfterCurrentStep));
        Changed(nameof(IsStopAfterCurrentStepRequested));
        Changed(nameof(StopAfterCurrentStepButtonText));
        Changed(nameof(WizardFooterStatus));
        return cancellation;
    }

    private void ClearStopAfterCurrentStep(CancellationTokenSource? cancellation)
    {
        if (cancellation is null || !ReferenceEquals(_stopAfterCurrentStep, cancellation)) return;
        _stopAfterCurrentStep = null;
        _stopAfterCurrentStepRequested = false;
        Changed(nameof(CanRequestStopAfterCurrentStep));
        Changed(nameof(IsStopAfterCurrentStepRequested));
        Changed(nameof(StopAfterCurrentStepButtonText));
        Changed(nameof(WizardFooterStatus));
    }

    private void CompleteVeyonInstallCancellation(DeploymentRunLog runLog, string backupDetail)
    {
        var cancelledInstall = new StepResult("veyon-install", ExecutionPlan.Cancelled,
            "已在执行前安全步骤后停止；Veyon 安装器没有启动。");
        var skippedVerify = new StepResult("verify", ExecutionPlan.Skipped,
            "安装已停止；没有进行安装结果验证。");
        runLog.ReportEvent("step", cancelledInstall.StepId, cancelledInstall, null);
        runLog.ReportEvent("verification", skippedVerify.StepId, skippedVerify, null);
        runLog.Finish(ExecutionPlan.Cancelled, false);
        UpdateExecutionStep(cancelledInstall.StepId, cancelledInstall.Status, cancelledInstall.Detail);
        UpdateExecutionStep(skippedVerify.StepId, skippedVerify.Status, skippedVerify.Detail);
        ExecutionOverallStatus = ExecutionPlan.Cancelled;
        ExecutionText = string.Join("\n", [
            "安装结果",
            "整体状态：已取消",
            backupDetail,
            cancelledInstall.Detail,
            "执行记录：" + runLog.LogPath
        ]);
        RefreshLatestExecutionHistory();
        InvalidatePreflightAndPreview();
    }

    private void MarkCurrentRunNeedsReview(DeploymentRunLog? runLog)
    {
        if (runLog is null) return;
        try { runLog.Finish(ExecutionPlan.NeedsReview, false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        _latestExecutionHistory = DeploymentRunLog.ReadLatestHistory(DeploymentRunLog.GetDefaultRoot());
        _needsPreviousRunReview = true;
        Changed(nameof(HasSavedExecutionHistory));
        Changed(nameof(NeedsPreviousRunReview));
        Changed(nameof(CanAcknowledgePreviousRun));
        NotifyExecutionAvailabilityChanged();
    }

    public bool HasReusablePreflight => HasCurrentExecutablePreflight();

    private bool HasCurrentExecutablePreflight()
    {
        return ReadOnlyPreflight.IsCurrent(_preflightReport, _preflightInput, CurrentPlanInput(), DateTimeOffset.UtcNow);
    }
    private async Task<ExecutionPlan?> FreezeAndValidateExecutionPlanAsync()
    {
        if (NeedsPreviousRunReview)
        {
            Error = "请先检查上次结果，再选择“检查完成，重新开始”。";
            return null;
        }
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
                // Reuse the reviewed report; fixed adapters recheck system preconditions.
                // Account identities are stable, disk-space/process descriptions are not.
                var accounts = WindowsAccountAdapter.CheckSelectedAccounts(confirmedInput);
                if (accounts.Checks.Any(check => check.Level == CheckLevel.Blocked) ||
                    accounts.Snapshot != confirmedReport.Accounts)
                    return (Plan: (ExecutionPlan?)null, Error: "账户状态已变化，请重新检查。");
                var plan = ExecutionPlan.Create(confirmedInput, confirmedInput.Package, confirmedReport.Accounts);
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
    private void InvalidatePreflightAndPreview()
    {
        _preflightRequestId++;
        PreviewText = "";
        PreflightText = "";
        PreflightItems.Clear();
        PreflightSummaryText = "";
        Changed(nameof(HasPreflightItems));
        _preflightReport = null;
        _preflightInput = null;
        Error = "";
        NotifyExecutionAvailabilityChanged();
    }
    private void ClearLoadedPackage()
    {
        if (_package is null) return;
        _package = null;
        _packageSourceLabel = "未选择";
        _deploymentInstallerPath = null;
        WebsiteAgentInstallStatus = "";
        SetPackageRecommendationNotice("校区或前缀已修改，原配置包和建议来源已失效；当前操作选择保持不变。");
        NotifyLoadedPackageChanged();
        PackageStatus = "校区或前缀已修改；旧公钥资料已失效，请重新选择校区配置包。";
    }
    private void ClearPackageSelection()
    {
        _package = null;
        _packageSourceLabel = "未选择";
        _deploymentInstallerPath = null;
        WebsiteAgentInstallStatus = "";
        SetPackageRecommendationNotice("");
        NotifyLoadedPackageChanged();
        _campus = ""; Changed(nameof(Campus));
        _prefix = "PC-"; Changed(nameof(Prefix)); Changed(nameof(ComputerName));
        PackageStatus = "未选择校区配置包";
        PackageError = "";
        Invalidate();
    }
    private void MarkOperationSelectionTouched()
    {
        if (_applyingPackageRecommendations) return;
        _operationSelectionTouched = true;
        if (LoadedPackage?.RecommendedOperations is not null)
            SetPackageRecommendationNotice("你已手动编辑操作选择；后续预检和重读配置包会保留当前选择。");
    }
    private void ApplyPackageRecommendations(PackageContext loaded, bool hadPreviousPackage, bool packageChanged)
    {
        var recommendations = loaded.RecommendedOperations;
        if (!_operationSelectionTouched)
        {
            _applyingPackageRecommendations = true;
            try
            {
                InstallVeyon = recommendations?.InstallVeyon ?? false;
                RenameComputer = recommendations?.RenameComputer ?? false;
                CreateStudent = recommendations?.CreateStudentAccount ?? false;
                // A general campus package cannot identify the local administrator account.
                ChangeAdminPassword = false;
            }
            finally { _applyingPackageRecommendations = false; }
        }

        if (recommendations is null)
        {
            SetPackageRecommendationNotice(_operationSelectionTouched
                ? "此配置包无默认建议，已保留你的选择。"
                : "此配置包无默认建议，请选择要执行的操作。");
        }
        else if (_operationSelectionTouched)
        {
            var sourceChanged = hadPreviousPackage && packageChanged;
            SetPackageRecommendationNotice(sourceChanged
                ? "配置包已更换，已保留你的选择。"
                : "已保留你的操作选择。");
        }
        else
        {
            var prefix = hadPreviousPackage && packageChanged ? "配置包已更换，已载入新建议。" : "已载入配置包建议。";
            SetPackageRecommendationNotice(prefix + "部署前请检查。");
        }
    }
    private void SetPackageRecommendationNotice(string value)
    {
        value ??= "";
        if (_packageRecommendationNotice == value) return;
        _packageRecommendationNotice = value;
        Changed(nameof(PackageRecommendationNotice));
        Changed(nameof(HasPackageRecommendationNotice));
        Changed(nameof(DeploymentSelectionSummary));
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
            return "可以继续。请核对计划和目标电脑。";
        if (IsExecuting) return "操作仍在进行，请等待结果。";
        if (NeedsPreviousRunReview)
            return "请先检查上次结果。";
        if (!HasRequiredAccountCredentials()) return GetAccountPasswordValidationError();
        if (action == "仅安装" && (!InstallVeyon || RenameComputer))
            return "仅安装时请只选择 Veyon。";
        if (!InstallVeyon && !RenameComputer && !CreateStudent && !ChangeAdminPassword)
            return "请至少选择一项操作。";
        if (HasGlobalError) return "请先处理上方错误。";
        if (!HasPreview) return "请先生成并核对计划。";
        if (InstallVeyon && LoadedPackage is null)
            return "请先导入有效的校区配置。";
        if (InstallVeyon && _deploymentInstallerPath is null)
            return "未找到 Veyon 安装文件，请重新检查。";
        if (!HasCurrentExecutablePreflight()) return "请完成环境检查并处理未通过项。";
        return "请重新检查电脑环境。";
    }

    private void ClearExecutionSteps()
    {
        ExecutionStepStatuses.Clear();
        _showingPreviousExecution = false;
        Changed(nameof(HasCurrentExecution));
        ExecutionOverallStatus = ExecutionPlan.NotStarted;
        _executionInput = null;
        Changed(nameof(ExecutionInputNote));
        Changed(nameof(ExecutionSummaryHeading)); Changed(nameof(ExecutionResultsHeading));
        Changed(nameof(ExecutionSummaryDescription));
        Changed(nameof(HasExecutionStepStatuses));
        NotifyExecutionProgressChanged();
    }

    private void LoadLatestExecutionHistory()
    {
        _latestExecutionHistory = DeploymentRunLog.ReadLatestHistory(DeploymentRunLog.GetDefaultRoot());
        UpdateLatestStateBackupPath();
        UpdatePreviousRunReviewRequirement();
        if (_latestExecutionHistory is not null)
            RestoreLatestExecutionHistory();
    }

    private void RefreshLatestExecutionHistory()
    {
        _latestExecutionHistory = DeploymentRunLog.ReadLatestHistory(DeploymentRunLog.GetDefaultRoot());
        UpdateLatestStateBackupPath();
        UpdatePreviousRunReviewRequirement();
        Changed(nameof(HasSavedExecutionHistory));
    }

    private void UpdateLatestStateBackupPath()
    {
        var runDirectory = _latestExecutionHistory?.LogPath is { } logPath
            ? Path.GetDirectoryName(logPath) : null;
        _latestStateBackupPath = runDirectory is null
            ? null : Path.Combine(runDirectory, "pre-change-state.vcbak");
        _stateBackupReviewSucceeded = false;
        _stateBackupHasVeyonConfig = false;
        StateBackupReviewText = "尚未读取快照。";
        StateBackupComparisonText = "尚未比对。";
        StateBackupSystemComparisonText = "尚未检查。";
        StateBackupExportText = "";
        NotifyStateBackupAvailabilityChanged();
    }

    private void UpdatePreviousRunReviewRequirement()
    {
        _needsPreviousRunReview = ShouldRequirePreviousRunReview(
            _latestExecutionHistory, GetSystemBootTimeUtc());
        Changed(nameof(NeedsPreviousRunReview));
        Changed(nameof(CanAcknowledgePreviousRun));
        NotifyExecutionAvailabilityChanged();
    }

    private static bool ShouldRequirePreviousRunReview(
        DeploymentRunHistory? history, DateTimeOffset? systemBootTimeUtc)
    {
        if (history is null || history.ReviewAcknowledged) return false;
        if (history.WasInterrupted || history.Status is ExecutionPlan.Failed or ExecutionPlan.NeedsReview or
            ExecutionPlan.PartiallyCompleted)
            return true;
        return history.Status == ExecutionPlan.RequiresReboot &&
               !WasRebootCompletedAfterRun(history, systemBootTimeUtc);
    }

    private static bool WasRebootCompletedAfterRun(
        DeploymentRunHistory history, DateTimeOffset? systemBootTimeUtc) =>
        history.Status == ExecutionPlan.RequiresReboot &&
        history.FinishedAtUtc is { } finishedAtUtc &&
        systemBootTimeUtc is { } bootTimeUtc && bootTimeUtc > finishedAtUtc;

    private static DateTimeOffset? GetSystemBootTimeUtc()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var uptimeMilliseconds = Math.Max(0, Environment.TickCount64);
        return DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(uptimeMilliseconds);
    }

    private void RestoreLatestExecutionHistory()
    {
        if (_latestExecutionHistory is not { } history) return;
        _showingPreviousExecution = true;
        _executionInput = null;
        var rebootCompleted = WasRebootCompletedAfterRun(history, GetSystemBootTimeUtc());
        ExecutionStepStatuses.Clear();
        foreach (var step in history.Steps)
        {
            var name = GetExecutionStepName(step.StepId);
            var displayStatus = step.Status == ExecutionPlan.Running
                ? ExecutionPlan.NeedsReview
                : rebootCompleted && step.Status == ExecutionPlan.RequiresReboot
                    ? ExecutionPlan.Succeeded : step.Status;
            var detail = history.WasInterrupted || step.Status == ExecutionPlan.Running
                ? "运行未完整结束；重新检查此项。"
                : displayStatus is ExecutionPlan.NeedsReview or ExecutionPlan.Failed or ExecutionPlan.PartiallyCompleted
                    ? step.StepId == "website-agent"
                        ? "检查学生代理和当前校区策略状态。"
                        : "查看执行日志中的原因，再检查本机状态。"
                    : "";
            ExecutionStepStatuses.Add(new StudentExecutionStepStatus(
                step.StepId, name, displayStatus, GetExecutionStatusLabel(displayStatus), detail));
        }
        ExecutionOverallStatus = rebootCompleted ? ExecutionPlan.Succeeded : history.Status;
        var lines = new List<string>
        {
            "上次部署记录",
            $"状态：{GetExecutionStatusLabel(ExecutionOverallStatus)}" +
                (history.RebootRequired ? rebootCompleted ? " · 已重启" : " · 需要重启" : ""),
            $"时间：{FormatLocalTime(history.FinishedAtUtc ?? history.StartedAtUtc)}",
            history.WasInterrupted
                ? "上次运行未完整结束，请重新检查本机状态。"
                : "请检查标记步骤和本机状态。"
        };
        lines.AddRange(history.Steps.Select(step =>
        {
            var displayStatus = step.Status == ExecutionPlan.Running
                ? ExecutionPlan.NeedsReview
                : rebootCompleted && step.Status == ExecutionPlan.RequiresReboot
                    ? ExecutionPlan.Succeeded : step.Status;
            return $"{GetExecutionStepName(step.StepId)}：{GetExecutionStatusLabel(displayStatus)}";
        }));
        ExecutionText = string.Join("\n", lines);
        Changed(nameof(HasExecutionStepStatuses));
        Changed(nameof(HasSavedExecutionHistory));
        Changed(nameof(ExecutionInputNote));
        NotifyExecutionProgressChanged();
    }

    public void OpenSavedExecutionHistory()
    {
        if (_latestExecutionHistory is null || !CanNavigateWizard) return;
        if (!HasExecution || _showingPreviousExecution)
            RestoreLatestExecutionHistory();
        if (WizardPage == 4) return;
        _wizardReturnPage = WizardPage;
        _wizardPage = 4;
        NotifyWizardNavigationChanged();
    }

    public async Task ReviewLatestStateBackupAsync()
    {
        if (_latestStateBackupPath is not { } backupPath || !HasRecoverableStateBackup)
        {
            StateBackupReviewText = "没有可用的上次部署快照。";
            _stateBackupReviewSucceeded = false;
            _stateBackupHasVeyonConfig = false;
            NotifyStateBackupAvailabilityChanged();
            return;
        }

        _isReviewingStateBackup = true;
        _stateBackupReviewSucceeded = false;
        _stateBackupHasVeyonConfig = false;
        StateBackupComparisonText = "尚未比对。";
        StateBackupSystemComparisonText = "尚未检查。";
        StateBackupExportText = "";
        NotifyStateBackupAvailabilityChanged();
        try
        {
            var review = await Task.Run(() => DeploymentStateBackup.ReadReview(backupPath));
            StateBackupReviewText = review.Detail;
            _stateBackupReviewSucceeded = review.Succeeded;
            _stateBackupHasVeyonConfig = review.HasVeyonConfig;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StateBackupReviewText = "无法读取快照。";
        }
        finally
        {
            _isReviewingStateBackup = false;
            NotifyStateBackupAvailabilityChanged();
        }
    }

    public async Task CompareCurrentStateBackupAsync()
    {
        if (!CanCompareCurrentStateBackup || _latestStateBackupPath is not { } backupPath) return;
        _isComparingStateBackup = true;
        NotifyStateBackupAvailabilityChanged();
        try
        {
            var result = await Task.Run(() => DeploymentStateBackup.CompareCurrentVeyonConfig(backupPath, _launcher));
            StateBackupComparisonText = result.Detail;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StateBackupComparisonText = "无法读取当前 Veyon 配置；没有修改系统配置。";
        }
        finally
        {
            _isComparingStateBackup = false;
            NotifyStateBackupAvailabilityChanged();
        }
    }

    public async Task CompareCurrentSystemBackupAsync()
    {
        if (!CanCompareCurrentSystemBackup || _latestStateBackupPath is not { } backupPath) return;
        _isComparingStateBackup = true;
        StateBackupSystemComparisonText = "正在读取当前电脑名与账户状态……";
        NotifyStateBackupAvailabilityChanged();
        try
        {
            var result = await Task.Run(() => DeploymentStateBackup.CompareCurrentSystemFacts(backupPath, _launcher));
            StateBackupSystemComparisonText = result.Detail;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StateBackupSystemComparisonText = "无法核对当前电脑名或账户状态；没有修改系统状态。";
        }
        finally
        {
            _isComparingStateBackup = false;
            NotifyStateBackupAvailabilityChanged();
        }
    }

    public async Task ExportStateBackupConfigAsync(string destinationPath)
    {
        if (!CanExportStateBackup || _latestStateBackupPath is not { } backupPath) return;
        _isExportingStateBackup = true;
        StateBackupExportText = "正在校验并导出配置……";
        NotifyStateBackupAvailabilityChanged();
        try
        {
            var result = await Task.Run(() => DeploymentStateBackup.ExportVeyonConfig(backupPath, destinationPath));
            StateBackupExportText = result.Detail;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StateBackupExportText = "导出失败。没有覆盖现有文件，也没有修改 Veyon 配置。";
        }
        finally
        {
            _isExportingStateBackup = false;
            NotifyStateBackupAvailabilityChanged();
        }
    }

    public void ReportStateBackupExportError(string message) => StateBackupExportText = message;

    private void NotifyStateBackupAvailabilityChanged()
    {
        Changed(nameof(HasRecoverableStateBackup));
        Changed(nameof(IsStateBackupBusy));
        Changed(nameof(CanCompareCurrentSystemBackup));
        Changed(nameof(CanCompareCurrentStateBackup));
        Changed(nameof(CanExportStateBackup));
    }

    /// <summary>Allows a new plan only after the prior result is reviewed and forces a new preflight.</summary>
    public void AcknowledgePreviousRunReview()
    {
        if (!CanAcknowledgePreviousRun) return;
        if (_latestExecutionHistory is not { } history) return;
        try
        {
            DeploymentRunLog.MarkReviewed(history);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or ArgumentException or System.Security.SecurityException)
        {
            Error = "无法保存检查状态；请检查本机运行记录后重试。";
            return;
        }

        _latestExecutionHistory = history with { ReviewAcknowledged = true };
        _needsPreviousRunReview = false;
        _wizardReturnPage = -1;
        Changed(nameof(NeedsPreviousRunReview));
        Changed(nameof(CanAcknowledgePreviousRun));
        InvalidatePreflightAndPreview();
        ClearExecutionSteps();
        ExecutionText = "";
        _highestCompletedWizardStep = 0;
        _wizardPage = 0;
        Changed(nameof(HasSavedExecutionHistory));
        NotifyWizardNavigationChanged();
    }

    private static string FormatLocalTime(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    private static Task InvokeOnSynchronizationContextAsync(SynchronizationContext? context, Action action)
    {
        if (context is null || SynchronizationContext.Current == context)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(state =>
        {
            var (callback, source) = ((Action, TaskCompletionSource<bool>))state!;
            try
            {
                callback();
                source.SetResult(true);
            }
            catch (Exception exception)
            {
                source.SetException(exception);
            }
        }, (action, completion));
        return completion.Task;
    }

    private void BeginExecutionSteps(IEnumerable<ExecutionStep> steps, bool includeBackup = false)
    {
        ExecutionStepStatuses.Clear();
        if (includeBackup)
            ExecutionStepStatuses.Add(new StudentExecutionStepStatus(
                "pre-change-backup", GetExecutionStepName("pre-change-backup"), ExecutionPlan.NotStarted,
                GetExecutionStatusLabel(ExecutionPlan.NotStarted), "等待开始。"));
        foreach (var step in steps)
            ExecutionStepStatuses.Add(new StudentExecutionStepStatus(
                step.Id, step.Description, ExecutionPlan.NotStarted,
                GetExecutionStatusLabel(ExecutionPlan.NotStarted), "等待开始。"));
        ExecutionOverallStatus = ExecutionPlan.Running;
        Changed(nameof(HasExecutionStepStatuses));
        NotifyExecutionProgressChanged();
    }

    private void UpdateExecutionStep(string stepId, string statusCode, string detail, string? name = null)
    {
        var index = -1;
        for (var i = 0; i < ExecutionStepStatuses.Count; i++)
        {
            if (string.Equals(ExecutionStepStatuses[i].StepId, stepId, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        var item = new StudentExecutionStepStatus(stepId,
            name ?? (index >= 0 ? ExecutionStepStatuses[index].Name : GetExecutionStepName(stepId)),
            statusCode, GetExecutionStatusLabel(statusCode), detail);
        if (index >= 0) ExecutionStepStatuses[index] = item;
        else ExecutionStepStatuses.Add(item);
        Changed(nameof(HasExecutionStepStatuses));
        Changed(nameof(CanRequestStopAfterCurrentStep));
        NotifyExecutionProgressChanged();
    }

    private void ApplyExecutionResults(IEnumerable<StepResult> results)
    {
        foreach (var result in results)
            UpdateExecutionStep(result.StepId, result.Status, result.Detail);
    }

    private void MarkRunningStepsNeedsReview(string detail)
    {
        for (var i = 0; i < ExecutionStepStatuses.Count; i++)
        {
            var item = ExecutionStepStatuses[i];
            if (item.StatusCode == ExecutionPlan.Running)
                ExecutionStepStatuses[i] = item with
                {
                    StatusCode = ExecutionPlan.NeedsReview,
                    Status = GetExecutionStatusLabel(ExecutionPlan.NeedsReview),
                    Detail = detail
                };
        }
        NotifyExecutionProgressChanged();
    }

    private void NotifyExecutionProgressChanged()
    {
        Changed(nameof(ExecutionProgressPercent));
        Changed(nameof(ExecutionProgressText));
        Changed(nameof(HasPendingComputerRenameRestart));
        Changed(nameof(ExecutionSummaryDescription));
    }

    private static string GetExecutionStepName(string stepId) => stepId switch
    {
        "pre-change-backup" => "保存执行前快照",
        "veyon-install" => "安装 Veyon",
        "veyon-key" => "导入并验证校区公钥",
        "website-agent" => "更新网站策略 Agent",
        "student-account" => "创建学生账户",
        "admin-password" => "修改指定管理员密码",
        "rename" => "修改电脑名",
        "verify" => "验证 Veyon 安装和服务",
        "veyon-verification" => "验证 Veyon 配置",
        _ => stepId
    };

    private static string GetExecutionStatusLabel(string status) => status switch
    {
        ExecutionPlan.NotStarted => "未开始",
        ExecutionPlan.Running => "进行中",
        ExecutionPlan.Succeeded => "已完成",
        ExecutionPlan.Failed => "失败",
        ExecutionPlan.Cancelled => "已取消",
        ExecutionPlan.Skipped => "已跳过",
        ExecutionPlan.RequiresReboot => "待重启",
        ExecutionPlan.PartiallyCompleted => "部分完成",
        ExecutionPlan.NeedsReview => "请检查",
        _ => "未知状态"
    };

    private void NotifyExecutionAvailabilityChanged()
    {
        Changed(nameof(CheckAllPassed)); Changed(nameof(CheckHasBlocker)); Changed(nameof(CheckNeedsAttention));
        Changed(nameof(CheckConclusionTitle)); Changed(nameof(CheckConclusionHint));
        Changed(nameof(DeploymentSelectionSummary));
        Changed(nameof(HasSelectedOperation));
        Changed(nameof(HasVeyonPackageRequirement));
        Changed(nameof(CanInstall));
        Changed(nameof(CanStartDeployment));
        Changed(nameof(CanInstallWebsitePolicyAgent));
        Changed(nameof(IsStudentControlsEnabled));
        Changed(nameof(CanPrepareDeployment));
        Changed(nameof(CanRefreshVeyonStatus));
        Changed(nameof(CanSearchCloudPackages)); Changed(nameof(CanLoadSelectedCloudPackage)); Changed(nameof(CanSaveSelectedCloudPackage));
        Changed(nameof(InstallAvailabilityText));
        Changed(nameof(DeploymentAvailabilityText));
        Changed(nameof(ExecutionInputNote));
        NotifyWizardNavigationChanged();
#if !STUDENT_SETUP_APP
        Changed(nameof(CanInstallTeacherVeyon));
        Changed(nameof(CanGenerateStudentPackage));
#endif
        Changed(nameof(CanVerifyStudentDeployment));
    }
    private void Invalidate()
    {
        _preflightRequestId++;
        _highestCompletedWizardStep = Math.Min(_highestCompletedWizardStep, WizardPage - 1);
        PreviewText = ""; PreflightText = ""; _preflightReport = null; _preflightInput = null;
        PreflightItems.Clear(); PreflightSummaryText = ""; Changed(nameof(HasPreflightItems));
        _studentDeploymentVerification = null;
        StudentDeploymentVerificationText = "";
        Error = "";
        Changed(nameof(NeedsVeyonPackage));
        Changed(nameof(DeploymentSelectionSummary));
        Changed(nameof(AdminPasswordRecommendationWarning));
        NotifyExecutionAvailabilityChanged();
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
