using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace VeyonCampus.Core;

/// <summary>
/// Takes blocked addresses out of the send path, including for flows that were already running.
///
/// A firewall rule stops new connections, and <see cref="TcpConnectionReset"/> drops established
/// TCP sessions. Neither reaches UDP: there is no session to reset, and Windows keeps forwarding an
/// established UDP flow while its state still matches the rule. That is how a running QUIC video
/// keeps playing after the policy lands, and why the site only stops once the application is
/// restarted. A /32 route with an on-link next hop makes the very next packet undeliverable, so the
/// client fails at once instead of streaming until somebody closes it.
///
/// The routes are bookkeeping the product owns: they are recorded before they are written and
/// removed on every policy change and on uninstall, so a blocked address never stays unreachable
/// after the lesson ends.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BlockedAddressRoute
{
    private const string StateKey = @"SOFTWARE\VeyonCampus\WebsitePolicy";
    private const string StateValue = "BlockedAddressRoutes";

    /// <summary>Enough for a large blocklist; a runaway list must not fill the routing table.</summary>
    private const int MaximumRoutes = 256;

    private const uint RouteProtocolNetMgmt = 3;
    private const uint ErrorObjectAlreadyExists = 5010;
    private const uint ErrorNotFound = 1168;

    /// <summary>
    /// Points every blocked host address at an unreachable next hop. Best effort: a route that the
    /// system refuses is skipped, and the firewall rule still applies to anything new.
    /// </summary>
    /// <returns>How many routes are in force afterwards, for diagnostics.</returns>
    /// <summary>Last failure reported by Windows while installing, for the diagnostic fixture.</summary>
    internal static uint LastFailureStatus { get; private set; }

    internal static int Install(IReadOnlyList<string> cidrs)
    {
        ArgumentNullException.ThrowIfNull(cidrs);
        LastFailureStatus = 0;
        using var key = OpenStateKey(writable: true);
        // Without somewhere to record them, routes could never be removed again; refuse rather than
        // leave an address permanently unreachable.
        if (key is null)
        {
            LastFailureStatus = 1;
            return 0;
        }

        var desired = new List<(IPAddress Address, uint Index)>();
        foreach (var cidr in cidrs)
        {
            if (desired.Count >= MaximumRoutes) break;
            if (!TryParseHost(cidr, out var address)) continue;
            var best = GetBestInterface(ToNetwork(address), out var index);
            if (best != 0 || index == 0)
            {
                LastFailureStatus = best == 0 ? 2 : 1000 + best;
                continue;
            }
            desired.Add((address, index));
        }
        var entries = desired.Select(item => $"{item.Address}:{item.Index}").ToList();
        // Every push re-applies the same plan. Rebuilding identical routes each time would tear down
        // working ones for a moment, and a teacher watching the class would see the block blink.
        var stored = ReadState(key);
        if (stored.Count == entries.Count &&
            stored.ToHashSet(StringComparer.Ordinal).SetEquals(entries) &&
            desired.All(item => RouteExists(item.Address, item.Index)))
            return entries.Count;

        RemoveRecorded(key);
        var installed = new List<string>();
        foreach (var (address, index) in desired)
        {
            var status = AddRoute(address, index);
            if (status is not (0 or ErrorObjectAlreadyExists))
            {
                LastFailureStatus = status;
                continue;
            }
            installed.Add($"{address}:{index}");
        }
        if (!WriteState(key, installed))
        {
            foreach (var entry in installed) RemoveEntry(entry);
            WriteState(key, []);
            return 0;
        }
        return installed.Count;
    }

    /// <summary>True when the route is still in the table; a student can delete one by hand.</summary>
    private static bool RouteExists(IPAddress address, uint index)
    {
        var row = HostRouteRow(address, index);
        return GetIpForwardEntry2(ref row) == 0;
    }

    /// <summary>Removes every route this product recorded. Safe to call when none exist.</summary>
    /// <returns>How many routes were removed.</returns>
    internal static int RemoveAll()
    {
        using var key = OpenStateKey(writable: true);
        return key is null ? 0 : RemoveRecorded(key);
    }

    private static int RemoveRecorded(RegistryKey key)
    {
        var stored = ReadState(key);
        if (stored.Count == 0) return 0;
        var removed = 0;
        foreach (var entry in stored)
            if (RemoveEntry(entry)) removed++;
        WriteState(key, []);
        return removed;
    }

    private static bool RemoveEntry(string entry)
    {
        // Entries are "a.b.c.d:index"; only IPv4 is recorded, so the last colon is the separator.
        var separator = entry.LastIndexOf(':');
        if (separator <= 0) return false;
        if (!IPAddress.TryParse(entry[..separator], out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (!uint.TryParse(entry[(separator + 1)..], out var index) || index == 0) return false;
        var status = RemoveRoute(address, index);
        return status is 0 or ErrorNotFound;
    }

    /// <summary>Host routes only: a wider prefix would take a whole range offline.</summary>
    internal static bool TryParseHost(string entry, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(entry)) return false;
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        var literal = slash < 0 ? entry : entry[..slash];
        if (slash >= 0 && (!int.TryParse(entry[(slash + 1)..], out var bits) || bits != 32)) return false;
        if (!IPAddress.TryParse(literal, out var parsed)) return false;
        if (parsed.AddressFamily != AddressFamily.InterNetwork) return false;
        address = parsed;
        return true;
    }

    private static uint AddRoute(IPAddress address, uint index)
    {
        var row = HostRouteRow(address, index);
        return CreateIpForwardEntry2(ref row);
    }

    private static uint RemoveRoute(IPAddress address, uint index)
    {
        var row = HostRouteRow(address, index);
        return DeleteIpForwardEntry2(ref row);
    }

    /// <summary>
    /// An on-link /32: the address is handed to the outgoing interface, which has no neighbour for
    /// it, so the packet is dropped instead of being delivered anywhere.
    /// </summary>
    private static MibIpForwardRow2 HostRouteRow(IPAddress address, uint index)
    {
        var row = default(MibIpForwardRow2);
        InitializeIpForwardEntry(ref row);
        row.InterfaceIndex = index;
        row.DestinationPrefix.Prefix.Family = (ushort)AddressFamily.InterNetwork;
        row.DestinationPrefix.Prefix.Address = ToNetwork(address);
        row.DestinationPrefix.PrefixLength = 32;
        row.Metric = 1;
        row.Protocol = (int)RouteProtocolNetMgmt;
        row.ValidLifetime = uint.MaxValue;
        row.PreferredLifetime = uint.MaxValue;
        row.Origin = 0;
        return row;
    }

    private static uint ToNetwork(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)bytes[0] | (uint)bytes[1] << 8 | (uint)bytes[2] << 16 | (uint)bytes[3] << 24;
    }

    private static List<string> ReadState(RegistryKey key)
    {
        try
        {
            if (key.GetValue(StateValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw)
                return [];
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException)
        {
            return [];
        }
    }

    private static bool WriteState(RegistryKey key, IReadOnlyList<string> entries)
    {
        try
        {
            if (entries.Count == 0)
                key.DeleteValue(StateValue, throwOnMissingValue: false);
            else
                key.SetValue(StateValue, string.Join(',', entries), RegistryValueKind.String);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException)
        {
            return false;
        }
    }

    private static RegistryKey? OpenStateKey(bool writable)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return writable
                ? root.CreateSubKey(StateKey, writable: true)
                : root.OpenSubKey(StateKey, writable: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// SOCKADDR_INET: a fixed 28-byte union. Only the IPv4 fields are filled in; the tail keeps the
    /// structure the size the routing table expects.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrInet
    {
        public ushort Family;
        public ushort Port;
        public uint Address;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;
        public uint Padding3;
        public uint Padding4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IpAddressPrefix
    {
        public SockaddrInet Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IpAddressPrefix DestinationPrefix;
        public SockaddrInet NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public int Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public int Origin;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint destination, out uint bestInterfaceIndex);

    [DllImport("iphlpapi.dll")]
    private static extern void InitializeIpForwardEntry(ref MibIpForwardRow2 route);

    [DllImport("iphlpapi.dll")]
    private static extern uint CreateIpForwardEntry2(ref MibIpForwardRow2 route);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpForwardEntry2(ref MibIpForwardRow2 route);

    [DllImport("iphlpapi.dll")]
    private static extern uint DeleteIpForwardEntry2(ref MibIpForwardRow2 route);
}
