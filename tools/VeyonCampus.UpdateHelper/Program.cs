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

if (args.Length > 0 && args[0] == "--start-student-agent")
    return StudentApplicationUpdateHandoff.RunHelper(args);

return ApplicationReleaseUpdateHandoff.RunHelper(args);
