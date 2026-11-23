using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace VeyonCampus.Core;

/// <summary>Resolves a hostname to the addresses a client on this machine would connect to.</summary>
public interface IWebsiteAddressResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Uses the machine resolver, which is also the one the hosts block steers.</summary>
public sealed class SystemWebsiteAddressResolver : IWebsiteAddressResolver
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
            return addresses;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or
                                              ArgumentException or NotSupportedException)
        {
            // An unresolvable name is a normal outcome: the domain may be typo'd, blocked upstream,
            // or only served over a name we cannot see from here. It is reported, not thrown.
            return Array.Empty<IPAddress>();
        }
    }
}

/// <summary>
/// The addresses a blocklist turn into, plus what could not be turned into anything.
/// </summary>
public sealed record WebsiteNetworkBlockPlan(
    IReadOnlyList<string> Addresses,
    int ResolvedDomainCount,
    IReadOnlyList<string> UnresolvedDomains,
    int SharedAddressCount,
    bool Truncated,
    WebsiteDnsLockdownPlan? DnsLockdown = null)
{
    public static WebsiteNetworkBlockPlan Empty { get; } =
        new([], 0, [], 0, false);
}

/// <summary>
/// Turns blocked domain names into the concrete addresses that outbound filtering can act on.
///
/// Hosts rewriting and browser policy are both name-level and both only reach software that asks
/// the system resolver or reads machine policy. A firewall rule is the only layer that also covers
/// a page reached by literal IP and a game client with its own DNS. The cost is that addresses are
/// shared: a blocked site behind a CDN can sit next to a site the school needs, so an address that
/// any exempt domain also resolves to is never blocked.
/// </summary>
public static class WebsiteNetworkBlockPlanner
{
    /// <summary>
    /// Windows accepts a long remote-address list but the rules become slow to evaluate, and a
    /// thousand-domain blocklist resolving to four addresses each is already an unusual case.
    /// </summary>
    public const int MaximumAddresses = 4096;

    private const int MaximumParallelResolutions = 16;

    public static async Task<WebsiteNetworkBlockPlan> BuildAsync(
        IReadOnlyList<string> blockedDomains,
        IReadOnlyList<string> exemptDomains,
        IWebsiteAddressResolver resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blockedDomains);
        ArgumentNullException.ThrowIfNull(exemptDomains);
        ArgumentNullException.ThrowIfNull(resolver);

        var blocked = await ResolveAllAsync(blockedDomains, resolver, cancellationToken).ConfigureAwait(false);
        var exempt = await ResolveAllAsync(exemptDomains, resolver, cancellationToken).ConfigureAwait(false);

        var exemptAddresses = new HashSet<IPAddress>(exempt.SelectMany(pair => pair.Value));
        var addresses = new SortedSet<string>(StringComparer.Ordinal);
        var shared = new HashSet<IPAddress>();
        var unresolved = new List<string>();
        foreach (var (domain, resolved) in blocked)
        {
            if (resolved.Count == 0)
            {
                unresolved.Add(domain);
                continue;
            }
            foreach (var address in resolved)
            {
                if (!IsBlockableAddress(address)) continue;
                if (exemptAddresses.Contains(address))
                {
                    shared.Add(address);
                    continue;
                }
                addresses.Add(ToCidr(address));
                if (addresses.Count >= MaximumAddresses) break;
            }
            if (addresses.Count >= MaximumAddresses) break;
        }

        var truncated = addresses.Count >= MaximumAddresses;
        unresolved.Sort(StringComparer.Ordinal);
        return new WebsiteNetworkBlockPlan(Array.AsReadOnly(addresses.ToArray()),
            blocked.Count(pair => pair.Value.Count > 0), Array.AsReadOnly(unresolved.ToArray()),
            shared.Count, truncated);
    }

    private static async Task<List<KeyValuePair<string, IReadOnlyList<IPAddress>>>> ResolveAllAsync(
        IReadOnlyList<string> domains, IWebsiteAddressResolver resolver, CancellationToken cancellationToken)
    {
        var distinct = domains.Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new IReadOnlyList<IPAddress>[distinct.Length];
        using var throttle = new SemaphoreSlim(MaximumParallelResolutions, MaximumParallelResolutions);
        var tasks = distinct.Select(async (domain, index) =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await resolver.ResolveAsync(domain, cancellationToken).ConfigureAwait(false);
            }
            finally { throttle.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return distinct.Select((domain, index) => new KeyValuePair<string, IReadOnlyList<IPAddress>>(
            domain, results[index] ?? Array.Empty<IPAddress>())).ToList();
    }

    /// <summary>
    /// Drops everything a school machine either cannot reach through the internet or must not lose.
    /// Blocking a private address would break the classroom LAN, so these are refused before the
    /// plan is built rather than filtered out by the firewall.
    /// </summary>
    public static bool IsBlockableAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.Equals(IPAddress.Broadcast)) return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return false;                       // fc00::/7 unique local
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) return false; // 2001:db8::/32
            return true;
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 or 10 or 127 => false,                                          // this network, private, loopback
            100 when octets[1] is >= 64 and <= 127 => false,                  // 100.64/10 carrier NAT
            169 when octets[1] == 254 => false,                               // link local
            172 when octets[1] is >= 16 and <= 31 => false,                   // private
            192 when octets[1] == 168 => false,                               // private
            192 when octets[1] == 0 && octets[2] is 0 or 2 => false,          // 192.0.0/24, 192.0.2/24 documentation
            198 when octets[1] is 18 or 19 => false,                          // benchmarking
            198 when octets[1] == 51 && octets[2] == 100 => false,            // documentation
            203 when octets[1] == 0 && octets[2] == 113 => false,             // documentation
            >= 224 => false,                                                  // multicast, reserved, broadcast
            _ => true
        };
    }

    public static string ToCidr(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString() + (address.AddressFamily == AddressFamily.InterNetworkV6 ? "/128" : "/32");
    }
}

/// <summary>What the network layer ended up enforcing, or why it could not.</summary>
/// <remarks>
/// The resolver fields are reported separately from the address block because they answer a
/// different question. An address block can be complete and still be bypassed by a browser that
/// uses its own DNS; only the resolver lockdown closes that, so the teacher has to be able to see
/// whether it is in force rather than infer it from "the site opened anyway".
/// </remarks>
public sealed record WebsiteNetworkBlockResult(bool Applied, long Revision, int AddressCount, int RuleCount,
    string? Error, bool ResolverLocked = false, int ResolverCidrCount = 0, int DohEndpointCount = 0,
    IReadOnlyList<string>? AllowedServers = null, string? ResolverLockError = null)
{
    public static WebsiteNetworkBlockResult Failed(long revision, string error) =>
        new(false, revision, 0, 0, error);
}

/// <summary>
/// How outbound block rules are read and written. The Windows implementation talks to the firewall
/// COM API; tests substitute an in-memory one so the plan and the verification are covered without
/// administrator rights.
/// </summary>
internal interface IWebsiteBlockRuleBackend : IDisposable
{
    /// <summary>Every address currently blocked by a rule this product owns.</summary>
    IReadOnlyList<string> ReadBlockedAddresses();

    /// <summary>Destination prefixes currently refused on port 53 by this product.</summary>
    IReadOnlyList<string> ReadResolverLockCidrs();

    /// <summary>Endpoints currently refused on port 443 because they serve DNS over HTTPS.</summary>
    IReadOnlyList<string> ReadDohEndpoints();

    /// <summary>
    /// Replaces this product's rules with rules covering exactly these addresses, plus the resolver
    /// lockdown. Both are written in one pass so the firewall never contains a half-applied policy.
    /// </summary>
    void Write(IReadOnlyList<string> addresses, IReadOnlyList<string> resolverLockCidrs,
        IReadOnlyList<string> dohEndpoints);

    /// <summary>Removes this product's rules and leaves every other rule alone.</summary>
    void Clear();
}

/// <summary>
/// Enforces a blocklist at the packet layer by refusing outbound connections to the addresses the
/// blocked domains resolve to. Nothing here decrypts traffic and nothing inspects content: the rule
/// matches a destination, so it applies to every process regardless of which browser or client it is.
/// </summary>
public static class WebsiteNetworkBlock
{
    public static WebsiteNetworkBlockResult Apply(long revision, WebsiteNetworkBlockPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!OperatingSystem.IsWindows())
            return WebsiteNetworkBlockResult.Failed(revision, "网络层封禁仅在 Windows 上可用。");
        try
        {
            using var backend = new WindowsWebsiteBlockRuleBackend();
            var result = Apply(revision, plan, backend);
            // A rule only decides whether a new connection may start. A client that was already
            // playing - auto-started with Windows, or opened during the break - keeps its established
            // session and the site still works, which looks exactly like "the push did nothing".
            // Drop those sessions so the policy bites on the machine as it is right now.
            if (result.Applied)
            {
                if (plan.Addresses.Count > 0)
                {
                    TcpConnectionReset.ResetTo(plan.Addresses);
                    // UDP has no session to reset, and Windows keeps forwarding an established UDP
                    // flow while its state still matches the rule. A /32 route takes the address out
                    // of every send path immediately, which is what stops a running QUIC video.
                    BlockedAddressRoute.Install(plan.Addresses);
                }
                else
                {
                    BlockedAddressRoute.RemoveAll();
                }
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidOperationException or System.Security.SecurityException or
                                              System.Runtime.InteropServices.COMException)
        {
            return WebsiteNetworkBlockResult.Failed(revision, exception.Message);
        }
    }

    internal static WebsiteNetworkBlockResult Apply(long revision, WebsiteNetworkBlockPlan plan,
        IWebsiteBlockRuleBackend backend)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(backend);
        var desired = Normalize(plan.Addresses);
        var resolverCidrs = Normalize(plan.DnsLockdown?.ResolverAddresses);
        var dohEndpoints = Normalize(plan.DnsLockdown?.DohEndpoints);

        if (desired.Length == 0 && resolverCidrs.Length == 0 && dohEndpoints.Length == 0)
        {
            backend.Clear();
            return new WebsiteNetworkBlockResult(true, revision, 0, 0, null);
        }

        var current = Normalize(backend.ReadBlockedAddresses());
        var currentResolvers = Normalize(backend.ReadResolverLockCidrs());
        var currentDoh = Normalize(backend.ReadDohEndpoints());
        if (!current.SequenceEqual(desired, StringComparer.OrdinalIgnoreCase) ||
            !currentResolvers.SequenceEqual(resolverCidrs, StringComparer.OrdinalIgnoreCase) ||
            !currentDoh.SequenceEqual(dohEndpoints, StringComparer.OrdinalIgnoreCase))
        {
            backend.Write(desired, resolverCidrs, dohEndpoints);
            current = Normalize(backend.ReadBlockedAddresses());
            currentResolvers = Normalize(backend.ReadResolverLockCidrs());
            currentDoh = Normalize(backend.ReadDohEndpoints());
        }

        if (!current.SequenceEqual(desired, StringComparer.OrdinalIgnoreCase))
            return WebsiteNetworkBlockResult.Failed(revision,
                $"网络层封禁写回不符：期望 {desired.Length} 个地址，实际 {current.Length} 个。");
        if (!currentResolvers.SequenceEqual(resolverCidrs, StringComparer.OrdinalIgnoreCase))
            return WebsiteNetworkBlockResult.Failed(revision,
                $"DNS 强制写回不符：期望 {resolverCidrs.Length} 段，实际 {currentResolvers.Length} 段。");
        if (!currentDoh.SequenceEqual(dohEndpoints, StringComparer.OrdinalIgnoreCase))
            return WebsiteNetworkBlockResult.Failed(revision,
                $"DoH 封锁写回不符：期望 {dohEndpoints.Length} 个端点，实际 {currentDoh.Length} 个。");

        return new WebsiteNetworkBlockResult(true, revision, desired.Length,
            WindowsWebsiteBlockRuleBackend.CountRules(desired.Length, resolverCidrs.Length, dohEndpoints.Length),
            null, resolverCidrs.Length > 0, resolverCidrs.Length, dohEndpoints.Length,
            plan.DnsLockdown?.AllowedServers, plan.DnsLockdown?.Error);
    }

    private static string[] Normalize(IReadOnlyList<string>? values) =>
        (values ?? Array.Empty<string>())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

    public static WebsiteNetworkBlockResult Clear()
    {
        if (!OperatingSystem.IsWindows())
            return WebsiteNetworkBlockResult.Failed(0, "网络层封禁仅在 Windows 上可用。");
        try
        {
            using var backend = new WindowsWebsiteBlockRuleBackend();
            backend.Clear();
            // Routes outlive the rules they accompanied: a machine that was restarted mid-lesson, or
            // an Agent that stopped before cleanup, would otherwise keep those addresses unreachable
            // long after the lesson ended.
            BlockedAddressRoute.RemoveAll();
            var remaining = backend.ReadBlockedAddresses();
            return remaining.Count == 0
                ? new WebsiteNetworkBlockResult(true, 0, 0, 0, null)
                : WebsiteNetworkBlockResult.Failed(0, $"网络层封禁规则删除后仍有 {remaining.Count} 个地址。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidOperationException or System.Security.SecurityException or
                                              System.Runtime.InteropServices.COMException)
        {
            return WebsiteNetworkBlockResult.Failed(0, exception.Message);
        }
    }

    public static bool HasRules()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var backend = new WindowsWebsiteBlockRuleBackend();
            return backend.ReadBlockedAddresses().Count > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidOperationException or System.Security.SecurityException or
                                              System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>
    /// Confirms the rules actually stop traffic by opening a connection to a few blocked addresses.
    ///
    /// Reading the rule back only proves the firewall accepted the configuration. Whether the
    /// machine then drops the connection depends on the profile, on a third-party firewall that may
    /// sit above it, and on the rule direction. A successful connection is unambiguous: traffic
    /// still flows, so the block is not in force. A failed one is weaker evidence, because the host
    /// may simply be down, which is why only success is reported.
    /// </summary>
    public static async Task<IReadOnlyList<string>> VerifyEnforcementAsync(
        IReadOnlyList<string> addresses, int probes = 3, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var reachable = new List<string>();
        foreach (var entry in addresses.Where(value => value.Contains('/'))
                     .OrderBy(value => value.Contains(':', StringComparison.Ordinal))
                     .Take(Math.Max(0, probes)))
        {
            var literal = entry[..entry.IndexOf('/', StringComparison.Ordinal)];
            if (!IPAddress.TryParse(literal, out var address)) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            using var client = new TcpClient(address.AddressFamily);
            try
            {
                await client.ConnectAsync(address, 443, timeout.Token).ConfigureAwait(false);
                if (client.Connected) reachable.Add(literal);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException or
                                                  ObjectDisposedException)
            {
                // Refused, unreachable or timed out: consistent with a working block.
            }
        }
        return reachable;
    }
}

/// <summary>
/// Writes outbound block rules through the Windows Firewall COM API. Outbound rather than inbound
/// because the destination is what is known; the school machine has no fixed address to protect.
/// Rules are named and described with a product marker so a later run can tell its own rules from
/// a technician's hand-written rule that happens to share a name.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWebsiteBlockRuleBackend : IWebsiteBlockRuleBackend
{
    internal const string RuleNamePrefix = "VeyonCampus Website Block";
    internal const string EncryptedDnsRulePrefix = "VeyonCampus Encrypted DNS Block";
    internal const string ResolverLockRulePrefix = "VeyonCampus Resolver Lock";
    internal const string DohBlockRulePrefix = "VeyonCampus DoH Block";
    internal const string Marker = "VeyonCampus Website Block v1";

    /// <summary>Addresses per rule. Long lists make every connection's classification slower.</summary>
    internal const int AddressesPerRule = 64;

    /// <summary>Prefixes per rule. A prefix list is cheaper per entry than an address list.</summary>
    internal const int CidrsPerRule = 32;

    private readonly dynamic _policy;

    public WindowsWebsiteBlockRuleBackend()
    {
        _policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                                           ?? throw new InvalidOperationException("本机没有 Windows 防火墙组件。"))!;
    }

    public IReadOnlyList<string> ReadBlockedAddresses() => ReadRemoteList(RuleNamePrefix);

    public IReadOnlyList<string> ReadResolverLockCidrs() => ReadRemoteList(ResolverLockRulePrefix);

    public IReadOnlyList<string> ReadDohEndpoints() => ReadRemoteList(DohBlockRulePrefix);

    public void Write(IReadOnlyList<string> addresses, IReadOnlyList<string> resolverLockCidrs,
        IReadOnlyList<string> dohEndpoints)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(resolverLockCidrs);
        ArgumentNullException.ThrowIfNull(dohEndpoints);
        Clear();
        WriteAddressRules(addresses);
        // DoT and DoQ share 853. Browsers that turn on encrypted DNS ask a resolver of their own and
        // never see the hosts block, which would make the name-level layers above pointless. Two
        // rules rather than one any-protocol rule: the firewall API only accepts ports on a
        // concrete protocol, and silently drops the port otherwise.
        AddRule(EncryptedDnsRulePrefix + " TCP", remoteAddresses: null, remotePorts: "853", protocol: 6);
        AddRule(EncryptedDnsRulePrefix + " UDP", remoteAddresses: null, remotePorts: "853", protocol: 17);
        // Port 53 to public resolvers, minus the ones this machine is configured with. Port 443 to
        // known DoH endpoints, because DoH hides inside HTTPS and cannot be caught by port alone.
        WritePortRules(ResolverLockRulePrefix, resolverLockCidrs, "53");
        WritePortRules(DohBlockRulePrefix, dohEndpoints, "443");
    }

    public void Clear()
    {
        var names = OwnedRuleNames();
        foreach (var name in names) _policy.Rules.Remove(name);
        var remaining = OwnedRuleNames();
        if (remaining.Count != 0)
            throw new IOException($"网络层封禁规则删除后仍存在 {remaining.Count} 条；请检查 Windows 防火墙。");
    }

    public void Dispose()
    {
        // The policy object is an unmanaged COM instance; releasing it lets the service host shut down
        // cleanly instead of holding a firewall handle for the lifetime of the process.
        if (System.Runtime.InteropServices.Marshal.IsComObject(_policy))
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_policy);
    }

    /// <summary>Address rules, the two encrypted-DNS rules and the port rules; used only for reporting.</summary>
    internal static int CountRules(int addressCount, int resolverCidrCount = 0, int dohEndpointCount = 0)
    {
        if (addressCount == 0 && resolverCidrCount == 0 && dohEndpointCount == 0) return 0;
        var addressRules = addressCount == 0
            ? 0
            : (int)Math.Ceiling(addressCount / (double)AddressesPerRule);
        return addressRules + 2 + CountPortRules(resolverCidrCount) + CountPortRules(dohEndpointCount);
    }

    private static int CountPortRules(int cidrCount) =>
        cidrCount == 0 ? 0 : (int)Math.Ceiling(cidrCount / (double)CidrsPerRule) * 2;

    private void WriteAddressRules(IReadOnlyList<string> addresses)
    {
        var v4 = addresses.Where(value => !value.Contains(':', StringComparison.Ordinal)).ToArray();
        var v6 = addresses.Where(value => value.Contains(':', StringComparison.Ordinal)).ToArray();
        var index = 0;
        foreach (var family in new[] { v4, v6 })
        {
            for (var offset = 0; offset < family.Length; offset += AddressesPerRule)
            {
                var chunk = family.Skip(offset).Take(AddressesPerRule).ToArray();
                AddRule($"{RuleNamePrefix} {index:D3}", remoteAddresses: string.Join(',', chunk),
                    remotePorts: null, protocol: 256);
                index++;
            }
        }
    }

    private void WritePortRules(string prefix, IReadOnlyList<string> cidrs, string port)
    {
        if (cidrs.Count == 0) return;
        var v4 = cidrs.Where(value => !value.Contains(':', StringComparison.Ordinal)).ToArray();
        var v6 = cidrs.Where(value => value.Contains(':', StringComparison.Ordinal)).ToArray();
        var index = 0;
        foreach (var (family, label) in new[] { (v4, "v4"), (v6, "v6") })
        {
            for (var offset = 0; offset < family.Length; offset += CidrsPerRule)
            {
                var chunk = string.Join(',', family.Skip(offset).Take(CidrsPerRule));
                AddRule($"{prefix} {label} TCP {index:D3}", chunk, port, protocol: 6);
                AddRule($"{prefix} {label} UDP {index:D3}", chunk, port, protocol: 17);
                index++;
            }
        }
    }

    private IReadOnlyList<string> ReadRemoteList(string prefix)
    {
        var values = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rule in RulesWithPrefix(prefix))
        {
            var remote = (string)((dynamic)rule).RemoteAddresses;
            if (string.IsNullOrWhiteSpace(remote) || remote == "*") continue;
            foreach (var entry in remote.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                     StringSplitOptions.TrimEntries))
                values.Add(entry);
        }
        return values.ToArray();
    }

    private void AddRule(string name, string? remoteAddresses, string? remotePorts, int protocol)
    {
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
        rule.Name = name;
        rule.Description = Marker;
        rule.Direction = 2;      // outbound
        rule.Action = 0;         // block
        rule.Protocol = protocol;
        rule.Profiles = int.MaxValue;
        rule.Enabled = true;
        if (remoteAddresses is not null) rule.RemoteAddresses = remoteAddresses;
        if (remotePorts is not null) rule.RemotePorts = remotePorts;
        _policy.Rules.Add(rule);
    }

    private List<object> RulesWithPrefix(string prefix)
    {
        var rules = new List<object>();
        foreach (dynamic rule in _policy.Rules)
        {
            var name = (string)rule.Name;
            if (!name.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)) continue;
            // A rule with our name but someone else's description is not ours; refuse rather than
            // deleting something a technician created.
            if (!string.Equals((string)rule.Description, Marker, StringComparison.Ordinal))
                throw new IOException($"防火墙规则“{name}”与本工具的标记不符；没有修改该规则。");
            rules.Add(rule);
        }
        return rules;
    }

    private List<object> OwnedRules()
    {
        var rules = new List<object>();
        foreach (dynamic rule in _policy.Rules)
        {
            var name = (string)rule.Name;
            if (!IsOwnedRuleName(name)) continue;
            if (!string.Equals((string)rule.Description, Marker, StringComparison.Ordinal))
                throw new IOException($"防火墙规则“{name}”与本工具的标记不符；没有修改该规则。");
            rules.Add(rule);
        }
        return rules;
    }

    private List<string> OwnedRuleNames() => OwnedRules().Select(rule => (string)((dynamic)rule).Name).ToList();

    internal static bool IsOwnedRuleName(string name) =>
        name.StartsWith(RuleNamePrefix + " ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(EncryptedDnsRulePrefix + " ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(ResolverLockRulePrefix + " ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(DohBlockRulePrefix + " ", StringComparison.OrdinalIgnoreCase);
}
