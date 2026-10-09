using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace VeyonCampus.Companion;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private StudentCompanionWindow? _window;
    private TrayIcon? _trayIcon;
    private StudentCompanionViewModel? _viewModel;
    private StudentCompanionStatusPoller? _statusPoller;
    private StudentCompanionEventPoller? _eventPoller;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _eventPoller = new StudentCompanionEventPoller();
            _viewModel = new StudentCompanionViewModel(_eventPoller.RequestHelpAsync);
            _window = new StudentCompanionWindow(_viewModel);
            desktop.MainWindow = _window;
            _eventPoller.Start(classroomEvent => Dispatcher.UIThread.Post(
                () => _viewModel?.ApplyTeacherEvent(classroomEvent)));
            _statusPoller = new StudentCompanionStatusPoller();
            _statusPoller.Start(snapshot => Dispatcher.UIThread.Post(() => _viewModel?.ApplyStatus(snapshot)));

            using var iconStream = AssetLoader.Open(new Uri(
                "avares://VeyonCampus.StudentCompanion/Assets/veyon-campus.ico"));
            var windowIcon = new WindowIcon(iconStream);
            _window.Icon = windowIcon;

            _trayIcon = new TrayIcon
            {
                Icon = windowIcon,
                ToolTipText = "Veyon Campus 课堂助手",
                IsVisible = true,
                Menu = CreateTrayMenu()
            };
            _trayIcon.Clicked += (_, _) => _window?.ShowFromTray();
            var icons = new TrayIcons { _trayIcon };
            TrayIcon.SetIcons(this, icons);

            desktop.Exit += async (_, _) =>
            {
                if (_statusPoller is not null) await _statusPoller.DisposeAsync();
                if (_eventPoller is not null) await _eventPoller.DisposeAsync();
                if (_trayIcon is not null) _trayIcon.IsVisible = false;
                _window?.CloseForExit();
            };

            if (desktop.Args?.Contains("--startup", StringComparer.Ordinal) == true)
            {
                _window.ShowInTaskbar = false;
                _window.WindowState = WindowState.Minimized;
                _window.Opened += (_, _) => _window.HideToTray();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private NativeMenu CreateTrayMenu()
    {
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem
        {
            Header = "打开课堂助手",
            Command = new RelayCommand(() => _window?.ShowFromTray())
        });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem
        {
            Header = "退出",
            Command = new RelayCommand(ExitApplication)
        });
        return menu;
    }

    private void ExitApplication()
    {
        _desktop?.Shutdown();
    }

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
