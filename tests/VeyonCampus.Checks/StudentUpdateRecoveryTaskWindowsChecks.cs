using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using VeyonCampus.Core;

internal static class StudentUpdateRecoveryTaskWindowsChecks
{
    private const string BootRecoveryTask = "VeyonCampus-StudentUpdate-Recovery";
    private const string WatchdogRecoveryTask = "VeyonCampus-StudentUpdate-Watchdog";
    private const string ReportPrefix = "STUDENT_RECOVERY_TASK_REPORT=";

    [SupportedOSPlatform("windows")]
    public static void Run(string campusId)
    {
        var evidence = new FixtureEvidence();
        var context = new FixtureContext();
        Exception? failure = null;
        try
        {
            RunAdminFixture(campusId, evidence, context);
            evidence.Status = "passed";
            evidence.FailedStage = null;
        }
        catch (Exception exception)
        {
            failure = exception;
            evidence.Status = "failed";
            evidence.FailureType = exception.GetType().Name;
            evidence.FailureHResult = FormatHResult(exception);
        }
        finally
        {
            try
            {
                Cleanup(context);
            }
            catch (Exception exception)
            {
                evidence.CleanupFailureType = exception.GetType().Name;
                if (failure is null)
                {
                    failure = exception;
                    evidence.Status = "failed";
                    evidence.FailedStage = "cleanup";
                    evidence.FailureType = exception.GetType().Name;
                    evidence.FailureHResult = FormatHResult(exception);
                }
            }
        }

        WriteReport(evidence);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [SupportedOSPlatform("windows")]
    public static void RunAsSystem(string requestPath)
    {
        var evidence = new SystemTaskEvidence();
        FixtureRequest? request = null;
        string? resultPath = null;
        var recoveryRegistrationAttempted = false;
        Exception? failure = null;
        try
        {
            evidence.FailedStage = "system-identity";
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User?.Value != "S-1-5-18")
                throw new UnauthorizedAccessException("Recovery task fixture process did not run as SYSTEM.");
            evidence.Checks["systemIdentity"] = true;

            evidence.FailedStage = "read-protected-request";
            resultPath = GetFixtureResultPath(requestPath);
            request = ReadRequest(requestPath);
            ValidateRequestPaths(request, requestPath);
            if (!PathEquals(resultPath, request.ResultPath))
                throw new InvalidDataException("SYSTEM fixture result path is not bound to its protected request directory.");

            evidence.FailedStage = "register-and-readback";
            recoveryRegistrationAttempted = true;
            StudentUpdateAtomicTransaction.RegisterRecoveryTasks(request.ConfigPath, request.AgentExecutablePath);
            VerifyRecoveryTask(BootRecoveryTask, request, "BootTrigger", "bootTask", evidence.Checks);
            VerifyRecoveryTask(WatchdogRecoveryTask, request, "CalendarTrigger", "watchdogTask", evidence.Checks);
            evidence.Checks["recoveryTasksRegisteredBySystem"] = true;

            evidence.FailedStage = "health-repair-update-lock-exclusion";
            CheckHealthRepairUpdateLockContention(request.ConfigPath);
            evidence.Checks["healthRepairUpdateLockRejected"] = true;

            evidence.FailedStage = "remove-and-readback";
            WebsitePolicyAgentInstaller.RemoveStudentUpdateRecoveryTasksForConfig(request.ConfigPath);
            if (WebsitePolicyAgentInstaller.HasStudentUpdateRecoveryTasks())
                throw new InvalidDataException("Recovery task fixture left an update recovery task registered.");
            evidence.Checks["recoveryTasksRemoved"] = true;
            evidence.Status = "passed";
            evidence.FailedStage = null;
        }
        catch (Exception exception)
        {
            failure = exception;
            evidence.Status = "failed";
            evidence.FailureType = exception.GetType().Name;
            evidence.FailureHResult = FormatHResult(exception);
        }
        finally
        {
            if (request is not null && recoveryRegistrationAttempted)
            {
                try
                {
                    WebsitePolicyAgentInstaller.RemoveStudentUpdateRecoveryTasksForConfig(request.ConfigPath);
                    evidence.Checks["recoveryTasksRemoved"] =
                        !WebsitePolicyAgentInstaller.HasStudentUpdateRecoveryTasks();
                }
                catch (Exception exception)
                {
                    evidence.CleanupFailureType = exception.GetType().Name;
                    if (failure is null)
                    {
                        failure = exception;
                        evidence.Status = "failed";
                        evidence.FailedStage = "remove-and-readback";
                        evidence.FailureType = exception.GetType().Name;
                        evidence.FailureHResult = FormatHResult(exception);
                    }
                }
            }

            if (resultPath is not null) WriteSystemTaskResult(resultPath, evidence);
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [SupportedOSPlatform("windows")]
    private static void RunAdminFixture(string campusId, FixtureEvidence evidence, FixtureContext context)
    {
        evidence.FailedStage = "preflight";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Student recovery task fixture only runs on Windows.");
        evidence.Checks["windowsPlatform"] = true;
        if (Environment.GetEnvironmentVariable("VEYONCAMPUS_ALLOW_AGENT_SYSTEM_FIXTURE") != "1")
            throw new InvalidOperationException("The SYSTEM recovery task fixture requires the disposable-runner opt-in.");

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Student recovery task fixture requires an elevated Windows process.");
        evidence.Checks["elevatedRunner"] = true;

        evidence.FailedStage = "installed-agent-preflight";
        var configPath = GetConfigurationPath(campusId);
        context.ConfigPath = configPath;
        var agentPath = WebsitePolicyAgentInstaller.InstalledExecutablePath;
        if (!File.Exists(configPath) || !File.Exists(agentPath))
            throw new FileNotFoundException("The first disposable Agent deployment is not available to the task fixture.");
        if (WebsitePolicyAgentInstaller.HasStudentUpdateRecoveryTasks())
            throw new IOException("An update recovery task already exists; the fixture made no task changes.");
        if (!PathEquals(WebsitePolicyAgentInstaller.ReadManagedAgentExecutablePath(configPath), agentPath))
            throw new InvalidDataException("The installed SYSTEM Agent action does not match the fixture configuration.");
        evidence.Checks["existingAgentTaskReadback"] = true;

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        context.FixtureParent = Path.Combine(programData, "VeyonCampus", "StudentUpdateTaskFixtures");
        context.FixtureParentExisted = Directory.Exists(context.FixtureParent);
        if (File.Exists(context.FixtureParent))
            throw new IOException("The SYSTEM task fixture parent path is occupied by a file.");
        if (context.FixtureParentExisted)
        {
            if ((File.GetAttributes(context.FixtureParent) & FileAttributes.ReparsePoint) != 0 ||
                !IsPrivate(context.FixtureParent, directory: true))
                throw new UnauthorizedAccessException("The existing SYSTEM task fixture parent is not a protected directory.");
        }
        else
        {
            Directory.CreateDirectory(context.FixtureParent);
            SecureAgentPath("SecurePrivateDirectory", context.FixtureParent);
        }
        context.FixtureRoot = Path.Combine(context.FixtureParent, Guid.NewGuid().ToString("N"));
        if (Directory.Exists(context.FixtureRoot) || File.Exists(context.FixtureRoot))
            throw new IOException("The unique SYSTEM task fixture directory is unexpectedly occupied.");

        evidence.FailedStage = "create-protected-request";
        Directory.CreateDirectory(context.FixtureRoot);
        SecureAgentPath("SecurePrivateDirectory", context.FixtureRoot);
        var requestPath = Path.Combine(context.FixtureRoot, "request.json");
        context.ResultPath = Path.Combine(context.FixtureRoot, "result.json");
        var request = new FixtureRequest
        {
            SchemaVersion = 1,
            AgentExecutablePath = agentPath,
            ConfigPath = configPath,
            ResultPath = context.ResultPath!
        };
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request), new UTF8Encoding(false));
        SecureAgentPath("SecurePrivateFile", requestPath);
        var systemTaskLauncherPath = CreateSystemTaskLauncher(requestPath);
        evidence.Checks["requestAclReadback"] = IsPrivate(requestPath, directory: false) &&
                                                   IsPrivate(systemTaskLauncherPath, directory: false) &&
                                                   IsPrivate(context.FixtureRoot, directory: true);
        if (!evidence.Checks["requestAclReadback"])
            throw new UnauthorizedAccessException("The SYSTEM fixture request and launcher ACLs did not read back as private.");

        evidence.FailedStage = "create-and-run-system-task";
        context.TaskName = "VeyonCampus-StudentUpdateFixture-" + Guid.NewGuid().ToString("N");
        context.TaskCreationAttempted = true;
        context.RecoveryCleanupRequired = true;
        CreateSystemTask(context.TaskName, systemTaskLauncherPath);
        RunSystemTask(context.TaskName);
        evidence.Checks["systemTaskScheduled"] = true;

        evidence.FailedStage = "wait-for-system-report";
        var systemEvidence = WaitForSystemTaskResult(context.ResultPath, TimeSpan.FromSeconds(60));
        foreach (var pair in systemEvidence.Checks)
            if (evidence.Checks.ContainsKey(pair.Key)) evidence.Checks[pair.Key] = pair.Value;
        evidence.Checks["systemChildPassed"] = systemEvidence.Status == "passed";
        if (systemEvidence.Status != "passed")
        {
            evidence.SystemChildFailedStage = systemEvidence.FailedStage;
            evidence.SystemChildFailureType = systemEvidence.FailureType;
            evidence.SystemChildFailureHResult = systemEvidence.FailureHResult;
            throw new InvalidOperationException("SYSTEM recovery task readback failed at " +
                                                (systemEvidence.FailedStage ?? "unknown") + " (" +
                                                (systemEvidence.FailureType ?? "unknown") + ").");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string CreateSystemTaskLauncher(string requestPath)
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("The Windows test executable path is unavailable.");
        var scriptPath = Path.Combine(Path.GetDirectoryName(requestPath)!, "run-system-fixture.ps1");
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "$processPath = '" + EscapePowerShellSingleQuoted(processPath) + "'"
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location
                               ?? throw new InvalidOperationException("The test assembly path is unavailable.");
            lines.Add("$assemblyPath = '" + EscapePowerShellSingleQuoted(assemblyPath) + "'");
            lines.Add("$requestPath = Join-Path $PSScriptRoot 'request.json'");
            lines.Add("& $processPath $assemblyPath --student-update-recovery-task-fixture $requestPath");
        }
        else
        {
            lines.Add("$requestPath = Join-Path $PSScriptRoot 'request.json'");
            lines.Add("& $processPath --student-update-recovery-task-fixture $requestPath");
        }
        lines.Add("exit $LASTEXITCODE");
        File.WriteAllText(scriptPath, string.Join(Environment.NewLine, lines) + Environment.NewLine,
            new UTF8Encoding(false));
        SecureAgentPath("SecurePrivateFile", scriptPath);
        return scriptPath;
    }

    [SupportedOSPlatform("windows")]
    private static void CreateSystemTask(string taskName, string launcherScriptPath)
    {
        var powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var commandLine = QuoteWindowsArgument(powershellPath) +
                          " -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                          QuoteWindowsArgument(launcherScriptPath);
        if (commandLine.Length > 260)
            throw new InvalidDataException("The short SYSTEM task launcher still exceeds the Windows Task Scheduler action limit.");

        var scheduled = DateTime.Now.AddMinutes(5);
        var runner = new ProcessRunner();
        runner.Run("schtasks.exe", ["/Create", "/TN", taskName, "/SC", "ONCE", "/SD",
                scheduled.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture), "/ST",
                scheduled.ToString("HH:mm", CultureInfo.InvariantCulture), "/RU", "SYSTEM", "/RL", "HIGHEST",
                "/TR", commandLine, "/F"], Environment.CurrentDirectory, TimeSpan.FromSeconds(30));
        if (runner.ExitCode != 0)
            throw CreateTaskSchedulerFailure("Could not create the one-time SYSTEM recovery task fixture", runner);
    }

    [SupportedOSPlatform("windows")]
    private static void RunSystemTask(string taskName)
    {
        var runner = new ProcessRunner();
        runner.Run("schtasks.exe", ["/Run", "/TN", taskName], Environment.CurrentDirectory,
            TimeSpan.FromSeconds(30));
        if (runner.ExitCode != 0)
            throw CreateTaskSchedulerFailure("Could not start the one-time SYSTEM recovery task fixture", runner);
    }

    private static IOException CreateTaskSchedulerFailure(string action, ProcessRunner runner)
    {
        static string Clip(string output)
        {
            const int limit = 512;
            var normalized = output.Trim().Replace('\r', ' ').Replace('\n', ' ');
            return normalized.Length <= limit ? normalized : normalized[..limit] + "…";
        }

        var stdout = Clip(runner.Stdout);
        var stderr = Clip(runner.Stderr);
        var details = string.Join("; ", new[]
        {
            $"exit={runner.ExitCode}",
            string.IsNullOrWhiteSpace(stdout) ? null : $"stdout={stdout}",
            string.IsNullOrWhiteSpace(stderr) ? null : $"stderr={stderr}"
        }.Where(value => value is not null));
        return new IOException($"{action} ({details}).");
    }

    [SupportedOSPlatform("windows")]
    private static SystemTaskEvidence WaitForSystemTaskResult(string resultPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(resultPath))
            {
                try
                {
                    return JsonSerializer.Deserialize<SystemTaskEvidence>(File.ReadAllBytes(resultPath))
                           ?? throw new InvalidDataException("SYSTEM fixture report is empty.");
                }
                catch (JsonException)
                {
                    // The child commits its result by renaming a protected temporary file.
                }
            }
            Thread.Sleep(200);
        }
        throw new TimeoutException("SYSTEM recovery task fixture did not write its bounded result report.");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyRecoveryTask(string taskName, FixtureRequest request, string triggerName,
        string checkPrefix, Dictionary<string, bool> checks)
    {
        var action = ReadTaskAction(taskName);
        var expectedArguments = "--recover-student-update " + QuoteWindowsArgument(request.ConfigPath);
        if (!string.Equals(action.UserId, "S-1-5-18", StringComparison.Ordinal) ||
            !PathEquals(action.ExecutablePath, request.AgentExecutablePath) ||
            !string.Equals(action.Arguments, expectedArguments, StringComparison.Ordinal))
            throw new InvalidDataException($"{taskName} action does not bind SYSTEM, the installed Agent, and the fixture configuration.");
        checks[checkPrefix + "Action"] = true;

        var acl = HasRestrictedTaskAcl(taskName);
        checks[checkPrefix + "Acl"] = acl;
        if (!acl)
            throw new UnauthorizedAccessException($"{taskName} task ACL is not restricted to SYSTEM and Administrators.");

        var runner = new ProcessRunner();
        runner.Run("schtasks.exe", ["/Query", "/TN", taskName, "/XML"], Environment.CurrentDirectory,
            TimeSpan.FromSeconds(20));
        if (runner.ExitCode != 0) throw new IOException($"Could not read back {taskName} trigger definition.");
        var xml = XDocument.Parse(runner.Stdout);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var trigger = xml.Descendants(ns + triggerName).FirstOrDefault();
        var triggerFound = trigger is not null;
        var triggerCheck = checkPrefix == "bootTask" ? "bootTrigger" : "watchdogTrigger";
        checks[triggerCheck] = triggerFound;
        if (!triggerFound) throw new InvalidDataException($"{taskName} has no expected {triggerName}.");
        if (checkPrefix == "watchdogTask")
        {
            var oneMinuteInterval = trigger!.Descendants(ns + "Interval").Any(element =>
                string.Equals(element.Value, "PT1M", StringComparison.OrdinalIgnoreCase));
            checks["watchdogOneMinuteSchedule"] = oneMinuteInterval;
            if (!oneMinuteInterval)
                throw new InvalidDataException($"{taskName} does not repeat at a one-minute interval.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static ScheduledTaskAction ReadTaskAction(string taskName)
    {
        var method = typeof(WebsitePolicyAgentInstaller).GetMethod("ReadScheduledTaskAction",
            BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new MissingMethodException("Student recovery task action reader was not found.");
        var value = Invoke(method, taskName)
                    ?? throw new InvalidDataException($"{taskName} was not registered.");
        var type = value.GetType();
        return new ScheduledTaskAction(
            type.GetProperty("UserId")?.GetValue(value) as string ?? "",
            type.GetProperty("ExecutablePath")?.GetValue(value) as string ?? "",
            type.GetProperty("Arguments")?.GetValue(value) as string ?? "");
    }

    [SupportedOSPlatform("windows")]
    private static bool HasRestrictedTaskAcl(string taskName)
    {
        var method = typeof(WebsitePolicyAgentInstaller).GetMethod("HasRestrictedScheduledTaskAcl",
            BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new MissingMethodException("Student recovery task ACL reader was not found.");
        return (bool)(Invoke(method, taskName) ?? false);
    }

    private static object? Invoke(MethodInfo method, params object[] arguments)
    {
        try { return method.Invoke(null, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static FixtureRequest ReadRequest(string requestPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Student recovery task fixture only runs on Windows.");
        var fullPath = Path.GetFullPath(requestPath);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var expectedRoot = Path.Combine(programData, "VeyonCampus", "StudentUpdateTaskFixtures");
        var prefix = Path.TrimEndingDirectorySeparator(expectedRoot) + Path.DirectorySeparatorChar;
        var parent = Path.GetDirectoryName(fullPath);
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || parent is null ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
            !IsPrivate(fullPath, directory: false) || !IsPrivate(parent, directory: true))
            throw new InvalidDataException("SYSTEM fixture request is outside its protected, fixed test directory.");
        var request = JsonSerializer.Deserialize<FixtureRequest>(File.ReadAllBytes(fullPath))
                      ?? throw new InvalidDataException("SYSTEM fixture request is invalid.");
        if (request.SchemaVersion != 1) throw new InvalidDataException("SYSTEM fixture request schema is unsupported.");
        return request;
    }

    private static string GetFixtureResultPath(string requestPath)
    {
        var fullPath = Path.GetFullPath(requestPath);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var expectedRoot = Path.Combine(programData, "VeyonCampus", "StudentUpdateTaskFixtures");
        var prefix = Path.TrimEndingDirectorySeparator(expectedRoot) + Path.DirectorySeparatorChar;
        var parent = Path.GetDirectoryName(fullPath);
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || parent is null ||
            !Directory.Exists(parent) || !IsPrivate(parent, directory: true))
            throw new InvalidDataException("SYSTEM fixture result path is outside its protected test directory.");
        return Path.Combine(parent, "result.json");
    }

    private static void ValidateRequestPaths(FixtureRequest request, string requestPath)
    {
        var configPath = Path.GetFullPath(request.ConfigPath);
        var agentPath = Path.GetFullPath(request.AgentExecutablePath);
        var resultPath = Path.GetFullPath(request.ResultPath);
        if (!PathEquals(agentPath, WebsitePolicyAgentInstaller.InstalledExecutablePath) ||
            !PathEquals(configPath, GetConfigurationPathFromConfig(configPath)) ||
            !PathEquals(Path.GetDirectoryName(resultPath)!, Path.GetDirectoryName(Path.GetFullPath(requestPath))!) ||
            !Path.GetFileName(resultPath).Equals("result.json", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(agentPath) || !File.Exists(configPath))
            throw new InvalidDataException("SYSTEM fixture request paths do not match the installed disposable Agent.");
        if ((File.GetAttributes(agentPath) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("SYSTEM fixture request references a reparse point.");
    }

    [SupportedOSPlatform("windows")]
    private static void CheckHealthRepairUpdateLockContention(string configPath)
    {
        var config = JsonSerializer.Deserialize<WebsitePolicyAgentConfig>(File.ReadAllBytes(configPath),
                         WebsitePolicyAgent.JsonOptions)
                     ?? throw new InvalidDataException("Installed Agent config could not be read for update-lock fixture.");
        var now = DateTimeOffset.UtcNow;
        var command = new StudentAgentHealthRepairCommand(1,
            StudentAgentHealthRepairCommandCryptography.Purpose, config.CampusId, Guid.NewGuid(), Guid.NewGuid(),
            now.AddSeconds(-1), now.AddSeconds(30), StudentAgentHealthRepairAction.EnsureVeyonServiceRunning);

        using var updateLock = StudentUpdateAtomicTransaction.AcquireMachineLock(TimeSpan.Zero);
        var receipt = Task.Run(() => StudentAgentHealthRepairProcessor.Process(command, configPath, config,
                new StudentAgentEdgePolicyRefreshSignalState()))
            .GetAwaiter().GetResult();
        if (receipt.Outcome != StudentAgentHealthRepairOutcome.BusyUpdating)
            throw new InvalidDataException("Health repair was not rejected while the atomic update mutex was held.");
        if (new StudentAgentHealthRepairLedger(configPath).FindCommand(command.CommandId) is not null)
            throw new InvalidDataException("Rejected health repair wrote a persistent command receipt.");
    }

    private static string GetConfigurationPath(string campusId)
    {
        if (string.IsNullOrWhiteSpace(campusId)) throw new InvalidDataException("Fixture campus ID is empty.");
        var hash = CampusHash(campusId);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "WebsitePolicy", hash, "agent-" + hash + ".json");
    }

    private static string GetConfigurationPathFromConfig(string configPath)
    {
        var full = Path.GetFullPath(configPath);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VeyonCampus", "WebsitePolicy");
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SYSTEM fixture configuration path is outside the fixed Agent configuration root.");
        var relative = Path.GetRelativePath(root, full);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0].Length != 24 ||
            !parts[1].Equals("agent-" + parts[0] + ".json", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("SYSTEM fixture configuration path is malformed.");
        return full;
    }

    private static string CampusHash(string campusId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(campusId)))[..24];

    private static string EscapePowerShellSingleQuoted(string value) => value.Replace("'", "''");

    private static bool IsPrivate(string path, bool directory)
    {
        var type = typeof(StudentUpdateAtomicTransaction).Assembly.GetType("VeyonCampus.Core.AgentFileSecurity",
            throwOnError: true)!;
        var method = type.GetMethod("IsPrivate", BindingFlags.Public | BindingFlags.Static)
                     ?? throw new MissingMethodException("Agent private ACL verifier was not found.");
        return (bool)(Invoke(method, path, directory) ?? false);
    }

    private static void SecureAgentPath(string methodName, string path)
    {
        var type = typeof(StudentUpdateAtomicTransaction).Assembly.GetType("VeyonCampus.Core.AgentFileSecurity",
            throwOnError: true)!;
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
                     ?? throw new MissingMethodException($"Agent ACL method {methodName} was not found.");
        _ = Invoke(method, path);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsScheduledTaskPresent(string taskName)
    {
        var method = typeof(WebsitePolicyAgentInstaller).GetMethod("IsScheduledTaskPresent",
            BindingFlags.NonPublic | BindingFlags.Static, binder: null, types: [typeof(string)], modifiers: null)
                     ?? throw new MissingMethodException("Student scheduled task presence reader was not found.");
        return (bool)(Invoke(method, taskName) ?? false);
    }

    [SupportedOSPlatform("windows")]
    private static void Cleanup(FixtureContext context)
    {
        Exception? cleanupFailure = null;
        var taskStillPresent = false;
        if (context.TaskCreationAttempted && context.TaskName is not null)
        {
            try
            {
                taskStillPresent = IsScheduledTaskPresent(context.TaskName);
                if (taskStillPresent)
                {
                    var runner = new ProcessRunner();
                    runner.Run("schtasks.exe", ["/End", "/TN", context.TaskName], Environment.CurrentDirectory,
                        TimeSpan.FromSeconds(20));
                    runner.Run("schtasks.exe", ["/Delete", "/TN", context.TaskName, "/F"],
                        Environment.CurrentDirectory, TimeSpan.FromSeconds(20));
                    if (runner.ExitCode != 0)
                        throw new IOException("Temporary SYSTEM task fixture could not be deleted.");
                    taskStillPresent = IsScheduledTaskPresent(context.TaskName);
                    if (taskStillPresent)
                        throw new IOException("Temporary SYSTEM task fixture remained after deletion.");
                }
            }
            catch (Exception exception)
            {
                taskStillPresent = true;
                cleanupFailure = exception;
            }
        }

        if (context.RecoveryCleanupRequired && context.ConfigPath is not null)
        {
            try { WebsitePolicyAgentInstaller.RemoveStudentUpdateRecoveryTasksForConfig(context.ConfigPath); }
            catch (Exception exception) { cleanupFailure ??= exception; }
        }

        if (!taskStillPresent && context.FixtureRoot is not null && Directory.Exists(context.FixtureRoot))
            DeleteFixturePath(context.FixtureRoot);
        if (!taskStillPresent && context.FixtureParent is not null && !context.FixtureParentExisted &&
            Directory.Exists(context.FixtureParent) &&
            !Directory.EnumerateFileSystemEntries(context.FixtureParent).Any())
            Directory.Delete(context.FixtureParent);

        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private static void WriteSystemTaskResult(string resultPath, SystemTaskEvidence evidence)
    {
        var temporaryPath = resultPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(evidence), new UTF8Encoding(false));
            SecureAgentPath("SecurePrivateFile", temporaryPath);
            File.Move(temporaryPath, resultPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void DeleteFixturePath(string path)
    {
        RejectReparsePoints(path);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void RejectReparsePoints(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to remove a reparse point in the SYSTEM task fixture.");
        if (!Directory.Exists(path)) return;
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) RejectReparsePoints(child);
    }

    private static string QuoteWindowsArgument(string value)
    {
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                builder.Append('\\', checked(backslashes * 2 + 1)).Append('"');
                backslashes = 0;
                continue;
            }
            if (backslashes > 0) builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(character);
        }
        if (backslashes > 0) builder.Append('\\', checked(backslashes * 2));
        return builder.Append('"').ToString();
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string FormatHResult(Exception exception) => $"0x{unchecked((uint)exception.HResult):X8}";

    private static void WriteReport(FixtureEvidence evidence)
    {
        var report = new
        {
            schemaVersion = 1,
            kind = "VeyonCampus.StudentUpdateRecoveryTaskFixture",
            status = evidence.Status,
            scope = "system-recovery-task-registration-and-readback-only",
            g0Acceptance = false,
            failedStage = evidence.FailedStage,
            failureType = evidence.FailureType,
            failureHResult = evidence.FailureHResult,
            cleanupFailureType = evidence.CleanupFailureType,
            systemChildFailedStage = evidence.SystemChildFailedStage,
            systemChildFailureType = evidence.SystemChildFailureType,
            systemChildFailureHResult = evidence.SystemChildFailureHResult,
            environment = new
            {
                osVersion = Environment.OSVersion.Version.ToString(),
                osVersionString = Environment.OSVersion.VersionString,
                githubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
                runnerImageOs = Environment.GetEnvironmentVariable("ImageOS"),
                runnerImageVersion = Environment.GetEnvironmentVariable("ImageVersion"),
                runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                commit = Environment.GetEnvironmentVariable("GITHUB_SHA")
            },
            checks = evidence.Checks
        };
        Console.WriteLine(ReportPrefix + JsonSerializer.Serialize(report));
    }

    private sealed class FixtureContext
    {
        public string? ConfigPath { get; set; }
        public string? FixtureParent { get; set; }
        public string? FixtureRoot { get; set; }
        public string? ResultPath { get; set; }
        public string? TaskName { get; set; }
        public bool FixtureParentExisted { get; set; }
        public bool TaskCreationAttempted { get; set; }
        public bool RecoveryCleanupRequired { get; set; }
    }

    private sealed class FixtureEvidence
    {
        public string Status { get; set; } = "failed";
        public string? FailedStage { get; set; } = "initialization";
        public string? FailureType { get; set; }
        public string? FailureHResult { get; set; }
        public string? CleanupFailureType { get; set; }
        public string? SystemChildFailedStage { get; set; }
        public string? SystemChildFailureType { get; set; }
        public string? SystemChildFailureHResult { get; set; }
        public Dictionary<string, bool> Checks { get; } = new(StringComparer.Ordinal)
        {
            ["windowsPlatform"] = false,
            ["elevatedRunner"] = false,
            ["existingAgentTaskReadback"] = false,
            ["requestAclReadback"] = false,
            ["systemTaskScheduled"] = false,
            ["systemIdentity"] = false,
            ["bootTaskAction"] = false,
            ["watchdogTaskAction"] = false,
            ["bootTaskAcl"] = false,
            ["watchdogTaskAcl"] = false,
            ["bootTrigger"] = false,
            ["watchdogTrigger"] = false,
            ["watchdogOneMinuteSchedule"] = false,
            ["recoveryTasksRegisteredBySystem"] = false,
            ["recoveryTasksRemoved"] = false,
            ["systemChildPassed"] = false,
            ["healthRepairUpdateLockRejected"] = false
        };
    }

    private sealed class SystemTaskEvidence
    {
        public SystemTaskEvidence() { }

        public string Status { get; set; } = "failed";
        public string? FailedStage { get; set; } = "initialization";
        public string? FailureType { get; set; }
        public string? FailureHResult { get; set; }
        public string? CleanupFailureType { get; set; }
        public Dictionary<string, bool> Checks { get; set; } = new(StringComparer.Ordinal)
        {
            ["systemIdentity"] = false,
            ["bootTaskAction"] = false,
            ["watchdogTaskAction"] = false,
            ["bootTaskAcl"] = false,
            ["watchdogTaskAcl"] = false,
            ["bootTrigger"] = false,
            ["watchdogTrigger"] = false,
            ["recoveryTasksRegisteredBySystem"] = false,
            ["recoveryTasksRemoved"] = false,
            ["healthRepairUpdateLockRejected"] = false
        };
    }

    private sealed class FixtureRequest
    {
        public FixtureRequest() { }

        public int SchemaVersion { get; set; }
        public string AgentExecutablePath { get; set; } = "";
        public string ConfigPath { get; set; } = "";
        public string ResultPath { get; set; } = "";
    }

    private sealed record ScheduledTaskAction(string UserId, string ExecutablePath, string Arguments);
}
