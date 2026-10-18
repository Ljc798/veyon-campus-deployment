using System.Diagnostics;
using System.Security;
using Microsoft.Win32;
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
        ReadHostStatus();
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

    private void RefreshHostStatus(object? sender, RoutedEventArgs e) => ReadHostStatus();

    private void ReadHostStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            WindowsEditionStatus.Text = "请在 Windows 教师机上查看主机版本。";
            RemoteDesktopStatus.Text = "此检查只读取 Windows 本机设置。";
            return;
        }

        try
        {
            using var versionKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            var productName = versionKey?.GetValue("ProductName") as string;
            var editionId = versionKey?.GetValue("EditionID") as string;
            WindowsEditionStatus.Text = DescribeWindowsEdition(productName, editionId);

            using var terminalServerKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Terminal Server", writable: false);
            var denyRemoteConnections = terminalServerKey?.GetValue("fDenyTSConnections");
            RemoteDesktopStatus.Text = denyRemoteConnections switch
            {
                int value when value == 0 => "本机配置项显示远程桌面已启用；账户权限、组织策略和网络连通性仍需确认。",
                int value when value == 1 => "本机配置项显示远程桌面未启用；管理员可通过上方按钮打开系统设置。",
                _ => "无法读取远程桌面开关；请通过 Windows 系统设置核对。"
            };
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or
                                           IOException or ArgumentException)
        {
            WindowsEditionStatus.Text = "无法读取 Windows 主机状态。请通过系统设置核对版本和远程桌面开关。";
            RemoteDesktopStatus.Text = "远程桌面开关读取失败；请通过 Windows 系统设置核对。";
        }
    }

    private static string DescribeWindowsEdition(string? productName, string? editionId)
    {
        if (string.IsNullOrWhiteSpace(productName))
            return "无法确认 Windows 版本；请在“设置 → 系统 → 关于”中核对。";

        var isHome = productName.Contains("Home", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(editionId, "Core", StringComparison.OrdinalIgnoreCase);
        if (isHome)
            return $"当前版本：{productName}。Windows Home 不能按标准远程桌面方式作为连接主机。";

        var supportsRemoteDesktopHost = ContainsAny(productName, "Pro", "Professional", "Enterprise", "Education") ||
                                        ContainsAny(editionId, "Professional", "Enterprise", "Education");
        return supportsRemoteDesktopHost
            ? $"当前版本：{productName}。版本支持远程桌面主机；仍需确认远程桌面开关、账户和网络。"
            : $"当前版本：{productName}。请在“设置 → 系统 → 关于”确认此版本支持远程桌面主机；应用不会猜测未知版本。";
    }

    private static bool ContainsAny(string? value, params string[] candidates) =>
        value is not null && candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
}
