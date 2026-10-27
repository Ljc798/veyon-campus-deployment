using System.Net;
using System.Net.NetworkInformation;

namespace VeyonCampus.App;

internal interface ITeacherMobileControlIdentityProvider
{
    MobileControlTlsIdentity CreateIdentity();
    IPAddress SelectPreferredLanAddress(IReadOnlyList<IPAddress> candidates);
}

internal static class TeacherMobileControlIdentityProviderFactory
{
    public static ITeacherMobileControlIdentityProvider CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return new WindowsTeacherMobileControlIdentityProvider();
        if (OperatingSystem.IsMacOS()) return new MacTeacherMobileControlIdentityProvider();
        return new UnsupportedTeacherMobileControlIdentityProvider();
    }
}

internal abstract class TeacherMobileControlIdentityProviderBase : ITeacherMobileControlIdentityProvider
{
    protected abstract bool RestrictToEthernetAndWifi { get; }
    protected abstract string MissingAddressMessage { get; }

    public MobileControlTlsIdentity CreateIdentity()
    {
        var addresses = ReadPrivateAddresses();
        return MobileControlTlsIdentityStore.Create(addresses);
    }

    public IPAddress SelectPreferredLanAddress(IReadOnlyList<IPAddress> candidates)
    {
        var candidateSet = candidates.ToHashSet();
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or
                                  NetworkInterfaceType.Wireless80211)
            .Select(adapter => new
            {
                Adapter = adapter,
                Properties = adapter.GetIPProperties(),
                Addresses = adapter.GetIPProperties().UnicastAddresses
                    .Where(item => candidateSet.Contains(item.Address)).Select(item => item.Address).ToArray()
            })
            .Where(item => item.Addresses.Length > 0)
            .ToArray();
        var defaultRoute = adapters
            .Where(item => item.Properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any)))
            .OrderByDescending(item => item.Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .FirstOrDefault();
        return defaultRoute?.Addresses[0] ?? candidates.FirstOrDefault() ??
            throw new InvalidOperationException("当前没有可用于手机连接的教师 LAN 地址。");
    }

    private IReadOnlyList<IPAddress> ReadPrivateAddresses()
    {
        var addresses = new SortedSet<IPAddress>(Comparer<IPAddress>.Create((left, right) =>
            StringComparer.Ordinal.Compare(left.ToString(), right.ToString())));
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (RestrictToEthernetAndWifi &&
                adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
                continue;
            foreach (var item in adapter.GetIPProperties().UnicastAddresses)
                if (MobileControlTlsIdentityStore.IsPrivateIpv4(item.Address)) addresses.Add(item.Address);
        }
        if (addresses.Count == 0) throw new InvalidOperationException(MissingAddressMessage);
        if (addresses.Count > 16) throw new InvalidDataException("本机私有 IPv4 地址过多；请先停用不需要的网络适配器。");
        return Array.AsReadOnly(addresses.ToArray());
    }
}

internal sealed class WindowsTeacherMobileControlIdentityProvider : TeacherMobileControlIdentityProviderBase
{
    protected override bool RestrictToEthernetAndWifi => false;
    protected override string MissingAddressMessage => "当前没有可供手机连接的私有 IPv4 地址。";
}

internal sealed class MacTeacherMobileControlIdentityProvider : TeacherMobileControlIdentityProviderBase
{
    protected override bool RestrictToEthernetAndWifi => true;
    protected override string MissingAddressMessage => "当前没有可供手机连接的以太网或 Wi-Fi 私有 IPv4 地址。";
}

internal sealed class UnsupportedTeacherMobileControlIdentityProvider : ITeacherMobileControlIdentityProvider
{
    public MobileControlTlsIdentity CreateIdentity() =>
        throw new PlatformNotSupportedException("手机控制服务仅支持 Windows 和 macOS 教师端。");

    public IPAddress SelectPreferredLanAddress(IReadOnlyList<IPAddress> candidates) =>
        throw new PlatformNotSupportedException("手机控制服务仅支持 Windows 和 macOS 教师端。");
}
