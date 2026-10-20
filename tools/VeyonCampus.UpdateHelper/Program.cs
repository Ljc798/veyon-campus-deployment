using VeyonCampus.Core;

if (args.Length > 0 && args[0] == "--verify-release-key")
{
    if (args.Length > 2)
        return 64;

    try
    {
        var fingerprint = ApplicationReleaseTrust.GetPinnedPublicKeyFingerprint();
        if (args.Length == 2 &&
            (args[1].Length != 64 || !args[1].All(Uri.IsHexDigit) ||
             !string.Equals(fingerprint, args[1], StringComparison.OrdinalIgnoreCase)))
            return 4;
        return 0;
    }
    catch (InvalidOperationException)
    {
        return 3;
    }
    catch (InvalidDataException)
    {
        return 3;
    }
}

if (args.Length >= 2 && args[0] == "--stop-installed-role-processes" &&
    (args.Length == 2 || args is [_, _, "--preserve-update-helper"]))
{
    try
    {
        VeyonCampusProcessCleanup.StopInstalledRoleProcesses(role: args[1],
            preserveUpdateHelper: args.Length == 3);
        return 0;
    }
    catch (VeyonCampusProcessCleanup.VeyonCampusUpdateInProgressException)
    {
        return 2;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                      System.ComponentModel.Win32Exception or InvalidOperationException or
                                      ArgumentException or System.Security.SecurityException)
    {
        try
        {
            var cleanupLogPath = Path.Combine(Path.GetTempPath(), "VeyonCampus-ProcessCleanup.log");
            File.AppendAllText(cleanupLogPath,
                $"{DateTimeOffset.Now:O} Teacher/student process cleanup failed:{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}",
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception) { }
        return 1;
    }
}

if (args.Length > 0 && args[0] == "--start-student-agent")
    return StudentApplicationUpdateHandoff.RunHelper(args);

return ApplicationReleaseUpdateHandoff.RunHelper(args);
