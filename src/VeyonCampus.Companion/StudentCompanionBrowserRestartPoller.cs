using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
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
                        BrowserSessionRestartLauncher.RequestRestartForOpenBrowsers(signal.RestartEdge ?? true);
                }
                else if (signal.RequestId != Guid.Empty && signal.RequestId != lastRequestId)
                {
                    BrowserSessionRestartLauncher.RequestRestartForOpenBrowsers(signal.RestartEdge ?? true);
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

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken,
        int maximumBytes = 16 * 1024)
    {
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
    private sealed record BrowserWindow(int ProcessId, IntPtr Handle);

    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyL = 0x4C;
    private const ushort VirtualKeyEnter = 0x0D;
    private static readonly BrowserTarget[] Targets =
    [
        new("msedge", "edge://restart"),
        new("chrome", "chrome://restart")
    ];

    /// <summary>
    /// Sends the browser's own restart URL to an existing window. Starting the executable with that URL
    /// creates a new window and does not reliably invoke Chromium's in-session restart handler.
    /// </summary>
    public static int RequestRestartForOpenBrowsers(bool restartEdge = true)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        var foregroundIsBrowser = false;
        var requested = 0;

        foreach (var target in Targets)
        {
            if (!restartEdge && target.ProcessName == "msedge") continue;
            var browserWindows = FindOpenBrowserWindows(target.ProcessName, currentSessionId)
                .GroupBy(window => window.ProcessId)
                .Select(group => group.OrderByDescending(window => window.ProcessId == foregroundProcessId).First())
                .OrderByDescending(window => window.ProcessId == foregroundProcessId);
            foreach (var window in browserWindows)
            {
                try
                {
                    if (!SetForegroundWindow(window.Handle))
                    {
                        GetWindowThreadProcessId(GetForegroundWindow(), out var activeProcessId);
                        if (activeProcessId != (uint)window.ProcessId) continue;
                    }

                    // Do not inject keys unless Windows confirms that the intended browser owns the foreground.
                    Thread.Sleep(120);
                    GetWindowThreadProcessId(GetForegroundWindow(), out var confirmedProcessId);
                    if (confirmedProcessId != (uint)window.ProcessId) continue;

                    foregroundIsBrowser |= window.ProcessId == foregroundProcessId;
                    if (NavigateCurrentWindowToRestartUrl(target.RestartUrl)) requested++;
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                                  UnauthorizedAccessException or IOException or ArgumentException)
                {
                    // A failed UI request must leave the browser running; continue with other browser types.
                }
            }
        }

        // Restore the previous application focus when it was not itself one of the browsers being restarted.
        if (!foregroundIsBrowser && foreground != IntPtr.Zero && IsWindow(foreground))
            _ = SetForegroundWindow(foreground);
        return requested;
    }

    private static IReadOnlyList<BrowserWindow> FindOpenBrowserWindows(string processName, int sessionId)
    {
        var processes = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId) continue;
                    processes.Add(process.Id);
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                                  NotSupportedException or UnauthorizedAccessException)
                {
                    // Ignore inaccessible renderer/background processes.
                }
            }
        }

        if (processes.Count == 0) return [];
        var windows = new List<BrowserWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var className = new StringBuilder(128);
            if (GetClassName(handle, className, className.Capacity) == 0 ||
                !string.Equals(className.ToString(), "Chrome_WidgetWin_1", StringComparison.Ordinal))
                return true;
            GetWindowThreadProcessId(handle, out var processId);
            if (processId <= int.MaxValue && processes.Contains((int)processId))
                windows.Add(new BrowserWindow((int)processId, handle));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool NavigateCurrentWindowToRestartUrl(string restartUrl)
    {
        // Ctrl+L focuses the address bar in the already-open browser window.
        if (!SendKeyboard(
                Key(VirtualKeyControl), Key(VirtualKeyL), Key(VirtualKeyL, KeyUp), Key(VirtualKeyControl, KeyUp)))
            return false;

        Thread.Sleep(80);
        var input = new List<KeyboardInput>(restartUrl.Length * 2 + 2);
        foreach (var character in restartUrl)
        {
            input.Add(new KeyboardInput(0, character, Unicode));
            input.Add(new KeyboardInput(0, character, Unicode | KeyUp));
        }
        input.Add(Key(VirtualKeyEnter));
        input.Add(Key(VirtualKeyEnter, KeyUp));
        return SendKeyboard(input.ToArray());
    }

    private static KeyboardInput Key(ushort virtualKey, uint flags = 0) =>
        new(virtualKey, 0, flags);

    private static bool SendKeyboard(params KeyboardInput[] keys)
    {
        var inputs = new Input[keys.Length];
        for (var index = 0; index < keys.Length; index++)
            inputs[index] = new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = keys[index] } };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return true;

        // Release Ctrl if Windows accepted only part of the shortcut sequence.
        var release = new[] { new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = Key(VirtualKeyControl, KeyUp) }
        } };
        _ = SendInput(1, release, Marshal.SizeOf<Input>());
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;

        public KeyboardInput(ushort virtualKey, ushort scanCode, uint flags)
        {
            VirtualKey = virtualKey;
            ScanCode = scanCode;
            Flags = flags;
            Time = 0;
            ExtraInfo = UIntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] private MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
}
