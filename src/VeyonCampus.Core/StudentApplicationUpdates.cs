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

public sealed record StudentApplicationUpdateDownloadRequest(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid CommandId,
    string ReleaseSha256,
    string Path,
    string? Range,
    string? IfRange,
    DateTimeOffset IssuedUtc,
    string Nonce);

public sealed record StudentApplicationUpdateDownloadCapabilityResponse(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid Nonce,
    DateTimeOffset CollectedUtc,
    int DownloadRequestProtocolVersion);

public static class StudentApplicationUpdateDownloadCapabilityCryptography
{
    public const string Purpose = "VeyonCampus.StudentApplicationUpdateDownloadCapability.v1";
    public const int SchemaVersion = 1;

    public static void Validate(StudentApplicationUpdateDownloadCapabilityResponse response,
        string expectedCampusId, Guid expectedNonce, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        var now = nowUtc.ToUniversalTime();
        if (response.SchemaVersion != SchemaVersion || response.Purpose != Purpose ||
            response.CampusId != expectedCampusId || response.Nonce != expectedNonce ||
            response.CollectedUtc.Offset != TimeSpan.Zero || response.CollectedUtc > now.AddMinutes(2) ||
            response.CollectedUtc < now.AddMinutes(-10) ||
            response.DownloadRequestProtocolVersion != StudentApplicationUpdateCryptography.DownloadRequestProtocolVersion)
            throw new InvalidDataException("学生更新下载能力回执无效或不受支持。");
    }
}

public sealed class StudentApplicationUpdateDownloadRequestSigner
{
    public const string HeaderName = "X-Veyon-Update-Request";
    public const string Purpose = "VeyonCampus.StudentApplicationUpdateDownloadRequest.v1";
    public const int ProtocolVersion = StudentApplicationUpdateCryptography.DownloadRequestProtocolVersion;
    private readonly RSA _agentPrivateKey;
    private readonly string _campusId;
    private readonly Guid _commandId;
    private readonly string _releaseSha256;

    public StudentApplicationUpdateDownloadRequestSigner(string campusId, Guid commandId, string releaseSha256,
        RSA agentPrivateKey)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        if (commandId == Guid.Empty) throw new ArgumentException("更新命令编号不能为空。", nameof(commandId));
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseSha256);
        if (!Regex.IsMatch(releaseSha256, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("发布摘要格式无效。", nameof(releaseSha256));
        _agentPrivateKey = agentPrivateKey ?? throw new ArgumentNullException(nameof(agentPrivateKey));
        _campusId = campusId;
        _commandId = commandId;
        _releaseSha256 = releaseSha256.ToLowerInvariant();
    }

    public string Sign(Uri downloadUri, string? range, string? ifRange)
    {
        ArgumentNullException.ThrowIfNull(downloadUri);
        if (downloadUri.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(downloadUri.Query) ||
            !string.IsNullOrEmpty(downloadUri.Fragment))
            throw new InvalidDataException("学生更新下载请求 URI 无效。");
        var request = new StudentApplicationUpdateDownloadRequest(ProtocolVersion, Purpose, _campusId,
            _commandId, _releaseSha256, downloadUri.AbsolutePath, range, ifRange, DateTimeOffset.UtcNow,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        return StudentAgentResponseCryptography.Sign(request, _agentPrivateKey, 4096);
    }
}

public static class StudentApplicationUpdateDownloadRequestCryptography
{
    public const int MaximumEnvelopeBytes = 4096;
    private static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(1);

    public static StudentApplicationUpdateDownloadRequest Verify(string signedJson, string agentPublicKeyPem,
        string expectedCampusId, Guid expectedCommandId, string expectedReleaseSha256, string expectedPath,
        string? expectedRange, string? expectedIfRange, DateTimeOffset commandExpiresUtc,
        DateTimeOffset? nowUtc = null)
    {
        var verified = StudentAgentResponseCryptography.Verify<StudentApplicationUpdateDownloadRequest>(
            signedJson, agentPublicKeyPem, MaximumEnvelopeBytes);
        if (!verified.MatchesPinnedKey)
            throw new InvalidDataException("学生更新下载请求不是来自已固定的 Agent 身份。");
        var request = verified.Payload;
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        WebsitePolicySigningKeyStore.ValidateCampusId(expectedCampusId);
        if (request is null || string.IsNullOrWhiteSpace(request.ReleaseSha256) ||
            string.IsNullOrWhiteSpace(request.Path) || string.IsNullOrWhiteSpace(request.Nonce) ||
            request.SchemaVersion != StudentApplicationUpdateDownloadRequestSigner.ProtocolVersion ||
            request.Purpose != StudentApplicationUpdateDownloadRequestSigner.Purpose ||
            request.CampusId != expectedCampusId || request.CommandId != expectedCommandId ||
            !string.Equals(request.ReleaseSha256, expectedReleaseSha256, StringComparison.OrdinalIgnoreCase) ||
            request.Path != expectedPath || request.Range != expectedRange || request.IfRange != expectedIfRange ||
            request.IssuedUtc.Offset != TimeSpan.Zero || request.IssuedUtc > now.Add(MaximumFutureSkew) ||
            request.IssuedUtc < now.Subtract(MaximumRequestAge) || request.IssuedUtc >= commandExpiresUtc ||
            !Regex.IsMatch(request.ReleaseSha256, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(request.Nonce, "^[A-Fa-f0-9]{32}$", RegexOptions.CultureInvariant) ||
            request.Path.Length is < 1 or > 512 || request.Range?.Length > 128 || request.IfRange?.Length > 128)
            throw new InvalidDataException("学生更新下载请求与签名命令、文件或有效期不匹配。");
        return request;
    }
}

public static class StudentApplicationUpdateCryptography
{
    public const int MaximumEnvelopeBytes = 128 * 1024;
    public const int LocalHttpPort = 39175;
    public const int DownloadRequestProtocolVersion = 1;
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
    private sealed record StudentArtifactDownloadResult(long Bytes, long IntegrityHashMilliseconds, int RetryCount);

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
                var downloadRetryCount = 0;
                bool? packageHashVerified = null;
                StudentArtifactDownloadResult downloadedArtifact;
                try
                {
                    using var downloadSigningKey = StudentAgentIdentityKeyStore
                        .LoadOrCreateForSystemAgent(configPath);
                    downloadedArtifact = await DownloadArtifactAsync(
                        localDownloadUri, manifest, installerPath, stagingDirectory,
                        new StudentApplicationUpdateDownloadRequestSigner(command.CampusId, command.CommandId,
                            manifest.Sha256, downloadSigningKey), cancellationToken,
                        (bytes, hashMilliseconds, hashVerified, retryCount) =>
                        {
                            transferredBytes = bytes;
                            integrityHashMilliseconds = hashMilliseconds;
                            packageHashVerified = hashVerified;
                            downloadRetryCount = retryCount;
                        }).ConfigureAwait(false);
                }
                catch
                {
                    lanTransferTiming.Complete(succeeded: false, bytes: transferredBytes,
                        retryCount: downloadRetryCount);
                    if (packageHashVerified is { } hashVerified)
                        StudentUpdatePhaseTimingStore.AddIntegrityHashMeasurement(configPath,
                            command.CommandId, integrityHashMilliseconds, transferredBytes,
                            succeeded: hashVerified);
                    throw;
                }
                lanTransferTiming.Complete(succeeded: true, bytes: downloadedArtifact.Bytes,
                    retryCount: downloadedArtifact.RetryCount);
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
        string installerPath, string stagingDirectory,
        StudentApplicationUpdateDownloadRequestSigner requestSigner, CancellationToken cancellationToken,
        Action<long, long, bool?, int>? reportMeasurements = null)
    {
        var partialPath = Path.Combine(stagingDirectory,
            "." + manifest.Sha256.ToLowerInvariant() + ".partial");
        var downloaded = await StudentApplicationUpdateArtifactTransfer.DownloadAsync(DownloadClient, downloadUri,
            partialPath, installerPath, manifest.Sha256, manifest.SizeBytes, MaximumArtifactBytes,
            cancellationToken, reportMeasurements,
            path => AgentFileSecurity.Secure(path, directory: false, executable: true),
            requestSigner).ConfigureAwait(false);
        return new StudentArtifactDownloadResult(downloaded.NetworkBytes, downloaded.IntegrityHashMilliseconds,
            downloaded.RetryCount);
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
    private const int MaximumRequestsPerGrant = 32;
    private readonly TcpListener[] _listeners;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _connectionLimit = new(24, 24);
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _clientTasks = [];
    private readonly object _grantGate = new();
    private readonly Dictionary<string, DownloadGrant> _downloadGrants = new(StringComparer.Ordinal);
    private readonly FileStream _artifactLease;
    private readonly string _installerPath;
    private readonly string _artifactSha256;
    private readonly string _entityTag;
    private readonly long _artifactSizeBytes;
    private readonly Task _acceptTask;
    private readonly IProgress<StudentApplicationUpdateLanProgress>? _progress;

    private sealed class DownloadGrant(string campusId, Guid commandId, string releaseSha256,
        DateTimeOffset expiresUtc, string? agentPublicKeyPem)
    {
        public string CampusId { get; } = campusId;
        public Guid CommandId { get; } = commandId;
        public string ReleaseSha256 { get; } = releaseSha256;
        public DateTimeOffset ExpiresUtc { get; } = expiresUtc;
        public string? AgentPublicKeyPem { get; } = agentPublicKeyPem;
        public HashSet<string> UsedNonces { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private StudentApplicationUpdateLanServer(string installerPath, string artifactSha256, long artifactSizeBytes,
        IProgress<StudentApplicationUpdateLanProgress>? progress)
    {
        if (artifactSizeBytes is < 1 or > ApplicationReleaseClient.MaximumArtifactBytes)
            throw new InvalidDataException("Teacher 暂存的 Student 安装器大小无效。");
        _entityTag = StudentApplicationUpdateArtifactTransfer.CreateEntityTag(artifactSha256);
        _artifactSha256 = artifactSha256.ToLowerInvariant();
        _artifactSizeBytes = artifactSizeBytes;
        _installerPath = Path.GetFullPath(installerPath);
        _progress = progress;
        if ((File.GetAttributes(_installerPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Teacher 暂存的 Student 安装器不能是重解析点。");
        if (new FileInfo(_installerPath).Length != _artifactSizeBytes)
            throw new InvalidDataException("Teacher 暂存的 Student 安装器大小与签名清单不符。");
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

        var artifactLease = new FileStream(_installerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        if (artifactLease.Length != _artifactSizeBytes)
        {
            artifactLease.Dispose();
            throw new InvalidDataException("Teacher 暂存的 Student 安装器大小与签名清单不符。");
        }
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
            artifactLease.Dispose();
            throw;
        }
        _artifactLease = artifactLease;
        _listeners = listeners.ToArray();
        _acceptTask = Task.WhenAll(_listeners.Select(listener => AcceptAsync(listener, _shutdown.Token)));
    }

    public static StudentApplicationUpdateLanServer Start(string installerPath, string artifactSha256,
        long artifactSizeBytes,
        IProgress<StudentApplicationUpdateLanProgress>? progress = null) =>
        new(installerPath, artifactSha256, artifactSizeBytes, progress);

    public async Task<Uri> GetDownloadUriAsync(string target, string campusId, Guid commandId,
        DateTimeOffset expiresUtc, string? targetAgentPublicKeyPem,
        CancellationToken cancellationToken = default)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        if (commandId == Guid.Empty || expiresUtc.Offset != TimeSpan.Zero || expiresUtc <= DateTimeOffset.UtcNow ||
            expiresUtc - DateTimeOffset.UtcNow > StudentApplicationUpdateCryptography.MaximumCommandLifetime)
            throw new InvalidDataException("学生更新下载授权的命令编号或有效期无效。");
        if (targetAgentPublicKeyPem is not null)
        {
            using var agentKey = RSA.Create();
            try { agentKey.ImportFromPem(targetAgentPublicKeyPem); }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException("学生更新下载授权的 Agent 公钥无效。", exception);
            }
            if (agentKey.KeySize is < 2048 or > 4096)
                throw new InvalidDataException("学生更新下载授权的 Agent 公钥位长无效。");
            targetAgentPublicKeyPem = agentKey.ExportSubjectPublicKeyInfoPem();
        }
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
                    StudentApplicationUpdateCryptography.LocalHttpPort,
                    "/v1/student-updates/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32))).Uri;
                _ = StudentApplicationUpdateCryptography.ValidateLocalDownloadUrl(uri.AbsoluteUri);
                var grant = new DownloadGrant(campusId, commandId, _artifactSha256, expiresUtc,
                    targetAgentPublicKeyPem);
                lock (_grantGate)
                {
                    if (_downloadGrants.Count >= 20_000)
                        throw new InvalidOperationException("本次教师更新的局域网下载授权数量超过安全上限。");
                    _downloadGrants.Add(uri.AbsolutePath, grant);
                }
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
        try { await Task.WhenAll(_clientTasks.ToArray()).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or
                                          UnauthorizedAccessException or ObjectDisposedException) { }
        _shutdown.Dispose();
        _connectionLimit.Dispose();
        lock (_grantGate) _downloadGrants.Clear();
        await _artifactLease.DisposeAsync().ConfigureAwait(false);
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
            _clientTasks.Add(HandleAndReleaseAsync(client, cancellationToken));
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
        var headerLines = Encoding.ASCII.GetString(headerBuffer, 0, headerEnd).Split("\r\n");
        var requestLine = headerLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || requestLine[0] != "GET" ||
            requestLine[2] is not ("HTTP/1.0" or "HTTP/1.1"))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "not found", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        DownloadGrant? grant;
        lock (_grantGate) _downloadGrants.TryGetValue(requestLine[1], out grant);
        if (grant is null || DateTimeOffset.UtcNow >= grant.ExpiresUtc)
        {
            await WriteTextResponseAsync(stream, 410, "Gone", "download authorization expired",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryReadOptionalHeader(headerLines, "Range", out var rangeHeader) ||
            !TryReadOptionalHeader(headerLines, "If-Range", out var ifRangeHeader) ||
            !TryReadOptionalHeader(headerLines, StudentApplicationUpdateDownloadRequestSigner.HeaderName,
                out var signedRequestHeader))
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "invalid range headers", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (grant.AgentPublicKeyPem is null)
        {
            if (signedRequestHeader is not null)
            {
                await WriteTextResponseAsync(stream, 403, "Forbidden", "unexpected download authorization",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            if (signedRequestHeader is null)
            {
                await WriteTextResponseAsync(stream, 403, "Forbidden", "signed download request required",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            StudentApplicationUpdateDownloadRequest request;
            try
            {
                request = StudentApplicationUpdateDownloadRequestCryptography.Verify(signedRequestHeader,
                    grant.AgentPublicKeyPem, grant.CampusId, grant.CommandId, grant.ReleaseSha256,
                    requestLine[1], rangeHeader, ifRangeHeader, grant.ExpiresUtc);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                await WriteTextResponseAsync(stream, 403, "Forbidden", "download request identity mismatch",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            bool nonceAccepted;
            lock (_grantGate)
                nonceAccepted = grant.UsedNonces.Count < MaximumRequestsPerGrant &&
                                grant.UsedNonces.Add(request.Nonce);
            if (!nonceAccepted)
            {
                await WriteTextResponseAsync(stream, 403, "Forbidden", "download request replay rejected",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        var range = StudentApplicationUpdateArtifactTransfer.ResolveRange(rangeHeader, ifRangeHeader,
            _entityTag, _artifactSizeBytes);
        if (range.Kind == StudentApplicationUpdateRangeKind.Invalid)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "invalid range", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (range.Kind == StudentApplicationUpdateRangeKind.Unsatisfiable)
        {
            await WriteRangeNotSatisfiableAsync(stream, _artifactSizeBytes, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        var offset = range.Kind == StudentApplicationUpdateRangeKind.Partial ? range.Offset : 0;
        var responseLength = _artifactSizeBytes - offset;

        var attributes = File.GetAttributes(_installerPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            await WriteTextResponseAsync(stream, 410, "Gone", "artifact unavailable", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        await using var artifact = new FileStream(_installerPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (artifact.Length != _artifactSizeBytes)
        {
            await WriteTextResponseAsync(stream, 410, "Gone", "artifact size changed", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (offset > 0) artifact.Position = offset;
        var statusCode = range.Kind == StudentApplicationUpdateRangeKind.Partial ? 206 : 200;
        var contentRangeHeader = statusCode == 206
            ? $"Content-Range: bytes {offset}-{_artifactSizeBytes - 1}/{_artifactSizeBytes}\r\n"
            : "";
        var responseHeader = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {(statusCode == 206 ? "Partial Content" : "OK")}\r\n" +
            $"Content-Type: application/octet-stream\r\nContent-Length: {responseLength}\r\n" +
            $"Accept-Ranges: bytes\r\nETag: {_entityTag}\r\n{contentRangeHeader}" +
            "Connection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(responseHeader, cancellationToken).ConfigureAwait(false);
        var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        var target = remoteAddress?.IsIPv4MappedToIPv6 == true
            ? remoteAddress.MapToIPv4().ToString()
            : remoteAddress?.ToString() ?? "学生电脑";
        var buffer = new byte[64 * 1024];
        long bytesSent = offset, lastReportedBytes = offset;
        if (offset > 0)
            _progress?.Report(new StudentApplicationUpdateLanProgress(target, offset, _artifactSizeBytes));
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

    private static bool TryReadOptionalHeader(IReadOnlyList<string> headerLines, string name, out string? value)
    {
        value = null;
        for (var index = 1; index < headerLines.Count; index++)
        {
            var line = headerLines[index];
            var separator = line.IndexOf(':');
            if (separator <= 0) return false;
            if (!line[..separator].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (value is not null) return false;
            value = line[(separator + 1)..].Trim();
            if (value.Length == 0) return false;
        }
        return true;
    }

    private static async Task WriteRangeNotSatisfiableAsync(Stream stream, long artifactSizeBytes,
        CancellationToken cancellationToken)
    {
        const string body = "range not satisfiable";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{artifactSizeBytes}\r\n" +
            $"Content-Type: text/plain; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
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
        Func<string, string, int?, CancellationToken,
            Task<(Guid CommandId, string SignedCommand)>> createSignedCommand,
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
        using var handler = new StudentAgentChannelHttpMessageHandler(allowLegacyDirectFallback: true);
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
                var downloadRequestProtocolVersion = await ReadDownloadRequestProtocolVersionAsync(client,
                    target, campusId, campusPrivateKey, pinnedKey, cancellationToken).ConfigureAwait(false);
                var command = await createSignedCommand(target, pinnedKey, downloadRequestProtocolVersion,
                    cancellationToken).ConfigureAwait(false);
                if (command.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(command.SignedCommand))
                    throw new InvalidDataException("教师端没有生成有效的学生更新命令。");
                var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port, "/v1/update").Uri;
                using var content = new StringContent(command.SignedCommand, Encoding.UTF8, "application/json");
                HttpResponseMessage responseMessage;
                using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var postCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var postTask = client.PostAsync(uri, content, postCancellation.Token);
                var progressMonitor = MonitorUpdateProgressWhilePostPendingAsync(client, target, campusId,
                    campusPrivateKey, pinnedKey, command.CommandId, progress,
                    () => Volatile.Read(ref completedTargets), validatedTargets.Count,
                    monitorCancellation.Token);
                var postDeadline = Task.Delay(TimeSpan.FromMinutes(15), cancellationToken);
                try
                {
                    var completedTask = await Task.WhenAny(postTask, postDeadline).ConfigureAwait(false);
                    if (completedTask == postDeadline && !postTask.IsCompleted)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        postCancellation.Cancel();
                        await StopAndObserveProgressMonitorAsync(monitorCancellation, progressMonitor)
                            .ConfigureAwait(false);
                        _ = ObservePostResponseAsync(postTask);
                        return new StudentApplicationUpdatePushResult(target, false,
                            "学生端更新请求超过 15 分钟仍未返回；本次已停止等待，更新事务可重新读取核对。",
                            NeedsReview: true);
                    }
                    await StopAndObserveProgressMonitorAsync(monitorCancellation, progressMonitor)
                        .ConfigureAwait(false);
                    responseMessage = await postTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    postCancellation.Cancel();
                    await StopAndObserveProgressMonitorAsync(monitorCancellation, progressMonitor)
                        .ConfigureAwait(false);
                    _ = ObservePostResponseAsync(postTask);
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    await StopAndObserveProgressMonitorAsync(monitorCancellation, progressMonitor)
                        .ConfigureAwait(false);
                    var uncertainReadback = await WaitForUpdateTerminalAsync(client, target, campusId,
                        campusPrivateKey, pinnedKey, command.CommandId, waitForProgress: true,
                        cancellationToken, progress, () => Volatile.Read(ref completedTargets),
                        validatedTargets.Count, maxWait: TimeSpan.FromSeconds(20),
                        allowStatusPolling: false).ConfigureAwait(false);
                    return ResolveUncertainUpdateResult(target, uncertainReadback, source,
                        expectedAgentVersion,
                        exception is TaskCanceledException
                            ? "等待学生端更新响应超时。"
                            : "教师端没有收到学生端更新响应。");
                }
                using var response = responseMessage;
                var resultText = await ReadBoundedTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted &&
                    string.IsNullOrWhiteSpace(resultText))
                {
                    var preflightFailure = await TryResolvePreflightFailureAsync(client, target, campusId,
                        pinnedKey, campusPrivateKey, command.CommandId, response.StatusCode, progress,
                        () => Volatile.Read(ref completedTargets), validatedTargets.Count,
                        cancellationToken).ConfigureAwait(false);
                    if (preflightFailure is not null) return preflightFailure;

                    var emptyReceiptReadback = await WaitForUpdateTerminalAsync(client, target, campusId,
                        campusPrivateKey, pinnedKey, command.CommandId, waitForProgress: true,
                        cancellationToken, progress, () => Volatile.Read(ref completedTargets),
                        validatedTargets.Count, maxWait: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                    var latePreflightFailure = await TryResolvePreflightFailureAsync(client, target, campusId,
                        pinnedKey, campusPrivateKey, command.CommandId, response.StatusCode, progress,
                        () => Volatile.Read(ref completedTargets), validatedTargets.Count,
                        cancellationToken).ConfigureAwait(false);
                    if (latePreflightFailure is not null) return latePreflightFailure;
                    return ResolveUncertainUpdateResult(target, emptyReceiptReadback, source,
                        expectedAgentVersion,
                        "学生 Agent 返回了空的更新签名回执；已停止长时间等待并在 15 秒内核对事务状态。");
                }
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
                {
                    var httpFailure = FormatUpdateHttpFailure(response.StatusCode, resultText);
                    var preflightFailure = await TryResolvePreflightFailureAsync(client, target, campusId,
                        pinnedKey, campusPrivateKey, command.CommandId, response.StatusCode, progress,
                        () => Volatile.Read(ref completedTargets), validatedTargets.Count,
                        cancellationToken).ConfigureAwait(false);
                    if (preflightFailure is not null) return preflightFailure with
                    {
                        Detail = $"{preflightFailure.Detail} Agent 响应：{httpFailure}"
                    };

                    var failedReadback = await WaitForUpdateTerminalAsync(client, target, campusId,
                        campusPrivateKey, pinnedKey, command.CommandId,
                        waitForProgress: false, cancellationToken, progress,
                        () => Volatile.Read(ref completedTargets), validatedTargets.Count,
                        maxWait: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                    if (failedReadback is null)
                    {
                        var latePreflightFailure = await TryResolvePreflightFailureAsync(client, target, campusId,
                            pinnedKey, campusPrivateKey, command.CommandId, response.StatusCode, progress,
                            () => Volatile.Read(ref completedTargets), validatedTargets.Count,
                            cancellationToken).ConfigureAwait(false);
                        if (latePreflightFailure is not null) return latePreflightFailure with
                        {
                            Detail = $"{latePreflightFailure.Detail} Agent 响应：{httpFailure}"
                        };
                    }
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
                            $"{httpFailure}；且未读取到本次命令对应的提交或回滚终态，需人工核对恢复状态。",
                            NeedsReview: true, IdentityCandidate: failedReadback?.IdentityCandidate);
                    return new StudentApplicationUpdatePushResult(target, false,
                        httpFailure);
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
                    var (readback, preflightFailure) = await WaitForUpdateTerminalOrPreflightFailureAsync(
                        client, target, campusId, campusPrivateKey, pinnedKey, command.CommandId,
                        HttpStatusCode.Accepted, cancellationToken, progress,
                        () => Volatile.Read(ref completedTargets), validatedTargets.Count,
                        maxWait: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                    if (preflightFailure is not null) return preflightFailure;
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
                                              InvalidOperationException or ArgumentException or JsonException)
            {
                var detail = exception switch
                {
                    SocketException => "无法解析或选择校区内网目标地址：" + exception.Message,
                    InvalidDataException or ArgumentException => "学生更新命令或回执输入无效：" + exception.Message,
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

    private static async Task<int?> ReadDownloadRequestProtocolVersionAsync(HttpClient client, string target,
        string campusId, RSA campusPrivateKey, string pinnedAgentPublicKeyPem,
        CancellationToken cancellationToken)
    {
        var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
            WebsitePolicyAgent.StudentUpdateDownloadCapabilityPath).Uri;
        var issuedUtc = DateTimeOffset.UtcNow;
        var request = new WebsitePolicyStatusRequest(4, WebsitePolicyStatusCryptography.RequestPurposeV4,
            campusId, Guid.NewGuid(), issuedUtc);
        using var content = new StringContent(WebsitePolicyStatusCryptography.SignRequest(request, campusPrivateKey),
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"读取学生更新下载能力返回 HTTP {(int)response.StatusCode}；未下发更新命令。",
                null, response.StatusCode);
        var signedResult = await ReadBoundedTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(signedResult))
            throw new InvalidDataException("学生端更新下载能力接口返回空签名回执；本次更新命令尚未下发。");
        var verified = StudentAgentResponseCryptography
            .Verify<StudentApplicationUpdateDownloadCapabilityResponse>(signedResult, pinnedAgentPublicKeyPem);
        if (!verified.MatchesPinnedKey)
            throw new InvalidDataException("学生更新下载能力回执与已固定的 Agent 身份不一致。");
        StudentApplicationUpdateDownloadCapabilityCryptography.Validate(verified.Payload, campusId,
            request.Nonce, DateTimeOffset.UtcNow);
        return verified.Payload.DownloadRequestProtocolVersion;
    }

    private static async Task MonitorUpdateProgressWhilePostPendingAsync(HttpClient client, string target,
        string campusId, RSA campusPrivateKey, string pinnedAgentPublicKeyPem, Guid commandId,
        IProgress<StudentApplicationUpdateProgress>? progress, Func<int> getCompletedTargets, int totalTargets,
        CancellationToken cancellationToken)
    {
        if (progress is null) return;
        var startedUtc = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            await ReportUpdatePhaseTimingsAsync(client, target, campusId, pinnedAgentPublicKeyPem,
                campusPrivateKey, commandId, startedUtc, timer, "inProgress", progress,
                getCompletedTargets(), totalTargets, cancellationToken,
                $"学生端更新处理中；已等待 {timer.Elapsed.TotalSeconds:0} 秒").ConfigureAwait(false);
        }
    }

    private static async Task<UpdateTerminalReadback?> WaitForUpdateTerminalAsync(HttpClient client, string target,
        string campusId, RSA campusPrivateKey, string pinnedAgentPublicKeyPem, Guid commandId,
        bool waitForProgress,
        CancellationToken cancellationToken, IProgress<StudentApplicationUpdateProgress>? progress,
        Func<int> getCompletedTargets, int totalTargets, TimeSpan? maxWait = null,
        bool allowStatusPolling = true)
    {
        var deadline = DateTimeOffset.UtcNow + (maxWait ?? TimeSpan.FromSeconds(100));
        var receiptStartedUtc = DateTimeOffset.UtcNow;
        var waitTimer = Stopwatch.StartNew();
        var lastProgressAt = TimeSpan.Zero;
        var lastTimingAt = TimeSpan.Zero;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (allowStatusPolling)
            {
                var readback = await TryReadUpdateTerminalOnceAsync(client, target, campusId,
                    campusPrivateKey, pinnedAgentPublicKeyPem, commandId, cancellationToken)
                    .ConfigureAwait(false);
                if (readback is not null && (!waitForProgress || readback.State is
                        "committed" or "rolledBack" or "needsReview"))
                {
                    await ReportUpdatePhaseTimingsAsync(client, target, campusId, pinnedAgentPublicKeyPem,
                        campusPrivateKey, commandId, receiptStartedUtc, waitTimer, "succeeded", progress,
                        getCompletedTargets(), totalTargets, cancellationToken).ConfigureAwait(false);
                    return readback;
                }
            }
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
        if (!allowStatusPolling)
        {
            var finalReadback = await TryReadUpdateTerminalOnceAsync(client, target, campusId,
                campusPrivateKey, pinnedAgentPublicKeyPem, commandId, cancellationToken).ConfigureAwait(false);
            if (finalReadback is { State: "committed" or "rolledBack" or "needsReview" })
            {
                await ReportUpdatePhaseTimingsAsync(client, target, campusId, pinnedAgentPublicKeyPem,
                    campusPrivateKey, commandId, receiptStartedUtc, waitTimer, "succeeded", progress,
                    getCompletedTargets(), totalTargets, cancellationToken).ConfigureAwait(false);
                return finalReadback;
            }
        }
        await ReportUpdatePhaseTimingsAsync(client, target, campusId, pinnedAgentPublicKeyPem,
            campusPrivateKey, commandId, receiptStartedUtc, waitTimer, "failed", progress,
            getCompletedTargets(), totalTargets, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static async Task<(UpdateTerminalReadback? Readback,
        StudentApplicationUpdatePushResult? PreflightFailure)> WaitForUpdateTerminalOrPreflightFailureAsync(
        HttpClient client, string target, string campusId, RSA campusPrivateKey,
        string pinnedAgentPublicKeyPem, Guid commandId, HttpStatusCode responseStatus,
        CancellationToken cancellationToken, IProgress<StudentApplicationUpdateProgress>? progress,
        Func<int> getCompletedTargets, int totalTargets, TimeSpan maxWait)
    {
        using var terminalWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var terminalWait = WaitForUpdateTerminalAsync(client, target, campusId, campusPrivateKey,
            pinnedAgentPublicKeyPem, commandId, waitForProgress: true, terminalWaitCancellation.Token,
            progress, getCompletedTargets, totalTargets, maxWait);

        while (!terminalWait.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            if (terminalWait.IsCompleted) break;

            var preflightFailure = await TryResolvePreflightFailureAsync(client, target, campusId,
                pinnedAgentPublicKeyPem, campusPrivateKey, commandId, responseStatus, progress,
                getCompletedTargets, totalTargets, cancellationToken).ConfigureAwait(false);
            if (preflightFailure is null) continue;

            terminalWaitCancellation.Cancel();
            try
            {
                var readback = await terminalWait.ConfigureAwait(false);
                if (readback is not null) return (readback, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The definitive preflight failure makes the longer transaction wait unnecessary.
            }
            return (null, preflightFailure);
        }

        return (await terminalWait.ConfigureAwait(false), null);
    }

    private static async Task<UpdateTerminalReadback?> TryReadUpdateTerminalOnceAsync(HttpClient client,
        string target, string campusId, RSA campusPrivateKey, string pinnedAgentPublicKeyPem, Guid commandId,
        CancellationToken cancellationToken)
    {
        var address = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
            WebsitePolicyAgent.StatusPathV4).Uri;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var request = new WebsitePolicyStatusRequest(4, WebsitePolicyStatusCryptography.RequestPurposeV4,
                campusId, Guid.NewGuid(), DateTimeOffset.UtcNow, commandId);
            var signedRequest = WebsitePolicyStatusCryptography.SignRequest(request, campusPrivateKey);
            using var content = new StringContent(signedRequest, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(address, content, attempt.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var resultText = await ReadBoundedTextAsync(response.Content, attempt.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resultText)) return null;
            var verified = StudentAgentResponseCryptography.Verify<StudentAgentStatusResponse>(resultText,
                pinnedAgentPublicKeyPem);
            WebsitePolicyStatusCryptography.ValidateResponse(verified.Payload, request, DateTimeOffset.UtcNow);
            var transaction = verified.Payload.UpdateTransaction
                              ?? throw new InvalidDataException("学生 Agent 缺少本次更新事务回执。");
            if (transaction.CommandId != commandId) return null;
            return new UpdateTerminalReadback(transaction.State, verified.MatchesPinnedKey,
                verified.Payload, CreateIdentityCandidate(target, campusId, verified, pinnedAgentPublicKeyPem));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
            InvalidDataException or JsonException or CryptographicException or IOException or UriFormatException)
        {
            return null;
        }
    }

    private static async Task ReportUpdatePhaseTimingsAsync(HttpClient client, string target, string campusId,
        string pinnedAgentPublicKeyPem, RSA campusPrivateKey, Guid commandId, DateTimeOffset receiptStartedUtc,
        Stopwatch receiptTimer, string outcome,
        IProgress<StudentApplicationUpdateProgress>? progress, int completedTargets, int totalTargets,
        CancellationToken cancellationToken, string stage = "正在核对教师端签名回执")
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
            stage, phases, commandId, snapshot?.Environment));
    }

    private static async Task<StudentApplicationUpdatePushResult?> TryResolvePreflightFailureAsync(
        HttpClient client, string target, string campusId, string pinnedAgentPublicKeyPem,
        RSA campusPrivateKey, Guid commandId, HttpStatusCode responseStatus,
        IProgress<StudentApplicationUpdateProgress>? progress, Func<int> getCompletedTargets,
        int totalTargets, CancellationToken cancellationToken)
    {
        var snapshot = await TryReadUpdatePhaseTimingsAsync(client, target, campusId,
            pinnedAgentPublicKeyPem, campusPrivateKey, commandId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.CommandId != commandId || !IsDefinitivePreflightFailure(snapshot))
            return null;

        progress?.Report(new StudentApplicationUpdateProgress(target, getCompletedTargets(), totalTargets,
            "学生端命令预检失败；本次更新未进入安装阶段。", snapshot.Phases, commandId,
            snapshot.Environment));
        return new StudentApplicationUpdatePushResult(target, false,
            $"学生 Agent 的签名阶段记录确认命令在“命令接收与预检”阶段失败（HTTP {(int)responseStatus}）；" +
            "本次更新未进入局域网传输或安装阶段。Agent 没有返回签名终态回执，已立即停止等待；" +
            "请检查学生端 SYSTEM Agent 的预检日志并处理遗留事务后重试。",
            NeedsReview: true);
    }

    private static bool IsDefinitivePreflightFailure(StudentUpdatePhaseTimingSnapshot snapshot)
    {
        var offer = snapshot.Phases.FirstOrDefault(phase => phase.Phase == StudentUpdatePhase.TeacherOffer);
        if (offer is null || offer.Outcome != "failed" || offer.CompletedUtc is null) return false;
        return snapshot.Phases.All(phase => phase.Phase is StudentUpdatePhase.IntegrityVerify or
            StudentUpdatePhase.TeacherOffer);
    }

    private static async Task<StudentUpdatePhaseTimingSnapshot?> TryReadUpdatePhaseTimingsAsync(HttpClient client,
        string target, string campusId, string pinnedAgentPublicKeyPem, RSA campusPrivateKey, Guid commandId,
        CancellationToken cancellationToken)
    {
        var (v3Unsupported, v3Snapshot) = await TryReadUpdatePhaseTimingsV3Async(client, target, campusId,
            pinnedAgentPublicKeyPem, campusPrivateKey, commandId, cancellationToken).ConfigureAwait(false);
        if (v3Snapshot is not null) return v3Snapshot;
        if (!v3Unsupported) return null;

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
            if (string.IsNullOrWhiteSpace(resultText)) return null;
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
        TryReadUpdatePhaseTimingsV3Async(
        HttpClient client, string target, string campusId, string pinnedAgentPublicKeyPem,
        RSA campusPrivateKey, Guid commandId, CancellationToken cancellationToken)
    {
        var address = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
            WebsitePolicyAgent.StudentUpdateTimingPathV3).Uri;
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
            if (string.IsNullOrWhiteSpace(resultText)) return (false, null);
            var verified = StudentAgentResponseCryptography.Verify<StudentUpdatePhaseTimingResponseV3>(
                resultText, pinnedAgentPublicKeyPem);
            if (!verified.MatchesPinnedKey) return (false, null);
            StudentUpdatePhaseTimingCryptography.ValidateV3(verified.Payload, request, campusId,
                DateTimeOffset.UtcNow);
            return (false, new StudentUpdatePhaseTimingSnapshot(1, verified.Payload.CommandId,
                verified.Payload.CollectedUtc, verified.Payload.Phases, verified.Payload.Environment));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (false, null); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          InvalidDataException or JsonException or CryptographicException or
                                          IOException or UriFormatException) { return (false, null); }
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
            if (string.IsNullOrWhiteSpace(resultText)) return (false, null);
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

    private static string FormatUpdateHttpFailure(HttpStatusCode statusCode, string responseBody)
    {
        var detail = Regex.Replace(responseBody, @"[\p{Cc}\s]+", " ", RegexOptions.CultureInvariant).Trim();
        if (detail.Length > 1000) detail = detail[..1000] + "…";
        return string.IsNullOrWhiteSpace(detail)
            ? $"HTTP {(int)statusCode}；学生 Agent 未返回错误详情"
            : $"HTTP {(int)statusCode}：{detail}";
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

    private static async Task ObservePostResponseAsync(Task<HttpResponseMessage> pendingPost)
    {
        try
        {
            using var response = await pendingPost.ConfigureAwait(false);
        }
        catch (Exception) { /* A signed terminal readback already resolved the operation. */ }
    }

    private static async Task StopAndObserveProgressMonitorAsync(CancellationTokenSource cancellation,
        Task pendingMonitor)
    {
        cancellation.Cancel();
        try { await pendingMonitor.ConfigureAwait(false); }
        catch (Exception) { /* Cancellation or a transient phase-read error must not mask the update result. */ }
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
