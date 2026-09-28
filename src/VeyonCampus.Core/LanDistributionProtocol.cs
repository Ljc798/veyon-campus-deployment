using System.Security.Cryptography;
using System.Text;
using System.Security.Cryptography.X509Certificates;

namespace VeyonCampus.Core;

/// <summary>Shared, deliberately small pieces of the teacher/student LAN pairing protocol.</summary>
public static class LanDistributionProtocol
{
    private const string AccessAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int HttpsPort = 48731;
    public const string PackageRoute = "/v1/campus-configuration";
    public const int AccessCodeLength = 20;
    public const int CertificateCodeLength = 16;

    public static string CreateAccessCode()
    {
        var random = RandomNumberGenerator.GetBytes(AccessCodeLength);
        var chars = new char[AccessCodeLength];
        for (var i = 0; i < chars.Length; i++) chars[i] = AccessAlphabet[random[i] & 31];
        return FormatGroups(new string(chars), 4);
    }

    public static string NormalizeAccessCode(string? code)
    {
        var normalized = new string((code ?? "").Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length != AccessCodeLength || normalized.Any(character => !AccessAlphabet.Contains(character)))
            throw new InvalidDataException("局域网配对码无效。");
        return normalized;
    }

    public static bool AccessCodeMatches(string? suppliedCode, ReadOnlySpan<byte> expectedSha256)
    {
        if (expectedSha256.Length != SHA256.HashSizeInBytes) return false;
        string normalized;
        try { normalized = NormalizeAccessCode(suppliedCode); }
        catch (InvalidDataException) { return false; }
        var actual = SHA256.HashData(Encoding.ASCII.GetBytes(normalized));
        return CryptographicOperations.FixedTimeEquals(actual, expectedSha256);
    }

    public static byte[] HashAccessCode(string code)
    {
        var normalized = NormalizeAccessCode(code);
        return SHA256.HashData(Encoding.ASCII.GetBytes(normalized));
    }

    public static string CertificateCode(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var digest = SHA256.HashData(certificate.RawData);
        return FormatGroups(Convert.ToHexString(digest.AsSpan(0, CertificateCodeLength / 2)), 4);
    }

    public static byte[] NormalizeCertificateCode(string? code)
    {
        var normalized = new string((code ?? "").Where(character => character is not ('-' or ' ' or '\t' or '\r' or '\n')).ToArray());
        if (normalized.Length != CertificateCodeLength || !normalized.All(Uri.IsHexDigit))
            throw new InvalidDataException("服务器证书核对码应为 16 位十六进制字符。");
        return Convert.FromHexString(normalized);
    }

    private static string FormatGroups(string value, int groupSize) =>
        string.Join('-', Enumerable.Range(0, (value.Length + groupSize - 1) / groupSize)
            .Select(index => value.Substring(index * groupSize, Math.Min(groupSize, value.Length - index * groupSize))));
}
