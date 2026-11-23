using System.Text;

namespace VeyonCampus.App;

/// <summary>
/// A private key the local Veyon WebAPI accepts. The PEM never leaves this object graph: it is not
/// logged, not put in exception messages, and not part of any diagnostic string.
/// </summary>
internal sealed record VeyonKeyCredential(string KeyName, string PrivateKeyPem)
{
    public override string ToString() => $"VeyonKeyCredential({KeyName})";
}

/// <summary>
/// Reads the teacher machine's Veyon key pair once per screen-wall session. The wall talks to the
/// WebAPI over loopback only, and that API's key authentication takes the private key in the request
/// body, so the material is loaded lazily and held in memory for the lifetime of the wall.
/// </summary>
internal static class VeyonKeyMaterial
{
    private const int MaximumPrivateKeyBytes = 256 * 1024;

    internal static string ResolvePrivateKeyRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Veyon", "keys", "private");

    /// <summary>
    /// Returns every usable key directory, ordered by name so the same key is preferred on each start.
    /// A missing directory or an unreadable file yields an empty list instead of throwing: the wall has
    /// to report "no key" as a state, not crash the classroom page.
    /// </summary>
    internal static IReadOnlyList<VeyonKeyCredential> TryLoad()
    {
        try
        {
            var root = ResolvePrivateKeyRoot();
            if (!Directory.Exists(root)) return Array.Empty<VeyonKeyCredential>();
            var credentials = new List<VeyonKeyCredential>();
            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                var path = Path.Combine(directory, "key");
                if (!File.Exists(path)) continue;
                var length = new FileInfo(path).Length;
                if (length is <= 0 or > MaximumPrivateKeyBytes) continue;
                var pem = File.ReadAllText(path, Encoding.UTF8);
                if (!pem.Contains("PRIVATE KEY", StringComparison.Ordinal)) continue;
                credentials.Add(new VeyonKeyCredential(Path.GetFileName(directory), pem));
            }

            return credentials;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.Security.SecurityException or ArgumentException or
                                           NotSupportedException)
        {
            return Array.Empty<VeyonKeyCredential>();
        }
    }
}
