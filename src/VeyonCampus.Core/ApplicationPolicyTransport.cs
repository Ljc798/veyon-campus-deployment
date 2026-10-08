using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

public sealed record ApplicationPolicyDeliveryResult(string Target, bool Succeeded, string Detail,
    bool NeedsReview = false);

public sealed record ApplicationPolicyAuditDeliveryResult(string Target, bool Succeeded, string Detail,
    ApplicationPolicyAuditResponse? Response = null, bool NeedsReview = false);

public sealed record ApplicationInventoryDeliveryResult(string Target, bool Succeeded, string Detail,
    IReadOnlyList<ApplicationInventoryItem> Items, bool NeedsReview = false,
    IReadOnlyList<StudentAccountInventoryItem>? StudentAccounts = null);

public sealed record ApplicationPolicyPushHistoryEntry(DateTimeOffset CreatedUtc, string CampusId, long Revision,
    ApplicationPolicyMode Mode, int RuleCount, int StudentCount, IReadOnlyList<ApplicationPolicyDeliveryResult> Results);

/// <summary>Delivers signed application policies and signed audit requests over the classroom LAN.</summary>
public static class ApplicationPolicyTransport
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        // The current SYSTEM Agent uses the shared default JSON options, which retain PascalCase DTO names.
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<IReadOnlyList<ApplicationPolicyDeliveryResult>> PushAsync(IEnumerable<string> targets,
        string signedPolicyJson, string campusId, CancellationToken cancellationToken = default)
    {
        var validated = WebsitePolicyTransport.NormalizeTargets(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(signedPolicyJson);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        return await SendAsync(validated, WebsitePolicyAgent.ApplicationPolicyPath, signedPolicyJson,
            campusId, cancellationToken, maximumResponseBytes: 8192).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<ApplicationPolicyAuditDeliveryResult>> ReadAuditAsync(
        IEnumerable<string> targets, string campusId, RSA privateKey, int lookbackHours = 24,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var validated = WebsitePolicyTransport.NormalizeTargets(targets);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var now = DateTimeOffset.UtcNow;
        var request = new ApplicationPolicyAuditRequest(1, ApplicationPolicyAuditCryptography.Purpose,
            campusId, Guid.NewGuid(), now, lookbackHours);
        var signedRequest = ApplicationPolicyAuditCryptography.SignRequest(request, privateKey);
        var results = await SendAsync(validated, WebsitePolicyAgent.ApplicationPolicyAuditPath, signedRequest,
            campusId, cancellationToken, maximumResponseBytes: 2 * 1024 * 1024).ConfigureAwait(false);
        return Array.AsReadOnly(results.Select(result =>
        {
            if (!result.Succeeded)
                return new ApplicationPolicyAuditDeliveryResult(result.Target, false, result.Detail, NeedsReview: result.NeedsReview);
            try
            {
                PolicyJson.RejectDuplicateFields(Encoding.UTF8.GetBytes(result.Detail));
                var response = JsonSerializer.Deserialize<ApplicationPolicyAuditResponse>(result.Detail, ResponseJsonOptions)
                    ?? throw new InvalidDataException("学生端没有返回审核结果。");
                ValidateResponse(response, request, DateTimeOffset.UtcNow);
                return new ApplicationPolicyAuditDeliveryResult(result.Target, true,
                    "收到 Agent 已签名确认的审核回执；身份、随机数和请求内容均已核对。", response);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
            {
                return new ApplicationPolicyAuditDeliveryResult(result.Target, false,
                    "学生端审核回执无效：" + exception.Message, NeedsReview: true);
            }
        }).ToArray());
    }

    public static async Task<IReadOnlyList<ApplicationInventoryDeliveryResult>> ReadInventoryAsync(
        IEnumerable<string> targets, string campusId, RSA privateKey, CancellationToken cancellationToken = default,
        bool accountsOnly = false)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var validated = WebsitePolicyTransport.NormalizeTargets(targets);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var request = new ApplicationInventoryRequest(1, ApplicationInventoryCryptography.RequestPurpose,
            campusId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var signedRequest = ApplicationInventoryCryptography.SignRequest(request, privateKey);
        var results = await SendAsync(validated, accountsOnly ? WebsitePolicyAgent.StudentAccountsPath : WebsitePolicyAgent.ApplicationInventoryPath, signedRequest,
            campusId, cancellationToken, timeout: TimeSpan.FromSeconds(50), maximumResponseBytes: 512 * 1024)
            .ConfigureAwait(false);
        return Array.AsReadOnly(results.Select(result =>
        {
            if (!result.Succeeded)
                return new ApplicationInventoryDeliveryResult(result.Target, false, result.Detail,
                    Array.Empty<ApplicationInventoryItem>(), result.NeedsReview);
            try
            {
                PolicyJson.RejectDuplicateFields(Encoding.UTF8.GetBytes(result.Detail));
                var response = JsonSerializer.Deserialize<ApplicationInventoryResponse>(result.Detail, ResponseJsonOptions)
                    ?? throw new InvalidDataException("学生端没有返回应用清单。");
                ApplicationInventoryCryptography.ValidateResponse(response, request, DateTimeOffset.UtcNow);
                if (accountsOnly && (response.StudentAccounts is null || response.Items.Count != 0))
                    throw new InvalidDataException("未返回学生账户；请更新学生 Agent 或在高级设置填写 SID。");
                return new ApplicationInventoryDeliveryResult(result.Target, true,
                    $"读取 {response.Items.Count} 个程序条目；Agent 身份和本次请求均已通过签名核对。", response.Items, StudentAccounts: response.StudentAccounts);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
            {
                return new ApplicationInventoryDeliveryResult(result.Target, false,
                    "学生端应用清单无效：" + exception.Message, Array.Empty<ApplicationInventoryItem>(), NeedsReview: true);
            }
        }).ToArray());
    }

    private static async Task<IReadOnlyList<ApplicationPolicyDeliveryResult>> SendAsync(
        IReadOnlyList<string> targets, string path, string body, string campusId, CancellationToken cancellationToken,
        TimeSpan? timeout = null, int maximumResponseBytes = 64 * 1024)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(6) };
        using var limit = new SemaphoreSlim(16, 16);
        var tasks = targets.Select(async target =>
        {
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port, path).Uri;
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                var nonce = Guid.NewGuid();
                using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
                request.Headers.TryAddWithoutValidation("X-VeyonCampus-Request-Nonce", nonce.ToString("D"));
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var responseBody = await ReadResponseBoundedAsync(response.Content,
                    StudentAgentResponseCryptography.MaximumCommandEnvelopeBytes,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new ApplicationPolicyDeliveryResult(target, false,
                        $"HTTP {(int)response.StatusCode}：{Truncate(responseBody, 512)}",
                        NeedsReview: (int)response.StatusCode >= 500);
                try
                {
                    var pinnedKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(campusId, target);
                    var verified = StudentAgentCommandAcknowledgementCryptography.Verify(responseBody, campusId,
                        nonce, body, (int)response.StatusCode, pinnedKey, DateTimeOffset.UtcNow,
                        maximumResponseBytes);
                    if (!verified.MatchesPinnedKey)
                        return new ApplicationPolicyDeliveryResult(target, false,
                            $"Agent 身份需先核对；指纹 {verified.Fingerprint}。请在教师端读取并固定此电脑身份后重试。",
                            NeedsReview: true);
                    return new ApplicationPolicyDeliveryResult(target, true, verified.Payload.Body);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or
                                                  UnauthorizedAccessException or CryptographicException)
                {
                    return new ApplicationPolicyDeliveryResult(target, false,
                        "Agent 策略/审核/清单回执未通过签名和身份核验：" + exception.Message, NeedsReview: true);
                }
            }
            catch (InvalidDataException exception)
            {
                return new ApplicationPolicyDeliveryResult(target, false, exception.Message, NeedsReview: true);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                var endpoint = $"{target}:{WebsitePolicyAgent.Port}";
                return new ApplicationPolicyDeliveryResult(target, false, exception is TaskCanceledException
                    ? $"连接 {endpoint} 超时。请确认学生端 Agent 已安装且电脑处于可通信状态。"
                    : $"无法连接 {endpoint}：{exception.Message} 请检查电脑名/IP、学生端 Agent 以及局域网防火墙。", NeedsReview: true);
            }
            finally { limit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static async Task<string> ReadResponseBoundedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength > maximumBytes)
            throw new InvalidDataException("学生端 HTTP 响应超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("学生端 HTTP 响应超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        try { return new UTF8Encoding(false, true).GetString(buffer.ToArray()); }
        catch (DecoderFallbackException exception)
        { throw new InvalidDataException("学生端 HTTP 响应不是有效 UTF-8。", exception); }
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static void ValidateResponse(ApplicationPolicyAuditResponse response,
        ApplicationPolicyAuditRequest request, DateTimeOffset nowUtc)
    {
        if (response.SchemaVersion != 1 || response.Purpose != "VeyonCampus.ApplicationPolicyAuditResponse.v1" ||
            response.CampusId != request.CampusId || response.Nonce != request.Nonce ||
            response.Results is null || response.Results.Count > ApplicationPolicyCompiler.MaximumRules * ApplicationPolicyCompiler.MaximumStudents ||
            response.FromUtc.Offset != TimeSpan.Zero || response.CollectedUtc.Offset != TimeSpan.Zero ||
            response.CollectedUtc < request.IssuedUtc.AddMinutes(-1) || response.CollectedUtc > nowUtc.AddMinutes(2) ||
            response.FromUtc < request.IssuedUtc.AddHours(-request.LookbackHours).AddMinutes(-5) ||
            response.FromUtc > request.IssuedUtc.AddHours(-request.LookbackHours).AddMinutes(5) ||
            (response.PolicyRevision is not null and <= 0) ||
            (response.Mode is { } mode && !Enum.IsDefined(mode)) ||
            (response.IsSimulation && (response.Mode != ApplicationPolicyMode.Audit || string.IsNullOrWhiteSpace(response.CoverageNote) || response.CoverageNote.Length > 512)) ||
            (!response.IsSimulation && response.CoverageNote is not null) ||
            response.Results.Any(item => item is null || item.RuleId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.DisplayName) || item.DisplayName.Length > 120 ||
                item.StudentSid is null || !Regex.IsMatch(item.StudentSid,
                    @"^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$",
                    RegexOptions.CultureInvariant) ||
                !uint.TryParse(item.StudentSid[(item.StudentSid.LastIndexOf('-') + 1)..], out var rid) || rid < 1000 ||
                item.WouldBlockCount < 0 || item.BlockedCount < 0))
            throw new InvalidDataException("审核回执的校区、随机数、时间或结果字段不匹配。");
    }
}

/// <summary>Stores bounded per-device application-policy outcomes without policy rules or signed payloads.</summary>
public static class ApplicationPolicyPushHistoryStore
{
    public const int MaximumRuns = 50;
    private const long MaximumHistoryBytes = 4 * 1024 * 1024;
    private const int MaximumDetailCharacters = 512;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "ApplicationPolicy");

    public static ApplicationPolicyPushHistoryEntry? ReadLatest(string? directory = null)
    {
        lock (Gate)
        {
            var entries = ReadEntries(GetHistoryPath(directory));
            return entries.Count == 0 ? null : entries[^1];
        }
    }

    public static void Append(ApplicationPolicyPushHistoryEntry entry, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (Gate)
        {
            var path = GetHistoryPath(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var entries = ReadEntries(path);
            entries.Add(Validate(entry));
            if (entries.Count > MaximumRuns) entries.RemoveRange(0, entries.Count - MaximumRuns);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
            if (bytes.Length > MaximumHistoryBytes)
                throw new IOException("本机应用策略推送记录超过大小限制；本次推送结果仍可见，但没有覆盖已有记录。");
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
        }
    }

    private static string GetHistoryPath(string? directory) => Path.Combine(
        Path.GetFullPath(directory ?? DefaultDirectory), "push-history.json");

    private static List<ApplicationPolicyPushHistoryEntry> ReadEntries(string path)
    {
        if (!File.Exists(path)) return [];
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumHistoryBytes ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("本机应用策略推送记录为空、过大或包含链接；没有覆盖记录。");
        var entries = JsonSerializer.Deserialize<List<ApplicationPolicyPushHistoryEntry>>(File.ReadAllBytes(path), JsonOptions)
                      ?? throw new InvalidDataException("本机应用策略推送记录格式无效。");
        if (entries.Count > MaximumRuns || entries.Any(entry => entry is null))
            throw new InvalidDataException("本机应用策略推送记录数量或内容无效。");
        return entries.Select(Validate).ToList();
    }

    private static ApplicationPolicyPushHistoryEntry Validate(ApplicationPolicyPushHistoryEntry entry)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(entry.CampusId);
        if (entry.Revision <= 0 || !Enum.IsDefined(entry.Mode) || entry.RuleCount is < 0 or > ApplicationPolicyCompiler.MaximumRules ||
            entry.StudentCount is < 0 or > ApplicationPolicyCompiler.MaximumStudents || entry.Results is null ||
            entry.Results.Count is 0 or > 150 || entry.Results.Any(result => result is null ||
                string.IsNullOrWhiteSpace(result.Target) || result.Detail is null))
            throw new InvalidDataException("本机应用策略推送记录字段无效。");
        var targets = WebsitePolicyTransport.NormalizeTargets(entry.Results.Select(result => result.Target));
        if (targets.Count != entry.Results.Count) throw new InvalidDataException("本机应用策略推送记录含重复设备。");
        var safe = entry.Results.Select(result => result with
        {
            Detail = new string(result.Detail.Where(character => !char.IsControl(character)).Take(MaximumDetailCharacters).ToArray())
        }).ToArray();
        return entry with { CreatedUtc = entry.CreatedUtc.ToUniversalTime(), Results = Array.AsReadOnly(safe) };
    }
}
