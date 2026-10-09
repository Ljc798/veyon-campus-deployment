using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;

namespace VeyonCampus.App;

internal sealed class ClassroomApplicationReviewWindow : Window
{
    public ClassroomApplicationReviewWindow(IReadOnlyList<MobileApplicationReviewTarget> targets)
    {
        Title = "确认练习模式应用审核";
        Width = 520;
        Height = 620;
        MinWidth = 420;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        var root = new StackPanel { Spacing = 14, Margin = new Thickness(22) };
        root.Children.Add(new TextBlock
        {
            Text = "阅读应用审核结果",
            FontSize = 21,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#173044")
        });
        root.Children.Add(new TextBlock
        {
            Text = "只有在你确认下方统计后，练习模式才会启用应用阻止。取消会恢复本次课堂策略。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#627785")
        });

        var cards = new StackPanel { Spacing = 8 };
        foreach (var target in targets.Take(150))
        {
            var card = new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(10),
                BorderBrush = Brush("#d8e2e8"),
                BorderThickness = new Thickness(1),
                Background = Brush("#f7fafb")
            };
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(new TextBlock
            {
                Text = target.Target,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("#173044")
            });
            content.Children.Add(new TextBlock
            {
                Text = target.IsSimulation ? "本机登记程序影响模拟" : "最近应用审核记录",
                FontSize = 12,
                Foreground = Brush("#627785")
            });
            foreach (var rule in target.Rules.Take(40))
                content.Children.Add(new TextBlock
                {
                    Text = $"{rule.DisplayName} · 命中 {rule.WouldBlockCount} · 已阻止 {rule.BlockedCount}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = Brush("#244254")
                });
            if (target.Rules.Count == 0)
                content.Children.Add(new TextBlock
                {
                    Text = "没有匹配的审核事件。",
                    FontSize = 12,
                    Foreground = Brush("#627785")
                });
            if (!string.IsNullOrWhiteSpace(target.CoverageNote))
                content.Children.Add(new TextBlock
                {
                    Text = target.CoverageNote,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Foreground = Brush("#627785")
                });
            card.Child = content;
            cards.Children.Add(card);
        }
        root.Children.Add(new ScrollViewer
        {
            Content = cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消并恢复", MinWidth = 112 };
        cancel.Click += (_, _) => Close(false);
        var confirm = new Button
        {
            Content = "确认启用阻止",
            MinWidth = 144,
            Background = Brush("#087f78"),
            Foreground = Brushes.White
        };
        confirm.Click += (_, _) => Close(true);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        root.Children.Add(actions);
        Content = root;
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
