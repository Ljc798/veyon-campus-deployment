using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using VeyonCampus.Core;

namespace VeyonCampus.InputGuard;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class StudentInputGuardServer
{
    private const int MaximumMessageBytes = 16 * 1024;
    private const string PipePrefix = "VeyonCampus.StudentInputGuard.";
    private const int PipeAccessDuplex = 0x00000003;
    private const int FileFlagOverlapped = 0x40000000;
    private const int FileFlagFirstPipeInstance = 0x00080000;
    private const int PipeRejectRemoteClients = 0x00000008;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly int _sessionId;
    private readonly string _pipeName;
    private readonly BlockingCollection<PendingRequest> _requests = new();
    private readonly Thread _lockThread;
    private bool _supported = true;
    private bool _locked;
    private Guid? _leaseId;
    private DateTimeOffset? _leaseExpiresUtc;
    private long _leaseExpiresTimestamp;
    private StudentInputGuardPhase _phase = StudentInputGuardPhase.Unlocked;
    private readonly HashSet<Guid> _requestIds = [];
    private readonly Queue<Guid> _requestOrder = [];

    public StudentInputGuardServer()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            throw new PlatformNotSupportedException("StudentInputGuard 只能在 Windows 交互桌面运行。");
        using var process = Process.GetCurrentProcess();
        _sessionId = process.SessionId;
        if (_sessionId <= 0) throw new UnauthorizedAccessException("拒绝在 Session 0 运行交互桌面 Guard。");
        _pipeName = PipePrefix + _sessionId;
        _lockThread = new Thread(RunLockThread)
        {
            IsBackground = false,
            Name = "Veyon Campus InputGuard lease",
            Priority = ThreadPriority.Normal
        };
        _lockThread.SetApartmentState(ApartmentState.STA);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _lockThread.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var pipe = CreateSecureServerPipe(_pipeName);
                try { await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false); }
                catch (IOException) when (cancellationToken.IsCancellationRequested) { break; }
                VerifyAgentClient(pipe.SafePipeHandle);
                var request = await ReadMessageAsync<StudentInputGuardIpcCommand>(pipe, cancellationToken)
                    .ConfigureAwait(false);
                var receipt = await QueueRequestAsync(request, cancellationToken).ConfigureAwait(false);
                await WriteMessageAsync(pipe, receipt, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _requests.CompleteAdding();
            if (_lockThread.IsAlive) _lockThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private Task<StudentInputGuardIpcReceipt> QueueRequestAsync(StudentInputGuardIpcCommand command,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<StudentInputGuardIpcReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ValidateCommand(command);
        _requests.Add(new PendingRequest(command, completion), cancellationToken);
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
    }

    private void RunLockThread()
    {
        try
        {
            while (!_requests.IsCompleted)
            {
                var wait = _locked ? GetLeaseWaitMilliseconds() : Timeout.Infinite;
                if (!_requests.TryTake(out var request, wait))
                {
                    ExpireLease();
                    continue;
                }
                try { request.Completion.TrySetResult(HandleRequest(request.Command)); }
                catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or
                                                  ArgumentException or System.ComponentModel.Win32Exception)
                {
                    request.Completion.TrySetResult(CreateReceipt(request.Command.RequestId,
                        StudentInputGuardOutcome.Failed, StudentInputGuardPhase.Unknown,
                        "Guard 未能确认操作结果：" + exception.Message, null, null));
                }
            }
        }
        finally
        {
            if (_locked) NativeMethods.BlockInput(false);
            _locked = false;
            _phase = StudentInputGuardPhase.Unknown;
            _leaseId = null;
            _leaseExpiresUtc = null;
        }
    }

    private StudentInputGuardIpcReceipt HandleRequest(StudentInputGuardIpcCommand command)
    {
        if (!_requestIds.Add(command.RequestId))
            return CreateReceipt(command.RequestId, StudentInputGuardOutcome.ReplayRejected, _phase,
                "拒绝重复的 Guard 本机请求。", _leaseId, _leaseExpiresUtc);
        _requestOrder.Enqueue(command.RequestId);
        while (_requestOrder.Count > 4096) _requestIds.Remove(_requestOrder.Dequeue());

        if (command.Operation == StudentInputGuardIpcOperation.ReadStatus)
            return CreateReceipt(command.RequestId, StudentInputGuardOutcome.Succeeded, _phase,
                "Guard 交互会话状态已读取。", _leaseId, _leaseExpiresUtc);

        var now = DateTimeOffset.UtcNow;
        switch (command.Action)
        {
            case StudentInputGuardAction.Activate:
                if (_locked)
                    return CreateReceipt(command.RequestId, StudentInputGuardOutcome.Conflict, _phase,
                        "已有输入锁定租约；拒绝覆盖。", _leaseId, _leaseExpiresUtc);
                if (!NativeMethods.BlockInput(true))
                    return CreateReceipt(command.RequestId, StudentInputGuardOutcome.Failed,
                        StudentInputGuardPhase.Unlocked,
                        $"Windows 未接受输入阻止请求（错误码 {Marshal.GetLastWin32Error()}）。", null, null);
                _locked = true;
                _phase = StudentInputGuardPhase.Locked;
                _leaseId = command.LeaseId;
                SetLeaseExpiry(command.LeaseExpiresUtc!.Value, now);
                return CreateReceipt(command.RequestId, StudentInputGuardOutcome.Succeeded, _phase,
                    "Windows 已接受本会话输入阻止请求；租约最长 20 秒。", _leaseId, _leaseExpiresUtc);

            case StudentInputGuardAction.Renew:
                if (!_locked || _leaseId != command.LeaseId)
                    return CreateReceipt(command.RequestId, StudentInputGuardOutcome.Conflict,
                        _phase == StudentInputGuardPhase.Locked ? StudentInputGuardPhase.Unknown : _phase,
                        "当前租约与续期请求不匹配；没有改变输入状态。", _leaseId, _leaseExpiresUtc);
                SetLeaseExpiry(command.LeaseExpiresUtc!.Value, now);
                return CreateReceipt(command.RequestId, StudentInputGuardOutcome.Succeeded, _phase,
                    "输入阻止租约已续期。", _leaseId, _leaseExpiresUtc);

            case StudentInputGuardAction.Release:
                if (!_locked || _leaseId != command.LeaseId)
                    return CreateReceipt(command.RequestId, StudentInputGuardOutcome.Conflict,
                        _phase == StudentInputGuardPhase.Locked ? StudentInputGuardPhase.Unknown : _phase,
                        "当前租约与解除请求不匹配；没有改变输入状态。", _leaseId, _leaseExpiresUtc);
                _phase = StudentInputGuardPhase.Unlocking;
                if (!NativeMethods.BlockInput(false))
                {
                    _phase = StudentInputGuardPhase.Unknown;
                    _locked = false;
                    _leaseId = null;
                    _leaseExpiresUtc = null;
                    return CreateReceipt(command.RequestId, StudentInputGuardOutcome.Unknown, _phase,
                        "Windows 未确认解除；请人工核对键鼠状态。", null, null);
                }
                _locked = false;
                _phase = StudentInputGuardPhase.Unlocked;
                _leaseId = null;
                _leaseExpiresUtc = null;
                _leaseExpiresTimestamp = 0;
                return CreateReceipt(command.RequestId, StudentInputGuardOutcome.Succeeded, _phase,
                    "输入阻止已由原锁定线程解除。", null, null);

            default:
                throw new InvalidDataException("Guard 收到未知操作类型。");
        }
    }

    private void ExpireLease()
    {
        if (!_locked) return;
        _phase = StudentInputGuardPhase.Unlocking;
        var released = false;
        for (var attempt = 0; attempt < 3 && !released; attempt++)
        {
            released = NativeMethods.BlockInput(false);
            if (!released) Thread.Sleep(100);
        }
        _locked = false;
        _phase = released ? StudentInputGuardPhase.Expired : StudentInputGuardPhase.Unknown;
        _leaseId = null;
        _leaseExpiresUtc = null;
        _leaseExpiresTimestamp = 0;
        if (!released)
        {
            // Exiting this same thread is an OS recovery path for BlockInput.
            _supported = false;
            _requests.CompleteAdding();
        }
    }

    private int GetLeaseWaitMilliseconds()
    {
        var remainingTicks = _leaseExpiresTimestamp - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0) return 0;
        var remainingMilliseconds = remainingTicks * 1000.0 / Stopwatch.Frequency;
        return (int)Math.Clamp(Math.Ceiling(remainingMilliseconds), 1, 1000);
    }

    private void SetLeaseExpiry(DateTimeOffset expiryUtc, DateTimeOffset nowUtc)
    {
        var remaining = expiryUtc - nowUtc;
        if (expiryUtc.Offset != TimeSpan.Zero || remaining <= TimeSpan.Zero ||
            remaining > StudentInputGuardCommandCryptography.MaximumLeaseLifetime)
            throw new InvalidDataException("Guard 租约时间无效或超过 20 秒上限。");
        _leaseExpiresUtc = expiryUtc;
        _leaseExpiresTimestamp = Stopwatch.GetTimestamp() +
            checked((long)(remaining.TotalSeconds * Stopwatch.Frequency));
    }

    private StudentInputGuardIpcReceipt CreateReceipt(Guid requestId, StudentInputGuardOutcome outcome,
        StudentInputGuardPhase phase, string detail, Guid? leaseId, DateTimeOffset? expiryUtc) =>
        new(1, requestId, _supported, phase, leaseId, expiryUtc, outcome,
            DateTimeOffset.UtcNow, detail);

    private static void ValidateCommand(StudentInputGuardIpcCommand command)
    {
        var malformedStatus = command.Operation == StudentInputGuardIpcOperation.ReadStatus &&
            (command.Action is not null || command.LeaseId is not null || command.LeaseExpiresUtc is not null);
        var actionNeedsExpiry = command.Action is StudentInputGuardAction.Activate or
            StudentInputGuardAction.Renew;
        var malformedExecute = command.Operation == StudentInputGuardIpcOperation.Execute &&
            (command.Action is null || !Enum.IsDefined(command.Action.Value) ||
             command.LeaseId is null || command.LeaseId == Guid.Empty ||
             (actionNeedsExpiry ? command.LeaseExpiresUtc is null : command.LeaseExpiresUtc is not null));
        if (command.SchemaVersion != 1 || command.RequestId == Guid.Empty ||
            !Enum.IsDefined(command.Operation) || malformedStatus || malformedExecute)
            throw new InvalidDataException("Guard 本机命令版本、请求编号或字段无效。");
    }

    private static async Task<T> ReadMessageAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length <= MaximumMessageBytes)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Agent 在发送完整命令前关闭了管道。");
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') buffer.WriteByte(one[0]);
        }
        if (buffer.Length is 0 or > MaximumMessageBytes)
            throw new InvalidDataException("Agent 本机命令超过大小限制。");
        return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), JsonOptions)
               ?? throw new InvalidDataException("Agent 本机命令为空。");
    }

    private static async Task WriteMessageAsync<T>(Stream stream, T message,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (json.Length is 0 or > MaximumMessageBytes)
            throw new InvalidDataException("Guard 本机回执超过大小限制。");
        var framed = new byte[json.Length + 1];
        json.CopyTo(framed, 0);
        framed[^1] = (byte)'\n';
        await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NamedPipeServerStream CreateSecureServerPipe(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value ?? throw new UnauthorizedAccessException("无法确定 Guard 用户 SID。");
        var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;{userSid})";
        if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1,
                out var descriptor, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false
            };
            var handle = NativeMethods.CreateNamedPipe(
                @"\\.\pipe\" + pipeName,
                PipeAccessDuplex | FileFlagOverlapped | FileFlagFirstPipeInstance,
                PipeRejectRemoteClients, 1, MaximumMessageBytes, MaximumMessageBytes, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new System.ComponentModel.Win32Exception(error);
            }
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true,
                isConnected: false, handle);
        }
        finally { NativeMethods.LocalFree(descriptor); }
    }

    private static void VerifyAgentClient(SafePipeHandle handle)
    {
        if (!NativeMethods.GetNamedPipeClientProcessId(handle, out var pid) || pid == 0 ||
            !NativeMethods.GetNamedPipeClientSessionId(handle, out var sessionId) || sessionId != 0)
            throw new UnauthorizedAccessException("无法确认本机 Agent 管道客户端。");
        using var process = Process.GetProcessById(checked((int)pid));
        var path = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
        if (process.SessionId != 0 || !string.Equals(path,
                WebsitePolicyAgentInstaller.InstalledExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Guard 仅接受安装目录中 Session 0 Agent 的命令。");
    }

    private sealed record PendingRequest(StudentInputGuardIpcCommand Command,
        TaskCompletionSource<StudentInputGuardIpcReceipt> Completion);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool block);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor,
            uint revision, out IntPtr securityDescriptor, out uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "CreateNamedPipeW")]
        internal static extern SafePipeHandle CreateNamedPipe(string name, int openMode, int pipeMode,
            int maximumInstances, int outputBufferSize, int inputBufferSize, int defaultTimeout,
            ref SecurityAttributes securityAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint clientSessionId);
    }
}
