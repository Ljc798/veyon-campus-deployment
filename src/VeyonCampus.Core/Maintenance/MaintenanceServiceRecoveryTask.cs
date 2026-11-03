using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace VeyonCampus.Core.Maintenance;

/// <summary>Registers the independent boot recovery helper used when the service image cannot start.</summary>
public static class MaintenanceServiceRecoveryTask
{
    public const string TaskName = @"\VeyonCampusMaintenanceServiceRecovery";
    private const string TaskDirectoryName = "MaintenanceServiceRecovery";

    [SupportedOSPlatform("windows")]
    public static void Register(string serviceExecutablePath)
    {
        EnsureAdministrator();
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceExecutablePath);
        var fullServicePath = Path.GetFullPath(serviceExecutablePath);
        var serviceDirectory = Path.GetDirectoryName(fullServicePath)
                               ?? throw new InvalidDataException("Maintenance 服务目录无效。");
        var helperPath = Path.Combine(serviceDirectory, "VeyonCampus.UpdateHelper.exe");
        var maintenanceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Veyon Campus", "MaintenanceService");
        var parentDirectory = Path.GetDirectoryName(serviceDirectory);
        var isVersionedDirectory = string.Equals(parentDirectory, Path.Combine(maintenanceRoot, "Versions"),
            StringComparison.OrdinalIgnoreCase);
        var isCurrentDirectory = string.Equals(serviceDirectory, Path.Combine(maintenanceRoot, "Current"),
            StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(fullServicePath, Path.Combine(serviceDirectory,
                "VeyonCampus.MaintenanceService.exe"), StringComparison.OrdinalIgnoreCase) ||
            (!isCurrentDirectory && !isVersionedDirectory))
            throw new InvalidDataException("Recovery task 只能使用受管 Maintenance 版本目录中的同目录 UpdateHelper。");
        if (isVersionedDirectory)
            _ = ApplicationReleaseClient.CompareVersions(Path.GetFileName(serviceDirectory),
                Path.GetFileName(serviceDirectory));
        PathLinkSecurity.RejectLinks(fullServicePath);
        PathLinkSecurity.RejectLinks(helperPath);
        if (!File.Exists(helperPath))
            throw new FileNotFoundException("Maintenance 独立恢复 UpdateHelper 不存在。", helperPath);

        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", TaskDirectoryName);
        Directory.CreateDirectory(dataDirectory);
        PathLinkSecurity.RejectLinks(dataDirectory);
        AgentFileSecurity.SecurePrivateDirectory(dataDirectory);
        var xmlPath = Path.Combine(dataDirectory, "MaintenanceServiceRecovery.xml");
        var xml = CreateTaskXml(helperPath, serviceDirectory);
        WritePrivateFile(xmlPath, new UTF8Encoding(false).GetBytes(xml));
        try
        {
            var existingTaskXml = TryReadTaskDefinition();
            if (existingTaskXml is not null)
                ValidateManagedTask(existingTaskXml, expectedHelperPath: null);
            RunTaskScheduler("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");
            var registeredTaskXml = TryReadTaskDefinition()
                                    ?? throw new InvalidDataException("Maintenance 恢复计划任务注册后无法读回。");
            ValidateManagedTask(registeredTaskXml, helperPath);
        }
        finally { TryDelete(xmlPath); }
    }

    [SupportedOSPlatform("windows")]
    public static void Remove()
    {
        EnsureAdministrator();
        var taskXml = TryReadTaskDefinition();
        if (taskXml is null) return;
        ValidateManagedTask(taskXml, expectedHelperPath: null);
        _ = RunTaskScheduler("/Delete", "/TN", TaskName, "/F");
    }

    private static string? TryReadTaskDefinition()
    {
        var listing = RunTaskScheduler("/Query", ["/FO", "CSV", "/NH"]);
        if (!ContainsTaskName(listing.Output, TaskName)) return null;
        var query = RunTaskScheduler("/Query", ["/TN", TaskName, "/XML"], allowFailure: true);
        if (query.ExitCode != 0)
            throw new InvalidOperationException("Maintenance 恢复计划任务存在，但无法读取 XML 定义；拒绝覆盖或删除。");
        var start = query.Output.IndexOf("<Task", StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("Maintenance 恢复计划任务查询未返回 XML 定义。");
        return query.Output[start..].Trim();
    }

    private static bool ContainsTaskName(string csv, string expectedTaskName)
    {
        using var reader = new StringReader(csv);
        while (reader.ReadLine() is { } line)
        {
            line = line.TrimStart('\uFEFF').TrimStart();
            if (line.Length == 0) continue;
            var firstField = new StringBuilder();
            var index = 0;
            if (line[0] == '"')
            {
                index = 1;
                while (index < line.Length)
                {
                    if (line[index] != '"')
                    {
                        firstField.Append(line[index++]);
                        continue;
                    }
                    if (index + 1 < line.Length && line[index + 1] == '"')
                    {
                        firstField.Append('"');
                        index += 2;
                        continue;
                    }
                    break;
                }
            }
            else
            {
                var comma = line.IndexOf(',');
                firstField.Append(comma < 0 ? line : line[..comma]);
            }
            if (string.Equals(firstField.ToString(), expectedTaskName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void ValidateManagedTask(string taskXml, string? expectedHelperPath)
    {
        XDocument document;
        try { document = XDocument.Parse(taskXml, LoadOptions.None); }
        catch (System.Xml.XmlException exception)
        { throw new InvalidDataException("同名 Maintenance 恢复计划任务 XML 无效；拒绝覆盖或删除。", exception); }

        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = document.Root;
        var principals = task?.Element(ns + "Principals")?.Elements(ns + "Principal").ToArray();
        var principalId = principals?.SingleOrDefault()?.Element(ns + "UserId")?.Value;
        var actions = task?.Element(ns + "Actions")?.Elements().ToArray();
        var action = actions?.SingleOrDefault();
        var command = action?.Element(ns + "Command")?.Value;
        var arguments = action?.Element(ns + "Arguments")?.Value;
        var workingDirectory = action?.Element(ns + "WorkingDirectory")?.Value;
        var maintenanceRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Veyon Campus", "MaintenanceService"));
        if (task?.Name != ns + "Task" || principals?.Length != 1 ||
            !string.Equals(principalId, "S-1-5-18", StringComparison.OrdinalIgnoreCase) ||
            actions?.Length != 1 || action?.Name != ns + "Exec" ||
            !string.Equals(arguments, "--recover-maintenance-service", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(workingDirectory))
            throw new InvalidDataException("同名恢复计划任务不属于 Maintenance SYSTEM 恢复任务；拒绝覆盖或删除。");

        var fullCommand = Path.GetFullPath(command);
        var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        var commandDirectory = Path.GetDirectoryName(fullCommand);
        var parentDirectory = commandDirectory is null ? null : Path.GetDirectoryName(commandDirectory);
        var validDirectory = commandDirectory is not null &&
            (string.Equals(commandDirectory, Path.Combine(maintenanceRoot, "Current"),
                 StringComparison.OrdinalIgnoreCase) ||
             parentDirectory is not null &&
             string.Equals(parentDirectory, Path.Combine(maintenanceRoot, "Versions"),
                 StringComparison.OrdinalIgnoreCase));
        if (!validDirectory || !string.Equals(Path.GetFileName(fullCommand), "VeyonCampus.UpdateHelper.exe",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fullWorkingDirectory, commandDirectory, StringComparison.OrdinalIgnoreCase) ||
            expectedHelperPath is not null && !string.Equals(fullCommand, Path.GetFullPath(expectedHelperPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("同名恢复计划任务的 Helper 路径不在受管目录或与注册目标不符；拒绝覆盖或删除。");
        if (parentDirectory is not null &&
            string.Equals(parentDirectory, Path.Combine(maintenanceRoot, "Versions"),
                StringComparison.OrdinalIgnoreCase))
            _ = ApplicationReleaseClient.CompareVersions(Path.GetFileName(commandDirectory),
                Path.GetFileName(commandDirectory));
    }

    private static string CreateTaskXml(string helperPath, string workingDirectory)
    {
        static string Escape(string value) => SecurityElement.Escape(value)
            ?? throw new InvalidDataException("恢复任务 XML 字段无效。");
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
               "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
               "<RegistrationInfo><Author>Veyon Campus</Author><Description>Recover an interrupted, signed MaintenanceService version switch.</Description></RegistrationInfo>" +
               "<Triggers><BootTrigger><Enabled>true</Enabled><Delay>PT30S</Delay></BootTrigger></Triggers>" +
               "<Principals><Principal id=\"System\"><UserId>S-1-5-18</UserId><LogonType>ServiceAccount</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
               "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
               "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable>" +
               "<ExecutionTimeLimit>PT5M</ExecutionTimeLimit><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings>" +
               "<Actions Context=\"System\"><Exec><Command>" + Escape(helperPath) + "</Command>" +
               "<Arguments>--recover-maintenance-service</Arguments><WorkingDirectory>" + Escape(workingDirectory) +
               "</WorkingDirectory></Exec></Actions></Task>";
    }

    private static (int ExitCode, string Output) RunTaskScheduler(string operation, params string[] arguments) =>
        RunTaskScheduler(operation, arguments, allowFailure: false);

    private static (int ExitCode, string Output) RunTaskScheduler(string operation, string[] arguments,
        bool allowFailure)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(operation);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 Windows Task Scheduler 命令。");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw new TimeoutException("等待 Windows Task Scheduler 命令超时。");
        }
        var output = standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult();
        if (!allowFailure && process.ExitCode != 0)
            throw new InvalidOperationException("Maintenance 恢复任务注册失败，schtasks 退出码 " + process.ExitCode + ".");
        return (process.ExitCode, output);
    }

    private static void WritePrivateFile(string path, byte[] bytes)
    {
        PathLinkSecurity.RejectLinksAllowingMissingComponents(path);
        using (new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) { }
        AgentFileSecurity.SecurePrivateFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { PathLinkSecurity.RejectLinks(path); File.Delete(path); } }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) { }
    }

    private static void EnsureAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18" &&
            !new System.Security.Principal.WindowsPrincipal(identity).IsInRole(
                System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Maintenance 恢复任务注册需要 SYSTEM 或管理员权限。");
    }
}
