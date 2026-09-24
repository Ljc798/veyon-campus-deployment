using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public sealed record CampusPackage(string Campus, string ComputerPrefix, string PublicKeyPath)
{
    public static CampusPackage Load(string directory)
    {
        var root = Path.GetFullPath(directory);
        var config = Path.Combine(root, "campus.json");
        if (!File.Exists(config))
            throw new InvalidDataException("所选文件夹中没有 campus.json，请选择完整的学生部署包。");
        if (new FileInfo(config).Length > 64 * 1024)
            throw new InvalidDataException("campus.json 过大，请检查部署包。");

        // ReadAllText handles the BOM written by Windows PowerShell 5.1.
        using var document = JsonDocument.Parse(File.ReadAllText(config));
        var json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("campus.json 必须是 JSON 对象。");
        string Required(string name)
        {
            if (!json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException($"campus.json 缺少有效的 {name} 字段。");
            return value.GetString()!;
        }

        var campus = Required("campus");
        var prefix = Required("computerPrefix");
        var keyFile = Required("keyFile");
        if (campus.Length > 100 || campus.Any(char.IsControl))
            throw new InvalidDataException("校区名称过长或包含控制字符。");
        // Treat separators consistently on Windows and macOS; accept only a leaf public-key filename.
        if (keyFile.IndexOfAny(['/', '\\', ':']) >= 0 || keyFile is "." or ".." ||
            !keyFile.EndsWith("-public.pem", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("keyFile 必须是部署目录内的 *-public.pem 公钥文件名。");
        var keyPath = Path.Combine(root, keyFile);
        var keyInfo = new FileInfo(keyPath);
        if (!keyInfo.Exists || keyInfo.Length == 0 || keyInfo.Length > 64 * 1024 || keyInfo.LinkTarget is not null)
            throw new InvalidDataException("公钥不存在、为空、过大或是符号链接，请检查部署包。");
        var keyText = File.ReadAllText(keyPath);
        if (keyText.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("所选文件包含私钥。学生部署包只能使用公钥。");
        if (!keyText.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) &&
            !keyText.Contains("-----BEGIN RSA PUBLIC KEY-----", StringComparison.Ordinal))
            throw new InvalidDataException("公钥缺少可识别的 PEM 头，请检查文件。");
        DeploymentPlan.ValidateComputerName(prefix, "1");
        return new CampusPackage(campus, prefix, keyPath);
    }
}

public sealed record DeploymentPlan(string Campus, string ComputerName, bool RenameComputer, IReadOnlyList<string> Steps)
{
    public static string ValidateComputerName(string prefix, string number)
    {
        if (!Regex.IsMatch(number, "^[0-9]{1,3}$") || !int.TryParse(number, out var n) || n is < 1 or > 99)
            throw new InvalidDataException("电脑编号请输入 1–99，例如 03；当前与原学生脚本保持一致。");
        if (!Regex.IsMatch(prefix, "^[A-Za-z0-9][A-Za-z0-9-]*$"))
            throw new InvalidDataException("电脑名前缀必须以字母或数字开头，只能包含英文字母、数字和连字符。");
        var name = prefix + n.ToString("D2");
        if (name.Length > 15 || !name.Any(char.IsAsciiLetter))
            throw new InvalidDataException("最终电脑名须包含英文字母，且总长度不能超过 15 个字符。");
        return name;
    }

    public static DeploymentPlan Create(string campus, string prefix, string number, bool rename)
    {
        if (string.IsNullOrWhiteSpace(campus) || campus.Trim().Length > 100 || campus.Any(char.IsControl))
            throw new InvalidDataException("请输入 1–100 个字符的校区名称，不能包含控制字符。");
        var name = ValidateComputerName(prefix, number);
        var steps = new List<string>
        {
            "检查 Windows 环境、管理员权限和部署包完整性",
            "检查并安装匹配版本的 Veyon（尚未接入）",
            "配置密钥认证并导入校区公钥（尚未接入）"
        };
        if (rename) steps.Add($"将本机重命名为 {name}，重启后生效（尚未接入）");
        steps.Add("检查 Veyon 服务并记录部署结果（尚未接入）");
        return new DeploymentPlan(campus.Trim(), name, rename, steps);
    }
}
