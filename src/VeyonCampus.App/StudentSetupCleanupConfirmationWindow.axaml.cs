using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace VeyonCampus.App;

public partial class StudentSetupCleanupConfirmationWindow : Window
{
    public StudentSetupCleanupConfirmationWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
    }

    private void UpdateConfirmationState(object? sender, RoutedEventArgs e)
    {
        ConfirmCleanupButton.IsEnabled = OtherSettingsCheck.IsChecked == true &&
            StudentAccountCheck.IsChecked == true && ComputerNameCheck.IsChecked == true;
    }

    private void ConfirmCleanup(object? sender, RoutedEventArgs e)
    {
        if (OtherSettingsCheck.IsChecked == true && StudentAccountCheck.IsChecked == true &&
            ComputerNameCheck.IsChecked == true)
            Close(true);
    }
}
