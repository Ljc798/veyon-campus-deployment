using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace VeyonCampus.App.Presentation.Views;

public partial class TeacherScreenWallView : UserControl
{
    private const double TileSpacing = 8;
    private TeacherViewModel? _observedModel;
    private ClassroomScreenWallViewModel? _observedWall;

    public TeacherScreenWallView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => UpdateMetrics(e.NewSize.Width);
        DataContextChanged += (_, _) => AttachWall();
        AttachWall();
    }

    public event Action<ClassroomScreenTileViewModel, bool>? DeviceSelected;
    public event Action<ClassroomScreenTileViewModel>? OpenDetailRequested;
    public event ClassroomDeviceActionRequestHandler? DeviceActionRequested;

    private void AttachWall()
    {
        var model = DataContext as TeacherViewModel;
        if (!ReferenceEquals(_observedModel, model))
        {
            if (_observedModel is not null) _observedModel.PropertyChanged -= ModelPropertyChanged;
            _observedModel = model;
            if (_observedModel is not null) _observedModel.PropertyChanged += ModelPropertyChanged;
        }
        if (_observedWall is not null) _observedWall.PropertyChanged -= WallPropertyChanged;
        _observedWall = model?.ClassroomScreenWall;
        if (_observedWall is not null) _observedWall.PropertyChanged += WallPropertyChanged;
        UpdateMetrics(Bounds.Width);
    }

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TeacherViewModel.ClassroomScreenWall)) AttachWall();
    }

    private void WallPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClassroomScreenWallViewModel.GridColumns) or
            nameof(ClassroomScreenWallViewModel.CompactMode) or nameof(ClassroomScreenWallViewModel.ThumbnailSize))
            UpdateMetrics(Bounds.Width);
    }

    private void UpdateMetrics(double width)
    {
        var wall = (DataContext as TeacherViewModel)?.ClassroomScreenWall;
        if (wall is null || width <= 0) return;
        var columns = Math.Clamp(wall.GridColumns, 2, 6);
        var slot = Math.Max(160, (width - TileSpacing * (columns - 1)) / columns);
        wall.SetTileMetrics(slot, slot * 9 / 16);
    }

    private void TilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ClassroomScreenTileViewModel tile } control ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        DeviceSelected?.Invoke(tile, extend);
    }

    private void TileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ClassroomScreenTileViewModel tile })
            OpenDetailRequested?.Invoke(tile);
    }

    private void OpenDetail(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClassroomScreenTileViewModel tile })
            OpenDetailRequested?.Invoke(tile);
    }

    private void DeviceContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget?.DataContext is not ClassroomScreenTileViewModel tile)
        {
            e.Cancel = true;
            return;
        }

        menu.DataContext = tile;
        foreach (var item in menu.Items.OfType<MenuItem>()) item.DataContext = tile;
    }

    private void RequestDeviceAction(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ClassroomScreenTileViewModel tile } item ||
            !Enum.TryParse<ClassroomDeviceAction>(item.Tag?.ToString(), out var action)) return;
        DeviceActionRequested?.Invoke(tile.TargetId, action);
    }
}
