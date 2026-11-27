using System.Diagnostics;
using System.Runtime.Versioning;

namespace VeyonCampus.Core;

/// <summary>
/// Keeps entertainment clients closed while a classroom website policy is in force.
/// </summary>
/// <remarks>
/// Address-level blocking cannot hold a client such as Douyin: the application ships its own
/// resolver, keeps a built-in address list, is scheduled to a different CDN node than the teacher
/// machine resolves, and speaks QUIC. Blocking the name and the addresses the teacher machine saw
/// therefore leaves the running client untouched. The only layer that reliably covers "this
/// application may not be used in class" is the process itself, so a policy that restricts the
/// matching site also closes and keeps closing the matching client.
///
/// The mapping is deliberately short and fixed: a process is only closed when the policy itself
/// names the site it belongs to, so a teacher who never blocks Douyin never loses Douyin. An
/// allowlist closes the entertainment clients it does not allow, which is the same intent stated
/// the other way round.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ClassroomAppSuppression
{
    /// <summary>Process names per restricted site. Names exclude the .exe suffix.</summary>
    internal static readonly (string Domain, string[] Processes)[] KnownClients =
    [
        ("douyin.com", ["douyin", "douyinlive", "douyinlauncher", "douyinpc"]),
        ("iesdouyin.com", ["douyin", "douyinlive", "douyinlauncher", "douyinpc"]),
        ("kuaishou.com", ["kuaishou", "kwai"]),
        ("xiaohongshu.com", ["xiaohongshu"]),
        ("weibo.com", ["weibo"]),
        ("bilibili.com", ["bilibili"]),
        ("huya.com", ["huya"]),
        ("douyu.com", ["douyu"])
    ];

    /// <summary>
    /// Offline games that belong to no domain, so no site entry can ever reach them.
    /// </summary>
    /// <remarks>
    /// These are closed whenever any restricting policy is in force. An offline card game needs no
    /// network at all, which makes it the first thing a student opens after pulling the link or
    /// turning the radio off — the very move the website policy is meant to survive. Names are the
    /// process image names Windows reports, matched case-insensitively by
    /// <see cref="Process.GetProcessesByName(string)"/>.
    /// </remarks>
    internal static readonly string[] OfflineGameProcesses =
    [
        // Windows built-in and Store card/board games.
        "solitaire", "solitairecollection", "microsoftsolitairecollection",
        "minesweeper", "spidersolitaire", "spider", "freecell", "mahjong", "mahjongg",
        "purbleplace", "jigsawpuzzle", "hearts", "chess", "msmahjong", "bubblewitch",
        // Locally installed single-player games.
        "plantsvszombies", "pvz", "pvzlawn", "angrybirds", "bejeweled", "zuma",
        "insaniquarium", "feedingfrenzy", "dinerdash", "cookingdash",
        // Arcade emulators.
        "mame", "mameui", "kawaks", "neogeo", "fba", "winKawaks", "nebula"
    ];

    private static readonly object Gate = new();
    private static string[] _processNames = [];
    private static DateTimeOffset? _untilUtc;
    private static long _revision;
    private static int _closedCount;
    private static DateTimeOffset? _lastClosedUtc;
    private static string? _lastClosedName;
    private static string? _lastError;

    /// <summary>Process names currently held closed; empty when no restricting policy is active.</summary>
    public static IReadOnlyList<string> ActiveProcessNames
    {
        get { lock (Gate) return _processNames; }
    }

    /// <summary>What the suppression has done, for diagnostics and the device health view.</summary>
    public static ClassroomAppSuppressionSnapshot Snapshot
    {
        get
        {
            lock (Gate)
                return new ClassroomAppSuppressionSnapshot(_processNames.Length, _untilUtc, _revision,
                    _closedCount, _lastClosedUtc, _lastClosedName, _lastError);
        }
    }

    /// <summary>
    /// Recomputes the restricted client list from the policy the machine currently holds. Called at
    /// Agent start, after every applied policy and on each watcher tick; a policy that expired or
    /// was released yields an empty list, which stops the pressure on the next sweep.
    /// </summary>
    public static void Sync(string campusId)
    {
        if (!OperatingSystem.IsWindows()) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(campusId);
        try
        {
            var status = WebsitePolicyRegistryStore.ReadStatus(campusId, DateTimeOffset.UtcNow,
                includeDomains: true);
            var now = DateTimeOffset.UtcNow;
            var active = status.Mode != WebsitePolicyMode.Disabled &&
                         (status.ExpiresUtc is not { } expiry || expiry > now) &&
                         status.ExpiredUtc is null;
            var names = active
                ? ResolveTargets(status.Mode, status.BlockedDomains ?? [], status.AllowedDomains ?? [])
                : [];
            lock (Gate)
            {
                _processNames = names;
                _untilUtc = active ? status.ExpiresUtc : null;
                _revision = status.Revision;
                _lastError = null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or InvalidOperationException or
                                              ArgumentException or System.Text.Json.JsonException or
                                              System.Security.SecurityException or
                                              System.Runtime.InteropServices.COMException)
        {
            // The registry may be mid-transaction while a policy is being applied. Keep the previous
            // list rather than releasing clients because of a transient read.
            lock (Gate) _lastError = exception.Message;
        }
    }

    /// <summary>Closes every running process on the current list once. Returns how many were closed.</summary>
    public static int Sweep()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        string[] names;
        lock (Gate) names = _processNames;
        if (names.Length == 0) return 0;
        var closed = 0;
        // Look up the handful of names on the list instead of walking every process on the machine:
        // a classroom PC can run several hundred processes and this runs every 1.5 seconds.
        foreach (var name in names)
        {
            Process[] matches;
            try { matches = Process.GetProcessesByName(name); }
            catch (Exception exception) when (exception is InvalidOperationException or
                                                  System.ComponentModel.Win32Exception or NotSupportedException)
            {
                continue;
            }
            foreach (var process in matches)
            {
                try
                {
                    if (process.SessionId == 0) continue;
                    var actual = process.ProcessName;
                    process.Kill(entireProcessTree: true);
                    closed++;
                    lock (Gate)
                    {
                        _closedCount++;
                        _lastClosedUtc = DateTimeOffset.UtcNow;
                        _lastClosedName = actual;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                                                      System.ComponentModel.Win32Exception or NotSupportedException or
                                                      UnauthorizedAccessException or AggregateException)
                {
                    // The process exited between enumeration and the kill, or belongs to another user
                    // this account may not touch. Both are normal; the next sweep tries again.
                }
                finally { process.Dispose(); }
            }
        }
        return closed;
    }

    /// <summary>Watcher loop: refresh the policy view and close matching clients every tick.</summary>
    public static async Task RunAsync(string campusId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        Sync(campusId);
        Sweep();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.5));
        // The list only changes when a policy is applied or expires, and both paths call Sync
        // themselves. Re-reading the registry on every tick would parse the whole domain document
        // twice a second for nothing, so the sweep runs often and the list refreshes slowly.
        var ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (++ticks % 8 == 0) Sync(campusId);
                Sweep();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Maps a policy to the clients it closes. A blocklist closes the clients of the sites it names;
    /// an allowlist closes every known client it does not allow; combined mode applies the blocklist
    /// first and then removes whatever the allowlist explicitly keeps.
    /// </summary>
    internal static string[] ResolveTargets(WebsitePolicyMode mode,
        IReadOnlyList<string> blockedDomains, IReadOnlyList<string> allowedDomains)
    {
        ArgumentNullException.ThrowIfNull(blockedDomains);
        ArgumentNullException.ThrowIfNull(allowedDomains);
        if (mode == WebsitePolicyMode.Disabled) return [];
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        // Offline games belong to no domain, so a site entry can never reach them. Any restricting
        // policy closes them as well: an offline game is exactly what a student falls back to once
        // the network is gone or the site list has taken effect.
        result.UnionWith(OfflineGameProcesses);
        switch (mode)
        {
            case WebsitePolicyMode.Blocklist:
                foreach (var entry in KnownClients)
                    if (blockedDomains.Any(domain => Matches(domain, entry.Domain)))
                        result.UnionWith(entry.Processes);
                break;
            case WebsitePolicyMode.Allowlist:
                foreach (var entry in KnownClients)
                    if (!allowedDomains.Any(domain => Matches(domain, entry.Domain)))
                        result.UnionWith(entry.Processes);
                break;
            case WebsitePolicyMode.Combined:
                // Combined mode is a blocklist with named exceptions, not an allowlist: only a site
                // the policy blocks may close its client, and an explicit exception wins.
                foreach (var entry in KnownClients)
                {
                    if (allowedDomains.Any(domain => Matches(domain, entry.Domain))) continue;
                    if (blockedDomains.Any(domain => Matches(domain, entry.Domain)))
                        result.UnionWith(entry.Processes);
                }
                break;
            default:
                return [];
        }
        return result.ToArray();
    }

    /// <summary>True when a policy entry covers the site, in either direction.</summary>
    internal static bool Matches(string policyDomain, string siteDomain)
    {
        var left = policyDomain.Trim().TrimStart('.').ToLowerInvariant();
        var right = siteDomain.TrimStart('.').ToLowerInvariant();
        return left.Equals(right, StringComparison.Ordinal) ||
               left.EndsWith("." + right, StringComparison.Ordinal);
    }
}

public sealed record ClassroomAppSuppressionSnapshot(int RestrictedProcessCount, DateTimeOffset? UntilUtc,
    long Revision, int ClosedCount, DateTimeOffset? LastClosedUtc, string? LastClosedProcess, string? LastError)
{
    public bool Active => RestrictedProcessCount > 0;
}
