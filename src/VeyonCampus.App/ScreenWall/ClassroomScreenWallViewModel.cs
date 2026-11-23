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
    private double _tilePictureHeight = 120;
    private int _gridColumns = 3;
    private string _thumbnailSize = "中";
    private bool _compactMode;
    private string _searchText = "";
    private string _statusFilter = "全部状态";
    private string _sortOrder = "按座位顺序";
    private Guid? _locatedTargetId;

    internal ClassroomScreenWallViewModel(Action<Action> dispatch)
    {
        _service = new ClassroomScreenWallService(dispatch, ApplyUpdate);
        _ageTimer.Tick += (_, _) => RefreshAges();
        _keyHint = _service.KeySummary;
    }

    public ObservableCollection<ClassroomScreenTileViewModel> Tiles { get; } = [];
    public ObservableCollection<ClassroomScreenTileViewModel> FilteredTiles { get; } = [];
    public ObservableCollection<ClassroomScreenTileViewModel> SeatedTiles { get; } = [];
    public ObservableCollection<ClassroomScreenTileViewModel> UnseatedTiles { get; } = [];
    public ObservableCollection<ClassroomScreenTileViewModel> AllUnseatedTiles { get; } = [];
    public event EventHandler? DeviceStateChanged;

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

    public string EmptyHintText => _controlDeviceCount == 0
        ? "当前教室还没有可调度的学生电脑。先在页面顶部点「开始课堂」。"
        : "当前课堂没有可显示的活动设备。检查教室名单和课堂状态后重试。";
    public double TileWidth { get => _tileWidth; private set => Set(ref _tileWidth, value); }
    public double TileHeight { get => _tileHeight; private set => Set(ref _tileHeight, value); }
    public double TilePictureHeight { get => _tilePictureHeight; private set => Set(ref _tilePictureHeight, value); }
    public int GridColumns
    {
        get => _gridColumns;
        set
        {
            var next = Math.Clamp(value, 2, 6);
            if (!Set(ref _gridColumns, next)) return;
            Raise(nameof(GridColumnsLabel));
        }
    }
    public string GridColumnsLabel => $"{GridColumns} 列";
    public string ThumbnailSize
    {
        get => _thumbnailSize;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "中" : value;
            if (!Set(ref _thumbnailSize, next)) return;
            GridColumns = next switch { "小" => 5, "大" => 2, _ => 3 };
        }
    }
    public bool CompactMode
    {
        get => _compactMode;
        set
        {
            if (!Set(ref _compactMode, value)) return;
            foreach (var tile in Tiles) tile.IsCompact = value;
        }
    }
    public bool IsWebApiReady { get => _isWebApiReady; private set => Set(ref _isWebApiReady, value); }
    public string WebApiWarning { get => _webApiWarning; private set => Set(ref _webApiWarning, value); }
    public bool HasWebApiWarning => !IsWebApiReady;
    public string KeyHint { get => _keyHint; private set => Set(ref _keyHint, value); }
    public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }
    public bool HasCredentials => _service.HasCredentials;

    public void SetPresentation(string searchText, string statusFilter, string sortOrder)
    {
        _searchText = searchText ?? "";
        _statusFilter = string.IsNullOrWhiteSpace(statusFilter) ? "全部状态" : statusFilter;
        _sortOrder = string.IsNullOrWhiteSpace(sortOrder) ? "按座位顺序" : sortOrder;
        RefreshPresentation();
    }

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
            .OrderBy(target => target.Row > 0 && target.Column > 0 ? target.Row : int.MaxValue)
            .ThenBy(target => target.Row > 0 && target.Column > 0 ? target.Column : int.MaxValue)
            .ThenBy(target => target.SeatNumber <= 0 ? int.MaxValue : target.SeatNumber)
            .Select(target => $"{target.TargetId:N}|{target.HostAddress}|{target.SeatNumber}|{target.Row}|{target.Column}|" +
                              $"{target.StudentName}|{target.ComputerName}"));
        if (string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
        {
            UpdateSummary();
            return;
        }

        _fingerprint = fingerprint;
        var ordered = targets.OrderBy(target => target.Row > 0 && target.Column > 0 ? target.Row : int.MaxValue)
            .ThenBy(target => target.Row > 0 && target.Column > 0 ? target.Column : int.MaxValue)
            .ThenBy(target => target.SeatNumber <= 0 ? int.MaxValue : target.SeatNumber)
            .ThenBy(target => target.HostAddress, StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = Tiles.ToDictionary(tile => tile.TargetId);
        for (var index = 0; index < ordered.Length; index++)
        {
            var target = ordered[index];
            if (existing.Remove(target.TargetId, out var tile))
            {
                tile.ApplyTarget(target);
                tile.IsLocated = target.TargetId == _locatedTargetId;
                if (index < Tiles.Count)
                {
                    if (!ReferenceEquals(Tiles[index], tile))
                    {
                        var oldIndex = Tiles.IndexOf(tile);
                        if (oldIndex >= 0) Tiles.Move(oldIndex, index);
                        else Tiles.Insert(index, tile);
                    }
                }
                else Tiles.Add(tile);
            }
            else
            {
                var newTile = new ClassroomScreenTileViewModel(target);
                newTile.IsLocated = target.TargetId == _locatedTargetId;
                newTile.IsCompact = CompactMode;
                newTile.PictureHeight = TilePictureHeight;
                newTile.PropertyChanged += TilePropertyChanged;
                if (index <= Tiles.Count) Tiles.Insert(index, newTile);
                else Tiles.Add(newTile);
            }
        }

        // Tiles that left the classroom must release their last frame; the collection no longer holds them.
        foreach (var leftover in existing.Values)
        {
            Tiles.Remove(leftover);
            leftover.Image?.Dispose();
        }
        HasTiles = Tiles.Count > 0;
        RefreshPresentation();
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

    internal void SetTileMetrics(double width, double pictureHeight)
    {
        if (width <= 0 || pictureHeight <= 0) return;
        TileWidth = Math.Max(160, Math.Round(width));
        TilePictureHeight = Math.Max(90, Math.Round(pictureHeight));
        TileHeight = TilePictureHeight + (CompactMode ? 88 : 112);
        foreach (var tile in Tiles) tile.PictureHeight = TilePictureHeight;
    }

    internal void ApplySelection(IReadOnlySet<Guid> selectedTargetIds)
    {
        foreach (var tile in Tiles)
            tile.IsSelected = selectedTargetIds.Contains(tile.TargetId);
    }

    internal void MarkLocated(Guid? targetId)
    {
        _locatedTargetId = targetId;
        foreach (var tile in Tiles) tile.IsLocated = tile.TargetId == targetId;
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
        RefreshPresentation();
        DeviceStateChanged?.Invoke(this, EventArgs.Empty);
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

        var previousState = tile.LinkState;
        if (update.Image is null) tile.ApplyState(update.State, update.Detail);
        else tile.ApplyFrame(update);
        if (previousState != tile.LinkState) DeviceStateChanged?.Invoke(this, EventArgs.Empty);
        RefreshPresentation();
        UpdateSummary();
    }

    private void RefreshPresentation()
    {
        var search = _searchText.Trim();
        bool Matches(ClassroomScreenTileViewModel tile)
        {
            if (search.Length > 0 && !tile.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase))
                return false;
            return _statusFilter switch
            {
                "在线" => tile.ConnectionFilterKey == "在线",
                "异常" => tile.ConnectionFilterKey == "异常",
                "网站受限" => tile.IsWebsiteRestricted,
                "键鼠已锁" => tile.IsInputLocked,
                "未排座位" => !tile.HasSeatAssignment,
                _ => true
            };
        }
        foreach (var tile in Tiles) tile.IsFilteredOut = !Matches(tile);
        var all = _sortOrder switch
        {
            "按编号排序" => Tiles.OrderBy(tile => tile.ComputerName, StringComparer.OrdinalIgnoreCase),
            "按状态排序" => Tiles.OrderBy(tile => tile.ConnectionFilterKey, StringComparer.Ordinal)
                .ThenBy(tile => tile.Row > 0 && tile.Column > 0 ? tile.Row : int.MaxValue)
                .ThenBy(tile => tile.Row > 0 && tile.Column > 0 ? tile.Column : int.MaxValue)
                .ThenBy(tile => tile.SeatNumber <= 0 ? int.MaxValue : tile.SeatNumber),
            _ => Tiles.OrderBy(tile => tile.Row > 0 && tile.Column > 0 ? tile.Row : int.MaxValue)
                .ThenBy(tile => tile.Row > 0 && tile.Column > 0 ? tile.Column : int.MaxValue)
                .ThenBy(tile => tile.SeatNumber <= 0 ? int.MaxValue : tile.SeatNumber)
        };
        var ordered = all.ToArray();
        var result = ordered.Where(Matches).ToArray();
        Replace(FilteredTiles, result);
        Replace(SeatedTiles, result.Where(tile => tile.HasSeatAssignment).ToArray());
        Replace(UnseatedTiles, result.Where(tile => !tile.HasSeatAssignment).ToArray());
        Replace(AllUnseatedTiles, ordered.Where(tile => !tile.HasSeatAssignment).ToArray());
        Raise(nameof(HasFilteredTiles));
        Raise(nameof(HasFilteredUnseatedTiles));
        Raise(nameof(HasUnseatedDevices));
        Raise(nameof(FilterSummary));
    }

    private void TilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClassroomScreenTileViewModel.LinkStatusText) or
            nameof(ClassroomScreenTileViewModel.IsInputLocked) or
            nameof(ClassroomScreenTileViewModel.IsWebsiteRestricted))
            RefreshPresentation();
    }

    public bool HasFilteredTiles => FilteredTiles.Count > 0;
    public bool HasFilteredUnseatedTiles => UnseatedTiles.Count > 0;
    public bool HasUnseatedDevices => AllUnseatedTiles.Count > 0;
    public string FilterSummary => _searchText.Trim().Length == 0 && _statusFilter == "全部状态"
        ? $"显示 {Tiles.Count} 台设备"
        : $"筛选后显示 {FilteredTiles.Count}/{Tiles.Count} 台";

    private static void Replace(ObservableCollection<ClassroomScreenTileViewModel> collection,
        IReadOnlyList<ClassroomScreenTileViewModel> items)
    {
        if (collection.Count == items.Count && collection.SequenceEqual(items)) return;
        collection.Clear();
        foreach (var item in items) collection.Add(item);
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

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
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
