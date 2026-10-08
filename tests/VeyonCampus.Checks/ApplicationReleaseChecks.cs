using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeyonCampus.Core;

internal static class ApplicationReleaseChecks
{
    public static void Run()
    {
        CheckSemVerOrdering();
        CheckSelfUpdateRollback();
        CheckSignedReleaseMetadataAndDownloadAsync().GetAwaiter().GetResult();
    }

    private static void CheckSelfUpdateRollback()
    {
        var temporaryDirectory = Path.Combine(CanonicalTempRoot(), "veyon-release-rollback-" + Guid.NewGuid().ToString("N"));
        var installDirectory = Path.Combine(temporaryDirectory, "Teacher");
        var recoveryDirectory = Path.Combine(temporaryDirectory, "Recovery");
        var previousDirectory = Path.Combine(recoveryDirectory, "Previous");
        var executableName = "VeyonCampus.Teacher.exe";
        var recovery = typeof(ApplicationReleaseUpdateHandoff).GetMethod("RestorePreviousInstallation",
                          System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                      ?? throw new InvalidOperationException("Self-update rollback method was not found.");
        try
        {
            CreateInstallation(previousDirectory, executableName, "1.0.0", "previous version");
            CreateInstallation(installDirectory, executableName, "2.0.0", "failed version");
            Exception? failure = null;
            try
            {
                recovery.Invoke(null,
                [
                    installDirectory,
                    previousDirectory,
                    recoveryDirectory,
                    executableName,
                    ApplicationReleaseRole.TeacherConsole,
                    "1.0.0",
                    new IOException("synthetic installer failure")
                ]);
            }
            catch (System.Reflection.TargetInvocationException exception)
            {
                failure = exception.InnerException;
            }

            Expect(failure is InvalidOperationException &&
                   failure.Message.Contains("旧版本 1.0.0 已恢复", StringComparison.Ordinal));
            Expect(File.ReadAllText(Path.Combine(installDirectory, executableName)) == "previous version");
            var failedInstallations = Directory.EnumerateDirectories(recoveryDirectory, "Failed-*").ToArray();
            Expect(failedInstallations.Length == 1 &&
                   File.ReadAllText(Path.Combine(failedInstallations[0], executableName)) == "failed version");

            var linkedInstallDirectory = Path.Combine(temporaryDirectory, "LinkedInstall");
            var linkedPreviousDirectory = Path.Combine(temporaryDirectory, "LinkedRecovery", "Previous");
            var linkedRecoveryDirectory = Path.GetDirectoryName(linkedPreviousDirectory)!;
            var linkTarget = Path.Combine(temporaryDirectory, "link-target");
            Directory.CreateDirectory(linkTarget);
            CreateInstallation(linkedPreviousDirectory, executableName, "1.0.0", "protected previous");
            CreateInstallation(linkedInstallDirectory, executableName, "2.0.0", "untrusted partial");
            Directory.CreateSymbolicLink(Path.Combine(linkedInstallDirectory, "linked-directory"), linkTarget);
            try
            {
                recovery.Invoke(null,
                [
                    linkedInstallDirectory,
                    linkedPreviousDirectory,
                    linkedRecoveryDirectory,
                    executableName,
                    ApplicationReleaseRole.TeacherConsole,
                    "1.0.0",
                    new IOException("synthetic installer failure")
                ]);
                throw new InvalidOperationException("Rollback moved an installation containing a reparse point.");
            }
            catch (System.Reflection.TargetInvocationException exception)
            {
                Expect(exception.InnerException is AggregateException &&
                       Directory.Exists(linkedPreviousDirectory) &&
                       File.ReadAllText(Path.Combine(linkedPreviousDirectory, executableName)) == "protected previous");
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }

        static void CreateInstallation(string directory, string executableName, string version, string executableText)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "veyon-campus-role.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    role = "TeacherConsole",
                    product = "VeyonCampus.TeacherConsole",
                    version
                }));
            File.WriteAllText(Path.Combine(directory, executableName), executableText);
        }
    }

    private static void CheckSemVerOrdering()
    {
        Expect(ApplicationReleaseClient.CompareVersions("1.10.0", "1.9.0") > 0);
        Expect(ApplicationReleaseClient.CompareVersions("9007199254740993.0.0", "9007199254740992.0.0") > 0);
        Expect(ApplicationReleaseClient.CompareVersions("1.2.3", "1.2.3+build.9") == 0);
        Expect(ApplicationReleaseClient.CompareVersions("1.2.3-rc.2", "1.2.3-rc.10") < 0);
        Expect(ApplicationReleaseClient.CompareVersions("1.2.3", "1.2.3-rc.10") > 0);
        Reject(() => ApplicationReleaseClient.CompareVersions("01.2.3", "1.2.3"));
    }

    private static async Task CheckSignedReleaseMetadataAndDownloadAsync()
    {
        using var signingKey = RSA.Create(2048);
        var publicKeyPem = signingKey.ExportSubjectPublicKeyInfoPem();
        var apiBase = new Uri("https://release-fixture.invalid/");
        var releaseId = Guid.NewGuid();
        var artifactBytes = Encoding.ASCII.GetBytes("synthetic signed setup artifact");
        var manifest = new ApplicationReleaseManifest(
            3,
            "VeyonCampus.TeacherConsole",
            "TeacherConsole",
            "1.10.0",
            "win-x64",
            "VeyonCampus-Teacher-Setup-1.10.0-win-x64.exe",
            artifactBytes.LongLength,
            Convert.ToHexString(SHA256.HashData(artifactBytes)),
            new Uri(apiBase, $"v1/releases/{releaseId:D}/artifact").AbsoluteUri,
            new ApplicationReleasePolicyCapabilities(1, 1));
        var release = Sign(manifest, signingKey);
        ApplicationReleaseClient.Verify(release, ApplicationReleaseRole.TeacherConsole, apiBase, publicKeyPem);
        ApplicationReleaseCompatibility.EnsureSupports(manifest, applicationPolicyRequired: true,
            studentSystemPolicyRequired: true);
        Reject(() => ApplicationReleaseClient.Verify(release, ApplicationReleaseRole.StudentSetup, apiBase, publicKeyPem));
        var legacyManifest = manifest with { SchemaVersion = 1, PolicyCapabilities = null };
        ApplicationReleaseClient.Verify(Sign(legacyManifest, signingKey), ApplicationReleaseRole.TeacherConsole,
            apiBase, publicKeyPem);
        var systemPolicyManifest = manifest with
        {
            SchemaVersion = 2,
            PolicyCapabilities = new ApplicationReleasePolicyCapabilities(1)
        };
        ApplicationReleaseClient.Verify(Sign(systemPolicyManifest, signingKey),
            ApplicationReleaseRole.TeacherConsole, apiBase, publicKeyPem);
        ApplicationReleaseCompatibility.EnsureSupports(systemPolicyManifest, applicationPolicyRequired: false,
            studentSystemPolicyRequired: true);
        Reject(() => ApplicationReleaseCompatibility.EnsureSupports(systemPolicyManifest,
            applicationPolicyRequired: true, studentSystemPolicyRequired: true));
        var changedCapability = manifest with
        {
            PolicyCapabilities = new ApplicationReleasePolicyCapabilities(2, 32768)
        };
        Reject(() => ApplicationReleaseClient.Verify(release with { Manifest = changedCapability },
            ApplicationReleaseRole.TeacherConsole, apiBase, publicKeyPem));
        var missingApplicationCapability = manifest with
        {
            PolicyCapabilities = new ApplicationReleasePolicyCapabilities(1)
        };
        Reject(() => ApplicationReleaseClient.Verify(Sign(missingApplicationCapability, signingKey),
            ApplicationReleaseRole.TeacherConsole, apiBase, publicKeyPem));

        var changedManifest = manifest with
        {
            Version = "1.11.0",
            FileName = "VeyonCampus-Teacher-Setup-1.11.0-win-x64.exe"
        };
        Reject(() => ApplicationReleaseClient.Verify(release with { Manifest = changedManifest },
            ApplicationReleaseRole.TeacherConsole, apiBase, publicKeyPem));
        var unexpectedOrigin = manifest with
        {
            DownloadUrl = $"https://other.invalid/v1/releases/{releaseId:D}/artifact"
        };
        Reject(() => ApplicationReleaseClient.Verify(Sign(unexpectedOrigin, signingKey),
            ApplicationReleaseRole.TeacherConsole, apiBase, publicKeyPem));

        var handler = new FixtureHandler(apiBase, release, artifactBytes);
        using var client = new HttpClient(handler);
        var releaseClient = new ApplicationReleaseClient(apiBase, client, publicKeyPem);
        var result = await releaseClient.CheckLatestAsync(ApplicationReleaseRole.TeacherConsole, "1.9.0");
        Expect(result.IsNewer && result.Release?.Manifest.Version == "1.10.0");
        var currentResult = await releaseClient.CheckLatestAsync(ApplicationReleaseRole.TeacherConsole, "1.10.0");
        Expect(!currentResult.IsNewer && currentResult.CurrentVersion == "1.10.0");
        var aheadResult = await releaseClient.CheckLatestAsync(ApplicationReleaseRole.TeacherConsole, "1.11.0");
        Expect(!aheadResult.IsNewer && aheadResult.Release?.Manifest.Version == "1.10.0");

        var stagingDirectory = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-release-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var downloadedPath = await releaseClient.DownloadAsync(release, ApplicationReleaseRole.TeacherConsole,
                stagingDirectory);
            Expect(File.ReadAllBytes(downloadedPath).SequenceEqual(artifactBytes));
            Expect(File.Exists(downloadedPath + ".release.json"));
            var stagedRelease = ApplicationReleaseClient.ReadVerifiedStagedRelease(downloadedPath,
                ApplicationReleaseRole.TeacherConsole, apiBase, publicKeyPem);
            Expect(stagedRelease.Manifest.Version == release.Manifest.Version);
            Expect(await releaseClient.DownloadAsync(release, ApplicationReleaseRole.TeacherConsole,
                stagingDirectory) == downloadedPath);

            var incomingDirectory = Path.Combine(stagingDirectory, "offline-media");
            Directory.CreateDirectory(incomingDirectory);
            var incomingInstallerPath = Path.Combine(incomingDirectory, manifest.FileName);
            File.WriteAllBytes(incomingInstallerPath, artifactBytes);
            File.WriteAllText(incomingInstallerPath + ".release.json", JsonSerializer.Serialize(release));
            var offlineStagingDirectory = Path.Combine(stagingDirectory, "offline-staged");
            var offlineInstallerPath = ApplicationReleaseClient.StageVerifiedOfflineRelease(incomingInstallerPath,
                offlineStagingDirectory, ApplicationReleaseRole.TeacherConsole, apiBase, publicKeyPem);
            Expect(File.ReadAllBytes(offlineInstallerPath).SequenceEqual(artifactBytes));
            Expect(ApplicationReleaseClient.ReadVerifiedStagedRelease(offlineInstallerPath,
                ApplicationReleaseRole.TeacherConsole, apiBase, publicKeyPem) == release);
            Expect(ApplicationReleaseClient.StageVerifiedOfflineRelease(offlineInstallerPath,
                offlineStagingDirectory, ApplicationReleaseRole.TeacherConsole, apiBase, publicKeyPem) == offlineInstallerPath);

            var corruptIncomingDirectory = Path.Combine(incomingDirectory, "corrupt");
            Directory.CreateDirectory(corruptIncomingDirectory);
            var corruptIncomingPath = Path.Combine(corruptIncomingDirectory, manifest.FileName);
            var corruptIncomingBytes = artifactBytes.ToArray();
            corruptIncomingBytes[0] ^= 0x01;
            File.WriteAllBytes(corruptIncomingPath, corruptIncomingBytes);
            File.WriteAllText(corruptIncomingPath + ".release.json", JsonSerializer.Serialize(release));
            Reject(() => ApplicationReleaseClient.StageVerifiedOfflineRelease(corruptIncomingPath,
                Path.Combine(stagingDirectory, "offline-corrupt"), ApplicationReleaseRole.TeacherConsole,
                apiBase, publicKeyPem));

            var collisionDirectory = Path.Combine(stagingDirectory, "offline-collision");
            Directory.CreateDirectory(collisionDirectory);
            var collisionPath = Path.Combine(collisionDirectory, manifest.FileName);
            File.WriteAllText(collisionPath, "preserve existing file");
            try
            {
                _ = ApplicationReleaseClient.StageVerifiedOfflineRelease(incomingInstallerPath,
                    collisionDirectory, ApplicationReleaseRole.TeacherConsole, apiBase, publicKeyPem);
                throw new InvalidOperationException("Offline staging overwrote an existing file.");
            }
            catch (IOException)
            {
                Expect(File.ReadAllText(collisionPath) == "preserve existing file");
            }
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        }

        var corruptDirectory = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-release-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var corruptBytes = artifactBytes.ToArray();
            corruptBytes[0] ^= 0x01;
            using var corruptClient = new HttpClient(new FixtureHandler(apiBase, release,
                corruptBytes));
            var corruptReleaseClient = new ApplicationReleaseClient(apiBase, corruptClient, publicKeyPem);
            await RejectAsync(() => corruptReleaseClient.DownloadAsync(release,
                ApplicationReleaseRole.TeacherConsole, corruptDirectory));
            Expect(!Directory.Exists(corruptDirectory) || !Directory.EnumerateFiles(corruptDirectory).Any());
        }
        finally
        {
            if (Directory.Exists(corruptDirectory)) Directory.Delete(corruptDirectory, recursive: true);
        }
    }

    private static ApplicationReleaseEnvelope Sign(ApplicationReleaseManifest manifest, RSA key)
    {
        var signature = key.SignData(ApplicationReleaseClient.CreateCanonicalPayload(manifest),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return new(manifest, "RSA-PSS-SHA256", Convert.ToBase64String(signature), DateTimeOffset.UtcNow);
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Application release check failed.");
    }

    private static string CanonicalTempRoot()
    {
        var full = Path.GetFullPath(TestPath.CanonicalTempRoot());
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException("Temp root is invalid.");
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            FileSystemInfo item = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            current = item.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
        }
        return Path.GetFullPath(current);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid application release input was accepted.");
    }

    private static async Task RejectAsync(Func<Task<string>> action)
    {
        try { _ = await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid application release artifact was accepted.");
    }

    private sealed class FixtureHandler(Uri apiBase, ApplicationReleaseEnvelope release, byte[] artifactBytes)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri ?? throw new InvalidOperationException("Fixture request URL missing.");
            if (requestUri.AbsolutePath == "/v3/releases/latest")
            {
                var body = JsonSerializer.Serialize(new { release });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }
            if (requestUri == new Uri(release.Manifest.DownloadUrl))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://github.com/Ljc798/veyon-campus-deployment/releases/download/v" +
                    release.Manifest.Version + "/" + release.Manifest.FileName);
                return Task.FromResult(response);
            }
            if (requestUri.Host == "github.com")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/fixture/signed-artifact");
                return Task.FromResult(response);
            }
            if (requestUri.Host == "release-assets.githubusercontent.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(artifactBytes)
                });
            throw new InvalidOperationException($"Unexpected release fixture request: {apiBase}{requestUri.PathAndQuery}");
        }
    }
}
