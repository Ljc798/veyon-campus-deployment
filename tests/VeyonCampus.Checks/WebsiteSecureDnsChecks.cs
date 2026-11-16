using VeyonCampus.Core;

/// <summary>
/// Encrypted DNS is the one setting that can silently undo every name-level layer: a browser that
/// resolves through its own DoH endpoint never asks the system resolver, so the hosts block stops
/// applying to it. These checks pin the ownership rules around turning it off, because getting them
/// wrong either leaves the hole open or deletes a policy the school set on purpose.
/// </summary>
internal static class WebsiteSecureDnsChecks
{
    public static void Run()
    {
        ClaimsAnAbsentValue();
        KeepsOwnershipOnReapply();
        LeavesAnExternalValueAlone();
        DropsOwnershipWhenSomeoneElseChangedIt();
        RestoresOnlyWhatItOwns();
        SurvivesTheJournal();
        NamesTheBrowsersThatStillResolvePrivately();
    }

    private static void ClaimsAnAbsentValue()
    {
        var before = Browser();
        var after = WebsitePolicyRegistryTransactions.ApplySecureDns(before, before, disable: true, "off", "Edge");
        Expect(after.PolicySecureDns is { Exists: true, Value: "off" }, "the disabled value was not written.");
        Expect(after.ManagedSecureDns is { Exists: true, Value: "off" }, "ownership was not recorded.");
    }

    private static void KeepsOwnershipOnReapply()
    {
        var owned = Browser(Text("off"), Text("off"));
        var after = WebsitePolicyRegistryTransactions.ApplySecureDns(owned, owned, disable: true, "off", "Edge");
        Expect(after.PolicySecureDns == Text("off") && after.ManagedSecureDns == Text("off"),
            "re-applying dropped ownership of a value this tool owns.");
    }

    private static void LeavesAnExternalValueAlone()
    {
        // A school that pinned DoH to a template must keep it. Failing the whole push would be worse,
        // and silently overwriting it would break their resolver.
        var external = Browser(Text("secure"));
        var after = WebsitePolicyRegistryTransactions.ApplySecureDns(external, external, disable: true, "off", "Edge");
        Expect(after.PolicySecureDns == Text("secure"), "an external encrypted-DNS policy was overwritten.");
        Expect(!after.ManagedSecureDns.Exists, "an external value was claimed as this tool's.");
    }

    private static void DropsOwnershipWhenSomeoneElseChangedIt()
    {
        var changed = Browser(Text("automatic"), Text("off"));
        var after = WebsitePolicyRegistryTransactions.ApplySecureDns(changed, changed, disable: true, "off", "Edge");
        Expect(after.PolicySecureDns == Text("automatic"), "an externally changed value was not preserved.");
        Expect(!after.ManagedSecureDns.Exists, "a value the tool no longer wrote was still claimed.");

        var disabled = WebsitePolicyRegistryTransactions.ApplySecureDns(changed, changed, disable: false, "off", "Edge");
        Expect(disabled.PolicySecureDns == Text("automatic"), "disabling removed a value this tool never owned.");
        Expect(!disabled.ManagedSecureDns.Exists, "disabling kept a stale ownership record.");
    }

    private static void RestoresOnlyWhatItOwns()
    {
        var owned = Browser(Text("off"), Text("off"));
        var cleared = WebsitePolicyRegistryTransactions.ApplySecureDns(owned, owned, disable: false, "off", "Edge");
        Expect(!cleared.PolicySecureDns.Exists, "disabling a policy left this tool's own value behind.");
        Expect(!cleared.ManagedSecureDns.Exists, "disabling kept the ownership record.");

        var untouched = Browser(Text("off"));
        var kept = WebsitePolicyRegistryTransactions.ApplySecureDns(untouched, untouched, disable: false, "off", "Edge");
        Expect(kept.PolicySecureDns == Text("off"), "disabling removed a value the tool did not own.");
    }

    private static void SurvivesTheJournal()
    {
        // The journal is the recovery contract: a pending transaction is replayed after a crash, so
        // the new values have to round-trip and still validate as a coherent owned snapshot.
        var browser = Browser(Text("off"), Text("off"));
        var before = Snapshot(browser, revision: 6);
        var after = Snapshot(browser, revision: 7);
        var transaction = new WebsitePolicyRegistryTransaction(1, Guid.NewGuid(),
            WebsitePolicyRegistryTransactionOperation.Apply, before, after);
        var json = WebsitePolicyRegistryTransactions.Serialize(transaction);
        var restored = WebsitePolicyRegistryTransactions.Deserialize(json);
        Expect(WebsitePolicyRegistryTransactions.Equivalent(restored.After, after),
            "the encrypted-DNS values did not survive the journal.");
        Expect(WebsitePolicyRegistryTransactions.Equivalent(restored.Before, before),
            "the previous encrypted-DNS values did not survive the journal.");

        // A value owned without a matching policy value is the state that would let a stale record
        // delete someone else's setting later, so it must not validate.
        var inconsistent = Snapshot(Browser(null, Text("off")), revision: 7);
        var broken = new WebsitePolicyRegistryTransaction(1, Guid.NewGuid(),
            WebsitePolicyRegistryTransactionOperation.Apply, Snapshot(Browser(), revision: 6), inconsistent);
        Reject(() => WebsitePolicyRegistryTransactions.Serialize(broken));
    }

    private static void NamesTheBrowsersThatStillResolvePrivately()
    {
        Expect(WebsitePolicyRegistryTransactions.SecureDnsConflict(Snapshot(Browser(Text("off")), 7)) is null,
            "an off value was reported as a conflict.");
        var conflict = WebsitePolicyRegistryTransactions.SecureDnsConflict(Snapshot(Browser(Text("secure")), 7));
        Expect(conflict == "Edge", $"the conflicting browser was not named: {conflict ?? "null"}.");

        // Firefox expresses the same setting as the numeric preference 5; 2 (TRR first) is a conflict.
        var firefox = Snapshot(Browser(), 7) with
        {
            Firefox = Browser(Text("2"))
        };
        Expect(WebsitePolicyRegistryTransactions.SecureDnsConflict(firefox) == "Firefox",
            "a Firefox preference that enables TRR was not reported.");
        var firefoxOff = firefox with { Firefox = Browser(Text("5")) };
        Expect(WebsitePolicyRegistryTransactions.SecureDnsConflict(firefoxOff) is null,
            "Firefox's disabled preference was reported as a conflict.");
    }

    private static WebsitePolicyRegistryValueSnapshot Text(string value) => new(true, value);

    private static WebsitePolicyBrowserRegistrySnapshot Browser(WebsitePolicyRegistryValueSnapshot? secureDns = null,
        WebsitePolicyRegistryValueSnapshot? managedSecureDns = null) => new(
        new(false, []), new(false, []), new(false, []), new(false, []), new(true, "1"),
        PolicySecureDns: secureDns, ManagedSecureDns: managedSecureDns);

    private static WebsitePolicyRegistrySnapshot Snapshot(WebsitePolicyBrowserRegistrySnapshot edge, long revision) =>
        new(true, new(true, "campus-a"), new(true, revision.ToString()), new(true, "Blocklist"),
            new(false, null), new(false, null), edge, Browser(), Browser());

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("an inconsistent encrypted-DNS snapshot was accepted.");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
