using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

internal static class DictionaryTermMatching
{
    public static string NormalizeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    public static IReadOnlyList<TextSpanMatch> FindMatches(string text, string value, string entityType)
    {
        return string.Equals(entityType, SensitiveEntityTypes.Username, StringComparison.Ordinal)
            ? FindNormalizedMatches(text, value, requireUsernameBoundary: true)
            : FindNormalizedMatches(text, value, requireUsernameBoundary: false);
    }

    private static IReadOnlyList<TextSpanMatch> FindNormalizedMatches(
        string text,
        string value,
        bool requireUsernameBoundary)
    {
        var matches = new List<TextSpanMatch>();
        var normalizedValue = NormalizeKey(value);
        if (normalizedValue.Length == 0)
        {
            return Array.Empty<TextSpanMatch>();
        }

        var normalizedText = CreateNormalizedProjection(text);
        if (normalizedText.Text.Length < normalizedValue.Length)
        {
            return Array.Empty<TextSpanMatch>();
        }

        var index = 0;

        while (index < normalizedText.Text.Length)
        {
            var matchIndex = normalizedText.Text.IndexOf(normalizedValue, index, StringComparison.Ordinal);
            if (matchIndex < 0)
            {
                break;
            }

            var start = normalizedText.OriginalIndexes[matchIndex];
            var endExclusive = normalizedText.OriginalIndexes[matchIndex + normalizedValue.Length - 1] + 1;
            if (HasBoundaries(text, start, endExclusive, requireUsernameBoundary))
            {
                matches.Add(new TextSpanMatch(start, endExclusive - start));
            }

            index = matchIndex + normalizedValue.Length;
        }

        return matches;
    }

    private static NormalizedTextProjection CreateNormalizedProjection(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var originalIndexes = new List<int>(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (!char.IsLetterOrDigit(character))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
            originalIndexes.Add(index);
        }

        return new NormalizedTextProjection(builder.ToString(), originalIndexes.ToArray());
    }

    private static bool HasBoundaries(
        string text,
        int start,
        int endExclusive,
        bool requireUsernameBoundary)
    {
        return requireUsernameBoundary
            ? IsUsernameBoundary(text, start - 1) && IsUsernameBoundary(text, endExclusive)
            : IsTermBoundary(text, start - 1) && IsTermBoundary(text, endExclusive);
    }

    private static bool IsTermBoundary(string text, int index)
    {
        return index < 0
            || index >= text.Length
            || !char.IsLetterOrDigit(text[index]);
    }

    private static bool IsUsernameBoundary(string text, int index)
    {
        return index < 0
            || index >= text.Length
            || !IsUsernameCharacter(text[index]);
    }

    private static bool IsUsernameCharacter(char value)
    {
        return char.IsLetterOrDigit(value)
            || value is '.' or '_' or '-';
    }

    private sealed record NormalizedTextProjection(string Text, IReadOnlyList<int> OriginalIndexes);
}

internal sealed record TextSpanMatch(int Offset, int Length);
