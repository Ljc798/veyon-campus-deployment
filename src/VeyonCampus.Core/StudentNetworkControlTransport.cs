using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace VeyonCampus.Core;

/// <summary>Result of one teacher-to-Agent network switch operation.</summary>
public sealed record StudentNetworkControlDeliveryResult(string Target, bool Succeeded, bool NeedsReview,
    StudentNetworkControlReceipt? Receipt, string Detail);

/// <summary>Result of reading a student machine's current network switch state.</summary>
public sealed record StudentNetworkControlStatusDeliveryResult(string Target, bool Succeeded,
    bool NeedsReview, StudentNetworkControlStatus? Status, string Detail);

/// <summary>
/// Teacher-side transport for the per-device network switches (hardening and the Wi-Fi radio).
/// </summary>
/// <remarks>
/// Mirrors the InputGuard transport: a signed request is verified against the pinned student Agent
/// identity, and the reply is a signed receipt. The Agent exposes both endpoints on the same
/// HttpListener as the website policy, so a machine that can receive a policy can receive a switch.
/// </remarks>
public static class StudentNetworkControlTransport
{
    private const string NonceHeader = "X-VeyonCampus-Request-Nonce";
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    public static async Task<StudentNetworkControlStatusDeliveryResult> ReadStatusAsync(string target,
        string campusId, RSA campusPrivateKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campusPrivateKey);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var normalizedTarget = WebsitePolicyTransport.NormalizeTargets([target]).Single();
        if (StudentInputGuardTransport.IsLocalMachineTarget(normalizedTarget))
            return new StudentNetworkControlStatusDeliveryResult(normalizedTarget, false, true, null,
                "已拒绝把教师电脑作为网络开关目标。");

        // First use pins the Agent identity, exactly like the website status read, so a brand-new
        // classroom can read back switches without a separate enrolment step.
        var pinnedKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(campusId, normalizedTarget);
        using var handler = new StudentAgentChannelHttpMessageHandler(campusId,
            allowLegacyDirectFallback: true, allowDirectFallbackWhenChannelOffline: true);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var requestNonce = Guid.NewGuid();
        var request = new WebsitePolicyStatusRequest(4, WebsitePolicyStatusCryptography.RequestPurposeV4,
            campusId, requestNonce, DateTimeOffset.UtcNow);
        var signedRequest = WebsitePolicyStatusCryptography.SignRequest(request, campusPrivateKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatusTimeout);
        try
        {
            using var response = await PostAsync(client, normalizedTarget,
                WebsitePolicyAgent.NetworkControlStatusPath, signedRequest, null, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new StudentNetworkControlStatusDeliveryResult(normalizedTarget, false,
                    response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict, null,
                    response.StatusCode == HttpStatusCode.NotFound
                        ? "此 Student Agent 尚不支持网络开关状态协议。"
                        : $"网络开关状态读取未确认（HTTP {(int)response.StatusCode}）。");

            var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var verified = StudentAgentResponseCryptography.Verify<StudentNetworkControlStatus>(
                Encoding.UTF8.GetString(body), pinnedKey);
            if (!verified.MatchesPinnedKey)
                return new StudentNetworkControlStatusDeliveryResult(normalizedTarget, false, true, null,
                    "Student Agent 身份指纹已变化；网络开关状态被拒绝。");
            if (verified.Payload.RequestNonce != requestNonce ||
                !string.Equals(verified.Payload.CampusId, campusId, StringComparison.Ordinal))
                return new StudentNetworkControlStatusDeliveryResult(normalizedTarget, false, true, null,
                    "网络开关状态回执与本次请求不匹配。");
            return new StudentNetworkControlStatusDeliveryResult(normalizedTarget, true, false,
                verified.Payload, "Agent 签名与设备身份均已验证。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
            OperationCanceledException or UriFormatException or InvalidDataException or
            System.Text.Json.JsonException or CryptographicException or ArgumentException)
        {
            return new StudentNetworkControlStatusDeliveryResult(normalizedTarget, false,
                exception is InvalidDataException or CryptographicException, null,
                exception is TaskCanceledException or OperationCanceledException
                    ? $"网络开关状态读取超过 {StatusTimeout.TotalSeconds:0} 秒，状态未知。"
                    : "网络开关状态读取失败：" + exception.Message);
        }
    }

    /// <summary>
    /// Sends one switch change. <paramref name="hardeningEnabled"/> and <paramref name="wifiEnabled"/>
    /// are nullable on purpose: null leaves that switch untouched, so "禁止改网络" and "关 WiFi" are
    /// independent gestures that never silently undo each other.
    /// </summary>
    public static async Task<StudentNetworkControlDeliveryResult> ExecuteAsync(string target,
        string campusId, RSA campusPrivateKey, bool? hardeningEnabled, bool? wifiEnabled,
        int wifiRestoreSeconds = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campusPrivateKey);
        if (hardeningEnabled is null && wifiEnabled is null)
            throw new InvalidDataException("网络开关命令没有指定任何开关。");
        if (wifiEnabled is false &&
            (wifiRestoreSeconds < StudentNetworkControlCommandCryptography.MinimumRestoreSeconds ||
             wifiRestoreSeconds > StudentNetworkControlCommandCryptography.MaximumRestoreSeconds))
            throw new InvalidDataException("关闭 WiFi 必须指定自动恢复时限。");
        var normalizedTarget = WebsitePolicyTransport.NormalizeTargets([target]).Single();
        if (StudentInputGuardTransport.IsLocalMachineTarget(normalizedTarget))
            return new StudentNetworkControlDeliveryResult(normalizedTarget, false, true, null,
                "已拒绝把教师电脑作为网络开关目标。");

        var now = DateTimeOffset.UtcNow;
        var command = new StudentNetworkControlCommand(1,
            StudentNetworkControlCommandCryptography.Purpose, campusId, Guid.NewGuid(), now,
            now.Add(StudentNetworkControlCommandCryptography.MaximumCommandLifetime), hardeningEnabled,
            wifiEnabled, wifiEnabled is false ? wifiRestoreSeconds : 0);
        var signedCommand = StudentNetworkControlCommandCryptography.Sign(command, campusPrivateKey);

        var pinnedKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(campusId, normalizedTarget);
        if (pinnedKey is null)
        {
            // A brand-new classroom has not pinned the Agent yet; the status read is also what pins it,
            // so do that first instead of refusing the very first switch.
            var enrolment = await ReadStatusAsync(normalizedTarget, campusId, campusPrivateKey,
                cancellationToken).ConfigureAwait(false);
            if (!enrolment.Succeeded || enrolment.Status is null)
                return new StudentNetworkControlDeliveryResult(normalizedTarget, false,
                    enrolment.NeedsReview, null,
                    "无法先固定或核验学生 Agent 身份，未下发网络开关：" + enrolment.Detail);
            pinnedKey = new StudentAgentIdentityTrustStore()
                .FindTrustedPublicKey(campusId, normalizedTarget);
        }
        if (pinnedKey is null)
            return new StudentNetworkControlDeliveryResult(normalizedTarget, false, true, null,
                "Student Agent 身份尚未固定；请先读取一次设备状态，再下发网络开关。");

        using var handler = new StudentAgentChannelHttpMessageHandler(campusId,
            allowLegacyDirectFallback: true, allowDirectFallbackWhenChannelOffline: true);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            using var response = await PostAsync(client, normalizedTarget,
                WebsitePolicyAgent.NetworkControlCommandPath, signedCommand, command.Nonce, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new StudentNetworkControlDeliveryResult(normalizedTarget, false,
                    response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict, null,
                    response.StatusCode == HttpStatusCode.NotFound
                        ? "此 Student Agent 尚不支持网络开关命令，请先更新学生端。"
                        : $"网络开关命令未确认（HTTP {(int)response.StatusCode}）。");

            var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var verified = StudentAgentResponseCryptography.Verify<StudentNetworkControlReceipt>(
                Encoding.UTF8.GetString(body), pinnedKey);
            if (!verified.MatchesPinnedKey)
                return new StudentNetworkControlDeliveryResult(normalizedTarget, false, true, null,
                    "Agent 身份指纹已变化；网络开关回执被拒绝。");
            var receipt = verified.Payload;
            if (receipt.CommandNonce != command.Nonce)
                return new StudentNetworkControlDeliveryResult(normalizedTarget, false, true, null,
                    "网络开关回执与本次命令不匹配。");
            return new StudentNetworkControlDeliveryResult(normalizedTarget,
                receipt.Outcome == StudentNetworkControlOutcome.Succeeded,
                receipt.Outcome is StudentNetworkControlOutcome.Unknown or
                    StudentNetworkControlOutcome.Unsupported or
                    StudentNetworkControlOutcome.ReplayRejected,
                receipt, receipt.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
            OperationCanceledException or UriFormatException or InvalidDataException or
            System.Text.Json.JsonException or CryptographicException or ArgumentException)
        {
            return new StudentNetworkControlDeliveryResult(normalizedTarget, false,
                exception is InvalidDataException or CryptographicException, null,
                exception is TaskCanceledException or OperationCanceledException
                    ? "网络开关命令回执超时；实际状态未知，请刷新状态后再决定是否重试。"
                    : "网络开关命令回执核验失败：" + exception.Message);
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string target, string path,
        string signedBody, Guid? nonce, CancellationToken cancellationToken)
    {
        var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port, path).Uri;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(signedBody, Encoding.UTF8, "application/json")
        };
        if (nonce is { } requestNonce)
            request.Headers.TryAddWithoutValidation(NonceHeader, requestNonce.ToString("D"));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var maximumBytes = StudentAgentResponseCryptography.MaximumEnvelopeBytes;
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("网络开关响应超过大小限制。");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("网络开关响应超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
