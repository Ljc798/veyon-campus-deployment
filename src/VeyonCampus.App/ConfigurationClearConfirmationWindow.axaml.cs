using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VeyonCampus.App;

public partial class ConfigurationClearConfirmationWindow : Window
{
    public ConfigurationClearConfirmationWindow()
    {
        InitializeComponent();
    }

    public ConfigurationClearConfirmationWindow(string role)
        : this("清空本机全部配置", BuildDetails(role), confirmation: true)
    {
    }

    public ConfigurationClearConfirmationWindow(string title, string details, bool confirmation)
        : this()
    {
        Title = title;
        Heading.Text = title;
        Details.Text = details;
        CancelButton.IsVisible = confirmation;
        ConfirmButton.Content = confirmation ? "清空全部配置" : "关闭";
    }

    private void Confirm(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);

    private static string BuildDetails(string role)
    {
        var roleName = role switch
        {
            "TeacherConsole" => "教师控制台",
            "StudentSetup" => "学生部署工具",
            _ => "Veyon Campus"
        };
        return $"将重置这台电脑上的 Veyon Campus 配置：{roleName} 保存的机房、名单、策略、密钥、证书和缓存；同时清空 Veyon 的系统配置、认证密钥及内置地点和电脑名单。" +
               "已部署的学生网站、应用和系统限制会先恢复，后台 Agent 与浏览器集成会移除。Veyon 和 Veyon Campus 程序仍保留；完成后需要重新打开并配置。";
    }
}
