using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Reflection;
using Microsoft.Win32.SafeHandles;
using VeyonCampus.Core;
using VeyonCampus.Core.Maintenance;

namespace VeyonCampus.MaintenanceService;

internal static class Program
{
    private const string ServiceName = "VeyonCampusMaintenanceService";
    private const string ServiceDisplayName = "Veyon Campus Maintenance Service";
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceStartAccess = 0x0010;
    private const uint ServiceStopAccess = 0x0020;
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteDacAccess = 0x00040000;
    private const uint ServiceAllAccess = 0x000F01FF;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlShutdown = 0x00000005;
    private const uint ServiceControlInterrogate = 0x00000004;
    private const uint ServiceConfigFailureActions = 2;
    private const uint ScStatusProcessInfo = 0;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ErrorServiceDoesNotExist = 1060;
    private const uint ErrorServiceAlreadyRunning = 1056;
    private const uint ErrorServiceNotActive = 1062;
    private const uint ErrorServiceSpecificError = 1066;
    private const uint ErrorFailedServiceControllerConnect = 1063;
    private const int SecurityDescriptorRevision = 1;
    private const int FailureActionRestart = 1;

    private static readonly TimeSpan ServiceTransitionTimeout = TimeSpan.FromSeconds(20);
    private static readonly ServiceMainCallback ServiceMainThunk = ServiceMain;
    private static readonly ServiceControlHandlerEx ControlHandlerThunk = ControlHandler;
    private static readonly object StatusGate = new();
    private static IntPtr _statusHandle;
    private static CancellationTokenSource? _stopSource;
    private static ServiceStatus _lastStatus;
    private static uint _checkpoint;

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        if (args.Length == 1)
        {
            try
            {
                return args[0] switch
                {
                    "--query-service" => QueryServiceCommand(),
                    "--verify-running" => VerifyServiceRunningCommand(),
                    "--verify-installation" => VerifyInstallationCommand(),
                    "--version" => WriteVersionCommand(),
                    "--stop-service" => StopServiceCommand(),
                    "--start-service" => StartServiceCommand(),
                    "--install-and-start-service" => InstallAndStartServiceCommand(),
                    "--uninstall-service" => UninstallServiceCommand(),
                    _ => 2
                };
            }
            catch (Exception exception) when (exception is Win32Exception or IOException or
                                              UnauthorizedAccessException or CryptographicException or
                                              InvalidOperationException or ArgumentException)
            {
                WriteLog("Service control operation failed: " + exception.GetType().Name);
                return 1;
            }
        }

        return RunServiceDispatcher();
    }

    private static int RunServiceDispatcher()
    {
        var table = new[]
        {
            new ServiceTableEntry { ServiceName = ServiceName, ServiceProcedure = ServiceMainThunk },
            new ServiceTableEntry { ServiceName = null, ServiceProcedure = null }
        };
        if (StartServiceCtrlDispatcher(table)) return 0;
        var error = Marshal.GetLastWin32Error();
        WriteLog("Service control dispatcher could not attach; Win32=" + error);
        return error == ErrorFailedServiceControllerConnect ? 1 : 1;
    }

    private static int QueryServiceCommand() => IsServiceInstalled() ? 0 : 3;

    private static int VerifyServiceRunningCommand()
    {
        using var manager = OpenServiceControlManager(ScManagerConnect);
        using var service = TryOpenService(manager, ServiceQueryStatus, out var notInstalled);
        return notInstalled || service is null ? 3 : QueryServiceState(service) == ServiceRunning ? 0 : 4;
    }

    private static int WriteVersionCommand()
    {
        var assembly = Assembly.GetEntryAssembly();
        var version = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly?.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(version)) return 1;
        Console.Out.WriteLine(version);
        return 0;
    }

    private static int VerifyInstallationCommand()
    {
        EnsureAdministrator();
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18")
            throw new UnauthorizedAccessException("Maintenance 安装健康核验必须由 SYSTEM 执行。");
        var configPath = MaintenanceServiceConfigurationStore.ResolveAgentConfigurationPath();
        _ = MaintenanceServiceConfigurationStore.LoadOrInitializeForSystemService(configPath);
        using var maintenanceIdentity = MaintenanceServiceIdentityStore.LoadOrCreateForSystemService(configPath);
        _ = MaintenanceServiceIdentityStore.CreateSignedEnrollment(configPath);
        return 0;
    }

    private static int StopServiceCommand()
    {
        if (!IsServiceInstalled()) return 3;
        StopServiceAndWait();
        return 0;
    }

    private static int StartServiceCommand()
    {
        if (!IsServiceInstalled()) return 3;
        StartServiceAndWait();
        return 0;
    }

    private static int InstallAndStartServiceCommand()
    {
        EnsureAdministrator();
        var executablePath = Environment.ProcessPath
                             ?? throw new InvalidOperationException("无法定位 Maintenance 服务可执行文件。" );
        executablePath = Path.GetFullPath(executablePath);
        PathLinkSecurity.RejectLinks(executablePath);
        using var manager = OpenServiceControlManager(ScManagerConnect | ScManagerCreateService);
        using var service = TryOpenService(manager, ServiceAllAccess | WriteDacAccess,
            out var notInstalled) ?? (notInstalled
                ? CreateService(manager, executablePath)
                : throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开已有 Maintenance 服务。"));

        if (notInstalled)
        {
            // CreateService already set the path and LocalSystem account.
        }
        else
        {
            var configuredPath = QueryServiceBinaryPath(service);
            var configuredExecutable = ExtractExecutablePath(configuredPath);
            if (!PathsEqual(configuredExecutable, executablePath))
            {
                if (!IsLegacyStudentServiceExecutable(configuredExecutable) &&
                    !IsManagedMaintenanceServiceExecutable(configuredExecutable))
                    throw new InvalidDataException("Maintenance 服务已注册到未知路径；拒绝覆盖 SCM 配置。" );
                if (!IsManagedMaintenanceServiceExecutable(executablePath))
                    throw new InvalidDataException("新 Maintenance 服务必须位于受管 Current 或 Versions 目录；拒绝切换 SCM 路径。" );
                PathLinkSecurity.RejectLinks(configuredExecutable);
                StopServiceAndWait(service);
                var servicePath = "\"" + executablePath + "\"";
                if (!ChangeServiceConfig(service, 0xFFFFFFFF, ServiceAutoStart, ServiceErrorNormal,
                        servicePath, null, IntPtr.Zero, null, "LocalSystem", null, ServiceDisplayName))
                    throw LastError("无法切换到签名安装包指定的 Maintenance 服务版本目录。" );
            }
        }
        ApplyServiceSecurity(service);
        ApplyFailureActions(service);
        StartServiceHandle(service);
        WaitForServiceState(service, ServiceRunning);
        WriteLog("Maintenance service installed or updated and started.");
        return 0;
    }

    private static string QueryServiceBinaryPath(SafeServiceHandle service)
    {
        _ = QueryServiceConfigNative(service, IntPtr.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || error != 122)
            throw new Win32Exception(error, "无法查询 Maintenance 服务启动路径。");
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!QueryServiceConfigNative(service, buffer, required, out _))
                throw LastError("无法读取 Maintenance 服务启动路径。");
            var config = Marshal.PtrToStructure<QueryServiceConfig>(buffer);
            return Marshal.PtrToStringUni(config.BinaryPathName)
                   ?? throw new InvalidDataException("Maintenance 服务 ImagePath 为空。");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ExtractExecutablePath(string servicePath)
    {
        var trimmed = servicePath.Trim();
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote <= 1) throw new InvalidDataException("Maintenance 服务 ImagePath 引号无效。");
            return Path.GetFullPath(trimmed[1..endQuote]);
        }
        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex < 0) throw new InvalidDataException("Maintenance 服务 ImagePath 缺少可执行文件路径。");
        return Path.GetFullPath(trimmed[..(exeIndex + 4)]);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsManagedMaintenanceServiceExecutable(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return false;
        PathLinkSecurity.RejectLinks(fullPath);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var serviceRoot = Path.Combine(programFiles, "Veyon Campus", "MaintenanceService");
        PathLinkSecurity.RejectLinksAllowingMissingComponents(serviceRoot);
        var currentExecutable = Path.Combine(serviceRoot, "Current", "VeyonCampus.MaintenanceService.exe");
        if (PathsEqual(fullPath, currentExecutable)) return true;

        var versionsRoot = Path.Combine(serviceRoot, "Versions");
        var versionDirectory = Path.GetDirectoryName(fullPath);
        if (versionDirectory is null || !string.Equals(Path.GetDirectoryName(versionDirectory), versionsRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(fullPath), "VeyonCampus.MaintenanceService.exe",
                StringComparison.OrdinalIgnoreCase))
            return false;
        var version = Path.GetFileName(versionDirectory);
        try { _ = ApplicationReleaseClient.CompareVersions(version, version); }
        catch (InvalidDataException) { return false; }
        return true;
    }

    private static bool IsLegacyStudentServiceExecutable(string path)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var studentServiceRoot = Path.Combine(programFiles, "Veyon Campus", "Student", "MaintenanceService");
        return PathsEqual(path, Path.Combine(studentServiceRoot, "VeyonCampus.MaintenanceService.exe")) ||
               PathsEqual(path, Path.Combine(studentServiceRoot, "Current", "VeyonCampus.MaintenanceService.exe"));
    }

    private static int UninstallServiceCommand()
    {
        EnsureAdministrator();
        if (!IsServiceInstalled()) return 0;
        StopServiceAndWait();
        using var manager = OpenServiceControlManager(ScManagerConnect);
        using var service = TryOpenService(manager, DeleteAccess | ServiceQueryStatus, out var notInstalled);
        if (notInstalled || service is null) return 0;
        if (!DeleteService(service))
            throw LastError("无法删除 Maintenance Windows 服务注册。" );
        var deadline = DateTime.UtcNow + ServiceTransitionTimeout;
        while (DateTime.UtcNow < deadline)
        {
            using var check = TryOpenService(manager, ServiceQueryStatus, out var removed);
            if (removed) return 0;
            Thread.Sleep(250);
        }
        throw new TimeoutException("Maintenance 服务仍在 SCM 中登记；不能继续移除服务文件。" );
    }

    private static void ServiceMain(int argumentCount, IntPtr arguments)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, ControlHandlerThunk, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            WriteLog("RegisterServiceCtrlHandlerEx failed; Win32=" + Marshal.GetLastWin32Error());
            return;
        }

        using var stopSource = new CancellationTokenSource();
        _stopSource = stopSource;
        SetServiceStatus(ServiceStartPending, 30000, 0);
        WriteLog("Maintenance service host entered SERVICE_START_PENDING.");
        try
        {
            SetServiceStatus(ServiceRunning, 0, 0);
            WriteLog("Maintenance service host entered SERVICE_RUNNING.");
            new MaintenanceServiceWorker(log: WriteLog).RunAsync(stopSource.Token).GetAwaiter().GetResult();
            SetServiceStatus(ServiceStopped, 0, 0);
            WriteLog("Maintenance service stopped cleanly.");
        }
        catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
        {
            SetServiceStatus(ServiceStopped, 0, 0);
            WriteLog("Maintenance service stopped by SCM control request.");
        }
        catch (Exception exception)
        {
            WriteLog("Maintenance service worker failed: " + exception.GetType().Name);
            SetServiceStatus(ServiceStopped, ErrorServiceSpecificError, 1);
        }
        finally
        {
            _stopSource = null;
            _statusHandle = IntPtr.Zero;
        }
    }

    private static uint ControlHandler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        _ = eventType;
        _ = eventData;
        _ = context;
        if (control is ServiceControlStop or ServiceControlShutdown)
        {
            SetServiceStatus(ServiceStopPending, 20000, 0);
            try { _stopSource?.Cancel(); }
            catch (ObjectDisposedException) { }
            return 0;
        }
        if (control == ServiceControlInterrogate)
        {
            lock (StatusGate)
            {
                if (_statusHandle != IntPtr.Zero) _ = SetServiceStatusNative(_statusHandle, _lastStatus);
            }
            return 0;
        }
        return 120;
    }

    private static void SetServiceStatus(uint state, uint win32ExitCode, uint serviceExitCode)
    {
        lock (StatusGate)
        {
            if (_statusHandle == IntPtr.Zero) return;
            _checkpoint = state is ServiceStartPending or ServiceStopPending ? _checkpoint + 1 : 0;
            _lastStatus = new ServiceStatus
            {
                ServiceType = ServiceWin32OwnProcess,
                CurrentState = state,
                ControlsAccepted = state == ServiceRunning
                    ? ServiceAcceptStop | ServiceAcceptShutdown
                    : 0,
                Win32ExitCode = win32ExitCode,
                ServiceSpecificExitCode = serviceExitCode,
                CheckPoint = _checkpoint,
                WaitHint = state is ServiceStartPending or ServiceStopPending ? 20000 : 0
            };
            if (!SetServiceStatusNative(_statusHandle, _lastStatus))
                WriteLog("SetServiceStatus failed; Win32=" + Marshal.GetLastWin32Error());
        }
    }

    private static bool IsServiceInstalled()
    {
        using var manager = OpenServiceControlManager(ScManagerConnect);
        using var service = TryOpenService(manager, ServiceQueryStatus, out var notInstalled);
        if (notInstalled) return false;
        return service is not null;
    }

    private static void StopServiceAndWait()
    {
        using var manager = OpenServiceControlManager(ScManagerConnect);
        using var service = TryOpenService(manager, ServiceStopAccess | ServiceQueryStatus, out var notInstalled);
        if (notInstalled || service is null) return;
        StopServiceAndWait(service);
    }

    private static void StopServiceAndWait(SafeServiceHandle service)
    {
        var state = QueryServiceState(service);
        if (state == ServiceStopped) return;
        if (state != ServiceStopPending)
        {
            if (!ControlService(service, ServiceControlStop, out _))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceNotActive)
                    throw new Win32Exception(error, "无法请求停止 Maintenance 服务。" );
            }
        }
        WaitForServiceState(service, ServiceStopped);
    }

    private static void StartServiceAndWait()
    {
        using var manager = OpenServiceControlManager(ScManagerConnect);
        using var service = TryOpenService(manager, ServiceStartAccess | ServiceQueryStatus,
            out var notInstalled);
        if (notInstalled || service is null) return;
        StartServiceHandle(service);
        WaitForServiceState(service, ServiceRunning);
    }

    private static void StartServiceHandle(SafeServiceHandle service)
    {
        if (QueryServiceState(service) == ServiceRunning) return;
        if (!StartServiceNative(service, 0, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceAlreadyRunning)
                throw new Win32Exception(error, "无法启动 Maintenance 服务。" );
        }
    }

    private static void WaitForServiceState(SafeServiceHandle service, uint expectedState)
    {
        var deadline = DateTime.UtcNow + ServiceTransitionTimeout;
        do
        {
            var state = QueryServiceState(service);
            if (state == expectedState) return;
            Thread.Sleep(250);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("Maintenance 服务未在时限内达到 SCM 目标状态。" );
    }

    private static uint QueryServiceState(SafeServiceHandle service)
    {
        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, (uint)size, out _))
                throw LastError("无法读取 Maintenance 服务状态。" );
            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer).CurrentState;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static SafeServiceHandle OpenServiceControlManager(uint desiredAccess)
    {
        var handle = OpenSCManager(null, null, desiredAccess);
        if (handle.IsInvalid) throw LastError("无法打开 Windows 服务控制管理器。" );
        return handle;
    }

    private static SafeServiceHandle? TryOpenService(SafeServiceHandle manager, uint desiredAccess,
        out bool notInstalled)
    {
        var service = OpenService(manager, ServiceName, desiredAccess);
        notInstalled = false;
        if (!service.IsInvalid) return service;
        var error = Marshal.GetLastWin32Error();
        service.Dispose();
        if (error == ErrorServiceDoesNotExist)
        {
            notInstalled = true;
            return null;
        }
        throw new Win32Exception(error, "无法打开 Maintenance 服务注册。" );
    }

    private static SafeServiceHandle CreateService(SafeServiceHandle manager, string executablePath)
    {
        var servicePath = "\"" + executablePath + "\"";
        var service = CreateServiceNative(manager, ServiceName, ServiceDisplayName, ServiceAllAccess | WriteDacAccess,
            ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal, servicePath, null, IntPtr.Zero,
            null, "LocalSystem", null);
        if (!service.IsInvalid) return service;
        var error = Marshal.GetLastWin32Error();
        service.Dispose();
        throw new Win32Exception(error, "无法创建 Maintenance Windows 服务。" );
    }

    private static void ApplyServiceSecurity(SafeServiceHandle service)
    {
        const string serviceDacl = "D:(A;;GA;;;SY)(A;;GA;;;BA)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(serviceDacl, SecurityDescriptorRevision,
                out var descriptor, out _))
            throw LastError("无法创建 Maintenance 服务安全描述符。" );
        try
        {
            if (!SetServiceObjectSecurity(service, DaclSecurityInformation, descriptor))
                throw LastError("无法限制 Maintenance 服务控制权限。" );
        }
        finally { _ = LocalFree(descriptor); }
    }

    private static void ApplyFailureActions(SafeServiceHandle service)
    {
        var actionSize = Marshal.SizeOf<ServiceFailureAction>();
        var actionBuffer = Marshal.AllocHGlobal(actionSize * 3);
        try
        {
            Marshal.StructureToPtr(new ServiceFailureAction(FailureActionRestart, 5000), actionBuffer, false);
            Marshal.StructureToPtr(new ServiceFailureAction(FailureActionRestart, 15000),
                IntPtr.Add(actionBuffer, actionSize), false);
            Marshal.StructureToPtr(new ServiceFailureAction(FailureActionRestart, 30000),
                IntPtr.Add(actionBuffer, actionSize * 2), false);
            var actions = new ServiceFailureActions(86400, IntPtr.Zero, IntPtr.Zero, 3, actionBuffer);
            if (!ChangeServiceConfig2(service, ServiceConfigFailureActions, ref actions))
                throw LastError("无法配置 Maintenance 服务自动恢复策略。" );
        }
        finally { Marshal.FreeHGlobal(actionBuffer); }
    }

    private static void EnsureAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Maintenance 服务安装操作需要管理员权限。" );
    }

    private static Win32Exception LastError(string message) => new(Marshal.GetLastWin32Error(), message);

    private static void WriteLog(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VeyonCampus", "MaintenanceServiceHost");
            Directory.CreateDirectory(directory);
            PathLinkSecurity.RejectLinks(directory);
            AgentFileSecurity.SecurePrivateDirectory(directory);
            var path = Path.Combine(directory, "service.log");
            if (File.Exists(path)) PathLinkSecurity.RejectLinks(path);
            else
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                AgentFileSecurity.SecurePrivateFile(path);
            }
            var info = new FileInfo(path);
            if (info.Length > 512 * 1024)
            {
                using var truncate = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                AgentFileSecurity.SecurePrivateFile(path);
            }
            var cleanMessage = new string(message.Where(character => !char.IsControl(character)).Take(512).ToArray());
            var line = Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O") + " " + cleanMessage + "\r\n");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096,
                FileOptions.WriteThrough);
            stream.Write(line);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or ArgumentException)
        {
            // Logging must never prevent service startup, shutdown, or recovery.
        }
    }

    private static bool SetServiceStatusNative(IntPtr statusHandle, ServiceStatus status) =>
        SetServiceStatusApi(statusHandle, ref status);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainCallback(int argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ServiceControlHandlerEx(uint control, uint eventType, IntPtr eventData,
        IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? ServiceName;
        [MarshalAs(UnmanagedType.FunctionPtr)] public ServiceMainCallback? ServiceProcedure;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QueryServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureAction(uint type, uint delay)
    {
        public uint Type = type;
        public uint Delay = delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions(uint resetPeriod, IntPtr rebootMessage, IntPtr command,
        uint actionCount, IntPtr actions)
    {
        public uint ResetPeriod = resetPeriod;
        public IntPtr RebootMessage = rebootMessage;
        public IntPtr Command = command;
        public uint ActionCount = actionCount;
        public IntPtr Actions = actions;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle CreateServiceNative(SafeServiceHandle manager,
        string serviceName, string displayName, uint desiredAccess, uint serviceType, uint startType,
        uint errorControl, string binaryPathName, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string serviceStartName, string? password);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(SafeServiceHandle service, uint serviceType,
        uint startType, uint errorControl, string binaryPathName, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string serviceStartName, string? password, string displayName);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigNative(SafeServiceHandle service,
        IntPtr serviceConfig, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceNative(SafeServiceHandle service, uint argumentCount,
        IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(SafeServiceHandle service, uint control,
        out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle service, uint infoLevel,
        IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(SafeServiceHandle service, uint infoLevel,
        ref ServiceFailureActions config);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(SafeServiceHandle service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceObjectSecurity(SafeServiceHandle service, uint securityInformation,
        IntPtr securityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string stringSecurityDescriptor,
        int stringSdRevision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName,
        ServiceControlHandlerEx handler, IntPtr context);

    [DllImport("advapi32.dll", EntryPoint = "SetServiceStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatusApi(IntPtr serviceStatusHandle, ref ServiceStatus status);
}
