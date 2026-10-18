using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public sealed record WebsitePolicyStatusRequest(int SchemaVersion, string Purpose, string CampusId,
    Guid Nonce, DateTimeOffset IssuedUtc);
public sealed record WebsitePolicyReportedState(long Revision, WebsitePolicyMode Mode,
    DateTimeOffset? ExpiresUtc, DateTimeOffset? ExpiredUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? BlockedDomains = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AllowedDomains = null);
public sealed record ApplicationPolicyReportedState(bool Supported, long? Revision,
    ApplicationPolicyMode? Mode, DateTimeOffset? ExpiresUtc);
public sealed record StudentSystemPolicyReportedState(bool Supported, long? Revision,
    StudentSystemPolicySettings? Settings, bool Pending);
public sealed record StudentAgentStatusResponse(int SchemaVersion, string Purpose, string CampusId,
    Guid Nonce, DateTimeOffset CollectedUtc, string AgentVersion, string ConfigFingerprint,
    WebsitePolicyReportedState Website, ApplicationPolicyReportedState? Application,
    StudentSystemPolicyReportedState? SystemPolicy = null,
    EdgeWebsitePolicyReportedState? EdgeWebsite = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StudentSetupVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    EdgeBrowserDiagnosticsSnapshot? EdgeBrowserDiagnostics = null);
public sealed record StudentAgentStatusDeliveryResult(string Target, bool Succeeded, string Detail,
    StudentAgentStatusResponse? Status = null, bool NeedsReview = false,
    StudentAgentIdentityTrustCandidate? IdentityCandidate = null);
public sealed record StudentAgentIdentityDiscoveryResult(string Target,
    StudentAgentIdentityTrustCandidate? Candidate, bool MatchesPinnedKey, string Detail);

public static class WebsitePolicyStatusCryptography
{
    public const string RequestPurpose = "VeyonCampus.WebsitePolicyStatusRequest.v1";
    public const string RequestPurposeV2 = "VeyonCampus.WebsitePolicyStatusRequest.v2";
    public const string RequestPurposeV3 = "VeyonCampus.WebsitePolicyStatusRequest.v3";
    public const string ResponsePurpose = "VeyonCampus.StudentAgentStatusResponse.v1";
    public const string ResponsePurposeV2 = "VeyonCampus.StudentAgentStatusResponse.v2";
    public const string ResponsePurposeV3 = "VeyonCampus.StudentAgentStatusResponse.v3";
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
        var expectedPurpose = request.SchemaVersion switch
        {
            1 => ResponsePurpose,
            2 => ResponsePurposeV2,
            3 => ResponsePurposeV3,
            _ => ""
        };
        if (response.StudentSetupVersion is { } setupVersion)
        {
            if (string.IsNullOrWhiteSpace(setupVersion) || setupVersion.Length > 64)
                throw new InvalidDataException("学生部署工具版本字段无效。");
            _ = ApplicationReleaseClient.CompareVersions(setupVersion, setupVersion);
        }
        if (response.SchemaVersion != request.SchemaVersion || response.Purpose != expectedPurpose || response.CampusId != request.CampusId ||
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
                (!system.Supported && (system.Revision != 0 || system.Settings is not null || system.Pending)))) ||
            (response.EdgeWebsite is { } edge && (edge.Revision < 0 || !Enum.IsDefined(edge.ExecutionMode) ||
                edge.PolicyHash is not { Length: 64 } || !edge.PolicyHash.All(Uri.IsHexDigit) ||
                edge.ExecutionMode == EdgeWebsitePolicyExecutionMode.ExtensionRealtime &&
                    (edge.ExtensionId is null || edge.ExtensionId.Length != 32 ||
                     edge.ExtensionId.Any(character => character is < 'a' or > 'p')) ||
                edge.ExecutionMode == EdgeWebsitePolicyExecutionMode.LegacyRegistry && edge.ExtensionId is not null ||
                edge.Profiles is null || edge.Profiles.Count > 128 ||
                edge.Profiles.Any(profile => profile is null ||
                    !Guid.TryParseExact(profile.ProfileId, "D", out var profileId) || profileId == Guid.Empty ||
                    profile.Revision != edge.Revision || profile.PolicyHash != edge.PolicyHash ||
                    profile.RulesHash is not { Length: 64 } || !profile.RulesHash.All(Uri.IsHexDigit) ||
                    profile.Status is not ("applied" or "degraded") || profile.RuleCount is < 0 or > 2002 ||
                    profile.AppliedUtc.Offset != TimeSpan.Zero ||
                    profile.Status == "applied" && profile.ErrorCode is not null ||
                    profile.Status == "degraded" && string.IsNullOrWhiteSpace(profile.ErrorCode)) ||
                edge.Profiles.Select(profile => profile.ProfileId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != edge.Profiles.Count)))
            throw new InvalidDataException("学生 Agent 状态响应字段无效。");
        if ((request.SchemaVersion == 3) != (response.EdgeBrowserDiagnostics is not null))
            throw new InvalidDataException("Edge 浏览器诊断与状态协议版本不匹配。");
        if (response.EdgeBrowserDiagnostics is { } edgeBrowserDiagnostics)
            EdgeBrowserDiagnosticsValidator.Validate(edgeBrowserDiagnostics, nowUtc);
        _ = ApplicationReleaseClient.CompareVersions(response.AgentVersion, response.AgentVersion);
    }

    private static void ValidateRequest(WebsitePolicyStatusRequest request, DateTimeOffset nowUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(request.CampusId);
        var now = nowUtc.ToUniversalTime();
        var purposeIsValid = request.SchemaVersion switch
        {
            1 => request.Purpose == RequestPurpose,
            2 => request.Purpose == RequestPurposeV2,
            3 => request.Purpose == RequestPurposeV3,
            _ => false
        };
        if (!purposeIsValid || request.Nonce == Guid.Empty ||
            request.IssuedUtc.Offset != TimeSpan.Zero || request.IssuedUtc > now.AddMinutes(1) ||
            request.IssuedUtc < now.Subtract(MaximumRequestAge))
            throw new InvalidDataException("网站状态读取请求版本、随机数或签发期限无效。");
    }
}

public static class WebsitePolicyStatusTransport
{
    public static async Task<IReadOnlyList<StudentAgentIdentityDiscoveryResult>> DiscoverIdentitiesAsync(
        IEnumerable<string> targets, string campusId, RSA privateKey,
        CancellationToken cancellationToken = default)
    {
        var results = await ReadAsync(targets, campusId, privateKey, cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(results.Select(result => new StudentAgentIdentityDiscoveryResult(result.Target,
            result.IdentityCandidate, result.Succeeded, result.Detail)).ToArray());
    }

    public static async Task<IReadOnlyList<StudentAgentStatusDeliveryResult>> ReadAsync(
        IEnumerable<string> targets, string campusId, RSA privateKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var validated = WebsitePolicyTransport.NormalizeTargets(targets);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var outgoing = validated.Select(target =>
        {
            var request = new WebsitePolicyStatusRequest(3, WebsitePolicyStatusCryptography.RequestPurposeV3,
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
                var request = outgoingRequest.Request;
                var uri = new UriBuilder(Uri.UriSchemeHttp, outgoingRequest.Target, WebsitePolicyAgent.Port,
                    WebsitePolicyAgent.StatusPathV3).Uri;
                var reply = await SendStatusRequestAsync(client, uri, outgoingRequest.SignedRequest,
                    cancellationToken).ConfigureAwait(false);
                var body = Encoding.UTF8.GetString(reply.Body);
                if (DoesNotSupportRequestedStatusVersion(reply.StatusCode, body))
                {
                    request = new WebsitePolicyStatusRequest(2, WebsitePolicyStatusCryptography.RequestPurposeV2,
                        campusId, Guid.NewGuid(), DateTimeOffset.UtcNow);
                    var signedFallback = WebsitePolicyStatusCryptography.SignRequest(request, privateKey);
                    var fallbackUri = new UriBuilder(Uri.UriSchemeHttp, outgoingRequest.Target,
                        WebsitePolicyAgent.Port, WebsitePolicyAgent.StatusPathV2).Uri;
                    reply = await SendStatusRequestAsync(client, fallbackUri, signedFallback,
                        cancellationToken).ConfigureAwait(false);
                    body = Encoding.UTF8.GetString(reply.Body);
                    if (DoesNotSupportRequestedStatusVersion(reply.StatusCode, body))
                    {
                        request = new WebsitePolicyStatusRequest(1, WebsitePolicyStatusCryptography.RequestPurpose,
                            campusId, Guid.NewGuid(), DateTimeOffset.UtcNow);
                        var legacyFallback = WebsitePolicyStatusCryptography.SignRequest(request, privateKey);
                        var legacyUri = new UriBuilder(Uri.UriSchemeHttp, outgoingRequest.Target,
                            WebsitePolicyAgent.Port, WebsitePolicyAgent.StatusPath).Uri;
                        reply = await SendStatusRequestAsync(client, legacyUri, legacyFallback,
                            cancellationToken).ConfigureAwait(false);
                        body = Encoding.UTF8.GetString(reply.Body);
                    }
                }
                if ((int)reply.StatusCode is < 200 or >= 300)
                    return new StudentAgentStatusDeliveryResult(outgoingRequest.Target, false,
                        $"HTTP {(int)reply.StatusCode}：{body}", NeedsReview: (int)reply.StatusCode >= 500);
                PolicyJson.RejectDuplicateFields(reply.Body);
                var pinnedKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(campusId,
                    outgoingRequest.Target);
                var verified = StudentAgentResponseCryptography.Verify<StudentAgentStatusResponse>(body, pinnedKey);
                var status = verified.Payload;
                WebsitePolicyStatusCryptography.ValidateResponse(status, request, DateTimeOffset.UtcNow);
                var candidate = new StudentAgentIdentityTrustCandidate(outgoingRequest.Target, campusId,
                    verified.PublicKeyPem, verified.Fingerprint, pinnedKey is null ? null :
                    StudentAgentResponseCryptography.GetFingerprint(pinnedKey));
                if (!verified.MatchesPinnedKey)
                    return new StudentAgentStatusDeliveryResult(outgoingRequest.Target, false,
                        pinnedKey is null
                            ? $"Agent 回执签名有效，但身份尚未由教师核对并固定。指纹：{verified.Fingerprint}"
                            : $"Agent 身份指纹已变化，拒绝信任。原指纹：{candidate.PreviouslyPinnedFingerprint}；新指纹：{verified.Fingerprint}",
                        NeedsReview: true, IdentityCandidate: candidate);
                return new StudentAgentStatusDeliveryResult(outgoingRequest.Target, true,
                    "学生 Agent 状态回执的签名、随机数和已固定设备身份均已验证。", status,
                    IdentityCandidate: candidate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or
                                              InvalidDataException or JsonException or CryptographicException)
            {
                var endpoint = $"{outgoingRequest.Target}:{WebsitePolicyAgent.Port}";
                var message = exception switch
                {
                    TaskCanceledException => $"连接学生 Agent 超时（{endpoint}）。请确认学生端 Agent 正在运行、电脑地址正确，且教师电脑可访问 TCP {WebsitePolicyAgent.Port}。学生端防火墙默认只允许同一子网；跨网段时需由管理员配置规则。此超时表示未收到 Agent 回应，暂时没有身份可供首次信任确认。Veyon Master 能看到电脑不代表策略通道已连通。",
                    HttpRequestException => $"无法连接学生 Agent（{endpoint}）。请确认学生端 Agent 正在运行、电脑地址正确，且网络或防火墙允许 TCP {WebsitePolicyAgent.Port}；默认规则只允许同一子网。",
                    _ => "读取状态失败：" + exception.Message
                };
                return new StudentAgentStatusDeliveryResult(outgoingRequest.Target, false,
                    message, NeedsReview: true);
            }
            finally { limit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
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

    private static async Task<(HttpStatusCode StatusCode, byte[] Body)> SendStatusRequestAsync(
        HttpClient client, Uri uri, string signedRequest, CancellationToken cancellationToken)
    {
        using var content = new StringContent(signedRequest, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
        var body = await ReadBoundedAsync(response.Content,
            StudentAgentResponseCryptography.MaximumEnvelopeBytes, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    private static bool DoesNotSupportRequestedStatusVersion(HttpStatusCode statusCode, string body) =>
        statusCode == HttpStatusCode.NotFound ||
        statusCode == HttpStatusCode.Conflict && body.Contains("请求版本", StringComparison.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
}
