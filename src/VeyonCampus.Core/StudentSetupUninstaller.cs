using System.Diagnostics;
using System.Runtime.Versioning;

namespace VeyonCampus.Core;

/// <summary>Stops the installed student application, then safely removes its managed Agent before Inno deletes files.</summary>
public static class StudentSetupUninstaller
{
    private const string Step = "student-setup-uninstall";
    private static readonly HashSet<string> StudentProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "VeyonCampus.StudentSetup.exe",
        "VeyonCampus.Worker.exe",
        "VeyonCampus.StudentCompanion.exe",
        "VeyonCampus.UpdateHelper.exe"
    };

    public static StepResult Run()
    {
        if (!OperatingSystem.IsWindows())
            return new(Step, ExecutionPlan.Failed, "学生端卸载仅支持 Windows。");
        if (!IsElevated())
            return new(Step, ExecutionPlan.Failed, "学生端完整卸载需要管理员权限。");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installRoot = Path.GetFullPath(AppContext.BaseDirectory);
        if (!IsExpectedInstallRoot(installRoot, programFiles))
            return new(Step, ExecutionPlan.Failed, "完整卸载只能从已安装的 Veyon Campus 学生目录启动。");

        try
        {
            StopStudentProcesses(installRoot, Path.Combine(programFiles, "Veyon Campus", "Updater", "Student"));
            var agentRemoval = WebsitePolicyAgentInstaller.Uninstall();
            if (agentRemoval.Status != ExecutionPlan.Succeeded)
                return new(Step, agentRemoval.Status,
                    "学生程序已停止，但后台 Agent 或其策略未能安全清理；保留安装文件以便检查并重试。" +
                    agentRemoval.Detail);
            var teacherInstalled = HasTeacherInstallation(programFiles);
            RemoveStudentUserData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                preserveTeacherData: teacherInstalled);
            return new(Step, ExecutionPlan.Succeeded,
                teacherInstalled
                    ? "学生程序进程、后台 Agent、可确认归属的策略、任务、网络规则、配置及学生部署缓存已清理；教师端共享资料已保留。可以删除学生端安装文件。"
                    : "学生程序进程、后台 Agent、可确认归属的策略、任务、网络规则、配置及当前用户的 Veyon Campus 数据已清理。可以删除学生端安装文件。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.ComponentModel.Win32Exception or InvalidOperationException or
                                          System.Security.SecurityException)
        {
            return new(Step, ExecutionPlan.NeedsReview,
                $"学生端停止进程或清理后台 Agent 时失败：{exception.Message}；安装文件已保留。" );
        }
    }

    internal static bool IsExpectedInstallRoot(string installRoot, string programFiles)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(programFiles)) return false;
        try
        {
            var expected = Path.GetFullPath(Path.Combine(programFiles, "Veyon Campus", "Student"));
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)), expected,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    internal static bool HasTeacherInstallation(string programFiles)
    {
        if (string.IsNullOrWhiteSpace(programFiles)) return false;
        try
        {
            return Directory.Exists(Path.Combine(Path.GetFullPath(programFiles), "Veyon Campus", "Teacher"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void RemoveStudentUserData(string localApplicationData, bool preserveTeacherData)
    {
        var localRoot = Path.GetFullPath(localApplicationData);
        var productRoot = Path.Combine(localRoot, "VeyonCampus");
        if (!Directory.Exists(productRoot)) return;
        if ((File.GetAttributes(productRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("本机 Veyon Campus 数据目录是重解析点；拒绝递归删除。");

        if (!preserveTeacherData)
        {
            VerifyTreeHasNoReparsePoints(productRoot);
            Directory.Delete(productRoot, recursive: true);
            return;
        }

        foreach (var name in new[] { "deployment-packages", "snapshots", "runs" })
        {
            var directory = Path.Combine(productRoot, name);
            if (!Directory.Exists(directory)) continue;
            VerifyTreeHasNoReparsePoints(directory);
            Directory.Delete(directory, recursive: true);
        }

        if (!Directory.EnumerateFileSystemEntries(productRoot).Any()) Directory.Delete(productRoot);
    }

    private static void VerifyTreeHasNoReparsePoints(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("学生部署数据目录是重解析点；拒绝递归删除。");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("学生部署数据目录含重解析点；拒绝递归删除。");
            if ((attributes & FileAttributes.Directory) != 0) VerifyTreeHasNoReparsePoints(entry);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    private static void StopStudentProcesses(string installRoot, string updaterRoot)
    {
        var currentProcessId = Environment.ProcessId;
        foreach (var processName in StudentProcessNames)
        {
            var stem = Path.GetFileNameWithoutExtension(processName);
            foreach (var process in Process.GetProcessesByName(stem))
            {
                using (process)
                {
                    if (process.Id == currentProcessId) continue;

                    string executablePath;
                    try
                    {
                        executablePath = process.MainModule?.FileName
                                         ?? throw new IOException("无法读取学生程序进程路径。");
                    }
                    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                                      InvalidOperationException or System.Security.SecurityException)
                    {
                        throw new IOException($"无法核验 {processName} 的安装路径；为避免误停其他软件，卸载已停止。", exception);
                    }

                    if (!StudentProcessNames.Contains(Path.GetFileName(executablePath)) ||
                        (!IsPathWithin(executablePath, installRoot) && !IsPathWithin(executablePath, updaterRoot)))
                        continue;

                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        if (!process.WaitForExit(10_000))
                            throw new IOException($"{processName} 在 10 秒内未停止。");
                    }
                    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        if (!process.HasExited)
                            throw new IOException($"无法停止学生程序 {processName}。", exception);
                    }
                }
            }
        }
    }

    private static bool IsPathWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
