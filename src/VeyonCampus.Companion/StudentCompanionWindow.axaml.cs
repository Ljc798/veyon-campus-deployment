using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using VeyonCampus.Core;

namespace VeyonCampus.Companion;

public partial class StudentCompanionWindow : Window
{
    private bool _allowClose;
    private readonly Action? _openCommunication;
    private readonly DispatcherTimer _noticeExpiryTimer = new();
    private readonly StudentCompanionViewModel _viewModel;

    public StudentCompanionWindow() : this(new StudentCompanionViewModel())
    {
    }

    public StudentCompanionWindow(StudentCompanionViewModel viewModel, Action? openCommunication = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _openCommunication = openCommunication;
        DataContext = viewModel;
        PositionInLowerRight();
        viewModel.PropertyChanged += ViewModelPropertyChanged;
        _noticeExpiryTimer.Tick += NoticeExpiryTimerTick;
        Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            HideToTray();
        };
        Closed += (_, _) =>
        {
            _noticeExpiryTimer.Stop();
            viewModel.PropertyChanged -= ViewModelPropertyChanged;
        };
        ScheduleNoticeExpiry();
    }

    public void ShowFromTray()
    {
        Opacity = 1;
        ShowInTaskbar = true;
        PositionInLowerRight();
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private async void HelpActionClicked(object? sender, RoutedEventArgs e)
    {
        await _viewModel.ActivateHelpActionAsync();
    }

    private void OpenCommunicationClicked(object? sender, RoutedEventArgs e) => _openCommunication?.Invoke();

    private void HideClicked(object? sender, RoutedEventArgs e) => HideToTray();

    private void PositionInLowerRight()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        var width = (int)Math.Round(Math.Min(Width, area.Width / scaling) * scaling);
        var height = (int)Math.Round(Math.Min(Height, area.Height / scaling) * scaling);
        var margin = (int)Math.Round(14 * scaling);
        Position = new PixelPoint(area.X + Math.Max(0, area.Width - width - margin),
            area.Y + Math.Max(0, area.Height - height - margin));
    }

    private void OpenClassroomNoticeLinkClicked(object? sender, RoutedEventArgs e)
    {
        var link = _viewModel.GetActiveClassroomNoticeLink(DateTimeOffset.UtcNow);
        if (link is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                          FileNotFoundException or NotSupportedException)
        {
            _viewModel.SetClassroomNoticeOpenFailed();
        }
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(StudentCompanionViewModel.ClassroomNoticeExpiresUtc))
            ScheduleNoticeExpiry();
    }

    private void NoticeExpiryTimerTick(object? sender, EventArgs args)
    {
        _noticeExpiryTimer.Stop();
        _viewModel.RefreshClassroomNoticeExpiry(DateTimeOffset.UtcNow);
        ScheduleNoticeExpiry();
    }

    private void ScheduleNoticeExpiry()
    {
        _noticeExpiryTimer.Stop();
        if (_viewModel.ClassroomNoticeExpiresUtc is not { } expiresUtc) return;
        var remaining = expiresUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _viewModel.RefreshClassroomNoticeExpiry(DateTimeOffset.UtcNow);
            return;
        }

        _noticeExpiryTimer.Interval = remaining > TimeSpan.FromMinutes(2)
            ? TimeSpan.FromMinutes(2)
            : remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining;
        _noticeExpiryTimer.Start();
    }
}
