using System.ComponentModel;
using System.Runtime.CompilerServices;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _campus = "", _prefix = "PC-", _number = "", _studentAccount = "User", _adminAccount = "";
    private string _error = "", _packageError = "", _preview = "", _preflight = "", _packageStatus = "未选择部署包", _operationHelp = "";
    private string _roomPrefix = "PC-", _roomStart = "1", _roomCount = "150", _roomError = "";
    private IReadOnlyList<string> _roomNames = Array.Empty<string>();
    private bool _installVeyon, _rename, _createStudent, _changeAdmin, _isStudent = true;
    private PackageContext? _package;

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
    public bool HasError => Error.Length > 0;
    public bool HasPackageError => PackageError.Length > 0;
    public bool HasGlobalError => HasError && (!HasPackageError || Error != PackageError);
    public bool HasPreview => PreviewText.Length > 0;
    public bool HasPreflight => PreflightText.Length > 0;
    public string OperationHelpText { get => _operationHelp; private set { _operationHelp = value; Changed(); Changed(nameof(HasOperationHelp)); } }
    public bool HasOperationHelp => OperationHelpText.Length > 0;
    public bool IsStudent => _isStudent;
    public bool IsTeacher => !_isStudent;
    public string PageTitle => IsStudent ? "学生端配置" : "教师端准备";
    public string PageDescription => IsStudent ? "分别选择要做的操作，核对目标和计划。" : "预览机房电脑清单；教师端实际配置将在后续接入。";
    public string EnvironmentNote => OperatingSystem.IsWindows()
        ? "当前为 Windows · 此版本只提供计划预览，不修改系统。"
        : "当前为界面预览环境 · 真正的系统部署将仅支持 Windows。";
    public string ComputerName
    {
        get { try { return MachineNaming.CreateName(Prefix, Number); } catch (InvalidDataException) { return "等待有效编号与前缀"; } }
    }

    public string RoomPrefix { get => _roomPrefix; set { _roomPrefix = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string RoomStart { get => _roomStart; set { _roomStart = value ?? ""; Changed(); ClearRoomPreview(); } }
    public string RoomCount { get => _roomCount; set { _roomCount = value ?? ""; Changed(); ClearRoomPreview(); } }
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
    public void ToggleOperationHelp(string message) => OperationHelpText = OperationHelpText == message ? "" : message;
    public void ReportError(string message) { PreviewText = ""; PreflightText = ""; Error = message; }
    private void ReportPackageError(string message) { PackageError = message; ReportError(message); }
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
    private void Invalidate() { PreviewText = ""; PreflightText = ""; Error = ""; }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
