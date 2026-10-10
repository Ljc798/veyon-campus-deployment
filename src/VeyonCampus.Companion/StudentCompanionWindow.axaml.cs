using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VeyonCampus.Core;

namespace VeyonCampus.Companion;

public partial class StudentCompanionWindow : Window
{
    private bool _allowClose;
    private readonly DispatcherTimer _noticeExpiryTimer = new();
    private readonly StudentCompanionViewModel _viewModel;

    public StudentCompanionWindow() : this(new StudentCompanionViewModel())
    {
    }

    public StudentCompanionWindow(StudentCompanionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
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
