using System.Security.Cryptography;
using VeyonCampus.Core;

internal static class StudentSystemPolicyChecks
{
    private const string Campus = "system-demo";
    private const string Sid = "S-1-5-21-123-456-789-1001";
    private const string Wallpaper = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";

    public static void Run()
    {
        var validJpeg = CreateJpeg(1920, 1080, 3);
        Expect(WindowsDefaultWallpaper.ReadJpegDimensions(validJpeg) == (1920, 1080, 3));
        Reject(() => WindowsDefaultWallpaper.ReadJpegDimensions(CreateJpeg(640, 480, 3)));
        Reject(() => WindowsDefaultWallpaper.ReadJpegDimensions(CreateJpeg(1920, 1080, 1)));
        Reject(() => WindowsDefaultWallpaper.ReadJpegDimensions(new byte[128]));
        CheckTrustedWallpaperAsset();

        using var key = RSA.Create(2048);
        var publicPem = key.ExportSubjectPublicKeyInfoPem();
        var now = DateTimeOffset.UtcNow;
        var policy = StudentSystemPolicyCompiler.Create(Campus, 1, [Sid]);
        var desired = StudentSystemPolicyCompiler.DesiredValues(policy, Wallpaper);
        var aclResources = desired.Keys.Where(resource => resource.StartsWith("user-acl|", StringComparison.Ordinal)).ToArray();
        Expect(aclResources.Length == 5 && aclResources.All(resource =>
            desired[resource] == StudentSystemPolicyValue.RegistryAclReadOnly()));
        Expect(policy.Settings == StudentSystemPolicySettings.Default && desired.ContainsKey(
            StudentSystemPolicyResource.UserRegistry(Sid, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "Wallpaper")));
        Expect(desired.ContainsKey(StudentSystemPolicyResource.AccountPasswordChangeable(Sid)) &&
               desired[StudentSystemPolicyResource.AccountPasswordChangeable(Sid)] == StudentSystemPolicyValue.Boolean(false));
        Expect(desired.ContainsKey(StudentSystemPolicyResource.LsaRight(Sid, "SeSystemtimePrivilege")) &&
               !desired.Keys.Any(resource => resource.EndsWith("|NoControlPanel", StringComparison.Ordinal)));
        var settingsVisibilityKey = StudentSystemPolicyResource.UserRegistry(Sid,
            @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "SettingsPageVisibility");
        Expect(desired[settingsVisibilityKey] == StudentSystemPolicyValue.String(
            StudentSystemPolicyCompiler.NetworkSettingsPageVisibilityPolicy) &&
               StudentSystemPolicyCompiler.NetworkSettingsPageVisibilityPolicy.StartsWith("hide:", StringComparison.Ordinal) &&
               StudentSystemPolicyCompiler.NetworkSettingsPageVisibilityPolicy.Contains("network-wifi", StringComparison.Ordinal) &&
               StudentSystemPolicyCompiler.NetworkSettingsPageVisibilityPolicy.Contains("network-vpn", StringComparison.Ordinal));
        Expect(StudentSystemPolicyCompiler.SupportsSettingsPageVisibilityEdition("Professional") &&
               StudentSystemPolicyCompiler.SupportsSettingsPageVisibilityEdition("Education") &&
               StudentSystemPolicyCompiler.SupportsSettingsPageVisibilityEdition("IoTEnterpriseS") &&
               !StudentSystemPolicyCompiler.SupportsSettingsPageVisibilityEdition("Core") &&
               !StudentSystemPolicyCompiler.SupportsSettingsPageVisibilityEdition("UnknownEdition") &&
               StudentSystemPolicyCompiler.SupportsDesktopWallpaperPolicyEdition("Professional") &&
               StudentSystemPolicyCompiler.SupportsDesktopWallpaperPolicyEdition("Enterprise") &&
               !StudentSystemPolicyCompiler.SupportsDesktopWallpaperPolicyEdition("Core"));
        Expect(desired.ContainsKey(StudentSystemPolicyResource.UserRegistry(Sid,
                   @"Software\Policies\Microsoft\Windows\Network Connections", "NC_DeleteConnection")) &&
               desired.ContainsKey(StudentSystemPolicyResource.UserRegistry(Sid,
                   @"Software\Policies\Microsoft\Windows\Network Connections", "NC_RasMyProperties")));
        Expect(desired[StudentSystemPolicyResource.MachineRegistry(@"Software\Policies\Microsoft\Windows\Installer", "DisableMSI")] ==
               StudentSystemPolicyValue.Dword(1) &&
               desired.ContainsKey(StudentSystemPolicyResource.UserRegistry(Sid,
                   @"Software\Policies\Microsoft\WindowsStore", "RemoveWindowsStore")) &&
               !desired.ContainsKey(StudentSystemPolicyResource.MachineRegistry(
                   @"Software\Policies\Microsoft\WindowsStore", "RemoveWindowsStore")));
        var noWallpaperNeeded = StudentSystemPolicyCompiler.DesiredValues(
            policy with { Settings = policy.Settings with { LockWallpaper = false } });
        Expect(!noWallpaperNeeded.Keys.Any(resource => resource.EndsWith("|Wallpaper", StringComparison.Ordinal)));
        var networkAllowed = StudentSystemPolicyCompiler.DesiredValues(
            policy with { Settings = policy.Settings with { ProhibitNetworkChanges = false } }, Wallpaper);
        Expect(!networkAllowed.ContainsKey(settingsVisibilityKey) &&
               !networkAllowed.Keys.Any(resource => resource.Contains("Network Connections|", StringComparison.Ordinal)));
        Reject(() => StudentSystemPolicyCompiler.Create(Campus, 1, [], StudentSystemPolicySettings.Default));
        Reject(() => StudentSystemPolicyCompiler.Create(Campus, 1, ["S-1-5-32-544"]));

        var signed = StudentSystemPolicyCryptography.Sign(policy, key);
        var verified = StudentSystemPolicyCryptography.Verify(signed, publicPem, Campus, 0, now);
        Expect(verified.Revision == 1 && verified.StudentSids.Single() == Sid);
        Reject(() => StudentSystemPolicyCryptography.Verify(signed, publicPem, "other-campus", 0, now));
        Reject(() => StudentSystemPolicyCryptography.Verify(signed, publicPem, Campus, 1, now));
        using var wrongKey = RSA.Create(2048);
        Reject(() => StudentSystemPolicyCryptography.Verify(signed, wrongKey.ExportSubjectPublicKeyInfoPem(), Campus, 0, now));
        Reject(() => StudentSystemPolicyCompiler.Validate(policy with { Purpose = ApplicationPolicyCompiler.Purpose }));
        Reject(() => ApplicationPolicyCryptography.Verify(signed, publicPem, Campus, 0, now));
        Reject(() => WebsitePolicyCryptography.Verify(signed, publicPem, Campus, 0));

        var backend = new FakeBackend();
        var originalSettingsVisibility = new StudentSystemPolicyValueState(true,
            StudentSystemPolicyValue.String("hide:bluetooth"));
        backend.Values[settingsVisibilityKey] = originalSettingsVisibility;
        var store = new FakeStore();
        var executionCoordinator = new FakeExecutionCoordinator();
        var runtime = new StudentSystemPolicyRuntime(backend, store, Campus, publicPem, executionCoordinator);
        var applied = runtime.Apply(signed, now);
        Expect(backend.LastPreflightRequiredNetworkSettingsPageVisibility &&
               backend.LastPreflightRequiredDesktopWallpaperPolicy);
        Expect(!applied.Pending && store.State is { Pending: false } &&
               backend.Values[StudentSystemPolicyResource.AccountPasswordChangeable(Sid)] ==
               new StudentSystemPolicyValueState(true, StudentSystemPolicyValue.Boolean(false)));
        Expect(executionCoordinator.Enabled && executionCoordinator.StudentSids.SequenceEqual([Sid]));
        Expect(backend.Values[StudentSystemPolicyResource.UserRegistry(Sid,
                   @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "Wallpaper")] ==
               new StudentSystemPolicyValueState(true, StudentSystemPolicyValue.String(Path.GetFullPath(Wallpaper))));
        Expect(backend.Values[settingsVisibilityKey] ==
               new StudentSystemPolicyValueState(true,
                   StudentSystemPolicyValue.String(StudentSystemPolicyCompiler.NetworkSettingsPageVisibilityPolicy)));
        Expect(aclResources.All(resource => backend.Values[resource] ==
            new StudentSystemPolicyValueState(true, StudentSystemPolicyValue.RegistryAclReadOnly())));

        var wallpaperOnlyBackend = new FakeBackend();
        var wallpaperOnlyRuntime = new StudentSystemPolicyRuntime(wallpaperOnlyBackend, new FakeStore(), Campus, publicPem);
        var wallpaperOnly = StudentSystemPolicyCompiler.Create(Campus, 1, [Sid],
            new StudentSystemPolicySettings(true, false, false, false, false, false), now);
        wallpaperOnlyRuntime.Apply(StudentSystemPolicyCryptography.Sign(wallpaperOnly, key), now);
        Expect(!wallpaperOnlyBackend.LastPreflightRequiredNetworkSettingsPageVisibility &&
               wallpaperOnlyBackend.LastPreflightRequiredDesktopWallpaperPolicy);

        var legacyBackend = new FakeBackend();
        var legacyInstalled = applied.InstalledValues.Where(pair => !pair.Key.StartsWith("user-acl|", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var legacyOriginals = applied.OriginalValues.Where(pair => !pair.Key.StartsWith("user-acl|", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in legacyInstalled) legacyBackend.Values[pair.Key] = pair.Value;
        var legacyStore = new FakeStore(applied with
        {
            InstalledValues = legacyInstalled,
            OriginalValues = legacyOriginals
        });
        var legacyRuntime = new StudentSystemPolicyRuntime(legacyBackend, legacyStore, Campus, publicPem);
        var migrated = legacyRuntime.ReadForAudit(now);
        Expect(migrated is { Pending: false } && aclResources.All(resource =>
            migrated.InstalledValues.ContainsKey(resource) && !migrated.OriginalValues[resource].Exists &&
            legacyBackend.Values[resource] == new StudentSystemPolicyValueState(true,
                StudentSystemPolicyValue.RegistryAclReadOnly())));
        var tamperedAcl = aclResources[0];
        legacyBackend.Values[tamperedAcl] = new StudentSystemPolicyValueState(true,
            new StudentSystemPolicyValue("registry-acl", "modified"));
        RejectIo(() => legacyRuntime.ReadForAudit(now));

        var externalResource = StudentSystemPolicyResource.UserRegistry(Sid,
            @"Software\Policies\Microsoft\Windows\Network Connections", "NC_LanProperties");
        backend.Values[externalResource] = new StudentSystemPolicyValueState(true, StudentSystemPolicyValue.Dword(0));
        RejectIo(() => runtime.ReadForAudit(now));
        backend.Values[externalResource] = applied.InstalledValues[externalResource];

        var disabled = StudentSystemPolicyCompiler.Create(Campus, 2, [],
            new StudentSystemPolicySettings(false, false, false, false, false, false), now.AddMinutes(1));
        runtime.Apply(StudentSystemPolicyCryptography.Sign(disabled, key), now.AddMinutes(1));
        Expect(!backend.LastPreflightRequiredNetworkSettingsPageVisibility &&
               !backend.LastPreflightRequiredDesktopWallpaperPolicy);
        Expect(store.State is { Pending: false, InstalledValues.Count: 0 } && !executionCoordinator.Enabled &&
               backend.Values[StudentSystemPolicyResource.AccountPasswordChangeable(Sid)].Value ==
               StudentSystemPolicyValue.Boolean(true));
        Expect(backend.Values[settingsVisibilityKey] == originalSettingsVisibility);
        Expect(aclResources.All(resource => !backend.Values.ContainsKey(resource)));

        var failingBackend = new FakeBackend { FailAfterWrites = 1 };
        var recoveryStore = new FakeStore();
        var recoveryRuntime = new StudentSystemPolicyRuntime(failingBackend, recoveryStore, Campus, publicPem);
        try { recoveryRuntime.Apply(signed, now); throw new Exception("Interrupted policy write unexpectedly succeeded."); }
        catch (IOException) { }
        Expect(recoveryStore.State is { Pending: true });
        var recovered = recoveryRuntime.ReadForAudit(now);
        Expect(recovered is { Pending: false } && recoveryStore.State is { Pending: false } &&
               recoveryStore.State.InstalledValues.Count == StudentSystemPolicyCompiler.DesiredValues(policy, Wallpaper).Count);
    }

    private sealed class FakeBackend : IStudentSystemPolicyBackend
    {
        public string DefaultWallpaperPath => Wallpaper;
        public Dictionary<string, StudentSystemPolicyValueState> Values { get; } = new(StringComparer.Ordinal);
        public int? FailAfterWrites { get; set; }
        public bool LastPreflightRequiredNetworkSettingsPageVisibility { get; private set; }
        public bool LastPreflightRequiredDesktopWallpaperPolicy { get; private set; }
        public void VerifyEnvironmentAndStudents(IReadOnlyList<string> studentSids,
            bool requireNetworkSettingsPageVisibility, bool requireDesktopWallpaperPolicy)
        {
            if (studentSids.Any(sid => sid != Sid)) throw new InvalidDataException("Unexpected test SID.");
            LastPreflightRequiredNetworkSettingsPageVisibility = requireNetworkSettingsPageVisibility;
            LastPreflightRequiredDesktopWallpaperPolicy = requireDesktopWallpaperPolicy;
        }
        public IReadOnlyDictionary<string, StudentSystemPolicyValueState> ReadValues(IReadOnlyCollection<string> resources) =>
            resources.ToDictionary(resource => resource,
                resource => Values.GetValueOrDefault(resource) ?? (resource.StartsWith("account|", StringComparison.Ordinal)
                    ? new StudentSystemPolicyValueState(true, StudentSystemPolicyValue.Boolean(true))
                    : new StudentSystemPolicyValueState(false, null)),
                StringComparer.Ordinal);
        public void WriteValues(IReadOnlyDictionary<string, StudentSystemPolicyValueState> values)
        {
            var written = 0;
            foreach (var pair in values)
            {
                if (FailAfterWrites is { } maximum && written >= maximum)
                {
                    FailAfterWrites = null;
                    throw new IOException("simulated interrupted policy transaction");
                }
                if (pair.Value.Exists) Values[pair.Key] = pair.Value;
                else Values.Remove(pair.Key);
                written++;
            }
        }
    }

    private sealed class FakeStore : IStudentSystemPolicyStateStore
    {
        public FakeStore() { }
        public FakeStore(StudentSystemPolicyRuntimeState? state) => State = state;
        public StudentSystemPolicyRuntimeState? State { get; private set; }
        public StudentSystemPolicyRuntimeState? Read() => State;
        public void Save(StudentSystemPolicyRuntimeState state) => State = state;
    }

    private sealed class FakeExecutionCoordinator : IStudentSoftwareExecutionPolicyCoordinator
    {
        public bool Enabled { get; private set; }
        public IReadOnlyList<string> StudentSids { get; private set; } = Array.Empty<string>();
        public void SetStudentSoftwareRestriction(IReadOnlyList<string> studentSids, bool enabled)
        { StudentSids = studentSids.ToArray(); Enabled = enabled; }
    }

    private static byte[] CreateJpeg(int width, int height, byte components)
    {
        var bytes = new byte[128];
        bytes[0] = 0xff; bytes[1] = 0xd8;
        bytes[2] = 0xff; bytes[3] = 0xc0; bytes[4] = 0; bytes[5] = 17;
        bytes[6] = 8;
        bytes[7] = (byte)(height >> 8); bytes[8] = (byte)height;
        bytes[9] = (byte)(width >> 8); bytes[10] = (byte)width;
        bytes[11] = components;
        bytes[21] = 0xff; bytes[22] = 0xda; bytes[23] = 0; bytes[24] = 12;
        bytes[^2] = 0xff; bytes[^1] = 0xd9;
        return bytes;
    }

    private static void CheckTrustedWallpaperAsset()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", WindowsDefaultWallpaper.AssetFileName);
        var commonData = Path.Combine(TestPath.CanonicalTempRoot(), "veyon-wallpaper-" + Guid.NewGuid().ToString("N"));
        var protectedPaths = new List<(string Path, bool Directory)>();
        try
        {
            Directory.CreateDirectory(commonData);
            var installed = WindowsDefaultWallpaper.EnsureBundledAsset(AppContext.BaseDirectory, commonData,
                (path, directory) => protectedPaths.Add((path, directory)));
            Expect(Path.GetFileName(installed) == WindowsDefaultWallpaper.AssetSha256 + ".jpg" &&
                   File.ReadAllBytes(installed).SequenceEqual(File.ReadAllBytes(source)) &&
                   protectedPaths.Any(entry => entry.Path == installed && !entry.Directory) &&
                   protectedPaths.Any(entry => entry.Directory && entry.Path.EndsWith(
                       Path.Combine("SystemPolicy", "Assets"), StringComparison.Ordinal)));
            Expect(WindowsDefaultWallpaper.EnsureBundledAsset(AppContext.BaseDirectory, commonData,
                       (path, directory) => protectedPaths.Add((path, directory))) == installed);

            File.WriteAllBytes(installed, CreateJpeg(1920, 1080, 3));
            Reject(() => WindowsDefaultWallpaper.EnsureBundledAsset(AppContext.BaseDirectory, commonData));
        }
        finally
        {
            if (Directory.Exists(commonData)) Directory.Delete(commonData, recursive: true);
        }
    }

    private static void Expect(bool value)
    {
        if (!value) throw new Exception("Student system policy check failed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException) { return; }
        throw new Exception("Invalid system policy input was accepted.");
    }

    private static void RejectIo(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new Exception("External system policy conflict was not detected.");
    }
}
