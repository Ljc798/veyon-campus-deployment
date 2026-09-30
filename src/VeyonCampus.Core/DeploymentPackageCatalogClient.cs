using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>Reads the public deployment package catalog and downloads validated package archives.</summary>
public sealed class DeploymentPackageCatalogClient
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Uri _baseAddress;

    public DeploymentPackageCatalogClient()
    {
        _baseAddress = DeploymentPackageApiConfiguration.GetApiBaseAddress();
    }

    internal Uri BaseAddress => _baseAddress;

    public async Task<DeploymentPackageCatalogSearchResult> SearchAsync(
        string query, long? campusId = null, CancellationToken cancellationToken = default)
    {
        query ??= "";
        if (query.Length > 100 || query.Any(char.IsControl))
            throw new InvalidDataException("搜索词最多 100 个字符，不能包含控制字符。");
        if (campusId is <= 0)
            throw new InvalidDataException("校区编号必须是正整数。");

        var queryParts = new List<string> { "limit=20", "offset=0" };
        if (query.Length > 0) queryParts.Add("query=" + Uri.EscapeDataString(query));
        if (campusId is not null) queryParts.Add("campusId=" + campusId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_baseAddress, "v1/deployment-packages?" + string.Join('&', queryParts)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<DeploymentPackageCatalogSearchResult>(
                   stream, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("网站返回的部署包目录格式无效。");
    }

    public async Task<byte[]> DownloadAsync(
        Guid packageId,
        string teacherPhoneLast4,
        CancellationToken cancellationToken = default)
    {
        if (packageId == Guid.Empty) throw new InvalidDataException("部署包编号无效。");
        if (teacherPhoneLast4 is not { Length: 4 } || !teacherPhoneLast4.All(char.IsAsciiDigit))
            throw new InvalidDataException("教师手机号后四位必须是 4 位数字。");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(_baseAddress, $"v1/deployment-packages/{packageId:D}/download"));
        request.Content = JsonContent.Create(new { teacherPhoneLast4 });
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync(response, cancellationToken);

        if (response.Content.Headers.ContentLength is > CampusConfigurationArchive.MaximumArchiveBytes)
            throw new InvalidDataException("下载的校区配置包超过 64 KiB，已停止保存。");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (target.Length + read > CampusConfigurationArchive.MaximumArchiveBytes)
                throw new InvalidDataException("下载的校区配置包超过 64 KiB，已停止保存。");
            target.Write(buffer, 0, read);
        }
        if (target.Length == 0) throw new InvalidDataException("网站返回了空的校区配置包。");
        return target.ToArray();
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
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(error.GetString()))
                detail = error.GetString()!;
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new HttpRequestException(detail, null, status);
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
    [property: JsonPropertyName("requiresPhoneLast4")] bool RequiresPhoneLast4)
{
    public string Summary => $"{CampusName} · {ComputerPrefix} · {PublishedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm} · {SizeBytes:N0} 字节";
}
