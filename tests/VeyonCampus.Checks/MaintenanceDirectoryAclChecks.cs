using System.Security.AccessControl;
using System.Security.Principal;
using VeyonCampus.Core;

/// <summary>
/// Reproduces the exact shape of the field failure: a Maintenance directory with a protected DACL
/// and a file written into it that inherited nothing, so its ACL is wider than the directory's.
/// The check then proves the ownership guard repairs such a file instead of refusing forever.
///
/// Needs an elevated process because it sets and reads real DACLs.
/// </summary>
internal static class MaintenanceDirectoryAclChecks
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Maintenance ACL fixture requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Maintenance ACL fixture requires an elevated Windows process.");

        var root = Path.Combine(Path.GetTempPath(), "veyon-maintenance-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var maintenance = Path.Combine(root, "Maintenance");
            Directory.CreateDirectory(maintenance);
            ApplyPrivateDirectoryAcl(maintenance);

            // A protected directory does not hand its ACEs to new children, which is how the replay
            // window ended up with an ACL nobody had set.
            var replay = Path.Combine(maintenance, "maintenance-replay-window.json");
            File.WriteAllText(replay, """{"schemaVersion":1,"entries":[]}""");
            var replayLock = replay + ".lock";
            File.WriteAllText(replayLock, string.Empty);
            var bootstrap = Path.Combine(maintenance, "maintenance-bootstrap.json");
            File.WriteAllText(bootstrap, "{}");
            var staging = Path.Combine(maintenance, "UpdateStaging");
            Directory.CreateDirectory(staging);
            var stagedInstaller = Path.Combine(staging, "VeyonCampus-Student-Setup-1.1.93-win-x64.exe");
            File.WriteAllText(stagedInstaller, "stub");

            // Make the failure unambiguous: grant Users read on the state file, exactly the kind of
            // inherited ACE that made the guard reject it.
            WidenAcl(replay);
            WidenAcl(replayLock);

            AgentFileSecurity.VerifyAgentMaintenanceDirectoryOwned(maintenance);

            // After a successful pass the guard has tightened the files it could.
            Expect(IsPrivateFile(replay), "the replay window ACL was not tightened.");
            Expect(IsPrivateFile(replayLock), "the replay lock ACL was not tightened.");
            Expect(IsPrivateFile(bootstrap), "the bootstrap ACL was not tightened.");
            Expect(IsPrivateFile(stagedInstaller), "the staged installer ACL was not tightened.");

            // An unknown name inside a private directory is product-owned by construction; it must
            // not block the update. A file that is a reparse point still must.
            File.WriteAllText(Path.Combine(maintenance, "some-future-state.json"), "{}");
            AgentFileSecurity.VerifyAgentMaintenanceDirectoryOwned(maintenance) ;
            Expect(IsPrivateFile(Path.Combine(maintenance, "some-future-state.json")),
                "a newly added product file was not tightened.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void ApplyPrivateDirectoryAcl(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-18"),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-544"),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void WidenAcl(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-18"),
            FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-544"),
            FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"),
            FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static bool IsPrivateFile(string path)
    {
        var actual = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        if (!actual.AreAccessRulesProtected) return false;
        var rules = actual.GetAccessRules(includeExplicit: true, includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        return rules.Length == 2 &&
               rules.All(rule => rule.AccessControlType == AccessControlType.Allow) &&
               rules.Any(rule => rule.IdentityReference.Value == "S-1-5-18") &&
               rules.Any(rule => rule.IdentityReference.Value == "S-1-5-32-544");
    }

    private static void TryDelete(string directory)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (Directory.Exists(entry)) continue;
                    var info = new FileInfo(entry) { IsReadOnly = false };
                    info.SetAccessControl(new FileSecurity());
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
