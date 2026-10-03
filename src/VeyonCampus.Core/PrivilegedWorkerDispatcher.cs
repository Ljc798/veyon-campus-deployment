using System.Security.Cryptography;

namespace VeyonCampus.Core;

/// <summary>Runs one protocol-validated operation through fixed product adapters.</summary>
public static class PrivilegedWorkerDispatcher
{
    public static PrivilegedWorkerResponse Dispatch(PrivilegedWorkerRequest request,
        WorkerInstallationInfo installation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(installation);
        var requestId = request.RequestId;
        var operation = request.Operation;
        var operationStarted = false;
        string? publicKeyPem = null;
        bool? teacherKeyCreated = null;
        try
        {
            PrivilegedWorkerProtocol.ValidateRequest(request);
            if (request.Caller.Role != installation.Role)
                throw new InvalidDataException("Worker role and requested operation do not match.");

            StepResult result;
            switch (operation)
            {
                case PrivilegedWorkerOperation.CreateStudentAccount:
                {
                    operationStarted = true;
                    var password = request.SecretUtf8 is null ? null : DecodeSecret(request.SecretUtf8);
                    result = new WindowsAccountAdapter().CreateStudentAccount(
                        request.AccountName!, password, request.ExpectedSid);
                    break;
                }
                case PrivilegedWorkerOperation.ChangeAdminPassword:
                {
                    operationStarted = true;
                    var password = DecodeSecret(request.SecretUtf8!);
                    result = new WindowsAccountAdapter().ChangeAdminPassword(
                        request.AccountName!, password, request.ExpectedSid!);
                    break;
                }
                case PrivilegedWorkerOperation.RenameComputer:
                    operationStarted = true;
                    result = new WindowsRenameAdapter().RequestRename(request.ComputerName!);
                    break;
                case PrivilegedWorkerOperation.InstallVeyon:
                {
                    _ = LoadOptionalPackage(request.PackageRoot);
                    var stagedInstaller = WorkerProtectedStaging.StageVeyonInstaller(
                        request.InstallerPath!, installation, request.Caller.UserSid);
                    try
                    {
                        operationStarted = true;
                        result = new WindowsVeyonAdapter().InstallVeyonOnly(
                            stagedInstaller, request.IsTeacher == true);
                    }
                    finally { WorkerProtectedStaging.DeleteStagedInstaller(stagedInstaller); }
                    if (result.Ok && request.IsTeacher == true)
                    {
                        var authentication = VeyonTeacherAuthentication.Configure();
                        result = authentication.Ok
                            ? result with { Detail = result.Detail + " " + authentication.Detail }
                            : new StepResult("veyon-install", ExecutionPlan.NeedsReview,
                                "Veyon 已安装，但教师密钥认证没有读回确认；请核对本机配置。" + authentication.Detail,
                                result.ExitCode, result.RebootRequired);
                    }
                    break;
                }
                case PrivilegedWorkerOperation.ConfigureVeyon:
                {
                    var package = LoadPackage(request.PackageRoot!);
                    using var snapshot = CreateSnapshot(package);
                    operationStarted = true;
                    result = new WindowsVeyonAdapter().ConfigureVeyonOnly(
                        package, snapshot, request.IsTeacher == true);
                    break;
                }
                case PrivilegedWorkerOperation.InstallWebsitePolicyAgent:
                {
                    var package = LoadPackage(request.PackageRoot!);
                    using var snapshot = CreateSnapshot(package);
                    operationStarted = true;
                    result = WebsitePolicyAgentInstaller.Install(package, snapshot,
                        Path.Combine(Path.GetDirectoryName(installation.UiExecutablePath)!, "WebsitePolicyAgent"));
                    break;
                }
                case PrivilegedWorkerOperation.UninstallWebsitePolicyAgent:
                    operationStarted = true;
                    result = WebsitePolicyAgentInstaller.Uninstall();
                    break;
                case PrivilegedWorkerOperation.ExportTeacherPublicKey:
                {
                    var destination = Path.Combine(Path.GetTempPath(),
                        "veyon-campus-public-" + Guid.NewGuid().ToString("N") + ".pem");
                    try
                    {
                        operationStarted = true;
                        var provisioned = new VeyonTeacherKeyProvisioner().ExportPublicKey(
                            request.CampusId!, destination);
                        teacherKeyCreated = provisioned.Created;
                        result = provisioned.Step;
                        if (result.Ok)
                            publicKeyPem = PackageBuilder.ReadPublicKeyPem(destination);
                    }
                    finally
                    {
                        try { if (File.Exists(destination)) File.Delete(destination); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    break;
                }
                case PrivilegedWorkerOperation.ConfigureTeacherAuthentication:
                    operationStarted = true;
                    result = VeyonTeacherAuthentication.Configure();
                    break;
                case PrivilegedWorkerOperation.AddVeyonRoom:
                    operationStarted = true;
                    var import = VeyonNetworkObjectDirectory.AddLocation(
                        request.LocationName!, request.Computers!);
                    var locationAction = import.LocationCreated ? "新建地点" :
                        import.AddedComputerCount > 0 ? "复用地点" : "无需更改地点";
                    result = new StepResult("room-directory", ExecutionPlan.Succeeded,
                        $"Veyon 静态目录已核对：{locationAction}“{import.LocationName}”，新增 {import.AddedComputerCount} 台、保留并跳过 {import.SkippedComputerCount} 台（规划共 {import.ComputerCount} 台；填写 {import.NamedStudentCount} 个显示名）。");
                    break;
                default:
                    throw new InvalidDataException("Worker operation is not registered.");
            }

            var expectedStepId = GetStepId(operation);
            if (!string.Equals(result.StepId, expectedStepId, StringComparison.Ordinal))
                result = new StepResult(expectedStepId, ExecutionPlan.NeedsReview,
                    "Worker 适配器返回了不匹配的步骤标识；请核对本机状态。");
            return new PrivilegedWorkerResponse(PrivilegedWorkerProtocol.CurrentVersion, requestId,
                result, publicKeyPem, teacherKeyCreated);
        }
        catch (Exception exception)
        {
            var stepId = GetStepId(operation);
            var status = operationStarted ? ExecutionPlan.NeedsReview : ExecutionPlan.Failed;
            var result = new StepResult(stepId, status,
                operationStarted
                    ? "提权操作未返回可信的完成状态；请检查本机状态后再决定是否重试。"
                    : "提权操作未开始：" + exception.Message);
            return new PrivilegedWorkerResponse(PrivilegedWorkerProtocol.CurrentVersion, requestId,
                result, null, teacherKeyCreated);
        }
        finally
        {
            if (request.SecretUtf8 is not null)
                CryptographicOperations.ZeroMemory(request.SecretUtf8);
        }
    }

    private static PackageContext? LoadOptionalPackage(string? packageRoot) =>
        packageRoot is null ? null : LoadPackage(packageRoot);

    private static PackageContext LoadPackage(string packageRoot)
    {
        var package = PackageContext.Load(packageRoot);
        package.VerifyUnchanged();
        return package;
    }

    private static PackageResourceSnapshot CreateSnapshot(PackageContext package) =>
        PackageResourceSnapshot.Create(Path.Combine(Path.GetTempPath(), "VeyonCampus", "worker-snapshots"), package);

    private static string DecodeSecret(byte[] secret) =>
        new System.Text.UTF8Encoding(false, true).GetString(secret);

    public static string GetStepId(PrivilegedWorkerOperation operation) => operation switch
    {
        PrivilegedWorkerOperation.CreateStudentAccount => "student-account",
        PrivilegedWorkerOperation.ChangeAdminPassword => "admin-password",
        PrivilegedWorkerOperation.RenameComputer => "rename",
        PrivilegedWorkerOperation.InstallVeyon => "veyon-install",
        PrivilegedWorkerOperation.ConfigureVeyon => "veyon-key",
        PrivilegedWorkerOperation.InstallWebsitePolicyAgent => "website-agent",
        PrivilegedWorkerOperation.UninstallWebsitePolicyAgent => "website-agent-uninstall",
        PrivilegedWorkerOperation.ExportTeacherPublicKey => "veyon-teacher-key",
        PrivilegedWorkerOperation.ConfigureTeacherAuthentication => "teacher-auth",
        PrivilegedWorkerOperation.AddVeyonRoom => "room-directory",
        _ => "worker-operation"
    };
}
