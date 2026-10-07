using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VeyonCampus.Core;

/// <summary>Explicit, verified ACLs for the SYSTEM agent's own files and directories.</summary>
[SupportedOSPlatform("windows")]
internal static class AgentFileSecurity
{
    public static void Secure(string path, bool directory, bool executable = false)
        => Secure(path, directory, executable, allowUsersRead: true);

    public static void SecurePrivateFile(string path) =>
        Secure(path, directory: false, executable: false, allowUsersRead: false);

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
        Secure(directory, directory: true);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            PathLinkSecurity.RejectLinks(path);
            if (Directory.Exists(path)) SecureTree(path, executable);
            else if (Path.GetFileName(path).Equals(StudentAgentIdentityKeyStore.FileName,
                         StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(path).StartsWith(StudentAgentIdentityKeyStore.FileName + ".tmp-",
                         StringComparison.OrdinalIgnoreCase))
                SecurePrivateFile(path);
            else Secure(path, directory: false,
                executable && !Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase));
        }
    }
}
