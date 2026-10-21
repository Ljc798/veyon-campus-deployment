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
    Busy,
    Expired,
    NeedsManualReview,
    Failed,
    ReplayRejected
}

public enum StudentAgentHealthRepairProgressPhase
{
    Delivered,
    Executing,
    Completed
}

public enum StudentAgentHealthRepairProgressState
{
    Queued,
    Delivered,
    Executing,
    Succeeded,
    Failed,
    TimedOut
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
    DateTimeOffset CompletedUtc,
    bool WasAlreadyProcessed = false);

public sealed record StudentAgentHealthRepairProgressFrame(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid CommandId,
    Guid Nonce,
    StudentAgentHealthRepairAction Action,
    StudentAgentHealthRepairProgressPhase Phase,
    StudentAgentHealthRepairOutcome? Outcome,
    long? WebsiteRevision,
    DateTimeOffset UpdatedUtc,
    bool WasAlreadyProcessed = false);

public sealed record StudentAgentHealthRepairProgressUpdate(
    Guid BatchId,
    string Target,
    Guid CommandId,
    StudentAgentHealthRepairAction Action,
    StudentAgentHealthRepairProgressState State,
    StudentAgentHealthRepairOutcome? Outcome,
    string Detail,
    DateTimeOffset UpdatedUtc);

public sealed record StudentAgentHealthRepairDeliveryResult(
    string Target,
    Guid CommandId,
    StudentAgentHealthRepairAction Action,
    bool Succeeded,
    bool NeedsReview,
    StudentAgentHealthRepairOutcome? Outcome,
    string Detail,
    StudentAgentHealthRepairProgressState State = StudentAgentHealthRepairProgressState.Failed);

public sealed record SignedStudentAgentHealthRepairCommand(string Payload, string Signature);

internal static class StudentAgentHealthRepairLedgerPolicy
{
    internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    internal static bool IsCoolingDown(IReadOnlyList<StudentAgentHealthRepairLedgerEntry> entries,
        StudentAgentHealthRepairAction action, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        var latest = entries.LastOrDefault(entry => entry.Action == action &&
            entry.Outcome is StudentAgentHealthRepairOutcome.Succeeded or
                StudentAgentHealthRepairOutcome.AlreadyHealthy or StudentAgentHealthRepairOutcome.Queued or
                StudentAgentHealthRepairOutcome.Failed);
        return latest is not null && nowUtc.ToUniversalTime() - latest.CompletedUtc < Cooldown;
    }

    internal static bool ShouldPersistOutcome(StudentAgentHealthRepairOutcome outcome)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        return outcome is not (StudentAgentHealthRepairOutcome.BusyUpdating or
            StudentAgentHealthRepairOutcome.CoolingDown or StudentAgentHealthRepairOutcome.ReplayRejected);
    }
}

public static class StudentAgentHealthRepairCommandCryptography
{
    public const string Purpose = "VeyonCampus.StudentAgentHealthRepairCommand.v1";
    public const string ReceiptPurpose = "VeyonCampus.StudentAgentHealthRepairReceipt.v1";
    public const string ProgressPurpose = "VeyonCampus.StudentAgentHealthRepairProgress.v1";
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

    public static void ValidateProgressFrame(StudentAgentHealthRepairProgressFrame frame,
        StudentAgentHealthRepairCommand command, StudentAgentHealthRepairProgressPhase? previousPhase,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var now = nowUtc.ToUniversalTime();
        var phaseIsValid = frame.Phase switch
        {
            StudentAgentHealthRepairProgressPhase.Delivered => previousPhase is null && frame.Outcome is null,
            StudentAgentHealthRepairProgressPhase.Executing =>
                previousPhase == StudentAgentHealthRepairProgressPhase.Delivered && frame.Outcome is null,
            StudentAgentHealthRepairProgressPhase.Completed =>
                (previousPhase == StudentAgentHealthRepairProgressPhase.Delivered ||
                 previousPhase == StudentAgentHealthRepairProgressPhase.Executing) && frame.Outcome is not null,
            _ => false
        };
        var completedAction = frame.Phase == StudentAgentHealthRepairProgressPhase.Completed;
        if (frame.SchemaVersion != 1 || frame.Purpose != ProgressPurpose ||
            frame.CampusId != command.CampusId || frame.CommandId != command.CommandId ||
            frame.Nonce != command.Nonce || frame.Action != command.Action || !phaseIsValid ||
            frame.UpdatedUtc.Offset != TimeSpan.Zero || frame.UpdatedUtc < command.IssuedUtc ||
            frame.UpdatedUtc > now.AddMinutes(1) || frame.UpdatedUtc < now.Subtract(TimeSpan.FromMinutes(3)) ||
            (command.Action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy
                ? frame.WebsiteRevision != command.ExpectedWebsiteRevision
                : frame.WebsiteRevision is not null) ||
            (frame.WasAlreadyProcessed &&
             (frame.Phase != StudentAgentHealthRepairProgressPhase.Completed ||
              previousPhase != StudentAgentHealthRepairProgressPhase.Delivered)) ||
            (completedAction && !Enum.IsDefined(frame.Outcome!.Value)) ||
            (completedAction &&
             (frame.Outcome is StudentAgentHealthRepairOutcome.Succeeded or
                 StudentAgentHealthRepairOutcome.Queued) &&
             previousPhase != StudentAgentHealthRepairProgressPhase.Executing && !frame.WasAlreadyProcessed))
            throw new InvalidDataException("健康修复进度帧与本次命令或状态顺序不匹配。");
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
        CancellationToken cancellationToken = default,
        IProgress<StudentAgentHealthRepairProgressUpdate>? progress = null,
        IReadOnlyDictionary<string, long?>? expectedWebsiteRevisions = null,
        Guid? batchIdOverride = null)
    {
        ArgumentNullException.ThrowIfNull(campusPrivateKey);
        if (!Enum.IsDefined(action)) throw new InvalidDataException("健康修复动作无效。");
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var validated = WebsitePolicyTransport.NormalizeTargets(targets);
        var revisionMap = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        if (expectedWebsiteRevisions is not null)
        {
            foreach (var pair in expectedWebsiteRevisions)
            {
                var normalizedTarget = WebsitePolicyTransport.NormalizeTargets([pair.Key]).Single();
                if (!revisionMap.TryAdd(normalizedTarget, pair.Value))
                    throw new InvalidDataException("健康修复版本映射包含重复电脑。");
            }
        }
        var hasInvalidWebsiteRevision = action == StudentAgentHealthRepairAction.RefreshActiveWebsitePolicy &&
            (expectedWebsiteRevision is { } commonRevision
                ? commonRevision <= 0
                : validated.Any(target => !revisionMap.TryGetValue(target, out var revision) ||
                                           revision is null or <= 0));
        if (revisionMap.Keys.Any(target => !validated.Contains(target, StringComparer.OrdinalIgnoreCase)) ||
            (expectedWebsiteRevision is not null && expectedWebsiteRevisions is not null) ||
            (action == StudentAgentHealthRepairAction.EnsureVeyonServiceRunning &&
             (expectedWebsiteRevision is not null || revisionMap.Count > 0)) || hasInvalidWebsiteRevision)
            throw new InvalidDataException("健康修复网站版本映射与动作或目标列表不匹配。");
        var batchId = batchIdOverride is { } requestedBatchId && requestedBatchId != Guid.Empty
            ? requestedBatchId
            : Guid.NewGuid();
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        using var limit = new SemaphoreSlim(8, 8);
        var tasks = validated.Select(async target =>
        {
            var commandId = Guid.NewGuid();
            Report(StudentAgentHealthRepairProgressState.Queued, null,
                "等待教师端有限并发队列；不会保存为离线待执行命令。");
            var acquired = false;
            try
            {
                await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
                var now = DateTimeOffset.UtcNow;
                var targetRevision = revisionMap.TryGetValue(target, out var perTargetRevision)
                    ? perTargetRevision
                    : expectedWebsiteRevision;
                var command = new StudentAgentHealthRepairCommand(1,
                    StudentAgentHealthRepairCommandCryptography.Purpose, campusId, commandId,
                    Guid.NewGuid(), now, now.Add(StudentAgentHealthRepairCommandCryptography.MaximumCommandLifetime),
                    action, targetRevision);
                var signedCommand = StudentAgentHealthRepairCommandCryptography.Sign(command, campusPrivateKey);
                var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
                    WebsitePolicyAgent.HealthRepairPath).Uri;
                using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(signedCommand, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation(StudentAgentHealthRepairCommandCryptography.RequestNonceHeader,
                    command.Nonce.ToString("D"));
                using var targetTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                targetTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    targetTimeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _ = await ReadBoundedResponseAsync(response.Content,
                        StudentAgentResponseCryptography.MaximumEnvelopeBytes, targetTimeout.Token)
                        .ConfigureAwait(false);
                    var detail = response.StatusCode == HttpStatusCode.NotFound
                        ? "学生 Agent 尚不支持健康修复，请先更新学生端。"
                        : $"健康修复请求未确认（HTTP {(int)response.StatusCode}）。";
                    Report(StudentAgentHealthRepairProgressState.Failed, null, detail);
                    return new StudentAgentHealthRepairDeliveryResult(target, command.CommandId, action,
                        false, response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict,
                        null, detail);
                }

                var pinnedKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(campusId, target);
                var receipt = await ReadVerifiedProgressAsync(response.Content, campusId, target, pinnedKey,
                    command, signedCommand, batchId, progress, targetTimeout.Token).ConfigureAwait(false);
                var succeeded = receipt.Outcome is StudentAgentHealthRepairOutcome.Succeeded or
                    StudentAgentHealthRepairOutcome.AlreadyHealthy or StudentAgentHealthRepairOutcome.Queued;
                var needsReview = receipt.Outcome is StudentAgentHealthRepairOutcome.NeedsManualReview or
                    StudentAgentHealthRepairOutcome.Failed or StudentAgentHealthRepairOutcome.ReplayRejected;
                return new StudentAgentHealthRepairDeliveryResult(target, command.CommandId, action,
                    succeeded, needsReview, receipt.Outcome,
                    Describe(receipt.Outcome, receipt.WasAlreadyProcessed),
                    succeeded ? StudentAgentHealthRepairProgressState.Succeeded :
                        StudentAgentHealthRepairProgressState.Failed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                              OperationCanceledException or
                                              UriFormatException or InvalidDataException or JsonException or
                                              CryptographicException or ArgumentException)
            {
                var timedOut = exception is TaskCanceledException or OperationCanceledException or HttpRequestException;
                var detail = exception is TaskCanceledException or OperationCanceledException
                    ? "教师端等待回执超时；Agent 若已签收，可能仍在完成当前动作。不会在设备重新上线时延迟排队执行；请先刷新状态，再判断是否重试。"
                    : exception is HttpRequestException ? "未能连接或保持与学生 Agent 的响应；是否收到命令无法确认，本次以 timed_out 结束，不会离线排队重试。"
                    : exception is InvalidDataException or CryptographicException
                        ? "Agent 身份或签名进度未通过核验；需要人工核对。"
                        : "健康修复回执格式无效；本次状态需要人工检查。";
                Report(timedOut ? StudentAgentHealthRepairProgressState.TimedOut :
                    StudentAgentHealthRepairProgressState.Failed, null, detail);
                return new StudentAgentHealthRepairDeliveryResult(target, commandId, action,
                    false, exception is InvalidDataException or CryptographicException, null, detail,
                    timedOut ? StudentAgentHealthRepairProgressState.TimedOut :
                        StudentAgentHealthRepairProgressState.Failed);
            }
            finally { if (acquired) limit.Release(); }

            void Report(StudentAgentHealthRepairProgressState state,
                StudentAgentHealthRepairOutcome? outcome, string detail) => progress?.Report(
                new StudentAgentHealthRepairProgressUpdate(batchId, target, commandId, action,
                    state, outcome, detail, DateTimeOffset.UtcNow));
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static async Task<StudentAgentHealthRepairReceipt> ReadVerifiedProgressAsync(
        HttpContent content, string campusId, string target, string? pinnedKey,
        StudentAgentHealthRepairCommand command, string signedCommand, Guid batchId,
        IProgress<StudentAgentHealthRepairProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentType?.MediaType != "application/x-ndjson")
            throw new InvalidDataException("Agent 没有返回受支持的签名进度流。");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var reader = new BoundedProgressLineReader(stream);
        StudentAgentHealthRepairProgressPhase? previousPhase = null;
        StudentAgentHealthRepairReceipt? receipt = null;
        var phaseCount = 0;
        while (true)
        {
            var responseText = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (responseText is null) break;
            if (receipt is not null || ++phaseCount > 3)
                throw new InvalidDataException("Agent 健康修复进度流有多余状态帧。");
            var verified = StudentAgentCommandAcknowledgementCryptography.Verify(responseText, campusId,
                command.Nonce, signedCommand, 200, pinnedKey, DateTimeOffset.UtcNow,
                maximumBodyBytes: 8192);
            if (!verified.MatchesPinnedKey)
                throw new InvalidDataException("Agent 身份未固定或已变化；修复回执未接受。");
            var body = Encoding.UTF8.GetBytes(verified.Payload.Body);
            PolicyJson.RejectDuplicateFields(body);
            var frame = JsonSerializer.Deserialize<StudentAgentHealthRepairProgressFrame>(
                            body, ApplicationPolicyCompiler.JsonOptions)
                        ?? throw new InvalidDataException("健康修复进度帧为空。");
            StudentAgentHealthRepairCommandCryptography.ValidateProgressFrame(frame, command, previousPhase,
                DateTimeOffset.UtcNow);
            previousPhase = frame.Phase;
            if (frame.Phase == StudentAgentHealthRepairProgressPhase.Delivered)
                progress?.Report(new StudentAgentHealthRepairProgressUpdate(batchId, target, command.CommandId,
                    command.Action, StudentAgentHealthRepairProgressState.Delivered, null,
                    "学生 Agent 已签收本次命令。", frame.UpdatedUtc));
            else if (frame.Phase == StudentAgentHealthRepairProgressPhase.Executing)
                progress?.Report(new StudentAgentHealthRepairProgressUpdate(batchId, target, command.CommandId,
                    command.Action, StudentAgentHealthRepairProgressState.Executing, null,
                    "学生 Agent 正在执行；等待签名结果。", frame.UpdatedUtc));
            else
            {
                var outcome = frame.Outcome!.Value;
                receipt = new StudentAgentHealthRepairReceipt(1,
                    StudentAgentHealthRepairCommandCryptography.ReceiptPurpose, frame.CampusId,
                    frame.CommandId, frame.Nonce, frame.Action, outcome, frame.WebsiteRevision, frame.UpdatedUtc,
                    frame.WasAlreadyProcessed);
                StudentAgentHealthRepairCommandCryptography.ValidateReceipt(receipt, command,
                    DateTimeOffset.UtcNow);
                var succeeded = outcome is StudentAgentHealthRepairOutcome.Succeeded or
                    StudentAgentHealthRepairOutcome.AlreadyHealthy or StudentAgentHealthRepairOutcome.Queued;
                progress?.Report(new StudentAgentHealthRepairProgressUpdate(batchId, target, command.CommandId,
                    command.Action, succeeded ? StudentAgentHealthRepairProgressState.Succeeded :
                        StudentAgentHealthRepairProgressState.Failed, outcome,
                    Describe(outcome, frame.WasAlreadyProcessed), frame.UpdatedUtc));
            }
        }
        return receipt ?? throw new InvalidDataException("Agent 未返回签名的终态回执。");
    }

    private static string Describe(StudentAgentHealthRepairOutcome outcome, bool wasAlreadyProcessed = false)
    {
        if (wasAlreadyProcessed) return "该命令此前已经处理；本次未重复执行。原结果：" + Describe(outcome);
        return Describe(outcome);
    }

    private static string Describe(StudentAgentHealthRepairOutcome outcome) => outcome switch
    {
        StudentAgentHealthRepairOutcome.Succeeded => "修复已完成并读回确认。",
        StudentAgentHealthRepairOutcome.AlreadyHealthy => "服务已经处于正常状态，无需修改。",
        StudentAgentHealthRepairOutcome.Queued => "策略刷新请求已排入队列；浏览器是否已应用仍需重新读取回执。",
        StudentAgentHealthRepairOutcome.BusyUpdating => "学生端更新或恢复正在进行；本次未执行修复。",
        StudentAgentHealthRepairOutcome.Busy => "学生 Agent 正在处理其他受保护操作；本次未执行修复。",
        StudentAgentHealthRepairOutcome.Expired => "命令在执行前已过期；本次未执行修复。",
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

    private sealed class BoundedProgressLineReader(Stream stream)
    {
        private const int MaximumTotalBytes = StudentAgentHealthRepairCommandCryptography.MaximumEnvelopeBytes * 3 + 3;
        private const int MaximumLineBytes = StudentAgentHealthRepairCommandCryptography.MaximumEnvelopeBytes;
        private readonly byte[] _chunk = new byte[4096];
        private int _offset;
        private int _count;
        private int _totalBytes;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();
            while (true)
            {
                if (_offset == _count)
                {
                    _count = await stream.ReadAsync(_chunk, cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0)
                    {
                        if (line.Length == 0) return null;
                        throw new InvalidDataException("Agent 健康修复进度帧缺少行结束符。");
                    }
                    _totalBytes += _count;
                    if (_totalBytes > MaximumTotalBytes)
                        throw new InvalidDataException("Agent 健康修复进度流超过大小限制。");
                }

                var newline = Array.IndexOf(_chunk, (byte)'\n', _offset, _count - _offset);
                var segmentEnd = newline >= 0 ? newline : _count;
                var segmentLength = segmentEnd - _offset;
                if (line.Length + segmentLength > MaximumLineBytes)
                    throw new InvalidDataException("Agent 健康修复进度帧超过大小限制。");
                line.Write(_chunk, _offset, segmentLength);
                _offset = newline >= 0 ? newline + 1 : _count;
                if (newline < 0) continue;
                var bytes = line.ToArray();
                if (bytes.Length > 0 && bytes[^1] == '\r') Array.Resize(ref bytes, bytes.Length - 1);
                return new UTF8Encoding(false, true).GetString(bytes);
            }
        }
    }
}
