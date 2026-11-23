using VeyonCampus.Core;

namespace VeyonCampus.Checks;

/// <summary>
/// Pure checks for the bounded uninstall ownership-recovery helper. No OS command runs here:
/// these assert that the command builders only target the exact verified path and that the
/// retry predicate stays narrow, so the helper cannot be reused as a broad cleanup tool.
/// </summary>
internal static class OwnedPathDeleteRecoveryChecks
{
    public static void Run()
    {
        var verifiedPath = @"C:\ProgramData\VeyonCampus\Teacher\cache";

        var takeOwn = OwnedPathDeleteRecovery.BuildTakeOwnArguments(verifiedPath);
        if (takeOwn.Count == 0 || takeOwn[0] != "/F" || takeOwn[1] != verifiedPath)
            throw new Exception("takeown must target the exact verified path via /F.");
        if (!takeOwn.Contains("/R") || !takeOwn.Contains("/D") || !takeOwn.Contains("Y"))
            throw new Exception("takeown must recurse only inside the verified product path.");
        if (takeOwn.Any(arg => arg.Contains("Veyon Solutions", StringComparison.OrdinalIgnoreCase) ||
                               arg.Contains("Program Files", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("takeown arguments must not broaden to other product roots.");

        var icacls = OwnedPathDeleteRecovery.BuildIcaclsArguments(verifiedPath);
        if (icacls.Count == 0 || icacls[0] != verifiedPath || !icacls.Contains("/grant"))
            throw new Exception("icacls must grant on the exact verified path.");
        if (!icacls.Any(arg => arg.Contains("S-1-5-32-544", StringComparison.Ordinal)) ||
            !icacls.Any(arg => arg.Contains("(OI)(CI)F", StringComparison.Ordinal)))
            throw new Exception("icacls must grant Administrators full control only.");
        if (!icacls.Contains("/T") || !icacls.Contains("/C"))
            throw new Exception("icacls must recurse within the verified path and continue on errors.");
        if (icacls.Any(arg => arg.Contains("Veyon Solutions", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("icacls arguments must not broaden to other product roots.");

        if (!OwnedPathDeleteRecovery.ShouldRetry(new UnauthorizedAccessException()))
            throw new Exception("Permission failures should be retried once after recovery.");
        if (!OwnedPathDeleteRecovery.ShouldRetry(new IOException("file in use")))
            throw new Exception("File-in-use failures should be retried once after recovery.");
        if (OwnedPathDeleteRecovery.ShouldRetry(new InvalidOperationException()) ||
            OwnedPathDeleteRecovery.ShouldRetry(new ArgumentException()))
            throw new Exception("Non-permission failures must stay fail-closed without recovery.");

        try
        {
            _ = OwnedPathDeleteRecovery.BuildTakeOwnArguments("   ");
            throw new Exception("Blank verified path should be rejected by the command builder.");
        }
        catch (ArgumentException) { }
        try
        {
            _ = OwnedPathDeleteRecovery.BuildIcaclsArguments("");
            throw new Exception("Empty verified path should be rejected by the command builder.");
        }
        catch (ArgumentException) { }
    }
}
