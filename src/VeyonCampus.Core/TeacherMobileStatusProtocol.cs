using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public sealed record WebsitePolicyStatusRequest(int SchemaVersion, string Purpose, string CampusId,
    Guid Nonce, DateTimeOffset IssuedUtc);
public sealed record WebsitePolicyReportedState(long Revision, WebsitePolicyMode Mode,
    DateTimeOffset? ExpiresUtc, DateTimeOffset? ExpiredUtc);
public sealed record ApplicationPolicyReportedState(bool Supported, long? Revision,
    ApplicationPolicyMode? Mode, DateTimeOffset? ExpiresUtc);
public sealed record StudentSystemPolicyReportedState(bool Supported, long? Revision,
    StudentSystemPolicySettings? Settings, bool Pending);
public sealed record StudentAgentStatusResponse(int SchemaVersion, string Purpose, string CampusId,
    Guid Nonce, DateTimeOffset CollectedUtc, string AgentVersion, string ConfigFingerprint,
    WebsitePolicyReportedState Website, ApplicationPolicyReportedState? Application,
    StudentSystemPolicyReportedState? SystemPolicy = null);
public sealed record StudentAgentStatusDeliveryResult(string Target, bool Succeeded, string Detail,
    StudentAgentStatusResponse? Status = null, bool NeedsReview = false);

public static class WebsitePolicyStatusCryptography
{
    public const string RequestPurpose = "VeyonCampus.WebsitePolicyStatusRequest.v1";
    public const string ResponsePurpose = "VeyonCampus.StudentAgentStatusResponse.v1";
    public static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = ApplicationPolicyCompiler.JsonOptions;

    public static string SignRequest(WebsitePolicyStatusRequest request, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ValidateRequest(request, request.IssuedUtc);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        if (payload.Length > 4096) throw new InvalidDataException("网站状态读取请求超过大小限制。");
        var envelope = new SignedWebsitePolicy(Convert.ToBase64String(payload),
            Convert.ToBase64String(privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static WebsitePolicyStatusRequest VerifyRequest(string signedJson, string publicKeyPem,
        string campusId, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(signedJson) || Encoding.UTF8.GetByteCount(signedJson) > 8192)
            throw new InvalidDataException("网站状态读取请求大小无效。");
        try
        {
            PolicyJson.RejectDuplicateFields(Encoding.UTF8.GetBytes(signedJson));
            var envelope = JsonSerializer.Deserialize<SignedWebsitePolicy>(signedJson, JsonOptions)
                ?? throw new InvalidDataException("网站状态读取请求信封为空。");
            var payload = Convert.FromBase64String(envelope.Payload);
            var signature = Convert.FromBase64String(envelope.Signature);
            if (payload.Length is 0 or > 4096 || signature.Length is < 256 or > 512)
                throw new InvalidDataException("网站状态读取请求字段大小无效。");
            PolicyJson.RejectDuplicateFields(payload);
            using var key = RSA.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize is < 2048 or > 4096 ||
                !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("网站状态读取请求签名无效。");
            var request = JsonSerializer.Deserialize<WebsitePolicyStatusRequest>(payload, JsonOptions)
                ?? throw new InvalidDataException("网站状态读取请求正文为空。");
            ValidateRequest(request, nowUtc);
            if (!string.Equals(request.CampusId, campusId, StringComparison.Ordinal))
                throw new InvalidDataException("网站状态读取请求属于其他校区。");
            return request;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("网站状态读取请求编码无效。", exception);
        }
    }

    public static void ValidateResponse(StudentAgentStatusResponse response,
        WebsitePolicyStatusRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        var now = nowUtc.ToUniversalTime();
        if (response.SchemaVersion != 1 || response.Purpose != ResponsePurpose || response.CampusId != request.CampusId ||
            response.Nonce != request.Nonce || response.CollectedUtc.Offset != TimeSpan.Zero ||
            response.CollectedUtc > now.AddMinutes(2) || response.CollectedUtc < now.AddMinutes(-10) ||
            string.IsNullOrWhiteSpace(response.AgentVersion) || response.AgentVersion.Length > 64 ||
            string.IsNullOrWhiteSpace(response.ConfigFingerprint) || response.ConfigFingerprint.Length != 64 ||
            !response.ConfigFingerprint.All(Uri.IsHexDigit) ||
            response.Website is null || response.Website.Revision < 0 || !Enum.IsDefined(response.Website.Mode) ||
            (response.Website.ExpiresUtc is { Offset: var websiteOffset } && websiteOffset != TimeSpan.Zero) ||
            (response.Website.ExpiredUtc is { Offset: var expiredOffset } && expiredOffset != TimeSpan.Zero) ||
            (response.Application is { } app &&
             (app.Revision < 0 || (app.Revision is null && app.Mode is not null) ||
              (app.Mode is { } mode && !Enum.IsDefined(mode)) ||
              (app.ExpiresUtc is { Offset: var appOffset } && appOffset != TimeSpan.Zero))) ||
            (response.SystemPolicy is { } system && (system.Revision < 0 ||
                (!system.Supported && (system.Revision != 0 || system.Settings is not null || system.Pending)))) )
            throw new InvalidDataException("学生 Agent 状态响应字段无效。");
    }

    private static void ValidateRequest(WebsitePolicyStatusRequest request, DateTimeOffset nowUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(request.CampusId);
        var now = nowUtc.ToUniversalTime();
        if (request.SchemaVersion != 1 || request.Purpose != RequestPurpose || request.Nonce == Guid.Empty ||
            request.IssuedUtc.Offset != TimeSpan.Zero || request.IssuedUtc > now.AddMinutes(1) ||
            request.IssuedUtc < now.Subtract(MaximumRequestAge))
            throw new InvalidDataException("网站状态读取请求版本、随机数或签发期限无效。");
    }
}

public static class WebsitePolicyStatusTransport
{
    public static async Task<IReadOnlyList<StudentAgentStatusDeliveryResult>> ReadAsync(
        IEnumerable<string> targets, string campusId, RSA privateKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var validated = WebsitePolicyTransport.NormalizeTargets(targets);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var outgoing = validated.Select(target =>
        {
            var request = new WebsitePolicyStatusRequest(1, WebsitePolicyStatusCryptography.RequestPurpose,
                campusId, Guid.NewGuid(), DateTimeOffset.UtcNow);
            return (Target: target, SignedRequest: WebsitePolicyStatusCryptography.SignRequest(request, privateKey),
                Request: request);
        }).ToArray();
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        using var limit = new SemaphoreSlim(16, 16);
        var tasks = outgoing.Select(async outgoingRequest =>
        {
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var uri = new UriBuilder(Uri.UriSchemeHttp, outgoingRequest.Target, WebsitePolicyAgent.Port,
                    WebsitePolicyAgent.StatusPath).Uri;
                using var content = new StringContent(outgoingRequest.SignedRequest, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
                var bytes = await ReadBoundedAsync(response.Content, 32 * 1024, cancellationToken).ConfigureAwait(false);
                var body = Encoding.UTF8.GetString(bytes);
                if (!response.IsSuccessStatusCode)
                    return new StudentAgentStatusDeliveryResult(outgoingRequest.Target, false,
                        $"HTTP {(int)response.StatusCode}：{body}", NeedsReview: (int)response.StatusCode >= 500);
                PolicyJson.RejectDuplicateFields(bytes);
                var status = JsonSerializer.Deserialize<StudentAgentStatusResponse>(body, JsonOptions)
                    ?? throw new InvalidDataException("学生 Agent 没有返回状态。");
                WebsitePolicyStatusCryptography.ValidateResponse(status, outgoingRequest.Request, DateTimeOffset.UtcNow);
                return new StudentAgentStatusDeliveryResult(outgoingRequest.Target, true,
                    "Agent 已报告状态；学生机回执尚未作数字签名验证。", status, NeedsReview: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or
                                              InvalidDataException or JsonException or CryptographicException)
            {
                return new StudentAgentStatusDeliveryResult(outgoingRequest.Target, false,
                    exception is TaskCanceledException ? $"读取 {outgoingRequest.Target} 状态超时。" : "读取状态失败：" + exception.Message,
                    NeedsReview: true);
            }
            finally { limit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 32 * 1024)
            throw new InvalidDataException("学生 Agent 状态响应超过大小限制。");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("学生 Agent 状态响应超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
}
