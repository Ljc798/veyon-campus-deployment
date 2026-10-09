using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace VeyonCampus.Core;

public sealed record ClassroomEventRemotePage(long Cursor, IReadOnlyList<string> Events);
public sealed record ClassroomEventSubmitResponse(bool Accepted, bool Duplicate);
public sealed record StudentAgentClassroomEventPage(bool Ready, Guid? SessionId, long Cursor,
    IReadOnlyList<ClassroomEvent> Events);
public sealed record StudentAgentClassroomEventSubmission(bool Accepted, Guid SessionId, Guid EventId,
    DateTimeOffset SubmittedUtc);

/// <summary>Student Agent relay to the one HTTPS endpoint named by its current signed grant.</summary>
public static class ClassroomEventTransport
{
    private const int MaximumRemoteResponseBytes = 1024 * 1024;
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PollTimeout = ClassroomEventBuffer.MaximumWait + TimeSpan.FromSeconds(3);

    public static async Task<StudentAgentClassroomEventSubmission> SubmitHelpRequestAsync(
        ClassroomEventAccessGrant grant, RSA agentIdentityKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(agentIdentityKey);
        var now = DateTimeOffset.UtcNow;
        ClassroomEventCryptography.ValidateGrant(grant, grant.CampusId, grant.SessionId, grant.Target, now);
        var classroomEvent = new ClassroomEvent(1, ClassroomEventCryptography.EventPurpose, grant.CampusId,
            grant.SessionId, Guid.NewGuid(), grant.Target, ClassroomEventSender.Student,
            ClassroomEventType.HelpRequested, now, now.Add(ClassroomEventCryptography.MaximumEventLifetime),
            ClassroomHelpReason.NeedHelp, null, null);
        var signedEvent = ClassroomEventCryptography.SignEvent(classroomEvent, agentIdentityKey);
        var endpoint = new Uri(grant.TeacherEndpoint);
        var uri = new UriBuilder(endpoint)
        {
            Path = "/api/classroom/events/student",
            Query = ""
        }.Uri;
        using var handler = CreatePinnedHandler(grant);
        using var client = new HttpClient(handler) { Timeout = SubmitTimeout };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(signedEvent, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
        request.Headers.TryAddWithoutValidation("Origin", endpoint.GetLeftPart(UriPartial.Authority));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var json = Encoding.UTF8.GetString(await ReadBoundedAsync(response.Content, MaximumRemoteResponseBytes,
            cancellationToken).ConfigureAwait(false));
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("教师端尚未收到课堂求助。", null, response.StatusCode);
        PolicyJson.RejectDuplicateFields(Encoding.UTF8.GetBytes(json));
        var result = JsonSerializer.Deserialize<ClassroomEventSubmitResponse>(json,
                         ApplicationPolicyCompiler.JsonOptions)
                     ?? throw new InvalidDataException("教师端课堂求助回执为空。");
        if (!result.Accepted)
            throw new InvalidDataException("教师端未接受本次课堂求助。");
        return new StudentAgentClassroomEventSubmission(true, grant.SessionId, classroomEvent.EventId,
            DateTimeOffset.UtcNow);
    }

    public static async Task<StudentAgentClassroomEventPage> ReadTeacherEventsAsync(
        ClassroomEventAccessGrant grant, long afterCursor, string teacherPublicKeyPem,
        RSA agentIdentityKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(agentIdentityKey);
        if (afterCursor < 0) throw new InvalidDataException("课堂事件读取游标无效。");
        var now = DateTimeOffset.UtcNow;
        ClassroomEventCryptography.ValidateGrant(grant, grant.CampusId, grant.SessionId, grant.Target, now);
        var endpoint = new Uri(grant.TeacherEndpoint);
        var uri = new UriBuilder(endpoint)
        {
            Path = "/api/classroom/events/student",
            Query = "after=" + afterCursor.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }.Uri;
        using var handler = CreatePinnedHandler(grant);
        using var client = new HttpClient(handler) { Timeout = PollTimeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.AccessToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, MaximumRemoteResponseBytes, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("教师端课堂事件暂时不可用。", null, response.StatusCode);
        PolicyJson.RejectDuplicateFields(bytes);
        var page = JsonSerializer.Deserialize<ClassroomEventRemotePage>(bytes,
                       ApplicationPolicyCompiler.JsonOptions)
                   ?? throw new InvalidDataException("教师端课堂事件页为空。");
        if (page.Cursor < afterCursor || page.Events is null || page.Events.Count > ClassroomEventBuffer.MaximumPageSize)
            throw new InvalidDataException("教师端课堂事件游标或分页无效。");

        var teacherEvents = new List<ClassroomEvent>(page.Events.Count);
        foreach (var signedJson in page.Events)
        {
            VerifiedClassroomEvent verified;
            try
            {
                verified = ClassroomEventCryptography.VerifyEvent(signedJson, grant.CampusId, grant.SessionId,
                    grant.Target, ClassroomEventSender.Teacher, teacherPublicKeyPem, DateTimeOffset.UtcNow);
            }
            catch (InvalidDataException)
            {
                var agentPublicKeyPem = agentIdentityKey.ExportSubjectPublicKeyInfoPem();
                verified = ClassroomEventCryptography.VerifyEvent(signedJson, grant.CampusId, grant.SessionId,
                    grant.Target, ClassroomEventSender.Student, agentPublicKeyPem, DateTimeOffset.UtcNow);
                continue;
            }
            teacherEvents.Add(verified.Event);
        }
        return new StudentAgentClassroomEventPage(true, grant.SessionId, page.Cursor,
            Array.AsReadOnly(teacherEvents.ToArray()));
    }

    private static HttpClientHandler CreatePinnedHandler(ClassroomEventAccessGrant grant) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            IsPinnedCertificate(certificate, grant.ServerCertificateSha256, DateTimeOffset.UtcNow)
    };

    public static bool IsPinnedCertificate(X509Certificate2? certificate, string expectedSha256,
        DateTimeOffset nowUtc)
    {
        if (certificate is null || expectedSha256.Length != 64) return false;
        try
        {
            var expected = Convert.FromHexString(expectedSha256);
            var actual = SHA256.HashData(certificate.RawData);
            return certificate.NotBefore.ToUniversalTime() <= nowUtc.ToUniversalTime().UtcDateTime &&
                   certificate.NotAfter.ToUniversalTime() > nowUtc.ToUniversalTime().UtcDateTime &&
                   CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("教师端课堂事件响应超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("教师端课堂事件响应超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
