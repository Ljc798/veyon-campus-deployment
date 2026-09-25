using System.Security.Cryptography;
using System.Text;

namespace VeyonCampus.Core;

/// <summary>Maps a business campus ID to Veyon's stable ASCII-only key identifier.</summary>
public static class VeyonAuthKeyId
{
    public static string ForCampus(string campusId)
    {
        if (string.IsNullOrEmpty(campusId) || campusId.Length > 100 ||
            campusId.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
            throw new InvalidDataException("校区 ID 只能包含 1–100 个英文字母、数字、连字符或下划线。");

        // Encode 128 bits as letters only. Veyon 4.11.2 CLI key identifiers permit
        // letters; hashing avoids locale, punctuation, and filesystem-name ambiguity.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(campusId));
        var chars = new char[33];
        chars[0] = 'C';
        for (var i = 0; i < 16; i++)
        {
            chars[1 + i * 2] = (char)('A' + (hash[i] >> 4));
            chars[2 + i * 2] = (char)('A' + (hash[i] & 0x0F));
        }
        return new string(chars);
    }

    public static string PublicKeyForCampus(string campusId) => ForCampus(campusId) + "/public";
}
