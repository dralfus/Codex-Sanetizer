using System;
using System.Collections.Generic;
using System.Text;

namespace CodexRedactionGate;

internal static class ComposerTextFormatting
{
    public static string NormalizeLineEndings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return text;
        }

        var newline = Environment.NewLine;
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                builder.Append(newline);
                continue;
            }

            if (character is '\n' or '\u2028' or '\u2029')
            {
                builder.Append(newline);
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    public static bool HasSameContentIgnoringWhitespace(string first, string second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return string.Equals(
            CollapseWhitespace(first),
            CollapseWhitespace(second),
            StringComparison.Ordinal);
    }

    public static IReadOnlyDictionary<string, string> Diagnostics(
        string rawText,
        string normalizedText,
        string captureStrategy)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ArgumentNullException.ThrowIfNull(normalizedText);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureStrategy);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["captured_length"] = normalizedText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["capture_strategy"] = captureStrategy,
            ["line_break_count"] = CountLineBreaks(normalizedText).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["line_ending_style"] = DescribeLineEndings(rawText),
            ["format_preserved"] = "true"
        };
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingWhitespace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = true;
                continue;
            }

            if (pendingWhitespace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(character);
            pendingWhitespace = false;
        }

        return builder.ToString();
    }

    private static int CountLineBreaks(string text)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                count++;
            }
            else if (text[index] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static string DescribeLineEndings(string text)
    {
        var hasCrLf = false;
        var hasLf = false;
        var hasCr = false;
        var hasUnicode = false;

        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '\r' when index + 1 < text.Length && text[index + 1] == '\n':
                    hasCrLf = true;
                    index++;
                    break;
                case '\r':
                    hasCr = true;
                    break;
                case '\n':
                    hasLf = true;
                    break;
                case '\u2028':
                case '\u2029':
                    hasUnicode = true;
                    break;
            }
        }

        var styles = new List<string>();
        if (hasCrLf)
        {
            styles.Add("crlf");
        }

        if (hasLf)
        {
            styles.Add("lf");
        }

        if (hasCr)
        {
            styles.Add("cr");
        }

        if (hasUnicode)
        {
            styles.Add("unicode");
        }

        return styles.Count == 0 ? "none" : string.Join("+", styles);
    }
}
