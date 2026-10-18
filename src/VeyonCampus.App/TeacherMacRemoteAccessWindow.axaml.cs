using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace VeyonCampus.App;

public partial class TeacherMacRemoteAccessWindow : Window
{
    public TeacherMacRemoteAccessWindow()
    {
        InitializeComponent();
        ComputerNameInput.Text = Environment.MachineName;
    }

    private async void CopyComputerName(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                ActionStatus.Text = "无法访问剪贴板，请手动记录电脑名称。";
                return;
            }

            await clipboard.SetTextAsync(Environment.MachineName);
            ActionStatus.Text = "电脑名称已复制，可粘贴到 Mac 上的 Windows App。";
        }
        catch (Exception)
        {
            ActionStatus.Text = "复制失败，请手动记录电脑名称。";
        }
    }

    private void OpenRemoteDesktopSettings(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            ActionStatus.Text = "请在 Windows 教师机上打开远程桌面设置。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:remotedesktop") { UseShellExecute = true });
            ActionStatus.Text = "已打开 Windows 远程桌面设置；请由管理员核对版本、启用状态和允许登录的账户。";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ActionStatus.Text = "无法打开系统设置。请在 Windows 中手动打开“设置 → 系统 → 远程桌面”。";
        }
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
}
