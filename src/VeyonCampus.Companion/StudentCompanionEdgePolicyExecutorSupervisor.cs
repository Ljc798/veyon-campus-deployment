using System.Diagnostics;
using System.Text;

namespace VeyonCampus.Companion;

/// <summary>Ensures the Edge policy executor stays available in this logged-in user's session.</summary>
internal sealed class StudentCompanionEdgePolicyExecutorSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(2);
    private const long MaximumLogBytes = 256 * 1024;

    private readonly string _executorPath;
    private readonly string _executorDirectory;
    private readonly string _logPath;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private TimeSpan _retryDelay = InitialRetryDelay;
    private DateTimeOffset _retryAfterUtc = DateTimeOffset.MinValue;

    public StudentCompanionEdgePolicyExecutorSupervisor()
    {
        _executorDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "EdgePolicyExecutor"));
        _executorPath = Path.Combine(_executorDirectory, "VeyonCampus.EdgePolicyExecutor.exe");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Path.GetTempPath();
        _logPath = Path.Combine(localAppData, "VeyonCampus", "EdgePolicyExecutor",
            "companion-supervisor.log");
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
        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (IsExecutorRunningInCurrentSession())
                {
                    _retryDelay = InitialRetryDelay;
                    _retryAfterUtc = DateTimeOffset.MinValue;
                }
                else if (DateTimeOffset.UtcNow >= _retryAfterUtc)
                {
                    if (await TryStartExecutorAsync(cancellationToken).ConfigureAwait(false))
                    {
                        _retryDelay = InitialRetryDelay;
                        _retryAfterUtc = DateTimeOffset.MinValue;
                    }
                    else
                    {
                        ScheduleRetry();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AppendLog("SupervisorError", $"{exception.GetType().Name}: {exception.Message}");
                ScheduleRetry();
            }

            try { await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private static bool IsExecutorRunningInCurrentSession()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        foreach (var process in Process.GetProcessesByName("VeyonCampus.EdgePolicyExecutor"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.SessionId == currentSessionId) return true;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return false;
    }

    private async Task<bool> TryStartExecutorAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_executorPath))
                throw new FileNotFoundException("安装目录中找不到 Edge 策略执行器。", _executorPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = _executorPath,
                WorkingDirectory = _executorDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                AppendLog("StartFailed", "Windows 未创建 Edge 策略执行器进程。");
                return false;
            }

            await Task.Delay(StartProbeInterval, cancellationToken).ConfigureAwait(false);
            process.Refresh();
            if (process.HasExited)
            {
                AppendLog("StartExited", $"启动后退出，ExitCode={process.ExitCode}。");
                return false;
            }

            AppendLog("Started", $"PID={process.Id}，SessionId={process.SessionId}。");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                          InvalidOperationException or UnauthorizedAccessException or
                                          IOException or ArgumentException or NotSupportedException or
                                          System.Security.SecurityException)
        {
            AppendLog("StartFailed", $"{exception.GetType().Name}: {exception.Message}");
            return false;
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
