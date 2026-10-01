using System.Net;
using System.Security.Cryptography;
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
            Expect(initial.Enabled && initial.PackageId is null && initial.PublisherInstanceId.Length == 32);
            TeacherCampusHeartbeatStateStore.SaveUserPreference(initial with { Enabled = false }, statePath);
            Expect(!TeacherCampusHeartbeatStateStore.LoadOrCreate(statePath).Enabled);
            TeacherCampusHeartbeatStateStore.SaveUserPreference(initial with { Enabled = true }, statePath);
            Expect(TeacherCampusHeartbeatStateStore.LoadOrCreate(statePath).Enabled);

            var legacyStatePath = Path.Combine(temporaryDirectory, "heartbeat-legacy.json");
            await File.WriteAllTextAsync(legacyStatePath,
                "{\"enabled\":false,\"packageId\":null,\"publisherInstanceId\":\"0123456789abcdef0123456789abcdef\",\"lastSentDay\":null}");
            Expect(TeacherCampusHeartbeatStateStore.LoadOrCreate(legacyStatePath).Enabled);
            var state = initial with { Enabled = true, PackageId = Guid.NewGuid() };
            var today = TeacherCampusHeartbeatStateStore.GetHongKongDate();
            Expect(TeacherCampusHeartbeatStateStore.IsDue(state, today));
            Expect(!TeacherCampusHeartbeatStateStore.IsDue(state with { Enabled = false }, today));
            Expect(!TeacherCampusHeartbeatStateStore.IsDue(state with { PackageId = null }, today));
            TeacherCampusHeartbeatStateStore.Save(state, statePath);
            using var signingKey = RSA.Create(2048);
            const string currentVersion = "0.4.40";
            var apiBase = new Uri("https://heartbeat-fixture.invalid/");
            var latestTeacherRelease = SignRelease(apiBase, signingKey, ApplicationReleaseRole.TeacherConsole,
                "0.4.41");
            var latestStudentRelease = SignRelease(apiBase, signingKey, ApplicationReleaseRole.StudentSetup,
                "0.4.42");
            var handler = new FixtureHandler
            {
                ResponseBody = JsonSerializer.Serialize(new
                {
                    latestReleases = new TeacherCampusLatestReleases(latestTeacherRelease, latestStudentRelease)
                })
            };
            using var httpClient = new HttpClient(handler);
            var client = new TeacherCampusHeartbeatClient(apiBase, httpClient);

            var heartbeat = await client.TrySendOnceDailyAsync(state, currentVersion, currentVersion, 24, statePath);
            Expect(heartbeat is not null && heartbeat.LatestReleases.TeacherConsole?.Manifest.Version == "0.4.41" &&
                   heartbeat.LatestReleases.StudentSetup?.Manifest.Version == "0.4.42");
            var successfulHeartbeat = heartbeat ?? throw new InvalidOperationException("Heartbeat response is missing.");
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
            Expect(await client.TrySendOnceDailyAsync(saved, currentVersion, currentVersion, 24, statePath) is null);
            Expect(handler.RequestCount == 1);
            await RejectAsync(() => client.TrySendOnceDailyAsync(saved, currentVersion, "01.0.0", 24, statePath));

            using var releaseHttpClient = new HttpClient(new RejectRequestsHandler());
            var releaseClient = new ApplicationReleaseClient(apiBase, releaseHttpClient,
                signingKey.ExportSubjectPublicKeyInfoPem());
            Expect(releaseClient.EvaluateLatest(successfulHeartbeat.LatestReleases.TeacherConsole,
                       ApplicationReleaseRole.TeacherConsole, currentVersion).IsNewer);
            Expect(!releaseClient.EvaluateLatest(successfulHeartbeat.LatestReleases.TeacherConsole,
                ApplicationReleaseRole.TeacherConsole, "0.4.41").IsNewer);
            Expect(releaseClient.EvaluateLatest(successfulHeartbeat.LatestReleases.StudentSetup,
                ApplicationReleaseRole.StudentSetup, currentVersion).IsNewer);
            var modifiedRelease = successfulHeartbeat.LatestReleases.TeacherConsole! with
            {
                Manifest = successfulHeartbeat.LatestReleases.TeacherConsole!.Manifest with
                {
                    Version = "0.4.43",
                    FileName = "VeyonCampus-Teacher-Setup-0.4.43-win-x64.exe"
                }
            };
            try
            {
                _ = releaseClient.EvaluateLatest(modifiedRelease, ApplicationReleaseRole.TeacherConsole,
                    currentVersion);
                throw new InvalidOperationException("An unsigned version change was accepted.");
            }
            catch (InvalidDataException) { }

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
            var retryResult = await retryClient.TrySendOnceDailyAsync(afterFailure, currentVersion,
                currentVersion, 24, retryStatePath);
            Expect(retryResult is not null && retryResult.LatestReleases.TeacherConsole is null &&
                   retryResult.LatestReleases.StudentSetup is null);
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

    private static ApplicationReleaseEnvelope SignRelease(Uri apiBase, RSA key,
        ApplicationReleaseRole role, string version)
    {
        var teacherRole = role == ApplicationReleaseRole.TeacherConsole;
        var releaseId = Guid.NewGuid();
        var manifest = new ApplicationReleaseManifest(
            1,
            teacherRole ? "VeyonCampus.TeacherConsole" : "VeyonCampus.StudentSetup",
            teacherRole ? "TeacherConsole" : "StudentSetup",
            version,
            "win-x64",
            $"VeyonCampus-{(teacherRole ? "Teacher" : "Student")}-Setup-{version}-win-x64.exe",
            256,
            new string('A', 64),
            new Uri(apiBase, $"v1/releases/{releaseId:D}/artifact").AbsoluteUri);
        var signature = key.SignData(ApplicationReleaseClient.CreateCanonicalPayload(manifest),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new(manifest, "RSA-PSS-SHA256", Convert.ToBase64String(signature), DateTimeOffset.UtcNow);
    }

    private static async Task RejectAsync(Func<Task<TeacherCampusHeartbeatResult?>> action)
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
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;
        public string? ResponseBody { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            AuthorizationHeader = request.Headers.Authorization?.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(ResponseStatusCode);
            if (ResponseStatusCode != HttpStatusCode.NoContent)
                response.Content = new StringContent(ResponseBody ?? "{}");
            return response;
        }
    }

    private sealed class RejectRequestsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No HTTP request was expected during release verification.");
    }
}
