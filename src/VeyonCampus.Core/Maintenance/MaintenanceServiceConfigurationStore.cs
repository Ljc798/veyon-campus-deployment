using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VeyonCampus.Core;

namespace VeyonCampus.Core.Maintenance;

public sealed record MaintenanceServiceBootstrapSnapshot(
    int SchemaVersion,
    string CampusId,
    string AgentIdentityFingerprint,
    string TeacherSigningPublicKeyPem,
    IReadOnlyList<string>? Endpoints,
    string? RootCertificateBase64,
    string? ConfigurationFingerprint,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// Stores the Maintenance Service's own teacher-channel bootstrap. The initial endpoint is
/// copied once from the protected Agent configuration; later changes require a signed v1
/// SyncTeacherEndpoint request and never replace the campus or teacher signing key.
/// </summary>
public static class MaintenanceServiceConfigurationStore
{
    public const string BootstrapFileName = "maintenance-bootstrap.json";
    private const int MaximumFileBytes = 64 * 1024;
    private static readonly Regex CampusDirectoryPattern = new("^[A-Fa-f0-9]{24}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [SupportedOSPlatform("windows")]
    public static string ResolveAgentConfigurationPath()
    {
        EnsureWindowsSystemIdentity();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "WebsitePolicy");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Maintenance 尚无受保护的校区 Agent 配置，不能建立教师连接。");
        PathLinkSecurity.RejectLinks(root);
        var directories = Directory.EnumerateDirectories(root).Take(257).ToArray();
        if (directories.Length > 256)
            throw new InvalidDataException("Maintenance 校区配置目录数量超过扫描上限；拒绝猜测活动配置。");
        var candidates = new List<string>();
        foreach (var directory in directories)
        {
            PathLinkSecurity.RejectLinks(directory);
            var campusHash = Path.GetFileName(directory);
            if (!CampusDirectoryPattern.IsMatch(campusHash)) continue;
            var candidate = Path.Combine(directory, "agent-" + campusHash + ".json");
            if (File.Exists(candidate)) candidates.Add(candidate);
        }
        if (candidates.Count != 1)
            throw new InvalidDataException(candidates.Count == 0
                ? "Maintenance 找不到唯一的 Student Agent 校区配置。"
                : "Maintenance 检测到多个 Student Agent 校区配置；须先人工核对，不会选择其中一个运行。");
        _ = MaintenanceServiceIdentityStore.ReadTrustedAgentConfiguration(candidates[0]);
        return Path.GetFullPath(candidates[0]);
    }

    [SupportedOSPlatform("windows")]
    public static MaintenanceServiceBootstrapSnapshot LoadOrInitializeForSystemService(string configPath,
        DateTimeOffset? nowUtc = null)
    {
        EnsureWindowsSystemIdentity();
        var agentConfig = MaintenanceServiceIdentityStore.ReadTrustedAgentConfiguration(configPath);
        var agentFingerprint = StudentAgentIdentityKeyStore.ReadFingerprint(configPath);
        var directory = MaintenanceServiceIdentityStore.GetServiceDirectory(configPath);
        EnsurePrivateDirectory(directory);
        var path = Path.Combine(directory, BootstrapFileName);
        if (File.Exists(path))
            return ReadAndValidate(path, agentConfig, agentFingerprint);

        var snapshot = CreateSnapshot(agentConfig, agentFingerprint, nowUtc ?? DateTimeOffset.UtcNow);
        WriteSnapshot(path, directory, snapshot);
        return snapshot;
    }

    /// <summary>
    /// Verifies the complete teacher-signed request again at the privileged boundary, then
    /// atomically changes only endpoint/root fields. A changed teacher signing key is not accepted.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static MaintenanceServiceBootstrapSnapshot ApplySignedTeacherEndpointUpdate(string configPath,
        string signedRequestJson, DateTimeOffset nowUtc)
    {
        EnsureWindowsSystemIdentity();
        var agentConfig = MaintenanceServiceIdentityStore.ReadTrustedAgentConfiguration(configPath);
        var agentFingerprint = StudentAgentIdentityKeyStore.ReadFingerprint(configPath);
        var directory = MaintenanceServiceIdentityStore.GetServiceDirectory(configPath);
        EnsurePrivateDirectory(directory);
        var path = Path.Combine(directory, BootstrapFileName);
        var current = File.Exists(path)
            ? ReadAndValidate(path, agentConfig, agentFingerprint)
            : CreateSnapshot(agentConfig, agentFingerprint, nowUtc);
        var request = MaintenanceProtocolV1.VerifyRequest(signedRequestJson,
            current.TeacherSigningPublicKeyPem, current.CampusId, current.AgentIdentityFingerprint, nowUtc);
        if (request.Command != MaintenanceProtocolV1Command.SyncTeacherEndpoint ||
            request.CommandId is null || request.TeacherConfiguration is null ||
            request.TargetTeacherConfigurationFingerprint is null)
            throw new InvalidDataException("Maintenance 只接受带签名的教师端点同步命令。");

        var endpoints = Array.AsReadOnly(request.TeacherConfiguration.Endpoints.ToArray());
        var rootCertificate = request.TeacherConfiguration.RootCertificateBase64;
        var fingerprint = StudentAgentChannelProtocol.GetBootstrapConfigurationFingerprint(
            current.CampusId, endpoints, rootCertificate)
            ?? throw new InvalidDataException("Maintenance 教师连接配置不能为空。");
        if (!string.Equals(fingerprint, request.TargetTeacherConfigurationFingerprint,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Maintenance 教师连接配置未通过目标指纹核验。");

        var updated = current with
        {
            Endpoints = endpoints,
            RootCertificateBase64 = rootCertificate,
            ConfigurationFingerprint = fingerprint,
            UpdatedUtc = nowUtc.ToUniversalTime()
        };
        WriteSnapshot(path, directory, updated);
        return updated;
    }

    private static MaintenanceServiceBootstrapSnapshot CreateSnapshot(WebsitePolicyAgentConfig config,
        string agentFingerprint, DateTimeOffset nowUtc)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(config.CampusId);
        var canonicalTeacherKey = CanonicalPublicKey(config.PublicKeyPem);
        StudentAgentChannelProtocol.ValidateBootstrapConfiguration(config.CampusId,
            config.TeacherChannelEndpoints, config.TeacherChannelRootCertificateBase64);
        var endpoints = config.TeacherChannelEndpoints is null
            ? null
            : Array.AsReadOnly(config.TeacherChannelEndpoints.ToArray());
        return new MaintenanceServiceBootstrapSnapshot(1, config.CampusId,
            MaintenanceValidation.Fingerprint(agentFingerprint, nameof(agentFingerprint)), canonicalTeacherKey,
            endpoints, config.TeacherChannelRootCertificateBase64,
            StudentAgentChannelProtocol.GetBootstrapConfigurationFingerprint(config.CampusId, endpoints,
                config.TeacherChannelRootCertificateBase64), nowUtc.ToUniversalTime());
    }

    private static MaintenanceServiceBootstrapSnapshot ReadAndValidate(string path,
        WebsitePolicyAgentConfig agentConfig, string agentFingerprint)
    {
        PathLinkSecurity.RejectLinks(path);
        AgentFileSecurity.SecurePrivateFile(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("Maintenance 教师连接配置文件大小无效。");
        var bytes = File.ReadAllBytes(path);
        PolicyJson.RejectDuplicateFields(bytes);
        var snapshot = JsonSerializer.Deserialize<MaintenanceServiceBootstrapSnapshot>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("Maintenance 教师连接配置为空。");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (!bytes.AsSpan().SequenceEqual(canonical))
            throw new InvalidDataException("Maintenance 教师连接配置不是规范 JSON。");
        var canonicalTeacherKey = CanonicalPublicKey(snapshot.TeacherSigningPublicKeyPem);
        StudentAgentChannelProtocol.ValidateBootstrapConfiguration(snapshot.CampusId, snapshot.Endpoints,
            snapshot.RootCertificateBase64);
        var actualConfigurationFingerprint = StudentAgentChannelProtocol.GetBootstrapConfigurationFingerprint(
            snapshot.CampusId, snapshot.Endpoints, snapshot.RootCertificateBase64);
        if (snapshot.SchemaVersion != 1 || snapshot.CampusId != agentConfig.CampusId ||
            snapshot.AgentIdentityFingerprint != MaintenanceValidation.Fingerprint(agentFingerprint,
                nameof(agentFingerprint)) ||
            !PublicKeysEqual(canonicalTeacherKey, CanonicalPublicKey(agentConfig.PublicKeyPem)) ||
            snapshot.ConfigurationFingerprint != actualConfigurationFingerprint ||
            snapshot.UpdatedUtc.Offset != TimeSpan.Zero ||
            snapshot.UpdatedUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            throw new InvalidDataException("Maintenance 教师连接配置与固定校区、Agent 或教师签名身份不匹配。");
        return snapshot with { TeacherSigningPublicKeyPem = canonicalTeacherKey };
    }

    private static void WriteSnapshot(string path, string directory,
        MaintenanceServiceBootstrapSnapshot snapshot)
    {
        PathLinkSecurity.RejectLinks(directory);
        if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (bytes.Length is 0 or > MaximumFileBytes)
            throw new InvalidDataException("Maintenance 教师连接配置超过大小限制。");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            AgentFileSecurity.SecurePrivateFile(temporary);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            PathLinkSecurity.RejectLinks(directory);
            if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);
            File.Move(temporary, path, overwrite: true);
            AgentFileSecurity.SecurePrivateFile(path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        AgentFileSecurity.SecurePrivateDirectory(directory);
    }

    private static string CanonicalPublicKey(string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        using var key = RSA.Create();
        key.ImportFromPem(publicKeyPem);
        if (key.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("Maintenance 教师签名密钥位长无效。");
        return key.ExportSubjectPublicKeyInfoPem();
    }

    private static bool PublicKeysEqual(string firstPem, string secondPem)
    {
        using var first = RSA.Create();
        using var second = RSA.Create();
        first.ImportFromPem(firstPem);
        second.ImportFromPem(secondPem);
        return CryptographicOperations.FixedTimeEquals(first.ExportSubjectPublicKeyInfo(),
            second.ExportSubjectPublicKeyInfo());
    }

    private static void EnsureWindowsSystemIdentity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Maintenance 配置仅支持 Windows。");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18")
            throw new UnauthorizedAccessException("Maintenance 教师连接配置只能由 SYSTEM 服务读取或写入。");
    }
}
