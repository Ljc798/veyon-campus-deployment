using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace VeyonCampus.Core;

/// <summary>What one attempt to turn the local WebAPI on actually achieved.</summary>
public sealed record VeyonLocalWebApiOutcome(bool Succeeded, bool AlreadyReady, string Detail);

/// <summary>
/// Turns on the Veyon WebAPI the teacher screen wall reads frames from. Veyon ships with that API
/// switched off, so a classroom machine that never ran a setup script shows "21 台 · 21 不可用"
/// with no picture and no obvious cause. This class makes the switch a product action instead of a
/// manual script step:
///   1. an inbound rule that only allows 127.0.0.1 (created *before* the listener starts, so the
///      key-bearing authentication request is never exposed to the LAN, not even for a second);
///   2. WebAPI/HttpServerEnabled + WebAPI/HttpServerPort through veyon-cli, with a read-back;
///   3. a VeyonService restart, then a real TCP connect to 127.0.0.1:port as the success proof.
/// The WebAPI has no TLS and its authentication carries the private key in the request body, which
/// is why the loopback rule is a precondition rather than a follow-up.
/// </summary>
public static class VeyonLocalWebApi
{
    public const int DefaultPort = 11080;
    public const string FirewallRuleName = "VeyonCampus Teacher WebAPI (loopback only)";

    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>True when a TCP connect to 127.0.0.1:port succeeds (the WebAPI answers).</summary>
    public static bool IsListening(int port = DefaultPort)
    {
        if (port is < 1 or > 65535) return false;
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(600)) &&
                   client.Connected;
        }
        catch (Exception exception) when (exception is SocketException or AggregateException or
                                          ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True when the current process can change service configuration and firewall rules.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Ensures the WebAPI listens on loopback. Idempotent: an already-listening port returns
    /// <see cref="VeyonLocalWebApiOutcome.AlreadyReady"/>. Caller must be elevated.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static VeyonLocalWebApiOutcome EnsureEnabled(int port = DefaultPort,
        IProcessLauncher? launcher = null)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, false, "本机 WebAPI 只能在 Windows 上启用。");
        port = Math.Clamp(port, 1024, 65535);
        if (IsListening(port))
            return new(true, true, $"本机 WebAPI 已在 127.0.0.1:{port} 监听。");
        if (!IsElevated())
            return new(false, false, "启用本机 WebAPI 需要管理员权限。");

        var cliPath = WindowsVeyonAdapter.ResolveVeyonCliPath();
        if (cliPath is null)
            return new(false, false, "没有找到 veyon-cli.exe；请先在“策略与预设”里完成 Veyon 安装。");
        launcher ??= new DefaultProcessLauncher();
        var cliDirectory = Path.GetDirectoryName(cliPath)!;

        // 1. Loopback-only inbound rule first: the listener must never be reachable from the LAN.
        var firewall = EnsureLoopbackFirewallRule(port, launcher);
        if (!firewall.Ok)
            return new(false, false, "无法创建只放行本机的 Windows 防火墙规则，已停止启用 WebAPI；" +
                                     "不会把带私钥的认证请求暴露到局域网。" + Truncate(firewall.Detail));

        // 2. Veyon configuration through its own CLI, then read back what was written.
        var enabled = launcher.Run(cliPath, ["config", "set", "WebAPI/HttpServerEnabled", "true"],
            cliDirectory, CliTimeout);
        if (!enabled.Ok)
            return new(false, false, "设置 WebAPI/HttpServerEnabled 失败（退出码 " +
                                     $"{enabled.ExitCode?.ToString() ?? "无"}）。{Truncate(enabled.Stderr)}");
        var portSet = launcher.Run(cliPath, ["config", "set", "WebAPI/HttpServerPort", port.ToString()],
            cliDirectory, CliTimeout);
        if (!portSet.Ok)
            return new(false, false, "设置 WebAPI/HttpServerPort 失败（退出码 " +
                                     $"{portSet.ExitCode?.ToString() ?? "无"}）。{Truncate(portSet.Stderr)}");
        var readBack = launcher.Run(cliPath, ["config", "get", "WebAPI/HttpServerEnabled"], cliDirectory, CliTimeout);
        if (!readBack.Ok || !readBack.Stdout.Contains("true", StringComparison.OrdinalIgnoreCase))
            return new(false, false, "读回 WebAPI/HttpServerEnabled 不是 true，配置没有生效；" +
                                     $"实际读回：{Truncate(readBack.Stdout)}");

        // 3. The HTTP server is hosted by veyon-service, so the setting only takes effect after a restart.
        var restart = VeyonServiceControl.Restart(TimeSpan.FromSeconds(60));
        if (!restart.Ok)
            return new(false, false, "WebAPI 配置已写入，但重启 VeyonService 失败：" + restart.Detail);

        var deadline = DateTime.UtcNow + ListenTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsListening(port))
                return new(true, false,
                    $"已启用本机 WebAPI（127.0.0.1:{port}，仅本机可访问）。屏幕墙会自动开始取图。");
            Thread.Sleep(300);
        }

        return new(false, false, $"配置已写入并重启了 VeyonService，但 127.0.0.1:{port} 仍未监听；" +
                                 "请检查 Veyon 版本是否包含 WebAPI 插件（本产品验证版本为 4.11.2）。");
    }

    private static (bool Ok, string Detail) EnsureLoopbackFirewallRule(int port, IProcessLauncher launcher)
    {
        var netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
        // Delete-then-add keeps the rule single and fixes a previously wider rule (e.g. LocalSubnet).
        launcher.Run(netsh, ["advfirewall", "firewall", "delete", "rule", $"name={FirewallRuleName}"],
            Path.GetTempPath(), TimeSpan.FromSeconds(30));
        var add = launcher.Run(netsh,
        [
            "advfirewall", "firewall", "add", "rule", $"name={FirewallRuleName}",
            "dir=in", "action=allow", "protocol=TCP", $"localport={port}",
            "remoteip=127.0.0.1", "profile=any", "enable=yes"
        ], Path.GetTempPath(), TimeSpan.FromSeconds(30));
        return (add.Ok, add.Ok ? "" : Truncate(add.Stderr.Length > 0 ? add.Stderr : add.Stdout));
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 240 ? trimmed : trimmed[..240] + "…";
    }
}

/// <summary>Stop/start of the Veyon service through the SCM, without shelling out to sc.exe.</summary>
internal static class VeyonServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 1;
    private const uint ServiceStopPending = 3;
    private const uint ServiceRunning = 4;
    private const int ErrorServiceDoesNotExist = 1060;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartService(IntPtr service, int argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [SupportedOSPlatform("windows")]
    internal static (bool Ok, string Detail) Restart(TimeSpan timeout)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return (false, LastError("无法打开服务管理器"));
        try
        {
            var service = OpenService(manager, VeyonFacts.ServiceName,
                ServiceQueryStatus | ServiceStart | ServiceStop);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return (false, error == ErrorServiceDoesNotExist
                    ? $"未安装 {VeyonFacts.ServiceName} 服务"
                    : LastError($"无法打开 {VeyonFacts.ServiceName}"));
            }
            try
            {
                var status = default(ServiceStatus);
                if (!QueryServiceStatus(service, ref status)) return (false, LastError("无法读取服务状态"));
                if (status.CurrentState != ServiceStopped &&
                    !ControlService(service, ServiceControlStop, ref status))
                    return (false, LastError("无法停止服务"));

                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (!QueryServiceStatus(service, ref status)) return (false, LastError("无法读取服务状态"));
                    if (status.CurrentState is ServiceStopped or 0) break;
                    if (status.CurrentState != ServiceStopPending && status.CurrentState != ServiceRunning) break;
                    Thread.Sleep(250);
                }

                if (!StartService(service, 0, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1056) return (false, LastError("无法启动服务")); // 1056 = already running
                }

                while (DateTime.UtcNow < deadline)
                {
                    if (!QueryServiceStatus(service, ref status)) return (false, LastError("无法读取服务状态"));
                    if (status.CurrentState == ServiceRunning) return (true, "服务已重启。");
                    Thread.Sleep(250);
                }
                return (false, "服务未在预期时间内进入运行状态");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static string LastError(string action) =>
        action + "（Win32 错误 " + Marshal.GetLastWin32Error() + "）";

    /// <summary>Diagnostic helper: current service state without changing anything.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool? IsRunning()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return null;
        try
        {
            var service = OpenService(manager, VeyonFacts.ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) return null;
            try
            {
                var status = default(ServiceStatus);
                return QueryServiceStatus(service, ref status) ? status.CurrentState == ServiceRunning : null;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
}
