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
    DateOnly? LastSentDay)
{
    public DateTimeOffset? FirstHeartbeatNotBeforeUtc { get; init; }
}

public static class TeacherCampusHeartbeatStateStore
{
    private const long MaximumStateBytes = 4096;
    private const string UserDisabledMarkerContent = "veyon-campus-teacher-heartbeat-disabled-v1";
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
            if (File.Exists(fullPath))
            {
                var existing = Read(fullPath);
                if (!existing.Enabled && !HasUserDisabledPreference(fullPath))
                {
                    existing = existing with { Enabled = true };
                    SaveUnsafe(fullPath, existing);
                }
                return existing;
            }
            var state = new TeacherCampusHeartbeatState(true, null,
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

    public static void SaveUserPreference(TeacherCampusHeartbeatState state, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (Gate)
        {
            var fullPath = GetPath(path);
            var validated = Validate(state);
            SaveUnsafe(fullPath, validated);
            PersistUserDisabledPreference(fullPath, disabled: !validated.Enabled);
        }
    }

    public static DateOnly GetHongKongDate() =>
        DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);

    public static TimeSpan InitialHeartbeatDelay => TimeSpan.FromHours(1);

    public static bool IsDue(TeacherCampusHeartbeatState? state, DateOnly day, DateTimeOffset? utcNow = null) =>
        state is { Enabled: true, PackageId: not null } && state.LastSentDay != day &&
        (state.FirstHeartbeatNotBeforeUtc is not { } notBefore || notBefore <= (utcNow ?? DateTimeOffset.UtcNow));

    private static string GetPath(string? path) => Path.GetFullPath(path ?? DefaultPath);

    private static string GetUserDisabledMarkerPath(string statePath) => statePath + ".user-disabled";

    private static bool HasUserDisabledPreference(string statePath)
    {
        var markerPath = GetUserDisabledMarkerPath(statePath);
        if (!File.Exists(markerPath))
        {
            if (Directory.Exists(markerPath))
                throw new InvalidDataException("Teacher heartbeat 用户设置标记不是普通文件。");
            return false;
        }
        if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Teacher heartbeat 用户设置标记不能是符号链接或重解析点。");
        var info = new FileInfo(markerPath);
        if (info.Length is < 1 or > 128 ||
            File.ReadAllText(markerPath, System.Text.Encoding.ASCII) != UserDisabledMarkerContent)
            throw new InvalidDataException("Teacher heartbeat 用户设置标记无效。");
        return true;
    }

    private static void PersistUserDisabledPreference(string statePath, bool disabled)
    {
        var markerPath = GetUserDisabledMarkerPath(statePath);
        if (!disabled)
        {
            if (Directory.Exists(markerPath))
                throw new InvalidDataException("Teacher heartbeat 用户设置标记不是普通文件。");
            if (File.Exists(markerPath))
            {
                if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Teacher heartbeat 用户设置标记不能是符号链接或重解析点。");
                File.Delete(markerPath);
            }
            return;
        }

        var temporaryPath = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 128, FileOptions.WriteThrough))
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(UserDisabledMarkerContent);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, markerPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

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
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
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

    public async Task<TeacherCampusHeartbeatResult?> TrySendOnceDailyAsync(TeacherCampusHeartbeatState state,
        string teacherVersion, string studentVersion, int configuredComputerCount,
        string? statePath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Enabled || state.PackageId is null) return null;
        ValidateVersion(teacherVersion);
        ValidateVersion(studentVersion);
        if (configuredComputerCount is < 0 or > 150)
            throw new InvalidDataException("Teacher heartbeat 机房电脑总数必须为 0–150。");
        var validatedState = ValidateState(state);
        var packageId = validatedState.PackageId
                        ?? throw new InvalidDataException("Teacher heartbeat packageId is required.");
        var day = TeacherCampusHeartbeatStateStore.GetHongKongDate();
        if (!TeacherCampusHeartbeatStateStore.IsDue(validatedState, day)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(new HeartbeatRequest(validatedState.PublisherInstanceId,
                packageId, teacherVersion, studentVersion, configuredComputerCount))
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        TeacherCampusLatestReleases latestReleases;
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            latestReleases = new(null, null);
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            var responseBytes = await ReadBoundedAsync(response.Content, MaximumResponseBytes, cancellationToken)
                .ConfigureAwait(false);
            HeartbeatResponse result;
            try
            {
                result = JsonSerializer.Deserialize<HeartbeatResponse>(responseBytes, ResponseJsonOptions)
                         ?? throw new InvalidDataException("Teacher heartbeat 响应为空。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Teacher heartbeat 响应格式无效。", exception);
            }
            latestReleases = result.LatestReleases
                              ?? throw new InvalidDataException("Teacher heartbeat 响应缺少 latestReleases。");
        }
        else
        {
            throw new HttpRequestException($"Teacher heartbeat 服务返回 HTTP {(int)response.StatusCode}。", null,
                response.StatusCode);
        }
        TeacherCampusHeartbeatStateStore.Save(validatedState with
        {
            LastSentDay = day,
            FirstHeartbeatNotBeforeUtc = null
        }, statePath);
        return new(latestReleases);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("Teacher heartbeat 响应过大。");
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (target.Length + bytesRead > maximumBytes)
                throw new InvalidDataException("Teacher heartbeat 响应过大。");
            target.Write(buffer, 0, bytesRead);
        }
        return target.ToArray();
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

    private sealed record HeartbeatResponse(
        [property: JsonPropertyName("latestReleases")] TeacherCampusLatestReleases? LatestReleases);
}

public sealed record TeacherCampusHeartbeatResult(TeacherCampusLatestReleases LatestReleases);

public sealed record TeacherCampusLatestReleases(
    [property: JsonPropertyName("teacherConsole")] ApplicationReleaseEnvelope? TeacherConsole,
    [property: JsonPropertyName("studentSetup")] ApplicationReleaseEnvelope? StudentSetup);
