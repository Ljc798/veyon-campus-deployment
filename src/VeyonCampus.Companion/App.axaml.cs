using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using VeyonCampus.Core;

namespace VeyonCampus.Companion;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private StudentCompanionWindow? _window;
    private StudentCompanionCommunicationWindow? _communicationWindow;
    private TrayIcon? _trayIcon;
    private StudentCompanionViewModel? _viewModel;
    private StudentCompanionStatusPoller? _statusPoller;
    private StudentCompanionEventPoller? _eventPoller;
    private StudentCompanionBrowserRestartPoller? _browserRestartPoller;
    private StudentCompanionEdgePolicyExecutorSupervisor? _edgePolicyExecutorSupervisor;
    private StudentCompanionInputGuardSupervisor? _inputGuardSupervisor;
    private RegisteredWaitHandle? _activationWait;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            var companionStartedUtc = DateTimeOffset.UtcNow;
            var eventPoller = new StudentCompanionEventPoller();
            _eventPoller = eventPoller;
            var viewModel = new StudentCompanionViewModel(eventPoller.RequestHelpAsync,
                eventPoller.MarkHelpResolvedAsync, eventPoller.SendChatMessageAsync);
            _viewModel = viewModel;
            var communicationWindow = new StudentCompanionCommunicationWindow(viewModel);
            _communicationWindow = communicationWindow;
            var window = new StudentCompanionWindow(viewModel, communicationWindow.ShowForUser);
            _window = window;
            var backgroundStartup = desktop.Args?.Contains("--startup", StringComparer.Ordinal) == true;
            if (backgroundStartup)
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            else
                desktop.MainWindow = window;
            _statusPoller = new StudentCompanionStatusPoller();
            var eventPollerStarted = false;
            _statusPoller.Start(snapshot => Dispatcher.UIThread.Post(() =>
            {
                viewModel.ApplyStatus(snapshot);
                if (eventPollerStarted || snapshot.ConnectionState != StudentCompanionConnectionState.ClassroomActive)
                    return;
                eventPollerStarted = true;
                eventPoller.Start(classroomEvent => Dispatcher.UIThread.Post(() =>
                {
                    var applied = viewModel.ApplyTeacherEvent(classroomEvent);
                    if (applied && (classroomEvent.Type is ClassroomEventType.ClassroomNotice or
                        ClassroomEventType.ClassroomChatMessage or ClassroomEventType.TeacherReply or
                        ClassroomEventType.HelpAcknowledged))
                        communicationWindow.ShowForIncomingMessage();
                }), companionStartedUtc);
            }));
            if (OperatingSystem.IsWindows())
            {
                _edgePolicyExecutorSupervisor = new StudentCompanionEdgePolicyExecutorSupervisor();
                _edgePolicyExecutorSupervisor.Start();
                _inputGuardSupervisor = new StudentCompanionInputGuardSupervisor();
                _inputGuardSupervisor.Start();
                _browserRestartPoller = new StudentCompanionBrowserRestartPoller();
                _browserRestartPoller.Start();
            }
            if (Program.ActivationEvent is { } activationEvent)
            {
                _activationWait = ThreadPool.RegisterWaitForSingleObject(activationEvent,
                    (_, _) => Dispatcher.UIThread.Post(() => _window?.ShowFromTray()),
                    null, Timeout.Infinite, executeOnlyOnce: false);
            }

            using var iconStream = AssetLoader.Open(new Uri(
                "avares://VeyonCampus.StudentCompanion/Assets/veyon-campus.ico"));
            var windowIcon = new WindowIcon(iconStream);
            window.Icon = windowIcon;

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
                if (_browserRestartPoller is not null) await _browserRestartPoller.DisposeAsync();
                if (_edgePolicyExecutorSupervisor is not null)
                    await _edgePolicyExecutorSupervisor.DisposeAsync();
                if (_inputGuardSupervisor is not null)
                    await _inputGuardSupervisor.DisposeAsync();
                _activationWait?.Unregister(null);
                _activationWait = null;
                if (_trayIcon is not null) _trayIcon.IsVisible = false;
                _communicationWindow?.CloseForExit();
                _window?.CloseForExit();
            };

            if (backgroundStartup)
            {
                // Do not assign a MainWindow for startup runs. The tray and pollers
                // stay active without showing a blank window at logon.
                _window.ShowInTaskbar = false;
                _window.WindowState = WindowState.Minimized;
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
        menu.Items.Add(new NativeMenuItem
        {
            Header = "给老师发消息",
            Command = new RelayCommand(() => _communicationWindow?.ShowForUser())
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
