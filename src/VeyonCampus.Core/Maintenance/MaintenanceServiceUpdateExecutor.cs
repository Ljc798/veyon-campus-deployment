using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;

namespace VeyonCampus.Core.Maintenance;

public sealed record MaintenanceServiceUpdateOffer(
    string ResultCode,
    string Detail,
    MaintenanceServiceTransactionReadbackPayload? Payload,
    Func<Task>? StartAfterResponseSent = null);

public sealed record MaintenanceServiceUpdateTransactionRecord(
    int SchemaVersion,
    Guid OperationId,
    Guid CommandId,
    string PreviousSetupVersion,
    string TargetSetupVersion,
    string? PreviousAgentVersion,
    string? TargetAgentVersion,
    string ReleaseSha256,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string State,
    int? HelperProcessId = null,
    long? HelperProcessStartUtcTicks = null);

/// <summary>
/// Downloads only a teacher-authorized, pinned-key StudentSetup package into SYSTEM-private
/// staging. Installation is handed to the versioned UpdateHelper after the service response
/// is sent, so Inno Setup can stop this service before replacing its files.
/// </summary>
public static class MaintenanceServiceUpdateExecutor
{
    public const string StagingDirectoryName = "UpdateStaging";
    private const string TransactionFileName = "maintenance-update-transaction.json";
    private const long MaximumTransactionBytes = 32 * 1024;
    private static readonly Regex StudentSetupFileNamePattern = new(
        "^VeyonCampus-Student-Setup-(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?-win-x64\\.exe$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly HttpClient DownloadClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = TimeSpan.FromMinutes(20) };

    [SupportedOSPlatform("windows")]
    public static async Task<MaintenanceServiceUpdateOffer> PrepareOfferAsync(string configPath,
        MaintenanceProtocolV1Request request, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureSystemIdentity();
        if (request.Command != MaintenanceProtocolV1Command.OfferSignedStudentSetupUpdate ||
            request.CommandId is not { } commandId || request.TargetSetupVersion is null ||
            request.ReleaseSha256 is null || request.SignedReleaseEnvelopeJson is null ||
            request.ArtifactTransferUrl is null)
            throw new InvalidDataException("Maintenance StudentSetup 更新命令字段不完整。" );

        var serviceDirectory = MaintenanceServiceIdentityStore.GetServiceDirectory(configPath);
        var stagingDirectory = Path.Combine(serviceDirectory, StagingDirectoryName);
        Directory.CreateDirectory(stagingDirectory);
        PathLinkSecurity.RejectLinks(stagingDirectory);
        AgentFileSecurity.SecurePrivateDirectory(stagingDirectory);
        var transactionPath = Path.Combine(serviceDirectory, TransactionFileName);
        var existing = ReadTransaction(transactionPath);
        if (existing is not null && existing.CommandId == commandId)
        {
            if (!string.Equals(existing.ReleaseSha256, request.ReleaseSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.TargetSetupVersion, request.TargetSetupVersion, StringComparison.Ordinal))
                throw new InvalidDataException("已存在的 Maintenance commandId 绑定了不同发行包。" );
            var current = ReadInstalledVersions(configPath, nowUtc);
            return new MaintenanceServiceUpdateOffer(existing.State is "pending" or "inProgress"
                    ? "pending" : "succeeded",
                "已读回同一 commandId 的 Maintenance 更新事务；没有重复下载或启动安装器。",
                new MaintenanceServiceTransactionReadbackPayload(ToStudentStatus(existing),
                    current with { TransactionState = existing.State }));
        }
        if (existing is { State: "inProgress" or "needsReview" })
            throw new InvalidOperationException("已有 Maintenance 更新事务尚未安全结束；保留事务并拒绝新的安装包。" );

        ApplicationReleaseEnvelope release;
        try
        {
            var signedEnvelopeBytes = Encoding.UTF8.GetBytes(request.SignedReleaseEnvelopeJson);
            PolicyJson.RejectDuplicateFields(signedEnvelopeBytes);
            release = JsonSerializer.Deserialize<ApplicationReleaseEnvelope>(signedEnvelopeBytes, JsonOptions)
                      ?? throw new InvalidDataException("StudentSetup 签名发行清单为空。" );
            var canonicalEnvelope = JsonSerializer.SerializeToUtf8Bytes(release, JsonOptions);
            if (!signedEnvelopeBytes.AsSpan().SequenceEqual(canonicalEnvelope))
                throw new InvalidDataException("StudentSetup 签名发行清单不是规范 JSON。" );
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("StudentSetup 签名发行清单格式无效。", exception);
        }
        var apiBaseAddress = DeploymentPackageApiConfiguration.GetApiBaseAddress();
        ApplicationReleaseClient.Verify(release, ApplicationReleaseRole.StudentSetup, apiBaseAddress,
            ApplicationReleaseTrust.LoadPinnedPublicKeyPem());
        if (!string.Equals(release.Manifest.Version, request.TargetSetupVersion, StringComparison.Ordinal) ||
            !string.Equals(release.Manifest.Sha256, request.ReleaseSha256, StringComparison.OrdinalIgnoreCase) ||
            !StudentSetupFileNamePattern.IsMatch(release.Manifest.FileName))
            throw new InvalidDataException("Maintenance 更新目标版本、SHA-256 或 StudentSetup 文件名与签名发行清单不匹配。" );
        var transferUri = StudentApplicationUpdateCryptography.ValidateLocalDownloadUrl(request.ArtifactTransferUrl);
        var currentSetupVersion = StudentApplicationUpdateProcessor.TryReadInstalledStudentSetupVersion()
                                  ?? throw new InvalidDataException("无法读取当前 StudentSetup 版本；未执行更新。" );
        if (ApplicationReleaseClient.CompareVersions(release.Manifest.Version, currentSetupVersion) <= 0)
            throw new InvalidDataException("Maintenance StudentSetup 更新必须严格高于当前安装版本。" );
        ApplicationReleaseCompatibility.EnsureSupports(release.Manifest,
            WindowsApplicationPolicyStateStore.HasAnyActiveState(),
            WindowsStudentSystemPolicyAgent.HasAnyActiveState());

        var installerPath = Path.Combine(stagingDirectory, release.Manifest.FileName);
        var envelopePath = installerPath + ".release.json";
        if (File.Exists(installerPath) || Directory.Exists(installerPath) ||
            File.Exists(envelopePath) || Directory.Exists(envelopePath))
            throw new IOException("Maintenance 私有暂存目录已有未完成安装包；保留现场材料并拒绝覆盖。" );

        var installed = ReadInstalledVersions(configPath, nowUtc);
        var record = new MaintenanceServiceUpdateTransactionRecord(1, request.OperationId, commandId,
            currentSetupVersion, release.Manifest.Version, installed.AgentRuntimeVersion,
            release.Manifest.StudentAgentVersion, release.Manifest.Sha256.ToUpperInvariant(), nowUtc.ToUniversalTime(),
            nowUtc.ToUniversalTime(), "pending");
        MaintenanceServiceUpdateJournal.Write(configPath, record);
        var artifactCreated = false;
        var envelopeCreated = false;
        try
        {
            WritePrivateFile(envelopePath, canonicalEnvelope);
            envelopeCreated = true;
            using var response = await DownloadClient.GetAsync(transferUri,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength !=
                release.Manifest.SizeBytes)
                throw new HttpRequestException("教师局域网 StudentSetup 包响应状态或长度与签名发行清单不匹配。",
                    null, response.StatusCode);

            using (new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            AgentFileSecurity.SecurePrivateFile(installerPath);
            artifactCreated = true;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var destination = new FileStream(installerPath, FileMode.Open, FileAccess.Write,
                             FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > release.Manifest.SizeBytes)
                        throw new InvalidDataException("教师局域网 StudentSetup 包超过签名清单大小。" );
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
                if (total != release.Manifest.SizeBytes)
                    throw new InvalidDataException("教师局域网 StudentSetup 包未完整下载。" );
            }

            var stagedRelease = await Task.Run(() => ApplicationReleaseClient.ReadVerifiedStagedRelease(
                installerPath, ApplicationReleaseRole.StudentSetup, apiBaseAddress,
                ApplicationReleaseTrust.LoadPinnedPublicKeyPem()), cancellationToken).ConfigureAwait(false);
            if (stagedRelease != release)
                throw new InvalidDataException("Maintenance 私有暂存包验签读回与教师签名清单不一致。" );

            var pending = new MaintenanceServiceTransactionReadbackPayload(
                ToStudentStatus(record with { State = "inProgress" }),
                installed with { TransactionState = "inProgress" });
            return new MaintenanceServiceUpdateOffer("pending",
                "教师签名命令、StudentSetup 发行签名和 SHA-256 均已核验；安装由 SYSTEM UpdateHelper 接管。",
                pending, () => StartHelperAndRecordFailure(configPath, installerPath, commandId));
        }
        catch
        {
            if (artifactCreated) TryDeleteFile(installerPath);
            if (envelopeCreated) TryDeleteFile(envelopePath);
            // The helper has not been offered this package yet, so the installed tree is unchanged.
            // Keep the transaction auditable while allowing a later command to retry cleanly.
            MaintenanceServiceUpdateJournal.MarkCompleted(configPath, commandId, "rolledBack");
            throw;
        }
    }

    public static MaintenanceServiceUpdateTransactionRecord? ReadTransaction(string configPath)
    {
        var directory = MaintenanceServiceIdentityStore.GetServiceDirectory(configPath);
        return ReadTransaction(Path.Combine(directory, TransactionFileName));
    }

    [SupportedOSPlatform("windows")]
    public static StudentUpdateTransactionStatus? ReadStatus(string configPath, Guid commandId,
        DateTimeOffset nowUtc)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("Maintenance 更新 commandId 无效。", nameof(commandId));
        var record = ReadTransaction(configPath);
        if (record is null || record.CommandId != commandId) return null;
        if (record.State is "pending" or "inProgress")
        {
            if (IsHelperRunning(record))
                return ToStudentStatus(record with { State = "inProgress" });
            if (record.HelperProcessId is null && nowUtc - record.CreatedUtc < TimeSpan.FromMinutes(2))
                return ToStudentStatus(record with { State = "inProgress" });

            var installed = StudentApplicationUpdateProcessor.TryReadInstalledStudentSetupVersion();
            var installedVersions = ReadInstalledVersions(configPath, nowUtc);
            var agentVersion = installedVersions.AgentRuntimeVersion;
            var targetSetupInstalled = string.Equals(installed, record.TargetSetupVersion,
                StringComparison.Ordinal);
            var targetAgentInstalled = record.TargetAgentVersion is { } targetAgent
                ? agentVersion is not null && ApplicationReleaseClient.CompareVersions(agentVersion, targetAgent) >= 0
                : record.PreviousAgentVersion is { } previousAgent && agentVersion is not null &&
                  ApplicationReleaseClient.CompareVersions(agentVersion, previousAgent) >= 0;
            var serviceVersion = installedVersions.MaintenanceServiceVersion;
            var targetMaintenanceServiceInstalled = serviceVersion is not null &&
                ApplicationReleaseClient.CompareVersions(serviceVersion, record.TargetSetupVersion) >= 0;
            var terminalState = targetSetupInstalled && targetAgentInstalled && targetMaintenanceServiceInstalled
                ? "committed" : "needsReview";
            MaintenanceServiceUpdateJournal.MarkCompleted(configPath, commandId, terminalState);
            record = ReadTransaction(configPath) ?? throw new InvalidDataException("Maintenance 更新事务读回丢失。" );
        }
        return ToStudentStatus(record);
    }

    public static StudentUpdateTransactionStatus ToStudentStatus(MaintenanceServiceUpdateTransactionRecord record) =>
        new(record.CommandId, record.State, record.PreviousSetupVersion, record.TargetSetupVersion,
            record.PreviousAgentVersion, record.TargetAgentVersion);

    public static void TryDeleteStagedPackage(string installerPath)
    {
        var fullInstallerPath = Path.GetFullPath(installerPath);
        var envelopePath = fullInstallerPath + ".release.json";
        try { if (File.Exists(fullInstallerPath)) { PathLinkSecurity.RejectLinks(fullInstallerPath); File.Delete(fullInstallerPath); } }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException) { }
        try { if (File.Exists(envelopePath)) { PathLinkSecurity.RejectLinks(envelopePath); File.Delete(envelopePath); } }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException) { }
    }

    internal static MaintenanceServiceInstalledVersionsPayload ReadInstalledVersions(string configPath,
        DateTimeOffset nowUtc)
    {
        string? agentRuntimeVersion = null;
        string? agentAssemblyVersion = null;
        try
        {
            var path = WebsitePolicyAgentInstaller.ReadManagedAgentExecutablePath(configPath);
            PathLinkSecurity.RejectLinks(path);
            if (File.Exists(path))
            {
                agentRuntimeVersion = Path.GetFileName(Path.GetDirectoryName(path));
                if (agentRuntimeVersion is not null)
                    _ = ApplicationReleaseClient.CompareVersions(agentRuntimeVersion, agentRuntimeVersion);
                agentAssemblyVersion = WorkerLaunchContext.GetAssemblyProductVersion(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.Security.SecurityException or
                                          ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or NotSupportedException) { }
        return new MaintenanceServiceInstalledVersionsPayload(
            StudentApplicationUpdateProcessor.TryReadInstalledStudentSetupVersion(), agentRuntimeVersion,
            agentAssemblyVersion, MaintenanceServiceCommandDispatcher.GetServiceVersion(),
            StudentUpdateAtomicTransaction.ReadCurrentState(configPath), nowUtc, IsSystemIdentity(),
            agentRuntimeVersion is not null);
    }

    private static async Task StartHelperAndRecordFailure(string configPath, string installerPath, Guid commandId)
    {
        try { ApplicationReleaseUpdateHandoff.StartFromMaintenanceService(installerPath, configPath, commandId); }
        catch
        {
            MaintenanceServiceUpdateJournal.MarkCompleted(configPath, commandId, "rolledBack");
            TryDeleteStagedPackage(installerPath);
            throw;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static MaintenanceServiceUpdateTransactionRecord? ReadTransaction(string path)
    {
        if (!File.Exists(path)) return null;
        PathLinkSecurity.RejectLinksAllowingMissingComponents(path);
        AgentFileSecurity.SecurePrivateFile(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumTransactionBytes)
            throw new InvalidDataException("Maintenance 更新事务文件大小无效。" );
        var bytes = File.ReadAllBytes(path);
        PolicyJson.RejectDuplicateFields(bytes);
        var record = JsonSerializer.Deserialize<MaintenanceServiceUpdateTransactionRecord>(bytes, JsonOptions)
                     ?? throw new InvalidDataException("Maintenance 更新事务文件为空。" );
        if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions)) ||
            record.SchemaVersion != 1 || record.OperationId == Guid.Empty || record.CommandId == Guid.Empty ||
            record.CreatedUtc.Offset != TimeSpan.Zero || record.UpdatedUtc.Offset != TimeSpan.Zero ||
            record.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(2) ||
            record.State is not ("pending" or "inProgress" or "committed" or "rolledBack" or "needsReview") ||
            !Regex.IsMatch(record.ReleaseSha256, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Maintenance 更新事务记录内容无效。" );
        _ = ApplicationReleaseClient.CompareVersions(record.PreviousSetupVersion, record.PreviousSetupVersion);
        _ = ApplicationReleaseClient.CompareVersions(record.TargetSetupVersion, record.TargetSetupVersion);
        if (record.PreviousAgentVersion is not null)
            _ = ApplicationReleaseClient.CompareVersions(record.PreviousAgentVersion, record.PreviousAgentVersion);
        if (record.TargetAgentVersion is not null)
            _ = ApplicationReleaseClient.CompareVersions(record.TargetAgentVersion, record.TargetAgentVersion);
        return record;
    }

    private static bool IsHelperRunning(MaintenanceServiceUpdateTransactionRecord record)
    {
        if (record.HelperProcessId is not { } processId ||
            record.HelperProcessStartUtcTicks is not { } startTicks) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || !string.Equals(process.ProcessName, "VeyonCampus.UpdateHelper",
                    StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(process.StartTime.ToUniversalTime().Ticks - startTicks) > TimeSpan.TicksPerSecond * 2)
                return false;
            var executablePath = process.MainModule?.FileName;
            var expectedPath = StudentApplicationUpdateHandoff.GetUpdateHelperPath(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), record.PreviousSetupVersion);
            return executablePath is not null && string.Equals(Path.GetFullPath(executablePath),
                Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or NotSupportedException)
        { return false; }
    }

    private static void WritePrivateFile(string path, byte[] bytes)
    {
        PathLinkSecurity.RejectLinksAllowingMissingComponents(path);
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        AgentFileSecurity.SecurePrivateFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsSystemIdentity()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User?.Value == "S-1-5-18";
    }

    private static void EnsureSystemIdentity()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Maintenance StudentSetup 更新仅支持 Windows。" );
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18")
            throw new UnauthorizedAccessException("Maintenance StudentSetup 更新只能由 SYSTEM 服务执行。" );
    }

    internal static class MaintenanceServiceUpdateJournalStore
    {
        private const string JournalMutexName = @"Global\VeyonCampus.MaintenanceUpdateJournal";

        public static void Write(string configPath, MaintenanceServiceUpdateTransactionRecord record)
        {
            using var gate = AcquireGate();
            WriteCore(GetPath(configPath), record);
        }

        public static void MarkHelperStarted(string configPath, Guid commandId, int processId, long startTicks)
        {
            using var gate = AcquireGate();
            var path = GetPath(configPath);
            var current = RequireMatching(path, commandId);
            if (current.State is not ("pending" or "inProgress"))
                throw new InvalidDataException("Maintenance 更新事务已经进入终态；UpdateHelper 不会重新开始。" );
            WriteCore(path, current with
            {
                State = "inProgress",
                HelperProcessId = processId,
                HelperProcessStartUtcTicks = startTicks,
                UpdatedUtc = DateTimeOffset.UtcNow
            });
        }

        public static void MarkCompleted(string configPath, Guid commandId, string state)
        {
            if (state is not ("committed" or "rolledBack" or "needsReview"))
                throw new ArgumentOutOfRangeException(nameof(state));
            using var gate = AcquireGate();
            var path = GetPath(configPath);
            var current = RequireMatching(path, commandId);
            if (current.State is "committed" or "rolledBack") return;
            WriteCore(path, current with
            {
                State = state,
                HelperProcessId = null,
                HelperProcessStartUtcTicks = null,
                UpdatedUtc = DateTimeOffset.UtcNow
            });
        }

        private static MaintenanceServiceUpdateTransactionRecord RequireMatching(string path, Guid commandId)
        {
            var current = ReadTransaction(path);
            if (current is null || current.CommandId != commandId)
                throw new InvalidDataException("Maintenance 更新事务与 UpdateHelper commandId 不匹配。" );
            return current;
        }

        private static void WriteCore(string path, MaintenanceServiceUpdateTransactionRecord record)
        {
            var current = ReadTransaction(path);
            if (current is not null)
            {
                if (current.CommandId == record.CommandId)
                {
                    if (current.OperationId != record.OperationId ||
                        !string.Equals(current.PreviousSetupVersion, record.PreviousSetupVersion,
                            StringComparison.Ordinal) ||
                        !string.Equals(current.ReleaseSha256, record.ReleaseSha256,
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(current.TargetSetupVersion, record.TargetSetupVersion,
                            StringComparison.Ordinal) ||
                        !string.Equals(current.PreviousAgentVersion, record.PreviousAgentVersion,
                            StringComparison.Ordinal) ||
                        !string.Equals(current.TargetAgentVersion, record.TargetAgentVersion,
                            StringComparison.Ordinal))
                        throw new InvalidDataException("Maintenance 更新 commandId 已绑定到不同事务意图。");
                    if (current.State is "committed" or "rolledBack") return;
                }
                else if (current.State is "pending" or "inProgress" or "needsReview")
                {
                    throw new InvalidOperationException("已有 Maintenance 更新事务尚未安全结束；拒绝覆盖事务日志。");
                }
            }
            var directory = Path.GetDirectoryName(path)!;
            PathLinkSecurity.RejectLinks(directory);
            if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            if (bytes.Length is 0 or > MaximumTransactionBytes)
                throw new IOException("Maintenance 更新事务超过存储上限。" );
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
                File.Move(temporary, path, overwrite: true);
                AgentFileSecurity.SecurePrivateFile(path);
            }
            finally { TryDeleteFile(temporary); }
        }

        private static IDisposable AcquireGate()
        {
            var mutex = new Mutex(initiallyOwned: false, JournalMutexName);
            try
            {
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                        throw new IOException("Maintenance 更新事务锁等待超时。" );
                }
                catch (AbandonedMutexException) { }
                return new MutexLease(mutex);
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }

        private sealed class MutexLease(Mutex mutex) : IDisposable
        {
            public void Dispose()
            {
                try { mutex.ReleaseMutex(); }
                finally { mutex.Dispose(); }
            }
        }

        private static string GetPath(string configPath) => Path.Combine(
            MaintenanceServiceIdentityStore.GetServiceDirectory(configPath), TransactionFileName);
    }
}

/// <summary>Public bridge for the already-authorized UpdateHelper process to update the journal.</summary>
public static class MaintenanceServiceUpdateJournal
{
    public static void MarkHelperStarted(string configPath, Guid commandId, int processId, long startTicks) =>
        MaintenanceServiceUpdateExecutor.MaintenanceServiceUpdateJournalStore.MarkHelperStarted(configPath,
            commandId, processId, startTicks);

    public static void MarkCompleted(string configPath, Guid commandId, string state) =>
        MaintenanceServiceUpdateExecutor.MaintenanceServiceUpdateJournalStore.MarkCompleted(configPath, commandId, state);

    public static void Write(string configPath, MaintenanceServiceUpdateTransactionRecord record) =>
        MaintenanceServiceUpdateExecutor.MaintenanceServiceUpdateJournalStore.Write(configPath, record);
}
