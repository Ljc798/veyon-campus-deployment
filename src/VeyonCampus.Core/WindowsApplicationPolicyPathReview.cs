using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VeyonCampus.Core;

/// <summary>Checks the mutable filesystem paths that the long-lived AppLocker baseline allows.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsApplicationPolicyPathReview
{
    internal static void VerifyAllowedExecutablePaths(IReadOnlySet<string> studentAccessSids)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Program Files ACL 检查仅支持 Windows。");
        ArgumentNullException.ThrowIfNull(studentAccessSids);

        var programFilesRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var windowsDirectories = new[] { "System32", "SysWOW64", "Microsoft.NET", "SystemApps", "WinSxS" }
            .Select(name => Path.GetFullPath(Path.Combine(windows, name)))
            .ToArray();
        if (string.IsNullOrWhiteSpace(windows) || programFilesRoots.Length == 0 ||
            programFilesRoots.Concat(windowsDirectories).Any(path => !Directory.Exists(path)))
            throw new IOException("无法确认 Windows Program Files 允许目录；没有启用学生软件限制。");

        var timer = Stopwatch.StartNew();
        foreach (var root in programFilesRoots.Concat(windowsDirectories))
            ReviewDirectory(new DirectoryInfo(root), studentAccessSids, timer);
        ReviewFile(new FileInfo(Path.Combine(windows, "explorer.exe")), studentAccessSids, timer);
    }

    private static void ReviewDirectory(DirectoryInfo directory, IReadOnlySet<string> studentAccessSids,
        Stopwatch timer)
    {
        CheckReviewTime(timer);
        RejectReparsePoint(directory);
        VerifyAcl(directory, directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner),
            studentAccessSids);

        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions
                 {
                     AttributesToSkip = 0,
                     IgnoreInaccessible = false,
                     RecurseSubdirectories = false,
                     ReturnSpecialDirectories = false
                 }))
        {
            CheckReviewTime(timer);
            RejectReparsePoint(entry);
            if (entry is DirectoryInfo childDirectory)
            {
                ReviewDirectory(childDirectory, studentAccessSids, timer);
                continue;
            }
            if (entry is FileInfo file)
                ReviewFile(file, studentAccessSids, timer);
        }
    }

    private static void ReviewFile(FileInfo file, IReadOnlySet<string> studentAccessSids, Stopwatch timer)
    {
        CheckReviewTime(timer);
        RejectReparsePoint(file);
        VerifyAcl(file, file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), studentAccessSids);
    }

    private static void VerifyAcl(FileSystemInfo entry, FileSystemSecurity acl,
        IReadOnlySet<string> studentAccessSids)
    {
        var owner = acl.GetOwner(typeof(SecurityIdentifier));
        if (owner is SecurityIdentifier ownerSid && studentAccessSids.Contains(ownerSid.Value))
            throw UnsafeAllowPath();

        foreach (FileSystemAccessRule rule in acl.GetAccessRules(includeExplicit: true, includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
        {
            var principalSid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (!AppLockerProgramFilesAclRules.IsStudentWriteAllowance(principalSid, rule.AccessControlType == AccessControlType.Allow,
                    (int)rule.FileSystemRights, studentAccessSids))
                continue;
            throw UnsafeAllowPath();
        }
    }

    private static void RejectReparsePoint(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Program Files 允许目录包含重解析点；无法证明路径 ACL 安全，没有启用学生软件限制。");
    }

    private static void CheckReviewTime(Stopwatch timer)
    {
        if (timer.Elapsed > TimeSpan.FromMinutes(2))
            throw new TimeoutException("Program Files ACL 审查超过两分钟；没有启用学生软件限制。");
    }

    private static IOException UnsafeAllowPath() =>
        new("AppLocker 允许范围内存在学生可写目录或文件；无法安全使用路径允许规则，没有启用学生软件限制。");
}

internal static class AppLockerProgramFilesAclRules
{
    // Numeric values of FileSystemRights: WriteData=2, AppendData=4, WriteEA=16,
    // DeleteChildren=64, WriteAttributes=256, Delete=65536, ChangePermissions=262144, TakeOwnership=524288.
    internal const int StudentWriteRightsMask = 2 | 4 | 16 | 64 | 256 | 65536 | 262144 | 524288;
    internal const int WriteDataMask = 2;
    internal const int ReadDataMask = 1;

    internal static bool IsStudentWriteAllowance(string principalSid, bool allow, int rights,
        IReadOnlySet<string> studentAccessSids) =>
        allow && studentAccessSids.Contains(principalSid) && (rights & StudentWriteRightsMask) != 0;
}
