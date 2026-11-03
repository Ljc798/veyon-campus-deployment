using System.Text.RegularExpressions;

namespace VeyonCampus.Core.Maintenance;

internal static class MaintenanceValidation
{
    private static readonly Regex CapabilityNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string RequiredText(string value, string label, int maximumLength = 512)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength ||
            normalized.Any(char.IsControl))
            throw new ArgumentException($"{label} 无效。", label);
        return normalized;
    }

    public static string? OptionalVersion(string? value, string label)
    {
        if (value is null) return null;
        var version = RequiredText(value, label, 64);
        _ = ApplicationReleaseClient.CompareVersions(version, version);
        return version;
    }

    public static string Fingerprint(string value, string label)
    {
        var fingerprint = RequiredText(value, label, 64);
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException($"{label} 必须是 SHA-256 十六进制摘要。", label);
        return fingerprint.ToUpperInvariant();
    }

    public static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();

    public static void ValidateCapabilityName(string value)
    {
        if (!CapabilityNamePattern.IsMatch(value))
            throw new ArgumentException("维护能力名称格式无效。", nameof(value));
    }
}
