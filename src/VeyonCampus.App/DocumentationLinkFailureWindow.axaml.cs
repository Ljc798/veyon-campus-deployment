using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace VeyonCampus.App;

public partial class DocumentationLinkFailureWindow : Window
{
    public const string DocumentationUrl = "https://kidscode.fun/docs";

    public DocumentationLinkFailureWindow()
    {
        InitializeComponent();
        DocumentationAddress.Text = DocumentationUrl;
    }

    private async void CopyAddress(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                CopyStatus.Text = "无法访问剪贴板；可选中网址手动复制。";
                return;
            }

            await clipboard.SetTextAsync(DocumentationUrl);
            CopyStatus.Text = "网址已复制。";
        }
        catch (Exception)
        {
            CopyStatus.Text = "复制失败；可选中网址手动复制。";
        }
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
}
