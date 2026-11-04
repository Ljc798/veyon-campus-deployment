using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;

namespace VeyonCampus.App.Maintenance;

internal sealed record MaintenanceServiceTeacherReply<TPayload>(
    MaintenanceProtocolV1Request Request,
    MaintenanceProtocolV1Response Response,
    TPayload? Payload,
    string SignedResponseSha256) where TPayload : class;

/// <summary>Teacher-side signed command client for the independently authenticated SYSTEM service.</summary>
internal sealed class MaintenanceServiceTeacherClient
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    private readonly MaintenanceServiceTrustStore _trustStore;

    public MaintenanceServiceTeacherClient(MaintenanceServiceTrustStore? trustStore = null) =>
        _trustStore = trustStore ?? new MaintenanceServiceTrustStore();

    public bool HasPinnedIdentity(MaintenanceDeviceIdentity device) =>
        device is not null && _trustStore.FindTrustedPublicKey(device.CampusId, device.RouteAddress,
            device.PinnedIdentityFingerprint) is not null;

    public bool HasConnectedChannel(MaintenanceDeviceIdentity device)
    {
        if (device is null) return false;
        var connectedIdentityFingerprint = MaintenanceServiceChannelHub.GetConnectedIdentityFingerprint(
            device.CampusId, device.RouteAddress);
        return connectedIdentityFingerprint is not null &&
               _trustStore.FindTrustedPublicKeyForIdentity(device.CampusId, device.RouteAddress,
                   device.PinnedIdentityFingerprint, connectedIdentityFingerprint) is not null;
    }

    public bool IsAvailable(MaintenanceDeviceIdentity device) =>
        HasConnectedChannel(device) && HasPinnedIdentity(device);

    public async Task<MaintenanceServiceTeacherReply<TPayload>> SendAsync<TPayload>(
        MaintenanceDeviceIdentity device, Guid operationId, Guid? commandId,
        MaintenanceProtocolV1Command command, RSA teacherSigningKey,
        MaintenanceTeacherBootstrapConfiguration? teacherConfiguration = null,
        string? targetTeacherConfigurationFingerprint = null,
        Func<Guid, string, CancellationToken, Task>? beforeSend = null,
        CancellationToken cancellationToken = default,
        string? targetSetupVersion = null,
        string? releaseSha256 = null,
        string? signedReleaseEnvelopeJson = null,
        string? artifactTransferUrl = null,
        long? targetTeacherConfigurationRevision = null,
        string? targetMaintenanceServiceVersion = null) where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(teacherSigningKey);
        var now = DateTimeOffset.UtcNow;
        var request = new MaintenanceProtocolV1Request(
            MaintenanceProtocolV1.CurrentVersion,
            MaintenanceProtocolV1.RequestPurpose,
            device.CampusId,
            device.PinnedIdentityFingerprint,
            Guid.NewGuid(),
            now,
            now.AddMinutes(2),
            operationId,
            commandId,
            command,
            TargetSetupVersion: targetSetupVersion,
            ReleaseSha256: releaseSha256,
            SignedReleaseEnvelopeJson: signedReleaseEnvelopeJson,
            ArtifactTransferUrl: artifactTransferUrl,
            TargetTeacherConfigurationFingerprint: targetTeacherConfigurationFingerprint,
            TeacherConfiguration: teacherConfiguration,
            TargetTeacherConfigurationRevision: targetTeacherConfigurationRevision,
            TargetMaintenanceServiceVersion: targetMaintenanceServiceVersion);
        string signedRequest;
        lock (teacherSigningKey)
            signedRequest = MaintenanceProtocolV1.SignRequest(request, teacherSigningKey, now);
        var signedRequestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedRequest)));
        if (beforeSend is not null)
        {
            if (commandId is not { } preparedCommandId)
                throw new InvalidDataException("Maintenance 持久化命令必须先绑定 commandId。" );
            await beforeSend(preparedCommandId, signedRequestSha256, cancellationToken).ConfigureAwait(false);
        }

        var connectedIdentityFingerprint = MaintenanceServiceChannelHub.GetConnectedIdentityFingerprint(
            device.CampusId, device.RouteAddress)
            ?? throw new IOException("目标 Maintenance 服务当前没有已认证连接。" );
        var trustedPublicKey = _trustStore.FindTrustedPublicKeyForIdentity(device.CampusId,
                                 device.RouteAddress, device.PinnedIdentityFingerprint,
                                 connectedIdentityFingerprint)
                             ?? throw new InvalidDataException("当前 Maintenance 服务身份未固定或回退信任窗口已到期；未发送签名命令。" );
        var signedResponse = await MaintenanceServiceChannelHub.SendAsync(device.CampusId,
            device.RouteAddress, signedRequest, cancellationToken).ConfigureAwait(false);
        var currentTrustedPublicKey = _trustStore.FindTrustedPublicKeyForIdentity(device.CampusId,
            device.RouteAddress, device.PinnedIdentityFingerprint, connectedIdentityFingerprint);
        if (!string.Equals(currentTrustedPublicKey, trustedPublicKey, StringComparison.Ordinal))
            throw new InvalidDataException("Maintenance 服务身份信任已变化；拒绝接受该响应。" );
        var response = MaintenanceProtocolV1.VerifyResponse(signedResponse, trustedPublicKey, request,
            DateTimeOffset.UtcNow);
        var responseSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedResponse)));
        return new MaintenanceServiceTeacherReply<TPayload>(request, response,
            ReadPayload<TPayload>(response.PayloadJson), responseSha256);
    }

    private static TPayload? ReadPayload<TPayload>(string? payloadJson) where TPayload : class
    {
        if (payloadJson is null) return null;
        var bytes = Encoding.UTF8.GetBytes(payloadJson);
        if (bytes.Length is 0 or > MaintenanceProtocolV1.MaximumPayloadBytes)
            throw new InvalidDataException("Maintenance 响应正文大小无效。" );
        PolicyJson.RejectDuplicateFields(bytes);
        var payload = JsonSerializer.Deserialize<TPayload>(bytes, PayloadJsonOptions)
                      ?? throw new InvalidDataException("Maintenance 响应正文为空。" );
        if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJsonOptions)))
            throw new InvalidDataException("Maintenance 响应正文不是规范 JSON。" );
        return payload;
    }
}
