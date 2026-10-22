using System.Diagnostics;
using System.ComponentModel;

namespace VeyonCampus.Core;

/// <summary>Stops only Veyon Campus processes launched from this role's installed or update directories.</summary>
public static class VeyonCampusProcessCleanup
{
    private static readonly IReadOnlyDictionary<string, string[]> ProcessNames =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["StudentSetup"] =
            [
                "VeyonCampus.StudentSetup.exe", "VeyonCampus.Worker.exe", "VeyonCampus.StudentCompanion.exe",
                "VeyonCampus.EdgePolicyExecutor.exe", "VeyonCampus.StudentInputGuard.exe",
                "VeyonCampus.EdgeNativeHost.exe", "VeyonCampus.UpdateHelper.exe"
            ],
            ["TeacherConsole"] =
            ["VeyonCampus.Teacher.exe", "VeyonCampus.Worker.exe", "VeyonCampus.UpdateHelper.exe"]
        };

    public static void StopInstalledRoleProcesses(string role, bool preserveUpdateHelper = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("停止 Veyon Campus 进程仅支持 Windows。");
        if (!ProcessNames.TryGetValue(role, out var names))
            throw new ArgumentException("Veyon Campus 程序角色无效。", nameof(role));

        var programFilesRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!preserveUpdateHelper)
            ThrowIfUpdateInstallerIsRunning(role, programFilesRoots);

        var namesToStop = preserveUpdateHelper
            ? names.Where(name => !string.Equals(name, "VeyonCampus.UpdateHelper.exe", StringComparison.OrdinalIgnoreCase))
            : names;
        var roleDirectory = role == "StudentSetup" ? "Student" : "Teacher";
        var processRoots = programFilesRoots.SelectMany(programFiles =>
        {
            var campusRoot = Path.Combine(programFiles, "Veyon Campus");
            return new[]
            {
                Path.Combine(campusRoot, roleDirectory),
                Path.Combine(campusRoot, "Updater", roleDirectory),
                Path.Combine(campusRoot, "UpdateRecovery", roleDirectory),
                Path.Combine(campusRoot, "UpdateStaging", roleDirectory)
            };
        }).ToArray();

        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var name in namesToStop)
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    var imagePath = ReadProcessPath(process, name);
                    if (!IsWithinAnyRoot(imagePath, processRoots)) continue;
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        if (!process.WaitForExit(10_000))
                            throw new IOException($"{name} 在 10 秒内未停止。");
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                    {
                        if (!process.HasExited)
                            throw new IOException($"无法停止 Veyon Campus 进程 {name}。", exception);
                    }
                }
            }

            if (pass < 2) Thread.Sleep(150);
        }

        foreach (var name in namesToStop)
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                var imagePath = ReadProcessPath(process, name);
                if (IsWithinAnyRoot(imagePath, processRoots))
                    throw new IOException($"仍有 Veyon Campus 进程 {name} 在运行；未继续安装或清理。");
            }
        }
    }

    private static void ThrowIfUpdateInstallerIsRunning(string role,
        IReadOnlyCollection<string> programFilesRoots)
    {
        var stagingRoots = programFilesRoots
            .Select(programFiles => Path.Combine(programFiles, "Veyon Campus", "UpdateStaging"))
            .ToArray();
        var installerPrefix = role == "StudentSetup"
            ? "VeyonCampus-Student-Setup-"
            : "VeyonCampus-Teacher-Setup-";
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                string processName;
                try { processName = process.ProcessName; }
                catch (InvalidOperationException) { continue; }
                catch (Win32Exception) { continue; }
                if (!processName.StartsWith(installerPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !processName.EndsWith("-win-x64", StringComparison.OrdinalIgnoreCase))
                    continue;

                var imagePath = ReadProcessPath(process, processName + ".exe");
                if (IsWithinAnyRoot(imagePath, stagingRoots))
                    throw new VeyonCampusUpdateInProgressException(role);
            }
        }
    }

    public sealed class VeyonCampusUpdateInProgressException : IOException
    {
        public VeyonCampusUpdateInProgressException(string role)
            : base(role == "StudentSetup"
                ? "检测到学生端更新安装器正在运行。请等待 VeyonCampus-Student-Setup 更新进度完成后再重试；若进度窗口不可见，请重启 Windows 后重试，避免中断安装。"
                : "检测到教师端更新安装器正在运行。请等待 VeyonCampus-Teacher-Setup 更新进度完成后再重试；若进度窗口不可见，请重启 Windows 后重试，避免中断安装。")
        {
        }
    }

    private static string ReadProcessPath(Process process, string name)
    {
        try
        {
            return Path.GetFullPath(process.MainModule?.FileName
                                     ?? throw new IOException("无法读取进程映像路径。"));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                          System.Security.SecurityException or IOException)
        {
            throw new IOException($"无法核验 {name} 的进程路径；为避免误停其他程序，已停止清理。", exception);
        }
    }

    private static bool IsWithinAnyRoot(string path, IEnumerable<string> roots) =>
        roots.Any(root => IsWithin(path, root));

    private static bool IsWithin(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
