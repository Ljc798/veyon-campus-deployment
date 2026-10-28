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
}
