using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeyonCampus.Core;

namespace VeyonCampus.Core.Maintenance;

public sealed record MaintenanceServiceChannelMessage(
    int SchemaVersion,
    string Type,
    string? CampusId = null,
    string? Nonce = null,
    string? Target = null,
    string? AgentIdentityFingerprint = null,
    string? MaintenanceIdentityFingerprint = null,
    string? SignedEnrollmentJson = null,
    string? Signature = null,
    Guid? RequestId = null,
    string? SignedRequestJson = null,
    string? SignedResponseJson = null);

/// <summary>Fixed WebSocket envelope for the independent service channel; never carries raw paths or commands.</summary>
public static class MaintenanceServiceChannelProtocol
{
    public const int CurrentVersion = 1;
    public const string WebSocketPath = "/api/maintenance/channel";
    public const string WebSocketSubProtocol = "veyon-campus-maintenance.v1";
    public const string Purpose = "VeyonCampus.Maintenance.ChannelHello.v1";
    public const int MaximumMessageBytes = 768 * 1024;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };

    public static byte[] Serialize(MaintenanceServiceChannelMessage message)
    {
        ValidateMessage(message);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (bytes.Length is 0 or > MaximumMessageBytes)
            throw new InvalidDataException("Maintenance 通道消息超过大小限制。");
        return bytes;
    }

    public static MaintenanceServiceChannelMessage Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumMessageBytes)
            throw new InvalidDataException("Maintenance 通道消息大小无效。");
        var json = bytes.ToArray();
        PolicyJson.RejectDuplicateFields(json);
        var message = JsonSerializer.Deserialize<MaintenanceServiceChannelMessage>(json, JsonOptions)
                      ?? throw new InvalidDataException("Maintenance 通道消息为空。");
        ValidateMessage(message);
        if (!bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions)))
            throw new InvalidDataException("Maintenance 通道消息不是规范 JSON。");
        return message;
    }

    public static MaintenanceServiceChannelMessage CreateHello(string campusId, string nonce,
        string signedEnrollmentJson, string agentIdentityFingerprint,
        string maintenanceIdentityFingerprint, RSA maintenancePrivateKey)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        ValidateNonce(nonce);
        ArgumentException.ThrowIfNullOrWhiteSpace(signedEnrollmentJson);
        if (Encoding.UTF8.GetByteCount(signedEnrollmentJson) > 64 * 1024)
            throw new InvalidDataException("Maintenance 身份登记超过握手大小限制。");
        ArgumentNullException.ThrowIfNull(maintenancePrivateKey);
        var agentFingerprint = MaintenanceValidation.Fingerprint(agentIdentityFingerprint,
            nameof(agentIdentityFingerprint));
        var maintenanceFingerprint = MaintenanceValidation.Fingerprint(maintenanceIdentityFingerprint,
            nameof(maintenanceIdentityFingerprint));
        if (maintenancePrivateKey.KeySize is < 2048 or > 4096 ||
            !string.Equals(StudentAgentResponseCryptography.GetFingerprint(
                maintenancePrivateKey.ExportSubjectPublicKeyInfoPem()), maintenanceFingerprint,
                StringComparison.Ordinal))
            throw new InvalidDataException("Maintenance 通道私钥与身份登记指纹不匹配。");
        var transcript = CreateHelloSignatureBytes(campusId, nonce, signedEnrollmentJson,
            agentFingerprint, maintenanceFingerprint);
        var signature = maintenancePrivateKey.SignData(transcript, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        return new MaintenanceServiceChannelMessage(CurrentVersion, "hello", campusId, nonce,
            AgentIdentityFingerprint: agentFingerprint,
            MaintenanceIdentityFingerprint: maintenanceFingerprint,
            SignedEnrollmentJson: signedEnrollmentJson,
            Signature: Convert.ToBase64String(signature));
    }

    public static bool VerifyHello(MaintenanceServiceChannelMessage message, string expectedCampusId,
        string expectedNonce, VerifiedMaintenanceServiceIdentityEnrollment enrollment)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        try
        {
            if (message.Type != "hello" || message.CampusId != expectedCampusId ||
                message.Nonce != expectedNonce || message.SignedEnrollmentJson is null ||
                message.Signature is null || message.AgentIdentityFingerprint is null ||
                message.MaintenanceIdentityFingerprint is null ||
                !string.Equals(MaintenanceValidation.Fingerprint(message.AgentIdentityFingerprint,
                        nameof(message.AgentIdentityFingerprint)), enrollment.AgentIdentityFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(MaintenanceValidation.Fingerprint(message.MaintenanceIdentityFingerprint,
                        nameof(message.MaintenanceIdentityFingerprint)), enrollment.MaintenanceIdentityFingerprint,
                    StringComparison.Ordinal))
                return false;

            var suppliedEnrollmentSha256 = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(message.SignedEnrollmentJson)));
            if (!string.Equals(suppliedEnrollmentSha256, enrollment.SignedEnrollmentSha256,
                    StringComparison.Ordinal))
                return false;

            using var key = RSA.Create();
            key.ImportFromPem(enrollment.MaintenancePublicKeyPem);
            var signature = Convert.FromBase64String(message.Signature);
            if (!string.Equals(Convert.ToBase64String(signature), message.Signature, StringComparison.Ordinal) ||
                signature.Length != key.KeySize / 8)
                return false;
            return key.VerifyData(CreateHelloSignatureBytes(expectedCampusId, expectedNonce,
                    message.SignedEnrollmentJson, enrollment.AgentIdentityFingerprint,
                    enrollment.MaintenanceIdentityFingerprint),
                signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or
                                          ArgumentException or InvalidDataException)
        { return false; }
    }

    private static byte[] CreateHelloSignatureBytes(string campusId, string nonce,
        string signedEnrollmentJson, string agentFingerprint, string maintenanceFingerprint)
    {
        ValidateNonce(nonce);
        var enrollmentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedEnrollmentJson)));
        return Encoding.UTF8.GetBytes(string.Join("\n", Purpose, campusId, nonce, agentFingerprint,
            maintenanceFingerprint, enrollmentHash));
    }

    private static void ValidateMessage(MaintenanceServiceChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.SchemaVersion != CurrentVersion || message.Type is not
            ("challenge" or "hello" or "ready" or "request" or "response" or "heartbeat"))
            throw new InvalidDataException("Maintenance 通道消息版本或类型无效。");
        switch (message.Type)
        {
            case "challenge":
                WebsitePolicySigningKeyStore.ValidateCampusId(message.CampusId ?? "");
                ValidateNonce(message.Nonce ?? "");
                RejectUnrelatedFields(message, allowCampus: true, allowNonce: true);
                break;
            case "hello":
                WebsitePolicySigningKeyStore.ValidateCampusId(message.CampusId ?? "");
                ValidateNonce(message.Nonce ?? "");
                _ = MaintenanceValidation.Fingerprint(message.AgentIdentityFingerprint ?? "",
                    nameof(message.AgentIdentityFingerprint));
                _ = MaintenanceValidation.Fingerprint(message.MaintenanceIdentityFingerprint ?? "",
                    nameof(message.MaintenanceIdentityFingerprint));
                EnsureText(message.SignedEnrollmentJson, 64 * 1024, "身份登记");
                EnsureText(message.Signature, 1024, "通道签名");
                RejectUnrelatedFields(message, allowCampus: true, allowNonce: true,
                    allowAgentFingerprint: true, allowMaintenanceFingerprint: true,
                    allowEnrollment: true, allowSignature: true);
                break;
            case "ready":
                WebsitePolicySigningKeyStore.ValidateCampusId(message.CampusId ?? "");
                _ = MaintenanceValidation.Fingerprint(message.MaintenanceIdentityFingerprint ?? "",
                    nameof(message.MaintenanceIdentityFingerprint));
                if (string.IsNullOrWhiteSpace(message.Target) || message.Target.Length > 256 ||
                    message.Target.Any(char.IsControl))
                    throw new InvalidDataException("Maintenance 通道 ready 目标无效。");
                RejectUnrelatedFields(message, allowCampus: true, allowTarget: true,
                    allowMaintenanceFingerprint: true);
                break;
            case "request":
                if (message.RequestId is null || message.RequestId == Guid.Empty)
                    throw new InvalidDataException("Maintenance 通道请求缺少 requestId。");
                EnsureText(message.SignedRequestJson, MaintenanceProtocolV1.MaximumEnvelopeBytes,
                    "签名命令");
                RejectUnrelatedFields(message, allowRequestId: true, allowRequest: true);
                break;
            case "response":
                if (message.RequestId is null || message.RequestId == Guid.Empty)
                    throw new InvalidDataException("Maintenance 通道响应缺少 requestId。");
                EnsureText(message.SignedResponseJson, MaintenanceProtocolV1.MaximumEnvelopeBytes,
                    "签名响应");
                RejectUnrelatedFields(message, allowRequestId: true, allowResponse: true);
                break;
            case "heartbeat":
                RejectUnrelatedFields(message);
                break;
        }
    }

    private static void ValidateNonce(string nonce)
    {
        try
        {
            var bytes = Convert.FromBase64String(nonce);
            if (bytes.Length != 32 || Convert.ToBase64String(bytes) != nonce)
                throw new InvalidDataException("Maintenance 握手随机数无效。");
        }
        catch (FormatException exception)
        { throw new InvalidDataException("Maintenance 握手随机数编码无效。", exception); }
    }

    private static void EnsureText(string? value, int maximumBytes, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(value) > maximumBytes)
            throw new InvalidDataException($"Maintenance 通道{label}字段无效。");
    }

    private static void RejectUnrelatedFields(MaintenanceServiceChannelMessage message,
        bool allowCampus = false, bool allowNonce = false, bool allowTarget = false,
        bool allowAgentFingerprint = false, bool allowMaintenanceFingerprint = false,
        bool allowEnrollment = false, bool allowSignature = false, bool allowRequestId = false,
        bool allowRequest = false, bool allowResponse = false)
    {
        if (!allowCampus && message.CampusId is not null || !allowNonce && message.Nonce is not null ||
            !allowTarget && message.Target is not null ||
            !allowAgentFingerprint && message.AgentIdentityFingerprint is not null ||
            !allowMaintenanceFingerprint && message.MaintenanceIdentityFingerprint is not null ||
            !allowEnrollment && message.SignedEnrollmentJson is not null ||
            !allowSignature && message.Signature is not null || !allowRequestId && message.RequestId is not null ||
            !allowRequest && message.SignedRequestJson is not null ||
            !allowResponse && message.SignedResponseJson is not null)
            throw new InvalidDataException("Maintenance 通道消息携带了与类型无关的字段。");
    }
}

/// <summary>Routes only signed protocol envelopes over currently authenticated service sessions.</summary>
public static class MaintenanceServiceChannelHub
{
    private static readonly ConcurrentDictionary<string, Registration> Connections = new(StringComparer.Ordinal);

    public static bool IsConnected(string campusId, string target) =>
        Connections.ContainsKey(Key(campusId, target));

    public static IDisposable Register(string campusId, string target,
        Func<string, CancellationToken, Task<string>> sendAsync)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);
        var key = Key(campusId, target);
        var registration = new Registration(Guid.NewGuid(), sendAsync);
        Connections[key] = registration;
        return new RegistrationLease(key, registration);
    }

    public static Task<string> SendAsync(string campusId, string target, string signedRequestJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedRequestJson);
        return Connections.TryGetValue(Key(campusId, target), out var registration)
            ? registration.SendAsync(signedRequestJson, cancellationToken)
            : throw new IOException("目标学生 Maintenance 服务当前没有已认证连接。");
    }

    private static string Key(string campusId, string target)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var normalized = WebsitePolicyTransport.NormalizeTargets([target]);
        if (normalized.Count != 1) throw new InvalidDataException("Maintenance 服务路由目标无效。");
        return campusId + "\n" + normalized[0].ToLowerInvariant();
    }

    private sealed class Registration(Guid id, Func<string, CancellationToken, Task<string>> sendAsync)
    {
        public Guid Id { get; } = id;
        public Task<string> SendAsync(string signedRequestJson, CancellationToken cancellationToken) =>
            sendAsync(signedRequestJson, cancellationToken);
    }

    private sealed class RegistrationLease(string key, Registration registration) : IDisposable
    {
        public void Dispose()
        {
            if (Connections.TryGetValue(key, out var current) && current.Id == registration.Id)
                Connections.TryRemove(key, out _);
        }
    }
}
