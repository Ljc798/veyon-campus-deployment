using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace VeyonCampus.Core;

/// <summary>Teacher-to-Agent status and one-device command transport for InputGuard.</summary>
public static class StudentInputGuardTransport
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);

    [SupportedOSPlatform("windows")]
    public static async Task<StudentInputGuardStatusDeliveryResult> ReadStatusAsync(string target,
        string campusId, RSA campusPrivateKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campusPrivateKey);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        var normalizedTarget = WebsitePolicyTransport.NormalizeTargets([target]).Single();
        if (IsLocalMachineTarget(normalizedTarget))
            return new StudentInputGuardStatusDeliveryResult(normalizedTarget, false,
                "已拒绝把教师电脑作为 InputGuard 目标。", NeedsReview: true);

        var pinnedKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(campusId, normalizedTarget);
        if (pinnedKey is null)
            return new StudentInputGuardStatusDeliveryResult(normalizedTarget, false,
                "Student Agent 身份尚未固定；请先在设备健康页完成身份核对。", NeedsReview: true);

        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var request = new WebsitePolicyStatusRequest(4, WebsitePolicyStatusCryptography.RequestPurposeV4,
            campusId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var signedRequest = WebsitePolicyStatusCryptography.SignRequest(request, campusPrivateKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatusTimeout);
        try
        {
            using var response = await PostAsync(client, normalizedTarget, WebsitePolicyAgent.InputGuardStatusPath,
                signedRequest, null, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new StudentInputGuardStatusDeliveryResult(normalizedTarget, false,
                    response.StatusCode == HttpStatusCode.NotFound
                        ? "此 Student Agent 尚不支持 InputGuard 状态协议。"
                        : $"InputGuard 状态读取未确认（HTTP {(int)response.StatusCode}）。",
                    NeedsReview: response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict);

            var body = await ReadBoundedAsync(response.Content, StudentAgentResponseCryptography.MaximumEnvelopeBytes,
                timeout.Token).ConfigureAwait(false);
            var verified = StudentAgentResponseCryptography.Verify<StudentInputGuardStatusResponse>(
                Encoding.UTF8.GetString(body), pinnedKey);
            var expectedFingerprint = StudentAgentResponseCryptography.GetFingerprint(pinnedKey);
            StudentInputGuardCommandCryptography.ValidateStatus(verified.Payload, request,
                expectedFingerprint, DateTimeOffset.UtcNow);
            if (!verified.MatchesPinnedKey)
                return new StudentInputGuardStatusDeliveryResult(normalizedTarget, false,
                    "Student Agent 身份指纹已变化；InputGuard 状态被拒绝。", NeedsReview: true);
            return new StudentInputGuardStatusDeliveryResult(normalizedTarget, true,
                verified.Payload.Supported
                    ? "Agent 签名、设备身份与 InputGuard 状态均已验证。"
                    : "Agent 签名和设备身份已验证；当前 Agent 未连接可用的交互桌面 Guard。",
                verified.Payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
            OperationCanceledException or UriFormatException or InvalidDataException or JsonException or
            CryptographicException or ArgumentException)
        {
            return new StudentInputGuardStatusDeliveryResult(normalizedTarget, false,
                exception is TaskCanceledException or OperationCanceledException
                    ? $"InputGuard 状态读取超过 {StatusTimeout.TotalSeconds:0} 秒，状态未知。"
                    : "InputGuard 状态读取失败：" + exception.Message,
                NeedsReview: exception is InvalidDataException or CryptographicException);
        }
    }

    [SupportedOSPlatform("windows")]
    public static async Task<StudentInputGuardDeliveryResult> ExecuteAsync(string target, string campusId,
        RSA campusPrivateKey, Guid roomId, StudentInputGuardAction action, Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campusPrivateKey);
        if (!Enum.IsDefined(action) || roomId == Guid.Empty || leaseId == Guid.Empty)
            throw new InvalidDataException("InputGuard 命令的动作、房间或租约标识无效。");
        var statusResult = await ReadStatusAsync(target, campusId, campusPrivateKey, cancellationToken)
            .ConfigureAwait(false);
        if (!statusResult.Succeeded || statusResult.Status is not { } status)
            return new StudentInputGuardDeliveryResult(statusResult.Target, false, statusResult.NeedsReview,
                null, null, statusResult.Detail);
        if (!status.Supported)
            return new StudentInputGuardDeliveryResult(statusResult.Target, false, false,
                StudentInputGuardOutcome.Unsupported, null,
                "Agent 尚未连接受控交互桌面 Guard；没有发送锁定命令。");
        if (action == StudentInputGuardAction.Activate &&
            status.Phase is not (StudentInputGuardPhase.Unlocked or StudentInputGuardPhase.Expired or
                StudentInputGuardPhase.GuardExited))
            return new StudentInputGuardDeliveryResult(statusResult.Target, false, true,
                StudentInputGuardOutcome.Conflict, null,
                "设备当前不是可开始新租约的状态；刷新状态后再决定操作。");
        if (action is StudentInputGuardAction.Renew or StudentInputGuardAction.Release &&
            (status.Phase != StudentInputGuardPhase.Locked || status.LeaseId != leaseId))
            return new StudentInputGuardDeliveryResult(statusResult.Target, false, true,
                StudentInputGuardOutcome.Conflict, null,
                "当前签名状态中的租约与本次操作不匹配；没有发送命令。");

        var now = DateTimeOffset.UtcNow;
        var expiresUtc = now.Add(StudentInputGuardCommandCryptography.MaximumCommandLifetime);
        var leaseExpiresUtc = action is StudentInputGuardAction.Activate or StudentInputGuardAction.Renew
            ? now.Add(StudentInputGuardCommandCryptography.MaximumLeaseLifetime)
            : (DateTimeOffset?)null;
        var command = new StudentInputGuardCommand(1, StudentInputGuardCommandCryptography.Purpose,
            campusId, roomId, status.DeviceFingerprint, StudentInputGuardMachineIdentity.ReadLocalMachineId(),
            status.AgentRunId, Guid.NewGuid(), Guid.NewGuid(), status.ControlEpoch, leaseId,
            now, expiresUtc, action, leaseExpiresUtc);
        string signedCommand;
        try { signedCommand = StudentInputGuardCommandCryptography.Sign(command, campusPrivateKey); }
        catch (InvalidDataException exception)
        {
            return new StudentInputGuardDeliveryResult(statusResult.Target, false, true,
                StudentInputGuardOutcome.Failed, null, exception.Message);
        }

        var pinnedKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(campusId, statusResult.Target);
        if (pinnedKey is null)
            return new StudentInputGuardDeliveryResult(statusResult.Target, false, true,
                null, null, "Student Agent 身份固定已变化；没有发送 InputGuard 命令。");
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            using var response = await PostAsync(client, statusResult.Target, WebsitePolicyAgent.InputGuardCommandPath,
                signedCommand, command.Nonce, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new StudentInputGuardDeliveryResult(statusResult.Target, false,
                    response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict,
                    null, null, response.StatusCode == HttpStatusCode.NotFound
                        ? "此 Student Agent 尚不支持 InputGuard 命令。"
                        : $"InputGuard 命令未确认（HTTP {(int)response.StatusCode}）。");

            var body = await ReadBoundedAsync(response.Content,
                StudentAgentResponseCryptography.MaximumEnvelopeBytes, timeout.Token).ConfigureAwait(false);
            var verified = StudentAgentResponseCryptography.Verify<StudentInputGuardReceipt>(
                Encoding.UTF8.GetString(body), pinnedKey);
            StudentInputGuardCommandCryptography.ValidateReceipt(verified.Payload, command,
                DateTimeOffset.UtcNow);
            if (!verified.MatchesPinnedKey)
                return new StudentInputGuardDeliveryResult(statusResult.Target, false, true,
                    null, null, "Agent 身份指纹已变化；InputGuard 回执被拒绝。");
            return new StudentInputGuardDeliveryResult(statusResult.Target,
                verified.Payload.Outcome == StudentInputGuardOutcome.Succeeded,
                verified.Payload.Outcome is StudentInputGuardOutcome.Unknown or
                    StudentInputGuardOutcome.Conflict or StudentInputGuardOutcome.ReplayRejected,
                verified.Payload.Outcome, verified.Payload, verified.Payload.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
            OperationCanceledException or UriFormatException or InvalidDataException or JsonException or
            CryptographicException or ArgumentException)
        {
            return new StudentInputGuardDeliveryResult(statusResult.Target, false,
                exception is InvalidDataException or CryptographicException, null, null,
                exception is TaskCanceledException or OperationCanceledException
                    ? "InputGuard 命令回执超时；实际状态未知，刷新签名状态后再决定是否重试。"
                    : "InputGuard 命令回执核验失败：" + exception.Message);
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
            request.Headers.TryAddWithoutValidation(StudentInputGuardCommandCryptography.RequestNonceHeader,
                requestNonce.ToString("D"));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("InputGuard 响应超过大小限制。");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("InputGuard 响应超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    [SupportedOSPlatform("windows")]
    public static bool IsLocalMachineTarget(string target)
    {
        var localName = Environment.MachineName;
        if (string.Equals(target, localName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, Dns.GetHostName(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, Environment.MachineName + ".local", StringComparison.OrdinalIgnoreCase))
            return true;

        var localAddresses = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName())) localAddresses.Add(address);
        }
        catch (SocketException) { }
        if (IPAddress.TryParse(target, out var parsed)) return localAddresses.Contains(parsed);
        try
        {
            return Dns.GetHostAddresses(target).Any(localAddresses.Contains);
        }
        catch (SocketException) { return false; }
    }
}
