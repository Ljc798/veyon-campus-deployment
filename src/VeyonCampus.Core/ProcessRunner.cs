using System.ComponentModel;

namespace VeyonCampus.Core;

/// <summary>
/// Process wrapper (P3-06): explicit file + argument list, no shell string
/// concatenation. Reads stdout/stderr concurrently to avoid deadlock,
/// enforces timeout and a bounded output capture.
/// </summary>
public sealed class ProcessRunner
{
    private static readonly SemaphoreSlim VeyonCliGate = new(1, 1);

    public int? ExitCode { get; private set; }
    public string Stdout { get; private set; } = "";
    public string Stderr { get; private set; } = "";
    public bool StdoutTruncated { get; private set; }
    public bool StderrTruncated { get; private set; }
    public bool TimedOut { get; private set; }

    /// <summary>Runs the process and waits for exit. Throws on timeout or launch failure.</summary>
    public void Run(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, int outputLimitChars = 8192)
        => RunCore(fileName, arguments, workingDirectory, timeout, null, outputLimitChars);

    /// <summary>Runs a process with sensitive input on redirected standard input.</summary>
    public void RunWithStandardInput(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, string standardInput, int outputLimitChars = 8192)
        => RunCore(fileName, arguments, workingDirectory, timeout, standardInput, outputLimitChars);

    private void RunCore(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, string? standardInput, int outputLimitChars)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (outputLimitChars < 0)
            throw new ArgumentOutOfRangeException(nameof(outputLimitChars));
        using var veyonCliLease = AcquireVeyonCliAccess(fileName, timeout);

        ExitCode = null;
        Stdout = "";
        Stderr = "";
        StdoutTruncated = false;
        StderrTruncated = false;
        TimedOut = false;

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true
        };
        if (standardInput is not null)
            startInfo.StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (stdout.Length + e.Data.Length + 1 <= outputLimitChars)
                stdout.Append(e.Data).Append('\n');
            else
                StdoutTruncated = true;
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (stderr.Length + e.Data.Length + 1 <= outputLimitChars)
                stderr.Append(e.Data).Append('\n');
            else
                StderrTruncated = true;
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"无法启动进程：{fileName}");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 740)
        {
            throw new UnauthorizedAccessException("此操作需要管理员权限。请在 Windows 权限提示中选择“是”，或以管理员身份重新打开应用后重试。", exception);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }
        if (!process.WaitForExit(timeout))
        {
            TimedOut = true;
            try { process.Kill(entireProcessTree: true); } catch (System.Exception) { }
            throw new TimeoutException($"进程在 {timeout} 内未完成：{Path.GetFileName(fileName)}");
        }
        process.WaitForExit(); // flush async output
        ExitCode = process.ExitCode;
        Stdout = stdout.ToString().Trim();
        Stderr = stderr.ToString().Trim();
    }

    private static IDisposable? AcquireVeyonCliAccess(string fileName, TimeSpan timeout)
    {
        var executableName = Path.GetFileNameWithoutExtension(fileName);
        if (!executableName.Equals("veyon-cli", StringComparison.OrdinalIgnoreCase) &&
            !executableName.Equals("veyon-wcli", StringComparison.OrdinalIgnoreCase))
            return null;

        if (IsVeyonProcessRunning("veyon-configurator"))
            throw new InvalidOperationException("Veyon Configurator 正在运行；为避免配置冲突，已暂停 Veyon 命令。请关闭设置窗口后重试。");

        if (!VeyonCliGate.Wait(timeout))
            throw new TimeoutException("等待其他 Veyon 配置操作完成超时；请稍候后重试。");
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (IsVeyonProcessRunning("veyon-configurator") ||
                   IsVeyonProcessRunning("veyon-cli") || IsVeyonProcessRunning("veyon-wcli"))
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Veyon Configurator 或另一项 Veyon 命令仍在访问配置；请等它结束后重试。");
                Thread.Sleep(100);
            }
            return new VeyonCliLease(VeyonCliGate);
        }
        catch
        {
            VeyonCliGate.Release();
            throw;
        }
    }

    /// <summary>Opens Veyon Configurator exclusively with this process's Veyon CLI work.</summary>
    public static void LaunchVeyonConfigurator(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!VeyonCliGate.Wait(0))
            throw new InvalidOperationException("另一个 Veyon 命令正在访问配置；请等它完成后再打开 Veyon Configurator。");
        try
        {
            if (IsVeyonProcessRunning("veyon-configurator") ||
                IsVeyonProcessRunning("veyon-cli") || IsVeyonProcessRunning("veyon-wcli"))
                throw new InvalidOperationException("检测到 Veyon Configurator 或命令行正在运行；请切换到现有窗口，或等当前 Veyon 操作结束后重试。");

            var fullPath = Path.GetFullPath(fileName);
            var workingDirectory = Path.GetDirectoryName(fullPath)
                                   ?? throw new InvalidOperationException("无法确定 Veyon Configurator 的工作目录。");
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath)
            {
                UseShellExecute = true,
                WorkingDirectory = workingDirectory
            }) ?? throw new InvalidOperationException("Windows 没有启动 Veyon Configurator。");

            _ = Task.Run(() =>
            {
                try { process.WaitForExit(); }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                finally
                {
                    process.Dispose();
                    VeyonCliGate.Release();
                }
            });
        }
        catch
        {
            VeyonCliGate.Release();
            throw;
        }
    }

    private static bool IsVeyonProcessRunning(string processName)
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(processName);
        try { return processes.Length > 0; }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private sealed class VeyonCliLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    /// <summary>Launches a detached GUI process without waiting; returns the process for status polling.</summary>
    public System.Diagnostics.Process LaunchDetached(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = false
        };
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);
        try
        {
            return System.Diagnostics.Process.Start(startInfo)
                   ?? throw new InvalidOperationException($"无法启动进程：{fileName}");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 740)
        {
            throw new UnauthorizedAccessException("此操作需要管理员权限。请在 Windows 权限提示中选择“是”，或以管理员身份重新打开应用后重试。", exception);
        }
    }
}
