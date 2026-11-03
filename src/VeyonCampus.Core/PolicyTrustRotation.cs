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

    internal static T VerifyWithAnyRoot<T>(IReadOnlyList<string> publicKeys, Func<string, T> verify)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        ArgumentNullException.ThrowIfNull(verify);
        var failures = new List<Exception>();
        foreach (var publicKey in publicKeys)
        {
            try { return verify(publicKey); }
            catch (Exception exception) when (exception is InvalidDataException or CryptographicException or
                                              ArgumentException or System.Text.Json.JsonException)
            { failures.Add(exception); }
        }
        throw new InvalidDataException("没有任何当前或过渡期策略信任根验证通过。", new AggregateException(failures));
    }

    internal static async Task<T> VerifyWithAnyRootAsync<T>(IReadOnlyList<string> publicKeys,
        Func<string, Task<T>> verify)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        ArgumentNullException.ThrowIfNull(verify);
        var failures = new List<Exception>();
        foreach (var publicKey in publicKeys)
        {
            try { return await verify(publicKey).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidDataException or CryptographicException or
                                              ArgumentException or System.Text.Json.JsonException)
            { failures.Add(exception); }
        }
        throw new InvalidDataException("没有任何当前或过渡期策略信任根验证通过。", new AggregateException(failures));
    }

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
        var authorization = Parse(envelopeJson);
        if (authorization.CampusId != expectedCampusId || authorization.Role != expectedRole)
            throw new InvalidDataException("策略信任根轮换授权校区或密钥用途不匹配。");

        var issued = authorization.IssuedUtc;
        var retire = authorization.RetirePreviousRootUtc;
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

        var unsigned = new UnsignedAuthorization(authorization.SchemaVersion, authorization.CampusId,
            authorization.Role, authorization.PreviousFingerprint, authorization.NewFingerprint,
            canonicalNewPem, issued, retire);
        var payload = SerializeCanonical(unsigned);
        var previousSignature = Convert.FromBase64String(authorization.PreviousRootSignature);
        var newProof = Convert.FromBase64String(authorization.NewRootProofSignature);
        if (!previousKey.VerifyData(payload, previousSignature, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss) ||
            !newKey.VerifyData(payload, newProof, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("策略信任根轮换没有通过旧根授权和新根持有证明。");

        return new VerifiedPolicyTrustRotation(expectedCampusId, expectedRole, previousFingerprint, newFingerprint,
            canonicalPreviousPem, canonicalNewPem, issued, retire);
    }

    public static PolicyTrustRotationAuthorization Parse(string envelopeJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeJson);
        var bytes = System.Text.Encoding.UTF8.GetBytes(envelopeJson);
        if (bytes.Length is 0 or > MaximumEnvelopeBytes)
            throw new InvalidDataException("策略信任根轮换授权大小无效。");
        PolicyJson.RejectDuplicateFields(bytes);
        var authorization = JsonSerializer.Deserialize<PolicyTrustRotationAuthorization>(bytes, JsonOptions)
                            ?? throw new InvalidDataException("策略信任根轮换授权为空。");
        WebsitePolicySigningKeyStore.ValidateCampusId(authorization.CampusId);
        if (authorization.SchemaVersion != 1 || !Enum.IsDefined(authorization.Role))
            throw new InvalidDataException("策略信任根轮换授权版本或密钥用途无效。");
        var issued = authorization.IssuedUtc.ToUniversalTime();
        var retire = authorization.RetirePreviousRootUtc.ToUniversalTime();
        using var newKey = ImportPublicKey(authorization.NewPublicKeyPem);
        var canonicalNewPem = newKey.ExportSubjectPublicKeyInfoPem();
        var previousFingerprint = ValidateFingerprint(authorization.PreviousFingerprint,
            nameof(authorization.PreviousFingerprint));
        var newFingerprint = Fingerprint(canonicalNewPem);
        if (authorization.NewFingerprint != newFingerprint || previousFingerprint == newFingerprint ||
            retire <= issued || retire - issued > MaximumTransition)
            throw new InvalidDataException("策略信任根轮换授权中的新指纹或过渡期无效。");
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
        if (previousSignature.Length is < 256 or > 512 || newProof.Length is < 256 or > 512)
            throw new InvalidDataException("策略信任根轮换签名长度不正确。");
        return authorization with
        {
            PreviousFingerprint = previousFingerprint,
            NewFingerprint = newFingerprint,
            NewPublicKeyPem = canonicalNewPem,
            IssuedUtc = issued,
            RetirePreviousRootUtc = retire,
            PreviousRootSignature = Convert.ToBase64String(previousSignature),
            NewRootProofSignature = Convert.ToBase64String(newProof)
        };
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

    private static string ValidateFingerprint(string value, string name)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"{name} 格式无效。");
        return value.ToUpperInvariant();
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
