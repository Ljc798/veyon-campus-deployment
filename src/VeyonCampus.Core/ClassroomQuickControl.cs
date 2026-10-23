using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>Short-lived classroom controls; legacy policy modes are not part of this contract.</summary>
public enum DeviceControlMode
{
    None,
    WebsiteRestriction,
    InputLocked,
    WebsiteAndInputLocked
}

public enum ClassroomQuickControlAction
{
    EnableWebsiteRestriction,
    LockInput,
    Release,
    ClearWebsiteRestrictions
}

public enum ClassroomQuickControlPhase
{
    NotStarted,
    NoneVerified,
    WaitingForAgent,
    WebsiteApplying,
    WebsiteReported,
    WebsiteApplied,
    InputLocking,
    InputLockedVerified,
    Switching,
    ReleasePending,
    Unknown,
    Conflict,
    Failed,
    Unsupported
}

/// <summary>
/// Records only the ownership evidence needed for safe compare-and-set and release. TargetId is the
/// stable room-directory identity; network addresses and student names are deliberately not persisted.
/// </summary>
public sealed record ClassroomQuickControlDeviceState(Guid TargetId, DeviceControlMode Mode,
    ClassroomQuickControlPhase Phase, long? WebsiteRevision, long? PendingWebsiteRevision, Guid? InputLeaseId,
    DateTimeOffset? ExpiresUtc, DateTimeOffset UpdatedUtc, string Detail)
{
    public DateTimeOffset? WebsiteExpiresUtc { get; init; }
    public DateTimeOffset? InputExpiresUtc { get; init; }
    public ClassroomQuickControlPhase? WebsitePhase { get; init; }
}

public sealed record ClassroomQuickControlSessionState(int SchemaVersion, Guid SessionId,
    string CampusId, Guid RoomId, bool Active, DateTimeOffset UpdatedUtc,
    IReadOnlyList<ClassroomQuickControlDeviceState> Devices);

public sealed record ClassroomQuickControlTargetResult(Guid TargetId, string DisplayName,
    DeviceControlMode PreviousMode, DeviceControlMode RequestedMode, DeviceControlMode CurrentMode,
    ClassroomQuickControlPhase Phase, bool Succeeded, bool NeedsReview,
    long? WebsiteRevision, Guid? InputLeaseId, string Detail);

public sealed record ClassroomQuickControlOperationResult(Guid SessionId,
    ClassroomQuickControlAction Action, DateTimeOffset RequestedUtc,
    IReadOnlyList<ClassroomQuickControlTargetResult> Results, string Message);

public sealed record ClassroomQuickControlTargetSnapshot(Guid TargetId, string DisplayName,
    DeviceControlMode Mode, ClassroomQuickControlPhase Phase, DateTimeOffset UpdatedUtc,
    long? WebsiteRevision, Guid? InputLeaseId, DateTimeOffset? ExpiresUtc, string Detail)
{
    public string WebsiteState { get; init; } = "unknown";
    public string InputState { get; init; } = "unknown";
}

public sealed record ClassroomQuickControlSnapshot(Guid SessionId, DateTimeOffset UpdatedUtc,
    IReadOnlyList<ClassroomQuickControlTargetSnapshot> Devices,
    bool PilotFeatureEnabled = false, string RoomName = "", int TargetCount = 0,
    string CampusName = "");

/// <summary>Bounded local ledger for quick-control ownership and unresolved releases.</summary>
public sealed class ClassroomQuickControlStateStore
{
    private sealed record StateFile(int SchemaVersion,
        IReadOnlyList<ClassroomQuickControlSessionState> Sessions);

    public const int CurrentSchemaVersion = 2;
    public const int MaximumTrackedSessions = 31;
    public const int MaximumDevicesPerSession = ClassroomSession.MaximumTargets;
    public const int MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly object ProcessGate = new();
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        WriteIndented = true
    };

    private readonly string _path;

    public ClassroomQuickControlStateStore(string? path = null) =>
        _path = Path.GetFullPath(path ?? DefaultPath);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Teacher", "classroom-quick-control.json");

    public IReadOnlyList<ClassroomQuickControlSessionState> ReadAll()
    {
        lock (ProcessGate) return Array.AsReadOnly(ReadFile().Sessions.ToArray());
    }

    public ClassroomQuickControlSessionState? Read(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂 ID 无效。");
        lock (ProcessGate) return ReadFile().Sessions.SingleOrDefault(item => item.SessionId == sessionId);
    }

    public ClassroomQuickControlSessionState Save(ClassroomQuickControlSessionState state)
    {
        ValidateSession(state);
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var current = ReadFile();
            var sessions = current.Sessions.Where(item => item.SessionId != state.SessionId)
                .Append(state)
                .Where(item => item.Active || item.Devices.Any(device => device.Mode != DeviceControlMode.None ||
                    device.Phase is not (ClassroomQuickControlPhase.NoneVerified or
                        ClassroomQuickControlPhase.NotStarted)))
                .OrderBy(item => item.UpdatedUtc)
                .ToArray();
            if (sessions.Length > MaximumTrackedSessions || sessions.Count(item => item.Active) > 1)
                throw new InvalidDataException("快捷管控仍有过多未完成课堂记录；请先核对待恢复项。");
            WriteFile(new StateFile(CurrentSchemaVersion, sessions));
            return state;
        }
    }

    public void Remove(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new InvalidDataException("课堂 ID 无效。");
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var current = ReadFile();
            var sessions = current.Sessions.Where(item => item.SessionId != sessionId).ToArray();
            if (sessions.Length != current.Sessions.Count)
                WriteFile(new StateFile(CurrentSchemaVersion, sessions));
        }
    }

    private StateFile ReadFile()
    {
        if (!File.Exists(_path)) return new StateFile(CurrentSchemaVersion, []);
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("快捷管控本机记录大小无效。");
        try
        {
            var bytes = File.ReadAllBytes(_path);
            PolicyJson.RejectDuplicateFields(bytes);
            var file = JsonSerializer.Deserialize<StateFile>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("快捷管控本机记录为空。");
            if (file.SchemaVersion == 1) file = UpgradeSchemaOne(file);
            ValidateFile(file);
            return file;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("快捷管控本机记录格式无效；原文件已保留。", exception);
        }
    }

    private void WriteFile(StateFile file)
    {
        ValidateFile(file);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
        if (bytes.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("快捷管控本机记录超过 4 MiB 限制。");
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("快捷管控本机记录路径无效。");
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

    private static StateFile UpgradeSchemaOne(StateFile file)
    {
        if (file.Sessions is null || file.Sessions.Count > MaximumTrackedSessions ||
            file.Sessions.Any(item => item is null || item.SchemaVersion != 1 || item.Devices is null ||
                item.Devices.Any(device => device is null)))
            throw new InvalidDataException("旧版快捷管控记录版本无效。");
        var sessions = file.Sessions.Select(session => session with
        {
            SchemaVersion = CurrentSchemaVersion,
            Devices = Array.AsReadOnly(session.Devices.Select(device => device with
            {
                Phase = device.Mode == DeviceControlMode.None && device.WebsiteRevision is null &&
                        device.PendingWebsiteRevision is null && device.InputLeaseId is null &&
                        device.Phase == ClassroomQuickControlPhase.Unknown &&
                        device.Detail.StartsWith("尚未读取本堂课的快捷管控状态", StringComparison.Ordinal)
                    ? ClassroomQuickControlPhase.NotStarted : device.Phase,
                Detail = device.Mode == DeviceControlMode.None && device.WebsiteRevision is null &&
                         device.PendingWebsiteRevision is null && device.InputLeaseId is null &&
                         device.Phase == ClassroomQuickControlPhase.Unknown &&
                         device.Detail.StartsWith("尚未读取本堂课的快捷管控状态", StringComparison.Ordinal)
                    ? "尚未执行网站限制或键鼠锁定操作；这不表示 Agent 离线或策略不可用。"
                    : device.Detail,
                WebsiteExpiresUtc = device.Mode == DeviceControlMode.WebsiteRestriction ||
                                    device.PendingWebsiteRevision is not null &&
                                    device.InputLeaseId is null
                    ? device.ExpiresUtc : null,
                InputExpiresUtc = device.InputLeaseId is not null ? device.ExpiresUtc : null
            }).ToArray())
        }).ToArray();
        return new StateFile(CurrentSchemaVersion, Array.AsReadOnly(sessions));
    }

    private FileStream AcquireFileLock()
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("快捷管控本机记录路径无效。");
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
        throw new IOException("另一 TeacherConsole 实例正在更新快捷管控记录，请稍后重试。", lastFailure);
    }

    private static void ValidateFile(StateFile? file)
    {
        if (file is null || file.SchemaVersion != CurrentSchemaVersion || file.Sessions is null ||
            file.Sessions.Count > MaximumTrackedSessions)
            throw new InvalidDataException("快捷管控记录版本或数量无效。");
        var ids = new HashSet<Guid>();
        foreach (var session in file.Sessions)
        {
            ValidateSession(session);
            if (!ids.Add(session.SessionId))
                throw new InvalidDataException("快捷管控记录包含重复课堂 ID。");
        }
        if (file.Sessions.Count(item => item.Active) > 1)
            throw new InvalidDataException("快捷管控记录包含多个活动课堂。");
    }

    private static void ValidateSession(ClassroomQuickControlSessionState? state)
    {
        if (state is null || state.SchemaVersion != CurrentSchemaVersion || state.SessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(state.CampusId) || state.CampusId.Length > 128 ||
            state.CampusId != state.CampusId.Trim() || state.CampusId.Any(char.IsControl) ||
            state.RoomId == Guid.Empty || state.UpdatedUtc.Offset != TimeSpan.Zero || state.Devices is null ||
            state.Devices.Count > MaximumDevicesPerSession || state.Devices.Any(device => device is null) ||
            state.Devices.Select(device => device.TargetId).Distinct().Count() != state.Devices.Count)
            throw new InvalidDataException("快捷管控课堂记录字段无效。");
        foreach (var device in state.Devices)
        {
            var websiteOwned = device.WebsiteRevision is not null || device.PendingWebsiteRevision is not null;
            var inputOwned = device.InputLeaseId is not null;
            if (device.TargetId == Guid.Empty || !Enum.IsDefined(device.Mode) || !Enum.IsDefined(device.Phase) ||
                device.WebsitePhase is { } websitePhase && !Enum.IsDefined(websitePhase) ||
                device.UpdatedUtc.Offset != TimeSpan.Zero || device.Detail is null || device.Detail.Length > 512 ||
                device.Detail.Any(char.IsControl) || device.WebsiteRevision is < 1 ||
                device.PendingWebsiteRevision is < 1 ||
                device.InputLeaseId == Guid.Empty ||
                device.Mode == DeviceControlMode.WebsiteRestriction && (!websiteOwned || inputOwned) ||
                device.Mode == DeviceControlMode.InputLocked && (!inputOwned || websiteOwned) ||
                device.Mode == DeviceControlMode.WebsiteAndInputLocked && (!websiteOwned || !inputOwned) ||
                device.Mode == DeviceControlMode.None && (device.WebsiteRevision is not null || inputOwned) ||
                device.ExpiresUtc is { Offset: var expiryOffset } && expiryOffset != TimeSpan.Zero ||
                device.WebsiteExpiresUtc is { Offset: var websiteExpiryOffset } && websiteExpiryOffset != TimeSpan.Zero ||
                device.InputExpiresUtc is { Offset: var inputExpiryOffset } && inputExpiryOffset != TimeSpan.Zero ||
                websiteOwned && device.WebsiteExpiresUtc is null && device.ExpiresUtc is null &&
                    device.Phase != ClassroomQuickControlPhase.WaitingForAgent ||
                inputOwned && device.InputExpiresUtc is null && device.ExpiresUtc is null)
                throw new InvalidDataException("快捷管控设备所有权字段无效。");
        }
    }
}
