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

        using var key = RSA.Create(2048);
        var publicPem = key.ExportSubjectPublicKeyInfoPem();
        var now = DateTimeOffset.UtcNow;
        var policy = StudentSystemPolicyCompiler.Create(Campus, 1, [Sid]);
        var desired = StudentSystemPolicyCompiler.DesiredValues(policy, Wallpaper);
        Expect(policy.Settings == StudentSystemPolicySettings.Default && desired.ContainsKey(
            StudentSystemPolicyResource.UserRegistry(Sid, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "Wallpaper")));
        Expect(desired.ContainsKey(StudentSystemPolicyResource.AccountPasswordChangeable(Sid)) &&
               desired[StudentSystemPolicyResource.AccountPasswordChangeable(Sid)] == StudentSystemPolicyValue.Boolean(false));
        Expect(desired.ContainsKey(StudentSystemPolicyResource.LsaRight(Sid, "SeSystemtimePrivilege")) &&
               !desired.Keys.Any(resource => resource.EndsWith("|NoControlPanel", StringComparison.Ordinal)));
        Expect(desired[StudentSystemPolicyResource.MachineRegistry(@"Software\Policies\Microsoft\Windows\Installer", "DisableMSI")] ==
               StudentSystemPolicyValue.Dword(1) &&
               desired.ContainsKey(StudentSystemPolicyResource.UserRegistry(Sid,
                   @"Software\Policies\Microsoft\WindowsStore", "RemoveWindowsStore")) &&
               !desired.ContainsKey(StudentSystemPolicyResource.MachineRegistry(
                   @"Software\Policies\Microsoft\WindowsStore", "RemoveWindowsStore")));
        var noWallpaperNeeded = StudentSystemPolicyCompiler.DesiredValues(
            policy with { Settings = policy.Settings with { LockWallpaper = false } });
        Expect(!noWallpaperNeeded.Keys.Any(resource => resource.EndsWith("|Wallpaper", StringComparison.Ordinal)));
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
        var store = new FakeStore();
        var executionCoordinator = new FakeExecutionCoordinator();
        var runtime = new StudentSystemPolicyRuntime(backend, store, Campus, publicPem, executionCoordinator);
        var applied = runtime.Apply(signed, now);
        Expect(!applied.Pending && store.State is { Pending: false } &&
               backend.Values[StudentSystemPolicyResource.AccountPasswordChangeable(Sid)] ==
               new StudentSystemPolicyValueState(true, StudentSystemPolicyValue.Boolean(false)));
        Expect(executionCoordinator.Enabled && executionCoordinator.StudentSids.SequenceEqual([Sid]));
        Expect(backend.Values[StudentSystemPolicyResource.UserRegistry(Sid,
                   @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "Wallpaper")] ==
               new StudentSystemPolicyValueState(true, StudentSystemPolicyValue.String(Path.GetFullPath(Wallpaper))));

        var externalResource = StudentSystemPolicyResource.UserRegistry(Sid,
            @"Software\Policies\Microsoft\Windows\Network Connections", "NC_LanProperties");
        backend.Values[externalResource] = new StudentSystemPolicyValueState(true, StudentSystemPolicyValue.Dword(0));
        RejectIo(() => runtime.ReadForAudit(now));
        backend.Values[externalResource] = applied.InstalledValues[externalResource];

        var disabled = StudentSystemPolicyCompiler.Create(Campus, 2, [],
            new StudentSystemPolicySettings(false, false, false, false, false, false), now.AddMinutes(1));
        runtime.Apply(StudentSystemPolicyCryptography.Sign(disabled, key), now.AddMinutes(1));
        Expect(store.State is { Pending: false, InstalledValues.Count: 0 } && !executionCoordinator.Enabled &&
               backend.Values[StudentSystemPolicyResource.AccountPasswordChangeable(Sid)].Value ==
               StudentSystemPolicyValue.Boolean(true));

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
        public void VerifyEnvironmentAndStudents(IReadOnlyList<string> studentSids)
        {
            if (studentSids.Any(sid => sid != Sid)) throw new InvalidDataException("Unexpected test SID.");
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
