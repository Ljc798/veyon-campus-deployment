using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace VeyonCampus.App;

public partial class StudentWebsiteAgentRemovalConfirmationWindow : Window
{
    public StudentWebsiteAgentRemovalConfirmationWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
    }

    private void UpdateConfirmationState(object? sender, RoutedEventArgs e) =>
        ConfirmRemovalButton.IsEnabled = ConfirmRemovalCheck.IsChecked == true;

    private void CancelRemoval(object? sender, RoutedEventArgs e) => Close(false);

    private void ConfirmRemoval(object? sender, RoutedEventArgs e)
    {
        if (ConfirmRemovalCheck.IsChecked == true) Close(true);
    }
}
