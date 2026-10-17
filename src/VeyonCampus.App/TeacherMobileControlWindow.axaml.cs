using System.Net.Sockets;
using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using QRCoder;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal partial class TeacherMobileControlWindow : Window
{
    private readonly TeacherMobileControlManager _manager;
    private readonly TeacherViewModel _teacherModel;
    private Bitmap? _pairingQrBitmap;

    public TeacherMobileControlWindow(TeacherMobileControlManager manager, TeacherViewModel teacherModel)
    {
        _manager = manager;
        _teacherModel = teacherModel;
        InitializeComponent();
        DataContext = manager;
        _manager.PropertyChanged += ManagerPropertyChanged;
        Opened += async (_, _) =>
        {
            FitWindowToWorkingArea();
            RefreshPairingQr();
            _manager.RefreshDevices();
            _manager.RefreshPendingPairings();
            _manager.RefreshProfiles();
            _manager.RefreshAudit();
            await RefreshProfilePreparationSummaryAsync();
        };
        Closed += (_, _) =>
        {
            _manager.PropertyChanged -= ManagerPropertyChanged;
            SetPairingQrBitmap(null);
        };
    }

    private void ManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TeacherMobileControlManager.PairingQrUrl)) RefreshPairingQr();
    }

    private async Task RefreshProfilePreparationSummaryAsync()
    {
        try
        {
            ProfileAccountSummary.Text = _teacherModel.MobilePolicyProfileAccountSummary;
            var selectedAccounts = _teacherModel.MobilePolicyProfileSelectedAccountNames;
            ProfileSelectedAccountNames.ItemsSource = selectedAccounts;
            ProfileSelectedAccountsExpander.IsVisible = selectedAccounts.Count > 0;
            var preview = await _teacherModel.GetMobilePolicyProfileTargetSummaryAsync();
            ProfileTargetSummary.Text = preview.Summary;
            ProfileTargetNames.ItemsSource = preview.ComputerNames;
            ProfileTargetNamesExpander.IsVisible = preview.ComputerNames.Count > 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException or
                                          UnauthorizedAccessException or PlatformNotSupportedException)
        {
            ProfileTargetSummary.Text = "暂时无法读取电脑名称；请先回到教师端检查电脑名单。";
            ProfileTargetNamesExpander.IsVisible = false;
            ProfileAccountSummary.Text = "无法读取账户选择；请先回到教师端检查账户。";
            ProfileSelectedAccountsExpander.IsVisible = false;
        }
        finally
        {
            SaveWebsiteApplicationProfileButton.IsEnabled = true;
            SaveApplicationProfileButton.IsEnabled = true;
            SaveSystemProfileButton.IsEnabled = true;
        }
    }

    private void RefreshPairingQr()
    {
        var payload = _manager.PairingQrUrl;
        if (string.IsNullOrEmpty(payload))
        {
            SetPairingQrBitmap(null);
            return;
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        using var stream = new MemoryStream(png.GetGraphic(8));
        SetPairingQrBitmap(new Bitmap(stream));
    }

    private void SetPairingQrBitmap(Bitmap? bitmap)
    {
        var previous = _pairingQrBitmap;
        _pairingQrBitmap = bitmap;
        PairingQrImage.Source = bitmap;
        if (!ReferenceEquals(previous, bitmap)) previous?.Dispose();
    }

    private async void StartService(object? sender, RoutedEventArgs e)
    {
        try { await _manager.StartAsync(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException or SocketException)
        { await ShowNoticeAsync("手机控制服务未启动", exception.Message); }
    }

    private async void StopService(object? sender, RoutedEventArgs e)
    {
        try { await _manager.StopAsync(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                          System.Net.Sockets.SocketException)
        { await ShowNoticeAsync("手机控制服务已停止", exception.Message); }
    }

    private void CreatePairingCode(object? sender, RoutedEventArgs e)
    {
        try { _manager.CreatePairingCode(); }
        catch (InvalidOperationException exception) { _ = ShowNoticeAsync("无法生成连接码", exception.Message); }
    }

    private void RefreshLists(object? sender, RoutedEventArgs e)
    {
        _manager.RefreshDevices();
        _manager.RefreshPendingPairings();
        _manager.RefreshProfiles();
        _manager.RefreshAudit(refreshVeyonDirectory: true);
    }

    private async void ApprovePairing(object? sender, RoutedEventArgs e)
    {
        if (_manager.SelectedPairing is not { } pairing) return;
        if (!await ConfirmAsync("允许手机连接",
                $"允许“{pairing.DeviceName}”连接教师电脑？只允许你当前持有的手机。")) return;
        try { _manager.ApproveSelectedPairing(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or CryptographicException)
        { await ShowNoticeAsync("手机连接未完成", exception.Message); }
    }

    private async void RejectPairing(object? sender, RoutedEventArgs e)
    {
        if (_manager.SelectedPairing is not { } pairing) return;
        if (!await ConfirmAsync("拒绝手机连接", $"不允许“{pairing.DeviceName}”连接教师电脑？", destructive: true)) return;
        _manager.RejectSelectedPairing();
    }

    private async void RevokeDevice(object? sender, RoutedEventArgs e)
    {
        if (_manager.SelectedDevice is not { } device) return;
        if (!await ConfirmAsync("解除手机连接", $"解除“{device.DisplayName}”的连接后，这部手机将不能再控制教师电脑。", destructive: true)) return;
        _manager.RevokeSelectedDevice();
    }

    private void SaveWebsiteProfile(object? sender, RoutedEventArgs e) =>
        SaveProfile(MobilePolicyProfileKind.Website,
            () => _teacherModel.SaveWebsiteMobileProfile(ProfileNameInput.Text ?? ""));

    private void SaveWebsiteApplicationProfile(object? sender, RoutedEventArgs e) =>
        SaveProfile(MobilePolicyProfileKind.WebsiteApplication,
            () => _teacherModel.SaveWebsiteApplicationMobileProfile(ProfileNameInput.Text ?? ""));

    private void SaveApplicationProfile(object? sender, RoutedEventArgs e) =>
        SaveProfile(MobilePolicyProfileKind.Application,
            () => _teacherModel.SaveApplicationMobileProfile(ProfileNameInput.Text ?? ""));

    private void SaveSystemProfile(object? sender, RoutedEventArgs e) =>
        SaveProfile(MobilePolicyProfileKind.System,
            () => _teacherModel.SaveStudentSystemMobileProfile(ProfileNameInput.Text ?? ""));

    private async void SaveProfile(MobilePolicyProfileKind kind, Func<MobilePolicyProfile> save)
    {
        try
        {
            var name = (ProfileNameInput.Text ?? "").Trim();
            var campusId = _teacherModel.CampusId.Trim();
            var existing = _teacherModel.ReadMobilePolicyProfiles().FirstOrDefault(profile =>
                profile.Kind == kind && profile.CampusId == campusId &&
                profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var hasStudentAccounts = kind is MobilePolicyProfileKind.Application or
                    MobilePolicyProfileKind.WebsiteApplication or MobilePolicyProfileKind.System;
                var replacement = kind == MobilePolicyProfileKind.Website
                    ? "网站规则"
                    : hasStudentAccounts ? "策略内容和逐电脑账户映射" : "策略内容";
                var scope = hasStudentAccounts
                    ? string.Join(Environment.NewLine, new[]
                    {
                        ProfileTargetSummary.Text,
                        ProfileAccountSummary.Text
                    }.Where(value => !string.IsNullOrWhiteSpace(value)))
                    : "";
                var scopeDetails = scope.Length == 0 ? "" : $"\n\n{scope}";
                var message = $"“{name}”已存在。保存会替换该预设的{replacement}。{scopeDetails}\n\n已经应用到学生电脑的策略不会因此改变。确认替换吗？";
                if (!await ConfirmAsync("替换已有手机策略预设", message, destructive: true)) return;
            }

            var profile = save();
            _manager.RefreshProfiles();
            _manager.SelectedProfile = _manager.Profiles.FirstOrDefault(item => item.Id == profile.Id);
            ProfileNameInput.Text = profile.Name;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          CryptographicException or PlatformNotSupportedException)
        { _ = ShowNoticeAsync("手机策略预设未保存", exception.Message); }
    }

    private async void DeleteProfile(object? sender, RoutedEventArgs e)
    {
        if (_manager.SelectedProfile is not { } profile) return;
        if (!await ConfirmAsync("删除手机策略预设", $"删除预设“{profile.Name}”？已发送到学生电脑的策略不会因此改变。", destructive: true)) return;
        _manager.DeleteSelectedProfile();
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();

    private async Task<bool> ConfirmAsync(string title, string message, bool destructive = false)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "取消", IsCancel = true, MinWidth = 84, Classes = { "secondary" } },
                            new Button { Content = "确认", IsDefault = true, MinWidth = 84,
                                Classes = { destructive ? "danger" : "primary" } }
                        }
                    }
                }
            }
        };
        FitDialogToOwnerWorkingArea(dialog, preferredWidth: 460d);
        var actions = (StackPanel)((StackPanel)dialog.Content!).Children[1]!;
        ((Button)actions.Children[0]!).Click += (_, _) => dialog.Close(false);
        ((Button)actions.Children[1]!).Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowNoticeAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button { Content = "关闭", IsDefault = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                }
            }
        };
        FitDialogToOwnerWorkingArea(dialog, preferredWidth: 500d);
        ((Button)((StackPanel)dialog.Content!).Children[1]!).Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

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
        MinWidth = Math.Min(420d, availableWidth);
        MinHeight = Math.Min(360d, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);

        var pixelWidth = (int)Math.Round(Width * scaling);
        var pixelHeight = (int)Math.Round(Height * scaling);
        Position = new PixelPoint(
            workArea.X + Math.Max(0, (workArea.Width - pixelWidth) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - pixelHeight) / 2));
    }

    private void FitDialogToOwnerWorkingArea(Window dialog, double preferredWidth)
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen is null) return;

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        var availableWidth = Math.Max(1d, screen.WorkingArea.Width / scaling - 24d);
        var availableHeight = Math.Max(1d, screen.WorkingArea.Height / scaling - 24d);
        dialog.MaxWidth = availableWidth;
        dialog.MaxHeight = availableHeight;
        dialog.Width = Math.Min(preferredWidth, availableWidth);
    }
}
