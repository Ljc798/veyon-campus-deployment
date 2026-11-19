using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

/// <summary>
/// Where a release catalogue is hosted. The catalogue is the small JSON asset attached to each
/// published release; it carries the signed manifest, the optional Student Agent target sidecar and
/// the mirror URLs that serve the same bytes.
/// </summary>
public enum ReleaseCatalogKind
{
    GitCode,
    GitHub
}

/// <summary>
/// One ordered catalogue source. GitCode is the primary because school networks in mainland China
/// reach it directly; GitHub stays as the second attempt for networks that block the former.
/// </summary>
public sealed record ReleaseCatalogSource(ReleaseCatalogKind Kind, string Owner, string Repository)
{
    public const string CatalogAssetName = "release-catalog.json";

    private static readonly Regex SlugPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string ApiBase => Kind == ReleaseCatalogKind.GitCode
        ? $"https://api.gitcode.com/api/v5/repos/{Owner}/{Repository}/"
        : $"https://api.github.com/repos/{Owner}/{Repository}/";

    public Uri LatestReleaseUri => new(ApiBase + "releases/latest");

    public Uri ReleaseByTagUri(string tag) => new(ApiBase + "releases/tags/" + Uri.EscapeDataString(tag));

    /// <summary>Parses a single "kind:owner/repository" entry.</summary>
    public static ReleaseCatalogSource Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            throw new InvalidDataException($"发布目录来源“{value}”缺少 kind:owner/repository 形式。");
        var kindText = value[..separator].Trim();
        var slug = value[(separator + 1)..].Trim();
        var kind = kindText.ToLowerInvariant() switch
        {
            "gitcode" => ReleaseCatalogKind.GitCode,
            "github" => ReleaseCatalogKind.GitHub,
            _ => throw new InvalidDataException($"发布目录来源“{value}”使用了未知的托管方“{kindText}”。")
        };
        var parts = slug.Split('/');
        if (parts.Length != 2 || !SlugPattern.IsMatch(parts[0]) || !SlugPattern.IsMatch(parts[1]))
            throw new InvalidDataException($"发布目录来源“{value}”的 owner/repository 无效。");
        return new ReleaseCatalogSource(kind, parts[0], parts[1]);
    }

    public override string ToString() =>
        (Kind == ReleaseCatalogKind.GitCode ? "gitcode:" : "github:") + Owner + "/" + Repository;
}

/// <summary>
/// Resolves the ordered catalogue sources. The environment override exists so a self-hosted or
/// offline test deployment can point the client at its own publisher without rebuilding.
/// </summary>
public static class ReleaseCatalogConfiguration
{
    public const string SourcesEnvironmentVariable = "VEYONCAMPUS_RELEASE_CATALOG_SOURCES";

    /// <summary>
    /// Set to 1 to switch the CloudBase fallback off, for the case the subscription lapses again or a
    /// build has to run entirely against the public catalogues.
    /// </summary>
    public const string CloudApiDisabledEnvironmentVariable = "VEYONCAMPUS_RELEASE_API_DISABLED";

    private static readonly ReleaseCatalogSource[] DefaultSources =
    [
        new(ReleaseCatalogKind.GitCode, "ljc_1126", "veyon-campus-deployment"),
        new(ReleaseCatalogKind.GitHub, "Ljc798", "veyon-campus-deployment")
    ];

    /// <summary>
    /// The CloudBase API is reachable again, so it goes back in as the fallback behind the signed
    /// catalogue: the catalogue stays authoritative because it is the copy every release publishes,
    /// while the API keeps a client updating when GitCode and GitHub are both unreachable.
    /// </summary>
    public static bool CloudApiFallbackEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable(CloudApiDisabledEnvironmentVariable)?.Trim(),
            "1", StringComparison.Ordinal);

    public static IReadOnlyList<ReleaseCatalogSource> Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(SourcesEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured)) return DefaultSources;
        var sources = configured
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ReleaseCatalogSource.Parse)
            .ToArray();
        if (sources.Length == 0)
            throw new InvalidDataException($"{SourcesEnvironmentVariable} 至少需要一个来源。");
        if (sources.Select(source => source.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new InvalidDataException($"{SourcesEnvironmentVariable} 包含重复来源。");
        return sources;
    }
}

/// <summary>
/// The resolved release for one role, plus the mirrors that carry byte-identical installers. Mirrors
/// are not covered by the release signature, but every downloaded byte is checked against the signed
/// SHA-256 before it is staged, so a mirror can never change what gets installed.
/// </summary>
public sealed record ReleaseCatalogEntry(
    ApplicationReleaseEnvelope Release,
    ApplicationReleaseAgentTargetEnvelope? StudentAgentTarget,
    IReadOnlyList<string> MirrorDownloadUrls);
