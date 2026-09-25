using System.ComponentModel;
using System.Runtime.CompilerServices;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _campus = "", _prefix = "PC-", _number = "", _studentAccount = "User", _adminAccount = "";
    private string _error = "", _packageError = "", _preview = "", _preflight = "", _packageStatus = "未选择部署包", _operationHelp = "", _execution = "";
    private string _roomPrefix = "PC-", _roomStart = "1", _roomCount = "150", _roomError = "";
    private string _campusId = "", _roomOutputDir = "", _installerSource = "", _packageOutput = "", _packageOutputError = "";
    private IReadOnlyList<string> _roomNames = Array.Empty<string>();
    private bool _installVeyon, _rename, _createStudent, _changeAdmin, _isStudent = true, _isExecuting = false;
    private PackageContext? _package;
    private WindowsVeyonAdapter? _adapter = new();

    public event PropertyChangedEventHandler? PropertyChanged;
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
    public string StudentAccountName { get => _studentAccount; set { _studentAccount = value ?? ""; Changed(); Invalidate(); } }
    public string AdminAccountName { get => _adminAccount; set { _adminAccount = value ?? ""; Changed(); Invalidate(); } }
    public bool InstallVeyon { get => _installVeyon; set { _installVeyon = value; Changed(); Invalidate(); } }
    public bool RenameComputer { get => _rename; set { _rename = value; Changed(); Invalidate(); } }
    public bool CreateStudent { get => _createStudent; set { _createStudent = value; Changed(); Invalidate(); } }
    public bool ChangeAdminPassword { get => _changeAdmin; set { _changeAdmin = value; Changed(); Invalidate(); } }
    public PackageContext? LoadedPackage => _package;
    public string PackageStatus { get => _packageStatus; private set { _packageStatus = value; Changed(); } }
    public string Error { get => _error; private set { _error = value; Changed(); Changed(nameof(HasError)); Changed(nameof(HasGlobalError)); } }
    public string PackageError { get => _packageError; private set { _packageError = value; Changed(); Changed(nameof(HasPackageError)); Changed(nameof(HasGlobalError)); } }
    public string PreviewText { get => _preview; private set { _preview = value; Changed(); Changed(nameof(HasPreview)); } }
    public string PreflightText { get => _preflight; private set { _preflight = value; Changed(); Changed(nameof(HasPreflight)); } }
    public string ExecutionText { get => _execution; private set { _execution = value; Changed(); Changed(nameof(HasExecution)); } }
    public bool HasError => Error.Length > 0;
    public bool HasPackageError => PackageError.Length > 0;
    public bool HasGlobalError => HasError && (!HasPackageError || Error != PackageError);
    public bool HasPreview => PreviewText.Length > 0;
    public bool HasPreflight => PreflightText.Length > 0;
    public bool HasExecution => ExecutionText.Length > 0;
    public bool IsExecuting => _isExecuting;
    // ① 安装 Veyon：需要已载入含安装资源的部署包即可（与是否勾选无关）。
    public bool CanInstall => !IsExecuting && LoadedPackage is not null &&
        LoadedPackage.InstallerPath is not null && !HasGlobalError;
    // ② 配置并部署：需要勾选了操作、载入部署包，且没有全局错误。
    public bool CanStartDeployment => !IsExecuting &&
        (InstallVeyon || RenameComputer || CreateStudent || ChangeAdminPassword) &&
        LoadedPackage is not null && !HasGlobalError;
    public string OperationHelpText { get => _operationHelp; private set { _operationHelp = value; Changed(); Changed(nameof(HasOperationHelp)); } }
    public bool HasOperationHelp => OperationHelpText.Length > 0;
    public bool IsStudent => _isStudent;
    public bool IsTeacher => !_isStudent;
    public string PageTitle => IsStudent ? "学生端配置" : "教师端准备";
    public string PageDescription => IsStudent ? "分别选择要做的操作，核对目标和计划。" : "预览机房电脑清单；教师端实际配置将在后续接入。";
    public string EnvironmentNote => OperatingSystem.IsWindows()
        ? "当前为 Windows · 支持分步安装与部署：先安装 Veyon，再配置并部署。"
        : "当前为界面预览环境 · 真正的系统部署将仅支持 Windows。";
    public string ComputerName
    {
        get { try { return MachineNaming.CreateName(Prefix, Number); } catch (InvalidDataException) { return "等待有效编号与前缀"; } }
    }

    public string RoomPrefix { get => _roomPrefix; set { _roomPrefix = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string RoomStart { get => _roomStart; set { _roomStart = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string RoomCount { get => _roomCount; set { _roomCount = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string CampusId { get => _campusId; set { _campusId = value ?? ""; Changed(); } }
    public string RoomOutputDir { get => _roomOutputDir; set { _roomOutputDir = value ?? ""; Changed(); } }
    public string InstallerSource { get => _installerSource; set { _installerSource = value ?? ""; Changed(); } }
    public string PackageOutput { get => _packageOutput; private set { _packageOutput = value; Changed(); Changed(nameof(HasPackageOutput)); } }
    public string PackageOutputError { get => _packageOutputError; set { _packageOutputError = value ?? ""; Changed(); Changed(nameof(HasPackageOutputError)); } }
    public bool HasPackageOutput => PackageOutput.Length > 0;
    public bool HasPackageOutputError => PackageOutputError.Length > 0;
    public IReadOnlyList<string> RoomNames { get => _roomNames; private set { _roomNames = value; Changed(); Changed(nameof(HasRoomPreview)); } }
    public bool HasRoomPreview => RoomNames.Count > 0;
    public string RoomError { get => _roomError; private set { _roomError = value; Changed(); Changed(nameof(HasRoomError)); } }
    public bool HasRoomError => RoomError.Length > 0;
    public string RoomSummary => HasRoomPreview ? $"共 {RoomNames.Count} 台，首台 {RoomNames[0]}，末台 {RoomNames[^1]}" : "尚未生成清单";

    public void Navigate(bool student)
    {
        _isStudent = student;
        Changed(nameof(IsStudent)); Changed(nameof(IsTeacher)); Changed(nameof(PageTitle)); Changed(nameof(PageDescription));
    }
    public void Reset()
    {
        _package = null;
        _campus = ""; Changed(nameof(Campus));
        _prefix = "PC-"; Changed(nameof(Prefix));
        Number = ""; StudentAccountName = "User"; AdminAccountName = "";
        InstallVeyon = false; RenameComputer = false; CreateStudent = false; ChangeAdminPassword = false;
        PackageStatus = "未选择部署包";
        ExecutionText = "";
        Changed(nameof(ComputerName)); Invalidate();
    }
    public void LoadPackage(string path)
    {
        ClearPackageSelection();
        try
        {
            var directory = PackageSource.Resolve(path);
            var loaded = PackageContext.Load(directory);
            _package = loaded;
            _campus = loaded.Campus; Changed(nameof(Campus));
            _prefix = loaded.ComputerPrefix; Changed(nameof(Prefix)); Changed(nameof(ComputerName));
            PackageStatus = $"已读取：{directory}\n{(loaded.SchemaVersion == 0 ? "旧版" : "新版")}资料，RSA 公钥指纹 {loaded.PublicKeyFingerprint[..12]}…；未读取 admin.txt。";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or
                                   System.Text.Json.JsonException or ArgumentException)
        {
            ReportPackageError($"无法读取部署包：{ex.Message}");
        }
    }
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
            var header = $"已选操作：{(InstallVeyon ? "Veyon " : "")}{(RenameComputer ? "改名 " : "")}" +
                         $"{(CreateStudent ? "创建学生账户 " : "")}{(ChangeAdminPassword ? "修改管理员密码" : "")}";
            if (plan.ComputerName is not null) header += $"\n目标电脑名：{plan.ComputerName}";
            if (plan.Campus is not null) header += $"\n校区：{plan.Campus}";
            PreviewText = header + "\n\n" +
                string.Join("\n\n", plan.Steps.Select((step, i) => $"{i + 1}. {step.Description}"));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or
                                   System.Text.Json.JsonException or ArgumentException)
        {
            Error = ex.Message;
        }
    }
    public void CheckEnvironment()
    {
        Error = ""; PreflightText = "";
        try
        {
            var input = new PlanInput(Campus, Prefix, Number, StudentAccountName, AdminAccountName,
                new OperationSelection(InstallVeyon, RenameComputer, CreateStudent, ChangeAdminPassword), _package);
            var report = ReadOnlyPreflight.Check(input);
            PreflightText = $"检查时间：{report.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 计划摘要：{report.PlanSha256[..12]}…" +
                (report.PackageSha256 is null ? "" : $" · 部署包摘要：{report.PackageSha256[..12]}…") + "\n\n" +
                string.Join("\n\n", report.Checks.Select(c =>
                $"{(c.Level == CheckLevel.Pass ? "✓" : c.Level == CheckLevel.Blocked ? "✗" : c.Level == CheckLevel.NotApplicable ? "—" : "?")} {c.Detail}"));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or
                                   System.Text.Json.JsonException or ArgumentException)
        {
            Error = ex.Message;
        }
    }
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
    private static readonly System.Text.RegularExpressions.Regex CampusIdPattern =
        new("^[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public void GenerateStudentPackage()
    {
        PackageOutput = ""; PackageOutputError = "";
        try
        {
            var campus = CampusId.Trim();
            if (campus.Length == 0 || !CampusIdPattern.IsMatch(campus))
            {
                PackageOutputError = "校区 ID 只能包含中英文、数字、连字符或下划线。";
                return;
            }
            if (string.IsNullOrWhiteSpace(InstallerSource) || !File.Exists(InstallerSource))
            {
                PackageOutputError = "请先选择 4.11.2 Veyon 安装程序（.exe 文件）。";
                return;
            }
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var outDir = string.IsNullOrWhiteSpace(RoomOutputDir)
                ? Path.Combine(desktop, "Veyon-Student-Deployment-" + campus)
                : Path.GetFullPath(RoomOutputDir);
            if (Directory.Exists(outDir))
            {
                PackageOutputError = "输出目录已存在，为防止覆盖现有密钥，请改路径或先查清原目录内容。";
                return;
            }
            var built = PackageBuilder.Build(outDir, campus, RoomPrefix, InstallerSource);
            PackageOutput = $"已生成部署包：{built}\n包含安装程序、公钥、campus.json 与 manifest.json；私钥保留在本地，未进入学生包。";
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or
                                   UnauthorizedAccessException or IOException)
        {
            PackageOutputError = "生成失败：" + ex.Message;
        }
    }
    public void ToggleOperationHelp(string message) => OperationHelpText = OperationHelpText == message ? "" : message;
    public void ReportError(string message) { PreviewText = ""; PreflightText = ""; Error = message; }

    /// <summary>第一步：单独安装 Veyon（学生组件，不装 Master）。安装完成后
    /// 系统进入"已装 Veyon"状态，再进行第二步部署（配置公钥/账户/改名）。</summary>
    public void InstallVeyonOnly()
    {
        if (_isExecuting) return;
        ExecutionText = ""; Error = "";
        try
        {
            if (LoadedPackage is null || LoadedPackage.InstallerPath is null)
            {
                Error = "请先选择包含安装资源的部署包（manifest.json 的 installer 字段）；旧版包不支持离线安装。";
                return;
            }
            _isExecuting = true;
            Changed(nameof(IsExecuting));
            Changed(nameof(CanInstall));
            Changed(nameof(CanStartDeployment));
            var adapter = _adapter ??= new WindowsVeyonAdapter();
            var installerResult = adapter.InstallVeyonOnly(LoadedPackage, isTeacher: false);
            var verification = WindowsVeyonAdapter.Verify(LoadedPackage);
            var lines = new List<string>
            {
                "安装结果",
                installerResult.Detail,
                "",
                "读回验证",
                $"安装状态：{verification.InstallState} · {verification.Version}",
                $"服务：{verification.ServiceState}",
                "",
                "下一步：点击“② 配置并部署 Veyon”完成公钥配置与后续操作。"
            };
            if (!installerResult.Ok)
                lines.Add("安装未成功；未继续配置步骤。请先解决安装问题。");
            ExecutionText = string.Join("\n", lines);
            InvalidatePreflightAndPreview();
        }
        catch (Exception ex)
        {
            Error = $"安装执行失败：{ex.Message}";
        }
        finally
        {
            _isExecuting = false;
            Changed(nameof(IsExecuting));
            Changed(nameof(CanInstall));
            Changed(nameof(CanStartDeployment));
        }
    }

    /// <summary>第二步：配置并部署（公钥 + 账户 + 改名）。要求 Veyon 已安装。
    /// 未安装时本步骤会先自动完成安装（一次性兼容入口）。</summary>
    public void RunDeployment()
    {
        if (_isExecuting) return;
        ExecutionText = ""; Error = "";
        try
        {
            var operations = new OperationSelection(InstallVeyon, RenameComputer, CreateStudent, ChangeAdminPassword);
            if (!operations.Any)
            {
                Error = "请至少勾选一项操作再开始部署。";
                return;
            }
            // P4 切片：配置 Veyon（公钥/认证）已接入执行；改名 / 账户属于 P5/P6，
            // 未实现前不允许混合执行，避免"勾选了但实际没做"的误解。
            if (!InstallVeyon && (RenameComputer || CreateStudent || ChangeAdminPassword))
            {
                var unsupported = new List<string>();
                if (RenameComputer) unsupported.Add("修改电脑名（P5）");
                if (CreateStudent) unsupported.Add("创建学生账户（P6）");
                if (ChangeAdminPassword) unsupported.Add("修改管理员密码（P6）");
                Error = "本版本尚不能执行：" + string.Join("、", unsupported) + "。当前只有“Veyon 配置”已接入；改名与账户将在 P5/P6 落地。";
                return;
            }
            if (LoadedPackage is null || LoadedPackage.InstallerPath is null)
            {
                Error = "所选部署包缺少 Veyon 安装资源（manifest.json 的 installer 字段）；旧版包不支持离线安装。";
                return;
            }
            _isExecuting = true;
            Changed(nameof(IsExecuting));
            Changed(nameof(CanInstall));
            Changed(nameof(CanStartDeployment));
            var adapter = _adapter ??= new WindowsVeyonAdapter();

            // 若尚未安装，先自动补安装（一次性兼容路径）；已装则直接进入配置。
            var facts = VeyonFacts.Probe();
            var installLine = facts.Status == VeyonFacts.NotInstalled
                ? (adapter.InstallVeyonOnly(LoadedPackage, isTeacher: false).Detail + "（检测到未安装，已自动补装）")
                : "Veyon 已安装，跳过安装，直接配置。";
            var keyResult = adapter.ConfigureVeyonOnly(LoadedPackage, isTeacher: false);

            var verification = WindowsVeyonAdapter.Verify(LoadedPackage);
            var lines = new List<string>
            {
                "部署结果",
                installLine,
                keyResult.Detail,
                "",
                "读回验证",
                $"安装状态：{verification.InstallState} · {verification.Version}",
                $"公钥：{(verification.KeyImported ? "已导入" : "未确认")} {verification.KeyDetail}",
                $"服务：{verification.ServiceState}"
            };
            if (!keyResult.Ok)
            {
                lines.Add("");
                lines.Add("配置未成功；已完成的安装保留，未修改的内容保持原状。");
            }
            ExecutionText = string.Join("\n", lines);
            InvalidatePreflightAndPreview();
        }
        catch (Exception ex)
        {
            Error = $"部署执行失败：{ex.Message}";
        }
        finally
        {
            _isExecuting = false;
            Changed(nameof(IsExecuting));
            Changed(nameof(CanInstall));
            Changed(nameof(CanStartDeployment));
        }
    }
    private void ReportPackageError(string message) { PackageError = message; ReportError(message); }
    private void InvalidatePreflightAndPreview() { PreviewText = ""; PreflightText = ""; Error = ""; }
    private void ClearLoadedPackage()
    {
        if (_package is null) return;
        _package = null;
        PackageStatus = "校区或前缀已修改；旧公钥资料已失效，请重新选择部署包。";
    }
    private void ClearPackageSelection()
    {
        _package = null;
        _campus = ""; Changed(nameof(Campus));
        _prefix = "PC-"; Changed(nameof(Prefix)); Changed(nameof(ComputerName));
        PackageStatus = "未选择部署包";
        PackageError = "";
        Invalidate();
    }
    private void ClearRoomPreview() { RoomNames = Array.Empty<string>(); RoomError = ""; Changed(nameof(RoomSummary)); }
    private void Invalidate() { PreviewText = ""; PreflightText = ""; Error = ""; ExecutionText = ""; Changed(nameof(CanInstall)); Changed(nameof(CanStartDeployment)); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
