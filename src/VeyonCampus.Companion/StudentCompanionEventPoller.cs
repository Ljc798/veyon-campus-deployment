using System.Net;
using System.Security.Cryptography;
using System.Text;
using VeyonCampus.Core;

namespace VeyonCampus.Companion;

/// <summary>Uses only the Agent's loopback event bridge; it contains no Teacher address or credential.</summary>
public sealed class StudentCompanionEventPoller : IAsyncDisposable
{
    private static readonly Uri EventUri = new("http://127.0.0.1:39174" + WebsitePolicyAgent.ClassroomEventLocalPath);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    public StudentCompanionEventPoller()
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        _client = new HttpClient(handler) { Timeout = ClassroomEventBuffer.MaximumWait + TimeSpan.FromSeconds(5) };
    }

    public void Start(Action<ClassroomEvent> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (_loop is not null) throw new InvalidOperationException("课堂事件轮询已经启动。");
        _loop = RunAsync(apply, _shutdown.Token);
    }

    public async Task<StudentAgentClassroomEventSubmission> RequestHelpAsync(Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("当前课堂 session 无效。");
        using var request = new HttpRequestMessage(HttpMethod.Post, EventUri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("暂时无法联系老师；请稍后重试。", null, response.StatusCode);
        var signedJson = Encoding.UTF8.GetString(bytes);
        var verified = StudentAgentResponseCryptography.Verify<StudentAgentClassroomEventSubmission>(signedJson);
        var result = verified.Payload;
        var now = DateTimeOffset.UtcNow;
        if (!result.Accepted || result.SessionId != sessionId || result.EventId == Guid.Empty ||
            result.SubmittedUtc.Offset != TimeSpan.Zero ||
            result.SubmittedUtc < now.Subtract(TimeSpan.FromMinutes(2)) ||
            result.SubmittedUtc > now.Add(ClassroomEventCryptography.MaximumFutureSkew))
            throw new InvalidDataException("课堂求助回执与当前课堂不匹配。");
        return result;
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

    private async Task RunAsync(Action<ClassroomEvent> apply, CancellationToken cancellationToken)
    {
        Guid? sessionId = null;
        long cursor = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    EventUri + "?after=" + cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("课堂事件通道暂不可用。", null, response.StatusCode);
                var signedJson = Encoding.UTF8.GetString(bytes);
                var verified = StudentAgentResponseCryptography.Verify<StudentAgentClassroomEventPage>(signedJson);
                var page = verified.Payload;
                if (!page.Ready || page.SessionId is null)
                {
                    sessionId = null;
                    cursor = 0;
                    await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (page.SessionId != sessionId)
                {
                    sessionId = page.SessionId;
                    cursor = 0;
                    continue;
                }
                if (page.Cursor < cursor || page.Events is null ||
                    page.Events.Count > ClassroomEventBuffer.MaximumPageSize)
                    throw new InvalidDataException("课堂事件本机分页无效。");
                cursor = page.Cursor;
                foreach (var item in page.Events)
                {
                    if (item.SessionId != sessionId || item.Sender != ClassroomEventSender.Teacher ||
                        item.EventId == Guid.Empty)
                        throw new InvalidDataException("课堂事件不是当前教师发来的有效消息。");
                    apply(item);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              InvalidDataException or IOException or System.Text.Json.JsonException or
                                              CryptographicException or WebException)
            {
                try { await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = 1024 * 1024;
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("本机课堂事件超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("本机课堂事件超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
