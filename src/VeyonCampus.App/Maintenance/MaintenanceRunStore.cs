using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;

namespace VeyonCampus.App.Maintenance;

public interface IMaintenanceRunStore
{
    Task<MaintenanceJournal> ReadAsync(CancellationToken cancellationToken = default);
    Task<MaintenanceJournal> AddOrGetAsync(MaintenanceRun run, CancellationToken cancellationToken = default);
    Task<MaintenanceJournal> UpdateAsync(MaintenanceRun run, CancellationToken cancellationToken = default);
    Task<MaintenanceJournal> SetDesiredConfigurationAsync(DesiredMaintenanceConfiguration desired,
        CancellationToken cancellationToken = default);
    Task<MaintenanceJournal> SetReportedConfigurationAsync(ReportedMaintenanceConfiguration reported,
        CancellationToken cancellationToken = default);
}

/// <summary>Per-user atomic teacher-side journal; it stores public evidence digests, never signing keys.</summary>
public sealed class MaintenanceRunStore : IMaintenanceRunStore
{
    private const long MaximumJournalBytes = 32 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<MaintenanceRunState>(JsonNamingPolicy.CamelCase,
            allowIntegerValues: false),
            new JsonStringEnumConverter<MaintenanceOperationKind>(JsonNamingPolicy.CamelCase,
                allowIntegerValues: false),
            new JsonStringEnumConverter<MaintenanceReleaseChannel>(JsonNamingPolicy.CamelCase,
                allowIntegerValues: false) }
    };

    private readonly string _journalPath;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate;

    public static string DefaultDirectory => Path.Combine(VeyonCampusPaths.GetLocalApplicationDataDirectory(),
        "VeyonCampus", "Maintenance");

    public MaintenanceRunStore(string? directory = null)
    {
        var fullDirectory = Path.GetFullPath(directory ?? DefaultDirectory);
        _journalPath = Path.Combine(fullDirectory, "maintenance-runs.json");
        _lockPath = Path.Combine(fullDirectory, "maintenance-runs.lock");
        _gate = Gates.GetOrAdd(_journalPath, _ => new SemaphoreSlim(1, 1));
    }

    public Task<MaintenanceJournal> ReadAsync(CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(() => ReadJournalCoreAsync(cancellationToken), cancellationToken);

    public Task<MaintenanceJournal> AddOrGetAsync(MaintenanceRun run,
        CancellationToken cancellationToken = default) => ExecuteLockedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(run);
        var current = await ReadJournalCoreAsync(cancellationToken).ConfigureAwait(false);
        var updated = current.AddOrGet(run, DateTimeOffset.UtcNow);
        if (!ReferenceEquals(current, updated))
            await WriteJournalAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }, cancellationToken);

    public Task<MaintenanceJournal> UpdateAsync(MaintenanceRun run,
        CancellationToken cancellationToken = default) => ExecuteLockedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(run);
        var current = await ReadJournalCoreAsync(cancellationToken).ConfigureAwait(false);
        var updated = current.Update(run, DateTimeOffset.UtcNow);
        await WriteJournalAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }, cancellationToken);

    public Task<MaintenanceJournal> SetDesiredConfigurationAsync(DesiredMaintenanceConfiguration desired,
        CancellationToken cancellationToken = default) => ExecuteLockedAsync(async () =>
    {
        var current = await ReadJournalCoreAsync(cancellationToken).ConfigureAwait(false);
        var updated = current.SetDesiredConfiguration(desired, DateTimeOffset.UtcNow);
        await WriteJournalAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }, cancellationToken);

    public Task<MaintenanceJournal> SetReportedConfigurationAsync(ReportedMaintenanceConfiguration reported,
        CancellationToken cancellationToken = default) => ExecuteLockedAsync(async () =>
    {
        var current = await ReadJournalCoreAsync(cancellationToken).ConfigureAwait(false);
        var updated = current.SetReportedConfiguration(reported, DateTimeOffset.UtcNow);
        await WriteJournalAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }, cancellationToken);

    private async Task<T> ExecuteLockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            PathLinkSecurity.RejectLinksAllowingMissingComponents(_journalPath);
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            return await action().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<MaintenanceJournal> ReadJournalCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath)) return MaintenanceJournal.Empty(DateTimeOffset.UtcNow);
        PathLinkSecurity.RejectLinks(_journalPath);
        var info = new FileInfo(_journalPath);
        if (info.Length is <= 0 or > MaximumJournalBytes)
            throw new InvalidDataException("Teacher Maintenance journal 大小无效；已保留原文件。");
        var bytes = await ReadBoundedAsync(_journalPath, cancellationToken).ConfigureAwait(false);
        try
        {
            PolicyJson.RejectDuplicateFields(bytes);
            return JsonSerializer.Deserialize<MaintenanceJournal>(bytes, JsonOptions)
                   ?? throw new InvalidDataException("Teacher Maintenance journal 为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Teacher Maintenance journal 格式无效；已保留原文件供恢复核对。",
                exception);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumJournalBytes)
            throw new InvalidDataException("Teacher Maintenance journal 超过读取限制；已保留原文件。");
        using var buffer = new MemoryStream((int)Math.Min(stream.Length, 1024 * 1024));
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumJournalBytes)
                throw new InvalidDataException("Teacher Maintenance journal 超过读取限制；已保留原文件。");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task WriteJournalAsync(MaintenanceJournal journal, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        if (bytes.LongLength > MaximumJournalBytes)
            throw new IOException("Teacher Maintenance journal 超过写入限制；未覆盖现有记录。");
        var temporaryPath = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            PathLinkSecurity.RejectLinksAllowingMissingComponents(_journalPath);
            File.Move(temporaryPath, _journalPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
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
            { await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false); }
            if (DateTimeOffset.UtcNow >= deadline)
                throw new IOException("另一个教师进程正在写 Maintenance journal；已超时等待本地锁。");
        }
    }
}
