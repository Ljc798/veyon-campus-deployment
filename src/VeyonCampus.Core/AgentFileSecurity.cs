using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using VeyonCampus.Core.Maintenance;

namespace VeyonCampus.Core;

/// <summary>Explicit, verified ACLs for the SYSTEM agent's own files and directories.</summary>
[SupportedOSPlatform("windows")]
internal static class AgentFileSecurity
{
    public static void Secure(string path, bool directory, bool executable = false)
        => Secure(path, directory, executable, allowUsersRead: true);

    public static void SecurePrivateFile(string path) =>
        Secure(path, directory: false, executable: false, allowUsersRead: false);

    public static void SecurePrivateDirectory(string path) =>
        Secure(path, directory: true, executable: false, allowUsersRead: false);

    public static bool IsPrivate(string path, bool directory)
    {
        PathLinkSecurity.RejectLinks(path);
        FileSystemSecurity expected = directory ? new DirectorySecurity() : new FileSecurity();
        expected.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = directory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        expected.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-18"),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        expected.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-544"),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        FileSystemSecurity actual = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        return actual.AreAccessRulesProtected && Rules(actual).SequenceEqual(Rules(expected));
    }

    private static void Secure(string path, bool directory, bool executable, bool allowUsersRead)
    {
        PathLinkSecurity.RejectLinks(path);
        FileSystemSecurity expected = directory ? new DirectorySecurity() : new FileSecurity();
        expected.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = directory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        var rules = new List<(string Sid, FileSystemRights Rights)>
        {
            ("S-1-5-18", FileSystemRights.FullControl),
            ("S-1-5-32-544", FileSystemRights.FullControl)
        };
        if (allowUsersRead)
            rules.Add(("S-1-5-32-545", directory || executable
                ? FileSystemRights.ReadAndExecute : FileSystemRights.Read));
        foreach (var (sid, rights) in rules)
            expected.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), rights,
                inheritance, PropagationFlags.None, AccessControlType.Allow));

        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)expected);
        else new FileInfo(path).SetAccessControl((FileSecurity)expected);
        FileSystemSecurity actual = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        // Windows may add the auto-inherited control bit even to a protected DACL.
        // Compare effective ACEs rather than the textual SDDL control flags.
        if (!actual.AreAccessRulesProtected || !Rules(actual).SequenceEqual(Rules(expected)))
            throw new IOException($"网站代理权限读回不一致：{path}");
    }

    private static string[] Rules(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => $"{rule.IdentityReference.Value}:{rule.AccessControlType}:{rule.FileSystemRights}:" +
                            $"{rule.InheritanceFlags}:{rule.PropagationFlags}:{rule.IsInherited}")
            .OrderBy(rule => rule, StringComparer.Ordinal).ToArray();

    public static void SecureTree(string directory, bool executable = true)
    {
        if (IsAgentMaintenanceDirectory(directory))
        {
            SecurePrivateDirectory(directory);
            VerifyPrivateMaintenanceDirectory(directory);
            return;
        }
        Secure(directory, directory: true);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            PathLinkSecurity.RejectLinks(path);
            if (Directory.Exists(path) && IsHealthRepairStateDirectory(path))
                VerifyPrivateHealthRepairState(path);
            else if (Directory.Exists(path) && IsAgentMaintenanceDirectory(path))
            {
                SecurePrivateDirectory(path);
                VerifyPrivateMaintenanceDirectory(path);
            }
            else if (Directory.Exists(path)) SecureTree(path, executable);
            else if (IsAgentConfigurationDirectory(directory) && IsLegacyHealthRepairLedgerFile(path))
                SecurePrivateFile(path);
            else if (Path.GetFileName(path).Equals(StudentAgentIdentityKeyStore.FileName,
                         StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(path).StartsWith(StudentAgentIdentityKeyStore.FileName + ".tmp-",
                         StringComparison.OrdinalIgnoreCase))
                SecurePrivateFile(path);
            else Secure(path, directory: false,
                executable && !Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool IsAgentConfigurationDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Path.GetFileName(parent).Equals("WebsitePolicy", StringComparison.OrdinalIgnoreCase))
            return false;
        var campusHash = Path.GetFileName(path);
        if (campusHash.Length != 24) return false;
        foreach (var character in campusHash)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    private static bool IsHealthRepairStateDirectory(string path) =>
        Path.GetFileName(path).Equals("health-repair", StringComparison.OrdinalIgnoreCase) &&
        Path.GetDirectoryName(path) is { } configDirectory && IsAgentConfigurationDirectory(configDirectory);

    private static bool IsAgentMaintenanceDirectory(string path) =>
        Path.GetFileName(path).Equals("Maintenance", StringComparison.OrdinalIgnoreCase) &&
        Path.GetDirectoryName(path) is { } configDirectory && IsAgentConfigurationDirectory(configDirectory);

    private static void VerifyPrivateMaintenanceDirectory(string directory)
    {
        if (!IsPrivate(directory, directory: true))
            throw new IOException($"Maintenance 服务目录 ACL 无效：{directory}");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            PathLinkSecurity.RejectLinks(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0 &&
                Path.GetFileName(path).Equals(MaintenanceServiceUpdateExecutor.StagingDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                VerifyPrivateMaintenanceUpdateStagingDirectory(path);
                continue;
            }
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException($"Maintenance 服务目录不允许未审核的子目录：{path}");
            SecurePrivateFile(path);
        }
    }

    private static void VerifyPrivateMaintenanceUpdateStagingDirectory(string directory)
    {
        SecurePrivateDirectory(directory);
        if (!IsPrivate(directory, directory: true))
            throw new IOException($"Maintenance 更新暂存目录 ACL 无效：{directory}");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            PathLinkSecurity.RejectLinks(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0 ||
                !Regex.IsMatch(Path.GetFileName(path),
                    "^VeyonCampus-Student-Setup-(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?-win-x64\\.exe(?:\\.release\\.json)?$",
                    RegexOptions.CultureInvariant))
                throw new IOException($"Maintenance 更新暂存目录包含未审核的路径：{path}");
            SecurePrivateFile(path);
        }
    }

    private static bool IsLegacyHealthRepairLedgerFile(string path)
    {
        var name = Path.GetFileName(path);
        const string temporaryPrefix = "health-repair-ledger.json.tmp-";
        if (name.Equals("health-repair-ledger.json", StringComparison.OrdinalIgnoreCase)) return true;
        return name.StartsWith(temporaryPrefix, StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParseExact(name[temporaryPrefix.Length..], "N", out _);
    }

    private static void VerifyPrivateHealthRepairState(string directory)
    {
        if (!IsPrivate(directory, directory: true))
            throw new IOException($"Agent 健康修复状态目录 ACL 无效：{directory}");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            PathLinkSecurity.RejectLinks(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0 ||
                !IsLegacyHealthRepairLedgerFile(path) ||
                !IsPrivate(path, directory: false))
                throw new IOException($"Agent 健康修复状态文件 ACL 或归属无效：{path}");
        }
    }
}
