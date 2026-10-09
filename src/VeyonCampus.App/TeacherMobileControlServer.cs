using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using VeyonCampus.Core;

namespace VeyonCampus.App;

public sealed record MobilePolicyProfileSummary(Guid Id, string Name, string CampusId,
    MobilePolicyProfileKind Kind, string Mode, int LifetimeMinutes, DateTimeOffset? UpdatedUtc,
    StudentSystemPolicySettings? SystemSettings = null);
public sealed record MobileTargetStatus(string Target, bool Online, string State, string? AgentVersion,
    DateTimeOffset? CollectedUtc, WebsitePolicyMode? WebsiteMode, long? WebsiteRevision, DateTimeOffset? WebsiteExpiresUtc,
    ApplicationPolicyMode? ApplicationMode, long? ApplicationRevision, DateTimeOffset? ApplicationExpiresUtc,
    bool ApplicationSupported, bool NeedsReview, string Detail,
    StudentSystemPolicyReportedState? SystemPolicy = null);
public sealed record MobilePolicyTargetResult(string Target, bool AgentAccepted, bool NeedsReview, string Detail);
public sealed record MobileApplicationReviewRule(Guid RuleId, string DisplayName, int WouldBlockCount, int BlockedCount);
public sealed record MobileApplicationReviewTarget(string Target, IReadOnlyList<MobileApplicationReviewRule> Rules,
    bool IsSimulation = false, string? CoverageNote = null);
public sealed record MobilePolicyOperationResponse(string State, bool RequiresReview, string? ReviewToken,
    string Message, long? Revision, DateTimeOffset? ExpiresUtc,
    IReadOnlyList<MobilePolicyTargetResult> Results,
    IReadOnlyList<MobileApplicationReviewTarget>? ApplicationReview = null);
public sealed record MobilePendingPairingView(Guid Id, string DeviceName, string SourceAddress,
    DateTimeOffset RequestedUtc);
public sealed record MobileControlAuditView(DateTimeOffset TimeUtc, string Device, string Action,
    string Targets, string Outcome);

internal sealed record MobilePairRequest(string PairingCode, string DeviceName);
internal sealed record MobilePairPollRequest(string Ticket);
internal sealed record MobilePairPendingResponse(string Ticket, DateTimeOffset ExpiresUtc);
internal sealed record MobilePairPollResponse(string State, string? AccessToken = null,
    MobilePairedDeviceView? Device = null);
internal sealed record MobileStatusRequest(IReadOnlyList<string> Targets, Guid? ProfileId = null);
internal sealed record MobilePolicyRequest(Guid ProfileId, IReadOnlyList<string> Targets, bool Enabled,
    string? ReviewToken = null);
internal sealed record MobileSessionResponse(MobilePairedDeviceView Device, string Status);
internal sealed record MobileReviewGrant(Guid DeviceId, Guid ProfileId, string ProfileFingerprint,
    long AuditRevision, string AuditFingerprint, IReadOnlyList<string> Targets, DateTimeOffset ExpiresUtc);

internal static class MobileControlLanNetworkPolicy
{
    public static bool IsPrivateIpv4Address(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    public static bool AreOnSameIpv4Subnet(IPAddress localAddress, IPAddress remoteAddress, IPAddress subnetMask)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(remoteAddress);
        ArgumentNullException.ThrowIfNull(subnetMask);
        if (localAddress.IsIPv4MappedToIPv6) localAddress = localAddress.MapToIPv4();
        if (remoteAddress.IsIPv4MappedToIPv6) remoteAddress = remoteAddress.MapToIPv4();
        if (localAddress.AddressFamily != AddressFamily.InterNetwork ||
            remoteAddress.AddressFamily != AddressFamily.InterNetwork ||
            subnetMask.AddressFamily != AddressFamily.InterNetwork) return false;

        var localBytes = localAddress.GetAddressBytes();
        var remoteBytes = remoteAddress.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        return Enumerable.Range(0, 4).All(index => (localBytes[index] & maskBytes[index]) ==
                                                   (remoteBytes[index] & maskBytes[index]));
    }
}

internal static class MobilePairingQrLink
{
    public static string Create(string teacherUrl, string pairingCode)
    {
        if (!Uri.TryCreate(teacherUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != TeacherMobileControlManager.HttpsPort ||
            !IPAddress.TryParse(uri.Host, out var address) ||
            !MobileControlLanNetworkPolicy.IsPrivateIpv4Address(address) ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.UserInfo.Length != 0)
            throw new InvalidDataException("二维码地址必须是教师机当前的私有 IPv4 HTTPS 地址。");
        if (pairingCode.Length != 8 || pairingCode.Any(character => character is < '0' or > '9'))
            throw new InvalidDataException("二维码配对码必须是 8 位数字。");

        var builder = new UriBuilder(uri) { Fragment = "pair=" + pairingCode };
        return builder.Uri.AbsoluteUri;
    }
}

/// <summary>Runs the teacher-only local HTTPS gateway and owns one-time mobile pairing state.</summary>
public sealed class TeacherMobileControlManager : INotifyPropertyChanged, IAsyncDisposable
{
    public const int HttpsPort = 39176;
    public const int BootstrapPort = 39177;
    private readonly Func<string> _activeCampusId;
    private readonly Action<Action> _dispatchToUi;
    private TeacherMobileControlService? _service;
    private bool _isRunning;
    private string _status = "手机控制服务未启动。";
    private string _lanUrls = "";
    private string _bootstrapUrls = "";
    private string _certificateFingerprint = "";
    private IReadOnlyList<string> _pairingUrls = Array.Empty<string>();
    private string? _selectedPairingUrl;
    private string _pairingQrUrl = "";
    private string _pairingCode = "";
    private string _pairingExpiry = "";
    private IReadOnlyList<MobilePairedDeviceView> _devices = Array.Empty<MobilePairedDeviceView>();
    private MobilePairedDeviceView? _selectedDevice;
    private IReadOnlyList<MobilePendingPairingView> _pendingPairings = Array.Empty<MobilePendingPairingView>();
    private MobilePendingPairingView? _selectedPairing;
    private IReadOnlyList<MobilePolicyProfile> _profiles = Array.Empty<MobilePolicyProfile>();
    private MobilePolicyProfile? _selectedProfile;
    private IReadOnlyList<MobileControlAuditView> _auditEntries = Array.Empty<MobileControlAuditView>();
    private string _auditStatus = "尚无手机控制记录。";

    public TeacherMobileControlManager(Func<string> activeCampusId, Action<Action> dispatchToUi)
    {
        _activeCampusId = activeCampusId ?? throw new ArgumentNullException(nameof(activeCampusId));
        _dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public bool CanStart => !IsRunning;
    public bool CanStop => IsRunning;
    public bool CanRevokeSelectedDevice => SelectedDevice is not null;
    public bool CanApproveSelectedPairing => SelectedPairing is not null;
    public bool CanRejectSelectedPairing => SelectedPairing is not null;
    public bool CanDeleteSelectedProfile => SelectedProfile is not null;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string LanUrls { get => _lanUrls; private set => Set(ref _lanUrls, value); }
    public string BootstrapUrls { get => _bootstrapUrls; private set => Set(ref _bootstrapUrls, value); }
    public string CertificateFingerprint { get => _certificateFingerprint; private set => Set(ref _certificateFingerprint, value); }
    public IReadOnlyList<string> PairingUrls { get => _pairingUrls; private set => Set(ref _pairingUrls, value); }
    public string? SelectedPairingUrl
    {
        get => _selectedPairingUrl;
        set
        {
            if (!Set(ref _selectedPairingUrl, value)) return;
            RefreshPairingQrUrl();
        }
    }
    public string PairingQrUrl { get => _pairingQrUrl; private set => Set(ref _pairingQrUrl, value); }
    public bool HasPairingQr => PairingQrUrl.Length > 0;
    public string PairingCode
    {
        get => _pairingCode;
        private set
        {
            if (!Set(ref _pairingCode, value)) return;
            RefreshPairingQrUrl();
        }
    }
    public string PairingExpiry { get => _pairingExpiry; private set => Set(ref _pairingExpiry, value); }
    public IReadOnlyList<MobilePairedDeviceView> Devices { get => _devices; private set => Set(ref _devices, value); }
    public IReadOnlyList<MobilePendingPairingView> PendingPairings
    {
        get => _pendingPairings;
        private set => Set(ref _pendingPairings, value);
    }
    public MobilePendingPairingView? SelectedPairing
    {
        get => _selectedPairing;
        set
        {
            if (!Set(ref _selectedPairing, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanApproveSelectedPairing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRejectSelectedPairing)));
        }
    }
    public MobilePairedDeviceView? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRevokeSelectedDevice)));
        }
    }
    public IReadOnlyList<MobilePolicyProfile> Profiles { get => _profiles; private set => Set(ref _profiles, value); }
    public IReadOnlyList<MobileControlAuditView> AuditEntries { get => _auditEntries; private set => Set(ref _auditEntries, value); }
    public string AuditStatus { get => _auditStatus; private set => Set(ref _auditStatus, value); }
    public MobilePolicyProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!Set(ref _selectedProfile, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDeleteSelectedProfile)));
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("手机控制只支持 Windows 教师端。");
        Status = "正在启动校园网手机控制服务……";
        TeacherMobileControlService? service = null;
        try
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(_activeCampusId().Trim());
            var addresses = GetPrivateAddresses();
            var identity = MobileControlTlsIdentityStore.Create(addresses);
            service = new TeacherMobileControlService(identity, OnPairingChanged, CurrentCampusId);
            await service.StartAsync(cancellationToken);
            _service = service;
            var pairingUrls = identity.Addresses.Select(address => $"https://{address}:{HttpsPort}/").ToArray();
            LanUrls = string.Join(Environment.NewLine, pairingUrls);
            PairingUrls = Array.AsReadOnly(pairingUrls);
            SelectedPairingUrl = pairingUrls.FirstOrDefault();
            BootstrapUrls = string.Join(Environment.NewLine, identity.Addresses.Select(address =>
                $"http://{address}:{BootstrapPort}/teacher-mobile-root.cer"));
            CertificateFingerprint = string.Join(" ", Enumerable.Range(0, identity.RootFingerprint.Length / 2)
                .Select(index => identity.RootFingerprint.Substring(index * 2, 2)));
            IsRunning = true;
            Status = "已启动。先在手机安装并信任教师根证书，再打开 HTTPS 地址并使用一次性配对码。控制服务仅在校园局域网监听。";
            RefreshDevices();
            RefreshPendingPairings();
            RefreshProfiles();
            RefreshAudit();
        }
        catch
        {
            if (service is not null) await service.DisposeAsync();
            Status = "手机控制服务启动失败；请检查端口、证书和校园网适配器。";
            throw;
        }
    }

    public async Task StopAsync()
    {
        var service = _service;
        _service = null;
        try
        {
            if (service is not null) await service.DisposeAsync();
        }
        finally
        {
            IsRunning = false;
            PairingCode = "";
            PairingExpiry = "";
            PairingUrls = Array.Empty<string>();
            SelectedPairingUrl = null;
            PendingPairings = Array.Empty<MobilePendingPairingView>();
            SelectedPairing = null;
            LanUrls = "";
            BootstrapUrls = "";
            CertificateFingerprint = "";
            Status = "手机控制服务已停止。";
        }
    }

    public void CreatePairingCode()
    {
        if (_service is null) throw new InvalidOperationException("请先启动手机控制服务。");
        var invitation = _service.CreatePairingInvitation();
        PairingCode = invitation.Code;
        PairingExpiry = $"有效至 {invitation.ExpiresUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}；扫码后仍需在手机确认配对，并由教师在此窗口批准。";
        Status = "已生成一次性配对二维码。手机扫码后确认请求；核对设备名称和 LAN 地址后再批准。";
    }

    public bool RevokeSelectedDevice()
    {
        if (SelectedDevice is null) return false;
        var id = SelectedDevice.Id;
        var name = SelectedDevice.DisplayName;
        var revoked = MobilePairedDeviceStore.Revoke(id);
        if (revoked) _service?.ForgetDevice(id);
        RefreshDevices();
        Status = revoked ? $"已撤销“{name}”的手机访问权。" : "所选手机已经撤销或不存在。";
        return revoked;
    }

    public void RefreshPendingPairings()
    {
        PendingPairings = _service?.ListPendingPairings() ?? Array.Empty<MobilePendingPairingView>();
        if (SelectedPairing is { } selected)
            SelectedPairing = PendingPairings.FirstOrDefault(item => item.Id == selected.Id);
    }

    public bool ApproveSelectedPairing()
    {
        if (SelectedPairing is not { } selected || _service is null) return false;
        var approved = _service.ApprovePairing(selected.Id);
        RefreshPendingPairings();
        Status = approved ? $"已批准手机“{selected.DeviceName}”配对。" : "配对请求已过期或已处理。";
        return approved;
    }

    public bool RejectSelectedPairing()
    {
        if (SelectedPairing is not { } selected || _service is null) return false;
        var rejected = _service.RejectPairing(selected.Id);
        RefreshPendingPairings();
        Status = rejected ? $"已拒绝手机“{selected.DeviceName}”的配对请求。" : "配对请求已过期或已处理。";
        return rejected;
    }

    public void RefreshDevices()
    {
        Devices = MobilePairedDeviceStore.List();
        if (SelectedDevice is { } selected)
            SelectedDevice = Devices.FirstOrDefault(device => device.Id == selected.Id);
    }

    public void RefreshProfiles()
    {
        var selectedId = SelectedProfile?.Id;
        var campusId = CurrentCampusId();
        Profiles = MobilePolicyProfileStore.ReadAll()
            .Where(profile => string.Equals(profile.CampusId, campusId, StringComparison.Ordinal)).ToArray();
        SelectedProfile = selectedId is { } id
            ? Profiles.FirstOrDefault(profile => profile.Id == id)
            : Profiles.FirstOrDefault();
    }

    public void RefreshAudit()
    {
        try
        {
            var entries = MobileControlAuditStore.Read().Reverse().Take(50).Select(entry =>
            {
                var outcomes = string.Join(" · ", (entry.Results ?? []).Select(result =>
                    result.Target + ": " + result.Outcome));
                if (outcomes.Length > 1600) outcomes = outcomes[..1600] + "…";
                var shortDeviceId = entry.DeviceId.ToString("N")[..8];
                var deviceName = MobilePairedDeviceStore.Find(entry.DeviceId)?.DisplayName;
                var device = string.IsNullOrWhiteSpace(deviceName) ? shortDeviceId : $"{deviceName} · {shortDeviceId}";
                return new MobileControlAuditView(entry.TimeUtc, device,
                    entry.Action, string.Join(", ", entry.Targets),
                    outcomes.Length == 0 ? entry.Outcome : entry.Outcome + " · " + outcomes);
            }).ToArray();
            AuditEntries = Array.AsReadOnly(entries);
            AuditStatus = entries.Length == 0 ? "尚无手机控制记录。" : $"最近 {entries.Length} 条；完整记录保留在教师电脑本机。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or JsonException or ArgumentException)
        {
            AuditEntries = Array.Empty<MobileControlAuditView>();
            AuditStatus = "无法读取本机手机控制记录：" + exception.Message;
        }
    }

    public bool DeleteSelectedProfile()
    {
        if (SelectedProfile is null) return false;
        var removed = MobilePolicyProfileStore.Remove(SelectedProfile.Id);
        RefreshProfiles();
        return removed;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void OnPairingChanged() => _dispatchToUi(() =>
    {
        RefreshPendingPairings();
        RefreshDevices();
        RefreshAudit();
    });

    private string CurrentCampusId()
    {
        var campusId = _activeCampusId().Trim();
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        return campusId;
    }

    private void RefreshPairingQrUrl()
    {
        PairingQrUrl = string.IsNullOrWhiteSpace(PairingCode) || string.IsNullOrWhiteSpace(SelectedPairingUrl)
            ? ""
            : MobilePairingQrLink.Create(SelectedPairingUrl, PairingCode);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPairingQr)));
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<IPAddress> GetPrivateAddresses()
    {
        var addresses = new SortedSet<IPAddress>(Comparer<IPAddress>.Create((left, right) =>
            StringComparer.Ordinal.Compare(left.ToString(), right.ToString())));
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var item in adapter.GetIPProperties().UnicastAddresses)
                if (MobileControlTlsIdentityStore.IsPrivateIpv4(item.Address)) addresses.Add(item.Address);
        }
        if (addresses.Count == 0) throw new InvalidOperationException("当前没有可供手机连接的私有 IPv4 地址。");
        if (addresses.Count > 16) throw new InvalidDataException("本机私有 IPv4 地址过多；请先停用不需要的网络适配器。");
        return Array.AsReadOnly(addresses.ToArray());
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(IsRunning))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStart)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStop)));
        }
        return true;
    }
}

internal sealed class TeacherMobileControlService : IAsyncDisposable
{
    private const int MaximumApiBodyBytes = 256 * 1024;
    private static readonly TimeSpan PairingRequestLifetime = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private readonly MobileControlTlsIdentity _identity;
    private readonly Action _stateChanged;
    private readonly Func<string> _activeCampusId;
    private readonly string? _storageDirectory;
    private readonly int _httpsPort;
    private readonly int _bootstrapPort;
    private readonly object _pairingGate = new();
    private readonly Dictionary<string, (DateTimeOffset StartedUtc, int Failures)> _pairingFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, PendingPairing> _pendingPairings = [];
    private readonly ConcurrentDictionary<string, MobileReviewGrant> _reviewGrants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _requestNonces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, RequestRateWindow> _requestRates = new();
    private WebApplication? _application;
    private byte[]? _pairingCodeHash;
    private DateTimeOffset _pairingExpiresUtc;
    private bool _pairingConsumed;
    private int _pairingFailureCount;
    private readonly SemaphoreSlim _policyGate = new(1, 1);

    private sealed record PendingPairing(Guid Id, string DeviceName, string SourceAddress,
        string TicketSha256, DateTimeOffset RequestedUtc, DateTimeOffset ExpiresUtc,
        string? ApprovedAccessToken = null, bool Rejected = false);
    private sealed class RequestRateWindow
    {
        public readonly object Gate = new();
        public DateTimeOffset StartedUtc = DateTimeOffset.UtcNow;
        public int Count;
    }

    public TeacherMobileControlService(MobileControlTlsIdentity identity, Action paired,
        Func<string> activeCampusId, string? storageDirectory = null,
        int httpsPort = TeacherMobileControlManager.HttpsPort,
        int bootstrapPort = TeacherMobileControlManager.BootstrapPort)
    {
        if (httpsPort is < 0 or > 65535 || bootstrapPort is < 0 or > 65535 || httpsPort == bootstrapPort)
            throw new ArgumentOutOfRangeException(nameof(httpsPort), "手机控制服务端口无效。");
        _identity = identity;
        _stateChanged = paired;
        _activeCampusId = activeCampusId;
        _storageDirectory = storageDirectory is null ? null : Path.GetFullPath(storageDirectory);
        _httpsPort = httpsPort;
        _bootstrapPort = bootstrapPort;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(TeacherMobileControlService).Assembly.FullName
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaximumApiBodyBytes;
            foreach (var address in _identity.Addresses)
            {
                options.Listen(address, _httpsPort,
                    endpoint => endpoint.UseHttps(_identity.Server));
                options.Listen(address, _bootstrapPort);
            }
        });
        var app = builder.Build();
        ConfigurePipeline(app);
        _application = app;
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public (string Code, DateTimeOffset ExpiresUtc) CreatePairingInvitation()
    {
        var code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset expires;
        lock (_pairingGate)
        {
            _pairingCodeHash = SHA256.HashData(Encoding.ASCII.GetBytes(code));
            expires = _pairingExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5);
            _pairingConsumed = false;
            _pairingFailures.Clear();
            _pairingFailureCount = 0;
            PurgePendingPairings(DateTimeOffset.UtcNow);
        }
        return (code, expires);
    }

    public IReadOnlyList<MobilePendingPairingView> ListPendingPairings()
    {
        lock (_pairingGate)
        {
            var now = DateTimeOffset.UtcNow;
            PurgePendingPairings(now);
            return Array.AsReadOnly(_pendingPairings.Values
                .Where(item => item.ApprovedAccessToken is null && !item.Rejected)
                .OrderBy(item => item.RequestedUtc)
                .Select(item => new MobilePendingPairingView(item.Id, item.DeviceName,
                    item.SourceAddress, item.RequestedUtc)).ToArray());
        }
    }

    public bool ApprovePairing(Guid id)
    {
        lock (_pairingGate)
        {
            var now = DateTimeOffset.UtcNow;
            PurgePendingPairings(now);
            if (!_pendingPairings.TryGetValue(id, out var pending) || pending.Rejected ||
                pending.ApprovedAccessToken is not null) return false;
            var reserved = _pendingPairings.Values.Count(item => item.ApprovedAccessToken is not null && item.ExpiresUtc > now);
            if (MobilePairedDeviceStore.List(_storageDirectory).Count + reserved >= 50)
                throw new InvalidDataException("已达到配对手机数量上限；请先撤销不再使用的设备。");
            _pendingPairings[id] = pending with
            {
                ApprovedAccessToken = CreateToken(),
                ExpiresUtc = now.Add(PairingRequestLifetime)
            };
            return true;
        }
    }

    public bool RejectPairing(Guid id)
    {
        lock (_pairingGate)
        {
            var now = DateTimeOffset.UtcNow;
            PurgePendingPairings(now);
            if (!_pendingPairings.TryGetValue(id, out var pending) || pending.Rejected ||
                pending.ApprovedAccessToken is not null) return false;
            _pendingPairings[id] = pending with { Rejected = true, ExpiresUtc = now.Add(PairingRequestLifetime) };
            return true;
        }
    }

    public void ForgetDevice(Guid deviceId)
    {
        _requestRates.TryRemove(deviceId, out _);
        var prefix = deviceId.ToString("N") + ":";
        foreach (var key in _requestNonces.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            _requestNonces.TryRemove(key, out _);
    }

    public async ValueTask DisposeAsync()
    {
        var app = Interlocked.Exchange(ref _application, null);
        try
        {
            if (app is not null)
            {
                try { await app.StopAsync().ConfigureAwait(false); }
                finally { await app.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            _identity.Dispose();
            _policyGate.Dispose();
            lock (_pairingGate) _pairingCodeHash = null;
        }
    }

    private void ConfigurePipeline(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Connection.LocalPort == _bootstrapPort)
            {
                await ServeBootstrap(context).ConfigureAwait(false);
                return;
            }
            if (!IsSameLan(context.Connection.RemoteIpAddress) || !context.Request.IsHttps ||
                !IsBoundHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Strict-Transport-Security"] = "max-age=86400";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; " +
                "manifest-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
            if (context.Request.Path.StartsWithSegments("/api") &&
                (context.Request.Method is "POST" or "PUT" or "DELETE") && !HasSameOrigin(context.Request))
            {
                await WriteError(context, StatusCodes.Status403Forbidden, "来源校验失败。")
                    .ConfigureAwait(false);
                return;
            }
            try { await next(context).ConfigureAwait(false); }
            catch (InvalidDataException exception)
            {
                await WriteError(context, StatusCodes.Status400BadRequest, exception.Message).ConfigureAwait(false);
            }
            catch (MobileAuthorizationException exception)
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, exception.Message).ConfigureAwait(false);
            }
            catch (MobileRateLimitException exception)
            {
                context.Response.Headers["Retry-After"] = "60";
                await WriteError(context, StatusCodes.Status429TooManyRequests, exception.Message).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                await WriteError(context, StatusCodes.Status400BadRequest, "请求 JSON 无效。").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception)
            {
                await WriteError(context, StatusCodes.Status500InternalServerError,
                    "手机控制操作失败；请在教师控制台查看当前逐台状态。").ConfigureAwait(false);
            }
        });

        app.MapGet("/", context => ServeAsset(context, "index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.js", context => ServeAsset(context, "app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/styles.css", context => ServeAsset(context, "styles.css", "text/css; charset=utf-8"));
        app.MapGet("/manifest.webmanifest", context => ServeAsset(context, "manifest.webmanifest", "application/manifest+json"));
        app.MapGet("/service-worker.js", context => ServeAsset(context, "service-worker.js", "text/javascript; charset=utf-8"));
        app.MapPost("/api/pair", async context => await PairAsync(context).ConfigureAwait(false));
        app.MapPost("/api/pair-status", async context => await PollPairingAsync(context).ConfigureAwait(false));
        app.MapGet("/api/session", async context =>
        {
            var device = Authorize(context);
            await WriteJson(context, new MobileSessionResponse(device, "已连接教师控制台。"), context.RequestAborted)
                .ConfigureAwait(false);
        });
        app.MapPost("/api/logout", async context =>
        {
            _ = Authorize(context);
            await WriteJson(context, new { signedOut = true }, context.RequestAborted).ConfigureAwait(false);
        });
        app.MapGet("/api/rooms", async context =>
        {
            _ = Authorize(context);
            var rooms = await Task.Run(VeyonNetworkObjectDirectory.ReadLocations, context.RequestAborted)
                .ConfigureAwait(false);
            await WriteJson(context, rooms, context.RequestAborted).ConfigureAwait(false);
        });
        app.MapGet("/api/profiles", async context =>
        {
            _ = Authorize(context);
            var campusId = CurrentCampusId();
            var profiles = MobilePolicyProfileStore.ReadAll(_storageDirectory)
                .Where(profile => string.Equals(profile.CampusId, campusId, StringComparison.Ordinal))
                .Select(ToSummary).ToArray();
            await WriteJson(context, profiles, context.RequestAborted).ConfigureAwait(false);
        });
        app.MapPost("/api/status", async context =>
        {
            if (!OperatingSystem.IsWindows())
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                return;
            }
            await ReadStatusAsync(context).ConfigureAwait(false);
        });
        app.MapPost("/api/policy", async context =>
        {
            if (!OperatingSystem.IsWindows())
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                return;
            }
            await ApplyPolicyAsync(context).ConfigureAwait(false);
        });
    }

    private async Task ServeBootstrap(HttpContext context)
    {
        if (context.Request.Method != "GET" || context.Request.Path != "/teacher-mobile-root.cer" ||
            !IsSameLan(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/pkix-cert";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Content-Disposition"] = "attachment; filename=veyon-campus-mobile-root.cer";
        context.Response.ContentLength = _identity.RootCertificateBytes.Length;
        await context.Response.Body.WriteAsync(_identity.RootCertificateBytes, context.RequestAborted)
            .ConfigureAwait(false);
    }

    private async Task PairAsync(HttpContext context)
    {
        if (!IsSameLan(context.Connection.RemoteIpAddress))
        {
            await WriteError(context, StatusCodes.Status403Forbidden, "配对只允许从教师电脑所在校园 LAN 发起。")
                .ConfigureAwait(false);
            return;
        }
        var request = await ReadJson<MobilePairRequest>(context.Request, 4096, context.RequestAborted)
            .ConfigureAwait(false);
        var displayName = MobilePairedDeviceStore.ValidateDisplayName(request.DeviceName);
        var sourceKey = context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown";
        if (!TryConsumePairingCode(request.PairingCode, sourceKey))
        {
            await WriteError(context, StatusCodes.Status401Unauthorized, "配对码错误、过期或已使用。").ConfigureAwait(false);
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var ticket = CreateToken();
        var expires = now.Add(PairingRequestLifetime);
        lock (_pairingGate)
        {
            PurgePendingPairings(now);
            if (_pendingPairings.Count >= 50)
                throw new InvalidDataException("待处理配对请求过多；请在教师端先处理现有请求。");
            var id = Guid.NewGuid();
            _pendingPairings.Add(id, new PendingPairing(id, displayName, sourceKey,
                MobilePairedDeviceStore.HashToken(ticket), now, expires));
        }
        _stateChanged();
        await WriteJson(context, new MobilePairPendingResponse(ticket, expires), context.RequestAborted)
            .ConfigureAwait(false);
    }

    private async Task PollPairingAsync(HttpContext context)
    {
        if (!IsSameLan(context.Connection.RemoteIpAddress))
            throw new MobileAuthorizationException("配对只允许从教师电脑所在校园 LAN 发起。");
        var request = await ReadJson<MobilePairPollRequest>(context.Request, 4096, context.RequestAborted)
            .ConfigureAwait(false);
        var ticketHash = MobilePairedDeviceStore.HashToken(request.Ticket ?? "");
        var sourceAddress = context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown";
        MobilePairPollResponse response = new("expired");
        var paired = false;
        lock (_pairingGate)
        {
            var now = DateTimeOffset.UtcNow;
            PurgePendingPairings(now);
            var pending = _pendingPairings.Values.FirstOrDefault(item =>
                CryptographicOperations.FixedTimeEquals(Convert.FromHexString(item.TicketSha256),
                    Convert.FromHexString(ticketHash)));
            if (pending is not null && pending.SourceAddress != sourceAddress)
                throw new MobileAuthorizationException("配对请求必须由最初提交配对码的手机继续完成。");
            if (pending is not null && pending.ExpiresUtc > now)
            {
                if (pending.Rejected)
                {
                    _pendingPairings.Remove(pending.Id);
                    response = new MobilePairPollResponse("rejected");
                }
                else if (pending.ApprovedAccessToken is { } token)
                {
                    var device = MobilePairedDeviceStore.Add(token, pending.DeviceName, _storageDirectory);
                    _pendingPairings.Remove(pending.Id);
                    response = new MobilePairPollResponse("approved", token, device);
                    paired = true;
                }
                else response = new MobilePairPollResponse("waiting-approval");
            }
        }
        if (paired) _stateChanged();
        await WriteJson(context, response, context.RequestAborted).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private async Task ReadStatusAsync(HttpContext context)
    {
        var device = Authorize(context);
        var request = await ReadJson<MobileStatusRequest>(context.Request, 16 * 1024, context.RequestAborted)
            .ConfigureAwait(false);
        var targets = await ValidateTargetsAsync(request.Targets, context.RequestAborted).ConfigureAwait(false);
        var campusId = CurrentCampusId();
        using var signingKey = WebsitePolicySigningKeyStore.Open(campusId);
        var results = await WebsitePolicyStatusTransport.ReadAsync(targets, campusId,
            signingKey.PrivateKey, context.RequestAborted).ConfigureAwait(false);
        var states = results.Select(result =>
        {
            var current = result.Status;
            return new MobileTargetStatus(result.Target, result.Succeeded,
                result.Succeeded ? "签名身份已验证" : result.IdentityCandidate is not null ? "身份待核对" : "状态未知",
                current?.AgentVersion, current?.CollectedUtc, current?.Website.Mode, current?.Website.Revision, current?.Website.ExpiresUtc,
                current?.Application?.Mode, current?.Application?.Revision, current?.Application?.ExpiresUtc,
                current?.Application?.Supported ?? false, result.NeedsReview, Truncate(result.Detail, 300),
                current?.SystemPolicy);
        }).ToArray();
        var statusAudit = states.Select(item => new MobileControlAuditTargetResult(item.Target,
            item.Online ? "signature-verified" : item.NeedsReview ? "needs-review" : "unknown")).ToArray();
        var statusAuditSaved = TryAppendAudit(new MobileControlAuditEntry(DateTimeOffset.UtcNow, device.Id,
            "status-read", null, targets, Summarize(states.Select(item => item.Online)), statusAudit));
        _stateChanged();
        if (!statusAuditSaved)
            states = states.Select(item => item with
            {
                State = item.State + " · 本机操作日志未保存",
                Detail = Truncate(item.Detail + " 教师电脑无法保存本次查看记录。", 300),
                NeedsReview = true
            }).ToArray();
        await WriteJson(context, states, context.RequestAborted).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private async Task ApplyPolicyAsync(HttpContext context)
    {
        var device = Authorize(context);
        var request = await ReadJson<MobilePolicyRequest>(context.Request, 16 * 1024, context.RequestAborted)
            .ConfigureAwait(false);
        var profile = MobilePolicyProfileCompiler.Validate(FindProfile(request.ProfileId));
        var targets = await ValidateTargetsAsync(request.Targets, context.RequestAborted).ConfigureAwait(false);
        await _policyGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            MobilePolicyOperationResponse response;
            if (profile.Kind == MobilePolicyProfileKind.Application && request.Enabled &&
                profile.ApplicationMode == ApplicationPolicyMode.Enforce)
            {
                response = string.IsNullOrWhiteSpace(request.ReviewToken)
                    ? await StageApplicationEnforcementAsync(device.Id, profile, targets, context.RequestAborted)
                        .ConfigureAwait(false)
                    : await CompleteApplicationEnforcementAsync(device.Id, profile, targets, request.ReviewToken,
                        context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(request.ReviewToken))
                    throw new InvalidDataException("该策略操作不接受审核票据。");
                response = await PushPolicyAsync(profile, targets, request.Enabled, context.RequestAborted)
                    .ConfigureAwait(false);
            }
            var action = request.Enabled
                ? profile.Kind switch
                {
                    MobilePolicyProfileKind.Application when profile.ApplicationMode == ApplicationPolicyMode.Enforce =>
                        string.IsNullOrWhiteSpace(request.ReviewToken) ? "application-audit-preflight" : "application-enforce",
                    MobilePolicyProfileKind.System => "system-policy-enable",
                    _ => "policy-enable"
                }
                : profile.Kind == MobilePolicyProfileKind.System ? "system-policy-disable" : "policy-disable";
            var policyAudit = response.Results.Select(item => new MobileControlAuditTargetResult(item.Target,
                item.NeedsReview ? "needs-review" : item.AgentAccepted ? "agent-accepted" : "failed")).ToArray();
            var auditSaved = TryAppendAudit(new MobileControlAuditEntry(DateTimeOffset.UtcNow, device.Id, action,
                profile.Id, targets, response.State, policyAudit));
            _stateChanged();
            if (!auditSaved)
                response = response with { Message = response.Message + " 教师电脑无法保存本次操作日志。" };
            await WriteJson(context, response, context.RequestAborted).ConfigureAwait(false);
        }
        finally { _policyGate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private async Task<MobilePolicyOperationResponse> PushPolicyAsync(MobilePolicyProfile profile,
        IReadOnlyList<string> targets, bool enabled, CancellationToken cancellationToken)
    {
        if (profile.Kind == MobilePolicyProfileKind.Website)
        {
            using var key = WebsitePolicySigningKeyStore.Open(profile.CampusId);
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? expires = enabled && profile.LifetimeMinutes > 0 ? now.AddMinutes(profile.LifetimeMinutes) : null;
            var mode = enabled ? profile.WebsiteMode!.Value : WebsitePolicyMode.Disabled;
            var domains = enabled ? profile.WebsiteDomains! : Array.Empty<string>();
            var policy = WebsitePolicyCompiler.Create(profile.CampusId,
                WebsitePolicyRevisionStore.Next(profile.CampusId), mode, domains, now, expires);
            var signed = WebsitePolicyCryptography.Sign(policy, key.PrivateKey);
            var results = await WebsitePolicyTransport.PushAsync(targets, signed, profile.CampusId, cancellationToken)
                .ConfigureAwait(false);
            return new MobilePolicyOperationResponse("agent-accepted", false, null,
                enabled ? "网站策略已发送；逐台结果会标明 Agent 签名和身份核对状态。" : "网站限制解除命令已发送。",
                policy.Revision, expires, results.Select(ToMobileResult).ToArray());
        }

        if (profile.Kind == MobilePolicyProfileKind.System)
        {
            using var key = StudentSystemPolicySigningKeyStore.Open(profile.CampusId);
            var policy = MobilePolicyProfileCompiler.CreateStudentSystemPolicy(profile,
                StudentSystemPolicySigningKeyStore.NextRevision(profile.CampusId), enabled, DateTimeOffset.UtcNow);
            var signed = StudentSystemPolicyCryptography.Sign(policy, key.PrivateKey);
            var results = await StudentSystemPolicyTransport.PushAsync(targets, signed, profile.CampusId,
                cancellationToken).ConfigureAwait(false);
            return new MobilePolicyOperationResponse("agent-accepted", false, null,
                enabled ? "长期系统限制已发送；逐台结果会标明 Agent 签名和身份核对状态，策略不会自动到期。" :
                    "长期系统限制解除命令已发送；学生端只恢复仍由本工具拥有的原始设置。",
                policy.Revision, null, results.Select(ToMobileResult).ToArray());
        }

        using (var key = ApplicationPolicySigningKeyStore.Open(profile.CampusId))
        {
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? expires = enabled ? now.AddMinutes(profile.LifetimeMinutes) : null;
            var mode = enabled ? profile.ApplicationMode!.Value : ApplicationPolicyMode.Disabled;
            var policy = ApplicationPolicyCompiler.Validate(new ApplicationPolicyDocument(1,
                ApplicationPolicyCompiler.Purpose, profile.CampusId,
                WebsitePolicyRevisionStore.Next(profile.CampusId), now, expires, mode,
                enabled ? profile.StudentSids! : Array.Empty<string>(),
                enabled ? profile.ApplicationRules! : Array.Empty<ApplicationDenyRule>()));
            var signed = ApplicationPolicyCryptography.Sign(policy, key.PrivateKey);
            var results = await ApplicationPolicyTransport.PushAsync(targets, signed, profile.CampusId, cancellationToken)
                .ConfigureAwait(false);
            return new MobilePolicyOperationResponse("agent-accepted", false, null,
                enabled ? "应用审核策略已发送；逐台结果会标明 Agent 签名和身份核对状态。" : "应用限制解除命令已发送。",
                policy.Revision, expires, results.Select(ToMobileResult).ToArray());
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<MobilePolicyOperationResponse> StageApplicationEnforcementAsync(Guid deviceId,
        MobilePolicyProfile profile, IReadOnlyList<string> targets, CancellationToken cancellationToken)
    {
        using var key = ApplicationPolicySigningKeyStore.Open(profile.CampusId);
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(profile.LifetimeMinutes);
        var revision = WebsitePolicyRevisionStore.Next(profile.CampusId);
        var auditPolicy = ApplicationPolicyCompiler.Validate(new ApplicationPolicyDocument(1,
            ApplicationPolicyCompiler.Purpose, profile.CampusId, revision, now, expires,
            ApplicationPolicyMode.Audit, profile.StudentSids!, profile.ApplicationRules!));
        var signed = ApplicationPolicyCryptography.Sign(auditPolicy, key.PrivateKey);
        var push = await ApplicationPolicyTransport.PushAsync(targets, signed, profile.CampusId, cancellationToken)
            .ConfigureAwait(false);
        if (push.Any(item => !item.Succeeded))
            return new MobilePolicyOperationResponse("needs-review", false, null,
                "部分电脑没有确认审核策略；尚未启用阻止。请核对逐台结果后重试。", revision, expires,
                push.Select(ToMobileResult).ToArray());

        var audit = await ApplicationPolicyTransport.ReadAuditAsync(targets, profile.CampusId,
            key.PrivateKey, 24, cancellationToken).ConfigureAwait(false);
        var matching = audit.Count == targets.Count && audit.All(item => item.Succeeded && item.Response is { } response &&
            response.PolicyRevision == revision && response.Mode == ApplicationPolicyMode.Audit);
        var review = audit.Select(item => new MobileApplicationReviewTarget(item.Target,
            item.Response?.Results.Select(result => new MobileApplicationReviewRule(result.RuleId,
                result.DisplayName, result.WouldBlockCount, result.BlockedCount)).ToArray() ??
                Array.Empty<MobileApplicationReviewRule>(), item.Response?.IsSimulation ?? false,
            item.Response?.CoverageNote)).ToArray();
        if (!matching)
            return new MobilePolicyOperationResponse("needs-review", false, null,
                "审核回执没有全部匹配刚推送的策略版本；尚未启用阻止。", revision, expires,
                audit.Select(item => new MobilePolicyTargetResult(item.Target, item.Succeeded,
                    NeedsReview: true, Truncate(item.Detail, 300))).ToArray(), review);

        var token = CreateToken();
        foreach (var grant in _reviewGrants.Where(entry => entry.Value.ExpiresUtc <= DateTimeOffset.UtcNow)
                     .Select(entry => entry.Key).ToArray())
            _reviewGrants.TryRemove(grant, out _);
        if (_reviewGrants.Count >= 1024)
            throw new InvalidDataException("等待确认的应用审核请求过多；请稍候重试。");
        var fingerprint = ProfileFingerprint(profile, targets);
        var auditFingerprint = ApplicationAuditFingerprint(audit);
        _reviewGrants[MobilePairedDeviceStore.HashToken(token)] = new MobileReviewGrant(deviceId, profile.Id, fingerprint,
            revision, auditFingerprint,
            Array.AsReadOnly(targets.ToArray()), DateTimeOffset.UtcNow.AddMinutes(5));
        return new MobilePolicyOperationResponse("awaiting-teacher-review", true, token,
            "请阅读下方各电脑的应用审核统计。Agent 身份签名和本次请求已核验；教师确认前不会启用阻止。",
            revision, expires, audit.Select(item => new MobilePolicyTargetResult(item.Target, true,
                NeedsReview: true, "审核策略版本与 Agent 身份签名已核验；请人工确认统计后继续。")).ToArray(), review);
    }

    [SupportedOSPlatform("windows")]
    private async Task<MobilePolicyOperationResponse> CompleteApplicationEnforcementAsync(Guid deviceId,
        MobilePolicyProfile profile, IReadOnlyList<string> targets, string rawReviewToken,
        CancellationToken cancellationToken)
    {
        foreach (var entry in _reviewGrants.ToArray())
            if (entry.Value.ExpiresUtc <= DateTimeOffset.UtcNow) _reviewGrants.TryRemove(entry.Key, out _);
        var tokenHash = MobilePairedDeviceStore.HashToken(rawReviewToken);
        if (!_reviewGrants.TryGetValue(tokenHash, out var grant) ||
            grant.DeviceId != deviceId || grant.ProfileId != profile.Id || grant.ExpiresUtc <= DateTimeOffset.UtcNow ||
            grant.ProfileFingerprint != ProfileFingerprint(profile, targets) ||
            !grant.Targets.SequenceEqual(targets, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("应用审核确认已过期、已使用，或与当前预设/目标不匹配；请重新审核。");

        using var key = ApplicationPolicySigningKeyStore.Open(profile.CampusId);
        var freshAudit = await ApplicationPolicyTransport.ReadAuditAsync(targets, profile.CampusId,
            key.PrivateKey, 24, cancellationToken).ConfigureAwait(false);
        var auditStillCurrent = freshAudit.Count == targets.Count && freshAudit.All(item =>
            item.Succeeded && item.Response is { } response && response.PolicyRevision == grant.AuditRevision &&
            response.Mode == ApplicationPolicyMode.Audit);
        if (!auditStillCurrent || ApplicationAuditFingerprint(freshAudit) != grant.AuditFingerprint)
        {
            _reviewGrants.TryRemove(tokenHash, out _);
            throw new InvalidDataException("确认前应用审核版本或统计已变化；为避免使用过期审核结果，请重新审核并确认。");
        }
        if (!_reviewGrants.TryRemove(tokenHash, out _))
            throw new InvalidDataException("应用审核确认已使用；请重新审核。");
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(profile.LifetimeMinutes);
        var policy = ApplicationPolicyCompiler.Validate(new ApplicationPolicyDocument(1,
            ApplicationPolicyCompiler.Purpose, profile.CampusId,
            WebsitePolicyRevisionStore.Next(profile.CampusId), now, expires, ApplicationPolicyMode.Enforce,
            profile.StudentSids!, profile.ApplicationRules!));
        var signed = ApplicationPolicyCryptography.Sign(policy, key.PrivateKey);
        var results = await ApplicationPolicyTransport.PushAsync(targets, signed, profile.CampusId, cancellationToken)
            .ConfigureAwait(false);
        return new MobilePolicyOperationResponse("agent-accepted", false, null,
            "执行策略已发送；逐台确认仅表示 Agent 接受策略，不代表应用启动效果已实机验证。",
            policy.Revision, expires, results.Select(ToMobileResult).ToArray());
    }

    private async Task<IReadOnlyList<string>> ValidateTargetsAsync(IReadOnlyList<string> requested,
        CancellationToken cancellationToken)
    {
        if (requested is null) throw new InvalidDataException("请至少选择一台 Veyon 机房电脑。");
        var targets = WebsitePolicyTransport.NormalizeTargets(requested);
        var locations = await Task.Run(VeyonNetworkObjectDirectory.ReadLocations, cancellationToken)
            .ConfigureAwait(false);
        var allowed = locations.SelectMany(location => location.Targets)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalid = targets.Where(target => !allowed.Contains(target)).ToArray();
        if (invalid.Length > 0)
            throw new InvalidDataException("目标不在教师端当前 Veyon 机房目录中：" + string.Join("、", invalid.Take(5)));
        return targets;
    }

    private MobilePolicyProfile FindProfile(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("手机策略预设标识无效。");
        var profile = MobilePolicyProfileStore.ReadAll(_storageDirectory).SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidDataException("手机策略预设已删除或不存在；请刷新列表。");
        if (!string.Equals(profile.CampusId, CurrentCampusId(), StringComparison.Ordinal))
            throw new InvalidDataException("教师端当前校区已改变或预设属于其他校区；请刷新并仅使用当前校区预设。");
        return profile;
    }

    private string CurrentCampusId()
    {
        var campusId = _activeCampusId().Trim();
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        return campusId;
    }

    private MobilePairedDeviceView Authorize(HttpContext context)
    {
        if (!IsSameLan(context.Connection.RemoteIpAddress))
            throw new MobileAuthorizationException("手机控制只允许从教师电脑所在校园 LAN 使用。");
        var header = context.Request.Headers["Authorization"].ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length > 180)
            throw new MobileAuthorizationException("需要手机配对凭据。");
        var device = MobilePairedDeviceStore.FindAuthorized(header[7..], _storageDirectory);
        if (device is null)
            throw new MobileAuthorizationException("手机配对已撤销或凭据无效。");
        if (!AcceptRequestNonce(context, device.Id))
            throw new MobileAuthorizationException("请求时间无效或随机数重复；请刷新后重试。");
        if (!AcceptRequestRate(device.Id))
            throw new MobileRateLimitException("手机请求过于频繁，请稍后再试。");
        return device;
    }

    private bool AcceptRequestRate(Guid deviceId)
    {
        var window = _requestRates.GetOrAdd(deviceId, _ => new RequestRateWindow());
        lock (window.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - window.StartedUtc >= TimeSpan.FromMinutes(1))
            {
                window.StartedUtc = now;
                window.Count = 0;
            }
            return ++window.Count <= 60;
        }
    }

    private bool AcceptRequestNonce(HttpContext context, Guid deviceId)
    {
        var nonceText = context.Request.Headers["X-Veyon-Request-Nonce"].ToString();
        var timestampText = context.Request.Headers["X-Veyon-Request-Timestamp"].ToString();
        var now = DateTimeOffset.UtcNow;
        if (!Guid.TryParseExact(nonceText, "N", out var nonce) || nonce == Guid.Empty ||
            !DateTimeOffset.TryParseExact(timestampText, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp) ||
            timestamp.Offset != TimeSpan.Zero || timestamp < now.AddMinutes(-2) || timestamp > now.AddMinutes(1))
            return false;
        foreach (var entry in _requestNonces.ToArray())
            if (entry.Value < now.AddMinutes(-5)) _requestNonces.TryRemove(entry.Key, out _);
        if (_requestNonces.Count >= 8192) return false;
        return _requestNonces.TryAdd(deviceId.ToString("N") + ":" + nonce.ToString("N"), now.AddMinutes(5));
    }

    private bool TryConsumePairingCode(string? code, string sourceKey)
    {
        lock (_pairingGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _pairingFailures.Where(item => now - item.Value.StartedUtc > TimeSpan.FromMinutes(10))
                         .Select(item => item.Key).ToArray())
                _pairingFailures.Remove(expired);
            if (_pairingFailures.Count > 256 || _pairingFailureCount >= 30) return false;
            if (!_pairingFailures.TryGetValue(sourceKey, out var failures) ||
                now - failures.StartedUtc > TimeSpan.FromMinutes(10))
                failures = (now, 0);
            if (failures.Failures >= 5) return false;
            var validShape = code is { Length: 8 } && code.All(char.IsAsciiDigit);
            var supplied = SHA256.HashData(Encoding.ASCII.GetBytes(validShape ? code! : "00000000"));
            var valid = validShape && !_pairingConsumed && _pairingCodeHash is not null && now <= _pairingExpiresUtc &&
                        CryptographicOperations.FixedTimeEquals(supplied, _pairingCodeHash);
            if (!valid)
            {
                _pairingFailures[sourceKey] = (failures.StartedUtc, failures.Failures + 1);
                _pairingFailureCount++;
                return false;
            }
            _pairingConsumed = true;
            _pairingCodeHash = null;
            _pairingFailures.Clear();
            return true;
        }
    }

    private void PurgePendingPairings(DateTimeOffset now)
    {
        foreach (var id in _pendingPairings.Values.Where(item => item.ExpiresUtc <= now)
                     .Select(item => item.Id).ToArray())
            _pendingPairings.Remove(id);
    }

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ProfileFingerprint(MobilePolicyProfile profile, IReadOnlyList<string> targets)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Profile = profile,
            Targets = targets.OrderBy(target => target, StringComparer.OrdinalIgnoreCase).ToArray()
        }, MobilePolicyProfileCompiler.JsonOptions);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static string ApplicationAuditFingerprint(IEnumerable<ApplicationPolicyAuditDeliveryResult> audit)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(audit.OrderBy(item => item.Target,
                StringComparer.OrdinalIgnoreCase).Select(item => new
            {
                item.Target,
                item.Succeeded,
                Revision = item.Response?.PolicyRevision,
                Mode = item.Response?.Mode,
                IsSimulation = item.Response?.IsSimulation,
                CoverageNote = item.Response?.CoverageNote,
                Results = item.Response?.Results.OrderBy(result => result.StudentSid, StringComparer.Ordinal)
                    .ThenBy(result => result.RuleId).ToArray()
            }), MobilePolicyProfileCompiler.JsonOptions);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static MobilePolicyProfileSummary ToSummary(MobilePolicyProfile profile) => new(profile.Id,
        profile.Name, profile.CampusId, profile.Kind,
        profile.Kind switch
        {
            MobilePolicyProfileKind.Website => profile.WebsiteMode!.Value.ToString(),
            MobilePolicyProfileKind.Application => profile.ApplicationMode!.Value.ToString(),
            MobilePolicyProfileKind.System => "长期基线",
            _ => throw new InvalidDataException("手机策略预设类型无效。")
        },
        profile.LifetimeMinutes, profile.UpdatedUtc,
        profile.Kind == MobilePolicyProfileKind.System ? profile.SystemSettings : null);

    private static MobilePolicyTargetResult ToMobileResult(WebsitePolicyPushResult result) => new(result.Target,
        result.Succeeded, result.NeedsReview, Truncate(result.Detail, 300));

    private static MobilePolicyTargetResult ToMobileResult(ApplicationPolicyDeliveryResult result) => new(result.Target,
        result.Succeeded, result.NeedsReview, Truncate(result.Detail, 300));

    private static MobilePolicyTargetResult ToMobileResult(StudentSystemPolicyDeliveryResult result) => new(result.Target,
        result.Succeeded, result.NeedsReview, Truncate(result.Detail, 300));

    private static string Summarize(IEnumerable<bool> success)
    {
        var results = success.ToArray();
        return $"{results.Count(value => value)}/{results.Length} responded";
    }

    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static async Task<T> ReadJson<T>(HttpRequest request, int maximumBytes, CancellationToken cancellationToken)
    {
        var mediaType = request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请求必须使用 application/json。");
        if (request.ContentLength is { } contentLength &&
            (contentLength > MaximumApiBodyBytes || contentLength > maximumBytes))
            throw new InvalidDataException("请求内容超过大小限制。");
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("请求内容超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        RejectDuplicateFields(bytes);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
            ?? throw new InvalidDataException("请求 JSON 为空。");
    }

    private static void RejectDuplicateFields(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        Visit(document.RootElement);
        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("请求 JSON 包含重复字段。");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Visit(child);
        }
    }

    private static Task WriteError(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.StatusCode = status;
        return WriteJson(context, new { error = message }, context.RequestAborted);
    }

    private static async Task WriteJson(HttpContext context, object value, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        await JsonSerializer.SerializeAsync(context.Response.Body, value, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ServeAsset(HttpContext context, string filename, string contentType)
    {
        var resourceName = "VeyonCampus.App.MobileWeb." + filename;
        await using var stream = typeof(TeacherMobileControlService).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.ContentType = contentType;
        context.Response.Headers["Cache-Control"] = "no-store";
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private bool IsBoundHost(string host) => IPAddress.TryParse(host, out var address) &&
        _identity.Addresses.Contains(address);

    private bool IsSameLan(IPAddress? remoteAddress)
    {
        if (remoteAddress is null) return false;
        var remote = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var item in adapter.GetIPProperties().UnicastAddresses)
            {
                if (!_identity.Addresses.Contains(item.Address) || item.IPv4Mask is not { } mask) continue;
                if (MobileControlLanNetworkPolicy.AreOnSameIpv4Subnet(item.Address, remote, mask)) return true;
            }
        }
        return false;
    }

    private bool HasSameOrigin(HttpRequest request)
    {
        var origin = request.Headers["Origin"].ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(parsed.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase) ||
            parsed.Port != _httpsPort || parsed.UserInfo.Length > 0 ||
            parsed.AbsolutePath != "/" || parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
            return false;
        return true;
    }

    private bool TryAppendAudit(MobileControlAuditEntry entry)
    {
        try
        {
            MobileControlAuditStore.Append(entry, _storageDirectory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          JsonException or ArgumentException)
        {
            return false;
        }
    }

    private sealed class MobileAuthorizationException(string message) : Exception(message);
    private sealed class MobileRateLimitException(string message) : Exception(message);
}
