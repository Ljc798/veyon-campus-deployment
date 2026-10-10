using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

/// <summary>Extracts one safe, explicit web link from a short teacher-signed classroom notice.</summary>
public static class ClassroomNoticeLinkParser
{
    private const int MaximumLinkCharacters = 2048;
    private static readonly Regex LinkPattern = new(
        @"https?://[^\s<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    public static Uri? ExtractSingleHttpLink(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var matches = LinkPattern.Matches(message);
        if (matches.Count != 1) return null;

        var match = matches[0];
        var candidate = match.Value;
        while (candidate.Length > 0 && IsTrailingPunctuation(candidate))
            candidate = candidate[..^1];
        if (candidate.Length is 0 or > MaximumLinkCharacters ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !(string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0 ||
            uri.AbsoluteUri.Length > MaximumLinkCharacters)
            return null;

        return uri;
    }

    private static bool IsTrailingPunctuation(string candidate)
    {
        var character = candidate[^1];
        if (character is ')' or ']' or '}')
        {
            var (open, close) = character switch
            {
                ')' => ('(', ')'),
                ']' => ('[', ']'),
                _ => ('{', '}')
            };
            return candidate.Count(value => value == close) > candidate.Count(value => value == open);
        }

        return character is '.' or ',' or '!' or '?' or ':' or ';' or
            '\uFF0C' or '\u3002' or '\uFF01' or '\uFF1F' or '\uFF1A' or '\uFF1B' or
            '\u201D' or '\u2019' or '\'' or '"';
    }
}
