using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;

namespace VeyonCampus.Core.Maintenance;

public sealed record MaintenanceServiceReleaseUpdateTransaction(
    int SchemaVersion,
    Guid OperationId,
    Guid CommandId,
    string PreviousVersion,
    string TargetVersion,
    string ReleaseSha256,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string State,
    int? HelperProcessId = null,
    long? HelperProcessStartUtcTicks = null);

public sealed record MaintenanceServiceReleaseUpdateOffer(
    string ResultCode,
    string Detail,
    MaintenanceServiceReleaseUpdateReadbackPayload? Payload,
    Func<Task>? StartAfterResponseSent = null);

/// <summary>Accepts and stages only a signed MaintenanceService release offered by the pinned teacher.</summary>
public static class MaintenanceServiceReleaseUpdateExecutor
{
    public const string TransactionFileName = "maintenance-service-release-transaction.json";
    private const string StagingDirectoryName = "UpdateStaging";
    private const long MaximumTransactionBytes = 16L * 1024;
    private const long MaximumArtifactBytes = 512L * 1024 * 1024;
    private static readonly Regex FileNamePattern = new(
        "^VeyonCampus-Maintenance-Setup-(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?-win-x64\\.exe$",
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
    public static async Task<MaintenanceServiceReleaseUpdateOffer> PrepareOfferAsync(string configPath,
        MaintenanceProtocolV1Request request, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureSystemIdentity();
        if (request.Command != MaintenanceProtocolV1Command.OfferSignedMaintenanceServiceUpdate ||
            request.CommandId is not { } commandId || request.TargetMaintenanceServiceVersion is null ||
            request.ReleaseSha256 is null || request.SignedReleaseEnvelopeJson is null ||
            request.ArtifactTransferUrl is null)
            throw new InvalidDataException("MaintenanceService 更新命令字段不完整。");

        var configPathFull = MaintenanceServiceConfigurationStore.ResolveAgentConfigurationPath();
        EnsureSamePath(configPathFull, configPath, "Maintenance 更新配置路径不是当前唯一可信 Agent 配置。");
        var serviceDirectory = MaintenanceServiceIdentityStore.GetServiceDirectory(configPathFull);
        var stagingDirectory = Path.Combine(serviceDirectory, StagingDirectoryName);
        Directory.CreateDirectory(stagingDirectory);
        PathLinkSecurity.RejectLinks(stagingDirectory);
        AgentFileSecurity.SecurePrivateDirectory(stagingDirectory);

        var existing = ReadTransaction(configPathFull);
        if (existing is not null && existing.CommandId == commandId)
        {
            if (existing.OperationId != request.OperationId ||
                !string.Equals(existing.TargetVersion, request.TargetMaintenanceServiceVersion, StringComparison.Ordinal) ||
                !string.Equals(existing.ReleaseSha256, request.ReleaseSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("已存在的 MaintenanceService commandId 绑定了不同发行包。");
            return new MaintenanceServiceReleaseUpdateOffer(
                existing.State is "pending" or "inProgress" ? "pending" :
                existing.State == "needsReview" ? "needsReview" : "succeeded",
                "已读回同一 commandId 的 MaintenanceService 更新事务；没有重复下载或启动安装器。",
                CreatePayload(configPathFull, existing, nowUtc));
        }
        if (existing is { State: "pending" or "inProgress" or "needsReview" })
            throw new InvalidOperationException("已有 MaintenanceService 更新事务未安全结束；拒绝覆盖事务或启动第二个安装器。");

        var signedEnvelopeBytes = Encoding.UTF8.GetBytes(request.SignedReleaseEnvelopeJson);
        PolicyJson.RejectDuplicateFields(signedEnvelopeBytes);
        ApplicationReleaseEnvelope release;
        try
        {
            release = JsonSerializer.Deserialize<ApplicationReleaseEnvelope>(signedEnvelopeBytes, JsonOptions)
                      ?? throw new InvalidDataException("MaintenanceService 签名发行清单为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("MaintenanceService 签名发行清单格式无效。", exception);
        }
        var canonicalEnvelope = JsonSerializer.SerializeToUtf8Bytes(release, JsonOptions);
        if (!signedEnvelopeBytes.AsSpan().SequenceEqual(canonicalEnvelope))
            throw new InvalidDataException("MaintenanceService 签名发行清单不是规范 JSON。");

        var apiBaseAddress = DeploymentPackageApiConfiguration.GetApiBaseAddress();
        ApplicationReleaseClient.Verify(release, ApplicationReleaseRole.MaintenanceService, apiBaseAddress,
            ApplicationReleaseTrust.LoadPinnedPublicKeyPem());
        if (release.Manifest.MaintenanceProtocolVersion != MaintenanceProtocolV1.CurrentVersion)
            throw new InvalidDataException("候选 MaintenanceService 与当前已支持维护协议不兼容；拒绝切换。");
        if (!string.Equals(release.Manifest.Version, request.TargetMaintenanceServiceVersion,
                StringComparison.Ordinal) ||
            !string.Equals(release.Manifest.Sha256, request.ReleaseSha256, StringComparison.OrdinalIgnoreCase) ||
            !FileNamePattern.IsMatch(release.Manifest.FileName) || release.Manifest.SizeBytes > MaximumArtifactBytes)
            throw new InvalidDataException("MaintenanceService 目标版本、SHA-256 或发行文件名与签名清单不匹配。");

        var currentVersion = MaintenanceServiceCommandDispatcher.GetServiceVersion();
        var studentTransactionState = StudentUpdateAtomicTransaction.ReadCurrentState(configPathFull);
        if (studentTransactionState is "inProgress" or "needsReview")
            throw new InvalidOperationException("StudentSetup/Agent 更新事务正在处理或需要恢复；暂不启动 MaintenanceService 更新。");
        if (ApplicationReleaseClient.CompareVersions(release.Manifest.Version, currentVersion) <= 0)
            throw new InvalidDataException("MaintenanceService 更新目标必须严格高于当前服务版本。");
        var transferUri = StudentApplicationUpdateCryptography.ValidateLocalDownloadUrl(request.ArtifactTransferUrl);
        var installerPath = Path.Combine(stagingDirectory, release.Manifest.FileName);
        var envelopePath = installerPath + ".release.json";
        if (File.Exists(installerPath) || Directory.Exists(installerPath) ||
            File.Exists(envelopePath) || Directory.Exists(envelopePath))
            throw new IOException("SYSTEM 私有暂存目录已有未完成 MaintenanceService 安装包；保留现场并拒绝覆盖。");

        var record = new MaintenanceServiceReleaseUpdateTransaction(1, request.OperationId, commandId,
            currentVersion, release.Manifest.Version, release.Manifest.Sha256.ToUpperInvariant(),
            nowUtc.ToUniversalTime(), nowUtc.ToUniversalTime(), "pending");
        WriteTransaction(configPathFull, record);
        var artifactCreated = false;
        var envelopeCreated = false;
        try
        {
            envelopeCreated = true;
            WritePrivateFile(envelopePath, canonicalEnvelope);
            using var response = await DownloadClient.GetAsync(transferUri,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Content.Headers.ContentLength != release.Manifest.SizeBytes)
                throw new HttpRequestException("MaintenanceService 局域网包响应状态或长度与签名清单不符。",
                    null, response.StatusCode);

            using (new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            artifactCreated = true;
            AgentFileSecurity.SecurePrivateFile(installerPath);
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
                        throw new InvalidDataException("MaintenanceService 局域网包超过签名清单大小。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
                if (total != release.Manifest.SizeBytes)
                    throw new InvalidDataException("MaintenanceService 局域网包未完整下载。");
            }
            var stagedRelease = await Task.Run(() => ApplicationReleaseClient.ReadVerifiedStagedRelease(
                installerPath, ApplicationReleaseRole.MaintenanceService, apiBaseAddress,
                ApplicationReleaseTrust.LoadPinnedPublicKeyPem()), cancellationToken).ConfigureAwait(false);
            if (stagedRelease != release)
                throw new InvalidDataException("MaintenanceService 暂存包验签读回与教师签名清单不一致。");

            var active = record with { State = "inProgress" };
            return new MaintenanceServiceReleaseUpdateOffer("pending",
                "MaintenanceService 发行签名、协议兼容性和 SHA-256 已核验；独立 SYSTEM UpdateHelper 将切换版本。",
                CreatePayload(configPathFull, active, nowUtc),
                () => StartHelperAndRecordFailure(configPathFull, installerPath, commandId));
        }
        catch
        {
            if (artifactCreated) TryDelete(installerPath);
            if (envelopeCreated) TryDelete(envelopePath);
            MarkCompleted(configPathFull, commandId, "rolledBack");
            throw;
        }
    }

    public static MaintenanceServiceReleaseUpdateTransaction? ReadTransaction(string configPath)
    {
        var directory = MaintenanceServiceIdentityStore.GetServiceDirectory(configPath);
        return ReadTransactionFile(Path.Combine(directory, TransactionFileName));
    }

    [SupportedOSPlatform("windows")]
    public static MaintenanceServiceReleaseUpdateTransaction? ReadStatus(string configPath, Guid commandId,
        DateTimeOffset nowUtc)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("MaintenanceService commandId 无效。", nameof(commandId));
        var record = ReadTransaction(configPath);
        if (record is null || record.CommandId != commandId) return null;
        if (record.State is "pending" or "inProgress")
        {
            if (IsHelperRunning(record)) return record with { State = "inProgress" };
            if (record.HelperProcessId is null && nowUtc - record.CreatedUtc < TimeSpan.FromMinutes(2)) return record;
            var registered = ReadRegisteredServiceVersion();
            var terminal = string.Equals(registered, record.TargetVersion, StringComparison.Ordinal) &&
                           VerifyInstalledService(record.TargetVersion) ? "committed" :
                string.Equals(registered, record.PreviousVersion, StringComparison.Ordinal) &&
                VerifyInstalledService(record.PreviousVersion) ? "rolledBack" : "needsReview";
            MarkCompleted(configPath, commandId, terminal);
            record = ReadTransaction(configPath)
                     ?? throw new InvalidDataException("MaintenanceService 更新事务读回丢失。");
        }
        return record;
    }

    public static MaintenanceServiceReleaseUpdateReadbackPayload CreatePayload(string configPath,
        MaintenanceServiceReleaseUpdateTransaction record, DateTimeOffset nowUtc) =>
        new(record, MaintenanceServiceUpdateExecutor.ReadInstalledVersions(configPath, nowUtc));

    public static void MarkHelperStarted(string configPath, Guid commandId, int processId, long startTicks)
    {
        using var gate = AcquireGate();
        var path = GetTransactionPath(configPath);
        var current = ReadTransactionFile(path);
        if (current is null || current.CommandId != commandId || current.State is not ("pending" or "inProgress"))
            throw new InvalidDataException("MaintenanceService UpdateHelper 找不到待执行的原 commandId 事务。");
        WriteTransactionCore(path, current with
        {
            State = "inProgress", HelperProcessId = processId, HelperProcessStartUtcTicks = startTicks,
            UpdatedUtc = DateTimeOffset.UtcNow
        });
    }

    public static void MarkCompleted(string configPath, Guid commandId, string state)
    {
        if (state is not ("committed" or "rolledBack" or "needsReview"))
            throw new ArgumentOutOfRangeException(nameof(state));
        using var gate = AcquireGate();
        var path = GetTransactionPath(configPath);
        var current = ReadTransactionFile(path);
        if (current is null || current.CommandId != commandId)
            throw new InvalidDataException("MaintenanceService 更新终态与 commandId 不匹配。");
        if (current.State is "committed" or "rolledBack") return;
        WriteTransactionCore(path, current with
        {
            State = state, HelperProcessId = null, HelperProcessStartUtcTicks = null, UpdatedUtc = DateTimeOffset.UtcNow
        });
    }

    public static void TryDeleteStagedPackage(string installerPath)
    {
        TryDelete(Path.GetFullPath(installerPath));
        TryDelete(Path.GetFullPath(installerPath) + ".release.json");
    }

    [SupportedOSPlatform("windows")]
    public static int RecoverInterruptedUpdateFromStartup()
    {
        EnsureSystemIdentity();
        string configPath;
        MaintenanceServiceReleaseUpdateTransaction? record;
        try
        {
            configPath = MaintenanceServiceConfigurationStore.ResolveAgentConfigurationPath();
            record = ReadTransaction(configPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.Text.Json.JsonException or
                                          InvalidOperationException or System.Security.SecurityException or
                                          ArgumentException)
        {
            WriteRecoveryLog("MaintenanceService startup recovery could not read the bound transaction/configuration (" +
                             exception.GetType().Name + "); evidence retained.");
            return 2;
        }
        if (record is null || record.State is "committed" or "rolledBack") return 0;
        if (record.State == "needsReview") return 2;

        Exception? lastFailure = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) Thread.Sleep(TimeSpan.FromSeconds(attempt * 10));
            try
            {
                record = ReadTransaction(configPath)
                         ?? throw new InvalidDataException("MaintenanceService 恢复事务记录丢失。");
                if (record.State is "committed" or "rolledBack") return 0;
                if (record.State == "needsReview") return 2;
                if (IsHelperRunning(record)) return 3;

                using var machineLock = StudentUpdateAtomicTransaction.AcquireMachineLock(TimeSpan.Zero);
                var previousExecutable = GetServiceExecutable(record.PreviousVersion);
                var targetExecutable = GetServiceExecutable(record.TargetVersion);
                if (VerifyInstalledService(record.TargetVersion))
                {
                    MarkCompleted(configPath, record.CommandId, "committed");
                    TryDeleteStagedPackage(GetStagedInstallerPath(configPath, record));
                    return 0;
                }

                PathLinkSecurity.RejectLinks(previousExecutable);
                if (!File.Exists(previousExecutable) ||
                    !string.Equals(ReadProductVersion(previousExecutable), record.PreviousVersion,
                        StringComparison.Ordinal))
                    throw new FileNotFoundException("MaintenanceService 原版本恢复文件不存在或版本不符。",
                        previousExecutable);
                if (File.Exists(targetExecutable)) PathLinkSecurity.RejectLinks(targetExecutable);
                if (!File.Exists(targetExecutable) ||
                    !string.Equals(ReadProductVersion(targetExecutable), record.TargetVersion,
                        StringComparison.Ordinal))
                    WriteRecoveryLog("目标 MaintenanceService 文件缺失或版本不符；尝试恢复已记录的原版本。");

                if (RunServiceCommand(previousExecutable, "--install-and-start-service", TimeSpan.FromMinutes(2)) != 0)
                    throw new InvalidOperationException("MaintenanceService 原版本安装/启动命令失败。");
                if (!VerifyInstalledService(record.PreviousVersion))
                    throw new InvalidOperationException("MaintenanceService 原版本切回后未通过 SCM 和 SYSTEM 健康核验。");
                MarkCompleted(configPath, record.CommandId, "rolledBack");
                TryDeleteStagedPackage(GetStagedInstallerPath(configPath, record));
                WriteRecoveryLog("MaintenanceService interrupted update rolled back to " + record.PreviousVersion + ".");
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or TimeoutException or
                                              System.ComponentModel.Win32Exception or System.Security.SecurityException or
                                              ArgumentException or System.Text.Json.JsonException)
            {
                lastFailure = exception;
            }
        }

        if (lastFailure is not null)
            WriteRecoveryLog("MaintenanceService startup recovery exhausted three attempts; transaction evidence retained (" +
                             lastFailure.GetType().Name + ").");
        return 2;
    }

    private static async Task StartHelperAndRecordFailure(string configPath, string installerPath, Guid commandId)
    {
        try { ApplicationReleaseUpdateHandoff.StartMaintenanceServiceReleaseHelper(installerPath, configPath, commandId); }
        catch
        {
            MarkCompleted(configPath, commandId, "rolledBack");
            TryDeleteStagedPackage(installerPath);
            throw;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static MaintenanceServiceReleaseUpdateTransaction? ReadTransactionFile(string path)
    {
        if (!File.Exists(path)) return null;
        PathLinkSecurity.RejectLinks(path);
        AgentFileSecurity.SecurePrivateFile(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumTransactionBytes)
            throw new InvalidDataException("MaintenanceService 更新事务文件大小无效。");
        var bytes = File.ReadAllBytes(path);
        PolicyJson.RejectDuplicateFields(bytes);
        var record = JsonSerializer.Deserialize<MaintenanceServiceReleaseUpdateTransaction>(bytes, JsonOptions)
                     ?? throw new InvalidDataException("MaintenanceService 更新事务文件为空。");
        if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions)) ||
            record.SchemaVersion != 1 || record.OperationId == Guid.Empty || record.CommandId == Guid.Empty ||
            record.CreatedUtc.Offset != TimeSpan.Zero || record.UpdatedUtc.Offset != TimeSpan.Zero ||
            record.State is not ("pending" or "inProgress" or "committed" or "rolledBack" or "needsReview") ||
            !Regex.IsMatch(record.ReleaseSha256, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("MaintenanceService 更新事务记录字段无效。");
        _ = ApplicationReleaseClient.CompareVersions(record.PreviousVersion, record.PreviousVersion);
        _ = ApplicationReleaseClient.CompareVersions(record.TargetVersion, record.TargetVersion);
        return record;
    }

    private static void WriteTransaction(string configPath, MaintenanceServiceReleaseUpdateTransaction record)
    {
        using var gate = AcquireGate();
        WriteTransactionCore(GetTransactionPath(configPath), record);
    }

    private static void WriteTransactionCore(string path, MaintenanceServiceReleaseUpdateTransaction record)
    {
        var current = ReadTransactionFile(path);
        if (current is not null)
        {
            if (current.CommandId == record.CommandId)
            {
                if (current.OperationId != record.OperationId || current.PreviousVersion != record.PreviousVersion ||
                    current.TargetVersion != record.TargetVersion ||
                    !string.Equals(current.ReleaseSha256, record.ReleaseSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("MaintenanceService commandId 已绑定到不同发行事务意图。");
                if (current.State is "committed" or "rolledBack") return;
            }
            else if (current.State is "pending" or "inProgress" or "needsReview")
                throw new InvalidOperationException("已有 MaintenanceService 更新事务未终结；拒绝覆盖日志。");
        }
        var directory = Path.GetDirectoryName(path)!;
        PathLinkSecurity.RejectLinks(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length == 0 || bytes.Length > (int)MaximumTransactionBytes)
            throw new IOException("MaintenanceService 更新事务超过存储上限。");
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
        finally { TryDelete(temporary); }
    }

    private static string GetTransactionPath(string configPath) =>
        Path.Combine(MaintenanceServiceIdentityStore.GetServiceDirectory(configPath), TransactionFileName);

    private static IDisposable AcquireGate()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Maintenance 更新事务仅支持 Windows。");
        var mutex = new Mutex(false, @"Global\VeyonCampus.MaintenanceServiceReleaseUpdateJournal");
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("等待 MaintenanceService 更新日志互斥锁超时。");
            }
            catch (AbandonedMutexException) { }
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }

    private static bool IsHelperRunning(MaintenanceServiceReleaseUpdateTransaction record)
    {
        if (record.HelperProcessId is not { } processId || record.HelperProcessStartUtcTicks is not { } ticks)
            return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || !string.Equals(process.ProcessName, "VeyonCampus.UpdateHelper",
                    StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(process.StartTime.ToUniversalTime().Ticks - ticks) > TimeSpan.TicksPerSecond * 2)
                return false;
            var helperPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Veyon Campus", "MaintenanceService", "Versions", record.PreviousVersion, "VeyonCampus.UpdateHelper.exe");
            return string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""),
                Path.GetFullPath(helperPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or NotSupportedException)
        { return false; }
    }

    private static string? ReadRegisteredServiceVersion()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var serviceRoot = Path.Combine(programFiles, "Veyon Campus", "MaintenanceService", "Versions");
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\VeyonCampusMaintenanceService", writable: false);
            var imagePath = key?.GetValue("ImagePath") as string;
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            var executable = ExtractExecutablePath(imagePath);
            PathLinkSecurity.RejectLinks(executable);
            var versionDirectory = Path.GetDirectoryName(executable);
            if (!string.Equals(Path.GetDirectoryName(versionDirectory), serviceRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(executable), "VeyonCampus.MaintenanceService.exe",
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(executable)) return null;
            var version = Path.GetFileName(versionDirectory);
            _ = ApplicationReleaseClient.CompareVersions(version, version);
            return version;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.Security.SecurityException or
                                          ArgumentException or System.ComponentModel.Win32Exception or
                                          NotSupportedException)
        { return null; }
    }

    private static bool VerifyInstalledService(string expectedVersion)
    {
        try
        {
            var registeredVersion = ReadRegisteredServiceVersion();
            if (!string.Equals(registeredVersion, expectedVersion, StringComparison.Ordinal)) return false;
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Veyon Campus", "MaintenanceService", "Versions", expectedVersion,
                "VeyonCampus.MaintenanceService.exe");
            return RunServiceCommand(executable, "--verify-running") == 0 &&
                   RunServiceCommand(executable, "--verify-installation") == 0 &&
                   string.Equals(ReadProductVersion(executable), expectedVersion, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.Security.SecurityException or
                                          ArgumentException or System.ComponentModel.Win32Exception or
                                          NotSupportedException)
        { return false; }
    }

    private static string GetServiceExecutable(string version)
    {
        _ = ApplicationReleaseClient.CompareVersions(version, version);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Veyon Campus", "MaintenanceService", "Versions", version,
            "VeyonCampus.MaintenanceService.exe");
        PathLinkSecurity.RejectLinksAllowingMissingComponents(executable);
        return executable;
    }

    private static string GetStagedInstallerPath(string configPath,
        MaintenanceServiceReleaseUpdateTransaction record)
    {
        var directory = Path.Combine(MaintenanceServiceIdentityStore.GetServiceDirectory(configPath),
            StagingDirectoryName);
        PathLinkSecurity.RejectLinksAllowingMissingComponents(directory);
        return Path.Combine(directory,
            $"VeyonCampus-Maintenance-Setup-{record.TargetVersion}-win-x64.exe");
    }

    private static int RunServiceCommand(string executablePath, string arguments, TimeSpan? timeout = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executablePath)
            {
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!
            });
            if (process is null) return -1;
            if (!process.WaitForExit(checked((int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds)))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
                return -1;
            }
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or System.Security.SecurityException)
        { return -1; }
    }

    private static string? ReadProductVersion(string executablePath) =>
        FileVersionInfo.GetVersionInfo(executablePath).ProductVersion?.Split('+')[0];

    private static string ExtractExecutablePath(string imagePath)
    {
        var value = imagePath.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end <= 1) throw new InvalidDataException("Maintenance SCM ImagePath 引号无效。");
            return Path.GetFullPath(value[1..end]);
        }
        var exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe < 0) throw new InvalidDataException("Maintenance SCM ImagePath 缺少 EXE 路径。");
        return Path.GetFullPath(value[..(exe + 4)]);
    }

    private static void WritePrivateFile(string path, byte[] bytes)
    {
        PathLinkSecurity.RejectLinksAllowingMissingComponents(path);
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        AgentFileSecurity.SecurePrivateFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { PathLinkSecurity.RejectLinks(path); File.Delete(path); } }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException) { }
    }

    private static void WriteRecoveryLog(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VeyonCampus", "MaintenanceServiceHost");
            Directory.CreateDirectory(directory);
            PathLinkSecurity.RejectLinks(directory);
            AgentFileSecurity.SecurePrivateDirectory(directory);
            var path = Path.Combine(directory, "service.log");
            if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);
            File.AppendAllText(path, DateTimeOffset.UtcNow.ToString("O") + " " +
                new string(message.Where(character => !char.IsControl(character)).Take(512).ToArray()) + "\r\n",
                new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException) { }
    }

    private static void EnsureSystemIdentity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("MaintenanceService 更新仅支持 Windows。");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18")
            throw new UnauthorizedAccessException("MaintenanceService 更新必须由 SYSTEM 服务执行。");
    }

    private static void EnsureSamePath(string left, string right, string message)
    {
        if (!string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(message);
    }
}
