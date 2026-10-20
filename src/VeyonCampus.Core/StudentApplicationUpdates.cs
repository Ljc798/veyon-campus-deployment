using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public sealed record StudentApplicationUpdateCommand(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("campusId")] string CampusId,
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("issuedUtc")] DateTimeOffset IssuedUtc,
    [property: JsonPropertyName("expiresUtc")] DateTimeOffset ExpiresUtc,
    [property: JsonPropertyName("release")] ApplicationReleaseEnvelope Release,
    [property: JsonPropertyName("localDownloadUrl")] string LocalDownloadUrl);

public sealed record SignedStudentApplicationUpdate(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature);

public static class StudentApplicationUpdateCryptography
{
    public const int MaximumEnvelopeBytes = 128 * 1024;
    public const int LocalHttpPort = 39175;
    public static readonly TimeSpan MaximumCommandLifetime = TimeSpan.FromMinutes(10);
    private const string SignatureAlgorithm = "RSA-PSS-SHA256";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string Sign(StudentApplicationUpdateCommand command, RSA campusPrivateKey,
        Uri apiBaseAddress, string developerPublicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(campusPrivateKey);
        var normalized = command with
        {
            IssuedUtc = command.IssuedUtc.ToUniversalTime(),
            ExpiresUtc = command.ExpiresUtc.ToUniversalTime()
        };
        Validate(normalized, apiBaseAddress, developerPublicKeyPem, DateTimeOffset.UtcNow);
        if (campusPrivateKey.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("校区更新签名密钥位长不受支持。");
        var payload = Serialize(normalized);
        var signature = campusPrivateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var envelope = new SignedStudentApplicationUpdate(Convert.ToBase64String(payload),
            Convert.ToBase64String(signature));
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumEnvelopeBytes)
            throw new InvalidDataException("学生更新命令超过大小限制。");
        return json;
    }

    public static StudentApplicationUpdateCommand Verify(string envelopeJson, string campusPublicKeyPem,
        string expectedCampusId, Uri apiBaseAddress, string developerPublicKeyPem,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(campusPublicKeyPem);
        if (Encoding.UTF8.GetByteCount(envelopeJson) > MaximumEnvelopeBytes)
            throw new InvalidDataException("学生更新命令超过大小限制。");
        SignedStudentApplicationUpdate envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedStudentApplicationUpdate>(envelopeJson, JsonOptions)
                       ?? throw new InvalidDataException("学生更新命令为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("学生更新签名信封格式无效。", exception);
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.Payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.Signature);
        var payload = DecodeCanonicalBase64(envelope.Payload, "学生更新命令正文");
        var signature = DecodeCanonicalBase64(envelope.Signature, "学生更新签名");
        using var campusKey = RSA.Create();
        try { campusKey.ImportFromPem(campusPublicKeyPem); }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("学生端校区公钥无效。", exception);
        }
        if (campusKey.KeySize is < 2048 or > 4096 || signature.Length != campusKey.KeySize / 8 ||
            !campusKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("学生更新校区签名无效；已拒绝该命令。");

        StudentApplicationUpdateCommand command;
        try
        {
            command = JsonSerializer.Deserialize<StudentApplicationUpdateCommand>(payload, JsonOptions)
                      ?? throw new InvalidDataException("学生更新命令正文为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("学生更新命令正文格式无效。", exception);
        }
        if (!payload.AsSpan().SequenceEqual(Serialize(command)))
            throw new InvalidDataException("学生更新命令正文不是规范编码。");
        Validate(command, apiBaseAddress, developerPublicKeyPem, now ?? DateTimeOffset.UtcNow);
        if (!string.Equals(command.CampusId, expectedCampusId, StringComparison.Ordinal))
            throw new InvalidDataException("学生更新命令属于其他校区。");
        return command;
    }

    public static Uri ValidateLocalDownloadUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.Port != LocalHttpPort ||
            !IPAddress.TryParse(uri.Host, out var address) || !IsPrivateLanAddress(address))
            throw new InvalidDataException("学生更新文件只能从校区局域网私有地址下载。");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 || segments[0] != "v1" || segments[1] != "student-updates" ||
            !Regex.IsMatch(segments[2], "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("学生更新局域网下载路径无效。");
        return uri;
    }

    private static void Validate(StudentApplicationUpdateCommand command, Uri apiBaseAddress,
        string developerPublicKeyPem, DateTimeOffset now)
    {
        if (command.Release is null || command.SchemaVersion != 1 || command.CommandId == Guid.Empty ||
            command.IssuedUtc.Offset != TimeSpan.Zero || command.ExpiresUtc.Offset != TimeSpan.Zero ||
            command.ExpiresUtc <= now || command.IssuedUtc > now.AddMinutes(1) ||
            command.ExpiresUtc <= command.IssuedUtc ||
            command.ExpiresUtc - command.IssuedUtc > MaximumCommandLifetime)
            throw new InvalidDataException("学生更新命令版本、编号或有效期无效。");
        try { WebsitePolicySigningKeyStore.ValidateCampusId(command.CampusId); }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("学生更新命令校区标识无效。", exception);
        }
        _ = ValidateLocalDownloadUrl(command.LocalDownloadUrl);
        ApplicationReleaseClient.Verify(command.Release, ApplicationReleaseRole.StudentSetup,
            apiBaseAddress, developerPublicKeyPem);
    }

    private static byte[] Serialize(StudentApplicationUpdateCommand command) =>
        JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions);

    private static byte[] DecodeCanonicalBase64(string value, string label)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (Convert.ToBase64String(bytes) != value)
                throw new FormatException();
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(label + "编码无效。", exception);
        }
    }

    public static bool IsPrivateLanAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168;
        return address.AddressFamily == AddressFamily.InterNetworkV6 && bytes[0] is >= 0xFC and <= 0xFD;
    }
}

public static class StudentApplicationUpdateReplayStore
{
    private const long MaximumStateBytes = 64 * 1024;
    private const int MaximumReplayEntries = 512;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static bool TryConsume(string statePath, Guid commandId, DateTimeOffset expiresUtc,
        DateTimeOffset? now = null)
    {
        if (commandId == Guid.Empty) throw new InvalidDataException("学生更新命令编号无效。");
        var path = Path.GetFullPath(statePath);
        var parentDirectory = Path.GetDirectoryName(path)
                              ?? throw new InvalidDataException("学生更新重放状态目录无效。");
        lock (Gate)
        {
            PathLinkSecurity.RejectLinks(parentDirectory);
            List<ReplayEntry> entries = [];
            if (File.Exists(path))
            {
                PathLinkSecurity.RejectLinks(path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    new FileInfo(path).Length is < 1 or > MaximumStateBytes)
                    throw new InvalidDataException("学生更新重放状态文件无效。");
                try
                {
                    entries = JsonSerializer.Deserialize<List<ReplayEntry>>(File.ReadAllBytes(path), JsonOptions)
                              ?? throw new InvalidDataException("学生更新重放状态文件为空。");
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("学生更新重放状态文件格式无效。", exception);
                }
            }
            var currentTime = now ?? DateTimeOffset.UtcNow;
            if (entries.Count > MaximumReplayEntries)
                throw new InvalidDataException("学生更新重放状态条目超过限制。");
            entries.RemoveAll(entry => entry.ExpiresUtc <= currentTime);
            if (entries.Any(entry => entry.CommandId == commandId)) return false;
            if (entries.Count >= MaximumReplayEntries)
                throw new InvalidDataException("学生更新重放状态暂满；请等待旧命令到期后重试。");
            entries.Add(new ReplayEntry(commandId, expiresUtc));

            var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, entries, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                if (OperatingSystem.IsWindows()) AgentFileSecurity.Secure(temporaryPath, directory: false);
                File.Move(temporaryPath, path, overwrite: true);
                if (OperatingSystem.IsWindows()) AgentFileSecurity.Secure(path, directory: false);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            return true;
        }
    }

    private sealed record ReplayEntry(
        [property: JsonPropertyName("commandId")] Guid CommandId,
        [property: JsonPropertyName("expiresUtc")] DateTimeOffset ExpiresUtc);
}

public sealed record StudentApplicationUpdateProcessingResult(StudentApplicationUpdateResponse Response)
{
    public string Message => Response.Message;
    public bool AgentRestartPending => Response.AgentRestartPending;
}

public static class StudentApplicationUpdateProcessor
{
    private sealed record StudentArtifactDownloadResult(long Bytes, long IntegrityHashMilliseconds);

    private const long MaximumArtifactBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan SetupInstallerTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SetupInstallerStopTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);
    private static readonly HttpClient DownloadClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = TimeSpan.FromMinutes(20) };
    private static readonly JsonSerializerOptions RoleInfoJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string? TryReadInstalledStudentSetupVersion()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Veyon Campus", "Student");
        var executablePath = Path.Combine(installDirectory, "VeyonCampus.StudentSetup.exe");
        try
        {
            PathLinkSecurity.RejectLinks(installDirectory);
            return ReadInstalledVersion(installDirectory, executablePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public static async Task<StudentApplicationUpdateProcessingResult> ProcessAsync(string signedCommandJson,
        string configPath,
        WebsitePolicyAgentConfig config, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Student 静默更新仅支持 Windows。");
        var teacherOfferStartedUtc = DateTimeOffset.UtcNow;
        var teacherOfferStartedTimestamp = Stopwatch.GetTimestamp();
        await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var verificationStartedUtc = DateTimeOffset.UtcNow;
            var verificationStartedTimestamp = Stopwatch.GetTimestamp();
            var command = StudentApplicationUpdateCryptography.Verify(signedCommandJson, config.PublicKeyPem,
                config.CampusId, DeploymentPackageApiConfiguration.GetApiBaseAddress(),
                ApplicationReleaseTrust.LoadPinnedPublicKeyPem());
            StudentUpdatePhaseTimingStore.RecordCompleted(configPath, command.CommandId,
                StudentUpdatePhase.IntegrityVerify, verificationStartedUtc, verificationStartedTimestamp);
            using var teacherOfferTiming = StudentUpdatePhaseTimingStore.BeginAt(configPath,
                command.CommandId, StudentUpdatePhase.TeacherOffer, teacherOfferStartedUtc,
                teacherOfferStartedTimestamp);
            var manifest = command.Release.Manifest;
            var installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Veyon Campus", "Student");
            // Named Mutex is thread-affine; limit the preflight lock to this synchronous section.
            using (StudentUpdateAtomicTransaction.AcquireMachineLock())
            {
                StudentUpdateAtomicTransaction.RecoverPending(configPath);
                StudentUpdateAtomicTransaction.EnsureNoActiveTransaction(configPath);
                PathLinkSecurity.RejectLinks(installDirectory);
                var executablePath = Path.Combine(installDirectory, "VeyonCampus.StudentSetup.exe");
                var currentVersion = ReadInstalledVersion(installDirectory, executablePath);
                var versionComparison = ApplicationReleaseClient.CompareVersions(manifest.Version, currentVersion);
                if (versionComparison < 0)
                    throw new InvalidDataException("学生更新命令试图降级；已拒绝。");
                var currentAgentVersion = WebsitePolicyAgent.GetRuntimeVersion();
                if (currentAgentVersion == "unknown")
                    throw new InvalidDataException("无法读取当前 SYSTEM Agent 版本；更新需管理员核对。");
                ApplicationReleaseCompatibility.EnsureSupports(manifest,
                    WindowsApplicationPolicyStateStore.HasAnyActiveState(),
                    WindowsStudentSystemPolicyAgent.HasAnyActiveState());
                var agentComparison = ApplicationReleaseClient.CompareVersions(manifest.Version, currentAgentVersion);
                if (versionComparison == 0 && agentComparison <= 0)
                {
                    teacherOfferTiming.Complete();
                    return RecordNoChange(command, configPath, config, currentVersion, currentAgentVersion);
                }
            }

            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
                                  ?? throw new InvalidDataException("Student Agent 配置目录无效。");
            var replayStatePath = Path.Combine(configDirectory, "student-update-replay.json");

            var stagingDirectory = Path.Combine(configDirectory, "student-update-" + command.CommandId.ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            AgentFileSecurity.SecureTree(stagingDirectory);
            var installerPath = Path.Combine(stagingDirectory, manifest.FileName);
            StudentUpdateTransactionRecord? transaction = null;
            try
            {
                teacherOfferTiming.Complete();
                var localDownloadUri = StudentApplicationUpdateCryptography.ValidateLocalDownloadUrl(
                    command.LocalDownloadUrl);
                StudentUpdatePhaseTimingStore.RecordEnvironment(configPath, command.CommandId,
                    StudentUpdateEnvironmentClassifier.Detect(localDownloadUri));
                using var lanTransferTiming = StudentUpdatePhaseTimingStore.Begin(configPath,
                    command.CommandId, StudentUpdatePhase.LanTransfer);
                long transferredBytes = 0;
                long integrityHashMilliseconds = 0;
                bool? packageHashVerified = null;
                StudentArtifactDownloadResult downloadedArtifact;
                try
                {
                    downloadedArtifact = await DownloadArtifactAsync(
                        localDownloadUri, manifest, installerPath, stagingDirectory, cancellationToken,
                        (bytes, hashMilliseconds, hashVerified) =>
                        {
                            transferredBytes = bytes;
                            integrityHashMilliseconds = hashMilliseconds;
                            packageHashVerified = hashVerified;
                        }).ConfigureAwait(false);
                }
                catch
                {
                    lanTransferTiming.Complete(succeeded: false, bytes: transferredBytes);
                    if (packageHashVerified is { } hashVerified)
                        StudentUpdatePhaseTimingStore.AddIntegrityHashMeasurement(configPath,
                            command.CommandId, integrityHashMilliseconds, transferredBytes,
                            succeeded: hashVerified);
                    throw;
                }
                lanTransferTiming.Complete(succeeded: true, bytes: downloadedArtifact.Bytes);
                StudentUpdatePhaseTimingStore.AddIntegrityHashMeasurement(configPath,
                    command.CommandId, downloadedArtifact.IntegrityHashMilliseconds, downloadedArtifact.Bytes,
                    succeeded: true);
                using var prepareAndBackupTiming = StudentUpdatePhaseTimingStore.Begin(configPath,
                    command.CommandId, StudentUpdatePhase.PrepareAndBackup);
                if (DateTimeOffset.UtcNow >= command.ExpiresUtc)
                    throw new InvalidDataException("学生更新命令在下载期间已过期；请由教师重新发送。");

                // Download is asynchronous, so reacquire and re-read all machine state before mutation.
                using var machineUpdateLock = StudentUpdateAtomicTransaction.AcquireMachineLock();
                StudentUpdateAtomicTransaction.RecoverPending(configPath);
                StudentUpdateAtomicTransaction.EnsureNoActiveTransaction(configPath);
                PathLinkSecurity.RejectLinks(installDirectory);
                var executablePath = Path.Combine(installDirectory, "VeyonCampus.StudentSetup.exe");
                var currentVersion = ReadInstalledVersion(installDirectory, executablePath);
                var versionComparison = ApplicationReleaseClient.CompareVersions(manifest.Version, currentVersion);
                if (versionComparison < 0)
                    throw new InvalidDataException("学生更新命令试图降级；已拒绝。");
                var currentAgentVersion = WebsitePolicyAgent.GetRuntimeVersion();
                if (currentAgentVersion == "unknown")
                    throw new InvalidDataException("无法读取当前 SYSTEM Agent 版本；更新需管理员核对。");
                ApplicationReleaseCompatibility.EnsureSupports(manifest,
                    WindowsApplicationPolicyStateStore.HasAnyActiveState(),
                    WindowsStudentSystemPolicyAgent.HasAnyActiveState());
                var agentComparison = ApplicationReleaseClient.CompareVersions(manifest.Version, currentAgentVersion);
                var targetAgentVersion = agentComparison > 0 ? manifest.Version : currentAgentVersion;
                if (versionComparison == 0 && agentComparison <= 0)
                {
                    prepareAndBackupTiming.Complete();
                    return RecordNoChange(command, configPath, config, currentVersion, currentAgentVersion);
                }

                EnsureUpdateDiskSpace(installDirectory, configDirectory, installerPath, manifest.SizeBytes);
                if (!StudentApplicationUpdateReplayStore.TryConsume(replayStatePath, command.CommandId,
                        command.ExpiresUtc))
                    throw new InvalidDataException("学生更新命令已执行过；已拒绝重放。");
                var configFingerprint = WebsitePolicyAgent.ConfigFingerprint(config);
                transaction = StudentUpdateAtomicTransaction.Create(configPath, command.CommandId, currentVersion,
                    manifest.Version, currentAgentVersion, targetAgentVersion, manifest.FileName, configFingerprint,
                    manifest.Sha256);
                StudentUpdateAtomicTransaction.RegisterRecoveryTasks(configPath,
                    Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 SYSTEM Agent 路径。"));
                transaction = StudentUpdateAtomicTransaction.Advance(configPath, transaction,
                    StudentUpdateTransactionState.PreviousSetupPreserved);
                StudentUpdateAtomicTransaction.PreservePreviousInstallation(configPath, transaction);
                transaction = StudentUpdateAtomicTransaction.Advance(configPath, transaction,
                    StudentUpdateTransactionState.SetupInstallationPending);
                prepareAndBackupTiming.Complete();

                using var setupInstallTiming = StudentUpdatePhaseTimingStore.Begin(configPath,
                    command.CommandId, StudentUpdatePhase.SetupInstall);
                var installerStartInfo = new ProcessStartInfo(installerPath)
                {
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = stagingDirectory
                };
                using (var installer = Process.Start(installerStartInfo)
                                       ?? throw new InvalidOperationException("无法启动学生静默安装器。"))
                {
                    if (!installer.WaitForExit((int)SetupInstallerTimeout.TotalMilliseconds))
                    {
                        var timeout = new TimeoutException("学生静默安装器超过 10 分钟；已请求终止并启动回滚流程。");
                        try
                        {
                            installer.Kill(entireProcessTree: true);
                            if (!installer.WaitForExit((int)SetupInstallerStopTimeout.TotalMilliseconds))
                                throw new TimeoutException("学生静默安装器在 30 秒终止期限内仍未退出。");
                        }
                        catch (Exception stopFailure) when (stopFailure is InvalidOperationException or
                                                           System.ComponentModel.Win32Exception or
                                                           TimeoutException)
                        {
                            if (!installer.HasExited)
                                throw new AggregateException("学生静默安装器已超时，且未能确认其退出；自动回滚将保留旧版。",
                                    timeout, stopFailure);
                        }
                        throw timeout;
                    }
                    if (installer.ExitCode != 0)
                        throw new InvalidOperationException($"学生静默安装器返回失败代码 {installer.ExitCode}。");
                }

                StudentUpdateAtomicTransaction.VerifyTargetSetupInstalled(transaction);
                transaction = StudentUpdateAtomicTransaction.Advance(configPath, transaction,
                    StudentUpdateTransactionState.SetupInstalled);
                setupInstallTiming.Complete();
                var installedVersion = manifest.Version;
                if (agentComparison <= 0)
                {
                    using (var healthTiming = StudentUpdatePhaseTimingStore.Begin(configPath,
                               command.CommandId, StudentUpdatePhase.HealthAndCommit))
                    {
                        if (!WebsitePolicyAgentInstaller.WaitForUpdatedAgentHealth(TimeSpan.FromSeconds(20),
                                configFingerprint, currentAgentVersion))
                        {
                            healthTiming.Complete(succeeded: false);
                            throw new TimeoutException("StudentSetup 已安装，但现有 SYSTEM Agent 未通过目标配置健康读回。");
                        }
                        StudentUpdateAtomicTransaction.Commit(configPath, transaction);
                        healthTiming.Complete();
                    }
                    return CreateResult(command, installedVersion, currentAgentVersion,
                        $"StudentSetup {installedVersion} 已读回；Student Agent {currentAgentVersion} 无需回退。",
                        agentRestartPending: false);
                }

                var agentSourceDirectory = Path.Combine(installDirectory, "WebsitePolicyAgent");
                StagedWebsitePolicyAgentUpdate stagedAgentUpdate;
                using (var agentStageTiming = StudentUpdatePhaseTimingStore.Begin(configPath,
                           command.CommandId, StudentUpdatePhase.AgentStage))
                {
                    stagedAgentUpdate = WebsitePolicyAgentInstaller.StageAgentUpdate(agentSourceDirectory,
                        manifest.Version, configPath);
                    transaction = StudentUpdateAtomicTransaction.Advance(configPath, transaction,
                        StudentUpdateTransactionState.AgentStaged);
                    agentStageTiming.Complete();
                }
                transaction = StudentUpdateAtomicTransaction.Advance(configPath, transaction,
                    StudentUpdateTransactionState.ActivationPending);
                using (var activationTiming = StudentUpdatePhaseTimingStore.Begin(configPath,
                           command.CommandId, StudentUpdatePhase.AgentActivate))
                {
                    WebsitePolicyAgentInstaller.ActivateStagedAgentUpdate(stagedAgentUpdate.TargetExecutablePath,
                        stagedAgentUpdate.PreviousExecutablePath, configPath);
                    StudentApplicationUpdateHandoff.Start(stagedAgentUpdate.TargetExecutablePath,
                        stagedAgentUpdate.PreviousExecutablePath, configPath, manifest.Version, currentAgentVersion,
                        configFingerprint, command.CommandId);
                    activationTiming.Complete();
                }
                return CreateResult(command, installedVersion, currentAgentVersion,
                    $"StudentSetup {installedVersion} 已读回；Student Agent {manifest.Version} 正在切换并重启。",
                    agentRestartPending: true);
            }
            catch (Exception updateFailure)
            {
                if (transaction is not null && transaction.State is not (StudentUpdateTransactionState.Committed or
                        StudentUpdateTransactionState.RolledBack))
                {
                    using var rollbackTiming = StudentUpdatePhaseTimingStore.Begin(configPath,
                        command.CommandId, StudentUpdatePhase.Rollback);
                    try
                    {
                        StudentUpdateAtomicTransaction.Rollback(configPath, transaction, updateFailure);
                        rollbackTiming.Complete();
                    }
                    catch (Exception rollbackFailure)
                    {
                        rollbackTiming.Complete(succeeded: false);
                        throw new AggregateException("学生端更新失败，联合回滚需要继续恢复；旧版材料已保留。",
                            updateFailure, rollbackFailure);
                    }
                }
                throw;
            }
            finally
            {
                if (transaction is null)
                {
                    try
                    {
                        StudentUpdateAtomicTransaction.CleanupUnclaimedStagingDirectory(configPath,
                            command.CommandId);
                    }
                    catch (Exception)
                    {
                        // An unreadable journal or unsafe staging path must leave the download in place.
                    }
                }
            }
        }
        finally { ApplyGate.Release(); }
    }

    private static StudentApplicationUpdateProcessingResult CreateResult(StudentApplicationUpdateCommand command,
        string studentSetupVersion, string agentVersion, string message, bool agentRestartPending) =>
        new(new StudentApplicationUpdateResponse(1, StudentApplicationUpdateResponseCryptography.Purpose,
            command.CampusId, command.CommandId, DateTimeOffset.UtcNow, studentSetupVersion, agentVersion,
            agentRestartPending, message));

    [SupportedOSPlatform("windows")]
    private static StudentApplicationUpdateProcessingResult RecordNoChange(StudentApplicationUpdateCommand command,
        string configPath, WebsitePolicyAgentConfig config, string currentSetupVersion, string currentAgentVersion)
    {
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
                              ?? throw new InvalidDataException("Student Agent 配置目录无效。");
        var replayStatePath = Path.Combine(configDirectory, "student-update-replay.json");
        if (!StudentApplicationUpdateReplayStore.TryConsume(replayStatePath, command.CommandId,
                command.ExpiresUtc))
            throw new InvalidDataException("学生更新命令已执行过；已拒绝重放。");
        var manifest = command.Release.Manifest;
        var transaction = StudentUpdateAtomicTransaction.Create(configPath, command.CommandId,
            currentSetupVersion, currentSetupVersion, currentAgentVersion, currentAgentVersion,
            manifest.FileName, WebsitePolicyAgent.ConfigFingerprint(config), manifest.Sha256);
        try
        {
            StudentUpdateAtomicTransaction.Commit(configPath, transaction);
        }
        catch (Exception commitFailure)
        {
            var current = StudentUpdateAtomicTransaction.Read(configPath);
            if (current is not null && current.State is not (StudentUpdateTransactionState.Committed or
                    StudentUpdateTransactionState.RolledBack))
            {
                try { StudentUpdateAtomicTransaction.Rollback(configPath, current, commitFailure); }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException("学生端无变更事务确认失败，且恢复状态仍需核对。",
                        commitFailure, rollbackFailure);
                }
            }
            throw;
        }
        return CreateResult(command, currentSetupVersion, currentAgentVersion,
            $"StudentSetup {currentSetupVersion} 与 Student Agent {currentAgentVersion} 已确认无需改变；本次事务已安全完成。",
            agentRestartPending: false);
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureUpdateDiskSpace(string installDirectory, string configDirectory, string installerPath,
        long installerBytes)
    {
        var installDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(installDirectory))!);
        var configDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(configDirectory))!);
        var installBytes = StudentUpdateAtomicTransaction.MeasureDirectoryBytes(installDirectory);
        var agentSourceDirectory = Path.Combine(installDirectory, "WebsitePolicyAgent");
        if (!Directory.Exists(agentSourceDirectory))
            throw new InvalidDataException("当前 StudentSetup 缺少可核验的 Student Agent 文件目录。");
        var agentBytes = StudentUpdateAtomicTransaction.MeasureDirectoryBytes(agentSourceDirectory);
        var installReserve = Math.Max(256L * 1024 * 1024, checked(installBytes + installerBytes / 2));
        var configReserve = Math.Max(64L * 1024 * 1024, checked(agentBytes + installerBytes / 4));
        var sameVolume = string.Equals(installDrive.Name, configDrive.Name, StringComparison.OrdinalIgnoreCase);
        if (installDrive.AvailableFreeSpace < installReserve ||
            configDrive.AvailableFreeSpace < (sameVolume ? checked(installReserve + configReserve) : configReserve))
            throw new IOException("学生电脑剩余磁盘空间不足以同时保留旧版本、暂存更新包并安装新版本。");
        if (new FileInfo(installerPath).Length != installerBytes)
            throw new InvalidDataException("学生更新暂存包大小与签名清单不一致。");
    }

    [SupportedOSPlatform("windows")]
    private static async Task<StudentArtifactDownloadResult> DownloadArtifactAsync(Uri downloadUri,
        ApplicationReleaseManifest manifest,
        string installerPath, string stagingDirectory, CancellationToken cancellationToken,
        Action<long, long, bool?>? reportMeasurements = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"学生更新局域网下载返回 HTTP {(int)response.StatusCode}。", null,
                response.StatusCode);
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != manifest.SizeBytes)
            throw new InvalidDataException("学生安装器大小与开发者签名清单不符。");

        var temporaryPath = Path.Combine(stagingDirectory, "." + Guid.NewGuid().ToString("N") + ".partial");
        long totalBytes = 0;
        long integrityHashTicks = 0;
        bool hashVerificationCompleted = false;
        bool hashVerificationSucceeded = false;
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[64 * 1024];
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > manifest.SizeBytes || totalBytes > MaximumArtifactBytes)
                        throw new InvalidDataException("学生安装器超过开发者签名清单声明的大小。");
                    var hashStarted = Stopwatch.GetTimestamp();
                    digest.AppendData(buffer, 0, bytesRead);
                    integrityHashTicks += Math.Max(0, Stopwatch.GetTimestamp() - hashStarted);
                    await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (totalBytes != manifest.SizeBytes)
                    throw new InvalidDataException("学生安装器下载不完整。");
            }

            var actualSha256 = Convert.ToHexString(digest.GetHashAndReset());
            hashVerificationCompleted = true;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualSha256),
                    Encoding.ASCII.GetBytes(manifest.Sha256)))
                throw new InvalidDataException("学生安装器 SHA-256 与开发者签名清单不符。");
            hashVerificationSucceeded = true;
            AgentFileSecurity.Secure(temporaryPath, directory: false, executable: true);
            File.Move(temporaryPath, installerPath, overwrite: false);
            AgentFileSecurity.Secure(installerPath, directory: false, executable: true);
            return new StudentArtifactDownloadResult(totalBytes,
                (long)Math.Max(0, integrityHashTicks * 1000d / Stopwatch.Frequency));
        }
        finally
        {
            try
            {
                reportMeasurements?.Invoke(totalBytes,
                    (long)Math.Max(0, integrityHashTicks * 1000d / Stopwatch.Frequency),
                    hashVerificationCompleted ? hashVerificationSucceeded : null);
            }
            catch (Exception) { /* Measurement reporting must not change the update result. */ }
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string ReadInstalledVersion(string installDirectory, string executablePath)
    {
        var markerPath = Path.Combine(installDirectory, "veyon-campus-role.json");
        PathLinkSecurity.RejectLinks(markerPath);
        if (!File.Exists(markerPath) || !File.Exists(executablePath))
            throw new InvalidDataException("未找到已安装的 StudentSetup；本次更新未运行。");
        var markerInfo = new FileInfo(markerPath);
        if (markerInfo.Length is < 1 or > 4096)
            throw new InvalidDataException("StudentSetup 角色标记大小无效。");
        var roleInfo = JsonSerializer.Deserialize<StudentRoleInformation>(File.ReadAllBytes(markerPath),
                          RoleInfoJsonOptions)
                       ?? throw new InvalidDataException("StudentSetup 角色标记为空。");
        if (roleInfo.SchemaVersion != 1 || roleInfo.Role != "StudentSetup" ||
            roleInfo.Product != "VeyonCampus.StudentSetup")
            throw new InvalidDataException("当前安装不是受支持的 StudentSetup 角色。");
        _ = ApplicationReleaseClient.CompareVersions(roleInfo.Version, roleInfo.Version);
        return roleInfo.Version;
    }

    private sealed record StudentRoleInformation(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("product")] string Product,
        [property: JsonPropertyName("version")] string Version);
}

public sealed record StudentApplicationUpdatePushResult(string Target, bool Succeeded, string Detail,
    bool NeedsReview = false, StudentAgentIdentityTrustCandidate? IdentityCandidate = null,
    bool RolledBack = false);

public sealed record StudentApplicationUpdateSourceVersions(string StudentSetupVersion, string AgentVersion,
    int StudentUpdateProtocolVersion);

public sealed record StudentApplicationUpdateProgress(string Target, int CompletedTargets, int TotalTargets,
    string Stage, IReadOnlyList<StudentUpdatePhaseTiming>? PhaseTimings = null, Guid? CommandId = null,
    StudentUpdateEnvironmentCategories? Environment = null);

public sealed record StudentApplicationUpdateLanProgress(string Target, long BytesSent, long TotalBytes);

public sealed class StudentApplicationUpdateLanServer : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 8192;
    private readonly TcpListener[] _listeners;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _connectionLimit = new(24, 24);
    private readonly string _installerPath;
    private readonly string _route;
    private readonly Task _acceptTask;
    private readonly IProgress<StudentApplicationUpdateLanProgress>? _progress;

    private StudentApplicationUpdateLanServer(string installerPath,
        IProgress<StudentApplicationUpdateLanProgress>? progress)
    {
        _installerPath = Path.GetFullPath(installerPath);
        _progress = progress;
        if ((File.GetAttributes(_installerPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Teacher 暂存的 Student 安装器不能是重解析点。");
        _route = "/v1/student-updates/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var lanAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicastAddress => unicastAddress.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                              !IPAddress.IsLoopback(address) &&
                              StudentApplicationUpdateCryptography.IsPrivateLanAddress(address))
            .Distinct()
            .ToArray();
        if (lanAddresses.Length == 0)
            throw new InvalidOperationException("未发现活动的校区私有 IPv4 网络接口；没有启动 Student 安装器服务。");

        var listeners = new List<TcpListener>(lanAddresses.Length);
        try
        {
            foreach (var address in lanAddresses)
            {
                var listener = new TcpListener(address, StudentApplicationUpdateCryptography.LocalHttpPort);
                listener.Start(64);
                listeners.Add(listener);
            }
        }
        catch
        {
            foreach (var listener in listeners) listener.Stop();
            throw;
        }
        _listeners = listeners.ToArray();
        _acceptTask = Task.WhenAll(_listeners.Select(listener => AcceptAsync(listener, _shutdown.Token)));
    }

    public static StudentApplicationUpdateLanServer Start(string installerPath,
        IProgress<StudentApplicationUpdateLanProgress>? progress = null) =>
        new(installerPath, progress);

    public async Task<Uri> GetDownloadUriAsync(string target, CancellationToken cancellationToken = default)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(target, out var literal)) addresses = [literal];
        else addresses = await Dns.GetHostAddressesAsync(target, cancellationToken).ConfigureAwait(false);
        foreach (var targetAddress in addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork))
        {
            try
            {
                using var routeSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                routeSocket.Connect(new IPEndPoint(targetAddress, WebsitePolicyAgent.Port));
                if (routeSocket.LocalEndPoint is not IPEndPoint localEndpoint ||
                    !StudentApplicationUpdateCryptography.IsPrivateLanAddress(localEndpoint.Address))
                    continue;
                var uri = new UriBuilder(Uri.UriSchemeHttp, localEndpoint.Address.ToString(),
                    StudentApplicationUpdateCryptography.LocalHttpPort, _route).Uri;
                _ = StudentApplicationUpdateCryptography.ValidateLocalDownloadUrl(uri.AbsoluteUri);
                return uri;
            }
            catch (SocketException) { }
        }
        throw new InvalidDataException($"无法为目标 {target} 选择校区内网教师地址；请检查电脑名解析和网络路由。");
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (var listener in _listeners) listener.Stop();
        try { await _acceptTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        _shutdown.Dispose();
        _connectionLimit.Dispose();
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            try { await _connectionLimit.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                break;
            }
            _ = HandleAndReleaseAsync(client, cancellationToken);
        }
    }

    private async Task HandleAndReleaseAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try { await HandleClientAsync(client, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or
                                          UnauthorizedAccessException or ObjectDisposedException) { }
        finally
        {
            client.Dispose();
            try { _connectionLimit.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var headerBuffer = new byte[MaximumHeaderBytes];
        var totalBytes = 0;
        var headerEnd = -1;
        while (totalBytes < headerBuffer.Length && headerEnd < 0)
        {
            var headerBytesRead = await stream.ReadAsync(headerBuffer.AsMemory(totalBytes), headerTimeout.Token)
                .ConfigureAwait(false);
            if (headerBytesRead == 0) return;
            totalBytes += headerBytesRead;
            headerEnd = headerBuffer.AsSpan(0, totalBytes).IndexOf("\r\n\r\n"u8);
        }
        if (headerEnd < 0)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "invalid request", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        var requestLine = Encoding.ASCII.GetString(headerBuffer, 0, headerEnd).Split("\r\n", 2)[0]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || requestLine[0] != "GET" || requestLine[1] != _route ||
            requestLine[2] is not ("HTTP/1.0" or "HTTP/1.1"))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "not found", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var attributes = File.GetAttributes(_installerPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            await WriteTextResponseAsync(stream, 410, "Gone", "artifact unavailable", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        await using var artifact = new FileStream(_installerPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var responseHeader = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {artifact.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(responseHeader, cancellationToken).ConfigureAwait(false);
        var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        var target = remoteAddress?.IsIPv4MappedToIPv6 == true
            ? remoteAddress.MapToIPv4().ToString()
            : remoteAddress?.ToString() ?? "学生电脑";
        var buffer = new byte[64 * 1024];
        long bytesSent = 0, lastReportedBytes = 0;
        int bytesRead;
        while ((bytesRead = await artifact.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            bytesSent += bytesRead;
            if (bytesSent - lastReportedBytes >= 1024 * 1024 || bytesSent == artifact.Length)
            {
                _progress?.Report(new StudentApplicationUpdateLanProgress(target, bytesSent, artifact.Length));
                lastReportedBytes = bytesSent;
            }
        }
    }

    private static async Task WriteTextResponseAsync(Stream stream, int statusCode, string reason,
        string body, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
    }
}

public static class StudentApplicationUpdateTransport
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(20);
    private const int MaximumResultBytes = StudentAgentResponseCryptography.MaximumEnvelopeBytes;

    public static async Task<IReadOnlyList<StudentApplicationUpdatePushResult>> PushAsync(
        IEnumerable<string> targets, string campusId, string expectedAgentVersion, RSA campusPrivateKey,
        Func<string, CancellationToken, Task<(Guid CommandId, string SignedCommand)>> createSignedCommand,
        CancellationToken cancellationToken = default,
        StudentAgentIdentityTrustStore? identityTrustStore = null,
        IProgress<StudentApplicationUpdateProgress>? progress = null,
        IReadOnlyDictionary<string, StudentApplicationUpdateSourceVersions>? sourceVersions = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(campusPrivateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAgentVersion);
        ArgumentNullException.ThrowIfNull(createSignedCommand);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        _ = ApplicationReleaseClient.CompareVersions(expectedAgentVersion, expectedAgentVersion);
        var validatedTargets = WebsitePolicyTransport.NormalizeTargets(targets);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        using var client = new HttpClient(handler) { Timeout = RequestTimeout };
        using var concurrencyLimit = new SemaphoreSlim(8, 8);
        var completedTargets = 0;
        progress?.Report(new StudentApplicationUpdateProgress("", 0, validatedTargets.Count,
            "正在连接学生电脑"));
        var tasks = validatedTargets.Select(async target =>
        {
            await concurrencyLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new StudentApplicationUpdateProgress(target, Volatile.Read(ref completedTargets),
                validatedTargets.Count, "正在连接学生 Agent"));
            try
            {
                var trustStore = identityTrustStore ?? new StudentAgentIdentityTrustStore();
                var pinnedKey = trustStore.FindTrustedPublicKey(campusId, target);
                if (pinnedKey is null)
                    throw new InvalidDataException("此电脑尚未固定可信 Agent 身份，不能远程自动更新。");
                if (sourceVersions is null || !sourceVersions.TryGetValue(target, out var source) ||
                    source.StudentUpdateProtocolVersion != WebsitePolicyStatusCryptography.CurrentStudentUpdateProtocolVersion)
                    throw new InvalidDataException("缺少可信的更新前版本或原子更新协议能力读回；已停止下发。");
                var command = await createSignedCommand(target, cancellationToken).ConfigureAwait(false);
                if (command.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(command.SignedCommand))
                    throw new InvalidDataException("教师端没有生成有效的学生更新命令。");
                var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port, "/v1/update").Uri;
                using var content = new StringContent(command.SignedCommand, Encoding.UTF8, "application/json");
                HttpResponseMessage responseMessage;
                try
                {
                    responseMessage = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    var uncertainReadback = await WaitForUpdateTerminalAsync(client, target, campusId,
                        campusPrivateKey, pinnedKey, command.CommandId, waitForProgress: true,
                        cancellationToken, progress, () => Volatile.Read(ref completedTargets),
                        validatedTargets.Count, maxWait: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    return ResolveUncertainUpdateResult(target, uncertainReadback, source,
                        expectedAgentVersion,
                        exception is TaskCanceledException
                            ? "等待学生端更新响应超时。"
                            : "教师端没有收到学生端更新响应。");
                }
                using var response = responseMessage;
                var resultText = await ReadBoundedTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
                {
                    var failedReadback = await WaitForUpdateTerminalAsync(client, target, campusId,
                        campusPrivateKey, pinnedKey, command.CommandId,
                        waitForProgress: false, cancellationToken, progress,
                        () => Volatile.Read(ref completedTargets), validatedTargets.Count).ConfigureAwait(false);
                    if (failedReadback is { State: "rolledBack" } rolledBack)
                    {
                        var restored = rolledBack.MatchesPinnedKey &&
                                       MatchesRolledBackVersions(rolledBack.Status, source);
                        return new StudentApplicationUpdatePushResult(target, false,
                            restored
                                ? $"更新失败，已确认 StudentSetup {source.StudentSetupVersion} 与 Student Agent {source.AgentVersion} 均恢复到更新前版本。"
                                : "事务报告已回滚，但签名状态或恢复后的版本与更新前记录不一致；需人工核对。",
                            NeedsReview: !restored, IdentityCandidate: rolledBack.IdentityCandidate,
                            RolledBack: restored);
                    }
                    if (failedReadback is { State: "committed" } committed)
                    {
                        var committedTarget = committed.MatchesPinnedKey &&
                            MatchesCommittedVersions(committed.Status, source, expectedAgentVersion);
                        if (committedTarget)
                            return new StudentApplicationUpdatePushResult(target, true,
                                "StudentSetup 与 Student Agent 的本次签名事务已确认提交。",
                                IdentityCandidate: committed.IdentityCandidate);
                    }
                    if (failedReadback is { State: "needsReview" } ||
                        failedReadback is { State: "inProgress" } || failedReadback is null ||
                        failedReadback is { State: "committed" } ||
                        (int)response.StatusCode >= 500)
                        return new StudentApplicationUpdatePushResult(target, false,
                            "学生端更新没有返回可确认的提交或回滚终态；需人工核对恢复状态。",
                            NeedsReview: true, IdentityCandidate: failedReadback?.IdentityCandidate);
                    return new StudentApplicationUpdatePushResult(target, false,
                        $"HTTP {(int)response.StatusCode}：{resultText}");
                }
                var restartPending = response.StatusCode == HttpStatusCode.Accepted;
                var verifiedUpdate = StudentAgentResponseCryptography.Verify<StudentApplicationUpdateResponse>(resultText,
                    pinnedKey);
                StudentApplicationUpdateResponseCryptography.Validate(verifiedUpdate.Payload, campusId,
                    command.CommandId, expectedAgentVersion, restartPending, DateTimeOffset.UtcNow);
                var updateIdentity = CreateIdentityCandidate(target, campusId, verifiedUpdate, pinnedKey);
                if (restartPending)
                {
                    progress?.Report(new StudentApplicationUpdateProgress(target,
                        Volatile.Read(ref completedTargets), validatedTargets.Count,
                        "已收到更新命令，正在安装并重启 Agent"));
                    var readback = await WaitForUpdateTerminalAsync(client, target, campusId, campusPrivateKey,
                        pinnedKey, command.CommandId, waitForProgress: true,
                        cancellationToken, progress, () => Volatile.Read(ref completedTargets),
                        validatedTargets.Count).ConfigureAwait(false);
                    if (readback is null)
                        return new StudentApplicationUpdatePushResult(target, false,
                            "未能读取到本次原子更新事务的签名终态；需核对任务、ACL、端口和启动日志。",
                            NeedsReview: true, IdentityCandidate: updateIdentity);
                    var candidate = readback.IdentityCandidate ?? updateIdentity;
                    if (readback.State == "committed")
                    {
                        var targetVersionsMatch = MatchesCommittedVersions(readback.Status, source,
                            expectedAgentVersion) &&
                            string.Equals(readback.Status.AgentVersion, verifiedUpdate.Payload.AgentVersion,
                                StringComparison.Ordinal);
                        var succeeded = readback.MatchesPinnedKey && verifiedUpdate.MatchesPinnedKey &&
                                        targetVersionsMatch;
                        return new StudentApplicationUpdatePushResult(target, succeeded,
                            succeeded
                                ? $"StudentSetup 与 Student Agent {expectedAgentVersion} 均已通过本次签名事务确认提交。"
                                : "事务报告已提交，但签名身份或最终版本与目标不一致；需人工核对。",
                            NeedsReview: !succeeded, IdentityCandidate: candidate);
                    }
                    if (readback.State == "rolledBack")
                    {
                        var restored = readback.MatchesPinnedKey &&
                                       MatchesRolledBackVersions(readback.Status, source);
                        return new StudentApplicationUpdatePushResult(target, false,
                            restored
                                ? $"更新失败，已确认 StudentSetup {source.StudentSetupVersion} 与 Student Agent {source.AgentVersion} 均恢复到更新前版本。"
                                : "事务报告已回滚，但签名状态或恢复后的版本与更新前记录不一致；需人工核对。",
                            NeedsReview: !restored, IdentityCandidate: candidate, RolledBack: restored);
                    }
                    return new StudentApplicationUpdatePushResult(target, false,
                        readback.State == "needsReview"
                            ? "学生端原子更新事务需要人工核对；恢复材料已保留。"
                            : "本次原子更新事务未返回可确认的提交或回滚结果；需人工核对。",
                        NeedsReview: true, IdentityCandidate: candidate);
                }
                var completedReadback = await WaitForUpdateTerminalAsync(client, target, campusId,
                    campusPrivateKey, pinnedKey, command.CommandId, waitForProgress: false,
                    cancellationToken, progress, () => Volatile.Read(ref completedTargets),
                    validatedTargets.Count).ConfigureAwait(false);
                if (completedReadback is { State: "committed" } committedReadback)
                {
                    var committed = verifiedUpdate.MatchesPinnedKey && committedReadback.MatchesPinnedKey &&
                                   MatchesCommittedVersions(committedReadback.Status, source, expectedAgentVersion) &&
                                   string.Equals(committedReadback.Status.AgentVersion,
                                       verifiedUpdate.Payload.AgentVersion, StringComparison.Ordinal);
                    return new StudentApplicationUpdatePushResult(target, committed,
                        committed
                            ? verifiedUpdate.Payload.Message
                            : "事务报告已提交，但签名身份或最终版本与事务清单不一致；需人工核对。",
                        NeedsReview: !committed,
                        IdentityCandidate: committedReadback.IdentityCandidate ?? updateIdentity);
                }
                if (completedReadback is { State: "rolledBack" } completedRollback)
                {
                    var restored = verifiedUpdate.MatchesPinnedKey && completedRollback.MatchesPinnedKey &&
                                   MatchesRolledBackVersions(completedRollback.Status, source);
                    return new StudentApplicationUpdatePushResult(target, false,
                        restored
                            ? $"更新失败，已确认 StudentSetup {source.StudentSetupVersion} 与 Student Agent {source.AgentVersion} 均恢复到更新前版本。"
                            : "事务报告已回滚，但签名状态或恢复后的版本与更新前记录不一致；需人工核对。",
                        NeedsReview: !restored,
                        IdentityCandidate: completedRollback.IdentityCandidate ?? updateIdentity,
                        RolledBack: restored);
                }
                return new StudentApplicationUpdatePushResult(target, false,
                    completedReadback is { State: "needsReview" }
                        ? "学生端更新事务需要人工核对；恢复材料已保留。"
                        : "更新回执已签名，但没有取得可验证的事务终态；需人工核对。",
                    NeedsReview: true,
                    IdentityCandidate: completedReadback?.IdentityCandidate ?? updateIdentity);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                return new StudentApplicationUpdatePushResult(target, false,
                    exception is TaskCanceledException ? "连接学生 Agent 或等待更新回执超时；请检查 Agent、TCP 39174 和目标电脑网络。" :
                    "无法联系学生端更新 Agent：" + exception.Message, NeedsReview: true);
            }
            catch (Exception exception) when (exception is SocketException or IOException or
                                              InvalidDataException or
                                              UnauthorizedAccessException or CryptographicException or
                                              InvalidOperationException or JsonException)
            {
                var detail = exception switch
                {
                    SocketException => "无法解析或选择校区内网目标地址：" + exception.Message,
                    InvalidDataException => "学生更新请求或回执校验失败：" + exception.Message,
                    _ => "学生更新目标处理失败：" + exception.Message
                };
                return new StudentApplicationUpdatePushResult(target, false, detail, NeedsReview: true);
            }
            finally
            {
                var completed = Interlocked.Increment(ref completedTargets);
                progress?.Report(new StudentApplicationUpdateProgress(target, completed,
                    validatedTargets.Count, "已完成此电脑处理"));
                concurrencyLimit.Release();
            }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static async Task<UpdateTerminalReadback?> WaitForUpdateTerminalAsync(HttpClient client, string target,
        string campusId, RSA campusPrivateKey, string pinnedAgentPublicKeyPem, Guid commandId,
        bool waitForProgress,
        CancellationToken cancellationToken, IProgress<StudentApplicationUpdateProgress>? progress,
        Func<int> getCompletedTargets, int totalTargets, TimeSpan? maxWait = null)
    {
        var address = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
            WebsitePolicyAgent.StatusPathV4).Uri;
        var deadline = DateTimeOffset.UtcNow + (maxWait ?? TimeSpan.FromSeconds(100));
        var receiptStartedUtc = DateTimeOffset.UtcNow;
        var waitTimer = Stopwatch.StartNew();
        var lastProgressAt = TimeSpan.Zero;
        var lastTimingAt = TimeSpan.Zero;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var request = new WebsitePolicyStatusRequest(4, WebsitePolicyStatusCryptography.RequestPurposeV4,
                    campusId, Guid.NewGuid(), DateTimeOffset.UtcNow, commandId);
                var signedRequest = WebsitePolicyStatusCryptography.SignRequest(request, campusPrivateKey);
                using var content = new StringContent(signedRequest, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(address, content, attempt.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    await ReportUpdatePhaseTimingsAsync(client, target, campusId, pinnedAgentPublicKeyPem,
                        campusPrivateKey, commandId, receiptStartedUtc, waitTimer, "failed", progress,
                        getCompletedTargets(), totalTargets, cancellationToken).ConfigureAwait(false);
                    return null;
                }
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("学生 Agent 原子更新状态端点尚未就绪。");
                var resultText = await ReadBoundedTextAsync(response.Content, attempt.Token).ConfigureAwait(false);
                var verified = StudentAgentResponseCryptography.Verify<StudentAgentStatusResponse>(resultText,
                    pinnedAgentPublicKeyPem);
                WebsitePolicyStatusCryptography.ValidateResponse(verified.Payload, request, DateTimeOffset.UtcNow);
                var transaction = verified.Payload.UpdateTransaction
                                  ?? throw new InvalidDataException("学生 Agent 缺少本次更新事务回执。");
                var readback = new UpdateTerminalReadback(transaction.State, verified.MatchesPinnedKey,
                    verified.Payload, CreateIdentityCandidate(target, campusId, verified, pinnedAgentPublicKeyPem));
                if (!waitForProgress || transaction.State is "committed" or "rolledBack" or "needsReview")
                {
                    await ReportUpdatePhaseTimingsAsync(client, target, campusId, pinnedAgentPublicKeyPem,
                        campusPrivateKey, commandId, receiptStartedUtc, waitTimer,
                        "succeeded", progress,
                        getCompletedTargets(), totalTargets, cancellationToken)
                        .ConfigureAwait(false);
                    return readback;
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or CryptographicException) { }
            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            if (waitTimer.Elapsed - lastProgressAt >= TimeSpan.FromSeconds(5))
            {
                progress?.Report(new StudentApplicationUpdateProgress(target, getCompletedTargets(), totalTargets,
                    $"正在核对提交或回滚状态（已等待 {waitTimer.Elapsed.TotalSeconds:0} 秒）"));
                lastProgressAt = waitTimer.Elapsed;
            }
            if (waitTimer.Elapsed - lastTimingAt >= TimeSpan.FromSeconds(5))
            {
                await ReportUpdatePhaseTimingsAsync(client, target, campusId, pinnedAgentPublicKeyPem,
                    campusPrivateKey, commandId, receiptStartedUtc, waitTimer, "inProgress", progress,
                    getCompletedTargets(), totalTargets, cancellationToken)
                    .ConfigureAwait(false);
                lastTimingAt = waitTimer.Elapsed;
            }
        }
        await ReportUpdatePhaseTimingsAsync(client, target, campusId, pinnedAgentPublicKeyPem,
            campusPrivateKey, commandId, receiptStartedUtc, waitTimer, "failed", progress,
            getCompletedTargets(), totalTargets, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static async Task ReportUpdatePhaseTimingsAsync(HttpClient client, string target, string campusId,
        string pinnedAgentPublicKeyPem, RSA campusPrivateKey, Guid commandId, DateTimeOffset receiptStartedUtc,
        Stopwatch receiptTimer, string outcome,
        IProgress<StudentApplicationUpdateProgress>? progress, int completedTargets, int totalTargets,
        CancellationToken cancellationToken)
    {
        if (progress is null) return;
        var snapshot = await TryReadUpdatePhaseTimingsAsync(client, target, campusId, pinnedAgentPublicKeyPem,
            campusPrivateKey, commandId, cancellationToken).ConfigureAwait(false);
        var receiptTiming = new StudentUpdatePhaseTiming(StudentUpdatePhase.TeacherReceipt, receiptStartedUtc,
            outcome == "inProgress" ? null : DateTimeOffset.UtcNow,
            outcome == "inProgress" ? null : receiptTimer.ElapsedMilliseconds, null, outcome);
        var phases = (snapshot?.Phases ?? Array.Empty<StudentUpdatePhaseTiming>())
            .Where(phase => phase.Phase != StudentUpdatePhase.TeacherReceipt)
            .Append(receiptTiming).ToArray();
        progress.Report(new StudentApplicationUpdateProgress(target, completedTargets, totalTargets,
            "正在核对教师端签名回执", phases, commandId, snapshot?.Environment));
    }

    private static async Task<StudentUpdatePhaseTimingSnapshot?> TryReadUpdatePhaseTimingsAsync(HttpClient client,
        string target, string campusId, string pinnedAgentPublicKeyPem, RSA campusPrivateKey, Guid commandId,
        CancellationToken cancellationToken)
    {
        var (v2Unsupported, v2Snapshot) = await TryReadUpdatePhaseTimingsV2Async(client, target, campusId,
            pinnedAgentPublicKeyPem, campusPrivateKey, commandId, cancellationToken).ConfigureAwait(false);
        if (v2Snapshot is not null) return v2Snapshot;
        if (!v2Unsupported) return null;

        var address = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
            WebsitePolicyAgent.StudentUpdateTimingPath).Uri;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(2));
        var request = new WebsitePolicyStatusRequest(4, WebsitePolicyStatusCryptography.RequestPurposeV4,
            campusId, Guid.NewGuid(), DateTimeOffset.UtcNow, commandId);
        try
        {
            var signedRequest = WebsitePolicyStatusCryptography.SignRequest(request, campusPrivateKey);
            using var content = new StringContent(signedRequest, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(address, content, attempt.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode) return null;
            var resultText = await ReadBoundedTextAsync(response.Content, attempt.Token).ConfigureAwait(false);
            var verified = StudentAgentResponseCryptography.Verify<StudentUpdatePhaseTimingResponse>(resultText,
                pinnedAgentPublicKeyPem);
            if (!verified.MatchesPinnedKey) return null;
            StudentUpdatePhaseTimingCryptography.Validate(verified.Payload, request, campusId, DateTimeOffset.UtcNow);
            return new StudentUpdatePhaseTimingSnapshot(1, verified.Payload.CommandId,
                verified.Payload.CollectedUtc, verified.Payload.Phases);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          InvalidDataException or JsonException or CryptographicException or
                                          IOException or UriFormatException) { return null; }
    }

    private static async Task<(bool Unsupported, StudentUpdatePhaseTimingSnapshot? Snapshot)>
        TryReadUpdatePhaseTimingsV2Async(
        HttpClient client, string target, string campusId, string pinnedAgentPublicKeyPem,
        RSA campusPrivateKey, Guid commandId, CancellationToken cancellationToken)
    {
        var address = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
            WebsitePolicyAgent.StudentUpdateTimingPathV2).Uri;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(2));
        var request = new WebsitePolicyStatusRequest(4, WebsitePolicyStatusCryptography.RequestPurposeV4,
            campusId, Guid.NewGuid(), DateTimeOffset.UtcNow, commandId);
        try
        {
            var signedRequest = WebsitePolicyStatusCryptography.SignRequest(request, campusPrivateKey);
            using var content = new StringContent(signedRequest, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(address, content, attempt.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return (true, null);
            if (!response.IsSuccessStatusCode) return (false, null);
            var resultText = await ReadBoundedTextAsync(response.Content, attempt.Token).ConfigureAwait(false);
            var verified = StudentAgentResponseCryptography.Verify<StudentUpdatePhaseTimingResponseV2>(
                resultText, pinnedAgentPublicKeyPem);
            if (!verified.MatchesPinnedKey) return (false, null);
            StudentUpdatePhaseTimingCryptography.ValidateV2(verified.Payload, request, campusId,
                DateTimeOffset.UtcNow);
            return (false, new StudentUpdatePhaseTimingSnapshot(1, verified.Payload.CommandId,
                verified.Payload.CollectedUtc, verified.Payload.Phases, verified.Payload.Environment));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (false, null); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          InvalidDataException or JsonException or CryptographicException or
                                          IOException or UriFormatException) { return (false, null); }
    }

    private static StudentAgentIdentityTrustCandidate CreateIdentityCandidate<T>(string target, string campusId,
        VerifiedStudentAgentResponse<T> verified, string? pinnedAgentPublicKeyPem) =>
        new(target, campusId, verified.PublicKeyPem, verified.Fingerprint, pinnedAgentPublicKeyPem is null ? null :
            StudentAgentResponseCryptography.GetFingerprint(pinnedAgentPublicKeyPem));

    private static bool MatchesRolledBackVersions(StudentAgentStatusResponse status,
        StudentApplicationUpdateSourceVersions source)
    {
        var transaction = status.UpdateTransaction;
        return transaction is not null &&
               string.Equals(transaction.PreviousSetupVersion, source.StudentSetupVersion, StringComparison.Ordinal) &&
               string.Equals(transaction.PreviousAgentVersion, source.AgentVersion, StringComparison.Ordinal) &&
               string.Equals(status.StudentSetupVersion, source.StudentSetupVersion, StringComparison.Ordinal) &&
               string.Equals(status.AgentVersion, source.AgentVersion, StringComparison.Ordinal);
    }

    private static bool MatchesCommittedVersions(StudentAgentStatusResponse status,
        StudentApplicationUpdateSourceVersions source, string expectedVersion)
    {
        var transaction = status.UpdateTransaction;
        if (transaction is null) return false;
        var expectedTargetAgentVersion = ApplicationReleaseClient.CompareVersions(source.AgentVersion,
            expectedVersion) > 0 ? source.AgentVersion : expectedVersion;
        return string.Equals(transaction.TargetSetupVersion, expectedVersion, StringComparison.Ordinal) &&
               string.Equals(transaction.TargetAgentVersion, expectedTargetAgentVersion, StringComparison.Ordinal) &&
               string.Equals(status.StudentSetupVersion, transaction.TargetSetupVersion, StringComparison.Ordinal) &&
               string.Equals(status.AgentVersion, transaction.TargetAgentVersion, StringComparison.Ordinal);
    }

    private static StudentApplicationUpdatePushResult ResolveUncertainUpdateResult(string target,
        UpdateTerminalReadback? readback, StudentApplicationUpdateSourceVersions source, string expectedVersion,
        string failureDetail)
    {
        if (readback is { State: "committed" } committed && committed.MatchesPinnedKey &&
            MatchesCommittedVersions(committed.Status, source, expectedVersion))
            return new StudentApplicationUpdatePushResult(target, true,
                "虽未收到更新 HTTP 响应，但签名事务已确认提交，且目标版本读回一致。",
                IdentityCandidate: committed.IdentityCandidate);
        if (readback is { State: "rolledBack" } rolledBack)
        {
            var restored = rolledBack.MatchesPinnedKey && MatchesRolledBackVersions(rolledBack.Status, source);
            return new StudentApplicationUpdatePushResult(target, false,
                restored
                    ? $"{failureDetail}；已确认 StudentSetup {source.StudentSetupVersion} 与 Student Agent {source.AgentVersion} 恢复到更新前版本。"
                    : $"{failureDetail}；事务报告已回滚，但恢复后的版本或签名身份不一致，需人工核对。",
                NeedsReview: !restored, IdentityCandidate: rolledBack.IdentityCandidate, RolledBack: restored);
        }
        return new StudentApplicationUpdatePushResult(target, false,
            readback is { State: "needsReview" }
                ? "学生端更新事务需要人工核对；恢复材料已保留。"
                : $"{failureDetail} 没有取得可验证的事务终态回执。",
            NeedsReview: true, IdentityCandidate: readback?.IdentityCandidate);
    }

    private sealed record UpdateTerminalReadback(string State, bool MatchesPinnedKey,
        StudentAgentStatusResponse Status, StudentAgentIdentityTrustCandidate IdentityCandidate);

    private static async Task<string> ReadBoundedTextAsync(HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResultBytes)
            throw new InvalidDataException("学生更新回执超过大小限制。");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + bytesRead > MaximumResultBytes)
                throw new InvalidDataException("学生更新回执超过大小限制。");
            buffer.Write(chunk, 0, bytesRead);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
