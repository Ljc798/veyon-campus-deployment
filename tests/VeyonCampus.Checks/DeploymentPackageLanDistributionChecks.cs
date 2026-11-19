using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VeyonCampus.Core;

/// <summary>
/// Covers classroom-local distribution: a teacher machine serving its own package, a student reading
/// it through the same catalogue client the cloud path uses, and the probe that tells a teacher which
/// machines in the room already run Veyon Campus.
/// </summary>
internal static class DeploymentPackageLanDistributionChecks
{
    public static async Task RunAsync()
    {
        var address = FindPrivateAddress();
        if (address is null)
        {
            Console.WriteLine("SKIP classroom LAN distribution: no active private IPv4 interface.");
            return;
        }
        var sample = FindSamplePackage();
        if (sample is null)
        {
            Console.WriteLine("SKIP classroom LAN distribution: sample package not found.");
            return;
        }

        RejectsAddressesOutsideTheClassroom();
        EnumeratesTheWholeSubnet();

        var downloads = new List<DeploymentPackageLanDownload>();
        DeploymentPackageLanServer shared;
        try
        {
            shared = DeploymentPackageLanServer.Start(sample, new Capture(downloads));
        }
        catch (SocketException)
        {
            // A developer machine with the teacher console open already owns 39178. CI never does, so
            // this is a skip rather than a failure - the enumeration checks above already ran.
            Console.WriteLine("SKIP classroom LAN distribution: port 39178 is already in use.");
            return;
        }
        await using var _ = shared;
        Expect(shared.Addresses.Count > 0, "the server bound no LAN address");
        Expect(shared.CatalogUrls.All(url => url.StartsWith("http://", StringComparison.Ordinal)),
            "the catalogue was not published over the LAN scheme");

        using var httpClient = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        var client = new DeploymentPackageCatalogClient(
            [DeploymentCatalogSource.Local(shared.Addresses[0])], null, httpClient);

        var result = await client.SearchAsync("");
        Expect(result.Items.Count == 1, "the LAN catalogue did not return the shared package");
        var entry = result.Items[0];
        Expect(entry.PackageId == shared.PackageId, "the catalogue advertised a different package id");
        Expect(entry.CampusName == shared.CampusName, "the catalogue advertised a different campus");
        Expect(entry.SizeBytes == shared.SizeBytes, "the catalogue advertised a different size");
        Expect(string.Equals(entry.Sha256, shared.Sha256, StringComparison.OrdinalIgnoreCase),
            "the catalogue advertised a different digest");
        Expect(entry.DownloadUrl.StartsWith($"http://{shared.Addresses[0]}:", StringComparison.Ordinal),
            "the download URL did not point back at the address the client used");

        var archive = await client.DownloadAsync(entry.PackageId, "0000");
        Expect(archive.Length == shared.SizeBytes, "the downloaded archive had the wrong length");

        var directory = Path.Combine(Path.GetTempPath(), "veyon-lan-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var context = CampusConfigurationArchive.ExtractToStore(archive, directory);
            Expect(context.Campus == shared.CampusName, "the delivered package did not load its campus");
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }

        Expect(downloads.Count == 1 && downloads[0].BytesSent == shared.SizeBytes,
            "the server did not report the download");

        var probe = await DeploymentPackageLanDiscovery.ProbeAsync(shared.Addresses[0].ToString());
        Expect(probe.HasProvider, "the probe did not detect the running share");
        Expect(probe.CampusName == shared.CampusName, "the probe reported the wrong campus");
        Expect(probe.Address == shared.Addresses[0].ToString(), "the probe resolved the wrong address");

        var providers = await DeploymentPackageLanDiscovery.FindProvidersAsync(
            [shared.Addresses[0].ToString()]);
        Expect(providers.Count == 1, "the provider sweep did not find the running share");
        Expect(providers[0].PackageId == shared.PackageId, "the provider sweep reported a different package");
        Expect(providers[0].SizeBytes == shared.SizeBytes, "the provider sweep reported a different size");
        Expect(string.Equals(providers[0].Sha256, shared.Sha256, StringComparison.OrdinalIgnoreCase),
            "the provider sweep reported a different digest");

        var unknown = await DeploymentPackageLanDiscovery.ProbeAsync("192.0.2.77");
        Expect(!unknown.HasProvider && !unknown.HasAgent, "the probe reported a share on a dead address");
    }

    private static void RejectsAddressesOutsideTheClassroom()
    {
        Expect(!DeploymentPackageLanDistribution.IsLanCatalogUri(
                new Uri("http://example.com:39178/v1/catalog")), "a public host was treated as a classroom share");
        Expect(!DeploymentPackageLanDistribution.IsLanCatalogUri(
                new Uri("https://192.168.1.5:39178/v1/catalog")), "HTTPS was treated as a classroom share");
        Expect(!DeploymentPackageLanDistribution.IsLanCatalogUri(
                new Uri("http://192.168.1.5:8080/v1/catalog")), "a foreign port was treated as a classroom share");
        Expect(!DeploymentPackageLanDistribution.IsLanPackageUri(
                new Uri("http://192.168.1.5:39178/v1/catalog")), "the catalogue path was treated as an archive");
        Expect(DeploymentPackageLanDistribution.IsLanPackageUri(
                new Uri("http://192.168.1.5:39178/v1/packages/b747bb33-f048-4118-9a4d-a7dcd298b919.zip")),
            "a classroom archive URL was rejected");
    }

    /// <summary>
    /// The sweep has to cover the subnet the machine is actually on. A student inside a NAT'd test VM
    /// sits on a /20 whose teacher (the host) is in the neighbouring /24 - assuming /24 is exactly how
    /// discovery "works" in the lab and finds nothing on the bench.
    /// </summary>
    private static void EnumeratesTheWholeSubnet()
    {
        var slash24 = DeploymentPackageLanDiscovery.EnumerateClassroomTargets(IPAddress.Parse("192.168.31.39"), 24);
        Expect(slash24.Count == 253, "a /24 did not enumerate 253 peers");
        Expect(!slash24.Contains("192.168.31.39"), "the sweep probed the machine it runs on");
        Expect(!slash24.Contains("192.168.31.0") && !slash24.Contains("192.168.31.255"),
            "the sweep probed the network or broadcast address");

        var slash23 = DeploymentPackageLanDiscovery.EnumerateClassroomTargets(IPAddress.Parse("10.1.2.7"), 23);
        Expect(slash23.Count == 509, "a /23 did not enumerate 509 peers");
        Expect(slash23.Contains("10.1.3.1"), "a /23 missed the second half of its own subnet");

        var slash20 = DeploymentPackageLanDiscovery.EnumerateClassroomTargets(IPAddress.Parse("172.21.161.5"), 20);
        Expect(slash20.Count == 4093, "a /20 did not enumerate 4093 peers");
        Expect(slash20.Contains("172.21.160.1"), "a /20 missed the gateway that sits in the neighbouring /24");

        var slash16 = DeploymentPackageLanDiscovery.EnumerateClassroomTargets(IPAddress.Parse("10.8.9.10"), 16);
        Expect(slash16.Count == 253, "a /16 was walked in full instead of falling back to the local /24");
        Expect(slash16.All(target => target.StartsWith("10.8.9.", StringComparison.Ordinal)),
            "the /16 fallback left the machine's own /24");

        var networks = DeploymentPackageLanDiscovery.FindClassroomNetworks();
        Expect(networks.All(network => network.PrefixLength is >= 8 and <= 30),
            "a classroom network reported an implausible prefix length");
        Expect(networks.All(network => network.SubnetDescription.Contains('/')),
            "a classroom network could not describe its subnet");
    }

    private sealed class Capture(List<DeploymentPackageLanDownload> sink) : IProgress<DeploymentPackageLanDownload>
    {
        public void Report(DeploymentPackageLanDownload value) => sink.Add(value);
    }

    private static IPAddress? FindPrivateAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicastAddress => unicastAddress.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                       !IPAddress.IsLoopback(address) &&
                                       StudentApplicationUpdateCryptography.IsPrivateLanAddress(address));

    /// <summary>
    /// Walks up from the test binary until the repository root appears. The sample is committed, so a
    /// CI agent has it too - unlike the generated packages under artifacts/.
    /// </summary>
    private static string? FindSamplePackage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "samples", "student-config-v3-demo");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "manifest.json")))
                return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception("Classroom LAN distribution check failed: " + message + ".");
    }
}
