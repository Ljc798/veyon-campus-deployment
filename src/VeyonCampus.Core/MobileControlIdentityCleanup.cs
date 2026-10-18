using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace VeyonCampus.Core;

/// <summary>Removes the teacher mobile-control certificate only when this product pinned it.</summary>
internal static class MobileControlIdentityCleanup
{
    private const string RootSubject = "CN=Veyon Campus Mobile Control CA";
    private const string RegistryPath = @"Software\VeyonCampus\MobileControl";
    private const string ThumbprintValue = "TlsRootCertificateThumbprint";
    private const string KeyName = "VeyonCampus.MobileControl.Root";
    private const int KeySize = 3072;
    private static readonly CngProvider Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    [SupportedOSPlatform("windows")]
    internal static void RemoveOwnedIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? pinnedThumbprint;
        bool hasSettings;
        using (var settings = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false))
        {
            hasSettings = settings is not null;
            pinnedThumbprint = settings?.GetValue(ThumbprintValue) as string;
        }
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        foreach (var certificate in store.Certificates.Cast<X509Certificate2>()
                     .Where(item => string.Equals(item.Subject, RootSubject, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            using (certificate)
            using (var privateKey = certificate.GetRSAPrivateKey())
            {
                var ownsKey = privateKey is RSACng cng &&
                    string.Equals(cng.Key.KeyName, KeyName, StringComparison.OrdinalIgnoreCase);
                var isPinned = IsThumbprint(pinnedThumbprint) &&
                    string.Equals(NormalizeThumbprint(certificate.Thumbprint),
                        NormalizeThumbprint(pinnedThumbprint!), StringComparison.OrdinalIgnoreCase);
                if (!ownsKey && !isPinned) continue;
                store.Remove(certificate);
                if (ownsKey && privateKey is RSACng ownedKey) ownedKey.Key.Delete();
            }
        }

        if (CngKey.Exists(KeyName, Provider))
        {
            using var orphanedKey = CngKey.Open(KeyName, Provider);
            if (orphanedKey.AlgorithmGroup == CngAlgorithmGroup.Rsa && orphanedKey.KeySize == KeySize)
                orphanedKey.Delete();
        }

        if (hasSettings)
            Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
    }

    private static bool IsThumbprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = NormalizeThumbprint(value);
        return normalized.Length == 40 && normalized.All(Uri.IsHexDigit);
    }

    private static string NormalizeThumbprint(string value) =>
        new string(value.Where(character => !char.IsWhiteSpace(character) && character != '-').ToArray()).ToUpperInvariant();
}
