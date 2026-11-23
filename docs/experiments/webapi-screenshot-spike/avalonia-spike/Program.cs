using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace WebApiSpike;

internal sealed class SpikeOptions
{
    public string Host { get; set; } = "PC-01";
    public int Port { get; set; } = 11080;
    public string KeyName { get; set; } = "CDPPLHAJJHEOHFJOGOJCGFKGIDKJOKNCA";
    public string KeyPath { get; set; } =
        @"C:\ProgramData\Veyon\keys\private\CDPPLHAJJHEOHFJOGOJCGFKGIDKJOKNCA\key";
    public int Seconds { get; set; } = 12;
    public int Width { get; set; } = 960;
    public int Height { get; set; } = 540;
    public string Format { get; set; } = "jpg";
    public string OutDir { get; set; } = "out";

    public static SpikeOptions Parse(string[] args)
    {
        var o = new SpikeOptions();
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--host": o.Host = args[++i]; break;
                case "--port": o.Port = int.Parse(args[++i]); break;
                case "--keyname": o.KeyName = args[++i]; break;
                case "--keypath": o.KeyPath = args[++i]; break;
                case "--seconds": o.Seconds = int.Parse(args[++i]); break;
                case "--width": o.Width = int.Parse(args[++i]); break;
                case "--height": o.Height = int.Parse(args[++i]); break;
                case "--format": o.Format = args[++i]; break;
                case "--out": o.OutDir = args[++i]; break;
            }
        }
        return o;
    }
}

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.Options = SpikeOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

internal sealed class App : Application
{
    public static SpikeOptions Options { get; set; } = new();

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Options);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class MainWindow : Window
{
    private readonly SpikeOptions _options;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new()
    {
        FontSize = 13,
        Margin = new Thickness(8, 4),
        Foreground = Brushes.Gainsboro
    };
    private readonly HttpClient _http;
    private readonly StringBuilder _log = new();
    private readonly string _logPath;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly List<double> _httpTimes = [];
    private readonly List<double> _decodeTimes = [];
    private string? _connectionUid;
    private int _frameCount;
    private int _errorCount;

    public MainWindow(SpikeOptions options)
    {
        _options = options;
        Directory.CreateDirectory(options.OutDir);
        _logPath = Path.Combine(options.OutDir, "avalonia-spike.log");
        _http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };

        Title = $"WebAPI 屏幕验证 — {options.Host}";
        Width = 1000;
        Height = 620;
        Background = new SolidColorBrush(Color.FromRgb(24, 26, 30));

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(32, 36, 44)),
                    Child = _status,
                    [DockPanel.DockProperty] = Dock.Top
                },
                new Border { Child = _image, Padding = new Thickness(6) }
            }
        };

        Opened += (_, _) => _ = RunAsync();
    }

    private void Log(string message)
    {
        var line = $"[{_uptime.ElapsedMilliseconds,6} ms] {message}";
        _log.AppendLine(line);
        try { File.WriteAllText(_logPath, _log.ToString()); } catch { /* best effort */ }
    }

    private void Status(string text)
    {
        _status.Text = text;
        Title = $"WebAPI 屏幕验证 — {_options.Host} — {text}";
    }

    private async Task RunAsync()
    {
        var baseUrl = $"http://127.0.0.1:{_options.Port}/api/v1";
        Log($"spike start host={_options.Host} baseUrl={baseUrl} size={_options.Width}x{_options.Height} format={_options.Format}");
        try
        {
            var method = await AuthenticateAsync(baseUrl);
            Log($"auth method uuid={method}");
            await CaptureLoopAsync(baseUrl, method);
        }
        catch (Exception ex)
        {
            _errorCount++;
            Log($"FATAL {ex.GetType().Name}: {ex.Message}");
            Status("失败：" + ex.Message);
        }
        finally
        {
            WriteSummary();
            await CloseConnectionAsync(baseUrl);
            await Task.Delay(300);
            Dispatcher.UIThread.Post(() => Close());
        }
    }

    private async Task<string> AuthenticateAsync(string baseUrl)
    {
        Status("读取认证方式…");
        var methodsWatch = Stopwatch.StartNew();
        using (var methodsResponse = await _http.GetAsync($"{baseUrl}/authentication/{_options.Host}"))
        {
            var methodsBody = await methodsResponse.Content.ReadAsStringAsync();
            methodsWatch.Stop();
            Log($"GET authentication methods -> {(int)methodsResponse.StatusCode} in {methodsWatch.ElapsedMilliseconds} ms body={methodsBody}");
            if (!methodsResponse.IsSuccessStatusCode) throw new InvalidOperationException("认证方式查询失败：" + methodsBody);
            using var doc = JsonDocument.Parse(methodsBody);
            var method = doc.RootElement.GetProperty("methods")[0].GetString()
                         ?? throw new InvalidOperationException("服务端未提供认证方式");
            return method;
        }
    }

    private async Task CaptureLoopAsync(string baseUrl, string method)
    {
        var keyData = await File.ReadAllTextAsync(_options.KeyPath);
        var payload = JsonSerializer.Serialize(new
        {
            method,
            credentials = new { keyname = _options.KeyName, keydata = keyData }
        });

        Status("密钥认证中…");
        var authWatch = Stopwatch.StartNew();
        using var authResponse = await _http.PostAsync($"{baseUrl}/authentication/{_options.Host}",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var authBody = await authResponse.Content.ReadAsStringAsync();
        authWatch.Stop();
        Log($"POST authentication -> {(int)authResponse.StatusCode} in {authWatch.ElapsedMilliseconds} ms body={authBody}");
        if (!authResponse.IsSuccessStatusCode) throw new InvalidOperationException("密钥认证失败：" + authBody);

        using (var doc = JsonDocument.Parse(authBody))
        {
            _connectionUid = doc.RootElement.GetProperty("connection-uid").GetString();
        }
        if (_connectionUid is null) throw new InvalidOperationException("响应缺少 connection-uid");
        _http.DefaultRequestHeaders.Remove("Connection-Uid");
        _http.DefaultRequestHeaders.Add("Connection-Uid", _connectionUid);
        Log($"connection established uid={_connectionUid} authMs={authWatch.ElapsedMilliseconds}");

        var framebufferUrl = $"{baseUrl}/framebuffer?width={_options.Width}&height={_options.Height}" +
                             $"&format={_options.Format}&compression=5&quality=80";

        Status("等待首帧…");
        var firstWatch = Stopwatch.StartNew();
        var attempts = 0;
        byte[]? firstBytes = null;
        while (firstWatch.Elapsed.TotalSeconds < 30)
        {
            attempts++;
            var probe = await TryFetchAsync(framebufferUrl);
            if (probe.Status == 200) { firstBytes = probe.Bytes; break; }
            if (probe.Status != 503) throw new InvalidOperationException($"首帧失败 HTTP {probe.Status}: {probe.Error}");
            await Task.Delay(250);
        }
        firstWatch.Stop();
        if (firstBytes is null) throw new TimeoutException("30 秒内未取得首帧");
        Log($"first frame in {firstWatch.ElapsedMilliseconds} ms after {attempts} attempt(s), {firstBytes.Length} bytes");

        var deadline = DateTime.UtcNow.AddSeconds(_options.Seconds);
        while (DateTime.UtcNow < deadline)
        {
            var frameWatch = Stopwatch.StartNew();
            var fetch = await TryFetchAsync(framebufferUrl);
            frameWatch.Stop();
            if (fetch.Status != 200)
            {
                _errorCount++;
                Log($"frame error HTTP {fetch.Status}: {fetch.Error}");
                await Task.Delay(500);
                continue;
            }
            var bytes = fetch.Bytes;
            var decodeWatch = Stopwatch.StartNew();
            Bitmap? bitmap = null;
            try
            {
                bitmap = new Bitmap(new MemoryStream(bytes));
            }
            catch (Exception ex)
            {
                _errorCount++;
                Log($"decode error {ex.GetType().Name}: {ex.Message} bytes={bytes.Length}");
                await Task.Delay(200);
                continue;
            }
            decodeWatch.Stop();
            _httpTimes.Add(frameWatch.Elapsed.TotalMilliseconds);
            _decodeTimes.Add(decodeWatch.Elapsed.TotalMilliseconds);
            _frameCount++;
            var index = _frameCount;
            var httpMs = frameWatch.Elapsed.TotalMilliseconds;
            var decodeMs = decodeWatch.Elapsed.TotalMilliseconds;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_image.Source is IDisposable previous) previous.Dispose();
                _image.Source = bitmap;
                Status($"第 {index} 帧 · HTTP {httpMs:F0} ms · 解码 {decodeMs:F1} ms · {bytes.Length / 1024} KB");
            });
            await Task.Delay(100);
        }

        if (_image.Source is IDisposable last) last.Dispose();
        using (var stream = new MemoryStream(firstBytes))
        {
            _image.Source = new Bitmap(stream);
        }
        await Task.Delay(250);
        SaveWindowRender();
        Log($"loop finished frames={_frameCount} errors={_errorCount}");
    }

    private async Task<string> GetAuthMethodAsync(string baseUrl)
    {
        using var response = await _http.GetAsync($"{baseUrl}/authentication/{_options.Host}");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("认证方式查询失败：" + body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("methods")[0].GetString()!;
    }

    private async Task<(int Status, byte[] Bytes, string Error)> TryFetchAsync(string url)
    {
        try
        {
            using var response = await _http.GetAsync(url);
            if (response.IsSuccessStatusCode)
                return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(), "");
            return ((int)response.StatusCode, [], await response.Content.ReadAsStringAsync());
        }
        catch (TaskCanceledException)
        {
            return (0, [], "timeout");
        }
    }

    private void SaveWindowRender()
    {
        try
        {
            var width = (int)Math.Max(1, Bounds.Width);
            var height = (int)Math.Max(1, Bounds.Height);
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(this);
            var path = Path.Combine(_options.OutDir, "avalonia-window.png");
            bitmap.Save(path);
            Log($"window render saved {path} ({width}x{height})");
        }
        catch (Exception ex)
        {
            Log($"window render failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task CloseConnectionAsync(string baseUrl)
    {
        if (_connectionUid is null) return;
        try
        {
            var watch = Stopwatch.StartNew();
            using var response = await _http.DeleteAsync($"{baseUrl}/authentication/{_options.Host}");
            watch.Stop();
            Log($"DELETE authentication -> {(int)response.StatusCode} in {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Log($"close failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void WriteSummary()
    {
        string Stat(List<double> values) => values.Count == 0
            ? "n/a"
            : $"n={values.Count} avg={values.Average():F1}ms min={values.Min():F1}ms max={values.Max():F1}ms p95={Percentile(values, 0.95):F1}ms";

        _log.AppendLine();
        _log.AppendLine("=== SUMMARY ===");
        _log.AppendLine($"host={_options.Host} size={_options.Width}x{_options.Height} format={_options.Format}");
        _log.AppendLine($"frames={_frameCount} errors={_errorCount} elapsed={_uptime.ElapsedMilliseconds}ms");
        _log.AppendLine($"http: {Stat(_httpTimes)}");
        _log.AppendLine($"decode: {Stat(_decodeTimes)}");
        if (_frameCount > 0)
        {
            var effective = _httpTimes.Zip(_decodeTimes, (a, b) => a + b).ToList();
            _log.AppendLine($"fetch+decode: {Stat(effective)}");
        }
        try { File.WriteAllText(_logPath, _log.ToString()); } catch { /* best effort */ }
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
