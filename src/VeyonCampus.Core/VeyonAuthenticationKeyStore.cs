using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VeyonCampus.Core;

/// <summary>Stores the Veyon teacher key in the current user's macOS certificate keychain.</summary>
public static class VeyonAuthenticationKeyStore
{
    private const int MaximumPemCharacters = 64 * 1024;

    public static bool ImportPrivateKey(string campusId, char[] privateKeyPem, char[] expectedPublicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(privateKeyPem);
        ArgumentNullException.ThrowIfNull(expectedPublicKeyPem);
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Veyon 私钥迁移只在 Mac 教师端写入当前用户钥匙串。");
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        if (privateKeyPem.Length is 0 or > MaximumPemCharacters ||
            expectedPublicKeyPem.Length is 0 or > MaximumPemCharacters)
            throw new InvalidDataException("Veyon PEM 文件为空或超过安全读取上限。");

        using var importedKey = RSA.Create();
        importedKey.ImportFromPem(privateKeyPem.AsSpan());
        using var expectedPublicKey = RSA.Create();
        expectedPublicKey.ImportFromPem(expectedPublicKeyPem.AsSpan());
        if (!SamePublicKey(importedKey, expectedPublicKey))
            throw new CryptographicException("此私钥与所选学生配置包中的 Veyon 公钥不匹配；没有保存私钥。");

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var subject = SubjectFor(campusId);
        var existing = FindBySubject(store, subject);
        if (existing is not null)
        {
            using (existing)
            using (var existingKey = existing.GetRSAPrivateKey())
            {
                if (existingKey is null)
                    throw new CryptographicException("钥匙串中已有此校区的 Veyon 证书，但私钥无法访问；没有覆盖现有项目。");
                if (!SamePublicKey(existingKey, expectedPublicKey))
                    throw new CryptographicException("钥匙串中已有不同的校区 Veyon 私钥；没有覆盖现有项目。");
                return false;
            }
        }

        var request = new CertificateRequest(subject, importedKey, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(20));
        if (!created.HasPrivateKey)
            throw new CryptographicException("Veyon 私钥没有附加到钥匙串证书；没有完成导入。");

        store.Add(created);
        using var stored = FindByThumbprint(store, created.Thumbprint) ??
            throw new CryptographicException("Veyon 私钥写入钥匙串后无法按指纹读回。");
        using var storedKey = stored.GetRSAPrivateKey() ??
            throw new CryptographicException("Veyon 私钥已写入钥匙串，但无法重新打开。");
        if (!SamePublicKey(storedKey, expectedPublicKey))
            throw new CryptographicException("Veyon 私钥写入钥匙串后的公钥校验失败。");
        return true;
    }

    public static bool HasPrivateKey(string campusId)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        using var certificate = FindBySubject(store, SubjectFor(campusId));
        if (certificate is null) return false;
        using var key = certificate.GetRSAPrivateKey();
        return key is not null;
    }

    /// <summary>Exports a short-lived in-memory copy for immediate injection into helper tmpfs.</summary>
    public static byte[] ExportPrivateKeyPemBytes(string campusId)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        using var certificate = FindBySubject(store, SubjectFor(campusId)) ??
            throw new CryptographicException("Mac 钥匙串中没有此校区的 Veyon 私钥。");
        using var key = certificate.GetRSAPrivateKey() ??
            throw new CryptographicException("Mac 钥匙串中的 Veyon 私钥无法使用。");
        var privateKeyDer = key.ExportRSAPrivateKey();
        try
        {
            return PemEncoding.WriteUtf8("RSA PRIVATE KEY"u8, privateKeyDer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyDer);
        }
    }

    private static string SubjectFor(string campusId) =>
        "CN=VeyonCampus-VeyonAuth-" + VeyonAuthKeyId.ForCampus(campusId);

    private static bool SamePublicKey(RSA left, RSA right)
    {
        var leftBytes = left.ExportSubjectPublicKeyInfo();
        var rightBytes = right.ExportSubjectPublicKeyInfo();
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static X509Certificate2? FindBySubject(X509Store store, string subject)
    {
        X509Certificate2? match = null;
        foreach (var certificate in store.Certificates.Cast<X509Certificate2>())
        {
            if (!string.Equals(certificate.Subject, subject, StringComparison.OrdinalIgnoreCase))
            {
                certificate.Dispose();
                continue;
            }

            if (match is not null)
            {
                match.Dispose();
                certificate.Dispose();
                foreach (var remaining in store.Certificates.Cast<X509Certificate2>()) remaining.Dispose();
                throw new CryptographicException("钥匙串中存在多个同名的校区 Veyon 证书；没有选择或覆盖。");
            }
            match = certificate;
        }
        return match;
    }

    private static X509Certificate2? FindByThumbprint(X509Store store, string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return null;
        X509Certificate2? match = null;
        foreach (var certificate in store.Certificates.Cast<X509Certificate2>())
        {
            if (!string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                certificate.Dispose();
                continue;
            }
            if (match is not null)
            {
                match.Dispose();
                certificate.Dispose();
                throw new CryptographicException("钥匙串中存在重复的 Veyon 证书指纹。");
            }
            match = certificate;
        }
        return match;
    }
}
