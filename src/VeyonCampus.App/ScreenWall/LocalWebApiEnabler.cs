using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using VeyonCampus.Core;

namespace VeyonCampus.App;

/// <summary>
/// Gets the local Veyon WebAPI running with one click. If this process already has administrator
/// rights the work happens in-process; otherwise the same executable is relaunched through UAC with
/// <c>--enable-local-webapi</c>, so the teacher never has to find and run a PowerShell script.
/// </summary>
internal static class LocalWebApiEnabler
{
    private static readonly TimeSpan ElevatedTimeout = TimeSpan.FromSeconds(120);

    internal static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static async Task<VeyonLocalWebApiOutcome> EnsureAsync(int port = VeyonLocalWebApi.DefaultPort)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, false, "本机 WebAPI 只能在 Windows 上启用。");
        if (VeyonLocalWebApi.IsListening(port))
            return new(true, true, $"本机 WebAPI 已在 127.0.0.1:{port} 监听。");
        if (IsElevated)
            return await Task.Run(() => VeyonLocalWebApi.EnsureEnabled(port)).ConfigureAwait(true);
        return await RunElevatedAsync(port).ConfigureAwait(true);
    }

    /// <summary>
    /// Relaunches this executable elevated and waits for it. The child only runs the enable branch
    /// (it returns before creating the single-instance mutex), so it cannot fight this window.
    /// </summary>
    private static async Task<VeyonLocalWebApiOutcome> RunElevatedAsync(int port)
    {
        var executable = Environment.ProcessPath ??
                         Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            return new(false, false, "无法定位教师端程序；请以管理员身份重新运行教师控制台后再试。");

        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, $"--enable-local-webapi {port}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
            });
            if (process is null)
                return new(false, false, "无法启动管理员授权流程；请以管理员身份重新运行教师控制台后再试。");

            using var timeout = new CancellationTokenSource(ElevatedTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    // The elevated helper already exited or cannot be signalled; the timeout message stands.
                }
                return new(false, false, "启用本机 WebAPI 超时（120 秒）；请稍后重试，或手动以管理员身份运行一次教师控制台。");
            }

            if (VeyonLocalWebApi.IsListening(port))
                return new(true, false, $"已启用本机 WebAPI（127.0.0.1:{port}，仅本机可访问）。屏幕墙会自动开始取图。");
            return new(false, false, process.ExitCode == 0
                ? $"管理员流程已完成，但 127.0.0.1:{port} 仍未监听；请重启 Veyon 服务后再试。"
                : $"启用本机 WebAPI 未成功（退出码 {process.ExitCode}）；请查看设备诊断中的 Veyon 状态。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new(false, false, "已取消管理员授权，本机 WebAPI 仍未启用；屏幕墙需要它才能取图。");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                          System.IO.IOException or UnauthorizedAccessException)
        {
            return new(false, false, "无法启动管理员授权流程：" + exception.Message);
        }
    }
}
