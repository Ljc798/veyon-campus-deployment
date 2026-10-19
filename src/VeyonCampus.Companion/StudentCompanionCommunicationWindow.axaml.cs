using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;

namespace VeyonCampus.Companion;

public partial class StudentCompanionCommunicationWindow : Window
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private readonly StudentCompanionViewModel _viewModel;
    private bool _allowClose;

    public StudentCompanionCommunicationWindow(StudentCompanionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CommunicationItems.CollectionChanged += CommunicationItemsChanged;
        Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            Hide();
        };
        Closed += (_, _) => viewModel.CommunicationItems.CollectionChanged -= CommunicationItemsChanged;
        PositionInLowerRight();
    }

    public void ShowForIncomingMessage()
    {
        ShowActivated = false;
        PositionInLowerRight();
        if (!IsVisible)
            Show();
        PlaceAboveWithoutActivation();
        ScrollToLatestMessage();
    }

    public void ShowForUser()
    {
        ShowActivated = true;
        PositionInLowerRight();
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        Activate();
        ChatInput.Focus();
        ScrollToLatestMessage();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private async void SendClicked(object? sender, RoutedEventArgs e) =>
        await _viewModel.SendClassroomChatMessageAsync();

    private async void RetryMessageClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not StudentCompanionCommunicationItem item) return;
        await _viewModel.RetryClassroomChatMessageAsync(item.EventId);
    }

    private void HideClicked(object? sender, RoutedEventArgs e) => Hide();

    private void CommunicationItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(ScrollToLatestMessage);

    private void ScrollToLatestMessage()
    {
        ChatHistoryScroller.Offset = new Vector(0, ChatHistoryScroller.Extent.Height);
    }

    private void PositionInLowerRight()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        var width = (int)Math.Round(Math.Min(Width, area.Width / scaling) * scaling);
        var height = (int)Math.Round(Math.Min(Height + 32d / scaling, area.Height / scaling) * scaling);
        var margin = (int)Math.Round(16 * scaling);
        Position = new PixelPoint(area.X + Math.Max(0, area.Width - width - margin),
            area.Y + Math.Max(0, area.Height - height - margin));
    }

    private void PlaceAboveWithoutActivation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scaling = screen is { Scaling: > 0 } selectedScreen ? selectedScreen.Scaling : 1d;
        _ = SetWindowPos(handle, IntPtr.Zero, Position.X, Position.Y,
            (int)Math.Round(Width * scaling), (int)Math.Round(Height * scaling) + 32,
            SwpNoActivate | SwpShowWindow);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}
