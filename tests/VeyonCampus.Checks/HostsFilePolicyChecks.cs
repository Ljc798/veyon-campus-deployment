using VeyonCampus.Core;

internal static class HostsFilePolicyChecks
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "veyoncampus-hosts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var hostsPath = Path.Combine(directory, "hosts");
        try
        {
            File.WriteAllText(hostsPath, "127.0.0.1 localhost\r\n# teacher note\r\n");

            var applied = HostsFileWebsitePolicy.Apply(7, "campus-a", ["example.com", "*.bad", "not a domain"],
                hostsPath);
            Expect(applied.Applied && applied.Error is null, "hosts block was not applied.");
            var text = File.ReadAllText(hostsPath);
            Expect(text.Contains("127.0.0.1 localhost"), "hosts block removed an unrelated entry.");
            Expect(text.Contains("# teacher note"), "hosts block removed an existing comment.");
            Expect(text.Contains("0.0.0.0 example.com"), "hosts block did not write the domain.");
            Expect(text.Contains("0.0.0.0 www.example.com"), "hosts block did not write the www variant.");
            Expect(!text.Contains("*.bad"), "hosts block wrote a wildcard entry.");
            Expect(!text.Contains("not a domain"), "hosts block wrote an invalid entry.");
            Expect(HostsFileWebsitePolicy.ReadAppliedRevision(hostsPath) == 7, "hosts revision was not recorded.");
            Expect(HostsFileWebsitePolicy.HasBlock(hostsPath), "hosts block was not detected.");

            // Re-applying must replace the block rather than append a second copy, and must drop
            // the domains that are no longer blocked.
            var second = HostsFileWebsitePolicy.Apply(9, "campus-a", ["other.example"], hostsPath);
            Expect(second.Applied, "second hosts apply failed.");
            var secondText = File.ReadAllText(hostsPath);
            Expect(CountOccurrences(secondText, HostsFileWebsitePolicy.BeginMarker) == 1,
                "hosts block was duplicated.");
            Expect(!secondText.Contains("0.0.0.0 example.com"), "hosts block kept a stale domain.");
            Expect(HostsFileWebsitePolicy.ReadAppliedRevision(hostsPath) == 9, "hosts revision was not replaced.");

            var cleared = HostsFileWebsitePolicy.Clear(hostsPath);
            Expect(cleared.Applied && !HostsFileWebsitePolicy.HasBlock(hostsPath), "hosts block was not cleared.");
            var clearedText = File.ReadAllText(hostsPath);
            Expect(clearedText.Contains("127.0.0.1 localhost") && clearedText.Contains("# teacher note"),
                "clearing the hosts block removed unrelated entries.");
            Expect(HostsFileWebsitePolicy.ReadAppliedRevision(hostsPath) is null, "hosts revision survived the clear.");

            var expanded = HostsFileWebsitePolicy.ExpandHostsEntries(["A.example", "www.b.example", "a.example"]);
            Expect(expanded.SequenceEqual(["a.example", "www.a.example", "www.b.example"], StringComparer.Ordinal),
                "hosts expansion produced unexpected entries: " + string.Join(",", expanded));

            // A hosts file that does not exist yet must be created rather than reported as a failure.
            var missingPath = Path.Combine(directory, "hosts-new");
            var created = HostsFileWebsitePolicy.Apply(11, "campus-b", ["blocked.example"], missingPath);
            Expect(created.Applied && HostsFileWebsitePolicy.ReadAppliedRevision(missingPath) == 11,
                "hosts block was not created for a missing hosts file.");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception("Hosts file policy check failed: " + message);
    }
}
