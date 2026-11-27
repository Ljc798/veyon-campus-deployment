using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VeyonCampus.Core;

/// <summary>One process the Windows Restart Manager reports as holding a file open.</summary>
public sealed record FileLockOwner(int ProcessId, string ApplicationName, string ServiceShortName,
    bool IsService, bool IsRestartable);

/// <summary>
/// Answers "who is holding this file" when Windows refuses an operation with error 32 (sharing
/// violation) or 33 (lock violation).
/// </summary>
/// <remarks>
/// A deployment run only ever starts a short-lived Helper from the installation directory, so the
/// only realistic holders there are a crashed Helper left behind by an earlier attempt, an antivirus
/// scan of the freshly copied file, or a second deployment window. Retrying cannot clear any of
/// those on its own, and the bare Win32 error tells an administrator nothing. The Restart Manager
/// lists the owners by name, which turns "被占用" into "被 abd.exe (PID 1234) 占用".
/// </remarks>
[SupportedOSPlatform("windows")]
public static class FileLockDiagnostics
{
    private const int ErrorMoreData = 234;
    private const int MaxAttempts = 3;

    /// <summary>Processes currently holding <paramref name="path"/> open, best effort.</summary>
    public static IReadOnlyList<FileLockOwner> FindOwners(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return [];
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var owners = TryRead(path, out var moreData);
            if (!moreData) return owners;
        }
        return [];
    }

    /// <summary>
    /// Human-readable owner list, or an empty string when the file is free or the lookup failed.
    /// </summary>
    public static string DescribeOwners(string path)
    {
        try
        {
            var owners = FindOwners(path);
            if (owners.Count == 0) return string.Empty;
            return string.Join("、", owners.Take(4).Select(owner =>
                owner.IsService
                    ? $"服务 {owner.ServiceShortName}（{owner.ApplicationName}）"
                    : $"{owner.ApplicationName}（PID {owner.ProcessId}）"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.ComponentModel.Win32Exception or
                                              DllNotFoundException or EntryPointNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Ends a Helper left behind by an earlier attempt, which is the one holder this product can
    /// legitimately clear: the Helper is a one-shot process, so a surviving instance is a hang.
    /// Anything else - antivirus, an installer, a helper owned by another account - is left alone
    /// and reported instead.
    /// </summary>
    public static bool TryEndStaleHelper(string path, out string detail)
    {
        detail = string.Empty;
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        var helperName = Path.GetFileName(path);
        var ended = 0;
        foreach (var owner in FindOwners(path))
        {
            if (owner.IsService || owner.ProcessId <= 0 ||
                owner.ProcessId == Environment.ProcessId ||
                !string.Equals(owner.ApplicationName, helperName, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var process = Process.GetProcessById(owner.ProcessId);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                ended++;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                                  System.ComponentModel.Win32Exception or NotSupportedException)
            {
                detail = $"结束残留 {helperName}（PID {owner.ProcessId}）失败：{exception.Message}";
            }
        }
        if (ended > 0 && detail.Length == 0)
            detail = $"已结束 {ended} 个残留的 {helperName} 进程。";
        return ended > 0;
    }

    private static IReadOnlyList<FileLockOwner> TryRead(string path, out bool moreData)
    {
        moreData = false;
        var key = Guid.NewGuid().ToString();
        var result = RmStartSession(out var session, 0, key);
        if (result != 0) return [];
        try
        {
            var files = new[] { Path.GetFullPath(path) };
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0) return [];

            uint needed = 0;
            uint count = 0;
            uint reasons = 0;
            result = RmGetList(session, out needed, ref count, null, ref reasons);
            if (result != ErrorMoreData && result != 0) return [];
            if (needed == 0) return [];

            var buffer = new RmProcessInfo[needed];
            count = needed;
            result = RmGetList(session, out needed, ref count, buffer, ref reasons);
            if (result == ErrorMoreData)
            {
                moreData = true;
                return [];
            }
            if (result != 0) return [];

            var owners = new List<FileLockOwner>((int)count);
            for (var index = 0; index < count; index++)
            {
                var info = buffer[index];
                owners.Add(new FileLockOwner(info.Process.ProcessId, info.ApplicationName ?? string.Empty,
                    info.ServiceShortName ?? string.Empty,
                    info.ApplicationType == RmAppType.Service, info.Restartable));
            }
            return owners;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private enum RmAppType
    {
        Unknown = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string? ApplicationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? ServiceShortName;

        public RmAppType ApplicationType;
        public uint AppStatus;
        public uint SessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, string sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[] fileNames,
        uint applicationCount, [In] RmUniqueProcess[]? applications, uint serviceCount, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint proceduresNeeded, ref uint procedureCount,
        [In, Out] RmProcessInfo[]? affectedApplications, ref uint rebootReasons);
}
