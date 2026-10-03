using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace VeyonCampus.Core;

/// <summary>Normalizes optional Veyon host overrides without performing DNS or network access.</summary>
public static class VeyonHostAddress
{
    public static string NormalizeOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (value != value.Trim() || value.Length > 253 ||
            value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) ||
                                   character is ';' or '"' or '/' or '\\' or '@' or '?' or '#'))
            throw new InvalidDataException("主机名或 IP 不能包含首尾空格、分隔符、URL 路径或控制字符。");

        if (value.Contains(':'))
        {
            if (IPAddress.TryParse(value, out var ipv6) && ipv6.AddressFamily == AddressFamily.InterNetworkV6)
                return ipv6.ToString().ToLowerInvariant();
            throw new InvalidDataException("自定义地址中的冒号只用于有效 IPv6 地址；请不要填写端口或 URL。");
        }

        var labels = value.Split('.');
        if (labels.Length == 4 && labels.All(label => label.Length > 0 &&
                label.All(character => character is >= '0' and <= '9')))
        {
            var octets = new int[4];
            for (var index = 0; index < labels.Length; index++)
            {
                if (!int.TryParse(labels[index], NumberStyles.None, CultureInfo.InvariantCulture, out octets[index]) ||
                    octets[index] > 255)
                    throw new InvalidDataException("自定义 IPv4 地址的每段必须是 0–255。");
            }
            return string.Join(".", octets.Select(octet => octet.ToString(CultureInfo.InvariantCulture)));
        }

        if (value.All(character => character is >= '0' and <= '9' or '.'))
            throw new InvalidDataException("IPv4 地址须使用四段点分十进制格式。");

        try
        {
            var ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(value);
            if (ascii.Length is < 1 or > 253 || Uri.CheckHostName(ascii) != UriHostNameType.Dns)
                throw new InvalidDataException("自定义主机名格式无效；请填写主机名、域名或 IP 地址。");
            return ascii.ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("自定义主机名格式无效；请填写主机名、域名或 IP 地址。", exception);
        }
    }
}
