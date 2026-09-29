using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public partial class StudentSetupWindow : Window
{
    private readonly MainViewModel _model = new();

    public StudentSetupWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
        DataContext = _model;
        Closing += (_, e) =>
        {
            if (!_model.CanExitSetup) e.Cancel = true;
        };
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.WizardPage))
            {
                DetailsDrawer.IsVisible = false;
            }
            UpdateWizardStepper();
        };
        UpdateWizardStepper();
        Opened += (_, _) => _ = _model.RefreshVeyonStatusAsync();
        foreach (var passwordInput in new[]
                 {
                     StudentInitialPasswordBox, StudentInitialPasswordConfirmationBox,
                     AdminPasswordBox, AdminPasswordConfirmationBox
                 })
        {
            passwordInput.IsUndoEnabled = false;
            passwordInput.CopyingToClipboard += (_, e) => e.Handled = true;
            passwordInput.CuttingToClipboard += (_, e) => e.Handled = true;
        }
        StudentInitialPasswordBox.TextChanged += (_, _) =>
            _model.SetStudentPasswordInput(StudentInitialPasswordBox.Text ?? "", StudentInitialPasswordConfirmationBox.Text ?? "");
        StudentInitialPasswordConfirmationBox.TextChanged += (_, _) =>
            _model.SetStudentPasswordInput(StudentInitialPasswordBox.Text ?? "", StudentInitialPasswordConfirmationBox.Text ?? "");
        AdminPasswordBox.TextChanged += (_, _) =>
            _model.SetAdminPasswordInput(AdminPasswordBox.Text ?? "", AdminPasswordConfirmationBox.Text ?? "");
        AdminPasswordConfirmationBox.TextChanged += (_, _) =>
            _model.SetAdminPasswordInput(AdminPasswordBox.Text ?? "", AdminPasswordConfirmationBox.Text ?? "");
        _model.ClearStudentPasswordRequested += (_, _) =>
        {
            StudentInitialPasswordBox.Text = "";
            StudentInitialPasswordConfirmationBox.Text = "";
        };
        _model.ClearAdminPasswordRequested += (_, _) =>
        {
            AdminPasswordBox.Text = "";
            AdminPasswordConfirmationBox.Text = "";
        };
        PackageDropZone.AddHandler(DragDrop.DragOverEvent, PackageDragOver);
        PackageDropZone.AddHandler(DragDrop.DragLeaveEvent, PackageDragLeave);
        PackageDropZone.AddHandler(DragDrop.DropEvent, PackageDrop);
    }

    private static string? GetSinglePath(DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles()?.Take(2).ToArray();
        if (items is not { Length: 1 }) return null;
        return items[0].TryGetLocalPath();
    }

    private void PackageDragOver(object? sender, DragEventArgs e)
    {
        bool accepted;
        try { accepted = PackageSource.IsCandidate(GetSinglePath(e)); }
        catch (Exception) { accepted = false; }
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        PackageDropZone.Classes.Set("dragover", accepted);
        e.Handled = true;
    }

    private void PackageDragLeave(object? sender, DragEventArgs e) => PackageDropZone.Classes.Set("dragover", false);

    private async void PackageDrop(object? sender, DragEventArgs e)
    {
        PackageDropZone.Classes.Set("dragover", false);
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        try
        {
            var path = GetSinglePath(e);
            if (path is null)
            {
                _model.RejectPackage("请每次只拖入一个本机校区配置包文件夹或清单文件；不能同时拖入多个文件。");
                return;
            }
            await ShowLocalPackageDialogAsync(path);
            if (_model.LoadedPackage is not null) e.DragEffects = DragDropEffects.Copy;
        }
        catch (Exception ex) { _model.RejectPackage($"无法读取拖入的校区配置包：{ex.Message}"); }
    }

    private void ResetForm(object? sender, RoutedEventArgs e) => _model.Reset();
    private void ClearPackage(object? sender, RoutedEventArgs e) => _model.ClearPackage();
    private async void OpenCloudPackageDialog(object? sender, RoutedEventArgs e)
    {
        var dialog = new StudentCloudPackageWindow(_model);
        await dialog.ShowDialog<bool>(this);
    }
    private void OpenMaintenance(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanOpenMaintenance) return;
        for (var i = 0; i < DetailsHost.Children.Count; i++)
            DetailsHost.Children[i].IsVisible = i == DetailsHost.Children.Count - 1;
        DetailsTitle.Text = "管理员维护";
        DetailsDrawer.IsVisible = true;
    }
    private async void NavigateWizardStep(object? sender, RoutedEventArgs e)
    {
        var previousPage = _model.WizardPage;
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var page))
            _model.NavigateWizardPage(page);
        if (previousPage != _model.WizardPage && _model.IsCheckPage && _model.CanPrepareDeployment)
            await _model.PrepareDeploymentAsync();
    }
    private void PreviousWizardPage(object? sender, RoutedEventArgs e) => _model.PreviousWizardPage();
    private async void NextWizardPage(object? sender, RoutedEventArgs e)
    {
        _model.NextWizardPage();
        if (_model.IsCheckPage && _model.CanPrepareDeployment)
            await _model.PrepareDeploymentAsync();
    }
    private void OpenDetails(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button || !int.TryParse(tag, out var index)) return;
        if (index == DetailsHost.Children.Count - 1 && !_model.CanOpenMaintenance) return;
        for (var i = 0; i < DetailsHost.Children.Count; i++)
            DetailsHost.Children[i].IsVisible = i == index;
        DetailsTitle.Text = button.Content?.ToString()?.TrimEnd(' ', '›');
        DetailsDrawer.IsVisible = true;
    }
    private void CloseDetails(object? sender, RoutedEventArgs e) => DetailsDrawer.IsVisible = false;
    private void CloseSetup(object? sender, RoutedEventArgs e)
    {
        if (_model.CanExitSetup) Close();
    }
    private async void PrepareDeployment(object? sender, RoutedEventArgs e) => await _model.PrepareDeploymentAsync();
    private async void RefreshVeyonStatus(object? sender, RoutedEventArgs e) => await _model.RefreshVeyonStatusAsync();
    private async void VerifyStudentDeployment(object? sender, RoutedEventArgs e) => await _model.VerifyStudentDeploymentAsync();
    private async void InstallWebsitePolicyAgent(object? sender, RoutedEventArgs e) => await _model.InstallWebsitePolicyAgentAsync();
    private async void RemoveWebsitePolicyAgent(object? sender, RoutedEventArgs e)
    {
        var confirmation = new StudentWebsiteAgentRemovalConfirmationWindow();
        if (await confirmation.ShowDialog<bool>(this) == true)
            await _model.RemoveWebsitePolicyAgentAsync();
    }
    private async void StartDeployment(object? sender, RoutedEventArgs e)
    {
        await _model.RunDeploymentAsync();
        if (_model.IsExecutionSuccessful) _model.NavigateWizardPage(4);
    }
    private async void InstallVeyon(object? sender, RoutedEventArgs e)
    {
        await _model.InstallVeyonOnlyAsync();
        if (_model.HasExecution) _model.NavigateWizardPage(4);
    }
    private void ShowOperationHelp(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string message }) _model.ToggleOperationHelp(message);
    }

    private async void LoadSharedPackage(object? sender, RoutedEventArgs e)
    {
        var path = NetworkPackagePathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            SharedPackageStatus.Text = "请输入教师电脑提供的 Windows 共享文件夹路径。";
            SharedPackageStatus.IsVisible = true;
            return;
        }
        try
        {
            SharedPackageStatus.Text = "正在从共享文件夹读取配置……";
            SharedPackageStatus.IsVisible = true;
            await _model.LoadPackageAsync(path, "教师共享");
            SharedPackageStatus.Text = _model.LoadedPackage is null
                ? "载入失败：" + _model.PackageError
                : $"已载入校区“{_model.LoadedPackage.Campus}”配置。请继续核对部署操作和电脑编号。";
        }
        catch (Exception exception)
        {
            SharedPackageStatus.Text = "载入共享配置失败：" + exception.Message;
            SharedPackageStatus.IsVisible = true;
        }
    }

    private void UpdateWizardStepper()
    {
        PageHeadingIcon.Data = Avalonia.Media.Geometry.Parse(_model.WizardPage switch
        {
            1 => "M3,6 H21 M3,12 H21 M3,18 H21 M8,3 V9 M16,9 V15 M9,15 V21",
            2 => "M3,4 H21 V17 H3 Z M8,21 H16 M12,17 V21",
            3 => "M12,2 L21,6 V12 C21,18 12,22 12,22 C12,22 3,18 3,12 V6 Z M8,12 L11,15 L17,9",
            4 => "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M7,12 L11,16 L17,8",
            _ => "M5,2 H14 L20,8 V22 H5 Z M14,2 V8 H20 M8,12 H16 M8,16 H16"
        });
        var buttons = new[] { WizardStepSource, WizardStepContent, WizardStepCheck, WizardStepDeploy, WizardStepComplete };
        var numbers = new[]
        {
            WizardStepSourceNumber, WizardStepContentNumber, WizardStepCheckNumber,
            WizardStepDeployNumber, WizardStepCompleteNumber
        };
        for (var index = 0; index < buttons.Length; index++)
        {
            buttons[index].Classes.Set("current", _model.WizardPage == index);
            buttons[index].Classes.Set("complete", _model.WizardPage != index && _model.IsWizardStepComplete(index));
            buttons[index].Classes.Set("error", _model.IsWizardStepError(index));
            buttons[index].IsEnabled = _model.CanNavigateWizardPage(index);
            numbers[index].Text = _model.WizardPage != index && _model.IsWizardStepComplete(index)
                ? "✓"
                : _model.IsWizardStepError(index) ? "!" : (index + 1).ToString();
        }
    }

    private async void SelectPackage(object? sender, RoutedEventArgs e)
    {
        await ShowLocalPackageDialogAsync();
    }

    private void SelectOperation(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string tag } || !int.TryParse(tag, out var selected)) return;
        for (var i = 0; i < OperationEditors.Children.Count; i++)
            OperationEditors.Children[i].IsVisible = i == selected;
        foreach (var picker in this.GetLogicalDescendants().OfType<RadioButton>())
            if (picker.GroupName == "OperationEditor") picker.IsChecked = picker.Tag?.ToString() == tag;
    }

    private async Task ShowLocalPackageDialogAsync(string? initialPath = null)
    {
        var preview = new MainViewModel();
        string? selectedPath = null;
        var dialog = new Window
        {
            Title = "导入本地配置", Width = 640, Height = 520, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.White
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("72,48,*,64"), Margin = new Avalonia.Thickness(28) };
        layout.Children.Add(new TextBlock { Text = "导入本地配置", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var choose = new Button { Content = "选择配置文件夹", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        Grid.SetRow(choose, 1); layout.Children.Add(choose);
        var status = new TextBlock { Text = "选择教师提供的配置文件夹，校验通过后再使用。", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var scroll = new ScrollViewer { Content = status, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Avalonia.Thickness(0,16) };
        Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var cancel = new Button { Content = "取消" };
        var use = new Button { Content = "使用此配置", IsEnabled = false }; use.Classes.Add("primary");
        actions.Children.Add(cancel); actions.Children.Add(use); Grid.SetRow(actions, 3); layout.Children.Add(actions);
        dialog.Content = layout;
        cancel.Click += (_, _) => dialog.Close(false);
        use.Click += (_, _) => dialog.Close(true);
        async Task PreviewPath(string path)
        {
            selectedPath = path; use.IsEnabled = false; choose.IsEnabled = false;
            status.Text = "正在校验配置…";
            try
            {
                await preview.LoadPackageAsync(path, "本机导入");
                status.Text = preview.LoadedPackage is null ? "配置包无效\n" + preview.PackageError : "✓ 配置校验通过\n\n" + preview.LoadedPackageSummary + "\n\n" + preview.PackageStatus;
                use.IsEnabled = preview.LoadedPackage is not null;
            }
            catch (Exception ex) { status.Text = "配置包无效\n" + ex.Message; }
            finally { choose.IsEnabled = true; }
        }
        choose.Click += async (_, _) =>
        {
            try
            {
                var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择校区配置文件夹", AllowMultiple = false });
                if (folders.Count == 0) return;
                using var folder = folders[0];
                if (folder.TryGetLocalPath() is { } path) await PreviewPath(path);
            }
            catch (Exception ex) { status.Text = "无法打开配置文件夹\n" + ex.Message; }
        };
        if (initialPath is not null) dialog.Opened += async (_, _) => await PreviewPath(initialPath);
        if (await dialog.ShowDialog<bool>(this) && selectedPath is not null)
            await _model.LoadPackageAsync(selectedPath, "本机导入");
    }

}
