using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record WebsitePolicyListDraft(string CampusId, string BlockedDomains,
    string AllowedDomains, DateTimeOffset UpdatedUtc, WebsitePolicyMode? Mode = null);

/// <summary>Stores the current editable website lists per campus for the teacher app and paired mobile UI.</summary>
public static class WebsitePolicyListDraftStore
{
    private sealed record DraftFile(int SchemaVersion, IReadOnlyList<WebsitePolicyListDraft> Drafts);
    private const long MaximumFileBytes = 2 * 1024 * 1024;
    private const int MaximumDrafts = 100;
    private const int MaximumCampusIdCharacters = 128;
    private const int MaximumListCharacters = 24 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static string DefaultDirectory => MobilePolicyProfileStore.DefaultDirectory;

    public static WebsitePolicyListDraft? Read(string campusId, string? directory = null)
    {
        ValidateCampusId(campusId);
        lock (Gate)
            return ReadFile(GetPath(directory)).SingleOrDefault(item =>
                string.Equals(item.CampusId, campusId, StringComparison.Ordinal));
    }

    public static void Save(string campusId, string blockedDomains, string allowedDomains,
        WebsitePolicyMode? mode = null, string? directory = null)
    {
        ValidateCampusId(campusId);
        ArgumentNullException.ThrowIfNull(blockedDomains);
        ArgumentNullException.ThrowIfNull(allowedDomains);
        if (blockedDomains.Length > MaximumListCharacters || allowedDomains.Length > MaximumListCharacters)
            throw new InvalidDataException("网站名单草稿超过本机保存限制。");

        lock (Gate)
        {
            var path = GetPath(directory);
            var drafts = ReadFile(path);
            var index = drafts.FindIndex(item => string.Equals(item.CampusId, campusId, StringComparison.Ordinal));
            var selectedMode = mode ?? (index >= 0 ? drafts[index].Mode : null) ?? InferMode(blockedDomains, allowedDomains);
            ValidateMode(selectedMode);
            var draft = new WebsitePolicyListDraft(campusId, blockedDomains, allowedDomains,
                DateTimeOffset.UtcNow, selectedMode);
            if (index >= 0) drafts[index] = draft;
            else drafts.Add(draft);
            if (drafts.Count > MaximumDrafts)
                throw new InvalidDataException("本机保存的网站名单校区数量超过限制。");
            WriteFile(path, drafts);
        }
    }

    /// <summary>Saves a phone-edited draft only when it has not changed since it was read.</summary>
    public static bool TrySave(string campusId, string blockedDomains, string allowedDomains,
        DateTimeOffset? expectedUpdatedUtc, out WebsitePolicyListDraft? savedDraft, string? directory = null)
    {
        ValidateCampusId(campusId);
        ArgumentNullException.ThrowIfNull(blockedDomains);
        ArgumentNullException.ThrowIfNull(allowedDomains);
        if (blockedDomains.Length > MaximumListCharacters || allowedDomains.Length > MaximumListCharacters)
            throw new InvalidDataException("网站名单草稿超过本机保存限制。");

        lock (Gate)
        {
            var path = GetPath(directory);
            var drafts = ReadFile(path);
            var index = drafts.FindIndex(item => string.Equals(item.CampusId, campusId, StringComparison.Ordinal));
            var current = index >= 0 ? drafts[index] : null;
            if (current?.UpdatedUtc != expectedUpdatedUtc)
            {
                savedDraft = current;
                return false;
            }

            var mode = current?.Mode ?? InferMode(blockedDomains, allowedDomains);
            var draft = new WebsitePolicyListDraft(campusId, blockedDomains, allowedDomains,
                DateTimeOffset.UtcNow, mode);
            if (index >= 0) drafts[index] = draft;
            else drafts.Add(draft);
            if (drafts.Count > MaximumDrafts)
                throw new InvalidDataException("本机保存的网站名单校区数量超过限制。");
            WriteFile(path, drafts);
            savedDraft = draft;
            return true;
        }
    }

    private static string GetPath(string? directory) => Path.Combine(
        Path.GetFullPath(directory ?? DefaultDirectory), "website-policy-lists.json");

    private static List<WebsitePolicyListDraft> ReadFile(string path)
    {
        if (!File.Exists(path)) return [];
        PathLinkSecurity.RejectLinks(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("本机网站名单文件大小无效。");
        var bytes = File.ReadAllBytes(path);
        PolicyJson.RejectDuplicateFields(bytes);
        var file = JsonSerializer.Deserialize<DraftFile>(bytes, Options)
                   ?? throw new InvalidDataException("本机网站名单文件为空。");
        if (file.SchemaVersion != 1 || file.Drafts is null || file.Drafts.Count > MaximumDrafts ||
            file.Drafts.Any(item => item is null))
            throw new InvalidDataException("本机网站名单文件版本或内容无效。");
        foreach (var draft in file.Drafts)
        {
            ValidateCampusId(draft.CampusId);
            if (draft.BlockedDomains is null || draft.AllowedDomains is null ||
                draft.BlockedDomains.Length > MaximumListCharacters ||
                draft.AllowedDomains.Length > MaximumListCharacters ||
                (draft.Mode.HasValue && draft.Mode.Value is not
                    (WebsitePolicyMode.Blocklist or WebsitePolicyMode.Allowlist or WebsitePolicyMode.Combined)))
                throw new InvalidDataException("本机网站名单文件包含无效名单。");
        }
        if (file.Drafts.Select(item => item.CampusId).Distinct(StringComparer.Ordinal).Count() != file.Drafts.Count)
            throw new InvalidDataException("本机网站名单文件包含重复校区。");
        return file.Drafts.ToList();
    }

    private static void WriteFile(string path, IReadOnlyList<WebsitePolicyListDraft> drafts)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("本机网站名单路径无效。");
        Directory.CreateDirectory(parent);
        PathLinkSecurity.RejectLinks(parent);
        if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DraftFile(1, drafts), Options);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("本机网站名单文件超过大小限制。");
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
            throw new InvalidDataException("校区名称无效，无法读取或保存网站名单。");
    }

    private static WebsitePolicyMode InferMode(string blockedDomains, string allowedDomains)
    {
        var hasBlocked = !string.IsNullOrWhiteSpace(blockedDomains);
        var hasAllowed = !string.IsNullOrWhiteSpace(allowedDomains);
        return (hasBlocked, hasAllowed) switch
        {
            (true, true) => WebsitePolicyMode.Combined,
            (false, true) => WebsitePolicyMode.Allowlist,
            _ => WebsitePolicyMode.Blocklist
        };
    }

    private static void ValidateMode(WebsitePolicyMode mode)
    {
        if (mode is not (WebsitePolicyMode.Blocklist or WebsitePolicyMode.Allowlist or WebsitePolicyMode.Combined))
            throw new InvalidDataException("本机网站名单策略模式无效。");
    }
}
