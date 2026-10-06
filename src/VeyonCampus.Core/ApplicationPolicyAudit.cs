using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record ApplicationPolicyAuditRequest(int SchemaVersion, string Purpose, string CampusId,
    Guid Nonce, DateTimeOffset IssuedUtc, int LookbackHours);
public sealed record ApplicationPolicyAuditEvent(DateTimeOffset TimeUtc, int EventId, string UserSid, Guid RuleId);
public sealed record ApplicationPolicyAuditSummary(Guid RuleId, string DisplayName, string StudentSid,
    int WouldBlockCount, int BlockedCount);
public sealed record ApplicationPolicyAuditResponse(int SchemaVersion, string Purpose, string CampusId,
    Guid Nonce, long? PolicyRevision, ApplicationPolicyMode? Mode, DateTimeOffset FromUtc,
    DateTimeOffset CollectedUtc, IReadOnlyList<ApplicationPolicyAuditSummary> Results);

public interface IApplicationPolicyAuditSource
{
    IReadOnlyCollection<ApplicationPolicyAuditEvent> ReadEvents(DateTimeOffset sinceUtc);
}

public static class ApplicationPolicyAuditCryptography
{
    public const string Purpose = "VeyonCampus.ApplicationPolicyAuditRequest.v1";
    public static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(5);

    public static string SignRequest(ApplicationPolicyAuditRequest request, RSA privateKey)
    {
        ValidateRequest(request, request.IssuedUtc);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, ApplicationPolicyCompiler.JsonOptions);
        if (payload.Length > 4096) throw new InvalidDataException("应用策略审计请求超过大小限制。");
        return JsonSerializer.Serialize(new SignedWebsitePolicy(Convert.ToBase64String(payload),
            Convert.ToBase64String(privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
    }

    public static ApplicationPolicyAuditRequest VerifyRequest(string envelopeJson, string publicKeyPem,
        string campusId, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson) || Encoding.UTF8.GetByteCount(envelopeJson) > 8192)
            throw new InvalidDataException("应用策略审计请求大小无效。");
        try
        {
            PolicyJson.RejectDuplicateFields(Encoding.UTF8.GetBytes(envelopeJson));
            var envelope = JsonSerializer.Deserialize<SignedWebsitePolicy>(envelopeJson,
                new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidDataException("应用策略审计请求信封为空。");
            var payload = Convert.FromBase64String(envelope.Payload);
            var signature = Convert.FromBase64String(envelope.Signature);
            if (payload.Length is 0 or > 4096 || signature.Length is < 256 or > 512)
                throw new InvalidDataException("应用策略审计请求字段大小无效。");
            PolicyJson.RejectDuplicateFields(payload);
            using var key = RSA.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize is < 2048 or > 4096 ||
                !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("应用策略审计请求签名无效。");
            var request = JsonSerializer.Deserialize<ApplicationPolicyAuditRequest>(payload,
                ApplicationPolicyCompiler.JsonOptions) ?? throw new InvalidDataException("应用策略审计请求正文为空。");
            ValidateRequest(request, nowUtc);
            if (!string.Equals(request.CampusId, campusId, StringComparison.Ordinal))
                throw new InvalidDataException("应用策略审计请求属于其他校区。");
            return request;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        { throw new InvalidDataException("应用策略审计请求编码无效。", ex); }
    }

    private static void ValidateRequest(ApplicationPolicyAuditRequest request, DateTimeOffset nowUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(request.CampusId);
        var now = nowUtc.ToUniversalTime();
        if (request.SchemaVersion != 1 || request.Purpose != Purpose || request.Nonce == Guid.Empty ||
            request.IssuedUtc.Offset != TimeSpan.Zero || request.LookbackHours is < 1 or > 24 ||
            request.IssuedUtc > now.AddMinutes(1) || request.IssuedUtc < now - MaximumRequestAge)
            throw new InvalidDataException("应用策略审计请求版本、时间窗或签发期限无效。");
    }
}

public static class ApplicationPolicyAuditReader
{
    public const int MaximumEvents = 10000;

    public static ApplicationPolicyAuditResponse Read(ApplicationPolicyRuntimeState? state,
        IReadOnlyCollection<ApplicationPolicyAuditEvent> events, ApplicationPolicyAuditRequest request,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(request);
        var now = nowUtc.ToUniversalTime();
        var from = now.AddHours(-request.LookbackHours);
        var results = new List<ApplicationPolicyAuditSummary>();
        if (events.Count > MaximumEvents) throw new InvalidDataException("AppLocker 审计事件过多；请缩短查询时段。");
        if (state is not null && state.Policy.Mode != ApplicationPolicyMode.Disabled)
        {
            var knownRules = new Dictionary<Guid, (string Sid, ApplicationDenyRule Rule)>();
            foreach (var sid in state.Policy.StudentSids)
            foreach (var rule in state.Policy.Rules)
                knownRules.Add(ApplicationPolicyCompiler.CompiledRuleId(state.CampusId, sid, rule.Id), (sid, rule));
            var counts = new Dictionary<(Guid RuleId, string Sid), (int Audited, int Blocked)>();
            foreach (var item in events)
            {
                if (item.TimeUtc < from || item.TimeUtc > now.AddMinutes(2) || item.EventId is not (8003 or 8004) ||
                    !knownRules.TryGetValue(item.RuleId, out var rule) || !string.Equals(item.UserSid, rule.Sid, StringComparison.Ordinal))
                    continue;
                var key = (item.RuleId, rule.Sid);
                var prior = counts.GetValueOrDefault(key);
                counts[key] = item.EventId == 8003 ? (prior.Audited + 1, prior.Blocked) : (prior.Audited, prior.Blocked + 1);
            }
            foreach (var (key, count) in counts.OrderBy(item => item.Key.Sid, StringComparer.Ordinal)
                         .ThenBy(item => item.Key.RuleId))
            {
                var rule = knownRules[key.RuleId].Rule;
                results.Add(new ApplicationPolicyAuditSummary(key.RuleId, rule.DisplayName, key.Sid,
                    count.Audited, count.Blocked));
            }
        }
        return new ApplicationPolicyAuditResponse(1, "VeyonCampus.ApplicationPolicyAuditResponse.v1",
            request.CampusId, request.Nonce, state?.Revision, state?.Policy.Mode, from, now,
            Array.AsReadOnly(results.ToArray()));
    }
}
