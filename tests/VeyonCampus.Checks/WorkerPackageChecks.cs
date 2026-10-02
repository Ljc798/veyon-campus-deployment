using System.Reflection;
using System.Text.Json;

namespace VeyonCampus.Checks;

internal static class WorkerPackageChecks
{
    public static void Run(string publishDirectory, string role)
    {
        var root = Path.GetFullPath(publishDirectory);
        var student = role == "StudentSetup";
        if (!student && role != "TeacherConsole") throw new ArgumentException("Unknown Veyon Campus role.");
        var uiName = student ? "VeyonCampus.StudentSetup.exe" : "VeyonCampus.Teacher.exe";
        var uiPath = Path.Combine(root, uiName);
        var workerPath = Path.Combine(root, "Worker", "VeyonCampus.Worker.exe");
        var appCorePath = Path.Combine(root, "VeyonCampus.Core.dll");
        var workerCorePath = Path.Combine(root, "Worker", "VeyonCampus.Core.dll");
        Expect(File.Exists(uiPath) && File.Exists(workerPath) && File.Exists(appCorePath) && File.Exists(workerCorePath));

        static string AssemblyVersion(string executablePath)
        {
            var assemblyPath = Path.ChangeExtension(executablePath, ".dll");
            return AssemblyName.GetAssemblyName(assemblyPath).Version?.ToString(3) ?? "";
        }
        var appVersion = AssemblyVersion(uiPath);
        Expect(appVersion.Length > 0 && appVersion == AssemblyVersion(workerPath));

        var roleMetadataBytes = File.ReadAllBytes(Path.Combine(root, "veyon-campus-role.json"));
        var metadata = roleMetadataBytes.AsMemory();
        if (metadata.Span.Length >= 3 && metadata.Span[0] == 0xEF && metadata.Span[1] == 0xBB && metadata.Span[2] == 0xBF)
            metadata = metadata[3..];
        using (var document = JsonDocument.Parse(metadata))
        {
            var json = document.RootElement;
            Expect(json.GetProperty("schemaVersion").GetInt32() == 1 &&
                   json.GetProperty("role").GetString() == role &&
                   json.GetProperty("version").GetString() == appVersion);
            Expect(json.GetProperty("product").GetString() ==
                   (student ? "VeyonCampus.StudentSetup" : "VeyonCampus.TeacherConsole"));
        }

        Expect(new FileInfo(workerCorePath).Length < 8L * 1024 * 1024);
        Expect(new FileInfo(appCorePath).Length > 8L * 1024 * 1024);
        var agentPath = Path.Combine(root, "WebsitePolicyAgent", "VeyonCampus.Agent.exe");
        Expect(File.Exists(agentPath) == student);

        static void Expect(bool condition)
        {
            if (!condition) throw new Exception("Worker publish artifact assertion failed.");
        }
    }
}
