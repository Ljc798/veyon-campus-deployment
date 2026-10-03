using System.Security.Cryptography;
using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class PackageBuilderFailureChecks
{
    public static void Run(string temporary)
    {
        var root = Path.Combine(temporary, "package-builder-failures");
        Directory.CreateDirectory(root);
        var publicKeyPath = Path.Combine(root, "public.pem");
        using (var key = RSA.Create(2048))
            File.WriteAllText(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());

        var existingOutput = Path.Combine(root, "existing-output");
        Directory.CreateDirectory(existingOutput);
        var protectedFile = Path.Combine(existingOutput, "keep.txt");
        File.WriteAllText(protectedFile, "preserve this output");
        Throws<IOException>(() => PackageBuilder.Build(existingOutput, "campus-demo", "PC-", publicKeyPath));
        Expect(File.ReadAllText(protectedFile) == "preserve this output" &&
               StagingDirectories(root).Length == 0);

        var failedOutput = Path.Combine(root, "write-failure-output");
        var writeFailure = new FaultingFileSystem { FailWriteFileName = "manifest.json" };
        Throws<IOException>(() => Build(failedOutput, publicKeyPath, writeFailure));
        Expect(!Directory.Exists(failedOutput) && StagingDirectories(root).Length == 0);

        var cancelledOutput = Path.Combine(root, "cancelled-output");
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelDuringBuild = new FaultingFileSystem
            {
                CancelAfterWriteFileName = "campus.json",
                Cancellation = cancellation
            };
            Throws<OperationCanceledException>(() =>
                Build(cancelledOutput, publicKeyPath, cancelDuringBuild, cancellation.Token));
            Expect(!Directory.Exists(cancelledOutput) && StagingDirectories(root).Length == 0);
        }

        var raceOutput = Path.Combine(root, "raced-output");
        var destinationRace = new FaultingFileSystem { CreateDestinationBeforeMove = true };
        Throws<IOException>(() => Build(raceOutput, publicKeyPath, destinationRace));
        Expect(File.ReadAllText(Path.Combine(raceOutput, "created-by-race.txt")) == "keep existing destination" &&
               StagingDirectories(root).Length == 0);

        var cleanupOutput = Path.Combine(root, "cleanup-failure-output");
        var cleanupFailure = new FaultingFileSystem
        {
            FailWriteFileName = "manifest.json",
            FailCleanup = true
        };
        var cleanupException = Throws<IOException>(() => Build(cleanupOutput, publicKeyPath, cleanupFailure));
        var abandonedStages = StagingDirectories(root);
        try
        {
            Expect(!Directory.Exists(cleanupOutput) && abandonedStages.Length == 1 &&
                   cleanupException.Message.Contains("清理失败", StringComparison.Ordinal) &&
                   cleanupException.Message.Contains(abandonedStages[0], StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var stage in abandonedStages)
                if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static string Build(string output, string publicKeyPath, FaultingFileSystem fileSystem,
        CancellationToken cancellationToken = default) =>
        PackageBuilder.BuildCore(output, "campus-demo", "PC-", publicKeyPath, null, false,
            cancellationToken, fileSystem);

    private static string[] StagingDirectories(string parent) =>
        Directory.GetDirectories(parent, ".student-package-staging-*", SearchOption.TopDirectoryOnly);

    private static TException Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException exception) { return exception; }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Package builder failure injection assertion failed.");
    }

    private sealed class FaultingFileSystem : IPackageBuildFileSystem
    {
        private readonly IPackageBuildFileSystem _physical = PhysicalPackageBuildFileSystem.Instance;
        public string? FailWriteFileName { get; init; }
        public string? CancelAfterWriteFileName { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public bool FailCleanup { get; init; }
        public bool CreateDestinationBeforeMove { get; init; }

        public bool DirectoryExists(string path) => _physical.DirectoryExists(path);
        public void CreateDirectory(string path) => _physical.CreateDirectory(path);

        public void WriteAllText(string path, string contents)
        {
            BeforeWrite(path);
            _physical.WriteAllText(path, contents);
            AfterWrite(path);
        }

        public void WriteAllBytes(string path, byte[] contents)
        {
            BeforeWrite(path);
            _physical.WriteAllBytes(path, contents);
            AfterWrite(path);
        }

        public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
            _physical.EnumerateFileSystemEntries(path);

        public void MoveDirectory(string source, string destination)
        {
            if (CreateDestinationBeforeMove)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "created-by-race.txt"), "keep existing destination");
            }
            _physical.MoveDirectory(source, destination);
        }

        public void DeleteDirectory(string path, bool recursive)
        {
            if (FailCleanup) throw new IOException("Injected cleanup failure.");
            _physical.DeleteDirectory(path, recursive);
        }

        private void BeforeWrite(string path)
        {
            if (Path.GetFileName(path).Equals(FailWriteFileName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Injected package write failure.");
        }

        private void AfterWrite(string path)
        {
            if (Path.GetFileName(path).Equals(CancelAfterWriteFileName, StringComparison.OrdinalIgnoreCase))
                Cancellation?.Cancel();
        }
    }
}
