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
    private string _seatText;
    private string _hostText;
    private string _linkStatusText = "待连接";
    private IBrush _linkStatusBrush = NeutralBrush;
    private IBrush _linkStatusBackground = NeutralSubtleBrush;
    private string _frameInfoText = "尚无画面";
    private string _detailText = "";
    private bool _hasFrame;
    private bool _isSelected;
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
        _seatText = target.SeatNumber > 0
            ? $"{target.SeatNumber.ToString("00", CultureInfo.InvariantCulture)} 号位"
            : "未排座位";
        _hostText = target.HostAddress;
        _detailText = string.IsNullOrWhiteSpace(target.ComputerName) ? target.HostAddress : target.ComputerName;
    }

    public Guid TargetId { get; }
    public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); }
    public string SeatText { get => _seatText; private set => Set(ref _seatText, value); }
    public string HostText { get => _hostText; private set => Set(ref _hostText, value); }
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

    public IBrush CardBorderBrush => IsSelected ? SelectedBorderBrush : CardBorderBrushDefault;
    public Avalonia.Thickness CardBorderThickness => IsSelected ? new Avalonia.Thickness(2) : new Avalonia.Thickness(1);

    public bool IsInputLocked => _isInputLocked;
    public bool IsInputUnverified => _isInputUnverified;
    public bool CanLockInput => !_isInputLocked;
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
        SeatText = target.SeatNumber > 0
            ? $"{target.SeatNumber.ToString("00", CultureInfo.InvariantCulture)} 号位"
            : "未排座位";
        HostText = target.HostAddress;
        DetailText = string.IsNullOrWhiteSpace(target.ComputerName) ? target.HostAddress : target.ComputerName;
    }

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
        // A live connection should not replace the card's seat/address subtitle with "连接正常";
        // problem states are exactly where the teacher needs the reason on the card.
        if (!string.IsNullOrWhiteSpace(detail) && state != ScreenWallLinkState.Live)
            DetailText = detail;
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
        var locked = device?.IsDeviceInputLocked ?? false;
        var unverified = device?.IsDeviceInputUnverified ?? false;
        if (_isInputLocked != locked || _isInputUnverified != unverified)
        {
            _isInputLocked = locked;
            _isInputUnverified = unverified;
            Raise(nameof(IsInputLocked));
            Raise(nameof(IsInputUnverified));
            Raise(nameof(CanLockInput));
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
