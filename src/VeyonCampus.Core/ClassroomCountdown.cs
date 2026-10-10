using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>A UTC deadline scoped to one local classroom session.</summary>
public sealed record ClassroomCountdown(int SchemaVersion, Guid SessionId,
    DateTimeOffset StartedUtc, DateTimeOffset DeadlineUtc)
{
    public const int CurrentSchemaVersion = 1;
    public const int MinimumMinutes = 1;
    public const int MaximumMinutes = 180;

    public TimeSpan Remaining(DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("倒计时检查时间必须使用 UTC。");
        return DeadlineUtc > nowUtc ? DeadlineUtc - nowUtc : TimeSpan.Zero;
    }

    internal static ClassroomCountdown Start(Guid sessionId, int durationMinutes, DateTimeOffset startedUtc)
    {
        if (sessionId == Guid.Empty || durationMinutes is < MinimumMinutes or > MaximumMinutes ||
            startedUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("课堂 ID、倒计时时长或开始时间无效。");
        try
        {
            var countdown = new ClassroomCountdown(CurrentSchemaVersion, sessionId, startedUtc,
                startedUtc.AddMinutes(durationMinutes));
            Validate(countdown);
            return countdown;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("倒计时截止时间超出允许范围。", exception);
        }
    }

    internal static void Validate(ClassroomCountdown? countdown)
    {
        if (countdown is null || countdown.SchemaVersion != CurrentSchemaVersion ||
            countdown.SessionId == Guid.Empty || countdown.StartedUtc.Offset != TimeSpan.Zero ||
            countdown.DeadlineUtc.Offset != TimeSpan.Zero || countdown.DeadlineUtc <= countdown.StartedUtc)
            throw new InvalidDataException("本机课堂倒计时结构或版本无效。");

        var duration = countdown.DeadlineUtc - countdown.StartedUtc;
        if (duration < TimeSpan.FromMinutes(MinimumMinutes) ||
            duration > TimeSpan.FromMinutes(MaximumMinutes) || duration.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new InvalidDataException("本机课堂倒计时时长超出 1–180 分钟范围。");
    }
}

/// <summary>Single bounded per-user countdown state. It contains no target or student data.</summary>
public sealed class ClassroomCountdownStore
{
    public const int MaximumFileBytes = 4096;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
    private static readonly object ProcessGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _path;

    public ClassroomCountdownStore(string? path = null) =>
        _path = Path.GetFullPath(path ?? DefaultPath);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Teacher", "classroom-countdown.json");

    public ClassroomCountdown? Read(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂 ID 无效。");
        lock (ProcessGate)
        {
            if (!File.Exists(_path)) return null;
            using var fileLock = AcquireFileLock();
            var countdown = ReadDocument();
            return countdown?.SessionId == sessionId ? countdown with { } : null;
        }
    }

    public ClassroomCountdown Start(Guid sessionId, int durationMinutes, DateTimeOffset startedUtc)
    {
        var countdown = ClassroomCountdown.Start(sessionId, durationMinutes, startedUtc);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            // Reject a damaged file before replacing it; the teacher can inspect and recover it manually.
            _ = ReadDocument();
            WriteDocument(countdown);
            return countdown with { };
        }
    }

    public bool End(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂 ID 无效。");
        lock (ProcessGate)
        {
            if (!File.Exists(_path)) return false;
            using var fileLock = AcquireFileLock();
            var countdown = ReadDocument();
            if (countdown?.SessionId != sessionId) return false;
            PathLinkSecurity.RejectLinks(_path);
            File.Delete(_path);
            return true;
        }
    }

    private ClassroomCountdown? ReadDocument()
    {
        if (!File.Exists(_path)) return null;
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("本机课堂倒计时文件大小超出限制；原文件未修改。");
        try
        {
            var bytes = File.ReadAllBytes(_path);
            PolicyJson.RejectDuplicateFields(bytes);
            var countdown = JsonSerializer.Deserialize<ClassroomCountdown>(bytes, JsonOptions)
                            ?? throw new InvalidDataException("本机课堂倒计时文件为空；原文件未修改。");
            ClassroomCountdown.Validate(countdown);
            return countdown;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("本机课堂倒计时格式无效；原文件未修改。", exception);
        }
    }

    private void WriteDocument(ClassroomCountdown countdown)
    {
        ClassroomCountdown.Validate(countdown);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(countdown, JsonOptions);
        if (bytes.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("本机课堂倒计时文件大小无效；原文件未修改。");
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("本机课堂倒计时路径无效。");
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

    private FileStream AcquireFileLock()
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("本机课堂倒计时路径无效。");
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
        throw new IOException("另一 TeacherConsole 实例正在更新课堂倒计时，请稍后重试。", lastFailure);
    }
}
