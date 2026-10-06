using VeyonCampus.Core;

internal static class WebsitePolicyRegistryTransactionChecks
{
    public static void Run()
    {
        var before = Snapshot("1", "Blocklist", ["blocked.example"], []);
        var after = Snapshot("2", "Allowlist", ["*"], ["school.example"]);
        for (var interruption = 1; interruption <= 17; interruption++)
        {
            var backend = new FakeBackend(before, interruption);
            try
            {
                WebsitePolicyRegistryTransactions.Commit(backend,
                    WebsitePolicyRegistryTransactionOperation.Apply, before, after);
            }
            catch (IOException) { }

            if (backend.Transaction is not null)
                WebsitePolicyRegistryTransactions.Reconcile(backend);
            Expect(backend.Transaction is null && WebsitePolicyRegistryTransactions.Equivalent(backend.State, after),
                $"Interrupted website transaction {interruption} did not recover.");
        }

        var pending = new WebsitePolicyRegistryTransaction(1, Guid.NewGuid(),
            WebsitePolicyRegistryTransactionOperation.Apply, before, after);
        var conflicting = new FakeBackend(before, failAtMutation: null) { Transaction = pending };
        conflicting.State = before with { Revision = Value("99") };
        try
        {
            WebsitePolicyRegistryTransactions.Reconcile(conflicting);
            throw new Exception("External website policy metadata was overwritten during recovery.");
        }
        catch (IOException) { }
        Expect(conflicting.State.Revision == Value("99") && conflicting.TargetApplications == 0,
            "Recovery changed an external website policy value.");

        var serialized = WebsitePolicyRegistryTransactions.Serialize(pending);
        Expect(WebsitePolicyRegistryTransactions.Serialize(WebsitePolicyRegistryTransactions.Deserialize(serialized)) == serialized,
            "Website policy transaction journal did not round-trip.");

        var absent = new WebsitePolicyRegistryListSnapshot(false, []);
        var original = List(["old.example"]);
        var target = List(["new.example", "portal.example"]);
        var partialStage = List(["new.example"]);
        Expect(WebsitePolicyRegistryTransactions.IsValidStagingPrefix(absent, target) &&
               WebsitePolicyRegistryTransactions.IsValidStagingPrefix(partialStage, target) &&
               WebsitePolicyRegistryTransactions.IsValidStagingPrefix(target, target) &&
               !WebsitePolicyRegistryTransactions.IsValidStagingPrefix(List(["unexpected.example"]), target),
            "Website policy staging recovery accepted the wrong partial contents.");
        WebsitePolicyRegistryTransactions.VerifyListSwapState(absent, partialStage, original, original, target, "Edge");
        WebsitePolicyRegistryTransactions.VerifyListSwapState(absent, target, original, original, target, "Chrome");
        try
        {
            WebsitePolicyRegistryTransactions.VerifyListSwapState(absent, List(["unexpected.example"]), original,
                original, target, "Edge");
            throw new Exception("A conflicting website policy staging key was accepted.");
        }
        catch (IOException) { }
        var pathRule = List(["https://school.example/Classes"]);
        var changedPathCase = List(["https://school.example/classes"]);
        try
        {
            WebsitePolicyRegistryTransactions.VerifyListSwapState(changedPathCase, absent, absent,
                pathRule, target, "Chrome");
            throw new Exception("A case-sensitive website path change was treated as owned state.");
        }
        catch (IOException) { }
    }

    private static WebsitePolicyRegistrySnapshot Snapshot(string revision, string mode,
        string[] blocklist, string[] allowlist)
    {
        var block = List(blocklist);
        var allow = List(allowlist);
        var browser = new WebsitePolicyBrowserRegistrySnapshot(block, allow, List(blocklist), List(allowlist), Value("1"));
        return new WebsitePolicyRegistrySnapshot(true, Value("campus-demo"), Value(revision), Value(mode),
            Empty(), Empty(), browser, browser);
    }

    private static WebsitePolicyRegistryListSnapshot List(string[] values) => new(values.Length > 0, values.ToArray());
    private static WebsitePolicyRegistryValueSnapshot Value(string value) => new(true, value);
    private static WebsitePolicyRegistryValueSnapshot Empty() => new(false, null);

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FakeBackend(WebsitePolicyRegistrySnapshot initial,
        int? failAtMutation) : IWebsitePolicyRegistryTransactionBackend
    {
        private int _mutations;
        private int? _failAtMutation = failAtMutation;
        public WebsitePolicyRegistrySnapshot State { get; set; } = initial;
        public WebsitePolicyRegistryTransaction? Transaction { get; set; }
        public int TargetApplications { get; private set; }

        public WebsitePolicyRegistrySnapshot ReadSnapshot() => State;
        public WebsitePolicyRegistryTransaction? ReadTransaction() => Transaction;

        public void SaveTransaction(WebsitePolicyRegistryTransaction transaction)
        {
            Transaction = transaction;
            AfterMutation();
        }

        public void VerifyCanApplyTarget(WebsitePolicyRegistryTransaction transaction)
        {
            Verify(State.Edge.PolicyBlocklist, transaction.Before.Edge.PolicyBlocklist, transaction.After.Edge.PolicyBlocklist);
            Verify(State.Edge.PolicyAllowlist, transaction.Before.Edge.PolicyAllowlist, transaction.After.Edge.PolicyAllowlist);
            Verify(State.Chrome.PolicyBlocklist, transaction.Before.Chrome.PolicyBlocklist, transaction.After.Chrome.PolicyBlocklist);
            Verify(State.Chrome.PolicyAllowlist, transaction.Before.Chrome.PolicyAllowlist, transaction.After.Chrome.PolicyAllowlist);

            static void Verify(WebsitePolicyRegistryListSnapshot current, WebsitePolicyRegistryListSnapshot before,
                WebsitePolicyRegistryListSnapshot after)
            {
                if (current.Exists != before.Exists && current.Exists != after.Exists ||
                    current.Exists && !current.Values.SequenceEqual(before.Values, StringComparer.Ordinal) &&
                    !current.Values.SequenceEqual(after.Values, StringComparer.Ordinal))
                    throw new IOException("External policy conflict in simulated registry backend.");
            }
        }

        public void ApplyTarget(WebsitePolicyRegistryTransaction transaction)
        {
            State = State with { Edge = State.Edge with { PolicyBlocklist = transaction.After.Edge.PolicyBlocklist } };
            AfterMutation();
            State = State with { Edge = State.Edge with { PolicyAllowlist = transaction.After.Edge.PolicyAllowlist } };
            AfterMutation();
            State = State with { Chrome = State.Chrome with { PolicyBlocklist = transaction.After.Chrome.PolicyBlocklist } };
            AfterMutation();
            State = State with { Chrome = State.Chrome with { PolicyAllowlist = transaction.After.Chrome.PolicyAllowlist } };
            AfterMutation();

            State = State with { Edge = State.Edge with { ManagedBlocklist = transaction.After.Edge.ManagedBlocklist } };
            AfterMutation();
            State = State with { Edge = State.Edge with { ManagedAllowlist = transaction.After.Edge.ManagedAllowlist } };
            AfterMutation();
            State = State with { Edge = State.Edge with { Initialized = transaction.After.Edge.Initialized } };
            AfterMutation();
            State = State with { Chrome = State.Chrome with { ManagedBlocklist = transaction.After.Chrome.ManagedBlocklist } };
            AfterMutation();
            State = State with { Chrome = State.Chrome with { ManagedAllowlist = transaction.After.Chrome.ManagedAllowlist } };
            AfterMutation();
            State = State with { Chrome = State.Chrome with { Initialized = transaction.After.Chrome.Initialized } };
            AfterMutation();

            State = State with { CampusId = transaction.After.CampusId };
            AfterMutation();
            State = State with { Revision = transaction.After.Revision };
            AfterMutation();
            State = State with { Mode = transaction.After.Mode };
            AfterMutation();
            State = State with { ExpiresUtc = transaction.After.ExpiresUtc };
            AfterMutation();
            State = State with { ExpiredUtc = transaction.After.ExpiredUtc };
            AfterMutation();
            TargetApplications++;
        }

        public void ClearTransaction()
        {
            Transaction = null;
            AfterMutation();
        }

        public void DeleteAgentKey()
        {
            State = State with { AgentKeyExists = false };
            Transaction = null;
        }

        private void AfterMutation()
        {
            _mutations++;
            if (_failAtMutation != _mutations) return;
            _failAtMutation = null;
            throw new IOException("Simulated interruption after a durable registry mutation.");
        }
    }
}
