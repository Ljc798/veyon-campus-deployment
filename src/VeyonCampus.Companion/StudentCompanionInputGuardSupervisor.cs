using System.Diagnostics;
using System.Text;

namespace VeyonCampus.Companion;

/// <summary>Runs an idle, unlocked Guard in the current interactive session and restarts it unlocked.</summary>
internal sealed class StudentCompanionInputGuardSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(1);
    private const long MaximumLogBytes = 128 * 1024;

    private readonly string _guardPath;
    private readonly string _guardDirectory;
    private readonly string _logPath;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private TimeSpan _retryDelay = InitialRetryDelay;
    private DateTimeOffset _retryAfterUtc = DateTimeOffset.MinValue;

    public StudentCompanionInputGuardSupervisor()
    {
        _guardDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "InputGuard"));
        _guardPath = Path.Combine(_guardDirectory, "VeyonCampus.StudentInputGuard.exe");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Path.GetTempPath();
        _logPath = Path.Combine(localAppData, "VeyonCampus", "InputGuard", "companion-supervisor.log");
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _loop is not null) return;
        _loop = RunAsync(_shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }
        StopGuardInCurrentSession();
        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (IsGuardRunningInCurrentSession())
                {
                    _retryDelay = InitialRetryDelay;
                    _retryAfterUtc = DateTimeOffset.MinValue;
                }
                else if (DateTimeOffset.UtcNow >= _retryAfterUtc)
                {
                    if (await TryStartGuardAsync(cancellationToken).ConfigureAwait(false))
                    {
                        _retryDelay = InitialRetryDelay;
                        _retryAfterUtc = DateTimeOffset.MinValue;
                    }
                    else ScheduleRetry();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                AppendLog("SupervisorError", $"{exception.GetType().Name}: {exception.Message}");
                ScheduleRetry();
            }
            try { await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private bool IsGuardRunningInCurrentSession()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("VeyonCampus.StudentInputGuard"))
        {
            using (process)
            {
                try
                {
                    var path = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
                    if (!process.HasExited && process.SessionId == current.SessionId &&
                        string.Equals(path, _guardPath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return false;
    }

    private async Task<bool> TryStartGuardAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_guardPath))
                throw new FileNotFoundException("学生安装目录缺少 InputGuard 程序。", _guardPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = _guardPath,
                WorkingDirectory = _guardDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--guard-server");
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            process.Refresh();
            if (process.HasExited)
            {
                AppendLog("StartExited", $"Guard 启动后退出，ExitCode={process.ExitCode}。");
                return false;
            }
            AppendLog("Started", $"PID={process.Id}，SessionId={process.SessionId}。");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                          InvalidOperationException or UnauthorizedAccessException or
                                          IOException or ArgumentException or NotSupportedException or
                                          System.Security.SecurityException)
        {
            AppendLog("StartFailed", $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private void StopGuardInCurrentSession()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("VeyonCampus.StudentInputGuard"))
        {
            using (process)
            {
                try
                {
                    var path = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
                    if (process.SessionId != current.SessionId ||
                        !string.Equals(path, _guardPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    AppendLog("StopFailed", $"{exception.GetType().Name}: {exception.Message}");
                }
            }
        }
    }

    private void ScheduleRetry()
    {
        _retryAfterUtc = DateTimeOffset.UtcNow + _retryDelay;
        _retryDelay = TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaximumRetryDelay.Ticks));
    }

    private void AppendLog(string eventName, string detail)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logPath);
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length >= MaximumLogBytes)
                File.WriteAllText(_logPath, string.Empty, Encoding.UTF8);
            File.AppendAllText(_logPath,
                $"{DateTimeOffset.UtcNow:O}\t{eventName}\t{detail}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
