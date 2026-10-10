using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core;

public sealed record CampusDailyOperationsReport(
    DateOnly ReportDate,
    int UpdateSucceededCount,
    int UpdatePartialCount,
    int UpdateFailedCount,
    int UpdateCancelledCount,
    int StudentTargetSucceededCount,
    int StudentTargetNeedsReviewCount,
    int StudentTargetFailedCount,
    int ClassroomSessionCount,
    Dictionary<string, int> FailureCounts);

public sealed record CampusOperationsTelemetryPreference(bool Enabled, DateTimeOffset? EnabledAtUtc);

/// <summary>Builds daily aggregates from local-only diagnostic and classroom stores.</summary>
public static class CampusOperationsTelemetrySummaryBuilder
{
    public const int MaximumLookbackDays = 14;
    public const int MaximumDailyCount = 20000;

    private static readonly HashSet<string> FailureCodes = new(StringComparer.Ordinal)
    {
        "UPDATE_PARTIAL", "UPDATE_TIMEOUT", "UPDATE_NETWORK", "UPDATE_PERMISSION_DENIED",
        "UPDATE_SIGNATURE_INVALID", "UPDATE_ARTIFACT_INVALID", "UPDATE_UNSUPPORTED",
        "UPDATE_CONFIGURATION", "UPDATE_LOCAL_IO", "UPDATE_INPUT_INVALID", "UPDATE_UNKNOWN"
    };

    public static IReadOnlyList<CampusDailyOperationsReport> Build(
        IReadOnlyList<UpdateDiagnosticEntry> diagnostics,
        IReadOnlyList<ClassroomSession> sessions,
        DateTimeOffset enabledAtUtc,
        DateOnly todayHkt)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(sessions);
        if (enabledAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("运维汇总启用时间必须使用 UTC。");

        var earliestDay = HongKongDate(enabledAtUtc);
        var firstReportDay = todayHkt.AddDays(-MaximumLookbackDays);
        if (earliestDay > firstReportDay) firstReportDay = earliestDay;
        var lastReportDay = todayHkt.AddDays(-1);
        if (firstReportDay > lastReportDay) return Array.Empty<CampusDailyOperationsReport>();

        var reportDates = Enumerable.Range(0, lastReportDay.DayNumber - firstReportDay.DayNumber + 1)
            .Select(offset => firstReportDay.AddDays(offset)).ToArray();
        var byDate = reportDates.ToDictionary(day => day,
            _ => new MutableDailyReport());

        foreach (var entry in diagnostics)
        {
            if (entry is null || entry.OccurredAtUtc < enabledAtUtc ||
                !byDate.TryGetValue(HongKongDate(entry.OccurredAtUtc), out var report)) continue;

            switch (entry.Outcome)
            {
                case UpdateDiagnosticOutcome.Succeeded:
                    report.UpdateSucceededCount = AddBounded(report.UpdateSucceededCount, 1);
                    break;
                case UpdateDiagnosticOutcome.Partial:
                    report.UpdatePartialCount = AddBounded(report.UpdatePartialCount, 1);
                    AddFailure(report, entry.Code);
                    break;
                case UpdateDiagnosticOutcome.Failed:
                    report.UpdateFailedCount = AddBounded(report.UpdateFailedCount, 1);
                    AddFailure(report, entry.Code);
                    break;
                case UpdateDiagnosticOutcome.Cancelled:
                    report.UpdateCancelledCount = AddBounded(report.UpdateCancelledCount, 1);
                    break;
                case UpdateDiagnosticOutcome.Started:
                    // A handoff and its eventual result have different IDs; counting both as attempts double counts.
                    break;
                default:
                    throw new InvalidDataException("更新诊断结果无效。");
            }

            if (entry.Outcome != UpdateDiagnosticOutcome.Started &&
                entry.Operation == UpdateDiagnosticOperation.StudentRollout &&
                entry.Counts is { IsValid: true } counts)
            {
                report.StudentTargetSucceededCount = AddBounded(report.StudentTargetSucceededCount, counts.Succeeded);
                report.StudentTargetNeedsReviewCount = AddBounded(report.StudentTargetNeedsReviewCount, counts.NeedsReview);
                report.StudentTargetFailedCount = AddBounded(report.StudentTargetFailedCount, counts.Failed);
            }
        }

        foreach (var session in sessions)
        {
            if (session is null || session.StartedAtUtc < enabledAtUtc ||
                !byDate.TryGetValue(HongKongDate(session.StartedAtUtc), out var report)) continue;
            report.ClassroomSessionCount = AddBounded(report.ClassroomSessionCount, 1);
        }

        return byDate.OrderBy(pair => pair.Key)
            .Where(pair => pair.Value.HasData)
            .Select(pair => pair.Value.ToReport(pair.Key))
            .ToArray();
    }

    public static DateOnly HongKongDate(DateTimeOffset timestampUtc)
    {
        var local = timestampUtc.ToOffset(TimeSpan.FromHours(8));
        return new DateOnly(local.Year, local.Month, local.Day);
    }

    public static bool IsValid(CampusDailyOperationsReport? report)
    {
        if (report is null ||
            !IsValidCount(report.UpdateSucceededCount) || !IsValidCount(report.UpdatePartialCount) ||
            !IsValidCount(report.UpdateFailedCount) || !IsValidCount(report.UpdateCancelledCount) ||
            !IsValidCount(report.StudentTargetSucceededCount) ||
            !IsValidCount(report.StudentTargetNeedsReviewCount) ||
            !IsValidCount(report.StudentTargetFailedCount) ||
            report.ClassroomSessionCount is < 0 or > 30 || report.FailureCounts is null ||
            report.FailureCounts.Count > FailureCodes.Count)
            return false;

        if (report.FailureCounts.Any(item => !FailureCodes.Contains(item.Key) ||
                                             item.Value is < 0 or > UpdateDiagnosticsStore.MaximumRecordCount))
            return false;
        return report.FailureCounts.Values.Sum() <= report.UpdatePartialCount + report.UpdateFailedCount;
    }

    public static bool IsFailureCode(string? code) => code is not null && FailureCodes.Contains(code);

    private static bool IsValidCount(int count) => count is >= 0 and <= MaximumDailyCount;

    private static int AddBounded(int current, int increment) =>
        (int)Math.Min(MaximumDailyCount, (long)current + increment);

    private static void AddFailure(MutableDailyReport report, string code)
    {
        if (!FailureCodes.Contains(code)) return;
        report.FailureCounts.TryGetValue(code, out var current);
        report.FailureCounts[code] = AddBounded(current, 1);
    }

    private sealed class MutableDailyReport
    {
        public int UpdateSucceededCount { get; set; }
        public int UpdatePartialCount { get; set; }
        public int UpdateFailedCount { get; set; }
        public int UpdateCancelledCount { get; set; }
        public int StudentTargetSucceededCount { get; set; }
        public int StudentTargetNeedsReviewCount { get; set; }
        public int StudentTargetFailedCount { get; set; }
        public int ClassroomSessionCount { get; set; }
        public Dictionary<string, int> FailureCounts { get; } = new(StringComparer.Ordinal);

        public bool HasData => UpdateSucceededCount + UpdatePartialCount + UpdateFailedCount +
                               UpdateCancelledCount + ClassroomSessionCount > 0;

        public CampusDailyOperationsReport ToReport(DateOnly reportDate) => new(reportDate,
            UpdateSucceededCount, UpdatePartialCount, UpdateFailedCount, UpdateCancelledCount,
            StudentTargetSucceededCount, StudentTargetNeedsReviewCount, StudentTargetFailedCount,
            ClassroomSessionCount,
            FailureCounts.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
    }
}

/// <summary>Stores one explicit opt-in and a bounded, idempotent queue of daily aggregates.</summary>
public sealed class CampusOperationsTelemetryStore
{
    private const int MaximumStateBytes = 64 * 1024;
    private const int CurrentSchemaVersion = 1;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    private readonly string _path;

    public CampusOperationsTelemetryStore(string? path = null)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _path = Path.GetFullPath(path ?? Path.Combine(
            string.IsNullOrWhiteSpace(localData) ? Path.GetTempPath() : localData,
            "VeyonCampus", "Telemetry", "operations-reporting.json"));
    }

    public CampusOperationsTelemetryPreference LoadPreference()
    {
        lock (Gate) return ReadUnsafe().Preference;
    }

    public CampusOperationsTelemetryPreference SetEnabled(bool enabled, DateTimeOffset? nowUtc = null)
    {
        lock (Gate)
        {
            var current = ReadUnsafe();
            var changed = enabled != current.Enabled;
            var enabledAt = enabled
                ? current.Enabled && current.EnabledAtUtc is { } existing
                    ? existing
                    : (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime()
                : (DateTimeOffset?)null;
            IReadOnlyList<CampusDailyOperationsReport> pending = enabled && !changed
                ? current.PendingReports
                : Array.Empty<CampusDailyOperationsReport>();
            IReadOnlyList<DateOnly> sentDates = enabled && !changed
                ? current.SentReportDates
                : Array.Empty<DateOnly>();
            WriteUnsafe(new State(CurrentSchemaVersion, enabled, enabledAt, pending.ToList(), sentDates.ToList()));
            return new CampusOperationsTelemetryPreference(enabled, enabledAt);
        }
    }

    public IReadOnlyList<CampusDailyOperationsReport> ReadPending()
    {
        lock (Gate) return ReadUnsafe().PendingReports.ToArray();
    }

    public void QueueReports(IEnumerable<CampusDailyOperationsReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        lock (Gate)
        {
            var current = ReadUnsafe();
            if (!current.Enabled || current.EnabledAtUtc is not { } enabledAt) return;
            var merged = current.PendingReports.ToDictionary(item => item.ReportDate);
            foreach (var report in reports)
            {
                if (!CampusOperationsTelemetrySummaryBuilder.IsValid(report) ||
                    report.ReportDate < CampusOperationsTelemetrySummaryBuilder.HongKongDate(enabledAt))
                    throw new InvalidDataException("待发送运维汇总不符合本地范围。");
                if (current.SentReportDates.Contains(report.ReportDate)) continue;
                merged[report.ReportDate] = report;
            }
            var pending = merged.OrderBy(item => item.Key)
                .TakeLast(CampusOperationsTelemetrySummaryBuilder.MaximumLookbackDays)
                .Select(item => item.Value).ToList();
            WriteUnsafe(current with { PendingReports = pending });
        }
    }

    public void MarkSent(DateOnly reportDate)
    {
        lock (Gate)
        {
            var current = ReadUnsafe();
            if (!current.Enabled) return;
            var pending = current.PendingReports.Where(item => item.ReportDate != reportDate).ToList();
            var alreadySent = current.SentReportDates.Contains(reportDate);
            var sentDates = current.SentReportDates.Append(reportDate).Distinct()
                .OrderBy(date => date)
                .TakeLast(CampusOperationsTelemetrySummaryBuilder.MaximumLookbackDays)
                .ToList();
            if (pending.Count != current.PendingReports.Count || !alreadySent)
                WriteUnsafe(current with { PendingReports = pending, SentReportDates = sentDates });
        }
    }

    private State ReadUnsafe()
    {
        var parent = Path.GetDirectoryName(_path) ?? throw new InvalidDataException("运维汇总目录无效。");
        Directory.CreateDirectory(parent);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("运维汇总目录不能是符号链接或重解析点。");
        if (!File.Exists(_path))
        {
            var initial = new State(CurrentSchemaVersion, false, null, [], []);
            WriteUnsafe(initial);
            return initial;
        }
        if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("运维汇总状态不能是符号链接或重解析点。");
        var info = new FileInfo(_path);
        if (info.Length is <= 0 or > MaximumStateBytes)
            throw new InvalidDataException("运维汇总状态大小无效。");
        State? state;
        try { state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(_path), JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("运维汇总状态格式无效。", exception); }
        var validated = state ?? throw new InvalidDataException("运维汇总状态为空。");
        if (validated.SentReportDates is null)
            validated = validated with { SentReportDates = [] };
        Validate(validated);
        return validated;
    }

    private void WriteUnsafe(State state)
    {
        Validate(state);
        var parent = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(parent);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (bytes.Length > MaximumStateBytes)
            throw new InvalidDataException("运维汇总状态超过大小上限。");
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
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
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Validate(State state)
    {
        if (state.SchemaVersion != CurrentSchemaVersion || state.PendingReports is null ||
            state.SentReportDates is null ||
            state.PendingReports.Count > CampusOperationsTelemetrySummaryBuilder.MaximumLookbackDays ||
            state.SentReportDates.Count > CampusOperationsTelemetrySummaryBuilder.MaximumLookbackDays ||
            state.Enabled != (state.EnabledAtUtc is not null) ||
            state.EnabledAtUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
            state.PendingReports.Any(report => !CampusOperationsTelemetrySummaryBuilder.IsValid(report)) ||
            state.SentReportDates.Distinct().Count() != state.SentReportDates.Count ||
            state.SentReportDates.Intersect(state.PendingReports.Select(report => report.ReportDate)).Any() ||
            state.PendingReports.Select(report => report.ReportDate).Distinct().Count() != state.PendingReports.Count)
            throw new InvalidDataException("运维汇总状态结构无效。");
    }

    private sealed record State(int SchemaVersion, bool Enabled, DateTimeOffset? EnabledAtUtc,
        List<CampusDailyOperationsReport> PendingReports, List<DateOnly> SentReportDates)
    {
        [JsonIgnore]
        public CampusOperationsTelemetryPreference Preference => new(Enabled, EnabledAtUtc);
    }
}

/// <summary>HTTPS client for opt-in Teacher daily operation summaries.</summary>
public sealed class CampusOperationsTelemetryClient
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Uri _endpoint;
    private readonly HttpClient _client;

    public CampusOperationsTelemetryClient()
        : this(DeploymentPackageApiConfiguration.GetApiBaseAddress(), SharedClient)
    {
    }

    public CampusOperationsTelemetryClient(Uri apiBaseAddress, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentNullException.ThrowIfNull(client);
        if ((apiBaseAddress.Scheme != Uri.UriSchemeHttps &&
             !(apiBaseAddress.Scheme == Uri.UriSchemeHttp && apiBaseAddress.IsLoopback)) ||
            !string.IsNullOrEmpty(apiBaseAddress.UserInfo) || !string.IsNullOrEmpty(apiBaseAddress.Query) ||
            !string.IsNullOrEmpty(apiBaseAddress.Fragment) || apiBaseAddress.AbsolutePath != "/")
            throw new InvalidDataException("运维汇总 API 必须使用 HTTPS 根地址；仅本机回环地址允许 HTTP。");
        _endpoint = new Uri(apiBaseAddress, "v1/telemetry/teacher/operations");
        _client = client;
    }

    public async Task UploadAsync(TeacherCampusHeartbeatState identity,
        CampusDailyOperationsReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.PackageId is not { } packageId || packageId == Guid.Empty ||
            identity.PublisherInstanceId is not { Length: 32 } ||
            !identity.PublisherInstanceId.All(Uri.IsHexDigit) ||
            !CampusOperationsTelemetrySummaryBuilder.IsValid(report))
            throw new InvalidDataException("运维汇总请求字段无效。");

        var publisherDailyToken = CreatePublisherDailyToken(identity.PublisherInstanceId, report.ReportDate);
        var request = new ReportRequest(publisherDailyToken, packageId,
            report.ReportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            report.UpdateSucceededCount, report.UpdatePartialCount, report.UpdateFailedCount,
            report.UpdateCancelledCount, report.StudentTargetSucceededCount,
            report.StudentTargetNeedsReviewCount, report.StudentTargetFailedCount,
            report.ClassroomSessionCount, report.FailureCounts);
        using var response = await _client.PostAsJsonAsync(_endpoint, request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.Accepted)
            throw new HttpRequestException("运维汇总服务暂不可用 (HTTP " + (int)response.StatusCode + ").");
    }

    private static string CreatePublisherDailyToken(string publisherInstanceId, DateOnly reportDate)
    {
        var key = Convert.FromHexString(publisherInstanceId);
        var message = Encoding.UTF8.GetBytes("VeyonCampus/OperationsReport/Publisher/v1\n" +
                                            reportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        try { return Convert.ToHexString(HMACSHA256.HashData(key, message)); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(message);
        }
    }

    private sealed record ReportRequest(
        string PublisherDailyToken,
        Guid PackageId,
        string ReportDate,
        int UpdateSucceededCount,
        int UpdatePartialCount,
        int UpdateFailedCount,
        int UpdateCancelledCount,
        int StudentTargetSucceededCount,
        int StudentTargetNeedsReviewCount,
        int StudentTargetFailedCount,
        int ClassroomSessionCount,
        Dictionary<string, int> FailureCounts);
}
