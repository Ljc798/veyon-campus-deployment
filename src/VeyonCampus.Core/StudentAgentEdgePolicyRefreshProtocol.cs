using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>A signed local signal asking an interactive user-session executor to refresh Edge policies.</summary>
public sealed record StudentAgentEdgePolicyRefreshSignal(int SchemaVersion, Guid RequestId,
    DateTimeOffset? RequestedUtc, long Revision);

/// <summary>Coalesces and persists the latest Edge policy refresh request for user-session executors.</summary>
public sealed class StudentAgentEdgePolicyRefreshSignalState
{
    private readonly object _gate = new();
    private readonly string? _storagePath;
    private readonly bool _protectStorage;
    private Guid _requestId;
    private DateTimeOffset? _requestedUtc;
    private long _revision;
    private Exception? _loadFailure;

    private static readonly JsonSerializerOptions StorageJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public StudentAgentEdgePolicyRefreshSignalState(string? storagePath = null)
        : this(storagePath, protectStorage: true)
    {
    }

    internal StudentAgentEdgePolicyRefreshSignalState(string? storagePath, bool protectStorage)
    {
        _storagePath = string.IsNullOrWhiteSpace(storagePath) ? null : Path.GetFullPath(storagePath);
        _protectStorage = protectStorage;
        if (_storagePath is not null) Load();
    }

    /// <summary>Returns a sanitized persistence problem so the Agent can audit it without stopping policy service.</summary>
    public Exception? LoadFailure { get { lock (_gate) return _loadFailure; } }

    public void Request(long revision, DateTimeOffset? requestedUtc = null, bool force = false)
    {
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        lock (_gate)
        {
            if (_requestId != Guid.Empty && revision < _revision)
                throw new InvalidDataException("Edge 刷新队列拒绝较旧的策略 Revision。");
            if (!force && _requestId != Guid.Empty && _revision == revision) return;
            var next = new StudentAgentEdgePolicyRefreshSignal(1, Guid.NewGuid(),
                (requestedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(), revision);
            Persist(next);
            Apply(next);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            var empty = new StudentAgentEdgePolicyRefreshSignal(1, Guid.Empty, null, 0);
            Persist(empty);
            Apply(empty);
        }
    }

    public StudentAgentEdgePolicyRefreshSignal Read()
    {
        lock (_gate) return new StudentAgentEdgePolicyRefreshSignal(1, _requestId, _requestedUtc, _revision);
    }

    private void Load()
    {
        try
        {
            var path = _storagePath!;
            var directory = Path.GetDirectoryName(path)
                           ?? throw new InvalidDataException("Edge 刷新队列路径没有父目录。");
            PathLinkSecurity.RejectLinks(directory);
            if (!File.Exists(path)) return;
            PathLinkSecurity.RejectLinks(path);
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 16 * 1024)
                throw new InvalidDataException("Edge 刷新队列大小无效。");
            var signal = JsonSerializer.Deserialize<StudentAgentEdgePolicyRefreshSignal>(
                             File.ReadAllBytes(path), StorageJsonOptions)
                         ?? throw new InvalidDataException("Edge 刷新队列为空。");
            Validate(signal);
            Apply(signal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or System.Security.SecurityException or JsonException)
        {
            // Queue corruption must not stop the SYSTEM Agent from accepting or expiring policy.
            _loadFailure = exception;
        }
    }

    private void Persist(StudentAgentEdgePolicyRefreshSignal signal)
    {
        if (_storagePath is null) return;
        var path = _storagePath;
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidDataException("Edge 刷新队列路径没有父目录。");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Edge 刷新队列的 Agent 配置目录不存在。");
        PathLinkSecurity.RejectLinks(directory);
        if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(signal, StorageJsonOptions);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsWindows() && _protectStorage) AgentFileSecurity.SecurePrivateFile(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            if (OperatingSystem.IsWindows() && _protectStorage) AgentFileSecurity.SecurePrivateFile(path);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Apply(StudentAgentEdgePolicyRefreshSignal signal)
    {
        _requestId = signal.RequestId;
        _requestedUtc = signal.RequestedUtc;
        _revision = signal.Revision;
        _loadFailure = null;
    }

    private static void Validate(StudentAgentEdgePolicyRefreshSignal signal)
    {
        if (signal.SchemaVersion != 1 || signal.Revision < 0)
            throw new InvalidDataException("Edge 刷新队列 schema 或 Revision 无效。");
        if (signal.RequestId == Guid.Empty)
        {
            if (signal.RequestedUtc is not null || signal.Revision != 0)
                throw new InvalidDataException("空 Edge 刷新队列包含请求数据。");
            return;
        }
        if (signal.RequestedUtc is not { } requested || requested.Offset != TimeSpan.Zero || signal.Revision <= 0)
            throw new InvalidDataException("Edge 刷新队列时间或 Revision 无效。");
    }
}

public enum EdgePolicyRefreshStatus
{
    RegistryCommitted,
    BrowserNotRunning,
    PendingSession,
    UnsupportedBrowserVersion,
    Dispatching,
    DispatchedUnverified,
    VerifiedApplied,
    RetryableFailure,
    NeedsReview
}

/// <summary>
/// Microsoft Edge Stable builds based on Chromium 142 or newer contain the
/// --refresh-platform-policy switch. The executor separately requires the
/// standard machine-wide Stable installation path and a matching user session.
/// </summary>
public static class EdgePolicyRefreshCompatibility
{
    private static readonly Version MinimumSupportedVersion = new(142, 0, 0, 0);

    public static bool IsSupportedVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Split('.').Length != 4 ||
            !Version.TryParse(version, out var parsed))
            return false;
        return parsed >= MinimumSupportedVersion;
    }
}

public static class EdgePolicyRefreshDispatchRules
{
    public static bool IsSameInteractiveUserSession(int executorSessionId, string? executorUserSid,
        int browserSessionId, string? browserUserSid) =>
        executorSessionId > 0 && executorSessionId == browserSessionId &&
        !string.IsNullOrWhiteSpace(executorUserSid) &&
        string.Equals(executorUserSid, browserUserSid, StringComparison.OrdinalIgnoreCase);

    public static bool IsSameIntegrityLevel(int executorIntegrityLevelRid, int browserIntegrityLevelRid) =>
        executorIntegrityLevelRid > 0 && executorIntegrityLevelRid == browserIntegrityLevelRid;

    public static bool ShouldRetry(int attemptCount, int maximumAttempts) =>
        attemptCount >= 0 && maximumAttempts > 0 && attemptCount < maximumAttempts;
}
