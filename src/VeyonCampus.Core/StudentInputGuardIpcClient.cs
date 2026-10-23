using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using System.Runtime.Versioning;

namespace VeyonCampus.Core;

/// <summary>Bounded SYSTEM-to-interactive-session pipe client with strict peer identity checks.</summary>
internal static class StudentInputGuardIpcClient
{
    internal const string PipePrefix = "VeyonCampus.StudentInputGuard.";
    internal const int MaximumMessageBytes = 16 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    [SupportedOSPlatform("windows")]
    public static async Task<StudentInputGuardIpcReceipt> ReadStatusAsync(CancellationToken cancellationToken,
        TimeSpan? responseTimeout = null)
    {
        var requestId = Guid.NewGuid();
        if (!File.Exists(GetGuardExecutablePath()))
            return CreateUnavailable(requestId, false,
                "未安装交互桌面 Guard；没有执行键鼠操作。");
        IReadOnlyList<int> sessions;
        try { sessions = GetActiveInteractiveSessions(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                          UnauthorizedAccessException or IOException)
        {
            return CreateUnavailable(requestId, true,
                "无法安全枚举活动交互桌面会话；状态未知。" + exception.GetType().Name);
        }
        if (sessions.Count != 1)
            return CreateUnavailable(requestId, true, sessions.Count == 0
                ? "当前没有唯一活动的交互桌面会话。"
                : "检测到多个活动交互桌面会话；为避免控制错会话，状态未知。");

        var command = new StudentInputGuardIpcCommand(1, requestId,
            StudentInputGuardIpcOperation.ReadStatus, null, null, null);
        return await SendAsync(sessions[0], command, cancellationToken, responseTimeout).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    public static async Task<StudentInputGuardIpcReceipt> ExecuteAsync(StudentInputGuardCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!File.Exists(GetGuardExecutablePath()))
            return CreateUnavailable(command.CommandId, false,
                "未安装交互桌面 Guard；没有执行键鼠操作。");
        IReadOnlyList<int> sessions;
        try { sessions = GetActiveInteractiveSessions(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                          UnauthorizedAccessException or IOException)
        {
            return CreateUnavailable(command.CommandId, true,
                "无法安全枚举活动交互桌面会话；没有执行键鼠操作。" + exception.GetType().Name);
        }
        if (sessions.Count != 1)
            return CreateUnavailable(command.CommandId, true, sessions.Count == 0
                ? "当前没有唯一活动的交互桌面会话；没有执行键鼠操作。"
                : "检测到多个活动交互桌面会话；没有执行键鼠操作。");

        var ipcCommand = new StudentInputGuardIpcCommand(1, command.CommandId,
            StudentInputGuardIpcOperation.Execute, command.Action, command.LeaseId,
            command.LeaseExpiresUtc);
        return await SendAsync(sessions[0], ipcCommand, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<StudentInputGuardIpcReceipt> SendAsync(int sessionId,
        StudentInputGuardIpcCommand command, CancellationToken cancellationToken,
        TimeSpan? responseTimeout = null)
    {
        using var pipe = new NamedPipeClientStream(".", PipePrefix + sessionId,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(responseTimeout ?? ConnectTimeout);
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            VerifyServerPeer(pipe.SafePipeHandle, sessionId);
            await WriteMessageAsync(pipe, command, timeout.Token).ConfigureAwait(false);
            var response = await ReadMessageAsync<StudentInputGuardIpcReceipt>(pipe, timeout.Token)
                .ConfigureAwait(false);
            ValidateResponse(response, command.RequestId, command.Operation);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
                                          InvalidDataException or JsonException or
                                          System.ComponentModel.Win32Exception or InvalidOperationException or
                                          UnauthorizedAccessException or CryptographicException or
                                          ArgumentException)
        {
            return CreateUnavailable(command.RequestId, true,
                exception is OperationCanceledException
                    ? "交互桌面 Guard 未在时限内回应；输入状态未知。"
                    : "本机 Guard 通信或进程身份校验失败；输入状态未知。");
        }
    }

    private static void ValidateResponse(StudentInputGuardIpcReceipt response, Guid requestId,
        StudentInputGuardIpcOperation operation)
    {
        if (response.SchemaVersion != 1 || response.RequestId != requestId ||
            !Enum.IsDefined(response.Phase) || !Enum.IsDefined(response.Outcome) ||
            response.ReportedUtc.Offset != TimeSpan.Zero ||
            response.ReportedUtc < DateTimeOffset.UtcNow.AddMinutes(-1) ||
            response.ReportedUtc > DateTimeOffset.UtcNow.AddMinutes(1) ||
            string.IsNullOrWhiteSpace(response.Detail) || response.Detail.Length > 1024 ||
            response.Detail.Any(char.IsControl) ||
            (!response.Supported && (response.Phase != StudentInputGuardPhase.Unsupported ||
                                     response.LeaseId is not null || response.LeaseExpiresUtc is not null)) ||
            (response.LeaseId is null) != (response.LeaseExpiresUtc is null) ||
            response.LeaseId == Guid.Empty ||
            response.LeaseExpiresUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
            response.LeaseExpiresUtc is { } expiry && (expiry <= response.ReportedUtc ||
                expiry > response.ReportedUtc.Add(StudentInputGuardCommandCryptography.MaximumLeaseLifetime)) ||
            response.Phase == StudentInputGuardPhase.Locked && response.LeaseId is null ||
            operation == StudentInputGuardIpcOperation.ReadStatus &&
            response.Outcome != StudentInputGuardOutcome.Succeeded)
            throw new InvalidDataException("Guard 本机回执字段无效或与请求不匹配。");
    }

    private static StudentInputGuardIpcReceipt CreateUnavailable(Guid requestId, bool installed,
        string detail) => new(1, requestId, installed,
        installed ? StudentInputGuardPhase.Unknown : StudentInputGuardPhase.Unsupported,
        null, null, installed ? StudentInputGuardOutcome.Unknown : StudentInputGuardOutcome.Unsupported,
        DateTimeOffset.UtcNow, detail);

    private static async Task WriteMessageAsync<T>(Stream stream, T message,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (bytes.Length is 0 or > MaximumMessageBytes)
            throw new InvalidDataException("Guard 本机命令超过大小限制。");
        var framed = new byte[bytes.Length + 1];
        bytes.CopyTo(framed, 0);
        framed[^1] = (byte)'\n';
        await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadMessageAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1];
        while (buffer.Length <= MaximumMessageBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Guard 在返回回执前关闭了管道。");
            if (chunk[0] == (byte)'\n') break;
            if (chunk[0] != (byte)'\r') buffer.WriteByte(chunk[0]);
        }
        if (buffer.Length is 0 or > MaximumMessageBytes)
            throw new InvalidDataException("Guard 本机回执超过大小限制。");
        var json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidDataException("Guard 本机回执为空。");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyServerPeer(SafePipeHandle pipeHandle, int expectedSessionId)
    {
        if (!GetNamedPipeServerProcessId(pipeHandle, out var processId) || processId == 0 ||
            !GetNamedPipeServerSessionId(pipeHandle, out var sessionId) || sessionId != expectedSessionId)
            throw new InvalidDataException("无法确认 Guard 管道服务端进程和会话。");
        using var process = Process.GetProcessById(checked((int)processId));
        var serverPath = WindowsProcessImagePath.Read(checked((int)processId));
        if (process.SessionId != expectedSessionId ||
            !string.Equals(serverPath, GetGuardExecutablePath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Guard 管道服务端不属于安装目录中的当前会话进程。");
    }

    [SupportedOSPlatform("windows")]
    private static string GetGuardExecutablePath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            throw new InvalidDataException("无法解析 Guard 的受保护安装目录。");
        return Path.GetFullPath(Path.Combine(programFiles, "Veyon Campus", "Student", "InputGuard",
            "VeyonCampus.StudentInputGuard.exe"));
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<int> GetActiveInteractiveSessions()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var sessions = new List<int>();
            var itemSize = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<WtsSessionInfo>(IntPtr.Add(buffer, index * itemSize));
                if (item.State == WtsConnectState.Active && item.SessionId > 0)
                    sessions.Add(item.SessionId);
            }
            return sessions;
        }
        finally { WTSFreeMemory(buffer); }
    }

    private enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr StationName;
        public WtsConnectState State;
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "WTSEnumerateSessionsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version,
        out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint serverSessionId);
}
