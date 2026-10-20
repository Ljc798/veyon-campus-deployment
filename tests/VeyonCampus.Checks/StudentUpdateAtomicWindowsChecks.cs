using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VeyonCampus.Core;

internal static class StudentUpdateAtomicWindowsChecks
{
    [SupportedOSPlatform("windows")]
    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Student atomic storage fixtures run only on Windows.");
        if (Environment.GetEnvironmentVariable("VEYONCAMPUS_ALLOW_STUDENT_UPDATE_ATOMIC_STORAGE_FIXTURE") != "1")
            throw new InvalidOperationException(
                "Set VEYONCAMPUS_ALLOW_STUDENT_UPDATE_ATOMIC_STORAGE_FIXTURE=1 only on a disposable elevated Windows runner.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Student atomic storage fixtures require an elevated Windows runner.");

        var installDirectory = StudentUpdateAtomicTransaction.GetInstallDirectory();
        var productDirectory = Path.GetDirectoryName(installDirectory)
                              ?? throw new InvalidDataException("Student product root is unavailable.");
        var recoveryParent = Path.Combine(productDirectory, "UpdateRecovery");
        var recoveryRoot = Path.Combine(recoveryParent, "Student");
        if (Directory.Exists(installDirectory) || File.Exists(installDirectory) ||
            Directory.Exists(recoveryRoot) || File.Exists(recoveryRoot))
            throw new IOException("The fixed Student install or recovery path is occupied; fixture made no changes.");

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var configurationRoot = Path.Combine(programData, "VeyonCampus", "WebsitePolicy");
        var campusHash = Guid.NewGuid().ToString("N")[..24];
        var configurationDirectory = Path.Combine(configurationRoot, campusHash);
        var configurationPath = Path.Combine(configurationDirectory, "agent-" + campusHash + ".json");
        if (Directory.Exists(configurationDirectory) || File.Exists(configurationDirectory))
            throw new IOException("The unique fixture configuration path is unexpectedly occupied.");

        var journalTransactionsRoot = Path.Combine(programData, "VeyonCampus", "StudentUpdateTransactions");
        var journalTransactionsRootExisted = Directory.Exists(journalTransactionsRoot);
        var journalDirectory = Path.Combine(journalTransactionsRoot, campusHash);
        if (Directory.Exists(journalDirectory) || File.Exists(journalDirectory))
            throw new IOException("The unique fixture journal path is unexpectedly occupied.");

        var productDirectoryExisted = Directory.Exists(productDirectory);
        var recoveryParentExisted = Directory.Exists(recoveryParent);
        var recoveryRootExisted = Directory.Exists(recoveryRoot);
        var configurationDirectoryCreated = false;
        var installDirectoryCreated = false;
        var commandId = Guid.NewGuid();
        var recoveryDirectory = Path.Combine(recoveryRoot, commandId.ToString("N"));
        try
        {
            Directory.CreateDirectory(configurationDirectory);
            configurationDirectoryCreated = true;
            SecureAgentPath("SecurePrivateDirectory", configurationDirectory);
            File.WriteAllText(configurationPath, "{}", new UTF8Encoding(false));
            SecureAgentPath("SecurePrivateFile", configurationPath);

            Directory.CreateDirectory(installDirectory);
            installDirectoryCreated = true;
            WriteSetupMarker(installDirectory, "1.0.0", "previous setup fixture");

            var now = DateTimeOffset.UtcNow;
            var transaction = StudentUpdateAtomicTransaction.Create(configurationPath, commandId,
                "1.0.0", "1.1.0", "1.0.0", "1.1.0",
                "VeyonCampus-Student-Setup-1.1.0-win-x64.exe", new string('b', 64), new string('c', 64));
            if (transaction.State != StudentUpdateTransactionState.Prepared ||
                StudentUpdateAtomicTransaction.Read(configurationPath) != transaction)
                throw new InvalidDataException("Student update journal did not read back the exact prepared record.");

            var preserved = StudentUpdateAtomicTransaction.Advance(configurationPath, transaction,
                StudentUpdateTransactionState.PreviousSetupPreserved);
            try
            {
                StudentUpdateAtomicTransaction.Advance(configurationPath, transaction,
                    StudentUpdateTransactionState.SetupInstallationPending);
                throw new InvalidOperationException("A stale transaction snapshot overwrote the current journal.");
            }
            catch (InvalidOperationException exception) when
                (exception.Message.Contains("记录已变化", StringComparison.Ordinal)) { }

            StudentUpdateAtomicTransaction.PreservePreviousInstallation(configurationPath, preserved);
            if (Directory.Exists(installDirectory) ||
                ReadSetupVersion(StudentUpdateAtomicTransaction.GetPreviousInstallationDirectory(preserved)) != "1.0.0" ||
                !Path.GetPathRoot(installDirectory)!.Equals(
                    Path.GetPathRoot(StudentUpdateAtomicTransaction.GetPreviousInstallationDirectory(preserved)),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("StudentSetup was not preserved by same-volume directory rename.");

            var setupPending = StudentUpdateAtomicTransaction.Advance(configurationPath, preserved,
                StudentUpdateTransactionState.SetupInstallationPending);
            Directory.CreateDirectory(installDirectory);
            installDirectoryCreated = true;
            WriteSetupMarker(installDirectory, "1.1.0", "candidate setup fixture");
            StudentUpdateAtomicTransaction.RestorePreviousInstallation(configurationPath, setupPending);
            if (ReadSetupVersion(installDirectory) != "1.0.0" ||
                ReadSetupVersion(StudentUpdateAtomicTransaction.GetFailedInstallationDirectory(setupPending)) != "1.1.0")
                throw new InvalidDataException("StudentSetup rollback did not restore the old tree and isolate the candidate.");

            var journalPath = Path.Combine(journalDirectory, "student-update-transaction.json");
            var journal = File.ReadAllText(journalPath);
            const string expectedState = "setupInstallationPending";
            if (!journal.Contains(expectedState, StringComparison.Ordinal))
                throw new InvalidDataException("Fixture journal did not contain the expected persisted phase.");
            File.WriteAllText(journalPath, journal.Replace(expectedState, "unknownFixtureState", StringComparison.Ordinal),
                new UTF8Encoding(false));
            SecureAgentPath("SecurePrivateFile", journalPath);
            try
            {
                StudentUpdateAtomicTransaction.Read(configurationPath);
                throw new InvalidOperationException("Unknown journal state was accepted.");
            }
            catch (InvalidDataException) { }
            if (ReadSetupVersion(installDirectory) != "1.0.0" ||
                !File.Exists(Path.Combine(StudentUpdateAtomicTransaction.GetFailedInstallationDirectory(setupPending),
                    "VeyonCampus.StudentSetup.exe")))
                throw new InvalidDataException("Malformed journal handling removed the restored installation or candidate evidence.");

            Console.WriteLine("PASS Windows student atomic storage: protected journal, stale-record rejection, same-volume preserve/restore, and malformed-journal retention");
        }
        finally
        {
            if (installDirectoryCreated && (Directory.Exists(installDirectory) || File.Exists(installDirectory)))
                DeleteFixturePath(installDirectory);
            if (Directory.Exists(recoveryDirectory) || File.Exists(recoveryDirectory))
                DeleteFixturePath(recoveryDirectory);
            if (!recoveryRootExisted && Directory.Exists(recoveryRoot) && !Directory.EnumerateFileSystemEntries(recoveryRoot).Any())
                Directory.Delete(recoveryRoot);
            if (!recoveryParentExisted && Directory.Exists(recoveryParent) && !Directory.EnumerateFileSystemEntries(recoveryParent).Any())
                Directory.Delete(recoveryParent);
            if (!productDirectoryExisted && Directory.Exists(productDirectory) && !Directory.EnumerateFileSystemEntries(productDirectory).Any())
                Directory.Delete(productDirectory);

            if (Directory.Exists(journalDirectory)) DeleteFixturePath(journalDirectory);
            if (!journalTransactionsRootExisted && !Directory.Exists(journalDirectory) && Directory.Exists(journalTransactionsRoot) &&
                !Directory.EnumerateFileSystemEntries(journalTransactionsRoot).Any())
                Directory.Delete(journalTransactionsRoot);
            if (configurationDirectoryCreated && Directory.Exists(configurationDirectory))
                DeleteFixturePath(configurationDirectory);
        }
    }

    private static void WriteSetupMarker(string directory, string version, string executableContents)
    {
        File.WriteAllText(Path.Combine(directory, "veyon-campus-role.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            role = "StudentSetup",
            product = "VeyonCampus.StudentSetup",
            version
        }), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "VeyonCampus.StudentSetup.exe"), executableContents,
            new UTF8Encoding(false));
    }

    private static string ReadSetupVersion(string directory)
    {
        using var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "veyon-campus-role.json")));
        return marker.RootElement.GetProperty("version").GetString()
               ?? throw new InvalidDataException("StudentSetup fixture version is missing.");
    }

    private static void SecureAgentPath(string methodName, string path)
    {
        var type = typeof(StudentApplicationUpdateProcessor).Assembly.GetType(
                       "VeyonCampus.Core.AgentFileSecurity", throwOnError: true)
                   ?? throw new InvalidOperationException("Student Agent ACL helper was not found.");
        try
        {
            type.GetMethod(methodName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
                .Invoke(null, [path]);
        }
        catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static void DeleteFixturePath(string path)
    {
        RejectReparsePoints(path);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void RejectReparsePoints(string root)
    {
        var attributes = File.GetAttributes(root);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to recursively remove a reparse point in the atomic fixture.");
        if ((attributes & FileAttributes.Directory) == 0) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root)) RejectReparsePoints(entry);
    }
}
