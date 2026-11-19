using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>
/// Reads the public deployment-package catalogue and downloads validated package archives.
/// </summary>
/// <remarks>
/// Discovery moved off CloudBase. The catalogue is now a small JSON document committed next to the
/// archives, so a client needs one GET against GitCode instead of a database-backed search. The old
/// HTTP API stays reachable as a fallback until every installation has been updated; the static
/// source is tried first because it is the only one that still answers when the environment is
/// isolated. Downloaded bytes are checked against the SHA-256 the catalogue advertises before they
/// reach <see cref="CampusConfigurationArchive"/>.
/// </remarks>
public sealed class DeploymentPackageCatalogClient
{
    private const int MaximumCatalogBytes = 1024 * 1024;
    private const int CatalogPageSize = 20;

    /// <summary>Per-source budget: a network that blackholes one host must not eat the whole retry.</summary>
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    /// <summary>
    /// Archives are only accepted from the two hosting providers this project publishes to. The
    /// catalogue itself is unsigned, so the host check is what stops a tampered document from
    /// pointing a download at an arbitrary server.
    /// </summary>
    private static readonly string[] AllowedDownloadHosts =
    [
        "api.gitcode.com",
        "gitcode.com",
        "raw.gitcode.com",
        "github.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com"
    ];

    private readonly HttpClient _client;
    private readonly IReadOnlyList<DeploymentCatalogSource> _catalogSources;
    private readonly Uri? _apiBaseAddress;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DeploymentPackageCatalogEntry> _recentEntries = new();
    /// <summary>
    /// Entries whose bytes only the CloudBase API can serve. The static catalogue has no download URL
    /// for a package it never heard of, so the download has to go back through the authenticated POST.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _cloudOnlyEntries = new();

    public DeploymentPackageCatalogClient()
        : this(DeploymentCatalogConfiguration.Resolve(),
            DeploymentCatalogConfiguration.ApiFallbackEnabled ? DeploymentPackageApiConfiguration.GetApiBaseAddress() : null,
            SharedDownloadClient)
    {
    }

    internal DeploymentPackageCatalogClient(
        IReadOnlyList<DeploymentCatalogSource> catalogSources, Uri? apiBaseAddress, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(catalogSources);
        ArgumentNullException.ThrowIfNull(client);
        _catalogSources = catalogSources;
        _apiBaseAddress = apiBaseAddress;
        _client = client;
    }

    private static HttpClient SharedDownloadClient { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The static sources this instance reads from, in order. Empty when disabled.</summary>
    internal IReadOnlyList<DeploymentCatalogSource> CatalogSources => _catalogSources;

    internal Uri? ApiBaseAddress => _apiBaseAddress;

    private bool CanUseApi => _apiBaseAddress is not null;

    public async Task<DeploymentPackageCatalogSearchResult> SearchAsync(
        string query, long? campusId = null, CancellationToken cancellationToken = default)
    {
        query ??= "";
        if (query.Length > 100 || query.Any(char.IsControl))
            throw new InvalidDataException("搜索词最多 100 个字符，不能包含控制字符。");
        if (campusId is <= 0)
            throw new InvalidDataException("校区编号必须是正整数。");

        var staticResult = await TrySearchCatalogAsync(query, campusId, cancellationToken).ConfigureAwait(false);
        if (staticResult is null)
        {
            if (!CanUseApi) throw new HttpRequestException("配置包目录暂时不可用，请检查网络后重试。");
            var cloudOnly = await SearchApiAsync(query, campusId, cancellationToken).ConfigureAwait(false);
            foreach (var item in cloudOnly.Items)
            {
                _recentEntries[item.PackageId] = item;
                _cloudOnlyEntries[item.PackageId] = 0;
            }
            return cloudOnly;
        }

        // The static catalogue is authoritative for what it lists - including withdrawals - but a
        // package published from a teacher machine only exists in the cloud. Merging by id keeps both
        // true: cloud-only packages become searchable, and a withdrawn entry never comes back.
        var items = staticResult.Result.Items.ToList();
        if (CanUseApi)
        {
            try
            {
                var cloud = await SearchApiAsync(query, campusId, cancellationToken).ConfigureAwait(false);
                foreach (var item in cloud.Items)
                {
                    if (staticResult.KnownIds.Contains(item.PackageId)) continue;
                    if (items.Any(existing => existing.PackageId == item.PackageId)) continue;
                    items.Add(item);
                    _recentEntries[item.PackageId] = item;
                    // No public archive URL exists for this one, so the download has to go back
                    // through the authenticated API even though it is shown in the same list.
                    _cloudOnlyEntries[item.PackageId] = 0;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              InvalidDataException or JsonException)
            {
                // The cloud is an addition, not a dependency: a school network that blocks the API
                // still gets everything the static catalogue carries.
            }
        }
        var ordered = items
            .OrderByDescending(item => item.PublishedAt)
            .ThenBy(item => item.PackageId)
            .ToList();
        var hasMore = staticResult.Result.HasMore || ordered.Count > CatalogPageSize;
        return new DeploymentPackageCatalogSearchResult(ordered.Take(CatalogPageSize).ToList(), CatalogPageSize, 0,
            hasMore);
    }

    public async Task<byte[]> DownloadAsync(
        Guid packageId,
        string teacherPhoneLast4,
        CancellationToken cancellationToken = default)
    {
        if (packageId == Guid.Empty) throw new InvalidDataException("部署包编号无效。");
        if (teacherPhoneLast4 is not { Length: 4 } || !teacherPhoneLast4.All(char.IsAsciiDigit))
            throw new InvalidDataException("教师手机号后四位必须是 4 位数字。");

        var (entry, catalogAnswered) = await FindEntryAsync(packageId, cancellationToken).ConfigureAwait(false);
        if (entry is not null)
        {
            // A package the static catalogue never listed has no public archive URL; its bytes live
            // behind the authenticated API, so the phone check still applies.
            if (_cloudOnlyEntries.ContainsKey(entry.PackageId))
                return await DownloadFromApiAsync(packageId, teacherPhoneLast4, cancellationToken).ConfigureAwait(false);
            return await DownloadFromCatalogAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        if (catalogAnswered || !CanUseApi)
            throw new InvalidDataException("找不到此部署包，可能已撤回。");
        return await DownloadFromApiAsync(packageId, teacherPhoneLast4, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ static catalogue

    /// <summary>
    /// Returns null when no source could answer, so the caller can fall back. A source that answers
    /// with an empty catalogue is authoritative: falling through to the retired API would resurrect
    /// packages a teacher deliberately withdrew.
    /// </summary>
    /// <summary>
    /// What one static source said: the page the caller asked for, plus every id the document lists.
    /// The full id set is what keeps a cloud-only merge from resurrecting a withdrawn package.
    /// </summary>
    private sealed record CatalogSearchOutcome(
        DeploymentPackageCatalogSearchResult Result, IReadOnlySet<Guid> KnownIds);

    private async Task<CatalogSearchOutcome?> TrySearchCatalogAsync(
        string query, long? campusId, CancellationToken cancellationToken)
    {
        if (_catalogSources.Count == 0) return null;
        var failures = new List<string>();
        foreach (var source in _catalogSources)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SourceTimeout);
            DeploymentCatalogDocument? document;
            try
            {
                document = await ReadCatalogAsync(source, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsSourceFailure(exception, cancellationToken))
            {
                failures.Add($"{source}：{exception.Message}");
                continue;
            }
            if (document is null) continue;

            var needle = query.Trim();
            var all = document.Packages ?? [];
            var knownIds = all.Select(entry => entry.PackageId).ToHashSet();
            var matched = all
                .Where(entry => !entry.IsWithdrawn)
                .Where(entry => campusId is null || entry.CampusId == campusId)
                .Where(entry => Matches(entry, needle))
                .OrderByDescending(entry => entry.PublishedAt)
                .ThenByDescending(entry => entry.PackageId)
                .ToList();
            var page = matched.Take(CatalogPageSize + 1).ToList();
            var hasMore = page.Count > CatalogPageSize;
            if (hasMore) page.RemoveAt(page.Count - 1);
            var items = page.Select(entry => entry.ToCatalogEntry()).ToList();
            foreach (var item in items) _recentEntries[item.PackageId] = item;
            return new CatalogSearchOutcome(
                new DeploymentPackageCatalogSearchResult(items, CatalogPageSize, 0, hasMore), knownIds);
        }
        if (failures.Count == 0) return null;
        // Every source failed. Hand over to the API when one is configured; otherwise the caller has
        // nothing left to try and needs the per-source detail to tell a blackholed host from a 404.
        if (CanUseApi) return null;
        throw new HttpRequestException("配置包目录暂时不可用：" + string.Join("；", failures));
    }

    private static bool Matches(CatalogPackageEntry entry, string needle)
    {
        if (needle.Length == 0) return true;
        return entry.CampusName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            entry.ComputerPrefix.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Looks one package up by id. The search result cache answers the normal download flow; a miss
    /// re-reads the catalogue so a caller that only has an id still works.
    /// </summary>
    private async Task<(DeploymentPackageCatalogEntry? Entry, bool CatalogAnswered)> FindEntryAsync(
        Guid packageId, CancellationToken cancellationToken)
    {
        if (_recentEntries.TryGetValue(packageId, out var cached)) return (cached, true);
        if (_catalogSources.Count == 0) return (null, false);

        var answered = false;
        foreach (var source in _catalogSources)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SourceTimeout);
            DeploymentCatalogDocument? document;
            try
            {
                document = await ReadCatalogAsync(source, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsSourceFailure(exception, cancellationToken))
            {
                continue;
            }
            if (document is null) continue;
            answered = true;
            var found = (document.Packages ?? [])
                .FirstOrDefault(entry => entry.PackageId == packageId && !entry.IsWithdrawn);
            if (found is not null) return (found.ToCatalogEntry(), true);
        }
        return (null, answered);
    }

    private async Task<DeploymentCatalogDocument?> ReadCatalogAsync(
        DeploymentCatalogSource source, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.CatalogUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("VeyonCampus/" + typeof(DeploymentPackageCatalogClient).Assembly.GetName().Version);
        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"返回 HTTP {(int)response.StatusCode}。", null, response.StatusCode);
        var bytes = await ReadBoundedAsync(response.Content, MaximumCatalogBytes, cancellationToken).ConfigureAwait(false);
        DeploymentCatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<DeploymentCatalogDocument>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("配置包目录格式无效。", exception);
        }
        if (document is null) throw new InvalidDataException("配置包目录内容为空。");
        if (document.SchemaVersion != 1)
            throw new InvalidDataException($"配置包目录版本 {document.SchemaVersion} 不受支持。");
        return document;
    }

    private async Task<byte[]> DownloadFromCatalogAsync(
        DeploymentPackageCatalogEntry entry, CancellationToken cancellationToken)
    {
        var urls = new List<string> { entry.DownloadUrl };
        // A catalogue entry may omit mirrors entirely; that is not a failure, just one URL.
        if (entry.Mirrors is { Count: > 0 }) urls.AddRange(entry.Mirrors);
        var failures = new List<string>();
        foreach (var candidate in urls)
        {
            if (!IsAllowedDownloadUrl(candidate, out var uri))
            {
                failures.Add($"忽略可疑下载地址：{candidate}");
                continue;
            }
            try
            {
                var bytes = await DownloadArchiveAsync(uri, cancellationToken).ConfigureAwait(false);
                if (bytes.Length != entry.SizeBytes)
                    throw new InvalidDataException(
                        $"下载大小 {bytes.Length:N0} 字节与目录登记的 {entry.SizeBytes:N0} 字节不一致。");
                var digest = Convert.ToHexString(SHA256.HashData(bytes));
                if (!string.Equals(digest, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载内容与目录登记的 SHA-256 不一致，已停止保存。");
                return bytes;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or
                                                  InvalidDataException or TaskCanceledException &&
                                                  !cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{uri.Host}：{exception.Message}");
            }
        }
        throw new HttpRequestException("配置包下载失败：" +
            (failures.Count > 0 ? string.Join("；", failures) : "目录没有可用的下载地址。"));
    }

    private async Task<byte[]> DownloadArchiveAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("VeyonCampus/" + typeof(DeploymentPackageCatalogClient).Assembly.GetName().Version);
        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound)
            throw new HttpRequestException("配置包文件不存在，可能尚未推送。", null, response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"配置包文件返回 HTTP {(int)response.StatusCode}。", null, response.StatusCode);
        if (response.Content.Headers.ContentLength is > CampusConfigurationArchive.MaximumArchiveBytes)
            throw new InvalidDataException("下载的校区配置包超过 64 KiB，已停止保存。");
        return await ReadBoundedAsync(response.Content, CampusConfigurationArchive.MaximumArchiveBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAllowedDownloadUrl(string value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.Query.Length > 200)
            return false;
        // A classroom teacher machine serves over plain HTTP on a private address. Anything else has
        // to be HTTPS on one of the publishing hosts.
        if (parsed.Scheme == Uri.UriSchemeHttp)
        {
            if (!DeploymentPackageLanDistribution.IsLanPackageUri(parsed)) return false;
            uri = parsed;
            return true;
        }
        if (parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (!AllowedDownloadHosts.Contains(parsed.Host, StringComparer.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (target.Length + read > maximumBytes)
                throw new InvalidDataException($"内容超过 {maximumBytes:N0} 字节上限，已停止读取。");
            target.Write(buffer, 0, read);
        }
        if (target.Length == 0) throw new InvalidDataException("下载内容为空。");
        return target.ToArray();
    }

    private static bool IsSourceFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException ||
        exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    // ------------------------------------------------------------------ retired HTTP API

    private async Task<DeploymentPackageCatalogSearchResult> SearchApiAsync(
        string query, long? campusId, CancellationToken cancellationToken)
    {
        var queryParts = new List<string> { "limit=20", "offset=0" };
        if (query.Length > 0) queryParts.Add("query=" + Uri.EscapeDataString(query));
        if (campusId is not null) queryParts.Add("campusId=" + campusId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_apiBaseAddress!, "v1/deployment-packages?" + string.Join('&', queryParts)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<DeploymentPackageCatalogSearchResult>(
                         stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidDataException("网站返回的部署包目录格式无效。");
        // No caching here on purpose: a cloud entry carries no download URL, so caching it would
        // shadow the usable static entry for the same id. Callers cache only what they actually show.
        return result;
    }

    private async Task<byte[]> DownloadFromApiAsync(
        Guid packageId, string teacherPhoneLast4, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(_apiBaseAddress!, $"v1/deployment-packages/{packageId:D}/download"));
        request.Content = JsonContent.Create(new { teacherPhoneLast4 });
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.Content.Headers.ContentLength is > CampusConfigurationArchive.MaximumArchiveBytes)
            throw new InvalidDataException("下载的校区配置包超过 64 KiB，已停止保存。");
        return await ReadBoundedAsync(response.Content, CampusConfigurationArchive.MaximumArchiveBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HttpRequestException> CreateApiExceptionAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        var detail = status switch
        {
            HttpStatusCode.NotFound => "找不到此部署包，可能已撤回。",
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway => "云端部署包服务暂时不可用。",
            HttpStatusCode.Forbidden => "教师手机号后四位不正确，或此配置包已撤回。",
            HttpStatusCode.TooManyRequests => "校验失败次数过多，请稍后再试。",
            _ => $"部署包服务返回 HTTP {(int)status}。"
        };
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(error.GetString()))
                detail = error.GetString()!;
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new HttpRequestException(detail, null, status);
    }

    // ------------------------------------------------------------------ catalogue document

    private sealed record DeploymentCatalogDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("generatedAt")] string? GeneratedAt,
        [property: JsonPropertyName("packages")] IReadOnlyList<CatalogPackageEntry>? Packages);

    private sealed record CatalogPackageEntry(
        [property: JsonPropertyName("packageId")] Guid PackageId,
        [property: JsonPropertyName("campusId")] long? CampusId,
        [property: JsonPropertyName("campusName")] string CampusName,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("computerPrefix")] string ComputerPrefix,
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("targetOs")] string TargetOs,
        [property: JsonPropertyName("architecture")] string Architecture,
        [property: JsonPropertyName("fileName")] string FileName,
        [property: JsonPropertyName("sizeBytes")] int SizeBytes,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
        [property: JsonPropertyName("mirrors")] IReadOnlyList<string>? Mirrors,
        [property: JsonPropertyName("downloadCount")] long DownloadCount,
        [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt,
        [property: JsonPropertyName("requiresPhoneLast4")] bool RequiresPhoneLast4,
        [property: JsonPropertyName("status")] string? Status)
    {
        public bool IsWithdrawn => string.Equals(Status, "withdrawn", StringComparison.OrdinalIgnoreCase);

        public DeploymentPackageCatalogEntry ToCatalogEntry() => new(
            PackageId,
            CampusId,
            string.IsNullOrWhiteSpace(DisplayName) ? CampusName : DisplayName,
            CampusName,
            ComputerPrefix,
            SchemaVersion,
            string.IsNullOrWhiteSpace(TargetOs) ? "windows" : TargetOs,
            string.IsNullOrWhiteSpace(Architecture) ? "x64" : Architecture,
            string.IsNullOrWhiteSpace(FileName) ? "campus-package.zip" : FileName,
            SizeBytes,
            Sha256,
            DownloadCount,
            PublishedAt,
            RequiresPhoneLast4,
            DownloadUrl,
            Mirrors ?? []);
    }
}

internal static class DeploymentPackageApiConfiguration
{
    private const string DefaultApiAddress = "https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com/";
    private const string DefaultCloudBaseEnvironmentId = "veyon-control-d3gs8hmuyd09c00a7";

    public static Uri GetApiBaseAddress()
    {
        var configuredAddress = Environment.GetEnvironmentVariable("VEYONCAMPUS_DEPLOYMENT_PACKAGES_API_BASE_URL");
        var address = string.IsNullOrWhiteSpace(configuredAddress)
            ? DefaultApiAddress
            : configuredAddress.Trim().TrimEnd('/') + "/";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) || !IsAllowedAddress(parsed))
            throw new InvalidOperationException("部署包目录 API 地址必须使用 HTTPS。开发环境仅允许回环地址使用 HTTP。");
        return parsed;
    }

    public static Uri GetCloudBaseApiBaseAddress()
    {
        var environmentId = GetCloudBaseEnvironmentId();
        return new Uri($"https://{environmentId}.api.tcloudbasegateway.com/");
    }

    public static string GetCloudBaseEnvironmentId()
    {
        var environmentId = Environment.GetEnvironmentVariable("VEYONCAMPUS_CLOUDBASE_ENV_ID");
        environmentId = string.IsNullOrWhiteSpace(environmentId)
            ? DefaultCloudBaseEnvironmentId
            : environmentId.Trim();
        if (environmentId.Length is < 1 or > 64 ||
            environmentId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new InvalidOperationException("CloudBase 环境 ID 格式无效。");

        return environmentId;
    }

    private static bool IsAllowedAddress(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps ||
        address.Scheme == Uri.UriSchemeHttp && address.IsLoopback;
}

public sealed record DeploymentPackageCatalogSearchResult(
    [property: JsonPropertyName("items")] List<DeploymentPackageCatalogEntry> Items,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("hasMore")] bool HasMore);

public sealed record DeploymentPackageCatalogEntry(
    [property: JsonPropertyName("packageId")] Guid PackageId,
    [property: JsonPropertyName("campusId")] long? CampusId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("campusName")] string CampusName,
    [property: JsonPropertyName("computerPrefix")] string ComputerPrefix,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("targetOs")] string TargetOs,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sizeBytes")] int SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("downloadCount")] long DownloadCount,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt,
    [property: JsonPropertyName("requiresPhoneLast4")] bool RequiresPhoneLast4,
    [property: JsonPropertyName("downloadUrl")] string DownloadUrl = "",
    [property: JsonPropertyName("mirrors")] IReadOnlyList<string>? Mirrors = null)
{
    public string Summary => $"{CampusName} · {PublishedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {SizeBytes:N0} 字节";
}
