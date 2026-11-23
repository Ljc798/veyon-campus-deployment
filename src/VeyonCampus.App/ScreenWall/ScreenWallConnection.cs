using System.Diagnostics;

namespace VeyonCampus.App;

internal enum ScreenWallLinkState
{
    Idle,
    Connecting,
    AwaitingFirstFrame,
    Live,
    Stale,
    Offline,
    AuthenticationFailed,
    Unavailable,
    FormatUnsupported
}

internal sealed record ScreenWallCapture(byte[] Jpeg, int HttpMs, DateTimeOffset CapturedUtc);

/// <summary>
/// One device's WebAPI session: authenticate once, then fetch frames over the same connection.
/// Re-authenticating per frame would cost 250–2000 ms each time, so the session is kept alive, renewed
/// before the server's 3 h lifetime, and dropped by the caller when the wall stops using it.
/// </summary>
internal sealed class ScreenWallConnection : IDisposable
{
    private static readonly TimeSpan MaximumSessionAge = TimeSpan.FromMinutes(150);
    private static readonly TimeSpan FirstFrameWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstFrameRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OfflineCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CredentialCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CapacityCooldown = TimeSpan.FromSeconds(5);

    private readonly VeyonWebApiClient _client;
    private readonly string _host;
    private readonly IReadOnlyList<VeyonKeyCredential> _credentials;
    private readonly Action<Guid, ScreenWallLinkState, string>? _onStateChanged;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _connectionUid;
    private DateTimeOffset _authenticatedUtc;
    private DateTimeOffset _cooldownUntilUtc;
    private int _credentialIndex;

    internal ScreenWallConnection(Guid targetId, string host, IReadOnlyList<VeyonKeyCredential> credentials,
        VeyonWebApiClient client, Action<Guid, ScreenWallLinkState, string>? onStateChanged)
    {
        TargetId = targetId;
        _host = host;
        _credentials = credentials;
        _client = client;
        _onStateChanged = onStateChanged;
        State = ScreenWallLinkState.Idle;
        Detail = "尚未连接";
    }

    internal Guid TargetId { get; }
    internal string Host => _host;
    internal ScreenWallLinkState State { get; private set; }
    internal string Detail { get; private set; }

    /// <summary>
    /// Fetches one frame. Returns null when the device is cooling down or the API has no frame yet;
    /// callers treat "no frame" as a state update, never as a reason to show a placeholder image.
    /// </summary>
    internal async Task<ScreenWallCapture?> CaptureAsync(ScreenWallQuality quality, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (now < _cooldownUntilUtc) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < _cooldownUntilUtc) return null;
            if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return null;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return await ReadFrameWithFirstFrameWaitAsync(quality, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (VeyonWebApiException exception) when (exception.IsConnectionInvalid && attempt == 0)
                {
                    // The server expired the connection (idle 60 s or 3 h lifetime): re-authenticate once.
                    await DropConnectionAsync().ConfigureAwait(false);
                    SetState(ScreenWallLinkState.Connecting, "连接已过期，正在重新认证");
                    if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false)) return null;
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ScreenWallCapture?> ReadFrameWithFirstFrameWaitAsync(ScreenWallQuality quality,
        CancellationToken cancellationToken)
    {
        var uid = _connectionUid ?? throw new InvalidOperationException("连接尚未认证。");
        var deadline = DateTimeOffset.UtcNow + FirstFrameWindow;
        while (true)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var bytes = await _client.ReadFrameAsync(uid, quality, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                SetState(ScreenWallLinkState.Live, "连接正常");
                return new ScreenWallCapture(bytes, (int)stopwatch.ElapsedMilliseconds, DateTimeOffset.UtcNow);
            }
            catch (VeyonWebApiException exception) when (exception.IsFirstFramePending)
            {
                SetState(ScreenWallLinkState.AwaitingFirstFrame, "已连接，等待学生机首帧");
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    SetState(ScreenWallLinkState.Stale, "10 秒内没有拿到首帧");
                    return null;
                }

                await Task.Delay(FirstFrameRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (VeyonWebApiException exception) when (exception.IsDeviceUnreachable)
            {
                _cooldownUntilUtc = DateTimeOffset.UtcNow + OfflineCooldown;
                await DropConnectionAsync().ConfigureAwait(false);
                SetState(ScreenWallLinkState.Offline, "学生机不可达（WebAPI 等待超时）");
                return null;
            }
            catch (VeyonWebApiException exception) when (exception.IsCapacityExceeded)
            {
                _cooldownUntilUtc = DateTimeOffset.UtcNow + CapacityCooldown;
                SetState(ScreenWallLinkState.Stale, "本机 WebAPI 连接数已满");
                return null;
            }
            catch (VeyonWebApiException exception) when (exception.IsImageFormatUnsupported)
            {
                _cooldownUntilUtc = DateTimeOffset.UtcNow + CredentialCooldown;
                SetState(ScreenWallLinkState.FormatUnsupported, "WebAPI 不接受该图像格式");
                return null;
            }
            catch (VeyonWebApiException exception) when (exception.IsCredentialRejected)
            {
                await DropConnectionAsync().ConfigureAwait(false);
                _cooldownUntilUtc = DateTimeOffset.UtcNow + CredentialCooldown;
                SetState(ScreenWallLinkState.AuthenticationFailed, "认证被拒绝，请检查教师端密钥");
                return null;
            }
            catch (VeyonWebApiException)
            {
                SetState(ScreenWallLinkState.Unavailable, "本机 WebAPI 无响应");
                await DropConnectionAsync().ConfigureAwait(false);
                _cooldownUntilUtc = DateTimeOffset.UtcNow + CapacityCooldown;
                return null;
            }
        }
    }

    private async Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (_connectionUid is { } existing)
        {
            if (DateTimeOffset.UtcNow - _authenticatedUtc < MaximumSessionAge) return true;
            await DropConnectionAsync().ConfigureAwait(false);
        }

        if (_credentials.Count == 0)
        {
            SetState(ScreenWallLinkState.AuthenticationFailed, "找不到可用的 Veyon 私钥");
            return false;
        }

        SetState(ScreenWallLinkState.Connecting, "正在认证");
        try
        {
            var method = await _client.ReadAuthenticationMethodAsync(_host, cancellationToken)
                .ConfigureAwait(false);
            VeyonWebApiException? lastRejection = null;
            for (var attempt = 0; attempt < _credentials.Count; attempt++)
            {
                var index = (_credentialIndex + attempt) % _credentials.Count;
                try
                {
                    _connectionUid = await _client.AuthenticateAsync(_host, method, _credentials[index],
                        cancellationToken).ConfigureAwait(false);
                    _authenticatedUtc = DateTimeOffset.UtcNow;
                    _credentialIndex = index;
                    SetState(ScreenWallLinkState.AwaitingFirstFrame, "认证通过，等待首帧");
                    return true;
                }
                catch (VeyonWebApiException exception) when (exception.IsCredentialRejected)
                {
                    lastRejection = exception;
                }
            }

            _cooldownUntilUtc = DateTimeOffset.UtcNow + CredentialCooldown;
            SetState(ScreenWallLinkState.AuthenticationFailed,
                lastRejection is null ? "认证被拒绝" : "认证被拒绝：密钥不匹配");
            return false;
        }
        catch (VeyonWebApiException exception) when (exception.IsDeviceUnreachable)
        {
            _cooldownUntilUtc = DateTimeOffset.UtcNow + OfflineCooldown;
            SetState(ScreenWallLinkState.Offline, "学生机不可达（认证超时）");
            return false;
        }
        catch (VeyonWebApiException)
        {
            _cooldownUntilUtc = DateTimeOffset.UtcNow + CapacityCooldown;
            SetState(ScreenWallLinkState.Unavailable, "本机 WebAPI 无响应");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private async Task DropConnectionAsync()
    {
        if (_connectionUid is not { } uid) return;
        _connectionUid = null;
        await _client.ReleaseAsync(_host, uid, CancellationToken.None).ConfigureAwait(false);
    }

    private void SetState(ScreenWallLinkState state, string detail)
    {
        if (State == state && string.Equals(Detail, detail, StringComparison.Ordinal)) return;
        State = state;
        Detail = detail;
        _onStateChanged?.Invoke(TargetId, state, detail);
    }

    /// <summary>Releases the server-side connection. Called when a device leaves the wall.</summary>
    internal async Task ReleaseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DropConnectionAsync().ConfigureAwait(false);
            State = ScreenWallLinkState.Idle;
            Detail = "已释放";
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // Cannot await in Dispose; the server also expires idle connections, so fire and forget.
        _ = DropConnectionAsync();
        _gate.Dispose();
    }
}
