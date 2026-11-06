using System.Net;
using System.Security.Cryptography;
using System.Text;
using VeyonCampus.Core;

namespace VeyonCampus.Companion;

/// <summary>Reads only the SYSTEM Agent's signed loopback snapshot; no LAN discovery or user settings.</summary>
public sealed class StudentCompanionStatusPoller : IAsyncDisposable
{
    private static readonly Uri StatusUriV2 = new("http://127.0.0.1:39174" + WebsitePolicyAgent.ClassroomStatusLocalPathV2);
    private static readonly Uri LegacyStatusUri = new("http://127.0.0.1:39174" + WebsitePolicyAgent.ClassroomStatusLocalPath);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    public StudentCompanionStatusPoller()
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }

    public void Start(Action<StudentCompanionStatusSnapshot> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (_loop is not null) throw new InvalidOperationException("课堂状态轮询已经启动。");
        _loop = RunAsync(apply, _shutdown.Token);
    }

    public static StudentCompanionStatusSnapshot MapSignedSnapshot(string signedJson, DateTimeOffset nowUtc)
    {
        var verified = StudentAgentResponseCryptography.Verify<ClassroomStatusSnapshot>(signedJson);
        var snapshot = verified.Payload;
        var identityFingerprint = verified.Fingerprint;
        var now = nowUtc.ToUniversalTime();
        if (!snapshot.Connected)
        {
            if (snapshot.SessionId is not null || snapshot.RoomName is not null || snapshot.TargetCount != 0 ||
                snapshot.ReceivedUtc is not null || snapshot.ExpiresUtc is not null || snapshot.Mode is not null)
                throw new InvalidDataException("断开状态包含课堂数据。");
            return new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.Disconnected,
                AgentIdentityFingerprint: identityFingerprint,
                StatusDetail: "学生 Agent 正在运行，但尚未收到教师端的课堂状态。请确认教师端已开始课堂，且与本机在同一网络。");
        }

        if (snapshot.ReceivedUtc is not { } received || snapshot.ExpiresUtc is not { } expires ||
            received.Offset != TimeSpan.Zero || expires.Offset != TimeSpan.Zero ||
            received > now.Add(ClassroomStatusCryptography.MaximumFutureSkew) || expires <= received ||
            expires - received > ClassroomStatusCryptography.MaximumLifetime)
            throw new InvalidDataException("课堂状态本机回执时间无效。");
        if (expires <= now)
            return new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.Disconnected,
                AgentIdentityFingerprint: identityFingerprint,
                StatusDetail: "教师端的课堂状态已超过 2 分钟没有刷新；教师端可能已退出，或推送被网络、防火墙阻断。");

        if (snapshot.SessionId is null)
        {
            if (snapshot.RoomName is not null || snapshot.TargetCount != 0 || snapshot.Mode is not null)
                throw new InvalidDataException("无课堂状态包含教室信息。");
            return new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.ConnectedWithoutClass,
                AgentIdentityFingerprint: identityFingerprint);
        }
        if (snapshot.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.RoomName) ||
            snapshot.RoomName.Length > 100 || snapshot.RoomName != snapshot.RoomName.Trim() ||
            snapshot.RoomName.Any(char.IsControl) || snapshot.TargetCount is < 1 or > ClassroomSession.MaximumTargets ||
            snapshot.Mode is { } mode && !Enum.IsDefined(mode))
            throw new InvalidDataException("活动课堂状态无效。");
        return new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.ClassroomActive,
            snapshot.RoomName, snapshot.TargetCount, snapshot.SessionId, snapshot.Mode, identityFingerprint);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }
        _client.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunAsync(Action<StudentCompanionStatusSnapshot> apply,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            StudentCompanionStatusSnapshot snapshot;
            try
            {
                using var response = await _client.GetAsync(StatusUriV2,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                string signedJson;
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    using var legacy = await _client.GetAsync(LegacyStatusUri,
                        HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (legacy.StatusCode == HttpStatusCode.NotFound)
                        throw new ClassroomStatusEndpointMissingException();
                    if (!legacy.IsSuccessStatusCode)
                        throw new HttpRequestException("Student Agent 尚未提供课堂状态。", null, legacy.StatusCode);
                    signedJson = await ReadBoundedAsync(legacy.Content, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException("Student Agent 尚未提供课堂状态。", null, response.StatusCode);
                    signedJson = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                }
                snapshot = MapSignedSnapshot(signedJson, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ClassroomStatusEndpointMissingException)
            {
                snapshot = new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.Disconnected,
                    StatusDetail: "本机学生 Agent 版本较旧，没有课堂状态接口。请在学生机上重新运行学生端安装包。");
            }
            catch (HttpRequestException exception)
            {
                snapshot = new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.Disconnected,
                    StatusDetail: exception.StatusCode is { } status
                        ? $"本机学生 Agent 返回 HTTP {(int)status}；课堂状态暂时不可用。"
                        : "无法连接本机学生 Agent（127.0.0.1:39174）。请确认 Veyon Campus 学生端服务正在运行。");
            }
            catch (Exception exception) when (exception is InvalidDataException or
                                              System.Text.Json.JsonException or CryptographicException)
            {
                snapshot = new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.Disconnected,
                    StatusDetail: "本机学生 Agent 的课堂状态无法验证：" + exception.Message);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
                snapshot = new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.Disconnected,
                    StatusDetail: "读取本机学生 Agent 的课堂状态超时；稍后会自动重试。");
            }

            apply(snapshot);
            try { await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = StudentAgentResponseCryptography.MaximumEnvelopeBytes;
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("本机课堂状态超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("本机课堂状态超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>
    /// Both the v2 and the legacy status paths answered 404. That is not a transport failure: the
    /// Agent is up and speaking HTTP, it simply predates the classroom status endpoints, which is a
    /// reinstall problem rather than a network one.
    /// </summary>
    private sealed class ClassroomStatusEndpointMissingException : Exception
    {
    }
}
