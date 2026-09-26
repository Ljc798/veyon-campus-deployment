using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public static class MachineNaming
{
    public const int MaximumNumber = 150;

    public static string CreateName(string prefix, string number)
    {
        if (!Regex.IsMatch(number, "^[0-9]{1,3}$", RegexOptions.CultureInvariant) ||
            !int.TryParse(number, out var parsed) || parsed is < 1 or > MaximumNumber)
            throw new InvalidDataException("电脑编号请输入 1–150，例如 03 或 150。");
        if (string.IsNullOrEmpty(prefix) ||
            !Regex.IsMatch(prefix, "^[A-Za-z0-9][A-Za-z0-9-]*$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("电脑名前缀必须以字母或数字开头，只能包含英文字母、数字和连字符。");
        var name = prefix + parsed.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        if (name.Length > 15 || !name.Any(char.IsAsciiLetter) || name.EndsWith("-", StringComparison.Ordinal))
            throw new InvalidDataException("最终电脑名须包含英文字母，且总长度不能超过 15 个字符。");
        return name;
    }

    public static IReadOnlyList<string> CreateRange(string prefix, string startNumber, string count)
    {
        if (!int.TryParse(startNumber, out var start) || !Regex.IsMatch(startNumber, "^[0-9]{1,3}$") ||
            !int.TryParse(count, out var size) || !Regex.IsMatch(count, "^[0-9]{1,3}$") ||
            start is < 1 or > MaximumNumber || size is < 1 or > MaximumNumber ||
            start + size - 1 > MaximumNumber)
            throw new InvalidDataException("机房编号范围必须在 1–150 内，且最多包含 150 台。");
        return Enumerable.Range(start, size).Select(n => CreateName(prefix, n.ToString())).ToArray();
    }
}

public sealed record OperationSelection(bool InstallVeyon, bool RenameComputer, bool CreateStudent,
    bool ChangeAdminPassword)
{
    public bool Any => InstallVeyon || RenameComputer || CreateStudent || ChangeAdminPassword;
}

public sealed record PlanInput(string Campus, string Prefix, string Number, string StudentAccountName,
    string AdminAccountName, OperationSelection Operations, PackageContext? Package);

public sealed record PlanStep(string Id, string Description);

public sealed record DeploymentPlan(string? Campus, string? ComputerName, OperationSelection Operations,
    IReadOnlyList<PlanStep> Steps)
{
    public static DeploymentPlan Create(PlanInput input)
    {
        if (!input.Operations.Any)
            throw new InvalidDataException("请至少选择一项操作。");
        if (input.Campus.Length > 100 || input.Campus.Any(char.IsControl))
            throw new InvalidDataException("校区名称最多 100 个字符，不能包含控制字符。");
        string? name = input.Operations.RenameComputer
            ? MachineNaming.CreateName(input.Prefix, input.Number) : null;
        var steps = new List<PlanStep> { new("preflight", "读取本机环境和已选操作的前置条件") };
        // 顺序约定（与 架构文档 §3 组合任务推荐顺序 一致）：
        // 1. 预检必须整段前置——账户冲突、SID 核对、服务状态、磁盘、重启待办
        //    全部在第一次真正修改之前完成；任何一项不过就整体停止。
        // 2. 账户操作先于 Veyon 安装：Veyon 服务通常以本地系统身份运行，
        //    一旦装好并注册服务，再动账户（尤其改管理员密码）会影响
        //    服务依赖的凭据链。与 4.11.2 旧脚本"先账户后 Veyon"的经验一致。
        // 3. 改密尽量后置：旧密码不可读回，且改密是"失败后最不能自动回滚"的步骤。
        // 4. Veyon 安装+配置放账户之后：装完即进入可用状态（服务+公钥+认证），
        //    不用回头再动账户。
        // 5. 改名放最后：唯一"重启后才生效"的操作，且改名后主机名解析会短暂不一致；
        //    放最后可让前面所有步骤在稳定的旧主机名上完成，失败时系统状态最接近原样。
        if (input.Operations.CreateStudent)
        {
            var account = ValidateAccountName(input.StudentAccountName, "学生账户");
            steps.Add(new("student-account", $"创建普通本地学生账户 {account}；初始密码可留空或设置，已有普通账户时保留原密码"));
        }
        if (input.Operations.ChangeAdminPassword)
        {
            var account = ValidateAccountName(input.AdminAccountName, "管理员账户");
            steps.Add(new("admin-password", $"再次核对本地管理员账户 {account} 的 SID 后设置新密码；旧密码不可读回或自动恢复"));
        }
        if (input.Operations.InstallVeyon)
        {
            var package = input.Package ?? throw new InvalidDataException("配置 Veyon 前请先选择包含公钥的校区配置包。");
            package.VerifyUnchanged();
            if (!string.Equals(input.Campus, package.Campus, StringComparison.Ordinal))
                throw new InvalidDataException("校区名称与已选部署包不一致，请重新选择部署包。");
            steps.Add(new("veyon-install", "检查并离线安装匹配版本的 Veyon 学生组件"));
            steps.Add(new("veyon-key", "设置密钥认证并导入已校验的校区公钥"));
            if (package.WebsitePolicyPublicKeyPath is not null)
                steps.Add(new("website-agent", "安装仅持有校区公钥的学生网站策略 SYSTEM 代理"));
        }
        if (name is not null)
            steps.Add(new("rename", $"将本机重命名为 {name}；重启后生效"));
        steps.Add(new("verify", "分别验证已选操作并记录结果"));
        return new DeploymentPlan(string.IsNullOrWhiteSpace(input.Campus) ? null : input.Campus.Trim(), name,
            input.Operations, steps);
    }

    /// <summary>Returns the validated target name for a rename selection.</summary>
    public static string ComputerNameFor(PlanInput input)
    {
        if (!input.Operations.RenameComputer)
            throw new InvalidDataException("当前计划未选择改名；没有可返回的目标名称。");
        return MachineNaming.CreateName(input.Prefix, input.Number);
    }

    private static string ValidateAccountName(string value, string label)
    {
        if (!WindowsAccountAdapter.IsValidAccountName(value, label))
            throw new InvalidDataException($"{label}名无效；请输入 1–20 个有效字符，并在执行前核对本地 SID。");
        return value;
    }
}
