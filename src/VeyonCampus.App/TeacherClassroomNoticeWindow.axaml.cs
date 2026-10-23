using Avalonia.Controls;
using Avalonia.Interactivity;
using System;

namespace VeyonCampus.App;

public partial class TeacherClassroomNoticeWindow : Window
{
    private TeacherViewModel? _model;
    private Action? _sendNotice;

    public TeacherClassroomNoticeWindow()
    {
        InitializeComponent();
    }

    public TeacherClassroomNoticeWindow(TeacherViewModel model, Action sendNotice)
        : this()
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _sendNotice = sendNotice ?? throw new ArgumentNullException(nameof(sendNotice));
        DataContext = _model;
    }

    private void CancelNotice(object? sender, RoutedEventArgs e) => Close();

    private void SendNotice(object? sender, RoutedEventArgs e)
    {
        var model = _model;
        if (model is null || !model.CanSendClassroomNotice) return;
        _sendNotice?.Invoke();
        if (string.IsNullOrWhiteSpace(model.ClassroomNoticeDraft) &&
            model.ClassroomNoticeStatus.StartsWith("已发送", StringComparison.Ordinal))
            Close();
    }
}
