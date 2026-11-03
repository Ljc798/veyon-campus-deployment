using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeyonCampus.Core;

namespace VeyonCampus.Core.Maintenance;

public enum MaintenanceProtocolV1Command
{
    GetHealth,
    GetCapabilities,
    GetInstalledVersions,
    OfferSignedStudentSetupUpdate,
    GetTransactionStatus,
    RecoverPendingTransaction,
    SyncTeacherEndpoint
}

/// <summary>The only remote operations accepted by the independent maintenance channel.</summary>
public sealed record MaintenanceTeacherBootstrapConfiguration(
    IReadOnlyList<string> Endpoints,
    string RootCertificateBase64);

public sealed record MaintenanceProtocolV1Request(
    int ProtocolVersion,
    string Purpose,
    string CampusId,
    string DeviceIdentityFingerprint,
    Guid Nonce,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    Guid OperationId,
    Guid? CommandId,
    MaintenanceProtocolV1Command Command,
    string? TargetSetupVersion = null,
    string? ReleaseSha256 = null,
    string? SignedReleaseEnvelopeJson = null,
    string? ArtifactTransferUrl = null,
    string? TargetTeacherConfigurationFingerprint = null,
    MaintenanceTeacherBootstrapConfiguration? TeacherConfiguration = null);

public sealed record MaintenanceProtocolV1Response(
    int ProtocolVersion,
    string Purpose,
    string CampusId,
    string DeviceIdentityFingerprint,
    Guid Nonce,
    Guid OperationId,
    Guid? CommandId,
    DateTimeOffset CollectedUtc,
    string ResultCode,
    string Detail,
    string? PayloadJson = null);

/// <summary>
/// Versioned, signed message contract for the independent SYSTEM Maintenance Service.
/// It intentionally contains no shell, process, executable-path, or arbitrary file operation.
/// </summary>
public static class MaintenanceProtocolV1
{
    public const int CurrentVersion = 1;
    public const string RequestPurpose = "VeyonCampus.Maintenance.Request.v1";
    public const string ResponsePurpose = "VeyonCampus.Maintenance.Response.v1";
    public const int MaximumPayloadBytes = 256 * 1024;
    public const int MaximumEnvelopeBytes = 512 * 1024;
    public static readonly TimeSpan MaximumCommandLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<MaintenanceProtocolV1Command>(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static string SignRequest(MaintenanceProtocolV1Request request, RSA teacherPrivateKey,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(teacherPrivateKey);
        EnsureKeySize(teacherPrivateKey);
        ValidateRequest(request, nowUtc ?? DateTimeOffset.UtcNow);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        return SignPayload(payload, teacherPrivateKey);
    }

    public static MaintenanceProtocolV1Request VerifyRequest(string signedJson, string teacherPublicKeyPem,
        string expectedCampusId, string expectedDeviceIdentityFingerprint, DateTimeOffset nowUtc)
    {
        var request = VerifyPayload<MaintenanceProtocolV1Request>(signedJson, teacherPublicKeyPem);
        ValidateRequest(request, nowUtc);
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        var expectedFingerprint = MaintenanceValidation.Fingerprint(expectedDeviceIdentityFingerprint,
            nameof(expectedDeviceIdentityFingerprint));
        var requestFingerprint = MaintenanceValidation.Fingerprint(request.DeviceIdentityFingerprint,
            nameof(request.DeviceIdentityFingerprint));
        if (!string.Equals(request.CampusId, expectedCampusId, StringComparison.Ordinal) ||
            !string.Equals(requestFingerprint, expectedFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Maintenance 命令的校区或设备固定身份不匹配。");
        return request;
    }

    public static string SignResponse(MaintenanceProtocolV1Response response, RSA devicePrivateKey)
    {
        ArgumentNullException.ThrowIfNull(devicePrivateKey);
        EnsureKeySize(devicePrivateKey);
        ValidateResponse(response);
        return SignPayload(JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions), devicePrivateKey);
    }

    public static MaintenanceProtocolV1Response VerifyResponse(string signedJson, string devicePublicKeyPem,
        MaintenanceProtocolV1Request request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = VerifyPayload<MaintenanceProtocolV1Response>(signedJson, devicePublicKeyPem);
        ValidateResponse(response);
        var now = nowUtc.ToUniversalTime();
        var requestFingerprint = MaintenanceValidation.Fingerprint(request.DeviceIdentityFingerprint,
            nameof(request.DeviceIdentityFingerprint));
        var responseFingerprint = MaintenanceValidation.Fingerprint(response.DeviceIdentityFingerprint,
            nameof(response.DeviceIdentityFingerprint));
        if (response.ProtocolVersion != request.ProtocolVersion || response.CampusId != request.CampusId ||
            !string.Equals(responseFingerprint, requestFingerprint, StringComparison.Ordinal) ||
            response.Nonce != request.Nonce || response.OperationId != request.OperationId ||
            response.CommandId != request.CommandId ||
            response.CollectedUtc > now.AddMinutes(2) || response.CollectedUtc < now.AddMinutes(-10))
            throw new InvalidDataException("Maintenance 签名响应与原请求身份、随机数或时序不匹配。");
        return response;
    }

    public static void ValidateRequest(MaintenanceProtocolV1Request request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        WebsitePolicySigningKeyStore.ValidateCampusId(request.CampusId);
        _ = MaintenanceValidation.Fingerprint(request.DeviceIdentityFingerprint,
            nameof(request.DeviceIdentityFingerprint));
        var now = nowUtc.ToUniversalTime();
        if (request.ProtocolVersion != CurrentVersion || request.Purpose != RequestPurpose ||
            !Enum.IsDefined(request.Command) || request.Nonce == Guid.Empty || request.OperationId == Guid.Empty ||
            request.CommandId == Guid.Empty || request.IssuedUtc.Offset != TimeSpan.Zero ||
            request.ExpiresUtc.Offset != TimeSpan.Zero || request.IssuedUtc > now.AddMinutes(1) ||
            request.IssuedUtc < now.Subtract(MaximumCommandLifetime) || request.ExpiresUtc <= now ||
            request.ExpiresUtc <= request.IssuedUtc || request.ExpiresUtc - request.IssuedUtc > MaximumCommandLifetime)
            throw new InvalidDataException("Maintenance 请求版本、随机数、时间窗或操作编号无效。");

        var hasRelease = request.TargetSetupVersion is not null || request.ReleaseSha256 is not null ||
                         request.SignedReleaseEnvelopeJson is not null || request.ArtifactTransferUrl is not null;
        var hasTeacherConfiguration = request.TargetTeacherConfigurationFingerprint is not null ||
                                      request.TeacherConfiguration is not null;
        switch (request.Command)
        {
            case MaintenanceProtocolV1Command.GetHealth:
            case MaintenanceProtocolV1Command.GetCapabilities:
            case MaintenanceProtocolV1Command.GetInstalledVersions:
                if (request.CommandId is not null || hasRelease || hasTeacherConfiguration)
                    throw new InvalidDataException("Maintenance 只读查询不能携带命令或安装/配置内容。");
                break;
            case MaintenanceProtocolV1Command.GetTransactionStatus:
            case MaintenanceProtocolV1Command.RecoverPendingTransaction:
                if (request.CommandId is null || hasRelease || hasTeacherConfiguration)
                    throw new InvalidDataException("事务查询或恢复必须绑定原 commandId，且不能携带其他副作用内容。");
                break;
            case MaintenanceProtocolV1Command.OfferSignedStudentSetupUpdate:
                if (request.CommandId is null || !hasRelease || hasTeacherConfiguration ||
                    request.TargetSetupVersion is null || request.ReleaseSha256 is null ||
                    request.SignedReleaseEnvelopeJson is null || request.ArtifactTransferUrl is null ||
                    Encoding.UTF8.GetByteCount(request.SignedReleaseEnvelopeJson) is 0 or > MaximumPayloadBytes)
                    throw new InvalidDataException("StudentSetup 更新命令必须绑定 commandId、目标版本、签名发行清单和摘要。");
                _ = ApplicationReleaseClient.CompareVersions(request.TargetSetupVersion,
                    request.TargetSetupVersion);
                _ = MaintenanceValidation.Fingerprint(request.ReleaseSha256, nameof(request.ReleaseSha256));
                _ = StudentApplicationUpdateCryptography.ValidateLocalDownloadUrl(request.ArtifactTransferUrl);
                break;
            case MaintenanceProtocolV1Command.SyncTeacherEndpoint:
                if (request.CommandId is null || hasRelease || !hasTeacherConfiguration ||
                    request.TargetTeacherConfigurationFingerprint is null || request.TeacherConfiguration is null)
                    throw new InvalidDataException("教师连接配置命令必须绑定 commandId、目标指纹和配置内容。");
                var fingerprint = StudentAgentChannelProtocol.GetBootstrapConfigurationFingerprint(request.CampusId,
                    request.TeacherConfiguration.Endpoints, request.TeacherConfiguration.RootCertificateBase64);
                if (!string.Equals(fingerprint, request.TargetTeacherConfigurationFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("教师连接配置内容与签名目标指纹不匹配。");
                break;
            default:
                throw new InvalidDataException("Maintenance 命令不在固定协议白名单内。");
        }
    }

    private static void ValidateResponse(MaintenanceProtocolV1Response response)
    {
        ArgumentNullException.ThrowIfNull(response);
        WebsitePolicySigningKeyStore.ValidateCampusId(response.CampusId);
        _ = MaintenanceValidation.Fingerprint(response.DeviceIdentityFingerprint,
            nameof(response.DeviceIdentityFingerprint));
        if (response.ProtocolVersion != CurrentVersion || response.Purpose != ResponsePurpose ||
            response.Nonce == Guid.Empty || response.OperationId == Guid.Empty || response.CommandId == Guid.Empty ||
            response.CollectedUtc.Offset != TimeSpan.Zero ||
            response.ResultCode is not ("succeeded" or "pending" or "notSupported" or "needsReview" or "failed") ||
            string.IsNullOrWhiteSpace(response.Detail) || response.Detail.Length > 512 ||
            response.Detail.Any(char.IsControl) || response.PayloadJson is { } payload &&
                (Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes || !IsJsonObject(payload)))
            throw new InvalidDataException("Maintenance 签名响应字段或大小无效。");
    }

    private static bool IsJsonObject(string json)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            PolicyJson.RejectDuplicateFields(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        { return false; }
    }

    private static string SignPayload(byte[] payload, RSA privateKey)
    {
        if (payload.Length is 0 or > MaximumPayloadBytes)
            throw new InvalidDataException("Maintenance 签名正文大小无效。");
        var envelope = new SignedMaintenanceMessage(Convert.ToBase64String(payload),
            Convert.ToBase64String(privateKey.SignData(payload, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length > MaximumEnvelopeBytes)
            throw new InvalidDataException("Maintenance 签名信封超过大小限制。");
        return Encoding.UTF8.GetString(bytes);
    }

    private static T VerifyPayload<T>(string signedJson, string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        var envelopeBytes = Encoding.UTF8.GetBytes(signedJson);
        if (envelopeBytes.Length > MaximumEnvelopeBytes)
            throw new InvalidDataException("Maintenance 签名信封超过大小限制。");
        try
        {
            PolicyJson.RejectDuplicateFields(envelopeBytes);
            var envelope = JsonSerializer.Deserialize<SignedMaintenanceMessage>(envelopeBytes, JsonOptions)
                           ?? throw new InvalidDataException("Maintenance 签名信封为空。");
            var payload = Convert.FromBase64String(envelope.Payload);
            var signature = Convert.FromBase64String(envelope.Signature);
            if (payload.Length is 0 or > MaximumPayloadBytes || signature.Length is < 256 or > 512)
                throw new InvalidDataException("Maintenance 签名字段大小无效。");
            PolicyJson.RejectDuplicateFields(payload);
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(publicKeyPem);
            EnsureKeySize(publicKey);
            if (!publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("Maintenance 签名无效。");
            var value = JsonSerializer.Deserialize<T>(payload, JsonOptions)
                        ?? throw new InvalidDataException("Maintenance 签名正文为空。");
            var canonical = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            if (!payload.AsSpan().SequenceEqual(canonical))
                throw new InvalidDataException("Maintenance 签名正文不是规范 JSON。");
            return value;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or
                                          ArgumentException)
        {
            throw new InvalidDataException("Maintenance 签名编码无效。", exception);
        }
    }

    private static void EnsureKeySize(RSA key)
    {
        if (key.KeySize is < 2048 or > 4096)
            throw new CryptographicException("Maintenance RSA 密钥必须为 2048–4096 位。");
    }

    private sealed record SignedMaintenanceMessage(string Payload, string Signature);
}
