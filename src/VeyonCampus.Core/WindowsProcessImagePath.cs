using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VeyonCampus.Core;

/// <summary>Queries a peer's executable without requesting access to its process memory.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsProcessImagePath
{
    public static string Read(int processId)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length == 0) throw new InvalidDataException("管道对端进程没有可核验的程序路径。");
        return Path.GetFullPath(path.ToString());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        StringBuilder executableName, ref int size);
}
