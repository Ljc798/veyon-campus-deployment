using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>Sends a minimal, anonymous once-a-day presence signal when explicitly configured.</summary>
public static class AnonymousUsageHeartbeat
{
    public const string DefaultEndpoint =
        "https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com/v1/heartbeat";
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan HongKongOffset = TimeSpan.FromHours(8);

    public static bool IsAllowedPackageEndpoint(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ||
        string.Equals(endpoint, DefaultEndpoint, StringComparison.Ordinal);

    public static Uri ValidateEndpoint(string endpoint)
    {
        if (endpoint.Length is 0 or > 2048 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("遥测地址必须是 HTTPS 地址；仅本机回环地址允许使用 HTTP，且不能含凭据、查询参数或片段。");
        return uri;
    }

    public static async Task RunAsync(string endpoint, string installationIdPath, string applicationVersion,
        Guid? deploymentId, CancellationToken cancellationToken = default)
    {
        var uri = ValidateEndpoint(endpoint);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        string? installationId = null;
        var lastSentDayPath = installationIdPath + ".last-hkt-day";

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                installationId ??= await GetOrCreateInstallationIdAsync(installationIdPath, cancellationToken)
                    .ConfigureAwait(false);
                var today = GetHongKongDate();
                var lastSent = await ReadLastSentStateAsync(lastSentDayPath, cancellationToken).ConfigureAwait(false);
                if (lastSent is null || lastSent.Day != today ||
                    lastSent.ApplicationVersion != applicationVersion || lastSent.DeploymentId != deploymentId)
                {
                    using var response = await client.PostAsJsonAsync(uri,
                        new HeartbeatRequest(installationId, applicationVersion, deploymentId), cancellationToken)
                        .ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.NoContent)
                        throw new HttpRequestException("Telemetry endpoint did not return HTTP 204.", null,
                            response.StatusCode);
                    await WriteLastSentStateAsync(lastSentDayPath, today, applicationVersion, deploymentId,
                        cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(DelayUntilNextHongKongDay(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Telemetry is best-effort; retry later without affecting the policy agent.
                try { await Task.Delay(FailureRetryInterval, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    private static DateOnly GetHongKongDate() =>
        DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(HongKongOffset).DateTime);

    private static TimeSpan DelayUntilNextHongKongDay()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(HongKongOffset);
        var nextMidnight = new DateTimeOffset(now.Date.AddDays(1), HongKongOffset);
        return nextMidnight - now;
    }

    private static async Task<LastHeartbeatState?> ReadLastSentStateAsync(string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var value = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
        var fields = value.Split('|');
        if (fields.Length != 3 ||
            !DateOnly.TryParseExact(fields[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day)) return null;
        Guid? deploymentId = null;
        if (fields[2].Length > 0)
        {
            if (!Guid.TryParse(fields[2], out var parsedDeploymentId)) return null;
            deploymentId = parsedDeploymentId;
        }
        return new LastHeartbeatState(day, fields[1], deploymentId);
    }

    private static async Task WriteLastSentStateAsync(string path, DateOnly day, string applicationVersion,
        Guid? deploymentId, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        var value = string.Join('|', day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            applicationVersion, deploymentId?.ToString("D") ?? "");
        await File.WriteAllTextAsync(temporaryPath, value, Encoding.ASCII, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async Task<string> GetOrCreateInstallationIdAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path)) return await ReadInstallationIdAsync(path, cancellationToken).ConfigureAwait(false);

        var parent = Path.GetDirectoryName(Path.GetFullPath(path))
                     ?? throw new InvalidDataException("匿名安装标识目录无效。");
        Directory.CreateDirectory(parent);
        var candidate = Guid.NewGuid().ToString("N");
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                256, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var bytes = Encoding.ASCII.GetBytes(candidate);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return candidate;
        }
        catch (IOException) when (File.Exists(path))
        {
            return await ReadInstallationIdAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadInstallationIdAsync(string path, CancellationToken cancellationToken)
    {
        var value = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
        if (value.Length != 32 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException("本机匿名安装标识格式无效。");
        return value.ToLowerInvariant();
    }

    private sealed record HeartbeatRequest(
        [property: JsonPropertyName("installationId")] string InstallationId,
        [property: JsonPropertyName("applicationVersion")] string ApplicationVersion,
        [property: JsonPropertyName("deploymentId")] Guid? DeploymentId);

    private sealed record LastHeartbeatState(DateOnly Day, string ApplicationVersion, Guid? DeploymentId);
}
