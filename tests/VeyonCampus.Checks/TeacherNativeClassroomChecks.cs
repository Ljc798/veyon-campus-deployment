using VeyonCampus.App;
using VeyonCampus.Core;

internal static class TeacherNativeClassroomChecks
{
    public static async Task RunAsync()
    {
        VerifyMachineIdentity();
        VerifyNativeRestoreTargets();
        await VerifyRestartRecoveryAsync().ConfigureAwait(false);
        await VerifyClassEndingWhileTeacherIsStoppedAsync().ConfigureAwait(false);
    }

    private static void VerifyMachineIdentity()
    {
        const string property = "  \"IOPlatformUUID\" = \"00112233-4455-6677-8899-aabbccddeeff\"";
        Expect(StudentInputGuardMachineIdentity.ParseMacPlatformUuid("+-o IOPlatformExpertDevice\n" +
            property + "\r\n}") == "00112233445566778899AABBCCDDEEFF", "Mac identity normalization");
        foreach (var invalid in new[]
                 {
                     "", property + "\n" + property, property + " trailing-data",
                     "\"IOPlatformUUID\" = \"00000000-0000-0000-0000-000000000000\"",
                     "\"IOPlatformUUID\" = \"not-a-uuid\"",
                     property.Replace("IOPlatformUUID", "IOPlatformUUIDBackup", StringComparison.Ordinal),
                     new string(' ', 8193) + property
                 })
        {
            var rejected = false;
            try { _ = StudentInputGuardMachineIdentity.ParseMacPlatformUuid(invalid); }
            catch (InvalidDataException) { rejected = true; }
            Expect(rejected, "Untrusted Mac identity output must fail closed");
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            var identity = StudentInputGuardMachineIdentity.ReadLocalMachineId();
            Expect(Guid.TryParseExact(identity, "N", out var parsed) && parsed != Guid.Empty &&
                identity == identity.ToUpperInvariant() &&
                StudentInputGuardMachineIdentity.ReadLocalMachineId() == identity,
                "The native host identity must be stable and protocol-compatible");
        }
    }

    private static void VerifyNativeRestoreTargets()
    {
        var room = new TeacherRoomProfile(Guid.NewGuid(), "同名机房", "LAB-", 3, 2, ["10.40.0.31", ""]);
        var campus = new TeacherCampusProfile(Guid.NewGuid(), "示范校区", [room]);
        var otherCampus = new TeacherCampusProfile(Guid.NewGuid(), "另一校区",
            [room with { HostOverrides = ["10.50.0.31", "10.50.0.32"] }]);
        var locations = new MacTeacherNetworkDirectory(() => [otherCampus, campus]).ReadLocations();
        var location = locations.Single(item => item.CampusProfileId == campus.ProfileId);
        Expect(location.RoomId == room.RoomId && location.Name == "示范校区 / 同名机房" &&
            location.Targets.SequenceEqual(["10.40.0.31", "LAB-04"]), "Native directory identity and hosts");
        Expect(MacTeacherNetworkDirectory.MatchesCurrentRoom(location, campus, room) &&
            MacTeacherNetworkDirectory.HasSameIdentity(location with { Name = "地点改名" }, campus, room),
            "Native classroom selection must use stable IDs instead of display labels");
        Expect(!MacTeacherNetworkDirectory.MatchesCurrentRoom(location, otherCampus,
                otherCampus.Rooms.Single()) &&
            !MacTeacherNetworkDirectory.MatchesCurrentRoom(location with { Targets = ["10.40.0.99"] }, campus, room) &&
            !MacTeacherNetworkDirectory.HasSameIdentity(location with { RoomId = null }, campus, room),
            "Native classroom selection must reject the wrong campus, stale targets, and missing IDs");
        var now = DateTimeOffset.UtcNow;
        var session = ClassroomSession.Start(campus, room.RoomId, now.AddHours(-1)).End(now);
        var restored = ClassroomRestoreLedger.MatchCurrentTargets(session, campus, room, locations);
        Expect(restored.Count == 2 && restored[0].TargetId == session.Targets[0].TargetId &&
            restored.Select(item => item.Target).SequenceEqual(["10.40.0.31", "LAB-04"]),
            "Restoration must select the original campus even when room IDs and names repeat");
        Expect(ClassroomRestoreLedger.MatchCurrentTargets(session, campus, room,
            [location with { Name = "显示名称已变化" }]).Count == 2, "Stable scope must survive display labels");
        foreach (var mismatched in new[]
                 {
                     location with { CampusProfileId = otherCampus.ProfileId, Name = room.DisplayName },
                     location with { RoomId = Guid.NewGuid(), Name = room.DisplayName },
                     location with { RoomId = null, Name = room.DisplayName }
                 })
            Expect(ClassroomRestoreLedger.MatchCurrentTargets(session, campus, room, [mismatched]).Count == 0,
                "An identified native location must never fall back to name matching");
        Expect(ClassroomRestoreLedger.MatchCurrentTargets(session, campus, room, [location, location]).Count == 0,
            "Ambiguous native locations must remain pending");
        Expect(ClassroomRestoreLedger.MatchCurrentTargets(session, campus,
            room with { HostOverrides = ["10.50.0.31", ""] }, locations).Count == 0,
            "A changed host binding must not redirect an old restoration");
        Expect(ClassroomRestoreLedger.MatchCurrentTargets(session, campus, room,
            [new VeyonNetworkLocation(room.DisplayName, location.Targets)]).Count == 2,
            "Windows Veyon name-based directory compatibility");
        Expect(TeacherClassroomQuickControlService.ResolveEndedRestoreSigningCampus("demo", null,
            ["another", "demo"]) == "demo", "Restoration must retain the recorded signing campus");
        Expect(TeacherClassroomQuickControlService.ResolveEndedRestoreSigningCampus("demo", "another",
            ["another", "demo"]) is null, "Conflicting signing context must remain pending");
        Expect(TeacherClassroomQuickControlService.ResolveEndedRestoreSigningCampus("demo", null,
            ["another"]) is null, "Restoration must never fall back to the currently selected or only available key");
    }

    private static async Task VerifyRestartRecoveryAsync()
    {
        var root = Path.Combine(TestPath.CanonicalTempRoot(), "native-classroom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var room = new TeacherRoomProfile(Guid.NewGuid(), "机房", "PC-", 1, 1, ["192.0.2.20"]);
            var campus = new TeacherCampusProfile(Guid.NewGuid(), "demo", [room]);
            new TeacherCampusDirectoryStore(Path.Combine(root, "campus-directory.json")).Save([campus]);
            var history = new ClassroomSessionStore(Path.Combine(root, "classroom-sessions.json"));
            var active = history.StartSession(campus, room.RoomId, now.AddHours(-1));
            history.EndSession(active.SessionId, now.AddMinutes(-30));
            var state = new ClassroomQuickControlStateStore(Path.Combine(root, "classroom-quick-control.json"));
            state.Save(new ClassroomQuickControlSessionState(ClassroomQuickControlStateStore.CurrentSchemaVersion,
                active.SessionId, "demo", room.RoomId, true, now, [new ClassroomQuickControlDeviceState(
                    active.Targets[0].TargetId, DeviceControlMode.None, ClassroomQuickControlPhase.NotStarted,
                    null, null, null, null, now, "尚未执行快捷管控。")]));
            ClassroomQuickControlDeviceState operation = state.Read(active.SessionId)!.Devices.Single();
            for (var revision = 0; revision < 3; revision++)
                operation = state.BeginOperation(active.SessionId, active.Targets[0].TargetId,
                    ClassroomQuickControlAction.EnableWebsiteRestriction, "teacher-command", "教师端应用策略。");
            var oldTaskId = operation.TaskId!.Value;
            var device = operation with
            {
                Mode = DeviceControlMode.WebsiteRestriction,
                WebsiteRevision = 71,
                PendingWebsiteRevision = 72,
                ExpiresUtc = now.AddMinutes(30),
                WebsiteExpiresUtc = now.AddMinutes(30),
                WebsitePhase = ClassroomQuickControlPhase.WebsiteApplying,
                UpdatedUtc = DateTimeOffset.UtcNow.AddSeconds(1),
                Detail = "Apply was interrupted by classroom end."
            };
            state.Save(state.Read(active.SessionId)! with { Devices = [device] });
            Guid? restoredTaskId = null;
            for (var restart = 0; restart < 2; restart++)
            {
                var unavailableDirectory = new UnavailableDirectory();
                await using (var service = new TeacherClassroomQuickControlService(() => "demo", root,
                                 unavailableDirectory))
                {
                    await unavailableDirectory.Called.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    var pending = state.Read(active.SessionId)!;
                    var target = pending.Devices.Single();
                    Expect(!pending.Active && target.DesiredRevision == 4 && target.TaskId != oldTaskId &&
                        target.DesiredAction == ClassroomQuickControlAction.Release &&
                        target.Phase == ClassroomQuickControlPhase.ReleasePending &&
                        target.WebsiteRevision == 71 && target.PendingWebsiteRevision == 72,
                        "Native restart must fence the interrupted Apply and retain an unconfirmed restoration");
                    if (restoredTaskId is null) restoredTaskId = target.TaskId;
                    else Expect(target.TaskId == restoredTaskId,
                        "Repeated restarts must reuse the original restoration task and Revision");
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyClassEndingWhileTeacherIsStoppedAsync()
    {
        var root = Path.Combine(TestPath.CanonicalTempRoot(), "native-classroom-ended-offline-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var room = new TeacherRoomProfile(Guid.NewGuid(), "机房", "PC-", 1, 1, ["192.0.2.21"]);
            var campus = new TeacherCampusProfile(Guid.NewGuid(), "demo", [room]);
            new TeacherCampusDirectoryStore(Path.Combine(root, "campus-directory.json")).Save([campus]);
            var history = new ClassroomSessionStore(Path.Combine(root, "classroom-sessions.json"));
            var session = history.StartSession(campus, room.RoomId, now.AddHours(-1));
            var state = new ClassroomQuickControlStateStore(Path.Combine(root, "classroom-quick-control.json"));
            state.Save(new ClassroomQuickControlSessionState(ClassroomQuickControlStateStore.CurrentSchemaVersion,
                session.SessionId, "demo", room.RoomId, true, now, [new ClassroomQuickControlDeviceState(
                    session.Targets[0].TargetId, DeviceControlMode.None, ClassroomQuickControlPhase.NotStarted,
                    null, null, null, null, now, "尚未执行快捷管控。")]));
            ClassroomQuickControlDeviceState operation = state.Read(session.SessionId)!.Devices.Single();
            for (var revision = 0; revision < 6; revision++)
                operation = state.BeginOperation(session.SessionId, session.Targets[0].TargetId,
                    ClassroomQuickControlAction.EnableWebsiteRestriction, "teacher-command", "教师端应用策略。");
            var oldTaskId = operation.TaskId!.Value;
            var device = operation with
            {
                Mode = DeviceControlMode.WebsiteRestriction,
                WebsiteRevision = 81,
                ExpiresUtc = now.AddMinutes(20),
                WebsiteExpiresUtc = now.AddMinutes(20),
                WebsitePhase = ClassroomQuickControlPhase.WebsiteApplied,
                Phase = ClassroomQuickControlPhase.WebsiteApplied,
                UpdatedUtc = DateTimeOffset.UtcNow.AddSeconds(1),
                Detail = "Policy was active before teacher shutdown."
            };
            state.Save(state.Read(session.SessionId)! with { Devices = [device] });

            await using (var firstStart = new TeacherClassroomQuickControlService(() => "demo", root,
                             new UnavailableDirectory()))
            {
                var paused = state.Read(session.SessionId)!;
                Expect(!paused.Active && paused.ClosedUtc is null,
                    "Restart recovery may pause the saved context without marking the class ended.");
            }

            history.EndSession(session.SessionId, now.AddMinutes(1));
            var unavailableDirectory = new UnavailableDirectory();
            await using (var restarted = new TeacherClassroomQuickControlService(() => "demo", root,
                             unavailableDirectory))
            {
                await unavailableDirectory.Called.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var ended = state.Read(session.SessionId)!;
                var target = ended.Devices.Single();
                Expect(!ended.Active && ended.ClosedUtc is not null && target.DesiredRevision == 7 &&
                    target.TaskId != oldTaskId && target.DesiredAction == ClassroomQuickControlAction.Release &&
                    target.Phase == ClassroomQuickControlPhase.ReleasePending,
                    "An ended class must create one durable release after the teacher was offline.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class UnavailableDirectory : ITeacherNetworkDirectory
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<VeyonNetworkLocation> ReadLocations()
        {
            Called.TrySetResult();
            throw new IOException("Fixture: native directory is temporarily unavailable.");
        }
    }

    private static void Expect(bool condition, string detail)
    {
        if (!condition) throw new InvalidDataException(detail);
    }
}
