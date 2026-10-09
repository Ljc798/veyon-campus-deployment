using System.Text;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class ClassroomSessionChecks
{
    public static void Run()
    {
        CheckSnapshotAndLifecycle();
        CheckStoreRetentionAndCorruptionHandling();
        CheckLinksAndConcurrentStarts();
        CheckSigningContextStore();
    }

    private static void CheckSnapshotAndLifecycle()
    {
        var profileId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var room = new TeacherRoomProfile(roomId, "三楼机房", "PC-", 1, 150,
            ["192.168.10.8", "teacher-pc.school.test"]);
        var campus = new TeacherCampusProfile(profileId, "示范校区", [room]);
        var localStart = new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.FromHours(8));

        var session = ClassroomSession.Start(campus, roomId, localStart);
        var anotherSession = ClassroomSession.Start(campus, roomId, localStart);
        Assert(session.SessionId != Guid.Empty && session.SessionId != anotherSession.SessionId);
        Assert(session.Room.CampusProfileId == profileId && session.Room.RoomId == roomId &&
               session.Room.CampusName == "示范校区" && session.Room.RoomName == "三楼机房");
        Assert(session.StartedAtUtc.Offset == TimeSpan.Zero && session.Status == ClassroomSessionStatus.Active &&
               session.EndedAtUtc is null && session.Targets.Length == 150 &&
               session.Targets[0].DeviceLabel == "PC-01" && session.Targets[^1].DeviceLabel == "PC-150");
        Assert(session.Targets.Select(target => target.TargetId).Distinct().Count() == 150 &&
               session.Targets.Select(target => target.TargetId)
                   .Intersect(anotherSession.Targets.Select(target => target.TargetId)).Any() == false);
        var json = System.Text.Json.JsonSerializer.Serialize(session);
        Assert(!json.Contains("192.168.10.8", StringComparison.Ordinal) &&
               !json.Contains("teacher-pc.school.test", StringComparison.Ordinal) &&
               !json.Contains("studentName", StringComparison.OrdinalIgnoreCase) &&
               !json.Contains("张三", StringComparison.Ordinal));

        var endedAtLocal = localStart.AddMinutes(45);
        var ended = session.End(endedAtLocal);
        var repeated = ended.End(endedAtLocal.AddHours(1));
        Assert(ended.Status == ClassroomSessionStatus.Ended && ended.EndedAtUtc?.Offset == TimeSpan.Zero &&
               ended.EndedAtUtc == repeated.EndedAtUtc);
        Reject(() => session.End(localStart.AddMinutes(-1)));
        Reject(() => ClassroomSession.Start(campus, Guid.NewGuid(), localStart));
        Reject(() => ClassroomSession.Start(campus with { Rooms = [room, room] }, roomId, localStart));

        var tooMany = room with { ComputerCount = 151 };
        Reject(() => ClassroomSession.Start(campus with { Rooms = [tooMany] }, roomId, localStart));
    }

    private static void CheckStoreRetentionAndCorruptionHandling()
    {
        var root = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-classroom-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "sessions.json");
            var store = new ClassroomSessionStore(path);
            var room = new TeacherRoomProfile(Guid.NewGuid(), "一楼机房", "LAB-", 3, 2);
            var campus = new TeacherCampusProfile(Guid.NewGuid(), "教学楼", [room]);
            Assert(store.ReadRecent().Count == 0 && store.ReadActive() is null);

            var origin = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            ClassroomSession? first = null;
            for (var index = 0; index < ClassroomSessionStore.MaximumEndedSessions + 2; index++)
            {
                var started = origin.AddHours(index * 2);
                var active = store.StartSession(campus, room.RoomId, started);
                first ??= active;
                Reject(() => new ClassroomSessionStore(path).StartSession(campus, room.RoomId, started));
                var ended = store.EndSession(active.SessionId, started.AddMinutes(40));
                Assert(ended is { Status: ClassroomSessionStatus.Ended });
                var repeated = store.EndSession(active.SessionId, started.AddHours(1));
                Assert(repeated?.EndedAtUtc == ended?.EndedAtUtc);
            }

            var recent = store.ReadRecent();
            Assert(recent.Count == ClassroomSessionStore.MaximumEndedSessions &&
                   recent.All(session => session.Status == ClassroomSessionStatus.Ended) &&
                   recent.All(session => session.SessionId != first!.SessionId) &&
                   recent[^1].StartedAtUtc == origin.AddHours((ClassroomSessionStore.MaximumEndedSessions + 1) * 2));
            var activeSession = store.StartSession(campus, room.RoomId, origin.AddDays(20));
            Assert(store.ReadActive()?.SessionId == activeSession.SessionId &&
                   store.ReadRecent().Count == ClassroomSessionStore.MaximumSessions);

            var snapshot = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(snapshot);
            Assert(text.Contains("schemaVersion", StringComparison.Ordinal) &&
                   text.Contains("deviceLabel", StringComparison.Ordinal) &&
                   !text.Contains("studentName", StringComparison.OrdinalIgnoreCase) &&
                   !text.Contains("targetHost", StringComparison.OrdinalIgnoreCase) &&
                   !text.Contains("192.168.", StringComparison.Ordinal));

            File.WriteAllText(path, "{");
            var corrupt = File.ReadAllBytes(path);
            Reject(() => store.ReadRecent());
            Reject(() => store.StartSession(campus, room.RoomId, origin.AddDays(21)));
            Assert(File.ReadAllBytes(path).SequenceEqual(corrupt));

            File.WriteAllText(path, "{\"schemaVersion\":1,\"sessions\":[],\"futureField\":true}");
            var unknownField = File.ReadAllBytes(path);
            Reject(() => store.ReadRecent());
            Assert(File.ReadAllBytes(path).SequenceEqual(unknownField));

            File.WriteAllText(path, "{\"schemaVersion\":1,\"schemaVersion\":1,\"sessions\":[]}");
            var duplicateField = File.ReadAllBytes(path);
            Reject(() => store.ReadRecent());
            Assert(File.ReadAllBytes(path).SequenceEqual(duplicateField));

            File.WriteAllBytes(path, new byte[ClassroomSessionStore.MaximumFileBytes + 1]);
            Reject(() => store.ReadRecent());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckLinksAndConcurrentStarts()
    {
        var root = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-classroom-session-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var room = new TeacherRoomProfile(Guid.NewGuid(), "二楼机房", "PC-", 1, 2);
            var campus = new TeacherCampusProfile(Guid.NewGuid(), "示范校区", [room]);
            var actualPath = Path.Combine(root, "actual.json");
            var seed = new ClassroomSessionStore(actualPath);
            var target = seed.StartSession(campus, room.RoomId, DateTimeOffset.UtcNow);
            var targetBytes = File.ReadAllBytes(actualPath);
            var linkPath = Path.Combine(root, "linked.json");
            File.CreateSymbolicLink(linkPath, actualPath);
            try
            {
                Reject(() => new ClassroomSessionStore(linkPath).ReadRecent());
                Assert(File.ReadAllBytes(actualPath).SequenceEqual(targetBytes));
            }
            finally
            {
                File.Delete(linkPath);
            }

            var concurrentPath = Path.Combine(root, "concurrent.json");
            var firstStore = new ClassroomSessionStore(concurrentPath);
            var secondStore = new ClassroomSessionStore(concurrentPath);
            using var barrier = new Barrier(2);
            var attempts = new[] { firstStore, secondStore }.Select(store => Task.Run(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    _ = store.StartSession(campus, room.RoomId, DateTimeOffset.UtcNow);
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            })).ToArray();
            Task.WaitAll(attempts);
            Assert(attempts.Count(task => task.Result) == 1 &&
                   firstStore.ReadRecent().Count(session => session.Status == ClassroomSessionStatus.Active) == 1);

            var lockedPath = Path.Combine(root, "locked.json");
            using (new FileStream(lockedPath + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                Reject(() => new ClassroomSessionStore(lockedPath).StartSession(campus, room.RoomId, DateTimeOffset.UtcNow));
            Assert(!File.Exists(lockedPath));
            Assert(target.SessionId != Guid.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckSigningContextStore()
    {
        var root = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-classroom-signing-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "context.json");
            var store = new ClassroomSigningContextStore(path);
            var sessionId = Guid.NewGuid();
            store.Save(sessionId, "campus-demo");
            Assert(store.Read(sessionId) == "campus-demo" && store.Read(Guid.NewGuid()) is null);
            Assert(!File.ReadAllText(path).Contains("PRIVATE KEY", StringComparison.Ordinal));
            Reject(() => store.Save(Guid.NewGuid(), " campus-demo"));
            store.Clear(Guid.NewGuid());
            Assert(store.Read(sessionId) == "campus-demo");
            store.Clear(sessionId);
            Assert(!File.Exists(path));

            File.WriteAllText(path, "{");
            var corrupt = File.ReadAllBytes(path);
            Reject(() => store.Read(sessionId));
            Assert(File.ReadAllBytes(path).SequenceEqual(corrupt));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException) { return; }
        throw new Exception("Invalid classroom session data was accepted.");
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new Exception("Classroom session assertion failed.");
    }
}
