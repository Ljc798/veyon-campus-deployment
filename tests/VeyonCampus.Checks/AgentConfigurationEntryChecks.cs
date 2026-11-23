using VeyonCampus.Core;

/// <summary>
/// The guard that decides whether a campus configuration folder may be replaced is a name list.
/// Anything missing from it aborts an Agent update, which is how 1.1.90 first shipped: the
/// Maintenance Service wrote its key into <c>Maintenance</c> and the updater refused to touch the
/// folder. These checks pin the list so a new product-owned file cannot silently block updates
/// again.
/// </summary>
internal static class AgentConfigurationEntryChecks
{
    private const string Hash = "3FB709974E759E6E9265A68";

    public static void Run()
    {
        RecognisesMaintenanceDirectory();
        RequiresMaintenanceToBeADirectory();
        RecognisesExistingProductEntries();
        RejectsUnknownEntries();
    }

    private static void RecognisesMaintenanceDirectory()
    {
        Expect(WebsitePolicyAgentInstaller.IsRecognizedAgentConfigurationEntry("Maintenance", Hash, isDirectory: true),
            "the Maintenance key directory is not recognised, so an Agent update would be refused.");
        Expect(WebsitePolicyAgentInstaller.IsRecognizedAgentConfigurationEntry("maintenance", Hash, isDirectory: true),
            "the Maintenance directory name is matched case-sensitively.");
    }

    private static void RequiresMaintenanceToBeADirectory()
    {
        // A file named Maintenance is not what the Maintenance Service writes, so it must not be
        // treated as owned and deleted along with the folder.
        Expect(!WebsitePolicyAgentInstaller.IsRecognizedAgentConfigurationEntry("Maintenance", Hash, isDirectory: false),
            "a file named Maintenance was accepted as a product-owned entry.");
    }

    private static void RecognisesExistingProductEntries()
    {
        var recognised = new (string Name, bool IsDirectory)[]
        {
            ("health-repair", true),
            ("student-update-" + new string('a', 32), true),
            ("agent-" + Hash + ".json", false),
            ("agent-" + Hash + ".json.tmp-" + new string('b', 32), false),
            ("agent-" + Hash + ".json.teacher-channel-status.json", false),
            ("agent-" + Hash + ".json.teacher-channel-status.json.tmp-" + new string('c', 32), false),
            ("student-update-replay.json", false),
            ("edge-policy-refresh.json", false),
            ("edge-policy-refresh.json.tmp-" + new string('d', 32), false),
            ("health-repair-ledger.json", false),
            ("health-repair-ledger.json.tmp-" + new string('e', 32), false),
            ("usage-installation-id", false),
            ("usage-installation-id.last-hkt-day", false),
            ("agent-startup.log", false),
            ("agent-runtime.log", false)
        };
        foreach (var (name, isDirectory) in recognised)
            Expect(WebsitePolicyAgentInstaller.IsRecognizedAgentConfigurationEntry(name, Hash, isDirectory),
                $"a product-owned entry was not recognised: {name}");
    }

    private static void RejectsUnknownEntries()
    {
        var rejected = new (string Name, bool IsDirectory)[]
        {
            ("notes.txt", false),
            ("agent-0123456789ABCDEF01234567.json", false),   // hash of a different campus
            ("agent-" + Hash + ".json.tmp-not-a-guid", false),
            ("student-update-not-a-guid", true),
            ("health-repair-copy", true),
            ("Maintenance.bak", true)
        };
        foreach (var (name, isDirectory) in rejected)
            Expect(!WebsitePolicyAgentInstaller.IsRecognizedAgentConfigurationEntry(name, Hash, isDirectory),
                $"an unrecognised entry was accepted: {name}");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
