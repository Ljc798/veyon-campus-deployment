using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>The small set of classroom states exposed to teachers and students.</summary>
public enum ClassroomMode
{
    Normal,
    Practice
}

public enum ClassroomPolicyKind
{
    Website,
    Application
}

/// <summary>Ownership is tied to an opaque per-session target ID, never a host name or address.</summary>
public sealed record ClassroomPolicyOwnership(Guid TargetId, ClassroomPolicyKind Kind, long Revision,
    Guid ProfileId);

/// <summary>Only active state and policies that may still need safe restoration are retained.</summary>
public sealed record ClassroomModeSessionState(int SchemaVersion, Guid SessionId, ClassroomMode Mode,
    bool Active, DateTimeOffset UpdatedUtc, IReadOnlyList<ClassroomPolicyOwnership> OwnedPolicies);

/// <summary>Bounded local ownership ledger for temporary classroom policies.</summary>
public sealed class ClassroomModeStateStore
{
    private sealed record ModeDocument(int SchemaVersion, IReadOnlyList<ClassroomModeSessionState> Sessions);

    public const int CurrentSchemaVersion = 1;
    public const int MaximumTrackedSessions = 31;
    public const int MaximumOwnedPoliciesPerSession = ClassroomSession.MaximumTargets * 2;
    public const int MaximumFileBytes = 512 * 1024;
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

    public ClassroomModeStateStore(string? path = null) => _path = Path.GetFullPath(path ?? DefaultPath);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Teacher", "classroom-mode-state.json");

    public IReadOnlyList<ClassroomModeSessionState> ReadAll()
    {
        lock (ProcessGate) return Array.AsReadOnly(ReadDocument().Sessions.ToArray());
    }

    public ClassroomModeSessionState? Read(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂 ID 无效。");
        lock (ProcessGate) return ReadDocument().Sessions.SingleOrDefault(item => item.SessionId == sessionId);
    }

    public ClassroomModeSessionState Save(ClassroomModeSessionState state)
    {
        ValidateSession(state);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var document = ReadDocument();
            var sessions = document.Sessions.Where(item => item.SessionId != state.SessionId).Append(state)
                .Where(item => item.Active || item.OwnedPolicies.Count > 0)
                .OrderBy(item => item.UpdatedUtc).ToArray();
            if (sessions.Count(item => item.Active) > 1 || sessions.Length > MaximumTrackedSessions)
                throw new InvalidDataException("本机仍有过多未完成的课堂策略恢复记录；请先检查并恢复旧记录。");
            WriteDocument(new ModeDocument(CurrentSchemaVersion, sessions));
            return state;
        }
    }

    public void Remove(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂 ID 无效。");
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var document = ReadDocument();
            var sessions = document.Sessions.Where(item => item.SessionId != sessionId).ToArray();
            if (sessions.Length != document.Sessions.Count)
                WriteDocument(new ModeDocument(CurrentSchemaVersion, sessions));
        }
    }

    private ModeDocument ReadDocument()
    {
        if (!File.Exists(_path)) return new ModeDocument(CurrentSchemaVersion, []);
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("本机课堂策略恢复记录大小超出限制。");
        try
        {
            var bytes = File.ReadAllBytes(_path);
            PolicyJson.RejectDuplicateFields(bytes);
            var document = JsonSerializer.Deserialize<ModeDocument>(bytes, JsonOptions)
                           ?? throw new InvalidDataException("本机课堂策略恢复记录为空。");
            ValidateDocument(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("本机课堂策略恢复记录格式无效；原文件未修改。", exception);
        }
    }

    private void WriteDocument(ModeDocument document)
    {
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("本机课堂策略恢复记录超过 512 KiB 限制。");
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("本机课堂策略恢复记录路径无效。");
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
                        ?? throw new InvalidDataException("本机课堂策略恢复记录路径无效。");
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
        throw new IOException("另一 TeacherConsole 实例正在更新课堂策略恢复记录，请稍后重试。", lastFailure);
    }

    private static void ValidateDocument(ModeDocument? document)
    {
        if (document is null || document.SchemaVersion != CurrentSchemaVersion || document.Sessions is null ||
            document.Sessions.Count > MaximumTrackedSessions)
            throw new InvalidDataException("本机课堂策略恢复记录版本或数量无效。");
        var ids = new HashSet<Guid>();
        var activeCount = 0;
        foreach (var state in document.Sessions)
        {
            ValidateSession(state);
            if (!ids.Add(state.SessionId)) throw new InvalidDataException("课堂策略恢复记录包含重复 session ID。");
            if (state.Active) activeCount++;
        }
        if (activeCount > 1) throw new InvalidDataException("课堂策略恢复记录包含多个活动课堂。");
    }

    private static void ValidateSession(ClassroomModeSessionState? state)
    {
        if (state is null || state.SchemaVersion != CurrentSchemaVersion || state.SessionId == Guid.Empty ||
            !Enum.IsDefined(state.Mode) || state.UpdatedUtc.Offset != TimeSpan.Zero || state.OwnedPolicies is null ||
            state.OwnedPolicies.Count > MaximumOwnedPoliciesPerSession ||
            (!state.Active && state.OwnedPolicies.Count == 0))
            throw new InvalidDataException("本机课堂策略恢复记录字段无效。");
        var keys = new HashSet<(Guid, ClassroomPolicyKind)>();
        foreach (var item in state.OwnedPolicies)
        {
            if (item is null || item.TargetId == Guid.Empty || !Enum.IsDefined(item.Kind) || item.Revision < 1 ||
                item.ProfileId == Guid.Empty || !keys.Add((item.TargetId, item.Kind)))
                throw new InvalidDataException("课堂策略拥有版本无效或重复。");
        }
    }
}
