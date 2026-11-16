using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public sealed partial class ApplicationReleaseClient
{
    private const int MaximumCatalogBytes = 256 * 1024;
    private const int MaximumIndexBytes = 512 * 1024;

    /// <summary>
    /// Per-source budget. A school network that silently drops packets to one host must not stall the
    /// version check for the lifetime of the shared HttpClient timeout.
    /// </summary>
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Release indexes carry far more fields than the client needs and the two hosts differ in a few
    /// of them, so index responses are read leniently. The catalogue document itself is ours and stays
    /// strict: an unexpected field there means the publisher produced something this client cannot
    /// honestly claim to understand.
    /// </summary>
    private static readonly JsonSerializerOptions IndexJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    /// <summary>
    /// Reads the catalogue asset for the newest release of one role. Returns null when no source
    /// publishes a catalogue (an older release, or a mirror that only carries installers); throws when
    /// every source failed to answer, so the caller can report an outage instead of "already current".
    /// </summary>
    private async Task<ReleaseCatalogEntry?> TryReadCatalogEntryAsync(ApplicationReleaseRole role,
        CancellationToken cancellationToken)
    {
        if (_catalogSources.Count == 0) return null;
        var failures = new List<string>();
        var answeredWithoutCatalog = false;
        foreach (var source in _catalogSources)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SourceTimeout);
            try
            {
                var entry = await ReadCatalogEntryFromSourceAsync(source, role, timeout.Token)
                    .ConfigureAwait(false);
                if (entry is not null) return entry;
                answeredWithoutCatalog = true;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or
                                                  JsonException or TaskCanceledException &&
                                                  !cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{source}: {exception.Message}");
            }
        }
        if (answeredWithoutCatalog) return null;
        throw new HttpRequestException(failures.Count > 0
            ? "版本目录暂时不可用：" + string.Join("；", failures)
            : "版本目录暂时不可用。");
    }

    private async Task<ReleaseCatalogEntry?> ReadCatalogEntryFromSourceAsync(ReleaseCatalogSource source,
        ApplicationReleaseRole role, CancellationToken cancellationToken)
    {
        var roleName = RoleName(role);
        var index = await ReadIndexAsync(source, source.LatestReleaseUri, cancellationToken).ConfigureAwait(false);
        if (index is null || index.Prerelease) return null;
        var asset = (index.Assets ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, ReleaseCatalogSource.CatalogAssetName, StringComparison.Ordinal));
        if (asset is null || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var assetUri) ||
            assetUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(assetUri.UserInfo))
            return null;

        ReleaseCatalogDocument? document;
        try
        {
            document = await ReadCatalogDocumentAsync(assetUri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }
        if (document is null || document.SchemaVersion != 1 || document.Roles is null) return null;
        if (!document.Roles.TryGetValue(roleName, out var entry) || entry?.Release?.Manifest is null) return null;
        if (document.Tag is null ||
            (!string.Equals(document.Tag, "v" + entry.Release.Manifest.Version, StringComparison.Ordinal) &&
             !string.Equals(document.Tag, "beta-v" + entry.Release.Manifest.Version, StringComparison.Ordinal)))
            throw new InvalidDataException("发布目录的标签与其中清单的版本不一致。");

        var mirrors = new List<string>();
        foreach (var mirror in entry.Mirrors ?? [])
        {
            if (string.IsNullOrWhiteSpace(mirror)) continue;
            if (!Uri.TryCreate(mirror.Trim(), UriKind.Absolute, out var mirrorUri) ||
                mirrorUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(mirrorUri.UserInfo) ||
                !string.IsNullOrEmpty(mirrorUri.Fragment))
                continue;
            if (mirrors.Contains(mirrorUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)) continue;
            if (string.Equals(mirrorUri.AbsoluteUri, entry.Release.Manifest.DownloadUrl,
                    StringComparison.OrdinalIgnoreCase)) continue;
            mirrors.Add(mirrorUri.AbsoluteUri);
        }
        return new ReleaseCatalogEntry(entry.Release, entry.StudentAgentTarget, mirrors);
    }

    /// <summary>
    /// Reads the human-readable notes off the release itself. They live outside the signed manifest on
    /// purpose: they never feed a digest, signature or compatibility decision.
    /// </summary>
    private async Task<string?> TryReadCatalogNotesAsync(string version, CancellationToken cancellationToken)
    {
        if (_catalogSources.Count == 0) return null;
        var tag = "v" + version;
        foreach (var source in _catalogSources)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SourceTimeout);
            try
            {
                var index = await ReadIndexAsync(source, source.ReleaseByTagUri(tag), timeout.Token)
                    .ConfigureAwait(false);
                if (index?.Body is { Length: > 0 } body) return body;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or
                                                  JsonException or TaskCanceledException &&
                                                  !cancellationToken.IsCancellationRequested)
            {
                // A source that cannot answer simply hands over to the next one; notes are optional.
            }
        }
        return null;
    }

    private async Task<ReleaseIndexResponse?> ReadIndexAsync(ReleaseCatalogSource source, Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await SendMetadataAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{source} 发布索引返回 {(int)response.StatusCode}。", null,
                response.StatusCode);
        var bytes = await ReadBoundedAsync(response.Content, MaximumIndexBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<ReleaseIndexResponse>(bytes, IndexJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{source} 发布索引格式无效。", exception);
        }
    }

    private async Task<ReleaseCatalogDocument?> ReadCatalogDocumentAsync(Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await SendMetadataAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("发布目录暂时不可用。", null, response.StatusCode);
        var bytes = await ReadBoundedAsync(response.Content, MaximumCatalogBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<ReleaseCatalogDocument>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("发布目录格式无效。", exception);
        }
    }


    /// <summary>
    /// The shared client never follows redirects by itself, because the installer download verifies
    /// every hop. Release-host API calls do redirect (a GitCode asset URL answers 302 to its CDN), so
    /// metadata reads follow up to three HTTPS hops here and fail closed on anything else.
    /// </summary>
    private async Task<HttpResponseMessage> SendMetadataAsync(Uri uri, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var hop = 0; hop < 4; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("VeyonCampus/" + typeof(ApplicationReleaseClient).Assembly.GetName().Version);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) ||
                response.Headers.Location is not { } location)
                return response;

            Uri next;
            try { next = location.IsAbsoluteUri ? location : new Uri(current, location); }
            finally { response.Dispose(); }
            if (next.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(next.UserInfo) ||
                !string.IsNullOrEmpty(next.Fragment))
                throw new InvalidDataException("版本目录重定向必须指向无凭据的 HTTPS 地址。");
            current = next;
        }
        throw new InvalidDataException("版本目录重定向次数过多。");
    }

    private sealed record ReleaseIndexResponse(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] IReadOnlyList<ReleaseIndexAsset>? Assets);

    private sealed record ReleaseIndexAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl);

    private sealed record ReleaseCatalogDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("tag")] string? Tag,
        [property: JsonPropertyName("roles")] Dictionary<string, ReleaseCatalogRoleEntry?>? Roles);

    private sealed record ReleaseCatalogRoleEntry(
        [property: JsonPropertyName("release")] ApplicationReleaseEnvelope? Release,
        [property: JsonPropertyName("studentAgentTarget")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ApplicationReleaseAgentTargetEnvelope? StudentAgentTarget,
        [property: JsonPropertyName("mirrors")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Mirrors);
}
