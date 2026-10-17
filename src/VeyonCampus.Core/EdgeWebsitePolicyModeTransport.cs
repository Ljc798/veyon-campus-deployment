using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace VeyonCampus.Core;

public sealed record EdgeWebsitePolicyMigrationResult(string Target, bool Succeeded, bool NeedsReview, string Detail);

/// <summary>Sends teacher-signed, per-device Edge mode migration commands over the existing Agent port.</summary>
public static class EdgeWebsitePolicyModeTransport
{
    public static async Task<IReadOnlyList<EdgeWebsitePolicyMigrationResult>> MigrateAsync(
        IEnumerable<string> targets, string campusId, RSA teacherPrivateKey, string extensionId,
        IReadOnlyDictionary<string, long> expectedRevisions,
        IReadOnlyDictionary<string, IReadOnlyList<string>> profileIdsByTarget,
        CancellationToken cancellationToken = default) =>
        await SetModeAsync(targets, campusId, teacherPrivateKey, extensionId, expectedRevisions,
            profileIdsByTarget, EdgeWebsitePolicyExecutionMode.ExtensionRealtime, cancellationToken)
            .ConfigureAwait(false);

    public static Task<IReadOnlyList<EdgeWebsitePolicyMigrationResult>> RestoreLegacyAsync(
        IEnumerable<string> targets, string campusId, RSA teacherPrivateKey, string extensionId,
        IReadOnlyDictionary<string, long> expectedRevisions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var validatedTargets = WebsitePolicyTransport.NormalizeTargets(targets);
        var emptyProfiles = validatedTargets.ToDictionary(target => target,
            _ => (IReadOnlyList<string>)Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return SetModeAsync(validatedTargets, campusId, teacherPrivateKey, extensionId, expectedRevisions,
            emptyProfiles, EdgeWebsitePolicyExecutionMode.LegacyRegistry, cancellationToken);
    }

    private static async Task<IReadOnlyList<EdgeWebsitePolicyMigrationResult>> SetModeAsync(
        IEnumerable<string> targets, string campusId, RSA teacherPrivateKey, string extensionId,
        IReadOnlyDictionary<string, long> expectedRevisions,
        IReadOnlyDictionary<string, IReadOnlyList<string>> profileIdsByTarget,
        EdgeWebsitePolicyExecutionMode desiredMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(teacherPrivateKey);
        ArgumentNullException.ThrowIfNull(expectedRevisions);
        ArgumentNullException.ThrowIfNull(profileIdsByTarget);
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        EdgeRealtimeWebsitePolicy.ValidateExtensionId(extensionId);
        if (!Enum.IsDefined(desiredMode)) throw new ArgumentOutOfRangeException(nameof(desiredMode));
        var validated = WebsitePolicyTransport.NormalizeTargets(targets);
        var revisions = NormalizeMap(expectedRevisions, validated, "策略版本");
        var profiles = NormalizeMap(profileIdsByTarget, validated, "Edge Profile 状态");
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        using var limit = new SemaphoreSlim(16, 16);
        var tasks = validated.Select(async target =>
        {
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var profileIds = profiles[target]
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (desiredMode == EdgeWebsitePolicyExecutionMode.ExtensionRealtime && profileIds.Length == 0)
                    return new EdgeWebsitePolicyMigrationResult(target, false, false,
                        "没有当前 Edge Profile 回执；没有启用实时扩展模式。");
                var nonce = Guid.NewGuid();
                var command = new EdgeWebsitePolicyModeCommand(1,
                    EdgeWebsitePolicyModeCommandCryptography.Purpose, campusId,
                    desiredMode, revisions[target], extensionId,
                    Array.AsReadOnly(profileIds), DateTimeOffset.UtcNow, nonce);
                var signedCommand = EdgeWebsitePolicyModeCommandCryptography.Sign(command, teacherPrivateKey);
                var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
                    WebsitePolicyAgent.EdgePolicyModePath).Uri;
                using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(signedCommand, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("X-VeyonCampus-Request-Nonce", nonce.ToString("D"));
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var responseText = await ReadBoundedResponseAsync(response.Content,
                    StudentAgentResponseCryptography.MaximumCommandEnvelopeBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new EdgeWebsitePolicyMigrationResult(target, false, (int)response.StatusCode >= 500,
                        $"HTTP {(int)response.StatusCode}：{responseText}");

                try
                {
                    var pinnedKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(campusId, target);
                    var verified = StudentAgentCommandAcknowledgementCryptography.Verify(responseText,
                        campusId, nonce, signedCommand, (int)response.StatusCode, pinnedKey,
                        DateTimeOffset.UtcNow, maximumBodyBytes: 8192);
                    if (!verified.MatchesPinnedKey)
                        return new EdgeWebsitePolicyMigrationResult(target, false, true,
                            $"Agent 身份需先核对；指纹 {verified.Fingerprint}。{verified.Payload.Body}");
                    return new EdgeWebsitePolicyMigrationResult(target, true, false, verified.Payload.Body);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or
                                                  UnauthorizedAccessException or CryptographicException)
                {
                    return new EdgeWebsitePolicyMigrationResult(target, false, true,
                        "Agent 迁移回执未通过签名和身份核验：" + exception.Message);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                return new EdgeWebsitePolicyMigrationResult(target, false, true,
                    exception is TaskCanceledException ? "连接学生 Agent 超时。" : "无法连接学生 Agent：" + exception.Message);
            }
            finally { limit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static Dictionary<string, TValue> NormalizeMap<TValue>(IReadOnlyDictionary<string, TValue> source,
        IReadOnlyList<string> targets, string label)
    {
        var normalized = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            var target = WebsitePolicyTransport.NormalizeTargets([pair.Key]).Single();
            if (!normalized.TryAdd(target, pair.Value))
                throw new InvalidDataException($"Edge 迁移{label}包含重复电脑。");
        }
        if (normalized.Count != targets.Count || targets.Any(target => !normalized.ContainsKey(target)))
            throw new InvalidDataException($"Edge 迁移{label}必须完整匹配所有目标电脑。");
        return normalized;
    }

    private static async Task<string> ReadBoundedResponseAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("学生 Agent 回执超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("学生 Agent 回执超过大小限制。");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
