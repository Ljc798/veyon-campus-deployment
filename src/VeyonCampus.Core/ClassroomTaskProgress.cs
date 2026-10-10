using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record ClassroomTaskProgressItem(Guid TaskId, string Title, bool IsCompleted);

/// <summary>A bounded checklist scoped to one local classroom session.</summary>
public sealed record ClassroomTaskProgress(int SchemaVersion, Guid SessionId, DateTimeOffset UpdatedUtc,
    ImmutableArray<ClassroomTaskProgressItem> Tasks)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumTasks = 20;
    public const int MaximumTitleCharacters = 120;

    public int CompletedCount => Tasks.Count(task => task.IsCompleted);

    internal static string ValidateTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidDataException("任务标题不能为空。");
        var normalized = title.Trim();
        if (normalized.Length > MaximumTitleCharacters || normalized.Any(character =>
                char.IsControl(character) || char.GetUnicodeCategory(character) is
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
            throw new InvalidDataException("任务标题不能超过 120 字，且只能占一行。");
        return normalized;
    }

    internal static void Validate(ClassroomTaskProgress? progress)
    {
        if (progress is null || progress.SchemaVersion != CurrentSchemaVersion ||
            progress.SessionId == Guid.Empty || progress.UpdatedUtc.Offset != TimeSpan.Zero ||
            progress.Tasks.IsDefault || progress.Tasks.Length is < 1 or > MaximumTasks)
            throw new InvalidDataException("课堂任务进度结构、版本或范围无效。");

        var taskIds = new HashSet<Guid>();
        foreach (var task in progress.Tasks)
        {
            if (task is null || task.TaskId == Guid.Empty || !taskIds.Add(task.TaskId) ||
                task.Title != ValidateTitle(task.Title))
                throw new InvalidDataException("课堂任务 ID 或标题无效。");
        }
    }
}

/// <summary>Atomically stores only the active session's task checklist in the teacher user profile.</summary>
public sealed class ClassroomTaskProgressStore
{
    public const int MaximumFileBytes = 16 * 1024;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
    private static readonly object ProcessGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _path;

    public ClassroomTaskProgressStore(string? path = null) =>
        _path = Path.GetFullPath(path ?? DefaultPath);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Teacher", "classroom-task-progress.json");

    public ClassroomTaskProgress? Read(Guid sessionId)
    {
        ValidateSessionId(sessionId);
        lock (ProcessGate)
        {
            if (!File.Exists(_path)) return null;
            using var fileLock = AcquireFileLock();
            var progress = ReadDocument();
            return progress?.SessionId == sessionId ? progress with { Tasks = progress.Tasks.ToImmutableArray() } : null;
        }
    }

    public ClassroomTaskProgress Add(Guid sessionId, string title, DateTimeOffset updatedUtc)
    {
        ValidateSessionId(sessionId);
        ValidateUtc(updatedUtc);
        var normalizedTitle = ClassroomTaskProgress.ValidateTitle(title);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var existing = ReadDocument();
            var tasks = existing?.SessionId == sessionId
                ? existing.Tasks
                : ImmutableArray<ClassroomTaskProgressItem>.Empty;
            if (tasks.Length >= ClassroomTaskProgress.MaximumTasks)
                throw new InvalidDataException("本堂课最多添加 20 项任务。");
            var next = new ClassroomTaskProgress(ClassroomTaskProgress.CurrentSchemaVersion, sessionId,
                updatedUtc, tasks.Add(new ClassroomTaskProgressItem(Guid.NewGuid(), normalizedTitle, false)));
            WriteDocument(next);
            return next;
        }
    }

    public ClassroomTaskProgress? SetCompleted(Guid sessionId, Guid taskId, bool isCompleted,
        DateTimeOffset updatedUtc)
    {
        ValidateSessionId(sessionId);
        if (taskId == Guid.Empty) throw new InvalidDataException("任务 ID 无效。");
        ValidateUtc(updatedUtc);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var existing = ReadDocument();
            if (existing?.SessionId != sessionId) return null;
            var index = -1;
            for (var candidate = 0; candidate < existing.Tasks.Length; candidate++)
            {
                if (existing.Tasks[candidate].TaskId != taskId) continue;
                index = candidate;
                break;
            }
            if (index < 0) return existing;
            if (existing.Tasks[index].IsCompleted == isCompleted) return existing;
            var tasks = existing.Tasks.SetItem(index, existing.Tasks[index] with { IsCompleted = isCompleted });
            var next = existing with { UpdatedUtc = updatedUtc, Tasks = tasks };
            WriteDocument(next);
            return next;
        }
    }

    public ClassroomTaskProgress? Remove(Guid sessionId, Guid taskId, DateTimeOffset updatedUtc)
    {
        ValidateSessionId(sessionId);
        if (taskId == Guid.Empty) throw new InvalidDataException("任务 ID 无效。");
        ValidateUtc(updatedUtc);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var existing = ReadDocument();
            if (existing?.SessionId != sessionId) return null;
            var tasks = existing.Tasks.RemoveAll(task => task.TaskId == taskId);
            if (tasks.Length == existing.Tasks.Length) return existing;
            if (tasks.IsEmpty)
            {
                DeleteFile();
                return null;
            }

            var next = existing with { UpdatedUtc = updatedUtc, Tasks = tasks };
            WriteDocument(next);
            return next;
        }
    }

    public bool Clear(Guid sessionId)
    {
        ValidateSessionId(sessionId);
        lock (ProcessGate)
        {
            if (!File.Exists(_path)) return false;
            using var fileLock = AcquireFileLock();
            var existing = ReadDocument();
            if (existing?.SessionId != sessionId) return false;
            DeleteFile();
            return true;
        }
    }

    private ClassroomTaskProgress? ReadDocument()
    {
        if (!File.Exists(_path)) return null;
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("课堂任务进度文件大小超出限制；原文件未修改。");
        try
        {
            var bytes = File.ReadAllBytes(_path);
            PolicyJson.RejectDuplicateFields(bytes);
            var progress = JsonSerializer.Deserialize<ClassroomTaskProgress>(bytes, JsonOptions)
                           ?? throw new InvalidDataException("课堂任务进度文件为空；原文件未修改。");
            ClassroomTaskProgress.Validate(progress);
            return progress;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("课堂任务进度格式无效；原文件未修改。", exception);
        }
    }

    private void WriteDocument(ClassroomTaskProgress progress)
    {
        ClassroomTaskProgress.Validate(progress);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(progress, JsonOptions);
        if (bytes.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("课堂任务进度文件超过 16 KiB 限制；原文件未修改。");
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("课堂任务进度路径无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        if (File.Exists(_path)) PathLinkSecurity.RejectLinks(_path);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void DeleteFile()
    {
        PathLinkSecurity.RejectLinks(_path);
        File.Delete(_path);
    }

    private FileStream AcquireFileLock()
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("课堂任务进度路径无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        var lockPath = _path + ".lock";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        IOException? lastFailure = null;
        do
        {
            try
            {
                if (File.Exists(lockPath)) PathLinkSecurity.RejectLinks(lockPath);
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException exception)
            {
                lastFailure = exception;
                Thread.Sleep(25);
            }
        } while (timer.Elapsed < LockTimeout);
        throw new IOException("另一 TeacherConsole 实例正在更新课堂任务进度，请稍后重试。", lastFailure);
    }

    private static void ValidateSessionId(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂 ID 无效。");
    }

    private static void ValidateUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new InvalidDataException("课堂任务更新时间必须使用 UTC。");
    }
}
