using System.Net;
using System.Net.Sockets;
using VeyonCampus.Core;

/// <summary>
/// Covers the resolver lockdown arithmetic. The point of the feature is that a student can no longer
/// pick a different DNS server and get a different answer, so the tests check that public resolvers
/// are refused, that the machine's own resolvers and every private range are not, and that a
/// malformed plan is reported instead of being written.
/// </summary>
internal static class WebsiteDnsLockdownChecks
{
    public static void Run()
    {
        SplitsRangesIntoAlignedPrefixes();
        KeepsPrivateRangesAndOwnResolversOpen();
        RefusesPublicResolvers();
        RefusesKnownResolversOnlyForBlocklists();
        BuildsAWellFormedResolverProbe();
        KeepsIpv6ResolversOpen();
        BlocksDohWithoutKnowingTheResolver();
        AppliesLockdownWithTheAddressBlock();
        ReportsLockdownWriteDivergence();
    }

    private static void SplitsRangesIntoAlignedPrefixes()
    {
        Expect(WebsiteDnsLockdown.ToCidrs(0u, 255u).SequenceEqual(["0.0.0.0/24"]),
            "an aligned /24 was split into more prefixes than needed.");
        Expect(WebsiteDnsLockdown.ToCidrs(1u, 2u).SequenceEqual(["0.0.0.1/32", "0.0.0.2/32"]),
            "a two-address tail was not reduced to two /32 prefixes.");
        Expect(WebsiteDnsLockdown.ToCidrs(0u, uint.MaxValue).SequenceEqual(["0.0.0.0/0"]),
            "the whole IPv4 space was not expressed as a single prefix.");
        Expect(WebsiteDnsLockdown.ToCidrs(0u, 0u).SequenceEqual(["0.0.0.0/32"]),
            "a single address was not expressed as /32.");
    }

    private static void KeepsPrivateRangesAndOwnResolversOpen()
    {
        var plan = WebsiteDnsLockdown.Build([IPAddress.Parse("192.168.10.1")]);
        Expect(plan.Error is null, $"a usable plan reported an error: {plan.Error}");
        Expect(plan.ResolverAddresses.Count > 0, "the lockdown planned no prefixes.");
        Expect(plan.AllowedServers is { Count: 1 } servers && servers[0] == "192.168.10.1",
            "the allowed server list did not record the LAN resolver.");

        foreach (var privateAddress in new[]
                 {
                     "10.0.0.1", "172.16.5.5", "192.168.10.1", "127.0.0.1", "169.254.1.1",
                     "100.64.0.1", "224.0.0.1", "255.255.255.255"
                 })
            Expect(!Covered(plan.ResolverAddresses, IPAddress.Parse(privateAddress)),
                $"the lockdown would refuse DNS to the private address {privateAddress}.");
    }

    private static void RefusesPublicResolvers()
    {
        var plan = WebsiteDnsLockdown.Build([IPAddress.Parse("192.168.10.1")]);
        foreach (var publicResolver in new[] { "8.8.8.8", "1.1.1.1", "223.5.5.5", "119.29.29.29" })
            Expect(Covered(plan.ResolverAddresses, IPAddress.Parse(publicResolver)),
                $"DNS to the public resolver {publicResolver} is still allowed.");

        // A machine whose DNS is a public resolver must keep that one open, or the rule takes the
        // machine offline. The same address must also stay reachable on 443: Windows upgrades a
        // known resolver to DNS-over-HTTPS by itself, and blocking that endpoint cuts the machine's
        // own lookups instead of a bypass.
        var publicPlan = WebsiteDnsLockdown.Build([IPAddress.Parse("223.5.5.5")]);
        Expect(!Covered(publicPlan.ResolverAddresses, IPAddress.Parse("223.5.5.5")),
            "the configured public resolver was refused on port 53, which would break name resolution.");
        Expect(Covered(publicPlan.ResolverAddresses, IPAddress.Parse("8.8.8.8")),
            "a different public resolver stayed open.");
        Expect(!publicPlan.DohEndpoints.Contains("223.5.5.5"),
            "the resolver this machine uses was still refused on 443, which breaks its DoH upgrade.");
        Expect(publicPlan.DohEndpoints.Contains("1.1.1.1"),
            "an unused DoH endpoint was dropped from the block list as well.");
    }

    /// <summary>
    /// A blacklist is enforced on destination addresses, so it only needs the resolvers a student
    /// would deliberately switch to. Refusing every public destination on port 53 also refused
    /// whatever resolver a launcher or a school tool shipped with, and that looks like "the site I
    /// typed does not open".
    /// </summary>
    private static void RefusesKnownResolversOnlyForBlocklists()
    {
        var plan = WebsiteDnsLockdown.Build([IPAddress.Parse("192.168.10.1")],
            restrictAllPublicResolvers: false);
        Expect(plan.Error is null, $"a usable blacklist plan reported an error: {plan.Error}");
        foreach (var known in new[] { "8.8.8.8", "1.1.1.1", "223.5.5.5", "114.114.114.114" })
            Expect(Covered(plan.ResolverAddresses, IPAddress.Parse(known)),
                $"the well-known resolver {known} is still reachable on port 53.");
        foreach (var other in new[] { "210.2.4.8", "203.119.1.1", "192.168.10.1", "10.1.2.3" })
            Expect(!Covered(plan.ResolverAddresses, IPAddress.Parse(other)),
                $"{other} was refused although a blocklist does not need the blanket rule.");

        // Allowlist mode keeps the blanket rule: there the name answer is the enforcement.
        var strict = WebsiteDnsLockdown.Build([IPAddress.Parse("192.168.10.1")],
            restrictAllPublicResolvers: true);
        Expect(Covered(strict.ResolverAddresses, IPAddress.Parse("210.2.4.8")),
            "allowlist mode stopped refusing an arbitrary public resolver.");
    }

    private static void BuildsAWellFormedResolverProbe()
    {
        var query = WebsiteDnsLockdown.BuildQuery("a.example", 0x4A1B);
        Expect(query.Length == 12 + 1 + 1 + 7 + 1 + 1 + 4, "the probe query has an unexpected length.");
        Expect(query[0] == 0x4A && query[1] == 0x1B, "the probe query id was not written.");
        Expect(query[2] == 0x01, "the probe query did not ask for recursion.");
        Expect(query[5] == 0x01, "the probe query did not declare one question.");
        Expect(query[12] == 1 && query[13] == (byte)'a', "the first label was not encoded.");
        Expect(query[^4] == 0x00 && query[^3] == 0x01 && query[^1] == 0x01,
            "the question type or class is wrong.");
    }

    private static void KeepsIpv6ResolversOpen()
    {
        var server = IPAddress.Parse("2001:4860:4860::8888");
        var plan = WebsiteDnsLockdown.Build([IPAddress.Parse("192.168.10.1"), server]);
        Expect(plan.ResolverAddresses.Any(value => value.Contains(':', StringComparison.Ordinal)),
            "no IPv6 prefix was planned.");
        Expect(!Covered(plan.ResolverAddresses, server),
            "the configured IPv6 resolver was refused on port 53.");
        Expect(Covered(plan.ResolverAddresses, IPAddress.Parse("2606:4700:4700::1111")),
            "a public IPv6 resolver stayed open.");
    }

    private static void BlocksDohWithoutKnowingTheResolver()
    {
        // A machine with no readable DNS setting must not have port 53 blocked, but DoH can still
        // be refused: that costs nothing and closes the most common bypass.
        var plan = WebsiteDnsLockdown.Build([]);
        Expect(plan.ResolverAddresses.Count == 0, "port 53 was blocked without knowing the resolver.");
        Expect(plan.Error is { Length: > 0 }, "the missing resolver was not reported.");
        Expect(plan.DohEndpoints.Count > 0, "the DoH endpoints were not blocked.");
        Expect(plan.DohEndpoints.Contains("1.1.1.1") && plan.DohEndpoints.Contains("8.8.8.8"),
            "the well-known DoH endpoints are missing.");
    }

    private static void AppliesLockdownWithTheAddressBlock()
    {
        var backend = new WebsiteNetworkBlockChecksFakeBackend();
        var lockdown = WebsiteDnsLockdown.Build([IPAddress.Parse("192.168.10.1")]);
        var plan = new WebsiteNetworkBlockPlan(["93.184.216.34/32"], 1, [], 0, false, lockdown);

        var result = WebsiteNetworkBlock.Apply(9, plan, backend);
        Expect(result.Applied && result.Error is null, $"the apply failed: {result.Error}");
        Expect(result.ResolverLocked, "the result did not report the lockdown as applied.");
        Expect(result.ResolverCidrCount == lockdown.ResolverAddresses.Count,
            "the reported prefix count does not match the plan.");
        Expect(result.DohEndpointCount == lockdown.DohEndpoints.Count,
            "the reported DoH count does not match the plan.");
        Expect(backend.Resolvers.Count == lockdown.ResolverAddresses.Count,
            "the backend did not receive the resolver prefixes.");
        Expect(backend.Doh.Count == lockdown.DohEndpoints.Count,
            "the backend did not receive the DoH endpoints.");

        // An unchanged plan must not rewrite the firewall: a rewrite briefly opens a hole.
        var again = WebsiteNetworkBlock.Apply(10, plan, backend);
        Expect(again.Applied && backend.Writes == 1, "an unchanged lockdown rewrote the rules.");

        // The address list is empty in allowlist mode, but the lockdown alone must still be written.
        var allowlist = new WebsiteNetworkBlockPlan([], 0, [], 0, false, lockdown);
        var third = WebsiteNetworkBlock.Apply(11, allowlist, backend);
        Expect(third.Applied && backend.Written.Count == 0,
            "the address block survived a plan without addresses.");
        Expect(backend.Resolvers.Count > 0, "the lockdown was cleared along with the address block.");
    }

    private static void ReportsLockdownWriteDivergence()
    {
        var backend = new WebsiteNetworkBlockChecksFakeBackend { IgnoreWrites = true };
        var lockdown = WebsiteDnsLockdown.Build([IPAddress.Parse("192.168.10.1")]);
        var result = WebsiteNetworkBlock.Apply(12,
            new WebsiteNetworkBlockPlan(["93.184.216.34/32"], 1, [], 0, false, lockdown), backend);
        Expect(!result.Applied, "a discarded lockdown write was reported as applied.");
        Expect(result.Error is { Length: > 0 }, "the divergence was not described.");
    }

    /// <summary>True when any prefix in the list contains the address.</summary>
    private static bool Covered(IReadOnlyList<string> prefixes, IPAddress address)
    {
        foreach (var entry in prefixes)
        {
            var slash = entry.IndexOf('/');
            if (slash < 0) continue;
            var network = IPAddress.Parse(entry[..slash]);
            if (network.AddressFamily != address.AddressFamily) continue;
            if (!int.TryParse(entry[(slash + 1)..], out var prefix)) continue;
            if (network.AddressFamily == AddressFamily.InterNetwork)
            {
                if (prefix is < 0 or > 32) continue;
                var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
                if (((uint)ToUInt128(network) & mask) == ((uint)ToUInt128(address) & mask)) return true;
                continue;
            }
            if (prefix is < 0 or > 128) continue;
            var mask6 = prefix == 0 ? (UInt128)0 : UInt128.MaxValue << (128 - prefix);
            if ((ToUInt128(network) & mask6) == (ToUInt128(address) & mask6)) return true;
        }
        return false;
    }

    private static UInt128 ToUInt128(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        UInt128 value = 0;
        foreach (var b in bytes) value = (value << 8) | b;
        return value;
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>The backend shape the block checks use, duplicated here to keep the suites independent.</summary>
    private sealed class WebsiteNetworkBlockChecksFakeBackend : IWebsiteBlockRuleBackend
    {
        public List<string> Written { get; set; } = [];
        public List<string> Resolvers { get; set; } = [];
        public List<string> Doh { get; set; } = [];
        public int Writes { get; private set; }
        public bool IgnoreWrites { get; init; }

        public IReadOnlyList<string> ReadBlockedAddresses() =>
            Written.OrderBy(value => value, StringComparer.Ordinal).ToArray();

        public IReadOnlyList<string> ReadResolverLockCidrs() =>
            Resolvers.OrderBy(value => value, StringComparer.Ordinal).ToArray();

        public IReadOnlyList<string> ReadDohEndpoints() =>
            Doh.OrderBy(value => value, StringComparer.Ordinal).ToArray();

        public void Write(IReadOnlyList<string> addresses, IReadOnlyList<string> resolverLockCidrs,
            IReadOnlyList<string> dohEndpoints)
        {
            Writes++;
            if (IgnoreWrites) return;
            Written = addresses.ToList();
            Resolvers = resolverLockCidrs.ToList();
            Doh = dohEndpoints.ToList();
        }

        public void Clear()
        {
            Writes++;
            if (IgnoreWrites) return;
            Written.Clear();
            Resolvers.Clear();
            Doh.Clear();
        }

        public void Dispose() { }
    }
}
