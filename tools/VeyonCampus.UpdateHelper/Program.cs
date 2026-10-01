using VeyonCampus.Core;

if (args.Length > 0 && args[0] == "--start-student-agent")
    return StudentApplicationUpdateHandoff.RunHelper(args);

return ApplicationReleaseUpdateHandoff.RunHelper(args);
