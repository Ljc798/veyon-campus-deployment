using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record ClassroomEventGrantRequest(string Target, string SignedGrant);
public sealed record ClassroomEventGrantAcknowledgement(int SchemaVersion, string Purpose, string CampusId,
    Guid MessageId, Guid SessionId, string Target, string GrantSha256, DateTimeOffset ReceivedUtc);
public sealed record ClassroomEventGrantDeliveryResult(string Target, bool Succeeded, bool NeedsReview,
    string Detail, ClassroomEventGrantAcknowledgement? Acknowledgement = null, string? AgentFingerprint = null);

/// <summary>Pushes a teacher-signed, per-target event grant through the existing Agent port.</summary>
public static class ClassroomEventGrantTransport
{
    public const string AcknowledgementPurpose = "VeyonCampus.ClassroomEventGrantAcknowledgement.v1";
    private const int MaximumAcknowledgementBytes = 64 * 1024;

    public static string SignAcknowledgement(ClassroomEventAccessGrant grant, string signedGrant,
        DateTimeOffset receivedUtc, RSA agentPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(signedGrant);
        ArgumentNullException.ThrowIfNull(agentPrivateKey);
        var acknowledgement = new ClassroomEventGrantAcknowledgement(1, AcknowledgementPurpose,
            grant.CampusId, grant.MessageId, grant.SessionId, grant.Target,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedGrant))),
            receivedUtc.ToUniversalTime());
        ValidateAcknowledgement(acknowledgement, grant, signedGrant, receivedUtc);
        return StudentAgentResponseCryptography.Sign(acknowledgement, agentPrivateKey);
    }

    public static VerifiedStudentAgentResponse<ClassroomEventGrantAcknowledgement> VerifyAcknowledgement(
        string signedJson, ClassroomEventAccessGrant grant, string signedGrant,
        string? pinnedAgentPublicKeyPem, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedGrant);
        var verified = StudentAgentResponseCryptography.Verify<ClassroomEventGrantAcknowledgement>(
            signedJson, pinnedAgentPublicKeyPem);
        ValidateAcknowledgement(verified.Payload, grant, signedGrant, nowUtc);
        return verified;
    }

    public static async Task<IReadOnlyList<ClassroomEventGrantDeliveryResult>> PushAsync(
        IEnumerable<ClassroomEventGrantRequest> grantRequests, string campusId, Guid sessionId,
        string teacherPublicKeyPem, StudentAgentIdentityTrustStore? trustStore = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grantRequests);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂事件授权 session 无效。");
        var requests = grantRequests.Select(request =>
        {
            var target = ClassroomEventCryptography.NormalizeSingleTarget(request.Target);
            var grant = ClassroomEventCryptography.VerifyGrant(request.SignedGrant, campusId,
                teacherPublicKeyPem, sessionId, target, DateTimeOffset.UtcNow);
            return (Target: target, Request: request, Grant: grant);
        }).ToArray();
        if (requests.Length > ClassroomSession.MaximumTargets ||
            requests.Select(item => item.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requests.Length)
            throw new InvalidDataException("课堂事件授权目标重复或超过本机课堂容量。");
        trustStore ??= new StudentAgentIdentityTrustStore();

        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        using var limit = new SemaphoreSlim(16, 16);
        var tasks = requests.Select(async item =>
        {
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var uri = new UriBuilder(Uri.UriSchemeHttp, item.Target, WebsitePolicyAgent.Port,
                    WebsitePolicyAgent.ClassroomEventGrantPath).Uri;
                using var content = new StringContent(item.Request.SignedGrant, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
                var body = Encoding.UTF8.GetString(await ReadBoundedAsync(response.Content,
                    MaximumAcknowledgementBytes, cancellationToken).ConfigureAwait(false));
                if (!response.IsSuccessStatusCode)
                    return new ClassroomEventGrantDeliveryResult(item.Target, false,
                        (int)response.StatusCode is >= 400 and < 500,
                        $"HTTP {(int)response.StatusCode}：{body}");

                var pinnedKey = trustStore.FindTrustedPublicKey(campusId, item.Target);
                if (pinnedKey is null)
                    return new ClassroomEventGrantDeliveryResult(item.Target, false, true,
                        "学生 Agent 身份尚未固定；拒绝接受课堂事件授权。");
                var verified = VerifyAcknowledgement(body, item.Grant, item.Request.SignedGrant, pinnedKey,
                    DateTimeOffset.UtcNow);
                if (!verified.MatchesPinnedKey)
                    return new ClassroomEventGrantDeliveryResult(item.Target, false, true,
                        "Agent 回执身份与教师已固定身份不匹配。", verified.Payload, verified.Fingerprint);
                return new ClassroomEventGrantDeliveryResult(item.Target, true, false,
                    "课堂事件授权已由已固定身份的 Student Agent 确认。", verified.Payload,
                    verified.Fingerprint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              UriFormatException or InvalidDataException or JsonException or
                                              CryptographicException or IOException)
            {
                var requiresReview = exception is InvalidDataException or JsonException or CryptographicException;
                return new ClassroomEventGrantDeliveryResult(item.Target, false, requiresReview,
                    exception is TaskCanceledException ? "课堂事件授权推送超时。" :
                        "课堂事件授权推送失败：" + exception.Message);
            }
            finally { limit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static void ValidateAcknowledgement(ClassroomEventGrantAcknowledgement acknowledgement,
        ClassroomEventAccessGrant grant, string signedGrant, DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedGrant)));
        if (acknowledgement.SchemaVersion != 1 || acknowledgement.Purpose != AcknowledgementPurpose ||
            acknowledgement.CampusId != grant.CampusId || acknowledgement.MessageId != grant.MessageId ||
            acknowledgement.SessionId != grant.SessionId || acknowledgement.Target != grant.Target ||
            acknowledgement.GrantSha256 != expectedHash || acknowledgement.ReceivedUtc.Offset != TimeSpan.Zero ||
            acknowledgement.ReceivedUtc > now.Add(ClassroomEventCryptography.MaximumFutureSkew) ||
            acknowledgement.ReceivedUtc < now.Subtract(ClassroomEventCryptography.MaximumGrantLifetime))
            throw new InvalidDataException("课堂事件授权回执与签名授权、设备或有效期不匹配。");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("课堂事件授权回执超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("课堂事件授权回执超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
