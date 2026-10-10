using System.Text.Json;
using VeyonCampus.Core;

internal static class ClassroomSeatLayoutChecks
{
    public static void Run()
    {
        var directory = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-seat-layout-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "seat-layouts.json");
            var store = new ClassroomSeatLayoutStore(path);
            var campusId = Guid.NewGuid();
            var roomId = Guid.NewGuid();
            var targets = MachineNaming.CreateRange("PC-", "1", "24");

            var initial = store.GetLayout(campusId, roomId, targets);
            Assert(initial.Columns == 5 && initial.TargetOrder.SequenceEqual(targets));
            Assert(!File.Exists(path), "默认机位应由现有电脑编号即时生成，不应仅因查看就创建配置文件。");
            var initialPositions = ClassroomSeatLayoutStore.GetLocations(initial);
            Assert(initialPositions[0] == new ClassroomSeatLocation("PC-01", 1, 1));
            Assert(initialPositions[23] == new ClassroomSeatLocation("PC-24", 5, 4));

            var swapped = store.Swap(campusId, roomId, targets, 0, 5);
            Assert(swapped.TargetOrder[0] == "PC-06" && swapped.TargetOrder[5] == "PC-01");
            var reloaded = new ClassroomSeatLayoutStore(path).GetLayout(campusId, roomId, targets);
            Assert(reloaded.TargetOrder.SequenceEqual(swapped.TargetOrder));
            Assert(ClassroomSeatLayoutStore.GetLocations(reloaded)[0] == new ClassroomSeatLocation("PC-06", 1, 1));

            var reorderedTargets = reloaded.TargetOrder.ToArray();
            var changedColumns = store.SetColumns(campusId, roomId, targets, 6);
            Assert(changedColumns.Columns == 6 && changedColumns.TargetOrder.SequenceEqual(reorderedTargets));
            Assert(ClassroomSeatLayoutStore.GetLocations(changedColumns)[6] == new ClassroomSeatLocation("PC-07", 2, 1));
            Reject(() => store.SetColumns(campusId, roomId, targets, 0));
            Reject(() => store.SetColumns(campusId, roomId, targets, targets.Count + 1));
            Reject(() => store.Swap(campusId, roomId, targets, -1, 0));

            var expandedTargets = MachineNaming.CreateRange("PC-", "1", "25");
            var expanded = store.GetLayout(campusId, roomId, expandedTargets);
            Assert(expanded.TargetOrder.Take(24).SequenceEqual(reorderedTargets) &&
                   expanded.TargetOrder[^1] == "PC-25" && expanded.Columns == 6);
            var reducedTargets = expandedTargets.Where(target => target != "PC-03").ToArray();
            var reduced = store.GetLayout(campusId, roomId, reducedTargets);
            Assert(reduced.TargetOrder.Length == reducedTargets.Length && !reduced.TargetOrder.Contains("PC-03") &&
                   reduced.TargetOrder.Take(23).SequenceEqual(reorderedTargets.Where(target => target != "PC-03")));

            var otherCampus = store.GetLayout(Guid.NewGuid(), roomId, reducedTargets);
            Assert(otherCampus.TargetOrder.SequenceEqual(reducedTargets));
            Assert(!store.GetLayout(campusId, Guid.NewGuid(), reducedTargets).TargetOrder
                .SequenceEqual(reduced.TargetOrder));

            var originalBytes = File.ReadAllBytes(path);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"schemaVersion\":1,\"layouts\":[]}");
            var malformedBytes = File.ReadAllBytes(path);
            Reject(() => store.GetLayout(campusId, roomId, reducedTargets));
            Reject(() => store.Swap(campusId, roomId, reducedTargets, 0, 1));
            Assert(File.ReadAllBytes(path).SequenceEqual(malformedBytes), "损坏文件不得被默认布局静默覆盖。");

            File.WriteAllBytes(path, originalBytes);
            var invalid = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                layouts = new[]
                {
                    new
                    {
                        campusProfileId = campusId,
                        roomId,
                        columns = 999,
                        targetOrder = targets
                    }
                }
            });
            File.WriteAllText(path, invalid);
            var invalidBytes = File.ReadAllBytes(path);
            Reject(() => store.GetLayout(campusId, roomId, targets));
            Assert(File.ReadAllBytes(path).SequenceEqual(invalidBytes), "越界列数不得触发损坏布局重写。");
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
        throw new InvalidOperationException("预期无效的座位图操作必须被拒绝。");
    }

    private static void Assert(bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException(message ?? "课堂座位图检查失败。");
    }
}
