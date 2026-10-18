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
                "VeyonCampus.EdgeNativeHost.exe", "VeyonCampus.UpdateHelper.exe"
            ],
            ["TeacherConsole"] =
            ["VeyonCampus.Teacher.exe", "VeyonCampus.Worker.exe", "VeyonCampus.UpdateHelper.exe"]
        };

    public static void StopInstalledRoleProcesses(string role)
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
            foreach (var name in names)
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

        foreach (var name in names)
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
