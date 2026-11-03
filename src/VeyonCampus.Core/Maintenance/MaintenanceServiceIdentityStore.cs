using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeyonCampus.Core;

namespace VeyonCampus.Core.Maintenance;

public sealed record MaintenanceServiceIdentityEnrollment(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    string DeviceIdentityFingerprint,
    string MaintenanceIdentityFingerprint,
    string MaintenancePublicKeyPem,
    DateTimeOffset CreatedUtc);

public sealed record VerifiedMaintenanceServiceIdentityEnrollment(
    MaintenanceServiceIdentityEnrollment Enrollment,
    string AgentIdentityFingerprint,
    string MaintenanceIdentityFingerprint,
    string MaintenancePublicKeyPem,
    string SignedEnrollmentSha256);

/// <summary>
/// Persists the Maintenance Service's separate signing key and creates an enrollment statement
/// signed by the already pinned Student Agent identity. The teacher must verify the Agent pin
/// before it may pin the Maintenance key; this class never auto-trusts an unknown key.
/// </summary>
public static class MaintenanceServiceIdentityStore
{
    public const string EnrollmentPurpose = "VeyonCampus.Maintenance.IdentityEnrollment.v1";
    public const string IdentityFileName = "maintenance-identity.json";
    public const int MaximumEnrollmentAgeMinutes = 15;
    private const string IdentityDirectoryName = "Maintenance";
    private const int MaximumIdentityFileBytes = 32 * 1024;
    private const int MaximumEnrollmentEnvelopeBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [SupportedOSPlatform("windows")]
    public static RSA LoadOrCreateForSystemService(string configPath)
    {
        EnsureWindowsSystemIdentity();
        var config = ReadTrustedAgentConfiguration(configPath);
        WebsitePolicySigningKeyStore.ValidateCampusId(config.CampusId);
        var directory = GetIdentityDirectory(configPath);
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        AgentFileSecurity.SecurePrivateDirectory(directory);

        var path = Path.Combine(directory, IdentityFileName);
        if (File.Exists(path)) return ReadPrivateKey(path);

        using var generated = RSA.Create(2048);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new StoredIdentity(1, generated.ExportPkcs8PrivateKeyPem()), JsonOptions);
        if (bytes.Length is 0 or > MaximumIdentityFileBytes)
            throw new InvalidDataException("Maintenance 身份密钥文件大小无效。");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            // Lock the temporary file's DACL before any private-key bytes are written.
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            AgentFileSecurity.SecurePrivateFile(temporary);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            PathLinkSecurity.RejectLinks(directory);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { return ReadPrivateKey(path); }
            AgentFileSecurity.SecurePrivateFile(path);
            return ImportPrivateKey(bytes);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [SupportedOSPlatform("windows")]
    public static string CreateSignedEnrollment(string configPath, DateTimeOffset? nowUtc = null)
    {
        EnsureWindowsSystemIdentity();
        var config = ReadTrustedAgentConfiguration(configPath);
        WebsitePolicySigningKeyStore.ValidateCampusId(config.CampusId);
        using var maintenanceKey = LoadOrCreateForSystemService(configPath);
        using var agentKey = StudentAgentIdentityKeyStore.LoadOrCreateForSystemAgent(configPath);
        var maintenancePublicKey = maintenanceKey.ExportSubjectPublicKeyInfoPem();
        var enrollment = new MaintenanceServiceIdentityEnrollment(
            SchemaVersion: 1,
            Purpose: EnrollmentPurpose,
            CampusId: config.CampusId,
            DeviceIdentityFingerprint: StudentAgentIdentityKeyStore.ReadFingerprint(configPath),
            MaintenanceIdentityFingerprint: StudentAgentResponseCryptography.GetFingerprint(maintenancePublicKey),
            MaintenancePublicKeyPem: maintenancePublicKey,
            CreatedUtc: (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());
        return StudentAgentResponseCryptography.Sign(enrollment, agentKey, MaximumEnrollmentEnvelopeBytes);
    }

    public static VerifiedMaintenanceServiceIdentityEnrollment VerifyEnrollment(
        string signedEnrollmentJson,
        string pinnedAgentPublicKeyPem,
        string expectedCampusId,
        string expectedAgentIdentityFingerprint,
        DateTimeOffset nowUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        ArgumentException.ThrowIfNullOrWhiteSpace(signedEnrollmentJson);
        if (Encoding.UTF8.GetByteCount(signedEnrollmentJson) > MaximumEnrollmentEnvelopeBytes)
            throw new InvalidDataException("Maintenance 身份登记信封超过大小限制。");

        var verified = StudentAgentResponseCryptography.Verify<MaintenanceServiceIdentityEnrollment>(
            signedEnrollmentJson, pinnedAgentPublicKeyPem, MaximumEnrollmentEnvelopeBytes);
        var enrollment = verified.Payload;
        var now = nowUtc.ToUniversalTime();
        var expectedAgentFingerprint = MaintenanceValidation.Fingerprint(expectedAgentIdentityFingerprint,
            nameof(expectedAgentIdentityFingerprint));
        var signedAgentFingerprint = MaintenanceValidation.Fingerprint(verified.Fingerprint,
            nameof(verified.Fingerprint));
        var deviceFingerprint = MaintenanceValidation.Fingerprint(enrollment.DeviceIdentityFingerprint,
            nameof(enrollment.DeviceIdentityFingerprint));
        var maintenanceFingerprint = MaintenanceValidation.Fingerprint(enrollment.MaintenanceIdentityFingerprint,
            nameof(enrollment.MaintenanceIdentityFingerprint));
        using var maintenancePublicKey = RSA.Create();
        maintenancePublicKey.ImportFromPem(enrollment.MaintenancePublicKeyPem);
        var canonicalMaintenancePublicKey = maintenancePublicKey.ExportSubjectPublicKeyInfoPem();
        var actualMaintenanceFingerprint = StudentAgentResponseCryptography.GetFingerprint(
            canonicalMaintenancePublicKey);

        if (!verified.MatchesPinnedKey ||
            !string.Equals(signedAgentFingerprint, expectedAgentFingerprint, StringComparison.Ordinal) ||
            enrollment.SchemaVersion != 1 || enrollment.Purpose != EnrollmentPurpose ||
            !string.Equals(enrollment.CampusId, expectedCampusId, StringComparison.Ordinal) ||
            !string.Equals(deviceFingerprint, expectedAgentFingerprint, StringComparison.Ordinal) ||
            !string.Equals(maintenanceFingerprint, actualMaintenanceFingerprint, StringComparison.Ordinal) ||
            !string.Equals(enrollment.MaintenancePublicKeyPem, canonicalMaintenancePublicKey,
                StringComparison.Ordinal) ||
            enrollment.CreatedUtc.Offset != TimeSpan.Zero || enrollment.CreatedUtc > now.AddMinutes(2) ||
            enrollment.CreatedUtc < now.AddMinutes(-MaximumEnrollmentAgeMinutes))
            throw new InvalidDataException("Maintenance 身份登记未由当前固定 Agent 身份授权，或身份/校区/时效不匹配。");

        return new VerifiedMaintenanceServiceIdentityEnrollment(enrollment, signedAgentFingerprint,
            actualMaintenanceFingerprint, canonicalMaintenancePublicKey,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedEnrollmentJson))));
    }

    [SupportedOSPlatform("windows")]
    public static WebsitePolicyAgentConfig ReadTrustedAgentConfiguration(string configPath)
    {
        EnsureWindowsSystemIdentity();
        var fullPath = Path.GetFullPath(configPath);
        PathLinkSecurity.RejectLinks(fullPath);
        AgentFileSecurity.SecurePrivateFile(fullPath);
        var config = WebsitePolicyAgentInstaller.ReadExistingConfig(fullPath)
                     ?? throw new InvalidDataException("Maintenance 尚无可绑定的受保护校区配置。");
        WebsitePolicyAgentInstaller.VerifyConfigPathIdentity(fullPath, config);
        WebsitePolicyAgentInstaller.ValidateInstalledAgentTrustConfiguration(config);
        return config;
    }

    [SupportedOSPlatform("windows")]
    public static string GetServiceDirectory(string configPath)
    {
        _ = ReadTrustedAgentConfiguration(configPath);
        return GetIdentityDirectory(configPath);
    }

    private static string GetIdentityDirectory(string configPath)
    {
        var fullConfigPath = Path.GetFullPath(configPath);
        var fileName = Path.GetFileName(fullConfigPath);
        if (fileName.Length != 35 ||
            !fileName.StartsWith("agent-", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            !fileName.AsSpan(6, 24).ToString().All(Uri.IsHexDigit))
            throw new InvalidDataException("Maintenance 身份绑定的 Agent 配置路径无效。");
        var configDirectory = Path.GetDirectoryName(fullConfigPath)
                              ?? throw new InvalidDataException("Maintenance 身份配置目录无效。");
        return Path.Combine(configDirectory, IdentityDirectoryName);
    }

    private static RSA ReadPrivateKey(string path)
    {
        PathLinkSecurity.RejectLinks(path);
        AgentFileSecurity.SecurePrivateFile(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumIdentityFileBytes)
            throw new InvalidDataException("Maintenance 身份密钥文件大小无效。");
        return ImportPrivateKey(File.ReadAllBytes(path));
    }

    private static RSA ImportPrivateKey(byte[] bytes)
    {
        PolicyJson.RejectDuplicateFields(bytes);
        var stored = JsonSerializer.Deserialize<StoredIdentity>(bytes, JsonOptions)
                     ?? throw new InvalidDataException("Maintenance 身份密钥文件为空。");
        if (stored.SchemaVersion != 1 || string.IsNullOrWhiteSpace(stored.PrivateKeyPem))
            throw new InvalidDataException("Maintenance 身份密钥文件版本或内容无效。");
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(stored.PrivateKeyPem);
            if (key.KeySize is < 2048 or > 4096 ||
                !string.Equals(key.ExportPkcs8PrivateKeyPem(), stored.PrivateKeyPem, StringComparison.Ordinal))
                throw new InvalidDataException("Maintenance 身份私钥格式或位长无效。");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static void EnsureWindowsSystemIdentity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Maintenance 身份存储仅支持 Windows。");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18")
            throw new UnauthorizedAccessException("Maintenance 身份密钥只能由 SYSTEM 服务读取或创建。");
    }

    private sealed record StoredIdentity(int SchemaVersion, string PrivateKeyPem);
}
