using System.Net;
using System.Text.Json;
using VeyonCampus.Core;

internal static class TeacherHeartbeatChecks
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "veyon-teacher-heartbeat-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(temporaryDirectory, "heartbeat.json");
        try
        {
            var initial = TeacherCampusHeartbeatStateStore.LoadOrCreate(statePath);
            Expect(!initial.Enabled && initial.PackageId is null && initial.PublisherInstanceId.Length == 32);
            var state = initial with { Enabled = true, PackageId = Guid.NewGuid() };
            var today = TeacherCampusHeartbeatStateStore.GetHongKongDate();
            Expect(TeacherCampusHeartbeatStateStore.IsDue(state, today));
            Expect(!TeacherCampusHeartbeatStateStore.IsDue(state with { Enabled = false }, today));
            Expect(!TeacherCampusHeartbeatStateStore.IsDue(state with { PackageId = null }, today));
            TeacherCampusHeartbeatStateStore.Save(state, statePath);
            var handler = new FixtureHandler();
            using var httpClient = new HttpClient(handler);
            var client = new TeacherCampusHeartbeatClient(new Uri("https://heartbeat-fixture.invalid/"), httpClient);

            Expect(await client.TrySendOnceDailyAsync(state, "0.4.40", "0.4.41", 24, statePath));
            Expect(handler.RequestCount == 1 && handler.AuthorizationHeader is null);
            using (var request = JsonDocument.Parse(handler.RequestBody!))
            {
                var fields = request.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
                Expect(fields.SetEquals(new[]
                {
                    "publisherInstanceId", "packageId", "teacherVersion", "studentVersion", "configuredComputerCount"
                }));
                Expect(request.RootElement.GetProperty("configuredComputerCount").GetInt32() == 24);
            }

            var saved = TeacherCampusHeartbeatStateStore.LoadOrCreate(statePath);
            Expect(saved.LastSentDay == today && !TeacherCampusHeartbeatStateStore.IsDue(saved, today));
            Expect(TeacherCampusHeartbeatStateStore.IsDue(saved, today.AddDays(1)));
            Expect(!await client.TrySendOnceDailyAsync(saved, "0.4.40", "0.4.41", 24, statePath));
            Expect(handler.RequestCount == 1);
            await RejectAsync(() => client.TrySendOnceDailyAsync(saved, "0.4.40", "01.0.0", 24, statePath));

            var retryStatePath = Path.Combine(temporaryDirectory, "heartbeat-retry.json");
            TeacherCampusHeartbeatStateStore.Save(state, retryStatePath);
            var retryHandler = new FixtureHandler { ResponseStatusCode = HttpStatusCode.ServiceUnavailable };
            using var retryHttpClient = new HttpClient(retryHandler);
            var retryClient = new TeacherCampusHeartbeatClient(new Uri("https://heartbeat-fixture.invalid/"), retryHttpClient);
            var failed = false;
            try { _ = await retryClient.TrySendOnceDailyAsync(state, "0.4.40", "0.4.41", 24, retryStatePath); }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                failed = true;
            }
            var afterFailure = TeacherCampusHeartbeatStateStore.LoadOrCreate(retryStatePath);
            Expect(failed && afterFailure.LastSentDay is null &&
                   TeacherCampusHeartbeatStateStore.IsDue(afterFailure, today));
            retryHandler.ResponseStatusCode = HttpStatusCode.NoContent;
            Expect(await retryClient.TrySendOnceDailyAsync(afterFailure, "0.4.40", "0.4.41", 24, retryStatePath));
            Expect(retryHandler.RequestCount == 2);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Teacher heartbeat check failed.");
    }

    private static async Task RejectAsync(Func<Task<bool>> action)
    {
        try { _ = await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid Teacher heartbeat input was accepted.");
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? RequestBody { get; private set; }
        public string? AuthorizationHeader { get; private set; }
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.NoContent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            AuthorizationHeader = request.Headers.Authorization?.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(ResponseStatusCode);
        }
    }
}
