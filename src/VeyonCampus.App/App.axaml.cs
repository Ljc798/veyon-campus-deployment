using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace VeyonCampus.App;

public partial class App : Application
{
#if TEACHER_CONSOLE
    private TrayIcon? _teacherTrayIcon;
#endif

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if TEACHER_CONSOLE
            var teacherWindow = new TeacherWindow();
            desktop.MainWindow = teacherWindow;
            if (OperatingSystem.IsWindows())
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                teacherWindow.EnableTrayBackgroundMode();
                InitializeTeacherTray(teacherWindow);
            }
#else
            desktop.MainWindow = new StudentSetupWindow();
#endif
        }
        base.OnFrameworkInitializationCompleted();
    }

#if TEACHER_CONSOLE
    private void InitializeTeacherTray(TeacherWindow window)
    {
        var icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri(
                $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico"))),
            ToolTipText = "Veyon Campus 教师控制台（后台运行）"
        };
        icon.Clicked += (_, _) => ShowTeacherWindow(window);

        var menu = new NativeMenu();
        var openItem = new NativeMenuItem("打开教师控制台");
        openItem.Click += (_, _) => ShowTeacherWindow(window);
        var exitItem = new NativeMenuItem("退出 Veyon Campus");
        exitItem.Click += (_, _) =>
        {
            window.AllowApplicationExit();
            ShutdownApplication();
        };
        menu.Items.Add(openItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);
        icon.Menu = menu;
        TrayIcon.SetIcons(this, new TrayIcons { icon });
        _teacherTrayIcon = icon;
    }

    internal bool ShutdownApplication()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return false;
        _teacherTrayIcon?.Dispose();
        _teacherTrayIcon = null;
        desktop.Shutdown();
        return true;
    }

    private static void ShowTeacherWindow(TeacherWindow window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
#endif
}
