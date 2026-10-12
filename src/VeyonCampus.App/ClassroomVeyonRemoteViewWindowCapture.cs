using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;

namespace VeyonCampus.App;

internal sealed record ClassroomVeyonWindowFrame(byte[] Jpeg, DateTimeOffset CapturedUtc);

/// <summary>Captures only the Veyon view window started for a paired mobile live-view lease.</summary>
internal sealed class ClassroomVeyonRemoteViewWindowCapture : IAsyncDisposable
{
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private const uint DibRgbColors = 0;
    private const uint MaximumWindowPixels = 16_000_000;
    private const int MaximumFrameWidth = 1280;
    private const int MaximumFrameHeight = 720;
    private const int MaximumFrameBytes = 3 * 1024 * 1024;
    private static readonly TimeSpan WindowStartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdleShutdownTimeout = TimeSpan.FromSeconds(65);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LiveSession? _session;
    private CancellationTokenSource? _idleShutdown;
    private bool _disposed;

    private sealed record LiveSession(Guid DeviceId, Guid LeaseId, string Target, Process Process, IntPtr Window);

    public async Task StartAsync(Guid deviceId, Guid leaseId, string target, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Veyon 手机实时画面只支持 Windows 教师端。");
        if (deviceId == Guid.Empty || leaseId == Guid.Empty)
            throw new ArgumentException("Veyon 手机监看授权标识无效。");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopCurrentAsync().ConfigureAwait(false);
            var normalizedTarget = VeyonCampus.Core.VeyonHostAddress.NormalizeOverride(target);
            var process = await ClassroomVeyonRemoteViewLauncher.StartProcessAsync(normalizedTarget,
                cancellationToken).ConfigureAwait(false);
            try
            {
                var window = await WaitForMainWindowAsync(process, cancellationToken).ConfigureAwait(false);
                _session = new LiveSession(deviceId, leaseId, normalizedTarget, process, window);
                RenewIdleShutdown();
            }
            catch
            {
                await StopProcessAsync(process).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ClassroomVeyonWindowFrame> CaptureAsync(Guid deviceId, Guid leaseId, string target,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Veyon 手机实时画面只支持 Windows 教师端。");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var session = _session;
            if (session is null || session.DeviceId != deviceId || session.LeaseId != leaseId ||
                !session.Target.Equals(target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Veyon 手机监看已停止；请重新打开实时画面。");
            if (session.Process.HasExited)
                throw new InvalidOperationException("教师电脑上的 Veyon 监看窗口已关闭；请重新打开实时画面。");

            var jpeg = CaptureWindowJpeg(session.Window);
            RenewIdleShutdown();
            return new ClassroomVeyonWindowFrame(jpeg, DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> StopAsync(Guid deviceId, Guid leaseId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not { } session || session.DeviceId != deviceId || session.LeaseId != leaseId)
                return false;
            await StopCurrentAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCurrentAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCurrentAsync()
    {
        CancelIdleShutdown();
        var session = _session;
        _session = null;
        if (session is not null) await StopProcessAsync(session.Process).ConfigureAwait(false);
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (TimeoutException) { }
        finally { process.Dispose(); }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<IntPtr> WaitForMainWindowAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + WindowStartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException("Veyon 监看程序已退出，未能打开实时画面。");
            var window = FindLargestVisibleWindow(process.Id);
            if (window != IntPtr.Zero) return window;
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("等待 Veyon 监看窗口超时；请确认教师电脑桌面未被锁定。");
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr FindLargestVisibleWindow(int processId)
    {
        var selected = IntPtr.Zero;
        long selectedArea = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var ownerId);
            if (ownerId != (uint)processId || !GetWindowRect(window, out var rect)) return true;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            var area = (long)width * height;
            if (width >= 320 && height >= 200 && area > selectedArea)
            {
                selected = window;
                selectedArea = area;
            }
            return true;
        }, IntPtr.Zero);
        return selected;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] CaptureWindowJpeg(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window))
            throw new InvalidOperationException("Veyon 监看窗口不可见；请在教师电脑恢复该窗口后重试。");
        if (IsIconic(window))
            throw new InvalidOperationException("Veyon 监看窗口已最小化；请在教师电脑恢复该窗口后重试。");
        if (!GetWindowRect(window, out var rect))
            throw new InvalidOperationException("无法读取 Veyon 监看窗口尺寸。");

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width is < 320 or > 8192 || height is < 200 or > 8192 ||
            (long)width * height > MaximumWindowPixels)
            throw new InvalidOperationException("Veyon 监看窗口尺寸超过手机实时画面的限制。");

        var windowDc = GetWindowDC(window);
        if (windowDc == IntPtr.Zero) throw new InvalidOperationException("无法读取 Veyon 监看窗口画面。");
        var memoryDc = IntPtr.Zero;
        var bitmapHandle = IntPtr.Zero;
        var previousBitmap = IntPtr.Zero;
        var scaledDc = IntPtr.Zero;
        var scaledBitmap = IntPtr.Zero;
        var previousScaledBitmap = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(windowDc);
            if (memoryDc == IntPtr.Zero) throw new InvalidOperationException("无法创建 Veyon 画面缓冲区。");
            bitmapHandle = CreateCompatibleBitmap(windowDc, width, height);
            if (bitmapHandle == IntPtr.Zero) throw new InvalidOperationException("无法创建 Veyon 画面位图。");
            previousBitmap = SelectObject(memoryDc, bitmapHandle);
            if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
                throw new InvalidOperationException("无法选择 Veyon 画面位图。");
            if (!PrintWindow(window, memoryDc, PrintWindowRenderFullContent))
                throw new InvalidOperationException("Windows 无法捕获 Veyon 监看窗口；请保持教师桌面处于解锁状态后重试。");

            var scale = Math.Min(1.0,
                Math.Min((double)MaximumFrameWidth / width, (double)MaximumFrameHeight / height));
            var outputWidth = Math.Max(1, (int)Math.Round(width * scale));
            var outputHeight = Math.Max(1, (int)Math.Round(height * scale));
            scaledDc = CreateCompatibleDC(windowDc);
            if (scaledDc == IntPtr.Zero) throw new InvalidOperationException("无法创建 Veyon 实时画面缩放缓冲区。");
            scaledBitmap = CreateCompatibleBitmap(windowDc, outputWidth, outputHeight);
            if (scaledBitmap == IntPtr.Zero) throw new InvalidOperationException("无法创建 Veyon 实时画面缩放位图。");
            previousScaledBitmap = SelectObject(scaledDc, scaledBitmap);
            if (previousScaledBitmap == IntPtr.Zero || previousScaledBitmap == new IntPtr(-1))
                throw new InvalidOperationException("无法选择 Veyon 实时画面缩放位图。");
            if (SetStretchBltMode(scaledDc, 4) == 0 || !SetBrushOrgEx(scaledDc, 0, 0, out _) ||
                !StretchBlt(scaledDc, 0, 0, outputWidth, outputHeight,
                    memoryDc, 0, 0, width, height, 0x00CC0020))
                throw new InvalidOperationException("无法缩放 Veyon 监看窗口画面。");

            _ = SelectObject(scaledDc, previousScaledBitmap);
            previousScaledBitmap = IntPtr.Zero;
            var stride = checked(outputWidth * 4);
            var pixels = new byte[checked(stride * outputHeight)];
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = outputWidth,
                    Height = -outputHeight,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = (uint)pixels.Length,
                    XPelsPerMeter = 3780,
                    YPelsPerMeter = 3780
                }
            };
            var rows = GetDIBits(windowDc, scaledBitmap, 0, (uint)outputHeight, pixels, ref info, DibRgbColors);
            if (rows != outputHeight) throw new InvalidOperationException("无法读取 Veyon 监看窗口图像数据。");
            for (var index = 3; index < pixels.Length; index += 4) pixels[index] = byte.MaxValue;

            var bmp = CreateBitmapFile(outputWidth, outputHeight, pixels);
            using var input = new MemoryStream(bmp, writable: false);
            using var decoded = Bitmap.DecodeToWidth(input, outputWidth, BitmapInterpolationMode.LowQuality);
            using var output = new MemoryStream();
            decoded.Save(output, JpegBitmapEncoderOptions.Default);
            var result = output.ToArray();
            if (result.Length is <= 0 or > MaximumFrameBytes)
                throw new InvalidDataException("Veyon 实时画面超过手机传输大小限制。");
            return result;
        }
        finally
        {
            if (previousScaledBitmap != IntPtr.Zero) _ = SelectObject(scaledDc, previousScaledBitmap);
            if (previousBitmap != IntPtr.Zero) _ = SelectObject(memoryDc, previousBitmap);
            if (scaledBitmap != IntPtr.Zero) _ = DeleteObject(scaledBitmap);
            if (bitmapHandle != IntPtr.Zero) _ = DeleteObject(bitmapHandle);
            if (scaledDc != IntPtr.Zero) _ = DeleteDC(scaledDc);
            if (memoryDc != IntPtr.Zero) _ = DeleteDC(memoryDc);
            _ = ReleaseDC(window, windowDc);
        }
    }

    private static byte[] CreateBitmapFile(int width, int height, byte[] pixels)
    {
        const int headerSize = 54;
        var result = new byte[checked(headerSize + pixels.Length)];
        result[0] = (byte)'B';
        result[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(2, 4), result.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(10, 4), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22, 4), -height);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(28, 2), 32);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(34, 4), pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(38, 4), 3780);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(42, 4), 3780);
        Buffer.BlockCopy(pixels, 0, result, headerSize, pixels.Length);
        return result;
    }

    private void RenewIdleShutdown()
    {
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _idleShutdown, replacement);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
        _ = StopAfterIdleAsync(replacement.Token);
    }

    private async Task StopAfterIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(IdleShutdownTimeout, cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested) await StopAllAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void CancelIdleShutdown()
    {
        var idle = Interlocked.Exchange(ref _idleShutdown, null);
        if (idle is null) return;
        idle.Cancel();
        idle.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ClassroomVeyonRemoteViewWindowCapture));
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowDC(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int SetStretchBltMode(IntPtr deviceContext, int stretchMode);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetBrushOrgEx(IntPtr deviceContext, int x, int y, out NativePoint previousOrigin);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool StretchBlt(IntPtr destinationDc, int x, int y, int width, int height,
        IntPtr sourceDc, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint rasterOperation);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr graphicsObject);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr deviceContext);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr deviceContext, IntPtr bitmap, uint startScan, uint scanLines,
        [Out] byte[] bits, ref BitmapInfo info, uint usage);
}
