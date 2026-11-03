using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeyonCampus.Core;

namespace VeyonCampus.Core.Maintenance;

public sealed record MaintenanceServiceHealthPayload(
    string State,
    string ServiceVersion,
    int ProtocolVersion,
    string? TeacherConfigurationFingerprint,
    string AgentConfigurationFingerprint,
    DateTimeOffset CheckedUtc,
    bool RunningAsSystem,
    bool? ManagedAgentTaskTrusted,
    long TeacherConfigurationRevision = 0);

public sealed record MaintenanceServiceCapabilitiesPayload(
    int ProtocolVersion,
    IReadOnlyList<string> SupportedCommands,
    IReadOnlyList<string> UnsupportedCommands,
    string AgentConfigurationFingerprint,
    string? TeacherConfigurationFingerprint);

public sealed record MaintenanceServiceInstalledVersionsPayload(
    string? StudentSetupVersion,
    string? AgentRuntimeVersion,
    string? AgentAssemblyVersion,
    string? MaintenanceServiceVersion,
    string TransactionState,
    DateTimeOffset CheckedUtc,
    bool RunningAsSystem,
    bool? ManagedAgentTaskTrusted);

public sealed record MaintenanceServiceTeacherConfigurationSyncPayload(
    string ConfigurationFingerprint,
    DateTimeOffset UpdatedUtc,
    MaintenanceServiceInstalledVersionsPayload InstalledVersions,
    long ConfigurationRevision = 0);

public sealed record MaintenanceServiceTransactionReadbackPayload(
    StudentUpdateTransactionStatus Transaction,
    MaintenanceServiceInstalledVersionsPayload InstalledVersions);

public sealed record MaintenanceServiceDispatchResult(string SignedResponseJson, bool ReconnectAfterResponse,
    Func<Task>? AfterResponseSent = null);

/// <summary>
/// Validates signed requests, commits the nonce before dispatch, and exposes only the fixed
/// Maintenance protocol operations. Update execution is explicitly not advertised until its
/// signed package handoff is implemented by the service host.
/// </summary>
public sealed class MaintenanceServiceCommandDispatcher
{
    private const string ReplayFileName = "maintenance-replay-window.json";
    private static readonly string[] SupportedCommands =
    [
        "getHealth",
        "getCapabilities",
        "getInstalledVersions",
        "offerSignedStudentSetupUpdate",
        "getTransactionStatus",
        "recoverPendingTransaction",
        "syncTeacherEndpoint"
    ];
    private static readonly string[] UnsupportedCommands = [];
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [SupportedOSPlatform("windows")]
    public async Task<MaintenanceServiceDispatchResult> DispatchSignedRequestAsync(string configPath,
        string signedRequestJson,
        DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Maintenance 命令仅支持 Windows。");
        EnsureSystemIdentity();
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var bootstrap = MaintenanceServiceConfigurationStore.LoadOrInitializeForSystemService(configPath, now);
        var deviceFingerprint = StudentAgentIdentityKeyStore.ReadFingerprint(configPath);
        var request = MaintenanceProtocolV1.VerifyRequest(signedRequestJson,
            bootstrap.TeacherSigningPublicKeyPem, bootstrap.CampusId, deviceFingerprint, now);
        var directory = MaintenanceServiceIdentityStore.GetServiceDirectory(configPath);
        var replayWindow = new MaintenanceReplayWindow(Path.Combine(directory, ReplayFileName));

        // The durable nonce write happens before any command can cause a side effect.
        var nonceAccepted = await replayWindow.TryAcceptAsync(request.Nonce, request.ExpiresUtc, now,
            cancellationToken).ConfigureAwait(false);
        string resultCode;
        string detail;
        string? payloadJson = null;
        Func<Task>? afterResponseSent = null;
        var reconnectAfterResponse = false;
        if (!nonceAccepted)
        {
            resultCode = "needsReview";
            detail = "该签名请求随机数已处理；为避免重复副作用，请查询原 commandId 或使用新随机数重查。";
        }
        else
        {
            try
            {
                if (request.Command == MaintenanceProtocolV1Command.OfferSignedStudentSetupUpdate)
                {
                    var offer = await MaintenanceServiceUpdateExecutor.PrepareOfferAsync(configPath, request,
                        now, cancellationToken).ConfigureAwait(false);
                    resultCode = offer.ResultCode;
                    detail = offer.Detail;
                    payloadJson = offer.Payload is null ? null : SerializePayload(offer.Payload);
                    afterResponseSent = offer.StartAfterResponseSent;
                    reconnectAfterResponse = afterResponseSent is not null;
                }
                else
                {
                    (resultCode, detail, payloadJson) = await ExecuteAsync(configPath, signedRequestJson,
                        request, bootstrap, now, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or CryptographicException or
                                              InvalidOperationException or TimeoutException or
                                              System.Security.SecurityException or ArgumentException)
            {
                resultCode = "needsReview";
                detail = "Maintenance 命令未完成；保留状态，请使用新请求随机数查询同一 operationId/commandId。";
                afterResponseSent = null;
                reconnectAfterResponse = false;
            }
        }

        using var maintenanceKey = MaintenanceServiceIdentityStore.LoadOrCreateForSystemService(configPath);
        var response = new MaintenanceProtocolV1Response(
            MaintenanceProtocolV1.CurrentVersion,
            MaintenanceProtocolV1.ResponsePurpose,
            request.CampusId,
            deviceFingerprint,
            request.Nonce,
            request.OperationId,
            request.CommandId,
            DateTimeOffset.UtcNow,
            resultCode,
            detail,
            payloadJson);
        return new MaintenanceServiceDispatchResult(MaintenanceProtocolV1.SignResponse(response, maintenanceKey),
            reconnectAfterResponse || request.Command == MaintenanceProtocolV1Command.SyncTeacherEndpoint &&
            resultCode == "succeeded", afterResponseSent);
    }

    private static async Task<(string ResultCode, string Detail, string? PayloadJson)> ExecuteAsync(
        string configPath, string signedRequestJson, MaintenanceProtocolV1Request request,
        MaintenanceServiceBootstrapSnapshot bootstrap, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case MaintenanceProtocolV1Command.GetHealth:
            {
                var state = bootstrap.ConfigurationFingerprint is null ? "degraded" : "ready";
                var payload = new MaintenanceServiceHealthPayload(state, GetServiceVersion(),
                    MaintenanceProtocolV1.CurrentVersion, bootstrap.ConfigurationFingerprint,
                    WebsitePolicyAgent.ConfigFingerprint(
                        MaintenanceServiceIdentityStore.ReadTrustedAgentConfiguration(configPath)), nowUtc,
                    IsSystemIdentity(), ReadManagedAgentTask(configPath) is not null,
                    bootstrap.ConfigurationRevision);
                return (state == "ready" ? "succeeded" : "pending",
                    state == "ready" ? "Maintenance 服务协议与受保护配置可读。" : "缺少教师连接配置，Maintenance 当前不能建立主动维护连接。",
                    SerializePayload(payload));
            }
            case MaintenanceProtocolV1Command.GetCapabilities:
                return ("succeeded", "已返回固定 Maintenance 命令能力及受保护配置指纹。", SerializePayload(
                    new MaintenanceServiceCapabilitiesPayload(MaintenanceProtocolV1.CurrentVersion,
                        Array.AsReadOnly(SupportedCommands), Array.AsReadOnly(UnsupportedCommands),
                        WebsitePolicyAgent.ConfigFingerprint(
                            MaintenanceServiceIdentityStore.ReadTrustedAgentConfiguration(configPath)),
                        bootstrap.ConfigurationFingerprint)));
            case MaintenanceProtocolV1Command.GetInstalledVersions:
                return ("succeeded", "已读取可确认的本机组件版本与事务状态。",
                    SerializePayload(ReadInstalledVersions(configPath, nowUtc)));
            case MaintenanceProtocolV1Command.GetTransactionStatus:
            {
                var commandId = request.CommandId ?? throw new InvalidDataException("事务查询缺少 commandId。" );
                var status = MaintenanceServiceUpdateExecutor.ReadStatus(configPath, commandId, nowUtc) ??
                             StudentUpdateAtomicTransaction.ReadStatus(configPath, commandId);
                var payload = CreateTransactionReadback(configPath, status, nowUtc);
                var resultCode = status.State switch
                {
                    "needsReview" => "needsReview",
                    "inProgress" => "pending",
                    _ => "succeeded"
                };
                return (resultCode, "已读取绑定 commandId 的学生更新事务及组件版本状态。",
                    SerializePayload(payload));
            }
            case MaintenanceProtocolV1Command.RecoverPendingTransaction:
                return await RecoverAsync(configPath, request, nowUtc, cancellationToken).ConfigureAwait(false);
            case MaintenanceProtocolV1Command.SyncTeacherEndpoint:
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The configuration store re-verifies the full signed request at the privileged write boundary.
                var updated = MaintenanceServiceConfigurationStore.ApplySignedTeacherEndpointUpdate(configPath,
                    signedRequestJson, DateTimeOffset.UtcNow);
                var installedVersions = ReadInstalledVersions(configPath, DateTimeOffset.UtcNow);
                return ("succeeded", "教师连接配置已写入 Maintenance 独立配置，并通过目标指纹读回。",
                    SerializePayload(new MaintenanceServiceTeacherConfigurationSyncPayload(
                        updated.ConfigurationFingerprint ?? throw new InvalidDataException(
                            "Maintenance 配置读回缺少目标指纹。"), updated.UpdatedUtc, installedVersions,
                        updated.ConfigurationRevision)));
            }
            case MaintenanceProtocolV1Command.OfferSignedStudentSetupUpdate:
                throw new InvalidOperationException("StudentSetup 更新命令未通过事务准备入口执行。" );
            default:
                return ("notSupported", "该 Maintenance 命令当前未实现。", null);
        }
    }

    private static Task<(string ResultCode, string Detail, string? PayloadJson)> RecoverAsync(
        string configPath, MaintenanceProtocolV1Request request, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var commandId = request.CommandId ?? throw new InvalidDataException("事务恢复缺少 commandId。");
        var maintenanceStatus = MaintenanceServiceUpdateExecutor.ReadStatus(configPath, commandId, nowUtc);
        if (maintenanceStatus is not null)
        {
            var maintenanceReadback = CreateTransactionReadback(configPath, maintenanceStatus, nowUtc);
            return Task.FromResult(maintenanceStatus.State switch
            {
                "committed" or "rolledBack" => ("succeeded",
                    "Maintenance UpdateHelper 更新已读回终态；没有重复启动安装器。",
                    SerializePayload(maintenanceReadback)),
                "needsReview" => ("needsReview",
                    "Maintenance 更新需要现场核对；保留事务和恢复材料。",
                    SerializePayload(maintenanceReadback)),
                _ => ("pending",
                    "Maintenance UpdateHelper 仍在处理或安装结果尚待确认；没有启动第二个安装器。",
                    SerializePayload(maintenanceReadback))
            });
        }
        var record = StudentUpdateAtomicTransaction.Read(configPath);
        if (record is null || record.CommandId != commandId)
        {
            var missing = StudentUpdateAtomicTransaction.ReadStatus(configPath, commandId);
            var readback = CreateTransactionReadback(configPath, missing, nowUtc);
            return Task.FromResult((missing.State == "needsReview" ? "needsReview" : "succeeded",
                "没有与该 commandId 匹配的活动事务；未触碰其他事务。", SerializePayload(readback)));
        }
        if (record.State is StudentUpdateTransactionState.Committed or
            StudentUpdateTransactionState.RolledBack or StudentUpdateTransactionState.Superseded)
        {
            var terminal = StudentUpdateAtomicTransaction.ReadStatus(configPath, commandId);
            var readback = CreateTransactionReadback(configPath, terminal, nowUtc);
            return Task.FromResult(("succeeded", "该事务已经处于终态；没有重复执行恢复。",
                SerializePayload(readback)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var recoveryExitCode = StudentUpdateAtomicTransaction.RecoverSpecificFromCommand(configPath, commandId);
        var status = StudentUpdateAtomicTransaction.ReadStatus(configPath, commandId);
        var updatedReadback = CreateTransactionReadback(configPath, status, DateTimeOffset.UtcNow);
        if (status.State is "committed" or "rolledBack" or "superseded")
            return Task.FromResult(("succeeded", "指定更新事务已执行原子恢复并读回终态。",
                SerializePayload(updatedReadback)));
        if (recoveryExitCode == 2)
            return Task.FromResult(("needsReview", "持久化事务已变化；未对其他 commandId 执行恢复。",
                SerializePayload(updatedReadback)));
        if (recoveryExitCode == 0)
            return Task.FromResult(("pending", "恢复任务已接受但事务仍未达到终态；保留恢复材料并等待再次核对。",
                SerializePayload(updatedReadback)));
        return Task.FromResult(("needsReview", "恢复执行失败或仍未收敛；保留原事务与恢复材料。",
            SerializePayload(updatedReadback)));
    }

    private static MaintenanceServiceTransactionReadbackPayload CreateTransactionReadback(string configPath,
        StudentUpdateTransactionStatus status, DateTimeOffset nowUtc)
    {
        var installed = ReadInstalledVersions(configPath, nowUtc);
        if (MaintenanceServiceUpdateExecutor.ReadTransaction(configPath) is { } maintenanceRecord &&
            maintenanceRecord.CommandId == status.CommandId)
            installed = installed with { TransactionState = status.State };
        return new(status, installed);
    }

    private static MaintenanceServiceInstalledVersionsPayload ReadInstalledVersions(string configPath,
        DateTimeOffset nowUtc)
    {
        string? agentRuntimeVersion = null;
        string? agentAssemblyVersion = null;
        try
        {
            var path = WebsitePolicyAgentInstaller.ReadManagedAgentExecutablePath(configPath);
            PathLinkSecurity.RejectLinks(path);
            if (File.Exists(path))
            {
                agentRuntimeVersion = Path.GetFileName(Path.GetDirectoryName(path))
                                      ?? throw new InvalidDataException("Agent 安装目录缺少版本号。" );
                _ = ApplicationReleaseClient.CompareVersions(agentRuntimeVersion, agentRuntimeVersion);
                agentAssemblyVersion = WorkerLaunchContext.GetAssemblyProductVersion(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.Security.SecurityException or
                                          ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Missing/misbound Agent task is represented as unknown, never as the service's own version.
        }

        return new MaintenanceServiceInstalledVersionsPayload(
            StudentApplicationUpdateProcessor.TryReadInstalledStudentSetupVersion(),
            agentRuntimeVersion,
            agentAssemblyVersion,
            GetServiceVersion(),
            StudentUpdateAtomicTransaction.ReadCurrentState(configPath), nowUtc,
            IsSystemIdentity(), ReadManagedAgentTask(configPath) is not null);
    }

    private static string? ReadManagedAgentTask(string configPath)
    {
        try
        {
            var path = WebsitePolicyAgentInstaller.ReadManagedAgentExecutablePath(configPath);
            PathLinkSecurity.RejectLinks(path);
            return File.Exists(path) ? path : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or System.Security.SecurityException or
                                          ArgumentException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsSystemIdentity()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User?.Value == "S-1-5-18";
    }

    private static void EnsureSystemIdentity()
    {
        if (!IsSystemIdentity())
            throw new UnauthorizedAccessException("Maintenance 命令必须由 SYSTEM 服务处理。");
    }

    internal static string GetServiceVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        return assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly?.GetName().Version?.ToString()
               ?? "unknown";
    }

    private static string SerializePayload<T>(T payload) => JsonSerializer.Serialize(payload, PayloadJsonOptions);
}
