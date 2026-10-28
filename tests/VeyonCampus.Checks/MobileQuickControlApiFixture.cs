using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeyonCampus.Core;

internal sealed class MobileQuickControlApiFixture(ClassroomSession session)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private int _reads;
    private int _operations;

    public Task<ClassroomQuickControlSnapshot> ReadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Expect(sessionId == session.SessionId, "Only the active classroom may be read");
        Interlocked.Increment(ref _reads);
        return Task.FromResult(new ClassroomQuickControlSnapshot(sessionId, DateTimeOffset.UtcNow,
            [new ClassroomQuickControlTargetSnapshot(session.Targets[0].TargetId, "学生 08",
                DeviceControlMode.WebsiteRestriction, ClassroomQuickControlPhase.Unknown,
                DateTimeOffset.UtcNow, 41, null, null, "Fixture: waiting for a signed readback.")
            { WebsiteState = "needsReview" }], RoomName: session.Room.RoomName, TargetCount: session.Targets.Length));
    }

    public Task<ClassroomQuickControlOperationResult> ExecuteAsync(Guid sessionId, ClassroomQuickControlAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Expect(sessionId == session.SessionId && action == ClassroomQuickControlAction.Release,
            "Only the requested active classroom operation may reach the service");
        Interlocked.Increment(ref _operations);
        return Task.FromResult(new ClassroomQuickControlOperationResult(sessionId, action, DateTimeOffset.UtcNow,
            [new ClassroomQuickControlTargetResult(session.Targets[0].TargetId, "学生 08",
                DeviceControlMode.WebsiteRestriction, DeviceControlMode.None, DeviceControlMode.WebsiteRestriction,
                ClassroomQuickControlPhase.Unknown, false, true, 41, null,
                "Fixture: no trusted Agent readback; restoration remains pending.")], "等待可信回执。"));
    }

    public async Task VerifyAsync(HttpClient client, string origin, string token)
    {
        using (var request = Request(HttpMethod.Get, "/api/classroom/quick-control", origin, token))
        using (var response = await client.SendAsync(request).ConfigureAwait(false))
        {
            var snapshot = JsonSerializer.Deserialize<ClassroomQuickControlSnapshot>(
                await response.Content.ReadAsStringAsync().ConfigureAwait(false), JsonOptions);
            Expect(response.StatusCode == HttpStatusCode.OK && snapshot?.SessionId == session.SessionId &&
                snapshot.Devices.Single() is { Phase: ClassroomQuickControlPhase.Unknown, WebsiteState: "needsReview" },
                "Native mobile quick-control status must retain an unconfirmed device state");
        }
        using (var request = Request(HttpMethod.Get, "/api/classroom/quick-control", origin, "invalid-token"))
        using (var response = await client.SendAsync(request).ConfigureAwait(false))
            Expect(response.StatusCode == HttpStatusCode.Unauthorized && _reads == 1,
                "Unauthorized reads must not reach the classroom service");

        var validBody = new { sessionId = session.SessionId, action = "release" };
        using (var request = Request(HttpMethod.Post, "/api/classroom/quick-control", origin, token,
                   new { sessionId = Guid.NewGuid(), action = "release" }))
        using (var response = await client.SendAsync(request).ConfigureAwait(false))
            Expect(response.StatusCode == HttpStatusCode.Conflict && _operations == 0,
                "Stale classroom operations must be rejected before execution");
        using (var request = Request(HttpMethod.Post, "/api/classroom/quick-control", origin, "invalid-token", validBody))
        using (var response = await client.SendAsync(request).ConfigureAwait(false))
            Expect(response.StatusCode == HttpStatusCode.Unauthorized && _operations == 0,
                "Unauthorized operations must not reach the classroom service");
        using (var request = Request(HttpMethod.Post, "/api/classroom/quick-control", "https://untrusted.example",
                   token, validBody))
        using (var response = await client.SendAsync(request).ConfigureAwait(false))
            Expect(response.StatusCode == HttpStatusCode.Forbidden && _operations == 0,
                "Cross-origin operations must remain blocked on native hosts");
        using (var request = Request(HttpMethod.Post, "/api/classroom/quick-control", origin, token, validBody))
        using (var response = await client.SendAsync(request).ConfigureAwait(false))
        {
            var result = JsonSerializer.Deserialize<ClassroomQuickControlOperationResult>(
                await response.Content.ReadAsStringAsync().ConfigureAwait(false), JsonOptions);
            Expect(response.StatusCode == HttpStatusCode.OK && _operations == 1 &&
                result?.SessionId == session.SessionId && result.Results.Single() is
                    { Succeeded: false, NeedsReview: true, Phase: ClassroomQuickControlPhase.Unknown },
                "Native mobile operations must return per-device pending results rather than unsupported or success");
        }
        using (var request = Request(HttpMethod.Post, "/api/classroom/mode", origin, token, new { mode = "practice" }))
        using (var response = await client.SendAsync(request).ConfigureAwait(false))
            Expect(response.StatusCode == HttpStatusCode.Gone,
                "Removed legacy modes must remain disabled on every teacher platform");
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string origin, string token,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("X-Veyon-Request-Nonce", Guid.NewGuid().ToString("N"));
        request.Headers.TryAddWithoutValidation("X-Veyon-Request-Timestamp",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8,
                "application/json");
        return request;
    }

    private static void Expect(bool condition, string detail)
    {
        if (!condition) throw new InvalidDataException(detail);
    }
}
