using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace VeyonCampus.Core;

/// <summary>Windows-only pipe creation and peer PID lookup for the elevated Worker protocol.</summary>
public static class WindowsWorkerPipe
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const int BufferBytes = 4 * 1024;
    private const int DefaultTimeoutMilliseconds = 5_000;

    public static NamedPipeServerStream CreateServer(string pipeName, string initiatingUserSid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The privileged Worker pipe is available only on Windows.");
        if (pipeName.Length is 0 or > 180 || pipeName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-')))
            throw new InvalidDataException("Worker pipe name is invalid.");
        if (!PrivilegedWorkerProtocol.IsValidLocalSid(initiatingUserSid))
            throw new InvalidDataException("Worker pipe initiating SID is invalid.");

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var caller = new SecurityIdentifier(initiatingUserSid);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(administrators, PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(caller, PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
        var attributesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
        try
        {
            Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptorPointer,
                InheritHandle = false
            };
            Marshal.StructureToPtr(attributes, attributesPointer, fDeleteOld: false);

            var handle = CreateNamedPipeW(
                $"\\\\.\\pipe\\{pipeName}",
                PipeAccessDuplex | FileFlagOverlapped | FileFlagFirstPipeInstance,
                PipeWait | PipeRejectRemoteClients,
                maxInstances: 1,
                outBufferSize: BufferBytes,
                inBufferSize: BufferBytes,
                defaultTimeout: DefaultTimeoutMilliseconds,
                attributesPointer);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Could not create the protected Worker pipe.");
            }

            try
            {
                return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true,
                    isConnected: false, handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attributesPointer);
            Marshal.FreeHGlobal(descriptorPointer);
        }
    }

    public static int GetClientProcessId(SafePipeHandle pipeHandle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!GetNamedPipeClientProcessId(pipeHandle, out var processId))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the Worker pipe client.");
        return checked((int)processId);
    }

    public static int GetServerProcessId(SafePipeHandle pipeHandle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!GetNamedPipeServerProcessId(pipeHandle, out var processId))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the Worker pipe server.");
        return checked((int)processId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout,
        IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
