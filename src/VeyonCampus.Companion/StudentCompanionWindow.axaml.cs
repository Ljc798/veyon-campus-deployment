using Avalonia.Controls;
namespace VeyonCampus.Companion;

public partial class StudentCompanionWindow : Window
{
    private bool _allowClose;

    public StudentCompanionWindow() : this(new StudentCompanionViewModel())
    {
    }

    public StudentCompanionWindow(StudentCompanionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            HideToTray();
        };
    }

    public void ShowFromTray()
    {
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

}
