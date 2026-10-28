using System.Text.Json;
using VeyonCampus.App;
using VeyonCampus.Core;

internal static class ClassroomQuickControlRevisionChecks
{
    public static void Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory,
            "quick-control-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var path = Path.Combine(root, "classroom-quick-control.json");
            var sessionId = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var roomId = Guid.NewGuid();
            var expires = now.AddMinutes(20);
            var reapplyExpires = now.AddMinutes(45);
            Expect(TeacherClassroomQuickControlService.ResolveControlExpiry(reapplyExpires, expires, now) ==
                   reapplyExpires);
            var initialDevice = new ClassroomQuickControlDeviceState(targetId, DeviceControlMode.None,
                ClassroomQuickControlPhase.NotStarted, null, null, null, null, now,
                "尚未执行快捷管控。");
            var store = new ClassroomQuickControlStateStore(path);
            store.Save(new ClassroomQuickControlSessionState(ClassroomQuickControlStateStore.CurrentSchemaVersion,
                sessionId, "campus-demo", roomId, true, now, [initialDevice])
            {
                StateRevision = 1,
                ControlExpiresUtc = expires
            });

            var apply = store.BeginOperation(sessionId, targetId,
                ClassroomQuickControlAction.EnableWebsiteRestriction, "teacher-command", "教师端点击应用限制。");
            Expect(apply.DesiredRevision == 1 && apply.TaskId is not null &&
                   apply.DesiredAction == ClassroomQuickControlAction.EnableWebsiteRestriction &&
                   apply.Phase == ClassroomQuickControlPhase.WebsiteApplying &&
                   apply.WebsitePhase == ClassroomQuickControlPhase.WebsiteApplying);
            var activeApply = apply with
            {
                Mode = DeviceControlMode.WebsiteRestriction,
                Phase = ClassroomQuickControlPhase.WebsiteApplying,
                WebsitePhase = ClassroomQuickControlPhase.WebsiteApplying,
                PendingWebsiteRevision = 41,
                WebsiteExpiresUtc = expires,
                ExpiresUtc = expires,
                UpdatedUtc = now.AddSeconds(1),
                Detail = "应用策略正在执行。"
            };
            store.Save(ReadSession(store, sessionId) with { Devices = [activeApply] });

            var wrongDeadlineRejected = false;
            try
            {
                _ = store.BeginOperation(sessionId, targetId, ClassroomQuickControlAction.Release,
                    "automatic-deadline", "过期截止任务。", expires.AddMinutes(-1));
            }
            catch (InvalidOperationException) { wrongDeadlineRejected = true; }
            Expect(wrongDeadlineRejected);

            store.SetControlExpiry(sessionId, reapplyExpires);
            var staleDeadlineRejected = false;
            try
            {
                _ = store.BeginOperation(sessionId, targetId, ClassroomQuickControlAction.Release,
                    "automatic-deadline", "旧截止时间任务。", expires);
            }
            catch (InvalidOperationException) { staleDeadlineRejected = true; }
            Expect(staleDeadlineRejected && ReadSession(store, sessionId).ControlExpiresUtc == reapplyExpires);

            var release = store.BeginOperation(sessionId, targetId, ClassroomQuickControlAction.Release,
                "teacher-command", "教师端点击恢复。");
            Expect(release.DesiredRevision == 2 && release.TaskId != apply.TaskId &&
                   release.DesiredAction == ClassroomQuickControlAction.Release &&
                   release.Phase == ClassroomQuickControlPhase.ReleasePending &&
                   release.WebsitePhase == ClassroomQuickControlPhase.ReleasePending);
            var failedRelease = release with
            {
                Mode = DeviceControlMode.WebsiteRestriction,
                Phase = ClassroomQuickControlPhase.Unknown,
                WebsitePhase = ClassroomQuickControlPhase.WebsiteApplied,
                WebsiteRevision = 41,
                PendingWebsiteRevision = 42,
                WebsiteExpiresUtc = expires,
                ExpiresUtc = expires,
                UpdatedUtc = now.AddSeconds(2),
                Detail = "恢复回执暂未匹配；保留待核对版本。"
            };
            var failedReleaseSession = store.Save(ReadSession(store, sessionId) with
                { Devices = [failedRelease] });
            var persistedFailure = failedReleaseSession.Devices.Single();
            Expect(ClassroomQuickControlDesiredState.ResolveCatchUpAction(persistedFailure) ==
                   ClassroomQuickControlAction.Release &&
                   ClassroomQuickControlDesiredState.NeedsCatchUp(persistedFailure));

            var ambiguousLegacy = persistedFailure with
            {
                DesiredRevision = 0,
                TaskId = null,
                DesiredAction = null
            };
            Expect(ClassroomQuickControlDesiredState.ResolveCatchUpAction(ambiguousLegacy) is null &&
                   !ClassroomQuickControlDesiredState.NeedsCatchUp(ambiguousLegacy));

            var staleApplyWrite = activeApply with
            {
                Phase = ClassroomQuickControlPhase.WebsiteApplied,
                WebsitePhase = ClassroomQuickControlPhase.WebsiteApplied,
                WebsiteRevision = 41,
                PendingWebsiteRevision = null,
                UpdatedUtc = now.AddMinutes(1),
                Detail = "旧 Apply 回执迟到。"
            };
            var afterStaleWrite = store.Save(failedReleaseSession with { Devices = [staleApplyWrite] });
            Expect(afterStaleWrite.Devices.Single() is
                { DesiredRevision: 2, DesiredAction: ClassroomQuickControlAction.Release,
                  Phase: ClassroomQuickControlPhase.Unknown, PendingWebsiteRevision: 42 });

            var ended = store.CloseSession(sessionId, now.AddSeconds(3));
            var endedDevice = ended.Devices.Single();
            Expect(!ended.Active && ended.ClosedUtc is not null && endedDevice.DesiredRevision == 3 &&
                   endedDevice.TaskId is not null && endedDevice.TaskId != release.TaskId &&
                   endedDevice.DesiredAction == ClassroomQuickControlAction.Release &&
                   endedDevice.Phase == ClassroomQuickControlPhase.ReleasePending);
            var repeatedEnd = store.CloseSession(sessionId, now.AddMinutes(1));
            Expect(repeatedEnd.Devices.Single().DesiredRevision == endedDevice.DesiredRevision &&
                   repeatedEnd.Devices.Single().TaskId == endedDevice.TaskId);

            var attemptedReopen = store.Save(ended with
            {
                Active = true,
                ClosedUtc = null,
                StateRevision = ended.StateRevision + 1,
                UpdatedUtc = now.AddMinutes(2)
            });
            Expect(!attemptedReopen.Active && attemptedReopen.ClosedUtc == ended.ClosedUtc);

            var lateApplyAfterClose = store.Save(ended with { Devices = [staleApplyWrite] });
            Expect(!lateApplyAfterClose.Active && lateApplyAfterClose.Devices.Single() is
                { DesiredRevision: 3, DesiredAction: ClassroomQuickControlAction.Release,
                  Phase: ClassroomQuickControlPhase.ReleasePending, PendingWebsiteRevision: 42 });

            var closedSessionRejectsNewWork = false;
            try
            {
                _ = store.BeginOperation(sessionId, targetId,
                    ClassroomQuickControlAction.EnableWebsiteRestriction, "teacher-command", "结束后旧 Apply。");
            }
            catch (InvalidOperationException) { closedSessionRejectsNewWork = true; }
            Expect(closedSessionRejectsNewWork);

            var retried = store.UpdateRetry(sessionId, targetId, endedDevice.DesiredRevision,
                endedDevice.TaskId!.Value, now.AddSeconds(4));
            Expect(retried.RetryFailureCount == 1 && retried.RetryAfterUtc == now.AddSeconds(19));
            var restartedStore = new ClassroomQuickControlStateStore(path);
            var afterRestart = restartedStore.Read(sessionId)!.Devices.Single();
            Expect(afterRestart.DesiredRevision == endedDevice.DesiredRevision &&
                   afterRestart.TaskId == endedDevice.TaskId && afterRestart.RetryFailureCount == 1 &&
                   afterRestart.RetryAfterUtc == retried.RetryAfterUtc);

            var lines = File.ReadAllLines(path + ".events.jsonl");
            var entries = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Expect(entries.Any(entry => entry.RootElement.GetProperty("source").GetString() ==
                        "stale-task-rejected" && !entry.RootElement.GetProperty("accepted").GetBoolean() &&
                        entry.RootElement.GetProperty("desiredRevision").GetInt64() == 1 &&
                        entry.RootElement.GetProperty("taskId").GetGuid() == apply.TaskId));
                Expect(entries.Any(entry => entry.RootElement.GetProperty("source").GetString() ==
                        "classroom-ended" && entry.RootElement.GetProperty("oldState").GetString() !=
                        entry.RootElement.GetProperty("newState").GetString()));
                Expect(entries.All(entry => entry.RootElement.TryGetProperty("timeUtc", out _) &&
                    entry.RootElement.TryGetProperty("triggerReason", out _) &&
                    entry.RootElement.TryGetProperty("policyRevision", out _)));
            }
            finally
            {
                foreach (var entry in entries) entry.Dispose();
            }

            store.Remove(sessionId);
            var lateTaskCannotRecreateSession = false;
            try { _ = store.Save(failedReleaseSession with { Active = true }); }
            catch (InvalidOperationException) { lateTaskCannotRecreateSession = true; }
            Expect(lateTaskCannotRecreateSession);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static ClassroomQuickControlSessionState ReadSession(ClassroomQuickControlStateStore store,
        Guid sessionId) => store.Read(sessionId) ?? throw new InvalidDataException("课堂状态缺失。");

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidDataException("课堂快捷管控 Revision 回归检查失败。");
    }
}
