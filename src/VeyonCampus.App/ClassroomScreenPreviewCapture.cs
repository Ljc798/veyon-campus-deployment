using System.Buffers.Binary;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal sealed record ClassroomScreenPreviewSnapshot(byte[] Jpeg, DateTimeOffset CapturedUtc, bool WasCaptured);

internal sealed record ClassroomScreenPreviewQualityProfile(string Value, int Width, int Height, int JpegQuality)
{
    public static ClassroomScreenPreviewQualityProfile FromValue(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "standard" => new ClassroomScreenPreviewQualityProfile("standard", 640, 360, 68),
        "high" => new ClassroomScreenPreviewQualityProfile("high", 960, 540, 84),
        _ => new ClassroomScreenPreviewQualityProfile("low", 320, 180, 48)
    };
}

/// <summary>Session-bound, in-memory thumbnail cache with conservative capture limits.</summary>
internal sealed class ClassroomScreenPreviewCapture
{
    internal const int MaximumPreviewWidth = 1280;
    internal const int MaximumPreviewHeight = 720;
    internal const int MaximumPreviewTargets = ClassroomSession.MaximumTargets;
    internal const int MaximumSnapshotBytes = 1024 * 1024;
    private const int MaximumSessionCacheBytes = 10 * 1024 * 1024;
    internal static readonly TimeSpan MinimumCaptureInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private const int MaximumInputBytes = 32 * 1024 * 1024;
    private const long MaximumInputPixels = 50_000_000;

    private sealed class TargetState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public byte[]? MasterJpeg;
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
        ClassroomScreenPreviewQualityProfile quality, CancellationToken cancellationToken)
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
            byte[]? masterJpeg = null;
            var capturedUtc = DateTimeOffset.MinValue;
            var wasCaptured = false;
            lock (_gate)
            {
                if (_sessionId != sessionId)
                    throw new OperationCanceledException("课堂已结束或已切换。", linked.Token);
                if (state.MasterJpeg is { Length: > 0 } cached &&
                    DateTimeOffset.UtcNow - state.CapturedUtc < MinimumCaptureInterval)
                {
                    masterJpeg = cached;
                    capturedUtc = state.CapturedUtc;
                }
                else if (DateTimeOffset.UtcNow - state.CapturedUtc >= CacheLifetime)
                    state.MasterJpeg = null;
            }

            if (masterJpeg is null)
            {
                await _globalCaptureGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    // Recheck after waiting for the global budget: another request for this
                    // target may have filled the cache while this request was queued.
                    lock (_gate)
                    {
                        if (_sessionId != sessionId)
                            throw new OperationCanceledException("课堂已结束或已切换。", linked.Token);
                        if (state.MasterJpeg is { Length: > 0 } cached &&
                            DateTimeOffset.UtcNow - state.CapturedUtc < MinimumCaptureInterval)
                        {
                            masterJpeg = cached;
                            capturedUtc = state.CapturedUtc;
                        }
                    }
                    if (masterJpeg is null)
                    {
                        masterJpeg = await _capture(normalizedTarget, linked.Token).ConfigureAwait(false);
                        ValidateMasterJpeg(masterJpeg);
                        capturedUtc = DateTimeOffset.UtcNow;
                        wasCaptured = true;
                    }
                }
                finally
                {
                    _globalCaptureGate.Release();
                }

                if (wasCaptured)
                {
                    var newlyCapturedJpeg = masterJpeg
                                            ?? throw new InvalidOperationException("屏幕采集没有返回画面。");
                    lock (_gate)
                    {
                        if (_sessionId != sessionId)
                            throw new OperationCanceledException("课堂已结束或已切换。", linked.Token);
                        linked.Token.ThrowIfCancellationRequested();
                        CacheSnapshotUnsafe(state, newlyCapturedJpeg, capturedUtc);
                    }
                    _ = ExpireSnapshotAsync(sessionId, normalizedTarget, state, capturedUtc, sessionToken);
                }
            }
            var capturedMasterJpeg = masterJpeg ??
                throw new InvalidOperationException("屏幕采集没有返回画面。");
            var jpeg = EncodePreview(capturedMasterJpeg, quality);
            ValidatePreviewJpeg(jpeg, quality);
            linked.Token.ThrowIfCancellationRequested();
            return new ClassroomScreenPreviewSnapshot(jpeg, capturedUtc, wasCaptured);
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
                state.MasterJpeg = null;
        }
    }

    private void CacheSnapshotUnsafe(TargetState state, byte[] masterJpeg, DateTimeOffset capturedUtc)
    {
        var otherStates = _targets.Values.Where(candidate => !ReferenceEquals(candidate, state) &&
                candidate.MasterJpeg is { Length: > 0 })
            .OrderBy(candidate => candidate.CapturedUtc).ToArray();
        var cachedBytes = otherStates.Sum(candidate => candidate.MasterJpeg!.Length);
        foreach (var candidate in otherStates)
        {
            if (cachedBytes + masterJpeg.Length <= MaximumSessionCacheBytes) break;
            cachedBytes -= candidate.MasterJpeg!.Length;
            candidate.MasterJpeg = null;
        }
        state.MasterJpeg = masterJpeg;
        state.CapturedUtc = capturedUtc;
    }

    private static byte[] EncodePreview(byte[] masterJpeg, ClassroomScreenPreviewQualityProfile quality)
    {
        var (width, height) = ReadJpegDimensions(masterJpeg);
        using var input = new MemoryStream(masterJpeg, writable: false);
        using var bitmap = (double)width / height > (double)quality.Width / quality.Height
            ? Bitmap.DecodeToWidth(input, quality.Width, BitmapInterpolationMode.MediumQuality)
            : Bitmap.DecodeToHeight(input, quality.Height, BitmapInterpolationMode.MediumQuality);
        using var output = new MemoryStream();
        bitmap.Save(output, new JpegBitmapEncoderOptions { Quality = quality.JpegQuality });
        return output.ToArray();
    }

    private static void ValidateMasterJpeg(byte[]? jpeg)
    {
        var (width, height) = ReadJpegDimensions(jpeg);
        if (width > MaximumPreviewWidth || height > MaximumPreviewHeight ||
            (long)width * height > 1_000_000)
            throw new InvalidDataException("屏幕原图缓存尺寸超过限制。");
    }

    private static void ValidatePreviewJpeg(byte[]? jpeg, ClassroomScreenPreviewQualityProfile quality)
    {
        var (width, height) = ReadJpegDimensions(jpeg);
        if (jpeg!.Length > MaximumSnapshotBytes || width > quality.Width || height > quality.Height)
            throw new InvalidDataException("屏幕画面超过手机传输大小或尺寸限制。");
    }

    private static (int Width, int Height) ReadJpegDimensions(byte[]? jpeg)
    {
        if (jpeg is not { Length: >= 4 and <= MaximumSnapshotBytes } ||
            !jpeg.AsSpan(0, 3).SequenceEqual(JpegSignature))
            throw new InvalidDataException("屏幕画面不是有效 JPEG 或超过大小限制。");

        var offset = 2;
        while (offset + 4 <= jpeg.Length)
        {
            if (jpeg[offset] != 0xFF)
            {
                offset++;
                continue;
            }
            while (offset < jpeg.Length && jpeg[offset] == 0xFF) offset++;
            if (offset >= jpeg.Length) break;
            var marker = jpeg[offset++];
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) continue;
            if (marker is 0xD9 or 0xDA || offset + 2 > jpeg.Length) break;
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > jpeg.Length) break;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
                0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (segmentLength < 7) break;
                var height = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset + 5, 2));
                if (width > 0 && height > 0) return (width, height);
                break;
            }
            offset += segmentLength;
        }
        throw new InvalidDataException("无法读取屏幕 JPEG 尺寸。");
    }

    internal static class VeyonCliScreenshotCapture
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
                if (await TerminateAsync(process).ConfigureAwait(false))
                    TryDeleteUnambiguousNewScreenshot(screenshotDirectory, normalizedTarget,
                        matchingFilesBefore, startedUtc);
                throw new TimeoutException("等待 Veyon 屏幕画面超时；已停止本次采集。");
            }
            catch (OperationCanceledException)
            {
                if (await TerminateAsync(process).ConfigureAwait(false))
                    TryDeleteUnambiguousNewScreenshot(screenshotDirectory, normalizedTarget,
                        matchingFilesBefore, startedUtc);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                TryDeleteUnambiguousNewScreenshot(screenshotDirectory, normalizedTarget,
                    matchingFilesBefore, startedUtc);
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
                bitmap.Save(output, new JpegBitmapEncoderOptions { Quality = 90 });
                result = output.ToArray();
                ValidateMasterJpeg(result);
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

        internal static string? ResolveCliPath(bool preferNonConsole = false)
        {
            var names = preferNonConsole
                ? new[] { "veyon-wcli.exe", "veyon-cli.exe" }
                : new[] { "veyon-cli.exe", "veyon-wcli.exe" };
            foreach (var programFiles in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var name in names)
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

        private static void TryDeleteUnambiguousNewScreenshot(string directory, string target,
            IReadOnlyDictionary<string, (long Length, long LastWriteTicks)> filesBefore,
            DateTimeOffset startedUtc)
        {
            try
            {
                var completedUtc = DateTimeOffset.UtcNow;
                var candidates = FindTargetScreenshots(directory, target)
                    .Where(path => !filesBefore.ContainsKey(path))
                    .Where(path => File.GetLastWriteTimeUtc(path) >= startedUtc.UtcDateTime.AddSeconds(-1) &&
                                   File.GetLastWriteTimeUtc(path) <= completedUtc.UtcDateTime.AddSeconds(2))
                    .ToArray();
                if (candidates.Length != 1) return;

                var path = candidates[0];
                RejectReparsePoint(path);
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaximumInputBytes) return;
                var fingerprint = GetFileFingerprint(path);
                if (GetFileFingerprint(path) != fingerprint) return;
                File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidDataException) { }
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

        private static async Task<bool> TerminateAsync(Process process)
        {
            Kill(process);
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException) { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }
}
