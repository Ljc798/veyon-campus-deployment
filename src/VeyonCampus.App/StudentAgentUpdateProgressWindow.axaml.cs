using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VeyonCampus.App;

public partial class StudentAgentUpdateProgressWindow : Window
{
    private MainViewModel? _model;

    public StudentAgentUpdateProgressWindow() => InitializeComponent();

    public StudentAgentUpdateProgressWindow(MainViewModel model) : this()
    {
        _model = model;
        DataContext = model;
        Closing += (_, e) =>
        {
            if (_model?.IsStudentAgentInstallInProgress == true) e.Cancel = true;
        };
    }

    private void CloseDialog(object? sender, RoutedEventArgs e) => Close();
}
