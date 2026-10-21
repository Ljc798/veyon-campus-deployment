using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace VeyonCampus.Core;

public enum StudentVeyonServiceState
{
    Unknown,
    NotInstalled,
    Running,
    Stopped,
    StartPending,
    StopPending
}

public sealed record StudentAgentHealthSnapshot(
    bool? RunningAsSystem,
    bool? ManagedTaskMatchesAgent,
    bool? ManagedTaskAclRestricted,
    StudentVeyonServiceState VeyonServiceState,
    string UpdateTransactionState,
    DateTimeOffset CollectedUtc);

/// <summary>Reads only the fixed Veyon service state needed by the student health snapshot.</summary>
public static class StudentVeyonServiceInspector
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const uint ServiceStopped = 1;
    private const uint ServiceStartPending = 2;
    private const uint ServiceStopPending = 3;
    private const uint ServiceRunning = 4;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    [SupportedOSPlatform("windows")]
    public static StudentVeyonServiceState ReadState()
    {
        if (!OperatingSystem.IsWindows()) return StudentVeyonServiceState.Unknown;
        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid) return StudentVeyonServiceState.Unknown;
        using var service = OpenService(manager, VeyonFacts.ServiceName, ServiceQueryStatus);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorServiceDoesNotExist
                ? StudentVeyonServiceState.NotInstalled
                : StudentVeyonServiceState.Unknown;
        }
        return QueryState(service, out var state)
            ? ToState(state)
            : StudentVeyonServiceState.Unknown;
    }

    [SupportedOSPlatform("windows")]
    public static StudentVeyonServiceState EnsureRunning()
    {
        if (!OperatingSystem.IsWindows())
            return StudentVeyonServiceState.Unknown;

        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid) throw LastError("无法打开 Windows 服务管理器。");
        using var service = OpenService(manager, VeyonFacts.ServiceName,
            ServiceQueryStatus | ServiceStart);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
                return StudentVeyonServiceState.NotInstalled;
            throw new Win32Exception(error, "无法读取 VeyonService。");
        }

        if (!QueryState(service, out var state)) throw LastError("无法读取 VeyonService 状态。");
        if (state == ServiceRunning) return StudentVeyonServiceState.Running;
        if (state == ServiceStopped)
        {
            if (!StartService(service, 0, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceAlreadyRunning)
                    throw new Win32Exception(error, "无法启动 VeyonService。");
            }
        }
        else if (state is not (ServiceStartPending or ServiceStopPending))
            return StudentVeyonServiceState.Unknown;

        var deadline = DateTime.UtcNow + StartTimeout;
        do
        {
            Thread.Sleep(250);
            if (!QueryState(service, out state)) throw LastError("无法读回 VeyonService 启动状态。");
            if (state == ServiceRunning) return StudentVeyonServiceState.Running;
            if (state == ServiceStopped && DateTime.UtcNow < deadline)
            {
                if (!StartService(service, 0, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceAlreadyRunning)
                        throw new Win32Exception(error, "VeyonService 未能保持启动状态。");
                }
            }
        } while (DateTime.UtcNow < deadline);

        return ToState(state);
    }

    private static bool QueryState(SafeServiceHandle service, out uint state)
    {
        state = 0;
        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, 0, buffer, (uint)size, out _)) return false;
            state = Marshal.PtrToStructure<ServiceStatusProcess>(buffer).CurrentState;
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static StudentVeyonServiceState ToState(uint state) => state switch
    {
        ServiceRunning => StudentVeyonServiceState.Running,
        ServiceStopped => StudentVeyonServiceState.Stopped,
        ServiceStartPending => StudentVeyonServiceState.StartPending,
        ServiceStopPending => StudentVeyonServiceState.StopPending,
        _ => StudentVeyonServiceState.Unknown
    };

    private static Win32Exception LastError(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    private sealed class SafeServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle service, int infoLevel,
        IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartService(SafeServiceHandle service, int argumentCount,
        IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
