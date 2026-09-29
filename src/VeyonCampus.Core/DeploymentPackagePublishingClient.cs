using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>Publishes validated campus configuration packages to the public catalog.</summary>
public sealed class DeploymentPackagePublishingClient
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Uri _apiBaseAddress;

    public DeploymentPackagePublishingClient()
        : this(DeploymentPackageApiConfiguration.GetApiBaseAddress())
    {
    }

    internal DeploymentPackagePublishingClient(Uri apiBaseAddress)
    {
        _apiBaseAddress = apiBaseAddress;
    }

    public async Task<IReadOnlyList<DeploymentPackagePublishableCampus>> GetActiveCampusesAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_apiBaseAddress, "v1/deployment-package-campuses"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync(response, "读取云端校区失败", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<PublishableCampusesResponse>(stream, JsonOptions, cancellationToken);
        var campuses = result?.Items ?? throw new InvalidDataException("发布服务返回的云端校区列表格式无效。");
        if (campuses.Any(campus => campus.CampusId <= 0 || string.IsNullOrWhiteSpace(campus.CampusName)))
            throw new InvalidDataException("发布服务返回了无效的校区资料。");
        return campuses;
    }

    public async Task<DeploymentPackagePublishedResult> PublishAsync(
        string campusName,
        string teacherName,
        string mobileLast4,
        string packageDirectory,
        CancellationToken cancellationToken = default)
    {
        campusName = campusName?.Trim() ?? "";
        teacherName = teacherName?.Trim() ?? "";
        if (campusName.Length is < 1 or > 100 || campusName == "智学前程-" || campusName.Any(char.IsControl))
            throw new InvalidDataException("校区名称必须为 1–100 个字符，且不能包含控制字符。");
        if (teacherName.Length is < 1 or > 32 || teacherName.Any(char.IsControl))
            throw new InvalidDataException("老师姓名必须为 1–32 个字符，且不能包含控制字符。");
        if (mobileLast4 is not { Length: 4 } || !mobileLast4.All(char.IsAsciiDigit))
            throw new InvalidDataException("手机号后四位必须是 4 位数字。");
        if (string.IsNullOrWhiteSpace(packageDirectory)) throw new InvalidDataException("请选择学生校区配置包文件夹。");

        var package = PackageManifest.Load(packageDirectory);
        if (package.SchemaVersion != 3 || package.WebsitePolicyPublicKeyPath is null)
            throw new InvalidDataException("云端目录只接受当前 schemaVersion=3 的学生校区配置包，请重新生成配置包。");

        // Create() snapshots the fixed public files, validates their hashes, and omits README or other files.
        var archiveBytes = CampusConfigurationArchive.Create(packageDirectory);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(campusName), "campusName");
        form.Add(new StringContent(teacherName), "teacherName");
        form.Add(new StringContent(mobileLast4), "mobileLast4");
        var archive = new ByteArrayContent(archiveBytes);
        archive.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(archive, "archive", "campus-package.zip");

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(_apiBaseAddress, "v1/deployment-packages"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = form;
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync(response, "发布校区配置包失败", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<DeploymentPackagePublishedResult>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("发布服务没有返回有效的发布结果。");
    }

    public async Task<IReadOnlyList<DeploymentPackageCatalogEntry>> FindMyPublishedPackagesAsync(
        string teacherName,
        string mobileLast4,
        CancellationToken cancellationToken = default)
    {
        teacherName = teacherName?.Trim() ?? "";
        if (teacherName.Length is < 1 or > 32 || teacherName.Any(char.IsControl))
            throw new InvalidDataException("老师姓名必须为 1–32 个字符，且不能包含控制字符。");
        if (mobileLast4 is not { Length: 4 } || !mobileLast4.All(char.IsAsciiDigit))
            throw new InvalidDataException("手机号后四位必须是 4 位数字。");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(_apiBaseAddress, "v1/deployment-packages/mine"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new { teacherName, mobileLast4 });
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync(response, "读取我的发布记录失败", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<MyPackagesResponse>(stream, JsonOptions, cancellationToken);
        return result?.Items ?? throw new InvalidDataException("发布服务返回的我的发布记录格式无效。");
    }

    private static async Task<HttpRequestException> CreateApiExceptionAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var detail = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "云端仍在运行旧版发布服务；请部署免登录发布 API。",
            HttpStatusCode.Forbidden => "手机号后四位不正确，或云端服务拒绝了此次操作。",
            HttpStatusCode.NotFound => "免登录发布 API 尚未部署，或没有找到匹配的已发布包。",
            HttpStatusCode.Conflict => "这个配置包已经发布过。请重新生成学生配置包后再发布。",
            HttpStatusCode.BadRequest => "请求未通过服务端校验，请确认校区名称、教师信息和配置内容。",
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway => "云端发布服务暂时不可用，请稍后重试。",
            _ => $"{operation}，服务返回 HTTP {(int)response.StatusCode}。"
        };
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            foreach (var field in new[] { "error", "detail", "error_description" })
            {
                if (json.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    detail = value.GetString()!;
                    break;
                }
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new HttpRequestException(detail, null, response.StatusCode);
    }

    private sealed record PublishableCampusesResponse(
        [property: JsonPropertyName("items")] List<DeploymentPackagePublishableCampus>? Items);

    private sealed record MyPackagesResponse(
        [property: JsonPropertyName("items")] List<DeploymentPackageCatalogEntry>? Items);
}

public sealed record DeploymentPackagePublishableCampus(
    [property: JsonPropertyName("campusId")] long CampusId,
    [property: JsonPropertyName("campusName")] string CampusName)
{
    public string DisplayName => $"{CampusName} · VC-{CampusId:D6}";
}

public sealed record DeploymentPackagePublishedResult(
    [property: JsonPropertyName("packageId")] Guid PackageId,
    [property: JsonPropertyName("campusId")] long? CampusId,
    [property: JsonPropertyName("campusName")] string CampusName,
    [property: JsonPropertyName("computerPrefix")] string ComputerPrefix,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sizeBytes")] int SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);
