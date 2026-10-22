using Avalonia.Controls;
using Avalonia.Interactivity;
using System;

namespace VeyonCampus.App;

public partial class TeacherClassroomNoticeWindow : Window
{
    private readonly TeacherViewModel _model;
    private readonly Action _sendNotice;

    public TeacherClassroomNoticeWindow(TeacherViewModel model, Action sendNotice)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _sendNotice = sendNotice ?? throw new ArgumentNullException(nameof(sendNotice));
        InitializeComponent();
        DataContext = _model;
    }

    private void CancelNotice(object? sender, RoutedEventArgs e) => Close();

    private void SendNotice(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanSendClassroomNotice) return;
        _sendNotice();
        if (string.IsNullOrWhiteSpace(_model.ClassroomNoticeDraft) &&
            _model.ClassroomNoticeStatus.StartsWith("已发送", StringComparison.Ordinal))
            Close();
    }
}
