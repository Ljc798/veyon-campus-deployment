using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace VeyonCampus.App;

/// <summary>
/// The screen wall's state: which tiles exist (ordered by seat, never by frame arrival), whether the
/// local WebAPI is usable, and the per-second refresh of "how old is this picture". It owns the capture
/// service so leaving the classroom page has exactly one place to release every connection.
/// </summary>
public sealed class ClassroomScreenWallViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ClassroomScreenWallService _service;
    private readonly DispatcherTimer _ageTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _fingerprint = "";
    private bool _hasTiles;
    private bool _isWebApiReady;
    private string _webApiWarning = "正在检查本机 WebAPI…";
    private string _keyHint = "";
    private string _summaryText = "尚未载入课堂设备";
    private int _controlDeviceCount;
    private bool _active;
    private bool _disposed;
    private double _tileWidth = 240;
    private double _tileHeight = 210;

    internal ClassroomScreenWallViewModel(Action<Action> dispatch)
    {
        _service = new ClassroomScreenWallService(dispatch, ApplyUpdate);
        _ageTimer.Tick += (_, _) => RefreshAges();
        _keyHint = _service.KeySummary;
    }

    public ObservableCollection<ClassroomScreenTileViewModel> Tiles { get; } = [];

    public bool HasTiles
    {
        get => _hasTiles;
        private set
        {
            if (_hasTiles == value) return;
            _hasTiles = value;
            Raise(nameof(HasTiles));
            Raise(nameof(HasNoTiles));
        }
    }

    public bool HasNoTiles => !_hasTiles;

    /// <summary>
    /// Empty-state copy that tells the two cases apart: no classroom running at all, versus a running
    /// classroom whose devices have no seat positions yet (nothing to order the wall by).
    /// </summary>
    public string EmptyHintText => _controlDeviceCount == 0
        ? "当前教室还没有可调度的学生电脑。先在页面顶部点「开始课堂」。"
        : $"课堂里有 {_controlDeviceCount} 台学生电脑，但还没有座位位置，无法按座位排列画面。" +
          "请到左侧「地点与学生名单」排好座位后再回来。";
    public double TileWidth { get => _tileWidth; private set => Set(ref _tileWidth, value); }
    public double TileHeight { get => _tileHeight; private set => Set(ref _tileHeight, value); }
    public bool IsWebApiReady { get => _isWebApiReady; private set => Set(ref _isWebApiReady, value); }
    public string WebApiWarning { get => _webApiWarning; private set => Set(ref _webApiWarning, value); }
    public bool HasWebApiWarning => !IsWebApiReady;
    public string KeyHint { get => _keyHint; private set => Set(ref _keyHint, value); }
    public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }
    public bool HasCredentials => _service.HasCredentials;

    internal async Task RefreshReadinessAsync(CancellationToken cancellationToken = default)
    {
        var reachable = await _service.ProbeAsync(cancellationToken).ConfigureAwait(true);
        IsWebApiReady = reachable;
        Raise(nameof(HasWebApiWarning));
        WebApiWarning = reachable
            ? ""
            : "本机 Veyon WebAPI 未启用（127.0.0.1:11080 无监听）。屏幕墙不会用占位图代替真实画面；" +
              "请先启用 WebAPI 并只放行本机访问。";
        if (!reachable) SummaryText = "WebAPI 未就绪，暂无画面";
    }

    /// <summary>Rebuilds tiles only when the classroom target set actually changed.</summary>
    internal async Task SyncAsync(IReadOnlyList<ClassroomScreenTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = string.Join("\n", targets
            .OrderBy(target => target.SeatNumber)
            .Select(target => $"{target.TargetId:N}|{target.HostAddress}|{target.SeatNumber}|" +
                              $"{target.StudentName}|{target.ComputerName}"));
        if (string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
        {
            UpdateSummary();
            return;
        }

        _fingerprint = fingerprint;
        var ordered = targets.OrderBy(target => target.SeatNumber).ThenBy(target => target.HostAddress,
            StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = Tiles.ToDictionary(tile => tile.TargetId);
        Tiles.Clear();
        foreach (var target in ordered)
        {
            if (existing.Remove(target.TargetId, out var tile))
            {
                tile.ApplyTarget(target);
                Tiles.Add(tile);
            }
            else
            {
                Tiles.Add(new ClassroomScreenTileViewModel(target));
            }
        }

        // Tiles that left the classroom must release their last frame; the collection no longer holds them.
        foreach (var leftover in existing.Values) leftover.Image?.Dispose();
        HasTiles = Tiles.Count > 0;
        await _service.SetTargetsAsync(ordered, cancellationToken).ConfigureAwait(true);
        UpdateSummary();
    }

    internal void SetActive(bool active)
    {
        if (_disposed) return;
        _active = active;
        _service.SetActive(active);
        if (active)
        {
            if (!_ageTimer.IsEnabled) _ageTimer.Start();
        }
        else
        {
            _ageTimer.Stop();
            _service.SetFocused(null);
        }
    }

    internal void SetFocused(Guid? targetId)
    {
        _service.SetFocused(targetId);
        if (targetId is { } id) _service.RequestImmediate(id);
    }

    internal void SetTileMetrics(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        TileWidth = Math.Max(160, Math.Round(width));
        TileHeight = Math.Max(150, Math.Round(height));
    }

    internal void ApplyControlState(IEnumerable<TeacherClassroomQuickControlDeviceItem> devices)
    {
        var byId = devices.ToDictionary(device => device.TargetId);
        if (_controlDeviceCount != byId.Count)
        {
            _controlDeviceCount = byId.Count;
            Raise(nameof(EmptyHintText));
            if (Tiles.Count == 0) UpdateSummary();
        }

        foreach (var tile in Tiles)
            tile.ApplyControlState(byId.GetValueOrDefault(tile.TargetId));
    }

    private void ApplyUpdate(ScreenWallUpdate update)
    {
        if (_disposed) return;
        var tile = Tiles.FirstOrDefault(item => item.TargetId == update.TargetId);
        if (tile is null)
        {
            update.Image?.Dispose();
            return;
        }

        if (update.Image is null) tile.ApplyState(update.State, update.Detail);
        else tile.ApplyFrame(update);
        UpdateSummary();
    }

    private void RefreshAges()
    {
        if (!_active) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var tile in Tiles) tile.RefreshAge(now);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (Tiles.Count == 0)
        {
            SummaryText = EmptyHintText;
            return;
        }

        var live = Tiles.Count(tile => tile.LinkState == ScreenWallLinkState.Live);
        var problems = Tiles.Count(tile => tile.LinkState is ScreenWallLinkState.Offline
            or ScreenWallLinkState.AuthenticationFailed or ScreenWallLinkState.Unavailable
            or ScreenWallLinkState.FormatUnsupported);
        var stale = Tiles.Count(tile => tile.LinkState == ScreenWallLinkState.Stale);
        var parts = new List<string> { $"{Tiles.Count} 台" };
        if (live > 0) parts.Add($"{live} 实时");
        if (stale > 0) parts.Add($"{stale} 画面过期");
        if (problems > 0) parts.Add($"{problems} 不可用");
        if (live == 0 && stale == 0 && problems == 0) parts.Add("正在连接");
        SummaryText = string.Join(" · ", parts);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ageTimer.Stop();
        foreach (var tile in Tiles)
        {
            tile.Image?.Dispose();
        }

        _ = _service.DisposeAsync();
    }
}
