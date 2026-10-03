using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

/// <summary>A local campus profile. ProfileId is a stable local identifier, never a CloudBase campus_id.</summary>
public sealed record TeacherCampusProfile(Guid ProfileId, string DisplayName, List<TeacherRoomProfile> Rooms)
{
    public string StableIdLabel => ProfileId.ToString("N")[..8];
}

/// <summary>A local room profile. Student names are intentionally not part of the saved profile.</summary>
public sealed record TeacherRoomProfile(Guid RoomId, string DisplayName, string Prefix,
    int StartNumber, int ComputerCount, List<string>? HostOverrides = null)
{
    public string StableIdLabel => RoomId.ToString("N")[..8];
    public string ComputerRangeLabel =>
        $"{Prefix}{StartNumber.ToString("D2", CultureInfo.InvariantCulture)}–{Prefix}{(StartNumber + ComputerCount - 1).ToString("D2", CultureInfo.InvariantCulture)} · {ComputerCount} 台";
}

/// <summary>
/// Per-user local directory for campus and room profiles. It stores names and planning fields only;
/// it is not an authorization source and is never included in student packages or CloudBase requests.
/// </summary>
public sealed class TeacherCampusDirectoryStore
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumProfiles = 1000;
    public const int MaximumRoomsPerCampus = 1000;
    public const int MaximumFileBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _path;

    public TeacherCampusDirectoryStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? DefaultPath);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyonCampus", "Teacher", "campus-directory.json");

    public IReadOnlyList<TeacherCampusProfile> Load()
    {
        lock (Gate)
        {
            if (!File.Exists(_path)) return Array.Empty<TeacherCampusProfile>();
            if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("本机校区档案不能是符号链接或重解析点。");

            var info = new FileInfo(_path);
            if (info.Length is < 1 or > MaximumFileBytes)
                throw new InvalidDataException("本机校区档案大小超出允许范围。");

            TeacherCampusDirectoryDocument document;
            try
            {
                document = JsonSerializer.Deserialize<TeacherCampusDirectoryDocument>(
                    File.ReadAllBytes(_path), JsonOptions)
                    ?? throw new InvalidDataException("本机校区档案为空。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("本机校区档案格式无效；未覆盖原文件。", exception);
            }

            // Schema 2 adds optional per-computer host/IP overrides; existing schema 1 profiles remain usable.
            if (document.SchemaVersion == 1)
                document = document with { SchemaVersion = CurrentSchemaVersion };

            Validate(document);
            return document.Campuses.Select(Clone).ToArray();
        }
    }

    public void Save(IEnumerable<TeacherCampusProfile> campuses)
    {
        ArgumentNullException.ThrowIfNull(campuses);
        lock (Gate)
        {
            // Never replace an unreadable or unsupported existing file with an empty/partial UI state.
            if (File.Exists(_path)) _ = Load();

            var document = new TeacherCampusDirectoryDocument(CurrentSchemaVersion,
                campuses.Select(Clone).ToList());
            Validate(document);
            var payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (payload.Length > MaximumFileBytes)
                throw new InvalidDataException("本机校区档案超过 1 MiB 限制。");

            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidDataException("本机校区档案路径无效。");
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

    private static void Validate(TeacherCampusDirectoryDocument document)
    {
        if (document.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("本机校区档案版本不受支持；未覆盖原文件。");
        if (document.Campuses is null || document.Campuses.Count > MaximumProfiles)
            throw new InvalidDataException("校区档案数量无效。");

        var profileIds = new HashSet<Guid>();
        foreach (var campus in document.Campuses)
        {
            if (campus is null || campus.ProfileId == Guid.Empty || !profileIds.Add(campus.ProfileId))
                throw new InvalidDataException("校区档案 ID 缺失或重复。");
            ValidateName(campus.DisplayName, "校区显示名");
            if (campus.Rooms is null || campus.Rooms.Count > MaximumRoomsPerCampus)
                throw new InvalidDataException("机房档案数量无效。");

            var roomIds = new HashSet<Guid>();
            foreach (var room in campus.Rooms)
            {
                if (room is null || room.RoomId == Guid.Empty || !roomIds.Add(room.RoomId))
                    throw new InvalidDataException("机房档案 ID 缺失或重复。");
                ValidateName(room.DisplayName, "机房显示名");
                if (room.HostOverrides is { } hostOverrides)
                {
                    if (hostOverrides.Count > room.ComputerCount)
                        throw new InvalidDataException("机房主机/IP 覆盖行数不能超过电脑数量。");
                    foreach (var host in hostOverrides)
                        if (!string.IsNullOrEmpty(host)) _ = VeyonHostAddress.NormalizeOverride(host);
                }
                try
                {
                    _ = MachineNaming.CreateRange(room.Prefix,
                        room.StartNumber.ToString(CultureInfo.InvariantCulture),
                        room.ComputerCount.ToString(CultureInfo.InvariantCulture));
                }
                catch (InvalidDataException exception)
                {
                    throw new InvalidDataException("机房电脑规划无效：" + exception.Message, exception);
                }
            }
        }
    }

    private static void ValidateName(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value != value.Trim() ||
            value.Any(char.IsControl))
            throw new InvalidDataException($"{label}须为 1–100 个字符，不含首尾空格或控制字符。");
    }

    private static TeacherCampusProfile Clone(TeacherCampusProfile campus)
    {
        ArgumentNullException.ThrowIfNull(campus);
        return campus with
        {
            Rooms = campus.Rooms?.Select(room => room with { HostOverrides = room.HostOverrides?.ToList() }).ToList()!
        };
    }

    private sealed record TeacherCampusDirectoryDocument(int SchemaVersion,
        List<TeacherCampusProfile> Campuses);
}
