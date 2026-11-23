using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;

namespace VeyonCampus.Core;

/// <summary>
/// The resolver lockdown the network layer writes alongside the address blocklist.
///
/// The address rules only cover names that were resolved when the policy was applied. A browser with
/// its own encrypted DNS, or a student who simply types a different DNS server, resolves the same
/// name to addresses that were never in the plan, so the block appears to work and then does not.
/// This plan closes both routes at the packet layer:
///
/// - port 53 to every public address is refused, except the resolvers this machine already uses, so
///   changing the DNS setting no longer changes the answer;
/// - port 853 (DoT/DoQ) is refused everywhere (written by the rule backend);
/// - port 443 to the well-known DoH endpoints is refused, because DoH shares the HTTPS port and
///   cannot be separated by port alone.
///
/// Private ranges are never touched: the classroom LAN, the gateway and a school DNS server on the
/// LAN all keep working.
/// </summary>
public sealed record WebsiteDnsLockdownPlan(
    IReadOnlyList<string> ResolverAddresses,
    IReadOnlyList<string> DohEndpoints,
    IReadOnlyList<string> AllowedServers,
    string? Error)
{
    public static WebsiteDnsLockdownPlan Empty { get; } = new([], [], [], null);

    public bool IsEmpty => ResolverAddresses.Count == 0 && DohEndpoints.Count == 0;
}

/// <summary>
/// Builds the resolver lockdown. Reading the machine's DNS servers is the only impure part; the plan
/// itself is a pure function so the range arithmetic can be checked without a firewall.
/// </summary>
public static class WebsiteDnsLockdown
{
    /// <summary>
    /// Well-known DoH endpoints. These addresses serve DNS over HTTPS and nothing the school needs,
    /// so refusing 443 to them costs nothing while removing the easiest bypass.
    /// </summary>
    private static readonly string[] KnownDohEndpointsV4 =
    [
        "1.1.1.1", "1.0.0.1",                 // Cloudflare
        "8.8.8.8", "8.8.4.4",                 // Google
        "9.9.9.9", "149.112.112.112",         // Quad9
        "208.67.222.222", "208.67.220.220",   // OpenDNS
        "94.140.14.14", "94.140.15.15",       // AdGuard
        "185.228.168.9", "185.228.169.9",     // CleanBrowsing
        "76.76.2.0", "76.76.10.0",            // Control D
        "223.5.5.5", "223.6.6.6",             // AliDNS (dns.alidns.com)
        "119.29.29.29", "182.254.116.116",    // DNSPod (doh.pub)
        "120.53.53.53",                       // Tencent DoH
        "180.76.76.76"                        // Baidu DNS
    ];

    private static readonly string[] KnownDohEndpointsV6 =
    [
        "2606:4700:4700::1111", "2606:4700:4700::1001",
        "2001:4860:4860::8888", "2001:4860:4860::8844",
        "2620:fe::fe", "2620:fe::9",
        "2620:119:35::35", "2620:119:53::53",
        "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"
    ];

    /// <summary>
    /// The resolvers a student would switch to in order to get around the hosts block: the public
    /// DNS services people actually know, plus the common Chinese ISP servers.
    ///
    /// A blacklist does not need more than this. Its block is written against destination addresses,
    /// so a resolver the machine was not using cannot make a blocked site reachable - it can only
    /// change which address the name maps to, and the rule already covers the addresses we resolved.
    /// Refusing *every* public destination on port 53, which this used to do, also refused whatever
    /// resolver a driver, a launcher or a school tool brought with it, and an unreachable resolver
    /// looks exactly like "the site I typed does not open". Only the allowlist mode, where the name
    /// answer is the enforcement, keeps the blanket rule.
    /// </summary>
    private static readonly string[] KnownPublicResolversV4 =
    [
        "8.8.8.8", "8.8.4.4",                  // Google
        "1.1.1.1", "1.0.0.1",                  // Cloudflare
        "9.9.9.9", "149.112.112.112",          // Quad9
        "208.67.222.222", "208.67.220.220",    // OpenDNS
        "94.140.14.14", "94.140.15.15",        // AdGuard
        "76.76.2.0", "76.76.10.0",             // Control D
        "223.5.5.5", "223.6.6.6",              // AliDNS
        "119.29.29.29", "182.254.116.116",     // DNSPod
        "120.53.53.53",                        // Tencent
        "180.76.76.76",                        // Baidu
        "114.114.114.114", "114.114.115.115",  // 114DNS
        "114.114.114.119", "114.114.115.119",
        "101.226.4.6", "218.30.118.6",         // 电信 DNS
        "202.96.128.86", "202.96.134.133",
        "202.96.209.133", "202.96.209.5",
        "202.106.0.20", "202.106.46.151",      // 联通 DNS
        "210.21.196.6", "221.5.88.88",
        "61.139.2.69", "218.2.135.1",
        "202.102.128.68", "219.141.136.10",
        "211.136.192.6", "211.136.17.107",     // 移动 DNS
        "218.85.157.99", "218.85.152.99"
    ];

    private static readonly string[] KnownPublicResolversV6 = KnownDohEndpointsV6;

    /// <summary>
    /// Everything that is either not reachable through the internet or must keep working: loopback,
    /// RFC1918, link local, carrier NAT, documentation, multicast and reserved. A DNS query to any
    /// of these is local and must not be refused.
    /// </summary>
    private static readonly (uint Start, uint End)[] ReservedV4Ranges =
    [
        Range(0, 8),          // 0.0.0.0/8 this network
        Range(10, 8),         // RFC1918
        Range(100, 64, 10),   // carrier NAT
        Range(127, 8),        // loopback
        Range(169, 254, 16),  // link local
        Range(172, 16, 12),   // RFC1918
        Range(192, 0, 24),    // IETF protocol assignments
        Range(192, 0, 2, 24), // TEST-NET-1
        Range(192, 88, 99, 24), // 6to4 relay anycast
        Range(192, 168, 16),  // RFC1918
        Range(198, 18, 15),   // benchmarking
        Range(198, 51, 100, 24), // TEST-NET-2
        Range(203, 0, 113, 24),  // TEST-NET-3
        Range(224, 4),        // multicast
        Range(240, 4)         // reserved
    ];

    /// <summary>2000::/3, the global unicast block; everything else is link local or special.</summary>
    private static readonly (UInt128 Start, UInt128 End) GlobalV6 =
        ((UInt128)0x2000 << 112, ((UInt128)0x4000 << 112) - 1);

    /// <summary>The DNS servers this machine currently uses, across every up interface.</summary>
    public static IReadOnlyList<IPAddress> ReadConfiguredServers()
    {
        var servers = new SortedSet<string>(StringComparer.Ordinal);
        var parsed = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                IPInterfaceProperties properties;
                try { properties = nic.GetIPProperties(); }
                catch (NetworkInformationException) { continue; }
                foreach (var raw in properties.DnsAddresses)
                {
                    var address = raw;
                    if (address.AddressFamily == AddressFamily.InterNetworkV6 &&
                        (address.IsIPv6LinkLocal || address.IsIPv6Multicast))
                        continue;
                    if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
                    if (!servers.Add(address.ToString())) continue;
                    parsed.Add(address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
        return parsed;
    }

    /// <summary>
    /// Builds the lockdown for these resolvers. An empty server list disables the port-53 rule
    /// instead of blocking every lookup: a machine whose DNS setting cannot be read must not be
    /// taken offline, and the DoH endpoints are still refused.
    /// </summary>
    /// <param name="restrictAllPublicResolvers">
    /// True only for allowlist mode, where the name answer *is* the enforcement and every public
    /// resolver has to be refused. A blacklist is enforced on destination addresses, so refusing
    /// the known public resolvers is enough and leaves resolvers the school's own software brings
    /// alone - refusing those used to look like "this one site does not open".
    /// </param>
    public static WebsiteDnsLockdownPlan Build(IReadOnlyList<IPAddress> configuredServers,
        bool restrictAllPublicResolvers = true)
    {
        ArgumentNullException.ThrowIfNull(configuredServers);
        var servers = configuredServers
            .Where(address => address is not null)
            .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .Distinct()
            .ToList();
        var own = servers.Select(address => address.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Never refuse 443 to the resolver this machine is actually using. Windows 11 upgrades a
        // known resolver to DNS-over-HTTPS on its own, so blocking that endpoint cuts the machine's
        // own lookups: cached names still open, everything else times out, which reads as "some
        // sites work and some do not".
        var doh = KnownDohEndpointsV4.Concat(KnownDohEndpointsV6)
            .Where(value => !own.Contains(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => IPAddress.Parse(value).AddressFamily)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray();

        if (servers.Count == 0)
            return new WebsiteDnsLockdownPlan([], Array.AsReadOnly(doh), [],
                "未读取到本机 DNS 服务器，53 端口强制未启用（避免断网）；DoH 端点仍然封锁。");

        string[] resolvers;
        if (restrictAllPublicResolvers)
        {
            var v4Holes = ReservedV4Ranges
                .Concat(servers.Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => SingleV4(address)))
                .ToList();
            var v4Ranges = SubtractAll([(0u, uint.MaxValue)], v4Holes);

            var v6Holes = servers.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(address => SingleV6(address))
                .ToList();
            var v6Ranges = SubtractAll([GlobalV6], v6Holes);

            resolvers = ToCidrs(v4Ranges).Concat(ToCidrs(v6Ranges))
                .OrderBy(value => value.Contains(':', StringComparison.Ordinal))
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }
        else
        {
            resolvers = KnownPublicResolversV4.Concat(KnownPublicResolversV6)
                .Where(value => !own.Contains(value))
                .Select(value => ToCidr(value))
                .OrderBy(value => value.Contains(':', StringComparison.Ordinal))
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        if (resolvers.Length == 0)
            return new WebsiteDnsLockdownPlan([], Array.AsReadOnly(doh), Describe(servers),
                "DNS 地址段计算结果为空，53 端口强制未启用。");

        return new WebsiteDnsLockdownPlan(Array.AsReadOnly(resolvers), Array.AsReadOnly(doh),
            Describe(servers), null);
    }

    private static string ToCidr(string address) =>
        IPAddress.Parse(address).AddressFamily == AddressFamily.InterNetwork
            ? address + "/32"
            : address + "/128";

    /// <summary>
    /// Confirms at least one configured resolver actually answers before port 53 is closed to the
    /// rest of the internet. A resolver list that is stale, or a server that is only reachable from
    /// another subnet, would otherwise turn a working machine into one that cannot resolve anything.
    /// The probe is a plain UDP query sent straight to the resolver, so it does not depend on the
    /// system resolver the lockdown is about to change.
    /// </summary>
    public static async Task<bool> AnyResolverRespondsAsync(IReadOnlyList<IPAddress> servers,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var candidates = servers
            .Where(address => address is not null && !address.Equals(IPAddress.Any) &&
                              !address.Equals(IPAddress.IPv6Any))
            .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .Distinct()
            .Take(4)
            .ToArray();
        if (candidates.Length == 0) return false;

        var results = await Task.WhenAll(candidates.Select(address =>
            ProbeAsync(address, timeout, cancellationToken))).ConfigureAwait(false);
        return results.Any(answered => answered);
    }

    private static async Task<bool> ProbeAsync(IPAddress server, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new UdpClient(server.AddressFamily);
            var query = BuildQuery(ProbeHost, id: 0x4A1B);
            await client.SendAsync(query.AsMemory(), new IPEndPoint(server, 53), cancellationToken)
                .ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var response = await client.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            var data = response.Buffer;
            if (data.Length < 12) return false;
            // A response to *some* query is enough: the point is that packets reach the resolver and
            // come back, not that this particular name resolves.
            return (data[2] & 0x80) != 0;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or
                                              ObjectDisposedException or ArgumentException)
        {
            return false;
        }
    }

    private const string ProbeHost = "www.msftconnecttest.com";

    /// <summary>A minimal recursive A query; only the header and question section are needed.</summary>
    internal static byte[] BuildQuery(string host, ushort id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var labels = host.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var length = 12 + labels.Sum(label => label.Length + 1) + 1 + 4;
        var packet = new byte[length];
        packet[0] = (byte)(id >> 8);
        packet[1] = (byte)(id & 0xFF);
        packet[2] = 0x01;                     // recursion desired
        packet[5] = 0x01;                     // one question
        var offset = 12;
        foreach (var label in labels)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            packet[offset++] = (byte)bytes.Length;
            bytes.CopyTo(packet, offset);
            offset += bytes.Length;
        }
        packet[offset++] = 0;                 // end of name
        packet[offset++] = 0x00;              // type A
        packet[offset++] = 0x01;
        packet[offset++] = 0x00;              // class IN
        packet[offset] = 0x01;
        return packet;
    }

    private static IReadOnlyList<string> Describe(IEnumerable<IPAddress> servers) =>
        Array.AsReadOnly(servers.Select(address => address.ToString()).ToArray());

    private static (uint Start, uint End) SingleV4(IPAddress address)
    {
        var value = ToUInt32(address);
        return (value, value);
    }

    private static (UInt128 Start, UInt128 End) SingleV6(IPAddress address)
    {
        var value = ToUInt128(address);
        return (value, value);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static UInt128 ToUInt128(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        UInt128 value = 0;
        foreach (var b in bytes) value = (value << 8) | b;
        return value;
    }

    /// <summary><c>address/bits</c> as a numeric range, so the subtraction is plain arithmetic.</summary>
    private static (uint Start, uint End) Range(int a, int prefix)
    {
        var start = (uint)a << 24;
        return (start, start + ((1u << (32 - prefix)) - 1));
    }

    private static (uint Start, uint End) Range(int a, int b, int prefix)
    {
        var start = ((uint)a << 24) | ((uint)b << 16);
        return (start, start + ((1u << (32 - prefix)) - 1));
    }

    private static (uint Start, uint End) Range(int a, int b, int c, int prefix)
    {
        var start = ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8);
        return (start, start + ((1u << (32 - prefix)) - 1));
    }

    private static List<(uint Start, uint End)> SubtractAll(
        List<(uint Start, uint End)> ranges, List<(uint Start, uint End)> holes)
    {
        foreach (var hole in holes) ranges = Subtract(ranges, hole);
        return ranges;
    }

    private static List<(uint Start, uint End)> Subtract(
        List<(uint Start, uint End)> ranges, (uint Start, uint End) hole)
    {
        var result = new List<(uint Start, uint End)>(ranges.Count + 1);
        foreach (var range in ranges)
        {
            if (hole.End < range.Start || hole.Start > range.End)
            {
                result.Add(range);
                continue;
            }
            if (hole.Start > range.Start) result.Add((range.Start, hole.Start - 1));
            if (hole.End < range.End) result.Add((hole.End + 1, range.End));
        }
        return result;
    }

    private static List<(UInt128 Start, UInt128 End)> SubtractAll(
        List<(UInt128 Start, UInt128 End)> ranges, List<(UInt128 Start, UInt128 End)> holes)
    {
        foreach (var hole in holes) ranges = Subtract(ranges, hole);
        return ranges;
    }

    private static List<(UInt128 Start, UInt128 End)> Subtract(
        List<(UInt128 Start, UInt128 End)> ranges, (UInt128 Start, UInt128 End) hole)
    {
        var result = new List<(UInt128 Start, UInt128 End)>(ranges.Count + 1);
        foreach (var range in ranges)
        {
            if (hole.End < range.Start || hole.Start > range.End)
            {
                result.Add(range);
                continue;
            }
            if (hole.Start > range.Start) result.Add((range.Start, hole.Start - 1));
            if (hole.End < range.End) result.Add((hole.End + 1, range.End));
        }
        return result;
    }

    private static IEnumerable<string> ToCidrs(IEnumerable<(uint Start, uint End)> ranges) =>
        ranges.SelectMany(range => ToCidrs(range.Start, range.End));

    private static IEnumerable<string> ToCidrs(IEnumerable<(UInt128 Start, UInt128 End)> ranges) =>
        ranges.SelectMany(range => ToCidrs(range.Start, range.End));

    /// <summary>
    /// Splits a range into the fewest aligned prefixes. Alignment matters: the firewall receives a
    /// prefix list, not a range, so an unaligned range would either over-block or leak.
    /// </summary>
    internal static IEnumerable<string> ToCidrs(uint start, uint end)
    {
        while (start <= end)
        {
            var bits = start == 0 ? 32 : BitOperations.TrailingZeroCount(start);
            while (bits > 0 && (ulong)start + (1UL << bits) - 1 > end) bits--;
            yield return $"{FormatV4(start)}/{32 - bits}";
            var next = (ulong)start + (1UL << bits);
            if (next > uint.MaxValue) yield break;
            start = (uint)next;
        }
    }

    internal static IEnumerable<string> ToCidrs(UInt128 start, UInt128 end)
    {
        // 1 << 128 wraps to 1 for UInt128, so the all-addresses case is answered before the loop.
        if (start == 0 && end == UInt128.MaxValue)
        {
            yield return "::/0";
            yield break;
        }
        while (start <= end)
        {
            var bits = start == 0 ? 128 : TrailingZeroCount(start);
            while (bits > 0 && start + ((UInt128)1 << bits) - 1 > end) bits--;
            yield return $"{FormatV6(start)}/{128 - bits}";
            var next = start + ((UInt128)1 << bits);
            if (next == 0) yield break;
            start = next;
        }
    }

    private static int TrailingZeroCount(UInt128 value)
    {
        var count = 0;
        while (count < 128 && (value & 1) == 0)
        {
            value >>= 1;
            count++;
        }
        return count;
    }

    private static string FormatV4(uint value) =>
        $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    private static string FormatV6(UInt128 value)
    {
        var bytes = new byte[16];
        for (var index = 15; index >= 0; index--)
        {
            bytes[index] = (byte)(value & 0xFF);
            value >>= 8;
        }
        return new IPAddress(bytes).ToString();
    }
}
