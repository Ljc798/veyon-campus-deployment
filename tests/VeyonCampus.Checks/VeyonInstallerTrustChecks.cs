using VeyonCampus.Core;

namespace VeyonCampus.Checks;

internal static class VeyonInstallerTrustChecks
{
    public static async Task RunAsync(string temporaryDirectory)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var store = new VeyonInstallerStore(Path.Combine(temporaryDirectory, "embedded-installer-cache"));
        var extracted = await store.EnsureAvailableAsync();
        Require(extracted.ExtractedFromApp && extracted.Trust.IsAllowed &&
                new FileInfo(extracted.InstallerPath).Length == VeyonInstallerTrust.FileSize,
            "The embedded Veyon installer did not pass its pinned trust checks.");

        var stagedInstaller = Path.Combine(temporaryDirectory, "veyon-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(extracted.InstallerPath, stagedInstaller);
        var stagedTrust = VeyonInstallerTrust.Check(stagedInstaller);
        Require(stagedTrust.IsAllowed && stagedTrust.HashMatched &&
                (!OperatingSystem.IsWindows() || stagedTrust.AuthenticodeVerified),
            "The Worker-style random staging filename changed the official installer's trust result.");

        // Allowing random staging names must never allow different installer bytes.
        using (var tampered = new FileStream(stagedInstaller, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            tampered.Position = 128;
            var original = tampered.ReadByte();
            tampered.Position = 128;
            tampered.WriteByte((byte)(original ^ 1));
        }
        var tamperedTrust = VeyonInstallerTrust.Check(stagedInstaller);
        Require(!tamperedTrust.IsAllowed && !tamperedTrust.HashMatched &&
                tamperedTrust.Detail.Contains("SHA-256", StringComparison.Ordinal),
            "A same-size modified installer was accepted after filename requirements were removed.");

        using (var truncated = new FileStream(stagedInstaller, FileMode.Open, FileAccess.Write, FileShare.None))
            truncated.SetLength(VeyonInstallerTrust.FileSize - 1);
        var truncatedTrust = VeyonInstallerTrust.Check(stagedInstaller);
        Require(!truncatedTrust.IsAllowed && !truncatedTrust.HashMatched &&
                truncatedTrust.Detail.Contains("大小不匹配", StringComparison.Ordinal),
            "A truncated installer was accepted.");

        var reused = await store.EnsureAvailableAsync();
        Require(!reused.ExtractedFromApp && reused.Trust.IsAllowed &&
                reused.InstallerPath == extracted.InstallerPath,
            "The verified embedded installer was not reused from cache.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
