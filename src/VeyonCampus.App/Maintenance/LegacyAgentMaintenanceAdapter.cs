using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;

namespace VeyonCampus.App.Maintenance;

public sealed record LegacyStudentSetupArtifact(string InstallerPath, Uri ApiBaseAddress,
    string DeveloperReleasePublicKeyPem, string? VerifiedAgentTargetVersion = null);

public sealed record MaintenanceServiceReleaseArtifact(string InstallerPath, ApplicationReleaseEnvelope Release,
    Uri ApiBaseAddress, string DeveloperReleasePublicKeyPem);

public sealed record TeacherConnectivityConfiguration(IReadOnlyList<string> Endpoints,
    string RootCertificateBase64, string Fingerprint);

public interface ILegacyStudentSetupArtifactProvider
{
    Task<LegacyStudentSetupArtifact> GetVerifiedArtifactAsync(MaintenancePlan plan,
        CancellationToken cancellationToken);
}

public interface IMaintenanceServiceReleaseArtifactProvider
{
    Task<MaintenanceServiceReleaseArtifact?> GetNewerMaintenanceServiceReleaseAsync(string currentVersion,
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapts legacy students to the existing authenticated 39174 status/update/configuration paths and 39175 artifact
/// server. Package selection stays injectable so the ViewModel migration can move its current stable/local-test
/// preparation code without changing the legacy wire protocol.
/// </summary>
public sealed class LegacyAgentMaintenanceAdapter : ITeacherMaintenanceAdapter
{
    private readonly ILegacyStudentSetupArtifactProvider _artifactProvider;
    private readonly IMaintenanceServiceReleaseArtifactProvider? _maintenanceServiceArtifactProvider;
    private readonly MaintenanceServiceTeacherClient _maintenanceServiceClient = new();

    public LegacyAgentMaintenanceAdapter(ILegacyStudentSetupArtifactProvider artifactProvider)
    {
        _artifactProvider = artifactProvider ?? throw new ArgumentNullException(nameof(artifactProvider));
        _maintenanceServiceArtifactProvider = artifactProvider as IMaintenanceServiceReleaseArtifactProvider;
    }

    public async Task<MaintenanceAdapterInspection> InspectAsync(MaintenanceDeviceIdentity device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_maintenanceServiceClient.IsAvailable(device))
            return await InspectViaMaintenanceServiceAsync(device, cancellationToken).ConfigureAwait(false);
        if (_maintenanceServiceClient.HasPinnedIdentity(device))
            return Unverified("设备已固定 Maintenance 身份，但独立通道当前不可用；拒绝静默回退到 Legacy，避免并行更新或身份降级。");
        if (_maintenanceServiceClient.HasConnectedChannel(device))
            return Unverified("检测到未固定或不匹配的 Maintenance 通道身份；拒绝回退到 Legacy，需要重新核对设备身份。");
        using var signingKey = WebsitePolicySigningKeyStore.Open(device.CampusId);
        var pinnedPublicKey = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(device.CampusId,
            device.RouteAddress);
        if (pinnedPublicKey is null)
            return Unverified("学生 Agent 身份尚未由教师核对固定；未创建维护计划。");
        var pinnedFingerprint = StudentAgentResponseCryptography.GetFingerprint(pinnedPublicKey);
        if (!string.Equals(pinnedFingerprint, device.PinnedIdentityFingerprint, StringComparison.Ordinal))
            return Unverified("当前教师身份固定值与维护设备身份不一致；需要重新核对。");

        var results = await WebsitePolicyStatusTransport.ReadAsync([device.RouteAddress], device.CampusId,
            signingKey.PrivateKey, cancellationToken, includeHealthSnapshot: true,
            allowLegacyDirectFallback: true, allowDirectFallbackWhenChannelOffline: true,
            includeTeacherChannelDiagnostics: true).ConfigureAwait(false);
        var result = results.Single();
        if (!result.Succeeded || result.Status is null || result.VerifiedResponseSha256 is null)
            return Unverified(result.Detail);

        var status = result.Status;
        var evidence = new MaintenanceEvidence("signed-student-status", pinnedFingerprint,
            result.VerifiedResponseSha256, null, status.CollectedUtc);
        var names = new List<string> { $"signed-status.v{status.SchemaVersion}" };
        string? teacherChannelFingerprint = null;
        string? teacherChannelResponseSha256 = null;
        string? capabilitySetupVersion = null;
        string? capabilityAgentAssemblyVersion = null;
        int? capabilityUpdateProtocolVersion = null;
        string? capabilityResponseSha256 = null;
        try
        {
            var channelReadback = await StudentApplicationUpdateTransport.ReadVerifiedDownloadCapabilityAsync(
                device.RouteAddress, device.CampusId, signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
            if (channelReadback is not null)
            {
                var reported = channelReadback.Capability;
                if (reported.AgentRuntimeVersion is { } reportedAgent &&
                    !string.Equals(reportedAgent, status.AgentVersion, StringComparison.Ordinal) ||
                    reported.StudentSetupVersion is { } reportedSetup && status.StudentSetupVersion is { } statusSetup &&
                    !string.Equals(reportedSetup, statusSetup, StringComparison.Ordinal) ||
                    reported.ConfigurationFingerprint is { } reportedConfig &&
                    !string.Equals(reportedConfig, status.ConfigFingerprint, StringComparison.OrdinalIgnoreCase) ||
                    reported.StudentUpdateProtocolVersion is { } reportedProtocol &&
                    status.StudentUpdateProtocolVersion is { } statusProtocol && reportedProtocol != statusProtocol)
                    throw new InvalidDataException("签名状态和能力接口读回的 Agent、Setup、配置或更新协议版本不一致。");
                capabilitySetupVersion = reported.StudentSetupVersion;
                capabilityAgentAssemblyVersion = reported.AgentProductVersion;
                capabilityUpdateProtocolVersion = reported.StudentUpdateProtocolVersion;
                capabilityResponseSha256 = channelReadback.SignedResponseSha256;
                teacherChannelFingerprint = reported.TeacherChannelConfigurationFingerprint;
                teacherChannelResponseSha256 = teacherChannelFingerprint is null
                    ? null
                    : channelReadback.SignedResponseSha256;
            }
            if (teacherChannelFingerprint is not null) names.Add("teacher-channel.bootstrap-fingerprint");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          InvalidDataException or CryptographicException or IOException)
        {
            // Keep signed version/status facts; channel capability remains unknown and must not be assumed current.
        }
        if (status.StudentUpdateProtocolVersion is { } updateProtocol)
            names.Add($"student-update.v{updateProtocol}");
        else if (capabilityUpdateProtocolVersion is { } capabilityUpdateProtocol)
            names.Add($"student-update.v{capabilityUpdateProtocol}");
        if (capabilityAgentAssemblyVersion is not null) names.Add("student-agent.assembly-version");
        if (status.UpdateTransaction is not null) names.Add("student-update.transaction-status");
        if (status.Health is not null) names.Add("student-agent.health-snapshot");
        var capabilities = new MaintenanceCapabilities(pinnedFingerprint, status.SchemaVersion,
            status.StudentUpdateProtocolVersion ?? capabilityUpdateProtocolVersion, status.AgentVersion,
            status.ConfigFingerprint, null, names,
            result.VerifiedResponseSha256, "RSA-PSS-SHA256", status.CollectedUtc, teacherChannelFingerprint,
            teacherChannelResponseSha256, capabilityResponseSha256);
        var versions = new MaintenanceComponentVersions(status.StudentSetupVersion ?? capabilitySetupVersion,
            status.AgentVersion, capabilityAgentAssemblyVersion, maintenanceVersion: null);
        return new MaintenanceAdapterInspection(true, versions, capabilities, evidence, evidence,
            "已通过签名状态、固定 Agent 身份、版本与配置指纹核验；兼容旧 Agent 的状态/更新 HTTP 通道。",
            status.Health?.UpdateTransactionState ?? status.UpdateTransaction?.State,
            StudentAgentChannelHub.IsConnected(device.CampusId, device.RouteAddress), DateTimeOffset.UtcNow,
            status.TeacherChannelDiagnostics);

        MaintenanceAdapterInspection Unverified(string detail) => new(false,
            new MaintenanceComponentVersions(null, null, null, null), null, null, null, detail);
    }

    private async Task<MaintenanceAdapterInspection> InspectViaMaintenanceServiceAsync(
        MaintenanceDeviceIdentity device, CancellationToken cancellationToken)
    {
        using var signingKey = WebsitePolicySigningKeyStore.Open(device.CampusId);
        try
        {
            var health = await _maintenanceServiceClient.SendAsync<MaintenanceServiceHealthPayload>(device,
                Guid.NewGuid(), null, MaintenanceProtocolV1Command.GetHealth, signingKey.PrivateKey,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var capabilities = await _maintenanceServiceClient.SendAsync<MaintenanceServiceCapabilitiesPayload>(
                device, health.Request.OperationId, null, MaintenanceProtocolV1Command.GetCapabilities,
                signingKey.PrivateKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            var installed = await _maintenanceServiceClient.SendAsync<MaintenanceServiceInstalledVersionsPayload>(
                device, health.Request.OperationId, null, MaintenanceProtocolV1Command.GetInstalledVersions,
                signingKey.PrivateKey, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (health.Response.ResultCode != "succeeded" || health.Payload is null ||
                health.Payload.State != "ready" || !health.Payload.RunningAsSystem ||
                health.Payload.ManagedAgentTaskTrusted != true)
                return UnverifiedMaintenance("Maintenance 服务身份已认证，但服务配置或受保护 SYSTEM Agent 任务尚未达到可维护状态。");
            if (capabilities.Response.ResultCode != "succeeded" || capabilities.Payload is null ||
                installed.Response.ResultCode != "succeeded" || installed.Payload is null)
                return UnverifiedMaintenance("Maintenance 服务未返回完整的签名能力或版本快照。");
            if (capabilities.Payload.ProtocolVersion != MaintenanceProtocolV1.CurrentVersion ||
                health.Payload.ProtocolVersion != capabilities.Payload.ProtocolVersion ||
                !string.Equals(health.Payload.AgentConfigurationFingerprint,
                    capabilities.Payload.AgentConfigurationFingerprint, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(health.Payload.TeacherConfigurationFingerprint,
                    capabilities.Payload.TeacherConfigurationFingerprint, StringComparison.OrdinalIgnoreCase))
                return UnverifiedMaintenance("Maintenance 健康与能力回执的协议或受保护配置指纹不一致。");

            var versions = ToMaintenanceVersions(installed.Payload);
            if (versions.StudentSetupVersion is null || versions.AgentRuntimeVersion is null ||
                string.IsNullOrWhiteSpace(capabilities.Payload.AgentConfigurationFingerprint))
                return UnverifiedMaintenance("Maintenance 签名版本或 Agent 配置指纹不完整；不能建立维护计划。");

            var identityEvidence = new MaintenanceEvidence("signed-maintenance-service-health",
                device.PinnedIdentityFingerprint, health.SignedResponseSha256, null,
                health.Response.CollectedUtc);
            var capabilitiesEvidence = new MaintenanceEvidence("signed-maintenance-service-capabilities",
                device.PinnedIdentityFingerprint, capabilities.SignedResponseSha256, null,
                capabilities.Response.CollectedUtc);
            var names = capabilities.Payload.SupportedCommands
                .Append("maintenance-service.v1").Distinct(StringComparer.Ordinal).ToArray();
            var maintenanceCapabilities = new MaintenanceCapabilities(device.PinnedIdentityFingerprint,
                capabilities.Payload.ProtocolVersion, null,
                versions.AgentRuntimeVersion, capabilities.Payload.AgentConfigurationFingerprint,
                versions.MaintenanceVersion, names, capabilities.SignedResponseSha256,
                "RSA-PSS-SHA256", capabilities.Response.CollectedUtc,
                capabilities.Payload.TeacherConfigurationFingerprint,
                capabilities.Payload.TeacherConfigurationFingerprint is null
                    ? null
                    : capabilities.SignedResponseSha256,
                capabilities.SignedResponseSha256);
            return new MaintenanceAdapterInspection(true, versions, maintenanceCapabilities, identityEvidence,
                capabilitiesEvidence,
                "已通过独立 Maintenance 服务签名身份、受保护配置、SYSTEM 任务、版本和固定命令能力核验；教师端可经该通道提交已签名的 StudentSetup 或 MaintenanceService 更新。",
                installed.Payload.TransactionState, StudentAgentChannelHub.IsConnected(device.CampusId,
                    device.RouteAddress), health.Response.CollectedUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or TimeoutException or
                                          InvalidOperationException or ArgumentException)
        {
            return UnverifiedMaintenance("Maintenance 独立服务签名核验失败：" + Sanitize(exception.Message));
        }

        static MaintenanceAdapterInspection UnverifiedMaintenance(string detail) => new(false,
            new MaintenanceComponentVersions(null, null, null, null), null, null, null, detail);
    }

    public async Task<IReadOnlyList<MaintenanceAdapterResult>> OfferUpdatesAsync(
        IReadOnlyList<MaintenanceUpdateRequest> requests,
        Func<MaintenanceUpdateCommandPrepared, CancellationToken, Task> beforeCommandSend,
        IProgress<MaintenanceCoordinatorProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(beforeCommandSend);
        if (requests.Count == 0) return Array.Empty<MaintenanceAdapterResult>();
        var plans = requests.Select(request => request.Plan).ToArray();
        var first = plans[0];
        if (plans.Any(plan => plan.Device.CampusId != first.Device.CampusId ||
                              !string.Equals(plan.TargetSetupVersion, first.TargetSetupVersion,
                                  StringComparison.Ordinal) ||
                              !string.Equals(plan.TargetAgentVersion, first.TargetAgentVersion,
                                  StringComparison.Ordinal) ||
                              !string.Equals(plan.TargetReleaseSha256, first.TargetReleaseSha256,
                                  StringComparison.Ordinal) || plan.Channel != first.Channel))
            throw new InvalidDataException("同一旧 Agent 更新批次必须使用同校区、同目标 StudentSetup、同发行摘要和同通道。");
        if (first.TargetSetupVersion is null || first.TargetReleaseSha256 is null)
            throw new InvalidDataException("旧 Agent 更新计划缺少 StudentSetup 目标版本或发行 SHA-256。");

        var artifact = await _artifactProvider.GetVerifiedArtifactAsync(first, cancellationToken)
            .ConfigureAwait(false);
        var release = await Task.Run(() => ApplicationReleaseClient.ReadVerifiedStagedRelease(
            artifact.InstallerPath, ApplicationReleaseRole.StudentSetup, artifact.ApiBaseAddress,
            artifact.DeveloperReleasePublicKeyPem), cancellationToken).ConfigureAwait(false);
        ApplicationReleaseClient.Verify(release, ApplicationReleaseRole.StudentSetup, artifact.ApiBaseAddress,
            artifact.DeveloperReleasePublicKeyPem);
        var stagedAgentVersion = artifact.VerifiedAgentTargetVersion ?? release.Manifest.StudentAgentVersion;
        if (!string.Equals(release.Manifest.Version, first.TargetSetupVersion, StringComparison.Ordinal) ||
            !string.Equals(release.Manifest.Sha256, first.TargetReleaseSha256, StringComparison.OrdinalIgnoreCase) ||
            first.TargetAgentVersion is not null && !string.Equals(first.TargetAgentVersion, stagedAgentVersion,
                StringComparison.Ordinal))
            throw new InvalidDataException("已验签 StudentSetup 包的版本或摘要与维护计划不一致。");
        if (first.Channel == MaintenanceReleaseChannel.Stable &&
            release.Manifest.ReleaseChannel == "local-test")
            throw new InvalidDataException("Stable 维护计划不能使用 local-test 发行包。");

        var planByTarget = plans.ToDictionary(plan => plan.Device.RouteAddress,
            StringComparer.OrdinalIgnoreCase);
        var sourceVersions = new Dictionary<string, StudentApplicationUpdateSourceVersions>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            var currentSetup = plan.CurrentVersions.StudentSetupVersion
                               ?? throw new InvalidDataException("旧 Agent 更新缺少已签名 StudentSetup 当前版本。");
            var currentAgent = plan.CurrentVersions.AgentRuntimeVersion
                               ?? throw new InvalidDataException("旧 Agent 更新缺少已签名 Agent 当前版本。");
            sourceVersions.Add(plan.Device.RouteAddress, new StudentApplicationUpdateSourceVersions(currentSetup,
                currentAgent, plan.Capabilities.StudentUpdateProtocolVersion,
                plan.Capabilities.ConfigurationFingerprint, plan.TargetSetupVersion, plan.TargetAgentVersion,
                release.Manifest.ReleaseChannel ?? "stable"));
        }

        var commandIds = new ConcurrentDictionary<Guid, Guid>();
        using var signingKey = WebsitePolicySigningKeyStore.Open(first.Device.CampusId);
        await using var lanServer = StudentApplicationUpdateLanServer.Start(artifact.InstallerPath,
            release.Manifest.Sha256, release.Manifest.SizeBytes);
        var developerPublicKey = artifact.DeveloperReleasePublicKeyPem;
        var updateProgress = progress is null ? null : new Progress<StudentApplicationUpdateProgress>(value =>
        {
            if (!planByTarget.TryGetValue(value.Target, out var plan)) return;
            progress.Report(new MaintenanceCoordinatorProgress(plan.OperationId,
                value.CommandId is null ? MaintenanceRunState.UpdateOffered : MaintenanceRunState.Updating,
                value.Stage, value.CompletedTargets, value.TotalTargets, value.CommandId,
                plan.Device.RouteAddress));
        });
        var output = new List<MaintenanceAdapterResult>(plans.Length);
        var maintenancePlanList = new List<MaintenancePlan>();
        var blockedPlans = new List<(MaintenancePlan Plan, bool HasPinnedIdentity)>();
        var legacyPlanList = new List<MaintenancePlan>();
        foreach (var plan in plans)
        {
            var hasConnectedChannel = _maintenanceServiceClient.HasConnectedChannel(plan.Device);
            var hasPinnedIdentity = _maintenanceServiceClient.HasPinnedIdentity(plan.Device);
            if (hasConnectedChannel && hasPinnedIdentity) maintenancePlanList.Add(plan);
            else if (hasConnectedChannel || hasPinnedIdentity) blockedPlans.Add((plan, hasPinnedIdentity));
            else legacyPlanList.Add(plan);
        }
        var maintenancePlans = maintenancePlanList.ToArray();
        foreach (var (plan, hasPinnedIdentity) in blockedPlans)
            output.Add(new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.ManualRequired,
                hasPinnedIdentity
                    ? "设备已固定 Maintenance 身份，但独立通道不可用；拒绝静默回退到 Legacy。"
                    : "检测到未固定或不匹配的 Maintenance 通道身份；需要重新核对设备身份。", null));
        var legacyPlans = legacyPlanList.ToArray();
        var signedReleaseEnvelopeJson = JsonSerializer.Serialize(release);
        foreach (var plan in maintenancePlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commandId = Guid.NewGuid();
            try
            {
                var freshInspection = await InspectAsync(plan.Device, cancellationToken).ConfigureAwait(false);
                var stalePlanReason = ValidateBeforeUpdateSend(plan, freshInspection);
                if (stalePlanReason is not null)
                {
                    output.Add(new MaintenanceAdapterResult(plan.OperationId,
                        MaintenanceAdapterDisposition.ManualRequired, stalePlanReason, null));
                    continue;
                }
                var issuedUtc = DateTimeOffset.UtcNow;
                var expiresUtc = issuedUtc + TimeSpan.FromMinutes(9);
                var downloadUri = await lanServer.GetDownloadUriAsync(plan.Device.RouteAddress,
                    plan.Device.CampusId, commandId, expiresUtc, targetAgentPublicKeyPem: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var reply = await _maintenanceServiceClient.SendAsync<MaintenanceServiceTransactionReadbackPayload>(
                    plan.Device, plan.OperationId, commandId,
                    MaintenanceProtocolV1Command.OfferSignedStudentSetupUpdate, signingKey.PrivateKey,
                    beforeSend: async (preparedCommandId, signedRequestSha256, token) =>
                    {
                        if (preparedCommandId != commandId)
                            throw new InvalidDataException("Maintenance 更新 commandId 在签名前后发生变化。" );
                        commandIds[plan.OperationId] = preparedCommandId;
                        await beforeCommandSend(new MaintenanceUpdateCommandPrepared(plan.OperationId,
                            preparedCommandId, signedRequestSha256), token).ConfigureAwait(false);
                    }, cancellationToken: cancellationToken,
                    targetSetupVersion: release.Manifest.Version,
                    releaseSha256: release.Manifest.Sha256,
                    signedReleaseEnvelopeJson: signedReleaseEnvelopeJson,
                    artifactTransferUrl: downloadUri.AbsoluteUri).ConfigureAwait(false);
                output.Add(ClassifyMaintenanceUpdateOffer(plan, commandId, reply));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or CryptographicException or TimeoutException or
                                              InvalidOperationException or ArgumentException or HttpRequestException)
            {
                var commandWasPrepared = commandIds.ContainsKey(plan.OperationId);
                output.Add(new MaintenanceAdapterResult(plan.OperationId,
                    commandWasPrepared ? MaintenanceAdapterDisposition.PendingVerification :
                        exception is InvalidDataException ? MaintenanceAdapterDisposition.ManualRequired :
                        MaintenanceAdapterDisposition.Failed,
                    commandWasPrepared
                        ? "Maintenance 更新命令已持久化，但服务回执未完成；请按原 commandId 查询事务。"
                        : Sanitize(exception.Message), null));
            }
        }

        var legacyTargets = legacyPlans.Select(plan => plan.Device.RouteAddress).ToArray();
        var legacySourceVersions = sourceVersions.Where(pair => legacyTargets.Contains(pair.Key,
                StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<StudentApplicationUpdatePushResult> deliveryResults =
            Array.Empty<StudentApplicationUpdatePushResult>();
        if (legacyTargets.Length > 0)
        {
            deliveryResults = await StudentApplicationUpdateTransport.PushAsync(legacyTargets,
                first.Device.CampusId, first.TargetSetupVersion, signingKey.PrivateKey,
                async (target, pinnedAgentPublicKey, downloadRequestProtocolVersion, token) =>
                {
                    if (!planByTarget.TryGetValue(target, out var plan))
                        throw new InvalidDataException("旧 Agent 更新传输返回了未计划的目标地址。");
                    var freshInspection = await InspectAsync(plan.Device, token).ConfigureAwait(false);
                    var stalePlanReason = ValidateBeforeUpdateSend(plan, freshInspection);
                    if (stalePlanReason is not null)
                        throw new InvalidDataException(stalePlanReason);
                    var issuedUtc = DateTimeOffset.UtcNow;
                    var commandId = Guid.NewGuid();
                    var expiresUtc = issuedUtc + StudentApplicationUpdateCryptography.MaximumCommandLifetime;
                    var downloadRequestKey = downloadRequestProtocolVersion ==
                                             StudentApplicationUpdateCryptography.DownloadRequestProtocolVersion
                        ? pinnedAgentPublicKey
                        : null;
                    var downloadUri = await lanServer.GetDownloadUriAsync(target, plan.Device.CampusId,
                        commandId, expiresUtc, downloadRequestKey, token).ConfigureAwait(false);
                    var command = new StudentApplicationUpdateCommand(1, plan.Device.CampusId, commandId,
                        issuedUtc, expiresUtc, release, downloadUri.AbsoluteUri);
                    string signedCommand;
                    lock (signingKey.PrivateKey)
                        signedCommand = StudentApplicationUpdateCryptography.Sign(command, signingKey.PrivateKey,
                            artifact.ApiBaseAddress, developerPublicKey);
                    var signedCommandSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedCommand)));
                    await beforeCommandSend(new MaintenanceUpdateCommandPrepared(plan.OperationId, commandId,
                        signedCommandSha256), token).ConfigureAwait(false);
                    commandIds[plan.OperationId] = commandId;
                    return (commandId, signedCommand);
                }, cancellationToken, progress: updateProgress, sourceVersions: legacySourceVersions)
                .ConfigureAwait(false);
        }

        foreach (var delivery in deliveryResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!planByTarget.TryGetValue(delivery.Target, out var plan)) continue;
            if (!commandIds.TryGetValue(plan.OperationId, out var commandId))
            {
                output.Add(new MaintenanceAdapterResult(plan.OperationId,
                    delivery.NeedsReview ? MaintenanceAdapterDisposition.ManualRequired :
                        MaintenanceAdapterDisposition.Failed, delivery.Detail, null));
                continue;
            }
            var statusResult = await ReadStatusAsync(plan, commandId, signingKey.PrivateKey, cancellationToken)
                .ConfigureAwait(false);
            output.Add(ClassifyUpdateResult(plan, delivery, statusResult, commandId));
        }
        return Array.AsReadOnly(output.ToArray());
    }

    /// <summary>Runs the independent MaintenanceService release transaction for one authenticated service.</summary>
    public async Task<MaintenanceAdapterResult> OfferMaintenanceServiceUpdateAsync(MaintenancePlan plan,
        Func<MaintenanceUpdateCommandPrepared, CancellationToken, Task> beforeCommandSend,
        IProgress<MaintenanceCoordinatorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var operationId = plan.OperationId;
        var device = plan.Device;
        if (plan.OperationKind != MaintenanceOperationKind.MaintenanceServiceUpdate ||
            (plan.TargetMaintenanceServiceVersion is null) != (plan.TargetReleaseSha256 is null))
            throw new InvalidDataException("MaintenanceService 检查计划的目标版本和发行 SHA-256 不匹配。");
        ArgumentNullException.ThrowIfNull(beforeCommandSend);
        if (!_maintenanceServiceClient.IsAvailable(device))
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.ManualRequired,
                "Maintenance 独立身份或认证通道不可用；拒绝经 Legacy Agent 更新服务自身。", null);
        if (_maintenanceServiceArtifactProvider is null)
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.ManualRequired,
                "当前发行提供器未实现独立 MaintenanceService 包获取。", null);

        using var signingKey = WebsitePolicySigningKeyStore.Open(device.CampusId);
        var inspection = await InspectAsync(device, cancellationToken).ConfigureAwait(false);
        if (!inspection.IdentityVerified || inspection.CurrentVersions.MaintenanceVersion is not { } currentVersion ||
            !string.Equals(currentVersion, plan.CurrentVersions.MaintenanceVersion, StringComparison.Ordinal))
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.ManualRequired,
                "无法从签名 Maintenance 健康读回确认当前服务版本。", inspection.IdentityEvidence,
                inspection.CurrentVersions, inspection.UpdateTransactionState);
        var supportedCommands = inspection.Capabilities?.SupportedCommands;
        if (supportedCommands is null ||
            !supportedCommands.Contains("offerSignedMaintenanceServiceUpdate", StringComparer.Ordinal) ||
            !supportedCommands.Contains("getMaintenanceServiceUpdateStatus", StringComparer.Ordinal))
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.ManualRequired,
                "当前 Maintenance 服务的签名能力列表未声明自更新命令和事务查询；不会发送它不认识的更新协议。请先通过受信任 StudentSetup/引导升级安装支持自更新的 Maintenance。",
                inspection.IdentityEvidence, inspection.CurrentVersions, inspection.UpdateTransactionState);
        if (plan.TargetMaintenanceServiceVersion is null)
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.NoChanges,
                "MaintenanceService 已是当前可用发行版本。", inspection.IdentityEvidence,
                inspection.CurrentVersions, inspection.UpdateTransactionState);

        MaintenanceServiceReleaseArtifact? artifact;
        try
        {
            artifact = await _maintenanceServiceArtifactProvider.GetNewerMaintenanceServiceReleaseAsync(
                currentVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or TimeoutException or
                                          InvalidOperationException or ArgumentException or HttpRequestException)
        {
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.Failed,
                "无法准备经过签名核验的 MaintenanceService 更新：" + Sanitize(exception.Message),
                inspection.IdentityEvidence, inspection.CurrentVersions, inspection.UpdateTransactionState);
        }
        if (artifact is null)
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.ManualRequired,
                "维护计划中的 MaintenanceService 目标已不再是可用的较新发行；请重新检查并生成计划。",
                inspection.IdentityEvidence, inspection.CurrentVersions, inspection.UpdateTransactionState);

        var release = artifact.Release;
        var commandId = Guid.NewGuid();
        var evidence = new MaintenanceEvidence("signed-maintenance-service-release",
            device.PinnedIdentityFingerprint, release.Manifest.Sha256, commandId, DateTimeOffset.UtcNow);
        var commandWasPrepared = false;
        try
        {
            ApplicationReleaseClient.Verify(release, ApplicationReleaseRole.MaintenanceService,
                artifact.ApiBaseAddress, artifact.DeveloperReleasePublicKeyPem);
            if (release.Manifest.MaintenanceProtocolVersion != MaintenanceProtocolV1.CurrentVersion ||
                ApplicationReleaseClient.CompareVersions(release.Manifest.Version, currentVersion) <= 0 ||
                !string.Equals(release.Manifest.Version, plan.TargetMaintenanceServiceVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(release.Manifest.Sha256, plan.TargetReleaseSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("MaintenanceService 发行版本、摘要或协议门槛与维护计划不匹配。");
            await using var lanServer = StudentApplicationUpdateLanServer.Start(artifact.InstallerPath,
                release.Manifest.Sha256, release.Manifest.SizeBytes);
            var expiresUtc = DateTimeOffset.UtcNow.AddMinutes(4);
            var downloadUri = await lanServer.GetDownloadUriAsync(device.RouteAddress, device.CampusId,
                commandId, expiresUtc, targetAgentPublicKeyPem: null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var signedEnvelopeJson = JsonSerializer.Serialize(release);
            var offer = await _maintenanceServiceClient.SendAsync<MaintenanceServiceReleaseUpdateReadbackPayload>(
                device, operationId, commandId, MaintenanceProtocolV1Command.OfferSignedMaintenanceServiceUpdate,
                signingKey.PrivateKey,
                beforeSend: async (preparedCommandId, signedCommandSha256, token) =>
                {
                    if (preparedCommandId != commandId)
                        throw new InvalidDataException("MaintenanceService commandId 在签名前后发生变化。");
                    await beforeCommandSend(new MaintenanceUpdateCommandPrepared(operationId, commandId,
                        signedCommandSha256), token).ConfigureAwait(false);
                    commandWasPrepared = true;
                }, cancellationToken: cancellationToken,
                releaseSha256: release.Manifest.Sha256,
                signedReleaseEnvelopeJson: signedEnvelopeJson,
                artifactTransferUrl: downloadUri.AbsoluteUri,
                targetMaintenanceServiceVersion: release.Manifest.Version).ConfigureAwait(false);
            if (offer.Response.ResultCode == "needsReview")
                return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.RecoveryRequired,
                    offer.Response.Detail, evidence, inspection.CurrentVersions, "needsReview",
                    offer.Response.CollectedUtc);
            if (offer.Response.ResultCode == "failed" || offer.Response.ResultCode == "notSupported")
                return new MaintenanceAdapterResult(operationId,
                    offer.Response.ResultCode == "notSupported" ? MaintenanceAdapterDisposition.ManualRequired :
                    MaintenanceAdapterDisposition.Failed, offer.Response.Detail, evidence,
                    inspection.CurrentVersions, null, offer.Response.CollectedUtc);
            progress?.Report(new MaintenanceCoordinatorProgress(operationId, MaintenanceRunState.Updating,
                "Maintenance Service 已接受独立签名更新；等待版本切换和健康核验。", 0, 1, commandId,
                device.RouteAddress));
            return await WaitForMaintenanceServiceReleaseAsync(operationId, device, commandId, release,
                currentVersion, evidence, inspection.CurrentVersions, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or TimeoutException or
                                          InvalidOperationException or ArgumentException or HttpRequestException)
        {
            return new MaintenanceAdapterResult(operationId,
                commandWasPrepared ? MaintenanceAdapterDisposition.PendingVerification :
                    exception is InvalidDataException ? MaintenanceAdapterDisposition.ManualRequired :
                    MaintenanceAdapterDisposition.Failed,
                commandWasPrepared
                    ? "MaintenanceService 更新命令已持久化；使用原 commandId 查询其独立版本事务。"
                    : Sanitize(exception.Message), evidence, inspection.CurrentVersions,
                commandWasPrepared ? "inProgress" : null, DateTimeOffset.UtcNow);
        }
    }

    private async Task<MaintenanceAdapterResult> WaitForMaintenanceServiceReleaseAsync(Guid operationId,
        MaintenanceDeviceIdentity device, Guid commandId, ApplicationReleaseEnvelope release,
        string previousVersion, MaintenanceEvidence evidence, MaintenanceComponentVersions previousVersions,
        IProgress<MaintenanceCoordinatorProgress>? progress, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var signingKey = WebsitePolicySigningKeyStore.Open(device.CampusId);
                var status = await _maintenanceServiceClient.SendAsync<MaintenanceServiceReleaseUpdateReadbackPayload>(
                    device, operationId, commandId, MaintenanceProtocolV1Command.GetMaintenanceServiceUpdateStatus,
                    signingKey.PrivateKey,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var transaction = status.Payload?.Transaction;
                var installed = status.Payload?.InstalledVersions;
                if (transaction is not null && (transaction.CommandId != commandId ||
                                                transaction.OperationId != operationId))
                    return new MaintenanceAdapterResult(operationId,
                        MaintenanceAdapterDisposition.RecoveryRequired,
                        "MaintenanceService 事务读回没有绑定原 operationId/commandId。", evidence,
                        previousVersions, "needsReview", status.Response.CollectedUtc);
                if (transaction is { State: "committed" })
                {
                    var fresh = await InspectAsync(device, cancellationToken).ConfigureAwait(false);
                    if (fresh.IdentityVerified && fresh.CurrentVersions.MaintenanceVersion is { } serviceVersion &&
                        string.Equals(serviceVersion, release.Manifest.Version, StringComparison.Ordinal) &&
                        fresh.Capabilities?.ProtocolVersion == release.Manifest.MaintenanceProtocolVersion &&
                        installed?.RunningAsSystem == true)
                        return new MaintenanceAdapterResult(operationId,
                            MaintenanceAdapterDisposition.UpdateCommittedAndHealthy,
                            "MaintenanceService 签名包已切换；SCM 路径、版本、SYSTEM 配置和认证健康通道均已读回。",
                            new MaintenanceEvidence("signed-maintenance-service-committed",
                                device.PinnedIdentityFingerprint, release.Manifest.Sha256, commandId,
                                status.Response.CollectedUtc), fresh.CurrentVersions, transaction.State,
                            status.Response.CollectedUtc);
                    return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.RecoveryRequired,
                        "MaintenanceService 事务报告完成，但新版本健康读回不一致；停止后续 Setup 更新。",
                        evidence, fresh.CurrentVersions, transaction.State, status.Response.CollectedUtc);
                }
                if (transaction is { State: "rolledBack" })
                    return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.UpdateRolledBack,
                        "MaintenanceService 更新失败；已确认旧版本仍由 SCM 运行。", evidence,
                        new MaintenanceComponentVersions(previousVersions.StudentSetupVersion,
                            previousVersions.AgentRuntimeVersion, previousVersions.AgentAssemblyVersion,
                            previousVersion), transaction.State,
                        status.Response.CollectedUtc);
                if (transaction is { State: "needsReview" } || status.Response.ResultCode == "needsReview")
                    return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.RecoveryRequired,
                        status.Response.Detail, evidence, previousVersions, "needsReview", status.Response.CollectedUtc);
                if (status.Response.ResultCode == "failed")
                    return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.Failed,
                        status.Response.Detail, evidence, previousVersions, null, status.Response.CollectedUtc);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or CryptographicException or TimeoutException or
                                              InvalidOperationException or ArgumentException or HttpRequestException)
            {
                // Service restart drops the authenticated socket; reconnect and query the same commandId.
            }
            progress?.Report(new MaintenanceCoordinatorProgress(operationId, MaintenanceRunState.WaitingForCommit,
                "等待 Maintenance Service 重连并读回相同 commandId 的版本事务。", 0, 1, commandId,
                device.RouteAddress));
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.UpdatePendingVerification,
            "MaintenanceService 正在切换或暂时离线；10 分钟内未取得签名终态，保留 commandId 待重新查询。",
            evidence, previousVersions, "inProgress", DateTimeOffset.UtcNow);
    }

    public async Task<MaintenanceAdapterResult> SynchronizeConfigurationAsync(MaintenancePlan plan,
        Func<MaintenanceUpdateCommandPrepared, CancellationToken, Task> beforeCommandSend,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(beforeCommandSend);
        var freshInspection = await InspectAsync(plan.Device, cancellationToken).ConfigureAwait(false);
        var stalePlanReason = ValidateBeforeConfigurationSync(plan, freshInspection);
        if (stalePlanReason is not null)
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.ManualRequired,
                stalePlanReason, null);
        var identity = ReadTeacherChannelIdentity(plan.Device.CampusId, cancellationToken);
        if (plan.ConfigurationSha256 is null || !string.Equals(plan.ConfigurationSha256,
                identity.BootstrapFingerprint, StringComparison.OrdinalIgnoreCase))
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.ManualRequired,
                "教师连接身份在维护计划生成后发生变化；请重新检查并生成新计划。", null);
        using var signingKey = WebsitePolicySigningKeyStore.Open(plan.Device.CampusId);
        if (_maintenanceServiceClient.IsAvailable(plan.Device))
            return await SynchronizeConfigurationViaMaintenanceServiceAsync(plan, identity, signingKey.PrivateKey,
                beforeCommandSend, cancellationToken).ConfigureAwait(false);

        Guid? commandId = null;
        var results = await StudentAgentConfigurationSyncTransport.ExecuteAsync([plan.Device.RouteAddress],
            plan.Device.CampusId, identity.Endpoints, identity.RootCertificateBase64, signingKey.PrivateKey,
            cancellationToken: cancellationToken,
            onCommandPrepared: async (target, preparedCommandId, signedCommandSha256, token) =>
            {
                if (!string.Equals(target, plan.Device.RouteAddress, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("配置同步适配器返回了未计划的目标地址。");
                commandId = preparedCommandId;
                await beforeCommandSend(new MaintenanceUpdateCommandPrepared(plan.OperationId,
                    preparedCommandId, signedCommandSha256), token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        var result = results.Single();
        if (result.Succeeded && commandId is { } acceptedCommand &&
            result.VerifiedAcknowledgementSha256 is not null)
        {
            return await VerifyConfigurationReadbackAsync(plan, acceptedCommand, result.Detail,
                signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
        }
        return new MaintenanceAdapterResult(plan.OperationId,
            commandId is not null ? MaintenanceAdapterDisposition.PendingVerification :
                result.NeedsReview ? MaintenanceAdapterDisposition.ManualRequired :
                MaintenanceAdapterDisposition.Failed,
            result.Detail, null);
    }

    private async Task<MaintenanceAdapterResult> SynchronizeConfigurationViaMaintenanceServiceAsync(
        MaintenancePlan plan, TeacherChannelIdentity identity, RSA signingKey,
        Func<MaintenanceUpdateCommandPrepared, CancellationToken, Task> beforeCommandSend,
        CancellationToken cancellationToken)
    {
        var commandId = Guid.NewGuid();
        var currentHealth = await _maintenanceServiceClient.SendAsync<MaintenanceServiceHealthPayload>(plan.Device,
            plan.OperationId, null, MaintenanceProtocolV1Command.GetHealth, signingKey,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (currentHealth.Response.ResultCode != "succeeded" || currentHealth.Payload is null ||
            currentHealth.Payload.State != "ready" || currentHealth.Payload.TeacherConfigurationRevision < 0)
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.PendingVerification,
                "Maintenance 未提供可用于递增教师配置 revision 的可信当前状态。", null);
        var targetRevision = checked(currentHealth.Payload.TeacherConfigurationRevision + 1);
        var configuration = new MaintenanceTeacherBootstrapConfiguration(identity.Endpoints,
            identity.RootCertificateBase64);
        var reply = await _maintenanceServiceClient.SendAsync<MaintenanceServiceTeacherConfigurationSyncPayload>(
            plan.Device, plan.OperationId, commandId, MaintenanceProtocolV1Command.SyncTeacherEndpoint,
            signingKey, configuration, identity.BootstrapFingerprint,
            async (preparedCommandId, signedRequestSha256, token) =>
            {
                if (preparedCommandId != commandId)
                    throw new InvalidDataException("Maintenance 配置命令编号在签名前后发生变化。" );
                await beforeCommandSend(new MaintenanceUpdateCommandPrepared(plan.OperationId,
                    preparedCommandId, signedRequestSha256), token).ConfigureAwait(false);
            }, cancellationToken, targetTeacherConfigurationRevision: targetRevision).ConfigureAwait(false);
        var evidence = new MaintenanceEvidence("signed-maintenance-configuration-status",
            plan.Device.PinnedIdentityFingerprint, reply.SignedResponseSha256, commandId,
            reply.Response.CollectedUtc);
        if (reply.Response.ResultCode != "succeeded")
        {
            var disposition = reply.Response.ResultCode switch
            {
                "needsReview" => MaintenanceAdapterDisposition.ManualRequired,
                "pending" => MaintenanceAdapterDisposition.PendingVerification,
                "notSupported" => MaintenanceAdapterDisposition.ManualRequired,
                _ => MaintenanceAdapterDisposition.Failed
            };
            return new MaintenanceAdapterResult(plan.OperationId, disposition,
                "Maintenance 服务未确认配置同步：" + reply.Response.Detail, evidence);
        }

        var payload = reply.Payload;
        if (payload is null || string.IsNullOrWhiteSpace(payload.ConfigurationFingerprint) ||
            !string.Equals(payload.ConfigurationFingerprint, identity.BootstrapFingerprint,
                StringComparison.OrdinalIgnoreCase) || payload.UpdatedUtc.Offset != TimeSpan.Zero ||
            payload.AgentConfigurationFingerprint is not { Length: 64 } agentConfigurationFingerprint ||
            agentConfigurationFingerprint.Any(character => !Uri.IsHexDigit(character)) ||
            payload.ConfigurationRevision != targetRevision ||
            payload.UpdatedUtc > reply.Response.CollectedUtc.AddMinutes(1) ||
            payload.UpdatedUtc < reply.Response.CollectedUtc.AddMinutes(-5))
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.PendingVerification,
                "Maintenance 已回执，但 Agent/Maintenance 配置指纹或持久化时间未通过签名读回校验。", evidence);

        var installed = payload.InstalledVersions;
        if (installed is null)
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.PendingVerification,
                "Maintenance 已回执教师连接指纹，但未返回组件和健康状态读回。", evidence);
        var versions = ToMaintenanceVersions(installed);
        if (!installed.RunningAsSystem || installed.ManagedAgentTaskTrusted == false)
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.RecoveryRequired,
                "教师连接配置已读回，但 SYSTEM 身份或受保护 Agent 启动任务核验失败。", evidence,
                versions, installed.TransactionState, reply.Response.CollectedUtc);
        if (installed.ManagedAgentTaskTrusted is null)
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.PendingVerification,
                "教师连接配置已读回，但 Agent 受保护启动任务状态未知。", evidence,
                versions, installed.TransactionState, reply.Response.CollectedUtc);
        if (installed.TransactionState == "needsReview")
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.RecoveryRequired,
                "教师连接配置已读回，但 Agent 更新事务需要恢复核对。", evidence,
                versions, installed.TransactionState, reply.Response.CollectedUtc);
        if (installed.TransactionState == "inProgress")
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.PendingVerification,
                "教师连接配置已读回，但 Agent 更新事务仍在进行。", evidence,
                versions, installed.TransactionState, reply.Response.CollectedUtc);

        var expectedSetup = MaximumVersion(plan.CurrentVersions.StudentSetupVersion, plan.TargetSetupVersion);
        var expectedAgent = MaximumVersion(plan.CurrentVersions.AgentRuntimeVersion, plan.TargetAgentVersion);
        if (versions.StudentSetupVersion is null || versions.AgentRuntimeVersion is null ||
            expectedSetup is null || expectedAgent is null ||
            ApplicationReleaseClient.CompareVersions(versions.StudentSetupVersion, expectedSetup) < 0 ||
            ApplicationReleaseClient.CompareVersions(versions.AgentRuntimeVersion, expectedAgent) < 0)
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.RecoveryRequired,
                "教师连接配置已读回，但实际 Setup/Agent 版本低于本维护计划要求。", evidence,
                versions, installed.TransactionState, reply.Response.CollectedUtc);

        return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.ConfigurationSynchronized,
            "Maintenance 与 Student Agent 均已通过签名回执确认教师连接配置，且核验了 SYSTEM、Agent 任务和组件版本。",
            evidence, versions, installed.TransactionState, reply.Response.CollectedUtc, targetRevision);
    }

    public async Task<MaintenanceAdapterResult> ReconcileAsync(MaintenanceRun run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var plan = run.Plan ?? throw new InvalidDataException("恢复维护事务缺少原维护计划。");
        if (_maintenanceServiceClient.IsAvailable(plan.Device))
            return await ReconcileThroughMaintenanceServiceAsync(run, plan, cancellationToken)
                .ConfigureAwait(false);
        if (IsConfigurationReconciliation(run))
        {
            if (plan.ConfigurationSha256 is not { } desiredConfiguration)
                return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.ManualRequired,
                    "维护计划缺少目标教师连接指纹；不能恢复配置同步结果。", null);
            using var key = WebsitePolicySigningKeyStore.Open(plan.Device.CampusId);
            var configCommandId = run.Transitions.LastOrDefault(item =>
                item.To == MaintenanceRunState.ConfigurationSyncing)?.Evidence?.CommandId;
            if (configCommandId is null)
                return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.ManualRequired,
                    "恢复配置事务缺少已记录的 commandId；不重发命令。", null);
            if (!string.Equals(plan.ConfigurationSha256, desiredConfiguration, StringComparison.OrdinalIgnoreCase))
                return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.ManualRequired,
                    "恢复配置事务的目标指纹与原计划不一致；不重发命令。", null);
            return await VerifyConfigurationReadbackAsync(plan, configCommandId.Value,
                "已重新核对配置同步事务。", key.PrivateKey, cancellationToken).ConfigureAwait(false);
        }
        if (run.AgentCommandId is null)
            return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.PendingVerification,
                "尚无 Agent 更新 commandId；只读状态不足以判断安装事务。", null);
        using var signingKey = WebsitePolicySigningKeyStore.Open(run.Device.CampusId);
        var status = await ReadStatusAsync(plan, run.AgentCommandId, signingKey.PrivateKey, cancellationToken)
            .ConfigureAwait(false);
        if (status.Status is null || status.ResponseSha256 is null)
            return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.PendingVerification,
                status.Detail, null);
        var evidence = new MaintenanceEvidence("signed-update-transaction-status",
            run.Device.PinnedIdentityFingerprint, status.ResponseSha256, run.AgentCommandId,
            status.Status.CollectedUtc);
        return ClassifyStatus(plan, run.AgentCommandId.Value, status.Status, evidence,
            "已重新读取学生事务状态。", agentAssemblyVersion: status.AgentAssemblyVersion);
    }

    public static string? ReadCurrentTeacherChannelFingerprint(string campusId) =>
        ReadCurrentTeacherChannelConfiguration(campusId).Fingerprint;

    public static TeacherConnectivityConfiguration ReadCurrentTeacherChannelConfiguration(string campusId)
    {
        var identity = ReadTeacherChannelIdentity(campusId, CancellationToken.None);
        return new TeacherConnectivityConfiguration(identity.Endpoints, identity.RootCertificateBase64,
            identity.BootstrapFingerprint);
    }

    private async Task<MaintenanceAdapterResult> ReconcileThroughMaintenanceServiceAsync(MaintenanceRun run,
        MaintenancePlan plan, CancellationToken cancellationToken)
    {
        using var signingKey = WebsitePolicySigningKeyStore.Open(plan.Device.CampusId);
        if (IsConfigurationReconciliation(run))
        {
            var commandId = run.Transitions.LastOrDefault(item =>
                item.To == MaintenanceRunState.ConfigurationSyncing)?.Evidence?.CommandId;
            if (commandId is null || plan.ConfigurationSha256 is null)
                return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.ManualRequired,
                    "配置同步恢复缺少原 commandId 或目标教师连接指纹；不重发配置命令。", null);

            var reply = await _maintenanceServiceClient.SendAsync<MaintenanceServiceHealthPayload>(plan.Device,
                run.OperationId, null, MaintenanceProtocolV1Command.GetHealth, signingKey.PrivateKey,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var evidence = new MaintenanceEvidence("signed-maintenance-configuration-status",
                plan.Device.PinnedIdentityFingerprint, reply.SignedResponseSha256, commandId,
                reply.Response.CollectedUtc);
            if (reply.Response.ResultCode != "succeeded" || reply.Payload is null ||
                !string.Equals(reply.Payload.TeacherConfigurationFingerprint, plan.ConfigurationSha256,
                    StringComparison.OrdinalIgnoreCase))
                return new MaintenanceAdapterResult(run.OperationId,
                    MaintenanceAdapterDisposition.PendingVerification,
                    "Maintenance 服务尚未签名确认原计划的教师连接指纹。", evidence);
            if (!reply.Payload.RunningAsSystem || reply.Payload.ManagedAgentTaskTrusted == false)
                return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.RecoveryRequired,
                    "目标教师连接配置已读回，但 SYSTEM 或受保护 Agent 启动任务核验失败。", evidence);
            if (reply.Payload.ManagedAgentTaskTrusted is null)
                return new MaintenanceAdapterResult(run.OperationId,
                    MaintenanceAdapterDisposition.PendingVerification,
                    "目标教师连接配置已读回，但 Agent 受保护启动任务状态未知。", evidence);

            var versionsReply = await _maintenanceServiceClient.SendAsync<
                MaintenanceServiceInstalledVersionsPayload>(plan.Device, run.OperationId, null,
                MaintenanceProtocolV1Command.GetInstalledVersions, signingKey.PrivateKey,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var installed = versionsReply.Payload;
            if (versionsReply.Response.ResultCode != "succeeded" || installed is null)
                return new MaintenanceAdapterResult(run.OperationId,
                    MaintenanceAdapterDisposition.PendingVerification,
                    "教师连接指纹已读回，但组件版本和更新事务快照暂不可用。", evidence);
            var versions = ToMaintenanceVersions(installed);
            if (!installed.RunningAsSystem || installed.ManagedAgentTaskTrusted == false)
                return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.RecoveryRequired,
                    "目标教师连接配置已读回，但 SYSTEM 或受保护 Agent 启动任务核验失败。", evidence,
                    versions, installed.TransactionState, versionsReply.Response.CollectedUtc);
            if (installed.ManagedAgentTaskTrusted is null)
                return new MaintenanceAdapterResult(run.OperationId,
                    MaintenanceAdapterDisposition.PendingVerification,
                    "目标教师连接配置已读回，但 Agent 受保护启动任务状态未知。", evidence,
                    versions, installed.TransactionState, versionsReply.Response.CollectedUtc);
            if (installed.TransactionState == "needsReview")
                return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.RecoveryRequired,
                    "目标教师连接配置已读回，但 Agent 更新事务需要恢复核对。", evidence,
                    versions, installed.TransactionState, versionsReply.Response.CollectedUtc);
            if (installed.TransactionState == "inProgress")
                return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.PendingVerification,
                    "目标教师连接配置已读回，但 Agent 更新事务仍在进行。", evidence,
                    versions, installed.TransactionState, versionsReply.Response.CollectedUtc);
            var expectedSetup = MaximumVersion(plan.CurrentVersions.StudentSetupVersion, plan.TargetSetupVersion);
            var expectedAgent = MaximumVersion(plan.CurrentVersions.AgentRuntimeVersion, plan.TargetAgentVersion);
            if (versions.StudentSetupVersion is null || versions.AgentRuntimeVersion is null ||
                expectedSetup is null || expectedAgent is null ||
                ApplicationReleaseClient.CompareVersions(versions.StudentSetupVersion, expectedSetup) < 0 ||
                ApplicationReleaseClient.CompareVersions(versions.AgentRuntimeVersion, expectedAgent) < 0)
                return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.RecoveryRequired,
                    "目标教师连接配置已读回，但实际 Setup/Agent 版本低于原维护计划。", evidence,
                    versions, installed.TransactionState, versionsReply.Response.CollectedUtc);
            return new MaintenanceAdapterResult(run.OperationId,
                MaintenanceAdapterDisposition.ConfigurationSynchronized,
                "Maintenance 服务已签名读回原教师连接指纹、组件版本和受保护 SYSTEM Agent 启动任务。",
                evidence, versions, installed.TransactionState, reply.Response.CollectedUtc,
                reply.Payload.TeacherConfigurationRevision);
        }

        if (run.AgentCommandId is not { } agentCommandId)
            return new MaintenanceAdapterResult(run.OperationId, MaintenanceAdapterDisposition.PendingVerification,
                "尚无 Agent 更新 commandId；只读状态不足以判断安装事务。", null);
        var statusReply = await ReadMaintenanceTransactionAsync(plan, run.OperationId, agentCommandId,
            signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
        var statusPayload = statusReply.Payload
                            ?? throw new InvalidDataException("Maintenance 事务响应正文为空。" );
        if (statusPayload.Transaction.State == "inProgress")
        {
            var recovery = await _maintenanceServiceClient.SendAsync<MaintenanceServiceTransactionReadbackPayload>(
                plan.Device, run.OperationId, agentCommandId,
                MaintenanceProtocolV1Command.RecoverPendingTransaction, signingKey.PrivateKey,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (recovery.Response.ResultCode == "needsReview" || recovery.Payload?.Transaction.State == "needsReview")
                return CreateMaintenanceTransactionResult(run, agentCommandId, recovery,
                    MaintenanceAdapterDisposition.RecoveryRequired,
                    "Maintenance 保留原子事务材料，但恢复过程需要现场核对。", recovery.Payload);
            statusReply = await ReadMaintenanceTransactionAsync(plan, run.OperationId, agentCommandId,
                signingKey.PrivateKey, cancellationToken).ConfigureAwait(false);
            statusPayload = statusReply.Payload
                            ?? throw new InvalidDataException("Maintenance 事务响应正文为空。" );
        }

        var transaction = statusPayload.Transaction;
        if (transaction.State == "needsReview")
            return CreateMaintenanceTransactionResult(run, agentCommandId, statusReply,
                MaintenanceAdapterDisposition.RecoveryRequired,
                "签名事务状态要求保留恢复材料并进行现场核对。", statusPayload);
        if (transaction.State == "superseded")
            return CreateMaintenanceTransactionResult(run, agentCommandId, statusReply,
                MaintenanceAdapterDisposition.RecoveryRequired,
                "原更新事务已被其他 commandId 替代；保留状态并要求核对后续事务。", statusPayload);
        if (transaction.State is not ("committed" or "rolledBack"))
            return CreateMaintenanceTransactionResult(run, agentCommandId, statusReply,
                MaintenanceAdapterDisposition.PendingVerification,
                "Maintenance 服务已读取原 commandId 的事务；事务尚未达到可裁定的终态。", statusPayload);

        var installed = statusPayload.InstalledVersions;
        var versions = ToMaintenanceVersions(installed);
        if (installed.TransactionState != transaction.State)
            return CreateMaintenanceTransactionResult(run, agentCommandId, statusReply,
                MaintenanceAdapterDisposition.PendingVerification,
                "事务日志和组件快照在读取期间发生变化；暂不裁定更新结果。", statusPayload);
        if (!installed.RunningAsSystem || installed.ManagedAgentTaskTrusted == false)
            return CreateMaintenanceTransactionResult(run, agentCommandId, statusReply,
                MaintenanceAdapterDisposition.RecoveryRequired,
                "事务已达到终态，但 SYSTEM 身份或 Agent 启动任务信任核验失败。", statusPayload);
        if (installed.ManagedAgentTaskTrusted is null || versions.StudentSetupVersion is null ||
            versions.AgentRuntimeVersion is null)
            return CreateMaintenanceTransactionResult(run, agentCommandId, statusReply,
                MaintenanceAdapterDisposition.PendingVerification,
                "事务已达到终态，但签名版本或 Agent 启动任务读回不完整。", statusPayload);

        var evidence = CreateMaintenanceTransactionEvidence(run, agentCommandId, statusReply);
        if (transaction.State == "committed")
        {
            var setupMatches = plan.TargetSetupVersion is { } targetSetup &&
                               string.Equals(versions.StudentSetupVersion, targetSetup, StringComparison.Ordinal) &&
                               string.Equals(transaction.TargetSetupVersion, targetSetup, StringComparison.Ordinal);
            var agentMatches = plan.TargetAgentVersion is { } targetAgent
                ? string.Equals(versions.AgentRuntimeVersion, targetAgent, StringComparison.Ordinal) &&
                  string.Equals(transaction.TargetAgentVersion, targetAgent, StringComparison.Ordinal)
                : transaction.TargetAgentVersion is { } transactionAgent
                    ? string.Equals(versions.AgentRuntimeVersion, transactionAgent, StringComparison.Ordinal) &&
                      (plan.CurrentVersions.AgentRuntimeVersion is not { } sourceAgent ||
                       ApplicationReleaseClient.CompareVersions(transactionAgent, sourceAgent) >= 0)
                    : plan.CurrentVersions.AgentRuntimeVersion is { } sourceRuntime &&
                      ApplicationReleaseClient.CompareVersions(versions.AgentRuntimeVersion, sourceRuntime) >= 0;
            return new MaintenanceAdapterResult(run.OperationId,
                setupMatches && agentMatches
                    ? MaintenanceAdapterDisposition.UpdateCommittedAndHealthy
                    : MaintenanceAdapterDisposition.RecoveryRequired,
                setupMatches && agentMatches
                    ? "Maintenance 签名事务、Setup/Agent 版本和受保护 SYSTEM 启动任务均已核验。"
                    : "事务报告 committed，但签名组件版本与计划目标不一致。",
                evidence, versions, transaction.State, statusReply.Response.CollectedUtc);
        }

        var setupRestored = string.Equals(versions.StudentSetupVersion,
                                plan.CurrentVersions.StudentSetupVersion, StringComparison.Ordinal) &&
                            string.Equals(transaction.PreviousSetupVersion,
                                plan.CurrentVersions.StudentSetupVersion, StringComparison.Ordinal);
        var agentRestored = string.Equals(versions.AgentRuntimeVersion,
                                 plan.CurrentVersions.AgentRuntimeVersion, StringComparison.Ordinal) &&
                            string.Equals(transaction.PreviousAgentVersion,
                                plan.CurrentVersions.AgentRuntimeVersion, StringComparison.Ordinal);
        return new MaintenanceAdapterResult(run.OperationId,
            setupRestored && agentRestored
                ? MaintenanceAdapterDisposition.UpdateRolledBack
                : MaintenanceAdapterDisposition.RecoveryRequired,
            setupRestored && agentRestored
                ? "Maintenance 签名读回确认原版本已恢复，SYSTEM Agent 启动任务仍受保护。"
                : "事务报告 rolledBack，但签名版本与原版本快照不一致。",
            evidence, versions, transaction.State, statusReply.Response.CollectedUtc);
    }

    private async Task<MaintenanceServiceTeacherReply<MaintenanceServiceTransactionReadbackPayload>>
        ReadMaintenanceTransactionAsync(MaintenancePlan plan, Guid operationId, Guid commandId,
            RSA signingKey, CancellationToken cancellationToken)
    {
        var reply = await _maintenanceServiceClient.SendAsync<MaintenanceServiceTransactionReadbackPayload>(
            plan.Device, operationId, commandId, MaintenanceProtocolV1Command.GetTransactionStatus,
            signingKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (reply.Payload is null || reply.Payload.Transaction.CommandId != commandId)
            throw new InvalidDataException("Maintenance 事务响应没有绑定原 commandId。" );
        return reply;
    }

    private static MaintenanceAdapterResult CreateMaintenanceTransactionResult(MaintenanceRun run,
        Guid commandId,
        MaintenanceServiceTeacherReply<MaintenanceServiceTransactionReadbackPayload> reply,
        MaintenanceAdapterDisposition disposition, string detail,
        MaintenanceServiceTransactionReadbackPayload? payload)
    {
        var evidence = CreateMaintenanceTransactionEvidence(run, commandId, reply);
        var state = payload?.Transaction.State;
        var versions = payload is null ? null : ToMaintenanceVersions(payload.InstalledVersions);
        return new MaintenanceAdapterResult(run.OperationId, disposition, detail, evidence,
            versions, versions is null ? null : state,
            versions is null ? null : reply.Response.CollectedUtc);
    }

    private static MaintenanceEvidence CreateMaintenanceTransactionEvidence(MaintenanceRun run,
        Guid commandId,
        MaintenanceServiceTeacherReply<MaintenanceServiceTransactionReadbackPayload> reply) =>
        new("signed-maintenance-update-transaction-status", run.Device.PinnedIdentityFingerprint,
            reply.SignedResponseSha256, commandId, reply.Response.CollectedUtc);

    private static MaintenanceComponentVersions ToMaintenanceVersions(
        MaintenanceServiceInstalledVersionsPayload versions) =>
        new(versions.StudentSetupVersion, versions.AgentRuntimeVersion,
            versions.AgentAssemblyVersion, versions.MaintenanceServiceVersion);

    private static TeacherChannelIdentity ReadTeacherChannelIdentity(string campusId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = TeacherMobileControlIdentityProviderFactory.CreateForCurrentPlatform();
        using var identity = provider.CreateIdentity();
        var preferredAddress = provider.SelectPreferredLanAddress(identity.Addresses);
        var endpoints = new[] { preferredAddress }
            .Concat(identity.Addresses.Where(address => !address.Equals(preferredAddress)))
            .Select(ClassroomEventCryptography.CreateTeacherEndpoint)
            .Distinct(StringComparer.Ordinal).ToArray();
        var rootCertificateBase64 = Convert.ToBase64String(identity.RootCertificateBytes);
        StudentAgentChannelProtocol.ValidateBootstrapConfiguration(campusId, endpoints, rootCertificateBase64);
        var fingerprint = StudentAgentChannelProtocol.GetBootstrapConfigurationFingerprint(campusId, endpoints,
            rootCertificateBase64) ?? throw new InvalidDataException("教师连接身份没有可验证的指纹。");
        return new TeacherChannelIdentity(Array.AsReadOnly(endpoints), rootCertificateBase64, fingerprint);
    }

    private async Task<LegacyStatusReadback> ReadStatusAsync(MaintenancePlan plan, Guid? commandId,
        RSA? providedSigningKey, CancellationToken cancellationToken)
    {
        using var ownedKey = providedSigningKey is null
            ? WebsitePolicySigningKeyStore.Open(plan.Device.CampusId)
            : null;
        var signingKey = providedSigningKey ?? ownedKey!.PrivateKey;
        var results = await WebsitePolicyStatusTransport.ReadAsync([plan.Device.RouteAddress],
            plan.Device.CampusId, signingKey, cancellationToken, includeHealthSnapshot: true,
            updateCommandId: commandId, allowLegacyDirectFallback: true,
            allowDirectFallbackWhenChannelOffline: true).ConfigureAwait(false);
        var result = results.Single();
        if (!result.Succeeded || result.Status is null || result.VerifiedResponseSha256 is null)
            return new LegacyStatusReadback(null, null, result.Detail);
        var pinned = new StudentAgentIdentityTrustStore().FindTrustedPublicKey(plan.Device.CampusId,
            plan.Device.RouteAddress);
        if (pinned is null || !string.Equals(StudentAgentResponseCryptography.GetFingerprint(pinned),
                plan.Device.PinnedIdentityFingerprint, StringComparison.Ordinal))
            return new LegacyStatusReadback(null, null, "签名状态身份与维护计划固定身份不一致。");

        string? agentAssemblyVersion = null;
        try
        {
            var capability = await StudentApplicationUpdateTransport.ReadVerifiedDownloadCapabilityAsync(
                plan.Device.RouteAddress, plan.Device.CampusId, signingKey, cancellationToken).ConfigureAwait(false);
            if (capability is not null)
            {
                var reported = capability.Capability;
                if (reported.AgentRuntimeVersion is { } agentVersion &&
                    !string.Equals(agentVersion, result.Status.AgentVersion, StringComparison.Ordinal) ||
                    reported.StudentSetupVersion is { } setupVersion &&
                    !string.Equals(setupVersion, result.Status.StudentSetupVersion, StringComparison.Ordinal) ||
                    reported.ConfigurationFingerprint is { } configFingerprint &&
                    !string.Equals(configFingerprint, result.Status.ConfigFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                    return new LegacyStatusReadback(null, null,
                        "签名状态和能力接口的运行版本、Setup 或配置指纹不一致；暂不裁定事务终态。");
                agentAssemblyVersion = reported.AgentProductVersion;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          InvalidDataException or CryptographicException or IOException)
        {
            // Runtime/Setup/transaction status remain signed evidence; assembly product version stays unknown.
        }
        return new LegacyStatusReadback(result.Status, result.VerifiedResponseSha256, result.Detail,
            agentAssemblyVersion);
    }

    private static MaintenanceAdapterResult ClassifyMaintenanceUpdateOffer(MaintenancePlan plan, Guid commandId,
        MaintenanceServiceTeacherReply<MaintenanceServiceTransactionReadbackPayload> reply)
    {
        var evidence = new MaintenanceEvidence("signed-maintenance-update-transaction-status",
            plan.Device.PinnedIdentityFingerprint, reply.SignedResponseSha256, commandId,
            reply.Response.CollectedUtc);
        if (reply.Response.ResultCode == "needsReview")
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.RecoveryRequired,
                reply.Response.Detail, evidence);
        if (reply.Response.ResultCode == "notSupported")
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.ManualRequired,
                reply.Response.Detail, evidence);
        if (reply.Response.ResultCode == "failed")
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.Failed,
                reply.Response.Detail, evidence);
        if (reply.Payload is not { } payload || payload.InstalledVersions is null ||
            payload.Transaction is null || payload.Transaction.CommandId != commandId)
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.PendingVerification,
                "Maintenance 已返回签名回执，但事务正文缺失或未绑定原 commandId。", evidence);

        var transaction = payload.Transaction;
        var installed = payload.InstalledVersions;
        var versions = ToMaintenanceVersions(installed);
        if (transaction.State is "pending" or "inProgress")
            return new MaintenanceAdapterResult(plan.OperationId,
                MaintenanceAdapterDisposition.UpdatePendingVerification,
                "Maintenance 已验签并接管 StudentSetup 包；等待 UpdateHelper 完成和服务重连。",
                evidence, versions, transaction.State, reply.Response.CollectedUtc);
        if (transaction.State == "needsReview" || reply.Response.ResultCode == "needsReview")
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.RecoveryRequired,
                "Maintenance 更新事务要求人工核对；保留原安装与事务材料。", evidence,
                versions, transaction.State, reply.Response.CollectedUtc);
        if (!installed.RunningAsSystem || installed.ManagedAgentTaskTrusted != true ||
            installed.TransactionState != transaction.State || versions.StudentSetupVersion is null ||
            versions.AgentRuntimeVersion is null ||
            versions.AgentAssemblyVersion is { } assemblyVersion &&
            !string.Equals(assemblyVersion, versions.AgentRuntimeVersion, StringComparison.Ordinal))
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.RecoveryRequired,
                "Maintenance 事务终态、SYSTEM 身份、Agent 任务或版本读回不一致。", evidence,
                versions, transaction.State, reply.Response.CollectedUtc);

        if (transaction.State == "committed")
        {
            var setupMatches = plan.TargetSetupVersion is { } targetSetup &&
                               string.Equals(versions.StudentSetupVersion, targetSetup, StringComparison.Ordinal) &&
                               string.Equals(transaction.TargetSetupVersion, targetSetup, StringComparison.Ordinal);
            var agentMatches = plan.TargetAgentVersion is { } targetAgent
                ? string.Equals(versions.AgentRuntimeVersion, targetAgent, StringComparison.Ordinal) &&
                  string.Equals(transaction.TargetAgentVersion, targetAgent, StringComparison.Ordinal)
                : transaction.TargetAgentVersion is { } transactionAgent
                    ? string.Equals(versions.AgentRuntimeVersion, transactionAgent, StringComparison.Ordinal) &&
                      plan.CurrentVersions.AgentRuntimeVersion is { } sourceAgent &&
                      ApplicationReleaseClient.CompareVersions(transactionAgent, sourceAgent) >= 0
                    : plan.CurrentVersions.AgentRuntimeVersion is { } sourceRuntime &&
                      ApplicationReleaseClient.CompareVersions(versions.AgentRuntimeVersion, sourceRuntime) >= 0;
            return new MaintenanceAdapterResult(plan.OperationId,
                setupMatches && agentMatches
                    ? MaintenanceAdapterDisposition.UpdateCommittedAndHealthy
                    : MaintenanceAdapterDisposition.RecoveryRequired,
                setupMatches && agentMatches
                    ? "Maintenance 签名事务、StudentSetup/Agent 版本和 SYSTEM 启动任务均已核验。"
                    : "Maintenance 报告 committed，但组件版本与维护计划不一致。",
                evidence, versions, transaction.State, reply.Response.CollectedUtc);
        }

        if (transaction.State == "rolledBack")
        {
            var setupRestored = string.Equals(versions.StudentSetupVersion,
                                   plan.CurrentVersions.StudentSetupVersion, StringComparison.Ordinal) &&
                               string.Equals(transaction.PreviousSetupVersion,
                                   plan.CurrentVersions.StudentSetupVersion, StringComparison.Ordinal);
            var agentRestored = string.Equals(versions.AgentRuntimeVersion,
                                   plan.CurrentVersions.AgentRuntimeVersion, StringComparison.Ordinal) &&
                               string.Equals(transaction.PreviousAgentVersion,
                                   plan.CurrentVersions.AgentRuntimeVersion, StringComparison.Ordinal);
            return new MaintenanceAdapterResult(plan.OperationId,
                setupRestored && agentRestored
                    ? MaintenanceAdapterDisposition.UpdateRolledBack
                    : MaintenanceAdapterDisposition.RecoveryRequired,
                setupRestored && agentRestored
                    ? "签名事务确认 StudentSetup 与 Agent 已恢复至更新前版本。"
                    : "事务报告 rolledBack，但更新前版本读回不一致。",
                evidence, versions, transaction.State, reply.Response.CollectedUtc);
        }

        return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.PendingVerification,
            "Maintenance 事务状态不在可裁定的终态集合中。", evidence,
            versions, transaction.State, reply.Response.CollectedUtc);
    }

    private static MaintenanceAdapterResult ClassifyUpdateResult(MaintenancePlan plan,
        StudentApplicationUpdatePushResult delivery, LegacyStatusReadback readback, Guid commandId)
    {
        if (readback.Status is null || readback.ResponseSha256 is null)
            return new MaintenanceAdapterResult(plan.OperationId,
                MaintenanceAdapterDisposition.PendingVerification,
                delivery.Detail + "；" + readback.Detail, null);
        var evidence = new MaintenanceEvidence("signed-update-transaction-status",
            plan.Device.PinnedIdentityFingerprint, readback.ResponseSha256, commandId,
            readback.Status.CollectedUtc);
        return ClassifyStatus(plan, commandId, readback.Status, evidence, delivery.Detail, delivery,
            readback.AgentAssemblyVersion);
    }

    private static MaintenanceAdapterResult ClassifyStatus(MaintenancePlan plan, Guid commandId,
        StudentAgentStatusResponse status, MaintenanceEvidence evidence, string detail,
        StudentApplicationUpdatePushResult? delivery = null, string? agentAssemblyVersion = null)
    {
        var transaction = status.UpdateTransaction;
        if (transaction?.CommandId != commandId)
            return new MaintenanceAdapterResult(plan.OperationId, MaintenanceAdapterDisposition.PendingVerification,
                "签名状态未绑定本 operation 的 Agent commandId。", null);
        var state = transaction.State;
        MaintenanceAdapterResult Observed(MaintenanceAdapterDisposition disposition, string summary) =>
            new(plan.OperationId, disposition, summary, evidence,
                new MaintenanceComponentVersions(status.StudentSetupVersion, status.AgentVersion,
                    agentAssemblyVersion, null),
                state, status.CollectedUtc);
        if (state == "committed")
        {
            var setupMatches = plan.TargetSetupVersion is { } targetSetup &&
                               string.Equals(status.StudentSetupVersion, targetSetup, StringComparison.Ordinal) &&
                               string.Equals(transaction.TargetSetupVersion, targetSetup, StringComparison.Ordinal);
            var agentMatches = status.AgentVersion is { } actualAgent &&
                               (plan.TargetAgentVersion is { } targetAgent
                                   ? string.Equals(actualAgent, targetAgent, StringComparison.Ordinal) &&
                                     string.Equals(transaction.TargetAgentVersion, targetAgent, StringComparison.Ordinal)
                                   : plan.CurrentVersions.AgentRuntimeVersion is { } sourceAgent &&
                                     string.Equals(transaction.TargetAgentVersion, actualAgent, StringComparison.Ordinal) &&
                                     ApplicationReleaseClient.CompareVersions(actualAgent, sourceAgent) >= 0);
            var healthMatches = status.Health is
                { RunningAsSystem: true, ManagedTaskMatchesAgent: true, ManagedTaskAclRestricted: true };
            if (setupMatches && agentMatches && healthMatches)
                return Observed(MaintenanceAdapterDisposition.UpdateCommittedAndHealthy, detail);
            return Observed(MaintenanceAdapterDisposition.RecoveryRequired,
                "事务报告 committed，但签名状态的目标版本或健康读回不匹配。" );
        }
        if (state == "rolledBack")
        {
            var setupRestored = string.Equals(status.StudentSetupVersion, plan.CurrentVersions.StudentSetupVersion,
                                   StringComparison.Ordinal) &&
                               string.Equals(transaction.PreviousSetupVersion,
                                   plan.CurrentVersions.StudentSetupVersion, StringComparison.Ordinal);
            var agentRestored = string.Equals(status.AgentVersion, plan.CurrentVersions.AgentRuntimeVersion,
                                    StringComparison.Ordinal) &&
                                string.Equals(transaction.PreviousAgentVersion,
                                    plan.CurrentVersions.AgentRuntimeVersion, StringComparison.Ordinal);
            var healthRestored = status.Health is
                { RunningAsSystem: true, ManagedTaskMatchesAgent: true, ManagedTaskAclRestricted: true };
            return Observed(setupRestored && agentRestored && healthRestored
                    ? MaintenanceAdapterDisposition.UpdateRolledBack
                    : MaintenanceAdapterDisposition.RecoveryRequired,
                setupRestored && agentRestored && healthRestored
                    ? detail
                    : "回滚状态已签名，但旧版本或 SYSTEM 健康状态没有完整恢复。" );
        }
        if (state == "recoveryRequired" || state == "needsReview")
            return Observed(MaintenanceAdapterDisposition.RecoveryRequired, detail);
        return Observed(MaintenanceAdapterDisposition.PendingVerification, detail);
    }

    private static async Task<MaintenanceAdapterResult> VerifyConfigurationReadbackAsync(
        MaintenancePlan plan, Guid commandId, string summary, RSA signingKey,
        CancellationToken cancellationToken)
    {
        var operationId = plan.OperationId;
        var pending = "配置命令已持久化；" + summary + "；未重复发送配置。";
        var capability = await StudentApplicationUpdateTransport.ReadVerifiedDownloadCapabilityAsync(
            plan.Device.RouteAddress, plan.Device.CampusId, signingKey, cancellationToken).ConfigureAwait(false);
        if (capability is null)
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.PendingVerification,
                pending + "学生端没有提供兼容的签名能力读回。", null);
        if (string.IsNullOrWhiteSpace(capability.Capability.ConfigurationFingerprint) ||
            capability.Capability.TeacherChannelConfigurationReady != true)
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.PendingVerification,
                pending + "签名能力回执缺少完整活动配置指纹。", null);
        if (!string.Equals(capability.Capability.TeacherChannelConfigurationFingerprint,
                plan.ConfigurationSha256, StringComparison.OrdinalIgnoreCase))
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.PendingVerification,
                pending + "教师连接配置指纹尚未达到计划目标。", null);

        var statusResults = await WebsitePolicyStatusTransport.ReadAsync([plan.Device.RouteAddress],
            plan.Device.CampusId, signingKey, cancellationToken, includeHealthSnapshot: true,
            allowLegacyDirectFallback: true, allowDirectFallbackWhenChannelOffline: true).ConfigureAwait(false);
        var statusResult = statusResults.Single();
        if (!statusResult.Succeeded || statusResult.Status is null || statusResult.VerifiedResponseSha256 is null)
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.PendingVerification,
                pending + "签名状态/健康读回暂不可用。 " + Sanitize(statusResult.Detail), null);

        var status = statusResult.Status;
        var health = status.Health;
        if (health is null)
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.PendingVerification,
                pending + "学生端没有返回带时间戳的健康快照。", null);
        if (health.UpdateTransactionState is "inProgress" or "needsReview")
            return new MaintenanceAdapterResult(operationId,
                health.UpdateTransactionState == "needsReview"
                    ? MaintenanceAdapterDisposition.RecoveryRequired
                    : MaintenanceAdapterDisposition.PendingVerification,
                pending + "学生端仍有未完成或需恢复核对的原子更新事务。", null);
        if (health.RunningAsSystem == false || health.ManagedTaskMatchesAgent == false ||
            health.ManagedTaskAclRestricted == false)
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.RecoveryRequired,
                "连接配置已读回，但 SYSTEM Agent 健康、任务指向或 ACL 核验失败；需现场恢复。", null);
        if (health.RunningAsSystem != true || health.ManagedTaskMatchesAgent != true ||
            health.ManagedTaskAclRestricted != true)
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.PendingVerification,
                pending + "SYSTEM Agent 健康事实不完整。", null);

        var reported = capability.Capability;
        if (!string.Equals(status.ConfigFingerprint, reported.ConfigurationFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(status.AgentVersion, reported.AgentRuntimeVersion, StringComparison.Ordinal) ||
            !string.Equals(status.StudentSetupVersion, reported.StudentSetupVersion, StringComparison.Ordinal))
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.PendingVerification,
                pending + "签名状态与签名能力的版本/配置快照不一致。", null);

        var expectedSetup = MaximumVersion(plan.CurrentVersions.StudentSetupVersion, plan.TargetSetupVersion);
        var expectedAgent = MaximumVersion(plan.CurrentVersions.AgentRuntimeVersion, plan.TargetAgentVersion);
        if (status.StudentSetupVersion is null || status.AgentVersion is null ||
            expectedSetup is null || expectedAgent is null ||
            ApplicationReleaseClient.CompareVersions(status.StudentSetupVersion, expectedSetup) < 0 ||
            ApplicationReleaseClient.CompareVersions(status.AgentVersion, expectedAgent) < 0)
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.RecoveryRequired,
                "连接配置已读回，但实际 Setup/Agent 版本低于本维护计划要求。", null);

        if (statusResult.VerifiedResponseSha256 is not { Length: 64 } responseSha256 ||
            responseSha256.Any(character => !Uri.IsHexDigit(character)))
            return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.PendingVerification,
                pending + "签名状态响应摘要格式无效。", null);
        var evidence = new MaintenanceEvidence("signed-configuration-final-status",
            plan.Device.PinnedIdentityFingerprint, responseSha256, commandId, status.CollectedUtc);
        return new MaintenanceAdapterResult(operationId, MaintenanceAdapterDisposition.ConfigurationSynchronized,
            summary + "；教师连接指纹、运行版本、配置摘要与 SYSTEM 健康状态均已签名读回。", evidence,
            new MaintenanceComponentVersions(status.StudentSetupVersion, status.AgentVersion,
                reported.AgentProductVersion, null), health.UpdateTransactionState, status.CollectedUtc);
    }

    private static string? MaximumVersion(string? first, string? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return ApplicationReleaseClient.CompareVersions(first, second) >= 0 ? first : second;
    }

    private static string? ValidateBeforeUpdateSend(MaintenancePlan plan,
        MaintenanceAdapterInspection inspection)
    {
        if (!inspection.IdentityVerified || inspection.Capabilities is null ||
            inspection.CurrentVersions != plan.CurrentVersions)
            return "生成更新计划后，学生身份或组件版本发生变化；未发送安装命令，请重新检查。";
        if (!MatchesTrustSnapshot(plan, inspection.Capabilities))
            return "生成更新计划后，学生配置/能力信任快照发生变化；未发送安装命令，请重新检查。";
        if (plan.Capabilities.ProtocolVersion != inspection.Capabilities.ProtocolVersion ||
            plan.Capabilities.StudentUpdateProtocolVersion != inspection.Capabilities.StudentUpdateProtocolVersion ||
            !string.Equals(plan.Capabilities.AgentRuntimeVersion, inspection.Capabilities.AgentRuntimeVersion,
                StringComparison.Ordinal))
            return "生成更新计划后，学生 Agent 能力发生变化；未发送安装命令，请重新检查。";
        if (!string.Equals(plan.Capabilities.TeacherChannelFingerprint,
                inspection.Capabilities.TeacherChannelFingerprint, StringComparison.OrdinalIgnoreCase))
            return "生成更新计划后，学生教师连接配置发生变化；未发送安装命令，请重新检查。";
        if (inspection.UpdateTransactionState is not ("notFound" or "committed" or "rolledBack" or "superseded"))
            return "学生端事务状态未知或尚未结束；未发送第二个更新命令。";
        return null;
    }

    private static string? ValidateBeforeConfigurationSync(MaintenancePlan plan,
        MaintenanceAdapterInspection inspection)
    {
        if (!inspection.IdentityVerified || inspection.Capabilities is null ||
            inspection.CurrentVersions.StudentSetupVersion is not { } actualSetup ||
            inspection.CurrentVersions.AgentRuntimeVersion is not { } actualAgent)
            return "配置同步前未能重新核验学生身份或实际组件版本；未发送配置。";
        if (!MatchesTrustSnapshot(plan, inspection.Capabilities))
            return "配置同步前发现校区/设备信任配置发生变化；未发送配置，请重新检查。";
        if (inspection.UpdateTransactionState is not ("notFound" or "committed" or "rolledBack" or "superseded"))
            return "配置同步前发现学生端事务状态未知或尚未结束；未发送配置。";

        var currentSetup = plan.CurrentVersions.StudentSetupVersion;
        var currentAgent = plan.CurrentVersions.AgentRuntimeVersion;
        if (currentSetup is null || currentAgent is null)
            return "配置同步计划没有可靠的起始 Setup/Agent 版本；未发送配置。";
        var requiredSetup = plan.TargetSetupVersion is { } setupTarget &&
                            ApplicationReleaseClient.CompareVersions(setupTarget, currentSetup) > 0
            ? setupTarget
            : currentSetup;
        var requiredAgent = plan.TargetAgentVersion is { } agentTarget &&
                            ApplicationReleaseClient.CompareVersions(agentTarget, currentAgent) > 0
            ? agentTarget
            : currentAgent;
        if (ApplicationReleaseClient.CompareVersions(actualSetup, requiredSetup) < 0 ||
            ApplicationReleaseClient.CompareVersions(actualAgent, requiredAgent) < 0)
            return "配置同步前的已签名版本未达到本维护计划要求；拒绝跨过未完成的软件更新阶段。";

        var before = plan.Capabilities.TeacherChannelFingerprint;
        var now = inspection.Capabilities.TeacherChannelFingerprint;
        if (now is not null && before is not null &&
            !string.Equals(now, before, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(now, plan.ConfigurationSha256, StringComparison.OrdinalIgnoreCase))
            return "配置同步计划生成后，学生端已出现第三种教师连接配置；停止覆盖并要求重新检查。";
        return null;
    }

    private static bool MatchesTrustSnapshot(MaintenancePlan plan, MaintenanceCapabilities current) =>
        string.Equals(plan.Device.PinnedIdentityFingerprint, current.AgentIdentityFingerprint,
            StringComparison.Ordinal) &&
        string.Equals(plan.Capabilities.ConfigurationFingerprint, current.ConfigurationFingerprint,
            StringComparison.OrdinalIgnoreCase);

    private static MaintenanceAdapterInspection Unverified(string detail) => new(false,
        new MaintenanceComponentVersions(null, null, null, null), null, null, null, detail);

    private static bool IsConfigurationReconciliation(MaintenanceRun run)
    {
        if (run.State == MaintenanceRunState.ConfigurationSyncing) return true;
        if (run.State != MaintenanceRunState.PendingVerification || run.Plan?.SynchronizeConfiguration != true)
            return false;
        var configurationIndex = run.Transitions.ToList().FindLastIndex(item =>
            item.To == MaintenanceRunState.ConfigurationSyncing);
        var agentHealthIndex = run.Transitions.ToList().FindLastIndex(item =>
            item.To == MaintenanceRunState.AgentHealthVerified);
        return configurationIndex >= 0 && configurationIndex > agentHealthIndex;
    }

    private sealed record TeacherChannelIdentity(IReadOnlyList<string> Endpoints, string RootCertificateBase64,
        string BootstrapFingerprint);
    private sealed record LegacyStatusReadback(StudentAgentStatusResponse? Status, string? ResponseSha256,
        string Detail, string? AgentAssemblyVersion = null);
}
