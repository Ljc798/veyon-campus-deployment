using System.Globalization;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public sealed record PackageVersionRange(string MinInclusive, string MaxExclusive);
public sealed record PackageCompatibility(PackageVersionRange StudentApp, PackageVersionRange Veyon)
{
    public static PackageCompatibility ForExactVersions(string studentAppVersion, string veyonVersion) =>
        new(new PackageVersionRange(studentAppVersion, Next(studentAppVersion, 3)),
            new PackageVersionRange(veyonVersion, Next(veyonVersion, 4)));

    public void Validate()
    {
        ValidateRange(StudentApp, 3, "Student App");
        ValidateRange(Veyon, 4, "Veyon");
    }

    public void EnsureCompatible(string studentAppVersion, string veyonVersion)
    {
        Validate();
        if (!InRange(studentAppVersion, StudentApp, 3) || !InRange(veyonVersion, Veyon, 4))
            throw new InvalidDataException(
                $"此校区包仅兼容 Student App [{StudentApp.MinInclusive}, {StudentApp.MaxExclusive}) 与 Veyon [{Veyon.MinInclusive}, {Veyon.MaxExclusive})；当前版本为 {studentAppVersion} / {veyonVersion}。");
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
