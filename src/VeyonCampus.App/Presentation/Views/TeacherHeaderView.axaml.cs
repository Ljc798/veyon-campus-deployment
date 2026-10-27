using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VeyonCampus.App.Presentation.Views;

public partial class TeacherHeaderView : UserControl
{
    public TeacherHeaderView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? ClassroomSessionToggleRequested;
    public event EventHandler<RoutedEventArgs>? ClassroomNoticeRequested;
    public event EventHandler<RoutedEventArgs>? ClassroomChatToggleRequested;

    public bool FocusClassroomChatToggle() => ClassroomChatDrawerToggle.Focus();

    private void RequestClassroomSessionToggle(object? sender, RoutedEventArgs e) =>
        ClassroomSessionToggleRequested?.Invoke(this, e);

    private void RequestClassroomNotice(object? sender, RoutedEventArgs e) =>
        ClassroomNoticeRequested?.Invoke(this, e);

    private void RequestClassroomChatToggle(object? sender, RoutedEventArgs e) =>
        ClassroomChatToggleRequested?.Invoke(this, e);
}
