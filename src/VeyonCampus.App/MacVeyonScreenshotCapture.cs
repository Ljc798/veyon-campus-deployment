using System.Diagnostics;
using System.Text;
using VeyonCampus.Core;

namespace VeyonCampus.App;

/// <summary>Runs the fixed screenshot-only helper in the local Ubuntu ARM64 container.</summary>
internal static class MacVeyonScreenshotCapture
{
    private const string ContainerName = "veyon-campus-helper-arm64";
    private const string HelperScript = "/usr/local/lib/veyon-helper/capture.py";
    private const int MaximumPngBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(55);

    public static async Task<byte[]> CaptureAsync(string target, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Ubuntu ARM64 Veyon helper 只用于 Mac 教师端。");

        var normalizedTarget = VeyonHostAddress.NormalizeOverride(target);
        if (normalizedTarget.Length == 0)
            throw new InvalidDataException("屏幕预览目标无效。");

        var dockerPath = ResolveDockerCliPath();
        if (dockerPath is null)
            throw new InvalidOperationException("找不到 Docker CLI；请安装并启动 Docker Desktop，再启动 Veyon helper。");

        var startInfo = new ProcessStartInfo
        {
            FileName = dockerPath,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(ContainerName);
        startInfo.ArgumentList.Add("/usr/bin/python3");
        startInfo.ArgumentList.Add(HelperScript);
        startInfo.ArgumentList.Add(normalizedTarget);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("无法启动本机 Veyon helper 请求。");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("无法启动 Docker CLI；请确认 Docker Desktop 已安装。", exception);
        }

        var stdoutTask = ReadBoundedBytesAsync(process.StandardOutput.BaseStream, MaximumPngBytes,
            CancellationToken.None);
        var stderrTask = ReadBoundedTextAsync(process.StandardError.BaseStream, 4096,
            CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw new TimeoutException("等待 Veyon helper 返回屏幕画面超时；已停止本次采集。");
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var png = await stdoutTask.ConfigureAwait(false);
        var detail = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            if (detail.Contains("No such container", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("is not running", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Veyon helper 尚未启动；请先运行 scripts/veyon-helper/start.sh。");
            if (detail.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) &&
                detail.Contains(HelperScript, StringComparison.Ordinal))
                throw new InvalidOperationException("Veyon helper 缺少截图脚本；请运行 scripts/veyon-helper/start.sh 更新 helper。");
            throw new InvalidOperationException(Truncate(detail, 600, "Veyon helper 未能取得学生屏幕画面。"));
        }

        if (png.Length == 0)
            throw new InvalidDataException("Veyon helper 没有返回屏幕画面。");
        return ClassroomScreenPreviewCapture.VeyonCliScreenshotCapture.EncodePngToMasterJpeg(png);
    }

    private static string? ResolveDockerCliPath()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, "docker"))
            .Concat([
                "/opt/homebrew/bin/docker",
                "/usr/local/bin/docker",
                "/Applications/Docker.app/Contents/Resources/bin/docker"
            ]);
        return candidates.FirstOrDefault(path => File.Exists(path) && !Directory.Exists(path));
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(Stream stream, int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 1024 * 1024));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximumBytes)
                throw new InvalidDataException("Veyon helper 返回的 PNG 超过大小限制。");
            output.Write(buffer, 0, count);
        }
    }

    private static async Task<string> ReadBoundedTextAsync(Stream stream, int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 1024));
        var buffer = new byte[1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            var remaining = maximumBytes - (int)output.Length;
            if (remaining > 0) output.Write(buffer, 0, Math.Min(remaining, count));
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task ObserveReadersAsync(Task<byte[]> stdoutTask, Task<string> stderrTask)
    {
        try { await stdoutTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                           ObjectDisposedException or OperationCanceledException) { }
        try { await stderrTask.ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or
                                           OperationCanceledException) { }
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (InvalidOperationException) { }
    }

    private static string Truncate(string value, int maximumLength, string fallback)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0) return fallback;
        return normalized.Length <= maximumLength ? normalized : normalized[^maximumLength..];
    }
}
