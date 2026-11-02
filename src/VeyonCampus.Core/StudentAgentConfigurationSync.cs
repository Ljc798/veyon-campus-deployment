using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record StudentAgentConfigurationSyncCommand(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid CommandId,
    Guid Nonce,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    string ExpectedAgentFingerprint,
    IReadOnlyList<string> TeacherChannelEndpoints,
    string TeacherChannelRootCertificateBase64);

public sealed record SignedStudentAgentConfigurationSyncCommand(string Payload, string Signature);

public sealed record StudentAgentConfigurationSyncProgress(
    string Target,
    string Detail,
    bool Completed,
    bool Succeeded = false,
    bool NeedsReview = false);

/// <summary>Signs a narrowly scoped update to the Agent's teacher connection bootstrap only.</summary>
public static class StudentAgentConfigurationSyncCryptography
{
    public const string Purpose = "VeyonCampus.StudentAgentConfigurationSync.v1";
    public const string RequestNonceHeader = "X-VeyonCampus-Request-Nonce";
    public const int MaximumEnvelopeBytes = 48 * 1024;
    public static readonly TimeSpan MaximumCommandLifetime = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = ApplicationPolicyCompiler.JsonOptions;

    public static string Sign(StudentAgentConfigurationSyncCommand command, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        Validate(command, command.IssuedUtc);
        if (privateKey.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("配置同步签名密钥位长不受支持。");
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions);
        if (payload.Length > MaximumEnvelopeBytes - 1024)
            throw new InvalidDataException("配置同步命令超过大小限制。");
        var envelope = new SignedStudentAgentConfigurationSyncCommand(Convert.ToBase64String(payload),
            Convert.ToBase64String(privateKey.SignData(payload, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss)));
        var signed = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(signed) > MaximumEnvelopeBytes)
            throw new InvalidDataException("配置同步命令超过大小限制。");
        return signed;
    }

    public static StudentAgentConfigurationSyncCommand Verify(string signedJson, string campusPublicKeyPem,
        string expectedCampusId, Guid expectedNonce, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedJson);
        if (Encoding.UTF8.GetByteCount(signedJson) > MaximumEnvelopeBytes || expectedNonce == Guid.Empty)
            throw new InvalidDataException("配置同步命令大小或随机数无效。");
        var envelopeBytes = Encoding.UTF8.GetBytes(signedJson);
        PolicyJson.RejectDuplicateFields(envelopeBytes);
        var envelope = JsonSerializer.Deserialize<SignedStudentAgentConfigurationSyncCommand>(signedJson, JsonOptions)
                       ?? throw new InvalidDataException("配置同步签名信封为空。");
        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("配置同步签名信封编码无效。", exception);
        }
        if (!string.Equals(Convert.ToBase64String(payload), envelope.Payload, StringComparison.Ordinal) ||
            !string.Equals(Convert.ToBase64String(signature), envelope.Signature, StringComparison.Ordinal) ||
            payload.Length is 0 or > MaximumEnvelopeBytes - 1024 || signature.Length is < 256 or > 512)
            throw new InvalidDataException("配置同步签名字段大小或编码无效。");
        PolicyJson.RejectDuplicateFields(payload);
        using var publicKey = RSA.Create();
        publicKey.ImportFromPem(campusPublicKeyPem);
        if (publicKey.KeySize is < 2048 or > 4096 ||
            !publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("配置同步命令签名无效。");
        var command = JsonSerializer.Deserialize<StudentAgentConfigurationSyncCommand>(payload, JsonOptions)
                      ?? throw new InvalidDataException("配置同步命令正文为空。");
        if (!string.Equals(command.CampusId, expectedCampusId, StringComparison.Ordinal))
            throw new InvalidDataException("配置同步命令属于其他校区。");
        if (command.Nonce != expectedNonce)
            throw new InvalidDataException("配置同步命令随机数与请求头不匹配。");
        Validate(command, nowUtc);
        if (!payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions)))
            throw new InvalidDataException("配置同步命令正文不是规范编码。");
        return command;
    }

    private static void Validate(StudentAgentConfigurationSyncCommand command, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(command);
        WebsitePolicySigningKeyStore.ValidateCampusId(command.CampusId);
        var now = nowUtc.ToUniversalTime();
        if (command.SchemaVersion != 1 || command.Purpose != Purpose || command.CommandId == Guid.Empty ||
            command.Nonce == Guid.Empty || command.IssuedUtc.Offset != TimeSpan.Zero ||
            command.ExpiresUtc.Offset != TimeSpan.Zero || command.IssuedUtc > now.AddMinutes(1) ||
            command.IssuedUtc < now.Subtract(TimeSpan.FromMinutes(2)) || command.ExpiresUtc <= now ||
            command.ExpiresUtc <= command.IssuedUtc || command.ExpiresUtc - command.IssuedUtc > MaximumCommandLifetime ||
            command.ExpectedAgentFingerprint is not { Length: 64 } fingerprint ||
            fingerprint.Any(character => !Uri.IsHexDigit(character)) ||
            command.TeacherChannelRootCertificateBase64 is null)
            throw new InvalidDataException("配置同步命令字段、有效期或校区无效。");
        StudentAgentChannelProtocol.ValidateBootstrapConfiguration(command.CampusId,
            command.TeacherChannelEndpoints, command.TeacherChannelRootCertificateBase64);
    }
}

/// <summary>Ships a signed teacher-channel bootstrap to already deployed SYSTEM Agents.</summary>
public static class StudentAgentConfigurationSyncTransport
{
    public static async Task<IReadOnlyList<WebsitePolicyPushResult>> ExecuteAsync(
        IEnumerable<string> targets, string campusId, IReadOnlyList<string> teacherChannelEndpoints,
        string teacherChannelRootCertificateBase64, RSA campusPrivateKey,
        IProgress<StudentAgentConfigurationSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var validated = WebsitePolicyTransport.NormalizeTargets(targets);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        ArgumentNullException.ThrowIfNull(campusPrivateKey);
        StudentAgentChannelProtocol.ValidateBootstrapConfiguration(campusId, teacherChannelEndpoints,
            teacherChannelRootCertificateBase64);
        var pins = new StudentAgentIdentityTrustStore();
        // Bootstrap repair must reach the local SYSTEM listener even when an old teacher WebSocket
        // address is remembered but currently disconnected. The command and acknowledgement remain signed.
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
        using var limit = new SemaphoreSlim(12, 12);
        var tasks = validated.Select(async target =>
        {
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var pinnedKey = pins.FindTrustedPublicKey(campusId, target);
                if (pinnedKey is null)
                {
                    var missingPin = new WebsitePolicyPushResult(target, false,
                        "学生 Agent 身份尚未核对；先执行“首次/身份变化时核对”，再同步配置。", NeedsReview: true);
                    progress?.Report(new StudentAgentConfigurationSyncProgress(target, missingPin.Detail, true,
                        NeedsReview: true));
                    return missingPin;
                }

                progress?.Report(new StudentAgentConfigurationSyncProgress(target, "正在发送签名配置…", false));
                var issued = DateTimeOffset.UtcNow;
                var command = new StudentAgentConfigurationSyncCommand(1, StudentAgentConfigurationSyncCryptography.Purpose,
                    campusId, Guid.NewGuid(), Guid.NewGuid(), issued, issued.Add(StudentAgentConfigurationSyncCryptography.MaximumCommandLifetime),
                    StudentAgentResponseCryptography.GetFingerprint(pinnedKey),
                    Array.AsReadOnly(teacherChannelEndpoints.ToArray()), teacherChannelRootCertificateBase64);
                string signedCommand;
                lock (campusPrivateKey)
                    signedCommand = StudentAgentConfigurationSyncCryptography.Sign(command, campusPrivateKey);
                var nonce = command.Nonce;
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
                        WebsitePolicyAgent.ConfigurationSyncPath).Uri)
                {
                    Content = new StringContent(signedCommand, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation(StudentAgentConfigurationSyncCryptography.RequestNonceHeader,
                    nonce.ToString("D"));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                var responseBody = await ReadBoundedResponseAsync(response.Content,
                    StudentAgentResponseCryptography.MaximumCommandEnvelopeBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var unsupported = (int)response.StatusCode == (int)HttpStatusCode.NotFound
                        ? "学生端 Agent 版本不支持批量配置同步；本次没有更改该电脑。早期 Agent 需要先通过一次性引导升级接入。"
                        : $"HTTP {(int)response.StatusCode}：{responseBody}";
                    var failed = new WebsitePolicyPushResult(target, false, unsupported,
                        NeedsReview: response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict ||
                                     (int)response.StatusCode >= 500);
                    progress?.Report(new StudentAgentConfigurationSyncProgress(target, failed.Detail, true,
                        NeedsReview: failed.NeedsReview));
                    return failed;
                }

                try
                {
                    var verified = StudentAgentCommandAcknowledgementCryptography.Verify(responseBody, campusId,
                        nonce, signedCommand, (int)response.StatusCode, pinnedKey, DateTimeOffset.UtcNow,
                        maximumBodyBytes: 2048);
                    if (!verified.MatchesPinnedKey)
                    {
                        var review = new WebsitePolicyPushResult(target, false,
                            $"Agent 身份需先核对；指纹 {verified.Fingerprint}。", NeedsReview: true);
                        progress?.Report(new StudentAgentConfigurationSyncProgress(target, review.Detail, true,
                            NeedsReview: true));
                        return review;
                    }
                    if (verified.Payload.Body is not ("configuration synced; reconnect pending" or
                        "configuration already current"))
                        throw new InvalidDataException("学生端没有确认配置同步结果。");
                    var result = new WebsitePolicyPushResult(target, true,
                        verified.Payload.Body == "configuration already current"
                            ? "配置已是最新，学生 Agent 已确认。"
                            : "配置已安全写入；学生 Agent 正在重新连接教师端。");
                    progress?.Report(new StudentAgentConfigurationSyncProgress(target, result.Detail, true,
                        Succeeded: true));
                    return result;
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or
                                                  UnauthorizedAccessException or CryptographicException)
                {
                    var review = new WebsitePolicyPushResult(target,
                        false, "Agent 配置同步回执未通过签名和身份核验：" + exception.Message,
                        NeedsReview: true);
                    progress?.Report(new StudentAgentConfigurationSyncProgress(target, review.Detail, true,
                        NeedsReview: true));
                    return review;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                UriFormatException or InvalidDataException or IOException or UnauthorizedAccessException or
                CryptographicException or InvalidOperationException)
            {
                var detail = exception is TaskCanceledException or OperationCanceledException
                    ? $"连接 {target}:{WebsitePolicyAgent.Port} 超时。请确认学生端在线、Agent 已更新且双方处于允许通信的局域网。"
                    : exception is HttpRequestException
                        ? $"无法连接 {target}:{WebsitePolicyAgent.Port}：{exception.Message}"
                        : "配置同步未完成：" + exception.Message;
                var failed = new WebsitePolicyPushResult(target, false, detail, NeedsReview: true);
                progress?.Report(new StudentAgentConfigurationSyncProgress(target, detail, true, NeedsReview: true));
                return failed;
            }
            finally { limit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static async Task<string> ReadBoundedResponseAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("学生 Agent 配置同步回执超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var bytes = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("学生 Agent 配置同步回执超过大小限制。");
            buffer.Write(bytes, 0, read);
        }
        try { return new UTF8Encoding(false, true).GetString(buffer.ToArray()); }
        catch (DecoderFallbackException exception)
        { throw new InvalidDataException("学生 Agent 配置同步回执不是有效 UTF-8。", exception); }
    }
}
