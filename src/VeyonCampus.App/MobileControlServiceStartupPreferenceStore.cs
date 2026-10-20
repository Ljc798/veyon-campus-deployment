using System.Runtime.Versioning;
using Microsoft.Win32;
using VeyonCampus.Core;

namespace VeyonCampus.App;

[SupportedOSPlatform("windows")]
internal static class MobileControlServiceStartupPreferenceStore
{
    private const string RegistryPath = @"Software\VeyonCampus\MobileControl";
    private const string AutoStartValue = "AutoStartService";
    private const string TlsRootThumbprintValue = "TlsRootCertificateThumbprint";

    public static bool IsEnabled()
    {
        using var settings = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        if (settings?.GetValue(AutoStartValue) is int value) return value == 1;

        // Migrate existing mobile-control installations: a pinned TLS root or paired phone
        // proves this service was configured before the startup preference existed.
        var wasPreviouslyConfigured = !string.IsNullOrWhiteSpace(
            settings?.GetValue(TlsRootThumbprintValue) as string);
        if (!wasPreviouslyConfigured) wasPreviouslyConfigured = MobilePairedDeviceStore.List().Count > 0;
        if (!wasPreviouslyConfigured) return false;

        using var writableSettings = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
                                     ?? throw new IOException("无法迁移教师手机控制的自动恢复设置。");
        writableSettings.SetValue(AutoStartValue, 1, RegistryValueKind.DWord);
        writableSettings.Flush();
        return true;
    }

    public static void SetEnabled(bool enabled)
    {
        using var settings = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
                            ?? throw new IOException("无法保存教师手机控制的自动恢复设置。");
        settings.SetValue(AutoStartValue, enabled ? 1 : 0, RegistryValueKind.DWord);
        settings.Flush();
    }
}
