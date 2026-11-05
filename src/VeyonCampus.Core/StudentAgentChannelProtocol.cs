using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>Messages exchanged over the student-initiated, authenticated classroom channel.</summary>
public sealed record StudentAgentChannelMessage(int SchemaVersion, string Type,
    string? CampusId = null, string? Target = null, string? Nonce = null,
    string? PublicKeyPem = null, string? Signature = null, Guid? RequestId = null,
    string? Method = null, string? PathAndQuery = null, string? ContentType = null,
    IReadOnlyDictionary<string, string[]>? Headers = null, byte[]? Body = null,
    int? StatusCode = null);

public sealed record StudentAgentChannelBootstrap(int SchemaVersion, string CampusId,
    IReadOnlyList<string> Endpoints, string RootCertificateBase64);

/// <summary>Shared wire format and connection registry used by the teacher and SYSTEM Agent.</summary>
public static class StudentAgentChannelProtocol
{
    public const int CurrentVersion = 1;
    public const string WebSocketPath = "/api/student-agent/channel";
    public const string WebSocketSubProtocolV1 = "veyon-campus-agent-channel.v1";
    public const string Purpose = "VeyonCampus.StudentAgentChannel.Hello.v1";
    public const int MaximumMessageBytes = 8 * 1024 * 1024;
    public const int MaximumRequestBodyBytes = 4 * 1024 * 1024;
    public const int MaximumResponseBodyBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    public static string? SelectWebSocketSubProtocol(IEnumerable<string> requestedSubProtocols)
    {
        ArgumentNullException.ThrowIfNull(requestedSubProtocols);
        return requestedSubProtocols.Contains(WebSocketSubProtocolV1, StringComparer.Ordinal)
            ? WebSocketSubProtocolV1
            : null;
    }

    public static bool IsCompatibleNegotiatedSubProtocol(string? subProtocol) =>
        subProtocol is null or WebSocketSubProtocolV1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static void ValidateBootstrapConfiguration(string campusId, IReadOnlyList<string>? endpoints,
        string? rootCertificateBase64)
    {
        if (endpoints is null && rootCertificateBase64 is null) return;
        if (endpoints is null || endpoints.Count is 0 or > 16 || string.IsNullOrWhiteSpace(rootCertificateBase64))
            throw new InvalidDataException("教师连接地址和 TLS 根证书必须同时配置。");
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 256 ||
                !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.Port != ClassroomEventCryptography.TeacherHttpsPort ||
                uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0 ||
                !IPAddress.TryParse(uri.Host, out var address) ||
                !ClassroomEventCryptography.IsPrivateIpv4Address(address) ||
                !string.Equals(uri.AbsoluteUri, endpoint, StringComparison.Ordinal) || !unique.Add(endpoint))
                throw new InvalidDataException("教师连接地址必须是唯一的私有 IPv4 HTTPS 地址。");
        }
        ValidateRootCertificate(rootCertificateBase64);
    }

    public static string? GetBootstrapConfigurationFingerprint(string campusId,
        IReadOnlyList<string>? endpoints, string? rootCertificateBase64)
    {
        ValidateBootstrapConfiguration(campusId, endpoints, rootCertificateBase64);
        if (endpoints is not { Count: > 0 } || string.IsNullOrWhiteSpace(rootCertificateBase64)) return null;
        var canonical = campusId + "\n" + string.Join("\n", endpoints) + "\n" + rootCertificateBase64;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static X509Certificate2 ValidateRootCertificate(string rootCertificateBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootCertificateBase64);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(rootCertificateBase64); }
        catch (FormatException exception) { throw new InvalidDataException("教师 TLS 根证书编码无效。", exception); }
        if (bytes.Length is 0 or > 16 * 1024)
            throw new InvalidDataException("教师 TLS 根证书大小无效。");
        try
        {
            var certificate = X509CertificateLoader.LoadCertificate(bytes);
            var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            if (certificate.Subject != "CN=Veyon Campus Mobile Control CA" ||
                certificate.Issuer != certificate.Subject || constraints?.CertificateAuthority != true ||
                usage is null || !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign) ||
                certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30))
            {
                certificate.Dispose();
                throw new InvalidDataException("教师 TLS 根证书主题、用途或有效期无效。");
            }
            return certificate;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("教师 TLS 根证书无效。", exception);
        }
    }

    public static byte[] Serialize(StudentAgentChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.SchemaVersion != CurrentVersion || string.IsNullOrWhiteSpace(message.Type) ||
            message.Type.Length > 32 || message.Type.Any(char.IsControl))
            throw new InvalidDataException("学生连接消息版本或类型无效。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (bytes.Length is 0 or > MaximumMessageBytes)
            throw new InvalidDataException("学生连接消息超过大小限制。");
        return bytes;
    }

    public static StudentAgentChannelMessage Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumMessageBytes)
            throw new InvalidDataException("学生连接消息大小无效。");
        var jsonBytes = bytes.ToArray();
        PolicyJson.RejectDuplicateFields(jsonBytes);
        var message = JsonSerializer.Deserialize<StudentAgentChannelMessage>(jsonBytes, JsonOptions)
                      ?? throw new InvalidDataException("学生连接消息为空。");
        if (message.SchemaVersion != CurrentVersion || string.IsNullOrWhiteSpace(message.Type) ||
            message.Type.Length > 32 || message.Type.Any(char.IsControl))
            throw new InvalidDataException("学生连接消息版本或类型无效。");
        return message;
    }

    public static byte[] CreateHelloSignatureBytes(string campusId, string nonce, string publicKeyPem)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length > 128 || nonce.Any(char.IsControl))
            throw new InvalidDataException("学生连接随机数无效。");
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        using var publicKey = RSA.Create();
        publicKey.ImportFromPem(publicKeyPem);
        if (publicKey.KeySize is < 2048 or > 4096 ||
            publicKey.ExportSubjectPublicKeyInfoPem() != publicKeyPem)
            throw new InvalidDataException("学生连接身份公钥格式无效。");
        var fingerprint = StudentAgentResponseCryptography.GetFingerprint(publicKeyPem);
        return Encoding.UTF8.GetBytes($"{Purpose}\n{campusId}\n{nonce}\n{fingerprint}");
    }

    public static StudentAgentChannelMessage CreateHello(string campusId, string nonce,
        RSA agentIdentityKey)
    {
        ArgumentNullException.ThrowIfNull(agentIdentityKey);
        if (agentIdentityKey.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("学生 Agent 身份密钥位长无效。");
        var publicKeyPem = agentIdentityKey.ExportSubjectPublicKeyInfoPem();
        var signature = agentIdentityKey.SignData(CreateHelloSignatureBytes(campusId, nonce, publicKeyPem),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new StudentAgentChannelMessage(CurrentVersion, "hello", campusId,
            Nonce: nonce, PublicKeyPem: publicKeyPem, Signature: Convert.ToBase64String(signature));
    }

    public static bool VerifyHello(StudentAgentChannelMessage message, string expectedCampusId,
        string expectedNonce, string pinnedPublicKeyPem)
    {
        if (message.Type != "hello" || message.CampusId != expectedCampusId ||
            message.Nonce != expectedNonce ||
            string.IsNullOrWhiteSpace(message.PublicKeyPem) || string.IsNullOrWhiteSpace(message.Signature))
            return false;
        try
        {
            using var pinned = RSA.Create();
            using var supplied = RSA.Create();
            pinned.ImportFromPem(pinnedPublicKeyPem);
            supplied.ImportFromPem(message.PublicKeyPem);
            if (pinned.KeySize is < 2048 or > 4096 || supplied.KeySize != pinned.KeySize ||
                !CryptographicOperations.FixedTimeEquals(pinned.ExportSubjectPublicKeyInfo(),
                    supplied.ExportSubjectPublicKeyInfo())) return false;
            var signature = Convert.FromBase64String(message.Signature);
            return supplied.VerifyData(CreateHelloSignatureBytes(expectedCampusId, expectedNonce,
                    message.PublicKeyPem),
                signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or
            ArgumentException or InvalidDataException)
        {
            return false;
        }
    }
}

/// <summary>Routes existing HTTP-based Agent protocols through a live WebSocket when available.</summary>
public static class StudentAgentChannelHub
{
    private static readonly StudentAgentChannelRegistry Registry = new(
        (campusId, target) => StudentAgentChannelSeenTargetStore.Mark(campusId, target),
        StudentAgentChannelSeenTargetStore.Contains);

    public static bool HasConnectedBefore(string campusId, string target) =>
        Registry.HasConnectedBefore(campusId, target);

    public static bool IsConnected(string campusId, string target) => Registry.IsConnected(campusId, target);

    public static IDisposable Register(string campusId, string target,
        Func<StudentAgentChannelMessage, CancellationToken, Task<StudentAgentChannelMessage>> sendAsync) =>
        Registry.Register(campusId, target, sendAsync);

    public static Task<StudentAgentChannelMessage> SendAsync(string campusId, string target,
        StudentAgentChannelMessage message, CancellationToken cancellationToken = default)
        => Registry.SendAsync(campusId, target, message, cancellationToken);

    public static async Task<HttpResponseMessage> SendHttpAsync(string campusId, string target,
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = Guid.NewGuid();
        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > StudentAgentChannelProtocol.MaximumRequestBodyBytes)
            throw new InvalidDataException("经学生连接发送的请求正文超过大小限制。");
        var uri = request.RequestUri ?? throw new InvalidDataException("学生 Agent 请求 URI 缺失。");
        var headers = request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
            foreach (var pair in request.Content.Headers)
                headers[pair.Key] = pair.Value.ToArray();
        var channelRequest = new StudentAgentChannelMessage(StudentAgentChannelProtocol.CurrentVersion,
            "http-request", RequestId: requestId, Method: request.Method.Method,
            PathAndQuery: uri.PathAndQuery, ContentType: request.Content?.Headers.ContentType?.ToString(),
            Headers: headers, Body: body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await SendAsync(campusId, target, channelRequest, timeout.Token).ConfigureAwait(false);
        if (result.Type != "http-response" || result.RequestId != requestId || result.StatusCode is < 100 or > 599 ||
            result.Body is { Length: > StudentAgentChannelProtocol.MaximumResponseBodyBytes })
            throw new InvalidDataException("学生 Agent 返回的连接响应无效。");
        var response = new HttpResponseMessage((HttpStatusCode)result.StatusCode.GetValueOrDefault())
        {
            RequestMessage = request,
            Content = new ByteArrayContent(result.Body ?? Array.Empty<byte>())
        };
        if (result.ContentType is { Length: > 0 } contentType &&
            MediaTypeHeaderValue.TryParse(contentType, out var parsedContentType))
            response.Content.Headers.ContentType = parsedContentType;
        if (result.Headers is not null)
            foreach (var pair in result.Headers)
            {
                if (pair.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    response.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                else response.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        return response;
    }

}

internal readonly record struct StudentAgentChannelKey(string CampusId, string Target);

internal sealed class StudentAgentChannelKeyComparer : IEqualityComparer<StudentAgentChannelKey>
{
    public static readonly StudentAgentChannelKeyComparer Instance = new();

    public bool Equals(StudentAgentChannelKey left, StudentAgentChannelKey right) =>
        string.Equals(left.CampusId, right.CampusId, StringComparison.Ordinal) &&
        string.Equals(left.Target, right.Target, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(StudentAgentChannelKey key) =>
        HashCode.Combine(StringComparer.Ordinal.GetHashCode(key.CampusId),
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.Target));
}

internal sealed class StudentAgentChannelRegistry(
    Action<string, string> persistSeenTarget,
    Func<string, string, bool> hasPersistedSeenTarget)
{
    private sealed record Registration(Guid Id,
        Func<StudentAgentChannelMessage, CancellationToken, Task<StudentAgentChannelMessage>> SendAsync);

    private readonly ConcurrentDictionary<StudentAgentChannelKey, Registration> _online =
        new(StudentAgentChannelKeyComparer.Instance);
    private readonly ConcurrentDictionary<StudentAgentChannelKey, byte> _previouslyConnected =
        new(StudentAgentChannelKeyComparer.Instance);

    public bool HasConnectedBefore(string campusId, string target)
    {
        var key = CreateKey(campusId, target);
        return _previouslyConnected.ContainsKey(key) || hasPersistedSeenTarget(key.CampusId, key.Target);
    }

    public bool IsConnected(string campusId, string target) => _online.ContainsKey(CreateKey(campusId, target));

    public IDisposable Register(string campusId, string target,
        Func<StudentAgentChannelMessage, CancellationToken, Task<StudentAgentChannelMessage>> sendAsync)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);
        var key = CreateKey(campusId, target);
        var registration = new Registration(Guid.NewGuid(), sendAsync);
        try { persistSeenTarget(key.CampusId, key.Target); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Security.SecurityException or NotSupportedException)
        {
            Trace.TraceError("Could not persist student channel target {0}/{1}: {2}",
                key.CampusId, key.Target, exception);
        }
        _online[key] = registration;
        _previouslyConnected[key] = 0;
        return new RegistrationLease(this, key, registration.Id);
    }

    public async Task<StudentAgentChannelMessage> SendAsync(string campusId, string target,
        StudentAgentChannelMessage message, CancellationToken cancellationToken = default)
    {
        var key = CreateKey(campusId, target);
        if (!_online.TryGetValue(key, out var registration))
            throw new HttpRequestException("学生 Agent 当前离线；本次请求未发送，请在 Agent 重新连接后重试。");
        return await registration.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static StudentAgentChannelKey CreateKey(string campusId, string target)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        return new StudentAgentChannelKey(campusId,
            ClassroomEventCryptography.NormalizeSingleTarget(target));
    }

    private sealed class RegistrationLease(StudentAgentChannelRegistry owner, StudentAgentChannelKey key,
        Guid registrationId) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (owner._online.TryGetValue(key, out var current) && current.Id == registrationId)
                owner._online.TryRemove(new KeyValuePair<StudentAgentChannelKey, Registration>(key, current));
        }
    }
}

/// <summary>
/// Remembers channel-enabled targets across TeacherConsole restarts so an offline student never
/// falls back to repeated direct HTTP connection attempts after a teacher reboot.
/// </summary>
internal static class StudentAgentChannelSeenTargetStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    private static HashSet<StudentAgentChannelKey>? _targets;
    private static string PathName => Path.Combine(VeyonCampusPaths.GetLocalApplicationDataDirectory(),
        "VeyonCampus", "StudentAgentChannel", "targets.json");

    public static bool Contains(string campusId, string target)
    {
        lock (Gate) return ReadTargets().Contains(CreateKey(campusId, target));
    }

    public static void Mark(string campusId, string target)
    {
        lock (Gate)
        {
            var targets = ReadTargets();
            if (!targets.Add(CreateKey(campusId, target))) return;
            if (targets.Count > 5000)
                throw new InvalidDataException("学生连接历史超过允许的电脑数量。");
            WriteTargets(targets);
        }
    }

    private static HashSet<StudentAgentChannelKey> ReadTargets()
    {
        if (_targets is not null) return _targets;
        var path = PathName;
        if (!File.Exists(path)) return _targets = new HashSet<StudentAgentChannelKey>(StudentAgentChannelKeyComparer.Instance);
        PathLinkSecurity.RejectLinks(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("学生连接历史文件大小无效。");
        var bytes = File.ReadAllBytes(path);
        PolicyJson.RejectDuplicateFields(bytes);
        var document = System.Text.Json.JsonSerializer.Deserialize<SeenTargetsDocument>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("学生连接历史文件为空。");
        var targets = new HashSet<StudentAgentChannelKey>(StudentAgentChannelKeyComparer.Instance);
        if (document.SchemaVersion == 1 && document.Targets is { Count: <= 5000 } legacyTargets)
        {
            // A v1 entry has no campus identity; treating it as shared history would cross campus boundaries.
            foreach (var rawTarget in legacyTargets)
            {
                var normalized = ClassroomEventCryptography.NormalizeSingleTarget(rawTarget);
                if (normalized != rawTarget)
                    throw new InvalidDataException("旧版学生连接历史包含无效电脑名称。");
            }
            return _targets = targets;
        }
        if (document.SchemaVersion != 2 || document.Entries is not { Count: <= 5000 } entries)
            throw new InvalidDataException("学生连接历史文件版本或电脑数量无效。");
        foreach (var entry in entries)
        {
            var key = CreateKey(entry.CampusId, entry.Target);
            if (key.Target != entry.Target || !targets.Add(key))
                throw new InvalidDataException("学生连接历史包含无效或重复的校区/电脑组合。");
        }
        return _targets = targets;
    }

    private static void WriteTargets(IReadOnlyCollection<StudentAgentChannelKey> targets)
    {
        var path = Path.GetFullPath(PathName);
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidDataException("学生连接历史目录无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new SeenTargetsDocument(2, null, targets.OrderBy(key => key.CampusId, StringComparer.Ordinal)
                .ThenBy(key => key.Target, StringComparer.OrdinalIgnoreCase)
                .Select(key => new SeenTargetEntry(key.CampusId, key.Target)).ToArray()), JsonOptions);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("学生连接历史文件超过大小限制。");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static StudentAgentChannelKey CreateKey(string campusId, string target)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        return new StudentAgentChannelKey(campusId,
            ClassroomEventCryptography.NormalizeSingleTarget(target));
    }

    private sealed record SeenTargetsDocument(int SchemaVersion, IReadOnlyList<string>? Targets = null,
        IReadOnlyList<SeenTargetEntry>? Entries = null);
    private sealed record SeenTargetEntry(string CampusId, string Target);
}

/// <summary>
/// Routes teacher control traffic over the authenticated student-initiated channel when available.
/// Explicitly opted-in, signed compatibility operations may fall back to direct LAN HTTP while that
/// channel is unavailable.
/// </summary>
public sealed class StudentAgentChannelHttpMessageHandler : DelegatingHandler
{
    private readonly string _campusId;
    private readonly bool _allowLegacyDirectFallback;
    private readonly bool _allowDirectFallbackWhenChannelOffline;

    public StudentAgentChannelHttpMessageHandler(string campusId, bool allowLegacyDirectFallback = false,
        bool allowDirectFallbackWhenChannelOffline = false)
        : base(CreateDirectHandler()) =>
        (_campusId, _allowLegacyDirectFallback, _allowDirectFallbackWhenChannelOffline) =
        (ValidateCampus(campusId), allowLegacyDirectFallback, allowDirectFallbackWhenChannelOffline);

    /// <summary>
    /// Windows resolves a plain host name to its IPv6 link-local address (fe80::/10) first. Dialling a
    /// link-local address without an interface scope does not fail fast: the connect attempt stalls until
    /// the request timeout. A student that only has a routable IPv4 address therefore produced
    /// "连接学生 Agent 超时" even though the agent was running and reachable over IPv4, which in turn
    /// kept the device on cached status and forced every maintenance run to manual review.
    /// </summary>
    private static SocketsHttpHandler CreateDirectHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectCallback = ConnectRoutableAddressAsync
    };

    private static async ValueTask<Stream> ConnectRoutableAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        IPAddress[] resolved;
        if (IPAddress.TryParse(host, out var literal))
            resolved = [literal];
        else
            resolved = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var candidates = OrderConnectCandidates(resolved);

        Exception? failure = null;
        foreach (var candidate in candidates)
        {
            var socket = new Socket(candidate.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(candidate, port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                failure = exception;
            }
        }
        throw new HttpRequestException($"无法连接 {host}:{port}；已尝试 {candidates.Length} 个解析地址。", failure);
    }

    /// <summary>Routable IPv4 first, then routable IPv6, then link-local/loopback as a last resort.</summary>
    internal static IPAddress[] OrderConnectCandidates(IReadOnlyList<IPAddress> resolved) =>
        resolved
            .OrderBy(address => IsUnusableFirstHop(address) ? 1 : 0)
            .ThenBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

    private static bool IsUnusableFirstHop(IPAddress address) =>
        address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IPAddress.IsLoopback(address);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Port != WebsitePolicyAgent.Port || uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.Host, out _) && Uri.CheckHostName(uri.Host) != UriHostNameType.Dns)
            return base.SendAsync(request, cancellationToken);
        var target = uri.Host;
        if (StudentAgentChannelHub.IsConnected(_campusId, target))
            return StudentAgentChannelHub.SendHttpAsync(_campusId, target, request, cancellationToken);
        if (ShouldUseDirectFallback(StudentAgentChannelHub.HasConnectedBefore(_campusId, target),
                _allowLegacyDirectFallback, _allowDirectFallbackWhenChannelOffline))
            return base.SendAsync(request, cancellationToken);
        if (StudentAgentChannelHub.HasConnectedBefore(_campusId, target))
            throw new HttpRequestException("学生 Agent 当前离线；本次请求未发送，请在 Agent 重新连接后重试。");
        throw new HttpRequestException("学生 Agent 尚未建立教师端长连接；已快速停止请求，不等待直连超时。请确认学生端已应用最新校区配置包并在线。");
    }

    internal static bool ShouldUseDirectFallback(bool hasConnectedBefore,
        bool allowLegacyDirectFallback, bool allowDirectFallbackWhenChannelOffline) =>
        allowDirectFallbackWhenChannelOffline || (!hasConnectedBefore && allowLegacyDirectFallback);

    private static string ValidateCampus(string campusId)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        return campusId;
    }
}
