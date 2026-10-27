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
    int StartNumber, int ComputerCount, List<string>? HostOverrides = null, List<int>? ComputerNumbers = null)
{
    public string StableIdLabel => RoomId.ToString("N")[..8];
    public IReadOnlyList<int> GetComputerNumbers() => ComputerNumbers is { } numbers
        ? Array.AsReadOnly(numbers.ToArray())
        : ComputerCount == 0
            ? Array.Empty<int>()
            : Array.AsReadOnly(Enumerable.Range(StartNumber, ComputerCount).ToArray());
    public IReadOnlyList<string> GetComputerNames() => MachineNaming.CreateNames(Prefix, GetComputerNumbers());
    public string ComputerRangeLabel
    {
        get
        {
            var numbers = GetComputerNumbers();
            if (numbers.Count == 0) return "尚未配置电脑";
            var first = numbers.Min();
            var last = numbers.Max();
            var rangeLabel = $"{Prefix}{first.ToString("D2", CultureInfo.InvariantCulture)}–{Prefix}{last.ToString("D2", CultureInfo.InvariantCulture)} · {numbers.Count} 台";
            return ComputerNumbers is null || numbers.Count == last - first + 1
                ? rangeLabel
                : rangeLabel + "（固定编号）";
        }
    }
}

/// <summary>Creates or refreshes a local classroom profile from one read-only Veyon location.</summary>
public static class TeacherCampusProfileFromVeyon
{
    public static TeacherCampusProfile MergeLocation(TeacherCampusProfile? campus, string campusName,
        VeyonNetworkLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Targets is null || location.Targets.Count is < 1 or > ClassroomSession.MaximumTargets)
            throw new InvalidDataException($"机房电脑数量必须为 1–{ClassroomSession.MaximumTargets} 台。请在 Veyon 中检查该机房。");

        var displayName = location.Name;
        ValidateName(displayName, "机房名称");
        var targets = location.Targets.Select(VeyonHostAddress.NormalizeOverride).ToArray();
        if (targets.Any(string.IsNullOrWhiteSpace) ||
            targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
            throw new InvalidDataException("Veyon 机房中有空地址或重复电脑，无法用于课堂。请先检查机房名单。");

        var selectedCampus = campus;
        if (selectedCampus is null)
        {
            ValidateName(campusName, "校区名称");
            selectedCampus = new TeacherCampusProfile(Guid.NewGuid(), campusName, []);
        }
        else
        {
            if (selectedCampus.ProfileId == Guid.Empty) throw new InvalidDataException("本机校区 ID 无效。");
            ValidateName(selectedCampus.DisplayName, "校区名称");
        }

        var rooms = selectedCampus.Rooms?.ToList() ?? [];
        var existingIndex = rooms.FindIndex(room =>
            string.Equals(room.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
        var existing = existingIndex >= 0 ? rooms[existingIndex] : null;
        var prefix = existing is { ComputerCount: > 0 } ? existing.Prefix : "PC-";
        var minimumNumber = existing is { ComputerCount: > 0 } ? existing.StartNumber : 1;
        var savedNumberByHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var usedNumbers = rooms.Where((_, index) => index != existingIndex)
            .SelectMany(room => room.GetComputerNumbers()).ToHashSet();
        if (existing is { ComputerCount: > 0 })
        {
            var savedNumbers = existing.GetComputerNumbers();
            usedNumbers.UnionWith(savedNumbers);
            if (existing.HostOverrides is { } savedHosts && savedHosts.Count == existing.ComputerCount)
            {
                if (savedNumbers.Count != savedHosts.Count)
                    throw new InvalidDataException("已保存电脑编号与主机清单数量不一致。");
                savedNumberByHost = savedHosts.Select((host, index) =>
                        (Host: VeyonHostAddress.NormalizeOverride(host), Number: savedNumbers[index]))
                    .ToDictionary(item => item.Host, item => item.Number, StringComparer.OrdinalIgnoreCase);
            }
        }

        var targetNumbers = new int[targets.Length];
        var knownNumberTargets = new HashSet<int>();
        for (var index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            var targetHasNumber = MachineNaming.TryParseNumber(prefix, target, out var targetNumber);
            string? computerDisplayName = null;
            var displayNumber = 0;
            if (location.TargetDisplayNames is not null &&
                location.TargetDisplayNames.TryGetValue(location.Targets[index], out var mappedDisplayName))
                computerDisplayName = mappedDisplayName;
            var displayNameHasNumber = computerDisplayName is not null &&
                MachineNaming.TryParseNumber(prefix, computerDisplayName, out displayNumber);
            if (targetHasNumber && displayNameHasNumber && targetNumber != displayNumber)
                throw new InvalidDataException($"Veyon 电脑“{computerDisplayName}”与主机名“{target}”的编号不一致；请先核对地点清单。");

            var parsedNumber = targetHasNumber ? targetNumber : displayNameHasNumber ? displayNumber : 0;
            var hasSavedNumber = savedNumberByHost.TryGetValue(target, out var savedNumber);
            if (hasSavedNumber && parsedNumber > 0 && savedNumber != parsedNumber)
                throw new InvalidDataException($"Veyon 电脑“{computerDisplayName ?? target}”的编号与本机已保存记录不一致；请先核对地点清单。");
            var knownNumber = hasSavedNumber ? savedNumber : parsedNumber;
            if (knownNumber == 0) continue;
            if (!knownNumberTargets.Add(knownNumber))
                throw new InvalidDataException($"Veyon 地点中存在重复电脑编号 {knownNumber}；请先核对电脑名和主机地址。");
            targetNumbers[index] = knownNumber;
        }

        usedNumbers.UnionWith(knownNumberTargets);
        var nextNumber = Math.Max(minimumNumber, usedNumbers.DefaultIfEmpty(minimumNumber - 1).Max() + 1);
        for (var index = 0; index < targetNumbers.Length; index++)
        {
            if (targetNumbers[index] != 0) continue;
            while (nextNumber <= MachineNaming.MaximumNumber && usedNumbers.Contains(nextNumber)) nextNumber++;
            if (nextNumber > MachineNaming.MaximumNumber)
                throw new InvalidDataException($"没有可分配的新电脑编号；最大编号为 {MachineNaming.MaximumNumber}。");
            targetNumbers[index] = nextNumber;
            usedNumbers.Add(nextNumber++);
        }

        var numbers = targetNumbers.ToList();
        var mergedRoom = new TeacherRoomProfile(existing?.RoomId ?? Guid.NewGuid(), displayName,
            prefix, numbers.Min(), targets.Length, targets.ToList(), numbers);
        if (existingIndex >= 0) rooms[existingIndex] = mergedRoom;
        else rooms.Add(mergedRoom);

        return selectedCampus with { Rooms = rooms };
    }

    private static void ValidateName(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value != value.Trim() ||
            value.Any(char.IsControl))
            throw new InvalidDataException($"{label}须为 1–100 个字符，不含首尾空格或控制字符。");
    }
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
        VeyonCampusPaths.GetLocalApplicationDataDirectory(),
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
                    if (room.StartNumber is < 1 or > MachineNaming.MaximumNumber)
                        throw new InvalidDataException("机房起始编号必须在 1–150 之间。");
                    if (room.ComputerNumbers is { } explicitNumbers)
                    {
                        if (explicitNumbers.Count != room.ComputerCount ||
                            explicitNumbers.Distinct().Count() != explicitNumbers.Count)
                            throw new InvalidDataException("自定义电脑编号数量与机房电脑数量不一致或存在重复。");
                        _ = MachineNaming.CreateNames(room.Prefix, explicitNumbers);
                    }
                    else
                    {
                        _ = MachineNaming.CreateRange(room.Prefix,
                            room.StartNumber.ToString(CultureInfo.InvariantCulture),
                            (room.ComputerCount == 0 ? 1 : room.ComputerCount)
                            .ToString(CultureInfo.InvariantCulture));
                    }
                    if (room.ComputerCount < 0)
                        throw new InvalidDataException("电脑数量不能小于零。");
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
            Rooms = campus.Rooms?.Select(room => room with
            {
                HostOverrides = room.HostOverrides?.ToList(),
                ComputerNumbers = room.ComputerNumbers?.ToList()
            }).ToList()!
        };
    }

    private sealed record TeacherCampusDirectoryDocument(int SchemaVersion,
        List<TeacherCampusProfile> Campuses);
}
