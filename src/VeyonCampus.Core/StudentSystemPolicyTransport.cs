using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record StudentSystemPolicyDeliveryResult(string Target, bool Succeeded, string Detail, bool NeedsReview = false);

public static class StudentSystemPolicyTransport
{
    public static async Task<IReadOnlyList<StudentSystemPolicyDeliveryResult>> PushAsync(
        IEnumerable<string> targets, string signedPolicy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedPolicy);
        if (Encoding.UTF8.GetByteCount(signedPolicy) > StudentSystemPolicyCompiler.MaximumPayloadBytes * 2)
            throw new InvalidDataException("学生机系统策略超过传输大小限制。");
        var validated = WebsitePolicyTransport.NormalizeTargets(targets);
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        using var limit = new SemaphoreSlim(16, 16);
        var tasks = validated.Select(async target =>
        {
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var uri = new UriBuilder(Uri.UriSchemeHttp, target, WebsitePolicyAgent.Port,
                    WebsitePolicyAgent.StudentSystemPolicyPath).Uri;
                using var content = new StringContent(signedPolicy, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                while (true)
                {
                    var count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    if (buffer.Length + count > 16 * 1024)
                        throw new InvalidDataException("学生端策略回执超过大小限制。");
                    buffer.Write(chunk, 0, count);
                }
                string body;
                try { body = new UTF8Encoding(false, true).GetString(buffer.ToArray()); }
                catch (DecoderFallbackException exception) { throw new InvalidDataException("学生端策略回执不是 UTF-8。", exception); }
                var ok = response.IsSuccessStatusCode;
                return new StudentSystemPolicyDeliveryResult(target, ok,
                    ok ? body : $"HTTP {(int)response.StatusCode}：{body[..Math.Min(512, body.Length)]}",
                    NeedsReview: !ok && (int)response.StatusCode >= 500);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or IOException or InvalidDataException)
            {
                return new StudentSystemPolicyDeliveryResult(target, false, exception.Message, NeedsReview: true);
            }
            finally { limit.Release(); }
        }).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }
}
