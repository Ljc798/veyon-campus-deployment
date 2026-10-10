using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
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
    DateTimeOffset RequestedUtc)
{
    public string RequestedLocalTime => RequestedUtc.ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}
public sealed record MobileControlAuditView(DateTimeOffset TimeUtc, string Device, string Action,
    string Targets, string Outcome);
public sealed record MobileClassroomRestoreItem(string RoomName, string DeviceLabel, string Policy);
public sealed record MobileClassroomRestoreListResponse(int Count, IReadOnlyList<MobileClassroomRestoreItem> Items,
    int HiddenCount);
public sealed record MobileClassroomCountdownInfo(DateTimeOffset DeadlineUtc);
public sealed record MobileClassroomTaskProgressItem(string Title, bool IsCompleted);
public sealed record MobileClassroomTaskProgressInfo(int CompletedCount, int TotalCount,
    IReadOnlyList<MobileClassroomTaskProgressItem> Tasks);

internal sealed record MobilePairRequest(string PairingCode, string DeviceName);
internal sealed record MobilePairPollRequest(string Ticket);
internal sealed record MobilePairPendingResponse(string Ticket, DateTimeOffset ExpiresUtc);
internal sealed record MobilePairPollResponse(string State, string? AccessToken = null,
    MobilePairedDeviceView? Device = null);
internal sealed record MobileStatusRequest(IReadOnlyList<string> Targets, Guid? ProfileId = null);
internal sealed record MobilePolicyRequest(Guid ProfileId, IReadOnlyList<string> Targets, bool Enabled,
    string? ReviewToken = null);
internal sealed record MobileSessionResponse(MobilePairedDeviceView Device, string Status,
    IReadOnlyList<string> ActiveClassroomTargets, string? ClassroomMode = null,
    IReadOnlyList<ClassroomSeatLocation>? ActiveClassroomSeatLocations = null,
    MobileClassroomCountdownInfo? ActiveClassroomCountdown = null,
    MobileClassroomTaskProgressInfo? ActiveClassroomTaskProgress = null,
    Guid? ActiveClassroomSessionId = null);
internal sealed record MobileReviewGrant(Guid DeviceId, Guid ProfileId, string ProfileFingerprint,
    long AuditRevision, string AuditFingerprint, IReadOnlyList<string> Targets, DateTimeOffset ExpiresUtc,
    Guid? ClassroomSessionId = null, ClassroomMode? ClassroomMode = null);
internal sealed record MobileClassroomModeRequest(ClassroomMode? Mode, string? ReviewToken = null);
internal sealed record MobileClassroomEventReplyRequest(Guid HelpEventId, string Message);
internal sealed record MobileClassroomEventNoticeRequest(string Message);
internal sealed record MobileClassroomNoticeResponse(bool Accepted, int TargetCount);
internal sealed record MobileScreenPreviewSessionResponse(Guid LeaseId, DateTimeOffset ExpiresUtc,
    Guid SessionId, IReadOnlyList<string> Targets, int TotalTargets);
internal sealed record MobileScreenPreviewStopRequest(Guid LeaseId);
internal sealed record MobileClassroomEventPage(long Cursor, IReadOnlyList<string> Events, Guid? SessionId = null);
internal sealed record MobileStudentEventSubmitResponse(bool Accepted, bool Duplicate);
internal sealed record TeacherClassroomEventContext(string CampusId, Guid SessionId,
    IReadOnlyList<string> Targets, IReadOnlyDictionary<string, Guid> TargetIds,
    ClassroomMode Mode, ClassroomSession Session);

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
    public static string Create(string bootstrapUrl, string teacherUrl, string pairingCode)
    {
        if (!Uri.TryCreate(bootstrapUrl, UriKind.Absolute, out var bootstrap) ||
            bootstrap.Scheme != Uri.UriSchemeHttp || bootstrap.Port != TeacherMobileControlManager.BootstrapPort ||
            !IPAddress.TryParse(bootstrap.Host, out var bootstrapAddress) ||
            !MobileControlLanNetworkPolicy.IsPrivateIpv4Address(bootstrapAddress) ||
            bootstrap.AbsolutePath != "/" || bootstrap.Query.Length != 0 || bootstrap.Fragment.Length != 0 ||
            bootstrap.UserInfo.Length != 0)
            throw new InvalidDataException("二维码说明页必须是教师机当前的私有 IPv4 地址。");
        if (!Uri.TryCreate(teacherUrl, UriKind.Absolute, out var teacher) ||
            teacher.Scheme != Uri.UriSchemeHttps || teacher.Port != TeacherMobileControlManager.HttpsPort ||
            !IPAddress.TryParse(teacher.Host, out var teacherAddress) ||
            !bootstrapAddress.Equals(teacherAddress) || teacher.AbsolutePath != "/" ||
            teacher.Query.Length != 0 || teacher.Fragment.Length != 0 || teacher.UserInfo.Length != 0)
            throw new InvalidDataException("安全控制地址必须与二维码说明页使用同一个教师机 IPv4 地址。");
        if (pairingCode.Length != 8 || pairingCode.Any(character => character is < '0' or > '9'))
            throw new InvalidDataException("二维码配对码必须是 8 位数字。");

        var builder = new UriBuilder(bootstrap) { Fragment = "pair=" + pairingCode };
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
    private readonly SemaphoreSlim _classroomSyncGate = new(1, 1);
    private readonly TeacherMobileControlService _classroomPolicyExecutor;
    private TeacherMobileControlService? _service;
    private TeacherClassroomEventContext? _classroomContext;
    private bool _isRunning;
    private string _status = "手机控制服务未启动。";
    private string _lanUrl = "";
    private string _bootstrapUrl = "";
    private string _certificateFingerprint = "";
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
    private string _classroomEventStatus = "当前没有活动课堂。";

    public TeacherMobileControlManager(Func<string> activeCampusId, Action<Action> dispatchToUi)
    {
        _activeCampusId = activeCampusId ?? throw new ArgumentNullException(nameof(activeCampusId));
        _dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        _classroomPolicyExecutor = TeacherMobileControlService.CreatePolicyExecutor(CurrentCampusId);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public bool CanStart => !IsRunning;
    public bool CanStop => IsRunning && _classroomContext is null;
    public bool CanRevokeSelectedDevice => SelectedDevice is not null;
    public bool CanApproveSelectedPairing => SelectedPairing is not null;
    public bool CanRejectSelectedPairing => SelectedPairing is not null;
    public bool CanDeleteSelectedProfile => SelectedProfile is not null;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string LanUrl { get => _lanUrl; private set => Set(ref _lanUrl, value); }
    public string BootstrapUrl { get => _bootstrapUrl; private set => Set(ref _bootstrapUrl, value); }
    public string CertificateFingerprint { get => _certificateFingerprint; private set => Set(ref _certificateFingerprint, value); }
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
    public string ClassroomEventStatus
    {
        get => _classroomEventStatus;
        private set => Set(ref _classroomEventStatus, value);
    }
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
        await _classroomSyncGate.WaitAsync(cancellationToken);
        try
        {
            await StartServiceCoreAsync(cancellationToken);
            if (_classroomContext is not null) await ApplyClassroomContextAsync(cancellationToken);
        }
        finally { _classroomSyncGate.Release(); }
    }

    internal async Task SyncClassroomSessionAsync(TeacherClassroomEventContext? context,
        CancellationToken cancellationToken = default)
    {
        await _classroomSyncGate.WaitAsync(cancellationToken);
        try
        {
            if (context is null)
            {
                _classroomContext = null;
                _service?.SetClassroomSession(null, null, null);
                ClassroomEventStatus = "当前没有活动课堂。";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStop)));
                return;
            }

            WebsitePolicySigningKeyStore.ValidateCampusId(context.CampusId);
            if (context.SessionId == Guid.Empty) throw new InvalidDataException("活动课堂 session 无效。");
            _classroomContext = context with
            {
                Targets = Array.AsReadOnly(WebsitePolicyTransport.NormalizeTargets(context.Targets).ToArray())
            };
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStop)));
            if (!IsRunning) await StartServiceCoreAsync(cancellationToken);
            await ApplyClassroomContextAsync(cancellationToken);
        }
        finally { _classroomSyncGate.Release(); }
    }

    private async Task StartServiceCoreAsync(CancellationToken cancellationToken)
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
            var pairingAddress = SelectPreferredLanAddress(identity.Addresses);
            LanUrl = $"https://{pairingAddress}:{HttpsPort}/";
            BootstrapUrl = $"http://{pairingAddress}:{BootstrapPort}/";
            CertificateFingerprint = string.Join(" ", Enumerable.Range(0, identity.RootFingerprint.Length / 2)
                .Select(index => identity.RootFingerprint.Substring(index * 2, 2)));
            IsRunning = true;
            Status = "服务已启动。手机先安装并完全信任教师根证书，再打开 HTTPS 地址并点“配对这部手机”；扫码或安装证书本身不会让设备出现在待批准列表。只允许同一校园网访问。";
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
        await _classroomSyncGate.WaitAsync();
        try
        {
            if (_classroomContext is not null)
                throw new InvalidOperationException("课堂进行中，手机与学生求助通道会在下课后自动停止。");
            await StopServiceCoreAsync();
        }
        finally
        {
            _classroomSyncGate.Release();
        }
    }

    private async Task StopServiceCoreAsync()
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
            PendingPairings = Array.Empty<MobilePendingPairingView>();
            SelectedPairing = null;
            LanUrl = "";
            BootstrapUrl = "";
            CertificateFingerprint = "";
            Status = "手机控制服务已停止。";
        }
    }

    private async Task ApplyClassroomContextAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("课堂事件通道只支持 Windows TeacherConsole。");
        var context = _classroomContext;
        var service = _service;
        if (context is null || service is null)
        {
            ClassroomEventStatus = "当前没有活动课堂。";
            return;
        }

            service.SetClassroomSession(context.CampusId, context.SessionId, context.Targets,
                context.TargetIds, context.Mode, context.Session);
        using var signingKey = WebsitePolicySigningKeyStore.Open(context.CampusId);
        var trustStore = new StudentAgentIdentityTrustStore();
        var grants = new List<ClassroomEventGrantRequest>(context.Targets.Count);
        var unpinned = 0;
        foreach (var target in context.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (trustStore.FindTrustedPublicKey(context.CampusId, target) is null)
            {
                unpinned++;
                continue;
            }
            var teacherAddress = await SelectTeacherAddressAsync(target, service.EventTeacherAddresses,
                cancellationToken);
            var signedGrant = service.CreateStudentEventGrant(context.CampusId, context.SessionId, target,
                teacherAddress, signingKey.PrivateKey, DateTimeOffset.UtcNow);
            grants.Add(new ClassroomEventGrantRequest(target, signedGrant));
        }

        var results = await ClassroomEventGrantTransport.PushAsync(grants, context.CampusId,
            context.SessionId, signingKey.PublicKeyPem, trustStore, cancellationToken);
        var ready = results.Count(item => item.Succeeded);
        var notReady = context.Targets.Count - ready;
        ClassroomEventStatus = notReady == 0
            ? $"课堂求助已连接 {ready} 台学生电脑。"
            : $"课堂求助已连接 {ready}/{context.Targets.Count} 台；{unpinned} 台身份未固定，其余设备将在课堂刷新时自动重试。";
    }

    internal (Guid SessionId, ClassroomEventPage Page)? ReadClassroomEvents(long afterCursor)
    {
        var context = _classroomContext;
        var service = _service;
        if (context is null || service is null) return null;
        return (context.SessionId, service.ReadTeacherClassroomEvents(context.SessionId, afterCursor));
    }

    internal MobileStudentEventSubmitResponse ReplyToClassroomEvent(Guid helpEventId, string message)
    {
        var service = _service ?? throw new InvalidOperationException("课堂消息服务尚未启动。");
        return service.ReplyToStudentClassroomEvent(helpEventId, message);
    }

    internal MobileClassroomNoticeResponse SendClassroomNotice(string message)
    {
        var service = _service ?? throw new InvalidOperationException("课堂消息服务尚未启动。");
        return service.SendClassroomNotice(message);
    }

    [SupportedOSPlatform("windows")]
    internal Task<MobilePolicyOperationResponse> ApplyClassroomModeAsync(ClassroomMode mode,
        string? reviewToken = null, CancellationToken cancellationToken = default)
    {
        var service = _service ?? throw new InvalidOperationException("手机控制服务尚未启动。");
        return service.ApplyClassroomModeAsync(Guid.Empty, mode, reviewToken, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    internal Task<MobilePolicyOperationResponse> RetryClassroomRestoresAsync(
        CancellationToken cancellationToken = default) =>
        (_service ?? _classroomPolicyExecutor).RetryEndedClassroomRestoresAsync(cancellationToken);

    public void CreatePairingCode()
    {
        if (_service is null) throw new InvalidOperationException("请先启动手机控制服务。");
        var invitation = _service.CreatePairingInvitation();
        PairingCode = invitation.Code;
        PairingExpiry = $"有效至 {invitation.ExpiresUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
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

    public async ValueTask DisposeAsync()
    {
        await _classroomSyncGate.WaitAsync();
        try
        {
            _classroomContext = null;
            await StopServiceCoreAsync();
            await _classroomPolicyExecutor.DisposeAsync();
        }
        finally { _classroomSyncGate.Release(); }
    }

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
        PairingQrUrl = string.IsNullOrWhiteSpace(PairingCode) || string.IsNullOrWhiteSpace(LanUrl) ||
                       string.IsNullOrWhiteSpace(BootstrapUrl)
            ? ""
            : MobilePairingQrLink.Create(BootstrapUrl, LanUrl, PairingCode);
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

    private static IPAddress SelectPreferredLanAddress(IReadOnlyList<IPAddress> candidates)
    {
        var candidateSet = candidates.ToHashSet();
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or
                                  NetworkInterfaceType.Wireless80211)
            .Select(adapter => new
            {
                Adapter = adapter,
                Properties = adapter.GetIPProperties(),
                Addresses = adapter.GetIPProperties().UnicastAddresses
                    .Where(item => candidateSet.Contains(item.Address)).Select(item => item.Address).ToArray()
            })
            .Where(item => item.Addresses.Length > 0)
            .ToArray();
        var defaultRoute = adapters
            .Where(item => item.Properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any)))
            .OrderByDescending(item => item.Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .FirstOrDefault();
        return defaultRoute?.Addresses[0] ?? candidates.FirstOrDefault() ??
            throw new InvalidOperationException("当前没有可用于手机连接的教师 LAN 地址。");
    }

    private static async Task<IPAddress> SelectTeacherAddressAsync(string target,
        IReadOnlyList<IPAddress> candidates, CancellationToken cancellationToken)
    {
        IPAddress[] targetAddresses;
        if (IPAddress.TryParse(target, out var parsedTarget)) targetAddresses = [parsedTarget];
        else
        {
            try { targetAddresses = await Dns.GetHostAddressesAsync(target, cancellationToken).ConfigureAwait(false); }
            catch (SocketException) { targetAddresses = Array.Empty<IPAddress>(); }
        }

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var item in adapter.GetIPProperties().UnicastAddresses)
            {
                if (!candidates.Contains(item.Address) || item.IPv4Mask is not { } mask) continue;
                if (targetAddresses.Any(address => MobileControlLanNetworkPolicy.AreOnSameIpv4Subnet(
                        item.Address, address, mask))) return item.Address;
            }
        }

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            var hasDefaultGateway = adapter.GetIPProperties().GatewayAddresses.Any(item =>
                item.Address.AddressFamily == AddressFamily.InterNetwork &&
                !item.Address.Equals(IPAddress.Any));
            if (!hasDefaultGateway) continue;
            var address = adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(item =>
                candidates.Contains(item.Address));
            if (address is not null) return address.Address;
        }
        return candidates.FirstOrDefault() ?? throw new InvalidOperationException("没有可用于课堂事件通道的教师 LAN 地址。");
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

internal sealed partial class TeacherMobileControlService : IAsyncDisposable
{
    private const int MaximumApiBodyBytes = 256 * 1024;
    private static readonly TimeSpan PairingRequestLifetime = TimeSpan.FromMinutes(2);
    private static readonly string[] RequestTimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffK",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "O"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private readonly MobileControlTlsIdentity? _identity;
    private readonly Action _stateChanged;
    private readonly Func<string> _activeCampusId;
    private readonly string? _storageDirectory;
    private readonly StudentAgentIdentityTrustStore _agentTrustStore;
    private readonly Func<string, WebsitePolicySigningKey> _openTeacherSigningKey;
    private readonly int _httpsPort;
    private readonly int _bootstrapPort;
    private readonly object _pairingGate = new();
    private readonly Dictionary<string, (DateTimeOffset StartedUtc, int Failures)> _pairingFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, PendingPairing> _pendingPairings = [];
    private readonly ConcurrentDictionary<string, MobileReviewGrant> _reviewGrants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _requestNonces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, RequestRateWindow> _requestRates = new();
    private readonly ConcurrentDictionary<Guid, RequestRateWindow> _screenPreviewRequestRates = new();
    private readonly ConcurrentDictionary<string, RequestRateWindow> _studentEventRates = new(StringComparer.Ordinal);
    private readonly ClassroomEventBuffer _classroomEvents = new();
    private readonly Dictionary<string, StudentEventAccessGrant> _studentEventGrants = new(StringComparer.Ordinal);
    private readonly object _classroomGate = new();
    private readonly object _screenPreviewGate = new();
    private readonly Dictionary<Guid, ScreenPreviewLease> _screenPreviewLeases = [];
    private readonly RequestRateWindow _screenPreviewGlobalRate = new();
    private readonly ClassroomScreenPreviewCapture _screenPreviewCapture;
    private CancellationTokenSource _classroomSessionChanged = new();
    private string? _classroomCampusId;
    private Guid? _classroomSessionId;
    private IReadOnlySet<string> _classroomTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastClassroomNoticeUtc = DateTimeOffset.MinValue;
    private WebApplication? _application;
    private byte[]? _pairingCodeHash;
    private DateTimeOffset _pairingExpiresUtc;
    private bool _pairingConsumed;
    private int _pairingFailureCount;
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    private readonly ClassroomModeStateStore _classroomModeStateStore;
    private readonly ClassroomSessionStore _classroomSessionStore;
    private readonly ClassroomSeatLayoutStore _classroomSeatLayoutStore;
    private readonly ClassroomCountdownStore _classroomCountdownStore;
    private readonly ClassroomTaskProgressStore _classroomTaskProgressStore;
    private readonly TeacherCampusDirectoryStore _campusDirectoryStore;
    private readonly ClassroomSigningContextStore _classroomSigningContextStore;
    private IReadOnlyDictionary<string, Guid> _classroomTargetIds =
        new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
    private ClassroomSession? _classroomSession;

    private sealed record PendingPairing(Guid Id, string DeviceName, string SourceAddress,
        string TicketSha256, DateTimeOffset RequestedUtc, DateTimeOffset ExpiresUtc,
        string? ApprovedAccessToken = null, bool Rejected = false,
        MobilePairedDeviceView? ApprovedDevice = null);
    private sealed record StudentEventAccessGrant(string CampusId, Guid SessionId, string Target,
        string AgentPublicKeyPem, string AgentFingerprint, DateTimeOffset ExpiresUtc);
    private sealed record ScreenPreviewLease(Guid LeaseId, Guid DeviceId, Guid SessionId,
        IReadOnlySet<string> Targets, DateTimeOffset ExpiresUtc);
    private sealed class RequestRateWindow
    {
        public readonly object Gate = new();
        public DateTimeOffset StartedUtc = DateTimeOffset.UtcNow;
        public int Count;
    }

    public TeacherMobileControlService(MobileControlTlsIdentity identity, Action paired,
        Func<string> activeCampusId, string? storageDirectory = null,
        int httpsPort = TeacherMobileControlManager.HttpsPort,
        int bootstrapPort = TeacherMobileControlManager.BootstrapPort,
        StudentAgentIdentityTrustStore? agentTrustStore = null,
        Func<string, WebsitePolicySigningKey>? openTeacherSigningKey = null,
        Func<string, CancellationToken, Task<byte[]>>? captureScreenAsync = null)
    {
        if (httpsPort is < 0 or > 65535 || bootstrapPort is < 0 or > 65535 || httpsPort == bootstrapPort)
            throw new ArgumentOutOfRangeException(nameof(httpsPort), "手机控制服务端口无效。");
        _identity = identity;
        _stateChanged = paired;
        _activeCampusId = activeCampusId;
        _storageDirectory = storageDirectory is null ? null : Path.GetFullPath(storageDirectory);
        _agentTrustStore = agentTrustStore ?? new StudentAgentIdentityTrustStore();
        _openTeacherSigningKey = openTeacherSigningKey ?? OpenTeacherSigningKey;
        _screenPreviewCapture = new ClassroomScreenPreviewCapture(captureScreenAsync);
        _classroomModeStateStore = new ClassroomModeStateStore(_storageDirectory is null ? null :
            Path.Combine(_storageDirectory, "classroom-mode-state.json"));
        _classroomSessionStore = new ClassroomSessionStore(_storageDirectory is null ? null :
            Path.Combine(_storageDirectory, "classroom-sessions.json"));
        _classroomSeatLayoutStore = new ClassroomSeatLayoutStore(_storageDirectory is null ? null :
            Path.Combine(_storageDirectory, "classroom-seat-layouts.json"));
        _classroomCountdownStore = new ClassroomCountdownStore(_storageDirectory is null ? null :
            Path.Combine(_storageDirectory, "classroom-countdown.json"));
        _classroomTaskProgressStore = new ClassroomTaskProgressStore(_storageDirectory is null ? null :
            Path.Combine(_storageDirectory, "classroom-task-progress.json"));
        _campusDirectoryStore = new TeacherCampusDirectoryStore(_storageDirectory is null ? null :
            Path.Combine(_storageDirectory, "campus-directory.json"));
        _classroomSigningContextStore = new ClassroomSigningContextStore(_storageDirectory is null ? null :
            Path.Combine(_storageDirectory, "classroom-signing-context.json"));
        _httpsPort = httpsPort;
        _bootstrapPort = bootstrapPort;
    }

    private TeacherMobileControlService(Func<string> activeCampusId)
    {
        _identity = null;
        _stateChanged = static () => { };
        _activeCampusId = activeCampusId ?? throw new ArgumentNullException(nameof(activeCampusId));
        _agentTrustStore = new StudentAgentIdentityTrustStore();
        _openTeacherSigningKey = OpenTeacherSigningKey;
        _screenPreviewCapture = new ClassroomScreenPreviewCapture();
        _classroomModeStateStore = new ClassroomModeStateStore();
        _classroomSessionStore = new ClassroomSessionStore();
        _classroomSeatLayoutStore = new ClassroomSeatLayoutStore();
        _classroomCountdownStore = new ClassroomCountdownStore();
        _classroomTaskProgressStore = new ClassroomTaskProgressStore();
        _campusDirectoryStore = new TeacherCampusDirectoryStore();
        _classroomSigningContextStore = new ClassroomSigningContextStore();
        _httpsPort = TeacherMobileControlManager.HttpsPort;
        _bootstrapPort = TeacherMobileControlManager.BootstrapPort;
    }

    internal static TeacherMobileControlService CreatePolicyExecutor(Func<string> activeCampusId) =>
        new(activeCampusId);

    public void SetClassroomSession(string? campusId, Guid? sessionId, IEnumerable<string>? targets,
        IReadOnlyDictionary<string, Guid>? targetIds = null, ClassroomMode mode = ClassroomMode.Normal,
        ClassroomSession? session = null)
    {
        IReadOnlySet<string> normalizedTargets;
        IReadOnlyDictionary<string, Guid> normalizedTargetIds;
        if (sessionId is { } activeSession)
        {
            if (activeSession == Guid.Empty || campusId is null)
                throw new InvalidDataException("活动课堂校区或 session 无效。");
            WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
            normalizedTargets = WebsitePolicyTransport.NormalizeTargets(targets ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!Enum.IsDefined(mode) || session is null || session.SessionId != activeSession ||
                session.Status != ClassroomSessionStatus.Active)
                throw new InvalidDataException("活动课堂模式或会话快照无效。");
            var suppliedIds = targetIds ?? throw new InvalidDataException("活动课堂缺少稳定目标 ID 映射。");
            if (suppliedIds.Count != normalizedTargets.Count || suppliedIds.Any(pair =>
                    !normalizedTargets.Contains(pair.Key) || pair.Value == Guid.Empty) ||
                suppliedIds.Values.Distinct().Count() != suppliedIds.Count)
                throw new InvalidDataException("活动课堂目标 ID 映射不完整或重复。");
            normalizedTargetIds = new Dictionary<string, Guid>(suppliedIds, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            if (campusId is not null || targets?.Any() == true || targetIds?.Any() == true || session is not null)
                throw new InvalidDataException("结束课堂不能保留校区或目标电脑。");
            normalizedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            normalizedTargetIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        }

        CancellationTokenSource? previousCancellation = null;
        Guid? previousSession = null;
        var sessionChanged = false;
        lock (_classroomGate)
        {
            var changed = _classroomSessionId != sessionId || _classroomCampusId != campusId;
            if (changed)
            {
                sessionChanged = true;
                previousSession = _classroomSessionId;
                previousCancellation = _classroomSessionChanged;
                _classroomSessionChanged = new CancellationTokenSource();
                _studentEventGrants.Clear();
                _studentEventRates.Clear();
                _lastClassroomNoticeUtc = DateTimeOffset.MinValue;
            }
            else
            {
                foreach (var key in _studentEventGrants.Where(item => !normalizedTargets.Contains(item.Value.Target))
                             .Select(item => item.Key).ToArray())
                    _studentEventGrants.Remove(key);
            }
            _classroomCampusId = campusId;
            _classroomSessionId = sessionId;
            _classroomTargets = normalizedTargets;
            _classroomTargetIds = normalizedTargetIds;
            _classroomSession = session;
        }
        if (previousSession is { } oldSession) _classroomEvents.ClearSession(oldSession);
        if (sessionChanged)
        {
            EndScreenPreviewLeases(previousSession, "课堂结束或已切换");
            _screenPreviewCapture.SetSession(sessionId);
        }
        if (previousCancellation is not null)
        {
            try { previousCancellation.Cancel(); }
            finally { previousCancellation.Dispose(); }
        }
    }

    public IReadOnlyList<IPAddress> EventTeacherAddresses => RequireIdentity().Addresses;

    public string CreateStudentEventGrant(string campusId, Guid sessionId, string target,
        IPAddress teacherAddress, RSA teacherPrivateKey, DateTimeOffset nowUtc)
    {
        var normalizedTarget = ClassroomEventCryptography.NormalizeSingleTarget(target);
        var pinnedAgentKey = _agentTrustStore.FindTrustedPublicKey(campusId, normalizedTarget)
                             ?? throw new InvalidDataException("该学生电脑的 Agent 身份尚未固定，不能开放课堂事件通道。");
        var fingerprint = StudentAgentResponseCryptography.GetFingerprint(pinnedAgentKey);
        var now = nowUtc.ToUniversalTime();
        var accessToken = ClassroomEventCryptography.CreateAccessToken();
        var grant = new ClassroomEventAccessGrant(1, ClassroomEventCryptography.GrantPurpose, campusId,
            Guid.NewGuid(), sessionId, normalizedTarget, ClassroomEventCryptography.CreateTeacherEndpoint(teacherAddress),
            Convert.ToHexString(SHA256.HashData(RequireIdentity().Server.RawData)), accessToken, now,
            now.Add(ClassroomEventCryptography.MaximumGrantLifetime));
        var signedGrant = ClassroomEventCryptography.SignGrant(grant, teacherPrivateKey);
        lock (_classroomGate)
        {
            if (_classroomCampusId != campusId || _classroomSessionId != sessionId ||
                !_classroomTargets.Contains(normalizedTarget))
                throw new InvalidDataException("课堂已结束或该电脑不属于当前课堂目标。");
            foreach (var expired in _studentEventGrants.Where(item => item.Value.ExpiresUtc <= now)
                         .Select(item => item.Key).ToArray())
                _studentEventGrants.Remove(expired);
            if (_studentEventGrants.Count >= ClassroomSession.MaximumTargets * 5)
                throw new InvalidDataException("课堂事件授权达到临时容量上限；请稍后刷新课堂状态。");
            var tokenHash = MobilePairedDeviceStore.HashToken(accessToken);
            _studentEventGrants.Add(tokenHash, new StudentEventAccessGrant(campusId, sessionId,
                normalizedTarget, pinnedAgentKey, fingerprint, grant.ExpiresUtc));
        }
        return signedGrant;
    }

    public ClassroomEventPage ReadTeacherClassroomEvents(Guid sessionId, long afterCursor)
    {
        lock (_classroomGate)
            if (_classroomSessionId != sessionId)
                return new ClassroomEventPage(afterCursor, Array.Empty<ClassroomEvent>(), Array.Empty<string?>());
        return _classroomEvents.ReadAfter(sessionId, null, afterCursor, ClassroomEventBuffer.MaximumPageSize,
            DateTimeOffset.UtcNow);
    }

    public MobileStudentEventSubmitResponse ReplyToStudentClassroomEvent(Guid helpEventId, string message)
    {
        if (helpEventId == Guid.Empty || string.IsNullOrWhiteSpace(message) ||
            message.Length > ClassroomEventCryptography.MaximumMessageCharacters || message.Any(char.IsControl))
            throw new InvalidDataException("课堂回复 ID 或正文无效。");
        var active = ReadActiveClassroom();
        var now = DateTimeOffset.UtcNow;
        var original = _classroomEvents.Find(active.SessionId, helpEventId, now);
        if (original is null || original.Sender != ClassroomEventSender.Student ||
            original.Type != ClassroomEventType.HelpRequested || !active.Targets.Contains(original.Target))
            throw new InvalidDataException("求助事件已过期、不属于当前课堂或目标不在本堂课中。");
        using var signingKey = _openTeacherSigningKey(active.CampusId);
        var reply = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, active.CampusId,
            active.SessionId, Guid.NewGuid(), original.Target, ClassroomEventSender.Teacher,
            ClassroomEventType.TeacherReply, now, now.Add(ClassroomEventCryptography.MaximumEventLifetime),
            null, message.Trim(), original.EventId);
        var signedReply = ClassroomEventCryptography.SignEvent(reply, signingKey.PrivateKey);
        var accepted = _classroomEvents.Append(reply, now, signedReply);
        return new MobileStudentEventSubmitResponse(true, !accepted);
    }

    public MobileClassroomNoticeResponse SendClassroomNotice(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > ClassroomEventCryptography.MaximumMessageCharacters ||
            message.Any(character => char.IsControl(character) && character is not '\n' and not '\t'))
            throw new InvalidDataException("课堂通知须为 1–500 个字符，且不能包含不可见控制字符。");

        var active = ReadActiveClassroom();
        using var signingKey = _openTeacherSigningKey(active.CampusId);
        var now = DateTimeOffset.UtcNow;
        var notice = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, active.CampusId,
            active.SessionId, Guid.NewGuid(), ClassroomEventCryptography.ClassroomNoticeTarget,
            ClassroomEventSender.Teacher, ClassroomEventType.ClassroomNotice, now,
            now.Add(ClassroomEventCryptography.MaximumEventLifetime), null, message.Trim(), null);
        var signedNotice = ClassroomEventCryptography.SignEvent(notice, signingKey.PrivateKey);
        lock (_classroomGate)
        {
            if (_classroomCampusId != active.CampusId || _classroomSessionId != active.SessionId ||
                _classroomTargets.Count == 0)
                throw new MobileAuthorizationException("课堂已结束或目标已改变；通知未发送。");
            if (now - _lastClassroomNoticeUtc < TimeSpan.FromSeconds(5))
                throw new MobileRateLimitException("请稍候再发送下一条课堂通知。");
            _classroomEvents.Append(notice, now, signedNotice);
            _lastClassroomNoticeUtc = now;
            return new MobileClassroomNoticeResponse(true, _classroomTargets.Count);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var identity = RequireIdentity();
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
            foreach (var address in identity.Addresses)
            {
                options.Listen(address, _httpsPort,
                    endpoint => endpoint.UseHttps(identity.Server));
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
            CancellationTokenSource classroomCancellation;
            lock (_classroomGate)
            {
                classroomCancellation = _classroomSessionChanged;
                _studentEventGrants.Clear();
                _classroomCampusId = null;
                _classroomSessionId = null;
                _classroomTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            try { classroomCancellation.Cancel(); }
            finally { classroomCancellation.Dispose(); }
            EndScreenPreviewLeases(null, "手机控制服务已停止");
            _screenPreviewCapture.SetSession(null);
            _identity?.Dispose();
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
            if (context.Request.Path.StartsWithSegments("/api"))
                context.Response.Headers["X-Veyon-Server-Time"] = DateTimeOffset.UtcNow.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
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
            var activeClassroomTargets = ReadActiveClassroomTargets(CurrentCampusId());
            var mode = ReadActiveClassroomMode(CurrentCampusId());
            var seatLocations = ReadActiveClassroomSeatLocations(CurrentCampusId());
            var countdown = ReadActiveClassroomCountdown(CurrentCampusId());
            var taskProgress = ReadActiveClassroomTaskProgress(CurrentCampusId());
            await WriteJson(context, new MobileSessionResponse(device, "已连接教师控制台。", activeClassroomTargets,
                    mode?.ToString().ToLowerInvariant(), seatLocations, countdown, taskProgress,
                    ReadActiveClassroomSessionId(CurrentCampusId())),
                    context.RequestAborted)
                .ConfigureAwait(false);
        });
        app.MapPost("/api/classroom/screen-preview/start", async context =>
        {
            var device = Authorize(context);
            var result = StartScreenPreview(device);
            await WriteJson(context, result, context.RequestAborted).ConfigureAwait(false);
        });
        app.MapPost("/api/classroom/screen-preview/stop", async context =>
        {
            var device = Authorize(context);
            var request = await ReadJson<MobileScreenPreviewStopRequest>(context.Request, 4096,
                context.RequestAborted).ConfigureAwait(false);
            var stopped = StopScreenPreview(device, request.LeaseId, "已由教师手机停止");
            await WriteJson(context, new { stopped }, context.RequestAborted).ConfigureAwait(false);
        });
        app.MapGet("/api/classroom/screen-preview/{target}", async context =>
        {
            var device = Authorize(context, enforceGeneralRate: false);
            if (!AcceptScreenPreviewRate(device.Id))
                throw new MobileRateLimitException("屏幕预览刷新较快；请稍后再试。");
            var target = VeyonHostAddress.NormalizeOverride(context.Request.RouteValues["target"]?.ToString());
            var active = ReadActiveClassroom();
            var leaseIdText = context.Request.Headers["X-Veyon-Screen-Preview-Lease"].ToString();
            if (!Guid.TryParseExact(leaseIdText, "N", out var leaseId) || leaseId == Guid.Empty)
                throw new MobileAuthorizationException("屏幕巡视授权已失效；请重新打开巡视。");
            RenewScreenPreviewLease(device.Id, leaseId, active.SessionId, target, active.Targets);
            var snapshot = await _screenPreviewCapture.GetAsync(active.SessionId, target,
                context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "image/png";
            context.Response.ContentLength = snapshot.Png.Length;
            context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            context.Response.Headers["X-Captured-Utc"] = snapshot.CapturedUtc.ToString("O",
                System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-Screen-Frame"] = snapshot.WasCaptured ? "captured" : "cached";
            await context.Response.Body.WriteAsync(snapshot.Png, context.RequestAborted).ConfigureAwait(false);
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
        app.MapPost("/api/classroom/mode", async context =>
        {
            if (!OperatingSystem.IsWindows())
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                return;
            }
            var device = Authorize(context);
            var request = await ReadJson<MobileClassroomModeRequest>(context.Request, 16 * 1024,
                context.RequestAborted).ConfigureAwait(false);
            if (request.Mode is not { } mode || !Enum.IsDefined(mode))
                throw new InvalidDataException("课堂模式缺失或无效。");
            var response = await ApplyClassroomModeAsync(device.Id, mode, request.ReviewToken,
                context.RequestAborted).ConfigureAwait(false);
            var audit = response.Results.Select(item => new MobileControlAuditTargetResult(item.Target,
                item.NeedsReview ? "needs-review" : item.AgentAccepted ? "agent-accepted" : "failed")).ToArray();
            var saved = TryAppendAudit(new MobileControlAuditEntry(DateTimeOffset.UtcNow, device.Id,
                mode == ClassroomMode.Practice ? "classroom-practice" : "classroom-normal",
                null, response.Results.Select(item => item.Target).ToArray(), response.State, audit));
            if (!saved) response = response with { Message = response.Message + " 本机操作日志未保存。" };
            await WriteJson(context, response, context.RequestAborted).ConfigureAwait(false);
        });
        app.MapGet("/api/classroom/restores", async context =>
        {
            _ = Authorize(context);
            await WriteJson(context, ListEndedClassroomRestores(), context.RequestAborted).ConfigureAwait(false);
        });
        app.MapPost("/api/classroom/restores/retry", async context =>
        {
            if (!OperatingSystem.IsWindows())
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                return;
            }
            var device = Authorize(context);
            try
            {
                var response = await RetryEndedClassroomRestoresAsync(context.RequestAborted).ConfigureAwait(false);
                var audit = response.Results.Select(item => new MobileControlAuditTargetResult(item.Target,
                    item.NeedsReview ? "needs-review" : item.AgentAccepted ? "restored" : "failed")).ToArray();
                var saved = TryAppendAudit(new MobileControlAuditEntry(DateTimeOffset.UtcNow, device.Id,
                    "classroom-restore", null, response.Results.Select(item => item.Target).ToArray(),
                    response.State, audit));
                if (!saved) response = response with { Message = response.Message + " 本机操作日志未保存。" };
                await WriteJson(context, response, context.RequestAborted).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                await WriteError(context, StatusCodes.Status409Conflict, exception.Message).ConfigureAwait(false);
            }
        });
        app.MapPost("/api/classroom/events/student", async context =>
            await SubmitStudentClassroomEventAsync(context).ConfigureAwait(false));
        app.MapGet("/api/classroom/events/student", async context =>
            await ReadStudentClassroomEventsAsync(context).ConfigureAwait(false));
        app.MapGet("/api/classroom/events", async context =>
            await ReadMobileClassroomEventsAsync(context).ConfigureAwait(false));
        app.MapPost("/api/classroom/events/reply", async context =>
            await ReplyToStudentClassroomEventAsync(context).ConfigureAwait(false));
        app.MapPost("/api/classroom/events/notice", async context =>
            await SendClassroomNoticeAsync(context).ConfigureAwait(false));
    }

    private MobileScreenPreviewSessionResponse StartScreenPreview(MobilePairedDeviceView device)
    {
        var active = ReadActiveClassroom();
        if (active.Targets.Count == 0)
            throw new MobileAuthorizationException("当前课堂没有可预览的电脑。");
        var targets = active.Targets.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .Take(ClassroomScreenPreviewCapture.MaximumPreviewTargets).ToArray();
        var now = DateTimeOffset.UtcNow;
        var leaseId = Guid.NewGuid();
        var lease = new ScreenPreviewLease(leaseId, device.Id, active.SessionId,
            targets.ToHashSet(StringComparer.OrdinalIgnoreCase), now.AddSeconds(60));
        ScreenPreviewLease? previous = null;
        lock (_screenPreviewGate)
        {
            if (_screenPreviewLeases.Remove(device.Id, out var existing)) previous = existing;
            _screenPreviewLeases[device.Id] = lease;
        }
        if (previous is not null)
            _ = TryAppendAudit(new MobileControlAuditEntry(now, device.Id, "classroom-screen-preview-stop", null,
                previous.Targets.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
                "同一手机重新打开巡视"));
        if (!TryAppendAudit(new MobileControlAuditEntry(now, device.Id, "classroom-screen-preview-start", null,
                targets, $"已开启；最多显示 5 台，闲置 60 秒后授权到期；课堂共有 {active.Targets.Count} 台。")))
        {
            lock (_screenPreviewGate)
            {
                if (_screenPreviewLeases.TryGetValue(device.Id, out var current) && current.LeaseId == leaseId)
                    _screenPreviewLeases.Remove(device.Id);
            }
            throw new InvalidOperationException("无法保存屏幕巡视审计记录；本次没有开放画面。");
        }
        return new MobileScreenPreviewSessionResponse(leaseId, lease.ExpiresUtc, active.SessionId,
            Array.AsReadOnly(targets), active.Targets.Count);
    }

    private bool StopScreenPreview(MobilePairedDeviceView device, Guid leaseId, string outcome)
    {
        if (leaseId == Guid.Empty) throw new InvalidDataException("屏幕巡视授权标识无效。");
        ScreenPreviewLease? removed = null;
        var clearSnapshots = false;
        lock (_screenPreviewGate)
        {
            if (_screenPreviewLeases.TryGetValue(device.Id, out var existing) && existing.LeaseId == leaseId)
            {
                _screenPreviewLeases.Remove(device.Id);
                removed = existing;
                clearSnapshots = !_screenPreviewLeases.Values.Any(item => item.SessionId == existing.SessionId &&
                    item.ExpiresUtc > DateTimeOffset.UtcNow);
            }
        }
        if (removed is null) return false;
        if (clearSnapshots) _screenPreviewCapture.ClearSnapshots(removed.SessionId);
        _ = TryAppendAudit(new MobileControlAuditEntry(DateTimeOffset.UtcNow, device.Id,
            "classroom-screen-preview-stop", null,
            removed.Targets.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(), outcome));
        return true;
    }

    private void RenewScreenPreviewLease(Guid deviceId, Guid leaseId, Guid sessionId, string target,
        IReadOnlySet<string> activeTargets)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_screenPreviewGate)
        {
            if (!_screenPreviewLeases.TryGetValue(deviceId, out var lease) || lease.LeaseId != leaseId ||
                lease.SessionId != sessionId || lease.ExpiresUtc <= now || !lease.Targets.Contains(target) ||
                !activeTargets.Contains(target))
            {
                if (_screenPreviewLeases.TryGetValue(deviceId, out var expired) && expired.ExpiresUtc <= now)
                    _screenPreviewLeases.Remove(deviceId);
                throw new MobileAuthorizationException("屏幕巡视已停止或课堂目标已改变；请重新打开巡视。");
            }
            _screenPreviewLeases[deviceId] = lease with { ExpiresUtc = now.AddSeconds(60) };
        }
    }

    private void EndScreenPreviewLeases(Guid? sessionId, string outcome)
    {
        KeyValuePair<Guid, ScreenPreviewLease>[] ended;
        lock (_screenPreviewGate)
        {
            ended = _screenPreviewLeases.Where(item => sessionId is null || item.Value.SessionId == sessionId)
                .ToArray();
            foreach (var item in ended) _screenPreviewLeases.Remove(item.Key);
        }
        foreach (var item in ended)
        {
            _screenPreviewCapture.ClearSnapshots(item.Value.SessionId);
            _ = TryAppendAudit(new MobileControlAuditEntry(DateTimeOffset.UtcNow, item.Key,
                "classroom-screen-preview-stop", null,
                item.Value.Targets.OrderBy(target => target, StringComparer.OrdinalIgnoreCase).ToArray(), outcome));
        }
    }

    private async Task SubmitStudentClassroomEventAsync(HttpContext context)
    {
        var grant = AuthorizeStudentEventAccess(context);
        if (!AcceptStudentEventRate(grant.Target))
            throw new MobileRateLimitException("课堂事件发送过于频繁；请稍后重试。");
        var signedJson = await ReadBoundedTextAsync(context.Request, ClassroomEventCryptography.MaximumEventEnvelopeBytes,
            context.RequestAborted).ConfigureAwait(false);
        var verified = ClassroomEventCryptography.VerifyEvent(signedJson, grant.CampusId, grant.SessionId,
            grant.Target, ClassroomEventSender.Student, grant.AgentPublicKeyPem, DateTimeOffset.UtcNow);
        if (!verified.MatchesPinnedKey || verified.Fingerprint != grant.AgentFingerprint)
            throw new MobileAuthorizationException("学生 Agent 身份与本堂课固定身份不匹配。");
        var active = ReadActiveClassroom();
        if (active.CampusId != grant.CampusId || active.SessionId != grant.SessionId ||
            !active.Targets.Contains(grant.Target))
            throw new MobileAuthorizationException("本堂课已结束或学生设备不属于当前课堂。");

        if (verified.Event.Type == ClassroomEventType.HelpResolved)
        {
            var helpEventId = verified.Event.CorrelationId
                              ?? throw new InvalidDataException("解决事件缺少原始求助 ID。");
            var original = _classroomEvents.FindRetained(grant.SessionId, helpEventId, DateTimeOffset.UtcNow);
            if (original is null || original.Type != ClassroomEventType.HelpRequested ||
                original.Sender != ClassroomEventSender.Student ||
                !string.Equals(original.Target, grant.Target, StringComparison.OrdinalIgnoreCase) ||
                !_classroomEvents.HasCorrelatedEvent(grant.SessionId, helpEventId,
                    ClassroomEventType.TeacherReply, ClassroomEventSender.Teacher, grant.Target,
                    DateTimeOffset.UtcNow))
                throw new InvalidDataException("只能确认同一设备已收到教师回复的课堂求助。");
        }
        else if (verified.Event.Type != ClassroomEventType.HelpRequested)
        {
            throw new InvalidDataException("Student Agent 不能提交该课堂事件类型。");
        }

        var accepted = _classroomEvents.Append(verified.Event, DateTimeOffset.UtcNow, signedJson);
        await WriteJson(context, new MobileStudentEventSubmitResponse(true, !accepted), context.RequestAborted)
            .ConfigureAwait(false);
    }

    private async Task ReadStudentClassroomEventsAsync(HttpContext context)
    {
        var grant = AuthorizeStudentEventAccess(context);
        var after = ReadEventCursor(context.Request);
        var page = await WaitForClassroomEventsAsync(grant.SessionId, grant.Target, after,
            grant.CampusId, context.RequestAborted).ConfigureAwait(false);
        await WriteSignedClassroomPageAsync(context, page, null, context.RequestAborted)
            .ConfigureAwait(false);
    }

    private async Task ReadMobileClassroomEventsAsync(HttpContext context)
    {
        _ = Authorize(context);
        var after = ReadEventCursor(context.Request);
        var active = ReadActiveClassroomIfAny();
        if (active is null)
        {
            await WriteJson(context, new MobileClassroomEventPage(after, Array.Empty<string>()),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }
        var page = await WaitForClassroomEventsAsync(active.Value.SessionId, null, after,
            active.Value.CampusId, context.RequestAborted).ConfigureAwait(false);
        await WriteSignedClassroomPageAsync(context, page, active.Value.SessionId, context.RequestAborted)
            .ConfigureAwait(false);
    }

    private async Task ReplyToStudentClassroomEventAsync(HttpContext context)
    {
        _ = Authorize(context);
        var request = await ReadJson<MobileClassroomEventReplyRequest>(context.Request, 4096,
            context.RequestAborted).ConfigureAwait(false);
        var response = ReplyToStudentClassroomEvent(request.HelpEventId, request.Message);
        await WriteJson(context, response, context.RequestAborted)
            .ConfigureAwait(false);
    }

    private async Task SendClassroomNoticeAsync(HttpContext context)
    {
        _ = Authorize(context);
        var request = await ReadJson<MobileClassroomEventNoticeRequest>(context.Request, 4096,
            context.RequestAborted).ConfigureAwait(false);
        var response = SendClassroomNotice(request.Message);
        await WriteJson(context, response, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task<ClassroomEventPage> WaitForClassroomEventsAsync(Guid sessionId, string? target,
        long afterCursor, string campusId, CancellationToken requestAborted)
    {
        CancellationToken sessionChanged;
        lock (_classroomGate)
        {
            if (_classroomSessionId != sessionId || _classroomCampusId != campusId)
                throw new MobileAuthorizationException("本堂课已结束或状态已改变。");
            sessionChanged = _classroomSessionChanged.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, sessionChanged);
        try
        {
            var page = await _classroomEvents.WaitForEventsAsync(sessionId, target, afterCursor,
                ClassroomEventBuffer.MaximumPageSize, ClassroomEventBuffer.MaximumWait,
                DateTimeOffset.UtcNow, linked.Token).ConfigureAwait(false);
            lock (_classroomGate)
                if (_classroomSessionId != sessionId || _classroomCampusId != campusId)
                    throw new MobileAuthorizationException("本堂课已结束或状态已改变。");
            return page;
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested && sessionChanged.IsCancellationRequested)
        {
            throw new MobileAuthorizationException("本堂课已结束或状态已改变。");
        }
    }

    private async Task WriteSignedClassroomPageAsync(HttpContext context, ClassroomEventPage page,
        Guid? sessionId, CancellationToken cancellationToken)
    {
        if (page.SignedEnvelopes.Count != page.Events.Count || page.SignedEnvelopes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("课堂事件缺少原始签名封装，无法安全转发。");
        var envelopes = page.SignedEnvelopes.Select(item => item!).ToArray();
        object response = sessionId is { } activeSession
            ? new MobileClassroomEventPage(page.Cursor, envelopes, activeSession)
            : new ClassroomEventRemotePage(page.Cursor, envelopes);
        await WriteJson(context, response, cancellationToken)
            .ConfigureAwait(false);
    }

    private (string CampusId, Guid SessionId, IReadOnlySet<string> Targets)? ReadActiveClassroomIfAny()
    {
        lock (_classroomGate)
        {
            if (_classroomSessionId is not { } sessionId || _classroomCampusId is not { } campusId)
                return null;
            return (campusId, sessionId, _classroomTargets);
        }
    }

    private StudentEventAccessGrant AuthorizeStudentEventAccess(HttpContext context)
    {
        if (!IsSameLan(context.Connection.RemoteIpAddress))
            throw new MobileAuthorizationException("课堂事件只允许在教师电脑所在校园 LAN 使用。");
        var header = context.Request.Headers["Authorization"].ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length > 180)
            throw new MobileAuthorizationException("需要本堂课的学生 Agent 授权。");
        var tokenHash = MobilePairedDeviceStore.HashToken(header[7..]);
        StudentEventAccessGrant? grant;
        lock (_classroomGate)
        {
            if (!_studentEventGrants.TryGetValue(tokenHash, out grant) ||
                grant.ExpiresUtc <= DateTimeOffset.UtcNow || _classroomCampusId != grant.CampusId ||
                _classroomSessionId != grant.SessionId || !_classroomTargets.Contains(grant.Target))
            {
                _studentEventGrants.Remove(tokenHash);
                throw new MobileAuthorizationException("课堂事件授权已过期、撤销或不属于当前课堂。");
            }
        }
        var currentPinnedKey = _agentTrustStore.FindTrustedPublicKey(grant.CampusId, grant.Target);
        if (currentPinnedKey is null || StudentAgentResponseCryptography.GetFingerprint(currentPinnedKey) !=
            grant.AgentFingerprint)
            throw new MobileAuthorizationException("学生 Agent 身份已撤销或轮换；请重新核对设备身份。");
        return grant;
    }

    private bool AcceptStudentEventRate(string target)
    {
        var window = _studentEventRates.GetOrAdd(target, _ => new RequestRateWindow());
        lock (window.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - window.StartedUtc >= TimeSpan.FromMinutes(1))
            {
                window.StartedUtc = now;
                window.Count = 0;
            }
            return ++window.Count <= 10;
        }
    }

    private static long ReadEventCursor(HttpRequest request)
    {
        var value = request.Query["after"].ToString();
        if (value.Length == 0) return 0;
        if (!long.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var cursor) || cursor < 0)
            throw new InvalidDataException("课堂事件读取游标无效。");
        return cursor;
    }

    private (string CampusId, Guid SessionId, IReadOnlySet<string> Targets) ReadActiveClassroom()
    {
        lock (_classroomGate)
        {
            if (_classroomSessionId is not { } sessionId || _classroomCampusId is not { } campusId)
                throw new MobileAuthorizationException("当前没有活动课堂。");
            return (campusId, sessionId, _classroomTargets);
        }
    }

    private IReadOnlyList<string> ReadActiveClassroomTargets(string campusId)
    {
        lock (_classroomGate)
        {
            if (_classroomSessionId is null || !string.Equals(_classroomCampusId, campusId, StringComparison.Ordinal) ||
                _classroomTargets.Count == 0)
                return Array.Empty<string>();
            return Array.AsReadOnly(_classroomTargets.OrderBy(target => target, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    private Guid? ReadActiveClassroomSessionId(string campusId)
    {
        lock (_classroomGate)
        {
            return _classroomSessionId is not null &&
                   string.Equals(_classroomCampusId, campusId, StringComparison.Ordinal)
                ? _classroomSessionId
                : null;
        }
    }

    private IReadOnlyList<ClassroomSeatLocation> ReadActiveClassroomSeatLocations(string campusId)
    {
        ClassroomSession? session;
        IReadOnlySet<string> targets;
        lock (_classroomGate)
        {
            if (_classroomSessionId is null || !string.Equals(_classroomCampusId, campusId, StringComparison.Ordinal) ||
                _classroomSession is not { } active || active.SessionId != _classroomSessionId)
                return Array.Empty<ClassroomSeatLocation>();
            session = active;
            targets = _classroomTargets;
        }

        try
        {
            var configuredTargets = session.Targets.Select(target => target.DeviceLabel).ToArray();
            var layout = _classroomSeatLayoutStore.GetLayout(session.Room.CampusProfileId, session.Room.RoomId,
                configuredTargets);
            var activeTargets = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            return Array.AsReadOnly(ClassroomSeatLayoutStore.GetLocations(layout)
                .Where(location => activeTargets.Contains(location.Target)).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Seat context is optional display data; a local layout failure must not break pairing or policy APIs.
            return Array.Empty<ClassroomSeatLocation>();
        }
    }

    private MobileClassroomCountdownInfo? ReadActiveClassroomCountdown(string campusId)
    {
        Guid sessionId;
        lock (_classroomGate)
        {
            if (_classroomSessionId is not { } activeSessionId ||
                !string.Equals(_classroomCampusId, campusId, StringComparison.Ordinal) ||
                _classroomSession?.SessionId != activeSessionId)
                return null;
            sessionId = activeSessionId;
        }

        try
        {
            var countdown = _classroomCountdownStore.Read(sessionId);
            return countdown is null ? null : new MobileClassroomCountdownInfo(countdown.DeadlineUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Countdown display is optional and must not interfere with the classroom session API.
            return null;
        }
    }

    private MobileClassroomTaskProgressInfo? ReadActiveClassroomTaskProgress(string campusId)
    {
        Guid sessionId;
        lock (_classroomGate)
        {
            if (_classroomSessionId is not { } activeSessionId ||
                !string.Equals(_classroomCampusId, campusId, StringComparison.Ordinal) ||
                _classroomSession?.SessionId != activeSessionId)
                return null;
            sessionId = activeSessionId;
        }

        try
        {
            var progress = _classroomTaskProgressStore.Read(sessionId);
            return progress is null
                ? null
                : new MobileClassroomTaskProgressInfo(progress.CompletedCount, progress.Tasks.Length,
                    Array.AsReadOnly(progress.Tasks.Select(task =>
                        new MobileClassroomTaskProgressItem(task.Title, task.IsCompleted)).ToArray()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Optional classroom progress must not interfere with pairing or policy controls.
            return null;
        }
    }

    private ClassroomMode? ReadActiveClassroomMode(string campusId)
    {
        lock (_classroomGate)
        {
            if (_classroomSessionId is not { } sessionId ||
                !string.Equals(_classroomCampusId, campusId, StringComparison.Ordinal)) return null;
            return _classroomModeStateStore.Read(sessionId)?.Mode ?? ClassroomMode.Normal;
        }
    }

    private static WebsitePolicySigningKey OpenTeacherSigningKey(string campusId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("教师校区签名只支持 Windows TeacherConsole。");
        return WebsitePolicySigningKeyStore.Open(campusId);
    }

    private async Task ServeBootstrap(HttpContext context)
    {
        if (context.Request.Method != "GET" || !IsSameLan(context.Connection.RemoteIpAddress) ||
            !IsBoundHost(context.Request.Host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var identity = RequireIdentity();
        if (context.Request.Path == "/")
        {
            var teacherAddress = IPAddress.Parse(context.Request.Host.Host);
            var secureUrl = $"https://{teacherAddress}:{_httpsPort}/";
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] =
                $"default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-{nonce}'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            var fingerprint = string.Join(" ", Enumerable.Range(0, identity.RootFingerprint.Length / 2)
                .Select(index => identity.RootFingerprint.Substring(index * 2, 2)));
            var page = $$"""
                <!doctype html>
                <html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>教师手机控制连接</title>
                <style>
                body{font:16px system-ui,sans-serif;line-height:1.6;margin:0;background:#f2f6f8;color:#173b4c}
                main{max-width:680px;margin:24px auto;padding:24px;background:white;border-radius:16px}
                h1{font-size:1.5rem;margin-top:0}li{margin:12px 0}.fingerprint{overflow-wrap:anywhere;background:#eef3f5;padding:12px;border-radius:8px}
                .button{display:inline-block;margin:8px 0;padding:12px 18px;border-radius:8px;background:#176d64;color:white;text-decoration:none;font-weight:700}
                .note{color:#526977;font-size:.95rem}
                </style></head><body><main>
                <h1>连接教师手机控制</h1>
                <ol>
                <li><a href="/teacher-mobile-root.cer">下载教师根证书</a>。iPhone：到“设置 → 通用 → VPN 与设备管理”安装下载的描述文件。</li>
                <li>核对下面的 SHA-256 指纹与教师电脑“手机控制设置”窗口中的指纹完全一致；不一致时不要安装。</li>
                <li>iPhone 还要到“设置 → 通用 → 关于本机 → 证书信任设置”，打开此根证书的“完全信任”；Android 按系统提示安装 CA 证书。</li>
                <li>返回浏览器，点下方按钮进入安全连接；填好手机名称后点“配对这部手机”，请求才会显示在教师端等待批准列表。</li>
                </ol>
                <p class="fingerprint"><strong>证书 SHA-256：</strong><br>{{fingerprint}}</p>
                <p><a id="secure" class="button" href="{{secureUrl}}">安装并信任证书后，打开安全连接</a></p>
                <p class="note">如果按钮无反应，可手动打开教师电脑显示的 HTTPS 地址（端口 39176）。若二维码没有自动填入配对码，请手动输入教师窗口显示的 8 位码。</p>
                <p class="note">说明页能打开但安全连接打不开：先确认手机已完全信任根证书；仍失败时检查教师电脑 TCP 39176（安全页）和 39177（说明页）的入站规则，以及校园 Wi-Fi 是否隔离设备。手机和教师电脑要在同一可互通网段。虚拟机使用 NAT 时通常无法直连，请改为桥接并重新生成二维码。</p>
                <script nonce="{{nonce}}">
                const secure = new URL(location.href);
                secure.protocol = "https:";
                secure.port = "{{_httpsPort}}";
                secure.pathname = "/";
                secure.search = "";
                const match = /^#pair=([0-9]{8})$/.exec(location.hash);
                secure.hash = match ? "pair=" + match[1] : "";
                document.getElementById("secure").href = secure.href;
                </script>
                </main></body></html>
                """;
            var pageBytes = Encoding.UTF8.GetBytes(page);
            context.Response.ContentLength = pageBytes.Length;
            await context.Response.Body.WriteAsync(pageBytes, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (context.Request.Path != "/teacher-mobile-root.cer")
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/pkix-cert";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Content-Disposition"] = "attachment; filename=veyon-campus-mobile-root.cer";
        context.Response.ContentLength = identity.RootCertificateBytes.Length;
        await context.Response.Body.WriteAsync(identity.RootCertificateBytes, context.RequestAborted)
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
                    response = new MobilePairPollResponse("rejected");
                }
                else if (pending.ApprovedAccessToken is { } token)
                {
                    var device = pending.ApprovedDevice ??
                        MobilePairedDeviceStore.Add(token, pending.DeviceName, _storageDirectory);
                    if (pending.ApprovedDevice is null)
                        _pendingPairings[pending.Id] = pending with { ApprovedDevice = device };
                    response = new MobilePairPollResponse("approved", token, device);
                    paired = pending.ApprovedDevice is null;
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
                enabled ? "网站限制已发送。" : "网站限制已解除。",
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
                enabled ? "长期系统限制已发送，不会自动到期。" :
                    "长期系统限制已解除；学生电脑只恢复本工具修改的设置。",
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
                enabled ? "影响检查已发送。" : "应用限制已解除。",
                policy.Revision, expires, results.Select(ToMobileResult).ToArray());
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<MobilePolicyOperationResponse> StageApplicationEnforcementAsync(Guid deviceId,
        MobilePolicyProfile profile, IReadOnlyList<string> targets, CancellationToken cancellationToken,
        Guid? classroomSessionId = null, ClassroomMode? classroomMode = null)
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
                "部分电脑未确认影响检查；尚未启用限制。请查看结果后重试。", revision, expires,
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
                "影响检查结果与当前规则不符；尚未启用限制。请重新检查。", revision, expires,
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
            Array.AsReadOnly(targets.ToArray()), DateTimeOffset.UtcNow.AddMinutes(5), classroomSessionId,
            classroomMode);
        return new MobilePolicyOperationResponse("awaiting-teacher-review", true, token,
            "请查看各电脑的影响检查结果。确认前不会启用限制。",
            revision, expires, audit.Select(item => new MobilePolicyTargetResult(item.Target, true,
                NeedsReview: true, "请核对影响检查结果。")).ToArray(), review);
    }

    [SupportedOSPlatform("windows")]
    private async Task<MobilePolicyOperationResponse> CompleteApplicationEnforcementAsync(Guid deviceId,
        MobilePolicyProfile profile, IReadOnlyList<string> targets, string rawReviewToken,
        CancellationToken cancellationToken, Guid? classroomSessionId = null,
        ClassroomMode? classroomMode = null)
    {
        foreach (var entry in _reviewGrants.ToArray())
            if (entry.Value.ExpiresUtc <= DateTimeOffset.UtcNow) _reviewGrants.TryRemove(entry.Key, out _);
        var tokenHash = MobilePairedDeviceStore.HashToken(rawReviewToken);
        if (!_reviewGrants.TryGetValue(tokenHash, out var grant) ||
            grant.DeviceId != deviceId || grant.ProfileId != profile.Id || grant.ExpiresUtc <= DateTimeOffset.UtcNow ||
            grant.ProfileFingerprint != ProfileFingerprint(profile, targets) ||
            !grant.Targets.SequenceEqual(targets, StringComparer.OrdinalIgnoreCase) ||
            grant.ClassroomSessionId != classroomSessionId || grant.ClassroomMode != classroomMode)
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
            "应用限制已发送。实际效果仍需在学生电脑确认。",
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

    private MobileControlTlsIdentity RequireIdentity() => _identity ??
        throw new InvalidOperationException("当前教师控制服务只允许执行本机课堂策略恢复，不能处理手机 HTTPS 请求。");

    private MobilePairedDeviceView Authorize(HttpContext context, bool enforceGeneralRate = true)
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
        if (enforceGeneralRate && !AcceptRequestRate(device.Id))
            throw new MobileRateLimitException("手机请求过于频繁，请稍后再试。");
        return device;
    }

    private bool AcceptRequestRate(Guid deviceId)
    {
        var window = _requestRates.GetOrAdd(deviceId, _ => new RequestRateWindow());
        return AcceptRate(window, 60);
    }

    private bool AcceptScreenPreviewRate(Guid deviceId)
    {
        var deviceWindow = _screenPreviewRequestRates.GetOrAdd(deviceId, _ => new RequestRateWindow());
        return AcceptRate(deviceWindow, 200) && AcceptRate(_screenPreviewGlobalRate, 200);
    }

    private static bool AcceptRate(RequestRateWindow window, int maximumRequestsPerMinute)
    {
        lock (window.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - window.StartedUtc >= TimeSpan.FromMinutes(1))
            {
                window.StartedUtc = now;
                window.Count = 0;
            }
            return ++window.Count <= maximumRequestsPerMinute;
        }
    }

    private bool AcceptRequestNonce(HttpContext context, Guid deviceId)
    {
        var nonceText = context.Request.Headers["X-Veyon-Request-Nonce"].ToString();
        var timestampText = context.Request.Headers["X-Veyon-Request-Timestamp"].ToString();
        var now = DateTimeOffset.UtcNow;
        if (!Guid.TryParseExact(nonceText, "N", out var nonce) || nonce == Guid.Empty ||
            !DateTimeOffset.TryParseExact(timestampText, RequestTimestampFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp) ||
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

    private static async Task<string> ReadBoundedTextAsync(HttpRequest request, int maximumBytes,
        CancellationToken cancellationToken)
    {
        var mediaType = request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请求必须使用 application/json。");
        if (request.ContentLength is { } contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("课堂事件超过大小限制。");
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("课堂事件超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        try { return new UTF8Encoding(false, true).GetString(buffer.ToArray()); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("课堂事件不是有效 UTF-8 文本。", exception);
        }
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
        RequireIdentity().Addresses.Contains(address);

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
                if (!RequireIdentity().Addresses.Contains(item.Address) || item.IPv4Mask is not { } mask) continue;
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

    internal sealed class MobileAuthorizationException(string message) : Exception(message);
    internal sealed class MobileRateLimitException(string message) : Exception(message);
}
