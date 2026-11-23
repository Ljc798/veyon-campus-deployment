using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VeyonCampus.Core;

/// <summary>
/// Drops TCP sessions that are already talking to an address the policy now refuses.
///
/// A firewall rule decides whether a *new* connection may be made. Windows does not tear down
/// connections that were established before the rule appeared, and a video or game client that was
/// started earlier - a client that auto-starts with Windows, or one opened during the break - keeps
/// streaming over its existing session. The rule is correct, the policy is applied, and the site
/// still plays. Resetting those sessions is what makes "push now" take effect on a machine whose
/// application was already running.
///
/// Only sessions whose remote address is already in the blocked set are touched, so this cannot
/// reach anything the firewall would allow.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TcpConnectionReset
{
    /// <summary>MIB_TCP_STATE_DELETE_TCB. The documented way to ask Windows to drop a session.</summary>
    private const uint DeleteTcb = 12;

    /// <summary>
    /// Resets every IPv4 TCP session whose remote address falls inside one of these prefixes.
    /// Best effort: a session that closes between enumeration and reset simply fails to reset.
    /// </summary>
    /// <returns>How many sessions were dropped, for diagnostics.</returns>
    internal static int ResetTo(IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        var parsed = Parse(prefixes);
        if (parsed.Count == 0) return 0;

        var reset = 0;
        TcpConnectionInformation[] connections;
        try
        {
            connections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
        }
        catch (NetworkInformationException) { return 0; }
        catch (PlatformNotSupportedException) { return 0; }

        foreach (var connection in connections)
        {
            var remote = connection.RemoteEndPoint;
            // SetTcpEntry only understands IPv4 rows. IPv6 is worth nothing here anyway: a blocked
            // IPv6 destination is refused per packet, and there is no documented equivalent call.
            if (remote.AddressFamily != AddressFamily.InterNetwork) continue;
            if (!Covers(parsed, remote.Address)) continue;
            try
            {
                var row = new MibTcpRow
                {
                    State = DeleteTcb,
                    LocalAddress = ToNetworkAddress(connection.LocalEndPoint.Address),
                    LocalPort = ToNetworkPort(connection.LocalEndPoint.Port),
                    RemoteAddress = ToNetworkAddress(remote.Address),
                    RemotePort = ToNetworkPort(remote.Port)
                };
                if (SetTcpEntry(ref row) == 0) reset++;
            }
            catch (Exception exception) when (exception is SocketException or ArgumentException or
                                                  InvalidOperationException or NotSupportedException) { }
        }
        return reset;
    }

    /// <summary>
    /// True when the address is inside one of the prefixes. Split out so the match can be checked
    /// without a live connection table.
    /// </summary>
    internal static bool Covers(IReadOnlyList<(IPAddress Network, int Prefix)> prefixes, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        foreach (var (network, prefix) in prefixes)
        {
            if (network.AddressFamily != address.AddressFamily) continue;
            var candidate = network.GetAddressBytes();
            var full = prefix / 8;
            var partial = prefix % 8;
            var match = true;
            for (var index = 0; index < full; index++)
            {
                if (bytes[index] == candidate[index]) continue;
                match = false;
                break;
            }
            if (!match) continue;
            if (partial == 0) return true;
            var mask = (byte)(0xFF << (8 - partial));
            if ((bytes[full] & mask) == (candidate[full] & mask)) return true;
        }
        return false;
    }

    /// <summary>Parses <c>a.b.c.d/n</c> entries; anything else is ignored rather than guessed at.</summary>
    internal static IReadOnlyList<(IPAddress Network, int Prefix)> Parse(IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        var parsed = new List<(IPAddress, int)>(prefixes.Count);
        foreach (var entry in prefixes)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var slash = entry.IndexOf('/', StringComparison.Ordinal);
            var literal = slash < 0 ? entry : entry[..slash];
            if (!IPAddress.TryParse(literal, out var network)) continue;
            if (network.AddressFamily != AddressFamily.InterNetwork) continue;
            var prefix = network.GetAddressBytes().Length * 8;
            if (slash >= 0)
            {
                if (!int.TryParse(entry[(slash + 1)..], out var bits) || bits is < 0 or > 32) continue;
                prefix = bits;
            }
            parsed.Add((network, prefix));
        }
        return parsed;
    }

    private static uint ToNetworkAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)bytes[0] | (uint)bytes[1] << 8 | (uint)bytes[2] << 16 | (uint)bytes[3] << 24;
    }

    private static uint ToNetworkPort(int port) =>
        (uint)((port & 0xFF) << 8) | ((uint)port >> 8 & 0xFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetTcpEntry(ref MibTcpRow row);
}
