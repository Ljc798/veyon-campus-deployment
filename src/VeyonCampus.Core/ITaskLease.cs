namespace VeyonCampus.Core;

/// <summary>
/// Task lease for cross-entry mutual exclusion (AR-01, P3-02/P3-13).
/// Entry points must acquire the lease synchronously before the first await
/// so that two requests can never pass the busy check at the same time;
/// the loser is rejected before doing any work. The lease is held through
/// all modifications and cleanup, released in the caller's finally block.
/// </summary>
public interface ITaskLease : IDisposable
{
    /// <summary>
    /// Attempts to take the only task slot. Returns false when another task
    /// is already running; the denial reason is always set.
    /// </summary>
    bool TryAcquire(out string denialReason);
}

/// <summary>Thread-safe in-process implementation backed by an interlocked counter.</summary>
public sealed class TaskLease : ITaskLease
{
    private int _held;

    public bool TryAcquire(out string denialReason)
    {
        if (Interlocked.CompareExchange(ref _held, 1, 0) is 0)
        {
            denialReason = "";
            return true;
        }
        denialReason = "已有任务正在执行；请等待结果后再发起新的操作。";
        return false;
    }

    public void Dispose() => Interlocked.Exchange(ref _held, 0);
}

/// <summary>
/// Optional helper for application code that mirrors a lease to an observable
/// busy state. MainViewModel currently centralizes its entry points in
/// TryBeginExclusiveTask/EndExclusiveTask. The cross-process named-pipe
/// reservation is provided by <see cref="NamedPipeTaskLease"/>.
/// </summary>
public static class TaskGate
{
    /// <summary>
    /// Tries to take the lease and notify the caller to refresh observable
    /// state. Release the lease in a finally block when this returns true.
    /// </summary>
    public static bool TryAcquire(ITaskLease lease, Action busyChange)
    {
        if (!lease.TryAcquire(out var denial))
        {
            busyChange?.Invoke(); // keep the observable state in sync without holding the slot
            return false;
        }
        busyChange?.Invoke();
        return true;
    }

    /// <summary>Releases the lease and clears the busy flag exactly once.</summary>
    public static void Release(ITaskLease lease, Action busyChange)
    {
        lease.Dispose();
        busyChange?.Invoke();
    }
}
