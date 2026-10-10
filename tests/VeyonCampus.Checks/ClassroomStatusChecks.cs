using System.Security.Cryptography;
using System.Text.Json;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class ClassroomStatusChecks
{
    public static void Run()
    {
        using var teacherKey = RSA.Create(2048);
        using var strangerKey = RSA.Create(2048);
        using var agentKey = RSA.Create(2048);
        Expect(ClassroomStatusLocalEndpointPolicy.Allows(System.Net.IPAddress.Loopback));
        Expect(ClassroomStatusLocalEndpointPolicy.Allows(System.Net.IPAddress.IPv6Loopback));
        Expect(!ClassroomStatusLocalEndpointPolicy.Allows(System.Net.IPAddress.Parse("192.168.1.20")));
        Expect(!ClassroomStatusLocalEndpointPolicy.Allows(null));
        var now = DateTimeOffset.UtcNow;
        var session = CreateSession(now);
        var active = ClassroomStatusCryptography.Create("campus-demo", session, now);
        var signed = ClassroomStatusCryptography.Sign(active, teacherKey);
        var verified = ClassroomStatusCryptography.Verify(signed, "campus-demo",
            teacherKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(1));
        Expect(verified == active && verified.Active && verified.RoomName == "机房 A" && verified.TargetCount == 2);
        var modeCommand = ClassroomStatusCryptography.Create("campus-demo", session, now.AddSeconds(2),
            ClassroomMode.Practice);
        var modeSigned = ClassroomStatusCryptography.Sign(modeCommand, teacherKey);
        var verifiedMode = ClassroomStatusCryptography.Verify(modeSigned, "campus-demo",
            teacherKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(3));
        Expect(verifiedMode.SchemaVersion == 2 && verifiedMode.Mode == ClassroomMode.Practice);
        using (var envelope = JsonDocument.Parse(signed))
        {
            var payload = envelope.RootElement.GetProperty("payload").GetString()!;
            var signature = envelope.RootElement.GetProperty("signature").GetString()!;
            var signatureBytes = signature.ToCharArray();
            signatureBytes[0] = signatureBytes[0] == 'A' ? 'B' : 'A';
            Reject(() => ClassroomStatusCryptography.Verify(JsonSerializer.Serialize(new
            {
                payload,
                signature = new string(signatureBytes)
            }), "campus-demo", teacherKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(1)));
            Reject(() => ClassroomStatusCryptography.Verify(
                $"{{\"payload\":\"{payload}\",\"payload\":\"{payload}\",\"signature\":\"{signature}\"}}",
                "campus-demo", teacherKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(1)));
            Reject(() => ClassroomStatusCryptography.Verify(JsonSerializer.Serialize(new
            {
                payload,
                signature,
                extra = true
            }), "campus-demo", teacherKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(1)));
        }

        Reject(() => ClassroomStatusCryptography.Verify(signed, "another-campus",
            teacherKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(1)));
        Reject(() => ClassroomStatusCryptography.Verify(signed, "campus-demo",
            strangerKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(1)));
        Reject(() => ClassroomStatusCryptography.Verify(signed, "campus-demo",
            teacherKey.ExportSubjectPublicKeyInfoPem(), now.Add(ClassroomStatusCryptography.MaximumLifetime)));
        Reject(() => ClassroomStatusCryptography.Sign(active with { Purpose = "VeyonCampus.WebsitePolicy.v1" }, teacherKey));
        Reject(() => ClassroomStatusCryptography.Sign(active with { TargetCount = 0 }, teacherKey));
        Reject(() => ClassroomStatusCryptography.Sign(active with { RoomName = "机房\nA" }, teacherKey));
        Reject(() => ClassroomStatusCryptography.Sign(modeCommand with { Mode = (ClassroomMode)99 }, teacherKey));

        var state = new ClassroomStatusAgentState();
        state.Apply(signed, "campus-demo", teacherKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(1));
        var snapshot = state.Read("campus-demo", now.AddSeconds(2));
        Expect(snapshot.Connected && snapshot.SessionId == session.SessionId && snapshot.RoomName == "机房 A" &&
               snapshot.TargetCount == 2);
        state.Apply(modeSigned, "campus-demo", teacherKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(3));
        Expect(state.Read("campus-demo", now.AddSeconds(4)).Mode == ClassroomMode.Practice);
        Reject(() => state.Apply(modeSigned, "campus-demo", teacherKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(5)));
        Expect(!state.Read("campus-demo", now.Add(ClassroomStatusCryptography.MaximumLifetime +
            TimeSpan.FromSeconds(3))).Connected);
        Expect(!new ClassroomStatusAgentState().Read("campus-demo", now.AddSeconds(3)).Connected);

        var ended = ClassroomStatusCryptography.Create("campus-demo", null, now.AddSeconds(4));
        var endedSigned = ClassroomStatusCryptography.Sign(ended, teacherKey);
        state.Apply(endedSigned, "campus-demo", teacherKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(5));
        var endedSnapshot = state.Read("campus-demo", now.AddSeconds(6));
        Expect(endedSnapshot.Connected && endedSnapshot.SessionId is null && endedSnapshot.RoomName is null &&
               endedSnapshot.TargetCount == 0);

        var acknowledgement = ClassroomStatusCryptography.SignAcknowledgement(ended, endedSigned,
            now.AddSeconds(5), agentKey);
        var verifiedAcknowledgement = ClassroomStatusCryptography.VerifyAcknowledgement(acknowledgement,
            ended, endedSigned, agentKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(6));
        Expect(verifiedAcknowledgement.MatchesPinnedKey &&
               verifiedAcknowledgement.Payload.MessageId == ended.MessageId);
        Reject(() => ClassroomStatusCryptography.VerifyAcknowledgement(acknowledgement, active,
            signed, agentKey.ExportSubjectPublicKeyInfoPem(), now.AddSeconds(6)));

        var path = Path.Combine(TestPath.CanonicalTempRoot(),
            "veyon-classroom-mode-" + Guid.NewGuid().ToString("N"), "state.json");
        var store = new ClassroomModeStateStore(path);
        var ownership = new ClassroomPolicyOwnership(session.Targets[0].TargetId,
            ClassroomPolicyKind.Website, 7, Guid.NewGuid());
        store.Save(new ClassroomModeSessionState(1, session.SessionId, ClassroomMode.Practice, true,
            now, [ownership]));
        var reloaded = new ClassroomModeStateStore(path).Read(session.SessionId);
        Expect(reloaded is { Active: true, Mode: ClassroomMode.Practice } &&
               reloaded.OwnedPolicies.Single() == ownership);
        Reject(() => store.Save(reloaded! with { OwnedPolicies = [ownership, ownership] }));
        store.Save(reloaded! with { Active = false });
        Expect(new ClassroomModeStateStore(path).Read(session.SessionId) is { Active: false });
        store.Remove(session.SessionId);
        Expect(store.Read(session.SessionId) is null);

        var restoreRoom = new TeacherRoomProfile(Guid.NewGuid(), "机房 B", "LAB-", 3, 2,
            ["10.40.0.31", "10.40.0.32"]);
        var restoreCampus = new TeacherCampusProfile(Guid.NewGuid(), "示范校区", [restoreRoom]);
        var endedSession = ClassroomSession.Start(restoreCampus, restoreRoom.RoomId, now).End(now.AddMinutes(40));
        var currentLocation = new VeyonNetworkLocation("机房 B", ["10.40.0.31", "10.40.0.32"]);
        var mapped = ClassroomRestoreLedger.MatchCurrentTargets(endedSession, restoreCampus, restoreRoom,
            [currentLocation]);
        Expect(mapped.Count == 2 && mapped[0].DeviceLabel == "LAB-03" &&
               mapped[0].TargetId == endedSession.Targets[0].TargetId && mapped[0].Target == "10.40.0.31");
        Expect(ClassroomRestoreLedger.MatchCurrentTargets(endedSession, restoreCampus, restoreRoom with
            { ComputerCount = 3 }, [currentLocation]).Count == 0);
        Expect(ClassroomRestoreLedger.MatchCurrentTargets(endedSession, restoreCampus, restoreRoom,
            [currentLocation, currentLocation]).Count == 0);
        Expect(ClassroomRestoreLedger.MatchCurrentTargets(endedSession, restoreCampus, restoreRoom,
            [new VeyonNetworkLocation("机房 B", ["10.40.0.31"])]).Count == 1);

        var pendingState = new ClassroomModeSessionState(1, endedSession.SessionId, ClassroomMode.Normal, false,
            now.AddMinutes(40), [new ClassroomPolicyOwnership(endedSession.Targets[0].TargetId,
                ClassroomPolicyKind.Website, 8, Guid.NewGuid()), new ClassroomPolicyOwnership(
                endedSession.Targets[1].TargetId, ClassroomPolicyKind.Application, 9, Guid.NewGuid())]);
        var pending = ClassroomRestoreLedger.ListPending([pendingState], [endedSession]);
        Expect(pending.Count == 2 && pending[0].RoomName == "机房 B" &&
               pending[0].DeviceLabel == "LAB-03" && pending[1].DeviceLabel == "LAB-04");
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ClassroomSession CreateSession(DateTimeOffset now)
    {
        var room = new TeacherRoomProfile(Guid.NewGuid(), "机房 A", "PC-", 1, 2);
        var campus = new TeacherCampusProfile(Guid.NewGuid(), "示范校区", [room]);
        return ClassroomSession.Start(campus, room.RoomId, now);
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Classroom status assertion failed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Classroom status protocol accepted an invalid message.");
    }
}
