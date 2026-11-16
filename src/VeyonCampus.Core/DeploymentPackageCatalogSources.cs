using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

/// <summary>
/// Where the static deployment-package catalogue lives. The catalogue is a JSON document committed
/// next to the package archives, so discovery costs one small GET instead of a database query.
/// </summary>
public enum DeploymentCatalogKind
{
    GitCode,
    GitHub
}

/// <summary>
/// One ordered catalogue source. GitCode is primary because its raw endpoint answers directly from
/// mainland China; GitHub stays as a second attempt for networks that block the former.
/// </summary>
public sealed record DeploymentCatalogSource(
    DeploymentCatalogKind Kind, string Owner, string Repository, string Reference = "develop")
{
    public const string CatalogFileName = "deployment-catalog.json";
    public const string PackageDirectoryName = "packages";

    private static readonly Regex SlugPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ReferencePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Uri CatalogUri => new(RawUrl(CatalogFileName), UriKind.Absolute);

    public Uri PackageUri(Guid packageId) =>
        new(RawUrl($"{PackageDirectoryName}/{packageId:D}.zip"), UriKind.Absolute);

    /// <summary>
    /// GitCode serves file bytes from its API host and takes the ref as a query parameter; the
    /// <c>raw.gitcode.com</c> subdomain only renders previews and answers binary requests with a
    /// placeholder SVG. GitHub's dedicated raw host is the one shape that omits the `/raw/` segment.
    /// The publishing script builds the same URLs, so both sides must agree here.
    /// </summary>
    private string RawUrl(string relativePath) => Kind == DeploymentCatalogKind.GitHub
        ? $"https://raw.githubusercontent.com/{Owner}/{Repository}/{Uri.EscapeDataString(Reference)}/{relativePath}"
        : $"https://api.gitcode.com/api/v5/repos/{Owner}/{Repository}/raw/{relativePath}" +
          $"?ref={Uri.EscapeDataString(Reference)}";

    /// <summary>Parses a "kind:owner/repository[@reference]" entry.</summary>
    public static DeploymentCatalogSource Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            throw new InvalidDataException($"配置包目录来源“{value}”缺少 kind:owner/repository 形式。");
        var kindText = value[..separator].Trim();
        var remainder = value[(separator + 1)..].Trim();
        var reference = "develop";
        var at = remainder.IndexOf('@');
        if (at >= 0)
        {
            reference = remainder[(at + 1)..].Trim();
            remainder = remainder[..at].Trim();
            if (!ReferencePattern.IsMatch(reference) || reference.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException($"配置包目录来源“{value}”的引用名无效。");
        }
        var kind = kindText.ToLowerInvariant() switch
        {
            "gitcode" => DeploymentCatalogKind.GitCode,
            "github" => DeploymentCatalogKind.GitHub,
            _ => throw new InvalidDataException($"配置包目录来源“{value}”使用了未知的托管方“{kindText}”。")
        };
        var parts = remainder.Split('/');
        if (parts.Length != 2 || !SlugPattern.IsMatch(parts[0]) || !SlugPattern.IsMatch(parts[1]))
            throw new InvalidDataException($"配置包目录来源“{value}”的 owner/repository 无效。");
        return new DeploymentCatalogSource(kind, parts[0], parts[1], reference);
    }

    public override string ToString() =>
        (Kind == DeploymentCatalogKind.GitCode ? "gitcode:" : "github:") + Owner + "/" + Repository +
        "@" + Reference;
}

/// <summary>
/// Resolves the catalogue sources and the two escape hatches. A school that has not committed
/// <c>packages/</c> yet keeps working through the retired HTTP API until the switch is flipped.
/// </summary>
public static class DeploymentCatalogConfiguration
{
    public const string SourcesEnvironmentVariable = "VEYONCAMPUS_DEPLOYMENT_CATALOG_SOURCES";
    public const string DisabledEnvironmentVariable = "VEYONCAMPUS_DEPLOYMENT_CATALOG_DISABLED";
    public const string ApiFallbackEnvironmentVariable = "VEYONCAMPUS_DEPLOYMENT_PACKAGES_API_FALLBACK";

    private static readonly DeploymentCatalogSource[] DefaultSources =
    [
        new(DeploymentCatalogKind.GitCode, "ljc_1126", "veyon-campus-deployment", "develop"),
        new(DeploymentCatalogKind.GitHub, "Ljc798", "veyon-campus-deployment", "develop")
    ];

    /// <summary>True unless an administrator turned the static catalogue off for diagnostics.</summary>
    public static bool StaticEnabled => !IsTruthy(Environment.GetEnvironmentVariable(DisabledEnvironmentVariable));

    /// <summary>Static-first, API-on-failure. Set to 0 once every client has been moved over.</summary>
    public static bool ApiFallbackEnabled =>
        !IsFalsey(Environment.GetEnvironmentVariable(ApiFallbackEnvironmentVariable));

    public static IReadOnlyList<DeploymentCatalogSource> Resolve()
    {
        if (!StaticEnabled) return [];
        var configured = Environment.GetEnvironmentVariable(SourcesEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured)) return DefaultSources;
        var sources = configured
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(DeploymentCatalogSource.Parse)
            .ToArray();
        if (sources.Length == 0)
            throw new InvalidDataException($"{SourcesEnvironmentVariable} 至少需要一个来源。");
        if (sources.Select(source => source.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new InvalidDataException($"{SourcesEnvironmentVariable} 包含重复来源。");
        return sources;
    }

    private static bool IsTruthy(string? value) =>
        value is not null && (value.Trim() is "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    private static bool IsFalsey(string? value) =>
        value is not null && (value.Trim() is "0" || value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));
}
