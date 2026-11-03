using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeyonCampus.Core.Maintenance;

public sealed record MaintenanceEventReport(int SchemaVersion, DateTimeOffset ExportedUtc,
    IReadOnlyList<MaintenanceEventReportEntry> Events);

public sealed record MaintenanceEventReportEntry(int EventNumber, string DeviceAlias,
    MaintenanceOperationKind? OperationKind, string FinalState, string? FailureStage,
    DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc, long DurationMilliseconds,
    int? ProtocolVersion, string ProtocolMode, MaintenanceReleaseChannel? ReleaseChannel,
    MaintenanceComponentVersions? TargetVersions, MaintenanceComponentVersions? ActualVersions,
    string? UpdateTransactionState);

/// <summary>Creates an aggregate-safe export that excludes identities, routes, free text and operation IDs.</summary>
public static class MaintenanceEventReportBuilder
{
    public const int MaximumEventCount = 500;
    public const int MaximumReportBytes = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter<MaintenanceOperationKind>(JsonNamingPolicy.CamelCase, false),
            new JsonStringEnumConverter<MaintenanceRunState>(JsonNamingPolicy.CamelCase, false),
            new JsonStringEnumConverter<MaintenanceReleaseChannel>(JsonNamingPolicy.CamelCase, false)
        }
    };

    public static MaintenanceEventReport Create(MaintenanceJournal journal, DateTimeOffset exportedUtc)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var exported = MaintenanceValidation.Utc(exportedUtc);
        var recentRuns = journal.Runs.OrderByDescending(run => run.UpdatedUtc)
            .Take(MaximumEventCount).OrderBy(run => run.CreatedUtc).ToArray();
        var aliases = new Dictionary<MaintenanceDeviceKey, string>();
        var events = new List<MaintenanceEventReportEntry>(recentRuns.Length);
        foreach (var run in recentRuns)
        {
            if (!aliases.TryGetValue(run.Device.Key, out var alias))
            {
                alias = "device-" + (aliases.Count + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                aliases.Add(run.Device.Key, alias);
            }
            var hasMaintenanceService = run.Capabilities?.Names.Contains("maintenance-service.v1",
                StringComparer.Ordinal) == true;
            var protocolVersion = hasMaintenanceService ? run.Capabilities?.ProtocolVersion : null;
            var protocolMode = hasMaintenanceService ? "maintenanceService" : "legacyAgent";
            var actual = run.VerifiedCurrentVersions ?? run.CurrentVersions;
            var state = run.State.ToString();
            var failureStage = run.State is MaintenanceRunState.Failed or MaintenanceRunState.RolledBack or
                MaintenanceRunState.RecoveryRequired or MaintenanceRunState.ManualRequired or
                MaintenanceRunState.PendingVerification
                ? run.Transitions.LastOrDefault()?.To.ToString()
                : null;
            var duration = Math.Max(0, (long)(run.UpdatedUtc - run.CreatedUtc).TotalMilliseconds);
            events.Add(new MaintenanceEventReportEntry(events.Count + 1, alias,
                run.Plan?.OperationKind,
                state, failureStage, run.CreatedUtc, run.UpdatedUtc, duration,
                protocolVersion, protocolMode, run.Plan?.Channel,
                run.Plan is null ? null : new MaintenanceComponentVersions(
                    run.Plan.TargetSetupVersion, run.Plan.TargetAgentVersion,
                    run.Plan.TargetAgentVersion, run.Plan.TargetMaintenanceServiceVersion),
                actual, run.VerifiedUpdateTransactionState));
        }
        return new MaintenanceEventReport(1, exported, Array.AsReadOnly(events.ToArray()));
    }

    public static byte[] Serialize(MaintenanceEventReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        if (bytes.Length > MaximumReportBytes)
            throw new InvalidDataException("匿名 Maintenance 事件报告超过大小限制。");
        return bytes;
    }
}
