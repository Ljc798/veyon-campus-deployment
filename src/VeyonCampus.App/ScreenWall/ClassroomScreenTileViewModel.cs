using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VeyonCampus.App;

/// <summary>
/// One card on the screen wall. Holds the real last frame from the student machine plus the two states a
/// teacher acts on from the card: keyboard/mouse lock and website restriction. Both are projected from
/// the classroom quick-control snapshot, so the card never claims a state the student Agent did not verify.
/// </summary>
public sealed class ClassroomScreenTileViewModel : INotifyPropertyChanged
{
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#16803C"));
    private static readonly IBrush SuccessSubtleBrush = new SolidColorBrush(Color.Parse("#E5F3EA"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#1769AA"));
    private static readonly IBrush InfoSubtleBrush = new SolidColorBrush(Color.Parse("#E7F1FA"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#9A5B00"));
    private static readonly IBrush WarningSubtleBrush = new SolidColorBrush(Color.Parse("#FFF3D6"));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#B42318"));
    private static readonly IBrush DangerSubtleBrush = new SolidColorBrush(Color.Parse("#FDECEC"));
    private static readonly IBrush NeutralBrush = new SolidColorBrush(Color.Parse("#526674"));
    private static readonly IBrush NeutralSubtleBrush = new SolidColorBrush(Color.Parse("#E9EEF2"));
    private static readonly IBrush SelectedBorderBrush = new SolidColorBrush(Color.Parse("#168B83"));
    private static readonly IBrush CardBorderBrushDefault = new SolidColorBrush(Color.Parse("#DFE8ED"));

    private Bitmap? _image;
    private string _displayName;
    private string _studentName;
    private string _seatText;
    private string _hostText;
    private string _computerName;
    private int _seatNumber;
    private int _row;
    private int _column;
    private double _pictureHeight = 120;
    private string _linkStatusText = "待连接";
    private IBrush _linkStatusBrush = NeutralBrush;
    private IBrush _linkStatusBackground = NeutralSubtleBrush;
    private string _frameInfoText = "尚无画面";
    private string _detailText = "";
    private string _websiteStatusDetail = "网站状态尚未核验";
    private string _inputStatusDetail = "键鼠状态尚未核验";
    private string _controlExpiryText = "尚未核验截止时间";
    private string _lastControlUpdateText = "尚无本机管控记录";
    private string _controlDetailText = "尚未收到课堂管控回执";
    private bool _hasFrame;
    private bool _isSelected;
    private bool _isLocated;
    private bool _isCompact;
    private bool _isFilteredOut;
    private bool _isInputLocked;
    private bool _isInputUnverified;
    private bool _isWebsiteRestricted;
    private string _websiteBadgeText = "网站未限制";
    private IBrush _websiteBadgeBrush = NeutralBrush;
    private IBrush _websiteBadgeBackground = NeutralSubtleBrush;
    private ScreenWallLinkState _linkState = ScreenWallLinkState.Idle;
    private DateTimeOffset _capturedUtc = DateTimeOffset.MinValue;
    private int _lastHttpMs;
    private int _lastBytes;

    internal ClassroomScreenTileViewModel(ClassroomScreenTarget target)
    {
        TargetId = target.TargetId;
        _displayName = target.BuildDisplayName();
        _studentName = target.StudentName;
        _computerName = string.IsNullOrWhiteSpace(target.ComputerName) ? target.HostAddress : target.ComputerName;
        _seatNumber = target.SeatNumber;
        _row = target.Row;
        _column = target.Column;
        _seatText = BuildSeatText(target.SeatNumber, target.Row, target.Column);
        _hostText = target.HostAddress;
        _detailText = string.IsNullOrWhiteSpace(target.ComputerName) ? target.HostAddress : target.ComputerName;
    }

    public Guid TargetId { get; }
    public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); }
    public string StudentName { get => _studentName; private set => Set(ref _studentName, value); }
    public string IdentitySummaryText => $"{ComputerName} · {SeatText}";
    public string StudentDisplayName => string.IsNullOrWhiteSpace(StudentName) ? "未关联学生姓名" : StudentName;
    public string SeatText { get => _seatText; private set => Set(ref _seatText, value); }
    public string HostText { get => _hostText; private set => Set(ref _hostText, value); }
    public string ComputerName { get => _computerName; private set => Set(ref _computerName, value); }
    public int SeatNumber { get => _seatNumber; private set => Set(ref _seatNumber, value); }
    public int Row { get => _row; private set => Set(ref _row, value); }
    public int Column { get => _column; private set => Set(ref _column, value); }
    public bool HasSeatAssignment => SeatNumber > 0 || Row > 0 && Column > 0;
    public string SeatCoordinateText => Row > 0 && Column > 0
        ? $"第 {Row} 行 · 第 {Column} 列" : "未关联到物理座位";
    public double PictureHeight { get => _pictureHeight; internal set => Set(ref _pictureHeight, value); }
    public Bitmap? Image { get => _image; private set => Set(ref _image, value); }
    public bool HasFrame { get => _hasFrame; private set => Set(ref _hasFrame, value); }
    public string LinkStatusText { get => _linkStatusText; private set => Set(ref _linkStatusText, value); }
    public IBrush LinkStatusBrush { get => _linkStatusBrush; private set => Set(ref _linkStatusBrush, value); }
    public IBrush LinkStatusBackground
    {
        get => _linkStatusBackground;
        private set => Set(ref _linkStatusBackground, value);
    }

    public string FrameInfoText { get => _frameInfoText; private set => Set(ref _frameInfoText, value); }
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }
    public string WebsiteStatusDetail { get => _websiteStatusDetail; private set => Set(ref _websiteStatusDetail, value); }
    public string InputStatusDetail { get => _inputStatusDetail; private set => Set(ref _inputStatusDetail, value); }
    public string ControlExpiryText { get => _controlExpiryText; private set => Set(ref _controlExpiryText, value); }
    public string LastControlUpdateText { get => _lastControlUpdateText; private set => Set(ref _lastControlUpdateText, value); }
    public string ControlDetailText { get => _controlDetailText; private set => Set(ref _controlDetailText, value); }

    public string ConnectionSummary => _linkState switch
    {
        ScreenWallLinkState.Live => "在线 · 屏幕实时更新",
        ScreenWallLinkState.Connecting or ScreenWallLinkState.AwaitingFirstFrame => "正在连接学生电脑",
        ScreenWallLinkState.Stale => "连接正常，画面暂未更新",
        ScreenWallLinkState.Offline => "学生电脑离线或不可达",
        ScreenWallLinkState.AuthenticationFailed => "Veyon 认证失败",
        ScreenWallLinkState.Unavailable => "本机 Veyon WebAPI 未就绪",
        ScreenWallLinkState.FormatUnsupported => "屏幕画面格式不受支持",
        _ => "等待屏幕连接"
    };

    public string SearchText => $"{DisplayName} {ComputerName} {HostText} {SeatText} {TargetId:N}";
    public string ConnectionFilterKey => _linkState is ScreenWallLinkState.Live ? "在线"
        : _linkState is ScreenWallLinkState.Offline or ScreenWallLinkState.AuthenticationFailed or
            ScreenWallLinkState.Unavailable or ScreenWallLinkState.FormatUnsupported or ScreenWallLinkState.Stale
            ? "异常" : "连接中";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            Raise(nameof(CardBorderBrush));
            Raise(nameof(CardBorderThickness));
        }
    }

    public bool IsLocated
    {
        get => _isLocated;
        internal set
        {
            if (!Set(ref _isLocated, value)) return;
            Raise(nameof(CardBorderBrush));
            Raise(nameof(CardBorderThickness));
        }
    }

    public bool IsCompact { get => _isCompact; internal set => Set(ref _isCompact, value); }
    public bool IsFilteredOut { get => _isFilteredOut; internal set => Set(ref _isFilteredOut, value); }

    public IBrush CardBorderBrush => IsSelected || IsLocated ? SelectedBorderBrush : CardBorderBrushDefault;
    public Avalonia.Thickness CardBorderThickness => IsLocated ? new Avalonia.Thickness(3)
        : IsSelected ? new Avalonia.Thickness(2) : new Avalonia.Thickness(1);

    public bool IsInputLocked => _isInputLocked;
    public bool IsInputUnverified => _isInputUnverified;
    public bool CanLockInput => !_isInputLocked && !_isInputUnverified;
    public bool CanUnlockInput => _isInputLocked || _isInputUnverified;
    public string InputButtonText => _isInputLocked ? "解除键鼠" : "锁定键鼠";
    public string InputBadgeText => _isInputLocked ? "键鼠已锁" : _isInputUnverified ? "键鼠待核验" : "键鼠未锁";
    public IBrush InputBadgeBrush => _isInputLocked ? InfoBrush : _isInputUnverified ? WarningBrush : NeutralBrush;
    public IBrush InputBadgeBackground =>
        _isInputLocked ? InfoSubtleBrush : _isInputUnverified ? WarningSubtleBrush : NeutralSubtleBrush;

    public bool IsWebsiteRestricted => _isWebsiteRestricted;
    public string WebsiteBadgeText { get => _websiteBadgeText; private set => Set(ref _websiteBadgeText, value); }
    public IBrush WebsiteBadgeBrush { get => _websiteBadgeBrush; private set => Set(ref _websiteBadgeBrush, value); }
    public IBrush WebsiteBadgeBackground
    {
        get => _websiteBadgeBackground;
        private set => Set(ref _websiteBadgeBackground, value);
    }

    public string WebsiteButtonText => _isWebsiteRestricted ? "恢复网站" : "网站策略";

    internal ScreenWallLinkState LinkState => _linkState;
    internal DateTimeOffset CapturedUtc => _capturedUtc;

    internal void ApplyTarget(ClassroomScreenTarget target)
    {
        DisplayName = target.BuildDisplayName();
        StudentName = target.StudentName;
        Raise(nameof(StudentDisplayName));
        ComputerName = string.IsNullOrWhiteSpace(target.ComputerName) ? target.HostAddress : target.ComputerName;
        SeatNumber = target.SeatNumber;
        Row = target.Row;
        Column = target.Column;
        SeatText = BuildSeatText(target.SeatNumber, target.Row, target.Column);
        Raise(nameof(SeatCoordinateText));
        Raise(nameof(IdentitySummaryText));
        Raise(nameof(HasSeatAssignment));
        HostText = target.HostAddress;
        DetailText = string.IsNullOrWhiteSpace(target.ComputerName) ? target.HostAddress : target.ComputerName;
    }

    private static string BuildSeatText(int seatNumber, int row, int column) => seatNumber > 0
        ? $"{seatNumber.ToString("00", CultureInfo.InvariantCulture)} 号位"
        : row > 0 && column > 0 ? $"第 {row} 行 · 第 {column} 列" : "未排座位";

    internal void ApplyState(ScreenWallLinkState state, string detail)
    {
        _linkState = state;
        (LinkStatusText, LinkStatusBrush, LinkStatusBackground) = state switch
        {
            ScreenWallLinkState.Live => ("实时", SuccessBrush, SuccessSubtleBrush),
            ScreenWallLinkState.Connecting => ("连接中", InfoBrush, InfoSubtleBrush),
            ScreenWallLinkState.AwaitingFirstFrame => ("等待首帧", InfoBrush, InfoSubtleBrush),
            ScreenWallLinkState.Stale => ("画面过期", WarningBrush, WarningSubtleBrush),
            ScreenWallLinkState.Offline => ("离线", DangerBrush, DangerSubtleBrush),
            ScreenWallLinkState.AuthenticationFailed => ("认证失败", DangerBrush, DangerSubtleBrush),
            ScreenWallLinkState.Unavailable => ("WebAPI 未启用", WarningBrush, WarningSubtleBrush),
            ScreenWallLinkState.FormatUnsupported => ("格式不支持", DangerBrush, DangerSubtleBrush),
            _ => ("待连接", NeutralBrush, NeutralSubtleBrush)
        };
        if (state == ScreenWallLinkState.Live)
            DetailText = string.IsNullOrWhiteSpace(ComputerName) ? HostText : ComputerName;
        else if (!string.IsNullOrWhiteSpace(detail))
            DetailText = detail;
        Raise(nameof(ConnectionSummary));
        Raise(nameof(ConnectionFilterKey));
    }

    internal void ApplyFrame(ScreenWallUpdate update)
    {
        ApplyState(update.State, update.Detail);
        if (update.Image is { } bitmap)
        {
            var previous = _image;
            Image = bitmap;
            HasFrame = true;
            _capturedUtc = update.CapturedUtc;
            _lastHttpMs = update.HttpMs;
            _lastBytes = update.ImageBytes;
            previous?.Dispose();
        }

        RefreshAge(DateTimeOffset.UtcNow);
    }

    internal void RefreshAge(DateTimeOffset now)
    {
        if (_capturedUtc == DateTimeOffset.MinValue)
        {
            FrameInfoText = _linkState == ScreenWallLinkState.Live ? "尚未收到画面" : LinkStatusText;
            return;
        }

        var age = now - _capturedUtc;
        var ageText = age.TotalSeconds < 1.5 ? "刚刚"
            : age.TotalSeconds < 60 ? $"{(int)age.TotalSeconds} 秒前"
            : $"{(int)age.TotalMinutes} 分钟前";
        var kilobytes = Math.Max(1, _lastBytes / 1024);
        FrameInfoText = $"画面 {ageText} · {_lastHttpMs} ms · {kilobytes} KB";
        if (_linkState == ScreenWallLinkState.Live && age.TotalSeconds > 6)
            ApplyState(ScreenWallLinkState.Stale, "画面超过 6 秒没有更新");
    }

    internal void ApplyControlState(TeacherClassroomQuickControlDeviceItem? device)
    {
        WebsiteStatusDetail = device?.WebsiteControlStatus ?? "网站状态尚未核验";
        InputStatusDetail = device?.InputControlStatus ?? "键鼠状态尚未核验";
        ControlExpiryText = device?.ExpiresText ?? "尚未核验截止时间";
        LastControlUpdateText = device?.UpdatedText ?? "尚无本机管控记录";
        ControlDetailText = device?.Detail ?? "尚未收到课堂管控回执";
        var locked = device?.IsDeviceInputLocked ?? false;
        var unverified = device?.IsDeviceInputUnverified ?? false;
        if (_isInputLocked != locked || _isInputUnverified != unverified)
        {
            _isInputLocked = locked;
            _isInputUnverified = unverified;
            Raise(nameof(IsInputLocked));
            Raise(nameof(IsInputUnverified));
            Raise(nameof(CanLockInput));
            Raise(nameof(CanUnlockInput));
            Raise(nameof(InputButtonText));
            Raise(nameof(InputBadgeText));
            Raise(nameof(InputBadgeBrush));
            Raise(nameof(InputBadgeBackground));
        }

        var websiteState = device?.WebsiteState ?? "unknown";
        var restricted = websiteState is "applied" or "reported" or "applying";
        var (text, brush, background) = websiteState switch
        {
            "applied" => ("网站已限制", SuccessBrush, SuccessSubtleBrush),
            "reported" => ("网站待核对", WarningBrush, WarningSubtleBrush),
            "applying" => ("网站执行中", InfoBrush, InfoSubtleBrush),
            "waitingForAgent" => ("等待学生端", WarningBrush, WarningSubtleBrush),
            "needsReview" => ("网站需核对", DangerBrush, DangerSubtleBrush),
            "expired" => ("网站已到期", NeutralBrush, NeutralSubtleBrush),
            _ => ("网站未限制", NeutralBrush, NeutralSubtleBrush)
        };
        if (_isWebsiteRestricted != restricted)
        {
            _isWebsiteRestricted = restricted;
            Raise(nameof(IsWebsiteRestricted));
            Raise(nameof(WebsiteButtonText));
        }

        WebsiteBadgeText = text;
        WebsiteBadgeBrush = brush;
        WebsiteBadgeBackground = background;
        Raise(nameof(SearchText));
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
}
