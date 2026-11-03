using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public enum PolicyTrustKeyRole
{
    WebsitePolicy,
    ApplicationPolicy,
    StudentSystemPolicy
}

/// <summary>
/// A bounded transition certificate for replacing one policy signing root. The old root
/// authorizes the new root, and the new root proves possession of its private key.
/// </summary>
public sealed record PolicyTrustRotationAuthorization(
    int SchemaVersion,
    string CampusId,
    PolicyTrustKeyRole Role,
    string PreviousFingerprint,
    string NewFingerprint,
    string NewPublicKeyPem,
    DateTimeOffset IssuedUtc,
    DateTimeOffset RetirePreviousRootUtc,
    string PreviousRootSignature,
    string NewRootProofSignature);

public sealed record VerifiedPolicyTrustRotation(
    string CampusId,
    PolicyTrustKeyRole Role,
    string PreviousFingerprint,
    string NewFingerprint,
    string PreviousPublicKeyPem,
    string NewPublicKeyPem,
    DateTimeOffset IssuedUtc,
    DateTimeOffset RetirePreviousRootUtc);

public static class PolicyTrustRotationCryptography
{
    public const int MaximumEnvelopeBytes = 16 * 1024;
    public static readonly TimeSpan MaximumTransition = TimeSpan.FromDays(90);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string Create(string campusId, PolicyTrustKeyRole role, RSA previousPrivateKey,
        RSA newPrivateKey, DateTimeOffset issuedUtc, DateTimeOffset retirePreviousRootUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentNullException.ThrowIfNull(previousPrivateKey);
        ArgumentNullException.ThrowIfNull(newPrivateKey);
        ValidateKeySize(previousPrivateKey, nameof(previousPrivateKey));
        ValidateKeySize(newPrivateKey, nameof(newPrivateKey));

        var issued = issuedUtc.ToUniversalTime();
        var retire = retirePreviousRootUtc.ToUniversalTime();
        if (retire <= issued || retire - issued > MaximumTransition)
            throw new InvalidDataException("策略信任根过渡期必须大于零且不超过 90 天。");

        var previousPem = previousPrivateKey.ExportSubjectPublicKeyInfoPem();
        var newPem = newPrivateKey.ExportSubjectPublicKeyInfoPem();
        var previousFingerprint = Fingerprint(previousPem);
        var newFingerprint = Fingerprint(newPem);
        if (previousFingerprint == newFingerprint)
            throw new InvalidDataException("新策略信任根必须与旧根不同。");

        var unsigned = new UnsignedAuthorization(1, campusId, role, previousFingerprint, newFingerprint,
            newPem, issued, retire);
        var payload = SerializeCanonical(unsigned);
        var previousSignature = previousPrivateKey.SignData(payload, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        var newProof = newPrivateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var authorization = new PolicyTrustRotationAuthorization(unsigned.SchemaVersion, unsigned.CampusId,
            unsigned.Role, unsigned.PreviousFingerprint, unsigned.NewFingerprint, unsigned.NewPublicKeyPem,
            unsigned.IssuedUtc, unsigned.RetirePreviousRootUtc, Convert.ToBase64String(previousSignature),
            Convert.ToBase64String(newProof));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(authorization, JsonOptions);
        if (bytes.Length > MaximumEnvelopeBytes)
            throw new InvalidDataException("策略信任根轮换授权超过大小限制。");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public static VerifiedPolicyTrustRotation Verify(string envelopeJson, string expectedCampusId,
        PolicyTrustKeyRole expectedRole, string trustedPreviousPublicKeyPem, DateTimeOffset nowUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        if (!Enum.IsDefined(expectedRole)) throw new ArgumentOutOfRangeException(nameof(expectedRole));
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeJson);
        var bytes = System.Text.Encoding.UTF8.GetBytes(envelopeJson);
        if (bytes.Length is 0 or > MaximumEnvelopeBytes)
            throw new InvalidDataException("策略信任根轮换授权大小无效。");
        PolicyJson.RejectDuplicateFields(bytes);
        var authorization = JsonSerializer.Deserialize<PolicyTrustRotationAuthorization>(bytes, JsonOptions)
                            ?? throw new InvalidDataException("策略信任根轮换授权为空。");
        if (authorization.SchemaVersion != 1 || authorization.CampusId != expectedCampusId ||
            authorization.Role != expectedRole)
            throw new InvalidDataException("策略信任根轮换授权版本、校区或密钥用途不匹配。");

        var issued = authorization.IssuedUtc.ToUniversalTime();
        var retire = authorization.RetirePreviousRootUtc.ToUniversalTime();
        var now = nowUtc.ToUniversalTime();
        if (issued > now + MaximumClockSkew || retire <= now || retire <= issued ||
            retire - issued > MaximumTransition)
            throw new InvalidDataException("策略信任根轮换授权尚未生效、已过期或过渡期无效。");

        using var previousKey = ImportPublicKey(trustedPreviousPublicKeyPem);
        using var newKey = ImportPublicKey(authorization.NewPublicKeyPem);
        var canonicalPreviousPem = previousKey.ExportSubjectPublicKeyInfoPem();
        var canonicalNewPem = newKey.ExportSubjectPublicKeyInfoPem();
        var previousFingerprint = Fingerprint(canonicalPreviousPem);
        var newFingerprint = Fingerprint(canonicalNewPem);
        if (authorization.PreviousFingerprint != previousFingerprint ||
            authorization.NewFingerprint != newFingerprint || previousFingerprint == newFingerprint)
            throw new InvalidDataException("策略信任根轮换授权中的旧/新指纹不匹配。");

        byte[] previousSignature;
        byte[] newProof;
        try
        {
            previousSignature = Convert.FromBase64String(authorization.PreviousRootSignature);
            newProof = Convert.FromBase64String(authorization.NewRootProofSignature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("策略信任根轮换签名编码无效。", exception);
        }

        var unsigned = new UnsignedAuthorization(authorization.SchemaVersion, authorization.CampusId,
            authorization.Role, authorization.PreviousFingerprint, authorization.NewFingerprint,
            canonicalNewPem, issued, retire);
        var payload = SerializeCanonical(unsigned);
        if (!previousKey.VerifyData(payload, previousSignature, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss) ||
            !newKey.VerifyData(payload, newProof, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("策略信任根轮换没有通过旧根授权和新根持有证明。");

        return new VerifiedPolicyTrustRotation(expectedCampusId, expectedRole, previousFingerprint, newFingerprint,
            canonicalPreviousPem, canonicalNewPem, issued, retire);
    }

    private static byte[] SerializeCanonical(UnsignedAuthorization value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", value.SchemaVersion);
            writer.WriteString("campusId", value.CampusId);
            writer.WriteNumber("role", (int)value.Role);
            writer.WriteString("previousFingerprint", value.PreviousFingerprint);
            writer.WriteString("newFingerprint", value.NewFingerprint);
            writer.WriteString("newPublicKeyPem", value.NewPublicKeyPem);
            writer.WriteString("issuedUtc", value.IssuedUtc.ToString("O"));
            writer.WriteString("retirePreviousRootUtc", value.RetirePreviousRootUtc.ToString("O"));
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static RSA ImportPublicKey(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        if (pem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("策略信任轮换只允许包含公钥。");
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(pem);
            ValidateKeySize(key, nameof(pem));
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static string Fingerprint(string publicKeyPem)
    {
        using var key = RSA.Create();
        key.ImportFromPem(publicKeyPem);
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }

    private static void ValidateKeySize(RSA key, string parameterName)
    {
        if (key.KeySize is < 2048 or > 4096)
            throw new ArgumentException("策略信任根 RSA 密钥长度必须为 2048 至 4096 位。", parameterName);
    }

    private sealed record UnsignedAuthorization(int SchemaVersion, string CampusId, PolicyTrustKeyRole Role,
        string PreviousFingerprint, string NewFingerprint, string NewPublicKeyPem, DateTimeOffset IssuedUtc,
        DateTimeOffset RetirePreviousRootUtc);
}
