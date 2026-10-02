using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

#pragma warning disable CA1416 // These ACL APIs are invoked only from Windows-gated Worker entry points.

namespace VeyonCampus.Core;

public sealed record WorkerLaunchContext(string PipeName, Guid RequestId, WorkerCallerIdentity Caller)
{
    public static WorkerLaunchContext ParseArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count != 18 || args.Count % 2 != 0)
            throw new InvalidDataException("Worker launch arguments are invalid.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            var name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(name, args[index + 1]))
                throw new InvalidDataException("Worker launch arguments are invalid.");
        }

        if (values.Count != 9 || !values.TryGetValue("--protocol", out var protocol) ||
            protocol != PrivilegedWorkerProtocol.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            !values.TryGetValue("--pipe", out var pipeName) || !IsValidPipeName(pipeName) ||
            !values.TryGetValue("--request-id", out var requestText) || !Guid.TryParseExact(requestText, "N", out var requestId) ||
            !values.TryGetValue("--caller-pid", out var pidText) || !int.TryParse(pidText, out var pid) || pid <= 0 ||
            !values.TryGetValue("--caller-sid", out var sid) || !PrivilegedWorkerProtocol.IsValidLocalSid(sid) ||
            !values.TryGetValue("--caller-session", out var sessionText) || !int.TryParse(sessionText, out var session) || session < 0 ||
            !values.TryGetValue("--caller-start-ticks", out var startText) || !long.TryParse(startText, out var startTicks) || startTicks <= 0 ||
            !values.TryGetValue("--caller-version", out var version) || string.IsNullOrWhiteSpace(version) || version.Length > 64 ||
            !values.TryGetValue("--caller-role", out var roleText) ||
            !Enum.TryParse<VeyonCampusRole>(roleText, ignoreCase: false, out var role) || !Enum.IsDefined(role))
            throw new InvalidDataException("Worker launch arguments are invalid.");

        var callerPath = WorkerInstallationGuard.GetExpectedUiPath(role);
        return new WorkerLaunchContext(pipeName, requestId,
            new WorkerCallerIdentity(pid, sid, session, callerPath, version, role, startTicks));
    }

    private static bool IsValidPipeName(string pipeName) => pipeName.Length is > 0 and <= 180 &&
        pipeName.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-');

    public string[] ToArguments()
    {
        PrivilegedWorkerProtocol.ValidateCallerIdentity(Caller);
        return
        [
            "--protocol", PrivilegedWorkerProtocol.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--pipe", PipeName,
            "--request-id", RequestId.ToString("N"),
            "--caller-pid", Caller.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--caller-sid", Caller.UserSid,
            "--caller-session", Caller.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--caller-start-ticks", Caller.StartTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--caller-version", Caller.ProductVersion,
            "--caller-role", Caller.Role.ToString()
        ];
    }
}

/// <summary>Checks the installed UI/Worker pair and rejects user-writable elevation paths.</summary>
public static class WorkerInstallationGuard
{
    private const string RoleInfoFileName = "veyon-campus-role.json";
    private const string WorkerDirectoryName = "Worker";
    private const string WorkerFileName = "VeyonCampus.Worker.exe";
    private const string StudentExecutableName = "VeyonCampus.StudentSetup.exe";
    private const string TeacherExecutableName = "VeyonCampus.Teacher.exe";
    private const FileSystemRights WriteRights = FileSystemRights.Write | FileSystemRights.Modify |
        FileSystemRights.FullControl | FileSystemRights.Delete | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories |
        FileSystemRights.AppendData | FileSystemRights.WriteData | FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles;

    public static WorkerCallerIdentity CaptureCurrentUiIdentity(VeyonCampusRole role)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The elevated Worker is Windows-only.");
        using var process = Process.GetCurrentProcess();
        var imagePath = Environment.ProcessPath ?? process.MainModule?.FileName
            ?? throw new InvalidOperationException("Could not determine the current UI executable path.");
        var sid = WindowsIdentity.GetCurrent().User?.Value
                  ?? throw new InvalidOperationException("Could not determine the current Windows user SID.");
        return new WorkerCallerIdentity(process.Id, sid, process.SessionId, Path.GetFullPath(imagePath),
            GetAssemblyProductVersion(imagePath), role, process.StartTime.ToUniversalTime().Ticks);
    }

    public static WorkerInstallationInfo ValidateCurrentUi(VeyonCampusRole role, WorkerCallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var observed = CaptureCurrentUiIdentity(role);
        ValidateCallerProcess(caller, observed.ProcessId, observed.UserSid, observed.SessionId,
            observed.StartTimeUtcTicks, observed.ExecutablePath);
        var expectedUiPath = GetExpectedUiPath(role);
        if (!PathEquals(observed.ExecutablePath, expectedUiPath))
            throw new UnauthorizedAccessException("The UI executable is not running from its expected install path.");
        return ValidatePair(role, observed.ExecutablePath, caller.UserSid);
    }

    public static WorkerInstallationInfo ValidateCurrentWorker(VeyonCampusRole role, string initiatingUserSid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The elevated Worker is Windows-only.");
        var workerPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not determine the Worker executable path.");
        if (PlatformFacts.IsCurrentProcessElevated != true)
            throw new UnauthorizedAccessException("The Worker did not receive an elevated Windows token.");
        var expectedWorkerPath = Path.Combine(GetExpectedInstallRoot(role), WorkerDirectoryName, WorkerFileName);
        if (!PathEquals(Path.GetFullPath(workerPath), expectedWorkerPath))
            throw new UnauthorizedAccessException("The Worker is not running from its expected install path.");
        return ValidatePair(role, Path.GetFullPath(workerPath), initiatingUserSid);
    }

    public static void ValidateCallerProcess(WorkerCallerIdentity expected, int actualProcessId,
        string actualUserSid, int actualSessionId, long actualStartTimeUtcTicks, string actualImagePath)
    {
        PrivilegedWorkerProtocol.ValidateCallerIdentity(expected);
        if (actualProcessId != expected.ProcessId || actualUserSid != expected.UserSid ||
            actualSessionId != expected.SessionId || actualStartTimeUtcTicks != expected.StartTimeUtcTicks ||
            !PathEquals(actualImagePath, expected.ExecutablePath) ||
            !string.Equals(GetAssemblyProductVersion(actualImagePath), expected.ProductVersion, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The Worker pipe caller identity does not match the launched UI.");
    }

    public static string GetExpectedUiPath(VeyonCampusRole role)
    {
        var installRoot = GetExpectedInstallRoot(role);
        return Path.Combine(installRoot, role == VeyonCampusRole.StudentSetup
            ? StudentExecutableName : TeacherExecutableName);
    }

    public static string GetExpectedWorkerPath(VeyonCampusRole role) =>
        Path.Combine(GetExpectedInstallRoot(role), WorkerDirectoryName, WorkerFileName);

    public static bool ProductVersionMatches(string path, string expectedVersion) =>
        string.Equals(GetAssemblyProductVersion(path), expectedVersion, StringComparison.Ordinal);

    public static void VerifyProtectedInstallationObject(string path, string initiatingUserSid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected installation checks are Windows-only.");
        EnsureProtectedPathObject(path, initiatingUserSid);
    }

    private static WorkerInstallationInfo ValidatePair(VeyonCampusRole role, string observedImagePath,
        string initiatingUserSid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The elevated Worker is Windows-only.");
        if (!PrivilegedWorkerProtocol.IsValidLocalSid(initiatingUserSid))
            throw new UnauthorizedAccessException("The Worker caller SID could not be verified.");

        var installRoot = GetExpectedInstallRoot(role);
        var expectedUiPath = GetExpectedUiPath(role);
        var workerDirectory = Path.Combine(installRoot, WorkerDirectoryName);
        var workerPath = Path.Combine(workerDirectory, WorkerFileName);
        var roleInfoPath = Path.Combine(installRoot, RoleInfoFileName);
        if (!PathEquals(observedImagePath, expectedUiPath) && !PathEquals(observedImagePath, workerPath))
            throw new UnauthorizedAccessException("The elevated Worker or UI is not running from its expected install path.");

        foreach (var path in GetPathComponents(installRoot, workerDirectory, workerPath, expectedUiPath, roleInfoPath))
            EnsureProtectedPathObject(path, initiatingUserSid);
        foreach (var path in EnumerateInstallationTree(installRoot))
            EnsureProtectedPathObject(path, initiatingUserSid);

        var metadata = ReadRoleMetadata(roleInfoPath, role);
        var appVersion = GetAssemblyProductVersion(expectedUiPath);
        var workerVersion = GetAssemblyProductVersion(workerPath);
        if (!string.Equals(metadata.Version, appVersion, StringComparison.Ordinal) ||
            !string.Equals(metadata.Version, workerVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The installed UI and Worker versions do not match.");

        return new WorkerInstallationInfo(role, installRoot, expectedUiPath, workerPath, roleInfoPath, metadata.Version);
    }

    private static string GetExpectedInstallRoot(VeyonCampusRole role)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            throw new InvalidOperationException("Could not determine the protected Program Files directory.");
        var roleFolder = role switch
        {
            VeyonCampusRole.StudentSetup => "Student",
            VeyonCampusRole.TeacherConsole => "Teacher",
            _ => throw new InvalidDataException("Worker role is invalid.")
        };
        return Path.GetFullPath(Path.Combine(programFiles, "Veyon Campus", roleFolder));
    }

    private static IEnumerable<string> GetPathComponents(string installRoot, string workerDirectory,
        string workerPath, string uiPath, string roleInfoPath)
    {
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var root = Path.GetFullPath(installRoot);
        if (!root.StartsWith(programFiles.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The Worker installation is outside Program Files.");
        yield return programFiles;
        var relative = Path.GetRelativePath(programFiles, root);
        var current = programFiles;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
        yield return workerDirectory;
        yield return workerPath;
        yield return uiPath;
        yield return roleInfoPath;
    }

    private static IEnumerable<string> EnumerateInstallationTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            yield return directory;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                yield return entry;
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
    }

    private static void EnsureProtectedPathObject(string path, string initiatingUserSid)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The Worker installation contains a reparse point.");
        var untrusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            initiatingUserSid,
            new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null).Value
        };

        FileSystemSecurity security = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is not null && untrusted.Contains(owner.Value))
            throw new UnauthorizedAccessException("The Worker installation object has an untrusted owner.");

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference is SecurityIdentifier identity && untrusted.Contains(identity.Value) &&
                (rule.FileSystemRights & WriteRights) != 0)
                throw new UnauthorizedAccessException("The Worker installation is writable by the UI user or a broad user group.");
        }
    }

    private static RoleMetadata ReadRoleMetadata(string path, VeyonCampusRole expectedRole)
    {
        var metadataBytes = File.ReadAllBytes(path);
        var utf8Json = metadataBytes.AsMemory();
        var prefix = utf8Json.Span;
        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
            utf8Json = utf8Json[3..];
        using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Installed role metadata is invalid.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Installed role metadata has duplicate fields.");
        if (names.Count != 4 || !root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
            !root.TryGetProperty("role", out var roleValue) || roleValue.GetString() != expectedRole.ToString() ||
            !root.TryGetProperty("product", out var productValue) ||
            productValue.GetString() != (expectedRole == VeyonCampusRole.StudentSetup
                ? "VeyonCampus.StudentSetup" : "VeyonCampus.TeacherConsole") ||
            !root.TryGetProperty("version", out var versionValue) || versionValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Installed role metadata does not match the Worker role.");
        var version = NormalizeVersion(versionValue.GetString() ?? "");
        if (version.Length == 0) throw new InvalidDataException("Installed role version is invalid.");
        return new RoleMetadata(version);
    }

    public static string GetAssemblyProductVersion(string executablePath)
    {
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Installed UI or Worker file is missing.", executablePath);
        var assemblyPath = Path.ChangeExtension(executablePath, ".dll");
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("Installed UI or Worker assembly is missing.", assemblyPath);
        var version = AssemblyName.GetAssemblyName(assemblyPath).Version
                      ?? throw new InvalidDataException("Installed UI or Worker assembly version is missing.");
        return NormalizeVersion(version.ToString(3));
    }

    private static string NormalizeVersion(string value)
    {
        var normalized = value.Trim();
        var metadataIndex = normalized.IndexOf('+');
        if (metadataIndex >= 0) normalized = normalized[..metadataIndex];
        return normalized;
    }

    public static string NormalizeProductVersion(string value) => NormalizeVersion(value);

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private sealed record RoleMetadata(string Version);
}

public sealed record WorkerInstallationInfo(VeyonCampusRole Role, string InstallRoot,
    string UiExecutablePath, string WorkerExecutablePath, string RoleMetadataPath, string ProductVersion);
