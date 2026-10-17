using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

public enum EdgeWebsitePolicyExecutionMode
{
    LegacyRegistry,
    ExtensionRealtime
}

public sealed record EdgeWebsitePolicySnapshot(
    int SchemaVersion,
    string MessageType,
    string CampusId,
    long Revision,
    string PolicyHash,
    EdgeWebsitePolicyExecutionMode ExecutionMode,
    WebsitePolicyMode Mode,
    IReadOnlyList<string> BlockedDomains,
    IReadOnlyList<string> AllowedDomains,
    string? ExpiresUtc,
    bool Expired,
    string? ExtensionId = null);

public sealed record EdgeWebsitePolicyApplyResult(
    int SchemaVersion,
    string MessageType,
    string ProfileId,
    long Revision,
    string PolicyHash,
    string RulesHash,
    string Status,
    int RuleCount,
    DateTimeOffset AppliedUtc,
    string? ErrorCode = null);

public sealed record EdgeWebsitePolicyModeCommand(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    EdgeWebsitePolicyExecutionMode DesiredMode,
    long ExpectedRevision,
    string ExtensionId,
    IReadOnlyList<string> ProfileIds,
    DateTimeOffset IssuedUtc,
    Guid Nonce);

public sealed record EdgeWebsitePolicyReportedState(
    EdgeWebsitePolicyExecutionMode ExecutionMode,
    long Revision,
    string PolicyHash,
    IReadOnlyList<EdgeWebsitePolicyApplyResult> Profiles,
    string? ExtensionId = null);

/// <summary>Validates snapshots shared with the Edge extension and hashes their exact policy semantics.</summary>
public static class EdgeRealtimeWebsitePolicy
{
    public const int MaximumSnapshotBytes = 128 * 1024;
    public const int MaximumSnapshotEnvelopeBytes = 192 * 1024;
    public const string SnapshotMessageType = "websitePolicy.snapshot";
    public const string ApplyResultMessageType = "websitePolicy.applyResult";

    public static EdgeWebsitePolicySnapshot CreateSnapshot(WebsitePolicyDocument? acceptedPolicy,
        string campusId, DateTimeOffset nowUtc,
        EdgeWebsitePolicyExecutionMode executionMode = EdgeWebsitePolicyExecutionMode.LegacyRegistry,
        string? extensionId = null)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var now = nowUtc.ToUniversalTime();
        var policy = acceptedPolicy;
        if (policy is not null && !string.Equals(policy.CampusId, campusId, StringComparison.Ordinal))
            throw new InvalidDataException("Edge 实时网站策略与当前校区不匹配。");

        var expired = policy?.ExpiresUtc is { } expiry && expiry <= now;
        var mode = policy is null || expired ? WebsitePolicyMode.Disabled : policy.Mode;
        var blocked = mode is WebsitePolicyMode.Blocklist or WebsitePolicyMode.Combined
            ? policy!.Domains.ToArray()
            : Array.Empty<string>();
        var allowed = mode is WebsitePolicyMode.Allowlist or WebsitePolicyMode.Combined
            ? (policy!.AllowedDomains ?? (mode == WebsitePolicyMode.Allowlist
                ? policy.Domains
                : Array.Empty<string>())).ToArray()
            : Array.Empty<string>();
        var revision = policy?.Revision ?? 0;
        var expiresText = policy?.ExpiresUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var hash = ComputePolicyHash(campusId, revision, mode, blocked, allowed, expiresText, expired);
        var snapshot = new EdgeWebsitePolicySnapshot(1, SnapshotMessageType, campusId, revision, hash,
            executionMode, mode, blocked, allowed, expiresText, expired, extensionId);
        ValidateSnapshot(snapshot, campusId);
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, ApplicationPolicyCompiler.JsonOptions).Length > MaximumSnapshotBytes)
            throw new InvalidDataException("Edge 实时网站策略快照超过大小限制。");
        return snapshot;
    }

    public static void ValidateSnapshot(EdgeWebsitePolicySnapshot snapshot, string expectedCampusId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        if (snapshot.SchemaVersion != 1 || snapshot.MessageType != SnapshotMessageType ||
            snapshot.CampusId != expectedCampusId || snapshot.Revision < 0 ||
            !Enum.IsDefined(snapshot.ExecutionMode) || !Enum.IsDefined(snapshot.Mode) ||
            snapshot.BlockedDomains is null || snapshot.AllowedDomains is null)
            throw new InvalidDataException("Edge 实时网站策略快照字段无效。");
        if (snapshot.ExecutionMode == EdgeWebsitePolicyExecutionMode.ExtensionRealtime)
            ValidateExtensionId(snapshot.ExtensionId);
        else if (snapshot.ExtensionId is not null)
            throw new InvalidDataException("Edge 旧模式快照不得包含实时扩展 ID。");

        DateTimeOffset? expires = null;
        if (snapshot.ExpiresUtc is not null)
        {
            if (!DateTimeOffset.TryParseExact(snapshot.ExpiresUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed) || parsed.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Edge 实时网站策略到期时间无效。");
            expires = parsed;
        }
        if (snapshot.Expired && (expires is null || snapshot.Mode != WebsitePolicyMode.Disabled ||
                                 snapshot.BlockedDomains.Count != 0 || snapshot.AllowedDomains.Count != 0) ||
            snapshot.Mode == WebsitePolicyMode.Disabled &&
            (snapshot.BlockedDomains.Count != 0 || snapshot.AllowedDomains.Count != 0))
            throw new InvalidDataException("Edge 实时网站策略到期状态与名单不匹配。");

        var blocked = WebsitePolicyCompiler.NormalizeDomains(snapshot.BlockedDomains);
        var allowed = WebsitePolicyCompiler.NormalizeDomains(snapshot.AllowedDomains);
        if (!blocked.SequenceEqual(snapshot.BlockedDomains, StringComparer.Ordinal) ||
            !allowed.SequenceEqual(snapshot.AllowedDomains, StringComparer.Ordinal))
            throw new InvalidDataException("Edge 实时网站策略快照名单未规范化或未排序。");
        if (snapshot.Mode == WebsitePolicyMode.Blocklist && (blocked.Count == 0 || allowed.Count != 0) ||
            snapshot.Mode == WebsitePolicyMode.Allowlist && (allowed.Count == 0 || blocked.Count != 0) ||
            snapshot.Mode == WebsitePolicyMode.Combined && blocked.Count == 0 && allowed.Count == 0)
            throw new InvalidDataException("Edge 实时网站策略模式与名单不匹配。");

        var expectedHash = ComputePolicyHash(snapshot.CampusId, snapshot.Revision, snapshot.Mode,
            blocked, allowed, snapshot.ExpiresUtc, snapshot.Expired);
        if (!string.Equals(snapshot.PolicyHash, expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException("Edge 实时网站策略摘要不匹配。");
    }

    public static string ComputePolicyHash(string campusId, long revision, WebsitePolicyMode mode,
        IReadOnlyList<string> blockedDomains, IReadOnlyList<string> allowedDomains, string? expiresUtc,
        bool expired)
    {
        ArgumentNullException.ThrowIfNull(blockedDomains);
        ArgumentNullException.ThrowIfNull(allowedDomains);
        var canonical = string.Join("\n",
            "1",
            campusId,
            revision.ToString(CultureInfo.InvariantCulture),
            mode.ToString().ToLowerInvariant(),
            expiresUtc ?? "",
            expired ? "1" : "0",
            blockedDomains.Count.ToString(CultureInfo.InvariantCulture),
            string.Join('\n', blockedDomains),
            allowedDomains.Count.ToString(CultureInfo.InvariantCulture),
            string.Join('\n', allowedDomains));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool ValidateApplyResult(EdgeWebsitePolicyApplyResult result, EdgeWebsitePolicySnapshot current,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(current);
        if (result.SchemaVersion != 1 || result.MessageType != ApplyResultMessageType ||
            !Guid.TryParseExact(result.ProfileId, "D", out var profileId) || profileId == Guid.Empty ||
            result.Revision != current.Revision || result.PolicyHash != current.PolicyHash ||
            !IsSha256(result.RulesHash) || result.RuleCount is < 0 or > 2002 ||
            result.AppliedUtc.Offset != TimeSpan.Zero || result.AppliedUtc > nowUtc.ToUniversalTime().AddMinutes(1) ||
            result.Status is not ("applied" or "degraded") ||
            result.Status == "applied" && result.ErrorCode is not null ||
            result.Status == "degraded" && string.IsNullOrWhiteSpace(result.ErrorCode) ||
            result.ErrorCode is { Length: > 128 })
            return false;
        return true;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static void ValidateExtensionId(string? extensionId)
    {
        if (extensionId is null || extensionId.Length != 32 ||
            extensionId.Any(character => character is < 'a' or > 'p'))
            throw new InvalidDataException("Edge 扩展 ID 必须为 32 个 a-p 范围内的小写字符。");
    }
}

/// <summary>Teacher-signed, revision-bound command for the one-time legacy-to-extension migration.</summary>
public static class EdgeWebsitePolicyModeCommandCryptography
{
    public const string Purpose = "VeyonCampus.EdgeWebsitePolicyModeCommand.v1";
    public const int MaximumEnvelopeBytes = 16 * 1024;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);

    public static string Sign(EdgeWebsitePolicyModeCommand command, RSA teacherPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(teacherPrivateKey);
        ArgumentNullException.ThrowIfNull(command);
        Validate(command, command.CampusId, command.ExpectedRevision, DateTimeOffset.UtcNow);
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, ApplicationPolicyCompiler.JsonOptions);
        if (payload.Length > MaximumEnvelopeBytes / 2)
            throw new InvalidDataException("Edge 实时模式切换命令超过大小限制。");
        var signature = teacherPrivateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var envelope = new SignedWebsitePolicy(Convert.ToBase64String(payload), Convert.ToBase64String(signature));
        var json = JsonSerializer.Serialize(envelope, ApplicationPolicyCompiler.JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumEnvelopeBytes)
            throw new InvalidDataException("Edge 实时模式切换命令超过大小限制。");
        return json;
    }

    public static EdgeWebsitePolicyModeCommand Verify(string signedJson, string publicKeyPem,
        string expectedCampusId, long currentRevision, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedJson);
        if (Encoding.UTF8.GetByteCount(signedJson) > MaximumEnvelopeBytes)
            throw new InvalidDataException("Edge 实时模式切换命令超过大小限制。");
        try
        {
            var envelopeBytes = Encoding.UTF8.GetBytes(signedJson);
            PolicyJson.RejectDuplicateFields(envelopeBytes);
            var envelope = JsonSerializer.Deserialize<SignedWebsitePolicy>(envelopeBytes,
                               ApplicationPolicyCompiler.JsonOptions)
                           ?? throw new InvalidDataException("Edge 实时模式切换命令为空。");
            var payload = Convert.FromBase64String(envelope.Payload);
            var signature = Convert.FromBase64String(envelope.Signature);
            if (payload.Length is 0 or > MaximumEnvelopeBytes / 2 || signature.Length > 1024)
                throw new InvalidDataException("Edge 实时模式切换命令正文或签名大小无效。");
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(publicKeyPem);
            if (!publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("Edge 实时模式切换命令签名无效。");
            PolicyJson.RejectDuplicateFields(payload);
            var command = JsonSerializer.Deserialize<EdgeWebsitePolicyModeCommand>(payload,
                              ApplicationPolicyCompiler.JsonOptions)
                          ?? throw new InvalidDataException("Edge 实时模式切换命令正文为空。");
            Validate(command, expectedCampusId, currentRevision, nowUtc);
            return command;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("Edge 实时模式切换命令格式、编码或签名无效。", exception);
        }
    }

    private static void Validate(EdgeWebsitePolicyModeCommand command, string expectedCampusId,
        long currentRevision, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(command);
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        EdgeRealtimeWebsitePolicy.ValidateExtensionId(command.ExtensionId);
        var now = nowUtc.ToUniversalTime();
        if (command.SchemaVersion != 1 || command.Purpose != Purpose || command.CampusId != expectedCampusId ||
            !Enum.IsDefined(command.DesiredMode) ||
            command.ExpectedRevision != currentRevision || currentRevision < 0 || command.Nonce == Guid.Empty ||
            command.IssuedUtc.Offset != TimeSpan.Zero || command.IssuedUtc > now.AddMinutes(1) ||
            command.IssuedUtc < now.Subtract(MaximumAge) || command.ProfileIds is null ||
            command.ProfileIds.Count > 128 ||
            command.DesiredMode == EdgeWebsitePolicyExecutionMode.ExtensionRealtime && command.ProfileIds.Count == 0)
            throw new InvalidDataException("Edge 实时模式切换命令校区、版本、目标、随机数或期限无效。");
        var normalized = new List<string>(command.ProfileIds.Count);
        foreach (var profileId in command.ProfileIds)
        {
            if (!Guid.TryParseExact(profileId, "D", out var parsed) || parsed == Guid.Empty ||
                !string.Equals(profileId, parsed.ToString("D"), StringComparison.Ordinal))
                throw new InvalidDataException("Edge 实时模式切换命令包含无效 Profile ID。");
            normalized.Add(profileId);
        }
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Count ||
            !normalized.SequenceEqual(normalized.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Edge 实时模式切换命令 Profile ID 必须唯一并按序排列。");
    }
}

/// <summary>In-memory apply acknowledgements, kept separately from the signed policy acceptance record.</summary>
public sealed class EdgeWebsitePolicyApplyResultState
{
    private readonly ConcurrentDictionary<Guid, EdgeWebsitePolicyApplyResult> _byProfile = new();

    public bool Record(EdgeWebsitePolicyApplyResult result, EdgeWebsitePolicySnapshot current, DateTimeOffset nowUtc)
    {
        if (!EdgeRealtimeWebsitePolicy.ValidateApplyResult(result, current, nowUtc) ||
            !Guid.TryParseExact(result.ProfileId, "D", out var profileId))
            return false;
        _byProfile[profileId] = result;
        if (_byProfile.Count > 128)
        {
            var stale = _byProfile.OrderBy(pair => pair.Value.AppliedUtc).Take(_byProfile.Count - 128).ToArray();
            foreach (var pair in stale) _byProfile.TryRemove(pair.Key, out _);
        }
        return true;
    }

    public IReadOnlyList<EdgeWebsitePolicyApplyResult> Read() =>
        _byProfile.Values.OrderBy(result => result.ProfileId, StringComparer.Ordinal).ToArray();

    public EdgeWebsitePolicyReportedState ReadCurrent(EdgeWebsitePolicySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var profiles = _byProfile.Values
            .Where(result => result.Revision == current.Revision && result.PolicyHash == current.PolicyHash)
            .OrderBy(result => result.ProfileId, StringComparer.Ordinal)
            .ToArray();
        return new EdgeWebsitePolicyReportedState(current.ExecutionMode, current.Revision,
            current.PolicyHash, Array.AsReadOnly(profiles), current.ExtensionId);
    }

    public bool IsAppliedForProfiles(EdgeWebsitePolicySnapshot current, IEnumerable<string> profileIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(profileIds);
        var requested = profileIds.ToArray();
        if (requested.Length == 0 || requested.Length > 128 || requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length)
            return false;
        foreach (var profileIdText in requested)
        {
            if (!Guid.TryParseExact(profileIdText, "D", out var profileId) || profileId == Guid.Empty ||
                !_byProfile.TryGetValue(profileId, out var result) || result.Status != "applied" ||
                result.Revision != current.Revision || result.PolicyHash != current.PolicyHash)
                return false;
        }
        return true;
    }
}
