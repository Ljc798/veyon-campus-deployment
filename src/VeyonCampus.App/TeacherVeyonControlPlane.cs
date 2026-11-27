using VeyonCampus.Core;

namespace VeyonCampus.App;

/// <summary>
/// Exposes only the single-frame screenshot operation needed by classroom previews.
/// Platform-specific Veyon commands stay behind this boundary.
/// </summary>
internal interface ITeacherVeyonControlPlane
{
    Task<byte[]> CaptureScreenAsync(string campusId, string target, CancellationToken cancellationToken);
}

/// <summary>
/// Windows single-frame capture. The desktop screen wall reads frames from the loopback Veyon WebAPI,
/// so the phone preview now uses the same source instead of <c>veyon-cli feature start ... Screenshot</c>.
/// The CLI path depends on a screenshot file appearing under %APPDATA%; when that file did not show up,
/// the desktop wall still showed a live picture while the phone stayed empty or timed out. The CLI is
/// kept as a fallback for machines where the loopback API is deliberately disabled.
/// </summary>
internal sealed class WindowsTeacherVeyonControlPlane : ITeacherVeyonControlPlane, IDisposable
{
    private const int MaximumCachedConnections = 64;
    private static readonly TimeSpan WebApiCooldown = TimeSpan.FromSeconds(30);

    private readonly VeyonWebApiClient _client = new();
    private readonly Dictionary<string, ScreenWallConnection> _connections =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<VeyonKeyCredential> _credentials = Array.Empty<VeyonKeyCredential>();
    private DateTimeOffset _webApiRetryUtc;

    public async Task<byte[]> CaptureScreenAsync(string campusId, string target,
        CancellationToken cancellationToken)
    {
        _ = campusId;
        var (jpeg, fallBackToCli, detail) = await TryCaptureOverWebApiAsync(target, cancellationToken)
            .ConfigureAwait(false);
        if (jpeg is not null) return jpeg;
        // The CLI is only a fallback for "this teacher has no loopback WebAPI". When the API answered
        // but rejected the frame, running a 45-second screenshot command would hide the real reason
        // (student unreachable vs. credential rejected), so report it instead.
        if (!fallBackToCli)
            throw new InvalidOperationException(detail is { Length: > 0 }
                ? detail : "Veyon WebAPI 没有返回画面。");
        return await ClassroomScreenPreviewCapture.VeyonCliScreenshotCapture
            .CaptureAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(byte[]? Jpeg, bool FallBackToCli, string Detail)> TryCaptureOverWebApiAsync(
        string target, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _webApiRetryUtc) return (null, false, "本机 WebAPI 正在冷却；请稍后重试。");
        if (_credentials.Count == 0)
            _credentials = VeyonKeyMaterial.TryLoad();
        if (_credentials.Count == 0) return (null, true, "");
        if (!await _client.ProbeAsync(cancellationToken).ConfigureAwait(false))
        {
            _webApiRetryUtc = now.AddMinutes(2);
            return (null, true, "");
        }

        var host = VeyonHostAddress.NormalizeOverride(target);
        if (host.Length == 0) return (null, true, "");
        ScreenWallConnection connection;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_connections.TryGetValue(host, out connection!))
            {
                if (_connections.Count >= MaximumCachedConnections)
                {
                    foreach (var stale in _connections.Take(_connections.Count / 2).ToArray())
                    {
                        _connections.Remove(stale.Key);
                        stale.Value.Dispose();
                    }
                }
                connection = new ScreenWallConnection(Guid.Empty, host, _credentials, _client, null);
                _connections[host] = connection;
            }
        }
        finally { _gate.Release(); }

        var frame = await connection.CaptureAsync(ScreenWallQuality.Detail, now, cancellationToken)
            .ConfigureAwait(false);
        if (frame is not null) return (frame.Jpeg, false, "");
        if (connection.State is ScreenWallLinkState.Unavailable)
            _webApiRetryUtc = DateTimeOffset.UtcNow.Add(WebApiCooldown);
        return (null, false, connection.Detail);
    }

    public void Dispose()
    {
        foreach (var connection in _connections.Values) connection.Dispose();
        _connections.Clear();
        _client.Dispose();
        _gate.Dispose();
    }
}

internal sealed class MacTeacherVeyonControlPlane : ITeacherVeyonControlPlane
{
    public Task<byte[]> CaptureScreenAsync(string campusId, string target, CancellationToken cancellationToken) =>
        MacVeyonScreenshotCapture.CaptureAsync(campusId, target, cancellationToken);
}

internal static class TeacherVeyonControlPlaneFactory
{
    public static ITeacherVeyonControlPlane? CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows()) return new WindowsTeacherVeyonControlPlane();
        if (OperatingSystem.IsMacOS()) return new MacTeacherVeyonControlPlane();
        return null;
    }
}
