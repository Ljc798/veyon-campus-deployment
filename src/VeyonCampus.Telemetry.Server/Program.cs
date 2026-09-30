using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

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
builder.Services.AddSingleton(new DeploymentPackageIdentityHasher(dailyHashKey));
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
builder.Services.AddSingleton<DeploymentPackageDownloadAttemptLimiter>();
builder.Services.AddSingleton(serviceProvider => new CloudBaseDeploymentPackageStore(
    serviceProvider.GetRequiredService<IHttpClientFactory>(),
    cloudBaseEnvId,
    cloudBaseApiKey,
    packageBucketId,
    serviceProvider.GetRequiredService<DeploymentPackageIdentityHasher>()));

var app = builder.Build();
app.Use(async (context, next) =>
{
    var requestPath = context.Request.Path;
    var isSensitiveApiResponse = context.Request.Method == HttpMethods.Post &&
        requestPath.StartsWithSegments("/v1/deployment-packages");
    if (isSensitiveApiResponse)
        context.Response.Headers["Cache-Control"] = "no-store";

    await next();
});
app.UseCors();

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
    var applicationVersion = request.ApplicationVersion ?? "unknown";
    if (applicationVersion.Length > 64 ||
        !Regex.IsMatch(applicationVersion, @"\A(?:unknown|[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:[-+][0-9A-Za-z.-]+)?)\z", RegexOptions.CultureInvariant))
        return Results.BadRequest(new { error = "applicationVersion must be a semantic numeric version" });
    if (request.DeploymentId == Guid.Empty)
        return Results.BadRequest(new { error = "deploymentId must be a non-empty GUID when supplied" });

    var hkDay = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);
    var digest = hasher.CreateDigest(hkDay, installationId);
    var deploymentDigest = request.DeploymentId is { } deploymentId
        ? hasher.CreateDeploymentDigest(hkDay, installationId, deploymentId)
        : null;
    try
    {
        await store.RecordAsync(hkDay, digest, deploymentDigest, applicationVersion,
            request.DeploymentId, cancellationToken);
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

sealed record HeartbeatRequest(string? InstallationId, string? ApplicationVersion, Guid? DeploymentId);

sealed class DailyHeartbeatHasher(byte[] dailyHashKey)
{
    private readonly byte[] _dailyHashKey = dailyHashKey.ToArray();

    public string CreateDigest(DateOnly hkDay, string installationId) =>
        CreateDigest(hkDay, Encoding.ASCII.GetBytes(installationId));

    public string CreateDeploymentDigest(DateOnly hkDay, string installationId, Guid deploymentId) =>
        CreateDigest(hkDay, Encoding.UTF8.GetBytes($"deployment:{deploymentId:N}:{installationId}"));

    private string CreateDigest(DateOnly hkDay, byte[] message)
    {
        var dayKey = Encoding.ASCII.GetBytes(hkDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var dailyKey = HMACSHA256.HashData(_dailyHashKey, dayKey);
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(dailyKey, message));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dailyKey);
            CryptographicOperations.ZeroMemory(dayKey);
            CryptographicOperations.ZeroMemory(message);
        }
    }
}

sealed class DeploymentPackageIdentityHasher(byte[] identityKey) : IDisposable
{
    private readonly byte[] _identityKey = identityKey.ToArray();

    public string CreatePhoneLast4Fingerprint(string teacherPhoneLast4) => Fingerprint("download-phone", teacherPhoneLast4);

    public string CreatePublisherFingerprintForUser(string userId) =>
        Fingerprint("publisher-user", userId);

    public string CreatePublisherFingerprintForName(string publisherName) =>
        Fingerprint("publisher-name", publisherName.Normalize(NormalizationForm.FormKC).Trim());

    private string Fingerprint(string purpose, string value)
    {
        var payload = Encoding.UTF8.GetBytes("VeyonCampus/DeploymentPackages/" + purpose + "/v1\n" + value);
        try { return Convert.ToHexString(HMACSHA256.HashData(_identityKey, payload)); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_identityKey);
}

sealed class CloudBasePgTelemetryStore(HttpClient httpClient, string envId, string apiKey)
{
    private readonly Uri _endpoint = new($"https://{envId}.api.tcloudbasegateway.com/v1/rdb/rest/rpc/record_telemetry_heartbeat_v2");

    public async Task RecordAsync(DateOnly hkDay, string digest, string? deploymentDigest,
        string applicationVersion, Guid? deploymentId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
        request.Content = JsonContent.Create(new
        {
            p_day_hkt = hkDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            p_installation_digest = digest,
            p_deployment_digest = deploymentDigest,
            p_application_version = applicationVersion,
            p_deployment_id = deploymentId
        });

        using var response = await httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("CloudBase PostgreSQL rejected the heartbeat.", null, response.StatusCode);
    }
}
