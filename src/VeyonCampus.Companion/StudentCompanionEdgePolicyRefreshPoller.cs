using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeyonCampus.Core;

namespace VeyonCampus.Companion;

/// <summary>Refreshes platform policies in an already-open Edge session without closing browser windows.</summary>
public sealed class StudentCompanionEdgePolicyRefreshPoller : IAsyncDisposable
{
    private static readonly Uri RequestUri = new("http://127.0.0.1:39174" +
        WebsitePolicyAgent.EdgePolicyRefreshRequestLocalPath);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    public StudentCompanionEdgePolicyRefreshPoller()
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("Edge 策略刷新轮询已经启动。");
        _loop = RunAsync(_shutdown.Token);
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

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        Guid? lastRequestId = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await _client.GetAsync(RequestUri,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    await DelayAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("Student Agent Edge 策略刷新接口暂不可用。", null,
                        response.StatusCode);

                var signedJson = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                var signal = StudentAgentResponseCryptography
                    .Verify<StudentAgentEdgePolicyRefreshSignal>(signedJson).Payload;
                var nowUtc = DateTimeOffset.UtcNow;
                Validate(signal, nowUtc);
                if (signal.RequestId == Guid.Empty)
                    lastRequestId = Guid.Empty;
                else if (lastRequestId != signal.RequestId)
                {
                    var result = EdgePlatformPolicyRefreshLauncher.RefreshOpenEdgeSession();
                    if (result != EdgeRefreshLaunchResult.Retry)
                        lastRequestId = signal.RequestId;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              InvalidDataException or IOException or JsonException or
                                              CryptographicException or Win32Exception or InvalidOperationException or
                                              UnauthorizedAccessException or PlatformNotSupportedException or
                                              ArgumentException or NotSupportedException)
            {
                // A failed refresh must not stop classroom status and event polling.
            }

            await DelayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Validate(StudentAgentEdgePolicyRefreshSignal signal, DateTimeOffset nowUtc)
    {
        if (signal.SchemaVersion != 1)
            throw new InvalidDataException("Student Agent Edge 策略刷新请求格式无效。");
        if (signal.RequestId == Guid.Empty)
        {
            if (signal.RequestedUtc is not null || signal.Revision != 0)
                throw new InvalidDataException("Student Agent 空 Edge 策略刷新请求包含数据。");
            return;
        }
        if (signal.RequestedUtc is not { } requested || requested.Offset != TimeSpan.Zero ||
            requested > nowUtc.AddMinutes(1) || signal.Revision <= 0)
            throw new InvalidDataException("Student Agent Edge 策略刷新请求时间或版本无效。");
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken,
        int maximumBytes = 16 * 1024)
    {
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("Student Agent Edge 策略刷新请求超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Student Agent Edge 策略刷新请求超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task DelayAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}

internal static class EdgePlatformPolicyRefreshLauncher
{
    public static EdgeRefreshLaunchResult RefreshOpenEdgeSession()
    {
        if (!OperatingSystem.IsWindows()) return EdgeRefreshLaunchResult.NoOpenEdgeWindow;
        var sessionId = Process.GetCurrentProcess().SessionId;
        var foundOpenWindow = false;
        foreach (var process in Process.GetProcessesByName("msedge"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId || process.MainWindowHandle == IntPtr.Zero) continue;
                    foundOpenWindow = true;
                    var executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                        return EdgeRefreshLaunchResult.Retry;

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    startInfo.ArgumentList.Add("--refresh-platform-policy");
                    using var refreshRequest = Process.Start(startInfo);
                    if (refreshRequest is not null) return EdgeRefreshLaunchResult.Requested;
                    return EdgeRefreshLaunchResult.Retry;
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                                  UnauthorizedAccessException or IOException or ArgumentException or
                                                  NotSupportedException)
                {
                    // Retry a failed launch while the signal stays current; never close or focus Edge.
                }
            }
        }
        return foundOpenWindow ? EdgeRefreshLaunchResult.Retry : EdgeRefreshLaunchResult.NoOpenEdgeWindow;
    }
}

internal enum EdgeRefreshLaunchResult
{
    Requested,
    NoOpenEdgeWindow,
    Retry
}
