using System.IO.Pipes;
using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.Worker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        byte[]? requestFrame = null;
        byte[]? responseFrame = null;
        PrivilegedWorkerRequest? request = null;
        try
        {
            if (!OperatingSystem.IsWindows()) return 2;
            var context = WorkerLaunchContext.ParseArguments(args);
            var installation = WorkerInstallationGuard.ValidateCurrentWorker(context.Caller.Role,
                context.Caller.UserSid);

            using var pipe = new NamedPipeClientStream(".", context.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

            var serverPid = WindowsWorkerPipe.GetServerProcessId(pipe.SafePipeHandle);
            if (serverPid != context.Caller.ProcessId)
                throw new UnauthorizedAccessException("Worker pipe server PID does not match the launched UI.");
            var callerProcess = WindowsProcessIdentityReader.Read(serverPid);
            WorkerInstallationGuard.ValidateCallerProcess(context.Caller, callerProcess.ProcessId,
                callerProcess.UserSid, callerProcess.SessionId, callerProcess.StartTimeUtcTicks,
                callerProcess.ImagePath);

            using (var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                requestFrame = await PrivilegedWorkerProtocol.ReadFrameAsync(pipe, requestTimeout.Token)
                    .ConfigureAwait(false);
            request = PrivilegedWorkerProtocol.DeserializeRequest(requestFrame);
            CryptographicOperations.ZeroMemory(requestFrame);
            requestFrame = null;
            if (request.RequestId != context.RequestId || request.Caller != context.Caller)
                throw new UnauthorizedAccessException("Worker request identity does not match its launch context.");

            var response = PrivilegedWorkerDispatcher.Dispatch(request, installation);
            using (var currentWorker = System.Diagnostics.Process.GetCurrentProcess())
            {
                var identity = WindowsProcessIdentityReader.Read(currentWorker.Id);
                response = response with
                {
                    WorkerUserSid = identity.UserSid,
                    WorkerSessionId = identity.SessionId
                };
            }
            responseFrame = PrivilegedWorkerProtocol.SerializeResponse(response);
            using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await PrivilegedWorkerProtocol.WriteFrameAsync(pipe, responseFrame, responseTimeout.Token)
                .ConfigureAwait(false);
            return 0;
        }
        catch
        {
            return 1;
        }
        finally
        {
            if (request?.SecretUtf8 is not null) CryptographicOperations.ZeroMemory(request.SecretUtf8);
            if (requestFrame is not null) CryptographicOperations.ZeroMemory(requestFrame);
            if (responseFrame is not null) CryptographicOperations.ZeroMemory(responseFrame);
        }
    }
}
