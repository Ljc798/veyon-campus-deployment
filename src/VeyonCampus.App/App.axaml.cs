using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace VeyonCampus.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if TEACHER_CONSOLE
            desktop.MainWindow = new TeacherWindow();
#else
            desktop.MainWindow = new StudentSetupWindow();
#endif
        }
        base.OnFrameworkInitializationCompleted();
    }
}
