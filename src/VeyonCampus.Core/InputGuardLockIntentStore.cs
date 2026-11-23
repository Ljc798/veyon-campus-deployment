using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

/// <summary>
/// A persisted "keep this machine's keyboard/mouse locked until <see cref="ReleaseUtc"/>" intent,
/// owned by the SYSTEM Agent and stored next to its config with a private ACL.
///
/// Why this exists: the Guard lease lives only in process memory, and the teacher's 5-second
/// renewal loop stops permanently on the first failed read. A student reboot therefore used to
/// drop the lock (and a reboot is exactly how a student tries to escape it). Persisting the
/// intent lets the Agent re-lock on startup and keep renewing on its own, independent of whether
/// the teacher is currently polling. The teacher's explicit 解除 deletes the file; ReleaseUtc is
/// an absolute deadline so even a forgotten lock always frees the machine by a known time.
/// </summary>
internal static class InputGuardLockIntentStore
{
    private const string FileName = "input-guard-lock-intent.json";

    private sealed record IntentDocument(
        int SchemaVersion,
        string CampusId,
        string LeaseId,
        string IssuedUtc,
        string ReleaseUtc,
        int GraceSeconds,
        // When the lock may engage after a reboot. Equals IssuedUtc for a normal lock (immediate);
        // on the first Agent restart it is set to startup+GraceSeconds and persisted, so the student
        // can reach the desktop and repeated reboots cannot reset the grace window forever.
        string ResumeAfterUtc);

    private static readonly JsonSerializerOptions JsonOptions = WebsitePolicyAgent.JsonOptions;

    private static string IntentPath(string configPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))
                        ?? throw new InvalidDataException("学生代理配置路径无效。");
        return Path.Combine(directory, FileName);
    }

    /// <summary>Writes (or replaces) the persisted intent atomically with a private ACL.</summary>
    internal static void Write(string configPath, string campusId, Guid leaseId,
        DateTimeOffset issuedUtc, DateTimeOffset releaseUtc, int graceSeconds,
        DateTimeOffset? resumeAfterUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campusId);
        if (leaseId == Guid.Empty) throw new InvalidDataException("键鼠锁定意愿缺少租约编号。");
        var path = IntentPath(configPath);
        var effectiveResume = resumeAfterUtc ?? issuedUtc;
        var document = new IntentDocument(1, campusId, leaseId.ToString("D"),
            issuedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            releaseUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            graceSeconds,
            effectiveResume.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            AgentFileSecurity.SecurePrivateFile(tempPath);
            File.Move(tempPath, path, overwrite: true);
            AgentFileSecurity.SecurePrivateFile(path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Removes the intent. Idempotent.</summary>
    internal static void Clear(string configPath)
    {
        var path = IntentPath(configPath);
        if (!File.Exists(path)) return;
        try { AgentFileSecurity.SecurePrivateFile(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        File.Delete(path);
    }

    /// <summary>Reads a still-valid intent. Returns null when absent, malformed, or past deadline.</summary>
    internal static InputGuardLockIntent? Read(string configPath, string campusId, DateTimeOffset nowUtc)
    {
        var path = IntentPath(configPath);
        if (!File.Exists(path)) return null;
        IntentDocument? document;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is 0 or > 8192) return null;
            document = JsonSerializer.Deserialize<IntentDocument>(bytes, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        if (document is null || document.SchemaVersion != 1 ||
            !string.Equals(document.CampusId, campusId, StringComparison.Ordinal) ||
            !Guid.TryParseExact(document.LeaseId, "D", out var leaseId) || leaseId == Guid.Empty ||
            !DateTimeOffset.TryParseExact(document.IssuedUtc, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var issuedUtc) ||
            !DateTimeOffset.TryParseExact(document.ReleaseUtc, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var releaseUtc) ||
            !DateTimeOffset.TryParseExact(document.ResumeAfterUtc, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var resumeAfterUtc) ||
            issuedUtc.Offset != TimeSpan.Zero || releaseUtc.Offset != TimeSpan.Zero ||
            resumeAfterUtc.Offset != TimeSpan.Zero ||
            document.GraceSeconds is < 0 or > 600 || releaseUtc <= issuedUtc ||
            releaseUtc > issuedUtc.Add(StudentInputGuardCommandCryptography.MaximumPersistedLockLifetime) ||
            resumeAfterUtc > releaseUtc)
            return null;
        var now = nowUtc.ToUniversalTime();
        // Past the absolute deadline: the intent is stale. Do NOT delete here (read is side-effect
        // free); the caller decides to release and clear through the normal path.
        if (now >= releaseUtc) return null;
        return new InputGuardLockIntent(leaseId, issuedUtc, releaseUtc, document.GraceSeconds, resumeAfterUtc);
    }
}

/// <summary>A validated, still-active persisted lock intent.</summary>
internal sealed record InputGuardLockIntent(
    Guid LeaseId,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ReleaseUtc,
    int GraceSeconds,
    DateTimeOffset ResumeAfterUtc);
