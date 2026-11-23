using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VeyonCampus.App.Presentation.Views;

public partial class TeacherScreenWallView : UserControl
{
    private const double TileSpacing = 8;
    private const double TargetTileWidth = 210;

    public TeacherScreenWallView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    /// <summary>Raised when the teacher asks for the keyboard/mouse lease on one card.</summary>
    public event Action<ClassroomScreenTileViewModel>? ToggleInputLockRequested;

    /// <summary>Raised when the teacher asks for the website list (or its removal) on one card.</summary>
    public event Action<ClassroomScreenTileViewModel>? ToggleWebsitePolicyRequested;

    /// <summary>Raised when the teacher opens one card in its own window.</summary>
    public event Action<ClassroomScreenTileViewModel>? OpenDetailRequested;

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is not TeacherViewModel { ClassroomScreenWall: { } wall }) return;
        var width = e.NewSize.Width;
        if (width <= 0) return;
        var columns = Math.Clamp((int)Math.Floor((width + TileSpacing) / TargetTileWidth), 2, 6);
        var slot = Math.Max(160, (width - TileSpacing * (columns - 1)) / columns);
        // Header + badges + two action rows are fixed; only the 16:9 picture area scales.
        var height = Math.Round((slot - 16) * 9 / 16) + 124;
        wall.SetTileMetrics(slot, height);
    }

    private void TilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ClassroomScreenTileViewModel tile }) return;
        if (DataContext is not TeacherViewModel { ClassroomScreenWall: { } wall }) return;
        foreach (var item in wall.Tiles) item.IsSelected = ReferenceEquals(item, tile);
    }

    private void TileDoubleTapped(object? sender, TappedEventArgs e) => OpenDetailFor(sender);

    private void OpenDetail(object? sender, RoutedEventArgs e) => OpenDetailFor(sender);

    private void OpenDetailFor(object? sender)
    {
        if (sender is not Control { DataContext: ClassroomScreenTileViewModel tile }) return;
        OpenDetailRequested?.Invoke(tile);
    }

    private void ToggleInputLock(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClassroomScreenTileViewModel tile })
            ToggleInputLockRequested?.Invoke(tile);
    }

    private void ToggleWebsitePolicy(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClassroomScreenTileViewModel tile })
            ToggleWebsitePolicyRequested?.Invoke(tile);
    }
}
