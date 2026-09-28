using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024);
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

var statsToken = builder.Configuration["Telemetry:StatsBearerToken"];
if (!string.IsNullOrEmpty(statsToken) && Encoding.UTF8.GetByteCount(statsToken) < 32)
    throw new InvalidOperationException("Telemetry:StatsBearerToken 至少需要 32 个 UTF-8 字节。");

builder.Services.AddSingleton(new DailyHeartbeatStore(dailyHashKey));
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

app.MapPost("/v1/heartbeat", (HeartbeatRequest request, DailyHeartbeatStore store) =>
{
    if (request.InstallationId is not { Length: 32 } installationId ||
        !installationId.All(Uri.IsHexDigit))
        return (IResult)Results.BadRequest(new { error = "installationId must be a 32-character hexadecimal value" });

    if (!store.TryRecord(installationId)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    return Results.NoContent();
});

app.MapGet("/v1/stats/today", (HttpContext context, DailyHeartbeatStore store) =>
{
    if (string.IsNullOrEmpty(statsToken)) return (IResult)Results.NotFound();
    var authorization = context.Request.Headers.Authorization.ToString();
    if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
        !SecretEquals(authorization[7..], statsToken))
        return (IResult)Results.Unauthorized();

    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(store.Snapshot());
});

app.Run();

static bool SecretEquals(string candidate, string expected)
{
    var candidateBytes = Encoding.UTF8.GetBytes(candidate);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return candidateBytes.Length == expectedBytes.Length &&
           CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes);
}

sealed record HeartbeatRequest(string? InstallationId);
sealed record DailyStats(string DayUtc, int ActiveDevices, long HeartbeatSignals);

sealed class DailyHeartbeatStore(byte[] dailyHashKey)
{
    private const int MaximumDistinctDevicesPerDay = 100_000;
    private readonly byte[] _dailyHashKey = dailyHashKey.ToArray();
    private readonly object _gate = new();
    private readonly HashSet<string> _deviceDigests = new(StringComparer.Ordinal);
    private DateOnly _day = DateOnly.FromDateTime(DateTime.UtcNow);
    private long _heartbeatSignals;

    public bool TryRecord(string installationId)
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            ResetIfNewDay(today);
            var dailyKey = HMACSHA256.HashData(_dailyHashKey,
                Encoding.ASCII.GetBytes(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            var digest = Convert.ToHexString(HMACSHA256.HashData(dailyKey, Encoding.ASCII.GetBytes(installationId)));
            CryptographicOperations.ZeroMemory(dailyKey);

            if (!_deviceDigests.Contains(digest) && _deviceDigests.Count >= MaximumDistinctDevicesPerDay)
                return false;
            _deviceDigests.Add(digest);
            _heartbeatSignals++;
            return true;
        }
    }

    public DailyStats Snapshot()
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            ResetIfNewDay(today);
            return new DailyStats(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _deviceDigests.Count, _heartbeatSignals);
        }
    }

    private void ResetIfNewDay(DateOnly today)
    {
        if (today == _day) return;
        _deviceDigests.Clear();
        _heartbeatSignals = 0;
        _day = today;
    }
}
