using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>A room-local row-first ordering of computer labels. It never contains student identities.</summary>
public sealed record ClassroomSeatLayout(Guid CampusProfileId, Guid RoomId, int Columns,
    string[] TargetOrder);

/// <summary>One derived physical position in a room's current classroom layout.</summary>
public sealed record ClassroomSeatLocation(string Target, int Row, int Column);

/// <summary>Per-user local storage for room seat ordering and row width.</summary>
public sealed class ClassroomSeatLayoutStore
{
    private sealed record SeatLayoutDocument(int SchemaVersion, List<ClassroomSeatLayout> Layouts);

    public const int CurrentSchemaVersion = 1;
    public const int MaximumLayouts = 1000;
    public const int MaximumTargets = ClassroomSession.MaximumTargets;
    public const int MaximumFileBytes = 16 * 1024 * 1024;

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

    public ClassroomSeatLayoutStore(string? path = null) => _path = Path.GetFullPath(path ?? DefaultPath);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Teacher", "classroom-seat-layouts.json");

    public ClassroomSeatLayout GetLayout(Guid campusProfileId, Guid roomId, IReadOnlyList<string> targets)
    {
        ValidateIdentity(campusProfileId, roomId);
        var normalizedTargets = NormalizeTargets(targets);
        lock (ProcessGate)
        {
            if (!File.Exists(_path)) return CreateDefault(campusProfileId, roomId, normalizedTargets);
            var document = ReadDocument();
            var current = document.Layouts.SingleOrDefault(layout =>
                layout.CampusProfileId == campusProfileId && layout.RoomId == roomId);
            if (current is null) return CreateDefault(campusProfileId, roomId, normalizedTargets);

            var normalized = Reconcile(current, campusProfileId, roomId, normalizedTargets);
            if (Equivalent(current, normalized)) return Clone(normalized);

            using (AcquireFileLock())
            {
                document = ReadDocument();
                current = document.Layouts.SingleOrDefault(layout =>
                    layout.CampusProfileId == campusProfileId && layout.RoomId == roomId);
                normalized = Reconcile(current, campusProfileId, roomId, normalizedTargets);
                if (!Equivalent(current, normalized))
                    WriteDocument(Replace(document, normalized));
            }
            return Clone(normalized);
        }
    }

    public ClassroomSeatLayout Swap(Guid campusProfileId, Guid roomId, IReadOnlyList<string> targets,
        int firstIndex, int secondIndex)
    {
        ValidateIdentity(campusProfileId, roomId);
        var normalizedTargets = NormalizeTargets(targets);
        if (firstIndex < 0 || firstIndex >= normalizedTargets.Length ||
            secondIndex < 0 || secondIndex >= normalizedTargets.Length)
            throw new InvalidDataException("座位位置超出当前机房电脑范围。");

        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var document = ReadDocument();
            var existing = document.Layouts.SingleOrDefault(layout =>
                layout.CampusProfileId == campusProfileId && layout.RoomId == roomId);
            var layout = Reconcile(existing, campusProfileId, roomId, normalizedTargets);
            if (firstIndex != secondIndex)
                (layout.TargetOrder[firstIndex], layout.TargetOrder[secondIndex]) =
                    (layout.TargetOrder[secondIndex], layout.TargetOrder[firstIndex]);
            WriteDocument(Replace(document, layout));
            return Clone(layout);
        }
    }

    public ClassroomSeatLayout SetColumns(Guid campusProfileId, Guid roomId, IReadOnlyList<string> targets,
        int columns)
    {
        ValidateIdentity(campusProfileId, roomId);
        var normalizedTargets = NormalizeTargets(targets);
        if (columns < 1 || columns > normalizedTargets.Length)
            throw new InvalidDataException("每排座位数必须在 1 到电脑总数之间。");

        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var document = ReadDocument();
            var existing = document.Layouts.SingleOrDefault(layout =>
                layout.CampusProfileId == campusProfileId && layout.RoomId == roomId);
            var layout = Reconcile(existing, campusProfileId, roomId, normalizedTargets) with { Columns = columns };
            WriteDocument(Replace(document, layout));
            return Clone(layout);
        }
    }

    public static IReadOnlyList<ClassroomSeatLocation> GetLocations(ClassroomSeatLayout layout)
    {
        ValidateLayout(layout);
        return Array.AsReadOnly(layout.TargetOrder.Select((target, index) => new ClassroomSeatLocation(
            target, index / layout.Columns + 1, index % layout.Columns + 1)).ToArray());
    }

    public static int DefaultColumns(int targetCount)
    {
        if (targetCount is < 1 or > MaximumTargets)
            throw new InvalidDataException("座位图电脑数量超出允许范围。");
        return Math.Clamp((int)Math.Ceiling(Math.Sqrt(targetCount)), 1, targetCount);
    }

    private SeatLayoutDocument ReadDocument()
    {
        if (!File.Exists(_path)) return new SeatLayoutDocument(CurrentSchemaVersion, []);
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("本机座位图文件大小超出限制；原文件未修改。");
        try
        {
            var bytes = File.ReadAllBytes(_path);
            PolicyJson.RejectDuplicateFields(bytes);
            var document = JsonSerializer.Deserialize<SeatLayoutDocument>(bytes, JsonOptions)
                           ?? throw new InvalidDataException("本机座位图文件为空；原文件未修改。");
            ValidateDocument(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("本机座位图格式无效；原文件未修改。", exception);
        }
    }

    private void WriteDocument(SeatLayoutDocument document)
    {
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("本机座位图超过 16 MiB 限制；原文件未修改。");
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("本机座位图路径无效。");
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
                        ?? throw new InvalidDataException("本机座位图路径无效。");
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
        throw new IOException("另一 TeacherConsole 实例正在更新座位图，请稍后重试。", lastFailure);
    }

    private static SeatLayoutDocument Replace(SeatLayoutDocument document, ClassroomSeatLayout layout)
    {
        var layouts = document.Layouts.Where(item => item.CampusProfileId != layout.CampusProfileId ||
                                                      item.RoomId != layout.RoomId)
            .Append(layout).ToList();
        return new SeatLayoutDocument(CurrentSchemaVersion, layouts);
    }

    private static ClassroomSeatLayout Reconcile(ClassroomSeatLayout? existing, Guid campusProfileId, Guid roomId,
        IReadOnlyList<string> targets)
    {
        if (existing is null) return CreateDefault(campusProfileId, roomId, targets);
        var canonicalTargets = targets.ToDictionary(target => target, StringComparer.OrdinalIgnoreCase);
        var order = new List<string>(targets.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in existing.TargetOrder)
            if (canonicalTargets.TryGetValue(target, out var canonical) && seen.Add(canonical))
                order.Add(canonical);
        foreach (var target in targets)
            if (seen.Add(target)) order.Add(target);
        return existing with
        {
            Columns = Math.Clamp(existing.Columns, 1, targets.Count),
            TargetOrder = order.ToArray()
        };
    }

    private static ClassroomSeatLayout CreateDefault(Guid campusProfileId, Guid roomId,
        IReadOnlyList<string> targets) => new(campusProfileId, roomId, DefaultColumns(targets.Count), targets.ToArray());

    private static string[] NormalizeTargets(IReadOnlyList<string> targets)
    {
        if (targets is null || targets.Count is < 1 or > MaximumTargets)
            throw new InvalidDataException("座位图必须包含 1 到 150 台电脑。");
        var normalized = targets.ToArray();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in normalized)
            if (string.IsNullOrWhiteSpace(target) || target.Length > 128 || target != target.Trim() ||
                target.Any(char.IsControl) || !unique.Add(target))
                throw new InvalidDataException("座位图电脑标签无效或重复。");
        return normalized;
    }

    private static void ValidateDocument(SeatLayoutDocument? document)
    {
        if (document is null || document.SchemaVersion != CurrentSchemaVersion || document.Layouts is null ||
            document.Layouts.Count > MaximumLayouts)
            throw new InvalidDataException("本机座位图版本或数量无效；原文件未修改。");
        var identities = new HashSet<(Guid Campus, Guid Room)>();
        foreach (var layout in document.Layouts)
        {
            ValidateLayout(layout);
            if (!identities.Add((layout.CampusProfileId, layout.RoomId)))
                throw new InvalidDataException("本机座位图包含重复校区/机房 ID。");
        }
    }

    private static void ValidateLayout(ClassroomSeatLayout? layout)
    {
        if (layout is null || layout.CampusProfileId == Guid.Empty || layout.RoomId == Guid.Empty ||
            layout.TargetOrder is null || layout.TargetOrder.Length is < 1 or > MaximumTargets ||
            layout.Columns < 1 || layout.Columns > layout.TargetOrder.Length)
            throw new InvalidDataException("座位图机房 ID、电脑数量或每排座位数无效。");
        _ = NormalizeTargets(layout.TargetOrder);
    }

    private static void ValidateIdentity(Guid campusProfileId, Guid roomId)
    {
        if (campusProfileId == Guid.Empty || roomId == Guid.Empty)
            throw new InvalidDataException("座位图必须绑定有效的本机校区和机房 ID。");
    }

    private static bool Equivalent(ClassroomSeatLayout? left, ClassroomSeatLayout? right) =>
        left is not null && right is not null && left.CampusProfileId == right.CampusProfileId &&
        left.RoomId == right.RoomId && left.Columns == right.Columns &&
        left.TargetOrder.SequenceEqual(right.TargetOrder, StringComparer.Ordinal);

    private static ClassroomSeatLayout Clone(ClassroomSeatLayout layout) =>
        layout with { TargetOrder = layout.TargetOrder.ToArray() };
}
