namespace VeyonCampus.Core.Maintenance;

public enum MaintenanceConfigurationModule
{
    CampusIdentity,
    TeacherConnectivity,
    PolicyTrust,
    SystemConfiguration,
    RuntimeSettings
}

public enum MaintenanceConfigurationApplyState
{
    Unknown,
    Pending,
    Applied,
    Rejected
}

public enum MaintenanceConfigurationReconciliationState
{
    NoDesiredConfiguration,
    AwaitingReport,
    PendingApplication,
    Rejected,
    Drifted,
    InSync
}

public sealed record MaintenanceConfigurationReconciliationStatus(
    MaintenanceConfigurationReconciliationState State,
    long? DesiredRevision,
    long? ReportedRevision,
    string? DesiredFingerprint,
    string? ReportedFingerprint,
    DateTimeOffset? LastReportedUtc);

/// <summary>Teacher-authoritative configuration intent for one fixed device and module.</summary>
public sealed record DesiredMaintenanceConfiguration
{
    public string DeviceIdentityFingerprint { get; }
    public MaintenanceConfigurationModule Module { get; }
    public int SchemaVersion { get; }
    public long Revision { get; }
    public string Fingerprint { get; }
    public string SignerFingerprint { get; }
    public string SignedEnvelopeSha256 { get; }
    public DateTimeOffset IssuedUtc { get; }

    public DesiredMaintenanceConfiguration(string deviceIdentityFingerprint,
        MaintenanceConfigurationModule module, int schemaVersion, long revision, string fingerprint,
        string signerFingerprint, string signedEnvelopeSha256, DateTimeOffset issuedUtc)
    {
        DeviceIdentityFingerprint = MaintenanceValidation.Fingerprint(deviceIdentityFingerprint,
            nameof(deviceIdentityFingerprint));
        if (!Enum.IsDefined(module)) throw new ArgumentOutOfRangeException(nameof(module));
        Module = module;
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        SchemaVersion = schemaVersion;
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
        Fingerprint = MaintenanceValidation.Fingerprint(fingerprint, nameof(fingerprint));
        SignerFingerprint = MaintenanceValidation.Fingerprint(signerFingerprint, nameof(signerFingerprint));
        SignedEnvelopeSha256 = MaintenanceValidation.Fingerprint(signedEnvelopeSha256,
            nameof(signedEnvelopeSha256));
        IssuedUtc = MaintenanceValidation.Utc(issuedUtc);
    }
}

/// <summary>Student-reported configuration fact, accepted only with verified signed response evidence.</summary>
public sealed record ReportedMaintenanceConfiguration
{
    public string DeviceIdentityFingerprint { get; }
    public MaintenanceConfigurationModule Module { get; }
    public int SchemaVersion { get; }
    public long Revision { get; }
    public string Fingerprint { get; }
    public MaintenanceConfigurationApplyState ApplyState { get; }
    public string SignedResponseSha256 { get; }
    public DateTimeOffset CollectedUtc { get; }

    public ReportedMaintenanceConfiguration(string deviceIdentityFingerprint,
        MaintenanceConfigurationModule module, int schemaVersion, long revision, string fingerprint,
        MaintenanceConfigurationApplyState applyState, string signedResponseSha256, DateTimeOffset collectedUtc)
    {
        DeviceIdentityFingerprint = MaintenanceValidation.Fingerprint(deviceIdentityFingerprint,
            nameof(deviceIdentityFingerprint));
        if (!Enum.IsDefined(module)) throw new ArgumentOutOfRangeException(nameof(module));
        Module = module;
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        SchemaVersion = schemaVersion;
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
        Fingerprint = MaintenanceValidation.Fingerprint(fingerprint, nameof(fingerprint));
        if (!Enum.IsDefined(applyState)) throw new ArgumentOutOfRangeException(nameof(applyState));
        ApplyState = applyState;
        SignedResponseSha256 = MaintenanceValidation.Fingerprint(signedResponseSha256,
            nameof(signedResponseSha256));
        CollectedUtc = MaintenanceValidation.Utc(collectedUtc);
    }
}

public static class MaintenanceConfigurationReconciliation
{
    public static MaintenanceConfigurationReconciliationStatus GetStatus(
        DesiredMaintenanceConfiguration? desired, ReportedMaintenanceConfiguration? reported)
    {
        if (desired is null)
            return new MaintenanceConfigurationReconciliationStatus(
                MaintenanceConfigurationReconciliationState.NoDesiredConfiguration,
                null, reported?.Revision, null, reported?.Fingerprint, reported?.CollectedUtc);
        if (reported is null)
            return new MaintenanceConfigurationReconciliationStatus(
                MaintenanceConfigurationReconciliationState.AwaitingReport,
                desired.Revision, null, desired.Fingerprint, null, null);
        var state = reported.ApplyState switch
        {
            MaintenanceConfigurationApplyState.Pending => MaintenanceConfigurationReconciliationState.PendingApplication,
            MaintenanceConfigurationApplyState.Rejected => MaintenanceConfigurationReconciliationState.Rejected,
            MaintenanceConfigurationApplyState.Unknown => MaintenanceConfigurationReconciliationState.AwaitingReport,
            MaintenanceConfigurationApplyState.Applied when Matches(desired, reported) =>
                MaintenanceConfigurationReconciliationState.InSync,
            _ => MaintenanceConfigurationReconciliationState.Drifted
        };
        return new MaintenanceConfigurationReconciliationStatus(state, desired.Revision, reported.Revision,
            desired.Fingerprint, reported.Fingerprint, reported.CollectedUtc);
    }

    public static bool Matches(DesiredMaintenanceConfiguration desired,
        ReportedMaintenanceConfiguration reported)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(reported);
        return desired.DeviceIdentityFingerprint == reported.DeviceIdentityFingerprint &&
               desired.Module == reported.Module && desired.SchemaVersion == reported.SchemaVersion &&
               desired.Revision == reported.Revision && desired.Fingerprint == reported.Fingerprint &&
               reported.ApplyState == MaintenanceConfigurationApplyState.Applied &&
               reported.CollectedUtc >= desired.IssuedUtc;
    }
}
