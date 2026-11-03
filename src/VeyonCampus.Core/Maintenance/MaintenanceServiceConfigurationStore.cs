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
    DateTimeOffset UpdatedUtc,
    long ConfigurationRevision = 0);

/// <summary>
/// Stores the Maintenance Service's own teacher-channel bootstrap. The initial endpoint is
/// copied once from the protected Agent configuration; later changes require a signed v1
/// SyncTeacherEndpoint request and never replace the campus or teacher signing key.
/// </summary>
public static class MaintenanceServiceConfigurationStore
{
    public const string BootstrapFileName = "maintenance-bootstrap.json";
    public const string ConfigurationSyncRecoveryFileName = "teacher-connectivity-sync-recovery.json";
    private const int MaximumFileBytes = 64 * 1024;
    private const int MaximumRecoveryFileBytes = 128 * 1024;
    private static readonly Regex CampusDirectoryPattern = new("^[A-Fa-f0-9]{24}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private sealed record LegacyBootstrapSnapshot(int SchemaVersion, string CampusId,
        string AgentIdentityFingerprint, string TeacherSigningPublicKeyPem, IReadOnlyList<string>? Endpoints,
        string? RootCertificateBase64, string? ConfigurationFingerprint, DateTimeOffset UpdatedUtc);
    private sealed record TeacherConnectivitySyncRecoveryRecord(int SchemaVersion, Guid CommandId,
        string AgentIdentityFingerprint, long TargetRevision, string TargetConfigurationFingerprint,
        DateTimeOffset StartedUtc, bool OriginalBootstrapExisted,
        MaintenanceServiceBootstrapSnapshot? OriginalBootstrap, string OriginalAgentConfigurationBase64);
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

    [SupportedOSPlatform("windows")]
    public static bool RecoverInterruptedTeacherEndpointUpdate(string configPath)
    {
        EnsureWindowsSystemIdentity();
        var agentFingerprint = StudentAgentIdentityKeyStore.ReadFingerprint(configPath);
        var directory = MaintenanceServiceIdentityStore.GetServiceDirectory(configPath);
        EnsurePrivateDirectory(directory);
        var recoveryPath = Path.Combine(directory, ConfigurationSyncRecoveryFileName);
        if (!File.Exists(recoveryPath)) return true;

        var recovery = ReadRecoveryRecord(recoveryPath);
        if (!string.Equals(recovery.AgentIdentityFingerprint, agentFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("教师连接配置恢复记录绑定了其他 Student Agent 身份；拒绝自动恢复。");
        var originalAgentBytes = DecodeOriginalAgentConfiguration(recovery.OriginalAgentConfigurationBase64);
        var originalAgent = DeserializeAgentConfiguration(originalAgentBytes);
        var currentAgent = MaintenanceServiceIdentityStore.ReadTrustedAgentConfiguration(configPath);
        if (!WebsitePolicyAgentInstaller.ConfigurationSnapshotsShareImmutableRuntimeState(currentAgent,
                originalAgent))
            throw new InvalidDataException("教师连接配置恢复记录与当前固定校区/策略身份不一致；拒绝自动恢复。");
        ValidateRecoveryBootstrap(recovery, originalAgent, agentFingerprint);

        StudentUpdateAtomicTransaction.MachineLock updateLock;
        try { updateLock = StudentUpdateAtomicTransaction.AcquireMachineLock(TimeSpan.Zero); }
        catch (StudentUpdateLockBusyException) { return false; }
        using (updateLock)
        {
            var lockedAgentFingerprint = StudentAgentIdentityKeyStore.ReadFingerprint(configPath);
            var lockedAgent = MaintenanceServiceIdentityStore.ReadTrustedAgentConfiguration(configPath);
            if (!string.Equals(lockedAgentFingerprint, agentFingerprint, StringComparison.OrdinalIgnoreCase) ||
                !WebsitePolicyAgentInstaller.ConfigurationSnapshotsShareImmutableRuntimeState(lockedAgent,
                    originalAgent))
                throw new InvalidDataException("Student Agent 身份在配置恢复期间发生变化；保留恢复记录供人工核对。");
            var transaction = StudentUpdateAtomicTransaction.Read(configPath);
            if (transaction is not null && transaction.State is not (StudentUpdateTransactionState.Committed or
                    StudentUpdateTransactionState.RolledBack or StudentUpdateTransactionState.Superseded))
                return false;

            PathLinkSecurity.RejectLinks(configPath);
            WebsitePolicyAgentInstaller.WriteSecureConfig(configPath, originalAgentBytes);
            var bootstrapPath = Path.Combine(directory, BootstrapFileName);
            if (recovery.OriginalBootstrapExisted)
                WriteSnapshot(bootstrapPath, directory, recovery.OriginalBootstrap!);
            else if (File.Exists(bootstrapPath))
            {
                PathLinkSecurity.RejectLinks(bootstrapPath);
                File.Delete(bootstrapPath);
            }
            WebsitePolicyAgentInstaller.RestartCurrentAgentTaskForConfigurationSync(configPath,
                WebsitePolicyAgent.ConfigFingerprint(originalAgent), TimeSpan.FromSeconds(20));
            DeleteRecoveryRecord(recoveryPath);
            return true;
        }
    }

    /// <summary>
    /// Verifies the complete teacher-signed request again at the privileged boundary, then updates
    /// the Agent and Maintenance endpoint/root snapshots under the shared machine update lock.
    /// A changed teacher signing key or any other Agent identity/trust field is rejected.
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
        _ = WebsitePolicyAgentInstaller.ReadManagedAgentExecutablePath(configPath);
        var request = MaintenanceProtocolV1.VerifyRequest(signedRequestJson,
            current.TeacherSigningPublicKeyPem, current.CampusId, current.AgentIdentityFingerprint, nowUtc);
        if (request.Command != MaintenanceProtocolV1Command.SyncTeacherEndpoint ||
            request.CommandId is null || request.TeacherConfiguration is null ||
            request.TargetTeacherConfigurationFingerprint is null ||
            request.TargetTeacherConfigurationRevision is not { } targetRevision)
            throw new InvalidDataException("Maintenance 只接受带签名且绑定 revision 的教师端点同步命令。");

        using var updateLock = StudentUpdateAtomicTransaction.AcquireMachineLock(TimeSpan.Zero);
        StudentUpdateAtomicTransaction.EnsureNoActiveTransaction(configPath);
        var originalAgentConfigurationBytes = File.ReadAllBytes(configPath);
        var lockedAgentFingerprint = StudentAgentIdentityKeyStore.ReadFingerprint(configPath);
        if (!string.Equals(lockedAgentFingerprint, agentFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Student Agent 固定身份在配置同步期间发生变化；拒绝写入。");
        var originalAgentConfiguration = WebsitePolicyAgentInstaller.ReadExistingConfig(configPath)
                                         ?? throw new InvalidDataException("Student Agent 配置在同步期间消失。");
        var originalBootstrapExisted = File.Exists(path);
        if (originalBootstrapExisted)
            current = ReadAndValidate(path, originalAgentConfiguration, agentFingerprint);
        else
            current = CreateSnapshot(originalAgentConfiguration, agentFingerprint, nowUtc);
        request = MaintenanceProtocolV1.VerifyRequest(signedRequestJson,
            current.TeacherSigningPublicKeyPem, current.CampusId, current.AgentIdentityFingerprint, nowUtc);
        if (request.TargetTeacherConfigurationRevision != checked(current.ConfigurationRevision + 1))
            throw new InvalidDataException("教师连接配置 revision 已变化或不连续；拒绝覆盖新状态。");

        var endpoints = Array.AsReadOnly(request.TeacherConfiguration!.Endpoints.ToArray());
        var rootCertificate = request.TeacherConfiguration.RootCertificateBase64;
        var fingerprint = StudentAgentChannelProtocol.GetBootstrapConfigurationFingerprint(
            current.CampusId, endpoints, rootCertificate)
            ?? throw new InvalidDataException("Maintenance 教师连接配置不能为空。");
        if (!string.Equals(fingerprint, request.TargetTeacherConfigurationFingerprint,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Maintenance 教师连接配置未通过目标指纹核验。");

        var updatedAgentConfiguration = originalAgentConfiguration with
        {
            TeacherChannelEndpoints = endpoints,
            TeacherChannelRootCertificateBase64 = rootCertificate
        };
        if (!WebsitePolicyAgentInstaller.ConfigurationSnapshotsShareImmutableRuntimeState(
                originalAgentConfiguration, updatedAgentConfiguration))
            throw new InvalidDataException("教师连接同步意外改变了校区、信任根或策略配置；拒绝写入。");
        var updated = current with
        {
            SchemaVersion = 2,
            Endpoints = endpoints,
            RootCertificateBase64 = rootCertificate,
            ConfigurationFingerprint = fingerprint,
            UpdatedUtc = nowUtc.ToUniversalTime(),
            ConfigurationRevision = targetRevision
        };
        var recoveryPath = Path.Combine(directory, ConfigurationSyncRecoveryFileName);
        if (File.Exists(recoveryPath))
            throw new InvalidOperationException("已有教师连接配置恢复记录尚未处理；拒绝覆盖恢复材料。");
        var recoveryRecord = new TeacherConnectivitySyncRecoveryRecord(1, request.CommandId!.Value,
            agentFingerprint, targetRevision, fingerprint, nowUtc.ToUniversalTime(), originalBootstrapExisted,
            originalBootstrapExisted ? current : null, Convert.ToBase64String(originalAgentConfigurationBytes));

        try
        {
            WriteRecoveryRecord(recoveryPath, directory, recoveryRecord);
            PathLinkSecurity.RejectLinks(configPath);
            WebsitePolicyAgentInstaller.WriteSecureConfig(configPath,
                JsonSerializer.SerializeToUtf8Bytes(updatedAgentConfiguration, WebsitePolicyAgent.JsonOptions));
            var agentReadback = WebsitePolicyAgentInstaller.ReadExistingConfig(configPath)
                                ?? throw new InvalidDataException("Student Agent 配置写入后无法读回。");
            if (!WebsitePolicyAgentInstaller.ConfigurationSnapshotsShareImmutableRuntimeState(
                    originalAgentConfiguration, agentReadback) ||
                !string.Equals(WebsitePolicyAgent.ConfigFingerprint(agentReadback),
                    WebsitePolicyAgent.ConfigFingerprint(updatedAgentConfiguration), StringComparison.Ordinal))
                throw new InvalidDataException("Student Agent 配置写入后指纹或固定身份读回不匹配。");

            WriteSnapshot(path, directory, updated);
            WebsitePolicyAgentInstaller.RestartCurrentAgentTaskForConfigurationSync(configPath,
                WebsitePolicyAgent.ConfigFingerprint(updatedAgentConfiguration), TimeSpan.FromSeconds(20));
            DeleteRecoveryRecord(recoveryPath);
            return updated;
        }
        catch (Exception updateFailure)
        {
            try
            {
                PathLinkSecurity.RejectLinks(configPath);
                WebsitePolicyAgentInstaller.WriteSecureConfig(configPath, originalAgentConfigurationBytes);
                if (originalBootstrapExisted)
                    WriteSnapshot(path, directory, current);
                else if (File.Exists(path))
                {
                    PathLinkSecurity.RejectLinks(path);
                    File.Delete(path);
                }
                WebsitePolicyAgentInstaller.RestartCurrentAgentTaskForConfigurationSync(configPath,
                    WebsitePolicyAgent.ConfigFingerprint(originalAgentConfiguration), TimeSpan.FromSeconds(20));
                DeleteRecoveryRecordIfExists(recoveryPath);
            }
            catch (Exception rollbackFailure)
            {
                throw new IOException(
                    "教师连接配置同步失败，且 Agent 配置、Maintenance bootstrap 或 SYSTEM Agent 恢复未完成；需要人工恢复。",
                    new AggregateException(updateFailure, rollbackFailure));
            }
            throw new IOException("教师连接配置同步未能完成；已恢复 Agent 配置并重启原连接。", updateFailure);
        }
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
        return new MaintenanceServiceBootstrapSnapshot(2, config.CampusId,
            MaintenanceValidation.Fingerprint(agentFingerprint, nameof(agentFingerprint)), canonicalTeacherKey,
            endpoints, config.TeacherChannelRootCertificateBase64,
            StudentAgentChannelProtocol.GetBootstrapConfigurationFingerprint(config.CampusId, endpoints,
                config.TeacherChannelRootCertificateBase64), nowUtc.ToUniversalTime(), 0);
    }

    private static TeacherConnectivitySyncRecoveryRecord ReadRecoveryRecord(string path)
    {
        PathLinkSecurity.RejectLinks(path);
        AgentFileSecurity.SecurePrivateFile(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumRecoveryFileBytes)
            throw new InvalidDataException("教师连接配置恢复记录大小无效；保留记录供人工核对。");
        var bytes = File.ReadAllBytes(path);
        PolicyJson.RejectDuplicateFields(bytes);
        var record = JsonSerializer.Deserialize<TeacherConnectivitySyncRecoveryRecord>(bytes, JsonOptions)
                     ?? throw new InvalidDataException("教师连接配置恢复记录为空。");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (!bytes.AsSpan().SequenceEqual(canonical) || record.SchemaVersion != 1 ||
            record.CommandId == Guid.Empty || record.TargetRevision <= 0 ||
            record.StartedUtc.Offset != TimeSpan.Zero || record.StartedUtc > DateTimeOffset.UtcNow.AddMinutes(2) ||
            record.TargetConfigurationFingerprint is not { Length: 64 } targetFingerprint ||
            targetFingerprint.Any(character => !Uri.IsHexDigit(character)) ||
            record.AgentIdentityFingerprint is not { Length: 64 } recordedAgentFingerprint ||
            recordedAgentFingerprint.Any(character => !Uri.IsHexDigit(character)) ||
            string.IsNullOrWhiteSpace(record.OriginalAgentConfigurationBase64) ||
            record.OriginalBootstrapExisted != (record.OriginalBootstrap is not null))
            throw new InvalidDataException("教师连接配置恢复记录字段或规范格式无效；保留记录供人工核对。");
        return record;
    }

    private static byte[] DecodeOriginalAgentConfiguration(string encoded)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException exception)
        { throw new InvalidDataException("恢复记录中的 Agent 配置编码无效。", exception); }
        if (bytes.Length is 0 or > MaximumFileBytes ||
            !string.Equals(Convert.ToBase64String(bytes), encoded, StringComparison.Ordinal))
            throw new InvalidDataException("恢复记录中的 Agent 配置大小或编码无效。");
        PolicyJson.RejectDuplicateFields(bytes);
        return bytes;
    }

    private static WebsitePolicyAgentConfig DeserializeAgentConfiguration(byte[] bytes) =>
        JsonSerializer.Deserialize<WebsitePolicyAgentConfig>(bytes, WebsitePolicyAgent.JsonOptions)
        ?? throw new InvalidDataException("恢复记录中的 Student Agent 配置为空。");

    private static void ValidateRecoveryBootstrap(TeacherConnectivitySyncRecoveryRecord recovery,
        WebsitePolicyAgentConfig originalAgent, string agentFingerprint)
    {
        if (!recovery.OriginalBootstrapExisted) return;
        var snapshot = recovery.OriginalBootstrap
                       ?? throw new InvalidDataException("教师连接配置恢复记录缺少原始 Maintenance bootstrap。");
        var publicKey = CanonicalPublicKey(snapshot.TeacherSigningPublicKeyPem);
        StudentAgentChannelProtocol.ValidateBootstrapConfiguration(snapshot.CampusId, snapshot.Endpoints,
            snapshot.RootCertificateBase64);
        var actualFingerprint = StudentAgentChannelProtocol.GetBootstrapConfigurationFingerprint(
            snapshot.CampusId, snapshot.Endpoints, snapshot.RootCertificateBase64);
        if (snapshot.SchemaVersion != 2 || snapshot.CampusId != originalAgent.CampusId ||
            snapshot.AgentIdentityFingerprint != MaintenanceValidation.Fingerprint(agentFingerprint,
                nameof(agentFingerprint)) || !PublicKeysEqual(publicKey, CanonicalPublicKey(originalAgent.PublicKeyPem)) ||
            snapshot.ConfigurationFingerprint != actualFingerprint || snapshot.ConfigurationRevision < 0 ||
            snapshot.UpdatedUtc.Offset != TimeSpan.Zero || snapshot.UpdatedUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            throw new InvalidDataException("恢复记录中的原始 Maintenance bootstrap 与 Agent 身份不匹配。");
    }

    private static void WriteRecoveryRecord(string path, string directory,
        TeacherConnectivitySyncRecoveryRecord record)
    {
        PathLinkSecurity.RejectLinks(directory);
        if (File.Exists(path)) throw new IOException("教师连接配置恢复记录已存在；拒绝覆盖。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length is 0 or > MaximumRecoveryFileBytes)
            throw new InvalidDataException("教师连接配置恢复记录超过大小限制。");
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
            if (File.Exists(path)) throw new IOException("教师连接配置恢复记录在写入期间已出现；拒绝覆盖。");
            File.Move(temporary, path);
            AgentFileSecurity.SecurePrivateFile(path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void DeleteRecoveryRecord(string path)
    {
        PathLinkSecurity.RejectLinks(path);
        AgentFileSecurity.SecurePrivateFile(path);
        File.Delete(path);
    }

    private static void DeleteRecoveryRecordIfExists(string path)
    {
        if (File.Exists(path)) DeleteRecoveryRecord(path);
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
        var canonical = snapshot.SchemaVersion == 1
            ? JsonSerializer.SerializeToUtf8Bytes(new LegacyBootstrapSnapshot(snapshot.SchemaVersion,
                snapshot.CampusId, snapshot.AgentIdentityFingerprint, snapshot.TeacherSigningPublicKeyPem,
                snapshot.Endpoints, snapshot.RootCertificateBase64, snapshot.ConfigurationFingerprint,
                snapshot.UpdatedUtc), JsonOptions)
            : JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (!bytes.AsSpan().SequenceEqual(canonical))
            throw new InvalidDataException("Maintenance 教师连接配置不是规范 JSON。");
        var canonicalTeacherKey = CanonicalPublicKey(snapshot.TeacherSigningPublicKeyPem);
        StudentAgentChannelProtocol.ValidateBootstrapConfiguration(snapshot.CampusId, snapshot.Endpoints,
            snapshot.RootCertificateBase64);
        var actualConfigurationFingerprint = StudentAgentChannelProtocol.GetBootstrapConfigurationFingerprint(
            snapshot.CampusId, snapshot.Endpoints, snapshot.RootCertificateBase64);
        if (snapshot.SchemaVersion is not (1 or 2) || snapshot.ConfigurationRevision < 0 ||
            snapshot.SchemaVersion == 1 && snapshot.ConfigurationRevision != 0 ||
            snapshot.CampusId != agentConfig.CampusId ||
            snapshot.AgentIdentityFingerprint != MaintenanceValidation.Fingerprint(agentFingerprint,
                nameof(agentFingerprint)) ||
            !PublicKeysEqual(canonicalTeacherKey, CanonicalPublicKey(agentConfig.PublicKeyPem)) ||
            snapshot.ConfigurationFingerprint != actualConfigurationFingerprint ||
            snapshot.UpdatedUtc.Offset != TimeSpan.Zero ||
            snapshot.UpdatedUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            throw new InvalidDataException("Maintenance 教师连接配置与固定校区、Agent 或教师签名身份不匹配。");
        return snapshot with { SchemaVersion = 2, TeacherSigningPublicKeyPem = canonicalTeacherKey };
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
