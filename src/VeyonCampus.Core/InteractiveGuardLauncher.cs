using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace VeyonCampus.Core;

/// <summary>
/// Starts the interactive InputGuard in the student's desktop session from the SYSTEM Agent.
/// </summary>
/// <remarks>
/// The Guard has to live in the interactive session because <c>BlockInput</c> only affects the
/// calling desktop; the Agent runs in Session 0. The normal starter is Student Companion, but a
/// machine can be logged in without it (never launched, closed, or the Guard exited). In that state
/// every signed command used to fail with "状态无法核验" while nothing on the teacher side could fix
/// it, so the Agent now starts the Guard itself.
///
/// Two details matter on a real classroom machine:
/// <list type="bullet">
/// <item><c>WTSQueryUserToken</c> requires <c>SeTcbPrivilege</c> to be enabled in the calling token.
/// SYSTEM holds it, but a scheduled-task token does not always have it enabled, and the failure
/// (1314) looks exactly like "no session". The required privileges are enabled before the call.</item>
/// <item>A Guard whose lease thread could not release input keeps its pipe open but never answers.
/// Such a process must be ended and started again, otherwise every attempt reports "unknown".</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class InteractiveGuardLauncher
{
    private const uint TokenAllAccess = 0x000F01FF;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAdjustSessionId = 0x0100;
    private const int TokenSessionIdClass = 12;
    private const int TokenUserClass = 1;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    private static readonly string[] RequiredPrivileges =
        ["SeTcbPrivilege", "SeAssignPrimaryTokenPrivilege", "SeIncreaseQuotaPrivilege"];

    /// <summary>True when the Guard executable is already running inside this session.</summary>
    public static bool IsRunningInSession(int sessionId, string executablePath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath)))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || process.SessionId != sessionId) continue;
                    var path = WindowsProcessImagePath.Read(process.Id);
                    if (string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                                                      System.ComponentModel.Win32Exception or
                                                      UnauthorizedAccessException or ArgumentException)
                {
                    continue;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// True when the Guard in this session runs as SYSTEM, which is what lets it act on the
    /// desktop. A Guard started with the student's token answers the pipe but cannot block input,
    /// so "a process exists" is not the same as "the Guard is usable".
    /// </summary>
    public static bool IsSystemGuardInSession(int sessionId, string executablePath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath)))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || process.SessionId != sessionId) continue;
                    if (!string.Equals(WindowsProcessImagePath.Read(process.Id), executablePath,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsSystemProcessToken(process)) return true;
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                                                      System.ComponentModel.Win32Exception or
                                                      UnauthorizedAccessException or ArgumentException)
                {
                    continue;
                }
            }
        }
        return false;
    }

    private static bool IsSystemProcessToken(Process process)
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process.Handle, TokenQuery, out token)) return false;
            if (!GetTokenInformation(token, TokenUserClass, IntPtr.Zero, 0, out var size) || size <= 0)
                return false;
            buffer = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(token, TokenUserClass, buffer, size, out _)) return false;
            var sid = Marshal.ReadIntPtr(buffer);
            return sid != IntPtr.Zero && new System.Security.Principal.SecurityIdentifier(sid).Value == "S-1-5-18";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                                              UnauthorizedAccessException or ArgumentException or
                                              InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>
    /// Ensures an idle Guard runs in every active interactive session. Returns true when at least
    /// one Guard process was started by this call.
    /// </summary>
    /// <param name="executablePath">The protected Guard executable the Agent expects.</param>
    /// <param name="restartUnresponsive">
    /// End a Guard that exists but does not answer the pipe before starting a new one. Callers pass
    /// true only after a status read actually failed, so a busy but healthy Guard is never killed.
    /// </param>
    /// <param name="detail">One line for the Agent log: started, already running, or why not.</param>
    public static bool TryLaunchInActiveSessions(string executablePath, bool restartUnresponsive,
        out string detail)
    {
        detail = "";
        if (!File.Exists(executablePath))
        {
            detail = "安装目录中缺少 InputGuard 程序。";
            return false;
        }
        IReadOnlyList<int> sessions;
        try { sessions = StudentInputGuardIpcClient.GetActiveInteractiveSessions(); }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException or IOException)
        {
            detail = "无法枚举活动交互桌面会话：" + exception.Message;
            return false;
        }
        if (sessions.Count == 0)
        {
            detail = "当前没有活动的交互桌面会话。";
            return false;
        }

        // Every active session gets a Guard. A lab VM can have a console desktop plus a remote one,
        // and starting only in the "likely" session left the other one with a pipe nobody served.
        var notes = new List<string>();
        var startedAny = false;
        foreach (var sessionId in StudentInputGuardIpcClient.OrderSessionsForAttempt(sessions))
        {
            if (IsSystemGuardInSession(sessionId, executablePath))
            {
                if (!restartUnresponsive)
                {
                    notes.Add($"会话 {sessionId} 已有 SYSTEM Guard。");
                    continue;
                }
                var stoppedSystem = StopGuardInSession(sessionId, executablePath);
                notes.Add($"已结束会话 {sessionId} 中无响应的 SYSTEM Guard {stoppedSystem} 个。");
                Thread.Sleep(300);
            }
            else if (IsRunningInSession(sessionId, executablePath))
            {
                // A student-token Guard answers the pipe but cannot block input (BlockInput returns 5),
                // so it is replaced regardless of whether it looks responsive.
                var stoppedUser = StopGuardInSession(sessionId, executablePath);
                notes.Add($"已结束会话 {sessionId} 中权限不足的 Guard {stoppedUser} 个，改以 SYSTEM 重启。");
                Thread.Sleep(300);
            }
            var started = TryStart(sessionId, executablePath, out var startDetail);
            startedAny |= started;
            notes.Add(startDetail);
        }
        detail = string.Join(" ", notes);
        return startedAny;
    }

    private static int StopGuardInSession(int sessionId, string executablePath)
    {
        var stopped = 0;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath)))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId ||
                        !string.Equals(WindowsProcessImagePath.Read(process.Id), executablePath,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                    stopped++;
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                                                      System.ComponentModel.Win32Exception or
                                                      UnauthorizedAccessException or NotSupportedException or
                                                      AggregateException)
                {
                    // Already gone, or owned by a token this account may not touch.
                }
            }
        }
        return stopped;
    }

    private static bool TryStart(int sessionId, string executablePath, out string detail)
    {
        detail = "";
        var privilegeNotes = EnableRequiredPrivileges();
        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        var tokenNotes = "";
        try
        {
            // Prefer this process's own SYSTEM token, moved into the target session.
            //
            // BlockInput returns ERROR_ACCESS_DENIED (5) when the caller cannot act on the desktop -
            // most commonly because the foreground window has a higher integrity level than the
            // caller. A Guard started with the student's filtered token therefore cannot freeze a
            // desktop that shows an elevated window, and some builds deny it outright. The same
            // protected EXE running as SYSTEM inside the session has no such limit.
            if (!TryCreateSessionSystemToken(sessionId, out primaryToken, out var systemTokenError))
            {
                tokenNotes = $"（SYSTEM 令牌不可用：{systemTokenError}；改用学生会话令牌，BlockInput 可能被拒绝）";
                if (!WTSQueryUserToken((uint)sessionId, out userToken))
                    return Fail("WTSQueryUserToken", Marshal.GetLastWin32Error(), privilegeNotes, out detail);
                if (!DuplicateTokenEx(userToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation,
                        TokenPrimary, out primaryToken))
                    return Fail("DuplicateTokenEx", Marshal.GetLastWin32Error(), privilegeNotes, out detail);
            }
            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                environment = IntPtr.Zero;

            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = @"winsta0\default"
            };
            var commandLine = new StringBuilder("\"" + executablePath + "\" --guard-server");
            var workingDirectory = Path.GetDirectoryName(executablePath);
            var flags = CreateUnicodeEnvironment | CreateNoWindow;

            var created = CreateProcessAsUser(primaryToken, executablePath, commandLine, IntPtr.Zero,
                IntPtr.Zero, false, flags, environment, workingDirectory, ref startup, out var processInfo);
            var error = created ? 0 : Marshal.GetLastWin32Error();
            if (!created)
            {
                // SeAssignPrimaryTokenPrivilege is not always usable in a scheduled task token.
                // CreateProcessWithTokenW only needs SeImpersonatePrivilege, which SYSTEM holds.
                created = CreateProcessWithToken(primaryToken, 0, executablePath, commandLine, flags,
                    environment, workingDirectory, ref startup, out processInfo);
                if (!created)
                {
                    var fallbackError = Marshal.GetLastWin32Error();
                    detail = $"无法在会话 {sessionId} 启动 Guard：CreateProcessAsUser={error}，" +
                             $"CreateProcessWithTokenW={fallbackError}{privilegeNotes}{tokenNotes}";
                    return false;
                }
            }

            var processId = processInfo.ProcessId;
            if (processInfo.ProcessHandle != IntPtr.Zero) CloseHandle(processInfo.ProcessHandle);
            if (processInfo.ThreadHandle != IntPtr.Zero) CloseHandle(processInfo.ThreadHandle);
            detail = $"已在会话 {sessionId} 启动 Guard（PID {processId}）{privilegeNotes}{tokenNotes}";
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException or
                                              InvalidOperationException or ArgumentException or
                                              System.Security.SecurityException)
        {
            detail = "启动 Guard 失败：" + exception.Message + privilegeNotes + tokenNotes;
            return false;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    /// <summary>
    /// Duplicates the Agent's own SYSTEM token and moves it into the target session, so the Guard
    /// runs with full integrity on that desktop. Needs SeTcbPrivilege (enabled above).
    /// </summary>
    private static bool TryCreateSessionSystemToken(int sessionId, out IntPtr token, out string error)
    {
        token = IntPtr.Zero;
        error = "";
        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery | TokenAdjustSessionId,
                out var current))
        {
            error = "OpenProcessToken " + Marshal.GetLastWin32Error();
            return false;
        }
        try
        {
            if (!DuplicateTokenEx(current, TokenAllAccess, IntPtr.Zero, SecurityImpersonation,
                    TokenPrimary, out var primary))
            {
                error = "DuplicateTokenEx " + Marshal.GetLastWin32Error();
                return false;
            }
            var id = sessionId;
            if (!SetTokenInformation(primary, TokenSessionIdClass, ref id, sizeof(int)))
            {
                error = "SetTokenInformation " + Marshal.GetLastWin32Error();
                CloseHandle(primary);
                return false;
            }
            token = primary;
            return true;
        }
        finally { CloseHandle(current); }
    }

    /// <summary>
    /// Enables the privileges the session launch needs and returns a short note about any that could
    /// not be enabled, so a 1314 in the log is explainable instead of mysterious.
    /// </summary>
    private static string EnableRequiredPrivileges()
    {
        var failed = new List<string>();
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            return "（无法打开本进程令牌，未启用会话启动特权）";
        try
        {
            foreach (var name in RequiredPrivileges)
            {
                if (!LookupPrivilegeValue(null, name, out var luid))
                {
                    failed.Add(name);
                    continue;
                }
                var state = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Privilege = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
                };
                if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) ||
                    Marshal.GetLastWin32Error() == 1300)  // ERROR_NOT_ALL_ASSIGNED
                    failed.Add(name);
            }
        }
        finally { CloseHandle(token); }
        return failed.Count == 0 ? "" : "（未能启用特权：" + string.Join("、", failed) + "）";
    }

    private static bool Fail(string api, int error, string notes, out string detail)
    {
        detail = $"{api} 失败（Win32 {error}）；无法在学生会话启动 Guard。{notes}";
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public long Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess,
        IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "CreateProcessAsUserW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName,
        StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "CreateProcessWithTokenW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithToken(IntPtr token, uint logonFlags, string? applicationName,
        StringBuilder commandLine, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(IntPtr token, int informationClass,
        ref int information, int informationLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass,
        IntPtr information, int informationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
