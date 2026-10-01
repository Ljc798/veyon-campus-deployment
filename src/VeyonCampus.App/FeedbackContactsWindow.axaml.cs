using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace VeyonCampus.App;

public partial class FeedbackContactsWindow : Window
{
    public FeedbackContactsWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public string EmailAddress => FeedbackIssueLink.EmailAddress;
    public string WeChatId => FeedbackIssueLink.WeChatId;

    public static void ShowFor(Window owner) => _ = new FeedbackContactsWindow().ShowDialog(owner);

    private void OpenEmail(object? sender, RoutedEventArgs e) => FeedbackIssueLink.OpenEmail();

    private async void CopyWeChatId(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                CopyStatus.Text = "无法访问剪贴板。";
                return;
            }

            await clipboard.SetTextAsync(WeChatId);
            CopyStatus.Text = "微信号已复制。";
        }
        catch (Exception)
        {
            CopyStatus.Text = "复制失败，请手动复制微信号。";
        }
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
}
