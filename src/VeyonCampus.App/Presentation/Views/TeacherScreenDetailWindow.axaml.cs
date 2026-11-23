using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VeyonCampus.App.Presentation.Views;

/// <summary>
/// A student's live screen in its own window. It reuses the wall's existing connection for that target
/// (the wall raises the cadence while this window is open) instead of authenticating a second session.
/// </summary>
public partial class TeacherScreenDetailWindow : Window
{
    private readonly ClassroomScreenTileViewModel? _tile;
    private readonly Action<Guid>? _toggleInputLock;
    private readonly Action<Guid>? _toggleWebsitePolicy;

    public TeacherScreenDetailWindow()
    {
        InitializeComponent();
    }

    internal TeacherScreenDetailWindow(ClassroomScreenTileViewModel tile, Action<Guid>? toggleInputLock,
        Action<Guid>? toggleWebsitePolicy) : this()
    {
        _tile = tile;
        _toggleInputLock = toggleInputLock;
        _toggleWebsitePolicy = toggleWebsitePolicy;
        DataContext = tile;
        Title = $"{tile.DisplayName} · 实时屏幕";
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();

    private void ToggleInputLock(object? sender, RoutedEventArgs e)
    {
        if (_tile is { } tile) _toggleInputLock?.Invoke(tile.TargetId);
    }

    private void ToggleWebsitePolicy(object? sender, RoutedEventArgs e)
    {
        if (_tile is { } tile) _toggleWebsitePolicy?.Invoke(tile.TargetId);
    }
}
