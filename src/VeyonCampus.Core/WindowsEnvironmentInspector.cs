using System.Text.Json;

namespace VeyonCampus.Core;

public enum RestoreEnvironmentEvidence { Possible, NoEvidence, Unknown }

public sealed record DomainMembershipFacts(bool? IsDomainJoined, string Detail);
public sealed record RestoreEnvironmentFacts(RestoreEnvironmentEvidence Evidence, string Detail);

/// <summary>Read-only checks for domain membership and Microsoft Unified Write Filter evidence.</summary>
public sealed class WindowsEnvironmentInspector
{
    private readonly IProcessLauncher _launcher;

    public WindowsEnvironmentInspector(IProcessLauncher? launcher = null) =>
        _launcher = launcher ?? new DefaultProcessLauncher();

    public DomainMembershipFacts ReadDomainMembership()
    {
        if (!OperatingSystem.IsWindows())
            return new(null, "非 Windows 平台；域或工作组状态检查不适用。");

        var script = "Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop | " +
                     "Select-Object PartOfDomain, Workgroup | ConvertTo-Json -Compress";
        var outcome = WindowsPowerShell.Run(_launcher, script, TimeSpan.FromSeconds(15));
        if (!outcome.Ok)
            return new(null, "无法读取域或工作组状态；按未知处理，未根据账户名称推断。" + Truncate(outcome.Stdout + outcome.Stderr));

        try
        {
            using var document = JsonDocument.Parse(outcome.Stdout);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("PartOfDomain", out var value))
            {
                if (value.ValueKind == JsonValueKind.True)
                    return new(true, "已确认本机加入域；域名称不展示，也不会由本工具更改域关系。");
                if (value.ValueKind == JsonValueKind.False)
                    return new(false, "已确认本机处于工作组环境；工作组名称不展示。");
            }
        }
        catch (JsonException) { }

        return new(null, "域或工作组状态无法解析；按未知处理。" + Truncate(outcome.Stdout));
    }

    /// <summary>
    /// Checks only for evidence of Microsoft's UWF feature. This cannot prove
    /// whether a volume is currently protected or whether a third-party or
    /// firmware restore mechanism exists.
    /// </summary>
    public RestoreEnvironmentFacts ReadRestoreEnvironmentEvidence()
    {
        if (!OperatingSystem.IsWindows())
            return new(RestoreEnvironmentEvidence.Unknown, "非 Windows 平台；还原环境检查不适用。");

        const string script = """
            $uwfDrivers = @(Get-CimInstance -ClassName Win32_SystemDriver -Filter "Name LIKE 'UWF%'" -ErrorAction Stop)
            $uwfTool = Get-Command -Name 'uwfmgr.exe' -ErrorAction SilentlyContinue
            [pscustomobject]@{
                HasUwfDriver = ($uwfDrivers.Count -gt 0)
                HasUwfTool = ($null -ne $uwfTool)
            } | ConvertTo-Json -Compress
            """;
        var outcome = WindowsPowerShell.Run(_launcher, script, TimeSpan.FromSeconds(20));
        if (!outcome.Ok)
            return new(RestoreEnvironmentEvidence.Unknown,
                "无法读取 Windows UWF 组件状态；还原环境未知，需现场确认。" + Truncate(outcome.Stdout + outcome.Stderr));

        try
        {
            using var document = JsonDocument.Parse(outcome.Stdout);
            var root = document.RootElement;
            var hasDriver = ReadBoolean(root, "HasUwfDriver");
            var hasTool = ReadBoolean(root, "HasUwfTool");
            if (hasDriver is null || hasTool is null)
                return new(RestoreEnvironmentEvidence.Unknown, "Windows UWF 查询结果不完整；还原环境未知，需现场确认。");

            if (hasDriver.Value || hasTool.Value)
                return new(RestoreEnvironmentEvidence.Possible,
                    "检测到 Microsoft UWF 组件或管理工具，表示可能存在写入保护，不代表筛选器已启用或系统盘受保护；请现场确认。第三方软件和硬件还原卡不在检测范围内。");

            return new(RestoreEnvironmentEvidence.NoEvidence,
                "未发现 Microsoft UWF 的明确迹象；这不能排除第三方还原软件或硬件还原卡，重启后是否保留变更仍需现场确认。");
        }
        catch (JsonException)
        {
            return new(RestoreEnvironmentEvidence.Unknown, "Windows UWF 查询结果无法解析；还原环境未知，需现场确认。");
        }
    }

    private static bool? ReadBoolean(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    private static string Truncate(string value)
    {
        value = value.Trim();
        return value.Length > 180 ? $" {value[..180]}…" : value.Length == 0 ? "" : $" {value}";
    }
}
