using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace VeyonCampus.Core;

public enum StudentInputGuardAction
{
    Activate,
    Renew,
    Release
}

public enum StudentInputGuardPhase
{
    Unsupported,
    Unlocked,
    Starting,
    Locked,
    Unlocking,
    Expired,
    GuardExited,
    Unknown
}

public enum StudentInputGuardOutcome
{
    Succeeded,
    Failed,
    Unknown,
    Conflict,
    Unsupported,
    ReplayRejected
}

public sealed record StudentInputGuardCommand(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid RoomId,
    string DeviceFingerprint,
    string TeacherMachineId,
    Guid AgentRunId,
    Guid CommandId,
    Guid Nonce,
    Guid ExpectedControlEpoch,
    Guid LeaseId,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    StudentInputGuardAction Action,
    DateTimeOffset? LeaseExpiresUtc,
    // Persisted-lock intent. When Activate carries these, the Agent writes a protected lease
    // intent and keeps renewing on its own until LockReleaseUtc, so a student reboot (which wipes
    // the in-memory lease) is restored and the lock no longer depends on the teacher's renewal
    // loop - which stops permanently on the first failed read. Null means "legacy 20s lease",
    // keeping older peers and the 20s fast path working.
    DateTimeOffset? LockReleaseUtc = null,
    int GraceSeconds = 0);

public sealed record SignedStudentInputGuardCommand(string Payload, string Signature);

public sealed record StudentInputGuardReceipt(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid RoomId,
    string DeviceFingerprint,
    Guid AgentRunId,
    Guid CommandId,
    Guid Nonce,
    Guid PreviousControlEpoch,
    Guid CurrentControlEpoch,
    Guid LeaseId,
    StudentInputGuardAction Action,
    StudentInputGuardOutcome Outcome,
    StudentInputGuardPhase Phase,
    DateTimeOffset CompletedUtc,
    string Detail);

public sealed record StudentInputGuardStatusResponse(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid Nonce,
    DateTimeOffset CollectedUtc,
    string DeviceFingerprint,
    Guid AgentRunId,
    Guid ControlEpoch,
    bool Supported,
    StudentInputGuardPhase Phase,
    Guid? LeaseId,
    DateTimeOffset? LeaseExpiresUtc);

public sealed record StudentInputGuardStatusDeliveryResult(string Target, bool Succeeded,
    string Detail, StudentInputGuardStatusResponse? Status = null, bool NeedsReview = false,
    bool TransientUnavailable = false, TimeSpan ClockOffset = default);

public sealed record StudentInputGuardDeliveryResult(string Target, bool Succeeded, bool NeedsReview,
    StudentInputGuardOutcome? Outcome, StudentInputGuardReceipt? Receipt, string Detail)
{
    /// <summary>
    /// Student clock minus teacher clock, as measured from the signed status response. Non-zero means
    /// the two machines disagree; every signed timestamp in this protocol is compared against the
    /// other machine's clock, so a skew of minutes used to make 锁定/解除 impossible to confirm.
    /// </summary>
    public TimeSpan ClockOffset { get; init; }
}

/// <summary>Teacher-signed, device-bound commands for a temporary InputGuard lease.</summary>
public static class StudentInputGuardCommandCryptography
{
    public const string Purpose = "VeyonCampus.StudentInputGuardCommand.v1";
    public const string ReceiptPurpose = "VeyonCampus.StudentInputGuardReceipt.v1";
    public const string RequestNonceHeader = "X-VeyonCampus-Request-Nonce";
    public const int MaximumEnvelopeBytes = 16 * 1024;
    public static readonly TimeSpan MaximumCommandLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumLeaseLifetime = TimeSpan.FromSeconds(20);
    // A persisted lock may last up to one school day; bounded so a bad or stale deadline can never
    // hold a machine indefinitely.
    public static readonly TimeSpan MaximumPersistedLockLifetime = TimeSpan.FromHours(12);
    // Reboot grace is short: just enough to log in and reach the desktop. Capped so an older intent
    // file written when the teacher used a longer grace cannot keep a fresh 45s setting from taking
    // effect after an in-place upgrade.
    public const int MaximumRebootGraceSeconds = 45;
    // The band a persisted intent has to fall into before the student will accept the command.
    public const int MinimumPersistedGraceSeconds = 30;
    public const int MaximumPersistedGraceSeconds = 600;
    private static readonly JsonSerializerOptions JsonOptions = ApplicationPolicyCompiler.JsonOptions;

    /// <summary>
    /// A persisted lock must carry a reboot grace inside <see cref="MinimumPersistedGraceSeconds"/>..
    /// <see cref="MaximumPersistedGraceSeconds"/>, because the student rejects the whole command when
    /// it does not. A caller that never picked one - the phone only sends a duration - used to hand
    /// over 0, so every timed single-device lock from the phone failed with "InputGuard 命令...无效"
    /// while the desktop worked (it passes 45). Anything missing or out of range is clamped into the
    /// accepted band here instead of travelling to the student as an invalid command.
    /// </summary>
    public static int NormalizePersistedGraceSeconds(int requestedSeconds) => requestedSeconds switch
    {
        0 => MaximumRebootGraceSeconds,
        < MinimumPersistedGraceSeconds => MinimumPersistedGraceSeconds,
        > MaximumPersistedGraceSeconds => MaximumPersistedGraceSeconds,
        _ => requestedSeconds
    };

    public static string Sign(StudentInputGuardCommand command, RSA campusPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(campusPrivateKey);
        Validate(command, command.IssuedUtc);
        if (campusPrivateKey.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("InputGuard 校区签名密钥位长不受支持。");
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions);
        if (payload.Length > 8192) throw new InvalidDataException("InputGuard 命令超过大小限制。");
        var envelope = new SignedStudentInputGuardCommand(Convert.ToBase64String(payload),
            Convert.ToBase64String(campusPrivateKey.SignData(payload, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss)));
        var signed = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(signed) > MaximumEnvelopeBytes)
            throw new InvalidDataException("InputGuard 命令超过大小限制。");
        return signed;
    }

    public static StudentInputGuardCommand Verify(string signedJson, string campusPublicKeyPem,
        string expectedCampusId, string expectedDeviceFingerprint, Guid expectedAgentRunId,
        Guid expectedControlEpoch, string expectedAgentMachineId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(campusPublicKeyPem);
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        ValidateFingerprint(expectedDeviceFingerprint);
        ValidateMachineId(expectedAgentMachineId);
        if (expectedAgentRunId == Guid.Empty || expectedControlEpoch == Guid.Empty ||
            Encoding.UTF8.GetByteCount(signedJson) > MaximumEnvelopeBytes)
            throw new InvalidDataException("InputGuard 命令核验上下文无效。");

        var envelopeBytes = Encoding.UTF8.GetBytes(signedJson);
        PolicyJson.RejectDuplicateFields(envelopeBytes);
        var envelope = JsonSerializer.Deserialize<SignedStudentInputGuardCommand>(signedJson, JsonOptions)
                       ?? throw new InvalidDataException("InputGuard 签名信封为空。");
        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("InputGuard 签名信封编码无效。", exception);
        }
        if (payload.Length is 0 or > 8192 || signature.Length is < 256 or > 512)
            throw new InvalidDataException("InputGuard 签名字段大小无效。");
        PolicyJson.RejectDuplicateFields(payload);
        using var publicKey = RSA.Create();
        publicKey.ImportFromPem(campusPublicKeyPem);
        if (publicKey.KeySize is < 2048 or > 4096 ||
            !publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("InputGuard 命令签名无效。");
        var command = JsonSerializer.Deserialize<StudentInputGuardCommand>(payload, JsonOptions)
                      ?? throw new InvalidDataException("InputGuard 命令正文为空。");
        Validate(command, nowUtc);
        if (!string.Equals(command.CampusId, expectedCampusId, StringComparison.Ordinal) ||
            !string.Equals(command.DeviceFingerprint, expectedDeviceFingerprint, StringComparison.Ordinal) ||
            command.AgentRunId != expectedAgentRunId || command.ExpectedControlEpoch != expectedControlEpoch ||
            string.Equals(command.TeacherMachineId, expectedAgentMachineId, StringComparison.Ordinal))
            throw new InvalidDataException("InputGuard 命令的校区、设备身份、Agent 运行代次或控制代次已变化。");
        if (!payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions)))
            throw new InvalidDataException("InputGuard 命令正文不是规范编码。");
        return command;
    }

    public static void ValidateReceipt(StudentInputGuardReceipt receipt,
        StudentInputGuardCommand command, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(command);
        var now = nowUtc.ToUniversalTime();
        var phaseMatchesSuccessfulAction = command.Action switch
        {
            StudentInputGuardAction.Activate or StudentInputGuardAction.Renew =>
                receipt.Phase == StudentInputGuardPhase.Locked,
            StudentInputGuardAction.Release =>
                receipt.Phase is StudentInputGuardPhase.Unlocked or StudentInputGuardPhase.Expired,
            _ => false
        };
        if (receipt.SchemaVersion != 1 || receipt.Purpose != ReceiptPurpose ||
            receipt.CampusId != command.CampusId || receipt.RoomId != command.RoomId ||
            receipt.DeviceFingerprint != command.DeviceFingerprint || receipt.AgentRunId != command.AgentRunId ||
            receipt.CommandId != command.CommandId || receipt.Nonce != command.Nonce ||
            receipt.PreviousControlEpoch != command.ExpectedControlEpoch || receipt.LeaseId != command.LeaseId ||
            receipt.Action != command.Action || !Enum.IsDefined(receipt.Outcome) ||
            !Enum.IsDefined(receipt.Phase) || receipt.CurrentControlEpoch == Guid.Empty ||
            receipt.CompletedUtc.Offset != TimeSpan.Zero || receipt.CompletedUtc < command.IssuedUtc ||
            receipt.CompletedUtc > now.AddMinutes(1) || receipt.CompletedUtc < now.Subtract(TimeSpan.FromMinutes(2)) ||
            string.IsNullOrWhiteSpace(receipt.Detail) || receipt.Detail.Length > 1024 ||
            receipt.Detail.Any(char.IsControl) ||
            (receipt.Outcome == StudentInputGuardOutcome.Succeeded &&
             (receipt.CurrentControlEpoch == receipt.PreviousControlEpoch ||
              !phaseMatchesSuccessfulAction)))
            throw new InvalidDataException("InputGuard 签名回执与本次目标、命令或状态不匹配。");
    }

    public static void ValidateStatus(StudentInputGuardStatusResponse status,
        WebsitePolicyStatusRequest request, string expectedDeviceFingerprint, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(request);
        ValidateFingerprint(expectedDeviceFingerprint);
        var now = nowUtc.ToUniversalTime();
        if (status.SchemaVersion != 1 || status.Purpose != "VeyonCampus.StudentInputGuardStatus.v1" ||
            status.CampusId != request.CampusId || status.Nonce != request.Nonce ||
            status.CollectedUtc.Offset != TimeSpan.Zero ||
            // The response is bound to a fresh teacher nonce and signed by the pinned Agent, so replay
            // is already excluded. Comparing CollectedUtc against the teacher clock with a two-minute
            // window additionally required both machines to agree within two minutes: one classroom PC
            // with a drifting clock then reported "状态无法核验" for every read, and an already locked
            // machine could not be released until its deadline. The window is now only a sanity bound
            // that matches the longest persisted lease, and the measured difference is reported so the
            // caller can translate timestamps instead of failing.
            status.CollectedUtc > now.Add(MaximumPersistedLockLifetime) ||
            status.CollectedUtc < now.Subtract(MaximumPersistedLockLifetime) ||
            status.DeviceFingerprint != expectedDeviceFingerprint || status.AgentRunId == Guid.Empty ||
            status.ControlEpoch == Guid.Empty || !Enum.IsDefined(status.Phase) ||
            status.Supported == (status.Phase == StudentInputGuardPhase.Unsupported) ||
            status.Supported && (status.LeaseId is { } leaseId && leaseId == Guid.Empty) ||
            status.LeaseExpiresUtc is { Offset: var leaseOffset } && leaseOffset != TimeSpan.Zero ||
            (status.LeaseId is null) != (status.LeaseExpiresUtc is null) ||
            status.LeaseExpiresUtc is { } expires && (expires <= status.CollectedUtc ||
                expires > status.CollectedUtc.Add(MaximumLeaseLifetime)) ||
            status.Phase == StudentInputGuardPhase.Locked && status.LeaseId is null ||
            !status.Supported && (status.LeaseId is not null || status.LeaseExpiresUtc is not null))
            throw new InvalidDataException("InputGuard 签名状态字段或请求绑定无效。");
    }

    private static void Validate(StudentInputGuardCommand command, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(command);
        WebsitePolicySigningKeyStore.ValidateCampusId(command.CampusId);
        ValidateFingerprint(command.DeviceFingerprint);
        ValidateMachineId(command.TeacherMachineId);
        var now = nowUtc.ToUniversalTime();
        var actionRequiresLease = command.Action is StudentInputGuardAction.Activate or StudentInputGuardAction.Renew;
        var leaseExpiryIsValid = command.LeaseExpiresUtc is { } leaseExpiry &&
            leaseExpiry.Offset == TimeSpan.Zero && leaseExpiry > now &&
            leaseExpiry <= now.Add(MaximumLeaseLifetime) && leaseExpiry > command.IssuedUtc &&
            leaseExpiry - command.IssuedUtc <= MaximumLeaseLifetime;
        // Persisted intent is only meaningful on Activate: a custom absolute release time plus a
        // bounded teacher-offline grace period (0 = legacy short lease, no persistence).
        var hasIntent = command.LockReleaseUtc is not null || command.GraceSeconds != 0;
        var intentIsValid = !hasIntent ||
            (command.Action == StudentInputGuardAction.Activate &&
             command.LockReleaseUtc is { } release && release.Offset == TimeSpan.Zero &&
             release > now && release <= now.Add(MaximumPersistedLockLifetime) &&
             command.GraceSeconds is >= MinimumPersistedGraceSeconds and <= MaximumPersistedGraceSeconds);
        if (command.SchemaVersion != 1 || command.Purpose != Purpose || command.RoomId == Guid.Empty ||
            command.AgentRunId == Guid.Empty || command.CommandId == Guid.Empty || command.Nonce == Guid.Empty ||
            command.ExpectedControlEpoch == Guid.Empty || command.LeaseId == Guid.Empty ||
            !Enum.IsDefined(command.Action) || command.IssuedUtc.Offset != TimeSpan.Zero ||
            command.ExpiresUtc.Offset != TimeSpan.Zero || command.IssuedUtc < now.Subtract(MaximumCommandLifetime) ||
            command.IssuedUtc > now.AddSeconds(5) || command.ExpiresUtc <= now ||
            command.ExpiresUtc > now.Add(MaximumCommandLifetime) || command.ExpiresUtc <= command.IssuedUtc ||
            command.ExpiresUtc - command.IssuedUtc > MaximumCommandLifetime ||
            (actionRequiresLease ? !leaseExpiryIsValid : command.LeaseExpiresUtc is not null) ||
            !intentIsValid)
            throw new InvalidDataException("InputGuard 命令版本、身份、动作、租约或有效期无效。");
    }

    private static void ValidateFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (fingerprint.Length != 64 || fingerprint != fingerprint.ToUpperInvariant() ||
            !fingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("InputGuard 设备指纹格式无效。");
    }

    private static void ValidateMachineId(string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        if (machineId.Length != 32 || machineId != machineId.ToUpperInvariant() ||
            !machineId.All(Uri.IsHexDigit) || !Guid.TryParseExact(machineId, "N", out var parsed) ||
            parsed == Guid.Empty)
            throw new InvalidDataException("InputGuard 教师电脑身份格式无效。");
    }
}

/// <summary>Local Agent-to-Guard messages. The named pipe ACL and peer process check are mandatory.</summary>
public enum StudentInputGuardIpcOperation
{
    ReadStatus,
    Execute
}

public sealed record StudentInputGuardIpcCommand(
    int SchemaVersion,
    Guid RequestId,
    StudentInputGuardIpcOperation Operation,
    StudentInputGuardAction? Action,
    Guid? LeaseId,
    DateTimeOffset? LeaseExpiresUtc);

public sealed record StudentInputGuardIpcReceipt(
    int SchemaVersion,
    Guid RequestId,
    bool Supported,
    StudentInputGuardPhase Phase,
    Guid? LeaseId,
    DateTimeOffset? LeaseExpiresUtc,
    StudentInputGuardOutcome Outcome,
    DateTimeOffset ReportedUtc,
    string Detail);

/// <summary>Agent-side lease coordinator for the verified interactive-session Guard process.</summary>
[SupportedOSPlatform("windows")]
internal sealed class StudentInputGuardAgentState
{
    public const string StatusPurpose = "VeyonCampus.StudentInputGuardStatus.v1";
    private readonly string _deviceFingerprint;
    private readonly string? _configPath;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _gate = new();
    private readonly HashSet<Guid> _commandIds = [];
    private readonly Queue<Guid> _commandOrder = [];
    private StudentInputGuardPhase? _lastPhase;
    private Guid? _lastLeaseId;
    private DateTimeOffset? _lastLeaseExpiryUtc;
    private string? _lastGuardFailure;
    private DateTimeOffset _nextGuardLaunchUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan GuardLaunchRetryInterval = TimeSpan.FromSeconds(30);
    private DateTimeOffset _nextSessionSnapshotUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan SessionSnapshotInterval = TimeSpan.FromSeconds(10);
    private Guid _controlEpoch = Guid.NewGuid();

    // Persisted lock intent: the Agent keeps the lease alive itself until the deadline, so a
    // student reboot (which wipes the in-memory lease) is restored and the lock no longer depends
    // on the teacher's renewal loop, which stops permanently on the first failed read.
    private InputGuardLockIntent? _persistentIntent;
    private bool _intentRestoredFromDisk;
    private DateTimeOffset _lastTeacherContactUtc = DateTimeOffset.MinValue;
    private Task? _intentKeeperTask;
    private static readonly TimeSpan IntentKeeperInterval = TimeSpan.FromSeconds(5);

    public StudentInputGuardAgentState(RSA agentIdentityKey, string? configPath = null)
    {
        ArgumentNullException.ThrowIfNull(agentIdentityKey);
        _configPath = configPath;
        _deviceFingerprint = StudentAgentResponseCryptography.GetFingerprint(
            agentIdentityKey.ExportSubjectPublicKeyInfoPem());
        AgentRunId = Guid.NewGuid();
    }

    public Guid AgentRunId { get; }
    public string DeviceFingerprint => _deviceFingerprint;
    public Guid ControlEpoch
    {
        get { lock (_gate) return _controlEpoch; }
    }

    public async Task<StudentInputGuardStatusResponse> ReadStatusAsync(WebsitePolicyStatusRequest request,
        DateTimeOffset nowUtc, CancellationToken cancellationToken, TimeSpan? responseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var guard = await StudentInputGuardIpcClient.ReadStatusAsync(cancellationToken, responseTimeout)
            .ConfigureAwait(false);
        guard = await EnsureGuardAvailableAsync(guard, cancellationToken).ConfigureAwait(false);
        var phase = guard.Supported ? guard.Phase : StudentInputGuardPhase.Unsupported;
        var leaseId = guard.Supported ? guard.LeaseId : null;
        var leaseExpiresUtc = guard.Supported ? guard.LeaseExpiresUtc : null;
        ObserveGuardAvailability(guard);
        await LogSessionSnapshotAsync(phase, leaseId, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            ObserveGuardState(phase, leaseId, leaseExpiresUtc);
            return new StudentInputGuardStatusResponse(1, StatusPurpose, request.CampusId, request.Nonce,
                nowUtc.ToUniversalTime(), _deviceFingerprint, AgentRunId, _controlEpoch,
                guard.Supported, phase, leaseId, leaseExpiresUtc);
        }
    }

    public async Task<StudentInputGuardReceipt> ExecuteAsync(StudentInputGuardCommand command,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var commandNow = nowUtc.ToUniversalTime();
            var guard = await StudentInputGuardIpcClient.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            guard = await EnsureGuardAvailableAsync(guard, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                var phase = guard.Supported ? guard.Phase : StudentInputGuardPhase.Unsupported;
                ObserveGuardState(phase, guard.Supported ? guard.LeaseId : null,
                    guard.Supported ? guard.LeaseExpiresUtc : null);
                if (_controlEpoch != command.ExpectedControlEpoch)
                    return CreateReceipt(command, StudentInputGuardOutcome.Conflict, phase, commandNow,
                        "Guard 状态在确认后已改变；没有执行本次命令。", command.ExpectedControlEpoch);
                if (!_commandIds.Add(command.CommandId))
                    return CreateReceipt(command, StudentInputGuardOutcome.ReplayRejected, phase, commandNow,
                        "已拒绝重复的 InputGuard 命令编号。", command.ExpectedControlEpoch);
                _commandOrder.Enqueue(command.CommandId);
                while (_commandOrder.Count > 4096) _commandIds.Remove(_commandOrder.Dequeue());
            }

            var result = await StudentInputGuardIpcClient.ExecuteAsync(command, cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                var previousEpoch = _controlEpoch;
                ObserveGuardState(result.Phase, result.LeaseId, result.LeaseExpiresUtc);
                if (result.Outcome == StudentInputGuardOutcome.Succeeded && _controlEpoch == previousEpoch)
                    _controlEpoch = Guid.NewGuid();
                // Persist the lock intent only after the Guard confirmed the lock. The Agent then
                // owns the lease and keeps renewing itself; the teacher's renewal loop is redundant
                // for this lease and may stop without dropping the lock.
                if (result.Outcome == StudentInputGuardOutcome.Succeeded &&
                    command.Action == StudentInputGuardAction.Activate && command.LockReleaseUtc is { } releaseUtc)
                {
                    lock (_gate)
                    {
                        _persistentIntent = new InputGuardLockIntent(command.LeaseId, command.IssuedUtc,
                            releaseUtc, command.GraceSeconds, command.IssuedUtc);
                        _intentRestoredFromDisk = false;
                        _lastTeacherContactUtc = commandNow;
                    }
                    if (_configPath is not null)
                    {
                        try
                        {
                            InputGuardLockIntentStore.Write(_configPath, command.CampusId, command.LeaseId,
                                command.IssuedUtc, releaseUtc, command.GraceSeconds);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                              InvalidDataException or NotSupportedException or
                                                              System.Security.SecurityException)
                        {
                            // Keep the in-memory intent even if persistence failed this once; the
                            // current lock still works, only reboot-restore would be unavailable.
                            ReportIntentNote("persist-failed", exception.Message);
                        }
                    }
                    EnsureIntentKeeper(command.CampusId);
                }
                else if (result.Outcome == StudentInputGuardOutcome.Succeeded &&
                         command.Action == StudentInputGuardAction.Release &&
                         _persistentIntent is { } releasing && releasing.LeaseId == command.LeaseId)
                {
                    lock (_gate)
                    {
                        _persistentIntent = null;
                        _intentRestoredFromDisk = false;
                    }
                    if (_configPath is not null)
                    {
                        try { InputGuardLockIntentStore.Clear(_configPath); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                              System.Security.SecurityException)
                        {
                            ReportIntentNote("clear-failed", exception.Message);
                        }
                    }
                }
                else if (result.Outcome == StudentInputGuardOutcome.Succeeded &&
                         command.Action == StudentInputGuardAction.Renew &&
                         _persistentIntent is { } renewing && renewing.LeaseId == command.LeaseId)
                {
                    // A signed teacher renewal is proof the teacher is still engaged; it extends the
                    // offline grace window without changing the absolute deadline.
                    lock (_gate) _lastTeacherContactUtc = commandNow;
                }
                if (_configPath is not null)
                {
                    try
                    {
                        WebsitePolicyAgentInstaller.ReportAgentOperationNote(_configPath, "input-guard-command",
                            $"{command.Action}: {result.Outcome}, phase={result.Phase}, " +
                            $"lease={command.LeaseId.ToString("N")[..8]}, detail={result.Detail}");
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                          InvalidOperationException or ArgumentException or
                                                          System.Security.SecurityException)
                    {
                        // Diagnostics must never change control behaviour.
                    }
                }
                return CreateReceipt(command, result.Outcome, result.Phase, result.ReportedUtc,
                    result.Detail, command.ExpectedControlEpoch);
            }
        }
        finally { _commandGate.Release(); }
    }

    /// <summary>
    /// Writes the pipe client's own reason into the Agent log when the interactive Guard cannot be
    /// read. The signed status protocol carries only a phase, so "Guard 未在时限内回应" and
    /// "检测到多个活动交互桌面会话" both reach the teacher as the same "状态无法核验" row; this
    /// note is what turns that into an answer without widening the signed protocol.
    /// </summary>
    private void ObserveGuardAvailability(StudentInputGuardIpcReceipt guard)
    {
        var failure = !guard.Supported || guard.Phase == StudentInputGuardPhase.Unknown
            ? guard.Detail
            : null;
        lock (_gate)
        {
            if (string.Equals(failure, _lastGuardFailure, StringComparison.Ordinal)) return;
            _lastGuardFailure = failure;
        }
        if (failure is not null && _configPath is not null)
            WebsitePolicyAgentInstaller.ReportAgentOperationNote(_configPath, "input-guard-status", failure);
    }

    /// <summary>
    /// Starts the interactive Guard when it is missing and re-reads the state once.
    /// </summary>
    /// <remarks>
    /// Student Companion is the normal starter, but a machine can be logged in without it (it was
    /// never launched, it was closed, or the Guard exited). In that state every signed command used
    /// to fail with "状态无法核验" while nothing on the teacher side could fix it. The Agent runs as
    /// SYSTEM, so it can start the same protected executable inside the user's session itself.
    ///
    /// Attempts are throttled: a machine with a permanently unusable session must not spawn a new
    /// process on every 15-second status poll.
    /// </remarks>
    private async Task<StudentInputGuardIpcReceipt> EnsureGuardAvailableAsync(
        StudentInputGuardIpcReceipt guard, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return guard;
        if (guard.Supported && guard.Phase != StudentInputGuardPhase.Unknown) return guard;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (now < _nextGuardLaunchUtc) return guard;
            _nextGuardLaunchUtc = now.Add(GuardLaunchRetryInterval);
        }
        string detail;
        bool started;
        try
        {
            // The status read just failed, so a Guard process that exists is not answering. Ending
            // and restarting it is the only way back from a Guard whose lease thread already gave up.
            started = InteractiveGuardLauncher.TryLaunchInActiveSessions(
                StudentInputGuardIpcClient.GetGuardExecutablePath(), restartUnresponsive: true, out detail);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                              UnauthorizedAccessException or PlatformNotSupportedException or
                                              System.ComponentModel.Win32Exception or
                                              System.Security.SecurityException)
        {
            started = false;
            detail = "无法启动交互桌面 Guard：" + exception.Message;
        }
        if (_configPath is not null)
            WebsitePolicyAgentInstaller.ReportAgentOperationNote(_configPath, "guard-launch", detail);
        if (!started) return guard;
        // A fresh Guard needs its runtime, pipe and lease thread before it answers; a single 700 ms
        // read raced the start often enough to keep reporting "unknown" for another 15 seconds.
        try
        {
            StudentInputGuardIpcReceipt? latest = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken).ConfigureAwait(false);
                latest = await StudentInputGuardIpcClient.ReadStatusAsync(cancellationToken,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (latest.Supported && latest.Phase != StudentInputGuardPhase.Unknown) break;
            }
            return latest ?? guard;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
    }

    /// <summary>
    /// Starts the single background keeper that owns a persisted lock. It renews the short Guard
    /// lease every 5 seconds, restores the lock after a reboot, releases at the absolute deadline,
    /// and fail-opens after the teacher-offline grace. The teacher's own renewal loop is therefore
    /// no longer required to hold this lease.
    /// </summary>
    private void EnsureIntentKeeper(string campusId)
    {
        var existing = _intentKeeperTask;
        if (existing is not null && !existing.IsCompleted) return;
        // The Agent runs as a machine-lifetime SYSTEM task; the keeper shares that lifetime.
        _intentKeeperTask = Task.Run(() => IntentKeeperLoopAsync(campusId, CancellationToken.None),
            CancellationToken.None);
    }

    /// <summary>
    /// Called once at Agent startup: reads a still-valid persisted intent and re-arms the keeper so
    /// a student reboot cannot escape the lock. The first restart applies the configured grace
    /// window (giving the student time to reach the desktop) and persists it, so repeated reboots
    /// cannot push the lock back forever.
    /// </summary>
    internal async Task RestorePersistedIntentAsync(string campusId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || _configPath is null) return;
        InputGuardLockIntent? intent;
        try { intent = InputGuardLockIntentStore.Read(_configPath, campusId, DateTimeOffset.UtcNow); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or NotSupportedException or
                                              System.Security.SecurityException)
        {
            ReportIntentNote("restore-read-failed", exception.Message);
            return;
        }
        if (intent is null) return;
        var now = DateTimeOffset.UtcNow;
        // The grace clock starts when the interactive desktop actually becomes usable, not when the
        // SYSTEM Agent boots (which is before the student logs in). The keeper anchors it on the
        // first tick that sees a usable Guard. Marking the source lets it tell a reboot restore from
        // a lock established in this very process.
        lock (_gate)
        {
            _persistentIntent = intent;
            _intentRestoredFromDisk = true;
            _lastTeacherContactUtc = now;
        }
        ReportIntentNote("restored", $"lease={intent.LeaseId.ToString("N")[..8]}, " +
            $"resumeAfter={intent.ResumeAfterUtc:O}, release={intent.ReleaseUtc:O}");
        EnsureIntentKeeper(campusId);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task IntentKeeperLoopAsync(string campusId, CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(IntentKeeperInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                InputGuardLockIntent? intent;
                bool restoredFromDisk;
                lock (_gate)
                {
                    intent = _persistentIntent;
                    restoredFromDisk = _intentRestoredFromDisk;
                }
                if (intent is null) return;
                var now = DateTimeOffset.UtcNow;
                if (now >= intent.ReleaseUtc)
                {
                    await ReleasePersistedAsync(campusId, intent.LeaseId, "deadline", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                // The absolute deadline (or an explicit teacher 解除) is the only way this lock ends.
                // There is deliberately no teacher-offline fail-open: the teacher closing the app
                // must not lift a lock that should hold for the lesson. Safety comes from the bounded
                // deadline, which guarantees the machine is never held indefinitely.

                // Anchor the reboot grace only once the interactive desktop is usable (a Guard
                // answers), so the student has the full grace window to log in and reach it.
                if (restoredFromDisk && intent.GraceSeconds > 0 && intent.ResumeAfterUtc <= intent.IssuedUtc)
                {
                    var guardNow = await StudentInputGuardIpcClient.ReadStatusAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!guardNow.Supported || guardNow.Phase == StudentInputGuardPhase.Unknown)
                    {
                        guardNow = await EnsureGuardAvailableAsync(guardNow, cancellationToken).ConfigureAwait(false);
                    }
                    if (guardNow.Supported && guardNow.Phase != StudentInputGuardPhase.Unknown)
                    {
                        var resume = now.AddSeconds(Math.Min(intent.GraceSeconds,
                            StudentInputGuardCommandCryptography.MaximumRebootGraceSeconds));
                        if (resume >= intent.ReleaseUtc) resume = intent.ReleaseUtc;
                        intent = intent with { ResumeAfterUtc = resume };
                        lock (_gate)
                        {
                            _persistentIntent = intent;
                            _intentRestoredFromDisk = false;
                            _lastTeacherContactUtc = now;
                        }
                        if (_configPath is not null)
                        {
                            try
                            {
                                InputGuardLockIntentStore.Write(_configPath, campusId, intent.LeaseId,
                                    intent.IssuedUtc, intent.ReleaseUtc, intent.GraceSeconds, resume);
                            }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                                  InvalidDataException or NotSupportedException or
                                                                  System.Security.SecurityException)
                            {
                                ReportIntentNote("grace-persist-failed", exception.Message);
                            }
                        }
                        ReportIntentNote("grace-anchored", $"resumeAfter={resume:O}");
                    }
                    else
                    {
                        // Desktop not ready yet; keep waiting before starting the grace clock.
                        continue;
                    }
                }

                if (now < intent.ResumeAfterUtc) continue;

                var guard = await StudentInputGuardIpcClient.ReadStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!guard.Supported || guard.Phase == StudentInputGuardPhase.Unknown)
                {
                    guard = await EnsureGuardAvailableAsync(guard, cancellationToken).ConfigureAwait(false);
                }
                if (guard.Phase == StudentInputGuardPhase.Locked && guard.LeaseId == intent.LeaseId)
                {
                    await SendLocalAsync(intent.LeaseId, now.Add(StudentInputGuardCommandCryptography.MaximumLeaseLifetime),
                        StudentInputGuardAction.Renew, campusId, cancellationToken).ConfigureAwait(false);
                }
                else if (guard.Phase is StudentInputGuardPhase.Unlocked or StudentInputGuardPhase.Expired or
                             StudentInputGuardPhase.GuardExited)
                {
                    // Reboot or Guard restart wiped the lease: re-lock under the same persisted id.
                    await SendLocalAsync(intent.LeaseId, now.Add(StudentInputGuardCommandCryptography.MaximumLeaseLifetime),
                        StudentInputGuardAction.Activate, campusId, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            lock (_gate) _intentKeeperTask = null;
        }
    }

    private async Task ReleasePersistedAsync(string campusId, Guid leaseId, string reason,
        CancellationToken cancellationToken)
    {
        await SendLocalAsync(leaseId, null, StudentInputGuardAction.Release, campusId, cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _persistentIntent = null;
            _intentRestoredFromDisk = false;
        }
        if (_configPath is not null)
        {
            try { InputGuardLockIntentStore.Clear(_configPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  System.Security.SecurityException)
            {
                ReportIntentNote("clear-failed", exception.Message);
            }
        }
        ReportIntentNote("released", $"reason={reason}, lease={leaseId.ToString("N")[..8]}");
    }

    /// <summary>Sends a local signed-equivalent command straight to the Guard (Agent is trusted).</summary>
    private async Task SendLocalAsync(Guid leaseId, DateTimeOffset? leaseExpiresUtc,
        StudentInputGuardAction action, string campusId, CancellationToken cancellationToken)
    {
        // The IPC hop is a trusted local boundary - only the SYSTEM Agent can reach the Guard pipe.
        // StudentInputGuardIpcClient.ExecuteAsync consumes CommandId/Action/LeaseId/LeaseExpiresUtc
        // only, so identity fields are filled with valid placeholders rather than re-read.
        var issued = DateTimeOffset.UtcNow;
        var command = new StudentInputGuardCommand(1, StudentInputGuardCommandCryptography.Purpose,
            campusId, Guid.Empty, _deviceFingerprint, "local-agent",
            AgentRunId, Guid.NewGuid(), Guid.NewGuid(), _controlEpoch, leaseId,
            issued, issued.Add(StudentInputGuardCommandCryptography.MaximumCommandLifetime),
            action, leaseExpiresUtc);
        var receipt = await StudentInputGuardIpcClient.ExecuteAsync(command, cancellationToken)
            .ConfigureAwait(false);
        lock (_gate) ObserveGuardState(receipt.Phase, receipt.LeaseId, receipt.LeaseExpiresUtc);
        ReportIntentNote("keeper", $"{action}: {receipt.Outcome}, phase={receipt.Phase}, {receipt.Detail}");
    }

    private void ReportIntentNote(string stage, string detail)
    {
        if (_configPath is null) return;
        try
        {
            var text = $"{stage}: {detail}";
            if (text.Length > 600) text = text[..600];
            WebsitePolicyAgentInstaller.ReportAgentOperationNote(_configPath, "input-guard-intent", text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidOperationException or ArgumentException or
                                              System.Security.SecurityException)
        {
            // Diagnostics never change control behaviour.
        }
    }

    private void ObserveGuardState(StudentInputGuardPhase phase, Guid? leaseId,
        DateTimeOffset? leaseExpiresUtc)
    {
        // The control epoch exists to stop a stale signed command from being replayed after the
        // control state actually changed. A renewal deliberately keeps the same lease and only
        // pushes its expiry forward, so expiry is metadata, not a state transition. Counting an
        // expiry refresh as a transition used to rotate the epoch on every 5-second renewal; with
        // two interactive sessions the status read and the command's own pre-read could land on
        // guards whose expiry timestamps differ slightly, rotating the epoch in between and making
        // the renew fail with "状态已改变" - which then cascaded into "租约不匹配" once the lease
        // expired. Flip only on phase or lease identity changes; a successful command that leaves
        // the epoch untouched still rotates it below (see ExecuteAsync).
        var changed = _lastPhase is { } previousPhase &&
                      (previousPhase != phase || _lastLeaseId != leaseId);
        _lastPhase = phase;
        _lastLeaseId = leaseId;
        _lastLeaseExpiryUtc = leaseExpiresUtc;
        if (changed)
        {
            _controlEpoch = Guid.NewGuid();
            LogGuardState(phase, leaseId, leaseExpiresUtc, "changed");
        }
    }

    /// <summary>
    /// Periodically records what every interactive session's Guard reported. The signed status can
    /// only carry the aggregate, so without this line a two-session mismatch ("locked on s2, unlocked
    /// on s1") is invisible. Throttled to one snapshot per interval and skipped while idle so a long
    /// lesson never floods the log. Runs inside the status read so no extra timer is needed.
    /// </summary>
    private async Task LogSessionSnapshotAsync(StudentInputGuardPhase aggregatePhase, Guid? leaseId,
        CancellationToken cancellationToken)
    {
        if (_configPath is null || !OperatingSystem.IsWindows()) return;
        var worthRecording = leaseId is not null ||
                             aggregatePhase is not (StudentInputGuardPhase.Unlocked or
                                 StudentInputGuardPhase.Unsupported);
        if (!worthRecording) return;
        DateTimeOffset due;
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _nextSessionSnapshotUtc) return;
            due = _nextSessionSnapshotUtc = DateTimeOffset.UtcNow.Add(SessionSnapshotInterval);
        }
        try
        {
            var snapshot = await StudentInputGuardIpcClient.DescribeSessionsAsync(cancellationToken)
                .ConfigureAwait(false);
            WebsitePolicyAgentInstaller.ReportAgentOperationNote(_configPath, "input-guard-sessions",
                $"aggregate={aggregatePhase}, lease={leaseId?.ToString("N")[..8] ?? "none"}; {snapshot}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidOperationException or ArgumentException or
                                              System.Security.SecurityException or
                                              System.ComponentModel.Win32Exception or
                                              OperationCanceledException)
        {
            // Diagnostics must never change control behaviour; a cancelled snapshot is benign.
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) return;
        }
    }

    /// <summary>
    /// Records the Guard state transition into the Agent log so a failed lock can be explained
    /// without a debugger. Called only on real transitions, so a busy class cannot flood the log.
    /// </summary>
    private void LogGuardState(StudentInputGuardPhase phase, Guid? leaseId,
        DateTimeOffset? leaseExpiresUtc, string reason)
    {
        if (_configPath is null) return;
        try
        {
            var expiry = leaseExpiresUtc is { } value ? value.ToString("O") : "null";
            var lease = leaseId?.ToString() ?? "null";
            WebsitePolicyAgentInstaller.ReportAgentOperationNote(_configPath, "input-guard-state",
                $"{reason}: phase={phase}, lease={lease}, expires={expiry}, epoch={_controlEpoch:N}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidOperationException or ArgumentException or
                                              System.Security.SecurityException)
        {
            // Diagnostics must never change control behaviour.
        }
    }

    private StudentInputGuardReceipt CreateReceipt(StudentInputGuardCommand command,
        StudentInputGuardOutcome outcome, StudentInputGuardPhase phase, DateTimeOffset completedUtc,
        string detail, Guid previousEpoch)
    {
        return new StudentInputGuardReceipt(1, StudentInputGuardCommandCryptography.ReceiptPurpose,
            command.CampusId, command.RoomId, _deviceFingerprint, AgentRunId, command.CommandId,
            command.Nonce, previousEpoch, _controlEpoch, command.LeaseId, command.Action, outcome,
            phase, completedUtc.ToUniversalTime(), detail);
    }
}
