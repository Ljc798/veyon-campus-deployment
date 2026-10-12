using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using VeyonCampus.Core;

namespace VeyonCampus.App;

internal static class ClassroomVeyonRemoteViewLauncher
{
    private static readonly TimeSpan StartupProbeDelay = TimeSpan.FromMilliseconds(500);

    [SupportedOSPlatform("windows")]
    public static async Task OpenAsync(string target, CancellationToken cancellationToken)
    {
        using var process = await StartProcessAsync(target, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    public static async Task<Process> StartProcessAsync(string target, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Veyon 原生监看窗口只支持 Windows 教师端。");

        var normalizedTarget = VeyonHostAddress.NormalizeOverride(target);
        var cliPath = ClassroomScreenPreviewCapture.VeyonCliScreenshotCapture.ResolveCliPath(
            preferNonConsole: true);
        if (cliPath is null)
            throw new InvalidOperationException("找不到 Veyon 命令行程序；请先在教师电脑安装 Veyon。");

        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            WorkingDirectory = Path.GetDirectoryName(cliPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("remoteaccess");
        startInfo.ArgumentList.Add("view");
        startInfo.ArgumentList.Add(normalizedTarget);

        Process process;
        try
        {
            process = Process.Start(startInfo) ??
                throw new InvalidOperationException("无法启动 Veyon 原生监看窗口。");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("教师电脑无法启动 Veyon；请检查 Veyon 安装和权限。", exception);
        }

        try
        {
            await Task.Delay(StartupProbeDelay, cancellationToken).ConfigureAwait(false);
            if (process.HasExited && process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Veyon 未能打开 {normalizedTarget} 的监看窗口（退出代码 {process.ExitCode}）。");
            return process;
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            process.Dispose();
            throw;
        }
    }
}
