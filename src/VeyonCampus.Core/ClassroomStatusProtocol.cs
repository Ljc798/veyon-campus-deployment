using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public sealed record ClassroomStatusCommand(int SchemaVersion, string Purpose, string CampusId,
    Guid MessageId, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc, Guid? SessionId,
    bool Active, string? RoomName, int TargetCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClassroomMode? Mode = null);

public sealed record ClassroomStatusSnapshot(bool Connected, Guid? SessionId = null,
    string? RoomName = null, int TargetCount = 0, DateTimeOffset? ReceivedUtc = null,
    DateTimeOffset? ExpiresUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClassroomMode? Mode = null);

public sealed record ClassroomStatusAcknowledgement(int SchemaVersion, string Purpose, string CampusId,
    Guid MessageId, string RequestSha256, DateTimeOffset ReceivedUtc, string AgentVersion);

public sealed record ClassroomStatusDeliveryResult(string Target, bool Succeeded, bool NeedsReview,
    string Detail, ClassroomStatusAcknowledgement? Acknowledgement = null, string? AgentFingerprint = null);

/// <summary>Purpose-separated signatures for read-only classroom presence; no policy adapter is involved.</summary>
public static class ClassroomStatusCryptography
{
    public const string CommandPurpose = "VeyonCampus.ClassroomStatus.v1";
    public const string CommandPurposeV2 = "VeyonCampus.ClassroomStatus.v2";
    public const string AcknowledgementPurpose = "VeyonCampus.ClassroomStatusAcknowledgement.v1";
    public const int MaximumCommandBytes = 8 * 1024;
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(1);

    private sealed record SignedEnvelope(string Payload, string Signature);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ClassroomStatusCommand Create(string campusId, ClassroomSession? session,
        DateTimeOffset nowUtc, ClassroomMode? mode = null)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var now = nowUtc.ToUniversalTime();
        var active = session is { Status: ClassroomSessionStatus.Active };
        if (mode is { } selectedMode && !Enum.IsDefined(selectedMode))
            throw new InvalidDataException("课堂模式无效。");
        var schemaVersion = active && mode is not null ? 2 : 1;
        var purpose = schemaVersion == 2 ? CommandPurposeV2 : CommandPurpose;
        var command = active
            ? new ClassroomStatusCommand(schemaVersion, purpose, campusId, Guid.NewGuid(), now,
                now.Add(MaximumLifetime), session!.SessionId, true, session.Room.RoomName,
                session.Targets.Length, schemaVersion == 2 ? mode : null)
            : new ClassroomStatusCommand(1, CommandPurpose, campusId, Guid.NewGuid(), now,
                now.Add(MaximumLifetime), null, false, null, 0);
        Validate(command, campusId, now);
        return command;
    }

    public static string Sign(ClassroomStatusCommand command, RSA teacherPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(teacherPrivateKey);
        Validate(command, command.CampusId, command.IssuedUtc);
        if (teacherPrivateKey.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("课堂状态签名密钥位长无效。");
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions);
        if (payload.Length is 0 or > MaximumCommandBytes * 3 / 4)
            throw new InvalidDataException("课堂状态消息超过大小限制。");
        var envelope = new SignedEnvelope(Convert.ToBase64String(payload), Convert.ToBase64String(
            teacherPrivateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        var signed = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(signed) > MaximumCommandBytes)
            throw new InvalidDataException("课堂状态消息超过大小限制。");
        return signed;
    }

    public static ClassroomStatusCommand Verify(string signedJson, string campusId,
        string teacherPublicKeyPem, DateTimeOffset nowUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        ArgumentException.ThrowIfNullOrWhiteSpace(teacherPublicKeyPem);
        if (string.IsNullOrWhiteSpace(signedJson) || Encoding.UTF8.GetByteCount(signedJson) > MaximumCommandBytes)
            throw new InvalidDataException("课堂状态消息大小无效。");
        try
        {
            var envelopeBytes = Encoding.UTF8.GetBytes(signedJson);
            PolicyJson.RejectDuplicateFields(envelopeBytes);
            var envelope = JsonSerializer.Deserialize<SignedEnvelope>(signedJson, JsonOptions)
                           ?? throw new InvalidDataException("课堂状态签名信封为空。");
            var payload = DecodeCanonicalBase64(envelope.Payload, "课堂状态正文");
            var signature = DecodeCanonicalBase64(envelope.Signature, "课堂状态签名");
            if (payload.Length is 0 or > MaximumCommandBytes * 3 / 4)
                throw new InvalidDataException("课堂状态正文超过大小限制。");
            PolicyJson.RejectDuplicateFields(payload);
            using var key = RSA.Create();
            key.ImportFromPem(teacherPublicKeyPem);
            if (key.KeySize is < 2048 or > 4096 || signature.Length != key.KeySize / 8 ||
                !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("课堂状态签名无效。");
            var command = JsonSerializer.Deserialize<ClassroomStatusCommand>(payload, JsonOptions)
                          ?? throw new InvalidDataException("课堂状态正文为空。");
            var canonical = JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions);
            if (!payload.AsSpan().SequenceEqual(canonical))
                throw new InvalidDataException("课堂状态正文不是规范 JSON 编码。");
            Validate(command, campusId, nowUtc);
            return command;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or
                                          ArgumentException)
        {
            throw new InvalidDataException("课堂状态签名信封编码无效。", exception);
        }
    }

    public static string SignAcknowledgement(ClassroomStatusCommand command, string signedCommand,
        DateTimeOffset receivedUtc, RSA agentPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(agentPrivateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(signedCommand);
        var acknowledgement = new ClassroomStatusAcknowledgement(1, AcknowledgementPurpose,
            command.CampusId, command.MessageId,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedCommand))),
            receivedUtc.ToUniversalTime(), WebsitePolicyAgent.GetRuntimeVersion());
        ValidateAcknowledgement(acknowledgement, command, signedCommand, receivedUtc);
        return StudentAgentResponseCryptography.Sign(acknowledgement, agentPrivateKey);
    }

    public static VerifiedStudentAgentResponse<ClassroomStatusAcknowledgement> VerifyAcknowledgement(
        string signedAcknowledgement, ClassroomStatusCommand command, string signedCommand,
        string? pinnedAgentPublicKeyPem, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedCommand);
        var verified = StudentAgentResponseCryptography.Verify<ClassroomStatusAcknowledgement>(
            signedAcknowledgement, pinnedAgentPublicKeyPem);
        ValidateAcknowledgement(verified.Payload, command, signedCommand, nowUtc);
        return verified;
    }

    public static ClassroomStatusSnapshot ToSnapshot(ClassroomStatusCommand command,
        DateTimeOffset receivedUtc, DateTimeOffset nowUtc)
    {
        Validate(command, command.CampusId, nowUtc);
        var received = receivedUtc.ToUniversalTime();
        return command.Active
            ? new ClassroomStatusSnapshot(true, command.SessionId, command.RoomName, command.TargetCount,
                received, command.ExpiresUtc, command.Mode)
            : new ClassroomStatusSnapshot(true, ReceivedUtc: received, ExpiresUtc: command.ExpiresUtc);
    }

    public static void Validate(ClassroomStatusCommand command, string campusId, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(command);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var now = nowUtc.ToUniversalTime();
        var validVersion = command.SchemaVersion == 1 && command.Purpose == CommandPurpose && command.Mode is null ||
                           command.SchemaVersion == 2 && command.Purpose == CommandPurposeV2 &&
                           command.Active && command.Mode is { } mode && Enum.IsDefined(mode);
        if (!validVersion || command.CampusId != campusId ||
            command.MessageId == Guid.Empty || command.IssuedUtc.Offset != TimeSpan.Zero ||
            command.ExpiresUtc.Offset != TimeSpan.Zero || command.IssuedUtc > now.Add(MaximumFutureSkew) ||
            command.IssuedUtc < now.Subtract(MaximumLifetime) || command.ExpiresUtc <= now ||
            command.ExpiresUtc <= command.IssuedUtc || command.ExpiresUtc - command.IssuedUtc > MaximumLifetime)
            throw new InvalidDataException("课堂状态版本、校区、用途、消息 ID 或有效期无效。");

        if (command.Active)
        {
            if (command.SessionId is null || command.SessionId == Guid.Empty ||
                string.IsNullOrWhiteSpace(command.RoomName) || command.RoomName.Length > 100 ||
                command.RoomName != command.RoomName.Trim() || command.RoomName.Any(char.IsControl) ||
                command.TargetCount is < 1 or > ClassroomSession.MaximumTargets)
                throw new InvalidDataException("活动课堂状态字段无效。");
        }
        else if (command.SchemaVersion != 1 || command.SessionId is not null || command.RoomName is not null ||
                 command.TargetCount != 0 || command.Mode is not null)
            throw new InvalidDataException("空闲状态不能包含课堂或目标电脑信息。");
    }

    private static void ValidateAcknowledgement(ClassroomStatusAcknowledgement acknowledgement,
        ClassroomStatusCommand command, string signedCommand, DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedCommand)));
        if (acknowledgement.SchemaVersion != 1 || acknowledgement.Purpose != AcknowledgementPurpose ||
            acknowledgement.CampusId != command.CampusId || acknowledgement.MessageId != command.MessageId ||
            acknowledgement.RequestSha256 != expectedHash || acknowledgement.ReceivedUtc.Offset != TimeSpan.Zero ||
            acknowledgement.ReceivedUtc > now.Add(MaximumFutureSkew) ||
            acknowledgement.ReceivedUtc < now.Subtract(MaximumLifetime) ||
            string.IsNullOrWhiteSpace(acknowledgement.AgentVersion) || acknowledgement.AgentVersion.Length > 64)
            throw new InvalidDataException("课堂状态回执与本次消息、校区或时间不匹配。");
    }

    private static byte[] DecodeCanonicalBase64(string value, string description)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (!string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal))
                throw new InvalidDataException($"{description}不是规范 Base64 编码。");
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{description}编码无效。", exception);
        }
    }
}

/// <summary>In-memory, replay-resistant latest classroom state; a service restart clears it.</summary>
public sealed class ClassroomStatusAgentState
{
    private readonly object _gate = new();
    private ClassroomStatusCommand? _current;
    private DateTimeOffset _receivedUtc;

    public ClassroomStatusCommand Apply(string signedJson, string campusId, string teacherPublicKeyPem,
        DateTimeOffset nowUtc)
    {
        var command = ClassroomStatusCryptography.Verify(signedJson, campusId, teacherPublicKeyPem, nowUtc);
        lock (_gate)
        {
            if (_current is { } current &&
                (command.MessageId == current.MessageId || command.IssuedUtc <= current.IssuedUtc))
                throw new InvalidDataException("重复或过期的课堂状态消息已拒绝。");
            _current = command;
            _receivedUtc = nowUtc.ToUniversalTime();
            return command;
        }
    }

    public ClassroomStatusSnapshot Read(string campusId, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var command = _current;
            if (command is null || command.CampusId != campusId || command.ExpiresUtc <= nowUtc.ToUniversalTime())
                return new ClassroomStatusSnapshot(false);
            return ClassroomStatusCryptography.ToSnapshot(command, _receivedUtc, nowUtc);
        }
    }
}

public static class ClassroomStatusLocalEndpointPolicy
{
    public static bool Allows(IPAddress? remoteAddress) => remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);
}

public static class ClassroomStatusTransport
{
    private const int MaximumAcknowledgementBytes = 64 * 1024;

    public static async Task<IReadOnlyList<ClassroomStatusDeliveryResult>> SendAsync(
        IEnumerable<string> targets, ClassroomStatusCommand command, RSA teacherPrivateKey,
        StudentAgentIdentityTrustStore? trustStore = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(teacherPrivateKey);
        var normalizedTargets = WebsitePolicyTransport.NormalizeTargets(targets);
        var signedCommand = ClassroomStatusCryptography.Sign(command, teacherPrivateKey);
        trustStore ??= new StudentAgentIdentityTrustStore();

        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        using var limit = new SemaphoreSlim(16, 16);
        var tasks = normalizedTargets.Select(async target =>
        {
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
                    WebsitePolicyAgent.ClassroomStatusPath).Uri;
                using var content = new StringContent(signedCommand, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
                var bytes = await ReadBoundedAsync(response.Content, MaximumAcknowledgementBytes, cancellationToken)
                    .ConfigureAwait(false);
                var body = Encoding.UTF8.GetString(bytes);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    return new ClassroomStatusDeliveryResult(target, false,
                        status is >= 400 and < 500 && status is not 404 and not 429,
                        $"HTTP {status}：{body}");
                }

                var pinnedKey = trustStore.FindTrustedPublicKey(command.CampusId, target);
                var verified = ClassroomStatusCryptography.VerifyAcknowledgement(body, command, signedCommand,
                    pinnedKey, DateTimeOffset.UtcNow);
                if (!verified.MatchesPinnedKey)
                {
                    var changed = pinnedKey is not null;
                    return new ClassroomStatusDeliveryResult(target, false, true,
                        changed ? "Agent 身份密钥与已固定记录不符，拒绝标记确认。" :
                            $"状态已签名接收，但 Agent 身份尚未固定。指纹：{verified.Fingerprint}",
                        verified.Payload, verified.Fingerprint);
                }
                return new ClassroomStatusDeliveryResult(target, true, false,
                    "课堂状态已由已固定身份的学生 Agent 确认。", verified.Payload, verified.Fingerprint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              UriFormatException or InvalidDataException or JsonException or
                                              CryptographicException or IOException)
            {
                var requiresReview = exception is InvalidDataException or JsonException or CryptographicException;
                return new ClassroomStatusDeliveryResult(target, false, requiresReview,
                    exception is TaskCanceledException ? "课堂状态推送超时。" :
                        "课堂状态推送失败：" + exception.Message);
            }
            finally { limit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("课堂状态回执超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("课堂状态回执超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
