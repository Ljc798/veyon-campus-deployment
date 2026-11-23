using Avalonia.Media.Imaging;

namespace VeyonCampus.App;

/// <summary>One scheduler result for one device: a real decoded frame, or a state change without one.</summary>
internal sealed record ScreenWallUpdate(Guid TargetId, ScreenWallLinkState State, string Detail,
    Bitmap? Image, int ImageBytes, DateTimeOffset CapturedUtc, int HttpMs);

/// <summary>
/// Drives every visible screen-wall tile from one background loop. One connection per device, a global
/// concurrency gate so 30 tiles cannot stampede the local WebAPI, and two cadences: a slow thumbnail
/// beat for the grid and a fast beat for the tile whose detail window is open.
/// </summary>
internal sealed class ClassroomScreenWallService : IAsyncDisposable
{
    private const int MaximumConcurrentCaptures = 4;
    private static readonly TimeSpan ThumbnailInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan FocusedInterval = TimeSpan.FromMilliseconds(130);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(120);

    private sealed class Entry
    {
        internal required ClassroomScreenTarget Target { get; set; }
        internal DateTimeOffset LastAttemptUtc;
        internal int InFlight;
    }

    private readonly VeyonWebApiClient _client = new();
    private readonly IReadOnlyList<VeyonKeyCredential> _credentials;
    private readonly Action<Action> _dispatch;
    private readonly Action<ScreenWallUpdate> _publish;
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Dictionary<Guid, ScreenWallConnection> _connections = [];
    private readonly SemaphoreSlim _captureGate = new(MaximumConcurrentCaptures, MaximumConcurrentCaptures);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private bool _active;
    private bool _webApiReachable;
    private Guid? _focused;
    private DateTimeOffset _lastProbeUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    internal ClassroomScreenWallService(Action<Action> dispatch, Action<ScreenWallUpdate> publish)
    {
        _dispatch = dispatch;
        _publish = publish;
        _credentials = VeyonKeyMaterial.TryLoad();
        _loop = Task.Run(() => RunAsync(_lifetime.Token));
    }

    internal bool HasCredentials => _credentials.Count > 0;
    internal string KeySummary => _credentials.Count == 0
        ? "未找到教师端 Veyon 私钥"
        : $"{_credentials.Count} 个可用密钥（{string.Join("、", _credentials.Select(item => item.KeyName))}）";

    internal async Task<bool> ProbeAsync(CancellationToken cancellationToken = default) =>
        await _client.ProbeAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Replaces the device set. Connections for devices that left are released immediately.</summary>
    internal async Task SetTargetsAsync(IReadOnlyList<ClassroomScreenTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var removed = new List<ScreenWallConnection>();
        lock (_gate)
        {
            var keep = targets.Select(target => target.TargetId).ToHashSet();
            foreach (var pair in _connections.Where(pair => !keep.Contains(pair.Key)).ToArray())
            {
                removed.Add(pair.Value);
                _connections.Remove(pair.Key);
                _entries.Remove(pair.Key);
            }

            foreach (var target in targets)
            {
                if (_entries.TryGetValue(target.TargetId, out var existing))
                {
                    // Seat or student name may change while the lesson runs; keep the cadence state.
                    existing.Target = target;
                }
                else
                {
                    _entries[target.TargetId] = new Entry { Target = target };
                }

                if (_connections.TryGetValue(target.TargetId, out var connection))
                {
                    if (string.Equals(connection.Host, target.HostAddress, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // The device moved: the session was authenticated against the old address.
                    removed.Add(connection);
                    _connections.Remove(target.TargetId);
                }

                _connections[target.TargetId] = new ScreenWallConnection(target.TargetId, target.HostAddress,
                    _credentials, _client, OnConnectionStateChanged);
            }
        }

        foreach (var connection in removed)
        {
            await connection.ReleaseAsync().ConfigureAwait(false);
            connection.Dispose();
        }

        if (removed.Count > 0) cancellationToken.ThrowIfCancellationRequested();
    }

    internal void SetActive(bool active)
    {
        lock (_gate) _active = active;
    }

    internal void SetFocused(Guid? targetId)
    {
        lock (_gate) _focused = targetId;
    }

    /// <summary>Forces the next tick to capture this device immediately (used when the detail opens).</summary>
    internal void RequestImmediate(Guid targetId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(targetId, out var entry)) entry.LastAttemptUtc = DateTimeOffset.MinValue;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (true)
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) return;
                bool active;
                lock (_gate) active = _active && !_disposed;
                if (!active) continue;

                var now = DateTimeOffset.UtcNow;
                if (now - _lastProbeUtc >= ProbeInterval)
                {
                    _lastProbeUtc = now;
                    _webApiReachable = await _client.ProbeAsync(cancellationToken).ConfigureAwait(false);
                    if (!_webApiReachable)
                    {
                        PublishAllUnavailable();
                        continue;
                    }
                }

                if (!_webApiReachable) continue;
                ScheduleDueCaptures(now, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private void ScheduleDueCaptures(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<(Entry Entry, bool IsFocused)> due = [];
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (Volatile.Read(ref entry.InFlight) == 1) continue;
                var focused = _focused == entry.Target.TargetId;
                var interval = focused ? FocusedInterval : ThumbnailInterval;
                if (now - entry.LastAttemptUtc < interval) continue;
                if (Interlocked.CompareExchange(ref entry.InFlight, 1, 0) != 0) continue;
                entry.LastAttemptUtc = now;
                due.Add((entry, focused));
            }
        }

        foreach (var item in due)
            _ = CaptureEntryAsync(item.Entry, item.IsFocused, cancellationToken);
    }

    private async Task CaptureEntryAsync(Entry entry, bool isFocused, CancellationToken cancellationToken)
    {
        ScreenWallConnection? connection;
        lock (_gate) _connections.TryGetValue(entry.Target.TargetId, out connection);
        if (connection is null)
        {
            Volatile.Write(ref entry.InFlight, 0);
            return;
        }

        try
        {
            var quality = isFocused ? ScreenWallQuality.Detail : ScreenWallQuality.Thumb;
            ScreenWallCapture? capture = null;
            var acquired = false;
            try
            {
                await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
                capture = await connection.CaptureAsync(quality, DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (acquired) _captureGate.Release();
            }

            if (capture is null)
            {
                // State was already reported through the connection callback; nothing to publish.
                return;
            }

            var image = Decode(capture.Jpeg);
            var update = new ScreenWallUpdate(entry.Target.TargetId, ScreenWallLinkState.Live,
                connection.Detail, image, capture.Jpeg.Length, capture.CapturedUtc, capture.HttpMs);
            _dispatch(() => _publish(update));
        }
        catch (OperationCanceledException)
        {
            // Shutdown or the device left the wall.
        }
        finally
        {
            Volatile.Write(ref entry.InFlight, 0);
        }
    }

    private void OnConnectionStateChanged(Guid targetId, ScreenWallLinkState state, string detail) =>
        _dispatch(() => _publish(new ScreenWallUpdate(targetId, state, detail, null, 0,
            DateTimeOffset.MinValue, 0)));

    private void PublishAllUnavailable()
    {
        List<(Guid TargetId, string Detail)> snapshot;
        lock (_gate)
        {
            snapshot = _entries.Values
                .Select(entry => (entry.Target.TargetId, Detail: "本机 WebAPI 未启用或未监听 127.0.0.1:11080"))
                .ToList();
        }

        foreach (var item in snapshot)
            _dispatch(() => _publish(new ScreenWallUpdate(item.TargetId, ScreenWallLinkState.Unavailable,
                item.Detail, null, 0, DateTimeOffset.MinValue, 0)));
    }

    private static Bitmap? Decode(byte[] jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                           NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<ScreenWallConnection> connections;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active = false;
            connections = _connections.Values.ToList();
            _connections.Clear();
            _entries.Clear();
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var connection in connections)
        {
            await connection.ReleaseAsync().ConfigureAwait(false);
            connection.Dispose();
        }

        _captureGate.Dispose();
        _client.Dispose();
        _lifetime.Dispose();
    }
}
