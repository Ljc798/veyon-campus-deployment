using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

#pragma warning disable CA1416 // This peer reader is Windows-only and checks the platform before any token/ACL calls.

namespace VeyonCampus.Core;

public sealed record WindowsProcessIdentity(int ProcessId, string UserSid, int SessionId,
    long StartTimeUtcTicks, string ImagePath, string ProductVersion, bool IsElevated);

/// <summary>Reads immutable process and token facts used to authenticate a local Worker peer.</summary>
public static class WindowsProcessIdentityReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int TokenElevation = 20;

    public static WindowsProcessIdentity Read(int processId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows process identity is available only on Windows.");
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));

        using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, checked((uint)processId));
        if (processHandle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Worker pipe peer process.");

        var pathBuffer = new StringBuilder(32_768);
        var pathLength = checked((uint)pathBuffer.Capacity);
        if (!QueryFullProcessImageName(processHandle, 0, pathBuffer, ref pathLength))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the Worker pipe peer image.");
        var imagePath = Path.GetFullPath(pathBuffer.ToString());

        if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect the Worker pipe peer token.");
        using (tokenHandle)
        {
            var userSid = ReadUserSid(tokenHandle);
            var elevated = ReadIsElevated(tokenHandle);
            using var process = Process.GetProcessById(processId);
            var startTime = process.StartTime.ToUniversalTime().Ticks;
            var productVersion = WorkerInstallationGuard.GetAssemblyProductVersion(imagePath);
            return new WindowsProcessIdentity(processId, userSid, process.SessionId, startTime,
                imagePath, productVersion, elevated);
        }
    }

    private static string ReadUserSid(SafeAccessTokenHandle tokenHandle)
    {
        _ = GetTokenInformation(tokenHandle, TokenUser, IntPtr.Zero, 0, out var required);
        if (required == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size the peer token identity.");
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!GetTokenInformation(tokenHandle, TokenUser, buffer, required, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the peer token identity.");
            var sidPointer = Marshal.ReadIntPtr(buffer);
            return new SecurityIdentifier(sidPointer).Value;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool ReadIsElevated(SafeAccessTokenHandle tokenHandle)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!GetTokenInformation(tokenHandle, TokenElevation, buffer, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the peer elevation state.");
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        StringBuilder imageName, ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);
}

#pragma warning restore CA1416
