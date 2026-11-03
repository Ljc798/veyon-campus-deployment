using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;

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
            RunTaskScheduler("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");
        }
        finally { TryDelete(xmlPath); }
    }

    [SupportedOSPlatform("windows")]
    public static void Remove()
    {
        EnsureAdministrator();
        var query = RunTaskScheduler("/Query", ["/TN", TaskName], allowFailure: true);
        if (query.ExitCode == 0) _ = RunTaskScheduler("/Delete", "/TN", TaskName, "/F");
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
