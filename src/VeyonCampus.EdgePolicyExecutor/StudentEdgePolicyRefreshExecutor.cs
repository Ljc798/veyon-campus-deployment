using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using VeyonCampus.Core;

namespace VeyonCampus.EdgePolicyExecutor;

[SupportedOSPlatform("windows")]
internal sealed class StudentEdgePolicyRefreshExecutor
{
    private static readonly Uri RequestUri = new("http://127.0.0.1:39174" +
        WebsitePolicyAgent.EdgePolicyRefreshRequestLocalPath);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AttemptBackoff = TimeSpan.FromSeconds(15);
    private const int MaximumAttempts = 3;
    private const int MaximumJournalBytes = 32 * 1024;
    private const long MaximumLogBytes = 256 * 1024;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    private readonly HttpClient _client;
    private readonly string _statePath;
    private readonly string _logPath;
    private Journal _journal;
    private DateTimeOffset _lastAgentErrorLogUtc = DateTimeOffset.MinValue;

    public StudentEdgePolicyRefreshExecutor()
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyonCampus", "EdgePolicyExecutor");
        Directory.CreateDirectory(directory);
        _statePath = Path.Combine(directory, "state.json");
        _logPath = Path.Combine(directory, "edge-refresh.log");
        _journal = LoadJournal();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var signal = await ReadSignalAsync(cancellationToken).ConfigureAwait(false);
                    if (signal.RequestId != Guid.Empty)
                    {
                        Validate(signal, DateTimeOffset.UtcNow);
                        await ProcessSignalAsync(signal, cancellationToken).ConfigureAwait(false);
                    }
                    else if (_journal.RequestId != Guid.Empty)
                    {
                        WriteState(Journal.Empty, "QueueCleared");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                                  InvalidDataException or IOException or JsonException or
                                                  CryptographicException or Win32Exception or InvalidOperationException or
                                                  UnauthorizedAccessException or PlatformNotSupportedException or
                                                  ArgumentException or NotSupportedException or System.Security.SecurityException)
                {
                    var nowUtc = DateTimeOffset.UtcNow;
                    if (nowUtc - _lastAgentErrorLogUtc >= TimeSpan.FromSeconds(30))
                    {
                        _lastAgentErrorLogUtc = nowUtc;
                        AppendLog("AgentUnavailableOrProtocolError", DiagnosticCode(exception));
                    }
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _client.Dispose();
        }
    }

    private async Task<StudentAgentEdgePolicyRefreshSignal> ReadSignalAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(RequestUri, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new StudentAgentEdgePolicyRefreshSignal(1, Guid.Empty, null, 0);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Student Agent Edge 刷新队列暂不可用。", null, response.StatusCode);
        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var envelope = Encoding.UTF8.GetString(bytes);
        return StudentAgentResponseCryptography.Verify<StudentAgentEdgePolicyRefreshSignal>(envelope).Payload;
    }

    private async Task ProcessSignalAsync(StudentAgentEdgePolicyRefreshSignal signal,
        CancellationToken cancellationToken)
    {
        var selection = FindOpenEdge();
        var target = selection.Target;
        if (target is null)
        {
            if (_journal.RequestId != signal.RequestId || _journal.Status != selection.Status ||
                _journal.ErrorCode != selection.ErrorCode)
                WriteState(new Journal(1, signal.RequestId, signal.Revision, selection.Status,
                    null, 0, DateTimeOffset.UtcNow, selection.ErrorCode), selection.Status.ToString());
            return;
        }

        if (!EdgePolicyRefreshCompatibility.IsSupportedVersion(target.Version))
        {
            if (_journal.RequestId != signal.RequestId || _journal.Status != EdgePolicyRefreshStatus.UnsupportedBrowserVersion ||
                !string.Equals(_journal.BrowserVersion, target.Version, StringComparison.Ordinal))
                WriteState(new Journal(1, signal.RequestId, signal.Revision,
                    EdgePolicyRefreshStatus.UnsupportedBrowserVersion, target.Version, 0,
                    DateTimeOffset.UtcNow, "EDGE_VERSION_NOT_VALIDATED"), "UnsupportedBrowserVersion",
                    target.IntegrityLevelRid);
            return;
        }

        if (_journal.RequestId == signal.RequestId &&
            _journal.Status == EdgePolicyRefreshStatus.DispatchedUnverified &&
            string.Equals(_journal.BrowserVersion, target.Version, StringComparison.Ordinal))
            return;

        var sameAttempt = _journal.RequestId == signal.RequestId &&
                          string.Equals(_journal.BrowserVersion, target.Version, StringComparison.Ordinal);
        var attempts = sameAttempt ? _journal.AttemptCount : 0;
        if (!EdgePolicyRefreshDispatchRules.ShouldRetry(attempts, MaximumAttempts))
        {
            if (_journal.Status != EdgePolicyRefreshStatus.NeedsReview)
                WriteState(_journal with { Status = EdgePolicyRefreshStatus.NeedsReview,
                    ErrorCode = "EDGE_REFRESH_RETRY_LIMIT", UpdatedUtc = DateTimeOffset.UtcNow }, "NeedsReview");
            return;
        }
        if (sameAttempt && _journal.Status == EdgePolicyRefreshStatus.RetryableFailure &&
            DateTimeOffset.UtcNow - _journal.UpdatedUtc < AttemptBackoff)
            return;

        attempts++;
        WriteState(new Journal(1, signal.RequestId, signal.Revision, EdgePolicyRefreshStatus.Dispatching,
            target.Version, attempts, DateTimeOffset.UtcNow, null), "Dispatching", target.IntegrityLevelRid);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = target.ExecutablePath,
                // Start the trusted executable directly in this logged-in user's token.
                // This matches `& msedge.exe --refresh-platform-policy` in ordinary PowerShell
                // and avoids ShellExecute's different argument and elevation behavior.
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--refresh-platform-policy");
            using var process = Process.Start(startInfo);
            if (process is null) throw new InvalidOperationException("Windows 未创建 Edge 刷新请求进程。");
            WriteState(new Journal(1, signal.RequestId, signal.Revision,
                EdgePolicyRefreshStatus.DispatchedUnverified, target.Version, attempts,
                DateTimeOffset.UtcNow, null), "DispatchedUnverified", target.IntegrityLevelRid);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                          UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            WriteState(new Journal(1, signal.RequestId, signal.Revision,
                attempts >= MaximumAttempts ? EdgePolicyRefreshStatus.NeedsReview : EdgePolicyRefreshStatus.RetryableFailure,
                target.Version, attempts, DateTimeOffset.UtcNow, DiagnosticCode(exception)),
                attempts >= MaximumAttempts ? "NeedsReview" : "RetryableFailure", target.IntegrityLevelRid);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static EdgeTargetSelection FindOpenEdge()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var sessionId = currentProcess.SessionId;
        var userSid = WindowsIdentity.GetCurrent().User?.Value;
        if (sessionId <= 0 || string.IsNullOrWhiteSpace(userSid))
            return EdgeTargetSelection.Deferred(EdgePolicyRefreshStatus.PendingSession, "EXECUTOR_SESSION_UNKNOWN");

        var executorIntegrityLevel = ReadProcessIntegrityLevel(currentProcess);
        var edgeProcesses = Process.GetProcessesByName("msedge");
        if (edgeProcesses.Length == 0)
            return EdgeTargetSelection.Deferred(EdgePolicyRefreshStatus.BrowserNotRunning, null);

        var visibleProcessIds = new HashSet<int>();
        _ = EnumWindows((window, parameter) =>
        {
            if (!IsWindowVisible(window)) return true;
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId <= int.MaxValue) visibleProcessIds.Add((int)processId);
            return true;
        }, IntPtr.Zero);

        var candidates = new List<EdgeTarget>();
        var matchingSessionProcess = false;
        var matchingUserProcess = false;
        var foreignSessionProcess = false;
        var foreignUserProcess = false;
        var pathNotValidated = false;
        var identityUnavailable = false;
        var integrityMismatch = false;
        foreach (var process in edgeProcesses)
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId)
                    {
                        foreignSessionProcess = true;
                        continue;
                    }
                    matchingSessionProcess = true;
                    var processUserSid = ReadProcessUserSid(process);
                    if (!string.Equals(userSid, processUserSid, StringComparison.OrdinalIgnoreCase))
                    {
                        foreignUserProcess = true;
                        continue;
                    }
                    matchingUserProcess = true;
                    if (!visibleProcessIds.Contains(process.Id)) continue;

                    var executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath) || !IsStandardStableInstallPath(executablePath))
                    {
                        pathNotValidated = true;
                        continue;
                    }
                    var integrityLevel = ReadProcessIntegrityLevel(process);
                    if (!EdgePolicyRefreshDispatchRules.IsSameIntegrityLevel(executorIntegrityLevel, integrityLevel))
                    {
                        integrityMismatch = true;
                        continue;
                    }
                    var version = FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
                    if (string.IsNullOrWhiteSpace(version)) version = FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        pathNotValidated = true;
                        continue;
                    }
                    candidates.Add(new EdgeTarget(process.Id, executablePath, version.Trim(), integrityLevel));
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                                  NotSupportedException or UnauthorizedAccessException or IOException or
                                                  System.Security.SecurityException or InvalidDataException)
                {
                    if (visibleProcessIds.Contains(process.Id)) identityUnavailable = true;
                }
            }
        }

        var target = candidates
            .OrderByDescending(candidate => EdgePolicyRefreshCompatibility.IsSupportedVersion(candidate.Version))
            .ThenBy(candidate => candidate.ProcessId)
            .FirstOrDefault();
        if (target is not null) return EdgeTargetSelection.Found(target);
        if (integrityMismatch)
            return EdgeTargetSelection.Deferred(EdgePolicyRefreshStatus.PendingSession, "EDGE_INTEGRITY_MISMATCH");
        if (pathNotValidated)
            return EdgeTargetSelection.Deferred(EdgePolicyRefreshStatus.NeedsReview, "EDGE_PATH_NOT_VALIDATED");
        if (identityUnavailable)
            return EdgeTargetSelection.Deferred(EdgePolicyRefreshStatus.PendingSession, "EDGE_PROCESS_IDENTITY_UNAVAILABLE");
        if (foreignSessionProcess || foreignUserProcess)
            return EdgeTargetSelection.Deferred(EdgePolicyRefreshStatus.PendingSession, "EDGE_SESSION_MISMATCH");
        if (matchingUserProcess)
            return EdgeTargetSelection.Deferred(EdgePolicyRefreshStatus.PendingSession, "EDGE_WINDOW_NOT_VISIBLE");
        if (matchingSessionProcess)
            return EdgeTargetSelection.Deferred(EdgePolicyRefreshStatus.PendingSession, "EDGE_USER_MISMATCH");
        return EdgeTargetSelection.Deferred(EdgePolicyRefreshStatus.BrowserNotRunning, null);
    }

    private static string ReadProcessUserSid(Process process)
    {
        if (!OpenProcessToken(process.SafeHandle, TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value ?? throw new InvalidDataException("无法读取 Edge 进程用户 SID。");
    }

    private static int ReadProcessIntegrityLevel(Process process)
    {
        if (!OpenProcessToken(process.SafeHandle, TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var requiredLength);
            var error = Marshal.GetLastWin32Error();
            if (requiredLength < Marshal.SizeOf<TokenMandatoryLabel>() || requiredLength > 4096)
                throw new Win32Exception(error, "无法确定 Windows 进程完整性级别。");
            var buffer = Marshal.AllocHGlobal((int)requiredLength);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, requiredLength, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var mandatoryLabel = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                if (mandatoryLabel.Label.Sid == IntPtr.Zero)
                    throw new InvalidDataException("Edge 进程令牌缺少完整性 SID。");
                var subAuthorityCountPointer = GetSidSubAuthorityCount(mandatoryLabel.Label.Sid);
                if (subAuthorityCountPointer == IntPtr.Zero)
                    throw new InvalidDataException("Edge 进程完整性 SID 无效。");
                var subAuthorityCount = Marshal.ReadByte(subAuthorityCountPointer);
                if (subAuthorityCount == 0)
                    throw new InvalidDataException("Edge 进程完整性 SID 没有 RID。");
                var lastSubAuthority = GetSidSubAuthority(mandatoryLabel.Label.Sid, (uint)(subAuthorityCount - 1));
                if (lastSubAuthority == IntPtr.Zero)
                    throw new InvalidDataException("Edge 进程完整性 RID 无效。");
                return Marshal.ReadInt32(lastSubAuthority);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static bool IsStandardStableInstallPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(fullPath), "msedge.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) is { Length: > 0 } x86
                ? Path.Combine(x86, "Microsoft", "Edge", "Application") : string.Empty
        }.Where(root => !string.IsNullOrWhiteSpace(root)).Select(Path.GetFullPath);
        return roots.Any(root => string.Equals(fullPath, Path.Combine(root, "msedge.exe"),
            StringComparison.OrdinalIgnoreCase));
    }

    private Journal LoadJournal()
    {
        try
        {
            if (!File.Exists(_statePath)) return Journal.Empty;
            var info = new FileInfo(_statePath);
            if (info.Length is <= 0 or > MaximumJournalBytes)
                throw new InvalidDataException("Edge 执行器状态文件大小无效。");
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(_statePath))
                          ?? throw new InvalidDataException("Edge 执行器状态文件为空。");
            if (journal.SchemaVersion != 1 || journal.Revision < 0 || journal.AttemptCount is < 0 or > MaximumAttempts ||
                journal.BrowserVersion?.Length > 64 || journal.ErrorCode?.Length > 80 || !Enum.IsDefined(journal.Status))
                throw new InvalidDataException("Edge 执行器状态字段无效。");
            return journal;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                          ArgumentException or System.Security.SecurityException)
        {
            AppendLog("NeedsReview", "EXECUTOR_STATE_INVALID");
            return Journal.Empty;
        }
    }

    private void WriteState(Journal journal, string eventName, int? browserIntegrityLevelRid = null)
    {
        _journal = journal;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        if (bytes.Length > MaximumJournalBytes) throw new InvalidDataException("Edge 执行器状态超过安全大小限制。");
        var temporaryPath = _statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        AppendLog(eventName, journal.ErrorCode, browserIntegrityLevelRid);
    }

    private void AppendLog(string eventName, string? errorCode, int? browserIntegrityLevelRid = null)
    {
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            var line = JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow,
                requestId = _journal.RequestId,
                revision = _journal.Revision,
                status = _journal.Status.ToString(),
                browserVersion = _journal.BrowserVersion,
                browserIntegrityLevelRid,
                sessionId = currentProcess.SessionId,
                attemptCount = _journal.AttemptCount,
                errorCode,
                eventName
            });
            var entry = line + Environment.NewLine;
            var entryBytes = Encoding.UTF8.GetByteCount(entry);
            if (entryBytes > MaximumLogBytes) return;
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length + entryBytes > MaximumLogBytes)
            {
                var backup = _logPath + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(_logPath, backup);
            }
            File.AppendAllText(_logPath, entry, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static string DiagnosticCode(Exception exception) => exception switch
    {
        HttpRequestException => "AGENT_UNAVAILABLE",
        CryptographicException => "AGENT_SIGNATURE_INVALID",
        UnauthorizedAccessException => "ACCESS_DENIED",
        Win32Exception => "PROCESS_START_FAILED",
        InvalidDataException => "INVALID_SIGNAL",
        _ => "REFRESH_REQUEST_FAILED"
    };

    private static void Validate(StudentAgentEdgePolicyRefreshSignal signal, DateTimeOffset nowUtc)
    {
        if (signal.SchemaVersion != 1 || signal.RequestId == Guid.Empty || signal.Revision <= 0 ||
            signal.RequestedUtc is not { } requested || requested.Offset != TimeSpan.Zero ||
            requested > nowUtc.AddMinutes(1))
            throw new InvalidDataException("Agent Edge 刷新请求格式或时间无效。");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken,
        int maximumBytes = 16 * 1024)
    {
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("Agent Edge 刷新请求超过大小限制。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Agent Edge 刷新请求超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private sealed record EdgeTarget(int ProcessId, string ExecutablePath, string Version, int IntegrityLevelRid);
    private sealed record EdgeTargetSelection(EdgeTarget? Target, EdgePolicyRefreshStatus Status, string? ErrorCode)
    {
        public static EdgeTargetSelection Found(EdgeTarget target) =>
            new(target, EdgePolicyRefreshStatus.RegistryCommitted, null);

        public static EdgeTargetSelection Deferred(EdgePolicyRefreshStatus status, string? errorCode) =>
            new(null, status, errorCode);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }
    private sealed record Journal(int SchemaVersion, Guid RequestId, long Revision,
        EdgePolicyRefreshStatus Status, string? BrowserVersion, int AttemptCount,
        DateTimeOffset UpdatedUtc, string? ErrorCode)
    {
        public static Journal Empty { get; } = new(1, Guid.Empty, 0, EdgePolicyRefreshStatus.RegistryCommitted,
            null, 0, DateTimeOffset.MinValue, null);
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(Microsoft.Win32.SafeHandles.SafeProcessHandle processHandle,
        uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);
}
