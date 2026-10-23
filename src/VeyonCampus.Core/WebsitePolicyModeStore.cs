using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record WebsitePolicyModeDraft(string CampusId, WebsitePolicyMode SelectedMode,
    DateTimeOffset UpdatedUtc);

/// <summary>Persists the selected website policy mode separately from legacy list drafts.</summary>
public static class WebsitePolicyModeStore
{
    private sealed record ModeFile(int SchemaVersion, IReadOnlyList<WebsitePolicyModeDraft> Drafts);
    private const long MaximumFileBytes = 256 * 1024;
    private const int MaximumDrafts = 100;
    private const int MaximumCampusIdCharacters = 128;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static string DefaultDirectory => WebsitePolicyListDraftStore.DefaultDirectory;

    public static WebsitePolicyMode? Read(string campusId, string? directory = null)
    {
        ValidateCampusId(campusId);
        lock (Gate)
            return ReadFile(GetPath(directory)).SingleOrDefault(item =>
                string.Equals(item.CampusId, campusId, StringComparison.Ordinal))?.SelectedMode;
    }

    public static void Save(string campusId, WebsitePolicyMode selectedMode, string? directory = null)
    {
        ValidateCampusId(campusId);
        if (selectedMode is not (WebsitePolicyMode.Blocklist or WebsitePolicyMode.Allowlist or
            WebsitePolicyMode.Combined))
            throw new InvalidDataException("网站限制方式无效。");

        lock (Gate)
        {
            var path = GetPath(directory);
            var drafts = ReadFile(path);
            var draft = new WebsitePolicyModeDraft(campusId, selectedMode, DateTimeOffset.UtcNow);
            var index = drafts.FindIndex(item => string.Equals(item.CampusId, campusId, StringComparison.Ordinal));
            if (index >= 0) drafts[index] = draft;
            else drafts.Add(draft);
            if (drafts.Count > MaximumDrafts)
                throw new InvalidDataException("本机保存的网站限制方式校区数量超过限制。");
            WriteFile(path, drafts);
        }
    }

    private static string GetPath(string? directory) => Path.Combine(
        Path.GetFullPath(directory ?? DefaultDirectory), "website-policy-modes.json");

    private static List<WebsitePolicyModeDraft> ReadFile(string path)
    {
        if (!File.Exists(path)) return [];
        PathLinkSecurity.RejectLinks(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("本机网站限制方式文件大小无效。");
        var bytes = File.ReadAllBytes(path);
        PolicyJson.RejectDuplicateFields(bytes);
        var file = JsonSerializer.Deserialize<ModeFile>(bytes, Options)
                   ?? throw new InvalidDataException("本机网站限制方式文件为空。");
        if (file.SchemaVersion != 1 || file.Drafts is null || file.Drafts.Count > MaximumDrafts ||
            file.Drafts.Any(item => item is null))
            throw new InvalidDataException("本机网站限制方式文件版本或内容无效。");
        foreach (var draft in file.Drafts)
        {
            ValidateCampusId(draft.CampusId);
            if (draft.SelectedMode is not (WebsitePolicyMode.Blocklist or WebsitePolicyMode.Allowlist or
                WebsitePolicyMode.Combined))
                throw new InvalidDataException("本机网站限制方式文件包含无效方式。");
        }
        if (file.Drafts.Select(item => item.CampusId).Distinct(StringComparer.Ordinal).Count() != file.Drafts.Count)
            throw new InvalidDataException("本机网站限制方式文件包含重复校区。");
        return file.Drafts.ToList();
    }

    private static void WriteFile(string path, IReadOnlyList<WebsitePolicyModeDraft> drafts)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("本机网站限制方式路径无效。");
        Directory.CreateDirectory(parent);
        PathLinkSecurity.RejectLinks(parent);
        if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ModeFile(1, drafts), Options);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("本机网站限制方式文件超过大小限制。");
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

    private static void ValidateCampusId(string campusId)
    {
        if (string.IsNullOrWhiteSpace(campusId) || campusId.Length > MaximumCampusIdCharacters ||
            !string.Equals(campusId, campusId.Trim(), StringComparison.Ordinal) ||
            campusId.Any(char.IsControl))
            throw new InvalidDataException("校区名称无效，无法读取或保存网站限制方式。");
    }
}
