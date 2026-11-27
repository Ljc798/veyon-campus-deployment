using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace VeyonCampus.Core;

/// <summary>
/// Removes the ordinary student account's ability to switch the network off or move it elsewhere.
/// </summary>
/// <remarks>
/// A student account that cannot reach the teacher console loses nothing of an applied policy, but
/// gains a window to play offline and a way to avoid the next instruction. On an all-in-one desk
/// with no cable the only disconnect switch a standard user still owns is the taskbar Wi-Fi tile
/// and airplane mode, because disabling an adapter through Device Manager or <c>netsh</c> already
/// needs elevation.
///
/// This class closes the remaining entries as far as Windows allows: the whole "Network &amp;
/// internet" Settings page is hidden, the classic connection UI is restricted to display-only, and
/// an optional Wi-Fi allowlist keeps a student from joining a phone hotspot. A determined student
/// can still reach the radio through the taskbar flyout, which is why <see cref="NetworkGuardian"/>
/// exists as the second line: whatever the route, once the network is gone the machine locks.
///
/// The restrictions are machine-wide. They deliberately do not apply to administrators in the
/// classic connection UI, so a technician can still reconnect the machine; an administrator can
/// also remove the policy values if the campus Wi-Fi changes.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class NetworkPolicyHardening
{
    private const string ExplorerPolicyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string NetworkConnectionsPolicyPath =
        @"SOFTWARE\Policies\Microsoft\Windows\Network Connections";
    private const string SettingsPageVisibilityValue = "SettingsPageVisibility";
    // Hides the whole "Network & internet" page; every nested page (Wi-Fi, airplane mode, VPN,
    // proxy, mobile hotspot, Ethernet) is hidden with it.
    private const string HiddenSettingsPages = "hide:network";
    private const string WifiInterfaceDescriptionNeedle = "Wi-Fi";

    /// <summary>
    /// Applies the machine-wide policy values. Safe to call repeatedly; values are written only when
    /// they differ so a periodic reconcile does not keep rewriting the registry.
    /// </summary>
    public static NetworkPolicyHardeningResult Apply()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("网络开关加固仅支持 Windows。");
        var changes = new List<string>();
        try
        {
            using (var explorer = Registry.LocalMachine.CreateSubKey(ExplorerPolicyPath, writable: true))
            {
                if (explorer is null)
                    return NetworkPolicyHardeningResult.Failed("无法打开资源管理器策略键。");
                if (explorer.GetValue(SettingsPageVisibilityValue) as string != HiddenSettingsPages)
                {
                    explorer.SetValue(SettingsPageVisibilityValue, HiddenSettingsPages,
                        RegistryValueKind.String);
                    changes.Add("已隐藏「网络和 Internet」设置页。");
                }
            }

            using (var connections = Registry.LocalMachine.CreateSubKey(NetworkConnectionsPolicyPath,
                       writable: true))
            {
                if (connections is null)
                    return NetworkPolicyHardeningResult.Failed("无法打开网络连接策略键。");
                // 1 = "Prohibit enabling/disabling a LAN connection" per the Windows policy schema.
                if (connections.GetValue("NC_LanConnect") is not 1)
                {
                    connections.SetValue("NC_LanConnect", 1, RegistryValueKind.DWord);
                    changes.Add("已禁止启用/禁用网络连接。");
                }
                if (connections.GetValue("NC_LanChangeProperties") is not 1)
                {
                    connections.SetValue("NC_LanChangeProperties", 1, RegistryValueKind.DWord);
                    changes.Add("已禁止修改网络连接属性。");
                }
            }

            return NetworkPolicyHardeningResult.Succeeded(changes);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
                                              System.Security.SecurityException or IOException or
                                              ArgumentException)
        {
            return NetworkPolicyHardeningResult.Failed("写入网络开关加固策略失败：" + exception.Message);
        }
    }

    /// <summary>
    /// Removes the values this product writes, and only when they still hold the value it writes.
    /// Called when the student Agent is uninstalled so a classroom machine does not keep a hidden
    /// network page and a blocked adapter toggle after the product is gone; a value an administrator
    /// changed in the meantime is left alone.
    /// </summary>
    public static NetworkPolicyHardeningResult Revert()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("网络开关加固仅支持 Windows。");
        var changes = new List<string>();
        try
        {
            using (var explorer = Registry.LocalMachine.OpenSubKey(ExplorerPolicyPath, writable: true))
            {
                if (explorer?.GetValue(SettingsPageVisibilityValue) as string == HiddenSettingsPages)
                {
                    explorer.DeleteValue(SettingsPageVisibilityValue, throwOnMissingValue: false);
                    changes.Add("已恢复「网络和 Internet」设置页。");
                }
            }

            using (var connections = Registry.LocalMachine.OpenSubKey(NetworkConnectionsPolicyPath,
                       writable: true))
            {
                if (connections is null) return NetworkPolicyHardeningResult.Succeeded(changes);
                foreach (var name in new[] { "NC_LanConnect", "NC_LanChangeProperties" })
                {
                    if (connections.GetValue(name) is not 1) continue;
                    connections.DeleteValue(name, throwOnMissingValue: false);
                    changes.Add($"已移除网络连接策略 {name}。");
                }
            }

            return NetworkPolicyHardeningResult.Succeeded(changes);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
                                              System.Security.SecurityException or IOException or
                                              ArgumentException)
        {
            return NetworkPolicyHardeningResult.Failed("恢复网络开关加固策略失败：" + exception.Message);
        }
    }

    /// <summary>
    /// Reads back what the machine currently enforces, for the device health view. Wi-Fi filters are
    /// only queried on request: reading them starts <c>netsh</c>, which the periodic reconcile does
    /// not need every time.
    /// </summary>
    public static NetworkPolicyHardeningSnapshot ReadSnapshot(bool includeWifiFilters = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("网络开关加固仅支持 Windows。");
        var settingsPageHidden = false;
        var connectionToggleBlocked = false;
        var propertyChangeBlocked = false;
        try
        {
            using (var explorer = Registry.LocalMachine.OpenSubKey(ExplorerPolicyPath, writable: false))
                settingsPageHidden = explorer?.GetValue(SettingsPageVisibilityValue) as string ==
                                     HiddenSettingsPages;
            using (var connections = Registry.LocalMachine.OpenSubKey(NetworkConnectionsPolicyPath,
                       writable: false))
            {
                connectionToggleBlocked = connections?.GetValue("NC_LanConnect") is 1;
                propertyChangeBlocked = connections?.GetValue("NC_LanChangeProperties") is 1;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
                                              System.Security.SecurityException or IOException)
        {
            return new NetworkPolicyHardeningSnapshot(false, false, false, null, exception.Message);
        }
        return new NetworkPolicyHardeningSnapshot(settingsPageHidden, connectionToggleBlocked,
            propertyChangeBlocked, includeWifiFilters ? ReadWifiFilters() : null, null);
    }

    /// <summary>
    /// Restricts Wi-Fi to the named SSIDs and blocks every other infrastructure network, so a
    /// student cannot sidestep the campus network by joining a phone hotspot. Administrator-only,
    /// applied once through <c>netsh</c>; the filter survives reboots.
    /// </summary>
    public static NetworkPolicyHardeningResult ApplyWifiAllowList(IReadOnlyList<string> allowedSsids)
    {
        ArgumentNullException.ThrowIfNull(allowedSsids);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("无线网络白名单仅支持 Windows。");
        var cleaned = allowedSsids
            .Where(ssid => !string.IsNullOrWhiteSpace(ssid))
            .Select(ssid => ssid.Trim())
            .Where(ssid => ssid.Length is > 0 and <= 32 && !ssid.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (cleaned.Length == 0)
            return NetworkPolicyHardeningResult.Failed("无线网络白名单为空，未作修改。");

        var changes = new List<string>();
        try
        {
            // Remove any previous deny-all first so re-applying with a changed SSID set does not
            // leave a stale block in place.
            RunNetsh("wlan delete filter permission=denyall networktype=infrastructure");
            foreach (var ssid in cleaned)
            {
                // Returns non-zero when the filter already exists; that is not a failure.
                RunNetsh($"wlan add filter permission=allow ssid=\"{ssid}\" networktype=infrastructure");
                changes.Add($"已允许无线网络 {ssid}。");
            }
            var deny = RunNetsh("wlan add filter permission=denyall networktype=infrastructure");
            if (deny.ExitCode != 0)
                return NetworkPolicyHardeningResult.Failed("添加无线网络 denyall 过滤失败：" + deny.Output);
            changes.Add("已阻止连接其他无线网络。");
            return NetworkPolicyHardeningResult.Succeeded(changes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return NetworkPolicyHardeningResult.Failed("配置无线网络白名单失败：" + exception.Message);
        }
    }

    /// <summary>Removes the Wi-Fi filters this class adds, for maintenance windows.</summary>
    public static NetworkPolicyHardeningResult ClearWifiAllowList()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("无线网络白名单仅支持 Windows。");
        var result = RunNetsh("wlan delete filter permission=denyall networktype=infrastructure");
        return result.ExitCode == 0
            ? NetworkPolicyHardeningResult.Succeeded(["已移除无线网络 denyall 过滤。"])
            : NetworkPolicyHardeningResult.Failed("移除无线网络过滤失败：" + result.Output);
    }

    /// <summary>Resolves the first Wi-Fi adapter description, used to scope per-interface commands.</summary>
    public static string? FindWifiAdapterDescription()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType !=
                    System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) continue;
                if (adapter.Description.Contains(WifiInterfaceDescriptionNeedle,
                        StringComparison.OrdinalIgnoreCase) ||
                    adapter.Name.Contains(WifiInterfaceDescriptionNeedle, StringComparison.OrdinalIgnoreCase))
                    return adapter.Description;
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return null;
    }

    private static string? ReadWifiFilters()
    {
        var result = RunNetsh("wlan show filters");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) return null;
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("Filter", StringComparison.OrdinalIgnoreCase) &&
                           !line.StartsWith("---", StringComparison.Ordinal))
            .ToArray();
        return lines.Length == 0 ? "无过滤规则" : string.Join("; ", lines);
    }

    private static (int ExitCode, string Output) RunNetsh(string arguments)
    {
        var startInfo = new ProcessStartInfo("netsh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 netsh。");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(15));
        return (process.HasExited ? process.ExitCode : -1, output.Trim());
    }
}

/// <summary>Outcome of a hardening pass; <see cref="Changes"/> is empty when nothing needed writing.</summary>
public sealed record NetworkPolicyHardeningResult(bool Applied, IReadOnlyList<string> Changes,
    string? Failure)
{
    public static NetworkPolicyHardeningResult Succeeded(IReadOnlyList<string> changes) =>
        new(true, changes, null);

    public static NetworkPolicyHardeningResult Failed(string failure) =>
        new(false, [], failure);
}

/// <summary>What the machine currently enforces, surfaced for diagnostics.</summary>
public sealed record NetworkPolicyHardeningSnapshot(bool SettingsPageHidden,
    bool ConnectionToggleBlocked, bool PropertyChangeBlocked, string? WifiFilters, string? Error)
{
    public bool FullyEnforced =>
        SettingsPageHidden && ConnectionToggleBlocked && PropertyChangeBlocked && Error is null;
}
