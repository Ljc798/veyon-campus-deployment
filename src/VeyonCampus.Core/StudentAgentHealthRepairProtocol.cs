using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

public enum StudentAgentHealthRepairAction
{
    EnsureVeyonServiceRunning,
    RefreshActiveWebsitePolicy
}

public enum StudentAgentHealthRepairOutcome
{
    Succeeded,
    AlreadyHealthy,
    Queued,
    BusyUpdating,
    CoolingDown,
    NotAvailable,
    NeedsManualReview,
    Failed,
    ReplayRejected
}

public sealed record StudentAgentHealthRepairCommand(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid CommandId,
    Guid Nonce,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    StudentAgentHealthRepairAction Action,
    long? ExpectedWebsiteRevision = null);

public sealed record StudentAgentHealthRepairReceipt(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid CommandId,
    Guid Nonce,
    StudentAgentHealthRepairAction Action,
    StudentAgentHealthRepairOutcome Outcome,
    long? WebsiteRevision,
    DateTimeOffset CompletedUtc);

public sealed record StudentAgentHealthRepairDeliveryResult(
    string Target,
    Guid CommandId,
    StudentAgentHealthRepairAction Action,
    bool Succeeded,
    bool NeedsReview,
    StudentAgentHealthRepairOutcome? Outcome,
    string Detail);

public sealed record SignedStudentAgentHealthRepairCommand(string Payload, string Signature);

public static class StudentAgentHealthRepairCommandCryptography
{
    public const string Purpose = "VeyonCampus.StudentAgentHealthRepairCommand.v1";
    public const string ReceiptPurpose = "VeyonCampus.StudentAgentHealthRepairReceipt.v1";
    public const string RequestNonceHeader = "X-VeyonCampus-Request-Nonce";
    public const int MaximumEnvelopeBytes = 16 * 1024;
    public static readonly TimeSpan MaximumCommandLifetime = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = ApplicationPolicyCompiler.JsonOptions;

    public static string Sign(StudentAgentHealthRepairCommand command, RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        Validate(command, command.IssuedUtc);
        if (privateKey.KeySize is < 2048 or > 4096)
            throw new InvalidDataException("校区健康修复签名密钥位长不受支持。");
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions);
        if (payload.Length > 8192) throw new InvalidDataException("健康修复命令超过大小限制。");
        var envelope = new SignedStudentAgentHealthRepairCommand(Convert.ToBase64String(payload),
            Convert.ToBase64String(privateKey.SignData(payload, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss)));
        var signed = JsonSerializer.Serialize(envelope, JsonOptions);
        if (Encoding.UTF8.GetByteCount(signed) > MaximumEnvelopeBytes)
            throw new InvalidDataException("健康修复命令超过大小限制。");
        return signed;
    }

    public static StudentAgentHealthRepairCommand Verify(string signedJson, string campusPublicKeyPem,
        string expectedCampusId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedJson);
        if (Encoding.UTF8.GetByteCount(signedJson) > MaximumEnvelopeBytes)
            throw new InvalidDataException("健康修复命令超过大小限制。");
        var envelopeBytes = Encoding.UTF8.GetBytes(signedJson);
        PolicyJson.RejectDuplicateFields(envelopeBytes);
        var envelope = JsonSerializer.Deserialize<SignedStudentAgentHealthRepairCommand>(signedJson, JsonOptions)
                       ?? throw new InvalidDataException("健康修复签名信封为空。");
        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("健康修复签名信封编码无效。", exception);
        }
        if (payload.Length is 0 or > 8192 || signature.Length is < 256 or > 512)
            throw new InvalidDataException("健康修复签名字段大小无效。");
        PolicyJson.RejectDuplicateFields(payload);
        using var publicKey = RSA.Create();
        publicKey.ImportFromPem(campusPublicKeyPem);
        if (publicKey.KeySize is < 2048 or > 4096 ||
            !publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("健康修复命令签名无效。");
        var command = JsonSerializer.Deserialize<StudentAgentHealthRepairCommand>(payload, JsonOptions)
                      ?? throw new InvalidDataException("健康修复命令正文为空。");
        if (!string.Equals(command.CampusId, expectedCampusId, StringComparison.Ordinal))
            throw new InvalidDataException("健康修复命令属于其他校区。");
        Validate(command, nowUtc);
        if (!payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions)))
            throw new InvalidDataException("健康修复命令正文不是规范编码。");
        return command;
    }

    public static void ValidateReceipt(StudentAgentHealthRepairReceipt receipt,
        StudentAgentHealthRepairCommand command, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var now = nowUtc.ToUniversalTime();
        if (receipt.SchemaVersion != 1 || receipt.Purpose != ReceiptPurpose ||
            receipt.CampusId != command.CampusId || receipt.CommandId != command.CommandId ||
            receipt.Nonce != command.Nonce || receipt.Action != command.Action ||
            !Enum.IsDefined(receipt.Outcome) || receipt.CompletedUtc.Offset != TimeSpan.Zero ||
            receipt.CompletedUtc < command.IssuedUtc || receipt.CompletedUtc > now.AddMinutes(1) ||
            receipt.CompletedUtc < now.Subtract(TimeSpan.FromMinutes(3)) ||
            (receipt.Action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy
                ? receipt.WebsiteRevision != command.ExpectedWebsiteRevision
                : receipt.WebsiteRevision is not null))
            throw new InvalidDataException("健康修复回执与本次命令不匹配。");
    }

    private static void Validate(StudentAgentHealthRepairCommand command, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(command);
        WebsitePolicySigningKeyStore.ValidateCampusId(command.CampusId);
        var now = nowUtc.ToUniversalTime();
        if (command.SchemaVersion != 1 || command.Purpose != Purpose ||
            command.CommandId == Guid.Empty || command.Nonce == Guid.Empty ||
            !Enum.IsDefined(command.Action) ||
            command.IssuedUtc.Offset != TimeSpan.Zero || command.ExpiresUtc.Offset != TimeSpan.Zero ||
            command.IssuedUtc < now.Subtract(MaximumCommandLifetime) || command.IssuedUtc > now.AddSeconds(30) ||
            command.ExpiresUtc <= now || command.ExpiresUtc > now.Add(MaximumCommandLifetime) ||
            command.ExpiresUtc <= command.IssuedUtc ||
            command.ExpiresUtc - command.IssuedUtc > MaximumCommandLifetime ||
            (command.Action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy
                ? command.ExpectedWebsiteRevision is null or <= 0
                : command.ExpectedWebsiteRevision is not null))
            throw new InvalidDataException("健康修复命令版本、随机数、动作或有效期无效。");
    }
}

public static class StudentAgentHealthRepairTransport
{
    public static async Task<IReadOnlyList<StudentAgentHealthRepairDeliveryResult>> ExecuteAsync(
        IEnumerable<string> targets, string campusId, RSA campusPrivateKey,
        StudentAgentHealthRepairAction action, long? expectedWebsiteRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campusPrivateKey);
        if (!Enum.IsDefined(action)) throw new InvalidDataException("健康修复动作无效。");
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var validated = WebsitePolicyTransport.NormalizeTargets(targets);
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var limit = new SemaphoreSlim(8, 8);
        var tasks = validated.Select(async target =>
        {
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var command = new StudentAgentHealthRepairCommand(1,
                    StudentAgentHealthRepairCommandCryptography.Purpose, campusId, Guid.NewGuid(),
                    Guid.NewGuid(), now, now.Add(StudentAgentHealthRepairCommandCryptography.MaximumCommandLifetime),
                    action, expectedWebsiteRevision);
                var signedCommand = StudentAgentHealthRepairCommandCryptography.Sign(command, campusPrivateKey);
                var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
                    WebsitePolicyAgent.HealthRepairPath).Uri;
                using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(signedCommand, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation(StudentAgentHealthRepairCommandCryptography.RequestNonceHeader,
                    command.Nonce.ToString("D"));
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var responseText = await ReadBoundedResponseAsync(response.Content,
                    StudentAgentResponseCryptography.MaximumEnvelopeBytes, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new StudentAgentHealthRepairDeliveryResult(target, command.CommandId, action,
                        false, response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict,
                        null, response.StatusCode == HttpStatusCode.NotFound
                            ? "学生 Agent 尚不支持健康修复，请先更新学生端。"
                            : $"健康修复请求未确认（HTTP {(int)response.StatusCode}）。");

                var pinnedKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(campusId, target);
                var verified = StudentAgentCommandAcknowledgementCryptography.Verify(responseText, campusId,
                    command.Nonce, signedCommand, (int)response.StatusCode, pinnedKey, DateTimeOffset.UtcNow,
                    maximumBodyBytes: 8192);
                if (!verified.MatchesPinnedKey)
                    return new StudentAgentHealthRepairDeliveryResult(target, command.CommandId, action,
                        false, true, null, "Agent 身份未固定或已变化；修复回执未接受。");

                PolicyJson.RejectDuplicateFields(Encoding.UTF8.GetBytes(verified.Payload.Body));
                var receipt = JsonSerializer.Deserialize<StudentAgentHealthRepairReceipt>(
                                  verified.Payload.Body, ApplicationPolicyCompiler.JsonOptions)
                              ?? throw new InvalidDataException("健康修复回执为空。");
                StudentAgentHealthRepairCommandCryptography.ValidateReceipt(receipt, command, DateTimeOffset.UtcNow);
                var succeeded = receipt.Outcome is StudentAgentHealthRepairOutcome.Succeeded or
                    StudentAgentHealthRepairOutcome.AlreadyHealthy or StudentAgentHealthRepairOutcome.Queued;
                var needsReview = receipt.Outcome is StudentAgentHealthRepairOutcome.NeedsManualReview or
                    StudentAgentHealthRepairOutcome.Failed or StudentAgentHealthRepairOutcome.ReplayRejected;
                return new StudentAgentHealthRepairDeliveryResult(target, command.CommandId, action,
                    succeeded, needsReview, receipt.Outcome, Describe(receipt.Outcome));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              UriFormatException or InvalidDataException or JsonException or
                                              CryptographicException or ArgumentException)
            {
                return new StudentAgentHealthRepairDeliveryResult(target, Guid.Empty, action,
                    false, exception is InvalidDataException or CryptographicException, null,
                    exception is TaskCanceledException ? "健康修复请求超时；请重新读取状态后再决定是否重试。"
                        : exception is HttpRequestException ? "无法连接学生 Agent；未确认是否执行修复。"
                        : "健康修复回执未通过签名、身份或格式核验。");
            }
            finally { limit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static string Describe(StudentAgentHealthRepairOutcome outcome) => outcome switch
    {
        StudentAgentHealthRepairOutcome.Succeeded => "修复已完成并读回确认。",
        StudentAgentHealthRepairOutcome.AlreadyHealthy => "服务已经处于正常状态，无需修改。",
        StudentAgentHealthRepairOutcome.Queued => "策略刷新请求已排入队列；浏览器是否已应用仍需重新读取回执。",
        StudentAgentHealthRepairOutcome.BusyUpdating => "学生端更新或恢复正在进行；本次未执行修复。",
        StudentAgentHealthRepairOutcome.CoolingDown => "该修复刚刚执行过，请等待冷却时间后再试。",
        StudentAgentHealthRepairOutcome.NotAvailable => "当前状态不满足修复条件。",
        StudentAgentHealthRepairOutcome.NeedsManualReview => "Agent 或本机状态需要人工检查；本次未执行修复。",
        StudentAgentHealthRepairOutcome.ReplayRejected => "检测到重复或冲突的命令编号；本次未执行修复。",
        _ => "修复未能完成；请查看设备状态后人工处理。"
    };

    private static async Task<string> ReadBoundedResponseAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("学生 Agent 健康修复回执超过大小限制。");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("学生 Agent 健康修复回执超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
