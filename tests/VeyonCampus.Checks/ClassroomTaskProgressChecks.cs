using VeyonCampus.Core;

internal static class ClassroomTaskProgressChecks
{
    public static void Run()
    {
        var directory = Path.Combine(TestPath.CanonicalTempRoot(),
            "veyon-classroom-task-progress-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "classroom-task-progress.json");
            var store = new ClassroomTaskProgressStore(path);
            var sessionId = Guid.NewGuid();
            var otherSessionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            Assert(store.Read(sessionId) is null && !File.Exists(path));
            Reject(() => store.Read(Guid.Empty));
            Reject(() => store.Add(Guid.Empty, "任务", now));
            Reject(() => store.Add(sessionId, "  ", now));
            Reject(() => store.Add(sessionId, new string('字', ClassroomTaskProgress.MaximumTitleCharacters + 1), now));
            Reject(() => store.Add(sessionId, "第一行\n第二行", now));
            Reject(() => store.Add(sessionId, "第一行\u2028第二行", now));
            Reject(() => store.Add(sessionId, "任务", now.ToOffset(TimeSpan.FromHours(8))));

            var first = store.Add(sessionId, "  阅读题目  ", now);
            var second = store.Add(sessionId, "完成练习", now.AddSeconds(1));
            Assert(first.Tasks.Single().Title == "阅读题目" && first.Tasks.Single().TaskId != Guid.Empty);
            var persisted = store.Read(sessionId);
            var reloaded = new ClassroomTaskProgressStore(path).Read(sessionId);
            Assert(persisted is { CompletedCount: 0, Tasks.Length: 2 } &&
                   persisted.Tasks[0].TaskId == first.Tasks[0].TaskId &&
                   reloaded is { CompletedCount: 0, Tasks.Length: 2 } &&
                   reloaded.Tasks.Select(task => (task.TaskId, task.Title, task.IsCompleted))
                       .SequenceEqual(persisted.Tasks.Select(task => (task.TaskId, task.Title, task.IsCompleted))),
                "清单应跨 Store 实例持久化，并只属于对应课堂。");
            Assert(store.Read(otherSessionId) is null && store.SetCompleted(otherSessionId,
                first.Tasks[0].TaskId, true, now) is null && File.Exists(path));

            var completed = store.SetCompleted(sessionId, first.Tasks[0].TaskId, true, now.AddSeconds(2));
            Assert(completed is { CompletedCount: 1, Tasks.Length: 2 } && completed.Tasks[0].IsCompleted);
            var unchanged = store.SetCompleted(sessionId, first.Tasks[0].TaskId, true, now.AddSeconds(3));
            Assert(unchanged?.UpdatedUtc == completed?.UpdatedUtc &&
                   unchanged?.Tasks[0].IsCompleted == true,
                "重复设置同一完成状态不应无谓改写更新时间。");
            Assert(store.SetCompleted(sessionId, Guid.NewGuid(), true, now.AddSeconds(3))?.UpdatedUtc ==
                   completed?.UpdatedUtc);
            var removed = store.Remove(sessionId, second.Tasks[1].TaskId, now.AddSeconds(4));
            Assert(removed is { CompletedCount: 1, Tasks.Length: 1 } && removed.Tasks[0].IsCompleted);
            Assert(store.Remove(sessionId, first.Tasks[0].TaskId, now.AddSeconds(5)) is null && !File.Exists(path),
                "删除最后一项后应清除空清单文件。");

            for (var index = 0; index < ClassroomTaskProgress.MaximumTasks; index++)
                _ = store.Add(sessionId, $"任务 {index + 1}", now.AddSeconds(index));
            Assert(store.Read(sessionId) is { Tasks.Length: ClassroomTaskProgress.MaximumTasks });
            var boundedBytes = File.ReadAllBytes(path);
            Reject(() => store.Add(sessionId, "超出上限", now.AddMinutes(1)));
            Assert(File.ReadAllBytes(path).SequenceEqual(boundedBytes), "达到上限后不得改写现有清单。");
            Assert(store.Clear(sessionId) && !File.Exists(path));
            Assert(!store.Clear(sessionId));

            File.WriteAllText(path, "{\"schemaVersion\":1,\"schemaVersion\":1}");
            var corruptBytes = File.ReadAllBytes(path);
            Reject(() => store.Read(sessionId));
            Reject(() => store.Add(sessionId, "不覆盖损坏数据", now));
            Reject(() => store.Clear(sessionId));
            Assert(File.ReadAllBytes(path).SequenceEqual(corruptBytes), "损坏清单不得被静默覆盖或删除。");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("预期无效的课堂任务进度操作必须被拒绝。");
    }

    private static void Assert(bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException(message ?? "课堂任务进度检查失败。");
    }
}
