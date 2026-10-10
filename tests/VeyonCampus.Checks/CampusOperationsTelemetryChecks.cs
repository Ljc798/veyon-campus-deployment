using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class CampusOperationsTelemetryChecks
{
    public static void Run()
    {
        CheckConsentAndDailySummary();
        CheckQueueIsBoundedByConsentAndIdempotent();
        CheckClientSendsOnlyFixedAggregateFields();
    }

    private static void CheckConsentAndDailySummary()
    {
        var enabledAt = new DateTimeOffset(2026, 10, 9, 6, 0, 0, TimeSpan.Zero);
        var beforeOptIn = Entry(new DateTimeOffset(2026, 10, 9, 5, 0, 0, TimeSpan.Zero),
            UpdateDiagnosticOutcome.Failed, "UPDATE_LOCAL_IO");
        var succeeded = Entry(new DateTimeOffset(2026, 10, 9, 7, 0, 0, TimeSpan.Zero),
            UpdateDiagnosticOutcome.Succeeded, "UPDATE_SUCCEEDED");
        var partial = Entry(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero),
            UpdateDiagnosticOutcome.Partial, "UPDATE_PARTIAL");
        var failed = Entry(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero),
            UpdateDiagnosticOutcome.Failed, "UPDATE_NETWORK", UpdateDiagnosticOperation.StudentRollout,
            new UpdateDiagnosticCounts(3, 1, 2));
        var cancelled = Entry(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero),
            UpdateDiagnosticOutcome.Cancelled, "UPDATE_CANCELLED");
        var started = Entry(new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero),
            UpdateDiagnosticOutcome.Started, "UPDATE_HANDOFF_STARTED");
        var incompleteToday = Entry(new DateTimeOffset(2026, 10, 10, 16, 30, 0, TimeSpan.Zero),
            UpdateDiagnosticOutcome.Succeeded, "UPDATE_SUCCEEDED");

        var room = new TeacherRoomProfile(Guid.NewGuid(), "教室 1", "PC-", 1, 2);
        var campus = new TeacherCampusProfile(Guid.NewGuid(), "测试校区", [room]);
        var sessionAfterOptIn = ClassroomSession.Start(campus, room.RoomId,
            new DateTimeOffset(2026, 10, 9, 8, 30, 0, TimeSpan.Zero));
        var sessionBeforeOptIn = ClassroomSession.Start(campus, room.RoomId,
            new DateTimeOffset(2026, 10, 9, 5, 30, 0, TimeSpan.Zero));

        var reports = CampusOperationsTelemetrySummaryBuilder.Build(
            [beforeOptIn, succeeded, partial, failed, cancelled, started, incompleteToday],
            [sessionBeforeOptIn, sessionAfterOptIn], enabledAt, new DateOnly(2026, 10, 11));
        Assert(reports.Count == 1 && reports[0].ReportDate == new DateOnly(2026, 10, 9));
        var report = reports[0];
        Assert(report.UpdateSucceededCount == 1 && report.UpdatePartialCount == 1 &&
               report.UpdateFailedCount == 1 && report.UpdateCancelledCount == 1);
        Assert(report.StudentTargetSucceededCount == 3 && report.StudentTargetNeedsReviewCount == 1 &&
               report.StudentTargetFailedCount == 2 && report.ClassroomSessionCount == 1);
        Assert(report.FailureCounts.Count == 2 && report.FailureCounts["UPDATE_PARTIAL"] == 1 &&
               report.FailureCounts["UPDATE_NETWORK"] == 1);
        Assert(CampusOperationsTelemetrySummaryBuilder.HongKongDate(
                   new DateTimeOffset(2026, 10, 10, 16, 30, 0, TimeSpan.Zero)) == new DateOnly(2026, 10, 11));
        Assert(CampusOperationsTelemetrySummaryBuilder.IsValid(report));
        Assert(!CampusOperationsTelemetrySummaryBuilder.IsValid(report with
            { FailureCounts = new Dictionary<string, int> { ["PRIVATE_LOG"] = 1 } }));
        Assert(!CampusOperationsTelemetrySummaryBuilder.IsValid(report with { UpdateFailedCount = 20001 }));
    }

    private static void CheckQueueIsBoundedByConsentAndIdempotent()
    {
        var root = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-operations-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "operations.json");
            var store = new CampusOperationsTelemetryStore(path);
            Assert(!store.LoadPreference().Enabled);
            var report = new CampusDailyOperationsReport(new DateOnly(2026, 10, 9),
                1, 1, 0, 0, 2, 0, 0, 1,
                new Dictionary<string, int> { ["UPDATE_PARTIAL"] = 1 });
            store.QueueReports([report]);
            Assert(store.ReadPending().Count == 0);

            var enabledAt = new DateTimeOffset(2026, 10, 9, 6, 0, 0, TimeSpan.Zero);
            Assert(store.SetEnabled(true, enabledAt).Enabled);
            store.QueueReports([report]);
            store.QueueReports([report with { ClassroomSessionCount = 2 }]);
            Assert(store.ReadPending().Count == 1 && store.ReadPending()[0].ClassroomSessionCount == 2);
            var serialized = awaitFileRead(path);
            Assert(!serialized.Contains("publisherInstanceId", StringComparison.OrdinalIgnoreCase) &&
                   !serialized.Contains("studentName", StringComparison.OrdinalIgnoreCase) &&
                   !serialized.Contains("private-log", StringComparison.OrdinalIgnoreCase));
            store.MarkSent(report.ReportDate);
            Assert(store.ReadPending().Count == 0);
            store.QueueReports([report]);
            store.QueueReports([report with { ClassroomSessionCount = 2 }]);
            Assert(store.ReadPending().Count == 0);
            store.SetEnabled(false);
            Assert(!store.LoadPreference().Enabled && store.ReadPending().Count == 0);

            var corruptedPath = Path.Combine(root, "corrupted.json");
            File.WriteAllText(corruptedPath, "{not-json}");
            try
            {
                _ = new CampusOperationsTelemetryStore(corruptedPath).LoadPreference();
                throw new InvalidOperationException("Corrupt telemetry state was accepted.");
            }
            catch (InvalidDataException) { }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckClientSendsOnlyFixedAggregateFields()
    {
        RunClientCheckAsync().GetAwaiter().GetResult();
    }

    private static async Task RunClientCheckAsync()
    {
        var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new CampusOperationsTelemetryClient(new Uri("https://operations.example/"), httpClient);
        var state = new TeacherCampusHeartbeatState(true, Guid.NewGuid(),
            "0123456789abcdef0123456789abcdef", null);
        var report = new CampusDailyOperationsReport(new DateOnly(2026, 10, 9),
            1, 0, 1, 0, 3, 1, 2, 1,
            new Dictionary<string, int> { ["UPDATE_NETWORK"] = 1 });
        await client.UploadAsync(state, report);
        Assert(handler.RequestUri?.AbsolutePath == "/v1/telemetry/teacher/operations" &&
               handler.Authorization is null);
        using var payload = JsonDocument.Parse(handler.RequestBody ?? "{}");
        var properties = payload.RootElement.EnumerateObject().Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert(properties.SetEquals(new[]
        {
            "publisherDailyToken", "packageId", "reportDate", "updateSucceededCount",
            "updatePartialCount", "updateFailedCount", "updateCancelledCount", "studentTargetSucceededCount",
            "studentTargetNeedsReviewCount", "studentTargetFailedCount", "classroomSessionCount", "failureCounts"
        }));
        Assert(payload.RootElement.GetProperty("reportDate").GetString() == "2026-10-09");
        var localPublisherId = Convert.FromHexString("0123456789abcdef0123456789abcdef");
        var tokenInput = Encoding.UTF8.GetBytes("VeyonCampus/OperationsReport/Publisher/v1\n2026-10-09");
        var expectedDailyToken = Convert.ToHexString(HMACSHA256.HashData(localPublisherId, tokenInput));
        CryptographicOperations.ZeroMemory(localPublisherId);
        CryptographicOperations.ZeroMemory(tokenInput);
        Assert(payload.RootElement.GetProperty("publisherDailyToken").GetString() == expectedDailyToken);
        Assert(!handler.RequestBody!.Contains("publisherInstanceId", StringComparison.OrdinalIgnoreCase));
        Assert(!handler.RequestBody!.Contains("studentName", StringComparison.OrdinalIgnoreCase) &&
               !handler.RequestBody.Contains("hostName", StringComparison.OrdinalIgnoreCase) &&
               !handler.RequestBody.Contains("log", StringComparison.OrdinalIgnoreCase));

        try
        {
            _ = new CampusOperationsTelemetryClient(new Uri("http://operations.example/"), httpClient);
            throw new InvalidOperationException("An insecure remote operations endpoint was accepted.");
        }
        catch (InvalidDataException) { }
    }

    private static UpdateDiagnosticEntry Entry(DateTimeOffset occurredAtUtc,
        UpdateDiagnosticOutcome outcome, string code,
        UpdateDiagnosticOperation operation = UpdateDiagnosticOperation.DownloadAndInstall,
        UpdateDiagnosticCounts? counts = null) =>
        new(1, occurredAtUtc, Guid.NewGuid(), UpdateDiagnosticModule.TeacherConsole, operation,
            outcome, code, "0.4.50", null,
            outcome is UpdateDiagnosticOutcome.Failed ? UpdateDiagnosticSeverity.Error : UpdateDiagnosticSeverity.Warning,
            false, counts);

    private static string awaitFileRead(string path) => File.ReadAllText(path);

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Campus operations telemetry check failed.");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("{\"accepted\":true}")
            };
        }
    }
}
