using VeyonCampus.Core;

internal static class ClassroomCountdownChecks
{
    public static void Run()
    {
        var directory = Path.Combine(TestPath.CanonicalTempRoot(),
            "veyon-classroom-countdown-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "classroom-countdown.json");
            var store = new ClassroomCountdownStore(path);
            var sessionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            Assert(store.Read(sessionId) is null && !File.Exists(path));
            Reject(() => store.Start(Guid.Empty, 15, now));
            Reject(() => store.Start(sessionId, 0, now));
            Reject(() => store.Start(sessionId, 181, now));
            Reject(() => store.Start(sessionId, 15,
                new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(8))));

            var countdown = store.Start(sessionId, 15, now);
            Assert(countdown.SessionId == sessionId && countdown.DeadlineUtc == now.AddMinutes(15));
            Assert(countdown.Remaining(now) == TimeSpan.FromMinutes(15));
            Assert(countdown.Remaining(now.AddMinutes(1)) == TimeSpan.FromMinutes(14));
            Assert(countdown.Remaining(countdown.DeadlineUtc) == TimeSpan.Zero);
            Assert(store.Read(sessionId) == countdown);
            Assert(store.Read(Guid.NewGuid()) is null);
            Assert(!store.End(Guid.NewGuid()) && File.Exists(path));
            Assert(store.End(sessionId) && !File.Exists(path));
            Assert(store.Start(sessionId, ClassroomCountdown.MinimumMinutes, now).DeadlineUtc == now.AddMinutes(1));
            Assert(store.End(sessionId));
            Assert(store.Start(sessionId, ClassroomCountdown.MaximumMinutes, now).DeadlineUtc == now.AddMinutes(180));
            Assert(store.End(sessionId));

            File.WriteAllText(path, "{\"schemaVersion\":1,\"schemaVersion\":1}");
            var corruptBytes = File.ReadAllBytes(path);
            Reject(() => store.Read(sessionId));
            Reject(() => store.Start(sessionId, 10, now));
            Assert(File.ReadAllBytes(path).SequenceEqual(corruptBytes), "损坏倒计时不得被启动操作静默覆盖。");

            File.WriteAllText(path, "{\"schemaVersion\":1,\"sessionId\":\"" + Guid.NewGuid() +
                                    "\",\"startedUtc\":\"" + now.ToString("O") +
                                    "\",\"deadlineUtc\":\"" + now.AddMinutes(181).ToString("O") + "\"}");
            var invalidBytes = File.ReadAllBytes(path);
            Reject(() => store.Read(sessionId));
            Assert(File.ReadAllBytes(path).SequenceEqual(invalidBytes), "越界截止时间不得改写原文件。");
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
        throw new InvalidOperationException("预期无效的课堂倒计时操作必须被拒绝。");
    }

    private static void Assert(bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException(message ?? "课堂倒计时检查失败。");
    }
}
