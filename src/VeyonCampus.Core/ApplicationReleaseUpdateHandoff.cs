using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public static class ApplicationReleaseUpdateHandoff
{
    private const string HelperFileName = "VeyonCampus.UpdateHelper.exe";

    public static void Start(string installerPath, ApplicationReleaseRole role, string currentVersion)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("应用更新安装仅支持 Windows。");

        _ = ApplicationReleaseClient.CompareVersions(currentVersion, currentVersion);
        var fullInstallerPath = Path.GetFullPath(installerPath);
        var updatesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyonCampus", "Updates");
        EnsureSamePath(Path.GetDirectoryName(fullInstallerPath) ?? "", updatesDirectory,
            "暂存安装器必须位于当前用户的 VeyonCampus 更新目录。");
        EnsureDirectoryIsNotReparsePoint(updatesDirectory);
        var release = ApplicationReleaseClient.ReadVerifiedStagedRelease(fullInstallerPath, role,
            DeploymentPackageApiConfiguration.GetApiBaseAddress(), ApplicationReleaseTrust.LoadPinnedPublicKeyPem());
        if (ApplicationReleaseClient.CompareVersions(release.Manifest.Version, currentVersion) <= 0)
            throw new InvalidDataException("更新版本必须严格高于当前版本。");

        var (roleSlug, executableName) = GetRolePaths(role);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installDirectory = Path.Combine(programFiles, "Veyon Campus", roleSlug);
        var currentExecutable = Environment.ProcessPath
                                ?? throw new InvalidOperationException("无法确定当前应用程序路径。");
        EnsureSamePath(currentExecutable, Path.Combine(installDirectory, executableName),
            "应用必须从固定的 Program Files 安装目录启动后才能自我更新。");

        var helperPath = Path.Combine(programFiles, "Veyon Campus", "Updater", roleSlug, currentVersion,
            HelperFileName);
        if ((File.GetAttributes(helperPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新助手不能是符号链接或重解析点。");

        using var currentProcess = Process.GetCurrentProcess();
        var startTicks = currentProcess.StartTime.ToUniversalTime().Ticks;
        var startInfo = new ProcessStartInfo(helperPath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(helperPath)!
        };
        startInfo.ArgumentList.Add("--apply-release");
        startInfo.ArgumentList.Add(role == ApplicationReleaseRole.TeacherConsole ? "TeacherConsole" : "StudentSetup");
        startInfo.ArgumentList.Add(fullInstallerPath);
        startInfo.ArgumentList.Add(currentProcess.Id.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(startTicks.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(currentVersion);
        using var helper = Process.Start(startInfo)
                           ?? throw new InvalidOperationException("无法启动应用更新助手。");
    }

    public static int RunHelper(string[] args)
    {
        try
        {
            if (args is ["--apply-release", var roleText, var installerPath, var processIdText,
                    var processStartTicksText, var currentVersion])
            {
                if (!int.TryParse(processIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ||
                    processId <= 0 ||
                    !long.TryParse(processStartTicksText, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var processStartTicks) || processStartTicks <= 0)
                    throw new InvalidDataException("更新父进程标识无效。");
                ApplyReleaseAsCurrentUser(installerPath, ParseRole(roleText), processId, processStartTicks,
                    currentVersion);
                return 0;
            }
            if (args is ["--elevated-apply-release", var elevatedRole, var encodedInstallerPath,
                    var encodedCurrentVersion])
            {
                var decodedInstallerPath = Encoding.UTF8.GetString(Convert.FromBase64String(encodedInstallerPath));
                var decodedCurrentVersion = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCurrentVersion));
                ApplyReleaseAsAdministrator(decodedInstallerPath, ParseRole(elevatedRole), decodedCurrentVersion);
                return 0;
            }
            return 2;
        }
        catch (Exception exception)
        {
            WriteFailureLog(exception);
            return 1;
        }
    }

    private static void ApplyReleaseAsCurrentUser(string installerPath, ApplicationReleaseRole role,
        int parentProcessId, long parentStartTicks, string currentVersion)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("应用更新安装仅支持 Windows。");
        _ = ApplicationReleaseClient.CompareVersions(currentVersion, currentVersion);
        var fullInstallerPath = Path.GetFullPath(installerPath);
        var updatesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyonCampus", "Updates");
        EnsureSamePath(Path.GetDirectoryName(fullInstallerPath) ?? "", updatesDirectory,
            "暂存安装器路径无效。");
        EnsureUserStagingPath(fullInstallerPath);
        var release = ReadVerifiedRelease(fullInstallerPath, role);
        if (ApplicationReleaseClient.CompareVersions(release.Manifest.Version, currentVersion) <= 0)
            throw new InvalidDataException("更新版本必须严格高于当前版本。");

        var (roleSlug, executableName) = GetRolePaths(role);
        var installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Veyon Campus", roleSlug);
        EnsureDirectoryIsNotReparsePoint(installDirectory);
        var executablePath = Path.Combine(installDirectory, executableName);
        var currentRoleInfo = ReadRoleInfo(installDirectory, role);
        if (!string.Equals(currentRoleInfo.Version, currentVersion, StringComparison.Ordinal))
            throw new InvalidDataException("当前安装版本与正在运行的版本不一致；未执行更新。");

        WaitForParentProcess(parentProcessId, parentStartTicks, executablePath);
        _ = ReadVerifiedRelease(fullInstallerPath, role);
        var helperPath = Environment.ProcessPath
                         ?? throw new InvalidOperationException("无法确定更新助手路径。");
        var elevatedStartInfo = new ProcessStartInfo(helperPath)
        {
            Arguments = string.Join(" ", "--elevated-apply-release",
                role == ApplicationReleaseRole.TeacherConsole ? "TeacherConsole" : "StudentSetup",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(fullInstallerPath)),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(currentVersion))),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(helperPath)!
        };
        try
        {
            using (var elevatedHelper = Process.Start(elevatedStartInfo)
                                        ?? throw new InvalidOperationException("无法启动管理员更新助手。"))
            {
                elevatedHelper.WaitForExit();
                if (elevatedHelper.ExitCode != 0)
                    throw new InvalidOperationException($"管理员更新助手返回失败代码 {elevatedHelper.ExitCode}；当前安装未报告成功。");
            }

            var installedRoleInfo = ReadRoleInfo(installDirectory, role);
            if (!string.Equals(installedRoleInfo.Version, release.Manifest.Version, StringComparison.Ordinal) ||
                !File.Exists(executablePath) ||
                (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("安装器退出后版本回读不匹配；未重启应用。");

            _ = Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = installDirectory
            }) ?? throw new InvalidOperationException("新版本安装成功，但无法重启应用。");
        }
        catch
        {
            TryRestartExistingVersion(installDirectory, executablePath, role, currentVersion);
            throw;
        }
    }

    private static void ApplyReleaseAsAdministrator(string installerPath, ApplicationReleaseRole role,
        string currentVersion)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("应用更新安装仅支持 Windows。");
        _ = ApplicationReleaseClient.CompareVersions(currentVersion, currentVersion);
        var fullInstallerPath = Path.GetFullPath(installerPath);
        EnsureUserStagingPath(fullInstallerPath);
        var release = ReadVerifiedRelease(fullInstallerPath, role);
        if (ApplicationReleaseClient.CompareVersions(release.Manifest.Version, currentVersion) <= 0)
            throw new InvalidDataException("更新版本必须严格高于当前版本。");

        var (roleSlug, executableName) = GetRolePaths(role);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installDirectory = Path.Combine(programFiles, "Veyon Campus", roleSlug);
        PathLinkSecurity.RejectLinks(installDirectory);
        var executablePath = Path.Combine(installDirectory, executableName);
        var currentRoleInfo = ReadRoleInfo(installDirectory, role);
        if (!string.Equals(currentRoleInfo.Version, currentVersion, StringComparison.Ordinal))
            throw new InvalidDataException("当前安装版本与正在运行的版本不一致；未执行更新。");
        if (!File.Exists(executablePath) || (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("当前安装程序缺失或不是普通文件；未执行更新。");
        VerifyNoReparsePointsInTree(installDirectory);

        var stagingRoot = Path.Combine(programFiles, "Veyon Campus", "UpdateStaging");
        Directory.CreateDirectory(stagingRoot);
        PathLinkSecurity.RejectLinks(stagingRoot);
        var protectedStagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(protectedStagingDirectory);
        try
        {
            var protectedInstallerPath = Path.Combine(protectedStagingDirectory, release.Manifest.FileName);
            File.Copy(fullInstallerPath, protectedInstallerPath, overwrite: false);
            File.Copy(fullInstallerPath + ".release.json", protectedInstallerPath + ".release.json",
                overwrite: false);
            release = ApplicationReleaseClient.ReadVerifiedStagedRelease(protectedInstallerPath, role,
                DeploymentPackageApiConfiguration.GetApiBaseAddress(), ApplicationReleaseTrust.LoadPinnedPublicKeyPem());
            CleanupOldHelperVersions(role, currentVersion, release.Manifest.Version);

            var recoveryRoot = Path.Combine(programFiles, "Veyon Campus", "UpdateRecovery", roleSlug);
            Directory.CreateDirectory(recoveryRoot);
            PathLinkSecurity.RejectLinks(recoveryRoot);
            var recoveryDirectory = Path.Combine(recoveryRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recoveryDirectory);
            PathLinkSecurity.RejectLinks(recoveryDirectory);
            var previousInstallationDirectory = Path.Combine(recoveryDirectory, "Previous");
            Directory.Move(installDirectory, previousInstallationDirectory);

            try
            {
                var installerStartInfo = new ProcessStartInfo(protectedInstallerPath)
                {
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = protectedStagingDirectory
                };
                using (var installer = Process.Start(installerStartInfo)
                                       ?? throw new InvalidOperationException("无法启动签名安装器。"))
                {
                    installer.WaitForExit();
                    if (installer.ExitCode != 0)
                        throw new InvalidOperationException(
                            $"Inno Setup 返回失败代码 {installer.ExitCode}；当前安装未报告成功。");
                }

                VerifyNoReparsePointsInTree(installDirectory);
                var installedRoleInfo = ReadRoleInfo(installDirectory, role);
                if (!string.Equals(installedRoleInfo.Version, release.Manifest.Version, StringComparison.Ordinal) ||
                    !File.Exists(executablePath) ||
                    (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("安装器退出后版本回读不匹配；未重启应用。");
            }
            catch (Exception updateFailure)
            {
                RestorePreviousInstallation(installDirectory, previousInstallationDirectory, recoveryDirectory,
                    executableName, role, currentVersion, updateFailure);
            }

            TryRemovePreviousInstallation(previousInstallationDirectory, recoveryDirectory, role, currentVersion);
        }
        finally
        {
            try { Directory.Delete(protectedStagingDirectory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void RestorePreviousInstallation(string installDirectory, string previousInstallationDirectory,
        string recoveryDirectory, string executableName, ApplicationReleaseRole role, string currentVersion,
        Exception updateFailure)
    {
        string? failedInstallationDirectory = null;
        try
        {
            if (Directory.Exists(installDirectory))
            {
                VerifyNoReparsePointsInTree(installDirectory);
                failedInstallationDirectory = Path.Combine(recoveryDirectory, "Failed-" + Guid.NewGuid().ToString("N"));
                Directory.Move(installDirectory, failedInstallationDirectory);
            }
            else if (File.Exists(installDirectory))
            {
                throw new IOException("更新后的安装路径被非目录占用；旧版仍保留在恢复目录。");
            }

            Directory.Move(previousInstallationDirectory, installDirectory);
            VerifyNoReparsePointsInTree(installDirectory);
            var restoredRoleInfo = ReadRoleInfo(installDirectory, role);
            var executablePath = Path.Combine(installDirectory, executableName);
            if (!string.Equals(restoredRoleInfo.Version, currentVersion, StringComparison.Ordinal) ||
                !File.Exists(executablePath) ||
                (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("旧版本安装目录恢复后读回不匹配。");
        }
        catch (Exception rollbackFailure)
        {
            throw new AggregateException(
                $"应用更新失败且旧版本恢复未完成；旧版本保留路径：{previousInstallationDirectory}。",
                updateFailure, rollbackFailure);
        }

        var failureLocation = failedInstallationDirectory is null
            ? ""
            : $"失败的新文件保留在 {failedInstallationDirectory}。";
        throw new InvalidOperationException($"应用更新失败；旧版本 {currentVersion} 已恢复。{failureLocation}",
            updateFailure);
    }

    private static void TryRemovePreviousInstallation(string previousInstallationDirectory,
        string recoveryDirectory, ApplicationReleaseRole role, string expectedVersion)
    {
        try
        {
            VerifyNoReparsePointsInTree(previousInstallationDirectory);
            var previousRoleInfo = ReadRoleInfo(previousInstallationDirectory, role);
            if (!string.Equals(previousRoleInfo.Version, expectedVersion, StringComparison.Ordinal))
                throw new InvalidDataException("旧版本恢复副本的角色或版本读回不匹配；未删除。");

            var uninstallerPath = Path.Combine(previousInstallationDirectory, "unins000.exe");
            if (File.Exists(uninstallerPath))
            {
                PathLinkSecurity.RejectLinks(uninstallerPath);
                File.Delete(uninstallerPath);
            }
            Directory.Delete(previousInstallationDirectory, recursive: true);
            Directory.Delete(recoveryDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or JsonException or System.Security.SecurityException)
        {
            WriteFailureLog(new IOException(
                $"新版本已通过版本读回；旧版暂存目录未能清理：{previousInstallationDirectory}。", exception));
        }
    }

    private static void VerifyNoReparsePointsInTree(string directory)
    {
        PathLinkSecurity.RejectLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"应用目录包含重解析点：{entry}。");
            if ((attributes & FileAttributes.Directory) != 0)
                VerifyNoReparsePointsInTree(entry);
        }
    }

    private static ApplicationReleaseEnvelope ReadVerifiedRelease(string installerPath,
        ApplicationReleaseRole role) => ApplicationReleaseClient.ReadVerifiedStagedRelease(installerPath, role,
        DeploymentPackageApiConfiguration.GetApiBaseAddress(), ApplicationReleaseTrust.LoadPinnedPublicKeyPem());

    private static void CleanupOldHelperVersions(ApplicationReleaseRole role, string currentVersion,
        string targetVersion)
    {
        var roleSlug = GetRolePaths(role).RoleSlug;
        var helperRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Veyon Campus", "Updater", roleSlug);
        if (!Directory.Exists(helperRoot) ||
            (File.GetAttributes(helperRoot) & FileAttributes.ReparsePoint) != 0)
            return;
        try
        {
            foreach (var helperDirectory in Directory.EnumerateDirectories(helperRoot))
            {
                var directoryVersion = Path.GetFileName(helperDirectory);
                if (directoryVersion is null || directoryVersion == currentVersion || directoryVersion == targetVersion ||
                    (File.GetAttributes(helperDirectory) & FileAttributes.ReparsePoint) != 0)
                    continue;
                try { Directory.Delete(helperDirectory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void EnsureUserStagingPath(string installerPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(installerPath)
                                          ?? throw new InvalidDataException("暂存安装器目录无效。"));
        var campusDirectory = directory.Parent
                              ?? throw new InvalidDataException("暂存用户配置目录无效。");
        var localDirectory = campusDirectory.Parent
                             ?? throw new InvalidDataException("暂存用户配置目录无效。");
        var appDataDirectory = localDirectory.Parent
                               ?? throw new InvalidDataException("暂存用户配置目录无效。");
        var profileDirectory = appDataDirectory.Parent
                               ?? throw new InvalidDataException("暂存用户配置目录无效。");
        if (directory.Name != "Updates" || campusDirectory.Name != "VeyonCampus" ||
            localDirectory.Name != "Local" || appDataDirectory.Name != "AppData")
            throw new InvalidDataException("暂存安装器必须位于用户 LocalAppData 更新目录。");
        EnsureDirectoryIsNotReparsePoint(directory.FullName);
        EnsureDirectoryIsNotReparsePoint(campusDirectory.FullName);
        EnsureDirectoryIsNotReparsePoint(localDirectory.FullName);
        EnsureDirectoryIsNotReparsePoint(appDataDirectory.FullName);
        EnsureDirectoryIsNotReparsePoint(profileDirectory.FullName);
    }

    private static void WaitForParentProcess(int processId, long startTicks, string expectedExecutablePath)
    {
        Process parent;
        try { parent = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.HasExited) return;
            var actualExecutablePath = parent.MainModule?.FileName
                                       ?? throw new InvalidDataException("更新父进程路径不可用。");
            EnsureSamePath(actualExecutablePath, expectedExecutablePath, "更新父进程不是当前已安装的应用。");
            if (parent.StartTime.ToUniversalTime().Ticks != startTicks)
                throw new InvalidDataException("更新父进程标识已过期。");
            if (!parent.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
                throw new TimeoutException("等待应用退出超时；未启动安装器。");
        }
    }

    private static RoleInformation ReadRoleInfo(string installDirectory, ApplicationReleaseRole role)
    {
        var markerPath = Path.Combine(installDirectory, "veyon-campus-role.json");
        if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("应用角色标记不能是符号链接或重解析点。");
        var markerInfo = new FileInfo(markerPath);
        if (markerInfo.Length is < 1 or > 4096)
            throw new InvalidDataException("应用角色标记大小无效。");
        var information = JsonSerializer.Deserialize<RoleInformation>(File.ReadAllBytes(markerPath),
                              new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                          ?? throw new InvalidDataException("应用角色标记为空。");
        var expectedRole = role == ApplicationReleaseRole.TeacherConsole ? "TeacherConsole" : "StudentSetup";
        var expectedProduct = role == ApplicationReleaseRole.TeacherConsole
            ? "VeyonCampus.TeacherConsole"
            : "VeyonCampus.StudentSetup";
        if (information.SchemaVersion != 1 || information.Role != expectedRole ||
            information.Product != expectedProduct)
            throw new InvalidDataException("应用角色标记与更新角色不符。");
        _ = ApplicationReleaseClient.CompareVersions(information.Version, information.Version);
        return information;
    }

    private static void TryRestartExistingVersion(string installDirectory, string executablePath,
        ApplicationReleaseRole role, string expectedVersion)
    {
        try
        {
            var installedRoleInfo = ReadRoleInfo(installDirectory, role);
            if (!string.Equals(installedRoleInfo.Version, expectedVersion, StringComparison.Ordinal) ||
                !File.Exists(executablePath) ||
                (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
                return;
            _ = Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = installDirectory
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.ComponentModel.Win32Exception or InvalidOperationException or
                                          JsonException)
        {
        }
    }

    private static (string RoleSlug, string ExecutableName) GetRolePaths(ApplicationReleaseRole role) => role switch
    {
        ApplicationReleaseRole.TeacherConsole => ("Teacher", "VeyonCampus.Teacher.exe"),
        ApplicationReleaseRole.StudentSetup => ("Student", "VeyonCampus.StudentSetup.exe"),
        _ => throw new InvalidDataException("更新角色无效。")
    };

    private static ApplicationReleaseRole ParseRole(string roleText) => roleText switch
    {
        "TeacherConsole" => ApplicationReleaseRole.TeacherConsole,
        "StudentSetup" => ApplicationReleaseRole.StudentSetup,
        _ => throw new InvalidDataException("更新角色无效。")
    };

    private static void EnsureSamePath(string actualPath, string expectedPath, string message)
    {
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(message);
    }

    private static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新目录不能是符号链接或重解析点。");
    }

    private static void WriteFailureLog(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VeyonCampus", "Updates");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "update-helper.log");
            File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} {exception.GetType().Name}: {exception.Message}{Environment.NewLine}");
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException) { }
    }

    private sealed record RoleInformation(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("product")] string Product,
        [property: JsonPropertyName("version")] string Version);
}
