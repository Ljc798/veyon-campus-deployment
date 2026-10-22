using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class TeacherRoomSelectionChecks
{
    public static void Run()
    {
        var directory = Path.Combine(TestPath.CanonicalTempRoot(),
            "veyon-room-selection-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "recent-room-selection.json");
            var store = new TeacherRoomSelectionStore(path);
            if (store.Load().Count != 0) throw new Exception("A new preference file should be empty.");

            var firstCampus = Guid.NewGuid();
            var secondCampus = Guid.NewGuid();
            var firstRoom = Guid.NewGuid();
            var recentRoom = Guid.NewGuid();
            var otherCampusRoom = Guid.NewGuid();
            store.Save(firstCampus, firstRoom);
            store.Save(firstCampus, recentRoom);
            store.Save(secondCampus, otherCampusRoom);

            var loaded = new TeacherRoomSelectionStore(path).Load();
            if (loaded.Count != 2 || loaded[firstCampus] != recentRoom ||
                loaded[secondCampus] != otherCampusRoom)
                throw new Exception("Recent room choices should persist independently per campus.");

            File.WriteAllText(path, "{");
            var corruptText = File.ReadAllText(path);
            try
            {
                _ = store.Load();
                throw new Exception("Malformed preference data should be rejected.");
            }
            catch (InvalidDataException) { }
            try
            {
                store.Save(firstCampus, firstRoom);
                throw new Exception("Malformed preference data should not be overwritten.");
            }
            catch (InvalidDataException) { }
            if (File.ReadAllText(path) != corruptText)
                throw new Exception("Malformed preference data was unexpectedly replaced.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
