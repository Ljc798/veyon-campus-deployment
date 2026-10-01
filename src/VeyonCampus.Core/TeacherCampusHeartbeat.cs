using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public sealed record TeacherCampusHeartbeatState(
    bool Enabled,
    Guid? PackageId,
    string PublisherInstanceId,
    DateOnly? LastSentDay);

public static class TeacherCampusHeartbeatStateStore
{
    private const long MaximumStateBytes = 4096;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Telemetry", "teacher-heartbeat.json");

    public static TeacherCampusHeartbeatState LoadOrCreate(string? path = null)
    {
        lock (Gate)
        {
            var fullPath = GetPath(path);
            if (File.Exists(fullPath)) return Read(fullPath);
            var state = new TeacherCampusHeartbeatState(false, null,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), null);
            SaveUnsafe(fullPath, state);
            return state;
        }
    }

    public static void Save(TeacherCampusHeartbeatState state, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (Gate) SaveUnsafe(GetPath(path), Validate(state));
    }

    public static DateOnly GetHongKongDate() =>
        DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);

    public static bool IsDue(TeacherCampusHeartbeatState? state, DateOnly day) =>
        state is { Enabled: true, PackageId: not null } && state.LastSentDay != day;

    private static string GetPath(string? path) => Path.GetFullPath(path ?? DefaultPath);

    private static TeacherCampusHeartbeatState Read(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Teacher heartbeat 本机状态不能是符号链接或重解析点。");
        var info = new FileInfo(path);
        if (info.Length is < 1 or > MaximumStateBytes)
            throw new InvalidDataException("Teacher heartbeat 本机状态大小无效。");
        TeacherCampusHeartbeatState? state;
        try { state = JsonSerializer.Deserialize<TeacherCampusHeartbeatState>(File.ReadAllBytes(path), JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("Teacher heartbeat 本机状态格式无效。", exception); }
        return Validate(state ?? throw new InvalidDataException("Teacher heartbeat 本机状态为空。"));
    }

    private static TeacherCampusHeartbeatState Validate(TeacherCampusHeartbeatState state)
    {
        if (state.PackageId == Guid.Empty || state.PublisherInstanceId is not { Length: 32 } ||
            !state.PublisherInstanceId.All(Uri.IsHexDigit))
            throw new InvalidDataException("Teacher heartbeat 本机包编号或随机实例标识无效。");
        return state with { PublisherInstanceId = state.PublisherInstanceId.ToLowerInvariant() };
    }

    private static void SaveUnsafe(string path, TeacherCampusHeartbeatState state)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Teacher heartbeat 状态目录无效。");
        Directory.CreateDirectory(parent);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Teacher heartbeat 状态目录不能是符号链接或重解析点。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Teacher heartbeat 本机状态不能是符号链接或重解析点。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Validate(state), JsonOptions);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

public sealed class TeacherCampusHeartbeatClient
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Uri _endpoint;
    private readonly HttpClient _client;

    public TeacherCampusHeartbeatClient()
        : this(DeploymentPackageApiConfiguration.GetApiBaseAddress(), SharedClient)
    {
    }

    public TeacherCampusHeartbeatClient(Uri apiBaseAddress, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentNullException.ThrowIfNull(client);
        if ((apiBaseAddress.Scheme != Uri.UriSchemeHttps &&
             !(apiBaseAddress.Scheme == Uri.UriSchemeHttp && apiBaseAddress.IsLoopback)) ||
            !string.IsNullOrEmpty(apiBaseAddress.UserInfo) || !string.IsNullOrEmpty(apiBaseAddress.Query) ||
            !string.IsNullOrEmpty(apiBaseAddress.Fragment) || apiBaseAddress.AbsolutePath != "/")
            throw new InvalidDataException("Teacher heartbeat API 必须使用 HTTPS 根地址；仅本机回环地址允许 HTTP。");
        _endpoint = new Uri(apiBaseAddress, "v1/heartbeat/teacher");
        _client = client;
    }

    public async Task<bool> TrySendOnceDailyAsync(TeacherCampusHeartbeatState state,
        string teacherVersion, string studentVersion, int configuredComputerCount,
        string? statePath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Enabled || state.PackageId is null) return false;
        ValidateVersion(teacherVersion);
        ValidateVersion(studentVersion);
        if (configuredComputerCount is < 0 or > 150)
            throw new InvalidDataException("Teacher heartbeat 机房电脑总数必须为 0–150。");
        var validatedState = ValidateState(state);
        var packageId = validatedState.PackageId
                        ?? throw new InvalidDataException("Teacher heartbeat packageId is required.");
        var day = TeacherCampusHeartbeatStateStore.GetHongKongDate();
        if (!TeacherCampusHeartbeatStateStore.IsDue(validatedState, day)) return false;

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(new HeartbeatRequest(validatedState.PublisherInstanceId,
                packageId, teacherVersion, studentVersion, configuredComputerCount))
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw new HttpRequestException($"Teacher heartbeat 服务返回 HTTP {(int)response.StatusCode}。", null,
                response.StatusCode);
        TeacherCampusHeartbeatStateStore.Save(validatedState with { LastSentDay = day }, statePath);
        return true;
    }

    private static TeacherCampusHeartbeatState ValidateState(TeacherCampusHeartbeatState state)
    {
        if (state.PackageId == Guid.Empty ||
            state.PublisherInstanceId is not { Length: 32 } || !state.PublisherInstanceId.All(Uri.IsHexDigit))
            throw new InvalidDataException("Teacher heartbeat 本机包编号或随机实例标识无效。");
        return state with { PublisherInstanceId = state.PublisherInstanceId.ToLowerInvariant() };
    }

    private static void ValidateVersion(string version)
    {
        _ = ApplicationReleaseClient.CompareVersions(version, version);
    }

    private sealed record HeartbeatRequest(
        [property: JsonPropertyName("publisherInstanceId")] string PublisherInstanceId,
        [property: JsonPropertyName("packageId")] Guid PackageId,
        [property: JsonPropertyName("teacherVersion")] string TeacherVersion,
        [property: JsonPropertyName("studentVersion")] string StudentVersion,
        [property: JsonPropertyName("configuredComputerCount")] int ConfiguredComputerCount);
}
