using System.Net;
using VeyonCampus.Core;

/// <summary>
/// Covers the part of the network layer that can be reasoned about without administrator rights:
/// which addresses may be blocked at all, what the planner does with shared and unresolvable names,
/// and whether a write that does not match the plan is reported instead of assumed to have worked.
/// Rule writing itself is the Windows Firewall COM API and is only exercised on a real machine.
/// </summary>
internal static class WebsiteNetworkBlockChecks
{
    public static void Run()
    {
        ClassifiesAddresses();
        BuildsPlanFromResolutions();
        DropsAddressesAnExemptDomainAlsoUses();
        StopsAtTheAddressCeiling();
        AppliesAndVerifiesRules();
        ReportsDivergenceInsteadOfTrustingTheWrite();
        ClearsWhenNothingIsBlocked();
        RecognisesOnlyItsOwnRules();
    }

    private static void ClassifiesAddresses()
    {
        foreach (var blocked in new[] { "93.184.216.34", "8.8.8.8", "2606:4700:4700::1111", "1.2.3.4" })
            Expect(WebsiteNetworkBlockPlanner.IsBlockableAddress(IPAddress.Parse(blocked)),
                $"{blocked} should be blockable.");

        foreach (var allowed in new[]
                 {
                     "127.0.0.1", "10.1.2.3", "172.16.0.1", "172.31.255.254", "192.168.1.10", "169.254.1.1",
                     "100.64.0.1", "0.0.0.0", "255.255.255.255", "224.0.0.1", "192.0.2.5", "198.51.100.7",
                     "203.0.113.9", "::1", "fe80::1", "fc00::1", "2001:db8::1", "ff02::1"
                 })
            Expect(!WebsiteNetworkBlockPlanner.IsBlockableAddress(IPAddress.Parse(allowed)),
                $"{allowed} must never be blocked.");

        // An IPv4-mapped IPv6 address is what a dual-stack resolver often returns for an A record;
        // it must be classified as the IPv4 address it wraps, not as a routable v6 address.
        Expect(!WebsiteNetworkBlockPlanner.IsBlockableAddress(IPAddress.Parse("::ffff:192.168.5.5")),
            "an IPv4-mapped private address was treated as public.");
        Expect(WebsiteNetworkBlockPlanner.ToCidr(IPAddress.Parse("::ffff:93.184.216.34")) == "93.184.216.34/32",
            "an IPv4-mapped public address was not reduced to IPv4.");
        Expect(WebsiteNetworkBlockPlanner.ToCidr(IPAddress.Parse("2606:4700::1")) == "2606:4700::1/128",
            "an IPv6 address was not given a /128 prefix.");
    }

    private static void BuildsPlanFromResolutions()
    {
        var resolver = new FakeResolver(new()
        {
            ["blocked.example"] = ["93.184.216.34", "93.184.216.35"],
            ["www.blocked.example"] = ["93.184.216.34"],
            ["offline.example"] = [],
            ["internal.example"] = ["10.0.0.8"]
        });
        var plan = WebsiteNetworkBlockPlanner.BuildAsync(
            ["blocked.example", "www.blocked.example", "offline.example", "internal.example"],
            [], resolver).GetAwaiter().GetResult();

        Expect(plan.Addresses.Count == 2, $"expected 2 deduplicated addresses, got {plan.Addresses.Count}.");
        Expect(plan.Addresses.Contains("93.184.216.34/32") && plan.Addresses.Contains("93.184.216.35/32"),
            "the plan dropped a resolved address.");
        Expect(plan.UnresolvedDomains.SequenceEqual(["offline.example"]),
            "an unresolvable domain was not reported.");
        Expect(plan.ResolvedDomainCount == 3, "the resolved-domain count is wrong.");
        Expect(!plan.Addresses.Any(value => value.StartsWith("10.", StringComparison.Ordinal)),
            "a private address reached the plan.");
        Expect(!plan.Truncated, "a small plan was reported as truncated.");
        Expect(plan.Addresses.SequenceEqual(plan.Addresses.OrderBy(value => value, StringComparer.Ordinal)),
            "the plan is not ordered, which makes the digest comparison unstable.");
    }

    private static void DropsAddressesAnExemptDomainAlsoUses()
    {
        // A blocked site and an allowed site behind the same CDN share the edge address. Blocking it
        // would take the allowed site down with the blocked one.
        var resolver = new FakeResolver(new()
        {
            ["blocked.example"] = ["93.184.216.37", "93.184.216.34", "93.184.216.36"],
            ["allowed.example"] = ["93.184.216.34"]
        });
        var plan = WebsiteNetworkBlockPlanner.BuildAsync(["blocked.example"], ["allowed.example"], resolver)
            .GetAwaiter().GetResult();

        Expect(plan.Addresses.Count == 2, $"expected the shared address to be dropped, got {plan.Addresses.Count}.");
        Expect(!plan.Addresses.Contains("93.184.216.34/32"), "the shared CDN address was blocked anyway.");
        Expect(plan.SharedAddressCount == 1, "the shared-address count was not reported.");
    }

    private static void StopsAtTheAddressCeiling()
    {
        var many = Enumerable.Range(1, WebsiteNetworkBlockPlanner.MaximumAddresses + 200)
            .Select(index => $"93.{index / 250}.{index % 250}.1").ToArray();
        var resolver = new FakeResolver(new() { ["huge.example"] = many });
        var plan = WebsiteNetworkBlockPlanner.BuildAsync(["huge.example"], [], resolver)
            .GetAwaiter().GetResult();
        Expect(plan.Addresses.Count == WebsiteNetworkBlockPlanner.MaximumAddresses,
            $"the plan did not stop at the ceiling: {plan.Addresses.Count}.");
        Expect(plan.Truncated, "the plan did not report truncation.");
    }

    private static void AppliesAndVerifiesRules()
    {
        var backend = new FakeBackend();
        var plan = new WebsiteNetworkBlockPlan(["1.1.1.1/32", "8.8.8.8/32"], 2, [], 0, false);

        var first = WebsiteNetworkBlock.Apply(5, plan, backend);
        Expect(first.Applied && first.Error is null, "the first apply failed.");
        Expect(first.AddressCount == 2, "the address count is wrong.");
        Expect(first.RuleCount == WindowsWebsiteBlockRuleBackend.CountRules(2),
            "the rule count does not match the chunking.");
        Expect(backend.Writes == 1, "the backend was not written once.");
        Expect(backend.Written.SequenceEqual(["1.1.1.1/32", "8.8.8.8/32"]), "the backend got the wrong addresses.");

        // Re-applying the same plan must not touch the firewall again: rewriting every ten minutes
        // would briefly open a hole and churn the firewall log.
        var second = WebsiteNetworkBlock.Apply(6, plan, backend);
        Expect(second.Applied && backend.Writes == 1, "an unchanged plan rewrote the rules.");

        var changed = WebsiteNetworkBlock.Apply(7,
            new WebsiteNetworkBlockPlan(["1.1.1.1/32"], 1, [], 0, false), backend);
        Expect(changed.Applied && backend.Writes == 2, "a changed plan did not rewrite the rules.");
        Expect(backend.Written.SequenceEqual(["1.1.1.1/32"]), "the stale address was kept.");
    }

    private static void ReportsDivergenceInsteadOfTrustingTheWrite()
    {
        var backend = new FakeBackend { IgnoreWrites = true };
        var result = WebsiteNetworkBlock.Apply(3,
            new WebsiteNetworkBlockPlan(["1.1.1.1/32"], 1, [], 0, false), backend);
        Expect(!result.Applied, "a backend that discarded the write was reported as applied.");
        Expect(result.Error is { Length: > 0 } && result.Error!.Contains("写回不符", StringComparison.Ordinal),
            "the divergence was not described.");
    }

    private static void ClearsWhenNothingIsBlocked()
    {
        var backend = new FakeBackend();
        WebsiteNetworkBlock.Apply(4, new WebsiteNetworkBlockPlan(["1.1.1.1/32"], 1, [], 0, false), backend);
        Expect(backend.Written.Count == 1, "the setup apply wrote nothing.");

        var cleared = WebsiteNetworkBlock.Apply(5, WebsiteNetworkBlockPlan.Empty, backend);
        Expect(cleared.Applied && cleared.AddressCount == 0, "the clear did not report success.");
        Expect(backend.Written.Count == 0, "the rules survived a plan with no addresses.");
    }

    private static void RecognisesOnlyItsOwnRules()
    {
        Expect(WindowsWebsiteBlockRuleBackend.IsOwnedRuleName("VeyonCampus Website Block 000"),
            "the rule prefix is not recognised.");
        Expect(WindowsWebsiteBlockRuleBackend.IsOwnedRuleName("VeyonCampus Encrypted DNS Block TCP"),
            "the encrypted-DNS rule is not recognised.");
        Expect(!WindowsWebsiteBlockRuleBackend.IsOwnedRuleName("VeyonCampus Website Policy Agent"),
            "the agent port rule was claimed as a block rule.");
        Expect(!WindowsWebsiteBlockRuleBackend.IsOwnedRuleName("Block Minecraft"),
            "an unrelated rule was claimed.");
    }

    private sealed class FakeResolver(Dictionary<string, string[]> map) : IWebsiteAddressResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(map.TryGetValue(host, out var values)
                ? values.Select(IPAddress.Parse).ToArray()
                : Array.Empty<IPAddress>());
    }

    private sealed class FakeBackend : IWebsiteBlockRuleBackend
    {
        public List<string> Written { get; set; } = [];
        public int Writes { get; private set; }
        public bool IgnoreWrites { get; init; }

        public IReadOnlyList<string> ReadBlockedAddresses() =>
            Written.OrderBy(value => value, StringComparer.Ordinal).ToArray();

        public void Write(IReadOnlyList<string> addresses)
        {
            Writes++;
            if (!IgnoreWrites) Written = addresses.ToList();
        }

        public void Clear()
        {
            Writes++;
            if (!IgnoreWrites) Written.Clear();
        }

        public void Dispose() { }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
