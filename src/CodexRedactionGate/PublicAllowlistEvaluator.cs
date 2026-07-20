using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

internal sealed class PublicAllowlistEvaluator
{
    private readonly RedactionPolicy _policy;

    public PublicAllowlistEvaluator(RedactionPolicy policy)
    {
        _policy = policy;
    }

    public bool IsPublicAllowlistedUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var valueUri))
        {
            return false;
        }

        foreach (var rule in _policy.AllowRules)
        {
            if (rule.Type != SensitiveEntityTypes.Url || rule.Match is null)
            {
                continue;
            }

            if (!Uri.TryCreate(rule.Match, UriKind.Absolute, out var ruleUri)
                || !HasSameOrigin(valueUri, ruleUri))
            {
                continue;
            }

            if (rule.Mode == "prefix"
                && valueUri.AbsolutePath.StartsWith(ruleUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if ((rule.Mode is null or "exact")
                && string.Equals(valueUri.PathAndQuery, ruleUri.PathAndQuery, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsInsidePublicAllowlistedUrl(string text, int offset)
    {
        foreach (Match match in SanitizerRegexes.Url.Matches(text))
        {
            var value = TextSpanUtilities.TrimTrailingUrlPunctuation(match.Value);
            if (offset >= match.Index
                && offset < match.Index + value.Length
                && IsPublicAllowlistedUrl(value))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsInternalUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (IsSensitiveUrl(value) || IsInternalDomain(uri.Host));
    }

    public bool IsInternalDomain(string value)
    {
        var normalized = value.TrimEnd('.').ToLowerInvariant();

        if (normalized.EndsWith(".local", StringComparison.Ordinal)
            || normalized.EndsWith(".internal", StringComparison.Ordinal)
            || normalized.EndsWith(".corp", StringComparison.Ordinal)
            || normalized.Contains(".corp.", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var rule in _policy.SensitiveRules)
        {
            if (rule.Type != SensitiveEntityTypes.Domain || rule.Match is null)
            {
                continue;
            }

            var ruleMatch = rule.Match.TrimEnd('.').ToLowerInvariant();
            if ((rule.Mode is null or "exact") && normalized == ruleMatch)
            {
                return true;
            }

            if (rule.Mode == "suffix"
                && (normalized == ruleMatch || normalized.EndsWith($".{ruleMatch}", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsSensitiveUrl(string value)
    {
        foreach (var rule in _policy.SensitiveRules)
        {
            if (rule.Type != SensitiveEntityTypes.Url || rule.Match is null)
            {
                continue;
            }

            if ((rule.Mode is null or "exact")
                && string.Equals(value, rule.Match, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (rule.Mode == "prefix"
                && value.StartsWith(rule.Match, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (rule.Mode == "suffix"
                && value.EndsWith(rule.Match, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (rule.Mode == "contains"
                && value.Contains(rule.Match, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSameOrigin(Uri valueUri, Uri ruleUri)
    {
        return string.Equals(valueUri.Scheme, ruleUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(valueUri.Host.TrimEnd('.'), ruleUri.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            && valueUri.Port == ruleUri.Port;
    }
}
