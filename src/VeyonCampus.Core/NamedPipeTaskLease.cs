using System.IO.Pipes;

namespace VeyonCampus.Core;

/// <summary>
/// Adds a same-machine, cross-process lease to the in-process task slot.
/// The pipe is kept open for the full operation. It is not used to exchange
/// client data; it only reserves one named-pipe instance with a current-user ACL.
/// </summary>
public sealed class NamedPipeTaskLease : ITaskLease
{
    private const string PipeName = "VeyonCampus.SystemMutationLease.v1";
    private readonly TaskLease _inProcessLease = new();
    private NamedPipeServerStream? _server;

    public bool TryAcquire(out string denialReason)
    {
        if (!_inProcessLease.TryAcquire(out denialReason))
            return false;

        NamedPipeServerStream? server = null;
        try
        {
            server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _server = server;
            denialReason = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   NotSupportedException)
        {
            server?.Dispose();
            _inProcessLease.Dispose();
            denialReason = ex is IOException
                ? "另一个 Veyon Campus 实例正在执行操作；请等待该任务结束后再试。"
                : "无法取得本机任务锁；请检查当前用户权限后重试。";
            return false;
        }
    }

    public void Dispose()
    {
        var server = Interlocked.Exchange(ref _server, null);
        try
        {
            server?.Dispose();
        }
        finally
        {
            _inProcessLease.Dispose();
        }
    }
}
