using System.ComponentModel;
using System.Reflection;
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
    private PreflightReport? _preflightReport;
    private PlanInput? _preflightInput;
    private int _preflightRequestId;
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
    public string PreviewText
    {
        get => _preview;
        private set
        {
            _preview = value;
            Changed(); Changed(nameof(HasPreview)); Changed(nameof(CanInstall)); Changed(nameof(CanStartDeployment));
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
    public bool IsExecuting => _isExecuting;
    // ① 安装 Veyon：需要已载入含安装资源的部署包即可（与是否勾选无关）。
    public bool CanInstall => !IsExecuting && InstallVeyon && !RenameComputer && !CreateStudent && !ChangeAdminPassword &&
        HasPreview && LoadedPackage is not null && LoadedPackage.InstallerPath is not null && !HasGlobalError &&
        HasCurrentExecutablePreflight();
    // ② 配置并部署：需要勾选了操作、载入部署包，且没有全局错误。
    public bool CanStartDeployment => !IsExecuting &&
        InstallVeyon && !RenameComputer && !CreateStudent && !ChangeAdminPassword &&
        HasPreview && LoadedPackage is not null && !HasGlobalError && HasCurrentExecutablePreflight();
    public string OperationHelpText { get => _operationHelp; private set { _operationHelp = value; Changed(); Changed(nameof(HasOperationHelp)); } }
    public bool HasOperationHelp => OperationHelpText.Length > 0;
    public bool IsStudent => _isStudent;
    public bool IsTeacher => !_isStudent;
    public string PageTitle => IsStudent ? "学生端配置" : "教师端准备";
    public string PageDescription => IsStudent ? "分别选择要做的操作，核对目标和计划。" : "预览机房电脑清单；教师端实际配置将在后续接入。";
    public string AppVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "版本未知";
    public string EnvironmentNote => OperatingSystem.IsWindows()
        ? "当前为 Windows · Veyon 执行功能为实验阶段；必须通过预检，结果未完整读回时显示需核对。"
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
            if (CreateStudent) risks.Add("新建账户失败时不会自动删除已创建账户；初始密码在后续独立步骤设置。");
            if (ChangeAdminPassword) risks.Add("密码无法读回或自动恢复；执行前必须核对本地账户 SID。");
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
    public void CheckEnvironment()
    {
        BeginEnvironmentCheck();
        try
        {
            var input = CurrentPlanInput();
            var report = ReadOnlyPreflight.Check(input);
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
            var report = await Task.Run(() => ReadOnlyPreflight.Check(input));
            if (requestId != _preflightRequestId || input != CurrentPlanInput()) return;
            DisplayPreflight(input, report);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or
                                   System.Text.Json.JsonException or ArgumentException)
        {
            if (requestId == _preflightRequestId) Error = ex.Message;
        }
    }
    private int BeginEnvironmentCheck()
    {
        Error = ""; PreflightText = "";
        _preflightReport = null; _preflightInput = null;
        Changed(nameof(CanInstall)); Changed(nameof(CanStartDeployment));
        return ++_preflightRequestId;
    }
    private PlanInput CurrentPlanInput() => new(Campus, Prefix, Number, StudentAccountName, AdminAccountName,
        new OperationSelection(InstallVeyon, RenameComputer, CreateStudent, ChangeAdminPassword), _package);
    private void DisplayPreflight(PlanInput input, PreflightReport report)
    {
        _preflightReport = report;
        _preflightInput = input;
        Changed(nameof(CanInstall)); Changed(nameof(CanStartDeployment));
        PreflightText = $"检查时间：{report.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 计划摘要：{report.PlanSha256[..12]}…" +
            (report.PackageSha256 is null ? "" : $" · 部署包摘要：{report.PackageSha256[..12]}…") + "\n\n" +
            string.Join("\n\n", report.Checks.Select(c =>
            $"{(c.Level == CheckLevel.Pass ? "✓" : c.Level == CheckLevel.Blocked ? "✗" : c.Level == CheckLevel.NotApplicable ? "—" : "?")} {c.Detail}"));
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
        string? outputDirectory = null;
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
                PackageOutputError = $"请先选择 Veyon {VeyonInstallerTrust.Version} 安装程序（.exe 文件）。";
                return;
            }
            var installerTrust = VeyonInstallerTrust.Check(InstallerSource);
            if (!installerTrust.IsAllowed)
            {
                PackageOutputError = "安装程序校验失败：" + installerTrust.Detail;
                return;
            }
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var outDir = string.IsNullOrWhiteSpace(RoomOutputDir)
                ? Path.Combine(desktop, "Veyon-Student-Deployment-" + campus)
                : Path.GetFullPath(RoomOutputDir);
            outputDirectory = outDir;
            if (Directory.Exists(outDir))
            {
                PackageOutputError = "输出目录已存在，为防止覆盖现有密钥，请改路径或先查清原目录内容。";
                return;
            }
            var built = PackageBuilder.Build(outDir, campus, RoomPrefix, InstallerSource);
            var keyDirectory = Path.Combine(Path.GetDirectoryName(built)!, Path.GetFileName(built) + "-teacher-only");
            PackageOutput = $"已生成学生部署包：{built}\n教师私钥保存在包外受限目录：{keyDirectory}\n安装器核验：{installerTrust.Detail}\n分发时只复制学生部署包目录。";
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or
                                   UnauthorizedAccessException or IOException)
        {
            PackageOutputError = "生成失败：" + ex.Message;
            if (outputDirectory is not null)
            {
                var fullOutput = Path.GetFullPath(outputDirectory);
                var keyDirectory = Path.Combine(Path.GetDirectoryName(fullOutput)!, Path.GetFileName(fullOutput) + "-teacher-only");
                if (Directory.Exists(keyDirectory) && Directory.EnumerateFiles(keyDirectory, "*-private.pem").Any())
                    PackageOutputError += $"\n已生成的教师私钥保留在受限目录：{keyDirectory}";
            }
        }
    }
    public void ToggleOperationHelp(string message) => OperationHelpText = OperationHelpText == message ? "" : message;
    public void ReportError(string message) { PreviewText = ""; PreflightText = ""; Error = message; }

    /// <summary>第一步：单独安装 Veyon（学生组件，不装 Master）。安装完成后
    /// 系统进入"已装 Veyon"状态，再进行第二步部署（配置公钥/账户/改名）。</summary>
    public async Task InstallVeyonOnlyAsync()
    {
        if (_isExecuting) return;
        ExecutionText = ""; Error = "";
        try
        {
            if (!InstallVeyon || RenameComputer || CreateStudent || ChangeAdminPassword)
            {
                Error = "安装入口只接受明确选择 Veyon 且未选择改名或账户操作的计划。";
                return;
            }
            if (LoadedPackage is null || LoadedPackage.InstallerPath is null)
            {
                Error = "请先选择包含安装资源的部署包（manifest.json 的 installer 字段）；旧版包不支持离线安装。";
                return;
            }
            var frozenPlan = await FreezeAndValidateExecutionPlanAsync();
            if (frozenPlan?.Package is not { } frozenPackage) return;
            _isExecuting = true;
            Changed(nameof(IsExecuting));
            Changed(nameof(CanInstall));
            Changed(nameof(CanStartDeployment));
            var adapter = _adapter ??= new WindowsVeyonAdapter();
            await Task.Run(frozenPackage.VerifyUnchanged);
            var installerResult = await Task.Run(() => adapter.InstallVeyonOnly(frozenPackage, isTeacher: false));
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
            _isExecuting = false;
            Changed(nameof(IsExecuting));
            Changed(nameof(CanInstall));
            Changed(nameof(CanStartDeployment));
        }
    }

    /// <summary>第二步：配置并部署（公钥 + 账户 + 改名）。要求 Veyon 已安装。
    /// 未安装时本步骤会先自动完成安装（一次性兼容入口）。</summary>
    public async Task RunDeploymentAsync()
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
            if (!InstallVeyon || RenameComputer || CreateStudent || ChangeAdminPassword)
            {
                var unsupported = new List<string>();
                if (!InstallVeyon) unsupported.Add("未选择 Veyon 配置");
                if (RenameComputer) unsupported.Add("修改电脑名");
                if (CreateStudent) unsupported.Add("创建学生账户");
                if (ChangeAdminPassword) unsupported.Add("修改管理员密码");
                Error = "当前执行器仅支持单独配置 Veyon；本次选择含未支持操作，已在修改前拒绝：" + string.Join("、", unsupported) + "。";
                return;
            }
            // P4 切片：配置 Veyon（公钥/认证）已接入执行；改名 / 账户属于 P5/P6，
            // 未实现前不允许混合执行，避免"勾选了但实际没做"的误解。
            if (LoadedPackage is null || LoadedPackage.InstallerPath is null)
            {
                Error = "所选部署包缺少 Veyon 安装资源（manifest.json 的 installer 字段）；旧版包不支持离线安装。";
                return;
            }
            var frozenPlan = await FreezeAndValidateExecutionPlanAsync();
            if (frozenPlan?.Package is not { } frozenPackage) return;
            _isExecuting = true;
            Changed(nameof(IsExecuting));
            Changed(nameof(CanInstall));
            Changed(nameof(CanStartDeployment));
            var adapter = _adapter ??= new WindowsVeyonAdapter();

            // 若尚未安装，先自动补安装（一次性兼容路径）；已装则直接进入配置。
            await Task.Run(frozenPackage.VerifyUnchanged);
            var facts = await Task.Run(VeyonFacts.Probe);
            if (facts.Status == "installed" && !VeyonFacts.IsSupportedVersionDetail(facts.VersionDetail))
            {
                var mismatch = new StepResult("veyon-version", ExecutionPlan.NeedsReview,
                    $"当前安装版本不是已验证的 Veyon {VeyonInstallerTrust.Version}，没有继续配置。{facts.VersionDetail}");
                var mismatchSummary = ExecutionPlan.Summarize([mismatch]);
                ExecutionText = $"部署结果\n整体状态：{mismatchSummary.Status}\n{mismatch.Detail}\n配置未开始。";
                InvalidatePreflightAndPreview();
                return;
            }
            if (facts.Status != VeyonFacts.NotInstalled && facts.Status != "installed")
            {
                var unknown = new StepResult("veyon-state", ExecutionPlan.NeedsReview,
                    "无法确认现有 Veyon 安装状态；为避免覆盖未知安装，没有继续。");
                var unknownSummary = ExecutionPlan.Summarize([unknown]);
                ExecutionText = $"部署结果\n整体状态：{unknownSummary.Status}\n{unknown.Detail}\n配置未开始。";
                InvalidatePreflightAndPreview();
                return;
            }
            var installResult = facts.Status == VeyonFacts.NotInstalled
                ? await Task.Run(() => adapter.InstallVeyonOnly(frozenPackage, isTeacher: false))
                : new StepResult("veyon-install", ExecutionPlan.Skipped,
                    $"已安装固定版本 Veyon {VeyonInstallerTrust.Version}，跳过安装步骤。");
            if (!installResult.Ok || installResult.RebootRequired)
            {
                var installSummary = ExecutionPlan.Summarize([installResult]);
                ExecutionText = $"安装结果\n整体状态：{installSummary.Status}" +
                                (installSummary.RebootRequired ? " · 需要重启" : "") +
                                $"\n{installResult.Detail}\n\n配置未开始。";
                InvalidatePreflightAndPreview();
                return;
            }
            await Task.Run(frozenPackage.VerifyUnchanged);
            var keyResult = await Task.Run(() => adapter.ConfigureVeyonOnly(frozenPackage, isTeacher: false));

            var verification = await Task.Run(() => WindowsVeyonAdapter.Verify(frozenPackage));
            var stepResults = new List<StepResult> { installResult, keyResult };
            if (keyResult.Ok && (!verification.KeyImported || verification.InstallState != "已安装" ||
                                 !VeyonFacts.IsSupportedVersionDetail(verification.Version) ||
                                 !verification.ServiceState.Contains("正在运行", StringComparison.Ordinal)))
                stepResults.Add(new("verify", ExecutionPlan.NeedsReview,
                    "配置命令已返回，但版本、公钥指纹或服务状态尚未全部读回确认。"));
            var summary = ExecutionPlan.Summarize(stepResults);
            var lines = new List<string>
            {
                "部署结果",
                $"整体状态：{summary.Status}" + (summary.RebootRequired ? " · 需要重启" : ""),
                installResult.Detail,
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
            else if (summary.Status == ExecutionPlan.NeedsReview)
            {
                lines.Add("");
                lines.Add("配置命令已返回，但整体结果需核对：安装、公钥指纹或服务运行状态尚未全部读回确认。");
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
        var currentInput = new PlanInput(Campus, Prefix, Number, StudentAccountName, AdminAccountName,
            new OperationSelection(InstallVeyon, RenameComputer, CreateStudent, ChangeAdminPassword), _package);
        if (confirmedReport is null || confirmedInput is null || !HasCurrentExecutablePreflight())
        { Error = "执行前检查缺失、已过期或与当前资料不一致。请重新检查。"; return null; }
        try
        {
            var result = await Task.Run(() =>
            {
                var report = ReadOnlyPreflight.Check(confirmedInput);
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
                var plan = ExecutionPlan.Create(confirmedInput, confirmedInput.Package);
                if (plan.Steps.Any(s => s.Id is not ("veyon-install" or "veyon-key")))
                    return (Plan: (ExecutionPlan?)null, Error: "冻结计划包含当前执行器不支持的步骤，已停止。");
                plan.Package!.VerifyUnchanged();
                return (Plan: (ExecutionPlan?)plan, Error: (string?)null);
            });
            if (currentInput != confirmedInput || _preflightReport != confirmedReport)
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
    private void Invalidate() { _preflightRequestId++; PreviewText = ""; PreflightText = ""; _preflightReport = null; _preflightInput = null; Error = ""; ExecutionText = ""; Changed(nameof(CanInstall)); Changed(nameof(CanStartDeployment)); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
