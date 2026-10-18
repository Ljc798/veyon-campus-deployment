using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace VeyonCampus.Core;

/// <summary>Removes only the Edge and startup integration installed by the student setup installer.</summary>
internal static class StudentSetupIntegrationCleanup
{
    private const string EdgeForceListPath = @"SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist";
    private const string EdgeMarkerPath = @"SOFTWARE\VeyonCampus\StudentSetup\EdgeRealtime";
    private const string NativeHostName = "fun.kidscode.veyoncampus.policy";
    private const string NativeHostPath = @"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\fun.kidscode.veyoncampus.policy";
    private const string EdgeStoreUpdateUrl = "https://edge.microsoft.com/extensionwebstorebase/v1/crx";

    public static void RemoveOwnedIntegration(bool removeShortcuts = true)
    {
        if (!OperatingSystem.IsWindows()) return;
        RemoveOwnedEdgeExtensionPolicy();
        RemoveNativeMessagingHostRegistration();
        if (removeShortcuts) RemoveKnownShortcuts();
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveOwnedEdgeExtensionPolicy()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        string? slot = null;
        string? ownedValue = null;
        using (var marker = hive.OpenSubKey(EdgeMarkerPath, writable: false))
        {
            slot = marker?.GetValue("PolicySlot") as string;
            ownedValue = marker?.GetValue("PolicyValue") as string;
        }
        if (int.TryParse(slot, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            index is >= 1 and <= 999 && ownedValue is not null && IsStudentEdgeExtensionValue(ownedValue))
        {
            using var forceList = hive.OpenSubKey(EdgeForceListPath, writable: true);
            if (string.Equals(forceList?.GetValue(slot) as string, ownedValue, StringComparison.Ordinal))
                forceList!.DeleteValue(slot, throwOnMissingValue: false);
        }
        hive.DeleteSubKeyTree(EdgeMarkerPath, throwOnMissingSubKey: false);
        using var remaining = hive.OpenSubKey(EdgeForceListPath, writable: false);
        if (remaining is not null && remaining.SubKeyCount == 0 && remaining.ValueCount == 0)
        {
            remaining.Dispose();
            hive.DeleteSubKey(EdgeForceListPath, throwOnMissingSubKey: false);
        }
    }

    private static bool IsStudentEdgeExtensionValue(string value)
    {
        var parts = value.Split(';', StringSplitOptions.None);
        return parts.Length == 2 && parts[0].Length == 32 &&
               parts[0].All(character => character is >= 'a' and <= 'p') &&
               string.Equals(parts[1], EdgeStoreUpdateUrl, StringComparison.Ordinal);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveNativeMessagingHostRegistration()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var host = hive.OpenSubKey(NativeHostPath, writable: false);
        if (host is null) return;

        var registeredPath = host.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(registeredPath)) return;
        var knownPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(programFiles => Path.GetFullPath(Path.Combine(
            programFiles, "Veyon Campus", "Student", "EdgeNativeHost", NativeHostName + ".json")));
        if (!knownPaths.Any(path => string.Equals(Path.GetFullPath(registeredPath), path,
                StringComparison.OrdinalIgnoreCase))) return;

        host.Dispose();
        hive.DeleteSubKeyTree(NativeHostPath, throwOnMissingSubKey: false);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveKnownShortcuts()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs",
                "Veyon Campus", "Veyon Campus 学生部署工具.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs",
                "Veyon Campus", "Veyon Campus 学生课堂助手.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                "Veyon Campus 学生课堂助手.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                "Veyon Campus 学生部署工具.lnk")
        };
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            PathLinkSecurity.RejectLinks(path);
            File.Delete(path);
        }

        var programGroup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "Veyon Campus");
        if (Directory.Exists(programGroup))
        {
            PathLinkSecurity.RejectLinks(programGroup);
            if (!Directory.EnumerateFileSystemEntries(programGroup).Any()) Directory.Delete(programGroup);
        }
    }
}
