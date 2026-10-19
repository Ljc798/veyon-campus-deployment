using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace VeyonCampus.Core;

public sealed record EdgeBrowserInstallationDiagnostic(string Channel, string Architecture,
    string? ImageFileVersion, string ExecutablePath, bool RefreshSupported);

public sealed record EdgeBrowserProcessDiagnostic(int ProcessId, int? SessionId, string? UserSid,
    int? IntegrityLevelRid, string Channel, string Architecture, string? ImageFileVersion,
    string? ExecutablePath, bool RefreshSupported, DateTimeOffset? StartedUtc,
    DateTimeOffset? ImageModifiedUtc, bool? ProcessMayPrecedeImageUpdate, string? ErrorCode);

public sealed record EdgeUpdateServiceDiagnostic(string Name, bool? Installed, string State,
    string StartType, string? ImageFileVersion, string? ErrorCode);

public sealed record EdgeExecutorReportDiagnostic(string UserSid, int? SessionId, Guid RequestId,
    long Revision, EdgePolicyRefreshStatus Status, string? BrowserVersion, int AttemptCount,
    DateTimeOffset UpdatedUtc, string? ErrorCode);

public sealed record EdgeBrowserDiagnosticsSnapshot(int SchemaVersion, DateTimeOffset CollectedUtc,
    long PolicyRevision, long RefreshRevision, Guid RefreshRequestId, DateTimeOffset? RefreshRequestedUtc,
    IReadOnlyList<EdgeBrowserInstallationDiagnostic> InstalledBrowsers,
    IReadOnlyList<EdgeBrowserProcessDiagnostic> RunningProcesses,
    IReadOnlyList<EdgeUpdateServiceDiagnostic> UpdateServices,
    IReadOnlyList<EdgeExecutorReportDiagnostic> ExecutorReports,
    IReadOnlyList<string> Warnings);

/// <summary>Read-only Windows inventory for teacher-side Edge compatibility diagnosis.</summary>
[SupportedOSPlatform("windows")]
public static class EdgeBrowserDiagnosticsReader
{
    private const int MaximumRunningProcesses = 48;
    private const int MaximumInstalledBrowsers = 64;
    private const int MaximumUserProfiles = 32;
    private const int MaximumExecutorStateBytes = 32 * 1024;
    private const int MaximumExecutorLogBytes = 256 * 1024;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int ScManagerConnect = 0x0001;
    private const int ServiceQueryStatus = 0x0004;

    private static readonly string[] UpdateServiceNames =
        ["edgeupdate", "edgeupdatem", "MicrosoftEdgeElevationService"];

    private static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
    };

    public static EdgeBrowserDiagnosticsSnapshot Read(long policyRevision,
        StudentAgentEdgePolicyRefreshSignal refreshSignal, DateTimeOffset collectedUtc)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Edge 浏览器诊断仅支持 Windows。");
        ArgumentNullException.ThrowIfNull(refreshSignal);
        if (policyRevision < 0) throw new ArgumentOutOfRangeException(nameof(policyRevision));

        var warnings = new List<string>();
        var profiles = ReadProfiles(warnings);
        var installed = ReadInstalledBrowsers(profiles, warnings);
        var running = ReadRunningProcesses(profiles, warnings);
        var services = UpdateServiceNames.Select(name => ReadUpdateService(name, warnings)).ToArray();
        var reports = ReadExecutorReports(profiles, warnings);
        return new EdgeBrowserDiagnosticsSnapshot(1, collectedUtc.ToUniversalTime(), policyRevision,
            refreshSignal.Revision, refreshSignal.RequestId, refreshSignal.RequestedUtc, Array.AsReadOnly(installed),
            Array.AsReadOnly(running), Array.AsReadOnly(services), Array.AsReadOnly(reports),
            Array.AsReadOnly(warnings.Distinct(StringComparer.Ordinal).Take(8).ToArray()));
    }

    public static string ClassifyChannel(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return "unknown";
        try
        {
            if (!string.Equals(Path.GetFileName(executablePath), "msedge.exe", StringComparison.OrdinalIgnoreCase))
                return "unknown";
            var application = Directory.GetParent(Path.GetFullPath(executablePath))?.Name;
            if (!string.Equals(application, "Application", StringComparison.OrdinalIgnoreCase)) return "unknown";
            var productDirectory = Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(executablePath))!)?.Name;
            return productDirectory?.ToLowerInvariant() switch
            {
                "edge" => "stable",
                "edge beta" => "beta",
                "edge dev" => "dev",
                "edge sxs" => "canary",
                _ => "unknown"
            };
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or NotSupportedException)
        {
            return "unknown";
        }
    }

    private static EdgeBrowserInstallationDiagnostic[] ReadInstalledBrowsers(
        IReadOnlyDictionary<string, string> profileRoots, List<string> warnings)
    {
        var results = new List<EdgeBrowserInstallationDiagnostic>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = GetProgramFilesRoots()
            .Select(root => Path.Combine(root, "Microsoft"))
            .Concat(profileRoots.Values.Take(MaximumUserProfiles)
                .Select(profileRoot => Path.Combine(profileRoot, "AppData", "Local", "Microsoft")))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        foreach (var (channelDirectory, channel) in EdgeChannelDirectories())
        {
            var executable = Path.Combine(root, channelDirectory, "Application", "msedge.exe");
            try
            {
                if (!File.Exists(executable) || !seenPaths.Add(Path.GetFullPath(executable))) continue;
                PathLinkSecurity.RejectLinks(executable);
                var image = ReadImage(executable);
                results.Add(new EdgeBrowserInstallationDiagnostic(channel, image.Architecture,
                    image.Version, SanitizeExecutablePath(executable, profileRoots)!, channel == "stable" &&
                    EdgePolicyRefreshCompatibility.IsSupportedVersion(image.Version) &&
                    IsStandardStablePath(executable)));
            }
            catch (Exception exception) when (IsDiagnosticReadFailure(exception))
            {
                warnings.Add("EDGE_INSTALLED_IMAGE_READ_FAILED");
            }
        }

        var ordered = results.OrderBy(item => ChannelOrder(item.Channel))
            .ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (ordered.Length > MaximumInstalledBrowsers) warnings.Add("EDGE_INSTALLED_BROWSER_LIST_TRUNCATED");
        return ordered.Take(MaximumInstalledBrowsers).ToArray();
    }

    private static EdgeBrowserProcessDiagnostic[] ReadRunningProcesses(
        IReadOnlyDictionary<string, string> profileRoots, List<string> warnings)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName("msedge"); }
        catch (Exception exception) when (IsDiagnosticReadFailure(exception))
        {
            warnings.Add("EDGE_PROCESS_ENUMERATION_FAILED");
            return [];
        }

        var results = new List<EdgeBrowserProcessDiagnostic>();
        try
        {
            foreach (var process in processes.Take(MaximumRunningProcesses))
            {
                using (process)
                {
                    var pid = SafeProcessValue(() => process.Id, 0);
                    if (pid <= 0)
                    {
                        warnings.Add("EDGE_PROCESS_IDENTITY_PARTIAL");
                        continue;
                    }
                    var sessionId = SafeProcessValue(() => process.SessionId, -1);
                    var path = TryGetProcessPath(process);
                    var userSid = TryGetProcessUserSid(process);
                    var integrity = TryGetProcessIntegrityLevel(process);
                    var started = TryGetProcessStartUtc(process);
                    var channel = ClassifyChannel(path);
                    string? version = null;
                    string architecture = "unknown";
                    DateTimeOffset? imageModifiedUtc = null;
                    string? errorCode = null;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        try
                        {
                            PathLinkSecurity.RejectLinks(path);
                            var image = ReadImage(path);
                            version = image.Version;
                            architecture = image.Architecture;
                            imageModifiedUtc = File.GetLastWriteTimeUtc(path);
                        }
                        catch (Exception exception) when (IsDiagnosticReadFailure(exception))
                        {
                            errorCode = "EDGE_PROCESS_IMAGE_UNREADABLE";
                        }
                    }
                    else errorCode = "EDGE_PROCESS_PATH_UNAVAILABLE";

                    var standardStable = path is not null && IsStandardStablePath(path);
                    var refreshSupported = channel == "stable" && standardStable &&
                                           EdgePolicyRefreshCompatibility.IsSupportedVersion(version);
                    bool? mayPrecedeUpdate = started is { } start && imageModifiedUtc is { } modified
                        ? start < modified
                        : null;
                    if (pid <= 0 || sessionId < 0)
                    {
                        warnings.Add("EDGE_PROCESS_IDENTITY_PARTIAL");
                        if (errorCode is null) errorCode = "EDGE_PROCESS_IDENTITY_PARTIAL";
                    }
                    results.Add(new EdgeBrowserProcessDiagnostic(pid,
                        sessionId >= 0 ? sessionId : null,
                        userSid, integrity, channel, architecture, version, SanitizeExecutablePath(path, profileRoots),
                        refreshSupported, started, imageModifiedUtc, mayPrecedeUpdate, errorCode));
                }
            }
            if (processes.Length > MaximumRunningProcesses) warnings.Add("EDGE_PROCESS_LIST_TRUNCATED");
        }
        finally
        {
            foreach (var process in processes.Skip(MaximumRunningProcesses)) process.Dispose();
        }
        return results.OrderBy(item => item.SessionId).ThenBy(item => item.ProcessId).ToArray();
    }

    private static EdgeUpdateServiceDiagnostic ReadUpdateService(string serviceName, List<string> warnings)
    {
        bool? installed = null;
        var startType = "unknown";
        string? imageVersion = null;
        try
        {
            var serviceRegistryPath = @"SYSTEM\CurrentControlSet\Services\" + serviceName;
            using var serviceKey = Registry.LocalMachine.OpenSubKey(serviceRegistryPath, writable: false);
            if (serviceKey is null)
                return new EdgeUpdateServiceDiagnostic(serviceName, false, "notInstalled", "unknown", null, null);

            installed = true;
            startType = ReadStartType(serviceKey);
            imageVersion = ReadServiceImageVersion(serviceKey);
            using var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager.IsInvalid)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            using var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service.IsInvalid)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(),
                    out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return new EdgeUpdateServiceDiagnostic(serviceName, true, MapServiceState(status.CurrentState),
                startType, imageVersion, null);
        }
        catch (Exception exception) when (IsDiagnosticReadFailure(exception))
        {
            warnings.Add("EDGE_UPDATE_SERVICE_QUERY_FAILED");
            return new EdgeUpdateServiceDiagnostic(serviceName, installed, "unavailable", startType,
                imageVersion, "EDGE_UPDATE_SERVICE_QUERY_FAILED");
        }
    }

    private static string ReadStartType(RegistryKey serviceKey) => serviceKey.GetValue("Start") is int start
        ? start switch
        {
            0 => "boot",
            1 => "system",
            2 => "automatic",
            3 => "manual",
            4 => "disabled",
            _ => "unknown"
        }
        : "unknown";

    private static string? ReadServiceImageVersion(RegistryKey serviceKey)
    {
        try
        {
            if (serviceKey.GetValue("ImagePath") is not string raw || raw.Length > 2048) return null;
            var path = ExtractExecutablePath(Environment.ExpandEnvironmentVariables(raw));
            if (path is null || !File.Exists(path)) return null;
            PathLinkSecurity.RejectLinks(path);
            return ReadImage(path).Version;
        }
        catch (Exception exception) when (IsDiagnosticReadFailure(exception)) { return null; }
    }

    private static string? ExtractExecutablePath(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0) return null;
        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }
        var match = Regex.Match(value, @"^(.+?\.exe)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static EdgeExecutorReportDiagnostic[] ReadExecutorReports(
        IReadOnlyDictionary<string, string> profileRoots, List<string> warnings)
    {
        var results = new List<EdgeExecutorReportDiagnostic>();
        foreach (var (sid, profileRoot) in profileRoots.Take(MaximumUserProfiles))
        {
            var directory = Path.Combine(profileRoot, "AppData", "Local", "VeyonCampus", "EdgePolicyExecutor");
            var statePath = Path.Combine(directory, "state.json");
            try
            {
                if (!File.Exists(statePath)) continue;
                PathLinkSecurity.RejectLinks(statePath);
                using var state = JsonDocument.Parse(ReadBoundedFile(statePath, MaximumExecutorStateBytes,
                    requireNonEmpty: true), JsonDocumentOptions);
                var root = state.RootElement;
                var schemaVersion = ReadInt32(root, "SchemaVersion");
                var requestId = ReadGuid(root, "RequestId");
                var revision = ReadInt64(root, "Revision");
                var status = ReadStatus(root, "Status");
                var browserVersion = ReadOptionalString(root, "BrowserVersion", 64);
                var attemptCount = ReadInt32(root, "AttemptCount");
                var updatedUtc = ReadDateTimeOffset(root, "UpdatedUtc");
                var errorCode = ReadOptionalString(root, "ErrorCode", 80);
                if (schemaVersion != 1 || requestId == Guid.Empty || revision <= 0 ||
                    attemptCount is < 0 or > 3 || updatedUtc.Offset != TimeSpan.Zero)
                    throw new InvalidDataException("Executor state fields are invalid.");
                var sessionId = ReadLatestExecutorSession(directory, requestId, revision);
                results.Add(new EdgeExecutorReportDiagnostic(sid, sessionId, requestId, revision,
                    status, browserVersion, attemptCount, updatedUtc, errorCode));
            }
            catch (Exception exception) when (IsDiagnosticReadFailure(exception) ||
                                             exception is JsonException or InvalidDataException or FormatException)
            {
                warnings.Add("EDGE_EXECUTOR_STATE_UNREADABLE");
            }
        }

        if (profileRoots.Count > MaximumUserProfiles) warnings.Add("EDGE_EXECUTOR_PROFILE_LIST_TRUNCATED");
        return results.OrderByDescending(item => item.UpdatedUtc).Take(MaximumUserProfiles).ToArray();
    }

    private static int? ReadLatestExecutorSession(string directory, Guid requestId, long revision)
    {
        var logPath = Path.Combine(directory, "edge-refresh.log");
        if (!File.Exists(logPath)) return null;
        PathLinkSecurity.RejectLinks(logPath);
        var text = Encoding.UTF8.GetString(ReadBoundedFile(logPath, MaximumExecutorLogBytes,
            requireNonEmpty: false));
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Reverse().Take(64))
        {
            try
            {
                using var entry = JsonDocument.Parse(line, JsonDocumentOptions);
                var root = entry.RootElement;
                if (ReadGuid(root, "requestId") != requestId || ReadInt64(root, "revision") != revision)
                    continue;
                var session = ReadInt32(root, "sessionId");
                return session > 0 ? session : null;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException) { }
        }
        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadProfiles(List<string> warnings)
    {
        var results = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var profileList = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", writable: false);
            if (profileList is null) return results;
            foreach (var sid in profileList.GetSubKeyNames().Where(IsProfileSid).Take(MaximumUserProfiles + 1))
            {
                using var profileKey = profileList.OpenSubKey(sid, writable: false);
                var rawProfileRoot = profileKey?.GetValue("ProfileImagePath") as string;
                if (string.IsNullOrWhiteSpace(rawProfileRoot)) continue;
                var profileRoot = Environment.ExpandEnvironmentVariables(rawProfileRoot);
                if (!Path.IsPathFullyQualified(profileRoot) || profileRoot.StartsWith(@"\\", StringComparison.Ordinal))
                    continue;
                profileRoot = Path.GetFullPath(profileRoot);
                if (Directory.Exists(profileRoot)) results[sid] = profileRoot;
            }
        }
        catch (Exception exception) when (IsDiagnosticReadFailure(exception))
        {
            warnings.Add("EDGE_PROFILE_LIST_UNAVAILABLE");
        }
        return results;
    }

    private static bool IsProfileSid(string sid) =>
        Regex.IsMatch(sid, @"^S-1-5-21-\d+-\d+-\d+-\d+$", RegexOptions.CultureInvariant);

    private static string? SanitizeExecutablePath(string? path,
        IReadOnlyDictionary<string, string> profileRoots)
    {
        if (path is null) return null;
        foreach (var profileRoot in profileRoots.Values.OrderByDescending(value => value.Length))
        {
            var prefix = profileRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return LimitDiagnosticPath("%USERPROFILE%" + path[(prefix.Length - 1)..]);
        }
        var appDataMarker = path.IndexOf("\\AppData\\Local\\Microsoft\\", StringComparison.OrdinalIgnoreCase);
        if (appDataMarker > 0) return LimitDiagnosticPath("%USERPROFILE%" + path[appDataMarker..]);
        var usersMarker = path.IndexOf("\\Users\\", StringComparison.OrdinalIgnoreCase);
        if (usersMarker >= 0)
        {
            var profileEnd = path.IndexOf('\\', usersMarker + "\\Users\\".Length);
            if (profileEnd > usersMarker)
                return LimitDiagnosticPath(path[..usersMarker] + "\\Users\\%USERPROFILE%" + path[profileEnd..]);
        }
        return LimitDiagnosticPath(path);
    }

    private static string LimitDiagnosticPath(string path) =>
        path.Length <= 2048 ? path : path[..2048];

    private static byte[] ReadBoundedFile(string path, int maximumBytes, bool requireNonEmpty)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximumBytes || requireNonEmpty && stream.Length == 0)
            throw new InvalidDataException("Executor diagnostic file size is invalid.");
        using var buffer = new MemoryStream((int)Math.Min(stream.Length, maximumBytes));
        var chunk = new byte[8192];
        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("Executor diagnostic file exceeds its size limit.");
            buffer.Write(chunk, 0, read);
        }
        if (requireNonEmpty && buffer.Length == 0)
            throw new InvalidDataException("Executor diagnostic file is empty.");
        return buffer.ToArray();
    }

    private static ProcessImage ReadImage(string path)
    {
        var versionInfo = FileVersionInfo.GetVersionInfo(path);
        var version = string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
            ? versionInfo.FileVersion
            : versionInfo.ProductVersion;
        return new ProcessImage(TrimTo(version, 64), ReadArchitecture(path));
    }

    private static string ReadArchitecture(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return "unknown";
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 64 || peOffset > stream.Length - 6) return "unknown";
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return "unknown";
            return reader.ReadUInt16() switch
            {
                0x014c => "x86",
                0x8664 => "x64",
                0xAA64 => "arm64",
                _ => "unknown"
            };
        }
        catch (Exception exception) when (IsDiagnosticReadFailure(exception) || exception is EndOfStreamException)
        {
            return "unknown";
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            var capacity = 32768;
            var path = new StringBuilder(capacity);
            return QueryFullProcessImageName(process.SafeHandle, 0, path, ref capacity) && capacity > 0
                ? path.ToString()
                : null;
        }
        catch (Exception exception) when (IsDiagnosticReadFailure(exception)) { return null; }
    }

    private static string? TryGetProcessUserSid(Process process)
    {
        try
        {
            if (!OpenProcessToken(process.SafeHandle, TokenQuery, out var token)) return null;
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                return identity.User?.Value;
        }
        catch (Exception exception) when (IsDiagnosticReadFailure(exception)) { return null; }
    }

    private static int? TryGetProcessIntegrityLevel(Process process)
    {
        try
        {
            if (!OpenProcessToken(process.SafeHandle, TokenQuery, out var token)) return null;
            using (token)
            {
                _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var requiredLength);
                if (requiredLength < Marshal.SizeOf<TokenMandatoryLabel>() || requiredLength > 4096) return null;
                var buffer = Marshal.AllocHGlobal((int)requiredLength);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, requiredLength, out _)) return null;
                    var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                    if (label.Label.Sid == IntPtr.Zero) return null;
                    var countPointer = GetSidSubAuthorityCount(label.Label.Sid);
                    if (countPointer == IntPtr.Zero) return null;
                    var count = Marshal.ReadByte(countPointer);
                    if (count == 0) return null;
                    var ridPointer = GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                    return ridPointer == IntPtr.Zero ? null : Marshal.ReadInt32(ridPointer);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch (Exception exception) when (IsDiagnosticReadFailure(exception)) { return null; }
    }

    private static DateTimeOffset? TryGetProcessStartUtc(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (Exception exception) when (IsDiagnosticReadFailure(exception)) { return null; }
    }

    private static T SafeProcessValue<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception exception) when (IsDiagnosticReadFailure(exception)) { return fallback; }
    }

    private static EdgePolicyRefreshStatus ReadStatus(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)) throw new InvalidDataException("Required executor state field is missing.");
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric) &&
            Enum.IsDefined(typeof(EdgePolicyRefreshStatus), numeric))
            return (EdgePolicyRefreshStatus)numeric;
        if (value.ValueKind == JsonValueKind.String &&
            Enum.TryParse<EdgePolicyRefreshStatus>(value.GetString(), ignoreCase: false, out var status) &&
            Enum.IsDefined(status))
            return status;
        throw new InvalidDataException("Executor state status is invalid.");
    }

    private static int ReadInt32(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result : throw new InvalidDataException("Required executor state number is invalid.");

    private static long ReadInt64(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result)
            ? result : throw new InvalidDataException("Required executor state number is invalid.");

    private static Guid ReadGuid(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out var result)
            ? result : throw new InvalidDataException("Required executor state identifier is invalid.");

    private static string? ReadOptionalString(JsonElement root, string propertyName, int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            text.Length > maximumLength || text.Any(char.IsControl))
            throw new InvalidDataException("Executor state text field is invalid.");
        return text;
    }

    private static DateTimeOffset ReadDateTimeOffset(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var result)
            ? result : throw new InvalidDataException("Executor state timestamp is invalid.");

    private static string[] GetProgramFilesRoots() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path))
         .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static (string Directory, string Channel)[] EdgeChannelDirectories() =>
    [
        ("Edge", "stable"),
        ("Edge Beta", "beta"),
        ("Edge Dev", "dev"),
        ("Edge SxS", "canary")
    ];

    private static int ChannelOrder(string channel) => channel switch
    {
        "stable" => 0,
        "beta" => 1,
        "dev" => 2,
        "canary" => 3,
        _ => 4
    };

    private static bool IsStandardStablePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return GetProgramFilesRoots().Any(root => string.Equals(fullPath,
            Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"),
            StringComparison.OrdinalIgnoreCase));
    }

    private static string MapServiceState(uint state) => state switch
    {
        1 => "stopped",
        2 => "startPending",
        3 => "stopPending",
        4 => "running",
        5 => "continuePending",
        6 => "pausePending",
        7 => "paused",
        _ => "unknown"
    };

    private static string? TrimTo(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximumLength)];

    private static bool IsDiagnosticReadFailure(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception or
            InvalidOperationException or NotSupportedException or ObjectDisposedException;

    private readonly record struct ProcessImage(string? Version, string Architecture);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeServiceHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        StringBuilder executableName, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName,
        int desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string serviceName,
        int desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle service, int infoLevel,
        out ServiceStatusProcess buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}

public static class EdgeBrowserDiagnosticsValidator
{
    private const int MaximumInstalledBrowsers = 64;
    private static readonly HashSet<string> Channels = new(StringComparer.Ordinal)
        { "stable", "beta", "dev", "canary", "unknown" };
    private static readonly HashSet<string> Architectures = new(StringComparer.Ordinal)
        { "x86", "x64", "arm64", "unknown" };
    private static readonly HashSet<string> UpdateServices = new(StringComparer.Ordinal)
        { "edgeupdate", "edgeupdatem", "MicrosoftEdgeElevationService" };
    private static readonly HashSet<string> ServiceStates = new(StringComparer.Ordinal)
        { "notInstalled", "unavailable", "stopped", "startPending", "stopPending", "running",
            "continuePending", "pausePending", "paused", "unknown" };
    private static readonly HashSet<string> ServiceStartTypes = new(StringComparer.Ordinal)
        { "boot", "system", "automatic", "manual", "disabled", "unknown" };
    private static readonly Regex SidPattern = new(@"^S-1-\d+(?:-\d+){1,15}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ErrorCodePattern = new(@"^[A-Z0-9_]{1,80}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void Validate(EdgeBrowserDiagnosticsSnapshot snapshot, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = nowUtc.ToUniversalTime();
        if (snapshot.SchemaVersion != 1 || snapshot.CollectedUtc.Offset != TimeSpan.Zero ||
            snapshot.CollectedUtc < now.AddMinutes(-10) || snapshot.CollectedUtc > now.AddMinutes(2) ||
            snapshot.PolicyRevision < 0 || snapshot.RefreshRevision < 0 || snapshot.InstalledBrowsers is null ||
            snapshot.InstalledBrowsers.Count > MaximumInstalledBrowsers || snapshot.RunningProcesses is null ||
            snapshot.RunningProcesses.Count > 128 || snapshot.UpdateServices is null ||
            snapshot.UpdateServices.Count > 3 || snapshot.ExecutorReports is null ||
            snapshot.ExecutorReports.Count > 32 || snapshot.Warnings is null || snapshot.Warnings.Count > 8 ||
            snapshot.Warnings.Any(warning => warning is null || !IsErrorCode(warning)))
            throw new InvalidDataException("Edge 浏览器诊断快照字段无效。");

        if (snapshot.RefreshRequestId == Guid.Empty)
        {
            if (snapshot.RefreshRevision != 0 || snapshot.RefreshRequestedUtc is not null)
                throw new InvalidDataException("空 Edge 刷新任务带有请求时间。");
        }
        else if (snapshot.RefreshRequestedUtc is not { Offset: var refreshOffset } refreshUtc ||
                 snapshot.RefreshRevision <= 0 || refreshOffset != TimeSpan.Zero || refreshUtc > now.AddMinutes(2))
            throw new InvalidDataException("Edge 刷新任务标识或时间无效。");

        foreach (var installation in snapshot.InstalledBrowsers)
        {
            if (installation is null || !Channels.Contains(installation.Channel) ||
                !Architectures.Contains(installation.Architecture) ||
                !IsOptionalVersion(installation.ImageFileVersion) ||
                !IsPath(installation.ExecutablePath) ||
                installation.RefreshSupported &&
                    (installation.Channel != "stable" ||
                     !EdgePolicyRefreshCompatibility.IsSupportedVersion(installation.ImageFileVersion)))
                throw new InvalidDataException("Edge 安装诊断条目无效。");
        }

        foreach (var process in snapshot.RunningProcesses)
        {
            if (process is null || process.ProcessId <= 0 || process.SessionId is < 0 ||
                process.UserSid is { } sid && !SidPattern.IsMatch(sid) ||
                process.IntegrityLevelRid is < 0 ||
                !Channels.Contains(process.Channel) || !Architectures.Contains(process.Architecture) ||
                !IsOptionalVersion(process.ImageFileVersion) ||
                process.ExecutablePath is not null && !IsPath(process.ExecutablePath) ||
                process.StartedUtc is { Offset: var startedOffset } && startedOffset != TimeSpan.Zero ||
                process.ImageModifiedUtc is { Offset: var modifiedOffset } && modifiedOffset != TimeSpan.Zero ||
                process.RefreshSupported &&
                    (process.Channel != "stable" ||
                     !EdgePolicyRefreshCompatibility.IsSupportedVersion(process.ImageFileVersion)) ||
                process.ErrorCode is not null && !IsErrorCode(process.ErrorCode))
                throw new InvalidDataException("Edge 运行进程诊断条目无效。");
        }

        if (snapshot.UpdateServices.Any(service => service is null) ||
            snapshot.ExecutorReports.Any(report => report is null))
            throw new InvalidDataException("Edge 更新服务或执行器诊断条目为空。");

        if (snapshot.UpdateServices.Select(service => service.Name).Distinct(StringComparer.Ordinal).Count() !=
            snapshot.UpdateServices.Count)
            throw new InvalidDataException("Edge 更新服务诊断条目重复。");
        foreach (var service in snapshot.UpdateServices)
        {
            if (service is null || !UpdateServices.Contains(service.Name) ||
                !ServiceStates.Contains(service.State) || !ServiceStartTypes.Contains(service.StartType) ||
                !IsOptionalVersion(service.ImageFileVersion) ||
                service.ErrorCode is not null && !IsErrorCode(service.ErrorCode) ||
                service.Installed is true && service.State == "notInstalled" ||
                service.Installed is false && service.State != "notInstalled" ||
                service.Installed is null && service.State != "unavailable")
                throw new InvalidDataException("Edge 更新服务诊断条目无效。");
        }

        if (snapshot.ExecutorReports.Select(report => report.UserSid)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.ExecutorReports.Count)
            throw new InvalidDataException("Edge 执行器诊断条目重复。");
        foreach (var report in snapshot.ExecutorReports)
        {
            if (report is null || !IsSid(report.UserSid) ||
                report.SessionId is < 0 || report.RequestId == Guid.Empty || report.Revision <= 0 ||
                !Enum.IsDefined(report.Status) || !IsOptionalVersion(report.BrowserVersion) ||
                report.AttemptCount is < 0 or > 3 || report.UpdatedUtc.Offset != TimeSpan.Zero ||
                report.UpdatedUtc > now.AddMinutes(2) ||
                report.ErrorCode is not null && !IsErrorCode(report.ErrorCode))
                throw new InvalidDataException("Edge 执行器诊断条目无效。");
        }
    }

    private static bool IsPath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 2048 &&
        !path.Any(char.IsControl);

    private static bool IsOptionalVersion(string? version) =>
        version is null || version.Length is > 0 and <= 64 && !version.Any(char.IsControl);

    private static bool IsSid(string? sid) => sid is { Length: <= 184 } && SidPattern.IsMatch(sid);

    private static bool IsErrorCode(string value) => ErrorCodePattern.IsMatch(value);
}
