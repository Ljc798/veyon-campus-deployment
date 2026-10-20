using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Win32;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public sealed record StudentUpdateEnvironmentCategories(
    [property: JsonPropertyName("networkType")] string NetworkType,
    [property: JsonPropertyName("osRelease")] string OsRelease)
{
    public static StudentUpdateEnvironmentCategories Unknown { get; } = new("unknown", "unknown");

    public static bool IsValid(StudentUpdateEnvironmentCategories? value) => value is not null &&
        value.NetworkType is "wired" or "wifi" or "mixed" or "other" or "unknown" &&
        value.OsRelease is "windows10" or "windows11" or "other" or "unknown";
}

/// <summary>
/// Derives only broad environment categories. It never retains the selected address,
/// network name, adapter name, or Windows product string.
/// </summary>
public static class StudentUpdateEnvironmentClassifier
{
    public static StudentUpdateEnvironmentCategories Detect(Uri localDownloadUri)
    {
        ArgumentNullException.ThrowIfNull(localDownloadUri);
        if (!OperatingSystem.IsWindows()) return StudentUpdateEnvironmentCategories.Unknown;
        try
        {
            return new StudentUpdateEnvironmentCategories(DetectNetworkType(localDownloadUri), DetectOsRelease());
        }
        catch (Exception exception) when (exception is SocketException or NetworkInformationException or
                                          InvalidOperationException or ArgumentException or
                                          PlatformNotSupportedException or UnauthorizedAccessException or
                                          System.Security.SecurityException or IOException or NotSupportedException)
        {
            return StudentUpdateEnvironmentCategories.Unknown;
        }
    }

    private static string DetectNetworkType(Uri localDownloadUri)
    {
        try
        {
            if (!IPAddress.TryParse(localDownloadUri.Host, out var remoteAddress) ||
                remoteAddress.AddressFamily != AddressFamily.InterNetwork)
                return "unknown";

            // A UDP connect selects the route and local interface without sending a packet.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(remoteAddress, 9));
            if (socket.LocalEndPoint is not IPEndPoint { Address: var localAddress }) return "unknown";

            var matches = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
                .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses
                    .Where(unicast => unicast.Address.Equals(localAddress))
                    .Select(_ => networkInterface))
                .DistinctBy(networkInterface => networkInterface.Id)
                .ToArray();
            if (matches.Length != 1) return "unknown";
            if (matches[0].NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "wifi";
            return matches[0].NetworkInterfaceType.ToString()
                .Contains("Ethernet", StringComparison.OrdinalIgnoreCase)
                ? "wired"
                : "other";
        }
        catch (Exception exception) when (exception is SocketException or NetworkInformationException or
                                          InvalidOperationException or ArgumentException or
                                          PlatformNotSupportedException)
        {
            return "unknown";
        }
    }

    [SupportedOSPlatform("windows")]
    private static string DetectOsRelease()
    {
        try
        {
            var build = Environment.OSVersion.Version.Build;
            var productName = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", null)
                as string;
            if (string.IsNullOrWhiteSpace(productName)) return "unknown";
            if (productName.Contains("Server", StringComparison.OrdinalIgnoreCase)) return "other";
            if (build >= 22_000 && productName.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                return "windows11";
            if (build is >= 10_240 and < 22_000 &&
                productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                return "windows10";
            return "other";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
                                          System.Security.SecurityException or InvalidOperationException or
                                          IOException or ArgumentException)
        {
            return "unknown";
        }
    }
}
