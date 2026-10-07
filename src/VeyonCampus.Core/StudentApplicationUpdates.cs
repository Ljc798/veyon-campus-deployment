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
    private const long MaximumArtifactBytes = 512L * 1024 * 1024;
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

    public static async Task<StudentApplicationUpdateProcessingResult> ProcessAsync(string signedCommandJson,
        string configPath,
        WebsitePolicyAgentConfig config, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Student 静默更新仅支持 Windows。");
        await ApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = StudentApplicationUpdateCryptography.Verify(signedCommandJson, config.PublicKeyPem,
                config.CampusId, DeploymentPackageApiConfiguration.GetApiBaseAddress(),
                ApplicationReleaseTrust.LoadPinnedPublicKeyPem());
            var manifest = command.Release.Manifest;
            var installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Veyon Campus", "Student");
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
                return CreateResult(command, currentVersion, currentAgentVersion,
                    $"StudentSetup {currentVersion} 与 Student Agent {currentAgentVersion} 已是目标版本或更新版本。",
                    agentRestartPending: false);

            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
                                  ?? throw new InvalidDataException("Student Agent 配置目录无效。");
            var replayStatePath = Path.Combine(configDirectory, "student-update-replay.json");
            if (!StudentApplicationUpdateReplayStore.TryConsume(replayStatePath, command.CommandId,
                    command.ExpiresUtc))
                throw new InvalidDataException("学生更新命令已执行过；已拒绝重放。");

            var stagingDirectory = Path.Combine(configDirectory, "student-update-" + command.CommandId.ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            AgentFileSecurity.SecureTree(stagingDirectory);
            var installerPath = Path.Combine(stagingDirectory, manifest.FileName);
            try
            {
                await DownloadArtifactAsync(StudentApplicationUpdateCryptography.ValidateLocalDownloadUrl(
                        command.LocalDownloadUrl), manifest, installerPath, stagingDirectory, cancellationToken)
                    .ConfigureAwait(false);
                if (DateTimeOffset.UtcNow >= command.ExpiresUtc)
                    throw new InvalidDataException("学生更新命令在下载期间已过期；请由教师重新发送。");

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
                    installer.WaitForExit();
                    if (installer.ExitCode != 0)
                        throw new InvalidOperationException($"学生静默安装器返回失败代码 {installer.ExitCode}。");
                }

                var installedVersion = ReadInstalledVersion(installDirectory, executablePath);
                if (!string.Equals(installedVersion, manifest.Version, StringComparison.Ordinal))
                    throw new InvalidDataException("安装器完成后版本回读不匹配；未报告更新成功。");
                if (agentComparison <= 0)
                    return CreateResult(command, installedVersion, currentAgentVersion,
                        $"StudentSetup {installedVersion} 已读回；Student Agent {currentAgentVersion} 无需回退。",
                        agentRestartPending: false);

                var agentSourceDirectory = Path.Combine(installDirectory, "WebsitePolicyAgent");
                var stagedAgentUpdate = WebsitePolicyAgentInstaller.StageAgentUpdate(agentSourceDirectory,
                    manifest.Version, configPath);
                StudentApplicationUpdateHandoff.Start(stagedAgentUpdate.TargetExecutablePath,
                    stagedAgentUpdate.PreviousExecutablePath, configPath, manifest.Version, currentAgentVersion,
                    WebsitePolicyAgent.ConfigFingerprint(config));
                return CreateResult(command, installedVersion, currentAgentVersion,
                    $"StudentSetup {installedVersion} 已读回；Student Agent {manifest.Version} 正在切换并重启。",
                    agentRestartPending: true);
            }
            finally
            {
                try { Directory.Delete(stagingDirectory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
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
    private static async Task DownloadArtifactAsync(Uri downloadUri, ApplicationReleaseManifest manifest,
        string installerPath, string stagingDirectory, CancellationToken cancellationToken)
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
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[64 * 1024];
                long totalBytes = 0;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > manifest.SizeBytes || totalBytes > MaximumArtifactBytes)
                        throw new InvalidDataException("学生安装器超过开发者签名清单声明的大小。");
                    digest.AppendData(buffer, 0, bytesRead);
                    await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (totalBytes != manifest.SizeBytes)
                    throw new InvalidDataException("学生安装器下载不完整。");
            }

            var actualSha256 = Convert.ToHexString(digest.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualSha256),
                    Encoding.ASCII.GetBytes(manifest.Sha256)))
                throw new InvalidDataException("学生安装器 SHA-256 与开发者签名清单不符。");
            AgentFileSecurity.Secure(temporaryPath, directory: false, executable: true);
            File.Move(temporaryPath, installerPath, overwrite: false);
            AgentFileSecurity.Secure(installerPath, directory: false, executable: true);
        }
        finally
        {
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
    bool NeedsReview = false, StudentAgentIdentityTrustCandidate? IdentityCandidate = null);

public sealed class StudentApplicationUpdateLanServer : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 8192;
    private readonly TcpListener[] _listeners;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _connectionLimit = new(24, 24);
    private readonly string _installerPath;
    private readonly string _route;
    private readonly Task _acceptTask;

    private StudentApplicationUpdateLanServer(string installerPath)
    {
        _installerPath = Path.GetFullPath(installerPath);
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

    public static StudentApplicationUpdateLanServer Start(string installerPath) =>
        new(installerPath);

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
            var bytesRead = await stream.ReadAsync(headerBuffer.AsMemory(totalBytes), headerTimeout.Token)
                .ConfigureAwait(false);
            if (bytesRead == 0) return;
            totalBytes += bytesRead;
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
        await artifact.CopyToAsync(stream, 64 * 1024, cancellationToken).ConfigureAwait(false);
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
        StudentAgentIdentityTrustStore? identityTrustStore = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(campusPrivateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAgentVersion);
        ArgumentNullException.ThrowIfNull(createSignedCommand);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        _ = ApplicationReleaseClient.CompareVersions(expectedAgentVersion, expectedAgentVersion);
        var validatedTargets = WebsitePolicyTransport.NormalizeTargets(targets);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        };
        using var client = new HttpClient(handler) { Timeout = RequestTimeout };
        using var concurrencyLimit = new SemaphoreSlim(8, 8);
        var tasks = validatedTargets.Select(async target =>
        {
            await concurrencyLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var trustStore = identityTrustStore ?? new StudentAgentIdentityTrustStore();
                var pinnedKey = trustStore.FindTrustedPublicKey(campusId, target);
                var command = await createSignedCommand(target, cancellationToken).ConfigureAwait(false);
                if (command.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(command.SignedCommand))
                    throw new InvalidDataException("教师端没有生成有效的学生更新命令。");
                var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port, "/v1/update").Uri;
                using var content = new StringContent(command.SignedCommand, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
                var resultText = await ReadBoundedTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
                {
                    return new StudentApplicationUpdatePushResult(target, false,
                        $"HTTP {(int)response.StatusCode}：{resultText}", NeedsReview: (int)response.StatusCode >= 500);
                }
                var restartPending = response.StatusCode == HttpStatusCode.Accepted;
                VerifiedStudentAgentResponse<StudentApplicationUpdateResponse>? verifiedUpdate = null;
                StudentAgentIdentityTrustCandidate? updateIdentity = null;
                try
                {
                    verifiedUpdate = StudentAgentResponseCryptography.Verify<StudentApplicationUpdateResponse>(resultText,
                        pinnedKey);
                    StudentApplicationUpdateResponseCryptography.Validate(verifiedUpdate.Payload, campusId,
                        command.CommandId, expectedAgentVersion, restartPending, DateTimeOffset.UtcNow);
                    updateIdentity = CreateIdentityCandidate(target, campusId, verifiedUpdate, pinnedKey);
                }
                catch (InvalidDataException) when (restartPending && pinnedKey is null)
                {
                    // Older Agents return plaintext for the first rollout. Never trust that body;
                    // continue only to a fresh, signed version-status challenge from the new Agent.
                }
                if (restartPending)
                {
                    var readback = await WaitForAgentVersionAsync(client, target, campusId, campusPrivateKey,
                        pinnedKey, expectedAgentVersion, cancellationToken).ConfigureAwait(false);
                    if (readback is null)
                        return new StudentApplicationUpdatePushResult(target, false,
                            "StudentSetup 安装后未能读取到目标 Agent 版本的签名状态；需核对任务、ACL、端口和启动日志。",
                            NeedsReview: true, IdentityCandidate: updateIdentity);
                    var candidate = readback.IdentityCandidate ?? updateIdentity;
                    var updateReplyTrusted = verifiedUpdate is null || verifiedUpdate.MatchesPinnedKey;
                    var succeeded = readback.MatchesPinnedKey && updateReplyTrusted;
                    return new StudentApplicationUpdatePushResult(target, succeeded,
                        succeeded
                            ? $"StudentSetup 与 Student Agent {expectedAgentVersion} 均已安装；更新回执、设备身份和签名状态读回均已验证。"
                            : readback.MatchesPinnedKey
                                ? $"Student Agent {expectedAgentVersion} 的签名状态已读回，但更新回执尚未能验证；需核对本次更新结果。"
                                : $"Student Agent {expectedAgentVersion} 已报告签名状态；身份尚未固定或与原指纹不同。请核对指纹后再确认。{candidate?.Fingerprint}",
                        NeedsReview: !succeeded, IdentityCandidate: candidate);
                }
                if (verifiedUpdate is null)
                    return new StudentApplicationUpdatePushResult(target, false,
                        "Agent 返回了未签名或无效的更新回执；没有将其记为成功。",
                        NeedsReview: true);
                var updateSucceeded = verifiedUpdate.MatchesPinnedKey;
                return new StudentApplicationUpdatePushResult(target, updateSucceeded,
                    updateSucceeded
                        ? verifiedUpdate.Payload.Message
                        : $"更新回执签名有效，但身份尚未固定或与原指纹不同。请核对指纹后再确认：{updateIdentity?.Fingerprint}",
                    NeedsReview: !updateSucceeded, IdentityCandidate: updateIdentity);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                return new StudentApplicationUpdatePushResult(target, false,
                    exception is TaskCanceledException ? "等待学生端静默更新回执超时；请检查 Agent 与目标电脑状态。" :
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
            finally { concurrencyLimit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static async Task<AgentVersionReadback?> WaitForAgentVersionAsync(HttpClient client, string target, string campusId,
        RSA campusPrivateKey, string? pinnedAgentPublicKeyPem, string expectedVersion,
        CancellationToken cancellationToken)
    {
        var address = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
            WebsitePolicyAgent.StatusPath).Uri;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(100);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var request = new WebsitePolicyStatusRequest(1, WebsitePolicyStatusCryptography.RequestPurpose,
                    campusId, Guid.NewGuid(), DateTimeOffset.UtcNow);
                var signedRequest = WebsitePolicyStatusCryptography.SignRequest(request, campusPrivateKey);
                using var content = new StringContent(signedRequest, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(address, content, attempt.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("Student Agent 状态端点尚未就绪。");
                var resultText = await ReadBoundedTextAsync(response.Content, attempt.Token).ConfigureAwait(false);
                var verified = StudentAgentResponseCryptography.Verify<StudentAgentStatusResponse>(resultText,
                    pinnedAgentPublicKeyPem);
                WebsitePolicyStatusCryptography.ValidateResponse(verified.Payload, request, DateTimeOffset.UtcNow);
                if (string.Equals(verified.Payload.AgentVersion, expectedVersion, StringComparison.Ordinal))
                    return new AgentVersionReadback(verified.MatchesPinnedKey,
                        CreateIdentityCandidate(target, campusId, verified, pinnedAgentPublicKeyPem));
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or CryptographicException) { }
            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static StudentAgentIdentityTrustCandidate CreateIdentityCandidate<T>(string target, string campusId,
        VerifiedStudentAgentResponse<T> verified, string? pinnedAgentPublicKeyPem) =>
        new(target, campusId, verified.PublicKeyPem, verified.Fingerprint, pinnedAgentPublicKeyPem is null ? null :
            StudentAgentResponseCryptography.GetFingerprint(pinnedAgentPublicKeyPem));

    private sealed record AgentVersionReadback(bool MatchesPinnedKey,
        StudentAgentIdentityTrustCandidate IdentityCandidate);

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
