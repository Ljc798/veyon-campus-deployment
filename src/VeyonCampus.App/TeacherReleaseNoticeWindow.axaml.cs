using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VeyonCampus.App;

public partial class TeacherReleaseNoticeWindow : Window
{
    public TeacherReleaseNoticeWindow()
    {
        InitializeComponent();
    }

    public TeacherReleaseNoticeWindow(string releaseDetails)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseDetails);
        ReleaseDetails.Text = releaseDetails;
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
}
