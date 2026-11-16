using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using VeyonCampus.Core;

namespace VeyonCampus.App;

/// <summary>One row in the classroom sweep: what answered on this address and what it is running.</summary>
public sealed class LanMachineRow
{
    public LanMachineRow(DeploymentPackageLanProbe probe)
    {
        Name = probe.Target;
        Address = probe.Address ?? "—";
        Installed = probe.HasAgent;
        Status = probe.HasProvider
            ? "正在分发"
            : probe.HasAgent
                ? "已装 Veyon Campus"
                : "未响应";
        Detail = probe.HasProvider
            ? $"提供“{probe.CampusName}”配置包"
            : probe.HasAgent
                ? $"Agent {probe.AgentVersion ?? "版本未知"}"
                : probe.Detail ?? "未安装或未开机";
    }

    public string Name { get; }
    public string Address { get; }
    public bool Installed { get; }
    public string Status { get; }
    public string Detail { get; }
}

/// <summary>
/// Classroom-local package sharing. A teacher picks the folder the app just generated, presses one
/// button, and the machines in the room can take the package without the internet, a repository or a
/// teacher account. The sweep exists so the teacher can see what is actually installed before class.
/// </summary>
/// <remarks>
/// This lives beside <c>TeacherViewModel</c> rather than inside it because it owns a listening socket
/// with a lifetime: the share has to stop when the panel closes or the app exits, and keeping that
/// state in a small object makes the ownership obvious.
/// </remarks>
public sealed class LanPackageSharingViewModel : INotifyPropertyChanged
{
    private DeploymentPackageLanServer? _share;
    private bool _isSharing;
    private string _status = "";
    private string _shareAddress = "";
    private string _scanStatus = "";
    private bool _isScanning;
    private int _downloadCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LanMachineRow> Machines { get; } = [];

    public bool IsSharing
    {
        get => _isSharing;
        private set { _isSharing = value; Raise(); Raise(nameof(CanStop)); }
    }

    public string Status
    {
        get => _status;
        private set { _status = value; Raise(); Raise(nameof(HasStatus)); }
    }

    public bool HasStatus => Status.Length > 0;

    public string ShareAddress
    {
        get => _shareAddress;
        private set { _shareAddress = value; Raise(); Raise(nameof(HasShareAddress)); }
    }

    public bool HasShareAddress => ShareAddress.Length > 0;

    public string ScanStatus
    {
        get => _scanStatus;
        private set { _scanStatus = value; Raise(); Raise(nameof(HasScanStatus)); }
    }

    public bool HasScanStatus => ScanStatus.Length > 0;

    public bool IsScanning
    {
        get => _isScanning;
        private set { _isScanning = value; Raise(); Raise(nameof(CanScan)); }
    }

    public bool CanScan => !IsScanning;

    public int DownloadCount
    {
        get => _downloadCount;
        private set { _downloadCount = value; Raise(); Raise(nameof(DownloadSummary)); }
    }

    public string DownloadSummary => DownloadCount == 0
        ? ""
        : $"已有 {DownloadCount} 台机器取走配置包。";

    public bool HasDownloads => DownloadCount > 0;

    public bool CanStop => IsSharing;

    /// <summary>
    /// Serves the chosen folder. The folder is validated exactly as a publish would validate it, so a
    /// share can never hand out something the installer will reject later.
    /// </summary>
    public async Task StartAsync(string packageDirectory)
    {
        if (IsSharing) return;
        var directory = (packageDirectory ?? "").Trim();
        if (directory.Length == 0 || !Directory.Exists(directory))
        {
            Status = "请先选择刚生成的学生配置包文件夹。";
            return;
        }
        Status = "正在校验配置包并启动局域网分发……";
        await StopAsync(keepStatus: true).ConfigureAwait(true);
        try
        {
            _share = DeploymentPackageLanServer.Start(directory, new DownloadProgress(this));
            DownloadCount = 0;
            IsSharing = true;
            ShareAddress = _share.CatalogUrls.FirstOrDefault() ?? "";
            Status = $"已在局域网分发“{_share.CampusName}”配置包（{_share.SizeBytes:N0} 字节）。" +
                     "保持本页面打开，教室里的学生端即可获取。";
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          SocketException or ArgumentException)
        {
            _share = null;
            IsSharing = false;
            ShareAddress = "";
            Status = exception is SocketException
                ? $"局域网分发启动失败：{DeploymentPackageLanDistribution.Port} 端口无法监听。" +
                  "请确认 Windows 防火墙已放行该端口，或换一个教室网络后重试。"
                : "局域网分发启动失败：" + exception.Message;
        }
    }

    public async Task StopAsync(bool keepStatus = false)
    {
        if (_share is { } share)
        {
            _share = null;
            try { await share.DisposeAsync().ConfigureAwait(true); }
            catch (Exception exception) when (exception is IOException or SocketException or
                                              ObjectDisposedException) { }
        }
        if (!IsSharing && keepStatus) return;
        IsSharing = false;
        ShareAddress = "";
        if (keepStatus) return;
        Status = DownloadCount > 0
            ? $"局域网分发已停止；本次共有 {DownloadCount} 台机器取走配置包。"
            : "局域网分发已停止。";
    }

    /// <summary>
    /// Sweeps the classroom subnet. The Veyon directory is not usable here: before the first
    /// deployment has run the teacher machine has no reliable address for the students, so the sweep
    /// walks addresses and asks each one what it is.
    /// </summary>
    public async Task ScanAsync()
    {
        if (IsScanning) return;
        var local = FindLocalClassroomAddress();
        if (local is null)
        {
            ScanStatus = "未找到教室私有网段；请确认本机已连接到教室网络。";
            return;
        }
        IsScanning = true;
        Machines.Clear();
        try
        {
            var targets = DeploymentPackageLanDiscovery.EnumerateClassroomTargets(local);
            ScanStatus = $"正在扫描 {targets.Count} 个地址（本机 {local}）……";
            var probes = await DeploymentPackageLanDiscovery.ProbeAsync(targets, 48).ConfigureAwait(true);
            var responsive = probes
                .Where(probe => probe.HasAgent || probe.HasProvider)
                .OrderBy(probe => probe.HasProvider ? 0 : 1)
                .ThenBy(probe => probe.Address, StringComparer.Ordinal)
                .ToArray();
            foreach (var probe in responsive) Machines.Add(new LanMachineRow(probe));
            var installed = probes.Count(probe => probe.HasAgent);
            ScanStatus = installed == 0
                ? "没有发现已装 Veyon Campus 的机器。若教室机器尚未部署，请先在学生端完成首次安装。"
                : $"发现 {installed} 台已装 Veyon Campus 的机器" +
                  (responsive.Any(probe => probe.HasProvider) ? "，其中一台正在分发配置包。" : "。");
        }
        catch (Exception exception) when (exception is SocketException or InvalidDataException or
                                          InvalidOperationException or TaskCanceledException)
        {
            ScanStatus = "扫描失败：" + exception.Message;
        }
        finally { IsScanning = false; }
    }

    private static IPAddress? FindLocalClassroomAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicastAddress => unicastAddress.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                       !IPAddress.IsLoopback(address) &&
                                       StudentApplicationUpdateCryptography.IsPrivateLanAddress(address));

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class DownloadProgress(LanPackageSharingViewModel owner)
        : IProgress<DeploymentPackageLanDownload>
    {
        public void Report(DeploymentPackageLanDownload value) => Dispatcher.UIThread.Post(() =>
            owner.DownloadCount += 1);
    }
}
