using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>A compact, signed snapshot for frequent classroom health refreshes.</summary>
public sealed record StudentAgentRuntimeSummary(
    int SchemaVersion,
    string Purpose,
    string CampusId,
    Guid Nonce,
    DateTimeOffset CollectedUtc,
    string AgentVersion,
    string ConfigFingerprint,
    StudentVeyonServiceState VeyonServiceState,
    long WebsiteRevision,
    WebsitePolicyMode WebsiteMode,
    DateTimeOffset? WebsiteExpiresUtc,
    bool ApplicationSupported,
    long? ApplicationRevision,
    ApplicationPolicyMode? ApplicationMode,
    DateTimeOffset? ApplicationExpiresUtc,
    bool SystemPolicySupported,
    long? SystemPolicyRevision,
    bool? SystemPolicyPending,
    EdgeWebsitePolicyExecutionMode? EdgeExecutionMode,
    long? EdgePolicyRevision,
    int? EdgeProfileCount,
    int? EdgeDegradedProfileCount,
    string UpdateTransactionState,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? StudentSetupVersion = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    StudentInputGuardRuntimeSnapshot? InputGuard = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? BootId = null);

/// <summary>Short-lived InputGuard state included only in runtime-summary schema v2.</summary>
public sealed record StudentInputGuardRuntimeSnapshot(int SchemaVersion, bool Supported,
    StudentInputGuardPhase Phase, DateTimeOffset CollectedUtc, Guid AgentRunId, Guid ControlEpoch,
    Guid? LeaseId, DateTimeOffset? LeaseExpiresUtc);

/// <summary>
/// Provides an opaque, stable identifier for the current Windows boot. It is derived from the
/// machine name and the estimated OS boot time, not from the Agent process lifetime.
/// </summary>
public static class StudentAgentBootIdentity
{
    private static readonly string? CurrentBootId = CreateBootId();

    public static string? GetCurrentBootId() => CurrentBootId;

    private static string? CreateBootId()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var uptimeMilliseconds = Environment.TickCount64;
            if (uptimeMilliseconds < 0) return null;

            // TickCount64 is monotonic across Agent restarts. Rounding the estimated boot time
            // absorbs its millisecond resolution while remaining far finer than a real reboot.
            var estimatedBootTicks = DateTimeOffset.UtcNow.UtcTicks -
                TimeSpan.FromMilliseconds(uptimeMilliseconds).Ticks;
            const long bucketTicks = 5 * TimeSpan.TicksPerSecond;
            var roundedBootTicks = (long)Math.Round(estimatedBootTicks / (double)bucketTicks) * bucketTicks;
            var machineName = Environment.MachineName.Trim().ToUpperInvariant();
            if (machineName.Length == 0) return null;

            var input = Encoding.UTF8.GetBytes(
                $"VeyonCampus.StudentAgentBootIdentity.v1|{machineName}|{roundedBootTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return Convert.ToHexString(SHA256.HashData(input));
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or
            OverflowException or CryptographicException)
        {
            return null;
        }
    }
}

public sealed record StudentAgentRuntimeSummaryDeliveryResult(string Target, bool Succeeded,
    string Detail, StudentAgentRuntimeSummary? Summary = null, bool NeedsReview = false,
    StudentAgentIdentityTrustCandidate? IdentityCandidate = null);

public static class StudentAgentRuntimeSummaryCryptography
{
    public const string Purpose = "VeyonCampus.StudentAgentRuntimeSummary.v2";
    public const int SchemaVersion = 2;
    public const string LegacyPurpose = "VeyonCampus.StudentAgentRuntimeSummary.v1";
    public const int LegacySchemaVersion = 1;

    public static void Validate(StudentAgentRuntimeSummary summary,
        WebsitePolicyStatusRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var now = nowUtc.ToUniversalTime();
        var isLegacy = summary.SchemaVersion == LegacySchemaVersion && summary.Purpose == LegacyPurpose &&
                       summary.InputGuard is null;
        var isCurrent = summary.SchemaVersion == SchemaVersion && summary.Purpose == Purpose &&
                        summary.InputGuard is not null;
        if ((!isLegacy && !isCurrent) ||
            summary.CampusId != request.CampusId || summary.Nonce != request.Nonce ||
            summary.CollectedUtc.Offset != TimeSpan.Zero || summary.CollectedUtc > now.AddMinutes(2) ||
            summary.CollectedUtc < now.AddMinutes(-2) || string.IsNullOrWhiteSpace(summary.AgentVersion) ||
            summary.AgentVersion.Length > 64 || string.IsNullOrWhiteSpace(summary.ConfigFingerprint) ||
            summary.ConfigFingerprint.Length != 64 || !summary.ConfigFingerprint.All(Uri.IsHexDigit) ||
            !Enum.IsDefined(summary.VeyonServiceState) ||
            summary.WebsiteRevision < 0 || !Enum.IsDefined(summary.WebsiteMode) ||
            summary.WebsiteExpiresUtc is { Offset: var websiteOffset } && websiteOffset != TimeSpan.Zero ||
            summary.ApplicationRevision < 0 || summary.SystemPolicyRevision < 0 ||
            summary.ApplicationMode is { } applicationMode && !Enum.IsDefined(applicationMode) ||
            summary.ApplicationExpiresUtc is { Offset: var applicationOffset } && applicationOffset != TimeSpan.Zero ||
            summary.EdgeExecutionMode is { } edgeMode && !Enum.IsDefined(edgeMode) ||
            summary.EdgePolicyRevision < 0 || summary.EdgeProfileCount is < 0 or > 128 ||
            summary.EdgeDegradedProfileCount is < 0 or > 128 ||
            summary.EdgeDegradedProfileCount > summary.EdgeProfileCount ||
            summary.BootId is { } bootId &&
                (bootId.Length != 64 || !bootId.All(Uri.IsHexDigit)) ||
            (summary.EdgeExecutionMode is null) != (summary.EdgePolicyRevision is null) ||
            (summary.EdgePolicyRevision is null) != (summary.EdgeProfileCount is null) ||
            (summary.EdgeProfileCount is null) != (summary.EdgeDegradedProfileCount is null) ||
            summary.ApplicationSupported != (summary.ApplicationRevision is not null) ||
            summary.ApplicationSupported != (summary.ApplicationMode is not null) ||
            !summary.ApplicationSupported && summary.ApplicationExpiresUtc is not null ||
            summary.SystemPolicySupported != (summary.SystemPolicyRevision is not null) ||
            summary.SystemPolicySupported != (summary.SystemPolicyPending is not null) ||
            summary.UpdateTransactionState is not ("notFound" or "inProgress" or "committed" or "rolledBack" or "needsReview"))
            throw new InvalidDataException("学生 Agent 轻量运行摘要字段无效。");

        if (summary.InputGuard is { } guard)
            ValidateInputGuard(guard, now);

        if (summary.StudentSetupVersion is { } setupVersion)
        {
            if (string.IsNullOrWhiteSpace(setupVersion) || setupVersion.Length > 64)
                throw new InvalidDataException("学生部署工具版本字段无效。");
            _ = ApplicationReleaseClient.CompareVersions(setupVersion, setupVersion);
        }
        _ = ApplicationReleaseClient.CompareVersions(summary.AgentVersion, summary.AgentVersion);
    }

    private static void ValidateInputGuard(StudentInputGuardRuntimeSnapshot guard, DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        if (guard.SchemaVersion != 1 || guard.CollectedUtc.Offset != TimeSpan.Zero ||
            guard.CollectedUtc > now.AddMinutes(2) || guard.CollectedUtc < now.AddMinutes(-2) ||
            guard.AgentRunId == Guid.Empty || guard.ControlEpoch == Guid.Empty ||
            !Enum.IsDefined(guard.Phase) || guard.Supported == (guard.Phase == StudentInputGuardPhase.Unsupported) ||
            (guard.LeaseId is null) != (guard.LeaseExpiresUtc is null) ||
            guard.LeaseId == Guid.Empty ||
            guard.LeaseExpiresUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
            guard.LeaseExpiresUtc is { } expiry && (expiry <= guard.CollectedUtc ||
                expiry > guard.CollectedUtc.Add(StudentInputGuardCommandCryptography.MaximumLeaseLifetime)) ||
            guard.Phase == StudentInputGuardPhase.Locked && guard.LeaseId is null ||
            !guard.Supported && guard.LeaseId is not null)
            throw new InvalidDataException("轻量状态中的 InputGuard 回执字段无效。");
    }
}

public static class StudentAgentRuntimeSummaryTransport
{
    public const string Path = WebsitePolicyAgent.RuntimeSummaryPathV2;
    public const string LegacyPath = WebsitePolicyAgent.RuntimeSummaryPath;
    public const int MaximumConcurrency = 12;
    private static readonly TimeSpan TargetTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly SemaphoreSlim ConcurrencyGate = new(MaximumConcurrency, MaximumConcurrency);

    public static async Task<IReadOnlyList<StudentAgentRuntimeSummaryDeliveryResult>> ReadAsync(
        IEnumerable<string> targets, string campusId, RSA privateKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var validated = WebsitePolicyTransport.NormalizeTargets(targets);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var tasks = validated.Select(async target =>
        {
            await ConcurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TargetTimeout);
                var (request, response) = await SendSummaryRequestAsync(client, target, campusId, privateKey,
                    Path, requestTimeout.Token).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    response.Dispose();
                    (request, response) = await SendSummaryRequestAsync(client, target, campusId, privateKey,
                        LegacyPath, requestTimeout.Token).ConfigureAwait(false);
                }
                using (response)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        return new StudentAgentRuntimeSummaryDeliveryResult(target, false,
                            "此学生 Agent 版本不支持自动轻量状态；请通过学生 Setup 的管理员维护更新 Agent。");
                    if ((int)response.StatusCode is < 200 or >= 300)
                        return new StudentAgentRuntimeSummaryDeliveryResult(target, false,
                            $"轻量状态接口返回 HTTP {(int)response.StatusCode}。",
                            NeedsReview: (int)response.StatusCode >= 500);

                    var bytes = await ReadBoundedAsync(response.Content, requestTimeout.Token).ConfigureAwait(false);
                    PolicyJson.RejectDuplicateFields(bytes);
                    var body = Encoding.UTF8.GetString(bytes);
                    var pinnedKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(campusId, target);
                    var verified = StudentAgentResponseCryptography.Verify<StudentAgentRuntimeSummary>(body, pinnedKey);
                    StudentAgentRuntimeSummaryCryptography.Validate(verified.Payload, request, DateTimeOffset.UtcNow);
                    var candidate = new StudentAgentIdentityTrustCandidate(target, campusId, verified.PublicKeyPem,
                        verified.Fingerprint, pinnedKey is null ? null :
                            StudentAgentResponseCryptography.GetFingerprint(pinnedKey));
                    if (!verified.MatchesPinnedKey)
                        return new StudentAgentRuntimeSummaryDeliveryResult(target, false,
                            pinnedKey is null
                                ? $"Agent 签名有效但身份尚未固定，指纹：{verified.Fingerprint}"
                                : "Agent 身份指纹已变化；轻量状态被拒绝。",
                            NeedsReview: true, IdentityCandidate: candidate);
                    return new StudentAgentRuntimeSummaryDeliveryResult(target, true,
                        "签名、随机数和固定设备身份均已验证。", verified.Payload,
                        IdentityCandidate: candidate);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                UriFormatException or InvalidDataException or JsonException or CryptographicException)
            {
                return new StudentAgentRuntimeSummaryDeliveryResult(target, false,
                    exception is TaskCanceledException
                        ? $"轻量状态读取超时（{TargetTimeout.TotalSeconds:0.#} 秒）。"
                        : "轻量状态读取失败：" + exception.Message,
                    NeedsReview: exception is InvalidDataException or CryptographicException);
            }
            finally { ConcurrencyGate.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static async Task<(WebsitePolicyStatusRequest Request, HttpResponseMessage Response)>
        SendSummaryRequestAsync(HttpClient client, string target, string campusId, RSA privateKey,
            string path, CancellationToken cancellationToken)
    {
        var request = new WebsitePolicyStatusRequest(4, WebsitePolicyStatusCryptography.RequestPurposeV4,
            campusId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var signedRequest = WebsitePolicyStatusCryptography.SignRequest(request, privateKey);
        var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port, path).Uri;
        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(signedRequest, Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        return (request, response);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length &&
            length > StudentAgentResponseCryptography.MaximumEnvelopeBytes)
            throw new InvalidDataException("轻量状态响应超过大小限制。");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > StudentAgentResponseCryptography.MaximumEnvelopeBytes)
                throw new InvalidDataException("轻量状态响应超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
