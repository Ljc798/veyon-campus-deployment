using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using VeyonCampus.Core;

internal static class AgentInstallationChecks
{
    [SupportedOSPlatform("windows")]
    public static void Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Agent ACL fixtures require an elevated Windows process.");
        var temporary = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-agent-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var source = Path.Combine(temporary, "source");
            Directory.CreateDirectory(Path.Combine(source, "nested"));
            File.WriteAllText(Path.Combine(source, "VeyonCampus.Agent.exe"), "agent fixture");
            File.WriteAllText(Path.Combine(source, "nested", "runtime.dll"), "runtime fixture");

            // Reproduce the previous installer on real NTFS ACLs, not a mocked command.
            var old = Path.Combine(temporary, "old");
            Directory.CreateDirectory(old);
            var oldFile = Path.Combine(old, "runtime.dll");
            File.Copy(Path.Combine(source, "nested", "runtime.dll"), oldFile);
            var runner = new ProcessRunner();
            runner.Run("icacls.exe", [old, "/inheritance:r", "/grant:r",
                "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-32-545:(OI)(CI)RX", "/T", "/C"],
                temporary, TimeSpan.FromSeconds(30));
            var oldRules = new FileInfo(oldFile).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToArray();
            Console.WriteLine($"Old installer exit={runner.ExitCode}; file ACL=" +
                              new FileInfo(oldFile).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            if (oldRules.Any(rule => rule.IdentityReference.Value == "S-1-5-18" &&
                rule.AccessControlType == AccessControlType.Allow &&
                !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) &&
                rule.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute)))
                throw new Exception("Old ACL defect did not reproduce; reassess the diagnosis.");
            Console.WriteLine("PASS reproduced old recursive directory-grant defect on a DLL");

            var target = Path.Combine(temporary, "installed", "0.4.23");
            Invoke("InstallApplicationFiles", source, target);
            AssertTree(target);
            if (File.ReadAllText(Path.Combine(target, "nested", "runtime.dll")) != "runtime fixture")
                throw new Exception("Copied content mismatch.");
            Console.WriteLine("PASS fresh installation, staging rename, nested file ACLs and content");

            var damaged = Path.Combine(target, "nested", "runtime.dll");
            var empty = new FileSecurity();
            empty.SetSecurityDescriptorSddlForm("D:P", AccessControlSections.Access);
            new FileInfo(damaged).SetAccessControl(empty);
            Invoke("InstallApplicationFiles", source, target);
            AssertTree(target);
            Invoke("InstallApplicationFiles", source, target);
            Console.WriteLine("PASS repair empty file DACL and repeat deployment");

            var config = Path.Combine(target, "agent.json");
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"fixture\":true}");
            Invoke("WriteSecureConfig", config, bytes);
            new FileInfo(config).SetAccessControl(empty);
            Invoke("WriteSecureConfig", config, bytes);
            AssertAcl(config, directory: false, executable: false);
            Console.WriteLine("PASS existing unchanged configuration ACL repair");
            File.Delete(config);

            File.WriteAllText(damaged, "different installed file");
            Reject(() => Invoke("InstallApplicationFiles", source, target));
            Console.WriteLine("PASS refuses to overwrite mismatched installed content");

            var link = Path.Combine(source, "linked");
            Directory.CreateSymbolicLink(link, target);
            try { Reject(() => Invoke("InstallApplicationFiles", source, Path.Combine(temporary, "installed", "linked-test"))); }
            finally { Directory.Delete(link); }
            Console.WriteLine("PASS rejects linked source directories");

            var artifactsDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts");
            var appProjectPath = Path.Combine(Environment.CurrentDirectory, "src", "VeyonCampus.App", "VeyonCampus.App.csproj");
            var appVersionText = XDocument.Load(appProjectPath).Root?.Elements("PropertyGroup")
                .SelectMany(group => group.Elements("Version")).Select(element => element.Value).FirstOrDefault();
            if (!Version.TryParse(appVersionText, out var appVersion))
                throw new InvalidDataException("无法从 VeyonCampus.App.csproj 读取应用版本。");
            var published = Path.Combine(artifactsDirectory,
                $"windows-x64-v{appVersion.Major}.{appVersion.Minor}.{appVersion.Build}-student-setup", "WebsitePolicyAgent");
            if (Directory.Exists(published))
            {
                var installed = Path.Combine(temporary, "published-agent");
                Invoke("InstallApplicationFiles", published, installed);
                AssertTree(installed);
                var launch = new ProcessRunner();
                // No arguments returns 2 after the native host and managed runtime load.
                // It never starts the listener, changes policies, or registers a task.
                launch.Run(Path.Combine(installed, "VeyonCampus.Agent.exe"), [], installed, TimeSpan.FromSeconds(30));
                if (launch.ExitCode != 2) throw new Exception("Published agent could not load: " + launch.Stderr);
                Console.WriteLine("PASS published self-contained Agent copied, ACL verified, runtime loaded (exit 2)");
            }
            else Console.WriteLine("SKIP published Agent smoke check: current Student package has not been built");
        }
        finally
        {
            // Only this uniquely created fixture directory can be removed.
            var full = Path.GetFullPath(temporary);
            var prefix = Path.GetFullPath(TestPath.CanonicalTempRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("veyon-agent-acl-", StringComparison.Ordinal))
                throw new IOException("Unexpected fixture cleanup path.");
            RestoreForCleanup(full);
            Directory.Delete(full, recursive: true);
        }
    }

    private static void Invoke(string method, params object[] args)
    {
        try
        {
            typeof(WebsitePolicyAgentInstaller).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { return; }
        throw new Exception("Unsafe installation was accepted.");
    }

    [SupportedOSPlatform("windows")]
    private static void AssertTree(string path)
    {
        AssertAcl(path, directory: true, executable: true);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (Directory.Exists(entry)) AssertTree(entry);
            else AssertAcl(entry, directory: false, executable: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertAcl(string path, bool directory, bool executable)
    {
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (!acl.AreAccessRulesProtected || rules.Length != 3 || rules.Any(rule => rule.IsInherited ||
            rule.AccessControlType != AccessControlType.Allow || rule.PropagationFlags != PropagationFlags.None))
            throw new Exception("Unrestricted or ineffective ACL: " + path);
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            if (!rules.Any(rule => rule.IdentityReference.Value == sid && rule.FileSystemRights == FileSystemRights.FullControl))
                throw new Exception("SYSTEM/admin lacks full access: " + path);
        var users = rules.Single(rule => rule.IdentityReference.Value == "S-1-5-32-545");
        var expected = (directory || executable ? FileSystemRights.ReadAndExecute : FileSystemRights.Read) | FileSystemRights.Synchronize;
        if (users.FileSystemRights != expected) throw new Exception("Unexpected Users access: " + path);
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreForCleanup(string path)
    {
        FileSystemSecurity acl = Directory.Exists(path) ? new DirectorySecurity() : new FileSecurity();
        acl.SetSecurityDescriptorSddlForm("D:P(A;;FA;;;BA)(A;;FA;;;SY)", AccessControlSections.Access);
        if (Directory.Exists(path))
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)acl);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) RestoreForCleanup(entry);
        }
        else new FileInfo(path).SetAccessControl((FileSecurity)acl);
    }
}
