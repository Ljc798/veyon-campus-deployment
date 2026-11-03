using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core.Maintenance;

/// <summary>
/// Durable replay window for signed Maintenance commands. A storage or lock error is fatal to
/// acceptance: callers must not dispatch the command when this store cannot commit its nonce.
/// The containing directory must be restricted to SYSTEM and Administrators by the installer.
/// </summary>
public sealed class MaintenanceReplayWindow
{
    private const int MaximumEntries = 100_000;
    private const long MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _path;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _processGate;

    public MaintenanceReplayWindow(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _lockPath = _path + ".lock";
        _processGate = ProcessGates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<bool> TryAcceptAsync(Guid nonce, DateTimeOffset expiresUtc,
        DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default)
    {
        if (nonce == Guid.Empty) throw new ArgumentException("Maintenance 命令随机数不能为空。", nameof(nonce));
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var expires = MaintenanceValidation.Utc(expiresUtc);
        if (expires <= now || expires - now > MaintenanceProtocolV1.MaximumCommandLifetime)
            throw new InvalidDataException("Maintenance 命令已过期或剩余时间超出协议上限。" );

        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            PathLinkSecurity.RejectLinksAllowingMissingComponents(_path);
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            var entries = await ReadEntriesAsync(now, cancellationToken).ConfigureAwait(false);
            entries.RemoveAll(entry => entry.ExpiresUtc <= now || now - entry.AcceptedUtc > Retention);
            if (entries.Any(entry => entry.Nonce == nonce))
            {
                await WriteEntriesAsync(entries, cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (entries.Count >= MaximumEntries)
                throw new IOException("Maintenance 重放窗口达到容量上限；拒绝接受新命令。" );
            entries.Add(new Entry(nonce, now, expires));
            await WriteEntriesAsync(entries, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _processGate.Release(); }
    }

    private async Task<List<Entry>> ReadEntriesAsync(DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        PathLinkSecurity.RejectLinks(_path);
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
            32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("Maintenance 重放窗口文件大小无效；拒绝处理命令。" );
        using var buffer = new MemoryStream((int)stream.Length);
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumFileBytes)
                throw new InvalidDataException("Maintenance 重放窗口文件超过读取限制。" );
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        PolicyJson.RejectDuplicateFields(bytes);
        var document = JsonSerializer.Deserialize<ReplayDocument>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("Maintenance 重放窗口为空。" );
        if (document.SchemaVersion != 1 || document.Entries is null || document.Entries.Count > MaximumEntries)
            throw new InvalidDataException("Maintenance 重放窗口版本或条目数无效。" );
        var seen = new HashSet<Guid>();
        foreach (var entry in document.Entries)
        {
            if (entry is null || entry.Nonce == Guid.Empty || !seen.Add(entry.Nonce) ||
                entry.AcceptedUtc.Offset != TimeSpan.Zero || entry.ExpiresUtc.Offset != TimeSpan.Zero ||
                entry.ExpiresUtc <= entry.AcceptedUtc ||
                entry.ExpiresUtc - entry.AcceptedUtc > MaintenanceProtocolV1.MaximumCommandLifetime ||
                entry.AcceptedUtc > now.AddMinutes(1))
                throw new InvalidDataException("Maintenance 重放窗口包含无效或重复条目。" );
        }
        return document.Entries.ToList();
    }

    private async Task WriteEntriesAsync(IReadOnlyList<Entry> entries, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ReplayDocument(1,
            Array.AsReadOnly(entries.OrderBy(entry => entry.AcceptedUtc).ToArray())), JsonOptions);
        if (bytes.LongLength > MaximumFileBytes)
            throw new IOException("Maintenance 重放窗口超过存储上限；拒绝接受命令。" );
        PathLinkSecurity.RejectLinksAllowingMissingComponents(_path);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 32 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            PathLinkSecurity.RejectLinksAllowingMissingComponents(_path);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PathLinkSecurity.RejectLinksAllowingMissingComponents(_lockPath);
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            { await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false); }
            if (DateTimeOffset.UtcNow >= deadline)
                throw new IOException("另一个 Maintenance 服务实例正在写重放窗口；等待锁超时。" );
        }
    }

    private sealed record ReplayDocument(int SchemaVersion, IReadOnlyList<Entry> Entries);
    private sealed record Entry(Guid Nonce, DateTimeOffset AcceptedUtc, DateTimeOffset ExpiresUtc);
}
