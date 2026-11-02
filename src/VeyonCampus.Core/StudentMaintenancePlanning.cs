namespace VeyonCampus.Core;

public enum StudentMaintenancePlanKind
{
    NoOp,
    UpdateOnly,
    ConfigurationOnly,
    UpdateThenConfiguration,
    RecoveryRequired,
    ManualRequired
}

public enum StudentMaintenanceTransport
{
    None,
    AuthenticatedAgentChannel,
    TrustedLegacyHttp,
    Unavailable
}

public sealed record StudentMaintenancePlanningInput(
    bool AgentIdentityTrusted,
    string? CurrentSetupVersion,
    string? CurrentAgentVersion,
    string TargetSetupVersion,
    string? TargetAgentVersion,
    int? StudentUpdateProtocolVersion,
    string? UpdateTransactionState,
    bool? TeacherChannelConfigurationMatches,
    bool HasAuthenticatedAgentChannel);

public sealed record StudentMaintenancePlan(
    StudentMaintenancePlanKind Kind,
    StudentMaintenanceTransport UpdateTransport,
    bool NeedsSoftwareUpdate,
    bool NeedsConfigurationSync,
    string Reason);

/// <summary>
/// Plans safe per-device maintenance from authenticated status facts. Unknown connection
/// configuration is treated as needing an idempotent signed sync; it is never assumed current.
/// </summary>
public static class StudentMaintenancePlanner
{
    public static StudentMaintenancePlan Create(StudentMaintenancePlanningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _ = ApplicationReleaseClient.CompareVersions(input.TargetSetupVersion, input.TargetSetupVersion);

        if (!input.AgentIdentityTrusted)
            return Manual("学生 Agent 身份尚未固定；先完成人工核对，再执行更新或连接配置同步。");

        if (input.UpdateTransactionState is "inProgress" or "needsReview")
            return new StudentMaintenancePlan(StudentMaintenancePlanKind.RecoveryRequired,
                StudentMaintenanceTransport.Unavailable, false, false,
                input.UpdateTransactionState == "inProgress"
                    ? "学生端已有未结束的原子更新事务；先重新查询该事务，不能并发下发新更新。"
                    : "学生端上次原子更新需要人工恢复核对；保留恢复材料，不下发新更新或配置。" );

        if (string.IsNullOrWhiteSpace(input.CurrentSetupVersion) ||
            string.IsNullOrWhiteSpace(input.CurrentAgentVersion))
            return Manual("无法可信读取当前 StudentSetup 与 Agent 版本；需要人工维护。");

        var setupComparison = ApplicationReleaseClient.CompareVersions(input.CurrentSetupVersion,
            input.TargetSetupVersion);
        var agentComparison = input.TargetAgentVersion is null
            ? 0
            : ApplicationReleaseClient.CompareVersions(input.CurrentAgentVersion, input.TargetAgentVersion);
        var needsSoftwareUpdate = setupComparison < 0 || agentComparison < 0;

        if (setupComparison > 0 && agentComparison < 0)
            return Manual($"当前 StudentSetup {input.CurrentSetupVersion} 高于目标 {input.TargetSetupVersion}，但 Agent {input.CurrentAgentVersion} 低于目标 {input.TargetAgentVersion}；不能降级 Setup 覆盖修复。");

        if (needsSoftwareUpdate && input.StudentUpdateProtocolVersion !=
            WebsitePolicyStatusCryptography.CurrentStudentUpdateProtocolVersion)
            return Manual("当前 Agent 未签名报告受支持的原子更新协议；不会下发安装包。");

        var needsConfigurationSync = input.TeacherChannelConfigurationMatches != true;
        var transport = !needsSoftwareUpdate
            ? StudentMaintenanceTransport.None
            : input.HasAuthenticatedAgentChannel
                ? StudentMaintenanceTransport.AuthenticatedAgentChannel
                : StudentMaintenanceTransport.TrustedLegacyHttp;

        if (needsSoftwareUpdate && transport == StudentMaintenanceTransport.Unavailable)
            return Manual("没有受支持且已认证的学生更新通道；不会下发安装包。");

        var kind = (needsSoftwareUpdate, needsConfigurationSync) switch
        {
            (false, false) => StudentMaintenancePlanKind.NoOp,
            (true, false) => StudentMaintenancePlanKind.UpdateOnly,
            (false, true) => StudentMaintenancePlanKind.ConfigurationOnly,
            _ => StudentMaintenancePlanKind.UpdateThenConfiguration
        };
        var reason = kind switch
        {
            StudentMaintenancePlanKind.NoOp => "软件和教师连接配置均已达标。",
            StudentMaintenancePlanKind.UpdateOnly => "仅需更新软件；连接配置已核验。",
            StudentMaintenancePlanKind.ConfigurationOnly => "软件版本已达标；需要核验并同步教师连接配置。",
            _ => "先更新完整 StudentSetup，再核验并同步教师连接配置。"
        };
        return new StudentMaintenancePlan(kind, transport, needsSoftwareUpdate,
            needsConfigurationSync, reason);
    }

    private static StudentMaintenancePlan Manual(string reason) =>
        new(StudentMaintenancePlanKind.ManualRequired, StudentMaintenanceTransport.Unavailable,
            false, false, reason);
}
