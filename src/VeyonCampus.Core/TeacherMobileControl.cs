using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public enum MobilePolicyProfileKind { Website, Application }

public sealed record MobilePolicyProfile(Guid Id, string Name, string CampusId, MobilePolicyProfileKind Kind,
    int LifetimeMinutes, WebsitePolicyMode? WebsiteMode = null, IReadOnlyList<string>? WebsiteDomains = null,
    ApplicationPolicyMode? ApplicationMode = null, IReadOnlyList<string>? StudentSids = null,
    IReadOnlyList<ApplicationDenyRule>? ApplicationRules = null, DateTimeOffset? UpdatedUtc = null);

public sealed record MobilePairedDevice(Guid Id, string DisplayName, string TokenSha256,
    DateTimeOffset PairedUtc, DateTimeOffset? RevokedUtc = null);

public sealed record MobilePairedDeviceView(Guid Id, string DisplayName, DateTimeOffset PairedUtc);
public sealed record MobileControlAuditTargetResult(string Target, string Outcome);
public sealed record MobileControlAuditEntry(DateTimeOffset TimeUtc, Guid DeviceId, string Action,
    Guid? ProfileId, IReadOnlyList<string> Targets, string Outcome,
    IReadOnlyList<MobileControlAuditTargetResult>? Results = null);

public static class MobilePolicyProfileCompiler
{
    public const int MaximumProfiles = 50;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static MobilePolicyProfile Validate(MobilePolicyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty || !Enum.IsDefined(profile.Kind))
            throw new InvalidDataException("手机策略预设标识或类型无效。");
        WebsitePolicySigningKeyStore.ValidateCampusId(profile.CampusId);
        var name = profile.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl))
            throw new InvalidDataException("预设名称必须为 1–64 个可见字符。");
        if (profile.UpdatedUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new InvalidDataException("预设更新时间必须是 UTC。");

        if (profile.Kind == MobilePolicyProfileKind.Website)
        {
            if (profile.WebsiteMode is not (WebsitePolicyMode.Blocklist or WebsitePolicyMode.Allowlist) ||
                profile.ApplicationMode is not null || profile.StudentSids is { Count: > 0 } ||
                profile.ApplicationRules is { Count: > 0 } ||
                profile.LifetimeMinutes is not (0 or 45 or 60 or 90 or 120 or 1440))
                throw new InvalidDataException("网站策略预设字段无效。");
            var domains = WebsitePolicyCompiler.NormalizeDomains(profile.WebsiteDomains ?? []);
            if (domains.Count == 0)
                throw new InvalidDataException("网站策略预设至少需要一个域名。");
            if (profile.LifetimeMinutes > 0 && profile.LifetimeMinutes > 1440)
                throw new InvalidDataException("网站策略预设期限不能超过 24 小时。");
            return profile with { Name = name, WebsiteDomains = domains, StudentSids = [], ApplicationRules = [] };
        }

        if (profile.WebsiteMode is not null || profile.WebsiteDomains is { Count: > 0 } ||
            profile.ApplicationMode is not (ApplicationPolicyMode.Audit or ApplicationPolicyMode.Enforce) ||
            profile.LifetimeMinutes is not (45 or 60 or 90 or 120 or 1440))
            throw new InvalidDataException("应用策略预设字段无效。");
        var sids = (profile.StudentSids ?? []).Select(sid => sid?.Trim() ?? "").ToArray();
        var rules = (profile.ApplicationRules ?? []).ToArray();
        var issued = DateTimeOffset.UtcNow;
        var app = ApplicationPolicyCompiler.Validate(new ApplicationPolicyDocument(1,
            ApplicationPolicyCompiler.Purpose, profile.CampusId, 1, issued,
            issued.AddMinutes(profile.LifetimeMinutes), profile.ApplicationMode!.Value, sids, rules));
        return profile with
        {
            Name = name,
            StudentSids = app.StudentSids,
            ApplicationRules = app.Rules,
            WebsiteDomains = []
        };
    }
}

/// <summary>Retains a small local history without policy bodies, credentials, or student event details.</summary>
public static class MobileControlAuditStore
{
    private const int MaximumEntries = 500;
    private const long MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void Append(MobileControlAuditEntry entry, string? directory = null)
    {
        var validated = Validate(entry);
        lock (Gate)
        {
            var path = Path.Combine(Path.GetFullPath(directory ?? MobilePolicyProfileStore.DefaultDirectory), "audit.json");
            var entries = ReadFile(path);
            entries.Add(validated);
            if (entries.Count > MaximumEntries) entries.RemoveRange(0, entries.Count - MaximumEntries);
            WriteFile(path, entries);
        }
    }

    public static IReadOnlyList<MobileControlAuditEntry> Read(string? directory = null)
    {
        lock (Gate)
            return Array.AsReadOnly(ReadFile(Path.Combine(Path.GetFullPath(directory ??
                MobilePolicyProfileStore.DefaultDirectory), "audit.json")).ToArray());
    }

    private static MobileControlAuditEntry Validate(MobileControlAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.TimeUtc.Offset != TimeSpan.Zero || entry.DeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(entry.Action) || entry.Action.Length > 40 || entry.Action.Any(char.IsControl) ||
            entry.Targets is null || entry.Targets.Count is 0 or > 150 ||
            entry.Targets.Any(target => string.IsNullOrWhiteSpace(target) || target.Length > 253 || target.Any(char.IsControl)) ||
            string.IsNullOrWhiteSpace(entry.Outcome) || entry.Outcome.Length > 80 || entry.Outcome.Any(char.IsControl))
            throw new InvalidDataException("手机控制审计记录字段无效。");
        var targets = entry.Targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var results = (entry.Results ?? []).ToArray();
        if (targets.Length != entry.Targets.Count || results.Length > 150 ||
            (results.Length > 0 && (results.Length != targets.Length ||
             results.Any(result => result is null || string.IsNullOrWhiteSpace(result.Target) ||
                 result.Target.Length > 253 || result.Target.Any(char.IsControl) ||
                 string.IsNullOrWhiteSpace(result.Outcome) || result.Outcome.Length > 40 || result.Outcome.Any(char.IsControl)) ||
             results.Select(result => result.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != results.Length ||
             results.Any(result => !targets.Contains(result.Target, StringComparer.OrdinalIgnoreCase)))))
            throw new InvalidDataException("手机控制审计逐台结果无效。");
        return entry with
        {
            Targets = Array.AsReadOnly(targets),
            Results = Array.AsReadOnly(results)
        };
    }

    private static List<MobileControlAuditEntry> ReadFile(string path)
    {
        if (!File.Exists(path)) return [];
        PathLinkSecurity.RejectLinks(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("手机控制审计文件大小无效。");
        var bytes = File.ReadAllBytes(path);
        PolicyJson.RejectDuplicateFields(bytes);
        var entries = JsonSerializer.Deserialize<List<MobileControlAuditEntry>>(bytes, Options)
                      ?? throw new InvalidDataException("手机控制审计文件为空。");
        if (entries.Count > MaximumEntries || entries.Any(entry => entry is null))
            throw new InvalidDataException("手机控制审计记录数量或内容无效。");
        return entries.Select(Validate).ToList();
    }

    private static void WriteFile(string path, IReadOnlyList<MobileControlAuditEntry> entries)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("手机控制审计路径无效。");
        Directory.CreateDirectory(parent);
        PathLinkSecurity.RejectLinks(parent);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, Options);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("手机控制审计文件超过大小限制。");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       8192, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }
}

/// <summary>Stores non-secret mobile policy templates in bounded, atomic JSON files.</summary>
public static class MobilePolicyProfileStore
{
    private sealed record ProfileFile(int SchemaVersion, IReadOnlyList<MobilePolicyProfile> Profiles);
    private const long MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = MobilePolicyProfileCompiler.JsonOptions;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VeyonCampus", "MobileControl");

    public static IReadOnlyList<MobilePolicyProfile> ReadAll(string? directory = null)
    {
        lock (Gate) return Array.AsReadOnly(ReadFile(GetPath(directory)).ToArray());
    }

    public static void Save(MobilePolicyProfile profile, string? directory = null)
    {
        var validated = MobilePolicyProfileCompiler.Validate(profile);
        lock (Gate)
        {
            var path = GetPath(directory);
            var profiles = ReadFile(path);
            var index = profiles.FindIndex(item => item.Id == validated.Id);
            if (index >= 0) profiles[index] = validated;
            else profiles.Add(validated);
            if (profiles.Count > MobilePolicyProfileCompiler.MaximumProfiles)
                throw new InvalidDataException($"手机策略预设最多允许 {MobilePolicyProfileCompiler.MaximumProfiles} 个。");
            WriteFile(path, profiles);
        }
    }

    public static bool Remove(Guid id, string? directory = null)
    {
        if (id == Guid.Empty) throw new InvalidDataException("预设标识无效。");
        lock (Gate)
        {
            var path = GetPath(directory);
            var profiles = ReadFile(path);
            var removed = profiles.RemoveAll(item => item.Id == id) > 0;
            if (removed) WriteFile(path, profiles);
            return removed;
        }
    }

    private static string GetPath(string? directory) => Path.Combine(
        Path.GetFullPath(directory ?? DefaultDirectory), "policy-profiles.json");

    private static List<MobilePolicyProfile> ReadFile(string path)
    {
        if (!File.Exists(path)) return [];
        PathLinkSecurity.RejectLinks(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("手机策略预设文件大小无效。");
        var bytes = File.ReadAllBytes(path);
        PolicyJson.RejectDuplicateFields(bytes);
        var file = JsonSerializer.Deserialize<ProfileFile>(bytes, Options)
                   ?? throw new InvalidDataException("手机策略预设文件为空。");
        if (file.SchemaVersion != 1 || file.Profiles is null ||
            file.Profiles.Count > MobilePolicyProfileCompiler.MaximumProfiles)
            throw new InvalidDataException("手机策略预设文件版本或数量无效。");
        if (file.Profiles.Any(profile => profile is null))
            throw new InvalidDataException("手机策略预设文件包含空记录。");
        var profiles = file.Profiles.Select(MobilePolicyProfileCompiler.Validate).ToList();
        if (profiles.Select(item => item.Id).Distinct().Count() != profiles.Count)
            throw new InvalidDataException("手机策略预设标识重复。");
        return profiles;
    }

    private static void WriteFile(string path, IReadOnlyList<MobilePolicyProfile> profiles)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("手机策略预设路径无效。");
        Directory.CreateDirectory(parent);
        PathLinkSecurity.RejectLinks(parent);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ProfileFile(1, profiles), Options);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("手机策略预设文件超过大小限制。");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       8192, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }
}

/// <summary>Stores only one-way mobile bearer-token hashes; callers must never serialize a raw token.</summary>
public static class MobilePairedDeviceStore
{
    private sealed record DeviceFile(int SchemaVersion, IReadOnlyList<MobilePairedDevice> Devices);
    private const int MaximumDevices = 50;
    private const long MaximumFileBytes = 128 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlyList<MobilePairedDeviceView> List(string? directory = null)
    {
        lock (Gate) return Array.AsReadOnly(ReadFile(GetPath(directory)).Where(item => item.RevokedUtc is null)
            .Select(item => new MobilePairedDeviceView(item.Id, item.DisplayName, item.PairedUtc)).ToArray());
    }

    public static MobilePairedDeviceView Add(string rawToken, string displayName, string? directory = null)
    {
        var tokenHash = HashToken(rawToken);
        var name = ValidateDisplayName(displayName);
        lock (Gate)
        {
            var path = GetPath(directory);
            var devices = ReadFile(path);
            if (devices.Count(item => item.RevokedUtc is null) >= MaximumDevices)
                throw new InvalidDataException("已达到配对手机数量上限；请先撤销不再使用的设备。");
            if (devices.Count >= MaximumDevices * 4)
            {
                var removeIds = devices.Where(item => item.RevokedUtc is not null)
                    .OrderBy(item => item.RevokedUtc).Take(devices.Count - MaximumDevices * 4 + 1)
                    .Select(item => item.Id).ToHashSet();
                if (removeIds.Count == 0)
                    throw new InvalidDataException("已配对手机历史记录已满；请先撤销不再使用的设备。");
                devices.RemoveAll(item => removeIds.Contains(item.Id));
            }
            var device = new MobilePairedDevice(Guid.NewGuid(), name, tokenHash, DateTimeOffset.UtcNow);
            devices.Add(device);
            WriteFile(path, devices);
            return new MobilePairedDeviceView(device.Id, device.DisplayName, device.PairedUtc);
        }
    }

    public static bool IsAuthorized(string? rawToken, string? directory = null)
        => FindAuthorized(rawToken, directory) is not null;

    public static MobilePairedDeviceView? FindAuthorized(string? rawToken, string? directory = null)
    {
        string hash;
        try { hash = HashToken(rawToken ?? ""); }
        catch (InvalidDataException) { return null; }
        lock (Gate)
        {
            foreach (var device in ReadFile(GetPath(directory)))
            {
                if (device.RevokedUtc is not null) continue;
                var stored = Convert.FromHexString(device.TokenSha256);
                var supplied = Convert.FromHexString(hash);
                if (CryptographicOperations.FixedTimeEquals(stored, supplied))
                    return new MobilePairedDeviceView(device.Id, device.DisplayName, device.PairedUtc);
            }
            return null;
        }
    }

    public static MobilePairedDeviceView? Find(Guid id, string? directory = null)
    {
        if (id == Guid.Empty) return null;
        lock (Gate)
        {
            var item = ReadFile(GetPath(directory)).FirstOrDefault(device => device.Id == id);
            return item is null ? null : new MobilePairedDeviceView(item.Id, item.DisplayName, item.PairedUtc);
        }
    }

    public static bool Revoke(Guid id, string? directory = null)
    {
        if (id == Guid.Empty) throw new InvalidDataException("配对设备标识无效。");
        lock (Gate)
        {
            var path = GetPath(directory);
            var devices = ReadFile(path);
            var index = devices.FindIndex(item => item.Id == id && item.RevokedUtc is null);
            if (index < 0) return false;
            devices[index] = devices[index] with { RevokedUtc = DateTimeOffset.UtcNow };
            WriteFile(path, devices);
            return true;
        }
    }

    public static string HashToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 128 || token.Any(char.IsControl))
            throw new InvalidDataException("手机配对凭据格式无效。");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static string ValidateDisplayName(string displayName)
    {
        var name = displayName?.Trim() ?? "";
        if (name.Length is < 1 or > 48 || name.Any(character => char.IsControl(character) ||
                char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))
            throw new InvalidDataException("手机名称必须为 1–48 个可见字符。");
        return name;
    }

    private static string GetPath(string? directory) => Path.Combine(
        Path.GetFullPath(directory ?? MobilePolicyProfileStore.DefaultDirectory), "paired-devices.json");

    private static List<MobilePairedDevice> ReadFile(string path)
    {
        if (!File.Exists(path)) return [];
        PathLinkSecurity.RejectLinks(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("已配对手机记录大小无效。");
        var bytes = File.ReadAllBytes(path);
        PolicyJson.RejectDuplicateFields(bytes);
        var file = JsonSerializer.Deserialize<DeviceFile>(bytes, Options)
                   ?? throw new InvalidDataException("已配对手机记录为空。");
        if (file.SchemaVersion != 1 || file.Devices is null || file.Devices.Count > MaximumDevices * 4)
            throw new InvalidDataException("已配对手机记录版本或数量无效。");
        if (file.Devices.Any(device => device is null))
            throw new InvalidDataException("已配对手机记录包含空记录。");
        foreach (var device in file.Devices)
        {
            if (device.Id == Guid.Empty || string.IsNullOrWhiteSpace(device.TokenSha256) || device.TokenSha256.Length != 64 ||
                !device.TokenSha256.All(Uri.IsHexDigit) || device.PairedUtc.Offset != TimeSpan.Zero ||
                (device.RevokedUtc is { Offset: var offset } && offset != TimeSpan.Zero))
                throw new InvalidDataException("已配对手机记录字段无效。");
            _ = ValidateDisplayName(device.DisplayName);
        }
        if (file.Devices.Select(item => item.Id).Distinct().Count() != file.Devices.Count)
            throw new InvalidDataException("已配对手机记录标识重复。");
        return file.Devices.ToList();
    }

    private static void WriteFile(string path, IReadOnlyList<MobilePairedDevice> devices)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("手机配对记录路径无效。");
        Directory.CreateDirectory(parent);
        PathLinkSecurity.RejectLinks(parent);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DeviceFile(1, devices), Options);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("已配对手机记录超过大小限制。");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       8192, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }
}
