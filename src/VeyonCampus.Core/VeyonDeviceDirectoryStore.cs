using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public enum VeyonDeviceDirectoryState
{
    InSync,
    Discovered,
    Renamed,
    HostChanged,
    Moved,
    MissingInVeyon,
    Conflict
}

public sealed record VeyonDeviceDirectoryEntry(Guid DeviceId, Guid VeyonObjectId,
    Guid VeyonLocationObjectId, string LocationName, string VeyonComputerName,
    string HostAddress, string MacAddress, VeyonDeviceDirectoryState State,
    string? ConflictDetail, DateTimeOffset LastObservedUtc, DateTimeOffset? MissingSinceUtc);

public sealed record VeyonDeviceDirectorySnapshot(int SchemaVersion, string DirectoryFingerprint,
    DateTimeOffset LastFullReadUtc, IReadOnlyList<VeyonDeviceDirectoryEntry> Devices);

/// <summary>
/// Per-user stable projection of the verified Veyon Builtin Directory. Veyon UUID is the
/// only automatic identity key; a missing/recreated UUID never inherits another DeviceId.
/// </summary>
public sealed class VeyonDeviceDirectoryStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumDevices = 10_000;
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(3);
    private static readonly object ProcessGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<VeyonDeviceDirectoryState>(allowIntegerValues: false) }
    };

    private readonly string _path;

    public VeyonDeviceDirectoryStore(string? path = null) =>
        _path = Path.GetFullPath(path ?? DefaultPath);

    public static string DefaultPath => Path.Combine(
        VeyonCampusPaths.GetLocalApplicationDataDirectory(), "VeyonCampus", "Teacher",
        "veyon-device-directory.json");

    public VeyonDeviceDirectorySnapshot Read()
    {
        lock (ProcessGate)
        using (AcquireFileLock()) return ReadNoLock();
    }

    /// <summary>Reconciles only a complete, trusted networkobjects dump/config snapshot.</summary>
    public VeyonDeviceDirectorySnapshot Reconcile(IReadOnlyList<VeyonNetworkObject> objects,
        string directoryFingerprint, DateTimeOffset? observedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Count > MaximumDevices * 2 || directoryFingerprint is not { Length: 64 } ||
            directoryFingerprint.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Veyon 设备快照数量或指纹无效；原设备目录记录未修改。");
        var now = (observedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (ProcessGate)
        using (AcquireFileLock())
        {
            var previous = ReadNoLock();
            var previousByObjectId = previous.Devices.ToDictionary(item => item.VeyonObjectId);
            var locations = objects.Where(item => item.Type.Equals("location", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (locations.Any(item => item.ObjectId == Guid.Empty) ||
                locations.Select(item => item.ObjectId).Distinct().Count() != locations.Length)
                throw new InvalidDataException("Veyon 地点 UUID 缺失或重复；原设备目录记录未修改。");
            var locationNames = locations.ToDictionary(item => item.ObjectId, item => item.Name);
            var computers = objects.Where(item => item.Type.Equals("computer", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (computers.Length > MaximumDevices || computers.Any(item => item.ObjectId == Guid.Empty) ||
                computers.Select(item => item.ObjectId).Distinct().Count() != computers.Length)
                throw new InvalidDataException("Veyon 电脑 UUID 缺失、重复或设备数量超限；原设备目录记录未修改。");

            var conflicts = new Dictionary<Guid, HashSet<string>>();
            void AddConflict(Guid objectId, string detail)
            {
                if (!conflicts.TryGetValue(objectId, out var details))
                    conflicts.Add(objectId, details = new HashSet<string>(StringComparer.Ordinal));
                details.Add(detail);
            }

            var ambiguousLocationIds = locations.Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).SelectMany(group => group)
                .Select(item => item.ObjectId).ToHashSet();

            foreach (var computer in computers)
            {
                if (!locationNames.ContainsKey(computer.ParentObjectId))
                    AddConflict(computer.ObjectId, "父地点 UUID 缺失或无法读取");
                else if (ambiguousLocationIds.Contains(computer.ParentObjectId))
                    AddConflict(computer.ObjectId, "Veyon 地点名称重复");
                if (string.IsNullOrWhiteSpace(computer.Name) || computer.Name.Length > 100 ||
                    computer.Name.Any(char.IsControl))
                    AddConflict(computer.ObjectId, "电脑名称为空或无效");
                try
                {
                    var normalizedHost = VeyonHostAddress.NormalizeOverride(computer.Host);
                    if (normalizedHost.Length == 0)
                        AddConflict(computer.ObjectId, "Host 为空，不能作为已验证学生目标");
                    else if (Uri.CheckHostName(normalizedHost) != UriHostNameType.Dns)
                        AddConflict(computer.ObjectId, "Veyon Host 不是固定 Windows 电脑名；IP 地址不能作为设备目录连接地址");
                }
                catch (InvalidDataException) { AddConflict(computer.ObjectId, "Host 格式无效"); }
            }

            var hostGroups = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var computer in computers)
            {
                try
                {
                    var host = VeyonHostAddress.NormalizeOverride(computer.Host);
                    if (host.Length == 0) continue;
                    if (!hostGroups.TryGetValue(host, out var owners)) hostGroups.Add(host, owners = []);
                    owners.Add(computer.ObjectId);
                }
                catch (InvalidDataException) { }
            }
            foreach (var owners in hostGroups.Values.Where(owners => owners.Count > 1))
                foreach (var objectId in owners) AddConflict(objectId, "Veyon Host 在多个电脑对象中重复");

            var entries = new List<VeyonDeviceDirectoryEntry>(computers.Length + previous.Devices.Count);
            foreach (var computer in computers)
            {
                previousByObjectId.TryGetValue(computer.ObjectId, out var prior);
                var locationName = locationNames.GetValueOrDefault(computer.ParentObjectId, "");
                string normalizedHost;
                try { normalizedHost = VeyonHostAddress.NormalizeOverride(computer.Host); }
                catch (InvalidDataException) { normalizedHost = computer.Host; }
                var isMoved = prior is not null && prior.VeyonLocationObjectId != computer.ParentObjectId;
                var isHostChanged = prior is not null &&
                    !string.Equals(prior.HostAddress, normalizedHost, StringComparison.OrdinalIgnoreCase);
                var isRenamed = prior is not null &&
                    !string.Equals(prior.VeyonComputerName, computer.Name, StringComparison.Ordinal);
                var state = conflicts.ContainsKey(computer.ObjectId) ? VeyonDeviceDirectoryState.Conflict
                    : prior is null ? VeyonDeviceDirectoryState.Discovered
                    : isMoved ? VeyonDeviceDirectoryState.Moved
                    : isHostChanged ? VeyonDeviceDirectoryState.HostChanged
                    : isRenamed ? VeyonDeviceDirectoryState.Renamed
                    : VeyonDeviceDirectoryState.InSync;
                entries.Add(new VeyonDeviceDirectoryEntry(prior?.DeviceId ?? Guid.NewGuid(),
                    computer.ObjectId, computer.ParentObjectId, Bound(locationName, 100),
                    Bound(computer.Name, 100), Bound(normalizedHost, 253), Bound(computer.Mac, 32), state,
                    conflicts.TryGetValue(computer.ObjectId, out var details)
                        ? string.Join("；", details.OrderBy(item => item, StringComparer.Ordinal)) : null,
                    now, null));
            }

            var currentIds = computers.Select(item => item.ObjectId).ToHashSet();
            foreach (var missing in previous.Devices.Where(item => !currentIds.Contains(item.VeyonObjectId)))
                entries.Add(missing with
                {
                    State = VeyonDeviceDirectoryState.MissingInVeyon,
                    ConflictDetail = null,
                    MissingSinceUtc = missing.MissingSinceUtc ?? now
                });

            if (entries.Count > MaximumDevices * 2)
                throw new InvalidDataException("Veyon 设备投影超出存储上限；原设备目录记录未修改。");
            var snapshot = new VeyonDeviceDirectorySnapshot(CurrentSchemaVersion, directoryFingerprint,
                now, Array.AsReadOnly(entries.OrderBy(item => item.LocationName,
                        StringComparer.OrdinalIgnoreCase).ThenBy(item => item.VeyonComputerName,
                        StringComparer.OrdinalIgnoreCase).ThenBy(item => item.VeyonObjectId)
                    .ToArray()));
            WriteNoLock(snapshot);
            return snapshot;
        }
    }

    private VeyonDeviceDirectorySnapshot ReadNoLock()
    {
        if (!File.Exists(_path))
            return new VeyonDeviceDirectorySnapshot(CurrentSchemaVersion, new string('0', 64),
                DateTimeOffset.MinValue, Array.Empty<VeyonDeviceDirectoryEntry>());
        PathLinkSecurity.RejectLinks(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("Veyon 设备目录缓存文件大小无效；原文件未修改。");
        var bytes = File.ReadAllBytes(_path);
        try
        {
            PolicyJson.RejectDuplicateFields(bytes);
            var snapshot = JsonSerializer.Deserialize<VeyonDeviceDirectorySnapshot>(bytes, JsonOptions)
                           ?? throw new InvalidDataException("Veyon 设备目录缓存为空；原文件未修改。");
            Validate(snapshot);
            return snapshot;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Veyon 设备目录缓存格式无效；原文件未修改。", exception);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    private void WriteNoLock(VeyonDeviceDirectorySnapshot snapshot)
    {
        Validate(snapshot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (bytes.Length is < 1 or > MaximumFileBytes)
            throw new InvalidDataException("Veyon 设备目录缓存超过 8 MiB；原文件未修改。");
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("Veyon 设备目录缓存路径无效。");
        Directory.CreateDirectory(directory);
        PathLinkSecurity.RejectLinks(directory);
        if (File.Exists(_path)) PathLinkSecurity.RejectLinks(_path);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private FileStream AcquireFileLock()
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidDataException("Veyon 设备目录缓存路径无效。");
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
        throw new IOException("另一 TeacherConsole 实例正在更新 Veyon 设备目录缓存；请稍后重试。", lastFailure);
    }

    private static void Validate(VeyonDeviceDirectorySnapshot snapshot)
    {
        if (snapshot.SchemaVersion != CurrentSchemaVersion || snapshot.DirectoryFingerprint is not { Length: 64 } fp ||
            fp.Any(character => !char.IsAsciiHexDigit(character)) || snapshot.Devices is null ||
            snapshot.Devices.Count > MaximumDevices * 2 || snapshot.LastFullReadUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Veyon 设备目录缓存版本、指纹、时间或数量无效；原文件未修改。");
        var deviceIds = new HashSet<Guid>();
        var objectIds = new HashSet<Guid>();
        foreach (var item in snapshot.Devices)
        {
            if (item is null || item.DeviceId == Guid.Empty || item.VeyonObjectId == Guid.Empty ||
                !deviceIds.Add(item.DeviceId) || !objectIds.Add(item.VeyonObjectId) ||
                !Enum.IsDefined(item.State) || item.LocationName is null || item.LocationName.Length > 100 ||
                item.VeyonComputerName is null || item.VeyonComputerName.Length > 100 ||
                item.HostAddress is null || item.HostAddress.Length > 253 || item.MacAddress is null ||
                item.MacAddress.Length > 32 || item.ConflictDetail?.Length > 500 ||
                item.LastObservedUtc.Offset != TimeSpan.Zero ||
                (item.MissingSinceUtc is { } missingSince && missingSince.Offset != TimeSpan.Zero))
                throw new InvalidDataException("Veyon 设备目录缓存含无效或重复设备身份；原文件未修改。");
        }
    }

    private static string Bound(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..max];
    }
}
