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
    DateTimeOffset? LeaseExpiresUtc);

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
    bool TransientUnavailable = false);

public sealed record StudentInputGuardDeliveryResult(string Target, bool Succeeded, bool NeedsReview,
    StudentInputGuardOutcome? Outcome, StudentInputGuardReceipt? Receipt, string Detail);

/// <summary>Teacher-signed, device-bound commands for a temporary InputGuard lease.</summary>
public static class StudentInputGuardCommandCryptography
{
    public const string Purpose = "VeyonCampus.StudentInputGuardCommand.v1";
    public const string ReceiptPurpose = "VeyonCampus.StudentInputGuardReceipt.v1";
    public const string RequestNonceHeader = "X-VeyonCampus-Request-Nonce";
    public const int MaximumEnvelopeBytes = 16 * 1024;
    public static readonly TimeSpan MaximumCommandLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumLeaseLifetime = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = ApplicationPolicyCompiler.JsonOptions;

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
            status.CollectedUtc.Offset != TimeSpan.Zero || status.CollectedUtc > now.AddMinutes(2) ||
            status.CollectedUtc < now.Subtract(TimeSpan.FromMinutes(2)) ||
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
        if (command.SchemaVersion != 1 || command.Purpose != Purpose || command.RoomId == Guid.Empty ||
            command.AgentRunId == Guid.Empty || command.CommandId == Guid.Empty || command.Nonce == Guid.Empty ||
            command.ExpectedControlEpoch == Guid.Empty || command.LeaseId == Guid.Empty ||
            !Enum.IsDefined(command.Action) || command.IssuedUtc.Offset != TimeSpan.Zero ||
            command.ExpiresUtc.Offset != TimeSpan.Zero || command.IssuedUtc < now.Subtract(MaximumCommandLifetime) ||
            command.IssuedUtc > now.AddSeconds(5) || command.ExpiresUtc <= now ||
            command.ExpiresUtc > now.Add(MaximumCommandLifetime) || command.ExpiresUtc <= command.IssuedUtc ||
            command.ExpiresUtc - command.IssuedUtc > MaximumCommandLifetime ||
            (actionRequiresLease ? !leaseExpiryIsValid : command.LeaseExpiresUtc is not null))
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
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _gate = new();
    private readonly HashSet<Guid> _commandIds = [];
    private readonly Queue<Guid> _commandOrder = [];
    private StudentInputGuardPhase? _lastPhase;
    private Guid? _lastLeaseId;
    private DateTimeOffset? _lastLeaseExpiryUtc;
    private Guid _controlEpoch = Guid.NewGuid();

    public StudentInputGuardAgentState(RSA agentIdentityKey)
    {
        ArgumentNullException.ThrowIfNull(agentIdentityKey);
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
        var phase = guard.Supported ? guard.Phase : StudentInputGuardPhase.Unsupported;
        var leaseId = guard.Supported ? guard.LeaseId : null;
        var leaseExpiresUtc = guard.Supported ? guard.LeaseExpiresUtc : null;
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
                return CreateReceipt(command, result.Outcome, result.Phase, result.ReportedUtc,
                    result.Detail, command.ExpectedControlEpoch);
            }
        }
        finally { _commandGate.Release(); }
    }

    private void ObserveGuardState(StudentInputGuardPhase phase, Guid? leaseId,
        DateTimeOffset? leaseExpiresUtc)
    {
        if (_lastPhase is { } previousPhase &&
            (previousPhase != phase || _lastLeaseId != leaseId || _lastLeaseExpiryUtc != leaseExpiresUtc))
            _controlEpoch = Guid.NewGuid();
        _lastPhase = phase;
        _lastLeaseId = leaseId;
        _lastLeaseExpiryUtc = leaseExpiresUtc;
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
