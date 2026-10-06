using System.Xml.Linq;

namespace VeyonCampus.Core;

/// <summary>Windows adapter must verify service, MDM, account scope and protected binaries before any write.</summary>
public interface IApplicationPolicyBackend
{
    void VerifyEnvironmentAndStudents(IReadOnlyList<string> studentSids);
    IReadOnlyCollection<string> ReadNonStudentLocalAccountSids(IReadOnlyList<string> studentSids);
    string ReadLocalPolicyXml();
    string ReadEffectivePolicyXml();
    IReadOnlyCollection<string> ReadProtectedAppLockerHashes();
    void WriteLocalPolicyXml(string xml);
}

public sealed record ApplicationPolicyRuntimeState(string CampusId, long Revision,
    ApplicationPolicyDocument Policy, string OriginalXml, string InstalledXml,
    bool Pending, string? PendingPreviousXml = null,
    IReadOnlyList<string>? SoftwareRestrictionStudentSids = null,
    IReadOnlyList<string>? SoftwareRestrictionAllowedSids = null);

/// <summary>State must live in an atomic, SYSTEM/Administrators-only store; Save must complete before Windows writes.</summary>
public interface IApplicationPolicyStateStore
{
    ApplicationPolicyRuntimeState? Read();
    void Save(ApplicationPolicyRuntimeState state);
}

/// <summary>Owns the local AppLocker policy shared by classroom application rules and the student software baseline.</summary>
public sealed class ApplicationPolicyRuntime(IApplicationPolicyBackend backend, IApplicationPolicyStateStore store,
    string campusId, string? publicKeyPem)
    : IStudentSoftwareExecutionPolicyCoordinator
{
    public void Apply(string signedEnvelope, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
            throw new InvalidOperationException("此学生端只启用了系统软件限制，没有课堂应用策略信任公钥。");
        var state = Reconcile(nowUtc);
        var policy = ApplicationPolicyCryptography.Verify(signedEnvelope, publicKeyPem, campusId, state?.Revision ?? 0, nowUtc);
        backend.VerifyEnvironmentAndStudents(OperationalStudentSids(state, policy.StudentSids));
        var current = CanonicalXml(backend.ReadLocalPolicyXml());
        var effective = CanonicalXml(backend.ReadEffectivePolicyXml());
        if (state is null)
        {
            RequireEmpty(current);
            RequireEmpty(effective);
        }
        else VerifyOwnership(state, current, effective);
        var original = state?.OriginalXml ?? current;
        var baselineStudents = state?.SoftwareRestrictionStudentSids;
        var baselineAllowed = state?.SoftwareRestrictionAllowedSids;
        var installed = CompileInstalledPolicy(policy, original, baselineStudents, baselineAllowed);
        var next = new ApplicationPolicyRuntimeState(campusId, policy.Revision, policy, original, installed, true, current,
            baselineStudents, baselineAllowed);
        Commit(next);
    }

    public void SetStudentSoftwareRestriction(IReadOnlyList<string> studentSids, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(studentSids);
        var state = Reconcile(DateTimeOffset.UtcNow);
        var targetStudents = enabled ? studentSids.Distinct(StringComparer.Ordinal).ToArray() : [];
        if (enabled && (targetStudents.Length == 0 || targetStudents.Length != studentSids.Count ||
                        targetStudents.Length > ApplicationPolicyCompiler.MaximumStudents ||
                        targetStudents.Any(sid => !System.Text.RegularExpressions.Regex.IsMatch(sid,
                            @"^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$",
                            System.Text.RegularExpressions.RegexOptions.CultureInvariant))))
            throw new InvalidDataException("软件安装限制的学生 SID 清单无效。");
        if (!enabled && state?.SoftwareRestrictionStudentSids is not { Count: > 0 }) return;

        var current = CanonicalXml(backend.ReadLocalPolicyXml());
        var effective = CanonicalXml(backend.ReadEffectivePolicyXml());
        if (state is null)
        {
            RequireEmpty(current);
            RequireEmpty(effective);
        }
        else VerifyOwnership(state, current, effective);

        var policy = state?.Policy ?? CreateSoftwareOnlyPolicy(campusId);
        var original = state?.OriginalXml ?? current;
        var verifySids = (enabled ? targetStudents : [])
            .Concat(ActivePolicyStudentSids(state))
            .Distinct(StringComparer.Ordinal).ToArray();
        backend.VerifyEnvironmentAndStudents(Array.AsReadOnly(verifySids));
        var allowedSids = enabled ? backend.ReadNonStudentLocalAccountSids(targetStudents)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : null;
        if (enabled && state?.SoftwareRestrictionStudentSids is { } oldStudents &&
            oldStudents.SequenceEqual(targetStudents, StringComparer.Ordinal) &&
            state.SoftwareRestrictionAllowedSids is { } oldAllowed &&
            oldAllowed.SequenceEqual(allowedSids!, StringComparer.Ordinal))
            return;
        var installed = CompileInstalledPolicy(policy, original,
            enabled ? targetStudents : null, allowedSids);
        var next = new ApplicationPolicyRuntimeState(campusId, state?.Revision ?? 0, policy,
            original, installed, true, current,
            enabled ? Array.AsReadOnly(targetStudents) : null,
            enabled && allowedSids is not null ? Array.AsReadOnly(allowedSids) : null);
        Commit(next);
    }

    public bool ExpireIfDue(DateTimeOffset nowUtc)
    {
        var state = Reconcile(nowUtc);
        if (state is null) return false;
        backend.VerifyEnvironmentAndStudents(OperationalStudentSids(state));
        VerifyOwnership(state, CanonicalXml(backend.ReadLocalPolicyXml()), CanonicalXml(backend.ReadEffectivePolicyXml()));
        if (state.Policy.Mode == ApplicationPolicyMode.Disabled ||
            state.Policy.ExpiresUtc is not { } expires || expires > nowUtc) return false;
        // Retain the highest accepted revision so expiry cannot enable replay.
        var disabled = state.Policy with { Mode = ApplicationPolicyMode.Disabled, Rules = [], StudentSids = [], ExpiresUtc = null };
        Commit(state with { Policy = disabled,
            InstalledXml = CompileInstalledPolicy(disabled, state.OriginalXml,
                state.SoftwareRestrictionStudentSids, state.SoftwareRestrictionAllowedSids),
            Pending = true, PendingPreviousXml = state.InstalledXml });
        return true;
    }

    public void VerifyCanRemove()
    {
        var state = Reconcile(DateTimeOffset.UtcNow);
        if (state is null)
        {
            RequireEmpty(CanonicalXml(backend.ReadLocalPolicyXml()));
            RequireEmpty(CanonicalXml(backend.ReadEffectivePolicyXml()));
            return;
        }
        backend.VerifyEnvironmentAndStudents(OperationalStudentSids(state));
        VerifyOwnership(state, CanonicalXml(backend.ReadLocalPolicyXml()), CanonicalXml(backend.ReadEffectivePolicyXml()));
    }

    /// <summary>Removes classroom rules while preserving a system software baseline; retains the revision tombstone.</summary>
    public long RestoreForRemoval()
    {
        var state = Reconcile(DateTimeOffset.UtcNow);
        if (state is null)
        {
            RequireEmpty(CanonicalXml(backend.ReadLocalPolicyXml()));
            RequireEmpty(CanonicalXml(backend.ReadEffectivePolicyXml()));
            return 0;
        }
        backend.VerifyEnvironmentAndStudents(OperationalStudentSids(state));
        VerifyOwnership(state, CanonicalXml(backend.ReadLocalPolicyXml()), CanonicalXml(backend.ReadEffectivePolicyXml()));
        if (state.Policy.Mode != ApplicationPolicyMode.Disabled)
        {
            var disabled = state.Policy with { Mode = ApplicationPolicyMode.Disabled, Rules = [], StudentSids = [], ExpiresUtc = null };
            Commit(state with { Policy = disabled,
                InstalledXml = CompileInstalledPolicy(disabled, state.OriginalXml,
                    state.SoftwareRestrictionStudentSids, state.SoftwareRestrictionAllowedSids), Pending = true,
                PendingPreviousXml = state.InstalledXml });
        }
        return state.Revision;
    }

    public ApplicationPolicyRuntimeState? ReadForAudit(DateTimeOffset nowUtc)
    {
        var state = Reconcile(nowUtc);
        if (state is null) return null;
        backend.VerifyEnvironmentAndStudents(OperationalStudentSids(state));
        VerifyOwnership(state, CanonicalXml(backend.ReadLocalPolicyXml()), CanonicalXml(backend.ReadEffectivePolicyXml()));
        return state;
    }

    private ApplicationPolicyRuntimeState? Reconcile(DateTimeOffset nowUtc)
    {
        var state = store.Read();
        if (state is null) return null;
        if (state.CampusId != campusId || state.Policy.CampusId != campusId || state.Revision != state.Policy.Revision)
            throw new InvalidDataException("应用策略状态校区或版本不一致；没有修改 Windows。");
        ApplicationPolicyCompiler.Validate(state.Policy);
        ValidateSoftwareScope(state.SoftwareRestrictionStudentSids, state.SoftwareRestrictionAllowedSids);
        RequireEmpty(CanonicalXml(state.OriginalXml));
        if (!state.Pending) return state;
        if (state.PendingPreviousXml is null) throw new InvalidDataException("应用策略待恢复状态缺少原值。");
        backend.VerifyEnvironmentAndStudents(OperationalStudentSids(state));
        var local = CanonicalXml(backend.ReadLocalPolicyXml());
        var effective = CanonicalXml(backend.ReadEffectivePolicyXml());
        if (local != CanonicalXml(state.InstalledXml) && local != CanonicalXml(state.PendingPreviousXml))
            throw new IOException("应用策略中断后出现外部规则；停止自动恢复。");
        if (local != effective) throw new IOException("本机与有效 AppLocker 策略不同；停止修改外部管理策略。");
        if (state.Policy.ExpiresUtc is { } expiry && expiry <= nowUtc)
        {
            // Never briefly install an expired pending restriction on restart.
            var disabled = state.Policy with { Mode = ApplicationPolicyMode.Disabled, Rules = [], StudentSids = [], ExpiresUtc = null };
            state = state with { Policy = disabled,
                InstalledXml = CompileInstalledPolicy(disabled, state.OriginalXml,
                    state.SoftwareRestrictionStudentSids, state.SoftwareRestrictionAllowedSids), PendingPreviousXml = local };
            Commit(state);
            return state with { Pending = false, PendingPreviousXml = null };
        }
        if (local != CanonicalXml(state.InstalledXml))
            backend.WriteLocalPolicyXml(state.InstalledXml);
        VerifyReadback(state.InstalledXml);
        state = state with { Pending = false, PendingPreviousXml = null };
        store.Save(state);
        return state;
    }

    private void Commit(ApplicationPolicyRuntimeState state)
    {
        store.Save(state);
        var expectedPrevious = CanonicalXml(state.PendingPreviousXml ?? throw new InvalidDataException("应用策略事务缺少原值。"));
        if (CanonicalXml(backend.ReadLocalPolicyXml()) != expectedPrevious ||
            CanonicalXml(backend.ReadEffectivePolicyXml()) != expectedPrevious)
            throw new IOException("持久化期间 AppLocker 策略发生外部变更；没有覆盖该变更。");
        // A failed/ambiguous write leaves a durable pending transaction. Restart
        // accepts only the original or intended value before finishing it.
        backend.WriteLocalPolicyXml(state.InstalledXml);
        VerifyReadback(state.InstalledXml);
        store.Save(state with { Pending = false, PendingPreviousXml = null });
    }

    private void VerifyReadback(string expected)
    {
        if (CanonicalXml(backend.ReadLocalPolicyXml()) != CanonicalXml(expected) ||
            CanonicalXml(backend.ReadEffectivePolicyXml()) != CanonicalXml(expected))
            throw new IOException("AppLocker 写入后本机/有效策略读回不符；保留恢复状态，不能报告已应用。");
    }
    private static void VerifyOwnership(ApplicationPolicyRuntimeState state, string local, string effective)
    {
        if (local != CanonicalXml(state.InstalledXml) || effective != local)
            throw new IOException("AppLocker 策略被学校或管理员修改；没有覆盖或删除外部规则。");
    }
    private static void RequireEmpty(string xml)
    {
        var root = XDocument.Parse(xml).Root!;
        if (root.Elements().Any()) throw new IOException("检测到现有 AppLocker 策略；没有接管学校规则。");
    }

    internal static bool IsSoftwareOnlyState(ApplicationPolicyRuntimeState state) =>
        state.Revision == 0 && state.Policy.Revision == 0 && state.Policy.Mode == ApplicationPolicyMode.Disabled &&
        state.Policy.Rules.Count == 0 && state.Policy.StudentSids.Count == 0 && state.Policy.ExpiresUtc is null;

    private static ApplicationPolicyDocument CreateSoftwareOnlyPolicy(string campusId) =>
        new(1, ApplicationPolicyCompiler.Purpose, campusId, 0, DateTimeOffset.UtcNow, null,
            ApplicationPolicyMode.Disabled, [], []);

    private string CompileInstalledPolicy(ApplicationPolicyDocument policy, string original,
        IReadOnlyCollection<string>? baselineStudents, IReadOnlyCollection<string>? baselineAllowed)
    {
        if (policy.Mode == ApplicationPolicyMode.Disabled && baselineStudents is not { Count: > 0 })
            return original;
        var hashes = policy.Mode == ApplicationPolicyMode.Disabled
            ? Array.Empty<string>() : backend.ReadProtectedAppLockerHashes();
        return CanonicalXml(ApplicationPolicyCompiler.CompileXml(policy, hashes, baselineStudents, baselineAllowed));
    }

    private static IReadOnlyList<string> OperationalStudentSids(ApplicationPolicyRuntimeState? state,
        IReadOnlyList<string>? additional = null) => Array.AsReadOnly((additional ?? [])
        .Concat(ActivePolicyStudentSids(state))
        .Concat(state?.SoftwareRestrictionStudentSids ?? [])
        .Distinct(StringComparer.Ordinal).ToArray());

    private static IReadOnlyList<string> ActivePolicyStudentSids(ApplicationPolicyRuntimeState? state) =>
        state is { Policy.Mode: ApplicationPolicyMode.Audit or ApplicationPolicyMode.Enforce }
            ? state.Policy.StudentSids : Array.Empty<string>();

    internal static void ValidateSoftwareScope(IReadOnlyList<string>? students, IReadOnlyList<string>? allowed)
    {
        if (students is null)
        {
            if (allowed is { Count: > 0 }) throw new InvalidDataException("持久应用策略包含无归属的豁免账户。");
            return;
        }
        if (students.Count is 0 or > ApplicationPolicyCompiler.MaximumStudents ||
            students.Distinct(StringComparer.Ordinal).Count() != students.Count ||
            students.Any(sid => !System.Text.RegularExpressions.Regex.IsMatch(sid,
                @"^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)) ||
            allowed is null || allowed.Count > 1024 || allowed.Distinct(StringComparer.Ordinal).Count() != allowed.Count ||
            allowed.Any(sid => !System.Text.RegularExpressions.Regex.IsMatch(sid,
                @"^S-1-5-21-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-(0|[1-9][0-9]{0,9})-([1-9][0-9]{0,9})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)) ||
            students.Intersect(allowed, StringComparer.Ordinal).Any())
            throw new InvalidDataException("持久应用策略的软件限制账户范围无效。");
    }
    internal static string CanonicalXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("AppLocker XML 大小无效。");
        try
        {
            using var reader = System.Xml.XmlReader.Create(new StringReader(xml), new System.Xml.XmlReaderSettings
                { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
            var root = XDocument.Load(reader).Root;
            if (root is null || root.Name != "AppLockerPolicy" || (string?)root.Attribute("Version") != "1")
                throw new InvalidDataException("AppLocker XML 根节点无效。");
            if (root.DescendantNodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
                throw new InvalidDataException("AppLocker XML 包含未知文本内容。");
            foreach (var collection in root.Elements("RuleCollection").ToArray())
            {
                if (!collection.HasElements && (string?)collection.Attribute("EnforcementMode") == "NotConfigured" &&
                    (string?)collection.Attribute("Type") is "Exe" or "Dll" or "Msi" or "Script" or "Appx" &&
                    collection.Attributes().All(attribute => attribute.Name == "Type" || attribute.Name == "EnforcementMode"))
                    collection.Remove();
            }
            // Windows may reorder rules and attributes on readback. Compare their
            // structure while retaining every rule, condition and enforcement value.
            return Normalize(root).ToString(SaveOptions.DisableFormatting);
        }
        catch (System.Xml.XmlException ex) { throw new InvalidDataException("AppLocker XML 无效。", ex); }
    }
    private static XElement Normalize(XElement element) => new(element.Name,
        element.Attributes().OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal)
            .Select(attribute => new XAttribute(attribute.Name, attribute.Value)),
        element.Elements().Select(Normalize).OrderBy(child => child.ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal));
}
