using System.ComponentModel;

namespace VeyonCampus.Core;

/// <summary>
/// Process wrapper (P3-06): explicit file + argument list, no shell string
/// concatenation. Reads stdout/stderr concurrently to avoid deadlock,
/// enforces timeout and a bounded output capture.
/// </summary>
public sealed class ProcessRunner
{
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
        StdoutTruncated = false;
        StderrTruncated = false;
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
