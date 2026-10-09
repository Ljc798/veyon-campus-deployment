using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace VeyonCampus.App;

internal sealed class MobileControlTlsIdentity : IDisposable
{
    public MobileControlTlsIdentity(X509Certificate2 root, X509Certificate2 server,
        byte[] rootCertificateBytes, string rootFingerprint, IReadOnlyList<IPAddress> addresses)
    {
        Root = root;
        Server = server;
        RootCertificateBytes = rootCertificateBytes;
        RootFingerprint = rootFingerprint;
        Addresses = addresses;
    }

    public X509Certificate2 Root { get; }
    public X509Certificate2 Server { get; }
    public byte[] RootCertificateBytes { get; }
    public string RootFingerprint { get; }
    public IReadOnlyList<IPAddress> Addresses { get; }
    public void Dispose() { Server.Dispose(); Root.Dispose(); }
}

[SupportedOSPlatform("windows")]
internal static class MobileControlTlsIdentityStore
{
    private const string RootSubject = "CN=Veyon Campus Mobile Control CA";
    private const string RegistryPath = @"Software\VeyonCampus\MobileControl";
    private const string ThumbprintValue = "TlsRootCertificateThumbprint";
    private const string KeyName = "VeyonCampus.MobileControl.Root";
    private const int KeySize = 3072;
    private static readonly CngProvider Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    public static MobileControlTlsIdentity Create(IReadOnlyList<IPAddress> addresses)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("手机控制服务的证书仅能在 Windows 教师端创建。");
        if (addresses.Count is 0 or > 16 || addresses.Any(address => !IsPrivateIpv4(address)))
            throw new InvalidDataException("手机控制服务没有有效的私有 IPv4 地址。");

        var root = GetOrCreateRoot();
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (root.NotAfter.ToUniversalTime() <= now.AddDays(30))
                throw new CryptographicException("教师手机控制根证书即将或已经到期；请先重新配对手机并轮换证书。");
            using var rootKey = root.GetRSAPrivateKey()
                ?? throw new CryptographicException("教师手机控制根证书私钥不可用。");
            using var serverKey = RSA.Create(2048);
            var request = new CertificateRequest("CN=Veyon Campus Teacher Mobile Control", serverKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var subjectNames = new SubjectAlternativeNameBuilder();
            foreach (var address in addresses) subjectNames.AddIpAddress(address);
            request.CertificateExtensions.Add(subjectNames.Build());
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7f;
            if (serial.All(value => value == 0)) serial[^1] = 1;
            using var publicServer = request.Create(root, now.AddMinutes(-5), now.AddDays(365), serial);
            var server = publicServer.CopyWithPrivateKey(serverKey);
            var rootBytes = root.Export(X509ContentType.Cert);
            var fingerprint = Convert.ToHexString(SHA256.HashData(rootBytes));
            return new MobileControlTlsIdentity(root, server, rootBytes, fingerprint,
                Array.AsReadOnly(addresses.ToArray()));
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public static bool IsPrivateIpv4(IPAddress address)
        => MobileControlLanNetworkPolicy.IsPrivateIpv4Address(address);

    private static X509Certificate2 GetOrCreateRoot()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        using var settings = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new IOException("无法读取教师手机控制证书记录。");
        var pinned = settings.GetValue(ThumbprintValue) as string;
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, pinned, validOnly: false);
            if (matches.Count != 1)
                throw new CryptographicException("固定的教师手机控制根证书缺失或重复；没有自动换证，以免已配对手机信任失效。");
            var certificate = matches[0];
            using var certificateKey = certificate.GetRSAPrivateKey();
            if (certificate.Subject != RootSubject || certificateKey is null)
            {
                certificate.Dispose();
                throw new CryptographicException("固定的教师手机控制根证书私钥不可用；没有自动换证。");
            }
            return certificate;
        }

        var existing = store.Certificates.Cast<X509Certificate2>()
            .Where(certificate => certificate.Subject == RootSubject).ToArray();
        if (existing.Length > 1)
        {
            foreach (var item in existing) item.Dispose();
            throw new CryptographicException("发现多个教师手机控制根证书，无法安全选择。");
        }
        if (existing.Length == 1)
        {
            using var candidate = existing[0];
            using var candidateKey = candidate.GetRSAPrivateKey();
            if (candidateKey is null)
                throw new CryptographicException("发现没有私钥的教师手机控制证书；没有覆盖它。");
            settings.SetValue(ThumbprintValue, NormalizeThumbprint(candidate.Thumbprint), RegistryValueKind.String);
            return new X509Certificate2(candidate);
        }

        using var key = OpenOrCreateCngKey();
        using var rsa = new RSACng(key);
        var request = new CertificateRequest(RootSubject, rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(10));
        store.Add(created);
        using var stored = store.Certificates.Find(X509FindType.FindByThumbprint, created.Thumbprint,
            validOnly: false).OfType<X509Certificate2>().SingleOrDefault()
            ?? throw new CryptographicException("教师手机控制根证书保存后无法读回。");
        using var storedKey = stored.GetRSAPrivateKey();
        if (storedKey is null)
            throw new CryptographicException("教师手机控制根证书私钥无法从用户证书库读回。");
        settings.SetValue(ThumbprintValue, NormalizeThumbprint(stored.Thumbprint), RegistryValueKind.String);
        return new X509Certificate2(stored);
    }

    private static CngKey OpenOrCreateCngKey()
    {
        if (CngKey.Exists(KeyName, Provider))
        {
            var existing = CngKey.Open(KeyName, Provider);
            if (existing.AlgorithmGroup != CngAlgorithmGroup.Rsa || existing.KeySize != KeySize)
            {
                existing.Dispose();
                throw new CryptographicException("教师手机控制根密钥算法无效；没有覆盖已有密钥。");
            }
            return existing;
        }
        var parameters = new CngKeyCreationParameters
        {
            Provider = Provider,
            KeyUsage = CngKeyUsages.Signing,
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.None
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(KeySize), CngPropertyOptions.None));
        try { return CngKey.Create(CngAlgorithm.Rsa, KeyName, parameters); }
        catch (CryptographicException) when (CngKey.Exists(KeyName, Provider))
        {
            var existing = CngKey.Open(KeyName, Provider);
            if (existing.AlgorithmGroup == CngAlgorithmGroup.Rsa && existing.KeySize == KeySize) return existing;
            existing.Dispose();
            throw new CryptographicException("并发创建的教师手机控制根密钥算法无效。");
        }
    }

    private static string NormalizeThumbprint(string thumbprint) =>
        string.Concat(thumbprint.Where(Uri.IsHexDigit)).ToUpperInvariant();
}
