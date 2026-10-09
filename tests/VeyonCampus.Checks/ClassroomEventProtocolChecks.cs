using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class ClassroomEventProtocolChecks
{
    public static void Run()
    {
        using var teacherKey = RSA.Create(2048);
        using var agentKey = RSA.Create(2048);
        using var strangerKey = RSA.Create(2048);
        var teacherPem = teacherKey.ExportSubjectPublicKeyInfoPem();
        var agentPem = agentKey.ExportSubjectPublicKeyInfoPem();
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        const string campus = "campus-demo";
        const string target = "PC-08";
        var endpoint = ClassroomEventCryptography.CreateTeacherEndpoint(IPAddress.Parse("192.168.1.10"));
        var token = ClassroomEventCryptography.CreateAccessToken();
        var grant = new ClassroomEventAccessGrant(1, ClassroomEventCryptography.GrantPurpose, campus,
            Guid.NewGuid(), sessionId, target, endpoint, new string('A', 64), token, now, now.AddMinutes(2));
        var signedGrant = ClassroomEventCryptography.SignGrant(grant, teacherKey);
        Expect(ClassroomEventCryptography.VerifyGrant(signedGrant, campus, teacherPem, sessionId, target,
            now.AddSeconds(1)) == grant);

        var grantState = new ClassroomEventGrantState();
        grantState.Apply(signedGrant, campus, teacherPem, sessionId, target, now.AddSeconds(1));
        Expect(grantState.Read(campus, sessionId, now.AddSeconds(2)) == grant);
        Reject(() => grantState.Apply(signedGrant, campus, teacherPem, sessionId, target, now.AddSeconds(3)));
        Expect(grantState.Read("other-campus", sessionId, now.AddSeconds(2)) is null);
        Expect(grantState.Read(campus, Guid.NewGuid(), now.AddSeconds(2)) is null);
        Expect(grantState.Read(campus, sessionId, now.AddMinutes(2)) is null);
        Expect(grantState.Clear(sessionId));
        Expect(grantState.Read(campus, sessionId, now.AddSeconds(2)) is null);
        Reject(() => grantState.Apply(signedGrant, campus, teacherPem, sessionId, target, now.AddSeconds(3)));
        var renewedGrant = grant with
        {
            MessageId = Guid.NewGuid(),
            AccessToken = ClassroomEventCryptography.CreateAccessToken(),
            IssuedUtc = now.AddSeconds(10),
            ExpiresUtc = now.AddSeconds(10).AddMinutes(2)
        };
        var renewedSignedGrant = ClassroomEventCryptography.SignGrant(renewedGrant, teacherKey);
        Expect(grantState.Apply(renewedSignedGrant, campus, teacherPem, sessionId, target,
            now.AddSeconds(11)) == renewedGrant);

        Reject(() => ClassroomEventCryptography.VerifyGrant(signedGrant, "other-campus", teacherPem,
            sessionId, target, now.AddSeconds(1)));
        Reject(() => ClassroomEventCryptography.VerifyGrant(signedGrant, campus, teacherPem,
            Guid.NewGuid(), target, now.AddSeconds(1)));
        Reject(() => ClassroomEventCryptography.VerifyGrant(signedGrant, campus, teacherPem,
            sessionId, "PC-09", now.AddSeconds(1)));
        Reject(() => ClassroomEventCryptography.SignGrant(grant with { TeacherEndpoint = "https://example.com:39176/" }, teacherKey));
        Reject(() => ClassroomEventCryptography.SignGrant(grant with { AccessToken = "short" }, teacherKey));
        Reject(() => ClassroomEventCryptography.SignGrant(grant with { ServerCertificateSha256 = "bad" }, teacherKey));
        Reject(() => ClassroomEventCryptography.SignGrant(grant with { ExpiresUtc = now.AddMinutes(3) }, teacherKey));
        using (var envelope = JsonDocument.Parse(signedGrant))
        {
            var payload = envelope.RootElement.GetProperty("payload").GetString()!;
            var signature = envelope.RootElement.GetProperty("signature").GetString()!;
            var publicKey = envelope.RootElement.GetProperty("publicKeyPem").GetString()!;
            Reject(() => ClassroomEventCryptography.VerifyGrant(
                JsonSerializer.Serialize(new { payload, signature, publicKeyPem = strangerKey.ExportSubjectPublicKeyInfoPem() }),
                campus, teacherPem, sessionId, target, now.AddSeconds(1)));
            Reject(() => ClassroomEventCryptography.VerifyGrant(
                $"{{\"payload\":\"{payload}\",\"payload\":\"{payload}\",\"signature\":\"{signature}\",\"publicKeyPem\":\"{publicKey.Replace("\n", "\\n")}\"}}",
                campus, teacherPem, sessionId, target, now.AddSeconds(1)));
        }

        var request = CreateEvent(now, sessionId, target, ClassroomEventSender.Student,
            ClassroomEventType.HelpRequested, ClassroomHelpReason.Error, "程序运行错误", null);
        var signedRequest = ClassroomEventCryptography.SignEvent(request, agentKey);
        var verifiedRequest = ClassroomEventCryptography.VerifyEvent(signedRequest, campus, sessionId,
            target, ClassroomEventSender.Student, agentPem, now.AddSeconds(1));
        Expect(verifiedRequest.Event == request && verifiedRequest.MatchesPinnedKey &&
               verifiedRequest.Fingerprint == StudentAgentResponseCryptography.GetFingerprint(agentPem));
        Reject(() => ClassroomEventCryptography.VerifyEvent(signedRequest, "other-campus", sessionId,
            target, ClassroomEventSender.Student, agentPem, now.AddSeconds(1)));
        Reject(() => ClassroomEventCryptography.VerifyEvent(signedRequest, campus, Guid.NewGuid(),
            target, ClassroomEventSender.Student, agentPem, now.AddSeconds(1)));
        Reject(() => ClassroomEventCryptography.VerifyEvent(signedRequest, campus, sessionId,
            "PC-09", ClassroomEventSender.Student, agentPem, now.AddSeconds(1)));
        Reject(() => ClassroomEventCryptography.VerifyEvent(signedRequest, campus, sessionId,
            target, ClassroomEventSender.Student, teacherPem, now.AddSeconds(1)));
        Reject(() => ClassroomEventCryptography.VerifyEvent(signedRequest, campus, sessionId,
            target, ClassroomEventSender.Teacher, agentPem, now.AddSeconds(1)));
        Reject(() => ClassroomEventCryptography.VerifyEvent(signedRequest, campus, sessionId,
            target, ClassroomEventSender.Student, null, now.AddMinutes(2)));
        Reject(() => ClassroomEventCryptography.SignEvent(request with { Message = new string('x', 501) }, agentKey));
        Reject(() => ClassroomEventCryptography.SignEvent(request with { Type = ClassroomEventType.TeacherReply }, agentKey));
        Reject(() => ClassroomEventCryptography.SignEvent(request with { Message = "x\u0001y" }, agentKey));

        var reply = CreateEvent(now.AddSeconds(2), sessionId, target, ClassroomEventSender.Teacher,
            ClassroomEventType.TeacherReply, null, "先检查循环边界。", request.EventId);
        var signedReply = ClassroomEventCryptography.SignEvent(reply, teacherKey);
        var verifiedReply = ClassroomEventCryptography.VerifyEvent(signedReply, campus, sessionId,
            target, ClassroomEventSender.Teacher, teacherPem, now.AddSeconds(3));
        Expect(verifiedReply.Event == reply && verifiedReply.MatchesPinnedKey);
        Reject(() => ClassroomEventCryptography.VerifyEvent(signedReply, campus, sessionId,
            target, ClassroomEventSender.Teacher, strangerKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(3)));

        var buffer = new ClassroomEventBuffer();
        Expect(buffer.Append(request, now.AddSeconds(1)));
        Expect(!buffer.Append(request, now.AddSeconds(2)));
        Expect(buffer.Append(reply, now.AddSeconds(3)));
        var otherTargetEvent = CreateEvent(now.AddSeconds(4), sessionId, "PC-09", ClassroomEventSender.Student,
            ClassroomEventType.HelpRequested, ClassroomHelpReason.NeedExplanation, null, null);
        Expect(buffer.Append(otherTargetEvent, now.AddSeconds(5)));
        var studentPage = buffer.ReadAfter(sessionId, target, 0, 10, now.AddSeconds(4));
        Expect(studentPage.Events.Count == 2 && studentPage.Cursor == 3 &&
               studentPage.Events[0] == request && studentPage.Events[1] == reply);
        var firstPage = buffer.ReadAfter(sessionId, null, 0, 1, now.AddSeconds(4));
        Expect(firstPage.Events.Count == 1 && firstPage.Cursor == 1);
        var secondPage = buffer.ReadAfter(sessionId, null, firstPage.Cursor, 10, now.AddSeconds(4));
        Expect(secondPage.Events.Count == 2 && secondPage.Cursor == 3 &&
               secondPage.Events[0] == reply && secondPage.Events[1] == otherTargetEvent);
        var targetPage = buffer.ReadAfter(sessionId, target, 0, 10, now.AddSeconds(4));
        Expect(targetPage.Events.Count == 2 && targetPage.Cursor == 3);
        Expect(buffer.ReadAfter(sessionId, "PC-10", 0, 10, now.AddSeconds(4)).Events.Count == 0);
        Reject(() => buffer.ReadAfter(sessionId, target, -1, 10, now.AddSeconds(4)));
        Reject(() => buffer.ReadAfter(sessionId, target, 0, ClassroomEventBuffer.MaximumPageSize + 1,
            now.AddSeconds(4)));
        Expect(buffer.Count(sessionId) == 3);
        buffer.ClearSession(sessionId);
        Expect(buffer.Count(sessionId) == 0 && buffer.ReadAfter(sessionId, null, 0, 10,
            now.AddSeconds(5)).Events.Count == 0);

        var boundedBuffer = new ClassroomEventBuffer();
        for (var index = 0; index < ClassroomEventBuffer.MaximumEventsPerSession; index++)
        {
            var buffered = CreateEvent(now, sessionId, target, ClassroomEventSender.Student,
                ClassroomEventType.HelpRequested, ClassroomHelpReason.Other, null, null);
            Expect(boundedBuffer.Append(buffered, now));
        }
        Expect(boundedBuffer.Count(sessionId) == ClassroomEventBuffer.MaximumEventsPerSession);
        var overflow = CreateEvent(now, sessionId, target, ClassroomEventSender.Student,
            ClassroomEventType.HelpRequested, ClassroomHelpReason.Other, null, null);
        Reject(() => boundedBuffer.Append(overflow, now));
        Expect(boundedBuffer.ReadAfter(sessionId, null, 0, ClassroomEventBuffer.MaximumPageSize,
            now.Add(ClassroomEventCryptography.EventRetention + TimeSpan.FromSeconds(1))).Events.Count == 0);

        var waitingSession = Guid.NewGuid();
        var waiting = boundedBuffer.WaitForEventsAsync(waitingSession, target, 0, 10,
            ClassroomEventBuffer.MaximumWait, DateTimeOffset.UtcNow);
        var wakeEvent = CreateEvent(DateTimeOffset.UtcNow, waitingSession, target,
            ClassroomEventSender.Student, ClassroomEventType.HelpRequested, ClassroomHelpReason.Error,
            null, null);
        Expect(boundedBuffer.Append(wakeEvent, DateTimeOffset.UtcNow));
        var wokenPage = waiting.GetAwaiter().GetResult();
        Expect(wokenPage.Events.Count == 1 && wokenPage.Events[0] == wakeEvent);
        var timedOut = boundedBuffer.WaitForEventsAsync(Guid.NewGuid(), target, 0, 10,
            TimeSpan.FromMilliseconds(10), DateTimeOffset.UtcNow).GetAwaiter().GetResult();
        Expect(timedOut.Events.Count == 0);

        Expect(ClassroomEventCryptography.IsPrivateIpv4Address(IPAddress.Parse("10.4.5.6")));
        Expect(ClassroomEventCryptography.IsPrivateIpv4Address(IPAddress.Parse("172.31.0.1")));
        Expect(ClassroomEventCryptography.IsPrivateIpv4Address(IPAddress.Parse("192.168.0.1")));
        Expect(!ClassroomEventCryptography.IsPrivateIpv4Address(IPAddress.Parse("172.32.0.1")));
        Expect(!ClassroomEventCryptography.IsPrivateIpv4Address(IPAddress.Parse("127.0.0.1")));
        Expect(!ClassroomEventCryptography.IsPrivateIpv4Address(IPAddress.Parse("8.8.8.8")));
    }

    private static ClassroomEvent CreateEvent(DateTimeOffset now, Guid sessionId, string target,
        ClassroomEventSender sender, ClassroomEventType type, ClassroomHelpReason? reason,
        string? message, Guid? correlationId) =>
        new(1, ClassroomEventCryptography.EventPurpose, "campus-demo", sessionId, Guid.NewGuid(), target,
            sender, type, now.ToUniversalTime(), now.ToUniversalTime().AddMinutes(2), reason, message,
            correlationId);

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Classroom event protocol assertion failed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Classroom event protocol accepted an invalid message.");
    }
}
