using System.Diagnostics;
using System.Threading;

namespace VeyonCampus.Core;

/// <summary>
/// Serializes Veyon Builtin Directory mutations across teacher UI and elevated worker
/// processes. Reads stay lock-free; every writer must re-read its preconditions after acquire.
/// </summary>
public sealed class VeyonDirectoryMutationGate : IDisposable
{
    private const string MutexName = @"Global\VeyonCampus.VeyonDirectoryMutation.v1";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private VeyonDirectoryMutationGate(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static VeyonDirectoryMutationGate Acquire(TimeSpan? timeout = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Veyon 目录写入锁仅支持 Windows 教师端。");
        var wait = timeout ?? DefaultTimeout;
        if (wait <= TimeSpan.Zero || wait > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        Mutex mutex;
        try { mutex = new Mutex(initiallyOwned: false, name: MutexName); }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("无法打开 Veyon 目录跨进程写入锁；已拒绝修改目录。", exception);
        }

        try
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                var remaining = wait - timer.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("另一个教师端进程正在修改 Veyon 目录；请稍候重试。");
                try
                {
                    if (mutex.WaitOne(remaining)) return new VeyonDirectoryMutationGate(mutex);
                    throw new TimeoutException("等待 Veyon 目录写入锁超时；没有修改目录。");
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner exited unexpectedly. Mutex ownership is transferred
                    // to this process; the caller must still perform its normal fresh read.
                    return new VeyonDirectoryMutationGate(mutex);
                }
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!_ownsMutex) return;
        _ownsMutex = false;
        try { _mutex.ReleaseMutex(); }
        finally { _mutex.Dispose(); }
    }
}
