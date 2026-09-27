using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VeyonCampus.Core;

public sealed record StudentDeploymentVerificationReport(DateTimeOffset CheckedAt,
    IReadOnlyList<PreflightCheck> Checks)
{
    public bool IsReadyToRemoveSetupTool => Checks.Count > 0 && Checks.All(check =>
        check.Level is CheckLevel.Pass or CheckLevel.NotApplicable);

    public string AsText() => string.Join(Environment.NewLine + Environment.NewLine,
        Checks.Select(check => $"{LevelText(check.Level)} {check.Detail}"));

    private static string LevelText(CheckLevel level) => level switch
    {
        CheckLevel.Pass => "✓",
        CheckLevel.NotApplicable => "—",
        CheckLevel.Warning => "!",
        _ => "?"
    };
}

/// <summary>Read-only post-deployment checks for the background components that must remain after setup cleanup.</summary>
public static class StudentDeploymentVerification
{
    public static StudentDeploymentVerificationReport Check(PackageContext package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var checks = new List<PreflightCheck>();
        if (!OperatingSystem.IsWindows())
        {
            checks.Add(new("platform", CheckLevel.Blocked, "部署后验证只支持 Windows；未检查或推断本机状态。"));
            return new StudentDeploymentVerificationReport(DateTimeOffset.UtcNow, checks.AsReadOnly());
        }

        try
        {
            package.VerifyUnchanged();
            var veyon = new WindowsVeyonVerificationService().Verify(package, isTeacher: false);
            checks.Add(veyon.Ok
                ? new("veyon", CheckLevel.Pass,
                    $"Veyon {veyon.Version}、学生端密钥认证、公钥和 VeyonService 均已读回确认。{veyon.Detail}")
                : new("veyon", CheckLevel.Unknown,
                    $"Veyon 后台状态未能全部确认；请先处理此项，不要删除部署工具。{veyon.Detail}"));

            if (package.WebsitePolicyPublicKeyPath is null)
                checks.Add(new("website-agent", CheckLevel.NotApplicable,
                    "当前校区包没有网站策略公钥；不需要保留网站策略代理。"));
            else
            {
                var websiteAgent = WebsitePolicyAgentInstaller.VerifyInstalled(package);
                checks.Add(websiteAgent.Ok
                    ? new("website-agent", CheckLevel.Pass, websiteAgent.Detail)
                    : new("website-agent", CheckLevel.Unknown,
                        $"网站策略后台代理未能全部确认；请先处理此项，不要删除部署工具。{websiteAgent.Detail}"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          CryptographicException or System.ComponentModel.Win32Exception or JsonException)
        {
            checks.Add(new("readback", CheckLevel.Unknown,
                $"只读检查无法确认部署状态：{exception.Message}。没有执行清理。"));
        }

        return new StudentDeploymentVerificationReport(DateTimeOffset.UtcNow, checks.AsReadOnly());
    }
}

public sealed record StudentSetupBundleFile(string Path, string Sha256);
public sealed record StudentSetupBundleManifest(int SchemaVersion, string Product,
    IReadOnlyList<StudentSetupBundleFile> Files);

/// <summary>Starts the separately installed agent as a one-shot helper to remove only files in the signed-off portable bundle manifest.</summary>
public static class StudentSetupBundleCleanup
{
    public const string ManifestFileName = "veyon-campus-student-setup.json";
    public const string MarkerFileName = ".veyon-campus-student-setup";
    private const string MarkerContents = "VeyonCampusStudentSetupBundle-v1";
    private const string ProductName = "VeyonCampus.StudentSetup";
    private const string CurrentAppHostName = "VeyonCampus.StudentSetup.exe";
    private const string LegacyAppHostName = "VeyonCampus.exe";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static bool CanSchedule(string bundleDirectory, out string detail)
    {
        try
        {
            var (_, manifest) = ReadAndValidateManifest(bundleDirectory, verifyHashes: false);
            var bundleRoot = Path.GetFullPath(bundleDirectory).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!CurrentProcessMatchesStudentBundle(bundleRoot, manifest))
            {
                detail = "当前运行程序不在受清单保护的学生部署包内；没有安排文件清理。";
                return false;
            }
            var agent = WebsitePolicyAgentInstaller.InstalledExecutablePath;
            if (!File.Exists(agent))
            {
                detail = "未找到独立网站代理清理助手；无法在后台组件保持运行时安全清理部署工具。";
                return false;
            }
            detail = "部署工具文件清单完整；清理助手位于独立后台代理目录。";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or JsonException or ArgumentException)
        {
            detail = exception.Message;
            return false;
        }
    }

    public static StepResult Schedule(string bundleDirectory, int parentProcessId, long parentStartTimeUtcTicks)
    {
        const string step = "student-setup-cleanup";
        if (!OperatingSystem.IsWindows())
            return new(step, ExecutionPlan.Failed, "部署工具清理只支持 Windows。" );
        try
        {
            var (_, manifest) = ReadAndValidateManifest(bundleDirectory, verifyHashes: true);
            var bundleRoot = Path.GetFullPath(bundleDirectory).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!CurrentProcessMatchesStudentBundle(bundleRoot, manifest))
                return new(step, ExecutionPlan.NeedsReview,
                    "当前运行程序不在受清单保护的学生部署包内；没有安排文件清理。" );
            var agent = WebsitePolicyAgentInstaller.InstalledExecutablePath;
            if (!File.Exists(agent))
                return new(step, ExecutionPlan.NeedsReview, "独立网站代理清理助手不存在；没有删除部署工具。" );

            var startInfo = new ProcessStartInfo
            {
                FileName = agent,
                WorkingDirectory = Path.GetDirectoryName(agent)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--cleanup-student-setup");
            startInfo.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(parentStartTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(Path.GetFullPath(bundleDirectory));
            using var process = Process.Start(startInfo);
            return process is null
                ? new(step, ExecutionPlan.NeedsReview, "清理助手未能启动；没有删除部署工具。" )
                : new(step, ExecutionPlan.Succeeded,
                    "只读部署检查已通过；独立清理助手已启动。关闭窗口后，它会按发布清单移除便携 GUI 文件，保留后台代理、学生配置包及其他未列入清单的文件。" );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or JsonException or ArgumentException)
        {
            return new(step, ExecutionPlan.NeedsReview, $"无法安排清理；没有删除部署工具：{exception.Message}" );
        }
    }

    public static bool WaitAndRemove(int parentProcessId, long parentStartTimeUtcTicks, string bundleDirectory)
    {
        try
        {
            if (parentProcessId <= 0 || parentProcessId == Environment.ProcessId) return false;
            if (!WaitForParentExit(parentProcessId, parentStartTimeUtcTicks, TimeSpan.FromMinutes(2))) return false;
            var (root, manifest) = ReadAndValidateManifest(bundleDirectory, verifyHashes: true);

            foreach (var entry in manifest.Files)
                File.Delete(ResolveBundleFile(root, entry.Path));
            File.Delete(Path.Combine(root, ManifestFileName));
            File.Delete(Path.Combine(root, MarkerFileName));

            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Files)
            {
                var directory = Path.GetDirectoryName(ResolveBundleFile(root, entry.Path));
                while (directory is not null && directory.Length > root.Length &&
                       directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    directories.Add(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            }
            foreach (var directory in directories.OrderByDescending(path => path.Length))
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or JsonException or ArgumentException or
                                          System.ComponentModel.Win32Exception)
        {
            WriteCleanupLog($"cleanup-failed {exception.GetType().Name}");
            return false;
        }
    }

    private static bool WaitForParentExit(int processId, long expectedStartTimeUtcTicks, TimeSpan timeout)
    {
        try
        {
            using var parent = Process.GetProcessById(processId);
            var actualStartTime = parent.StartTime.ToUniversalTime().Ticks;
            if (actualStartTime != expectedStartTimeUtcTicks) return true;
            return parent.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static (string Root, StudentSetupBundleManifest Manifest) ReadAndValidateManifest(
        string bundleDirectory, bool verifyHashes)
    {
        if (string.IsNullOrWhiteSpace(bundleDirectory)) throw new InvalidDataException("部署工具目录为空。" );
        var root = Path.GetFullPath(bundleDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || rootInfo.LinkTarget is not null ||
            (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0 || IsProtectedRoot(root))
            throw new InvalidDataException("部署工具目录不存在、是重解析点或属于受保护系统目录；没有清理。" );

        var marker = Path.Combine(root, MarkerFileName);
        if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
            !string.Equals(File.ReadAllText(marker).Trim(), MarkerContents, StringComparison.Ordinal))
            throw new InvalidDataException("目录缺少本项目学生部署包标记；拒绝删除未识别目录。" );

        var manifestPath = Path.Combine(root, ManifestFileName);
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists || manifestInfo.Length is <= 0 or > 2 * 1024 * 1024 ||
            (manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("部署工具文件清单不存在、过大或不是普通文件。" );
        var manifest = JsonSerializer.Deserialize<StudentSetupBundleManifest>(File.ReadAllBytes(manifestPath), JsonOptions)
                       ?? throw new InvalidDataException("部署工具文件清单为空。" );
        if (manifest.SchemaVersion != 1 || manifest.Product != ProductName ||
            manifest.Files is null || manifest.Files.Count is 0 or > 10000)
            throw new InvalidDataException("部署工具文件清单版本、产品或文件数量无效。" );

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? appHostName = null;
        var hasIndependentAgent = false;
        foreach (var entry in manifest.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Sha256) ||
                !Regex.IsMatch(entry.Sha256, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("部署工具清单含有无效路径或 SHA-256。" );
            var path = NormalizeRelativePath(entry.Path);
            if (path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(MarkerFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("清单不能把自身或包标记列为应用文件。" );
            if (!normalized.Add(path)) throw new InvalidDataException("部署工具清单含有重复路径。" );
            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith("VeyonCampus.Teacher.", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("VeyonCampus.Teacher", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("学生部署清单包含教师控制台文件；拒绝清理混合角色目录。" );
            if (IsStudentSetupAppHostName(path))
            {
                if (appHostName is not null)
                    throw new InvalidDataException("学生部署清单包含多个 GUI 主程序；拒绝清理混合角色目录。" );
                appHostName = path;
            }
            hasIndependentAgent |= path.Replace('\\', '/').Equals("WebsitePolicyAgent/VeyonCampus.Agent.exe",
                StringComparison.OrdinalIgnoreCase);
            var fullPath = ResolveBundleFile(root, path);
            EnsureNoReparsePointBetween(root, fullPath);
            if (!File.Exists(fullPath)) throw new InvalidDataException($"部署工具文件缺失：{path}" );
            if (verifyHashes && !string.Equals(HashFile(fullPath), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"部署工具文件与发布清单不符：{path}" );
        }
        if (appHostName is null || !hasIndependentAgent)
            throw new InvalidDataException("部署工具清单未同时包含 GUI 主程序和独立代理发布文件。" );
        return (root, manifest);
    }

    private static bool CurrentProcessMatchesStudentBundle(string bundleRoot,
        StudentSetupBundleManifest manifest)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)) return false;
        var fullProcessPath = Path.GetFullPath(processPath);
        var processDirectory = Path.GetDirectoryName(fullProcessPath);
        var appHostName = Path.GetFileName(fullProcessPath);
        return processDirectory is not null && PathEquals(processDirectory, bundleRoot) &&
               IsStudentSetupAppHostName(appHostName) &&
               manifest.Files.Any(entry => entry.Path.Replace('\\', '/').Equals(appHostName,
                   StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStudentSetupAppHostName(string path) =>
        path.Equals(CurrentAppHostName, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(LegacyAppHostName, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\0'))
            throw new InvalidDataException("部署工具清单包含非相对路径。" );
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException("部署工具清单包含目录穿越路径。" );
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static string ResolveBundleFile(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("清理目标超出部署工具目录。" );
        return fullPath;
    }

    private static void EnsureNoReparsePointBetween(string root, string filePath)
    {
        var current = Path.GetDirectoryName(filePath);
        while (current is not null && current.Length >= root.Length)
        {
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("部署工具路径包含重解析点；拒绝清理。" );
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
        if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("部署工具文件是重解析点；拒绝清理。" );
    }

    private static bool IsProtectedRoot(string path)
    {
        var protectedPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetPathRoot(path)
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value =>
            Path.GetFullPath(value!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return protectedPaths.Any(protectedPath => string.Equals(path, protectedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void WriteCleanupLog(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "VeyonCampus-setup-cleanup.log");
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
