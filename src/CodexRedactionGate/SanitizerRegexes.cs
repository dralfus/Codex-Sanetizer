using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

internal static class SanitizerRegexes
{
    public static readonly Regex Url = new(
        @"https?://[^\s<>()]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    public static readonly Regex Domain = new(
        @"\b[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static readonly Regex Ipv4 = new(
        @"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?!\d|\.\d)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static readonly Regex Cidr = new(
        @"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}/(?:[0-9]|[12]\d|3[0-2])(?![\d/])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static readonly Regex Email = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,63}\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    public static readonly Regex WindowsUserPath = new(
        @"(?<!\w)(?:[A-Z]:\\Users\\[^\s\\/:*?""<>|]+(?:\\[^\s\\/:*?""<>|]+)+|%USERPROFILE%\\[^\s\\/:*?""<>|]+(?:\\[^\s\\/:*?""<>|]+)*)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    public static readonly Regex WindowsPromptUserPath = new(
        @"(?<!\w)[A-Z]:\\Users\\(?<username>[^\s\\/:*?""<>|]+)(?=>)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    public static readonly Regex UnixPath = new(
        @"(?<!\w)/(?:home|Users|workspace|mnt|var|etc|opt|srv|tmp)/[^\s<>()]+(?:/[^\s<>()]+)*",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static readonly Regex ConnectionString = new(
        @"(?<!\w)(?:Server|Host|Data Source|Address|Addr|Network Address)\s*=[^;\r\n]+(?:;\s*(?:Database|Initial Catalog|User Id|Username|User|Uid|Password|Pwd|Port|Encrypt|TrustServerCertificate)\s*=[^;\r\n]+){2,};?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    public static readonly Regex TokenValue = new(
        @"\b(?:api[_-]?key|token|access[_-]?token)\s*[:=]\s*([A-Z0-9._-]{16,})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    public static readonly Regex PasswordValue = new(
        @"\b(?:password|passwd|pwd)\s*[:=]\s*([^\s;]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    public static readonly Regex PrivateKey = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
}

internal static class TextSpanUtilities
{
    public static IReadOnlyList<int> FindOffsets(string text, string value)
    {
        var offsets = new List<int>();
        var index = 0;

        while (index < text.Length)
        {
            var matchIndex = text.IndexOf(value, index, StringComparison.Ordinal);
            if (matchIndex < 0)
            {
                break;
            }

            offsets.Add(matchIndex);
            index = matchIndex + value.Length;
        }

        return offsets;
    }

    public static IReadOnlyList<int> FindOffsetsForEntity(string text, string value, string entityType)
    {
        return entityType == SensitiveEntityTypes.Username
            ? FindUsernameOffsets(text, value)
            : FindOffsets(text, value);
    }

    public static IReadOnlyList<int> FindUsernameOffsets(string text, string value)
    {
        var offsets = new List<int>();
        var index = 0;

        while (index < text.Length)
        {
            var matchIndex = text.IndexOf(value, index, StringComparison.Ordinal);
            if (matchIndex < 0)
            {
                break;
            }

            var endExclusive = matchIndex + value.Length;
            if (IsUsernameBoundary(text, matchIndex - 1)
                && IsUsernameBoundary(text, endExclusive))
            {
                offsets.Add(matchIndex);
            }

            index = matchIndex + value.Length;
        }

        return offsets;
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

    public static bool ContainsPasswordKey(string value)
    {
        return value.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Password =", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Pwd=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Pwd =", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPrivateCidr(string value)
    {
        var slashIndex = value.IndexOf('/');
        return slashIndex > 0
            && IsPrivateIpv4(value[..slashIndex]);
    }

    public static bool IsPrivateIpv4(string value)
    {
        var octets = value.Split('.');
        if (octets.Length != 4
            || !octets.All(octet => byte.TryParse(octet, out _)))
        {
            return false;
        }

        var first = byte.Parse(octets[0]);
        var second = byte.Parse(octets[1]);

        return first == 10
            || first == 127
            || (first == 169 && second == 254)
            || (first == 172 && second is >= 16 and <= 31)
            || (first == 192 && second == 168);
    }

    public static string TrimTrailingUrlPunctuation(string value)
    {
        return value.TrimEnd('.', ',', ';', ':', '!', '?');
    }

    public static string TrimTrailingPathPunctuation(string value)
    {
        return value.TrimEnd('.', ',', ';', '!', '?');
    }
}
