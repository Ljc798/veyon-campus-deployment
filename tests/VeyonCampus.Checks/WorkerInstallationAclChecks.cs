#pragma warning disable CA1416 // Pure ACL flag-mask assertions; no Windows-only API is invoked.

using System.Security.AccessControl;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class WorkerInstallationAclChecks
{
    public static void Run()
    {
        Expect(!WorkerInstallationGuard.GrantsWriteAccess(FileSystemRights.Read));
        Expect(!WorkerInstallationGuard.GrantsWriteAccess(FileSystemRights.ReadAndExecute));
        Expect(WorkerInstallationGuard.GrantsWriteAccess(FileSystemRights.Write));
        Expect(WorkerInstallationGuard.GrantsWriteAccess(FileSystemRights.Modify));
        Expect(WorkerInstallationGuard.GrantsWriteAccess(FileSystemRights.FullControl));
        Expect(WorkerInstallationGuard.GrantsWriteAccess(FileSystemRights.ChangePermissions));
        Expect(WorkerInstallationGuard.GrantsWriteAccess(FileSystemRights.TakeOwnership));

        static void Expect(bool condition)
        {
            if (!condition) throw new Exception("Worker installation ACL permission assertion failed.");
        }
    }
}
