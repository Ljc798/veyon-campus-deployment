using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record WebsitePolicyPresetLists(string BlockedDomains, string AllowedDomains,
    DateTimeOffset? UpdatedUtc);

/// <summary>Stores teacher-editable common website lists on the teacher computer.</summary>
public static class WebsitePolicyPresetStore
{
    private sealed record PresetFile(int SchemaVersion, string BlockedDomains, string AllowedDomains,
        DateTimeOffset UpdatedUtc);

    private const long MaximumFileBytes = 64 * 1024;
    private const int MaximumListCharacters = 24 * 1024;
    private static readonly object Gate = new();
    private static readonly string[] DefaultBlockedDomains =
    [
        "douyin.com", "kuaishou.com", "bilibili.com", "b23.tv", "xiaohongshu.com", "xhslink.com",
        "weibo.com", "weibo.cn", "douyu.com", "huya.com", "4399.com", "7k7k.com"
    ];
    private static readonly string[] DefaultAllowedDomains =
    [
        "smartedu.cn", "icourse163.org", "xuetangx.com"
    ];
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static string DefaultDirectory => WebsitePolicyListDraftStore.DefaultDirectory;

    public static WebsitePolicyPresetLists Read(string? directory = null)
    {
        var path = GetPath(directory);
        lock (Gate)
        {
            if (!File.Exists(path)) return CreateDefaults();
            PathLinkSecurity.RejectLinks(path);
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumFileBytes)
                throw new InvalidDataException("常用网站名单文件大小无效。");
            var bytes = File.ReadAllBytes(path);
            PolicyJson.RejectDuplicateFields(bytes);
            var file = JsonSerializer.Deserialize<PresetFile>(bytes, Options)
                       ?? throw new InvalidDataException("常用网站名单文件为空。");
            if (file.SchemaVersion != 1 || file.BlockedDomains is null || file.AllowedDomains is null ||
                file.BlockedDomains.Length > MaximumListCharacters ||
                file.AllowedDomains.Length > MaximumListCharacters)
                throw new InvalidDataException("常用网站名单文件版本或内容无效。");

            return new WebsitePolicyPresetLists(NormalizeList(file.BlockedDomains),
                NormalizeList(file.AllowedDomains), file.UpdatedUtc);
        }
    }

    public static WebsitePolicyPresetLists Save(string blockedDomains, string allowedDomains,
        string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(blockedDomains);
        ArgumentNullException.ThrowIfNull(allowedDomains);
        if (blockedDomains.Length > MaximumListCharacters || allowedDomains.Length > MaximumListCharacters)
            throw new InvalidDataException("常用网站名单超过本机保存限制。");

        var blocked = NormalizeList(blockedDomains);
        var allowed = NormalizeList(allowedDomains);
        var saved = new WebsitePolicyPresetLists(blocked, allowed, DateTimeOffset.UtcNow);
        lock (Gate) WriteFile(GetPath(directory), saved);
        return saved;
    }

    private static WebsitePolicyPresetLists CreateDefaults() =>
        new(string.Join(Environment.NewLine, DefaultBlockedDomains),
            string.Join(Environment.NewLine, DefaultAllowedDomains), null);

    private static string NormalizeList(string value)
    {
        var entries = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, WebsitePolicyCompiler.NormalizeDomains(entries));
    }

    private static string GetPath(string? directory) => Path.Combine(
        Path.GetFullPath(directory ?? DefaultDirectory), "website-policy-presets.json");

    private static void WriteFile(string path, WebsitePolicyPresetLists presets)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("常用网站名单路径无效。");
        Directory.CreateDirectory(parent);
        PathLinkSecurity.RejectLinks(parent);
        if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new PresetFile(1, presets.BlockedDomains, presets.AllowedDomains, presets.UpdatedUtc!.Value), Options);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("常用网站名单文件超过大小限制。");

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
