using System.Net;
using System.Security.Cryptography;
using System.Text;
using VeyonCampus.Core;

namespace VeyonCampus.Companion;

/// <summary>Reads only the SYSTEM Agent's signed loopback snapshot; no LAN discovery or user settings.</summary>
public sealed class StudentCompanionStatusPoller : IAsyncDisposable
{
    private static readonly Uri StatusUri = new("http://127.0.0.1:39174" + WebsitePolicyAgent.ClassroomStatusLocalPath);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    public StudentCompanionStatusPoller()
    {
        var handler = new HttpClientHandler { UseProxy = false };
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
        var now = nowUtc.ToUniversalTime();
        if (!snapshot.Connected)
        {
            if (snapshot.SessionId is not null || snapshot.RoomName is not null || snapshot.TargetCount != 0 ||
                snapshot.ReceivedUtc is not null || snapshot.ExpiresUtc is not null)
                throw new InvalidDataException("断开状态包含课堂数据。");
            return new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.Disconnected);
        }

        if (snapshot.ReceivedUtc is not { } received || snapshot.ExpiresUtc is not { } expires ||
            received.Offset != TimeSpan.Zero || expires.Offset != TimeSpan.Zero ||
            received > now.Add(ClassroomStatusCryptography.MaximumFutureSkew) || expires <= received ||
            expires - received > ClassroomStatusCryptography.MaximumLifetime)
            throw new InvalidDataException("课堂状态本机回执时间无效。");
        if (expires <= now)
            return new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.Disconnected);

        if (snapshot.SessionId is null)
        {
            if (snapshot.RoomName is not null || snapshot.TargetCount != 0)
                throw new InvalidDataException("无课堂状态包含教室信息。");
            return new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.ConnectedWithoutClass);
        }
        if (snapshot.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.RoomName) ||
            snapshot.RoomName.Length > 100 || snapshot.RoomName != snapshot.RoomName.Trim() ||
            snapshot.RoomName.Any(char.IsControl) || snapshot.TargetCount is < 1 or > ClassroomSession.MaximumTargets)
            throw new InvalidDataException("活动课堂状态无效。");
        return new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.ClassroomActive,
            snapshot.RoomName, snapshot.TargetCount);
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
                using var response = await _client.GetAsync(StatusUri,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("Student Agent 尚未提供课堂状态。", null, response.StatusCode);
                var signedJson = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                snapshot = MapSignedSnapshot(signedJson, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              InvalidDataException or IOException or System.Text.Json.JsonException or
                                              CryptographicException)
            {
                snapshot = new StudentCompanionStatusSnapshot(StudentCompanionConnectionState.Disconnected);
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
}
