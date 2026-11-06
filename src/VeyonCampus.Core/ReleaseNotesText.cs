using System.Text;
using System.Text.RegularExpressions;

namespace VeyonCampus.Core;

/// <summary>
/// Turns the Markdown body of a release into readable plain text for the update panel. The release
/// notes are informational only, so a compact rendering is enough: headings lose their hashes, inline
/// emphasis loses its markers, and links keep just their label. Anything unrecognised is passed
/// through unchanged so nothing silently disappears.
/// </summary>
public static partial class ReleaseNotesText
{
    public const int DefaultMaximumCharacters = 6000;

    public static string ToPlainText(string? markdown, int maximumCharacters = DefaultMaximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(markdown) || maximumCharacters < 1) return "";
        var builder = new StringBuilder(markdown.Length);
        var inCodeFence = false;
        var blankLines = 0;
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence)
            {
                AppendLine(builder, line, ref blankLines);
                continue;
            }

            var text = line.TrimStart();
            if (text.StartsWith('>')) text = text.TrimStart('>').TrimStart();
            var headingLevel = 0;
            while (headingLevel < text.Length && text[headingLevel] == '#') headingLevel++;
            if (headingLevel is > 0 and < 7 && headingLevel < text.Length && text[headingLevel] == ' ')
            {
                text = text[(headingLevel + 1)..].TrimStart();
                if (builder.Length > 0 && blankLines == 0) AppendLine(builder, "", ref blankLines);
            }
            AppendLine(builder, StripInlineMarkup(text), ref blankLines);
        }

        var plain = builder.ToString().TrimEnd();
        if (plain.Length <= maximumCharacters) return plain;
        var cut = plain.LastIndexOf('\n', Math.Min(maximumCharacters, plain.Length - 1));
        if (cut < maximumCharacters / 2) cut = maximumCharacters;
        return plain[..cut].TrimEnd() + "\n…";
    }

    private static void AppendLine(StringBuilder builder, string text, ref int blankLines)
    {
        if (text.TrimEnd().Length == 0)
        {
            if (blankLines > 0) return;
            blankLines++;
            builder.Append('\n');
            return;
        }
        blankLines = 0;
        builder.Append(text.TrimEnd()).Append('\n');
    }

    private static string StripInlineMarkup(string text)
    {
        if (text.Length == 0) return text;
        var value = LinkPattern().Replace(text, "$1");
        value = value.Replace("**", "", StringComparison.Ordinal)
            .Replace("__", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal);
        if (value.Length > 3 && value.StartsWith("*", StringComparison.Ordinal) && value.EndsWith('*') &&
            !value.StartsWith("**", StringComparison.Ordinal))
            value = value[1..^1];
        return value;
    }

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();
}
