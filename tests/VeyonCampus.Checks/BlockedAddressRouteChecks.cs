using System.Net.Sockets;
using System.Security.Principal;
using VeyonCampus.Core;

/// <summary>
/// Proves the route that takes a blocked address out of the send path actually works, and that it is
/// removed again. This one touches the real routing table, so it runs only when asked for and only
/// with administrator rights; it uses a public resolver as a probe because that is reachable from a
/// school machine without depending on any blocked site.
/// </summary>
internal static class BlockedAddressRouteChecks
{
    private static readonly string[] CandidateProbes = ["223.5.5.5", "119.29.29.29", "1.1.1.1"];

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP blocked-address-route: Windows only");
            return;
        }
        if (!IsElevated())
        {
            Console.WriteLine("SKIP blocked-address-route: 需要管理员权限才能修改路由表");
            return;
        }

        Expect(!BlockedAddressRoute.TryParseHost("203.0.113.0/24", out _),
            "a /24 was accepted as a host route; that would take a whole range offline.");
        Expect(!BlockedAddressRoute.TryParseHost("2606:4700::/32", out _), "an IPv6 prefix was accepted.");
        Expect(BlockedAddressRoute.TryParseHost("203.0.113.7/32", out var host) && host.ToString() == "203.0.113.7",
            "a /32 host route was not parsed.");
        Expect(BlockedAddressRoute.TryParseHost("203.0.113.7", out _), "a bare host address was not parsed.");

        var probe = CandidateProbes.FirstOrDefault(CanConnect);
        if (probe is null)
        {
            Console.WriteLine("SKIP blocked-address-route: 找不到可达的探针地址");
            return;
        }

        try
        {
            var installed = BlockedAddressRoute.Install([probe + "/32"]);
            Expect(installed >= 1,
                $"route install reported no route for {probe} (last status {BlockedAddressRoute.LastFailureStatus}).");
            Expect(!CanConnect(probe), $"{probe} stayed reachable after its host route was installed.");
            // The catch-up loop re-applies the same plan; that must not change or drop the route.
            Expect(BlockedAddressRoute.Install([probe + "/32"]) == installed,
                "re-applying the same plan changed the route count.");
            Expect(!CanConnect(probe), $"{probe} became reachable again after a repeated apply.");
        }
        finally
        {
            var removed = BlockedAddressRoute.RemoveAll();
            Console.WriteLine($"removed {removed} blocked-address route(s) for {probe}");
        }

        Expect(CanConnect(probe), $"{probe} did not recover after the route was removed.");
        Expect(BlockedAddressRoute.RemoveAll() == 0, "removing routes a second time reported a removal.");
    }

    private static bool CanConnect(string address)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(address, 443);
            return task.Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch (Exception exception) when (exception is SocketException or AggregateException or
                                              ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
                                              PlatformNotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
