using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace VeyonCampus.Core;

/// <summary>Starts one matching elevated Worker and exchanges exactly one bounded operation.</summary>
public static class ElevatedWorkerClient
{
    private static readonly TimeSpan UserConsentTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(20);

    public static async Task<PrivilegedWorkerResponse> ExecuteAsync(VeyonCampusRole role,
        Func<Guid, WorkerCallerIdentity, PrivilegedWorkerRequest> requestFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        PrivilegedWorkerRequest? request = null;
        byte[]? requestFrame = null;
        byte[]? responseFrame = null;
        Process? workerProcess = null;
        var requestSent = false;
        var requestId = Guid.NewGuid();
        var stepId = "worker-operation";
        NamedPipeServerStream? server = null;

        try
        {
            if (!OperatingSystem.IsWindows())
                return Failure(ExecutionPlan.Failed, "提权 Worker 仅支持 Windows。", requestId, stepId);

            var caller = WorkerInstallationGuard.CaptureCurrentUiIdentity(role);
            request = requestFactory(requestId, caller);
            stepId = PrivilegedWorkerDispatcher.GetStepId(request.Operation);
            if (request.RequestId != requestId || request.Caller != caller)
                throw new InvalidDataException("Worker request factory changed its request or caller identity.");
            PrivilegedWorkerProtocol.ValidateRequest(request);
            var installation = WorkerInstallationGuard.ValidateCurrentUi(role, caller);
            requestFrame = PrivilegedWorkerProtocol.SerializeRequest(request);

            var pipeName = "VeyonCampus-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            server = WindowsWorkerPipe.CreateServer(pipeName, caller.UserSid);
            var launchContext = new WorkerLaunchContext(pipeName, requestId, caller);
            var needsElevation = RequiresElevation(PlatformFacts.IsCurrentProcessElevated);
            var startInfo = new ProcessStartInfo
            {
                FileName = installation.WorkerExecutablePath,
                Arguments = string.Join(" ", launchContext.ToArguments().Select(QuoteWindowsArgument)),
                WorkingDirectory = installation.InstallRoot,
                UseShellExecute = needsElevation,
                Verb = needsElevation ? "runas" : "",
                CreateNoWindow = !needsElevation,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                workerProcess = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Windows did not start the elevated Worker.");
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                return Failure(ExecutionPlan.Cancelled, "已取消管理员权限确认；没有发送操作请求。", requestId, stepId);
            }

            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(UserConsentTimeout);
                await server.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
            }

            var workerPid = WindowsWorkerPipe.GetClientProcessId(server.SafePipeHandle);
            if (workerPid == caller.ProcessId ||
                workerProcess is not null && !workerProcess.HasExited && workerProcess.Id != workerPid)
                throw new UnauthorizedAccessException("The Worker pipe peer is not the launched Worker process.");
            var workerIdentity = WindowsProcessIdentityReader.Read(workerPid);
            if (!workerIdentity.IsElevated || workerIdentity.SessionId != caller.SessionId ||
                !PathEquals(workerIdentity.ImagePath, installation.WorkerExecutablePath) ||
                !string.Equals(WorkerInstallationGuard.NormalizeProductVersion(workerIdentity.ProductVersion),
                    installation.ProductVersion, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The Worker pipe peer identity, elevation, path, or version is invalid.");

            requestSent = true;
            await PrivilegedWorkerProtocol.WriteFrameAsync(server, requestFrame, CancellationToken.None).ConfigureAwait(false);
            using var responseTimeout = new CancellationTokenSource(OperationTimeout);
            responseFrame = await PrivilegedWorkerProtocol.ReadFrameAsync(server, responseTimeout.Token).ConfigureAwait(false);
            var response = PrivilegedWorkerProtocol.DeserializeResponse(responseFrame);
            if (response.RequestId != requestId)
                return Failure(ExecutionPlan.NeedsReview,
                    "Worker 回应的请求 ID 不匹配；操作结果需要检查。", requestId, stepId);
            if (!string.Equals(response.Result.StepId, stepId, StringComparison.Ordinal))
                return Failure(ExecutionPlan.NeedsReview,
                    "Worker 回应的步骤标识不匹配；操作结果需要检查。", requestId, stepId);
            if (!string.Equals(response.WorkerUserSid, workerIdentity.UserSid, StringComparison.Ordinal) ||
                response.WorkerSessionId != workerIdentity.SessionId)
                return Failure(ExecutionPlan.NeedsReview,
                    "Worker 未返回与已验证进程一致的管理员身份；操作结果需要检查。", requestId, stepId);

            if (workerProcess is not null)
            {
                try { await workerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    try { workerProcess.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
            }
            return response;
        }
        catch (OperationCanceledException) when (!requestSent)
        {
            return Failure(ExecutionPlan.Cancelled, "管理员权限确认前已取消；没有发送操作请求。", requestId, stepId);
        }
        catch (Exception exception)
        {
            return Failure(requestSent ? ExecutionPlan.NeedsReview : ExecutionPlan.Failed,
                requestSent
                    ? "Worker 连接在发送操作后中断；本机状态可能已变化，请核对后再决定是否重试。"
                    : "无法启动或验证提权 Worker：" + exception.Message,
                requestId, stepId);
        }
        finally
        {
            if (request?.SecretUtf8 is not null) CryptographicOperations.ZeroMemory(request.SecretUtf8);
            if (requestFrame is not null) CryptographicOperations.ZeroMemory(requestFrame);
            if (responseFrame is not null) CryptographicOperations.ZeroMemory(responseFrame);
            server?.Dispose();
            workerProcess?.Dispose();
        }
    }

    public static bool RequiresElevation(bool isElevated) => !isElevated;

    private static PrivilegedWorkerResponse Failure(string status, string detail, Guid requestId, string stepId) =>
        new(PrivilegedWorkerProtocol.CurrentVersion, requestId,
            new StepResult(stepId, status, detail));

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
            return argument;
        var builder = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }
}
