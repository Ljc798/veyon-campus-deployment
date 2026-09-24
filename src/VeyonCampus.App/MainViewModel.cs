using System.ComponentModel;
using System.Runtime.CompilerServices;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _campus = "", _prefix = "PC-", _number = "", _error = "", _preview = "";
    private string _packageStatus = "未选择部署包 · 手动填写仅用于预览";
    private bool _rename, _isStudent = true;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Campus { get => _campus; set { _campus = value; Changed(); Invalidate(); } }
    public string Prefix { get => _prefix; set { _prefix = value; Changed(); Invalidate(); } }
    public string Number { get => _number; set { _number = value; Changed(); Invalidate(); } }
    public bool RenameComputer { get => _rename; set { _rename = value; Changed(); Invalidate(); } }
    public string PackageStatus { get => _packageStatus; private set { _packageStatus = value; Changed(); } }
    public string Error { get => _error; private set { _error = value; Changed(); Changed(nameof(HasError)); } }
    public string PreviewText { get => _preview; private set { _preview = value; Changed(); Changed(nameof(HasPreview)); } }
    public bool HasError => Error.Length > 0;
    public bool HasPreview => PreviewText.Length > 0;
    public bool IsStudent => _isStudent;
    public bool IsTeacher => !_isStudent;
    public string PageTitle => IsStudent ? "学生端配置" : "教师端准备";
    public string PageDescription => IsStudent ? "准备部署资料，检查每台学生电脑的配置计划。" : "统一准备校区配置，让后续逐机部署更简单。";
    public string EnvironmentNote => OperatingSystem.IsWindows()
        ? "当前为 Windows · 基础预览阶段，无需管理员权限。"
        : "当前为界面预览环境 · 实际部署将仅支持 Windows，当前可读取资料和预览计划。";
    public string ComputerName
    {
        get { try { return DeploymentPlan.ValidateComputerName(Prefix, Number); } catch (InvalidDataException) { return "等待有效编号与前缀"; } }
    }
    public void Navigate(bool student)
    {
        _isStudent = student;
        Changed(nameof(IsStudent)); Changed(nameof(IsTeacher)); Changed(nameof(PageTitle)); Changed(nameof(PageDescription));
    }
    public void Reset()
    {
        Campus = ""; Prefix = "PC-"; Number = ""; RenameComputer = false;
        PackageStatus = "未选择部署包 · 手动填写仅用于预览";
    }
    public void LoadPackage(string directory)
    {
        // Clear old package information before attempting a different package.
        Reset();
        try
        {
            var package = CampusPackage.Load(directory);
            Campus = package.Campus; Prefix = package.ComputerPrefix;
            PackageStatus = $"已读取：{directory}\n公钥文件与配置格式检查通过；未读取 admin.txt。表单修改仅影响本次预览。";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            ReportError($"无法读取部署包：{ex.Message}");
        }
    }
    public void GeneratePreview()
    {
        Error = ""; PreviewText = "";
        try
        {
            var plan = DeploymentPlan.Create(Campus, Prefix, Number, RenameComputer);
            var naming = plan.RenameComputer ? $"计划改名：{plan.ComputerName}" : $"规划编号名称：{plan.ComputerName}（保留本机现有名称）";
            PreviewText = $"校区：{plan.Campus}\n{naming}\n账户与密码：保持现状\n\n" +
                string.Join("\n\n", plan.Steps.Select((step, i) => $"{i + 1}. {step}"));
        }
        catch (InvalidDataException ex) { Error = ex.Message; }
    }
    public void ReportError(string message) { PreviewText = ""; Error = message; }
    private void Invalidate() { PreviewText = ""; Error = ""; Changed(nameof(ComputerName)); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
