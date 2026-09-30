using System.Security.Cryptography;

namespace VeyonCampus.Core;

/// <summary>
/// Validates the RSA SubjectPublicKeyInfo PEM format exported and imported by
/// Veyon's AuthKeys module (QCA/OpenSSL uses PEM_read_bio_PUBKEY).
/// </summary>
internal static class VeyonPublicKeyValidator
{
    private const string BeginMarker = "-----BEGIN PUBLIC KEY-----";
    private const string EndMarker = "-----END PUBLIC KEY-----";
    private const string Pkcs1BeginMarker = "-----BEGIN RSA PUBLIC KEY-----";

    public static RSA Import(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
            throw new InvalidDataException("Veyon 校区公钥内容为空。");
        if (pem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("学生部署包只能包含公钥，不能包含私钥。");

        var text = pem.Trim();
        if (text.StartsWith(Pkcs1BeginMarker, StringComparison.Ordinal))
            throw new InvalidDataException("Veyon 校区公钥需使用标准 SubjectPublicKeyInfo PEM（BEGIN PUBLIC KEY）；RSA PUBLIC KEY 格式不受支持。");
        if (!text.StartsWith(BeginMarker, StringComparison.Ordinal))
            throw new InvalidDataException("Veyon 校区公钥必须是以 BEGIN PUBLIC KEY 开始的完整 PEM 文件。");

        var bodyStart = BeginMarker.Length;
        if (text.AsSpan(bodyStart).StartsWith("\r\n", StringComparison.Ordinal))
            bodyStart += 2;
        else if (text.AsSpan(bodyStart).StartsWith("\n", StringComparison.Ordinal))
            bodyStart += 1;
        else
            throw new InvalidDataException("Veyon 校区公钥 BEGIN PUBLIC KEY 标记后必须换行。");

        var endMarkerIndex = text.IndexOf(EndMarker, bodyStart, StringComparison.Ordinal);
        if (endMarkerIndex < 0)
            throw new InvalidDataException("Veyon 校区公钥 PEM 缺少匹配的 END PUBLIC KEY 结束标记。");
        if (endMarkerIndex == bodyStart || text[endMarkerIndex - 1] is not ('\n' or '\r'))
            throw new InvalidDataException("Veyon 校区公钥 END PUBLIC KEY 标记必须单独占一行。");
        if (text.IndexOf(BeginMarker, bodyStart, StringComparison.Ordinal) >= 0 ||
            text.IndexOf(EndMarker, endMarkerIndex + EndMarker.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Veyon 校区公钥 PEM 只能包含一个完整的公钥块。");
        if (text[(endMarkerIndex + EndMarker.Length)..].Any(character => !char.IsWhiteSpace(character)))
            throw new InvalidDataException("Veyon 校区公钥结束标记后存在额外内容。");

        var encodedBody = text[bodyStart..endMarkerIndex];
        if (encodedBody.Any(character => !IsBase64(character) && !IsPemWhitespace(character)))
            throw new InvalidDataException("Veyon 校区公钥 PEM 主体包含非 Base64 字符。");
        var base64 = new string(encodedBody.Where(IsBase64).ToArray());
        if (base64.Length == 0)
            throw new InvalidDataException("Veyon 校区公钥 PEM 主体为空。");

        byte[] subjectPublicKeyInfo;
        try
        {
            subjectPublicKeyInfo = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Veyon 校区公钥 PEM 主体不是有效的 Base64 编码。", ex);
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != subjectPublicKeyInfo.Length)
                throw new InvalidDataException("Veyon 校区公钥编码包含额外数据。");
            if (rsa.KeySize is < 2048 or > 4096)
                throw new InvalidDataException("Veyon 校区 RSA 公钥位长必须在 2048–4096 位范围内。");
            return rsa;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            rsa.Dispose();
            throw new InvalidDataException("Veyon 校区公钥不是受支持的 RSA SubjectPublicKeyInfo；请使用 Veyon 导出的 RSA 公钥。", ex);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    private static bool IsBase64(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=';

    private static bool IsPemWhitespace(char character) => character is ' ' or '\t' or '\r' or '\n';
}
