using System.Text.Json;
using Microsoft.Win32;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal static class MobileControlServiceStartupPreferenceStore
{
    private const string RegistryPath = @"Software\VeyonCampus\MobileControl";
    private const string AutoStartValue = "AutoStartService";
    private const string TlsRootThumbprintValue = "TlsRootCertificateThumbprint";
    private static readonly string PortablePreferencePath = Path.Combine(
        VeyonCampusPaths.GetLocalApplicationDataDirectory(),
        "VeyonCampus", "MobileControl", "startup-preference.json");

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return IsPortableEnabled();
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
        if (!OperatingSystem.IsWindows())
        {
            SetPortableEnabled(enabled);
            return;
        }
        using var settings = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
                            ?? throw new IOException("无法保存教师手机控制的自动恢复设置。");
        settings.SetValue(AutoStartValue, enabled ? 1 : 0, RegistryValueKind.DWord);
        settings.Flush();
    }

    private static bool IsPortableEnabled()
    {
        if (!File.Exists(PortablePreferencePath)) return MobilePairedDeviceStore.List().Count > 0;
        RejectLinks(PortablePreferencePath);
        var info = new FileInfo(PortablePreferencePath);
        if (info.Length is <= 0 or > 4096)
            throw new InvalidDataException("手机服务自动恢复设置大小无效。");
        PortablePreference? preference;
        try
        {
            preference = JsonSerializer.Deserialize<PortablePreference>(File.ReadAllBytes(PortablePreferencePath));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("手机服务自动恢复设置格式无效。", exception);
        }
        if (preference is not { SchemaVersion: 1 })
            throw new InvalidDataException("手机服务自动恢复设置版本无效。");
        return preference.Enabled;
    }

    private static void SetPortableEnabled(bool enabled)
    {
        var directory = Path.GetDirectoryName(PortablePreferencePath)
            ?? throw new InvalidDataException("手机服务自动恢复设置路径无效。");
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        var temporary = PortablePreferencePath + ".tmp-" + Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new PortablePreference(1, enabled));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(PortablePreferencePath)) RejectLinks(PortablePreferencePath);
            File.Move(temporary, PortablePreferencePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null;
             current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("手机服务自动恢复设置路径不能包含符号链接或重解析点。");
        }
    }

    private sealed record PortablePreference(int SchemaVersion, bool Enabled);
}
