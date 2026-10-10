using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Xml.Linq;
using VeyonCampus.Core;

internal static class AgentReconfigurationChecks
{
    [SupportedOSPlatform("windows")]
    public static void Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Agent reconfiguration fixture requires an elevated Windows process.");

        var initialState = WebsitePolicyAgentInstaller.VerifyAbsent();
        RequireSucceeded(initialState, "preflight");

        var temporary = Path.Combine(TestPath.CanonicalTempRoot(),
            "veyon-agent-reconfigure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var installationAttempted = false;
        try
        {
            var agentDirectory = FindPublishedAgentDirectory();
            using var veyonKey = RSA.Create(2048);
            using var websitePolicyKey = RSA.Create(3072);
            using var replacementWebsitePolicyKey = RSA.Create(3072);
            var veyonPublicKeyPath = Path.Combine(temporary, "veyon-public.pem");
            File.WriteAllText(veyonPublicKeyPath, veyonKey.ExportSubjectPublicKeyInfoPem());

            var firstCampus = "ci-" + Guid.NewGuid().ToString("N")[..16];
            var secondCampus = "ci-" + Guid.NewGuid().ToString("N")[..16];
            string BuildPackage(string name, string campus, string websitePublicKeyPem) => PackageBuilder.Build(
                Path.Combine(temporary, name), campus, "PC-", veyonPublicKeyPath,
                websitePolicyPublicKeyPem: websitePublicKeyPem);

            var firstWebsitePublicKey = websitePolicyKey.ExportSubjectPublicKeyInfoPem();
            var replacementWebsitePublicKey = replacementWebsitePolicyKey.ExportSubjectPublicKeyInfoPem();
            var firstPackage = PackageContext.Load(BuildPackage("package-first", firstCampus, firstWebsitePublicKey));
            var changedPackage = PackageContext.Load(BuildPackage("package-changed", firstCampus, firstWebsitePublicKey));
            if (firstPackage.DeploymentId is null || changedPackage.DeploymentId is null ||
                firstPackage.DeploymentId == changedPackage.DeploymentId)
                throw new InvalidDataException("Fixture packages must have distinct deployment IDs to exercise configuration reload.");
            var rotatedTrustPackage = PackageContext.Load(
                BuildPackage("package-rotated-trust", firstCampus, replacementWebsitePublicKey));
            var switchedCampusPackage = PackageContext.Load(
                BuildPackage("package-switched-campus", secondCampus, replacementWebsitePublicKey));

            var snapshots = Path.Combine(temporary, "snapshots");
            Directory.CreateDirectory(snapshots);

            installationAttempted = true;
            InstallAndVerify(firstPackage, snapshots, agentDirectory, "initial deployment");
            InstallAndVerify(firstPackage, snapshots, agentDirectory, "repeated identical deployment");
            InstallAndVerify(changedPackage, snapshots, agentDirectory, "changed deployment configuration");
            InstallAndVerify(rotatedTrustPackage, snapshots, agentDirectory, "changed signing-key replacement");
            InstallAndVerify(switchedCampusPackage, snapshots, agentDirectory, "campus replacement");

            Console.WriteLine("PASS Windows Agent repeated deployment, config restart, signing-key rotation, campus replacement, and health verification");
        }
        finally
        {
            try
            {
                if (installationAttempted)
                {
                    var removed = WebsitePolicyAgentInstaller.Uninstall();
                    RequireSucceeded(removed, "cleanup uninstall");
                }
                RequireSucceeded(WebsitePolicyAgentInstaller.VerifyAbsent(), "cleanup verification");
            }
            finally
            {
                RemoveFixtureDirectorySafely(temporary);
            }
        }
    }

    private static void InstallAndVerify(PackageContext package, string snapshots, string agentDirectory,
        string scenario)
    {
        using var snapshot = PackageResourceSnapshot.Create(snapshots, package);
        RequireSucceeded(WebsitePolicyAgentInstaller.Install(package, snapshot, agentDirectory), scenario + " install");
        RequireSucceeded(WebsitePolicyAgentInstaller.VerifyInstalled(package), scenario + " readback");
    }

    private static string FindPublishedAgentDirectory()
    {
        var projectPath = Path.Combine(Environment.CurrentDirectory,
            "src", "VeyonCampus.App", "VeyonCampus.App.csproj");
        var versionText = XDocument.Load(projectPath).Root?.Elements("PropertyGroup")
            .SelectMany(group => group.Elements("Version"))
            .Select(element => element.Value)
            .FirstOrDefault();
        if (!Version.TryParse(versionText, out var version))
            throw new InvalidDataException("无法从 VeyonCampus.App.csproj 读取应用版本。");

        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts",
            $"windows-x64-v{version.Major}.{version.Minor}.{version.Build}-student-setup", "WebsitePolicyAgent");
        if (!File.Exists(Path.Combine(directory, "VeyonCampus.Agent.exe")))
            throw new FileNotFoundException("StudentSetup 尚未生成独立的 WebsitePolicyAgent。", directory);
        return directory;
    }

    private static void RequireSucceeded(StepResult result, string scenario)
    {
        if (result.Status != ExecutionPlan.Succeeded)
            throw new InvalidOperationException($"Agent {scenario} did not succeed ({result.Status}): {result.Detail}");
        Console.WriteLine($"PASS Agent {scenario}");
    }

    private static void RemoveFixtureDirectorySafely(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(TestPath.CanonicalTempRoot()).TrimEnd(Path.DirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("veyon-agent-reconfigure-", StringComparison.Ordinal))
            throw new IOException("Unexpected Agent reconfiguration fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
