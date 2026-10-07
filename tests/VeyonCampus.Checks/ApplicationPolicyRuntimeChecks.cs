using System.Security.Cryptography;
using VeyonCampus.Core;

internal static class ApplicationPolicyRuntimeChecks
{
    public static void Run()
    {
        using var key = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        var policy = new ApplicationPolicyDocument(1, ApplicationPolicyCompiler.Purpose, "demo", 1, now,
            now.AddHours(1), ApplicationPolicyMode.Audit, ["S-1-5-21-1-2-3-1001"],
            [new ApplicationDenyRule(Guid.NewGuid(), ApplicationRuleKind.Hash, "Game", FileSha256: new string('A', 64),
                AppLockerHashSha256: new string('B', 64), SourceFileName: "game.exe", SourceFileLength: 100)]);
        var signed = ApplicationPolicyCryptography.Sign(policy, key);
        var backend = new Backend();
        var store = new Store();
        var runtime = new ApplicationPolicyRuntime(backend, store, "demo", key.ExportSubjectPublicKeyInfoPem());
        backend.BeforeWrite = () => Expect(store.State?.Pending == true);
        runtime.Apply(signed, now);
        Expect(store.State is { Pending: false, Revision: 1 } && backend.Local.Contains("AuditOnly") &&
               backend.PathVerificationCalls == 0);
        Expect(ApplicationPolicyRuntime.RequiresApplicationPolicyCapabilityForUpdate(store.State));
        Reject(() => runtime.Apply(signed, now));
        var external = backend.Local.Replace("AuditOnly", "Enabled", StringComparison.Ordinal);
        backend.Local = external;
        Reject(() => runtime.ExpireIfDue(now.AddHours(2)));
        Expect(backend.Local == external);
        backend.Local = store.State!.InstalledXml;
        Expect(runtime.ExpireIfDue(now.AddHours(2)) && store.State is { Revision: 1, Policy.Mode: ApplicationPolicyMode.Disabled });
        Reject(() => runtime.Apply(signed, now));
        runtime.VerifyCanRemove();
        Expect(runtime.RestoreForRemoval() == 1 && store.State is
            { Revision: 1, Pending: false, Policy.Mode: ApplicationPolicyMode.Disabled });
        Expect(backend.Local == store.State!.OriginalXml);

        var composedBackend = new Backend();
        var composedStore = new Store();
        var composedRuntime = new ApplicationPolicyRuntime(composedBackend, composedStore, "demo",
            key.ExportSubjectPublicKeyInfoPem());
        composedRuntime.SetStudentSoftwareRestriction(["S-1-5-21-1-2-3-1001"], true);
        Expect(composedStore.State is { Revision: 0, Pending: false, SoftwareRestrictionStudentSids.Count: 1 } &&
               composedBackend.PathVerificationCalls == 1 &&
               ApplicationPolicyRuntime.RequiresApplicationPolicyCapabilityForUpdate(composedStore.State) &&
               composedBackend.Local.Contains("EnforcementMode=\"Enabled\"") &&
               composedBackend.Local.Contains("%WINDIR%\\Temp\\*", StringComparison.Ordinal) &&
               composedBackend.Local.Contains("S-1-5-32-544", StringComparison.Ordinal));
        composedRuntime.Apply(signed, now);
        Expect(composedStore.State is { Revision: 1, Policy.Mode: ApplicationPolicyMode.Audit } &&
               composedBackend.Local.Contains("EnforcementMode=\"Enabled\"") &&
               !composedBackend.Local.Contains("FileHashRule", StringComparison.Ordinal));
        var enforced = policy with { Revision = 2, Mode = ApplicationPolicyMode.Enforce, IssuedUtc = now.AddMinutes(1),
            ExpiresUtc = now.AddHours(1) };
        composedRuntime.Apply(ApplicationPolicyCryptography.Sign(enforced, key), now.AddMinutes(1));
        Expect(composedBackend.Local.Contains("EnforcementMode=\"Enabled\"") &&
               composedBackend.Local.Contains("FileHashRule", StringComparison.Ordinal));
        composedRuntime.RestoreForRemoval();
        Expect(composedStore.State is { Policy.Mode: ApplicationPolicyMode.Disabled } &&
               composedBackend.Local.Contains("%PROGRAMFILES%\\*", StringComparison.Ordinal) &&
               !composedBackend.Local.Contains("FileHashRule", StringComparison.Ordinal));
        composedRuntime.SetStudentSoftwareRestriction([], false);
        Expect(composedBackend.Local == composedStore.State!.OriginalXml &&
               composedStore.State.SoftwareRestrictionStudentSids is null &&
               !ApplicationPolicyRuntime.RequiresApplicationPolicyCapabilityForUpdate(composedStore.State));

        var unknown = new Backend { Local = external };
        var untouched = new Store();
        Reject(() => new ApplicationPolicyRuntime(unknown, untouched, "demo", key.ExportSubjectPublicKeyInfoPem()).Apply(signed, now));
        Expect(untouched.State is null && unknown.Writes == 0);
        var managed = new Backend { EffectiveOverride = external };
        Reject(() => new ApplicationPolicyRuntime(managed, new Store(), "demo", key.ExportSubjectPublicKeyInfoPem()).Apply(signed, now));
        Expect(managed.Writes == 0);

        // A write may succeed while its response is lost. The persisted intent
        // permits restart recovery, but never installs an already expired intent.
        var ambiguous = new Backend { FailAfterWrite = true };
        var pending = new Store();
        var recovery = new ApplicationPolicyRuntime(ambiguous, pending, "demo", key.ExportSubjectPublicKeyInfoPem());
        Reject(() => recovery.Apply(signed, now));
        Expect(pending.State is { Pending: true });
        ambiguous.FailAfterWrite = false;
        recovery.ExpireIfDue(now.AddHours(2));
        Expect(pending.State is { Pending: false, Revision: 1, Policy.Mode: ApplicationPolicyMode.Disabled } && !ambiguous.Local.Contains("FileHashRule"));
        var refused = new Store { FailSave = true };
        var safe = new Backend();
        Reject(() => new ApplicationPolicyRuntime(safe, refused, "demo", key.ExportSubjectPublicKeyInfoPem()).Apply(signed, now));
        Expect(safe.Writes == 0);
        var raced = new Backend();
        var racingStore = new Store { AfterSave = () => raced.Local = external };
        Reject(() => new ApplicationPolicyRuntime(raced, racingStore, "demo", key.ExportSubjectPublicKeyInfoPem()).Apply(signed, now));
        Expect(raced.Writes == 0 && raced.Local == external && racingStore.State is { Pending: true });
        racingStore.AfterSave = null;
        Reject(() => new ApplicationPolicyRuntime(raced, racingStore, "demo", key.ExportSubjectPublicKeyInfoPem()).ExpireIfDue(now.AddHours(2)));

        using var auditKey = RSA.Create(2048);
        var auditRequest = new ApplicationPolicyAuditRequest(1, ApplicationPolicyAuditCryptography.Purpose, "demo",
            Guid.NewGuid(), now, 24);
        var signedAudit = ApplicationPolicyAuditCryptography.SignRequest(auditRequest, auditKey);
        Expect(ApplicationPolicyAuditCryptography.VerifyRequest(signedAudit, auditKey.ExportSubjectPublicKeyInfoPem(),
            "demo", now.AddMinutes(1)) == auditRequest);
        Reject(() => ApplicationPolicyAuditCryptography.VerifyRequest(signedAudit, auditKey.ExportSubjectPublicKeyInfoPem(),
            "other-campus", now.AddMinutes(1)));
        var auditedPolicy = policy with { Rules = policy.Rules, StudentSids = policy.StudentSids };
        var simulationState = new ApplicationPolicyRuntimeState("demo", 1, auditedPolicy,
            "<AppLockerPolicy Version=\"1\" />",
            ApplicationPolicyCompiler.CompileXml(auditedPolicy, [new string('C', 64)],
                ["S-1-5-21-1-2-3-1001"], ["S-1-5-21-1-2-3-2001"]), false, null,
            ["S-1-5-21-1-2-3-1001"], ["S-1-5-21-1-2-3-2001"]);
        var simulation = ApplicationPolicyAuditReader.Simulate(simulationState,
            [new ApplicationInventoryItem("Game", @"C:\\Program Files\\Game\\game.exe", "game.exe",
                null, null, null, new string('D', 64), new string('B', 64), 100)], auditRequest, now);
        Expect(simulation.IsSimulation && simulation.Results.Count == 1 &&
               simulation.Results[0].WouldBlockCount == 1 && simulation.Results[0].BlockedCount == 0 &&
               !string.IsNullOrWhiteSpace(simulation.CoverageNote));
        var ruleId = ApplicationPolicyCompiler.CompiledRuleId("demo", "S-1-5-21-1-2-3-1001", policy.Rules[0].Id);
        var auditState = new ApplicationPolicyRuntimeState("demo", 1, auditedPolicy, backend.Local,
            ApplicationPolicyCompiler.CompileXml(auditedPolicy, [new string('C', 64)]), false);
        var summary = ApplicationPolicyAuditReader.Read(auditState,
            [new ApplicationPolicyAuditEvent(now, 8003, "S-1-5-21-1-2-3-1001", ruleId),
             new ApplicationPolicyAuditEvent(now, 8004, "S-1-5-21-1-2-3-1001", ruleId),
             new ApplicationPolicyAuditEvent(now, 8003, "S-1-5-21-1-2-3-1002", ruleId),
             new ApplicationPolicyAuditEvent(now.AddDays(-2), 8003, "S-1-5-21-1-2-3-1001", ruleId)],
            auditRequest, now);
        Expect(summary.Results.Count == 1 && summary.Results[0].RuleId == ruleId &&
               summary.Results[0].WouldBlockCount == 1 && summary.Results[0].BlockedCount == 1);
    }
    private sealed class Store : IApplicationPolicyStateStore
    {
        public ApplicationPolicyRuntimeState? State;
        public bool FailSave;
        public Action? AfterSave;
        public ApplicationPolicyRuntimeState? Read() => State;
        public void Save(ApplicationPolicyRuntimeState state)
        { if (FailSave) throw new IOException("save failure"); State = state; AfterSave?.Invoke(); }
    }
    private sealed class Backend : IApplicationPolicyBackend
    {
        public string Local = "<AppLockerPolicy Version=\"1\"><RuleCollection Type=\"Exe\" EnforcementMode=\"NotConfigured\" /></AppLockerPolicy>";
        public string? EffectiveOverride;
        public int Writes;
        public int PathVerificationCalls;
        public bool FailAfterWrite;
        public Action? BeforeWrite;
        public void VerifyEnvironmentAndStudents(IReadOnlyList<string> sids) { }
        public void VerifyStudentSoftwareAllowPaths(IReadOnlyList<string> sids) => PathVerificationCalls++;
        public IReadOnlyCollection<string> ReadNonStudentLocalAccountSids(IReadOnlyList<string> sids) =>
            ["S-1-5-21-1-2-3-2001"];
        public string ReadLocalPolicyXml() => Local;
        public string ReadEffectivePolicyXml() => EffectiveOverride ?? Local;
        public IReadOnlyCollection<string> ReadProtectedAppLockerHashes() => [new string('C', 64)];
        public void WriteLocalPolicyXml(string xml)
        { BeforeWrite?.Invoke(); Writes++; Local = xml; if (FailAfterWrite) throw new IOException("response lost"); }
    }
    private static void Expect(bool condition) { if (!condition) throw new Exception("Application policy runtime check failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { return; } throw new Exception("Unsafe application policy mutation accepted."); }
}
