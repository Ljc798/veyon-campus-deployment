using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public enum StudentUpdateTransactionState
{
    Prepared,
    PreviousSetupPreserved,
    SetupInstallationPending,
    SetupInstalled,
    AgentStaged,
    ActivationPending,
    Committed,
    RollbackPending,
    RolledBack,
    RecoveryRequired,
    Superseded
}

public sealed class StudentUpdateLockBusyException(string message) : IOException(message)
{
}

public sealed record StudentUpdateTransactionRecord(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("campusHash")] string CampusHash,
    [property: JsonPropertyName("previousSetupVersion")] string PreviousSetupVersion,
    [property: JsonPropertyName("targetSetupVersion")] string TargetSetupVersion,
    [property: JsonPropertyName("previousAgentVersion")] string PreviousAgentVersion,
    [property: JsonPropertyName("targetAgentVersion")] string TargetAgentVersion,
    [property: JsonPropertyName("installerFileName")] string InstallerFileName,
    [property: JsonPropertyName("configurationFingerprint")] string ConfigurationFingerprint,
    [property: JsonPropertyName("manifestSha256")] string ManifestSha256,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("updatedUtc")] DateTimeOffset UpdatedUtc,
    [property: JsonPropertyName("state")] StudentUpdateTransactionState State,
    [property: JsonPropertyName("lastError")] string? LastError = null,
    [property: JsonPropertyName("handoffHelperProcessId")] int? HandoffHelperProcessId = null,
    [property: JsonPropertyName("handoffHelperStartUtcTicks")] long? HandoffHelperStartUtcTicks = null);

/// <summary>Durable recovery data for a Teacher-pushed StudentSetup and Agent update.</summary>
[SupportedOSPlatform("windows")]
public static class StudentUpdateAtomicTransaction
{
    private const int CurrentSchemaVersion = 1;
    private const long MaximumJournalBytes = 64 * 1024;
    private const string JournalFileName = "student-update-transaction.json";
    private const string ProductDirectoryName = "Veyon Campus";
    private const string AgentProductDirectoryName = "VeyonCampus";
    private const string AgentConfigurationDirectoryName = "WebsitePolicy";
    private static readonly TimeSpan[] JournalSharingRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3)
    ];
    private static readonly TimeSpan StuckHandoffHelperTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OrphanedInstallerStopTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<StudentUpdateTransactionState>(JsonNamingPolicy.CamelCase,
            allowIntegerValues: false) }
    };

    public sealed class MachineLock : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _ownsMutex;

        internal MachineLock(Mutex mutex, bool ownsMutex)
        {
            _mutex = mutex;
            _ownsMutex = ownsMutex;
        }

        public void Dispose()
        {
            if (_ownsMutex)
            {
                try { _mutex.ReleaseMutex(); }
                finally { _ownsMutex = false; }
            }
            _mutex.Dispose();
        }
    }

    public static MachineLock AcquireMachineLock(TimeSpan? timeout = null)
    {
        EnsureWindowsSystemIdentity();
        var mutex = new Mutex(initiallyOwned: false, @"Global\VeyonCampus.StudentApplicationUpdate");
        try
        {
            var wait = timeout ?? TimeSpan.Zero;
            bool owns;
            try { owns = mutex.WaitOne(wait); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns)
            {
                mutex.Dispose();
                throw new StudentUpdateLockBusyException("另一项学生端更新或恢复正在进行；本次操作未更改安装文件。");
            }
            return new MachineLock(mutex, ownsMutex: true);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public static StudentUpdateTransactionRecord Create(string configPath, Guid commandId,
        string previousSetupVersion, string targetSetupVersion, string previousAgentVersion,
        string targetAgentVersion, string installerFileName, string configurationFingerprint, string manifestSha256)
    {
        if (commandId == Guid.Empty) throw new InvalidDataException("学生更新事务编号无效。");
        _ = ApplicationReleaseClient.CompareVersions(previousSetupVersion, previousSetupVersion);
        _ = ApplicationReleaseClient.CompareVersions(targetSetupVersion, targetSetupVersion);
        _ = ApplicationReleaseClient.CompareVersions(previousAgentVersion, previousAgentVersion);
        _ = ApplicationReleaseClient.CompareVersions(targetAgentVersion, targetAgentVersion);
        ValidateInstallerFileName(installerFileName, targetSetupVersion);
        RequireHex(configurationFingerprint, "学生更新事务配置指纹");
        RequireHex(manifestSha256, "学生更新事务发行摘要");

        var campusHash = GetCampusHash(configPath);
        var existing = Read(configPath);
        if (existing is not null && existing.State is not (StudentUpdateTransactionState.Committed or
                StudentUpdateTransactionState.RolledBack or StudentUpdateTransactionState.Superseded))
            throw new InvalidDataException("检测到尚未结束的学生更新事务；必须先恢复或人工核对，不能覆盖事务记录。");
        if (existing?.State == StudentUpdateTransactionState.Superseded &&
            !TryCleanupSupersededTransaction(configPath, existing))
            throw new IOException("已被更高版本取代的学生更新事务恢复材料尚未清理；本次更新未开始。");

        var now = DateTimeOffset.UtcNow;
        var record = new StudentUpdateTransactionRecord(CurrentSchemaVersion, commandId, campusHash,
            previousSetupVersion, targetSetupVersion, previousAgentVersion, targetAgentVersion, installerFileName,
            configurationFingerprint.ToUpperInvariant(), manifestSha256.ToUpperInvariant(), now, now,
            StudentUpdateTransactionState.Prepared);
        Write(configPath, record);
        return record;
    }

    public static StudentUpdateTransactionRecord? Read(string configPath)
    {
        var journalPath = GetJournalPath(configPath);
        if (!File.Exists(journalPath)) return null;
        PathLinkSecurity.RejectLinks(journalPath);
        var journalDirectory = Path.GetDirectoryName(journalPath)!;
        var journalRoot = Path.GetDirectoryName(journalDirectory)!;
        if (!AgentFileSecurity.IsPrivate(journalRoot, directory: true) ||
            !AgentFileSecurity.IsPrivate(journalDirectory, directory: true) ||
            !AgentFileSecurity.IsPrivate(journalPath, directory: false))
            throw new InvalidDataException("学生更新事务 journal 的 SYSTEM/Administrators ACL 无效；已保留恢复材料。");
        var journalBytes = ReadJournalBytes(journalPath);
        if (journalBytes.LongLength is < 1 or > MaximumJournalBytes)
            throw new InvalidDataException("学生更新事务记录大小无效；已保留恢复材料并停止。");

        StudentUpdateTransactionRecord record;
        try
        {
            record = JsonSerializer.Deserialize<StudentUpdateTransactionRecord>(journalBytes, JsonOptions)
                     ?? throw new InvalidDataException("学生更新事务记录为空；已保留恢复材料并停止。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("学生更新事务记录格式无效；已保留恢复材料并停止。", exception);
        }
        ValidateRecord(configPath, record);
        return record;
    }

    public static StudentUpdateTransactionStatus ReadStatus(string configPath, Guid commandId)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("学生更新命令编号无效。", nameof(commandId));
        try
        {
            var record = Read(configPath);
            if (record is null || record.CommandId != commandId)
                return new StudentUpdateTransactionStatus(commandId, "notFound");
            var state = record.State switch
            {
                StudentUpdateTransactionState.Committed => "committed",
                StudentUpdateTransactionState.RolledBack => "rolledBack",
                StudentUpdateTransactionState.Superseded => "superseded",
                StudentUpdateTransactionState.RecoveryRequired => "needsReview",
                _ => "inProgress"
            };
            return new StudentUpdateTransactionStatus(commandId, state, record.PreviousSetupVersion,
                record.TargetSetupVersion, record.PreviousAgentVersion, record.TargetAgentVersion);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or InvalidDataException or
                                          JsonException or ArgumentException)
        {
            // A corrupt or inaccessible journal is never reported as success. Preserve it for recovery.
            return new StudentUpdateTransactionStatus(commandId, "needsReview");
        }
    }

    public static string ReadCurrentState(string configPath)
    {
        try
        {
            var record = Read(configPath);
            return record?.State switch
            {
                null => "notFound",
                StudentUpdateTransactionState.Committed => "committed",
                StudentUpdateTransactionState.RolledBack => "rolledBack",
                StudentUpdateTransactionState.Superseded => "superseded",
                StudentUpdateTransactionState.RecoveryRequired => "needsReview",
                _ => "inProgress"
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or InvalidDataException or
                                          JsonException or ArgumentException)
        {
            return "needsReview";
        }
    }

    public static StudentUpdateTransactionRecord Advance(string configPath,
        StudentUpdateTransactionRecord current, StudentUpdateTransactionState next, string? detail = null)
    {
        ValidateRecord(configPath, current);
        var persisted = Read(configPath);
        if (!IsCurrentRecord(current, persisted))
            throw new InvalidOperationException("学生更新事务记录已变化；拒绝使用旧阶段覆盖 journal。");
        if (!IsTransitionAllowed(current.State, next))
            throw new InvalidOperationException($"学生更新事务状态不能从 {current.State} 转为 {next}。");
        var updated = current with
        {
            State = next,
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastError = detail is null ? current.LastError : Truncate(detail, 4000)
        };
        Write(configPath, updated);
        return updated;
    }

    public static StudentUpdateTransactionRecord RebaseTargetAgentVersion(string configPath,
        StudentUpdateTransactionRecord current, string expectedCurrentTargetAgentVersion,
        string verifiedTargetAgentVersion)
    {
        ArgumentNullException.ThrowIfNull(current);
        _ = ApplicationReleaseClient.CompareVersions(expectedCurrentTargetAgentVersion,
            expectedCurrentTargetAgentVersion);
        _ = ApplicationReleaseClient.CompareVersions(verifiedTargetAgentVersion, verifiedTargetAgentVersion);
        ValidateRecord(configPath, current);
        var persisted = Read(configPath);
        if (!IsCurrentRecord(current, persisted))
            throw new InvalidOperationException("学生更新事务记录已变化；拒绝重设 Agent 目标版本。");
        if (!CanRebaseTargetAgentVersion(current, expectedCurrentTargetAgentVersion,
                verifiedTargetAgentVersion))
            throw new InvalidDataException("只能在 StudentSetup 安装后，将事务中的 Agent 目标重设为已验真的内置 Agent 版本，且不得降级。");

        var updated = current with
        {
            TargetAgentVersion = verifiedTargetAgentVersion,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        Write(configPath, updated);
        return updated;
    }

    public static StudentUpdateTransactionRecord RebaseLegacySetupVersionAgentAlias(string configPath,
        StudentUpdateTransactionRecord current, string expectedSetupVersionAlias,
        string verifiedAgentProductVersion)
    {
        ArgumentNullException.ThrowIfNull(current);
        _ = ApplicationReleaseClient.CompareVersions(expectedSetupVersionAlias, expectedSetupVersionAlias);
        _ = ApplicationReleaseClient.CompareVersions(verifiedAgentProductVersion, verifiedAgentProductVersion);
        ValidateRecord(configPath, current);
        var persisted = Read(configPath);
        if (!IsCurrentRecord(current, persisted))
            throw new InvalidOperationException("学生更新事务记录已变化；拒绝迁移旧版 Setup/Agent 版本别名。");
        if (!CanRebaseLegacySetupVersionAgentAlias(current, expectedSetupVersionAlias,
                verifiedAgentProductVersion))
            throw new InvalidDataException("旧版 Agent 事务不符合 Setup/Agent 版本别名迁移条件；已停止切换。");
        var updated = current with
        {
            TargetAgentVersion = verifiedAgentProductVersion,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        Write(configPath, updated);
        return updated;
    }

    internal static bool CanRebaseLegacySetupVersionAgentAlias(StudentUpdateTransactionRecord current,
        string expectedSetupVersionAlias, string verifiedAgentProductVersion)
    {
        if (current.State != StudentUpdateTransactionState.ActivationPending ||
            !string.Equals(current.TargetSetupVersion, expectedSetupVersionAlias, StringComparison.Ordinal) ||
            !string.Equals(current.TargetAgentVersion, expectedSetupVersionAlias, StringComparison.Ordinal))
            return false;
        try
        {
            return ApplicationReleaseClient.CompareVersions(verifiedAgentProductVersion,
                       current.PreviousAgentVersion) >= 0 &&
                   !string.Equals(verifiedAgentProductVersion, expectedSetupVersionAlias, StringComparison.Ordinal);
        }
        catch (InvalidDataException) { return false; }
    }

    internal static bool CanRebaseTargetAgentVersion(StudentUpdateTransactionRecord current,
        string expectedCurrentTargetAgentVersion, string verifiedTargetAgentVersion)
    {
        if (current.State != StudentUpdateTransactionState.SetupInstalled ||
            !string.Equals(current.TargetAgentVersion, expectedCurrentTargetAgentVersion,
                StringComparison.Ordinal))
            return false;
        try
        {
            return ApplicationReleaseClient.CompareVersions(verifiedTargetAgentVersion,
                       current.PreviousAgentVersion) >= 0 &&
                   ApplicationReleaseClient.CompareVersions(verifiedTargetAgentVersion,
                       current.TargetAgentVersion) >= 0;
        }
        catch (InvalidDataException) { return false; }
    }

    public static StudentUpdateTransactionRecord RecordRecoveryFailure(string configPath,
        StudentUpdateTransactionRecord current, Exception exception)
    {
        var persisted = Read(configPath);
        if (persisted is null || persisted.CommandId != current.CommandId)
            throw new InvalidOperationException("学生更新事务记录缺失或命令已变化；不能记录恢复状态。");
        if (persisted.State is StudentUpdateTransactionState.Committed or StudentUpdateTransactionState.RolledBack or
            StudentUpdateTransactionState.Superseded)
            return persisted;
        if (!IsCurrentRecord(current, persisted))
            throw new InvalidOperationException("学生更新事务记录已变化；不能覆盖更新后的恢复状态。");
        current = persisted;
        if (current.State != StudentUpdateTransactionState.RecoveryRequired)
            current = Advance(configPath, current, StudentUpdateTransactionState.RecoveryRequired,
                exception.ToString());
        else
        {
            current = current with { UpdatedUtc = DateTimeOffset.UtcNow, LastError = Truncate(exception.ToString(), 4000) };
            Write(configPath, current);
        }
        return current;
    }

    public static string GetInstallDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductDirectoryName, "Student");

    public static string GetPreviousInstallationDirectory(StudentUpdateTransactionRecord record) =>
        Path.Combine(GetRecoveryDirectory(record), "Previous");

    public static string GetFailedInstallationDirectory(StudentUpdateTransactionRecord record) =>
        Path.Combine(GetRecoveryDirectory(record), "FailedCandidate");

    public static string GetRecoveryDirectory(StudentUpdateTransactionRecord record) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductDirectoryName,
            "UpdateRecovery", "Student", record.CommandId.ToString("N"));

    public static string GetAgentConfigurationDirectory(string configPath) =>
        Path.GetDirectoryName(Path.GetFullPath(ValidateConfigPath(configPath)))!;

    public static void EnsureNoActiveTransaction(string configPath)
    {
        var record = Read(configPath);
        if (record is not null && record.State is not (StudentUpdateTransactionState.Committed or
                StudentUpdateTransactionState.RolledBack or StudentUpdateTransactionState.Superseded))
            throw new InvalidDataException($"学生端存在未完成的更新事务（{record.State}）；已阻止新更新以避免覆盖恢复数据。");
    }

    public static void PreservePreviousInstallation(string configPath, StudentUpdateTransactionRecord record)
    {
        _ = GetCampusHash(configPath);
        var installDirectory = GetInstallDirectory();
        var recoveryDirectory = GetRecoveryDirectory(record);
        PathLinkSecurity.RejectLinks(installDirectory);
        EnsureNoReparsePointsInTree(installDirectory);
        if (!Directory.Exists(installDirectory) ||
            !string.Equals(ReadInstalledVersion(installDirectory), record.PreviousSetupVersion,
                StringComparison.Ordinal))
            throw new InvalidDataException("移动 StudentSetup 前旧版角色与事务记录不匹配；安装目录保持原位。");
        var previousSetupExecutable = Path.Combine(installDirectory, "VeyonCampus.StudentSetup.exe");
        if (!File.Exists(previousSetupExecutable) ||
            !WorkerInstallationGuard.ProductVersionMatches(previousSetupExecutable, record.PreviousSetupVersion))
            throw new InvalidDataException("移动 StudentSetup 前旧版程序与角色版本读回不匹配；安装目录保持原位。");
        WebsitePolicyAgentInstaller.ValidatePreviousAgentRecoveryMaterial(configPath, record.PreviousAgentVersion,
            record.ConfigurationFingerprint);

        var previousDirectory = GetPreviousInstallationDirectory(record);
        if (Directory.Exists(previousDirectory))
            throw new IOException("学生更新恢复目录已存在；没有覆盖旧恢复资料。");
        CreateProtectedDirectory(Path.GetDirectoryName(recoveryDirectory)!);
        CreateProtectedDirectory(recoveryDirectory);
        Directory.Move(installDirectory, previousDirectory);
        PathLinkSecurity.RejectLinks(previousDirectory);
        EnsureNoReparsePointsInTree(previousDirectory);
        if (!string.Equals(ReadInstalledVersion(previousDirectory), record.PreviousSetupVersion,
                StringComparison.Ordinal) ||
            !WorkerInstallationGuard.ProductVersionMatches(
                Path.Combine(previousDirectory, "VeyonCampus.StudentSetup.exe"), record.PreviousSetupVersion))
            throw new InvalidDataException("保存旧 StudentSetup 后角色或程序版本读回不匹配；恢复材料仍保留。");
    }

    public static void RestorePreviousInstallation(string configPath, StudentUpdateTransactionRecord record)
    {
        _ = GetCampusHash(configPath);
        var installDirectory = GetInstallDirectory();
        var previousDirectory = GetPreviousInstallationDirectory(record);
        if (!Directory.Exists(previousDirectory))
        {
            if (Directory.Exists(installDirectory) && ReadInstalledVersion(installDirectory) == record.PreviousSetupVersion)
                return;
            throw new IOException("旧 StudentSetup 恢复目录缺失，且活动安装不是已核验的旧版本。");
        }

        PathLinkSecurity.RejectLinks(previousDirectory);
        EnsureNoReparsePointsInTree(previousDirectory);
        var recoveryDirectory = GetRecoveryDirectory(record);
        var failedDirectory = GetFailedInstallationDirectory(record);
        if (Directory.Exists(failedDirectory))
            failedDirectory = Path.Combine(recoveryDirectory, "FailedCandidate-" + Guid.NewGuid().ToString("N"));

        if (Directory.Exists(installDirectory))
        {
            PathLinkSecurity.RejectLinks(installDirectory);
            EnsureNoReparsePointsInTree(installDirectory);
            Directory.Move(installDirectory, failedDirectory);
        }
        else if (File.Exists(installDirectory))
        {
            throw new IOException("StudentSetup 活动路径被非目录文件占用；旧版仍安全保留在恢复目录。");
        }

        Directory.Move(previousDirectory, installDirectory);
        PathLinkSecurity.RejectLinks(installDirectory);
        EnsureNoReparsePointsInTree(installDirectory);
        var restoredVersion = ReadInstalledVersion(installDirectory);
        if (!string.Equals(restoredVersion, record.PreviousSetupVersion, StringComparison.Ordinal))
            throw new InvalidDataException("StudentSetup 旧版恢复后的角色或版本读回不匹配。");
    }

    public static bool IsTargetSetupInstalled(StudentUpdateTransactionRecord record)
    {
        try { return ReadInstalledVersion(GetInstallDirectory()) == record.TargetSetupVersion; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                           JsonException or ArgumentException)
        { return false; }
    }

    /// <summary>
    /// Allows the installed StudentSetup to repair its bundled Agent when an older failed
    /// transaction cannot roll back because its backup is gone, but a verified later Setup is
    /// already active. This is deliberately narrower than normal transaction recovery.
    /// </summary>
    public static bool CanBootstrapSupersededRecovery(string configPath,
        StudentUpdateTransactionRecord record, string configurationFingerprint, string? installedAgentVersion)
        => CanBootstrapSupersededRecovery(configPath, record, configurationFingerprint, installedAgentVersion,
            out _);

    public static bool CanBootstrapSupersededRecovery(string configPath,
        StudentUpdateTransactionRecord record, string configurationFingerprint, string? installedAgentVersion,
        out string refusalReason)
    {
        if (!OperatingSystem.IsWindows() || record.State != StudentUpdateTransactionState.RecoveryRequired ||
            installedAgentVersion is null ||
            !string.Equals(installedAgentVersion, record.PreviousAgentVersion, StringComparison.Ordinal))
        {
            refusalReason = "当前 SYSTEM Agent 版本或校区配置指纹与失败事务不一致。";
            return false;
        }

        string installedSetupVersion;
        try
        {
            if (!string.Equals(GetExpectedConfigurationFingerprintForAgent(configPath, record,
                    installedAgentVersion), configurationFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                refusalReason = "当前 SYSTEM Agent 健康指纹与受信任配置的匹配结果不一致。";
                return false;
            }
            installedSetupVersion = ReadVerifiedInstalledSetupVersion();
            var previousSetup = GetPreviousInstallationDirectory(record);
            PathLinkSecurity.RejectLinksAllowingMissingComponents(previousSetup);
            var previousSetupBackupPresent = Directory.Exists(previousSetup) || File.Exists(previousSetup);
            if (!HasSupersedingInstallationFacts(record, installedSetupVersion, installedAgentVersion,
                    previousSetupBackupPresent))
            {
                refusalReason = GetSupersedingVersionFenceFailure(record, installedSetupVersion,
                    installedAgentVersion, previousSetupBackupPresent);
                return false;
            }

            var previousAgent = GetAgentExecutablePath(record.PreviousAgentVersion);
            PathLinkSecurity.RejectLinks(previousAgent);
            if (!File.Exists(previousAgent) ||
                !WorkerInstallationGuard.ProductVersionMatches(previousAgent, record.PreviousAgentVersion))
            {
                refusalReason = "事务记录中的旧 Agent 文件缺失或版本不符，无法建立可信回滚点。";
                return false;
            }

            var recoveryDirectory = GetRecoveryDirectory(record);
            var stagingDirectory = Path.GetDirectoryName(GetInstallerPath(configPath, record))!;
            VerifyOrRepairOptionalPrivateDirectory(recoveryDirectory);
            VerifyOrRepairOptionalPrivateDirectory(stagingDirectory);
            refusalReason = "";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException or
                                           System.Security.SecurityException)
        {
            refusalReason = "无法安全核验旧更新事务材料：" + exception.Message;
            return false;
        }
    }

    internal static bool HasSupersedingInstallationFacts(StudentUpdateTransactionRecord record,
        string installedSetupVersion, string installedAgentVersion, bool previousSetupBackupPresent)
    {
        return record.State == StudentUpdateTransactionState.RecoveryRequired &&
               ApplicationReleaseClient.CompareVersions(record.TargetSetupVersion, record.PreviousSetupVersion) > 0 &&
               ApplicationReleaseClient.CompareVersions(record.TargetAgentVersion, record.PreviousAgentVersion) > 0 &&
               ApplicationReleaseClient.CompareVersions(installedSetupVersion, record.TargetSetupVersion) > 0 &&
               ApplicationReleaseClient.CompareVersions(installedAgentVersion, record.PreviousAgentVersion) >= 0 &&
               !previousSetupBackupPresent;
    }

    private static string GetSupersedingVersionFenceFailure(StudentUpdateTransactionRecord record,
        string installedSetupVersion, string installedAgentVersion, bool previousSetupBackupPresent)
    {
        if (ApplicationReleaseClient.CompareVersions(record.TargetSetupVersion, record.PreviousSetupVersion) <= 0)
            return "失败事务的目标 StudentSetup 版本不是高于原版本的更新。";
        if (ApplicationReleaseClient.CompareVersions(record.TargetAgentVersion, record.PreviousAgentVersion) <= 0)
            return "失败事务的目标 Agent 版本不是高于原版本的更新。";
        if (ApplicationReleaseClient.CompareVersions(installedSetupVersion, record.TargetSetupVersion) <= 0)
            return $"当前已安装 StudentSetup {installedSetupVersion} 没有高于失败事务目标 {record.TargetSetupVersion}。";
        if (ApplicationReleaseClient.CompareVersions(installedAgentVersion, record.PreviousAgentVersion) < 0)
            return $"当前 Agent {installedAgentVersion} 低于事务原版本 {record.PreviousAgentVersion}。";
        if (previousSetupBackupPresent)
            return "旧版 StudentSetup 回滚备份仍存在；应先由旧事务执行正常回滚。";
        return "当前 StudentSetup、Agent 与失败事务的版本关系不满足安全接管条件。";
    }

    private static void VerifyOrRepairOptionalPrivateDirectory(string path)
    {
        PathLinkSecurity.RejectLinksAllowingMissingComponents(path);
        if (File.Exists(path))
            throw new InvalidDataException($"学生更新事务目录路径被普通文件占用：{path}");
        if (!Directory.Exists(path)) return;
        EnsureNoReparsePointsInTree(path);
        if (!AgentFileSecurity.IsPrivate(path, directory: true))
            AgentFileSecurity.SecurePrivateDirectory(path);
        if (!AgentFileSecurity.IsPrivate(path, directory: true))
            throw new InvalidDataException($"学生更新事务目录 ACL 修复后仍不符合 SYSTEM/Administrators 独占要求：{path}");
    }

    /// <summary>Retries cleanup of a previously validated superseded transaction from an elevated UI.</summary>
    public static void EnsureSupersededRecoveryCleanup(string configPath)
    {
        var record = Read(configPath);
        if (record?.State != StudentUpdateTransactionState.Superseded) return;
        if (!TryCleanupSupersededTransaction(configPath, record))
            throw new IOException("更新事务已由更高版本接管，但恢复材料仍未清理；请稍后重试。");
    }

    private static bool TrySupersedeVerifiedRecovery(string configPath, StudentUpdateTransactionRecord record)
    {
        try
        {
            var installedSetupVersion = ReadVerifiedInstalledSetupVersion();
            var currentAgentPath = WebsitePolicyAgentInstaller.ReadManagedAgentExecutablePath(configPath);
            var currentAgentVersion = Path.GetFileName(Path.GetDirectoryName(currentAgentPath));
            if (string.IsNullOrWhiteSpace(currentAgentVersion) ||
                !HasSupersedingInstallationFacts(record, installedSetupVersion, currentAgentVersion,
                    Directory.Exists(GetPreviousInstallationDirectory(record))))
                return false;

            var expectedCurrentAgent = GetAgentExecutablePath(currentAgentVersion);
            if (!PathEquals(currentAgentPath, expectedCurrentAgent)) return false;
            PathLinkSecurity.RejectLinks(expectedCurrentAgent);
            if (!File.Exists(expectedCurrentAgent) ||
                !WorkerInstallationGuard.ProductVersionMatches(expectedCurrentAgent, currentAgentVersion) ||
                !string.Equals(WebsitePolicyAgent.GetRuntimeVersion(), currentAgentVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(WebsitePolicyAgent.ConfigFingerprint(
                    WebsitePolicyAgentInstaller.ReadVerifiedManagedAgentConfiguration(configPath)),
                    GetExpectedConfigurationFingerprintForAgent(configPath, record, currentAgentVersion),
                    StringComparison.OrdinalIgnoreCase) ||
                !WebsitePolicyAgentInstaller.WaitForUpdatedAgentHealth(TimeSpan.FromSeconds(2),
                    GetExpectedConfigurationFingerprintForAgent(configPath, record, currentAgentVersion), currentAgentVersion))
                return false;

            WebsitePolicyAgentInstaller.VerifyStudentUpdateRecoveryTasks(expectedCurrentAgent, configPath);
            var recoveryDirectory = GetRecoveryDirectory(record);
            VerifyOrRepairOptionalPrivateDirectory(recoveryDirectory);

            var superseded = Advance(configPath, record, StudentUpdateTransactionState.Superseded,
                $"保留并核验了较新的 StudentSetup {installedSetupVersion} 与健康 Agent {currentAgentVersion}；" +
                "旧更新的目标 Agent 和 StudentSetup 回滚副本均缺失，已安全结束旧事务。");
            if (!TryCleanupSupersededTransaction(configPath, superseded))
                throw new IOException("更新事务已安全收敛，但旧恢复材料尚未清理。");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException or
                                           System.Security.SecurityException or TimeoutException)
        { return false; }
    }

    private static string ReadVerifiedInstalledSetupVersion()
    {
        var installDirectory = GetInstallDirectory();
        PathLinkSecurity.RejectLinks(installDirectory);
        EnsureNoReparsePointsInTree(installDirectory);
        var version = ReadInstalledVersion(installDirectory);
        var executable = Path.Combine(installDirectory, "VeyonCampus.StudentSetup.exe");
        if (!WorkerInstallationGuard.ProductVersionMatches(executable, version))
            throw new InvalidDataException("活动 StudentSetup 角色标记与程序版本读回不匹配。");
        return version;
    }

    private static bool TryCleanupSupersededTransaction(string configPath,
        StudentUpdateTransactionRecord record)
    {
        try
        {
            // Keep the watchdog pointed at the new Agent until the terminal journal is written.
            // Any interrupted cleanup is then safe to retry using the already verified newer Agent.
            WebsitePolicyAgentInstaller.RemoveStudentUpdateRecoveryTasksForConfig(configPath);
            CleanupStagingDirectory(configPath, record);
            var recoveryDirectory = GetRecoveryDirectory(record);
            if (Directory.Exists(recoveryDirectory))
            {
                PathLinkSecurity.RejectLinks(recoveryDirectory);
                EnsureNoReparsePointsInTree(recoveryDirectory);
                Directory.Delete(recoveryDirectory, recursive: true);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.ComponentModel.Win32Exception or
                                          System.Security.SecurityException)
        {
            WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath,
                new IOException("已收敛的学生更新事务恢复材料清理尚未完成。", exception));
            return false;
        }
    }

    public static void VerifyTargetSetupInstalled(StudentUpdateTransactionRecord record)
    {
        PathLinkSecurity.RejectLinks(GetInstallDirectory());
        EnsureNoReparsePointsInTree(GetInstallDirectory());
        if (!IsTargetSetupInstalled(record))
            throw new InvalidDataException("StudentSetup 角色或目标版本未通过安装后读回。");
        var executable = Path.Combine(GetInstallDirectory(), "VeyonCampus.StudentSetup.exe");
        if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("StudentSetup 目标启动文件缺失或不是普通文件。");
    }

    public static void Commit(string configPath, StudentUpdateTransactionRecord record)
    {
        VerifyTargetSetupInstalled(record);
        var expectedAgentPath = GetAgentExecutablePath(record.TargetAgentVersion);
        var actualAgentPath = WebsitePolicyAgentInstaller.ReadManagedAgentExecutablePath(configPath);
        if (!PathEquals(actualAgentPath, expectedAgentPath))
            throw new InvalidDataException("提交前 SYSTEM Agent 任务没有指向事务目标版本。");
        var expectedFingerprint = GetExpectedConfigurationFingerprintForAgent(configPath, record,
            record.TargetAgentVersion);
        if (!WebsitePolicyAgentInstaller.WaitForUpdatedAgentHealth(TimeSpan.FromSeconds(5),
                expectedFingerprint, record.TargetAgentVersion))
            throw new TimeoutException("提交前 SYSTEM Agent 目标版本或配置指纹健康读回已失效。");
        var committed = Advance(configPath, record, StudentUpdateTransactionState.Committed);
        TryCleanupCommittedTransaction(configPath, committed);
    }

    internal static bool MatchesTrustedV140ConfigurationFingerprint(StudentUpdateTransactionRecord record,
        WebsitePolicyAgentConfig config)
    {
        if (!string.Equals(record.PreviousSetupVersion, "1.1.40", StringComparison.Ordinal) ||
            config.TeacherChannelEndpoints is { Count: > 0 } ||
            !string.IsNullOrEmpty(config.TeacherChannelRootCertificateBase64))
            return false;
        return string.Equals(record.ConfigurationFingerprint,
            WebsitePolicyAgent.LegacyV140ConfigFingerprint(config), StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    internal static string GetExpectedConfigurationFingerprintForAgent(string configPath,
        StudentUpdateTransactionRecord record, string agentVersion)
    {
        var config = WebsitePolicyAgentInstaller.ReadVerifiedManagedAgentConfiguration(configPath);
        var currentFingerprint = WebsitePolicyAgent.ConfigFingerprint(config);
        if (string.Equals(record.ConfigurationFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
            return currentFingerprint;
        if (MatchesTrustedV140ConfigurationFingerprint(record, config))
        {
            if (string.Equals(agentVersion, record.PreviousAgentVersion, StringComparison.Ordinal))
                return record.ConfigurationFingerprint;
            if (string.Equals(agentVersion, record.TargetAgentVersion, StringComparison.Ordinal))
                return currentFingerprint;
        }
        throw new InvalidDataException("更新事务配置指纹与当前受信任校区配置不匹配；未忽略或改写指纹。");
    }

    public static void Rollback(string configPath, StudentUpdateTransactionRecord record, Exception cause)
    {
        var persisted = Read(configPath);
        if (persisted is null || persisted.CommandId != record.CommandId)
            throw new InvalidOperationException("学生更新事务记录缺失或命令已变化；拒绝执行过期回滚。");
        if (persisted.State is StudentUpdateTransactionState.Committed or StudentUpdateTransactionState.RolledBack or
            StudentUpdateTransactionState.Superseded)
            return;
        record = persisted;

        try
        {
            if (record.State is not (StudentUpdateTransactionState.RollbackPending or
                    StudentUpdateTransactionState.RecoveryRequired))
                record = Advance(configPath, record, StudentUpdateTransactionState.RollbackPending, cause.ToString());
            else if (record.State == StudentUpdateTransactionState.RecoveryRequired)
                record = Advance(configPath, record, StudentUpdateTransactionState.RollbackPending, cause.ToString());

            StopOrphanedInstallerProcesses(configPath, record);
            var previousAgentPath = GetAgentExecutablePath(record.PreviousAgentVersion);
            var targetAgentPath = GetAgentExecutablePath(record.TargetAgentVersion);
            Exception? taskRestoreFailure = null;
            Exception? setupRestoreFailure = null;
            try
            {
                WebsitePolicyAgentInstaller.RestoreAgentTaskActionForTransaction(targetAgentPath,
                    previousAgentPath, configPath);
            }
            catch (Exception exception) { taskRestoreFailure = exception; }
            try { RestorePreviousInstallation(configPath, record); }
            catch (Exception exception) { setupRestoreFailure = exception; }
            if (taskRestoreFailure is not null || setupRestoreFailure is not null)
                throw new AggregateException("学生更新期间至少一项安装组件未能恢复。",
                    new[] { taskRestoreFailure, setupRestoreFailure }.OfType<Exception>());

            var previousConfigFingerprint = GetExpectedConfigurationFingerprintForAgent(configPath, record,
                record.PreviousAgentVersion);
            if (!WebsitePolicyAgentInstaller.WaitForUpdatedAgentHealth(TimeSpan.FromSeconds(2),
                    previousConfigFingerprint, record.PreviousAgentVersion))
            {
                WebsitePolicyAgentInstaller.StartCurrentAgentTask(configPath);
                if (!WebsitePolicyAgentInstaller.WaitForUpdatedAgentHealth(TimeSpan.FromSeconds(30),
                        previousConfigFingerprint, record.PreviousAgentVersion))
                    throw new TimeoutException("旧版本 SYSTEM Agent 未通过版本与配置指纹健康读回。");
            }
            if (!PathEquals(WebsitePolicyAgentInstaller.ReadManagedAgentExecutablePath(configPath), previousAgentPath))
                throw new InvalidDataException("旧 SYSTEM Agent 任务动作恢复读回不匹配。");

            record = Advance(configPath, record, StudentUpdateTransactionState.RolledBack,
                "StudentSetup 与 SYSTEM Agent 已恢复到更新前版本；" + Truncate(cause.Message, 1000));
            _ = TryCleanupRolledBackTransaction(configPath, record);
        }
        catch (Exception rollbackFailure)
        {
            var reportedRollbackFailure = rollbackFailure;
            try
            {
                RecordRecoveryFailure(configPath, record,
                    new AggregateException("学生更新失败，且联合回滚尚未通过读回。", cause, rollbackFailure));
            }
            catch (Exception recoveryStateFailure)
            {
                reportedRollbackFailure = new AggregateException("学生更新失败，且无法持久化回滚恢复状态。",
                    rollbackFailure, recoveryStateFailure);
            }
            throw new AggregateException("学生更新失败，且联合回滚尚未通过读回；事务恢复材料已保留。", cause,
                reportedRollbackFailure);
        }
    }

    public static int RecoverFromCommand(string configPath)
    {
        EnsureWindowsSystemIdentity();
        MachineLock updateLock;
        try { updateLock = AcquireMachineLock(TimeSpan.Zero); }
        catch (IOException exception) when (exception.Message.StartsWith("另一项学生端更新或恢复正在进行",
                   StringComparison.Ordinal))
        { return 0; }
        using (updateLock)
        {
            try
            {
                RecoverPending(configPath);
                return 0;
            }
            catch (Exception exception)
            {
                WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath, exception);
                return 1;
            }
        }
    }

    /// <summary>
    /// Recovers only the transaction named by an authenticated Maintenance request. The command
    /// binding is checked again after acquiring the machine lock so a stale request cannot
    /// recover a later transaction that replaced the journal between inspection and dispatch.
    /// Return codes: 0 recovered or lock busy, 1 recovery failure, 2 journal no longer matches.
    /// </summary>
    public static int RecoverSpecificFromCommand(string configPath, Guid expectedCommandId)
    {
        EnsureWindowsSystemIdentity();
        if (expectedCommandId == Guid.Empty)
            throw new ArgumentException("学生更新命令编号无效。", nameof(expectedCommandId));

        MachineLock updateLock;
        try { updateLock = AcquireMachineLock(TimeSpan.Zero); }
        catch (IOException exception) when (exception.Message.StartsWith("另一项学生端更新或恢复正在进行",
                   StringComparison.Ordinal))
        { return 0; }

        using (updateLock)
        {
            try
            {
                var current = Read(configPath);
                if (current is null || current.CommandId != expectedCommandId) return 2;
                RecoverPending(configPath);
                return 0;
            }
            catch (Exception exception)
            {
                WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath, exception);
                return 1;
            }
        }
    }

    public static void RecoverPending(string configPath)
    {
        var record = Read(configPath);
        if (record is null) return;
        if (record.State is StudentUpdateTransactionState.Committed or StudentUpdateTransactionState.RolledBack)
        {
            if (record.State == StudentUpdateTransactionState.Committed)
            {
                if (!TryCleanupCommittedTransaction(configPath, record))
                    throw new IOException("更新已提交，但受保护恢复资料清理仍未完成；暂不开始下一次更新。");
            }
            else if (!TryCleanupRolledBackTransaction(configPath, record))
                throw new IOException("更新已回滚，但受保护失败候选清理仍未完成；暂不开始下一次更新。");
            return;
        }
        if (record.State == StudentUpdateTransactionState.Superseded)
        {
            if (!TryCleanupSupersededTransaction(configPath, record))
                throw new IOException("已被更高版本取代的更新已收敛，但恢复材料清理仍待重试。");
            return;
        }

        StopStuckHandoffHelper(configPath, record);
        StopOrphanedInstallerProcesses(configPath, record);

        if (record.State == StudentUpdateTransactionState.RecoveryRequired &&
            TrySupersedeVerifiedRecovery(configPath, record))
            return;

        if (record.State == StudentUpdateTransactionState.ActivationPending && IsHandoffHelperRunning(record))
            return;

        if (record.State == StudentUpdateTransactionState.ActivationPending && IsTargetHealthy(configPath, record))
        {
            Commit(configPath, record);
            return;
        }

        var failure = new InvalidOperationException("系统启动或更新助手恢复了未提交的学生端更新事务。");
        Rollback(configPath, record, failure);
    }

    public static void RegisterRecoveryTasks(string configPath, string recoveryAgentExecutablePath) =>
        WebsitePolicyAgentInstaller.RegisterStudentUpdateRecoveryTasks(recoveryAgentExecutablePath, configPath);

    public static void RecordHandoffHelperStarted(string configPath, Guid commandId, int processId,
        long processStartUtcTicks)
    {
        if (processId <= 0 || processStartUtcTicks <= 0)
            throw new InvalidDataException("学生 Agent 更新助手进程标识无效。");
        var record = Read(configPath)
                     ?? throw new InvalidDataException("缺少学生更新事务记录。");
        if (record.CommandId != commandId || record.State != StudentUpdateTransactionState.ActivationPending)
            throw new InvalidDataException("Agent 更新助手不能绑定到非激活阶段的事务。");
        record = record with
        {
            HandoffHelperProcessId = processId,
            HandoffHelperStartUtcTicks = processStartUtcTicks,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        Write(configPath, record);
    }

    public static void RemoveForUninstall(string configPath)
    {
        var record = Read(configPath);
        if (record is null) return;
        if (record.State is not (StudentUpdateTransactionState.Committed or StudentUpdateTransactionState.RolledBack or
                StudentUpdateTransactionState.Superseded))
            throw new InvalidOperationException("学生更新事务尚未结束；卸载必须等到更新恢复或人工核对后再继续。");
        WebsitePolicyAgentInstaller.RemoveStudentUpdateRecoveryTasksForConfig(configPath);
        var recoveryDirectory = GetRecoveryDirectory(record);
        if (Directory.Exists(recoveryDirectory))
        {
            PathLinkSecurity.RejectLinks(recoveryDirectory);
            EnsureNoReparsePointsInTree(recoveryDirectory);
            Directory.Delete(recoveryDirectory, recursive: true);
        }

        var journalPath = GetJournalPath(configPath);
        if (File.Exists(journalPath)) File.Delete(journalPath);
        var campusDirectory = Path.GetDirectoryName(journalPath)!;
        if (Directory.Exists(campusDirectory) && !Directory.EnumerateFileSystemEntries(campusDirectory).Any())
            Directory.Delete(campusDirectory);
        var transactionsRoot = Path.GetDirectoryName(campusDirectory)!;
        if (Directory.Exists(transactionsRoot) && !Directory.EnumerateFileSystemEntries(transactionsRoot).Any())
            Directory.Delete(transactionsRoot);
    }

    public static string GetAgentExecutablePath(string version) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AgentProductDirectoryName,
        "WebsitePolicyAgent", version, "VeyonCampus.Agent.exe");

    public static string GetInstallerPath(string configPath, StudentUpdateTransactionRecord record)
    {
        ValidateRecord(configPath, record);
        return Path.Combine(GetAgentConfigurationDirectory(configPath),
            "student-update-" + record.CommandId.ToString("N"), record.InstallerFileName);
    }

    public static void CleanupUnclaimedStagingDirectory(string configPath, Guid commandId)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("学生更新命令编号无效。", nameof(commandId));
        var record = Read(configPath);
        if (!CanCleanupUnclaimedStagingDirectory(commandId, record?.CommandId, record?.State)) return;

        var stagingDirectory = Path.Combine(GetAgentConfigurationDirectory(configPath),
            "student-update-" + commandId.ToString("N"));
        DeleteProtectedStagingDirectory(stagingDirectory);
    }

    private static bool IsTargetHealthy(string configPath, StudentUpdateTransactionRecord record)
    {
        try
        {
            VerifyTargetSetupInstalled(record);
            var expectedAgentPath = GetAgentExecutablePath(record.TargetAgentVersion);
            if (!PathEquals(WebsitePolicyAgentInstaller.ReadManagedAgentExecutablePath(configPath), expectedAgentPath))
                return false;
            var expectedFingerprint = GetExpectedConfigurationFingerprintForAgent(configPath, record,
                record.TargetAgentVersion);
            return WebsitePolicyAgentInstaller.WaitForUpdatedAgentHealth(TimeSpan.FromSeconds(90),
                expectedFingerprint, record.TargetAgentVersion);
        }
        catch (Exception)
        { return false; }
    }

    private static bool IsHandoffHelperRunning(StudentUpdateTransactionRecord record)
    {
        if (record.HandoffHelperProcessId is not { } processId ||
            record.HandoffHelperStartUtcTicks is not { } expectedStartTicks)
            return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != expectedStartTicks) return false;
            var expectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                ProductDirectoryName, "Updater", "Student", record.TargetSetupVersion,
                "VeyonCampus.UpdateHelper.exe");
            var actualPath = process.MainModule?.FileName;
            return actualPath is not null && PathEquals(actualPath, expectedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return false; }
    }

    private static void StopStuckHandoffHelper(string configPath, StudentUpdateTransactionRecord record)
    {
        if (record.State != StudentUpdateTransactionState.ActivationPending ||
            record.HandoffHelperProcessId is not { } processId ||
            record.HandoffHelperStartUtcTicks is not { } expectedStartTicks ||
            DateTimeOffset.UtcNow - record.UpdatedUtc < StuckHandoffHelperTimeout)
            return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != expectedStartTicks) return;
            var expectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                ProductDirectoryName, "Updater", "Student", record.TargetSetupVersion,
                "VeyonCampus.UpdateHelper.exe");
            var actualPath = process.MainModule?.FileName;
            if (actualPath is null || !PathEquals(actualPath, expectedPath)) return;
            process.Kill(entireProcessTree: false);
            if (!process.WaitForExit(10_000))
                throw new TimeoutException("超时的学生 Agent 更新助手未能停止；自动恢复保留等待。 ");
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static void StopOrphanedInstallerProcesses(string configPath, StudentUpdateTransactionRecord record)
    {
        var installerPath = GetInstallerPath(configPath, record);
        var processName = Path.GetFileNameWithoutExtension(record.InstallerFileName);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || process.HasExited) continue;
                string? actualPath;
                try { actualPath = process.MainModule?.FileName; }
                catch (ArgumentException) { continue; }
                catch (InvalidOperationException) { continue; }
                catch (System.ComponentModel.Win32Exception exception)
                {
                    throw new IOException("无法核验本事务安装器进程路径；为避免并发改写，保留恢复材料。",
                        exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    throw new IOException("无法核验本事务安装器进程权限；为避免并发改写，保留恢复材料。",
                        exception);
                }

                if (actualPath is null || !PathEquals(actualPath, installerPath)) continue;
                try
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit((int)OrphanedInstallerStopTimeout.TotalMilliseconds))
                        throw new TimeoutException("本事务残留安装器未能在 30 秒内停止；旧版仍保留在恢复目录。 ");
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (System.ComponentModel.Win32Exception exception)
                {
                    throw new IOException("无法停止本事务残留安装器；旧版仍保留在恢复目录。", exception);
                }
            }
        }
    }

    private static bool TryCleanupCommittedTransaction(string configPath, StudentUpdateTransactionRecord record)
    {
        var previousAgentPath = GetAgentExecutablePath(record.PreviousAgentVersion);
        try
        {
            CleanupStagingDirectory(configPath, record);
            // Remove the recovery trigger before deleting its only rollback copy. If cleanup is
            // interrupted, RecoverPending can safely retry from the committed journal.
            WebsitePolicyAgentInstaller.RemoveStudentUpdateRecoveryTasks(previousAgentPath, configPath);
            var recoveryDirectory = GetRecoveryDirectory(record);
            if (Directory.Exists(recoveryDirectory))
            {
                PathLinkSecurity.RejectLinks(recoveryDirectory);
                EnsureNoReparsePointsInTree(recoveryDirectory);
                Directory.Delete(recoveryDirectory, recursive: true);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.ComponentModel.Win32Exception)
        {
            WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath,
                new IOException("学生更新已健康提交，但受保护恢复材料的延后清理尚未完成。", exception));
            return false;
        }
    }

    private static bool TryCleanupRolledBackTransaction(string configPath, StudentUpdateTransactionRecord record)
    {
        var previousAgentPath = GetAgentExecutablePath(record.PreviousAgentVersion);
        try
        {
            CleanupStagingDirectory(configPath, record);
            // A stale recovery task must not outlive the backup it would restore. The terminal
            // rollback journal remains available so interrupted cleanup is still idempotent.
            WebsitePolicyAgentInstaller.RemoveStudentUpdateRecoveryTasks(previousAgentPath, configPath);
            var recoveryDirectory = GetRecoveryDirectory(record);
            if (Directory.Exists(recoveryDirectory))
            {
                PathLinkSecurity.RejectLinks(recoveryDirectory);
                EnsureNoReparsePointsInTree(recoveryDirectory);
                Directory.Delete(recoveryDirectory, recursive: true);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.ComponentModel.Win32Exception)
        {
            WebsitePolicyAgentInstaller.ReportAgentStartupFailure(configPath,
                new IOException("学生更新已验证回滚，但恢复任务或失败候选的延后清理尚未完成。", exception));
            return false;
        }
    }

    public static long MeasureDirectoryBytes(string directory)
    {
        EnsureNoReparsePointsInTree(directory);
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
    }

    private static void CleanupStagingDirectory(string configPath, StudentUpdateTransactionRecord record)
    {
        var installerPath = GetInstallerPath(configPath, record);
        DeleteProtectedStagingDirectory(Path.GetDirectoryName(installerPath)!);
    }

    private static void DeleteProtectedStagingDirectory(string stagingDirectory)
    {
        if (File.Exists(stagingDirectory))
            throw new IOException("学生更新安装包暂存路径被普通文件占用；已保留恢复状态。");
        if (!Directory.Exists(stagingDirectory)) return;

        PathLinkSecurity.RejectLinks(stagingDirectory);
        EnsureNoReparsePointsInTree(stagingDirectory);
        if (!AgentFileSecurity.IsPrivate(stagingDirectory, directory: true))
            throw new InvalidDataException("学生更新安装包暂存目录的 SYSTEM/Administrators ACL 无效；已保留恢复状态。");
        Directory.Delete(stagingDirectory, recursive: true);
    }

    private static bool CanCleanupUnclaimedStagingDirectory(Guid commandId, Guid? journalCommandId,
        StudentUpdateTransactionState? journalState)
    {
        if (journalCommandId is null) return true;
        if (journalCommandId == commandId) return false;
        return journalState is StudentUpdateTransactionState.Committed or StudentUpdateTransactionState.RolledBack or
            StudentUpdateTransactionState.Superseded;
    }

    private static bool IsCurrentRecord(StudentUpdateTransactionRecord expected,
        StudentUpdateTransactionRecord? persisted) => persisted is not null && persisted == expected;

    private static void Write(string configPath, StudentUpdateTransactionRecord record)
    {
        ValidateRecord(configPath, record);
        var journalPath = GetJournalPath(configPath);
        var directory = Path.GetDirectoryName(journalPath)!;
        EnsurePrivateJournalDirectory(directory);
        var temporaryPath = journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, record, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            AgentFileSecurity.SecurePrivateFile(temporaryPath);
            ReplaceJournalWithRetry(temporaryPath, journalPath);
            AgentFileSecurity.SecurePrivateFile(journalPath);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static byte[] ReadJournalBytes(string journalPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Allow atomic replacement while a recovery task is reading the previous snapshot.
                using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                if (stream.Length is < 1 or > MaximumJournalBytes)
                    throw new InvalidDataException("学生更新事务记录大小无效；已保留恢复材料并停止。");
                using var buffer = new MemoryStream((int)stream.Length);
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (IOException exception) when (IsJournalSharingViolation(exception) &&
                                                attempt < JournalSharingRetryDelays.Length)
            {
                Thread.Sleep(JournalSharingRetryDelays[attempt]);
            }
        }
    }

    private static void ReplaceJournalWithRetry(string temporaryPath, string journalPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, journalPath, overwrite: true);
                return;
            }
            catch (IOException exception) when (IsJournalSharingViolation(exception) &&
                                                attempt < JournalSharingRetryDelays.Length)
            {
                Thread.Sleep(JournalSharingRetryDelays[attempt]);
            }
        }
    }

    private static bool IsJournalSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static string GetJournalPath(string configPath)
    {
        var campusHash = GetCampusHash(configPath);
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AgentProductDirectoryName, "StudentUpdateTransactions", campusHash);
        RejectExistingAncestors(dataRoot);
        return Path.Combine(dataRoot, JournalFileName);
    }

    private static string GetCampusHash(string configPath)
    {
        var path = ValidateConfigPath(configPath);
        var campusHash = Path.GetFileName(Path.GetDirectoryName(path));
        return campusHash!;
    }

    private static string ValidateConfigPath(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AgentProductDirectoryName, AgentConfigurationDirectoryName);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("学生更新事务配置路径不在固定 ProgramData 校区目录中。");
        var relative = Path.GetRelativePath(root, fullPath);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[0-9A-Fa-f]{24}$", RegexOptions.CultureInvariant) ||
            !string.Equals(parts[1], "agent-" + parts[0] + ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("学生更新事务配置路径格式无效。");
        PathLinkSecurity.RejectLinks(fullPath);
        return fullPath;
    }

    private static void ValidateRecord(string configPath, StudentUpdateTransactionRecord record)
    {
        if (record.SchemaVersion != CurrentSchemaVersion || record.CommandId == Guid.Empty ||
            !Enum.IsDefined(record.State) || record.CreatedUtc.Offset != TimeSpan.Zero ||
            record.UpdatedUtc.Offset != TimeSpan.Zero || record.UpdatedUtc < record.CreatedUtc ||
            (record.HandoffHelperProcessId is null) != (record.HandoffHelperStartUtcTicks is null) ||
            record.HandoffHelperProcessId is <= 0 || record.HandoffHelperStartUtcTicks is <= 0 ||
            !string.Equals(record.CampusHash, GetCampusHash(configPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("学生更新事务记录 schema、状态或校区绑定无效；已保留恢复材料。");
        _ = ApplicationReleaseClient.CompareVersions(record.PreviousSetupVersion, record.PreviousSetupVersion);
        _ = ApplicationReleaseClient.CompareVersions(record.TargetSetupVersion, record.TargetSetupVersion);
        _ = ApplicationReleaseClient.CompareVersions(record.PreviousAgentVersion, record.PreviousAgentVersion);
        _ = ApplicationReleaseClient.CompareVersions(record.TargetAgentVersion, record.TargetAgentVersion);
        ValidateInstallerFileName(record.InstallerFileName, record.TargetSetupVersion);
        RequireHex(record.ConfigurationFingerprint, "学生更新事务配置指纹");
        RequireHex(record.ManifestSha256, "学生更新事务发行摘要");
        if (record.LastError is { Length: > 4000 })
            throw new InvalidDataException("学生更新事务诊断文字超过限制；已保留恢复材料。");
    }

    private static bool IsTransitionAllowed(StudentUpdateTransactionState current,
        StudentUpdateTransactionState next)
    {
        if (current is StudentUpdateTransactionState.Committed or StudentUpdateTransactionState.RolledBack or
            StudentUpdateTransactionState.Superseded)
            return false;
        if (next == StudentUpdateTransactionState.RecoveryRequired) return true;

        return (current, next) switch
        {
            (StudentUpdateTransactionState.Prepared, StudentUpdateTransactionState.Committed) => true,
            (StudentUpdateTransactionState.Prepared, StudentUpdateTransactionState.PreviousSetupPreserved) => true,
            (StudentUpdateTransactionState.Prepared, StudentUpdateTransactionState.RollbackPending) => true,
            (StudentUpdateTransactionState.PreviousSetupPreserved, StudentUpdateTransactionState.SetupInstallationPending) => true,
            (StudentUpdateTransactionState.PreviousSetupPreserved, StudentUpdateTransactionState.RollbackPending) => true,
            (StudentUpdateTransactionState.SetupInstallationPending, StudentUpdateTransactionState.SetupInstalled) => true,
            (StudentUpdateTransactionState.SetupInstallationPending, StudentUpdateTransactionState.RollbackPending) => true,
            (StudentUpdateTransactionState.SetupInstalled, StudentUpdateTransactionState.AgentStaged) => true,
            (StudentUpdateTransactionState.SetupInstalled, StudentUpdateTransactionState.ActivationPending) => true,
            (StudentUpdateTransactionState.SetupInstalled, StudentUpdateTransactionState.Committed) => true,
            (StudentUpdateTransactionState.SetupInstalled, StudentUpdateTransactionState.RollbackPending) => true,
            (StudentUpdateTransactionState.AgentStaged, StudentUpdateTransactionState.ActivationPending) => true,
            (StudentUpdateTransactionState.AgentStaged, StudentUpdateTransactionState.RollbackPending) => true,
            (StudentUpdateTransactionState.ActivationPending, StudentUpdateTransactionState.Committed) => true,
            (StudentUpdateTransactionState.ActivationPending, StudentUpdateTransactionState.RollbackPending) => true,
            (StudentUpdateTransactionState.RollbackPending, StudentUpdateTransactionState.RolledBack) => true,
            (StudentUpdateTransactionState.RecoveryRequired, StudentUpdateTransactionState.RollbackPending) => true,
            (StudentUpdateTransactionState.RecoveryRequired, StudentUpdateTransactionState.Superseded) => true,
            _ => false
        };
    }

    private static string ReadInstalledVersion(string installDirectory)
    {
        var markerPath = Path.Combine(installDirectory, "veyon-campus-role.json");
        PathLinkSecurity.RejectLinks(markerPath);
        var executablePath = Path.Combine(installDirectory, "VeyonCampus.StudentSetup.exe");
        if (!File.Exists(markerPath) || !File.Exists(executablePath))
            throw new InvalidDataException("未找到有效 StudentSetup 角色标记或启动文件。");
        var markerInfo = new FileInfo(markerPath);
        if (markerInfo.Length is < 1 or > 4096)
            throw new InvalidDataException("StudentSetup 角色标记大小无效。");
        var marker = JsonDocument.Parse(File.ReadAllBytes(markerPath));
        using (marker)
        {
            var root = marker.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("role").GetString() != "StudentSetup" ||
                root.GetProperty("product").GetString() != "VeyonCampus.StudentSetup")
                throw new InvalidDataException("安装目录角色标记不属于受支持的 StudentSetup。");
            var version = root.GetProperty("version").GetString()
                          ?? throw new InvalidDataException("StudentSetup 版本标记为空。");
            _ = ApplicationReleaseClient.CompareVersions(version, version);
            return version;
        }
    }

    private static void EnsureNoReparsePointsInTree(string directory)
    {
        PathLinkSecurity.RejectLinks(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("StudentSetup 目录不存在。");
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                PathLinkSecurity.RejectLinks(path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
    }

    private static void CreateProtectedDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        RejectExistingAncestors(fullPath);
        Directory.CreateDirectory(fullPath);
        PathLinkSecurity.RejectLinks(fullPath);
        AgentFileSecurity.SecurePrivateDirectory(fullPath);
    }

    private static void EnsurePrivateJournalDirectory(string directory)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AgentProductDirectoryName, "StudentUpdateTransactions");
        var expectedLeaf = Path.Combine(root, GetCampusHashFromJournalDirectory(directory));
        if (!PathEquals(directory, expectedLeaf))
            throw new InvalidDataException("学生更新事务 journal 路径不符合固定目录布局。");
        CreateProtectedDirectory(root);
        CreateProtectedDirectory(expectedLeaf);
    }

    private static string GetCampusHashFromJournalDirectory(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var expectedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AgentProductDirectoryName, "StudentUpdateTransactions"));
        var prefix = Path.TrimEndingDirectorySeparator(expectedRoot) + Path.DirectorySeparatorChar;
        var relative = fullDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullDirectory[prefix.Length..]
            : "";
        if (!Regex.IsMatch(relative, "^[0-9A-Fa-f]{24}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("学生更新事务 journal 校区目录无效。");
        return relative;
    }

    private static void RejectExistingAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (current is not null)
        {
            if (File.Exists(current) || Directory.Exists(current)) PathLinkSecurity.RejectLinks(current);
            current = Path.GetDirectoryName(current);
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static void RequireHex(string value, string label)
    {
        if (!Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException(label + "格式无效。");
    }

    private static void ValidateInstallerFileName(string value, string targetVersion)
    {
        var expected = "VeyonCampus-Student-Setup-" + targetVersion + "-win-x64.exe";
        if (!string.Equals(value, expected, StringComparison.Ordinal) || value.Length > 200 ||
            Path.GetFileName(value) != value)
            throw new InvalidDataException("学生更新事务安装器文件名不符合固定发行命名。 ");
    }

    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static void EnsureWindowsSystemIdentity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("学生原子更新事务仅支持 Windows。");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18")
            throw new UnauthorizedAccessException("学生更新事务只能由 SYSTEM Agent 执行。");
    }
}
