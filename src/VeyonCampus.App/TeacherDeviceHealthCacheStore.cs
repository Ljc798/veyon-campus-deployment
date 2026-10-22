using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.App;

internal sealed record TeacherDeviceHealthCacheEntry(
    string CampusId,
    string Target,
    string StudentName,
    string PreviousStatus,
    string AgentSummary,
    string ServiceSummary,
    string PolicySummary,
    string UpdateSummary,
    DateTimeOffset? LastCollectedUtc,
    DateTimeOffset SavedUtc,
    IReadOnlyList<TeacherDeviceHealthCachedIssue>? PreviousIssues = null);

internal sealed record TeacherDeviceHealthCachedIssue(
    string Code,
    string Title,
    TeacherDeviceHealthCacheIssueSeverity Severity,
    string Detail);

internal enum TeacherDeviceHealthCacheIssueSeverity
{
    Critical,
    Warning,
    Informational
}

/// <summary>Stores the latest local display summaries and bounded historical issue details.</summary>
internal sealed class TeacherDeviceHealthCacheStore
{
    private sealed record Document(int SchemaVersion, List<TeacherDeviceHealthCacheEntry> Entries);

    public const int CurrentSchemaVersion = 2;
    public const int MaximumEntries = 2048;
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    public const int CacheLifetimeDays = 30;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _path;

    public TeacherDeviceHealthCacheStore(string? path = null) =>
        _path = Path.GetFullPath(path ?? DefaultPath);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Teacher", "device-health-cache.json");

    public IReadOnlyList<TeacherDeviceHealthCacheEntry> Load()
    {
        lock (Gate)
        {
            if (!File.Exists(_path)) return Array.Empty<TeacherDeviceHealthCacheEntry>();
            if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("本机设备状态缓存不能是符号链接或重解析点。");

            var info = new FileInfo(_path);
            if (info.Length is < 1 or > MaximumFileBytes)
                throw new InvalidDataException("本机设备状态缓存大小超出允许范围。");

            Document document;
            try
            {
                document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(_path), JsonOptions)
                           ?? throw new InvalidDataException("本机设备状态缓存为空。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("本机设备状态缓存格式无效；未覆盖原文件。", exception);
            }

            Validate(document, allowPreviousSchema: true);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-CacheLifetimeDays);
            return document.Entries.Where(entry => entry.SavedUtc >= cutoff).ToArray();
        }
    }

    public void Save(IEnumerable<TeacherDeviceHealthCacheEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (Gate)
        {
            // Keep a malformed or unsupported cache intact so a later write cannot destroy useful data.
            if (File.Exists(_path)) _ = Load();

            var cutoff = DateTimeOffset.UtcNow.AddDays(-CacheLifetimeDays);
            var document = new Document(CurrentSchemaVersion, entries
                .Where(entry => entry.SavedUtc >= cutoff)
                .OrderByDescending(entry => entry.SavedUtc)
                .Take(MaximumEntries)
                .ToList());
            Validate(document);
            var payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (payload.Length > MaximumFileBytes)
                throw new InvalidDataException("本机设备状态缓存超过 8 MiB 限制。");

            var directory = Path.GetDirectoryName(_path)
                            ?? throw new InvalidDataException("本机设备状态缓存路径无效。");
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    private static void Validate(Document document, bool allowPreviousSchema = false)
    {
        if (document.SchemaVersion != CurrentSchemaVersion &&
            !(allowPreviousSchema && document.SchemaVersion == 1))
            throw new InvalidDataException("本机设备状态缓存版本不受支持；未覆盖原文件。");
        if (document.Entries is null || document.Entries.Count > MaximumEntries)
            throw new InvalidDataException("本机设备状态缓存记录数量无效。");

        foreach (var entry in document.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.CampusId) || entry.CampusId.Length > 128 ||
                string.IsNullOrWhiteSpace(entry.Target) || entry.Target.Length > 256 ||
                string.IsNullOrWhiteSpace(entry.StudentName) || entry.StudentName.Length > 128 ||
                entry.PreviousStatus is null || entry.PreviousStatus.Length > 128 ||
                entry.AgentSummary is null || entry.AgentSummary.Length > 1024 ||
                entry.ServiceSummary is null || entry.ServiceSummary.Length > 1024 ||
                entry.PolicySummary is null || entry.PolicySummary.Length > 2048 ||
                entry.UpdateSummary is null || entry.UpdateSummary.Length > 1024 ||
                entry.PreviousIssues is { Count: > 8 } ||
                entry.SavedUtc == default)
                throw new InvalidDataException("本机设备状态缓存记录无效；未覆盖原文件。");

            if (entry.PreviousIssues is null) continue;
            foreach (var issue in entry.PreviousIssues)
            {
                if (issue is null || string.IsNullOrWhiteSpace(issue.Code) || issue.Code.Length > 64 ||
                    string.IsNullOrWhiteSpace(issue.Title) || issue.Title.Length > 128 ||
                    issue.Detail is null || issue.Detail.Length > 512 || !Enum.IsDefined(issue.Severity))
                    throw new InvalidDataException("本机设备异常缓存记录无效；未覆盖原文件。");
            }
        }
    }
}
