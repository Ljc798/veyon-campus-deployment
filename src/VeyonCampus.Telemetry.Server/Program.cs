using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
var portText = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(portText))
{
    if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        throw new InvalidOperationException("PORT 必须是 1–65535 之间的有效端口。");

    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 128 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 128 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);

var dailyHashKeyText = builder.Configuration["Telemetry:DailyHashKey"];
if (string.IsNullOrWhiteSpace(dailyHashKeyText))
    throw new InvalidOperationException("配置 Telemetry:DailyHashKey（至少 32 字节的 Base64 密钥）后才能启动。");

byte[] dailyHashKey;
try { dailyHashKey = Convert.FromBase64String(dailyHashKeyText); }
catch (FormatException exception)
{
    throw new InvalidOperationException("Telemetry:DailyHashKey 必须是 Base64。", exception);
}
if (dailyHashKey.Length < 32)
    throw new InvalidOperationException("Telemetry:DailyHashKey 解码后至少需要 32 字节。");

var cloudBaseEnvId = builder.Configuration["CloudBase:EnvId"]?.Trim();
var cloudBaseApiKey = builder.Configuration["CloudBase:ApiKey"]?.Trim();
if (string.IsNullOrWhiteSpace(cloudBaseEnvId) || string.IsNullOrWhiteSpace(cloudBaseApiKey))
    throw new InvalidOperationException("必须通过 CloudBase:EnvId 和 CloudBase:ApiKey 配置 PostgreSQL HTTP API；API Key 只能放在服务端密钥配置中。");
if (!cloudBaseEnvId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
    throw new InvalidOperationException("CloudBase:EnvId 格式无效。");

builder.Services.AddSingleton(new DailyHeartbeatHasher(dailyHashKey));
CryptographicOperations.ZeroMemory(dailyHashKey);
builder.Services.AddHttpClient("CloudBasePg", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton(serviceProvider => new CloudBasePgTelemetryStore(
    serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("CloudBasePg"),
    cloudBaseEnvId,
    cloudBaseApiKey));
var packageBucketId = builder.Configuration["CloudBase:DeploymentPackageBucket"]?.Trim();
if (string.IsNullOrWhiteSpace(packageBucketId))
    packageBucketId = DeploymentPackageEndpoints.BucketId;
if (!packageBucketId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
    throw new InvalidOperationException("CloudBase:DeploymentPackageBucket 格式无效。");
builder.Services.AddHttpClient("CloudBasePackages", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton(serviceProvider => new CloudBaseDeploymentPackageStore(
    serviceProvider.GetRequiredService<IHttpClientFactory>(),
    cloudBaseEnvId,
    cloudBaseApiKey,
    packageBucketId));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

app.MapPost("/v1/heartbeat", async (
    HeartbeatRequest request,
    DailyHeartbeatHasher hasher,
    CloudBasePgTelemetryStore store,
    CancellationToken cancellationToken) =>
{
    if (request.InstallationId is not { Length: 32 } installationId ||
        !installationId.All(Uri.IsHexDigit))
        return Results.BadRequest(new { error = "installationId must be a 32-character hexadecimal value" });

    var utcDay = DateOnly.FromDateTime(DateTime.UtcNow);
    var digest = hasher.CreateDigest(utcDay, installationId);
    try
    {
        await store.RecordAsync(utcDay, digest, cancellationToken);
        return Results.NoContent();
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException)
    {
        // Never log the request body, raw installation ID, digest, or API key.
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (TaskCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).WithMetadata(new RequestSizeLimitAttribute(2 * 1024));

DeploymentPackageEndpoints.Map(app);

app.Run();

sealed record HeartbeatRequest(string? InstallationId);

sealed class DailyHeartbeatHasher(byte[] dailyHashKey)
{
    private readonly byte[] _dailyHashKey = dailyHashKey.ToArray();

    public string CreateDigest(DateOnly utcDay, string installationId)
    {
        var dayKey = Encoding.ASCII.GetBytes(utcDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var dailyKey = HMACSHA256.HashData(_dailyHashKey, dayKey);
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(dailyKey, Encoding.ASCII.GetBytes(installationId)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dailyKey);
            CryptographicOperations.ZeroMemory(dayKey);
        }
    }
}

sealed class CloudBasePgTelemetryStore(HttpClient httpClient, string envId, string apiKey)
{
    private readonly Uri _endpoint = new($"https://{envId}.api.tcloudbasegateway.com/v1/rdb/rest/rpc/record_telemetry_heartbeat");

    public async Task RecordAsync(DateOnly utcDay, string digest, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
        request.Content = JsonContent.Create(new
        {
            p_utc_day = utcDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            p_installation_digest = digest
        });

        using var response = await httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("CloudBase PostgreSQL rejected the heartbeat.", null, response.StatusCode);
    }
}
