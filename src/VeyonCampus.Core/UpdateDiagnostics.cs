using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public enum UpdateDiagnosticModule { TeacherConsole, StudentSetup }
public enum UpdateDiagnosticOperation { Check, DownloadAndInstall, OfflineExport, OfflineVerify, OfflineInstall, StudentRollout }
public enum UpdateDiagnosticOutcome { Started, Succeeded, Failed, Cancelled, Partial }
public enum UpdateDiagnosticSeverity { Information, Warning, Error }

public sealed record UpdateDiagnosticCounts(int Succeeded, int NeedsReview, int Failed)
{
    public bool IsValid => Succeeded is >= 0 and <= 150 && NeedsReview is >= 0 and <= 150 &&
                           Failed is >= 0 and <= 150 && Succeeded + NeedsReview + Failed <= 150;
}

public sealed record UpdateDiagnosticEntry(
    int SchemaVersion,
    DateTimeOffset OccurredAtUtc,
    Guid EventId,
    UpdateDiagnosticModule Module,
    UpdateDiagnosticOperation Operation,
    UpdateDiagnosticOutcome Outcome,
    string Code,
    string Version,
    string? TargetVersion,
    UpdateDiagnosticSeverity Severity,
    bool Retryable,
    UpdateDiagnosticCounts? Counts = null)
{
    public static UpdateDiagnosticEntry Create(UpdateDiagnosticModule module,
        UpdateDiagnosticOperation operation, string? version, string? targetVersion = null,
        Exception? exception = null, UpdateDiagnosticCounts? counts = null)
    {
        var partial = exception is null && counts is { IsValid: true } &&
                      (counts.NeedsReview > 0 || counts.Failed > 0);
        var failure = exception is null
            ? partial ? UpdateDiagnosticCatalog.Partial : UpdateDiagnosticCatalog.Success
            : UpdateDiagnosticCatalog.Classify(exception);
        var outcome = exception is OperationCanceledException
            ? UpdateDiagnosticOutcome.Cancelled
            : exception is not null
                ? UpdateDiagnosticOutcome.Failed
                : partial ? UpdateDiagnosticOutcome.Partial : UpdateDiagnosticOutcome.Succeeded;

        return new UpdateDiagnosticEntry(1, DateTimeOffset.UtcNow, Guid.NewGuid(), module, operation,
            outcome, failure.Code, SanitizeVersion(version) ?? "unknown", SanitizeVersion(targetVersion),
            failure.Severity, failure.Retryable, counts is { IsValid: true } ? counts : null);
    }

    public static UpdateDiagnosticEntry HandoffStarted(UpdateDiagnosticModule module,
        UpdateDiagnosticOperation operation, string? version, string? targetVersion) =>
        new(1, DateTimeOffset.UtcNow, Guid.NewGuid(), module, operation, UpdateDiagnosticOutcome.Started,
            UpdateDiagnosticCatalog.HandoffStarted.Code, SanitizeVersion(version) ?? "unknown",
            SanitizeVersion(targetVersion), UpdateDiagnosticSeverity.Information, false);

    private static string? SanitizeVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 32 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_')
            ? value
            : null;
}

public sealed record UpdateDiagnosticFailure(string Code, UpdateDiagnosticSeverity Severity,
    bool Retryable, string Guidance)
{
    public string ToUserMessage() => $"{Code}：{Guidance}{(Retryable ? " 可重试。" : "")}";
}

public sealed record UpdateDiagnosticExport(int SchemaVersion, DateTimeOffset ExportedAtUtc,
    IReadOnlyList<UpdateDiagnosticEntry> Events);

public static class UpdateDiagnosticCatalog
{
    public static UpdateDiagnosticFailure Success { get; } =
        new("UPDATE_SUCCEEDED", UpdateDiagnosticSeverity.Information, false, "更新操作已完成。");

    public static UpdateDiagnosticFailure Partial { get; } =
        new("UPDATE_PARTIAL", UpdateDiagnosticSeverity.Warning, true, "部分电脑需要核对或重试。");

    public static UpdateDiagnosticFailure HandoffStarted { get; } =
        new("UPDATE_HANDOFF_STARTED", UpdateDiagnosticSeverity.Information, false, "更新助手已启动；安装结果尚未确认。");

    private static readonly UpdateDiagnosticFailure Cancelled =
        new("UPDATE_CANCELLED", UpdateDiagnosticSeverity.Information, false, "操作已取消。");

    public static UpdateDiagnosticFailure Classify(Exception exception) => exception switch
    {
        TaskCanceledException or TimeoutException =>
            new("UPDATE_TIMEOUT", UpdateDiagnosticSeverity.Warning, true, "连接超时，请稍后重试。"),
        OperationCanceledException => Cancelled,
        HttpRequestException =>
            new("UPDATE_NETWORK", UpdateDiagnosticSeverity.Warning, true, "无法连接更新服务，请检查网络后重试。"),
        UnauthorizedAccessException =>
            new("UPDATE_PERMISSION_DENIED", UpdateDiagnosticSeverity.Error, false, "当前账户无权访问所需文件，请检查管理员和目录权限。"),
        System.Security.Cryptography.CryptographicException =>
            new("UPDATE_SIGNATURE_INVALID", UpdateDiagnosticSeverity.Error, false, "签名验证失败，请从可信发布页重新获取完整更新包。"),
        InvalidDataException or System.Text.Json.JsonException or FormatException =>
            new("UPDATE_ARTIFACT_INVALID", UpdateDiagnosticSeverity.Error, false, "发布清单或安装包无效，请重新获取可信更新包。"),
        PlatformNotSupportedException or NotSupportedException =>
            new("UPDATE_UNSUPPORTED", UpdateDiagnosticSeverity.Error, false, "此系统不支持该更新，请使用受支持的 Windows x64 版本。"),
        InvalidOperationException =>
            new("UPDATE_CONFIGURATION", UpdateDiagnosticSeverity.Error, false, "更新配置或固定签名公钥不可用，请安装正式构建。"),
        IOException =>
            new("UPDATE_LOCAL_IO", UpdateDiagnosticSeverity.Warning, true, "本机文件读写失败，请检查磁盘空间和文件占用后重试。"),
        ArgumentException =>
            new("UPDATE_INPUT_INVALID", UpdateDiagnosticSeverity.Error, false, "所选更新文件或输入格式无效，请重新选择完整更新包。"),
        _ => new("UPDATE_UNKNOWN", UpdateDiagnosticSeverity.Error, false, "更新失败，请记录此错误码并联系维护人员。")
    };

    internal static bool IsKnownCode(string? code) => code is
        "UPDATE_SUCCEEDED" or "UPDATE_PARTIAL" or "UPDATE_HANDOFF_STARTED" or "UPDATE_CANCELLED" or "UPDATE_TIMEOUT" or
        "UPDATE_NETWORK" or "UPDATE_PERMISSION_DENIED" or "UPDATE_SIGNATURE_INVALID" or
        "UPDATE_ARTIFACT_INVALID" or "UPDATE_UNSUPPORTED" or "UPDATE_CONFIGURATION" or
        "UPDATE_LOCAL_IO" or "UPDATE_INPUT_INVALID" or "UPDATE_UNKNOWN";
}

public sealed class UpdateDiagnosticsStore
{
    public const int MaximumRecordCount = 128;
    public const int MaximumRecordBytes = 4 * 1024;
    public const long MaximumStoreBytes = 512 * 1024;
    private const int MaximumExportBytes = 1024 * 1024;
    private readonly string _directory;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public UpdateDiagnosticsStore(string? directory = null)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _directory = Path.GetFullPath(directory ?? Path.Combine(
            string.IsNullOrWhiteSpace(localData) ? Path.GetTempPath() : localData,
            "VeyonCampus", "Diagnostics", "updates"));
    }

    public int Append(UpdateDiagnosticEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsValid(entry)) throw new InvalidDataException("Update diagnostic entry is invalid.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        if (bytes.Length > MaximumRecordBytes) throw new InvalidDataException("Update diagnostic entry exceeds its size limit.");

        lock (_gate)
        {
            EnsureDirectory();
            var timestamp = entry.OccurredAtUtc.ToUniversalTime()
                .ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            var fileName = $"{timestamp}-{entry.EventId:N}.json";
            var destination = Path.Combine(_directory, fileName);
            WriteAtomically(destination, bytes, overwrite: false);
            Prune();
            return EnumerateRecordFiles().Count;
        }
    }

    public IReadOnlyList<UpdateDiagnosticEntry> ReadRecent()
    {
        lock (_gate)
        {
            EnsureDirectory();
            var entries = new List<UpdateDiagnosticEntry>();
            foreach (var file in EnumerateRecordFiles()
                         .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                         .Take(MaximumRecordCount))
            {
                if (file.Length is <= 0 or > MaximumRecordBytes) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<UpdateDiagnosticEntry>(File.ReadAllBytes(file.FullName), JsonOptions);
                    if (entry is not null && IsValid(entry)) entries.Add(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    // A damaged local record is ignored; diagnostics must never interfere with updates.
                }
            }
            return entries.OrderByDescending(entry => entry.OccurredAtUtc).ToArray();
        }
    }

    public int ExportTo(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new ArgumentException("A destination directory is required.", nameof(destinationPath));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The selected export directory does not exist.");
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A diagnostic export cannot replace a reparse point.");

        var entries = ReadRecent();
        var export = new UpdateDiagnosticExport(1, DateTimeOffset.UtcNow, entries);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions);
        if (bytes.Length > MaximumExportBytes) throw new InvalidDataException("Update diagnostic export exceeds its size limit.");
        WriteAtomically(destination, bytes, overwrite: true);
        return entries.Count;
    }

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(_directory);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The update diagnostics directory cannot be a reparse point.");
    }

    private List<FileInfo> EnumerateRecordFiles()
    {
        var result = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
                     .Take(MaximumRecordCount * 4))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
                var file = new FileInfo(path);
                var name = file.Name;
                if (name.Length == 57 && name[19] == '-' && name.EndsWith(".json", StringComparison.Ordinal) &&
                    Guid.TryParseExact(name.AsSpan(20, 32), "N", out _))
                    result.Add(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable entries.
            }
        }
        return result;
    }

    private void Prune()
    {
        var files = EnumerateRecordFiles().OrderByDescending(file => file.LastWriteTimeUtc).ToList();
        var totalBytes = files.Sum(file => file.Length);
        while (files.Count > MaximumRecordCount || totalBytes > MaximumStoreBytes)
        {
            var oldest = files[^1];
            files.RemoveAt(files.Count - 1);
            totalBytes -= oldest.Length;
            try { oldest.Delete(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Retention cleanup is best-effort and does not affect the update operation.
            }
        }
    }

    private static bool IsValid(UpdateDiagnosticEntry entry) =>
        entry.SchemaVersion == 1 && entry.EventId != Guid.Empty &&
        Enum.IsDefined(entry.Module) && Enum.IsDefined(entry.Operation) &&
        Enum.IsDefined(entry.Outcome) && Enum.IsDefined(entry.Severity) &&
        UpdateDiagnosticCatalog.IsKnownCode(entry.Code) && IsSafeVersion(entry.Version) &&
        (entry.TargetVersion is null || IsSafeVersion(entry.TargetVersion)) &&
        (entry.Counts is null || entry.Counts.IsValid);

    private static bool IsSafeVersion(string value) => value.Length is > 0 and <= 32 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_');

    private static void WriteAtomically(string destination, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
