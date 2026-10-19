using System.Globalization;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public sealed record PackageVersionRange(string MinInclusive, string MaxExclusive);
public sealed record PackageCompatibility(PackageVersionRange StudentApp, PackageVersionRange Veyon,
    PackageVersionRange? StudentAgent = null)
{
    public static PackageCompatibility ForExactVersions(string studentAppVersion, string veyonVersion) =>
        new(new PackageVersionRange(studentAppVersion, Next(studentAppVersion, 3)),
            new PackageVersionRange(veyonVersion, Next(veyonVersion, 4)));

    public static PackageCompatibility ForSystemPolicyVersions(string studentAppVersion, string veyonVersion,
        string minimumStudentAgentVersion, string maxExclusiveStudentAgentVersion = "0.5.0") =>
        new(new PackageVersionRange(studentAppVersion, Next(studentAppVersion, 3)),
            new PackageVersionRange(veyonVersion, Next(veyonVersion, 4)),
            new PackageVersionRange(minimumStudentAgentVersion, maxExclusiveStudentAgentVersion));

    // Schemas 4–6 are explicitly versioned formats. Compatible releases within the same
    // product major can read them; breaking package changes must use a new schema version.
    public static PackageCompatibility ForSupportedProtocolVersions(string studentAppVersion, string veyonVersion,
        string? minimumStudentAgentVersion = null) =>
        new(StudentAppProtocolRange(studentAppVersion),
            VeyonProtocolRange(veyonVersion),
            minimumStudentAgentVersion is null ? null : new PackageVersionRange(minimumStudentAgentVersion, "0.5.0"));

    public void EnsureReadable(int schemaVersion, string studentAppVersion, string veyonVersion, string? studentAgentVersion)
    {
        Validate();
        var legacyPreOneCompatibility = schemaVersion is >= 4 and <= 6 &&
            Compare(StudentApp.MinInclusive, "0.4.48", 3) >= 0 &&
            Compare(StudentApp.MinInclusive, "0.4.53", 3) <= 0 &&
            StudentApp.MaxExclusive == Next(StudentApp.MinInclusive, 3) &&
            Compare(studentAppVersion, StudentApp.MinInclusive, 3) >= 0 &&
            Compare(studentAppVersion, "0.5.0", 3) < 0;
        var compatible = this;
        if (legacyPreOneCompatibility)
            compatible = compatible with { StudentApp = new PackageVersionRange(StudentApp.MinInclusive, "0.5.0") };
        else if (schemaVersion is >= 4 and <= 6 &&
                 TryGetMajor(StudentApp.MinInclusive, out var packageMajor) &&
                 TryGetMajor(studentAppVersion, out var currentMajor) &&
                 packageMajor == currentMajor)
            compatible = compatible with { StudentApp = MajorRange(packageMajor) };
        if (schemaVersion is >= 4 and <= 6 &&
            TryGetMajorMinor(Veyon.MinInclusive, out var packageVeyonMajor, out var packageVeyonMinor) &&
            TryGetMajorMinor(veyonVersion, out var currentVeyonMajor, out var currentVeyonMinor) &&
            packageVeyonMajor == currentVeyonMajor && packageVeyonMinor == currentVeyonMinor)
            compatible = compatible with { Veyon = VeyonProtocolRange(Veyon.MinInclusive) };
        compatible.EnsureCompatible(studentAppVersion, veyonVersion, studentAgentVersion);
    }

    public void Validate()
    {
        ValidateRange(StudentApp, 3, "Student App");
        ValidateRange(Veyon, 4, "Veyon");
        if (StudentAgent is not null) ValidateRange(StudentAgent, 3, "Student Agent");
    }

    public void EnsureCompatible(string studentAppVersion, string veyonVersion, string? studentAgentVersion = null)
    {
        Validate();
        if (!InRange(studentAppVersion, StudentApp, 3) || !InRange(veyonVersion, Veyon, 4) ||
            (StudentAgent is not null && (studentAgentVersion is null || !InRange(studentAgentVersion, StudentAgent, 3))))
            throw new InvalidDataException(
                $"此校区包仅兼容 Student App [{StudentApp.MinInclusive}, {StudentApp.MaxExclusive}) 与 Veyon [{Veyon.MinInclusive}, {Veyon.MaxExclusive})" +
                (StudentAgent is null ? "" : $"，以及 Student Agent [{StudentAgent.MinInclusive}, {StudentAgent.MaxExclusive})") +
                $"；当前版本为 {studentAppVersion} / {veyonVersion}" +
                (StudentAgent is null ? "。" : $" / {studentAgentVersion ?? "未检测到"}。"));
    }

    public static int Compare(string left, string right, int components)
    {
        var a = Parse(left, components);
        var b = Parse(right, components);
        for (var i = 0; i < components; i++)
        {
            var comparison = a[i].CompareTo(b[i]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static bool InRange(string version, PackageVersionRange range, int components) =>
        Compare(version, range.MinInclusive, components) >= 0 && Compare(version, range.MaxExclusive, components) < 0;

    private static void ValidateRange(PackageVersionRange range, int components, string label)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (Compare(range.MinInclusive, range.MaxExclusive, components) >= 0)
            throw new InvalidDataException($"{label} 兼容版本区间必须满足 minInclusive < maxExclusive。");
    }

    private static string Next(string version, int components)
    {
        var values = Parse(version, components);
        checked { values[^1]++; }
        return string.Join('.', values.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    }

    private static string NextMinor(string version)
    {
        var values = Parse(version, 3);
        checked { values[1]++; }
        values[2] = 0;
        return string.Join('.', values.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    }

    private static PackageVersionRange StudentAppProtocolRange(string version)
    {
        if (!TryGetMajor(version, out var major))
            throw new InvalidDataException("Student App 版本无效。");
        return major == 0
            ? new PackageVersionRange(version, NextMinor(version))
            : MajorRange(major);
    }

    private static PackageVersionRange VeyonProtocolRange(string version)
    {
        var values = Parse(version, 4);
        if (values[1] >= 65535)
            throw new InvalidDataException("Veyon 次版本号超出兼容范围。");
        return new PackageVersionRange($"{values[0]}.{values[1]}.0.0",
            $"{values[0]}.{values[1] + 1}.0.0");
    }

    private static PackageVersionRange MajorRange(int major)
    {
        if (major is < 0 or >= 65535)
            throw new InvalidDataException("Student App 主版本号超出兼容范围。");
        return new PackageVersionRange($"{major}.0.0", $"{major + 1}.0.0");
    }

    private static bool TryGetMajor(string version, out int major)
    {
        major = -1;
        var firstSeparator = version.IndexOf('.');
        return firstSeparator > 0 && int.TryParse(version.AsSpan(0, firstSeparator), NumberStyles.None,
            CultureInfo.InvariantCulture, out major) && major is >= 0 and <= 65535;
    }

    private static bool TryGetMajorMinor(string version, out int major, out int minor)
    {
        major = -1;
        minor = -1;
        var parts = version.Split('.');
        return parts.Length == 4 &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major) &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor) &&
               major is >= 0 and <= 65535 && minor is >= 0 and <= 65535;
    }

    private static int[] Parse(string? value, int components)
    {
        if (value is null || !Regex.IsMatch(value, $"^(0|[1-9][0-9]*)\\.{string.Join("\\.", Enumerable.Repeat("(0|[1-9][0-9]*)", components - 1))}$",
                RegexOptions.CultureInvariant))
            throw new InvalidDataException($"兼容版本必须是规范的 {components} 段数字版本。");
        var parts = value.Split('.').Select(part => int.TryParse(part, NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : -1).ToArray();
        if (parts.Length != components || parts.Any(part => part is < 0 or > 65535))
            throw new InvalidDataException($"兼容版本必须是每段 0–65535 的 {components} 段数字版本。");
        return parts;
    }
}

public sealed record PackagePayloadFile(string Path, long Size, string Sha256);
