using System.Security.Cryptography;
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

/// <summary>
/// Teacher-side pins for Maintenance identities. A new key is admitted only through an
/// enrollment signed by the already pinned Agent identity; changed keys require an explicit
/// rotation decision and the same enrollment proof.
/// </summary>
public sealed class MaintenanceServiceTrustStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private const int MaximumPins = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _path;
    private readonly StudentAgentIdentityTrustStore _agentTrustStore;
    private readonly object _gate = new();

    public MaintenanceServiceTrustStore(StudentAgentIdentityTrustStore? agentTrustStore = null,
        string? path = null)
    {
        _agentTrustStore = agentTrustStore ?? new StudentAgentIdentityTrustStore();
        _path = path ?? DefaultPath;
    }

    public static string DefaultPath => Path.Combine(
        VeyonCampusPaths.GetLocalApplicationDataDirectory(),
        "VeyonCampus", "MaintenanceTrust", "services.json");

    public IReadOnlyList<MaintenanceServiceIdentityTrustPin> List()
    {
        lock (_gate) return Array.AsReadOnly(ReadPins().ToArray());
    }

    public string? FindTrustedPublicKey(string campusId, string target, string agentIdentityFingerprint)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var normalizedTarget = NormalizeTarget(target);
        var agentFingerprint = MaintenanceValidation.Fingerprint(agentIdentityFingerprint,
            nameof(agentIdentityFingerprint));
        lock (_gate)
        {
            return ReadPins().SingleOrDefault(pin => pin.CampusId == campusId &&
                string.Equals(pin.Target, normalizedTarget, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pin.AgentIdentityFingerprint, agentFingerprint, StringComparison.Ordinal))
                ?.MaintenancePublicKeyPem;
        }
    }

    public MaintenanceServiceIdentityTrustPin PinEnrollment(string target, string campusId,
        string agentIdentityFingerprint, string signedEnrollmentJson, bool allowKeyRotation = false,
        DateTimeOffset? nowUtc = null)
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
            agentPublicKey, campusId, agentFingerprint, nowUtc ?? DateTimeOffset.UtcNow);
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
                (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());
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

    private static string NormalizeTarget(string target)
    {
        var normalized = WebsitePolicyTransport.NormalizeTargets([target]);
        if (normalized.Count != 1) throw new InvalidDataException("Maintenance 信任目标无效。");
        return normalized[0].ToLowerInvariant();
    }

    private sealed record TrustDocument(int SchemaVersion, IReadOnlyList<MaintenanceServiceIdentityTrustPin> Pins);
}
