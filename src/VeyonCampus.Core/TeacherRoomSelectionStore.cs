using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public sealed record TeacherRoomSelection(Guid CampusProfileId, Guid RoomId);

public sealed record TeacherRoomSelectionDocument(int SchemaVersion,
    IReadOnlyList<TeacherRoomSelection> Selections);

/// <summary>Stores the teacher's last selected local room per campus profile.</summary>
public sealed class TeacherRoomSelectionStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 64 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _path;

    public TeacherRoomSelectionStore(string? path = null) =>
        _path = Path.GetFullPath(path ?? DefaultPath);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Teacher", "recent-room-selection.json");

    public IReadOnlyDictionary<Guid, Guid> Load()
    {
        lock (Gate) return ReadNoLock();
    }

    public void Save(Guid campusProfileId, Guid roomId)
    {
        if (campusProfileId == Guid.Empty || roomId == Guid.Empty)
            throw new InvalidDataException("最近教室记录需要有效的校区和地点编号。");

        lock (Gate)
        {
            var selections = ReadNoLock().ToDictionary(item => item.Key, item => item.Value);
            selections[campusProfileId] = roomId;
            if (selections.Count > TeacherCampusDirectoryStore.MaximumProfiles)
                throw new InvalidDataException("最近教室记录数量超出限制。");

            var document = new TeacherRoomSelectionDocument(CurrentSchemaVersion,
                selections.OrderBy(item => item.Key)
                    .Select(item => new TeacherRoomSelection(item.Key, item.Value)).ToArray());
            var payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (payload.Length > MaximumFileBytes)
                throw new InvalidDataException("最近教室记录超过文件大小限制。");

            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidDataException("最近教室记录路径无效。");
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

    private Dictionary<Guid, Guid> ReadNoLock()
    {
        if (!File.Exists(_path)) return [];
        if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("最近教室记录不能是符号链接或重解析点。");

        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("最近教室记录大小超出限制。");

        TeacherRoomSelectionDocument document;
        try
        {
            document = JsonSerializer.Deserialize<TeacherRoomSelectionDocument>(
                File.ReadAllBytes(_path), JsonOptions)
                ?? throw new InvalidDataException("最近教室记录为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("最近教室记录格式无效；未覆盖原文件。", exception);
        }

        if (document.SchemaVersion != CurrentSchemaVersion || document.Selections is null ||
            document.Selections.Count > TeacherCampusDirectoryStore.MaximumProfiles)
            throw new InvalidDataException("最近教室记录版本或数量无效；未覆盖原文件。");

        var result = new Dictionary<Guid, Guid>();
        foreach (var selection in document.Selections)
        {
            if (selection is null || selection.CampusProfileId == Guid.Empty || selection.RoomId == Guid.Empty ||
                !result.TryAdd(selection.CampusProfileId, selection.RoomId))
                throw new InvalidDataException("最近教室记录包含无效或重复地点；未覆盖原文件。");
        }
        return result;
    }
}
