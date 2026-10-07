using System.Net.Sockets;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal partial class TeacherMobileControlWindow : Window
{
    private readonly TeacherMobileControlManager _manager;
    private readonly TeacherViewModel _teacherModel;

    public TeacherMobileControlWindow(TeacherMobileControlManager manager, TeacherViewModel teacherModel)
    {
        _manager = manager;
        _teacherModel = teacherModel;
        InitializeComponent();
        DataContext = manager;
        Opened += (_, _) =>
        {
            _manager.RefreshDevices();
            _manager.RefreshPendingPairings();
            _manager.RefreshProfiles();
            _manager.RefreshAudit();
        };
    }

    private async void StartService(object? sender, RoutedEventArgs e)
    {
        try { await _manager.StartAsync(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or SocketException)
        { await ShowNoticeAsync("手机控制服务未启动", exception.Message); }
    }

    private async void StopService(object? sender, RoutedEventArgs e)
    {
        try { await _manager.StopAsync(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                          System.Net.Sockets.SocketException)
        { await ShowNoticeAsync("手机控制服务已停止", exception.Message); }
    }

    private void CreatePairingCode(object? sender, RoutedEventArgs e)
    {
        try { _manager.CreatePairingCode(); }
        catch (InvalidOperationException exception) { _ = ShowNoticeAsync("无法生成配对码", exception.Message); }
    }

    private void RefreshLists(object? sender, RoutedEventArgs e)
    {
        _manager.RefreshDevices();
        _manager.RefreshPendingPairings();
        _manager.RefreshProfiles();
        _manager.RefreshAudit();
    }

    private async void ApprovePairing(object? sender, RoutedEventArgs e)
    {
        if (_manager.SelectedPairing is not { } pairing) return;
        if (!await ConfirmAsync("批准手机配对",
                $"允许“{pairing.DeviceName}”从 {pairing.SourceAddress} 访问此教师控制台？只批准你当前持有的手机。")) return;
        try { _manager.ApproveSelectedPairing(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or CryptographicException)
        { await ShowNoticeAsync("手机配对未完成", exception.Message); }
    }

    private async void RejectPairing(object? sender, RoutedEventArgs e)
    {
        if (_manager.SelectedPairing is not { } pairing) return;
        if (!await ConfirmAsync("拒绝手机配对", $"拒绝来自 {pairing.SourceAddress} 的“{pairing.DeviceName}”请求？")) return;
        _manager.RejectSelectedPairing();
    }

    private async void RevokeDevice(object? sender, RoutedEventArgs e)
    {
        if (_manager.SelectedDevice is not { } device) return;
        if (!await ConfirmAsync("撤销手机配对", $"将立即撤销“{device.DisplayName}”的手机访问。")) return;
        _manager.RevokeSelectedDevice();
    }

    private void SaveWebsiteProfile(object? sender, RoutedEventArgs e) =>
        SaveProfile(() => _teacherModel.SaveWebsiteMobileProfile(ProfileNameInput.Text ?? ""));

    private void SaveApplicationProfile(object? sender, RoutedEventArgs e) =>
        SaveProfile(() => _teacherModel.SaveApplicationMobileProfile(ProfileNameInput.Text ?? ""));

    private void SaveSystemProfile(object? sender, RoutedEventArgs e) =>
        SaveProfile(() => _teacherModel.SaveStudentSystemMobileProfile(ProfileNameInput.Text ?? ""));

    private void SaveProfile(Func<MobilePolicyProfile> save)
    {
        try
        {
            var profile = save();
            _manager.RefreshProfiles();
            _manager.SelectedProfile = _manager.Profiles.FirstOrDefault(item => item.Id == profile.Id);
            ProfileNameInput.Text = profile.Name;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        { _ = ShowNoticeAsync("手机策略预设未保存", exception.Message); }
    }

    private async void DeleteProfile(object? sender, RoutedEventArgs e)
    {
        if (_manager.SelectedProfile is not { } profile) return;
        if (!await ConfirmAsync("删除手机策略预设", $"删除预设“{profile.Name}”？已发送到学生电脑的策略不会因此改变。")) return;
        _manager.DeleteSelectedProfile();
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "取消", IsCancel = true, MinWidth = 84 },
                            new Button { Content = "确认", IsDefault = true, MinWidth = 84 }
                        }
                    }
                }
            }
        };
        var actions = (StackPanel)((StackPanel)dialog.Content!).Children[1]!;
        ((Button)actions.Children[0]!).Click += (_, _) => dialog.Close(false);
        ((Button)actions.Children[1]!).Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowNoticeAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button { Content = "关闭", IsDefault = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                }
            }
        };
        ((Button)((StackPanel)dialog.Content!).Children[1]!).Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }
}
