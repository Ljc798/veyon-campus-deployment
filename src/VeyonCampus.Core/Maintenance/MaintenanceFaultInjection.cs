using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.Core.Maintenance;

/// <summary>
/// P3-03 fault-injection seam. Production default is <see cref="SystemClock"/> /
/// <see cref="RealMaintenanceFaultProbe"/>; tests inject faults through
/// <see cref="FaultyMaintenanceProbe"/>. Only the journal/recovery/transaction surfaces
/// that compile on every platform are exercised through this seam — Windows-only
/// service/SCM/registry facts stay on the real code paths (see the FIELD-PENDING
/// checklist in docs/maintenance).
/// </summary>
public interface IMaintenanceClock
{
    DateTimeOffset NowUtc { get; }
}

public sealed class SystemClock : IMaintenanceClock
{
    public DateTimeOffset NowUtc => DateTimeOffset.UtcNow;
}

public sealed class FixedClock : IMaintenanceClock
{
    public DateTimeOffset NowUtc { get; private set; }
    public FixedClock(DateTimeOffset nowUtc) => NowUtc = MaintenanceValidation.Utc(nowUtc);
    public void Advance(TimeSpan delta) => NowUtc += delta;
}

/// <summary>
/// Readable transaction/recovery facts for a maintenance device, without executing side effects.
/// </summary>
public interface IMaintenanceFaultProbe
{
    /// <summary>
    /// Journal-level facts: active (non-terminal) runs, and interrupted (non-terminal)
    /// update transactions for the maintenance service itself.
    /// </summary>
    MaintenanceFaultObservation Observe(MaintenanceDeviceKey device, MaintenanceJournal journal);

    /// <summary>Read the persisted transaction state, and reconcile it to a terminal state when the helper is gone.</summary>
    string? ReconcileInterruptedTransaction(MaintenanceDeviceKey device, Guid commandId,
        IMaintenanceClock clock, out string terminalState);

    /// <summary>Whether two teachers could interleave maintenance writes for this device.</summary>
    bool IsConcurrentWriteHazard(MaintenanceDeviceKey device, Guid operationId, MaintenanceJournal journal);

    /// <summary>True when the previous maintenance-service version's files are still usable.</summary>
    bool CanRecoverToPrevious(MaintenanceDeviceKey device, string previousVersion);
}

public sealed record MaintenanceFaultObservation(
    IReadOnlyList<Guid> ActiveOperationIds,
    bool HasInterruptedServiceUpdate,
    bool RejectedAccess);

/// <summary>
/// The production probe wraps the real journal and (on Windows) the maintenance
/// update journal. It never hides a fault: unresolvable evidence comes back as
/// "review required", which the teacher UI must render as RecoveryRequired,
/// never as Completed.
/// </summary>
public sealed class RealMaintenanceFaultProbe : IMaintenanceFaultProbe
{
    private readonly Func<MaintenanceDeviceKey, MaintenanceServiceReleaseUpdateTransaction?> _releaseTransactionReader;

    public RealMaintenanceFaultProbe() =>
        _releaseTransactionReader = _ => null;

    internal RealMaintenanceFaultProbe(
        Func<MaintenanceDeviceKey, MaintenanceServiceReleaseUpdateTransaction?> releaseTransactionReader) =>
        _releaseTransactionReader = releaseTransactionReader;

    public MaintenanceFaultObservation Observe(MaintenanceDeviceKey device, MaintenanceJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var active = journal.Runs.Where(run => run.Device.Key == device && !run.IsTerminal)
            .Select(run => run.OperationId).ToArray();
        var release = _releaseTransactionReader(device);
        var interrupted = release is { State: "pending" or "inProgress" };
        return new MaintenanceFaultObservation(active, interrupted, false);
    }

    public string ReconcileInterruptedTransaction(MaintenanceDeviceKey device, Guid commandId,
        IMaintenanceClock clock, out string terminalState)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var release = _releaseTransactionReader(device);
        if (release is null || release.CommandId != commandId ||
            release.State is not ("pending" or "inProgress"))
        {
            terminalState = "notFound";
            return "notFound";
        }
        terminalState = "needsReview";
        return "needsReview";
    }

    public bool IsConcurrentWriteHazard(MaintenanceDeviceKey device, Guid operationId, MaintenanceJournal journal) =>
        journal.Runs.Any(run => run.Device.Key == device && !run.IsTerminal && run.OperationId != operationId);

    public bool CanRecoverToPrevious(MaintenanceDeviceKey device, string previousVersion) =>
        string.Equals(previousVersion, "", StringComparison.Ordinal) == false &&
        ApplicationReleaseClient.CompareVersions(previousVersion, previousVersion) >= 0;
}

/// <summary>Test probe: every fact is a scripted input so scenarios can assert deterministically.</summary>
public sealed class FaultyMaintenanceProbe : IMaintenanceFaultProbe
{
    public readonly record struct Scenario(
        IReadOnlyList<Guid> ActiveOperationIds,
        bool HasInterruptedServiceUpdate,
        bool RejectedAccess,
        string ReconciledTerminalState,
        bool ReconcileVisible,
        bool ConcurrentWriteHazard,
        bool PreviousVersionRecoverable);

    public Scenario? Script { get; set; }

    public MaintenanceFaultObservation Observe(MaintenanceDeviceKey device, MaintenanceJournal journal)
    {
        var script = Script ?? throw new InvalidOperationException("Fault scenario not scripted.");
        return new MaintenanceFaultObservation(
            script.ActiveOperationIds, script.HasInterruptedServiceUpdate, script.RejectedAccess);
    }

    public string ReconcileInterruptedTransaction(MaintenanceDeviceKey device, Guid commandId,
        IMaintenanceClock clock, out string terminalState)
    {
        var script = Script ?? throw new InvalidOperationException("Fault scenario not scripted.");
        terminalState = script.ReconcileVisible ? script.ReconciledTerminalState : "notFound";
        return terminalState;
    }

    public bool IsConcurrentWriteHazard(MaintenanceDeviceKey device, Guid operationId, MaintenanceJournal journal)
    {
        var script = Script ?? throw new InvalidOperationException("Fault scenario not scripted.");
        return script.ConcurrentWriteHazard;
    }

    public bool CanRecoverToPrevious(MaintenanceDeviceKey device, string previousVersion)
    {
        var script = Script ?? throw new InvalidOperationException("Fault scenario not scripted.");
        return script.PreviousVersionRecoverable;
    }
}

/// <summary>
/// Recovery decision used by both the teacher-side journal and the service-side
/// reconciliation: after a restart the run must never surface "completed".
/// </summary>
public static class MaintenanceFaultRecovery
{
    /// <summary>
    /// After a kill/power-loss, a run whose update stage was interrupted lands in
    /// RecoveryRequired instead of Completed. Returns the terminal-safe run state
    /// for the device given the observed facts.
    /// </summary>
    public static MaintenanceRunState ClassifyRecovery(MaintenanceFaultObservation observation,
        string? reconciledTransactionState)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.RejectedAccess)
            return MaintenanceRunState.ManualRequired;
        if (observation.HasInterruptedServiceUpdate)
        {
            switch (reconciledTransactionState)
            {
                case "committed":
                case "rolledBack":
                    return MaintenanceRunState.Completed;
                case null:
                case "notFound":
                    return MaintenanceRunState.RecoveryRequired;
                default:
                    return MaintenanceRunState.RecoveryRequired;
            }
        }
        if (observation.ActiveOperationIds.Count > 0)
            return MaintenanceRunState.RecoveryRequired;
        return MaintenanceRunState.Completed;
    }

    /// <summary>
    /// A PendingVerification / RecoveryRequired / Unknown state must not start a
    /// side-effecting command. Unknown (unbound evidence) is treated as refused.
    /// </summary>
    public static bool MayStartSideEffectingStage(MaintenanceRunState state, string? verifiedTransactionState)
    {
        if (state is MaintenanceRunState.PendingVerification or MaintenanceRunState.RecoveryRequired or
            MaintenanceRunState.Failed or MaintenanceRunState.ManualRequired)
            return false;
        // "Unknown" / "needsReview" readback: the device did not confirm the outcome.
        if (verifiedTransactionState is "needsReview" or "inProgress" or null)
            return state is MaintenanceRunState.Discovered or MaintenanceRunState.IdentityVerified or
                       MaintenanceRunState.CapabilitiesVerified or MaintenanceRunState.PlanReady;
        return state is MaintenanceRunState.PlanReady or MaintenanceRunState.UpdateOffered or
               MaintenanceRunState.Updating or MaintenanceRunState.WaitingForCommit or
               MaintenanceRunState.AgentHealthVerified or MaintenanceRunState.MaintenanceServiceHealthVerified or
               MaintenanceRunState.ConfigurationSyncing;
    }

    /// <summary>
    /// The journal is the durable source of truth across a restart. Given the observed
    /// active runs, the second teacher must be refused; only the first active run can
    /// continue.
    /// </summary>
    public static bool RefuseSecondConcurrentOperation(MaintenanceJournal journal, MaintenanceDeviceKey device,
        Guid requestingOperationId)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(device);
        var active = journal.Runs.Where(run => run.Device.Key == device && !run.IsTerminal).ToArray();
        if (active.Length == 0)
            return false; // no conflict; the request can create the active run.
        return active.Any(run => run.OperationId != requestingOperationId);
    }

    /// <summary>
    /// Cross-version safety matrix: an old-protocol client that keeps its trust root must
    /// still reject an unknown target version and a bad signature. The signature check
    /// is deliberately a real RSA verification — the "old protocol minimum compatibility
    /// negotiation" rule is that the trust root, not the protocol constant, is the gate.
    /// </summary>
    public sealed record CrossVersionFacts(
        string CurrentServiceVersion,
        string UnknownTargetVersion,
        bool SignatureVerifies,
        bool TrustRootRetained);

    public static string NegotiateCrossVersionUpdate(CrossVersionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        // A candidate that drops the retained trust root is refused even if everything else matches.
        if (!facts.TrustRootRetained)
            return "refused:trust-root-lost";
        if (!facts.SignatureVerifies)
            return "refused:signature";
        // An unknown target version is one that the running service cannot validate;
        // CompareVersions throws for unparseable versions, and that refusal must win.
        try
        {
            _ = ApplicationReleaseClient.CompareVersions(facts.UnknownTargetVersion,
                facts.CurrentServiceVersion);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return "refused:unknown-target-version";
        }
        return "accepted";
    }
}

/// <summary>
/// A signed maintenance command for the cross-version matrix. The signature is real RSA-PSS;
/// "old protocol minimum compatibility" = the pinned trust root still verifies the payload
/// even when the protocol version is the legacy minimum.
/// </summary>
public sealed class MaintenanceSignedCommandEnvelope
{
    public int ProtocolVersion { get; }
    public string Payload { get; }
    public string Signature { get; }
    public string TrustRootFingerprint { get; }

    public MaintenanceSignedCommandEnvelope(int protocolVersion, string payload, string signature,
        string trustRootFingerprint)
    {
        if (protocolVersion < 1)
            throw new ArgumentException("维护协议版本无效。", nameof(protocolVersion));
        ProtocolVersion = protocolVersion;
        Payload = MaintenanceValidation.RequiredText(payload, nameof(payload), 4096);
        Signature = signature;
        TrustRootFingerprint = MaintenanceValidation.Fingerprint(trustRootFingerprint,
            nameof(trustRootFingerprint));
    }

    public static (MaintenanceSignedCommandEnvelope, string PublicPem) Issue(int protocolVersion,
        string payload, RSA signingKey, string trustRootFingerprint)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        var raw = System.Text.Encoding.UTF8.GetBytes(payload);
        var signature = System.Convert.ToBase64String(
            signingKey.SignData(raw, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return (new MaintenanceSignedCommandEnvelope(protocolVersion, payload, signature,
            trustRootFingerprint), signingKey.ExportSubjectPublicKeyInfoPem());
    }

    public bool Verifies(RSA trustRoot)
    {
        ArgumentNullException.ThrowIfNull(trustRoot);
        try
        {
            var raw = System.Text.Encoding.UTF8.GetBytes(Payload);
            return trustRoot.VerifyData(raw, System.Convert.FromBase64String(Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
