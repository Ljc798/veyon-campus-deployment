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
    bool ApplicationSupported, bool SupportsPolicyCompareAndSet, bool NeedsReview, string Detail,
    StudentSystemPolicyReportedState? SystemPolicy = null,
    EdgeWebsitePolicyReportedState? EdgeWebsite = null,
    IReadOnlyList<string>? WebsiteBlockedDomains = null,
    IReadOnlyList<string>? WebsiteAllowedDomains = null);
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
internal sealed record MobileResumeResponse(string AccessToken);
internal sealed record MobileStatusRequest(IReadOnlyList<string> Targets, Guid? ProfileId = null);
internal sealed record MobilePolicyRequest(Guid ProfileId, IReadOnlyList<string> Targets, bool Enabled,
    string? ReviewToken = null,
    IReadOnlyDictionary<string, long>? ExpectedWebsiteRevisions = null,
    IReadOnlyDictionary<string, long>? ExpectedApplicationRevisions = null,
    IReadOnlyDictionary<string, long>? ExpectedSystemRevisions = null);
internal sealed record MobileCustomWebsitePolicyRequest(IReadOnlyList<string> Targets, bool Enabled,
    string? Mode = null, int? DurationMinutes = null, IReadOnlyList<string>? Domains = null,
    IReadOnlyDictionary<string, long>? ExpectedWebsiteRevisions = null,
    DateTimeOffset? ExpiresUtc = null, IReadOnlyList<string>? BlockedDomains = null,
    IReadOnlyList<string>? AllowedDomains = null);
internal sealed record MobilePolicyExpectedRevisions(
    IReadOnlyDictionary<string, long>? Website = null,
    IReadOnlyDictionary<string, long>? Application = null,
    IReadOnlyDictionary<string, long>? System = null);
internal sealed record MobileSessionResponse(MobilePairedDeviceView Device, string Status,
    IReadOnlyList<string> ActiveClassroomTargets, string? ClassroomMode = null,
    IReadOnlyList<ClassroomSeatLocation>? ActiveClassroomSeatLocations = null,
    MobileClassroomCountdownInfo? ActiveClassroomCountdown = null,
    MobileClassroomTaskProgressInfo? ActiveClassroomTaskProgress = null,
    Guid? ActiveClassroomSessionId = null,
    IReadOnlyList<MobileClassroomScreenTarget>? ActiveClassroomScreenTargets = null);
internal sealed record MobileClassroomScreenTarget(string TargetId, int SeatNumber, int Row, int Column,
    string StudentName, string ComputerName);
internal sealed record MobileClassroomSessionRequest(bool? Active);
internal sealed record MobileClassroomSessionActionResponse(bool Active, string Message,
    IReadOnlyList<string> Targets, Guid? SessionId);
internal sealed record MobileReviewGrant(Guid DeviceId, Guid ProfileId, string ProfileFingerprint,
    long AuditRevision, string AuditFingerprint, IReadOnlyList<string> Targets, DateTimeOffset ExpiresUtc,
    Guid? ClassroomSessionId = null, ClassroomMode? ClassroomMode = null);
internal sealed record MobileClassroomModeRequest(ClassroomMode? Mode, string? ReviewToken = null);
internal sealed record MobileScreenPreviewStartRequest(IReadOnlyList<string>? Targets = null,
    string? Quality = null, int? RefreshIntervalSeconds = null);
internal sealed record MobileClassroomEventReplyRequest(Guid HelpEventId, string Message);
internal sealed record MobileClassroomEventNoticeRequest(string Message);
internal sealed record MobileClassroomNoticeResponse(bool Accepted, int TargetCount);
internal sealed record MobileScreenPreviewSessionResponse(Guid LeaseId, DateTimeOffset ExpiresUtc,
    Guid SessionId, IReadOnlyList<string> Targets, int TotalTargets, string Quality,
    int RefreshIntervalSeconds);
internal sealed record MobileScreenPreviewStopRequest(Guid LeaseId);
internal sealed record MobileClassroomEventPage(long Cursor, IReadOnlyList<string> Events, Guid? SessionId = null);
internal sealed record MobileStudentEventSubmitResponse(bool Accepted, bool Duplicate);
internal sealed record TeacherClassroomChatSendResult(bool Accepted, bool Duplicate, DateTimeOffset ExpiresUtc);
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
    private readonly Func<bool, CancellationToken, Task<MobileClassroomSessionActionResponse>>? _setClassroomSession;
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
    private int _auditRefreshGeneration;
    private readonly object _auditDisplayNameGate = new();
    private IReadOnlyDictionary<string, string> _auditTargetDisplayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private string _classroomEventStatus = "当前没有活动课堂。";

    public TeacherMobileControlManager(Func<string> activeCampusId, Action<Action> dispatchToUi)
        : this(activeCampusId, dispatchToUi, null)
    {
    }

    internal TeacherMobileControlManager(Func<string> activeCampusId, Action<Action> dispatchToUi,
        Func<bool, CancellationToken, Task<MobileClassroomSessionActionResponse>>? setClassroomSession)
    {
        _activeCampusId = activeCampusId ?? throw new ArgumentNullException(nameof(activeCampusId));
        _dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        _setClassroomSession = setClassroomSession;
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
            service = new TeacherMobileControlService(identity, OnPairingChanged, CurrentCampusId,
                setClassroomSession: _setClassroomSession);
            await service.StartAsync(cancellationToken);
            _service = service;
            var pairingAddress = SelectPreferredLanAddress(identity.Addresses);
            LanUrl = $"https://{pairingAddress}:{HttpsPort}/";
            BootstrapUrl = $"http://{pairingAddress}:{BootstrapPort}/";
            CertificateFingerprint = string.Join(" ", Enumerable.Range(0, identity.RootFingerprint.Length / 2)
                .Select(index => identity.RootFingerprint.Substring(index * 2, 2)));
            IsRunning = true;
            Status = "手机和教师电脑需连接同一校园网。";
            RefreshDevices();
            RefreshPendingPairings();
            RefreshProfiles();
            RefreshAudit(refreshVeyonDirectory: true);
        }
        catch
        {
            if (service is not null) await service.DisposeAsync();
            Status = "手机控制服务未启动。";
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
        foreach (var target in context.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (trustStore.FindTrustedPublicKey(context.CampusId, target) is null)
            {
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
        var readyTargets = results.Where(item => item.Succeeded).Select(item => item.Target).ToArray();
        service.RetainConfirmedClassroomEventGrants(context.SessionId, readyTargets);
        var chatTargets = results.Where(item => item.Succeeded &&
                item.Acknowledgement?.Capabilities?.Contains(ClassroomEventCryptography.ClassroomChatCapability,
                    StringComparer.Ordinal) == true)
            .Select(item => item.Target).ToArray();
        service.SetClassroomChatCapabilities(context.SessionId, chatTargets);
        var outdatedAgents = results.Count(item => !item.Succeeded &&
            item.Detail.Contains("版本过旧或未", StringComparison.Ordinal));
        ClassroomEventStatus = context.Targets.Count == 0
            ? "没有学生电脑可连接。"
            : ready == context.Targets.Count
                ? $"课堂消息已连接 {ready} 台；一对一聊天可用 {chatTargets.Length} 台。"
                : $"课堂消息已连接 {ready}/{context.Targets.Count} 台；一对一聊天可用 {chatTargets.Length} 台。" +
                  (outdatedAgents > 0
                      ? $"有 {outdatedAgents} 台 Student Agent 版本过旧或不支持当前课堂沟通，请更新学生端后重试。"
                      : "请检查其余电脑身份和连接。");
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

    internal IReadOnlyList<string> ReadClassroomChatTargets() =>
        _service?.ReadClassroomChatTargets() ?? Array.Empty<string>();

    internal TeacherClassroomChatSendResult SendClassroomChatMessage(string target, string message,
        Guid? eventId = null)
    {
        var service = _service ?? throw new InvalidOperationException("课堂消息服务尚未启动。");
        return service.SendClassroomChatMessage(target, message, eventId);
    }

    internal MobileClassroomNoticeResponse SendClassroomNotice(string message)
    {
        var service = _service ?? throw new InvalidOperationException("课堂消息服务尚未启动。");
        return service.SendClassroomNotice(message);
    }

    internal ClassroomQuizTeacherSnapshot OpenClassroomQuiz(string question,
        IReadOnlyList<string> choices, int durationMinutes) =>
        (_service ?? throw new InvalidOperationException("课堂消息服务尚未启动。"))
        .OpenClassroomQuiz(question, choices, durationMinutes);

    internal ClassroomQuizTeacherSnapshot? ReadClassroomQuiz(Guid? quizId = null) =>
        (_service ?? throw new InvalidOperationException("课堂消息服务尚未启动。"))
        .ReadClassroomQuiz(quizId);

    internal ClassroomQuizTeacherSnapshot CloseClassroomQuiz(Guid quizId) =>
        (_service ?? throw new InvalidOperationException("课堂消息服务尚未启动。"))
        .CloseClassroomQuiz(quizId);

    internal ClassroomQuizTeacherSnapshot RevealClassroomQuiz(Guid quizId) =>
        (_service ?? throw new InvalidOperationException("课堂消息服务尚未启动。"))
        .RevealClassroomQuiz(quizId);

    internal bool CancelClassroomQuiz(Guid quizId) =>
        (_service ?? throw new InvalidOperationException("课堂消息服务尚未启动。"))
        .CancelClassroomQuiz(quizId);

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
        PairingExpiry = $"连接码有效至 {invitation.ExpiresUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        Status = "二维码已生成，等待手机扫描。";
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
        Status = approved ? $"已允许“{selected.DeviceName}”连接。" : "手机连接请求已过期或处理完毕。";
        return approved;
    }

    public bool RejectSelectedPairing()
    {
        if (SelectedPairing is not { } selected || _service is null) return false;
        var rejected = _service.RejectPairing(selected.Id);
        RefreshPendingPairings();
        Status = rejected ? $"已拒绝“{selected.DeviceName}”的连接请求。" : "手机连接请求已过期或处理完毕。";
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

    public void RefreshAudit(bool refreshVeyonDirectory = false)
    {
        var generation = Interlocked.Increment(ref _auditRefreshGeneration);
        _ = RefreshAuditCoreAsync(generation, refreshVeyonDirectory);
    }

    private async Task RefreshAuditCoreAsync(int generation, bool refreshVeyonDirectory)
    {
        try
        {
            if (refreshVeyonDirectory)
            {
                var refreshedNames = await Task.Run(() =>
                {
                    try
                    {
                        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var location in VeyonNetworkObjectDirectory.ReadLocations())
                        foreach (var pair in location.TargetDisplayNames ?? new Dictionary<string, string>())
                        {
                            var name = pair.Value?.Trim();
                            if (!string.IsNullOrWhiteSpace(name) && !IPAddress.TryParse(name, out _))
                                names.TryAdd(pair.Key, name);
                        }
                        return names;
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or
                                                      UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                                                      InvalidOperationException or TimeoutException or PlatformNotSupportedException)
                    {
                        return null;
                    }
                }).ConfigureAwait(false);
                if (refreshedNames is not null)
                {
                    lock (_auditDisplayNameGate)
                        if (generation == Volatile.Read(ref _auditRefreshGeneration))
                            _auditTargetDisplayNames = refreshedNames;
                }
            }

            IReadOnlyDictionary<string, string> displayNames;
            lock (_auditDisplayNameGate) displayNames = _auditTargetDisplayNames;
            var entries = await Task.Run(() =>
            {
                string DisplayComputer(string target) =>
                    displayNames.TryGetValue(target, out var name) ? name : "未命名电脑";

                return MobileControlAuditStore.Read().Reverse().Take(50).Select(entry =>
                {
                    var outcomes = string.Join(" · ", (entry.Results ?? []).Select(result =>
                        DisplayComputer(result.Target) + ": " + result.Outcome));
                    if (outcomes.Length > 1600) outcomes = outcomes[..1600] + "…";
                    var shortDeviceId = entry.DeviceId.ToString("N")[..8];
                    var deviceName = MobilePairedDeviceStore.Find(entry.DeviceId)?.DisplayName;
                    var device = string.IsNullOrWhiteSpace(deviceName) ? shortDeviceId : $"{deviceName} · {shortDeviceId}";
                    return new MobileControlAuditView(entry.TimeUtc, device,
                        entry.Action, string.Join(", ", entry.Targets.Select(DisplayComputer)),
                        outcomes.Length == 0 ? entry.Outcome : entry.Outcome + " · " + outcomes);
                }).ToArray();
            }).ConfigureAwait(false);
            _dispatchToUi(() =>
            {
                if (generation != Volatile.Read(ref _auditRefreshGeneration)) return;
                AuditEntries = Array.AsReadOnly(entries);
                AuditStatus = entries.Length == 0 ? "尚无手机控制记录。" : $"最近 {entries.Length} 条；完整记录保留在教师电脑本机。";
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or System.Security.SecurityException or
                                          JsonException or ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or TimeoutException)
        {
            _dispatchToUi(() =>
            {
                if (generation != Volatile.Read(ref _auditRefreshGeneration)) return;
                AuditEntries = Array.Empty<MobileControlAuditView>();
                AuditStatus = "无法读取本机手机控制记录：" + exception.Message;
            });
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
    private static readonly int[] ScreenPreviewRefreshIntervalsSeconds = [3, 5, 10, 15, 30, 60];
    private const string MobileResumeCookieName = "vc_mobile_resume";
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
    private readonly ConcurrentDictionary<string, RequestRateWindow> _resumeRates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, RequestRateWindow> _screenPreviewRequestRates = new();
    private readonly ConcurrentDictionary<string, RequestRateWindow> _studentEventRates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RequestRateWindow> _teacherChatRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClassroomEventBuffer _classroomEvents = new();
    private readonly Dictionary<string, StudentEventAccessGrant> _studentEventGrants = new(StringComparer.Ordinal);
    private readonly object _classroomGate = new();
    private readonly object _screenPreviewGate = new();
    private readonly Dictionary<Guid, ScreenPreviewLease> _screenPreviewLeases = [];
    private readonly RequestRateWindow _screenPreviewGlobalRate = new();
    private readonly ClassroomScreenPreviewCapture _screenPreviewCapture;
    private readonly SemaphoreSlim _mobileClassroomSessionGate = new(1, 1);
    private readonly Func<bool, CancellationToken, Task<MobileClassroomSessionActionResponse>>? _setClassroomSession;
    private readonly ClassroomQuizStore _classroomQuizzes = new();
    private CancellationTokenSource _classroomSessionChanged = new();
    private string? _classroomCampusId;
    private Guid? _classroomSessionId;
    private IReadOnlySet<string> _classroomTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _classroomChatTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
    private readonly ClassroomSeatGridStore _classroomSeatGridStore;
    private readonly string _classroomSeatGridPath;
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
    private sealed record ScreenTargetBinding(Guid TargetId, string TargetKey, string HostAddress,
        int SeatNumber, int Row, int Column, string StudentName, string ComputerName);
    private sealed record ScreenPreviewLease(Guid LeaseId, Guid DeviceId, Guid SessionId,
        string CampusId, IReadOnlySet<string> Targets,
        IReadOnlyDictionary<string, ScreenTargetBinding> BindingSnapshot,
        IReadOnlyDictionary<string, string> AuditTargetLabels, string Quality, int RefreshIntervalSeconds,
        IReadOnlyDictionary<string, DateTimeOffset> LastTargetRequestUtc, CancellationTokenSource Cancellation,
        DateTimeOffset ExpiresUtc);
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
        Func<string, CancellationToken, Task<byte[]>>? captureScreenAsync = null,
        Func<bool, CancellationToken, Task<MobileClassroomSessionActionResponse>>? setClassroomSession = null)
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
        _setClassroomSession = setClassroomSession;
        _classroomModeStateStore = new ClassroomModeStateStore(_storageDirectory is null ? null :
            Path.Combine(_storageDirectory, "classroom-mode-state.json"));
        _classroomSessionStore = new ClassroomSessionStore(_storageDirectory is null ? null :
            Path.Combine(_storageDirectory, "classroom-sessions.json"));
        _classroomSeatLayoutStore = new ClassroomSeatLayoutStore(_storageDirectory is null ? null :
            Path.Combine(_storageDirectory, "classroom-seat-layouts.json"));
        _classroomSeatGridPath = Path.GetFullPath(_storageDirectory is null
            ? ClassroomSeatGridStore.DefaultPath
            : Path.Combine(_storageDirectory, "classroom-seat-grid.json"));
        _classroomSeatGridStore = new ClassroomSeatGridStore(_classroomSeatGridPath);
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
        _classroomSeatGridPath = Path.GetFullPath(ClassroomSeatGridStore.DefaultPath);
        _classroomSeatGridStore = new ClassroomSeatGridStore(_classroomSeatGridPath);
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
        var quizContextChanged = false;
        lock (_classroomGate)
        {
            var changed = _classroomSessionId != sessionId || _classroomCampusId != campusId;
            var targetsChanged = !_classroomTargets.SetEquals(normalizedTargets);
            quizContextChanged = changed || targetsChanged;
            if (changed || targetsChanged)
            {
                sessionChanged = changed;
                previousSession = _classroomSessionId;
                previousCancellation = _classroomSessionChanged;
                _classroomSessionChanged = new CancellationTokenSource();
                if (changed)
                {
                    _studentEventGrants.Clear();
                    _studentEventRates.Clear();
                    _teacherChatRates.Clear();
                    _classroomChatTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _lastClassroomNoticeUtc = DateTimeOffset.MinValue;
                }
                else
                {
                    _classroomChatTargets = _classroomChatTargets.Where(normalizedTargets.Contains)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var key in _studentEventGrants.Where(item => !normalizedTargets.Contains(item.Value.Target))
                                 .Select(item => item.Key).ToArray())
                        _studentEventGrants.Remove(key);
                }
            }
            _classroomCampusId = campusId;
            _classroomSessionId = sessionId;
            _classroomTargets = normalizedTargets;
            _classroomTargetIds = normalizedTargetIds;
            _classroomSession = session;
        }
        if (sessionChanged && previousSession is { } oldSession) _classroomEvents.ClearSession(oldSession);
        if (quizContextChanged && (previousSession ?? sessionId) is { } quizSessionToEnd)
            _classroomQuizzes.EndSession(quizSessionToEnd);
        if (sessionChanged || quizContextChanged)
        {
            EndScreenPreviewLeases(previousSession ?? sessionId, "课堂结束或目标已改变");
            if (sessionChanged) _screenPreviewCapture.SetSession(sessionId);
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

    public void SetClassroomChatCapabilities(Guid sessionId, IEnumerable<string> supportedTargets)
    {
        ArgumentNullException.ThrowIfNull(supportedTargets);
        var supplied = supportedTargets.ToArray();
        IReadOnlyList<string> normalizedTargets = supplied.Length == 0 ? Array.Empty<string>() :
            WebsitePolicyTransport.NormalizeTargets(supplied);
        var normalized = normalizedTargets
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_classroomGate)
        {
            if (_classroomSessionId != sessionId)
                throw new InvalidDataException("聊天能力回执不属于当前课堂。");
            // Each call receives the complete result set from the latest grant push.
            // Replace the snapshot so a target that stops advertising support cannot
            // remain enabled from an earlier acknowledgement in the same session.
            _classroomChatTargets = normalized.Where(_classroomTargets.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void RetainConfirmedClassroomEventGrants(Guid sessionId, IEnumerable<string> confirmedTargets)
    {
        ArgumentNullException.ThrowIfNull(confirmedTargets);
        var supplied = confirmedTargets.ToArray();
        IReadOnlyList<string> normalizedTargets = supplied.Length == 0 ? Array.Empty<string>() :
            WebsitePolicyTransport.NormalizeTargets(supplied);
        var confirmed = normalizedTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_classroomGate)
        {
            if (_classroomSessionId != sessionId)
                throw new InvalidDataException("课堂事件回执不属于当前课堂。");
            if (confirmed.Any(target => !_classroomTargets.Contains(target)))
                throw new InvalidDataException("课堂事件回执包含非当前课堂目标。");

            // A legacy or unreachable Agent may have installed a grant before its
            // acknowledgement failed. Keep only grants confirmed by this refresh.
            foreach (var tokenHash in _studentEventGrants
                         .Where(item => item.Value.SessionId == sessionId && !confirmed.Contains(item.Value.Target))
                         .Select(item => item.Key).ToArray())
                _studentEventGrants.Remove(tokenHash);
        }
    }

    public IReadOnlyList<string> ReadClassroomChatTargets()
    {
        lock (_classroomGate)
            return Array.AsReadOnly(_classroomChatTargets.OrderBy(target => target,
                StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public TeacherClassroomChatSendResult SendClassroomChatMessage(string target, string message,
        Guid? eventId = null)
    {
        var normalizedTarget = ClassroomEventCryptography.NormalizeSingleTarget(target);
        if (string.IsNullOrWhiteSpace(message) || message.Trim().Length >
            ClassroomEventCryptography.MaximumMessageCharacters || message.Any(char.IsControl) ||
            eventId == Guid.Empty)
            throw new InvalidDataException("聊天消息须为 1–500 个字符，且不能包含不可见控制字符。");
        var active = ReadActiveClassroom();
        lock (_classroomGate)
            if (_classroomSessionId != active.SessionId || _classroomCampusId != active.CampusId ||
                !_classroomTargets.Contains(normalizedTarget) || !_classroomChatTargets.Contains(normalizedTarget))
                throw new MobileAuthorizationException("该学生电脑未加入当前课堂聊天，或 Agent 版本不支持聊天。");
        var window = _teacherChatRates.GetOrAdd(normalizedTarget, _ => new RequestRateWindow());
        lock (window.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - window.StartedUtc >= TimeSpan.FromMinutes(1))
            {
                window.StartedUtc = now;
                window.Count = 0;
            }
            if (++window.Count > 30)
                throw new MobileRateLimitException("该学生的聊天消息已达到发送上限；请稍后重试。");
        }
        using var signingKey = _openTeacherSigningKey(active.CampusId);
        var issuedUtc = DateTimeOffset.UtcNow;
        var chatMessage = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, active.CampusId,
            active.SessionId, eventId ?? Guid.NewGuid(), normalizedTarget, ClassroomEventSender.Teacher,
            ClassroomEventType.ClassroomChatMessage, issuedUtc,
            issuedUtc.Add(ClassroomEventCryptography.MaximumEventLifetime), null, message.Trim(),
            ClassroomEventCryptography.CreateConversationId(active.SessionId, normalizedTarget));
        var signedMessage = ClassroomEventCryptography.SignEvent(chatMessage, signingKey.PrivateKey);
        lock (_classroomGate)
        {
            if (_classroomSessionId != active.SessionId || _classroomCampusId != active.CampusId ||
                !_classroomTargets.Contains(normalizedTarget) || !_classroomChatTargets.Contains(normalizedTarget))
                throw new MobileAuthorizationException("课堂状态已改变；聊天消息未发送。");
            var accepted = _classroomEvents.Append(chatMessage, issuedUtc, signedMessage);
            var retainedMessage = _classroomEvents.Find(active.SessionId, chatMessage.EventId, issuedUtc);
            return new TeacherClassroomChatSendResult(true, !accepted,
                retainedMessage?.ExpiresUtc ?? chatMessage.ExpiresUtc);
        }
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

    public ClassroomQuizTeacherSnapshot OpenClassroomQuiz(string question, IReadOnlyList<string> choices,
        int durationMinutes)
    {
        if (durationMinutes is < 1 or > 240)
            throw new InvalidDataException("课堂测验时长须为 1–240 分钟。");
        var active = ReadActiveClassroomSession();
        var now = DateTimeOffset.UtcNow;
        return _classroomQuizzes.Open(active.Session, question, choices, now,
            now.AddMinutes(durationMinutes));
    }

    public ClassroomQuizTeacherSnapshot? ReadClassroomQuiz(Guid? quizId = null)
    {
        var active = ReadActiveClassroomSession();
        var now = DateTimeOffset.UtcNow;
        return quizId is { } id
            ? _classroomQuizzes.ReadForTeacher(active.Session.SessionId, id, now)
            : _classroomQuizzes.ReadCurrentForTeacher(active.Session.SessionId, now);
    }

    public ClassroomQuizTeacherSnapshot CloseClassroomQuiz(Guid quizId)
    {
        var active = ReadActiveClassroomSession();
        return _classroomQuizzes.Close(active.Session.SessionId, quizId, DateTimeOffset.UtcNow)
               ?? throw new InvalidDataException("课堂测验已过期或不属于当前课堂。");
    }

    public ClassroomQuizTeacherSnapshot RevealClassroomQuiz(Guid quizId)
    {
        var active = ReadActiveClassroomSession();
        return _classroomQuizzes.Reveal(active.Session.SessionId, quizId, DateTimeOffset.UtcNow)
               ?? throw new InvalidDataException("课堂测验已过期或不属于当前课堂。");
    }

    public bool CancelClassroomQuiz(Guid quizId)
    {
        var active = ReadActiveClassroomSession();
        if (!_classroomQuizzes.Cancel(active.Session.SessionId, quizId))
            throw new InvalidDataException("课堂测验已过期或不属于当前课堂。");
        return true;
    }

    private (string CampusId, ClassroomSession Session) ReadActiveClassroomSession()
    {
        lock (_classroomGate)
        {
            if (_classroomSessionId is not { } sessionId || _classroomCampusId is not { } campusId ||
                _classroomSession is not { } session || session.SessionId != sessionId)
                throw new MobileAuthorizationException("当前没有活动课堂。");
            return (campusId, session);
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
                throw new InvalidDataException("已达到已连接手机数量上限；请先解除不再使用的手机连接。");
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
        _screenPreviewRequestRates.TryRemove(deviceId, out _);
        var prefix = deviceId.ToString("N") + ":";
        foreach (var key in _requestNonces.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            _requestNonces.TryRemove(key, out _);
        ScreenPreviewLease? lease = null;
        var clearSnapshots = false;
        lock (_screenPreviewGate)
        {
            if (_screenPreviewLeases.Remove(deviceId, out var existing))
            {
                lease = existing;
                clearSnapshots = !_screenPreviewLeases.Values.Any(item => item.SessionId == existing.SessionId &&
                    item.ExpiresUtc > DateTimeOffset.UtcNow);
            }
        }
        if (lease is null) return;
        CancelScreenPreviewLease(lease);
        if (clearSnapshots) _screenPreviewCapture.ClearSnapshots(lease.SessionId);
        _ = TryAppendAudit(new MobileControlAuditEntry(DateTimeOffset.UtcNow, deviceId,
            "classroom-screen-preview-stop", null,
            ScreenPreviewAuditTargets(lease), "手机已取消配对"));
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
                    $"手机控制接口 {context.Request.Method} {context.Request.Path} 操作失败；请在教师控制台查看当前逐台状态。")
                    .ConfigureAwait(false);
            }
        });

        app.MapGet("/", context => ServeAsset(context, "index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.js", context => ServeAsset(context, "app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/styles.css", context => ServeAsset(context, "styles.css", "text/css; charset=utf-8"));
        app.MapGet("/manifest.webmanifest", context => ServeAsset(context, "manifest.webmanifest", "application/manifest+json"));
        app.MapGet("/service-worker.js", context => ServeAsset(context, "service-worker.js", "text/javascript; charset=utf-8"));
        app.MapPost("/api/pair", async context => await PairAsync(context).ConfigureAwait(false));
        app.MapPost("/api/pair-status", async context => await PollPairingAsync(context).ConfigureAwait(false));
        app.MapPost("/api/mobile-resume", async context => await ResumeMobileSessionAsync(context).ConfigureAwait(false));
        app.MapPost("/api/mobile-forget", async context => await ForgetMobileSessionAsync(context).ConfigureAwait(false));
        app.MapGet("/api/session", async context =>
        {
            var device = Authorize(context);
            var campusId = CurrentCampusId();
            var activeClassroomTargets = ReadActiveClassroomTargets(campusId);
            var mode = ReadActiveClassroomMode(campusId);
            var seatLocations = ReadActiveClassroomSeatLocations(campusId);
            var countdown = ReadActiveClassroomCountdown(campusId);
            var taskProgress = ReadActiveClassroomTaskProgress(campusId);
            var sessionId = ReadActiveClassroomSessionId(campusId);
            var screenTargets = sessionId is { } activeSessionId
                ? ReadActiveClassroomScreenTargets(campusId, activeSessionId)
                    .Select(target => new MobileClassroomScreenTarget(target.TargetKey, target.SeatNumber,
                        target.Row, target.Column, target.StudentName, target.ComputerName)).ToArray()
                : Array.Empty<MobileClassroomScreenTarget>();
            await WriteJson(context, new MobileSessionResponse(device,
                    "已连接教师控制台。", activeClassroomTargets,
                    mode?.ToString().ToLowerInvariant(), seatLocations, countdown, taskProgress,
                    sessionId, Array.AsReadOnly(screenTargets)),
                    context.RequestAborted)
                .ConfigureAwait(false);
        });
        app.MapPost("/api/classroom/session", async context =>
        {
            if (!OperatingSystem.IsWindows())
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                return;
            }
            var device = Authorize(context);
            var request = await ReadJson<MobileClassroomSessionRequest>(context.Request, 4096,
                context.RequestAborted).ConfigureAwait(false);
            if (request.Active is not { } desiredActive)
                throw new InvalidDataException("课堂状态缺失或无效。");
            if (_setClassroomSession is null)
                throw new InvalidOperationException("手机课堂开关尚未连接到教师控制台。");

            await _mobileClassroomSessionGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                MobileClassroomSessionActionResponse result;
                try
                {
                    result = await _setClassroomSession(desiredActive, context.RequestAborted)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException exception)
                {
                    await WriteError(context, StatusCodes.Status409Conflict, exception.Message)
                        .ConfigureAwait(false);
                    return;
                }

                if (result.Active != desiredActive || result.Targets.Count > ClassroomSession.MaximumTargets ||
                    (result.Active && (result.SessionId is null || result.Targets.Count == 0)) ||
                    (!result.Active && (result.SessionId is not null || result.Targets.Count != 0)))
                {
                    await WriteError(context, StatusCodes.Status409Conflict,
                        "课堂状态没有按预期更新；请刷新后重试。").ConfigureAwait(false);
                    return;
                }

                var action = desiredActive ? "classroom-session-start-mobile" : "classroom-session-end-mobile";
                var audit = result.Targets.Select(target => new MobileControlAuditTargetResult(target,
                    desiredActive ? "class-started" : "class-ended")).ToArray();
                if (!TryAppendAudit(new MobileControlAuditEntry(DateTimeOffset.UtcNow, device.Id, action,
                        null, result.Targets, result.Message, audit)))
                    result = result with { Message = result.Message + " 本机操作日志未保存。" };
                await WriteJson(context, result, context.RequestAborted).ConfigureAwait(false);
            }
            finally { _mobileClassroomSessionGate.Release(); }
        });
        app.MapPost("/api/classroom/screen-preview/start", async context =>
        {
            var device = Authorize(context);
            var request = await ReadJson<MobileScreenPreviewStartRequest>(context.Request, 16 * 1024,
                context.RequestAborted).ConfigureAwait(false);
            var result = StartScreenPreview(device, request.Targets, quality: request.Quality,
                refreshIntervalSeconds: request.RefreshIntervalSeconds);
            try
            {
                await WriteJson(context, result, context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                StopScreenPreview(device, result.LeaseId, "巡视启动请求已取消");
            }
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
            var target = NormalizeScreenTargetKey(context.Request.RouteValues["target"]?.ToString());
            var active = ReadActiveClassroom();
            var leaseIdText = context.Request.Headers["X-Veyon-Screen-Preview-Lease"].ToString();
            if (!Guid.TryParseExact(leaseIdText, "N", out var leaseId) || leaseId == Guid.Empty)
                throw new MobileAuthorizationException("屏幕巡视授权已失效；请重新打开巡视。");
            var bindings = ReadActiveClassroomScreenTargets(active.CampusId, active.SessionId);
            var binding = bindings.SingleOrDefault(item => item.TargetKey == target)
                          ?? throw new MobileAuthorizationException("这个座位已不在当前课堂的电脑关联中；请刷新后重试。");
            var activeBindings = bindings.ToDictionary(item => item.TargetKey,
                StringComparer.OrdinalIgnoreCase);
            var lease = RenewScreenPreviewLease(device.Id, leaseId, active.SessionId, target, activeBindings);
            var quality = ClassroomScreenPreviewQualityProfile.FromValue(lease.Quality);
            using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, lease.Cancellation.Token);
            var snapshot = await _screenPreviewCapture.GetAsync(active.SessionId, binding.HostAddress, quality,
                captureCancellation.Token).ConfigureAwait(false);
            EnsureScreenPreviewLeaseBindingsCurrent(device, lease);
            captureCancellation.Token.ThrowIfCancellationRequested();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "image/jpeg";
            context.Response.ContentLength = snapshot.Jpeg.Length;
            context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            context.Response.Headers["X-Captured-Utc"] = snapshot.CapturedUtc.ToString("O",
                System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-Screen-Frame"] = snapshot.WasCaptured ? "captured" : "cached";
            context.Response.Headers["X-Screen-Quality"] = quality.Value;
            await context.Response.Body.WriteAsync(snapshot.Jpeg, captureCancellation.Token).ConfigureAwait(false);
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
            await WriteJson(context, AddStudentSeatNames(rooms), context.RequestAborted).ConfigureAwait(false);
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
        app.MapPost("/api/policy/custom-website", async context =>
        {
            if (!OperatingSystem.IsWindows())
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                return;
            }
            await ApplyCustomWebsitePolicyAsync(context).ConfigureAwait(false);
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
        app.MapGet("/api/classroom/quiz", async context =>
            await ReadMobileClassroomQuizAsync(context).ConfigureAwait(false));
        app.MapGet("/api/classroom/quiz/student", async context =>
            await ReadStudentClassroomQuizAsync(context).ConfigureAwait(false));
        app.MapPost("/api/classroom/quiz/student", async context =>
            await SubmitStudentClassroomQuizAnswerAsync(context).ConfigureAwait(false));
    }

    private MobileScreenPreviewSessionResponse StartScreenPreview(MobilePairedDeviceView device,
        IReadOnlyList<string>? requestedTargets, string? quality = null, int? refreshIntervalSeconds = null)
    {
        var active = ReadActiveClassroom();
        var screenTargets = ReadActiveClassroomScreenTargets(active.CampusId, active.SessionId);
        if (screenTargets.Count == 0)
            throw new MobileAuthorizationException("当前课堂还没有已关联座位的学生电脑；请先在教师电脑保存座位和电脑关联。");
        var qualityProfile = ClassroomScreenPreviewQualityProfile.FromValue(quality);
        var safeRefreshIntervalSeconds = NormalizeScreenPreviewRefreshInterval(refreshIntervalSeconds);
        var bindingsByKey = screenTargets.ToDictionary(item => item.TargetKey,
            StringComparer.OrdinalIgnoreCase);
        string[] targets;
        if (requestedTargets is null)
        {
            targets = screenTargets.Select(item => item.TargetKey).ToArray();
        }
        else
        {
            if (requestedTargets.Count is < 1 or > ClassroomScreenPreviewCapture.MaximumPreviewTargets)
                throw new InvalidDataException($"一次可查看 1 至 {ClassroomScreenPreviewCapture.MaximumPreviewTargets} 个已关联座位。");
            targets = requestedTargets.Select(NormalizeScreenTargetKey).ToArray();
            if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
                throw new InvalidDataException("屏幕预览座位不能重复选择。");
            if (targets.Any(target => !bindingsByKey.ContainsKey(target)))
                throw new MobileAuthorizationException("只能查看本堂课中已关联座位的学生电脑。");
        }
        var auditLabels = targets.ToDictionary(target => target,
            target => $"座位 {bindingsByKey[target].SeatNumber}", StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var leaseId = Guid.NewGuid();
        var lease = new ScreenPreviewLease(leaseId, device.Id, active.SessionId,
            active.CampusId, targets.ToHashSet(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ScreenTargetBinding>(bindingsByKey, StringComparer.OrdinalIgnoreCase),
            auditLabels,
            qualityProfile.Value, safeRefreshIntervalSeconds,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            new CancellationTokenSource(), now.AddSeconds(60));
        ScreenPreviewLease? previous = null;
        lock (_screenPreviewGate)
        {
            if (_screenPreviewLeases.Remove(device.Id, out var existing)) previous = existing;
            _screenPreviewLeases[device.Id] = lease;
        }
        if (previous is not null)
        {
            previous.Cancellation.Cancel();
            _ = TryAppendAudit(new MobileControlAuditEntry(now, device.Id, "classroom-screen-preview-stop", null,
                ScreenPreviewAuditTargets(previous),
                "同一手机重新打开巡视"));
        }
        var detail = $"已开启；画质 {qualityProfile.Value}、刷新 {safeRefreshIntervalSeconds} 秒，已授权 {targets.Length} 个座位；闲置 60 秒后授权到期。当前已关联 {screenTargets.Count} 个座位。";
        if (!TryAppendAudit(new MobileControlAuditEntry(now, device.Id, "classroom-screen-preview-start", null,
                ScreenPreviewAuditTargets(lease), detail)))
        {
            lock (_screenPreviewGate)
            {
                if (_screenPreviewLeases.TryGetValue(device.Id, out var current) && current.LeaseId == leaseId)
                    _screenPreviewLeases.Remove(device.Id);
            }
            lease.Cancellation.Cancel();
            throw new InvalidOperationException("无法保存屏幕巡视审计记录；本次没有开放画面。");
        }
        return new MobileScreenPreviewSessionResponse(leaseId, lease.ExpiresUtc, active.SessionId,
            Array.AsReadOnly(targets), screenTargets.Count, qualityProfile.Value, safeRefreshIntervalSeconds);
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
        removed.Cancellation.Cancel();
        if (clearSnapshots) _screenPreviewCapture.ClearSnapshots(removed.SessionId);
        _ = TryAppendAudit(new MobileControlAuditEntry(DateTimeOffset.UtcNow, device.Id,
            "classroom-screen-preview-stop", null,
            ScreenPreviewAuditTargets(removed), outcome));
        return true;
    }

    private static IReadOnlyList<string> ScreenPreviewAuditTargets(ScreenPreviewLease lease) =>
        Array.AsReadOnly(lease.Targets
            .Select(target => lease.AuditTargetLabels.TryGetValue(target, out var label) ? label : "课堂座位")
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase).ToArray());

    private static void CancelScreenPreviewLease(ScreenPreviewLease lease) => lease.Cancellation.Cancel();

    private static string NormalizeScreenTargetKey(string? value)
    {
        var candidate = value?.Trim();
        if (candidate is null || !Guid.TryParse(candidate, out var targetId) || targetId == Guid.Empty)
            throw new InvalidDataException("座位目标无效，请刷新课堂后重试。");
        return targetId.ToString("N");
    }

    private ScreenPreviewLease RenewScreenPreviewLease(Guid deviceId, Guid leaseId, Guid sessionId, string target,
        IReadOnlyDictionary<string, ScreenTargetBinding> activeBindings)
    {
        var now = DateTimeOffset.UtcNow;
        ScreenPreviewLease? revokedLease = null;
        ScreenPreviewLease? renewedLease = null;
        var valid = false;
        var tooSoon = false;
        var bindingsChanged = false;
        var clearSnapshots = false;
        lock (_screenPreviewGate)
        {
            if (_screenPreviewLeases.TryGetValue(deviceId, out var lease))
            {
                if (lease.LeaseId == leaseId && lease.SessionId == sessionId && lease.ExpiresUtc > now &&
                    !lease.Cancellation.IsCancellationRequested &&
                    !ScreenTargetBindingsEqual(lease.BindingSnapshot, activeBindings))
                {
                    _screenPreviewLeases.Remove(deviceId);
                    revokedLease = lease;
                    bindingsChanged = true;
                    clearSnapshots = !_screenPreviewLeases.Values.Any(item => item.SessionId == lease.SessionId &&
                        item.ExpiresUtc > now);
                }
                else if (lease.LeaseId != leaseId || lease.SessionId != sessionId || lease.ExpiresUtc <= now ||
                         lease.Cancellation.IsCancellationRequested || !lease.Targets.Contains(target) ||
                         !activeBindings.ContainsKey(target))
                {
                    if (lease.ExpiresUtc <= now)
                    {
                        _screenPreviewLeases.Remove(deviceId);
                        revokedLease = lease;
                        clearSnapshots = !_screenPreviewLeases.Values.Any(item => item.SessionId == lease.SessionId &&
                            item.ExpiresUtc > now);
                    }
                }
                else if (lease.LastTargetRequestUtc.TryGetValue(target, out var lastRequestedUtc) &&
                         now - lastRequestedUtc < TimeSpan.FromSeconds(lease.RefreshIntervalSeconds))
                {
                    tooSoon = true;
                }
                else
                {
                    var lastRequests = lease.LastTargetRequestUtc.ToDictionary(pair => pair.Key,
                        pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    lastRequests[target] = now;
                    var updatedLease = lease with
                    {
                        LastTargetRequestUtc = lastRequests,
                        ExpiresUtc = now.AddSeconds(60)
                    };
                    _screenPreviewLeases[deviceId] = updatedLease;
                    renewedLease = updatedLease;
                    valid = true;
                }
            }
        }
        if (revokedLease is not null)
        {
            CancelScreenPreviewLease(revokedLease);
            if (clearSnapshots) _screenPreviewCapture.ClearSnapshots(revokedLease.SessionId);
        }
        if (tooSoon)
            throw new MobileRateLimitException("屏幕画面还在所选刷新间隔内；请稍后再读取。");
        if (bindingsChanged)
            throw new MobileAuthorizationException("座位或学生信息已变化，屏幕巡视已停止；请刷新课堂后重新开启。");
        if (!valid)
            throw new MobileAuthorizationException("屏幕巡视已停止或课堂目标已改变；请重新打开巡视。");
        return renewedLease!;
    }

    private void EnsureScreenPreviewLeaseBindingsCurrent(MobilePairedDeviceView device, ScreenPreviewLease lease)
    {
        var currentBindings = ReadActiveClassroomScreenTargets(lease.CampusId, lease.SessionId)
            .ToDictionary(item => item.TargetKey, StringComparer.OrdinalIgnoreCase);
        if (ScreenTargetBindingsEqual(lease.BindingSnapshot, currentBindings)) return;
        StopScreenPreview(device, lease.LeaseId, "采集期间座位或学生信息已变化");
        throw new MobileAuthorizationException("座位或学生信息已变化，屏幕巡视已停止；请刷新课堂后重新开启。");
    }

    private static bool ScreenTargetBindingsEqual(
        IReadOnlyDictionary<string, ScreenTargetBinding> left,
        IReadOnlyDictionary<string, ScreenTargetBinding> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
            if (!right.TryGetValue(pair.Key, out var current) || pair.Value != current)
                return false;
        return true;
    }

    private static int NormalizeScreenPreviewRefreshInterval(int? requested)
    {
        var clamped = Math.Clamp(requested ?? 10, 3, 60);
        return ScreenPreviewRefreshIntervalsSeconds
            .OrderBy(value => Math.Abs(value - clamped)).ThenBy(value => value).First();
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
            CancelScreenPreviewLease(item.Value);
            _screenPreviewCapture.ClearSnapshots(item.Value.SessionId);
            _ = TryAppendAudit(new MobileControlAuditEntry(DateTimeOffset.UtcNow, item.Key,
                "classroom-screen-preview-stop", null,
                ScreenPreviewAuditTargets(item.Value), outcome));
        }
    }

    private async Task SubmitStudentClassroomEventAsync(HttpContext context)
    {
        var grant = AuthorizeStudentEventAccess(context);
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

        var limit = verified.Event.Type switch
        {
            ClassroomEventType.ClassroomChatMessage => 30,
            ClassroomEventType.ClassroomChatDelivered => 60,
            _ => 10
        };
        if (!AcceptStudentEventRate(grant.Target, limit))
            throw new MobileRateLimitException("课堂事件发送过于频繁；请稍后重试。");

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
        else if (verified.Event.Type == ClassroomEventType.ClassroomChatMessage)
        {
            lock (_classroomGate)
                if (!_classroomChatTargets.Contains(grant.Target))
                    throw new MobileAuthorizationException("该学生电脑的 Agent 未确认支持课堂聊天。");
            if (verified.Event.CorrelationId != ClassroomEventCryptography.CreateConversationId(
                    grant.SessionId, grant.Target))
                throw new InvalidDataException("学生聊天消息不属于该电脑的当前对话。");
        }
        else if (verified.Event.Type == ClassroomEventType.ClassroomChatDelivered)
        {
            var teacherEventId = verified.Event.CorrelationId
                                 ?? throw new InvalidDataException("聊天送达确认缺少教师消息 ID。");
            var original = _classroomEvents.Find(grant.SessionId, teacherEventId, DateTimeOffset.UtcNow);
            lock (_classroomGate)
                if (!_classroomChatTargets.Contains(grant.Target))
                    throw new MobileAuthorizationException("该学生电脑的 Agent 未确认支持课堂聊天。");
            if (original is null || original.Type != ClassroomEventType.ClassroomChatMessage ||
                original.Sender != ClassroomEventSender.Teacher ||
                !string.Equals(original.Target, grant.Target, StringComparison.OrdinalIgnoreCase) ||
                original.CorrelationId != ClassroomEventCryptography.CreateConversationId(
                    grant.SessionId, grant.Target))
                throw new InvalidDataException("聊天送达确认与当前学生对话不匹配。");
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
        await WriteSignedClassroomPageAsync(context, page, active.Value.SessionId, context.RequestAborted,
                includePrivateChat: false)
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

    private async Task ReadMobileClassroomQuizAsync(HttpContext context)
    {
        _ = Authorize(context);
        var active = ReadActiveClassroomIfAny();
        if (active is null)
        {
            await WriteJson(context, (ClassroomQuizTeacherSnapshot?)null, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }
        var quizIdText = context.Request.Query["quizId"].ToString();
        Guid? quizId = null;
        if (quizIdText.Length > 0)
        {
            if (!Guid.TryParseExact(quizIdText, "D", out var parsed) || parsed == Guid.Empty)
                throw new InvalidDataException("课堂测验 ID 无效。");
            quizId = parsed;
        }
        var response = ReadClassroomQuiz(quizId);
        await WriteJson(context, response, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task ReadStudentClassroomQuizAsync(HttpContext context)
    {
        var grant = AuthorizeStudentEventAccess(context);
        var active = ReadActiveClassroom();
        if (active.CampusId != grant.CampusId || active.SessionId != grant.SessionId ||
            !active.Targets.Contains(grant.Target))
            throw new MobileAuthorizationException("本堂课已结束或学生设备不属于当前课堂。");

        var now = DateTimeOffset.UtcNow;
        var teacherSnapshot = _classroomQuizzes.ReadCurrentForTeacher(grant.SessionId, now);
        if (teacherSnapshot is null)
        {
            await WriteJson(context, new ClassroomQuizStudentPage(false, null, null, null, false, null),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }
        var studentSnapshot = _classroomQuizzes.ReadForStudent(grant.SessionId, teacherSnapshot.QuizId,
            grant.Target, now);
        if (studentSnapshot is null)
        {
            await WriteJson(context, new ClassroomQuizStudentPage(false, null, null, null, false, null),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }
        using var signingKey = _openTeacherSigningKey(grant.CampusId);
        var signedQuestion = ClassroomQuizCryptography.SignQuestion(teacherSnapshot, grant.CampusId,
            signingKey.PrivateKey, now);
        var response = new ClassroomQuizStudentPage(true, signedQuestion, studentSnapshot.State,
            studentSnapshot.SelectedChoiceId, studentSnapshot.CanSubmit, studentSnapshot.ExpiresUtc);
        await WriteJson(context, response, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task SubmitStudentClassroomQuizAnswerAsync(HttpContext context)
    {
        var grant = AuthorizeStudentEventAccess(context);
        if (!AcceptStudentEventRate(grant.Target, 10))
            throw new MobileRateLimitException("课堂测验提交过于频繁；请稍后重试。");
        var quizIdText = context.Request.Query["quizId"].ToString();
        if (!Guid.TryParseExact(quizIdText, "D", out var quizId) || quizId == Guid.Empty)
            throw new InvalidDataException("课堂测验 ID 无效。");
        var signedJson = await ReadBoundedTextAsync(context.Request,
            ClassroomQuizCryptography.MaximumAnswerEnvelopeBytes, context.RequestAborted).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var quiz = _classroomQuizzes.ReadForTeacher(grant.SessionId, quizId, now);
        if (quiz is null)
        {
            await WriteJson(context, new ClassroomQuizAnswerReceipt(false,
                ClassroomQuizSubmissionStatus.Expired, quizId, Guid.Empty), context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }
        var allowedChoiceIds = quiz.Choices.Select(choice => choice.Id).ToHashSet(StringComparer.Ordinal);
        var answer = ClassroomQuizCryptography.VerifyAnswer(signedJson, grant.CampusId, grant.SessionId,
            quizId, allowedChoiceIds, grant.AgentPublicKeyPem, now);
        var status = _classroomQuizzes.Submit(grant.SessionId, quizId, grant.Target,
            answer.SubmissionId, answer.ChoiceId, now);
        var receipt = new ClassroomQuizAnswerReceipt(
            status is ClassroomQuizSubmissionStatus.Accepted or ClassroomQuizSubmissionStatus.Duplicate,
            status, quizId, answer.SubmissionId);
        await WriteJson(context, receipt, context.RequestAborted).ConfigureAwait(false);
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
        Guid? sessionId, CancellationToken cancellationToken, bool includePrivateChat = true)
    {
        if (page.SignedEnvelopes.Count != page.Events.Count || page.SignedEnvelopes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("课堂事件缺少原始签名封装，无法安全转发。");
        var selected = Enumerable.Range(0, page.Events.Count).Where(index => includePrivateChat ||
            page.Events[index].Type is not ClassroomEventType.ClassroomChatMessage and
                not ClassroomEventType.ClassroomChatDelivered).ToArray();
        var envelopes = selected.Select(index => page.SignedEnvelopes[index]!).ToArray();
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

    private bool AcceptStudentEventRate(string target, int limit)
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
            return ++window.Count <= limit;
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
        IReadOnlyDictionary<string, Guid> targetIds;
        lock (_classroomGate)
        {
            if (_classroomSessionId is null || !string.Equals(_classroomCampusId, campusId, StringComparison.Ordinal) ||
                _classroomSession is not { } active || active.SessionId != _classroomSessionId)
                return Array.Empty<ClassroomSeatLocation>();
            session = active;
            targets = _classroomTargets;
            targetIds = new Dictionary<string, Guid>(_classroomTargetIds, StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var activeTargets = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_classroomSeatGridPath))
            {
                var grid = _classroomSeatGridStore.GetLayout(session.Room.CampusProfileId, session.Room.RoomId);
                var gridLocations = ClassroomSeatGridStore.GetLocations(grid);
                var activeLocations = new List<ClassroomSeatLocation>();
                foreach (var location in gridLocations)
                {
                    if (location.VeyonObjectId is null || location.VeyonLocationObjectId is null ||
                        string.IsNullOrWhiteSpace(location.VeyonHostAddress))
                        continue;
                    var target = VeyonHostAddress.NormalizeOverride(location.VeyonHostAddress);
                    if (target.Length == 0 || !activeTargets.Contains(target)) continue;
                    activeLocations.Add(new ClassroomSeatLocation(target, location.Row, location.Column,
                        location.SeatNumber, location.StudentName, location.VeyonComputerName));
                }
                return Array.AsReadOnly(activeLocations.ToArray());
            }

            // Keep existing mobile seat positions available until the teacher saves a grid layout.
            var configuredTargets = session.Targets.Select(target => target.DeviceLabel).ToArray();
            var legacyLayout = _classroomSeatLayoutStore.GetLayout(session.Room.CampusProfileId,
                session.Room.RoomId, configuredTargets);
            var targetIdByLabel = session.Targets.ToDictionary(target => target.DeviceLabel,
                target => target.TargetId, StringComparer.OrdinalIgnoreCase);
            var hostByTargetId = targetIds.ToDictionary(pair => pair.Value, pair => pair.Key);
            var locations = new List<ClassroomSeatLocation>();
            foreach (var location in ClassroomSeatLayoutStore.GetLocations(legacyLayout))
            {
                var normalized = VeyonHostAddress.NormalizeOverride(location.Target);
                string? host = activeTargets.Contains(normalized) ? normalized : null;
                if (host is null && targetIdByLabel.TryGetValue(location.Target, out var targetId) &&
                    hostByTargetId.TryGetValue(targetId, out var mappedHost) && activeTargets.Contains(mappedHost))
                    host = mappedHost;
                if (host is null) continue;
                var computerName = string.Equals(location.Target, host, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : location.Target;
                locations.Add(new ClassroomSeatLocation(host, location.Row, location.Column,
                    StudentName: null, ComputerName: computerName));
            }
            return Array.AsReadOnly(locations.ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Seat context is optional display data; a local layout failure must not break pairing or policy APIs.
            return Array.Empty<ClassroomSeatLocation>();
        }
    }

    private IReadOnlyList<ScreenTargetBinding> ReadActiveClassroomScreenTargets(string campusId, Guid sessionId)
    {
        ClassroomSession? session;
        IReadOnlySet<string> activeTargets;
        IReadOnlyDictionary<string, Guid> targetIds;
        lock (_classroomGate)
        {
            if (_classroomSessionId != sessionId || !string.Equals(_classroomCampusId, campusId,
                    StringComparison.Ordinal) || _classroomSession is not { } current ||
                current.SessionId != sessionId)
                return Array.Empty<ScreenTargetBinding>();
            session = current;
            activeTargets = new HashSet<string>(_classroomTargets, StringComparer.OrdinalIgnoreCase);
            targetIds = new Dictionary<string, Guid>(_classroomTargetIds, StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var locations = ReadActiveClassroomSeatLocations(campusId);
            var hostByTargetId = targetIds.ToDictionary(pair => pair.Value, pair => pair.Key);
            var targetByLabel = session.Targets.ToDictionary(target => target.DeviceLabel,
                target => target, StringComparer.OrdinalIgnoreCase);
            var targetById = session.Targets.ToDictionary(target => target.TargetId);
            var bindings = new List<ScreenTargetBinding>();
            var usedIds = new HashSet<Guid>();
            var seatOrdinal = 0;
            foreach (var location in locations)
            {
                seatOrdinal++;
                var host = VeyonHostAddress.NormalizeOverride(location.Target);
                Guid targetId;
                if (!targetIds.TryGetValue(host, out targetId))
                {
                    if (!targetByLabel.TryGetValue(location.Target, out var legacyTarget) ||
                        !hostByTargetId.TryGetValue(legacyTarget.TargetId, out host))
                        continue;
                    targetId = legacyTarget.TargetId;
                }
                if (!activeTargets.Contains(host) || !usedIds.Add(targetId) ||
                    !targetById.TryGetValue(targetId, out var sessionTarget))
                    continue;

                var normalizedHost = VeyonHostAddress.NormalizeOverride(host);
                var computerName = location.ComputerName ?? sessionTarget.DeviceLabel;
                if (IPAddress.TryParse(computerName, out _)) computerName = string.Empty;
                bindings.Add(new ScreenTargetBinding(targetId, targetId.ToString("N"), normalizedHost,
                    location.SeatNumber ?? seatOrdinal, location.Row, location.Column,
                    location.StudentName ?? "", computerName));
            }

            lock (_classroomGate)
            {
                if (_classroomSessionId != sessionId || !string.Equals(_classroomCampusId, campusId,
                        StringComparison.Ordinal) || _classroomSession?.SessionId != sessionId)
                    return Array.Empty<ScreenTargetBinding>();
                return Array.AsReadOnly(bindings.Where(binding =>
                        _classroomTargetIds.TryGetValue(binding.HostAddress, out var currentTargetId) &&
                        currentTargetId == binding.TargetId && _classroomTargets.Contains(binding.HostAddress))
                    .OrderBy(binding => binding.SeatNumber).ToArray());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return Array.Empty<ScreenTargetBinding>();
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
                <title>连接教师电脑</title>
                <style>
                body{font:16px system-ui,sans-serif;line-height:1.6;margin:0;background:#f2f6f8;color:#173b4c}
                main{max-width:680px;margin:24px auto;padding:24px;background:white;border-radius:16px}
                h1{font-size:1.5rem;margin-top:0}li{margin:12px 0}.fingerprint{overflow-wrap:anywhere;background:#eef3f5;padding:12px;border-radius:8px}
                .button{display:inline-block;margin:8px 0;padding:12px 18px;border-radius:8px;background:#176d64;color:white;text-decoration:none;font-weight:700}
                .note{color:#526977;font-size:.95rem}details{margin-top:12px}summary{cursor:pointer;font-weight:600}
                </style></head><body><main>
                <h1>连接教师电脑</h1>
                <ol>
                <li><a href="/teacher-mobile-root.cer">下载安全证书</a>。首次连接时，按手机提示安装。iPhone 还需开启“完全信任”。</li>
                <li>核对下方安全码和教师电脑上的一致。</li>
                <li>打开安全连接，连接码会自动填入。填写手机名称并点“申请连接”；再请教师在电脑上允许。</li>
                </ol>
                <details>
                  <summary>iPhone 安装说明</summary>
                  <p class="note">下载后到“设置 → 通用 → VPN 与设备管理”安装；再到“设置 → 通用 → 关于本机 → 证书信任设置”，开启“完全信任”。</p>
                </details>
                <p class="fingerprint"><strong>安全码：</strong><br>{{fingerprint}}</p>
                <p><a id="secure" class="button" href="{{secureUrl}}">打开安全连接</a></p>
                <details>
                  <summary>无法连接？</summary>
                  <p class="note">确认手机和教师电脑连着同一校园 Wi-Fi，并已安装安全证书；然后重新扫码。</p>
                  <p class="note">二维码没有自动填入连接码时，请输入教师电脑窗口显示的 8 位码。</p>
                </details>
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
        if (response.State == "approved" && response.AccessToken is { } accessToken)
            context.Response.Cookies.Append(MobileResumeCookieName, accessToken, MobileResumeCookieOptions());
        await WriteJson(context, response, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task ResumeMobileSessionAsync(HttpContext context)
    {
        if (!IsSameLan(context.Connection.RemoteIpAddress) || !HasSameOrigin(context.Request))
        {
            await WriteError(context, StatusCodes.Status403Forbidden,
                "配对恢复只允许由此教师控制页在校园 LAN 发起。").ConfigureAwait(false);
            return;
        }
        if (!context.Request.HasJsonContentType())
        {
            await WriteError(context, StatusCodes.Status415UnsupportedMediaType, "配对恢复请求格式无效。")
                .ConfigureAwait(false);
            return;
        }
        var sourceAddress = context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown";
        if (!AcceptRate(_resumeRates.GetOrAdd(sourceAddress, _ => new RequestRateWindow()), 20))
        {
            await WriteError(context, StatusCodes.Status429TooManyRequests, "恢复请求过于频繁，请稍后重试。")
                .ConfigureAwait(false);
            return;
        }

        var token = context.Request.Cookies[MobileResumeCookieName];
        var device = MobilePairedDeviceStore.FindAuthorized(token, _storageDirectory);
        if (device is null)
        {
            context.Response.Cookies.Delete(MobileResumeCookieName, MobileResumeCookieOptions(delete: true));
            await WriteError(context, StatusCodes.Status401Unauthorized, "此手机配对已撤销或凭据失效。")
                .ConfigureAwait(false);
            return;
        }
        await WriteJson(context, new MobileResumeResponse(token!), context.RequestAborted).ConfigureAwait(false);
    }

    private async Task ForgetMobileSessionAsync(HttpContext context)
    {
        if (!IsSameLan(context.Connection.RemoteIpAddress) || !HasSameOrigin(context.Request))
        {
            await WriteError(context, StatusCodes.Status403Forbidden,
                "手机连接记录只允许由此教师控制页清除。").ConfigureAwait(false);
            return;
        }
        var authorization = context.Request.Headers["Authorization"].ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.Ordinal) &&
            MobilePairedDeviceStore.FindAuthorized(authorization[7..], _storageDirectory) is { } device &&
            MobilePairedDeviceStore.Revoke(device.Id, _storageDirectory))
            _stateChanged();
        context.Response.Cookies.Delete(MobileResumeCookieName, MobileResumeCookieOptions(delete: true));
        await WriteJson(context, new { forgotten = true }, context.RequestAborted).ConfigureAwait(false);
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
                current?.Application?.Supported ?? false, SupportsPolicyCompareAndSet(current?.AgentVersion),
                result.NeedsReview, Truncate(result.Detail, 300),
                current?.SystemPolicy, current?.EdgeWebsite,
                current?.Website.BlockedDomains, current?.Website.AllowedDomains);
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
        var request = await ReadJson<MobilePolicyRequest>(context.Request, 64 * 1024, context.RequestAborted)
            .ConfigureAwait(false);
        var profile = MobilePolicyProfileCompiler.Validate(FindProfile(request.ProfileId));
        var targets = await ValidateTargetsAsync(request.Targets, context.RequestAborted).ConfigureAwait(false);
        if (request.Enabled && (profile.Kind is MobilePolicyProfileKind.Application or
                MobilePolicyProfileKind.WebsiteApplication or MobilePolicyProfileKind.System))
            EnsureStudentAccountProfileCoversTargets(profile, targets);
        await _policyGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            var expected = string.IsNullOrWhiteSpace(request.ReviewToken)
                ? await ReadExpectedRevisionsAsync(targets, profile.CampusId,
                    website: profile.Kind is MobilePolicyProfileKind.Website or MobilePolicyProfileKind.WebsiteApplication,
                    application: profile.Kind is MobilePolicyProfileKind.Application or MobilePolicyProfileKind.WebsiteApplication,
                    system: profile.Kind == MobilePolicyProfileKind.System, cancellationToken: context.RequestAborted)
                    .ConfigureAwait(false)
                : null;
            if (expected is not null)
            {
                EnsureExpectedRevisionsMatch(request.ExpectedWebsiteRevisions, expected.Website, targets, "网站限制");
                EnsureExpectedRevisionsMatch(request.ExpectedApplicationRevisions, expected.Application, targets, "应用限制");
                EnsureExpectedRevisionsMatch(request.ExpectedSystemRevisions, expected.System, targets, "长期系统限制");
            }
            MobilePolicyOperationResponse response;
            if ((profile.Kind is MobilePolicyProfileKind.Application or MobilePolicyProfileKind.WebsiteApplication) &&
                request.Enabled &&
                profile.ApplicationMode == ApplicationPolicyMode.Enforce)
            {
                if (!string.IsNullOrWhiteSpace(request.ReviewToken))
                {
                    response = await CompleteApplicationEnforcementAsync(device.Id, profile, targets,
                        request.ReviewToken, context.RequestAborted).ConfigureAwait(false);
                    if (profile.Kind == MobilePolicyProfileKind.WebsiteApplication)
                        response = response with { Message = "应用限制已确认启用；组合预设的网站策略仍按审核阶段显示的逐台结果生效。" };
                }
                else
                {
                    MobilePolicyOperationResponse? websiteResponse = null;
                    if (profile.Kind == MobilePolicyProfileKind.WebsiteApplication)
                        websiteResponse = await PushPolicyAsync(ToWebsiteComponent(profile), targets, true,
                            context.RequestAborted, expected).ConfigureAwait(false);
                    var applicationResponse = await StageApplicationEnforcementAsync(device.Id, profile, targets,
                        expected?.Application ?? throw new InvalidDataException("无法读取应用限制版本；未发送策略。"),
                        context.RequestAborted).ConfigureAwait(false);
                    response = websiteResponse is null ? applicationResponse :
                        MergeCombinedProfileResponses(websiteResponse, applicationResponse,
                            "网站策略已发送；请查看应用影响检查并确认，确认前不会启用应用阻止。");
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(request.ReviewToken))
                    throw new InvalidDataException("该策略操作不接受审核票据。");
                response = await PushPolicyAsync(profile, targets, request.Enabled, context.RequestAborted, expected)
                    .ConfigureAwait(false);
            }
            var action = request.Enabled
                ? profile.Kind switch
                {
                    MobilePolicyProfileKind.Application when profile.ApplicationMode == ApplicationPolicyMode.Enforce =>
                        string.IsNullOrWhiteSpace(request.ReviewToken) ? "application-audit-preflight" : "application-enforce",
                    MobilePolicyProfileKind.WebsiteApplication when profile.ApplicationMode == ApplicationPolicyMode.Enforce =>
                        string.IsNullOrWhiteSpace(request.ReviewToken) ? "combined-application-audit-preflight" : "combined-application-enforce",
                    MobilePolicyProfileKind.WebsiteApplication => "combined-policy-enable",
                    MobilePolicyProfileKind.System => "system-policy-enable",
                    _ => "policy-enable"
                }
                : profile.Kind == MobilePolicyProfileKind.System ? "system-policy-disable" :
                    profile.Kind == MobilePolicyProfileKind.WebsiteApplication ? "combined-policy-disable" : "policy-disable";
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
    private async Task ApplyCustomWebsitePolicyAsync(HttpContext context)
    {
        var device = Authorize(context);
        var request = await ReadJson<MobileCustomWebsitePolicyRequest>(context.Request, 64 * 1024,
            context.RequestAborted).ConfigureAwait(false);
        var targets = await ValidateTargetsAsync(request.Targets, context.RequestAborted).ConfigureAwait(false);
        var campus = CurrentCampusId();
        var mode = WebsitePolicyMode.Disabled;
        var domains = Array.Empty<string>();
        IReadOnlyList<string>? allowedDomains = null;
        DateTimeOffset? expiresUtc = null;
        if (request.Enabled)
        {
            if (request.ExpiresUtc is { } requestedExpiry)
            {
                expiresUtc = requestedExpiry.ToUniversalTime();
                var blocked = WebsitePolicyCompiler.NormalizeDomains(request.BlockedDomains ?? Array.Empty<string>());
                var allowed = WebsitePolicyCompiler.NormalizeDomains(request.AllowedDomains ?? Array.Empty<string>());
                if (blocked.Count > 0 && allowed.Count > 0)
                {
                    mode = WebsitePolicyMode.Combined;
                    domains = blocked.ToArray();
                    allowedDomains = allowed;
                }
                else if (blocked.Count > 0)
                {
                    mode = WebsitePolicyMode.Blocklist;
                    domains = blocked.ToArray();
                }
                else if (allowed.Count > 0)
                {
                    mode = WebsitePolicyMode.Allowlist;
                    domains = allowed.ToArray();
                }
                else throw new InvalidDataException("请至少填写一个黑名单或白名单网站。");
            }
            else
            {
                if (request.DurationMinutes is not (>= 10 and <= 300))
                    throw new InvalidDataException("请设置有效的网站限制截止时间。");
                mode = request.Mode?.Trim().ToLowerInvariant() switch
                {
                    "blocklist" => WebsitePolicyMode.Blocklist,
                    "allowlist" => WebsitePolicyMode.Allowlist,
                    _ => throw new InvalidDataException("请选择黑名单或白名单模式。")
                };
                domains = WebsitePolicyCompiler.NormalizeDomains(request.Domains ?? Array.Empty<string>()).ToArray();
                if (domains.Length == 0)
                    throw new InvalidDataException("启用自选网站策略至少需要一个网站域名。");
            }
        }

        await _policyGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            using var signingKey = WebsitePolicySigningKeyStore.Open(campus);
            var issuedUtc = DateTimeOffset.UtcNow;
            if (request.Enabled && expiresUtc is null) expiresUtc = issuedUtc.AddMinutes(request.DurationMinutes!.Value);
            var expected = await ReadExpectedRevisionsAsync(targets, campus, website: true,
                application: false, system: false, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            EnsureExpectedRevisionsMatch(request.ExpectedWebsiteRevisions, expected.Website, targets, "网站限制");
            var expectedByTarget = expected.Website!;
            var revision = WebsitePolicyRevisionStore.Next(campus, expectedByTarget.Values.Max());
            var signedByTarget = targets.ToDictionary(target => target, target => WebsitePolicyCryptography.Sign(
                WebsitePolicyCompiler.Create(campus, revision, mode, domains, issuedUtc, expiresUtc,
                    allowedDomains, expectedRevision: expectedByTarget[target]), signingKey.PrivateKey), StringComparer.OrdinalIgnoreCase);
            var pushed = await WebsitePolicyTransport.PushAsync(targets, signedByTarget, campus, context.RequestAborted)
                .ConfigureAwait(false);
            var results = pushed.Select(ToMobileResult).ToArray();
            var response = new MobilePolicyOperationResponse("agent-accepted", false, null,
                request.Enabled
                    ? $"网站限制已发送，截止 {expiresUtc!.Value.ToLocalTime():yyyy-MM-dd HH:mm}。"
                    : "学生代理限制已解除。",
                revision, expiresUtc, results);
            var auditResults = results.Select(item => new MobileControlAuditTargetResult(item.Target,
                item.NeedsReview ? "needs-review" : item.AgentAccepted ? "agent-accepted" : "failed")).ToArray();
            var auditSaved = TryAppendAudit(new MobileControlAuditEntry(DateTimeOffset.UtcNow, device.Id,
                request.Enabled ? "custom-website-enable" : "custom-website-disable", null, targets,
                response.State, auditResults));
            _stateChanged();
            if (!auditSaved)
                response = response with { Message = response.Message + " 教师电脑无法保存本次操作日志。" };
            await WriteJson(context, response, context.RequestAborted).ConfigureAwait(false);
        }
        finally { _policyGate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private async Task<MobilePolicyOperationResponse> PushPolicyAsync(MobilePolicyProfile profile,
        IReadOnlyList<string> targets, bool enabled, CancellationToken cancellationToken,
        MobilePolicyExpectedRevisions? expected = null)
    {
        if (profile.Kind == MobilePolicyProfileKind.WebsiteApplication)
        {
            var website = await PushPolicyAsync(ToWebsiteComponent(profile), targets, enabled, cancellationToken, expected)
                .ConfigureAwait(false);
            var application = await PushPolicyAsync(ToApplicationComponent(profile), targets, enabled, cancellationToken, expected)
                .ConfigureAwait(false);
            return MergeCombinedProfileResponses(website, application,
                enabled ? "网站和应用两项策略已分别签名发送；请检查逐台结果。" :
                    "网站和应用两项策略已分别发送解除命令；请检查逐台结果。");
        }
        if (profile.Kind == MobilePolicyProfileKind.Website)
        {
            using var key = WebsitePolicySigningKeyStore.Open(profile.CampusId);
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? expires = enabled && profile.LifetimeMinutes > 0 ? now.AddMinutes(profile.LifetimeMinutes) : null;
            var mode = enabled ? profile.WebsiteMode!.Value : WebsitePolicyMode.Disabled;
            var domains = enabled ? profile.WebsiteDomains! : Array.Empty<string>();
            var allowedDomains = enabled && mode == WebsitePolicyMode.Combined
                ? profile.WebsiteAllowDomains : null;
            var expectedByTarget = expected?.Website;
            var revision = WebsitePolicyRevisionStore.Next(profile.CampusId, expectedByTarget?.Values.Max() ?? 0);
            var signedByTarget = targets.ToDictionary(target => target, target => WebsitePolicyCryptography.Sign(
                WebsitePolicyCompiler.Create(profile.CampusId, revision, mode, domains, now, expires, allowedDomains,
                    expectedByTarget is null ? null : expectedByTarget[target]), key.PrivateKey), StringComparer.OrdinalIgnoreCase);
            var results = await WebsitePolicyTransport.PushAsync(targets, signedByTarget, profile.CampusId, cancellationToken)
                .ConfigureAwait(false);
            return new MobilePolicyOperationResponse("agent-accepted", false, null,
                enabled ? "网站限制已发送。" : "网站限制已解除。",
                revision, expires, results.Select(ToMobileResult).ToArray());
        }

        if (profile.Kind == MobilePolicyProfileKind.System)
        {
            using var key = StudentSystemPolicySigningKeyStore.Open(profile.CampusId);
            var expectedByTarget = expected?.System;
            var revision = StudentSystemPolicySigningKeyStore.NextRevision(profile.CampusId,
                expectedByTarget?.Values.Max() ?? 0);
            var signedByTarget = targets.ToDictionary(target => target, target => StudentSystemPolicyCryptography.Sign(
                MobilePolicyProfileCompiler.CreateStudentSystemPolicy(profile, revision, enabled,
                    DateTimeOffset.UtcNow, expectedByTarget is null ? null : expectedByTarget[target], target), key.PrivateKey),
                StringComparer.OrdinalIgnoreCase);
            var results = await StudentSystemPolicyTransport.PushAsync(targets, signedByTarget, profile.CampusId,
                cancellationToken).ConfigureAwait(false);
            return new MobilePolicyOperationResponse("agent-accepted", false, null,
                enabled ? "长期系统限制已发送，不会自动到期。" :
                    "长期系统限制已解除；学生电脑只恢复本工具修改的设置。",
                revision, null, results.Select(ToMobileResult).ToArray());
        }

        using (var key = ApplicationPolicySigningKeyStore.Open(profile.CampusId))
        {
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? expires = enabled ? now.AddMinutes(profile.LifetimeMinutes) : null;
            var mode = enabled ? profile.ApplicationMode!.Value : ApplicationPolicyMode.Disabled;
            var expectedByTarget = expected?.Application;
            var revision = WebsitePolicyRevisionStore.Next(profile.CampusId, expectedByTarget?.Values.Max() ?? 0);
            var envelopes = MobilePolicyProfileCompiler.CreateApplicationPolicyEnvelopes(profile, targets,
                mode, revision, now, expires, key.PrivateKey, expectedByTarget);
            var results = await ApplicationPolicyTransport.PushAsync(targets, envelopes, profile.CampusId, cancellationToken)
                .ConfigureAwait(false);
            return new MobilePolicyOperationResponse("agent-accepted", false, null,
                enabled ? "影响检查已发送。" : "应用限制已解除。",
                revision, expires, results.Select(ToMobileResult).ToArray());
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<MobilePolicyOperationResponse> StageApplicationEnforcementAsync(Guid deviceId,
        MobilePolicyProfile profile, IReadOnlyList<string> targets,
        IReadOnlyDictionary<string, long> expectedRevisions, CancellationToken cancellationToken,
        Guid? classroomSessionId = null, ClassroomMode? classroomMode = null)
    {
        using var key = ApplicationPolicySigningKeyStore.Open(profile.CampusId);
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(profile.LifetimeMinutes);
        var revision = WebsitePolicyRevisionStore.Next(profile.CampusId, expectedRevisions.Values.Max());
        var signedByTarget = MobilePolicyProfileCompiler.CreateApplicationPolicyEnvelopes(profile, targets,
            ApplicationPolicyMode.Audit, revision, now, expires, key.PrivateKey, expectedRevisions);
        var push = await ApplicationPolicyTransport.PushAsync(targets, signedByTarget, profile.CampusId, cancellationToken)
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
        var fingerprint = ProfileFingerprint(FindProfile(profile.Id), targets);
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
        var expectedRevisions = targets.ToDictionary(target => target, _ => grant.AuditRevision,
            StringComparer.OrdinalIgnoreCase);
        var revision = WebsitePolicyRevisionStore.Next(profile.CampusId, grant.AuditRevision);
        var signedByTarget = MobilePolicyProfileCompiler.CreateApplicationPolicyEnvelopes(profile, targets,
            ApplicationPolicyMode.Enforce, revision, now, expires, key.PrivateKey, expectedRevisions);
        var results = await ApplicationPolicyTransport.PushAsync(targets, signedByTarget, profile.CampusId, cancellationToken)
            .ConfigureAwait(false);
        return new MobilePolicyOperationResponse("agent-accepted", false, null,
            "已向学生电脑发送应用限制；请查看逐台状态。",
            revision, expires, results.Select(ToMobileResult).ToArray());
    }

    private async Task<IReadOnlyList<string>> ValidateTargetsAsync(IReadOnlyList<string> requested,
        CancellationToken cancellationToken)
    {
        if (requested is null) throw new InvalidDataException("请至少选择一台 Veyon 机房电脑。");
        IReadOnlyList<string> targets;
        try { targets = WebsitePolicyTransport.NormalizeTargets(requested); }
        catch (InvalidDataException)
        { throw new InvalidDataException("电脑目标格式无效；请从当前 Veyon 电脑名单重新选择。"); }
        var locations = await Task.Run(VeyonNetworkObjectDirectory.ReadLocations, cancellationToken)
            .ConfigureAwait(false);
        var allowed = locations.SelectMany(location => location.Targets)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalid = targets.Where(target => !allowed.Contains(target)).ToArray();
        if (invalid.Length > 0)
            throw new InvalidDataException("所选电脑已不在教师端当前 Veyon 机房目录中；请刷新电脑名单后重试。");
        var duplicateTargets = VeyonNetworkObjectDirectory.FindTargetsInMultipleLocations(locations);
        if (targets.Any(duplicateTargets.Contains))
            throw new InvalidDataException("所选电脑目标在多个 Veyon 地点中重复；请先修复 Veyon 电脑名单后重试。");
        return targets;
    }

    private IReadOnlyList<VeyonNetworkLocation> AddStudentSeatNames(
        IReadOnlyList<VeyonNetworkLocation> locations)
    {
        var namesByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var namesByComputer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var campus in _campusDirectoryStore.Load())
            foreach (var room in campus.Rooms)
            {
                var layout = _classroomSeatGridStore.GetLayout(campus.ProfileId, room.RoomId);
                foreach (var seat in layout.Seats.Where(item => !string.IsNullOrWhiteSpace(item.StudentName)))
                {
                    var name = seat.StudentName.Trim();
                    if (!string.IsNullOrWhiteSpace(seat.VeyonHostAddress))
                        namesByHost[NormalizeSeatMapTarget(seat.VeyonHostAddress)] = name;
                    if (!string.IsNullOrWhiteSpace(seat.VeyonComputerName))
                        namesByComputer[NormalizeSeatMapTarget(seat.VeyonComputerName)] = name;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Keep Veyon's computer names available when the optional local seat map cannot be read.
        }

        return locations.Select(location =>
        {
            var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (location.TargetDisplayNames is not null)
                foreach (var pair in location.TargetDisplayNames) displayNames[pair.Key] = pair.Value;
            foreach (var target in location.Targets)
            {
                if (namesByHost.TryGetValue(NormalizeSeatMapTarget(target), out var studentName))
                    displayNames[target] = studentName;
                else if (displayNames.TryGetValue(target, out var computerName) &&
                         namesByComputer.TryGetValue(NormalizeSeatMapTarget(computerName), out studentName))
                    displayNames[target] = studentName;
            }
            return location with { TargetDisplayNames = displayNames };
        }).ToArray();
    }

    private static string NormalizeSeatMapTarget(string value)
    {
        try { return VeyonHostAddress.NormalizeOverride(value).ToLowerInvariant(); }
        catch (InvalidDataException) { return value.Trim().ToLowerInvariant(); }
    }

    [SupportedOSPlatform("windows")]
    private async Task<MobilePolicyExpectedRevisions> ReadExpectedRevisionsAsync(
        IReadOnlyList<string> targets, string campusId, bool website, bool application, bool system,
        CancellationToken cancellationToken)
    {
        var locations = await Task.Run(VeyonNetworkObjectDirectory.ReadLocations, cancellationToken)
            .ConfigureAwait(false);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locations)
        foreach (var pair in location.TargetDisplayNames ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(pair.Value) && !IPAddress.TryParse(pair.Value.Trim(), out _))
                names.TryAdd(pair.Key, pair.Value.Trim());

        using var signingKey = WebsitePolicySigningKeyStore.Open(campusId);
        var delivery = await WebsitePolicyStatusTransport.ReadAsync(targets, campusId,
            signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
        var websiteRevisions = website ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) : null;
        var applicationRevisions = application ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) : null;
        var systemRevisions = system ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) : null;
        foreach (var result in delivery)
        {
            if (!names.TryGetValue(result.Target, out var computerName))
                throw new InvalidDataException("Veyon 电脑名单缺少有效电脑名；请修复名单后再操作。");
            if (!result.Succeeded || result.Status is not { } status)
                throw new InvalidDataException($"无法验证“{computerName}”的 Agent 状态和身份；未发送策略。请先核对该电脑身份并刷新状态。");
            if (!SupportsPolicyCompareAndSet(status.AgentVersion))
                throw new InvalidDataException($"“{computerName}”的 Agent 版本不支持策略冲突保护；未发送策略。请先更新学生 Agent。");
            if (website) websiteRevisions!.Add(result.Target, status.Website.Revision);
            if (application)
            {
                if (status.Application is not { Supported: true, Revision: { } applicationRevision })
                    throw new InvalidDataException($"“{computerName}”未报告受支持的应用限制版本；未发送策略。请更新学生 Agent 后重试。");
                applicationRevisions!.Add(result.Target, applicationRevision);
            }
            if (system)
            {
                if (status.SystemPolicy is not { Supported: true, Pending: false, Revision: { } systemRevision })
                    throw new InvalidDataException($"“{computerName}”系统限制状态未知或仍待复核；未发送策略。请先检查学生电脑状态。");
                systemRevisions!.Add(result.Target, systemRevision);
            }
        }
        return new MobilePolicyExpectedRevisions(websiteRevisions, applicationRevisions, systemRevisions);
    }

    private static void EnsureExpectedRevisionsMatch(IReadOnlyDictionary<string, long>? supplied,
        IReadOnlyDictionary<string, long>? current, IReadOnlyList<string> targets, string policyName)
    {
        if (current is null)
        {
            if (supplied is { Count: > 0 })
                throw new InvalidDataException($"本次操作不包含{policyName}；请刷新手机页面后重试。");
            return;
        }
        if (supplied is null)
            throw new InvalidDataException($"手机页面没有提交已读到的{policyName}版本；请刷新状态后重新确认。");

        var normalized = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in supplied)
        {
            string target;
            try { target = WebsitePolicyTransport.NormalizeTargets([pair.Key]).Single(); }
            catch (InvalidDataException)
            { throw new InvalidDataException("手机页面提交的策略版本对应了无效电脑目标；请刷新名单后重试。"); }
            if (pair.Value < 0 || !normalized.TryAdd(target, pair.Value))
                throw new InvalidDataException($"手机页面提交的{policyName}版本无效；请刷新状态后重试。");
        }
        if (normalized.Count != targets.Count || targets.Any(target => !normalized.ContainsKey(target)))
            throw new InvalidDataException($"所选电脑或{policyName}状态已变化；请刷新状态并重新确认，未发送策略。");
        if (targets.Any(target => normalized[target] != current[target]))
            throw new InvalidDataException($"所选电脑的{policyName}状态已变化；请刷新状态并重新确认，未发送策略。");
    }

    private static bool SupportsPolicyCompareAndSet(string? agentVersion) =>
        !string.IsNullOrWhiteSpace(agentVersion) &&
        ApplicationReleaseClient.CompareVersions(agentVersion, "0.4.50") >= 0;

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
        return AcceptRate(deviceWindow, 120) && AcceptRate(_screenPreviewGlobalRate, 600);
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
            MobilePolicyProfileKind.WebsiteApplication =>
                profile.WebsiteMode!.Value.ToString() + "|" + profile.ApplicationMode!.Value.ToString(),
            MobilePolicyProfileKind.System => "长期基线",
            _ => throw new InvalidDataException("手机策略预设类型无效。")
        },
        profile.LifetimeMinutes, profile.UpdatedUtc,
        profile.Kind == MobilePolicyProfileKind.System ? profile.SystemSettings : null);

    private static MobilePolicyProfile ToWebsiteComponent(MobilePolicyProfile profile) =>
        MobilePolicyProfileCompiler.Validate(profile with
        {
            Kind = MobilePolicyProfileKind.Website,
            ApplicationMode = null,
            StudentSids = [],
            ApplicationRules = [],
            SystemSettings = null,
            SoftwareInstallationImpactReviewed = false,
            StudentSidsByTarget = null
        });

    private static MobilePolicyProfile ToApplicationComponent(MobilePolicyProfile profile) =>
        MobilePolicyProfileCompiler.Validate(profile with
        {
            Kind = MobilePolicyProfileKind.Application,
            WebsiteMode = null,
            WebsiteDomains = [],
            WebsiteAllowDomains = null,
            StudentSids = [],
            SystemSettings = null,
            SoftwareInstallationImpactReviewed = false
        });

    private static void EnsureStudentAccountProfileCoversTargets(MobilePolicyProfile profile,
        IReadOnlyList<string> targets)
    {
        var savedTargets = profile.StudentSidsByTarget;
        if (savedTargets is null)
        {
            if (targets.Count > 1)
                throw new InvalidDataException("此预设仍使用旧版共用账户，不能用于多台电脑。请在教师端读取并选择每台电脑的学生账户后重新保存预设。");
            return;
        }
        var missing = targets.Where(target => !savedTargets.ContainsKey(target)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("预设未为这些电脑保存学生账户，请先在教师端读取并选择学生账户后重新保存：" +
                string.Join("、", missing.Take(5)));
    }

    private static MobilePolicyOperationResponse MergeCombinedProfileResponses(
        MobilePolicyOperationResponse website, MobilePolicyOperationResponse application, string message)
    {
        var websiteResults = website.Results.ToDictionary(item => item.Target, StringComparer.OrdinalIgnoreCase);
        var applicationResults = application.Results.ToDictionary(item => item.Target, StringComparer.OrdinalIgnoreCase);
        var targets = websiteResults.Keys.Union(applicationResults.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var results = targets.Select(target =>
        {
            var hasWebsite = websiteResults.TryGetValue(target, out var websiteResult);
            var hasApplication = applicationResults.TryGetValue(target, out var applicationResult);
            var accepted = hasWebsite && hasApplication && websiteResult!.AgentAccepted && applicationResult!.AgentAccepted;
            var needsReview = !hasWebsite || !hasApplication || websiteResult!.NeedsReview || applicationResult!.NeedsReview || !accepted;
            var websiteStatus = !hasWebsite ? "未返回结果" : websiteResult!.AgentAccepted ? "已确认" :
                websiteResult.NeedsReview ? "需核对" : "失败";
            var applicationStatus = !hasApplication ? "未返回结果" : applicationResult!.AgentAccepted ?
                applicationResult.NeedsReview ? "待教师确认" : "已确认" :
                applicationResult.NeedsReview ? "需核对" : "失败";
            var detail = $"网站{websiteStatus}：{websiteResult?.Detail ?? "无回执"}；" +
                         $"应用{applicationStatus}：{applicationResult?.Detail ?? "无回执"}";
            return new MobilePolicyTargetResult(target, accepted, needsReview, Truncate(detail, 300));
        }).ToArray();
        return new MobilePolicyOperationResponse(application.State, application.RequiresReview,
            application.ReviewToken, message, application.Revision ?? website.Revision,
            application.ExpiresUtc ?? website.ExpiresUtc, results, application.ApplicationReview);
    }

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

    private static async Task WriteJson(HttpContext context, object? value, CancellationToken cancellationToken)
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

    private static CookieOptions MobileResumeCookieOptions(bool delete = false) => new()
    {
        Path = "/api/mobile-resume",
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = delete ? null : TimeSpan.FromDays(365)
    };

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
