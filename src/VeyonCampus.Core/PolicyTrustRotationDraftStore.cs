using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace VeyonCampus.Core;

public sealed record PolicyTrustRotationDraftEntry(
    PolicyTrustKeyRole Role,
    string PreviousCertificateThumbprint,
    string CandidateCertificateThumbprint,
    string AuthorizationJson);

public sealed record PolicyTrustRotationDraft(
    int SchemaVersion,
    string CampusId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset RetirePreviousRootUtc,
    IReadOnlyList<PolicyTrustRotationDraftEntry> Entries);

/// <summary>Persists resumable rotation metadata; private keys remain in non-exportable CNG certificates.</summary>
public static class PolicyTrustRotationDraftStore
{
    private const string RegistryRoot = @"Software\VeyonCampus\PolicyTrustRotationDrafts";
    private const string DraftValue = "DraftJson";
    private const int MaximumDraftBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    [SupportedOSPlatform("windows")]
    public static PolicyTrustRotationDraft Create(string campusId, IEnumerable<PolicyTrustKeyRole> roles,
        DateTimeOffset retirePreviousRootUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        ArgumentNullException.ThrowIfNull(roles);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("策略信任根轮换草稿目前保存在 Windows 当前用户注册表中。");
        var selectedRoles = roles.ToArray();
        if (selectedRoles.Length is < 1 or > 3 || selectedRoles.Any(role => !Enum.IsDefined(role)) ||
            selectedRoles.Distinct().Count() != selectedRoles.Length)
            throw new InvalidDataException("轮换草稿必须选择 1 至 3 个不同的策略用途。");
        if (ReadRaw(campusId) is not null)
            throw new InvalidOperationException("此校区已有未完成的策略根轮换草稿；请先完成或清理该草稿。");

        var createdUtc = DateTimeOffset.UtcNow;
        var retireUtc = retirePreviousRootUtc.ToUniversalTime();
        if (retireUtc <= createdUtc || retireUtc - createdUtc > PolicyTrustRotationCryptography.MaximumTransition)
            throw new InvalidDataException("策略根退休时间必须在现在之后且不超过 90 天。");

        var entries = new List<PolicyTrustRotationDraftEntry>(selectedRoles.Length);
        foreach (var role in selectedRoles)
        {
            using var previous = OpenCurrentRoot(campusId, role);
            using var candidate = CreateCandidateRoot(campusId, role);
            var authorization = PolicyTrustRotationCryptography.Create(campusId, role,
                previous.PrivateKey, candidate.PrivateKey, createdUtc, retireUtc);
            entries.Add(new PolicyTrustRotationDraftEntry(role, previous.CertificateThumbprint,
                candidate.CertificateThumbprint, authorization));
        }

        var draft = new PolicyTrustRotationDraft(1, campusId, createdUtc, retireUtc,
            Array.AsReadOnly(entries.ToArray()));
        WriteRaw(campusId, draft);
        return draft;
    }

    [SupportedOSPlatform("windows")]
    public static PolicyTrustRotationDraft? Read(string campusId, DateTimeOffset? nowUtc = null)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("策略信任根轮换草稿目前保存在 Windows 当前用户注册表中。");
        var json = ReadRaw(campusId);
        if (json is null) return null;
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length is 0 or > MaximumDraftBytes)
            throw new InvalidDataException("策略根轮换草稿大小无效。");
        PolicyJson.RejectDuplicateFields(bytes);
        var draft = JsonSerializer.Deserialize<PolicyTrustRotationDraft>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("策略根轮换草稿为空。");
        ValidateDraft(draft, campusId, nowUtc ?? DateTimeOffset.UtcNow);
        return draft;
    }

    private static void ValidateDraft(PolicyTrustRotationDraft draft, string campusId, DateTimeOffset nowUtc)
    {
        if (draft.SchemaVersion != 1 || !string.Equals(draft.CampusId, campusId, StringComparison.Ordinal) ||
            draft.Entries is null || draft.Entries.Count is < 1 or > 3 ||
            draft.Entries.Select(item => item.Role).Distinct().Count() != draft.Entries.Count ||
            draft.CreatedUtc.Offset != TimeSpan.Zero || draft.RetirePreviousRootUtc.Offset != TimeSpan.Zero ||
            draft.CreatedUtc > nowUtc.ToUniversalTime().AddMinutes(5) ||
            draft.RetirePreviousRootUtc <= draft.CreatedUtc ||
            draft.RetirePreviousRootUtc <= nowUtc.ToUniversalTime() ||
            draft.RetirePreviousRootUtc - draft.CreatedUtc > PolicyTrustRotationCryptography.MaximumTransition)
            throw new InvalidDataException("策略根轮换草稿版本、校区、用途或过渡期限无效。");

        foreach (var entry in draft.Entries)
        {
            if (!Enum.IsDefined(entry.Role))
                throw new InvalidDataException("策略根轮换草稿包含未知用途。");
            var previousThumbprint = NormalizeThumbprint(entry.PreviousCertificateThumbprint);
            var candidateThumbprint = NormalizeThumbprint(entry.CandidateCertificateThumbprint);
            using var previous = OpenCurrentRoot(campusId, entry.Role);
            if (!string.Equals(previous.CertificateThumbprint, previousThumbprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("策略根轮换草稿的旧密钥已不再是当前固定密钥。");
            var authorization = PolicyTrustRotationCryptography.Verify(entry.AuthorizationJson, campusId,
                entry.Role, previous.PublicKeyPem, nowUtc);
            using var candidate = OpenCandidateRoot(campusId, entry.Role, candidateThumbprint);
            if (!PublicKeysEqual(candidate.PublicKeyPem, authorization.NewPublicKeyPem) ||
                !string.Equals(candidate.CertificateThumbprint, candidateThumbprint,
                    StringComparison.OrdinalIgnoreCase) ||
                authorization.IssuedUtc != draft.CreatedUtc ||
                authorization.RetirePreviousRootUtc != draft.RetirePreviousRootUtc)
                throw new InvalidDataException("候选证书、轮换授权和草稿退休时间不一致。");
        }
    }

    private static string NormalizeThumbprint(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        var normalized = new string(thumbprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length is < 40 or > 128 || normalized.Length != thumbprint.Count(Uri.IsHexDigit))
            throw new InvalidDataException("策略证书指纹格式无效。");
        return normalized;
    }

    private static bool PublicKeysEqual(string firstPem, string secondPem)
    {
        using var first = RSA.Create();
        using var second = RSA.Create();
        first.ImportFromPem(firstPem);
        second.ImportFromPem(secondPem);
        return first.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(second.ExportSubjectPublicKeyInfo());
    }

    private static RotationRoot OpenCurrentRoot(string campusId, PolicyTrustKeyRole role) => role switch
    {
        PolicyTrustKeyRole.WebsitePolicy => RotationRoot.From(WebsitePolicySigningKeyStore.Open(campusId)),
        PolicyTrustKeyRole.ApplicationPolicy => RotationRoot.From(ApplicationPolicySigningKeyStore.Open(campusId)),
        PolicyTrustKeyRole.StudentSystemPolicy => RotationRoot.From(StudentSystemPolicySigningKeyStore.Open(campusId)),
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private static RotationRoot CreateCandidateRoot(string campusId, PolicyTrustKeyRole role) => role switch
    {
        PolicyTrustKeyRole.WebsitePolicy => RotationRoot.From(
            WebsitePolicySigningKeyStore.CreateUnpinnedReplacement(campusId)),
        PolicyTrustKeyRole.ApplicationPolicy => RotationRoot.From(
            ApplicationPolicySigningKeyStore.CreateUnpinnedReplacement(campusId)),
        PolicyTrustKeyRole.StudentSystemPolicy => RotationRoot.From(
            StudentSystemPolicySigningKeyStore.CreateUnpinnedReplacement(campusId)),
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private static RotationRoot OpenCandidateRoot(string campusId, PolicyTrustKeyRole role, string thumbprint) =>
        role switch
        {
            PolicyTrustKeyRole.WebsitePolicy => RotationRoot.From(
                WebsitePolicySigningKeyStore.OpenByThumbprint(campusId, thumbprint)),
            PolicyTrustKeyRole.ApplicationPolicy => RotationRoot.From(
                ApplicationPolicySigningKeyStore.OpenReplacementByThumbprint(campusId, thumbprint)),
            PolicyTrustKeyRole.StudentSystemPolicy => RotationRoot.From(
                StudentSystemPolicySigningKeyStore.OpenReplacementByThumbprint(campusId, thumbprint)),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

    private static string? ReadRaw(string campusId)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPathFor(campusId), writable: false);
        return key?.GetValue(DraftValue) as string;
    }

    private static void WriteRaw(string campusId, PolicyTrustRotationDraft draft)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions);
        if (bytes.Length > MaximumDraftBytes)
            throw new InvalidDataException("策略根轮换草稿超过保存大小限制。");
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPathFor(campusId), writable: true)
                        ?? throw new IOException("无法保存策略根轮换草稿；没有更改当前信任根。");
        key.SetValue(DraftValue, Encoding.UTF8.GetString(bytes), RegistryValueKind.String);
    }

    private static string RegistryPathFor(string campusId) => RegistryRoot + "\\" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(campusId)));

    private sealed class RotationRoot(RSA privateKey, string publicKeyPem, string certificateThumbprint,
        IDisposable owner) : IDisposable
    {
        public RSA PrivateKey { get; } = privateKey;
        public string PublicKeyPem { get; } = publicKeyPem;
        public string CertificateThumbprint { get; } = certificateThumbprint;
        public void Dispose() => owner.Dispose();

        public static RotationRoot From(WebsitePolicySigningKey key) =>
            new(key.PrivateKey, key.PublicKeyPem, key.CertificateThumbprint, key);

        public static RotationRoot From(ApplicationPolicySigningKey key) =>
            new(key.PrivateKey, key.PublicKeyPem, key.CertificateThumbprint, key);
    }
}
