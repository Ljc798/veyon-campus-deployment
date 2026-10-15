using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeyonCampus.Core;

namespace VeyonCampus.Companion;

/// <summary>Polls the local SYSTEM Agent and restarts only browsers open in this interactive user session.</summary>
public sealed class StudentCompanionBrowserRestartPoller : IAsyncDisposable
{
    private static readonly Uri RequestUri = new("http://127.0.0.1:39174" +
        WebsitePolicyAgent.BrowserRestartRequestLocalPath);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupRequestFreshness = TimeSpan.FromSeconds(15);
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    public StudentCompanionBrowserRestartPoller()
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("浏览器重启请求轮询已经启动。");
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
                    throw new HttpRequestException("Student Agent 浏览器重启接口暂不可用。", null, response.StatusCode);

                var signedJson = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                var signal = StudentAgentResponseCryptography.Verify<StudentAgentBrowserRestartSignal>(signedJson).Payload;
                var nowUtc = DateTimeOffset.UtcNow;
                Validate(signal, nowUtc);
                if (lastRequestId is null)
                {
                    lastRequestId = signal.RequestId;
                    if (signal.RequestId != Guid.Empty && signal.RequestedUtc is { } firstRequest &&
                        firstRequest >= nowUtc - StartupRequestFreshness)
                        BrowserSessionRestartLauncher.RestartOpenBrowsers();
                }
                else if (signal.RequestId != Guid.Empty && signal.RequestId != lastRequestId)
                {
                    BrowserSessionRestartLauncher.RestartOpenBrowsers();
                    lastRequestId = signal.RequestId;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              InvalidDataException or IOException or JsonException or
                                              CryptographicException or Win32Exception or InvalidOperationException or
                                              UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // A failed request or browser launch must not stop classroom status/event polling.
            }

            await DelayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Validate(StudentAgentBrowserRestartSignal signal, DateTimeOffset nowUtc)
    {
        if (signal.SchemaVersion != 1)
            throw new InvalidDataException("Student Agent 浏览器重启请求格式无效。");
        if (signal.RequestId == Guid.Empty)
        {
            if (signal.RequestedUtc is not null)
                throw new InvalidDataException("Student Agent 空浏览器重启请求包含时间。");
            return;
        }
        if (signal.RequestedUtc is not { } requested || requested.Offset != TimeSpan.Zero ||
            requested > nowUtc.AddMinutes(1))
            throw new InvalidDataException("Student Agent 浏览器重启请求时间无效。");
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = 16 * 1024;
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("Student Agent 浏览器重启请求超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Student Agent 浏览器重启请求超过大小限制。");
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

internal static class BrowserSessionRestartLauncher
{
    private sealed record BrowserTarget(string ProcessName, string RestartUrl);
    private static readonly BrowserTarget[] Targets =
    [
        new("msedge", "edge://restart"),
        new("chrome", "chrome://restart")
    ];

    public static int RestartOpenBrowsers()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var restarted = 0;
        foreach (var target in Targets)
        {
            var executable = FindOpenBrowserExecutable(target.ProcessName, currentSessionId);
            if (executable is null) continue;
            try
            {
                var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
                startInfo.ArgumentList.Add(target.RestartUrl);
                using var process = Process.Start(startInfo);
                if (process is not null) restarted++;
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                              UnauthorizedAccessException or IOException)
            {
                // Leave that browser running; another browser can still be restarted.
            }
        }
        return restarted;
    }

    private static string? FindOpenBrowserExecutable(string processName, int sessionId)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId || process.MainWindowHandle == IntPtr.Zero) continue;
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                                  NotSupportedException or UnauthorizedAccessException)
                {
                    // Ignore inaccessible renderer/background processes and continue to the browser window process.
                }
            }
        }
        return null;
    }
}
