using System.Buffers.Binary;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal sealed record ClassroomScreenPreviewSnapshot(byte[] Png, DateTimeOffset CapturedUtc, bool WasCaptured);

/// <summary>Session-bound, in-memory thumbnail cache with conservative capture limits.</summary>
internal sealed class ClassroomScreenPreviewCapture
{
    internal const int MaximumPreviewWidth = 320;
    internal const int MaximumPreviewHeight = 180;
    internal const int MaximumPreviewTargets = 5;
    internal const int MaximumSnapshotBytes = 1024 * 1024;
    internal static readonly TimeSpan MinimumCaptureInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const int MaximumInputBytes = 32 * 1024 * 1024;
    private const long MaximumInputPixels = 50_000_000;

    private sealed class TargetState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public byte[]? Png;
        public DateTimeOffset CapturedUtc;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, TargetState> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _globalCaptureGate = new(2, 2);
    private readonly Func<string, CancellationToken, Task<byte[]>> _capture;
    private Guid? _sessionId;
    private CancellationTokenSource _sessionCancellation = new();

    public ClassroomScreenPreviewCapture(Func<string, CancellationToken, Task<byte[]>>? capture = null) =>
        _capture = capture ?? VeyonCliScreenshotCapture.CaptureAsync;

    public void SetSession(Guid? sessionId)
    {
        CancellationTokenSource? previous = null;
        lock (_gate)
        {
            if (_sessionId == sessionId) return;
            previous = _sessionCancellation;
            _sessionCancellation = new CancellationTokenSource();
            _sessionId = sessionId;
            _targets.Clear();
        }
        previous.Cancel();
        previous.Dispose();
    }

    public async Task<ClassroomScreenPreviewSnapshot> GetAsync(Guid sessionId, string target,
        CancellationToken cancellationToken)
    {
        var normalizedTarget = VeyonHostAddress.NormalizeOverride(target);
        TargetState state;
        CancellationToken sessionToken;
        CancellationTokenSource linked;
        lock (_gate)
        {
            if (_sessionId != sessionId)
                throw new OperationCanceledException("课堂已结束或已切换。", cancellationToken);
            if (!_targets.TryGetValue(normalizedTarget, out state!))
            {
                state = new TargetState();
                _targets.Add(normalizedTarget, state);
            }
            sessionToken = _sessionCancellation.Token;
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionToken);
        }

        using var linkedScope = linked;
        await state.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_sessionId != sessionId)
                    throw new OperationCanceledException("课堂已结束或已切换。", linked.Token);
                if (state.Png is { Length: > 0 } cached &&
                    DateTimeOffset.UtcNow - state.CapturedUtc < MinimumCaptureInterval)
                    return new ClassroomScreenPreviewSnapshot(cached, state.CapturedUtc, false);
                if (DateTimeOffset.UtcNow - state.CapturedUtc >= CacheLifetime)
                    state.Png = null;
            }

            await _globalCaptureGate.WaitAsync(linked.Token).ConfigureAwait(false);
            byte[] png;
            try
            {
                // Recheck after waiting for the global budget: another request for this
                // target may have filled the cache while this request was queued.
                lock (_gate)
                {
                    if (_sessionId != sessionId)
                        throw new OperationCanceledException("课堂已结束或已切换。", linked.Token);
                    if (state.Png is { Length: > 0 } cached &&
                        DateTimeOffset.UtcNow - state.CapturedUtc < MinimumCaptureInterval)
                        return new ClassroomScreenPreviewSnapshot(cached, state.CapturedUtc, false);
                }
                png = await _capture(normalizedTarget, linked.Token).ConfigureAwait(false);
                ValidateThumbnail(png);
            }
            finally
            {
                _globalCaptureGate.Release();
            }

            var capturedUtc = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                if (_sessionId != sessionId)
                    throw new OperationCanceledException("课堂已结束或已切换。", linked.Token);
                state.Png = png;
                state.CapturedUtc = capturedUtc;
            }
            _ = ExpireSnapshotAsync(sessionId, normalizedTarget, state, capturedUtc, sessionToken);
            return new ClassroomScreenPreviewSnapshot(png, capturedUtc, true);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public void ClearSnapshots(Guid sessionId)
    {
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
            _targets.Clear();
        }
    }

    private async Task ExpireSnapshotAsync(Guid sessionId, string target, TargetState state,
        DateTimeOffset capturedUtc, CancellationToken sessionToken)
    {
        try { await Task.Delay(CacheLifetime, sessionToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested) { return; }
        lock (_gate)
        {
            if (_sessionId == sessionId && _targets.TryGetValue(target, out var current) &&
                ReferenceEquals(current, state) && state.CapturedUtc == capturedUtc)
                state.Png = null;
        }
    }

    private static void ValidateThumbnail(byte[]? png)
    {
        if (png is not { Length: >= 24 and <= MaximumSnapshotBytes } ||
            !png.AsSpan(0, 8).SequenceEqual(PngSignature) ||
            !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("屏幕缩略图格式或大小无效。");
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width is 0 or > MaximumPreviewWidth || height is 0 or > MaximumPreviewHeight ||
            (long)width * height > MaximumInputPixels)
            throw new InvalidDataException("屏幕缩略图尺寸超过限制。");
    }

    private static class VeyonCliScreenshotCapture
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);

        public static async Task<byte[]> CaptureAsync(string target, CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("屏幕预览只支持 Windows 教师端。");
            var cliPath = ResolveCliPath();
            if (cliPath is null)
                throw new InvalidOperationException("找不到 Veyon CLI；请先在教师电脑安装固定版本 Veyon。");

            var normalizedTarget = VeyonHostAddress.NormalizeOverride(target);
            var veyonDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Veyon");
            var screenshotDirectory = Path.Combine(veyonDataDirectory, "Screenshots");
            if (Directory.Exists(veyonDataDirectory)) RejectReparsePoint(veyonDataDirectory);
            if (Directory.Exists(screenshotDirectory)) RejectReparsePoint(screenshotDirectory);
            Directory.CreateDirectory(screenshotDirectory);
            RejectReparsePoint(screenshotDirectory);
            var matchingFilesBefore = FindTargetScreenshots(screenshotDirectory, normalizedTarget)
                .ToDictionary(path => path, GetFileFingerprint, StringComparer.OrdinalIgnoreCase);
            var startedUtc = DateTimeOffset.UtcNow;

            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                WorkingDirectory = Path.GetDirectoryName(cliPath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("feature");
            startInfo.ArgumentList.Add("start");
            startInfo.ArgumentList.Add(normalizedTarget);
            startInfo.ArgumentList.Add("Screenshot");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException("无法启动 Veyon 截图功能。");
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, 4096);
            var stderrTask = ReadBoundedAsync(process.StandardError, 4096);
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(CommandTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                throw new TimeoutException("等待 Veyon 屏幕画面超时；已停止本次采集。");
            }
            catch (OperationCanceledException)
            {
                await TerminateAsync(process).ConfigureAwait(false);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (detail.Length > 240) detail = detail[^240..];
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? "Veyon 未能取得该电脑的屏幕画面。"
                    : "Veyon 未能取得该电脑的屏幕画面：" + detail);
            }

            var completedUtc = DateTimeOffset.UtcNow;
            var newFiles = FindTargetScreenshots(screenshotDirectory, normalizedTarget)
                .Where(path => !matchingFilesBefore.ContainsKey(path))
                .Where(path => File.GetLastWriteTimeUtc(path) >= startedUtc.UtcDateTime.AddSeconds(-1) &&
                               File.GetLastWriteTimeUtc(path) <= completedUtc.UtcDateTime.AddSeconds(2))
                .ToArray();
            if (newFiles.Length != 1)
            {
                foreach (var prior in matchingFilesBefore)
                {
                    if (File.Exists(prior.Key) && GetFileFingerprint(prior.Key) != prior.Value)
                        throw new InvalidDataException("Veyon 截图名称与已有文件冲突；为保护已有截图，本次预览已取消。");
                }
                throw new InvalidDataException("无法唯一定位本次 Veyon 截图；没有读取或删除不确定的文件。");
            }

            var screenshotPath = newFiles[0];
            RejectReparsePoint(screenshotPath);
            var generatedFingerprint = GetFileFingerprint(screenshotPath);
            byte[] result;
            try
            {
                var info = new FileInfo(screenshotPath);
                if (info.Length is <= 0 or > MaximumInputBytes)
                    throw new InvalidDataException("Veyon 原始截图大小超过限制。");
                var input = await File.ReadAllBytesAsync(screenshotPath, cancellationToken).ConfigureAwait(false);
                if (GetFileFingerprint(screenshotPath) != generatedFingerprint)
                    throw new InvalidDataException("Veyon 截图在读取期间发生变化；为保护已有文件，本次预览已取消。");
                ValidateInputPng(input);
                using var inputStream = new MemoryStream(input, writable: false);
                var originalWidth = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(16, 4));
                var originalHeight = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(20, 4));
                using var bitmap = (double)originalWidth / originalHeight >
                                   (double)MaximumPreviewWidth / MaximumPreviewHeight
                    ? Bitmap.DecodeToWidth(inputStream, MaximumPreviewWidth, BitmapInterpolationMode.LowQuality)
                    : Bitmap.DecodeToHeight(inputStream, MaximumPreviewHeight, BitmapInterpolationMode.LowQuality);
                using var output = new MemoryStream();
                bitmap.Save(output, PngBitmapEncoderOptions.Default);
                result = output.ToArray();
                ValidateThumbnail(result);
            }
            finally
            {
                try
                {
                    RejectReparsePoint(screenshotPath);
                    if (GetFileFingerprint(screenshotPath) != generatedFingerprint)
                        throw new IOException("截图文件已变化；为保护已有文件，本次预览不删除该文件。");
                    File.Delete(screenshotPath);
                }
                catch (IOException exception)
                {
                    throw new IOException("缩略图已读取，但教师机无法删除临时原始截图。请检查 Veyon 截图目录权限。", exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    throw new UnauthorizedAccessException("教师机无法删除临时原始截图。请检查 Veyon 截图目录权限。", exception);
                }
            }
            return result;
        }

        private static string? ResolveCliPath()
        {
            foreach (var programFiles in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var name in new[] { "veyon-cli.exe", "veyon-wcli.exe" })
            {
                var path = Path.Combine(programFiles, "Veyon", name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static IEnumerable<string> FindTargetScreenshots(string directory, string target)
        {
            var fileTarget = target.Replace(':', '-');
            return Directory.EnumerateFiles(directory, $"*_{fileTarget}_*.png", SearchOption.TopDirectoryOnly);
        }

        private static (long Length, long LastWriteTicks) GetFileFingerprint(string path)
        {
            var info = new FileInfo(path);
            return (info.Length, info.LastWriteTimeUtc.Ticks);
        }

        private static void ValidateInputPng(byte[] png)
        {
            if (png.Length is < 24 or > MaximumInputBytes ||
                !png.AsSpan(0, 8).SequenceEqual(PngSignature) ||
                !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                throw new InvalidDataException("Veyon 截图不是有效 PNG 或超过大小限制。");
            var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
            if (width is 0 or > 16384 || height is 0 or > 16384 ||
                (long)width * height > MaximumInputPixels)
                throw new InvalidDataException("Veyon 截图像素尺寸超过处理上限。");
        }

        private static void RejectReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Veyon 截图路径不能是目录联接或符号链接。");
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
        {
            var buffer = new char[1024];
            var output = new System.Text.StringBuilder(Math.Min(maximumCharacters, 1024));
            while (true)
            {
                var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) return output.ToString();
                var retained = Math.Min(count, maximumCharacters - output.Length);
                if (retained > 0) output.Append(buffer, 0, retained);
            }
        }

        private static void Kill(Process process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static async Task TerminateAsync(Process process)
        {
            Kill(process);
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
            catch (InvalidOperationException) { }
        }
    }
}
