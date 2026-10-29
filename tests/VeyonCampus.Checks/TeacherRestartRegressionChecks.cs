using System.Net;
using System.Net.Http;
using VeyonCampus.App;
using VeyonCampus.Core;
using VeyonCampus.Checks;

internal static class TeacherRestartRegressionChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(TestPath.CanonicalTempRoot(), "teacher-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckSavedSeatMapRecovery(Path.Combine(root, "saved-map"));
            var room = new TeacherRoomProfile(Guid.NewGuid(), "教室一", "PC-", 1, 2,
                ["192.0.2.51", "192.0.2.52"]);
            var other = new TeacherRoomProfile(Guid.NewGuid(), "教室二", "LAB-", 3, 1, ["192.0.2.53"]);
            var campus = new TeacherCampusProfile(Guid.NewGuid(), "demo", [room, other]);
            var campuses = new TeacherCampusDirectoryStore(Path.Combine(root, "campus-directory.json"));
            campuses.Save([campus]);
            var seats = new ClassroomSeatGridStore(Path.Combine(root, "seats.json"));
            seats.SaveSeat(campus.ProfileId, room.RoomId, 1, 1, "学生甲", 1);
            seats.SaveSeat(campus.ProfileId, room.RoomId, 1, 2, "学生乙", 2);
            var history = new ClassroomSessionStore(Path.Combine(root, "classroom-sessions.json"));
            var session = history.StartSession(campus, room.RoomId, DateTimeOffset.UtcNow);
            var model = new TeacherViewModel(campusDirectoryStore: campuses, classroomSeatGridStore: seats,
                classroomSessionStore: history,
                classroomModeStateStore: new ClassroomModeStateStore(Path.Combine(root, "modes.json")),
                classroomCountdownStore: new ClassroomCountdownStore(Path.Combine(root, "countdown.json")),
                classroomTaskProgressStore: new ClassroomTaskProgressStore(Path.Combine(root, "tasks.json")),
                roomSelectionStore: new TeacherRoomSelectionStore(Path.Combine(root, "selection.json")),
                websitePolicyDraftDirectory: Path.Combine(root, "drafts"));
            Expect(model.WebsitePolicyTargetChoices.Select(item => item.Target).SequenceEqual(room.HostOverrides!),
                "Restoring the selected room must populate the shared policy roster without visiting another page.");
            Expect(model.WebsitePolicyTargetChoices.Select(item => item.StudentName)
                    .SequenceEqual(["学生甲", "学生乙"]) && model.DeviceHealthRows.Count == 2,
                "The policy and health pages must share restored students and device addresses.");
            model.SelectPage("updates");
            Expect(model.HasDeviceHealthTargets && model.DeviceHealthRows.Count == 2,
                "Opening devices first must retain its roster.");
            model.SelectedRoomProfile = other;
            Expect(model.WebsitePolicyTargetChoices.Single().Target == "192.0.2.53" &&
                   model.DeviceHealthRows.Single().Target == "192.0.2.53",
                "Switching rooms must replace the policy and health scopes together.");
            model.SelectedRoomProfile = room;

            var now = DateTimeOffset.UtcNow;
            var deadline = now.AddMinutes(17);
            var targetId = session.Targets[0].TargetId;
            var state = new ClassroomQuickControlStateStore(Path.Combine(root, "classroom-quick-control.json"));
            state.Save(new ClassroomQuickControlSessionState(ClassroomQuickControlStateStore.CurrentSchemaVersion,
                session.SessionId, "demo", room.RoomId, true, now,
                [new ClassroomQuickControlDeviceState(targetId, DeviceControlMode.None,
                    ClassroomQuickControlPhase.NotStarted, null, null, null, null, now, "尚未启用")])
                { ControlExpiresUtc = deadline });
            var operation = state.BeginOperation(session.SessionId, targetId, ClassroomQuickControlAction.EnableWebsiteRestriction,
                "teacher-command", "保存期望状态");
            state.Save(state.Read(session.SessionId)! with { Devices = [operation with
            {
                Mode = DeviceControlMode.WebsiteRestriction, Phase = ClassroomQuickControlPhase.WebsiteApplied,
                WebsitePhase = ClassroomQuickControlPhase.WebsiteApplied, WebsiteRevision = 41,
                ExpiresUtc = deadline, WebsiteExpiresUtc = deadline
            }] });
            await using var service = new TeacherClassroomQuickControlService(() => "demo", root);
            var restored = state.Read(session.SessionId)!;
            Expect(restored.ControlExpiresUtc == deadline && !restored.Active && restored.ClosedUtc is null,
                "A process restart must retain the original deadline and classroom ownership.");
            var context = new TeacherClassroomEventContext("demo", session.SessionId, ["192.0.2.51"],
                new Dictionary<string, Guid> { ["192.0.2.51"] = targetId }, session,
                new Dictionary<string, string> { ["192.0.2.51"] = "学生甲" });
            var device = restored.Devices.Single();
            state.UpdateRetry(session.SessionId, targetId, device.DesiredRevision, device.TaskId!.Value, now);
            var waiting = service.ReadSavedSnapshot(context);
            Expect(waiting.Devices.Single().WebsiteState == "needsReview",
                "Backoff alone must not turn an unverified owned website policy into waitingForAgent.");
            var row = new TeacherClassroomQuickControlDeviceItem(waiting.Devices.Single());
            model.UpdateClassroomQuickControlSnapshot(waiting);
            var summaryBeforeRetry = model.ClassroomQuickControlProgressSummary;
            var statusBeforeRetry = row.StatusText;
            Expect(statusBeforeRetry.Contains("需核对") && summaryBeforeRetry.Contains("需核对 1"),
                "The device row and aggregate must retain review status during retry backoff.");
            var pending = state.Read(session.SessionId)!;
            state.Save(pending with { Devices = [pending.Devices.Single() with { RetryAfterUtc = now.AddSeconds(-1) }] });
            var due = service.ReadSavedSnapshot(context);
            Expect(due.Devices.Single().WebsiteState == waiting.Devices.Single().WebsiteState,
                "A retry becoming due must not flicker the displayed policy state.");
            model.UpdateClassroomQuickControlSnapshot(due);
            row.Update(due.Devices.Single());
            Expect(row.StatusText == statusBeforeRetry && model.ClassroomQuickControlProgressSummary == summaryBeforeRetry,
                "Neither per-device nor aggregate status may flicker when a retry becomes due.");
            Expect(model.HasCurrentClassroomQuickControlDeadline &&
                   model.CurrentClassroomQuickControlCountdownMinutes is >= 16 and <= 17 &&
                   model.CurrentClassroomQuickControlDeadlineText.Contains(deadline.ToLocalTime().ToString("HH:mm")),
                "The old deadline must remain visible while restored devices need review.");
            model.SetClassroomQuickControlSessionNotice("暂时无法核对连接");
            Expect(model.HasCurrentClassroomQuickControlDeadline && model.HasClassroomQuickControlDevices,
                "A transient notice must not erase the current session's countdown or device state.");
            var summaryRefreshed = false;
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(model.ClassroomQuickControlProgressSummary)) summaryRefreshed = true;
            };
            model.UpdateClassroomQuickControlSnapshot(due);
            Expect(summaryRefreshed && !model.ClassroomQuickControlProgressSummary.Contains("暂时无法核对连接"),
                "A successful unchanged readback must clear the transient connection notice.");
            model.UpdateClassroomQuickControlSnapshot(due with { ControlExpiresUtc = now.AddSeconds(-1) });
            Expect(!model.HasCurrentClassroomQuickControlDeadline &&
                   model.CurrentClassroomQuickControlCountdownMinutes == 0,
                "An expired deadline must stop the countdown rather than restart a default duration.");

            UpdateDiagnosticsChecks.Run();
            foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway })
            {
                var failure = UpdateDiagnosticCatalog.Classify(new HttpRequestException("private", null, status));
                Expect(failure.Retryable && failure.Guidance.Contains($"HTTP {(int)status}") &&
                       !failure.Guidance.Contains("检查网络"), "Service errors must not be blamed on client connectivity.");
            }
            Expect(!UpdateDiagnosticCatalog.Classify(new HttpRequestException("private", null,
                HttpStatusCode.Forbidden)).Retryable, "Access denials must not invite endless retries.");
            Console.WriteLine("PASS restored student roster, room switching, durable deadline, stable retry state and update HTTP diagnostics");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void CheckSavedSeatMapRecovery(string root)
    {
        Directory.CreateDirectory(root);
        var hosts = new List<string> { "192.0.2.81", "192.0.2.82" };
        var originalRoom = new TeacherRoomProfile(Guid.NewGuid(), "原机房", "PC-", 1, 2, hosts, [1, 2]);
        var duplicateRoom = originalRoom with { RoomId = Guid.NewGuid(), ComputerNumbers = [3, 2] };
        var original = new TeacherCampusProfile(Guid.NewGuid(), "原校区", [originalRoom]);
        var duplicate = new TeacherCampusProfile(Guid.NewGuid(), "新校区", [duplicateRoom]);
        var campuses = new TeacherCampusDirectoryStore(Path.Combine(root, "campuses.json"));
        campuses.Save([duplicate, original]);
        var seats = new ClassroomSeatGridStore(Path.Combine(root, "seats.json"));
        seats.SaveSeat(original.ProfileId, originalRoom.RoomId, 2, 3, "学生甲", 1);
        var saved = seats.SaveSeat(original.ProfileId, originalRoom.RoomId, 4, 5, "学生乙", 2);
        var savedFile = File.ReadAllBytes(Path.Combine(root, "seats.json"));
        TeacherViewModel CreateModel() => new(campusDirectoryStore: campuses, classroomSeatGridStore: seats,
            classroomSessionStore: new ClassroomSessionStore(Path.Combine(root, "sessions.json")),
            classroomModeStateStore: new ClassroomModeStateStore(Path.Combine(root, "modes.json")),
            classroomCountdownStore: new ClassroomCountdownStore(Path.Combine(root, "countdown.json")),
            classroomTaskProgressStore: new ClassroomTaskProgressStore(Path.Combine(root, "tasks.json")),
            websitePolicyDraftDirectory: Path.Combine(root, "drafts"));
        var model = CreateModel();
        Expect(model.SelectedCampusProfile?.ProfileId == duplicate.ProfileId && model.SeatGridSeatCount == 0,
            "The fixture must reproduce selecting an empty duplicate of a saved room.");
        Expect(!model.ShowRoomLocationSettings,
            "Duplicate local profiles for the same physical location must not expose multiple-location settings.");
        var location = new VeyonNetworkLocation(originalRoom.DisplayName, hosts.AsEnumerable().Reverse().ToArray());
        var locationsProperty = typeof(TeacherViewModel).GetProperty(nameof(TeacherViewModel.WebsiteLocations))!;
        locationsProperty.SetValue(model, new[] { location });
        Expect(!model.ShowRoomLocationSettings, "One Veyon location must hide the entire location settings block.");
        locationsProperty.SetValue(model, new[] { location, new VeyonNetworkLocation("另一机房", ["192.0.2.83"]) });
        Expect(model.ShowRoomLocationSettings, "Multiple Veyon locations must retain location settings.");
        locationsProperty.SetValue(model, new[] { location });
        model.ApplyClassroomVeyonLocation(location);
        Expect(model.SelectedCampusProfile?.ProfileId == original.ProfileId &&
               model.SelectedRoomProfile?.RoomId == originalRoom.RoomId &&
               model.SeatGridSeatCount == 2 && model.SeatGridNamedCount == 2,
            "Reading the same Veyon hosts must recover the original saved map rather than the empty duplicate.");
        Expect(model.SelectedRoomProfile!.GetComputerNumbers().SequenceEqual([2, 1]),
            "Saved computer numbers must follow their hosts even when the Veyon host order changes.");
        Expect(File.ReadAllBytes(Path.Combine(root, "seats.json")).SequenceEqual(savedFile) &&
               seats.GetLayout(original.ProfileId, originalRoom.RoomId).Seats.SequenceEqual(saved.Seats),
            "Recovering a saved room must preserve every seat ID, position, name and number without rewriting the map.");
        var restarted = CreateModel();
        restarted.ApplyClassroomVeyonLocation(location);
        Expect(restarted.SelectedRoomProfile?.RoomId == originalRoom.RoomId && restarted.SeatGridNamedCount == 2,
            "The saved map must remain recoverable after another process restart.");
        var activeSessions = new ClassroomSessionStore(Path.Combine(root, "active-sessions.json"));
        var activeSession = activeSessions.StartSession(duplicate, duplicateRoom.RoomId, DateTimeOffset.UtcNow);
        var activeModel = new TeacherViewModel(campusDirectoryStore: campuses,
            classroomSeatGridStore: seats, classroomSessionStore: activeSessions,
            classroomModeStateStore: new ClassroomModeStateStore(Path.Combine(root, "active-modes.json")),
            classroomCountdownStore: new ClassroomCountdownStore(Path.Combine(root, "active-countdown.json")),
            classroomTaskProgressStore: new ClassroomTaskProgressStore(Path.Combine(root, "active-tasks.json")),
            websitePolicyDraftDirectory: Path.Combine(root, "active-drafts"));
        var resolveNames = typeof(TeacherViewModel).GetMethod("ResolveClassroomStudentDisplayNames",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var activeNames = (IReadOnlyDictionary<string, string>)resolveNames.Invoke(activeModel,
            [activeSession, hosts])!;
        Expect(activeSession.Room.CampusProfileId == duplicate.ProfileId && activeModel.SeatGridSeatCount == 0 &&
               activeNames[hosts[0]] == "学生甲" && activeNames[hosts[1]] == "学生乙",
            "An active session pinned to an empty duplicate profile must still label quick-control targets from the matching saved map.");
        model.SelectedCampusProfile = duplicate;
        model.ApplyClassroomVeyonLocation(new VeyonNetworkLocation(originalRoom.DisplayName,
            ["192.0.2.91", "192.0.2.92"]));
        Expect(model.SelectedCampusProfile?.ProfileId == duplicate.ProfileId && model.SeatGridSeatCount == 0,
            "A same-named room with different hosts must not borrow another room's student map.");
        campuses.Save([duplicate, original]);
        seats.SaveSeat(duplicate.ProfileId, duplicateRoom.RoomId, 1, 1, "另一学生", 3);
        model = CreateModel();
        model.ApplyClassroomVeyonLocation(location);
        Expect(model.SelectedCampusProfile?.ProfileId == duplicate.ProfileId && model.SeatGridSeatCount == 1,
            "A deliberately selected populated map must not be replaced by another saved map.");
        var empty = new TeacherCampusProfile(Guid.NewGuid(), "待确认校区",
            [duplicateRoom with { RoomId = Guid.NewGuid() }]);
        campuses.Save([empty, duplicate, original]);
        model = CreateModel();
        locationsProperty.SetValue(model, new[] { location });
        var directoryBeforeAmbiguousRead = File.ReadAllBytes(Path.Combine(root, "campuses.json"));
        model.ApplyClassroomVeyonLocation(location);
        Expect(model.HasClassroomDirectoryError && model.ShowRoomLocationSettings &&
               model.SelectedCampusProfile?.ProfileId == empty.ProfileId &&
               File.ReadAllBytes(Path.Combine(root, "campuses.json")).SequenceEqual(directoryBeforeAmbiguousRead),
            "Ambiguous saved maps must retain the selection controls and preserve the directory for manual selection.");
        Console.WriteLine("PASS single-location settings, duplicate profile recovery, saved seats and stable host numbering");
    }
}
