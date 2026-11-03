using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>Sanitized, local-only facts about the SYSTEM Agent's authenticated teacher connection.</summary>
public sealed record StudentAgentTeacherChannelDiagnosticsSnapshot(
    int SchemaVersion,
    string State,
    string? FailureCode,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    DateTimeOffset? LastConfigurationChangeUtc,
    string? ConfigurationFingerprint,
    string Detail);

public static class StudentAgentTeacherChannelDiagnostics
{
    public const int CurrentSchemaVersion = 1;
    private const long MaximumBytes = 16 * 1024;
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static void Initialize(string configPath)
    {
        var entry = GetEntry(configPath);
        lock (entry.Gate)
        {
            if (entry.Initialized) return;
            entry.Initialized = true;
            try
            {
                PathLinkSecurity.RejectLinksAllowingMissingComponents(entry.Path);
                if (!File.Exists(entry.Path)) return;
                if (OperatingSystem.IsWindows() && !AgentFileSecurity.IsPrivate(entry.Path, directory: false))
                {
                    // Do not sign data that may have been modified through a user-writable ACL.
                    AgentFileSecurity.SecurePrivateFile(entry.Path);
                    return;
                }
                var info = new FileInfo(entry.Path);
                if (info.Length is <= 0 or > MaximumBytes) return;
                var snapshot = JsonSerializer.Deserialize<StudentAgentTeacherChannelDiagnosticsSnapshot>(
                    ReadSnapshotBytes(entry.Path), JsonOptions);
                if (snapshot is not null && Validate(snapshot)) entry.Snapshot = snapshot;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              JsonException or InvalidDataException or ArgumentException or
                                              System.Security.SecurityException)
            { }
        }
    }

    public static void RecordConfiguration(string configPath, string? fingerprint)
    {
        if (fingerprint is not null && (fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character))))
            fingerprint = null;
        Mutate(configPath, current =>
        {
            // First observation establishes a baseline; it is not evidence that the configuration changed.
            var changed = current.ConfigurationFingerprint is not null &&
                          (fingerprint is null || !string.Equals(current.ConfigurationFingerprint, fingerprint,
                              StringComparison.OrdinalIgnoreCase));
            return current with
            {
                State = changed ? "disconnected" : current.State,
                LastConfigurationChangeUtc = changed ? DateTimeOffset.UtcNow : current.LastConfigurationChangeUtc,
                ConfigurationFingerprint = fingerprint,
                FailureCode = changed ? "ConfigurationChanged" : current.FailureCode,
                Detail = changed ? "教师连接配置已更新，正在重新验证连接。" : current.Detail
            };
        });
    }

    public static void RecordAttempt(string configPath) => Mutate(configPath, current => current with
    {
        State = "connecting",
        FailureCode = null,
        LastAttemptUtc = DateTimeOffset.UtcNow,
        Detail = "正在建立经 TLS 和 Agent 身份认证的教师连接。"
    });

    public static void RecordConnected(string configPath) => Mutate(configPath, current => current with
    {
        State = "connected",
        FailureCode = null,
        LastSuccessUtc = DateTimeOffset.UtcNow,
        Detail = "TLS、Agent 身份和维护协议握手均已通过。"
    });

    public static void RecordFailure(string configPath, string failureCode)
    {
        if (!AllowedFailureCodes.Contains(failureCode, StringComparer.Ordinal))
            failureCode = "NetworkFailure";
        var failedUtc = DateTimeOffset.UtcNow;
        Mutate(configPath, current => current with
        {
            State = failureCode is "MissingEndpoint" or "MissingRootCertificate" or "InvalidConfiguration"
                ? "unconfigured" : "disconnected",
            FailureCode = failureCode,
            LastAttemptUtc = failedUtc,
            LastFailureUtc = failedUtc,
            Detail = DetailFor(failureCode)
        });
    }

    public static StudentAgentTeacherChannelDiagnosticsSnapshot? ReadSnapshot(string configPath)
    {
        var entry = GetEntry(configPath);
        lock (entry.Gate) return entry.Snapshot;
    }

    public static string ClassifyFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var chain = Flatten(exception).ToArray();
        if (chain.OfType<StudentAgentTeacherChannelFailureException>().FirstOrDefault() is { } classified &&
            AllowedFailureCodes.Contains(classified.FailureCode, StringComparer.Ordinal))
            return classified.FailureCode;
        if (chain.Any(item => item is AuthenticationException)) return "TlsValidationFailure";
        var sockets = chain.OfType<SocketException>().ToArray();
        if (sockets.Any(item => item.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or
                SocketError.TryAgain)) return "DnsFailure";
        if (sockets.Any(item => item.SocketErrorCode == SocketError.ConnectionRefused))
            return "TeacherNotListening";
        if (sockets.Any(item => item.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable or
                SocketError.TimedOut or SocketError.NetworkDown or SocketError.AddressNotAvailable))
            return "TcpConnectFailure";
        if (chain.Any(item => item is InvalidDataException or JsonException)) return "ProtocolMismatch";
        if (chain.Any(item => item is WebSocketException)) return "NetworkFailure";
        if (chain.Any(item => item is UriFormatException or ArgumentException)) return "InvalidConfiguration";
        return "NetworkFailure";
    }

    public static bool Validate(StudentAgentTeacherChannelDiagnosticsSnapshot snapshot)
    {
        if (snapshot is null || snapshot.State is null || snapshot.Detail is null ||
            snapshot.SchemaVersion != CurrentSchemaVersion ||
            snapshot.State is not ("unknown" or "unconfigured" or "connecting" or "connected" or "disconnected") ||
            snapshot.FailureCode is { } code && !AllowedFailureCodes.Contains(code, StringComparer.Ordinal) ||
            snapshot.ConfigurationFingerprint is { } fingerprint &&
                (fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character))) ||
            snapshot.Detail.Length > 256 || snapshot.Detail.Any(char.IsControl) ||
            !IsUtc(snapshot.LastAttemptUtc) || !IsUtc(snapshot.LastSuccessUtc) ||
            !IsUtc(snapshot.LastFailureUtc) || !IsUtc(snapshot.LastConfigurationChangeUtc))
            return false;
        return snapshot.State == "connected" ? snapshot.LastSuccessUtc is not null && snapshot.FailureCode is null
            : snapshot.State is "disconnected" or "unconfigured" ? snapshot.FailureCode is not null
            : true;
    }

    private static readonly string[] AllowedFailureCodes =
    [
        "MissingEndpoint", "MissingRootCertificate", "InvalidConfiguration", "DnsFailure",
        "TcpConnectFailure", "TlsValidationFailure", "AuthenticationRejected", "ProtocolMismatch",
        "TeacherNotListening", "NetworkFailure", "ConfigurationChanged"
    ];

    private static string DetailFor(string code) => code switch
    {
        "MissingEndpoint" => "教师端地址未配置。",
        "MissingRootCertificate" => "教师端 TLS 根证书未配置。",
        "InvalidConfiguration" => "教师连接配置格式无效或违反设备信任约束。",
        "DnsFailure" => "无法解析教师端地址。",
        "TcpConnectFailure" => "无法建立到教师端的网络连接。",
        "TlsValidationFailure" => "教师端 TLS 证书链、有效期或主机名验证失败。",
        "AuthenticationRejected" => "教师端拒绝了 Agent 身份认证。",
        "ProtocolMismatch" => "教师端与学生 Agent 的维护通道协议不兼容。",
        "TeacherNotListening" => "教师端连接服务未监听或拒绝连接。",
        "ConfigurationChanged" => "教师连接配置已更新，正在重新验证连接。",
        _ => "教师端连接失败；保留详细日志供本机排查。"
    };

    private static void Mutate(string configPath,
        Func<StudentAgentTeacherChannelDiagnosticsSnapshot, StudentAgentTeacherChannelDiagnosticsSnapshot> update)
    {
        var entry = GetEntry(configPath);
        lock (entry.Gate)
        {
            entry.Initialized = true;
            entry.Snapshot = update(entry.Snapshot);
            Persist(entry);
        }
    }

    private static Entry GetEntry(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var normalized = Path.GetFullPath(configPath);
        return Entries.GetOrAdd(normalized, path => new Entry(path + ".teacher-channel-status.json"));
    }

    private static byte[] ReadSnapshotBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("教师连接诊断文件大小无效。");
        using var buffer = new MemoryStream((int)stream.Length);
        stream.CopyTo(buffer);
        if (buffer.Length > MaximumBytes)
            throw new InvalidDataException("教师连接诊断文件超过大小限制。");
        var bytes = buffer.ToArray();
        PolicyJson.RejectDuplicateFields(bytes);
        return bytes;
    }

    private static void Persist(Entry entry)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entry.Snapshot, JsonOptions);
            if (bytes.LongLength > MaximumBytes) return;
            PathLinkSecurity.RejectLinksAllowingMissingComponents(entry.Path);
            var temporary = entry.Path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (OperatingSystem.IsWindows()) AgentFileSecurity.SecurePrivateFile(temporary);
                PathLinkSecurity.RejectLinksAllowingMissingComponents(entry.Path);
                File.Move(temporary, entry.Path, overwrite: true);
                if (OperatingSystem.IsWindows()) AgentFileSecurity.SecurePrivateFile(entry.Path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or InvalidDataException or
                                          System.Security.SecurityException)
        { }
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Reverse()) pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }

    private static bool IsUtc(DateTimeOffset? value) => value is null || value.Value.Offset == TimeSpan.Zero;

    private sealed class Entry(string path)
    {
        public object Gate { get; } = new();
        public string Path { get; } = path;
        public bool Initialized { get; set; }
        public StudentAgentTeacherChannelDiagnosticsSnapshot Snapshot { get; set; } =
            new(CurrentSchemaVersion, "unknown", null, null, null, null, null, null,
                "尚未尝试连接教师端。");
    }
}

internal sealed class StudentAgentTeacherChannelFailureException(string failureCode, Exception innerException)
    : IOException("教师连接阶段失败。", innerException)
{
    public string FailureCode { get; } = failureCode;
}
