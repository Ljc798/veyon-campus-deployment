using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public enum ClassroomSessionStatus
{
    Active,
    Ended
}

/// <summary>A name and numbering snapshot of a room in the local teacher directory.</summary>
public sealed record ClassroomRoomSnapshot(Guid CampusProfileId, string CampusName, Guid RoomId,
    string RoomName, string ComputerPrefix, int StartNumber, int ComputerCount);

/// <summary>A randomly addressed computer that exists only for one classroom session.</summary>
public sealed record ClassroomSessionTarget(Guid TargetId, string DeviceLabel);

/// <summary>
/// A local classroom session. Target IDs are routing labels, never authentication credentials.
/// The model intentionally contains no student names, network addresses or message content.
/// </summary>
public sealed record ClassroomSession(int SchemaVersion, Guid SessionId, ClassroomRoomSnapshot Room,
    DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc, ClassroomSessionStatus Status,
    ImmutableArray<ClassroomSessionTarget> Targets)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumTargets = 150;

    public static ClassroomSession Start(TeacherCampusProfile campus, Guid roomId,
        DateTimeOffset startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(campus);
        if (campus.ProfileId == Guid.Empty || campus.Rooms is null || roomId == Guid.Empty)
            throw new InvalidDataException("本机校区或机房档案无效，无法开始课堂。");

        var matches = campus.Rooms.Where(room => room is not null && room.RoomId == roomId).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("所选机房不属于当前本机校区档案。");

        var room = matches[0];
        ValidateDisplayName(campus.DisplayName, "校区");
        ValidateDisplayName(room.DisplayName, "机房");

        IReadOnlyList<string> labels;
        try
        {
            labels = MachineNaming.CreateRange(room.Prefix,
                room.StartNumber.ToString(CultureInfo.InvariantCulture),
                room.ComputerCount.ToString(CultureInfo.InvariantCulture));
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("机房电脑编号无效，无法开始课堂。", exception);
        }

        var snapshot = new ClassroomRoomSnapshot(campus.ProfileId, campus.DisplayName, room.RoomId,
            room.DisplayName, room.Prefix, room.StartNumber, room.ComputerCount);
        var targets = labels.Select(label => new ClassroomSessionTarget(Guid.NewGuid(), label)).ToImmutableArray();
        var session = new ClassroomSession(CurrentSchemaVersion, Guid.NewGuid(), snapshot,
            startedAtUtc.ToUniversalTime(), null, ClassroomSessionStatus.Active, targets);
        Validate(session);
        return session;
    }

    /// <summary>Ends once. Repeated completion calls preserve the original end time.</summary>
    public ClassroomSession End(DateTimeOffset endedAtUtc)
    {
        Validate(this);
        if (Status == ClassroomSessionStatus.Ended && EndedAtUtc is not null)
            return this;
        var endedUtc = endedAtUtc.ToUniversalTime();
        if (endedUtc < StartedAtUtc)
            throw new InvalidDataException("课堂结束时间不能早于开始时间。");
        var ended = this with { EndedAtUtc = endedUtc, Status = ClassroomSessionStatus.Ended };
        Validate(ended);
        return ended;
    }

    internal static void Validate(ClassroomSession? session)
    {
        if (session is null || session.SchemaVersion != CurrentSchemaVersion || session.SessionId == Guid.Empty ||
            session.Room is null || session.Targets.IsDefaultOrEmpty || session.Targets.Length > MaximumTargets ||
            session.StartedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("课堂记录结构或版本无效。");

        var room = session.Room;
        if (room.CampusProfileId == Guid.Empty || room.RoomId == Guid.Empty)
            throw new InvalidDataException("课堂记录的校区或机房 ID 无效。");
        ValidateDisplayName(room.CampusName, "校区");
        ValidateDisplayName(room.RoomName, "机房");

        IReadOnlyList<string> expectedLabels;
        try
        {
            expectedLabels = MachineNaming.CreateRange(room.ComputerPrefix,
                room.StartNumber.ToString(CultureInfo.InvariantCulture),
                room.ComputerCount.ToString(CultureInfo.InvariantCulture));
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("课堂记录的机房电脑范围无效。", exception);
        }
        if (session.Targets.Length != expectedLabels.Count)
            throw new InvalidDataException("课堂目标数量与机房快照不匹配。");

        var targetIds = new HashSet<Guid>();
        for (var index = 0; index < session.Targets.Length; index++)
        {
            var target = session.Targets[index];
            if (target is null || target.TargetId == Guid.Empty || !targetIds.Add(target.TargetId) ||
                !string.Equals(target.DeviceLabel, expectedLabels[index], StringComparison.Ordinal))
                throw new InvalidDataException("课堂目标 ID 或电脑编号无效。");
        }

        switch (session.Status)
        {
            case ClassroomSessionStatus.Active when session.EndedAtUtc is null:
                break;
            case ClassroomSessionStatus.Ended when session.EndedAtUtc is { Offset: var offset } ended &&
                                                   offset == TimeSpan.Zero && ended >= session.StartedAtUtc:
                break;
            default:
                throw new InvalidDataException("课堂状态与结束时间不匹配。");
        }
    }

    private static void ValidateDisplayName(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value != value.Trim() ||
            value.Any(char.IsControl))
            throw new InvalidDataException($"{label}名称无效，无法用于课堂记录。");
    }
}

/// <summary>
/// Bounded per-user session history. The directory and atomic replacement follow the current
/// user's application-data ACL; no session data is sent to a network service.
/// </summary>
public sealed class ClassroomSessionStore
{
    public const int MaximumEndedSessions = 30;
    public const int MaximumSessions = MaximumEndedSessions + 1;
    public const int MaximumFileBytes = 1024 * 1024;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
    private static readonly object ProcessGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        WriteIndented = true
    };

    private readonly string _path;

    public ClassroomSessionStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? DefaultPath);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Teacher", "classroom-sessions.json");

    public IReadOnlyList<ClassroomSession> ReadRecent()
    {
        lock (ProcessGate)
            return ReadDocument().Sessions;
    }

    public ClassroomSession? ReadActive()
    {
        lock (ProcessGate)
            return ReadDocument().Sessions.SingleOrDefault(session => session.Status == ClassroomSessionStatus.Active);
    }

    public ClassroomSession StartSession(TeacherCampusProfile campus, Guid roomId,
        DateTimeOffset startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(campus);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var document = ReadDocument();
            if (document.Sessions.Any(session => session.Status == ClassroomSessionStatus.Active))
                throw new InvalidDataException("已有一堂进行中的课堂；请先结束后再开始新课堂。");

            var session = ClassroomSession.Start(campus, roomId, startedAtUtc);
            var sessions = KeepRecent(document.Sessions.Append(session));
            WriteDocument(new SessionDocument(ClassroomSession.CurrentSchemaVersion, sessions));
            return session;
        }
    }

    /// <summary>Ends a known session. Repeating the request returns the original ended record.</summary>
    public ClassroomSession? EndSession(Guid sessionId, DateTimeOffset endedAtUtc)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂 ID 无效。");
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var document = ReadDocument();
            var existing = document.Sessions.SingleOrDefault(session => session.SessionId == sessionId);
            if (existing is null) return null;
            if (existing.Status == ClassroomSessionStatus.Ended) return existing;

            var ended = existing.End(endedAtUtc);
            var sessions = KeepRecent(document.Sessions.Select(session =>
                session.SessionId == existing.SessionId ? ended : session));
            WriteDocument(new SessionDocument(ClassroomSession.CurrentSchemaVersion, sessions));
            return ended;
        }
    }

    private SessionDocument ReadDocument()
    {
        if (!File.Exists(_path)) return new SessionDocument(ClassroomSession.CurrentSchemaVersion, []);
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("本机课堂记录文件大小超出限制。");

        try
        {
            var bytes = File.ReadAllBytes(_path);
            PolicyJson.RejectDuplicateFields(bytes);
            var document = JsonSerializer.Deserialize<SessionDocument>(bytes, JsonOptions)
                           ?? throw new InvalidDataException("本机课堂记录文件为空。");
            ValidateDocument(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("本机课堂记录格式无效；原文件未修改。", exception);
        }
    }

    private void WriteDocument(SessionDocument document)
    {
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("本机课堂记录超过 1 MiB 限制。");

        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("本机课堂记录路径无效。");
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
                        ?? throw new InvalidDataException("本机课堂记录路径无效。");
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

        throw new IOException("另一 TeacherConsole 实例正在更新课堂记录，请稍后重试。", lastFailure);
    }

    private static ImmutableArray<ClassroomSession> KeepRecent(IEnumerable<ClassroomSession> sessions)
    {
        var materialized = sessions.ToArray();
        var active = materialized.SingleOrDefault(session => session.Status == ClassroomSessionStatus.Active);
        var ended = materialized.Where(session => session.Status == ClassroomSessionStatus.Ended)
            .OrderByDescending(session => session.StartedAtUtc)
            .ThenByDescending(session => session.SessionId)
            .Take(MaximumEndedSessions)
            .OrderBy(session => session.StartedAtUtc)
            .ThenBy(session => session.SessionId);
        return ended.Concat(active is null ? [] : [active]).ToImmutableArray();
    }

    private static void ValidateDocument(SessionDocument? document)
    {
        if (document is null || document.SchemaVersion != ClassroomSession.CurrentSchemaVersion ||
            document.Sessions.IsDefault || document.Sessions.Length > MaximumSessions)
            throw new InvalidDataException("本机课堂记录版本或数量无效。");

        var sessionIds = new HashSet<Guid>();
        var activeCount = 0;
        var endedCount = 0;
        foreach (var session in document.Sessions)
        {
            ClassroomSession.Validate(session);
            if (!sessionIds.Add(session.SessionId))
                throw new InvalidDataException("本机课堂记录包含重复 session ID。");
            if (session.Status == ClassroomSessionStatus.Active) activeCount++;
            else endedCount++;
        }
        if (activeCount > 1 || endedCount > MaximumEndedSessions)
            throw new InvalidDataException("本机课堂记录超过活动会话或历史保留上限。");
    }

    private sealed record SessionDocument(int SchemaVersion, ImmutableArray<ClassroomSession> Sessions);
}
