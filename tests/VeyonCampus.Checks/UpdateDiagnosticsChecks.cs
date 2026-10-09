using System.Net.Http;
using System.Text.Json;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class UpdateDiagnosticsChecks
{
    public static void Run()
    {
        CheckClassifierUsesStableSafeErrors();
        CheckStorageIsBoundedAndExportIsRedacted();
    }

    private static void CheckClassifierUsesStableSafeErrors()
    {
        const string secret = "token-should-never-leave-the-exception";
        var network = UpdateDiagnosticCatalog.Classify(new HttpRequestException($"{secret} /Users/alex/private/path"));
        Assert(network.Code == "UPDATE_NETWORK" && network.Retryable &&
               network.Severity == UpdateDiagnosticSeverity.Warning);
        Assert(!network.Guidance.Contains(secret, StringComparison.Ordinal) &&
               !network.ToUserMessage().Contains("/Users/alex/private/path", StringComparison.Ordinal));

        var timeout = UpdateDiagnosticCatalog.Classify(new TimeoutException(secret));
        Assert(timeout.Code == "UPDATE_TIMEOUT" && timeout.Retryable);
        var signature = UpdateDiagnosticCatalog.Classify(new System.Security.Cryptography.CryptographicException(secret));
        Assert(signature.Code == "UPDATE_SIGNATURE_INVALID" && !signature.Retryable);
        var data = UpdateDiagnosticCatalog.Classify(new InvalidDataException(secret));
        Assert(data.Code == "UPDATE_ARTIFACT_INVALID" && !data.Guidance.Contains(secret, StringComparison.Ordinal));
        var permission = UpdateDiagnosticCatalog.Classify(new UnauthorizedAccessException(secret));
        Assert(permission.Code == "UPDATE_PERMISSION_DENIED" && !permission.Retryable);

        var handoff = UpdateDiagnosticEntry.HandoffStarted(UpdateDiagnosticModule.TeacherConsole,
            UpdateDiagnosticOperation.DownloadAndInstall, "0.4.58", "0.4.59");
        Assert(handoff.Code == "UPDATE_HANDOFF_STARTED" &&
               handoff.Outcome == UpdateDiagnosticOutcome.Started &&
               !UpdateDiagnosticCatalog.HandoffStarted.Guidance.Contains("安装成功", StringComparison.Ordinal));
    }

    private static void CheckStorageIsBoundedAndExportIsRedacted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "veyon-update-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new UpdateDiagnosticsStore(Path.Combine(directory, "store"));
            const string secret = "Bearer-token-and-C:\\Users\\student\\private.zip";
            var unsafeVersion = UpdateDiagnosticEntry.Create(UpdateDiagnosticModule.StudentSetup,
                UpdateDiagnosticOperation.Check, "C:\\Users\\student\\private.zip",
                "C:\\Users\\student\\private.zip");
            Assert(unsafeVersion.Version == "unknown" && unsafeVersion.TargetVersion is null);
            for (var index = 0; index < UpdateDiagnosticsStore.MaximumRecordCount + 12; index++)
            {
                var entry = UpdateDiagnosticEntry.Create(
                    index % 2 == 0 ? UpdateDiagnosticModule.TeacherConsole : UpdateDiagnosticModule.StudentSetup,
                    UpdateDiagnosticOperation.StudentRollout,
                    "0.4.58",
                    index == 0 ? "C:\\Users\\student\\private.zip" : "0.4.59",
                    new IOException(secret),
                    new UpdateDiagnosticCounts(7, 1, 2));
                store.Append(entry);
            }

            var recent = store.ReadRecent();
            Assert(recent.Count == UpdateDiagnosticsStore.MaximumRecordCount);
            Assert(recent.All(entry => entry.Version == "0.4.58" && entry.TargetVersion == "0.4.59"));
            Assert(recent.All(entry => entry.Outcome == UpdateDiagnosticOutcome.Failed &&
                                       entry.Code == "UPDATE_LOCAL_IO" &&
                                       entry.Counts == new UpdateDiagnosticCounts(7, 1, 2)));
            var storedFiles = Directory.GetFiles(Path.Combine(directory, "store"), "*.json");
            Assert(storedFiles.Length <= UpdateDiagnosticsStore.MaximumRecordCount);
            Assert(storedFiles.Sum(path => new FileInfo(path).Length) <= UpdateDiagnosticsStore.MaximumStoreBytes);
            Assert(!Directory.EnumerateFiles(Path.Combine(directory, "store"), ".*.tmp").Any());

            var exportPath = Path.Combine(directory, "manual-export.json");
            var exportedCount = store.ExportTo(exportPath);
            var json = File.ReadAllText(exportPath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert(exportedCount == UpdateDiagnosticsStore.MaximumRecordCount);
            Assert(root.GetProperty("schemaVersion").GetInt32() == 1);
            var first = root.GetProperty("events")[0];
            Assert(first.GetProperty("module").GetString() is "teacherConsole" or "studentSetup");
            Assert(first.GetProperty("operation").GetString() == "studentRollout");
            Assert(first.GetProperty("code").GetString() == "UPDATE_LOCAL_IO");
            Assert(first.GetProperty("version").GetString() == "0.4.58");
            Assert(first.GetProperty("severity").GetString() == "warning");
            Assert(first.GetProperty("retryable").GetBoolean());
            Assert(first.GetProperty("counts").GetProperty("succeeded").GetInt32() == 7);
            Assert(!json.Contains(secret, StringComparison.Ordinal));
            Assert(!json.Contains("exception", StringComparison.OrdinalIgnoreCase));
            Assert(!json.Contains("stackTrace", StringComparison.OrdinalIgnoreCase));
            Assert(!json.Contains("filePath", StringComparison.OrdinalIgnoreCase));
            Assert(!json.Contains("target", StringComparison.OrdinalIgnoreCase) ||
                   !json.Contains("private.zip", StringComparison.OrdinalIgnoreCase));

            var hostile = new UpdateDiagnosticEntry(1, DateTimeOffset.UtcNow, Guid.NewGuid(),
                UpdateDiagnosticModule.TeacherConsole, UpdateDiagnosticOperation.Check,
                UpdateDiagnosticOutcome.Failed, secret, "0.4.58", null,
                UpdateDiagnosticSeverity.Error, false);
            try
            {
                store.Append(hostile);
                throw new Exception("A diagnostic with an unknown code was accepted.");
            }
            catch (InvalidDataException) { }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Update diagnostics check failed.");
    }
}
