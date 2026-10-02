namespace VeyonCampus.Core;

/// <summary>Copies an untrusted local Veyon installer into the protected app tree before verification and execution.</summary>
public static class WorkerProtectedStaging
{
    public static string StageVeyonInstaller(string sourcePath, WorkerInstallationInfo installation,
        string initiatingUserSid)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Worker staging is Windows-only.");
        if (!string.Equals(Path.GetFullPath(installation.WorkerExecutablePath),
                Path.GetFullPath(WorkerInstallationGuard.GetExpectedWorkerPath(installation.Role)),
                StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Worker staging root is outside the protected product installation.");
        if (!Path.IsPathFullyQualified(sourcePath) || sourcePath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("The Veyon installer must be a local absolute path.");
        var source = new FileInfo(sourcePath);
        if (!source.Exists || (source.Attributes & FileAttributes.ReparsePoint) != 0 ||
            source.Length is <= 0 or > 256L * 1024 * 1024)
            throw new InvalidDataException("The Veyon installer source is not a regular local file.");

        var stagingDirectory = Path.Combine(Path.GetDirectoryName(installation.WorkerExecutablePath)!, "Staging");
        Directory.CreateDirectory(stagingDirectory);
        WorkerInstallationGuard.VerifyProtectedInstallationObject(stagingDirectory, initiatingUserSid);
        var destination = Path.Combine(stagingDirectory, "veyon-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            WorkerInstallationGuard.VerifyProtectedInstallationObject(destination, initiatingUserSid);
            return destination;
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void DeleteStagedInstaller(string? stagedPath)
    {
        if (string.IsNullOrWhiteSpace(stagedPath)) return;
        try { if (File.Exists(stagedPath)) File.Delete(stagedPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
