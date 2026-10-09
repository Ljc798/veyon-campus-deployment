using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public enum ClassroomEventSender
{
    Student,
    Teacher
}

public enum ClassroomEventType
{
    HelpRequested,
    HelpAcknowledged,
    TeacherReply,
    HelpResolved,
    ClassroomNotice
}

public enum ClassroomHelpReason
{
    NeedHelp
}

public sealed record ClassroomEventAccessGrant(int SchemaVersion, string Purpose, string CampusId,
    Guid MessageId, Guid SessionId, string Target, string TeacherEndpoint, string ServerCertificateSha256,
    string AccessToken, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc);

public sealed record ClassroomEvent(int SchemaVersion, string Purpose, string CampusId, Guid SessionId,
    Guid EventId, string Target, ClassroomEventSender Sender, ClassroomEventType Type,
    DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc, ClassroomHelpReason? HelpReason,
    string? Message, Guid? CorrelationId);

public sealed record VerifiedClassroomEvent(ClassroomEvent Event, string PublicKeyPem, string Fingerprint,
    bool MatchesPinnedKey);

public sealed record ClassroomEventPage(long Cursor, IReadOnlyList<ClassroomEvent> Events,
    IReadOnlyList<string?> SignedEnvelopes);

/// <summary>Strict, purpose-separated signatures for short-lived classroom event grants and events.</summary>
public static class ClassroomEventCryptography
{
    public const string GrantPurpose = "VeyonCampus.ClassroomEventGrant.v1";
    public const string EventPurpose = "VeyonCampus.ClassroomEvent.v1";
    public const int TeacherHttpsPort = 39176;
    public const int MaximumGrantEnvelopeBytes = 16 * 1024;
    public const int MaximumEventEnvelopeBytes = 16 * 1024;
    public const int MaximumMessageCharacters = 500;
    public const string ClassroomNoticeTarget = "*";
    public static readonly TimeSpan MaximumGrantLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumEventLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan EventRetention = TimeSpan.FromMinutes(15);

    private sealed record SignedPayload(string Payload, string Signature, string PublicKeyPem);
    private static readonly JsonSerializerOptions JsonOptions = ApplicationPolicyCompiler.JsonOptions;
    private static readonly Regex Sha256Hex = new("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex AccessTokenPattern = new("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant);

    public static string CreateAccessToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    public static string CreateTeacherEndpoint(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IsPrivateIpv4Address(address))
            throw new InvalidDataException("课堂事件服务必须使用教师电脑的私有 IPv4 地址。");
        return $"https://{address}:{TeacherHttpsPort}/";
    }

    public static string SignGrant(ClassroomEventAccessGrant grant, RSA teacherPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ValidateGrant(grant, grant.CampusId, grant.SessionId, grant.Target, grant.IssuedUtc);
        return SignPayload(grant, teacherPrivateKey, MaximumGrantEnvelopeBytes);
    }

    public static ClassroomEventAccessGrant VerifyGrant(string signedJson, string expectedCampusId,
        string teacherPublicKeyPem, Guid expectedSessionId, string expectedTarget, DateTimeOffset nowUtc)
    {
        var grant = VerifyPayload<ClassroomEventAccessGrant>(signedJson, teacherPublicKeyPem,
            MaximumGrantEnvelopeBytes, out _);
        ValidateGrant(grant, expectedCampusId, expectedSessionId, expectedTarget, nowUtc);
        return grant;
    }

    public static ClassroomEventAccessGrant VerifyGrant(string signedJson, string expectedCampusId,
        string teacherPublicKeyPem, Guid expectedSessionId, DateTimeOffset nowUtc)
    {
        var grant = VerifyPayload<ClassroomEventAccessGrant>(signedJson, teacherPublicKeyPem,
            MaximumGrantEnvelopeBytes, out _);
        ValidateGrant(grant, expectedCampusId, expectedSessionId, grant.Target, nowUtc);
        return grant;
    }

    public static string SignEvent(ClassroomEvent classroomEvent, RSA signingKey)
    {
        ArgumentNullException.ThrowIfNull(classroomEvent);
        ValidateEvent(classroomEvent, classroomEvent.CampusId, classroomEvent.SessionId,
            classroomEvent.Target, classroomEvent.Sender, classroomEvent.IssuedUtc);
        return SignPayload(classroomEvent, signingKey, MaximumEventEnvelopeBytes);
    }

    public static VerifiedClassroomEvent VerifyEvent(string signedJson, string expectedCampusId,
        Guid expectedSessionId, string expectedTarget, ClassroomEventSender expectedSender,
        string? pinnedPublicKeyPem, DateTimeOffset nowUtc)
    {
        var classroomEvent = VerifyPayload<ClassroomEvent>(signedJson, pinnedPublicKeyPem,
            MaximumEventEnvelopeBytes, out var publicKeyPem);
        ValidateEvent(classroomEvent, expectedCampusId, expectedSessionId, expectedTarget,
            expectedSender, nowUtc);
        var fingerprint = StudentAgentResponseCryptography.GetFingerprint(publicKeyPem);
        var matchesPin = pinnedPublicKeyPem is not null && KeysMatch(publicKeyPem, pinnedPublicKeyPem);
        return new VerifiedClassroomEvent(classroomEvent, publicKeyPem, fingerprint, matchesPin);
    }

    public static void ValidateEvent(ClassroomEvent classroomEvent, string expectedCampusId,
        Guid expectedSessionId, string expectedTarget, ClassroomEventSender expectedSender,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(classroomEvent);
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        var isClassroomNotice = classroomEvent.Type == ClassroomEventType.ClassroomNotice &&
                                classroomEvent.Sender == ClassroomEventSender.Teacher &&
                                classroomEvent.Target == ClassroomNoticeTarget;
        var target = isClassroomNotice
            ? expectedTarget == ClassroomNoticeTarget
                ? ClassroomNoticeTarget
                : NormalizeSingleTarget(expectedTarget)
            : NormalizeSingleTarget(expectedTarget);
        var targetMatches = isClassroomNotice || classroomEvent.Target == target;
        var now = nowUtc.ToUniversalTime();
        var issued = classroomEvent.IssuedUtc;
        var expires = classroomEvent.ExpiresUtc;
        if (classroomEvent.SchemaVersion != 1 || classroomEvent.Purpose != EventPurpose ||
            classroomEvent.CampusId != expectedCampusId || classroomEvent.SessionId == Guid.Empty ||
            classroomEvent.SessionId != expectedSessionId || classroomEvent.EventId == Guid.Empty ||
            !targetMatches || classroomEvent.Sender != expectedSender ||
            !Enum.IsDefined(classroomEvent.Sender) || !Enum.IsDefined(classroomEvent.Type) ||
            issued.Offset != TimeSpan.Zero || expires.Offset != TimeSpan.Zero ||
            issued > now.Add(MaximumFutureSkew) || issued < now.Subtract(MaximumEventLifetime) ||
            expires <= now || expires <= issued || expires - issued > MaximumEventLifetime)
            throw new InvalidDataException("课堂事件版本、身份、目标或有效期无效。");

        if (classroomEvent.Message is { } message &&
            (message.Length > MaximumMessageCharacters || message.Any(char.IsControl) &&
             message.Any(character => char.IsControl(character) && character is not '\n' and not '\t')))
            throw new InvalidDataException("课堂事件正文超过限制或包含不可见控制字符。");

        var messageIsEmpty = string.IsNullOrWhiteSpace(classroomEvent.Message);
        var shapeIsValid = (classroomEvent.Type, classroomEvent.Sender) switch
        {
            (ClassroomEventType.HelpRequested, ClassroomEventSender.Student) =>
                classroomEvent.HelpReason is { } reason && Enum.IsDefined(reason) &&
                classroomEvent.CorrelationId is null,
            (ClassroomEventType.HelpAcknowledged, ClassroomEventSender.Teacher) =>
                classroomEvent.HelpReason is null && classroomEvent.CorrelationId is { } acknowledged &&
                acknowledged != Guid.Empty && messageIsEmpty,
            (ClassroomEventType.TeacherReply, ClassroomEventSender.Teacher) =>
                classroomEvent.HelpReason is null && classroomEvent.CorrelationId is { } replied &&
                replied != Guid.Empty && !messageIsEmpty,
            (ClassroomEventType.HelpResolved, ClassroomEventSender.Student) =>
                classroomEvent.HelpReason is null && classroomEvent.CorrelationId is { } resolved &&
                resolved != Guid.Empty && messageIsEmpty,
            (ClassroomEventType.ClassroomNotice, ClassroomEventSender.Teacher) =>
                classroomEvent.HelpReason is null && classroomEvent.CorrelationId is null && !messageIsEmpty,
            _ => false
        };
        if (!shapeIsValid)
            throw new InvalidDataException("课堂事件类型、发送者或正文形状不匹配。");
    }

    public static void ValidateGrant(ClassroomEventAccessGrant grant, string expectedCampusId,
        Guid expectedSessionId, string expectedTarget, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(grant);
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        var target = NormalizeSingleTarget(expectedTarget);
        var now = nowUtc.ToUniversalTime();
        if (grant.SchemaVersion != 1 || grant.Purpose != GrantPurpose || grant.CampusId != expectedCampusId ||
            grant.MessageId == Guid.Empty || grant.SessionId == Guid.Empty || grant.SessionId != expectedSessionId ||
            grant.Target != target || grant.IssuedUtc.Offset != TimeSpan.Zero || grant.ExpiresUtc.Offset != TimeSpan.Zero ||
            grant.IssuedUtc > now.Add(MaximumFutureSkew) || grant.IssuedUtc < now.Subtract(MaximumGrantLifetime) ||
            grant.ExpiresUtc <= now || grant.ExpiresUtc <= grant.IssuedUtc ||
            grant.ExpiresUtc - grant.IssuedUtc > MaximumGrantLifetime ||
            !AccessTokenPattern.IsMatch(grant.AccessToken) || !Sha256Hex.IsMatch(grant.ServerCertificateSha256) ||
            !IsValidTeacherEndpoint(grant.TeacherEndpoint))
            throw new InvalidDataException("课堂事件授权版本、范围、凭据或有效期无效。");
    }

    public static bool IsPrivateIpv4Address(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168;
    }

    public static string NormalizeSingleTarget(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var targets = WebsitePolicyTransport.NormalizeTargets([target]);
        if (targets.Count != 1 || !string.Equals(targets[0], target.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("课堂事件目标电脑无效。");
        return targets[0];
    }

    private static bool IsValidTeacherEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != TeacherHttpsPort || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0 || !IPAddress.TryParse(uri.Host, out var address) ||
            !IsPrivateIpv4Address(address)) return false;
        return string.Equals(uri.AbsoluteUri, endpoint, StringComparison.Ordinal);
    }

    private static string SignPayload<T>(T payload, RSA signingKey, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("课堂事件签名密钥位长无效。");
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (payloadBytes.Length is 0 || payloadBytes.Length > maximumBytes * 3 / 4)
            throw new InvalidDataException("课堂事件签名正文超过大小限制。");
        var envelope = new SignedPayload(Convert.ToBase64String(payloadBytes),
            Convert.ToBase64String(signingKey.SignData(payloadBytes, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss)), signingKey.ExportSubjectPublicKeyInfoPem());
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > maximumBytes)
            throw new InvalidDataException("课堂事件签名封装超过大小限制。");
        return json;
    }

    private static T VerifyPayload<T>(string signedJson, string? pinnedPublicKeyPem, int maximumBytes,
        out string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedJson);
        if (Encoding.UTF8.GetByteCount(signedJson) > maximumBytes)
            throw new InvalidDataException("课堂事件签名封装超过大小限制。");
        try
        {
            var envelopeBytes = Encoding.UTF8.GetBytes(signedJson);
            PolicyJson.RejectDuplicateFields(envelopeBytes);
            var envelope = JsonSerializer.Deserialize<SignedPayload>(signedJson, JsonOptions)
                           ?? throw new InvalidDataException("课堂事件签名封装为空。");
            var payloadBytes = DecodeCanonicalBase64(envelope.Payload, "课堂事件正文");
            var signature = DecodeCanonicalBase64(envelope.Signature, "课堂事件签名");
            if (payloadBytes.Length is 0 || payloadBytes.Length > maximumBytes * 3 / 4)
                throw new InvalidDataException("课堂事件正文超过大小限制。");
            PolicyJson.RejectDuplicateFields(payloadBytes);
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(envelope.PublicKeyPem);
            if (publicKey.KeySize is < 2048 or > 4096 || signature.Length != publicKey.KeySize / 8 ||
                !publicKey.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("课堂事件签名无效。");
            publicKeyPem = publicKey.ExportSubjectPublicKeyInfoPem();
            if (!string.Equals(publicKeyPem, envelope.PublicKeyPem, StringComparison.Ordinal))
                throw new InvalidDataException("课堂事件公钥格式不规范。");
            if (pinnedPublicKeyPem is not null && !KeysMatch(publicKeyPem, pinnedPublicKeyPem))
                throw new InvalidDataException("课堂事件签名密钥与已固定身份不匹配。");
            var payload = JsonSerializer.Deserialize<T>(payloadBytes, JsonOptions)
                          ?? throw new InvalidDataException("课堂事件签名正文为空。");
            if (!payloadBytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions)))
                throw new InvalidDataException("课堂事件签名正文编码不规范。");
            return payload;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or
                                          ArgumentException)
        {
            throw new InvalidDataException("课堂事件签名封装或公钥无效。", exception);
        }
    }

    private static bool KeysMatch(string firstPem, string secondPem)
    {
        using var first = RSA.Create();
        using var second = RSA.Create();
        first.ImportFromPem(firstPem);
        second.ImportFromPem(secondPem);
        return first.KeySize is >= 2048 and <= 4096 && second.KeySize is >= 2048 and <= 4096 &&
               CryptographicOperations.FixedTimeEquals(first.ExportSubjectPublicKeyInfo(),
                   second.ExportSubjectPublicKeyInfo());
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

/// <summary>Bounded, in-memory classroom event buffer. The caller authenticates each event before appending.</summary>
public sealed class ClassroomEventBuffer
{
    public const int MaximumEventsPerSession = 512;
    public const int MaximumPageSize = 50;
    public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(25);
    private const int MaximumSessions = 4;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<BufferedEvent>> _sessions = [];
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _changed = [];
    private readonly HashSet<Guid> _eventIds = [];
    private long _nextSequence;

    private sealed record BufferedEvent(long Sequence, ClassroomEvent Event, DateTimeOffset StoredUtc,
        string? SignedEnvelope);

    public bool Append(ClassroomEvent classroomEvent, DateTimeOffset nowUtc, string? signedEnvelope = null)
    {
        ArgumentNullException.ThrowIfNull(classroomEvent);
        var now = nowUtc.ToUniversalTime();
        ClassroomEventCryptography.ValidateEvent(classroomEvent, classroomEvent.CampusId,
            classroomEvent.SessionId, classroomEvent.Target, classroomEvent.Sender, now);
        if (signedEnvelope is not null &&
            Encoding.UTF8.GetByteCount(signedEnvelope) > ClassroomEventCryptography.MaximumEventEnvelopeBytes)
            throw new InvalidDataException("课堂事件签名封装超过大小限制。");
        lock (_gate)
        {
            Purge(now);
            if (_eventIds.Contains(classroomEvent.EventId)) return false;
            if (!_sessions.TryGetValue(classroomEvent.SessionId, out var events))
            {
                if (_sessions.Count >= MaximumSessions)
                    throw new InvalidDataException("教师端同时保留的课堂事件 session 已达到上限。");
                events = [];
                _sessions.Add(classroomEvent.SessionId, events);
            }
            if (classroomEvent.Type == ClassroomEventType.TeacherReply &&
                events.Any(item => item.Event.Type == ClassroomEventType.TeacherReply &&
                                   item.Event.CorrelationId == classroomEvent.CorrelationId))
                return false;
            if (classroomEvent.Type == ClassroomEventType.HelpResolved &&
                events.Any(item => item.Event.Type == ClassroomEventType.HelpResolved &&
                                   item.Event.CorrelationId == classroomEvent.CorrelationId))
                return false;
            if (events.Count >= MaximumEventsPerSession)
                throw new InvalidDataException("本堂课的待处理事件已达到上限；请先处理現有事件。");
            events.Add(new BufferedEvent(++_nextSequence, classroomEvent, now, signedEnvelope));
            _eventIds.Add(classroomEvent.EventId);
            if (_changed.Remove(classroomEvent.SessionId, out var changed)) changed.TrySetResult(true);
            return true;
        }
    }

    public ClassroomEventPage ReadAfter(Guid sessionId, string? target, long afterCursor,
        int maximumCount, DateTimeOffset nowUtc)
    {
        if (sessionId == Guid.Empty || afterCursor < 0 || maximumCount is < 1 or > MaximumPageSize)
            throw new InvalidDataException("课堂事件读取游标或分页大小无效。");
        var normalizedTarget = target is null ? null : ClassroomEventCryptography.NormalizeSingleTarget(target);
        lock (_gate)
        {
            Purge(nowUtc.ToUniversalTime());
            return ReadAfterUnsafe(sessionId, normalizedTarget, afterCursor, maximumCount,
                nowUtc.ToUniversalTime());
        }
    }

    public async Task<ClassroomEventPage> WaitForEventsAsync(Guid sessionId, string? target,
        long afterCursor, int maximumCount, TimeSpan maximumWait, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (maximumWait <= TimeSpan.Zero || maximumWait > MaximumWait)
            throw new InvalidDataException("课堂事件等待时间超过固定上限。");
        var normalizedTarget = target is null ? null : ClassroomEventCryptography.NormalizeSingleTarget(target);
        if (sessionId == Guid.Empty || afterCursor < 0 || maximumCount is < 1 or > MaximumPageSize)
            throw new InvalidDataException("课堂事件读取游标或分页大小无效。");
        var deadline = nowUtc.ToUniversalTime() + maximumWait;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task signalTask;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                Purge(now);
                var page = ReadAfterUnsafe(sessionId, normalizedTarget, afterCursor, maximumCount, now);
                if (page.Events.Count > 0) return page;
                var remaining = deadline - now;
                if (remaining <= TimeSpan.Zero) return page;
                if (!_changed.TryGetValue(sessionId, out var signal))
                {
                    signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _changed.Add(sessionId, signal);
                }
                signalTask = signal.Task;
            }

            var remainingWait = deadline - DateTimeOffset.UtcNow;
            if (remainingWait <= TimeSpan.Zero)
                return ReadAfter(sessionId, normalizedTarget, afterCursor, maximumCount,
                    DateTimeOffset.UtcNow);
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(remainingWait, waitCancellation.Token);
            var completed = await Task.WhenAny(signalTask, delay).ConfigureAwait(false);
            if (completed == delay)
            {
                await delay.ConfigureAwait(false);
                return ReadAfter(sessionId, normalizedTarget, afterCursor, maximumCount,
                    DateTimeOffset.UtcNow);
            }
            waitCancellation.Cancel();
            await signalTask.ConfigureAwait(false);
        }
    }

    public ClassroomEvent? Find(Guid sessionId, Guid eventId, DateTimeOffset nowUtc)
    {
        if (sessionId == Guid.Empty || eventId == Guid.Empty) return null;
        lock (_gate)
        {
            Purge(nowUtc.ToUniversalTime());
            return _sessions.TryGetValue(sessionId, out var events)
                ? events.Where(item => item.Event.ExpiresUtc > nowUtc.ToUniversalTime())
                    .Select(item => item.Event).SingleOrDefault(item => item.EventId == eventId)
                : null;
        }
    }

    public ClassroomEvent? FindRetained(Guid sessionId, Guid eventId, DateTimeOffset nowUtc)
    {
        if (sessionId == Guid.Empty || eventId == Guid.Empty) return null;
        lock (_gate)
        {
            Purge(nowUtc.ToUniversalTime());
            return _sessions.TryGetValue(sessionId, out var events)
                ? events.Select(item => item.Event).SingleOrDefault(item => item.EventId == eventId)
                : null;
        }
    }

    public bool HasCorrelatedEvent(Guid sessionId, Guid correlationId, ClassroomEventType type,
        ClassroomEventSender sender, string target, DateTimeOffset nowUtc)
    {
        if (sessionId == Guid.Empty || correlationId == Guid.Empty) return false;
        var normalizedTarget = ClassroomEventCryptography.NormalizeSingleTarget(target);
        lock (_gate)
        {
            Purge(nowUtc.ToUniversalTime());
            return _sessions.TryGetValue(sessionId, out var events) && events.Any(item =>
                item.Event.Type == type && item.Event.Sender == sender &&
                item.Event.CorrelationId == correlationId &&
                string.Equals(item.Event.Target, normalizedTarget, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void ClearSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty) return;
        lock (_gate)
        {
            if (_sessions.Remove(sessionId, out var events))
                foreach (var item in events) _eventIds.Remove(item.Event.EventId);
            if (_changed.Remove(sessionId, out var changed)) changed.TrySetResult(true);
        }
    }

    public int Count(Guid sessionId)
    {
        lock (_gate) return _sessions.TryGetValue(sessionId, out var events) ? events.Count : 0;
    }

    private void Purge(DateTimeOffset nowUtc)
    {
        foreach (var sessionId in _sessions.Keys.ToArray())
        {
            var events = _sessions[sessionId];
            foreach (var expired in events.Where(item => nowUtc - item.StoredUtc > ClassroomEventCryptography.EventRetention)
                         .ToArray())
            {
                events.Remove(expired);
                _eventIds.Remove(expired.Event.EventId);
            }
            if (events.Count == 0) _sessions.Remove(sessionId);
        }
    }

    private ClassroomEventPage ReadAfterUnsafe(Guid sessionId, string? normalizedTarget,
        long afterCursor, int maximumCount, DateTimeOffset nowUtc)
    {
        if (!_sessions.TryGetValue(sessionId, out var events))
            return new ClassroomEventPage(afterCursor, Array.Empty<ClassroomEvent>(), Array.Empty<string?>());
        var pending = events.Where(item => item.Sequence > afterCursor).ToArray();
        bool MatchesTarget(BufferedEvent item) => normalizedTarget is null ||
            string.Equals(item.Event.Target, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
            item.Event.Type == ClassroomEventType.ClassroomNotice &&
            item.Event.Target == ClassroomEventCryptography.ClassroomNoticeTarget;
        var page = pending.Where(item => item.Event.ExpiresUtc > nowUtc && MatchesTarget(item))
            .Take(maximumCount).ToArray();
        var cursor = page.Length > 0 && pending.Any(item => item.Sequence > page[^1].Sequence &&
                                                            item.Event.ExpiresUtc > nowUtc &&
                                                            MatchesTarget(item))
            ? page[^1].Sequence
            : pending.Length == 0 ? afterCursor : pending[^1].Sequence;
        return new ClassroomEventPage(cursor, Array.AsReadOnly(page.Select(item => item.Event).ToArray()),
            Array.AsReadOnly(page.Select(item => item.SignedEnvelope).ToArray()));
    }
}

/// <summary>Agent-side memory state for one current classroom event grant.</summary>
public sealed class ClassroomEventGrantState
{
    private readonly object _gate = new();
    private ClassroomEventAccessGrant? _current;
    private Guid? _lastSessionId;
    private Guid? _lastMessageId;
    private DateTimeOffset? _lastIssuedUtc;

    public ClassroomEventAccessGrant Apply(string signedGrant, string campusId, string teacherPublicKeyPem,
        Guid activeSessionId, string expectedTarget, DateTimeOffset nowUtc)
    {
        var grant = ClassroomEventCryptography.VerifyGrant(signedGrant, campusId, teacherPublicKeyPem,
            activeSessionId, expectedTarget, nowUtc);
        return ApplyVerified(grant);
    }

    public ClassroomEventAccessGrant Apply(string signedGrant, string campusId, string teacherPublicKeyPem,
        Guid activeSessionId, DateTimeOffset nowUtc)
    {
        var grant = ClassroomEventCryptography.VerifyGrant(signedGrant, campusId, teacherPublicKeyPem,
            activeSessionId, nowUtc);
        return ApplyVerified(grant);
    }

    private ClassroomEventAccessGrant ApplyVerified(ClassroomEventAccessGrant grant)
    {
        lock (_gate)
        {
            if (_lastSessionId == grant.SessionId &&
                (_lastMessageId == grant.MessageId || _lastIssuedUtc is { } lastIssued &&
                 grant.IssuedUtc <= lastIssued))
                throw new InvalidDataException("重复或较旧的课堂事件授权已拒绝。");
            _lastSessionId = grant.SessionId;
            _lastMessageId = grant.MessageId;
            _lastIssuedUtc = grant.IssuedUtc;
            _current = grant;
            return grant;
        }
    }

    public ClassroomEventAccessGrant? Read(string campusId, Guid activeSessionId, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var grant = _current;
            return grant is not null && grant.CampusId == campusId && grant.SessionId == activeSessionId &&
                   grant.ExpiresUtc > nowUtc.ToUniversalTime() ? grant : null;
        }
    }

    public bool Clear(Guid sessionId)
    {
        lock (_gate)
        {
            if (_current?.SessionId != sessionId) return false;
            _current = null;
            return true;
        }
    }

    public bool ClearUnless(Guid? activeSessionId)
    {
        lock (_gate)
        {
            if (_current is null || activeSessionId == _current.SessionId) return false;
            _current = null;
            return true;
        }
    }
}
