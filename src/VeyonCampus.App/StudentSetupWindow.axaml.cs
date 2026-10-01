using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public partial class StudentSetupWindow : Window
{
    private const int MaintenanceDetailsIndex = 9;
    private const int StateBackupDetailsIndex = 10;
    private static readonly string[] SpinnerFrames = ["◴", "◷", "◶", "◵"];
    private readonly MainViewModel _model = new();
    private readonly DispatcherTimer _executionSpinnerTimer;
    private int _spinnerFrame;

    public StudentSetupWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(
            $"avares://{typeof(App).Assembly.GetName().Name}/Assets/veyon-campus.ico")));
        DataContext = _model;
        _executionSpinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _executionSpinnerTimer.Tick += (_, _) =>
        {
            _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
            ExecutionSpinner.Text = SpinnerFrames[_spinnerFrame];
            PreflightSpinner.Text = SpinnerFrames[_spinnerFrame];
        };
        Closing += (_, e) =>
        {
            if (!_model.CanExitSetup) e.Cancel = true;
        };
        Closed += (_, _) => _executionSpinnerTimer.Stop();
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.WizardPage))
            {
                DetailsDrawer.IsVisible = false;
            }
            if (e.PropertyName is nameof(MainViewModel.IsExecuting) or nameof(MainViewModel.IsPreparingDeployment))
            {
                if (_model.IsExecuting || _model.IsPreparingDeployment) _executionSpinnerTimer.Start();
                else _executionSpinnerTimer.Stop();
            }
            UpdateWizardStepper();
        };
        UpdateWizardStepper();
        Opened += (_, _) =>
        {
            FitWindowToWorkingArea();
            _ = _model.RefreshVeyonStatusAsync();
        };
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
    private void OpenSavedExecutionHistory(object? sender, RoutedEventArgs e) => _model.OpenSavedExecutionHistory();
    private void AcknowledgePreviousRunReview(object? sender, RoutedEventArgs e) => _model.AcknowledgePreviousRunReview();
    private void RequestStopAfterCurrentStep(object? sender, RoutedEventArgs e) => _model.RequestStopAfterCurrentStep();
    private async void OpenCloudPackageDialog(object? sender, RoutedEventArgs e)
    {
        var dialog = new StudentCloudPackageWindow(_model);
        await dialog.ShowDialog<bool>(this);
    }
    private void OpenMaintenance(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanOpenMaintenance) return;
        for (var i = 0; i < DetailsHost.Children.Count; i++)
            DetailsHost.Children[i].IsVisible = i == MaintenanceDetailsIndex;
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
        if (index < 0 || index >= DetailsHost.Children.Count) return;
        if (index == MaintenanceDetailsIndex && !_model.CanOpenMaintenance) return;
        if (index == StateBackupDetailsIndex && !_model.HasRecoverableStateBackup) return;
        for (var i = 0; i < DetailsHost.Children.Count; i++)
            DetailsHost.Children[i].IsVisible = i == index;
        DetailsTitle.Text = button.Content?.ToString()?.TrimEnd(' ', '›');
        DetailsDrawer.IsVisible = true;
        if (index == StateBackupDetailsIndex) _ = _model.ReviewLatestStateBackupAsync();
    }
    private void CloseDetails(object? sender, RoutedEventArgs e) => DetailsDrawer.IsVisible = false;

    private void FitWindowToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen is null) return;

        var workArea = screen.WorkingArea;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        var availableWidth = Math.Max(1d, workArea.Width / scaling - 24d);
        var availableHeight = Math.Max(1d, workArea.Height / scaling - 24d);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        MinWidth = Math.Min(720d, availableWidth);
        MinHeight = Math.Min(520d, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);

        var pixelWidth = (int)Math.Round(Width * scaling);
        var pixelHeight = (int)Math.Round(Height * scaling);
        Position = new PixelPoint(
            workArea.X + Math.Max(0, (workArea.Width - pixelWidth) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - pixelHeight) / 2));
    }

    private void OpenFeedbackIssue(object? sender, RoutedEventArgs e) => FeedbackIssueLink.Open();

    private void OpenFeedbackContacts(object? sender, RoutedEventArgs e) => FeedbackContactsWindow.ShowFor(this);

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
        if (!_model.BeginDeploymentFromCheckPage()) return;
        if (_model.CanInstall) await _model.InstallVeyonOnlyAsync();
        else await _model.RunDeploymentAsync();
        if (_model.CanReviewExecutionResult) _model.NavigateWizardPage(4);
    }

    private async void CompareCurrentStateBackup(object? sender, RoutedEventArgs e) =>
        await _model.CompareCurrentStateBackupAsync();

    private async void CompareCurrentSystemBackup(object? sender, RoutedEventArgs e) =>
        await _model.CompareCurrentSystemBackupAsync();

    private async void ExportStateBackupConfig(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanExportStateBackup) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出执行前 Veyon 配置",
                SuggestedFileName = "veyon-config-before-recovery.json",
                DefaultExtension = ".json",
                FileTypeChoices =
                [
                    new FilePickerFileType("Veyon 配置 JSON")
                    {
                        Patterns = ["*.json"],
                        MimeTypes = ["application/json"]
                    }
                ]
            });
            var path = file?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
                await _model.ExportStateBackupConfigAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidOperationException or NotSupportedException)
        {
            _model.ReportStateBackupExportError("无法选择或写入本地导出位置；未覆盖现有文件，也没有修改 Veyon 配置。");
        }
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
