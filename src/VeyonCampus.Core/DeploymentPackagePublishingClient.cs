using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>Publishes validated campus packages without a teacher CloudBase account.</summary>
public sealed class DeploymentPackagePublishingClient
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Uri _apiBaseAddress;

    public DeploymentPackagePublishingClient()
        : this(DeploymentPackageApiConfiguration.GetApiBaseAddress())
    {
    }

    internal DeploymentPackagePublishingClient(Uri apiBaseAddress) => _apiBaseAddress = apiBaseAddress;

    public async Task<DeploymentPackagePublishedResult> PublishAsync(
        string campusName,
        string publisherName,
        string teacherPhoneLast4,
        string packageDirectory,
        CancellationToken cancellationToken = default)
    {
        campusName = NormalizeRequired(campusName, "校区名称");
        publisherName = NormalizeRequired(publisherName, "教师姓名");
        if (teacherPhoneLast4 is not { Length: 4 } || !teacherPhoneLast4.All(char.IsAsciiDigit))
            throw new InvalidDataException("教师手机号后四位必须是 4 位数字。");
        if (string.IsNullOrWhiteSpace(packageDirectory))
            throw new InvalidDataException("请选择学生校区配置包文件夹。");

        var package = PackageManifest.Load(packageDirectory);
        if (package.SchemaVersion is not (3 or 4 or 5) || package.WebsitePolicyPublicKeyPath is null ||
            (package.SchemaVersion is 4 or 5) != (package.ApplicationPolicyPublicKeyPath is not null) ||
            (package.SchemaVersion == 5) != (package.StudentSystemPolicyPublicKeyPath is not null))
            throw new InvalidDataException("云端目录只接受 schemaVersion=3/4/5 且文件清单完整的学生配置包。");
        var packageCampus = package.Campus.Normalize(System.Text.NormalizationForm.FormKC).Trim();
        if (!string.Equals(campusName, packageCampus, StringComparison.Ordinal))
            throw new InvalidDataException($"校区名称必须与配置包中的校区名称“{packageCampus}”一致；请使用同名配置包再发布。");

        // Snapshot only the fixed public files and validate their manifest hashes.
        var archiveBytes = CampusConfigurationArchive.Create(packageDirectory);
        if (archiveBytes.Length is < 1 or > 64 * 1024)
            throw new InvalidDataException("ZIP 必须为 64 KiB 以内。");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(campusName), "campusName");
        form.Add(new StringContent(publisherName), "publisherName");
        form.Add(new StringContent(teacherPhoneLast4), "teacherPhoneLast4");
        var archive = new ByteArrayContent(archiveBytes);
        archive.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(archive, "archive", "campus-package.zip");

        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(_apiBaseAddress, "v1/deployment-packages"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = form;
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<DeploymentPackagePublishedResult>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("发布服务没有返回有效的发布结果。");
    }

    private static string NormalizeRequired(string? value, string label)
    {
        var normalized = value?.Normalize(System.Text.NormalizationForm.FormKC).Trim() ?? "";
        if (normalized.Length is < 1 or > 100 || normalized.Any(char.IsControl))
            throw new InvalidDataException($"{label}必须为 1–100 个字符。");
        return normalized;
    }

    private static async Task<HttpRequestException> CreateApiExceptionAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = response.StatusCode switch
        {
            HttpStatusCode.Conflict => "这个配置包已经发布过。请重新生成学生配置包后再发布。",
            HttpStatusCode.BadRequest => "请求未通过服务端校验，请确认校区名称和配置内容。",
            (HttpStatusCode)413 => "上传内容超过大小限制：ZIP 不超过 64 KiB，请求体不超过 128 KiB。",
            HttpStatusCode.TooManyRequests => "云端请求过于频繁，请稍后重试。",
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway => "云端发布服务暂时不可用，请稍后重试。",
            _ => $"发布配置包失败，服务返回 HTTP {(int)response.StatusCode}。"
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
        if (response.StatusCode == HttpStatusCode.BadGateway &&
            detail.Contains("CloudBase rejected package publication", StringComparison.OrdinalIgnoreCase))
        {
            detail = "CloudBase 数据库拒绝了发布事务（HTTP 502）。当前测试环境仍需部署迁移 20261001090000 和更新后的 veyon-api，再重试发布。";
        }
        return new HttpRequestException(detail, null, response.StatusCode);
    }
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
