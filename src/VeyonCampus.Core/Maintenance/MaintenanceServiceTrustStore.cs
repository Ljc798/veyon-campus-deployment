using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeyonCampus.Core;

namespace VeyonCampus.Core.Maintenance;

public sealed record MaintenanceServiceIdentityTrustPin(
    string Target,
    string CampusId,
    string AgentIdentityFingerprint,
    string MaintenanceIdentityFingerprint,
    string MaintenancePublicKeyPem,
    DateTimeOffset PinnedUtc);

public sealed record PendingMaintenanceServiceIdentityRotation(
    string Target,
    string CampusId,
    string AgentIdentityFingerprint,
    string PreviousMaintenanceIdentityFingerprint,
    string CandidateMaintenanceIdentityFingerprint,
    string CandidateMaintenancePublicKeyPem,
    string SignedEnrollmentJson,
    DateTimeOffset RequestedUtc);

/// <summary>
/// Teacher-side pins for Maintenance identities. A new key is admitted only through an
/// enrollment signed by the already pinned Agent identity; changed keys require an explicit
/// rotation decision and the same enrollment proof.
/// </summary>
public sealed class MaintenanceServiceTrustStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private const int MaximumPins = 5000;
    private const int MaximumPendingRotations = 1000;
    private const int MaximumPendingEnrollmentBytes = 64 * 1024;
    private static readonly TimeSpan PendingRotationLifetime =
        TimeSpan.FromMinutes(MaintenanceServiceIdentityStore.MaximumEnrollmentAgeMinutes);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _path;
    private readonly string _pendingRotationPath;
    private readonly StudentAgentIdentityTrustStore _agentTrustStore;
    private readonly object _gate = new();

    public MaintenanceServiceTrustStore(StudentAgentIdentityTrustStore? agentTrustStore = null,
        string? path = null)
    {
        _agentTrustStore = agentTrustStore ?? new StudentAgentIdentityTrustStore();
        _path = path ?? DefaultPath;
        _pendingRotationPath = _path + ".pending-rotations";
    }

    public static string DefaultPath => Path.Combine(
        VeyonCampusPaths.GetLocalApplicationDataDirectory(),
        "VeyonCampus", "MaintenanceTrust", "services.json");

    public IReadOnlyList<MaintenanceServiceIdentityTrustPin> List()
    {
        lock (_gate) return Array.AsReadOnly(ReadPins().ToArray());
    }

    public IReadOnlyList<PendingMaintenanceServiceIdentityRotation> ListPendingRotations(string campusId,
        DateTimeOffset? nowUtc = null)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_gate)
        {
            var pins = ReadPins();
            var rotations = PruneExpiredPendingRotations(now);
            return Array.AsReadOnly(rotations.Where(item => item.CampusId == campusId &&
                pins.Any(pin => pin.CampusId == item.CampusId && pin.Target == item.Target &&
                    pin.AgentIdentityFingerprint == item.AgentIdentityFingerprint &&
                    pin.MaintenanceIdentityFingerprint == item.PreviousMaintenanceIdentityFingerprint))
                .ToArray());
        }
    }

    public MaintenanceServiceIdentityTrustPin? FindTrustedPin(string campusId, string target,
        string agentIdentityFingerprint)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var normalizedTarget = NormalizeTarget(target);
        var agentFingerprint = MaintenanceValidation.Fingerprint(agentIdentityFingerprint,
            nameof(agentIdentityFingerprint));
        lock (_gate)
            return ReadPins().SingleOrDefault(pin => pin.CampusId == campusId &&
                string.Equals(pin.Target, normalizedTarget, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pin.AgentIdentityFingerprint, agentFingerprint, StringComparison.Ordinal));
    }

    public string? FindTrustedPublicKey(string campusId, string target, string agentIdentityFingerprint)
    {
        return FindTrustedPin(campusId, target, agentIdentityFingerprint)?.MaintenancePublicKeyPem;
    }

    public PendingMaintenanceServiceIdentityRotation QueueKeyRotation(string target, string campusId,
        string agentIdentityFingerprint, string expectedPreviousMaintenanceIdentityFingerprint,
        string signedEnrollmentJson, DateTimeOffset? nowUtc = null)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var normalizedTarget = NormalizeTarget(target);
        var agentFingerprint = MaintenanceValidation.Fingerprint(agentIdentityFingerprint,
            nameof(agentIdentityFingerprint));
        var previousFingerprint = MaintenanceValidation.Fingerprint(
            expectedPreviousMaintenanceIdentityFingerprint, nameof(expectedPreviousMaintenanceIdentityFingerprint));
        ArgumentException.ThrowIfNullOrWhiteSpace(signedEnrollmentJson);
        if (Encoding.UTF8.GetByteCount(signedEnrollmentJson) is 0 or > MaximumPendingEnrollmentBytes)
            throw new InvalidDataException("待审批 Maintenance 身份登记超过大小限制。");

        var agentPublicKey = _agentTrustStore.FindTrustedPublicKey(campusId, normalizedTarget)
                             ?? throw new InvalidDataException("Maintenance 密钥轮换没有固定的 Agent 授权根。");
        if (!string.Equals(StudentAgentResponseCryptography.GetFingerprint(agentPublicKey), agentFingerprint,
                StringComparison.Ordinal))
            throw new InvalidDataException("Maintenance 密钥轮换的 Agent 身份不再匹配当前固定身份。");
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var verified = MaintenanceServiceIdentityStore.VerifyEnrollment(signedEnrollmentJson, agentPublicKey,
            campusId, agentFingerprint, now);
        if (verified.MaintenanceIdentityFingerprint == previousFingerprint)
            throw new InvalidDataException("候选 Maintenance 密钥与现有固定密钥相同；无需轮换。");

        lock (_gate)
        {
            var pin = ReadPins().SingleOrDefault(item => item.CampusId == campusId &&
                item.Target == normalizedTarget && item.AgentIdentityFingerprint == agentFingerprint)
                ?? throw new InvalidDataException("Maintenance 密钥轮换缺少现有固定服务密钥。");
            if (pin.MaintenanceIdentityFingerprint != previousFingerprint)
                throw new InvalidDataException("Maintenance 服务密钥在排队期间已变化；拒绝覆盖当前信任记录。");
            var rotations = PruneExpiredPendingRotations(now).ToList();
            var existingIndex = rotations.FindIndex(item => item.CampusId == campusId &&
                item.Target == normalizedTarget && item.AgentIdentityFingerprint == agentFingerprint);
            var candidate = new PendingMaintenanceServiceIdentityRotation(normalizedTarget, campusId,
                agentFingerprint, previousFingerprint, verified.MaintenanceIdentityFingerprint,
                verified.MaintenancePublicKeyPem, signedEnrollmentJson, now);
            if (existingIndex >= 0) rotations.RemoveAt(existingIndex);
            if (rotations.Count >= MaximumPendingRotations)
                throw new InvalidDataException("待审批 Maintenance 密钥轮换数量达到上限。");
            rotations.Add(candidate);
            WritePendingRotations(rotations);
            return candidate;
        }
    }

    public MaintenanceServiceIdentityTrustPin ApproveKeyRotation(
        PendingMaintenanceServiceIdentityRotation request, string expectedPreviousFingerprint,
        string expectedCandidateFingerprint, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var previousFingerprint = MaintenanceValidation.Fingerprint(expectedPreviousFingerprint,
            nameof(expectedPreviousFingerprint));
        var candidateFingerprint = MaintenanceValidation.Fingerprint(expectedCandidateFingerprint,
            nameof(expectedCandidateFingerprint));
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_gate)
        {
            var allRotations = ReadPendingRotations().ToList();
            var requested = allRotations.SingleOrDefault(item => item.CampusId == request.CampusId &&
                item.Target == request.Target && item.AgentIdentityFingerprint == request.AgentIdentityFingerprint &&
                item.PreviousMaintenanceIdentityFingerprint == previousFingerprint &&
                item.CandidateMaintenanceIdentityFingerprint == candidateFingerprint);
            if (requested is not null && !IsPendingRotationFresh(requested, now))
            {
                allRotations.Remove(requested);
                WritePendingRotations(allRotations);
                throw new InvalidDataException("Maintenance 密钥轮换证明已过期；请让设备重新连接以生成新的授权证明。");
            }
            var pending = PruneExpiredPendingRotations(now).SingleOrDefault(item =>
                item.CampusId == request.CampusId && item.Target == request.Target &&
                item.AgentIdentityFingerprint == request.AgentIdentityFingerprint &&
                item.PreviousMaintenanceIdentityFingerprint == previousFingerprint &&
                item.CandidateMaintenanceIdentityFingerprint == candidateFingerprint)
                ?? throw new InvalidDataException("待审批密钥轮换请求已不存在或与确认内容不匹配。");
            var pin = ReadPins().SingleOrDefault(item => item.CampusId == pending.CampusId &&
                item.Target == pending.Target && item.AgentIdentityFingerprint == pending.AgentIdentityFingerprint)
                ?? throw new InvalidDataException("待审批密钥轮换缺少原有固定密钥；拒绝首次信任新密钥。");
            if (pin.MaintenanceIdentityFingerprint != previousFingerprint)
                throw new InvalidDataException("现有固定密钥已变化；待审批轮换请求已过期。");
            var agentPublicKey = _agentTrustStore.FindTrustedPublicKey(pending.CampusId, pending.Target)
                                 ?? throw new InvalidDataException("待审批密钥轮换缺少固定 Agent 授权根。");
            if (!string.Equals(StudentAgentResponseCryptography.GetFingerprint(agentPublicKey),
                    pending.AgentIdentityFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("待审批密钥轮换的 Agent 身份发生变化；拒绝批准。");
            var verified = MaintenanceServiceIdentityStore.VerifyEnrollment(pending.SignedEnrollmentJson,
                agentPublicKey, pending.CampusId, pending.AgentIdentityFingerprint, now);
            if (verified.MaintenanceIdentityFingerprint != candidateFingerprint ||
                verified.MaintenancePublicKeyPem != pending.CandidateMaintenancePublicKeyPem)
                throw new InvalidDataException("新 Maintenance 公钥与 Agent 签名授权内容不匹配。");

            var rotated = PinEnrollmentCore(pending.Target, pending.CampusId, pending.AgentIdentityFingerprint,
                pending.SignedEnrollmentJson, allowKeyRotation: true, now);
            WritePendingRotations(ReadPendingRotations().Where(item => item.CampusId != pending.CampusId ||
                item.Target != pending.Target || item.AgentIdentityFingerprint != pending.AgentIdentityFingerprint)
                .ToArray());
            return rotated;
        }
    }

    public bool RejectKeyRotation(PendingMaintenanceServiceIdentityRotation request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var rotations = ReadPendingRotations().ToList();
            var count = rotations.RemoveAll(item => item.CampusId == request.CampusId &&
                item.Target == request.Target && item.AgentIdentityFingerprint == request.AgentIdentityFingerprint &&
                item.CandidateMaintenanceIdentityFingerprint == request.CandidateMaintenanceIdentityFingerprint);
            if (count > 0) WritePendingRotations(rotations);
            return count > 0;
        }
    }

    public MaintenanceServiceIdentityTrustPin PinEnrollment(string target, string campusId,
        string agentIdentityFingerprint, string signedEnrollmentJson, DateTimeOffset? nowUtc = null) =>
        PinEnrollmentCore(target, campusId, agentIdentityFingerprint, signedEnrollmentJson,
            allowKeyRotation: false, nowUtc ?? DateTimeOffset.UtcNow);

    private MaintenanceServiceIdentityTrustPin PinEnrollmentCore(string target, string campusId,
        string agentIdentityFingerprint, string signedEnrollmentJson, bool allowKeyRotation,
        DateTimeOffset nowUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var normalizedTarget = NormalizeTarget(target);
        var agentFingerprint = MaintenanceValidation.Fingerprint(agentIdentityFingerprint,
            nameof(agentIdentityFingerprint));
        ArgumentException.ThrowIfNullOrWhiteSpace(signedEnrollmentJson);

        var agentPublicKey = _agentTrustStore.FindTrustedPublicKey(campusId, normalizedTarget)
                             ?? throw new InvalidDataException("Maintenance 登记失败：该设备尚无固定 Agent 身份。");
        if (!string.Equals(StudentAgentResponseCryptography.GetFingerprint(agentPublicKey), agentFingerprint,
                StringComparison.Ordinal))
            throw new InvalidDataException("Maintenance 登记的 Agent 身份与教师端当前设备固定身份不匹配。");

        var verified = MaintenanceServiceIdentityStore.VerifyEnrollment(signedEnrollmentJson,
            agentPublicKey, campusId, agentFingerprint, nowUtc);
        lock (_gate)
        {
            var pins = ReadPins().ToList();
            var existingIndex = pins.FindIndex(pin => pin.CampusId == campusId &&
                string.Equals(pin.Target, normalizedTarget, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pin.AgentIdentityFingerprint, agentFingerprint, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                var existing = pins[existingIndex];
                if (string.Equals(existing.MaintenanceIdentityFingerprint,
                        verified.MaintenanceIdentityFingerprint, StringComparison.Ordinal))
                    return existing;
                if (!allowKeyRotation)
                    throw new InvalidDataException("Maintenance 身份密钥已变化；须先核对新旧指纹并明确批准轮换。");
                pins.RemoveAt(existingIndex);
            }
            if (pins.Count >= MaximumPins)
                throw new InvalidDataException("Maintenance 身份信任清单达到容量上限。");

            var pin = new MaintenanceServiceIdentityTrustPin(normalizedTarget, campusId, agentFingerprint,
                verified.MaintenanceIdentityFingerprint, verified.MaintenancePublicKeyPem,
                nowUtc.ToUniversalTime());
            pins.Add(pin);
            WritePins(pins);
            return pin;
        }
    }

    public bool Remove(string campusId, string target, string expectedAgentIdentityFingerprint,
        string expectedMaintenanceIdentityFingerprint)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var normalizedTarget = NormalizeTarget(target);
        var agentFingerprint = MaintenanceValidation.Fingerprint(expectedAgentIdentityFingerprint,
            nameof(expectedAgentIdentityFingerprint));
        var maintenanceFingerprint = MaintenanceValidation.Fingerprint(expectedMaintenanceIdentityFingerprint,
            nameof(expectedMaintenanceIdentityFingerprint));
        lock (_gate)
        {
            var pins = ReadPins().ToList();
            var index = pins.FindIndex(pin => pin.CampusId == campusId &&
                string.Equals(pin.Target, normalizedTarget, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pin.AgentIdentityFingerprint, agentFingerprint, StringComparison.Ordinal) &&
                string.Equals(pin.MaintenanceIdentityFingerprint, maintenanceFingerprint,
                    StringComparison.Ordinal));
            if (index < 0) return false;
            pins.RemoveAt(index);
            WritePins(pins);
            return true;
        }
    }

    private IReadOnlyList<MaintenanceServiceIdentityTrustPin> ReadPins()
    {
        if (!File.Exists(_path)) return Array.Empty<MaintenanceServiceIdentityTrustPin>();
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("Maintenance 身份信任清单大小无效。");
        var bytes = File.ReadAllBytes(_path);
        PolicyJson.RejectDuplicateFields(bytes);
        var document = JsonSerializer.Deserialize<TrustDocument>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("Maintenance 身份信任清单为空。");
        if (document.SchemaVersion != 1 || document.Pins is null || document.Pins.Count > MaximumPins)
            throw new InvalidDataException("Maintenance 身份信任清单版本或条目数无效。");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in document.Pins)
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(pin.CampusId);
            var target = NormalizeTarget(pin.Target);
            var agentFingerprint = MaintenanceValidation.Fingerprint(pin.AgentIdentityFingerprint,
                nameof(pin.AgentIdentityFingerprint));
            var maintenanceFingerprint = MaintenanceValidation.Fingerprint(pin.MaintenanceIdentityFingerprint,
                nameof(pin.MaintenanceIdentityFingerprint));
            using var key = RSA.Create();
            key.ImportFromPem(pin.MaintenancePublicKeyPem);
            var canonicalPem = key.ExportSubjectPublicKeyInfoPem();
            var actualFingerprint = StudentAgentResponseCryptography.GetFingerprint(canonicalPem);
            var identity = pin.CampusId + "\n" + target + "\n" + agentFingerprint;
            if (pin.Target != target || pin.AgentIdentityFingerprint != agentFingerprint ||
                pin.MaintenanceIdentityFingerprint != maintenanceFingerprint ||
                pin.MaintenancePublicKeyPem != canonicalPem || maintenanceFingerprint != actualFingerprint ||
                pin.PinnedUtc.Offset != TimeSpan.Zero || !seen.Add(identity))
                throw new InvalidDataException("Maintenance 身份信任条目无效或重复。");
        }
        return document.Pins;
    }

    private IReadOnlyList<PendingMaintenanceServiceIdentityRotation> ReadPendingRotations()
    {
        if (!File.Exists(_pendingRotationPath)) return Array.Empty<PendingMaintenanceServiceIdentityRotation>();
        PathLinkSecurity.RejectLinks(_pendingRotationPath);
        var info = new FileInfo(_pendingRotationPath);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("Maintenance 待审批密钥轮换清单大小无效。");
        var bytes = File.ReadAllBytes(_pendingRotationPath);
        PolicyJson.RejectDuplicateFields(bytes);
        var document = JsonSerializer.Deserialize<PendingRotationDocument>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("Maintenance 待审批密钥轮换清单为空。");
        if (document.SchemaVersion != 1 || document.Requests is null ||
            document.Requests.Count > MaximumPendingRotations)
            throw new InvalidDataException("Maintenance 待审批密钥轮换清单版本或条目数无效。");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in document.Requests)
        {
            WebsitePolicySigningKeyStore.ValidateCampusId(request.CampusId);
            var target = NormalizeTarget(request.Target);
            var agentFingerprint = MaintenanceValidation.Fingerprint(request.AgentIdentityFingerprint,
                nameof(request.AgentIdentityFingerprint));
            var previousFingerprint = MaintenanceValidation.Fingerprint(
                request.PreviousMaintenanceIdentityFingerprint, nameof(request.PreviousMaintenanceIdentityFingerprint));
            var candidateFingerprint = MaintenanceValidation.Fingerprint(
                request.CandidateMaintenanceIdentityFingerprint, nameof(request.CandidateMaintenanceIdentityFingerprint));
            using var candidateKey = RSA.Create();
            candidateKey.ImportFromPem(request.CandidateMaintenancePublicKeyPem);
            var canonicalCandidatePem = candidateKey.ExportSubjectPublicKeyInfoPem();
            var identity = request.CampusId + "\n" + target + "\n" + agentFingerprint;
            if (request.Target != target || request.AgentIdentityFingerprint != agentFingerprint ||
                request.PreviousMaintenanceIdentityFingerprint != previousFingerprint ||
                request.CandidateMaintenanceIdentityFingerprint != candidateFingerprint ||
                request.CandidateMaintenancePublicKeyPem != canonicalCandidatePem ||
                StudentAgentResponseCryptography.GetFingerprint(canonicalCandidatePem) != candidateFingerprint ||
                previousFingerprint == candidateFingerprint || request.RequestedUtc.Offset != TimeSpan.Zero ||
                string.IsNullOrWhiteSpace(request.SignedEnrollmentJson) ||
                Encoding.UTF8.GetByteCount(request.SignedEnrollmentJson) is 0 or > MaximumPendingEnrollmentBytes ||
                !seen.Add(identity))
                throw new InvalidDataException("Maintenance 待审批密钥轮换条目无效或重复。");
        }
        return document.Requests;
    }

    private IReadOnlyList<PendingMaintenanceServiceIdentityRotation> PruneExpiredPendingRotations(
        DateTimeOffset nowUtc)
    {
        var rotations = ReadPendingRotations();
        var fresh = rotations.Where(item => IsPendingRotationFresh(item, nowUtc)).ToArray();
        if (fresh.Length != rotations.Count) WritePendingRotations(fresh);
        return fresh;
    }

    private static bool IsPendingRotationFresh(PendingMaintenanceServiceIdentityRotation request,
        DateTimeOffset nowUtc) => request.RequestedUtc >= nowUtc - PendingRotationLifetime &&
                                  request.RequestedUtc <= nowUtc.AddMinutes(2);

    private void WritePins(IReadOnlyCollection<MaintenanceServiceIdentityTrustPin> pins)
    {
        var fullPath = Path.GetFullPath(_path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidDataException("Maintenance 信任清单目录无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TrustDocument(1, pins.ToArray()), JsonOptions);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("Maintenance 身份信任清单超过大小限制。");
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            PathLinkSecurity.RejectLinks(directory);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void WritePendingRotations(IReadOnlyCollection<PendingMaintenanceServiceIdentityRotation> requests)
    {
        var fullPath = Path.GetFullPath(_pendingRotationPath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidDataException("Maintenance 待审批轮换目录无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinksAllowingMissingComponents(fullPath);
        PathLinkSecurity.RejectLinks(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PendingRotationDocument(1, requests.ToArray()),
            JsonOptions);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("Maintenance 待审批密钥轮换清单超过大小限制。");
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            PathLinkSecurity.RejectLinks(directory);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string NormalizeTarget(string target)
    {
        var normalized = WebsitePolicyTransport.NormalizeTargets([target]);
        if (normalized.Count != 1) throw new InvalidDataException("Maintenance 信任目标无效。");
        return normalized[0].ToLowerInvariant();
    }

    private sealed record TrustDocument(int SchemaVersion, IReadOnlyList<MaintenanceServiceIdentityTrustPin> Pins);
    private sealed record PendingRotationDocument(int SchemaVersion,
        IReadOnlyList<PendingMaintenanceServiceIdentityRotation> Requests);
}

