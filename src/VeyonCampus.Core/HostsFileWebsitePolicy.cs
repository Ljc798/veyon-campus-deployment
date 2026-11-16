using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace VeyonCampus.Core;

/// <summary>Outcome of writing the managed hosts block. Failures are reported, never thrown.</summary>
public sealed record HostsFilePolicyResult(bool Applied, long Revision, int EntryCount, string? Error)
{
    public static HostsFilePolicyResult Failed(long revision, string error) => new(false, revision, 0, error);
}

/// <summary>
/// Keeps a marked block in the Windows hosts file that resolves blocked domains to 0.0.0.0.
/// Chromium policy is only honoured by browsers that implement it; 360, QQ, Sogou and most
/// other vendor browsers ignore machine policy entirely, and hosts is the only mechanism that
/// applies to every process on the machine. The block is delimited by markers so a teacher's
/// own entries and other software's entries survive untouched.
///
/// Hosts has no wildcard syntax, so a domain is written both as-is and with a www prefix.
/// That covers the addresses teachers actually type; deeper subdomains still rely on browser
/// policy where it exists.
/// </summary>
public static class HostsFileWebsitePolicy
{
    public const string BeginMarker = "# BEGIN VeyonCampus Website Policy";
    public const string EndMarker = "# END VeyonCampus Website Policy";

    /// <summary>Entries are capped well below the policy limit of 1000 because every domain is written twice.</summary>
    private const int MaximumDomains = 1000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    /// <summary>Returns the revision recorded in the managed block, or null when there is no block.</summary>
    public static long? ReadAppliedRevision(string? hostsPath = null)
    {
        var path = hostsPath ?? DefaultPath;
        if (!File.Exists(path)) return null;
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return null; }

        var inside = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == BeginMarker) { inside = true; continue; }
            if (trimmed == EndMarker) return null;
            if (!inside) continue;
            const string prefix = "# revision:";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(trimmed[prefix.Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                    out var revision))
                return revision;
        }
        return null;
    }

    /// <summary>
    /// Replaces the managed block with entries for <paramref name="blockedDomains"/>. An empty
    /// domain list removes the block, which is how Disabled and expiry are applied.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static HostsFilePolicyResult Apply(long revision, string campusId,
        IEnumerable<string> blockedDomains, string? hostsPath = null)
    {
        ArgumentNullException.ThrowIfNull(blockedDomains);
        if (!OperatingSystem.IsWindows())
            return HostsFilePolicyResult.Failed(revision, "hosts 兜底仅在 Windows 上可用。");

        var entries = ExpandHostsEntries(blockedDomains);
        // An empty list is how the block is removed, and removal needs no campus identity.
        if (entries.Count > 0 && string.IsNullOrWhiteSpace(campusId))
            return HostsFilePolicyResult.Failed(revision, "hosts 兜底缺少校区标识。");
        return Write(revision, campusId, entries, hostsPath ?? DefaultPath);
    }

    /// <summary>Removes the managed block. Used by uninstall and by disabling the policy.</summary>
    [SupportedOSPlatform("windows")]
    public static HostsFilePolicyResult Clear(string? hostsPath = null) =>
        Apply(0, "", Array.Empty<string>(), hostsPath);

    /// <summary>True when the block exists and still lists at least one host.</summary>
    public static bool HasBlock(string? hostsPath = null)
    {
        var path = hostsPath ?? DefaultPath;
        if (!File.Exists(path)) return false;
        try
        {
            var inside = false;
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed == BeginMarker) { inside = true; continue; }
                if (trimmed == EndMarker) return false;
                if (inside && trimmed.Length > 0 && !trimmed.StartsWith('#')) return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>
    /// Expands domains into concrete host entries. Hosts matching is exact, so each domain is
    /// emitted with and without the www prefix to approximate the subdomain coverage that
    /// Chromium's URLBlocklist gives an unprefixed host.
    /// </summary>
    public static IReadOnlyList<string> ExpandHostsEntries(IEnumerable<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in domains)
        {
            var domain = raw?.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(domain)) continue;
            if (domain.Length > 253 || domain.Contains(' ') || domain.Contains('/') || domain.Contains('*'))
                continue;
            hosts.Add(domain.ToLowerInvariant());
            if (!domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
                !domain.Contains(':'))
                hosts.Add("www." + domain.ToLowerInvariant());
            if (hosts.Count > MaximumDomains * 2) break;
        }
        return hosts.ToArray();
    }

    private static HostsFilePolicyResult Write(long revision, string campusId, IReadOnlyList<string> entries,
        string path)
    {
        if (entries.Count > MaximumDomains * 2)
            return HostsFilePolicyResult.Failed(revision,
                $"hosts 兜底最多允许 {MaximumDomains} 个域名（每个域名展开为两条记录）。");

        string[] existing;
        try
        {
            existing = File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return HostsFilePolicyResult.Failed(revision, DescribeWriteFailure(exception));
        }

        var kept = StripManagedBlock(existing);
        var result = new List<string>(kept.Count + entries.Count + 4);
        result.AddRange(kept);
        if (entries.Count > 0)
        {
            if (result.Count > 0 && result[^1].Trim().Length > 0) result.Add("");
            result.Add(BeginMarker);
            result.Add($"# revision: {revision.ToString(CultureInfo.InvariantCulture)}");
            result.Add($"# campus: {campusId}");
            result.Add("# 由 Veyon Campus 学生端维护；删除本区块会解除所有浏览器的兜底拦截。");
            foreach (var entry in entries)
                result.Add("0.0.0.0 " + entry);
            result.Add(EndMarker);
        }
        while (result.Count > 0 && result[^1].Trim().Length == 0) result.RemoveAt(result.Count - 1);

        var content = string.Join("\r\n", result) + "\r\n";
        var temporaryPath = path + ".veyoncampus.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content, Utf8NoBom);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException)
        {
            TryDelete(temporaryPath);
            return HostsFilePolicyResult.Failed(revision, DescribeWriteFailure(exception));
        }

        _ = DnsFlushResolverCache();
        return new HostsFilePolicyResult(true, revision, entries.Count, null);
    }

    private static List<string> StripManagedBlock(IReadOnlyList<string> lines)
    {
        var kept = new List<string>(lines.Count);
        var inside = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == BeginMarker) { inside = true; continue; }
            if (trimmed == EndMarker) { inside = false; continue; }
            if (!inside) kept.Add(line);
        }
        return kept;
    }

    private static string DescribeWriteFailure(Exception exception) => exception switch
    {
        UnauthorizedAccessException or System.Security.SecurityException =>
            "没有写入 hosts 文件的权限。安全软件（常见于 360 安全卫士、腾讯电脑管家）会锁定 hosts，请把 C:\\Windows\\System32\\drivers\\etc\\hosts 加入其信任区，或以 SYSTEM 身份重新应用。",
        IOException =>
            $"hosts 文件被其他程序占用，无法写入：{exception.Message}。请关闭锁定 hosts 的安全软件后重新应用。",
        _ => $"写入 hosts 失败：{exception.Message}"
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("dnsapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();
}
