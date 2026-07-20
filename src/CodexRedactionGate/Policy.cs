using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

public sealed record RedactionPolicy(
    int Version,
    string Profile,
    PolicyDefaults Defaults,
    PolicyScannerSettings ScannerSettings,
    IReadOnlyList<PolicyRule> AllowRules,
    IReadOnlyList<PolicyRule> SensitiveRules,
    IReadOnlyList<PolicyRule> RegexRules,
    IReadOnlyList<PolicyRule> BlockRules)
{
    public static RedactionPolicy BuiltInDefaults { get; } = new(
        Version: 1,
        Profile: "built-in-defaults",
        Defaults: new PolicyDefaults(
            UnknownHighRisk: PolicyActions.Confirm,
            Secret: PolicyActions.RedactNonRestorable,
            InternalIdentifier: PolicyActions.PseudonymizeRestorable),
        ScannerSettings: new PolicyScannerSettings(
            GitleaksEnabled: true,
            GitleaksTimeoutMs: 5000),
        AllowRules: Array.Empty<PolicyRule>(),
        SensitiveRules: Array.Empty<PolicyRule>(),
        RegexRules: Array.Empty<PolicyRule>(),
        BlockRules: Array.Empty<PolicyRule>());
}

public static class RedactionPolicyProfiles
{
    public static RedactionPolicy DefaultPublicAllowlist { get; } = RedactionPolicy.BuiltInDefaults with
    {
        Profile = "default-public-allowlist",
        AllowRules = new[]
        {
            AllowUrlPrefix("https://learn.microsoft.com/"),
            AllowUrlPrefix("https://docs.github.com/"),
            AllowUrlPrefix("https://www.nuget.org/"),
            AllowUrlPrefix("https://www.npmjs.com/"),
            AllowUrlPrefix("https://pypi.org/"),
            AllowUrlPrefix("https://pkg.go.dev/")
        }
    };

    private static PolicyRule AllowUrlPrefix(string match)
    {
        return new PolicyRule(
            Type: SensitiveEntityTypes.Url,
            Match: match,
            Pattern: null,
            Mode: "prefix",
            Action: PolicyActions.Allow,
            Reason: "public documentation or package registry",
            Label: null);
    }
}

public sealed record PolicyDefaults(
    string UnknownHighRisk,
    string Secret,
    string InternalIdentifier);

public sealed record PolicyScannerSettings(
    bool GitleaksEnabled,
    int GitleaksTimeoutMs);

public sealed record PolicyRule(
    string Type,
    string? Match,
    string? Pattern,
    string? Mode,
    string Action,
    string? Reason,
    string? Label);

public sealed record PolicyLoadResult(
    RedactionPolicy ActivePolicy,
    bool LoadedFromFile,
    bool Activated,
    IReadOnlyList<Warning> Warnings);

public static class PolicyActions
{
    public const string Allow = "allow";
    public const string PseudonymizeRestorable = "pseudonymize_restorable";
    public const string RedactNonRestorable = "redact_non_restorable";
    public const string SessionAlias = "session_alias";
    public const string Confirm = "confirm";
    public const string Block = "block";

    private static readonly HashSet<string> KnownActions = new(StringComparer.Ordinal)
    {
        Allow,
        PseudonymizeRestorable,
        RedactNonRestorable,
        SessionAlias,
        Confirm,
        Block
    };

    public static bool IsKnown(string action)
    {
        return KnownActions.Contains(action);
    }
}

public sealed class TomlPolicyLoader
{
    private const int MaxRegexPatternLength = 512;

    private static readonly HashSet<string> KnownModes = new(StringComparer.Ordinal)
    {
        "exact",
        "prefix",
        "suffix",
        "contains"
    };

    public PolicyLoadResult LoadOrDefault(string? policyFilePath, RedactionPolicy? lastKnownGood = null)
    {
        if (string.IsNullOrWhiteSpace(policyFilePath) || !File.Exists(policyFilePath))
        {
            return new PolicyLoadResult(
                ActivePolicy: RedactionPolicy.BuiltInDefaults,
                LoadedFromFile: false,
                Activated: true,
                Warnings: new[]
                {
                    new Warning(
                        Code: "policy_missing_using_defaults",
                        Message: "Policy file was not found; built-in safe defaults are active.",
                        Severity: WarningSeverity.Info)
                });
        }

        try
        {
            var policy = Parse(File.ReadAllLines(policyFilePath));
            return new PolicyLoadResult(
                ActivePolicy: policy,
                LoadedFromFile: true,
                Activated: true,
                Warnings: Array.Empty<Warning>());
        }
        catch (Exception exception) when (exception is PolicyValidationException or IOException or UnauthorizedAccessException)
        {
            return new PolicyLoadResult(
                ActivePolicy: lastKnownGood ?? RedactionPolicy.BuiltInDefaults,
                LoadedFromFile: true,
                Activated: false,
                Warnings: new[]
                {
                    new Warning(
                        Code: "invalid_policy_rejected",
                        Message: "Policy file is invalid and was not activated.",
                        Severity: WarningSeverity.Error)
                });
        }
    }

    private static RedactionPolicy Parse(IReadOnlyList<string> lines)
    {
        var version = 1;
        var profile = "default";
        var defaults = RedactionPolicy.BuiltInDefaults.Defaults;
        var scannerSettings = RedactionPolicy.BuiltInDefaults.ScannerSettings;
        var allowRules = new List<Dictionary<string, string>>();
        var sensitiveRules = new List<Dictionary<string, string>>();
        var regexRules = new List<Dictionary<string, string>>();
        var blockRules = new List<Dictionary<string, string>>();
        var section = PolicySection.Root;
        Dictionary<string, string>? currentRule = null;

        foreach (var rawLine in lines)
        {
            var line = RemoveComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                currentRule = null;
                section = ParseArraySection(line);
                currentRule = new Dictionary<string, string>(StringComparer.Ordinal);
                AddRulePlaceholder(section, currentRule, allowRules, sensitiveRules, regexRules, blockRules);
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                currentRule = null;
                section = ParseSection(line);
                continue;
            }

            var (key, value) = ParseKeyValue(line);

            if (section == PolicySection.Root)
            {
                if (key == "version")
                {
                    version = ParseVersion(value);
                }
                else if (key == "profile")
                {
                    profile = ParseString(value);
                }
                else
                {
                    throw new PolicyValidationException();
                }

                continue;
            }

            if (section == PolicySection.Defaults)
            {
                defaults = ApplyDefault(defaults, key, ParseString(value));
                continue;
            }

            if (section == PolicySection.Scanners)
            {
                scannerSettings = ApplyScannerSetting(scannerSettings, key, value);
                continue;
            }

            if (currentRule is null)
            {
                throw new PolicyValidationException();
            }

            currentRule[key] = ParseString(value);
        }

        if (version != 1)
        {
            throw new PolicyValidationException();
        }

        ValidateDefaultActions(defaults);
        return new RedactionPolicy(
            Version: version,
            Profile: profile,
            Defaults: defaults,
            ScannerSettings: scannerSettings,
            AllowRules: MaterializeRules(allowRules, PolicyRuleSection.Allow, RuleShape.MatchRequired, PolicyActions.Allow),
            SensitiveRules: MaterializeRules(sensitiveRules, PolicyRuleSection.Sensitive, RuleShape.MatchRequired, null),
            RegexRules: MaterializeRules(regexRules, PolicyRuleSection.Regex, RuleShape.PatternRequired, null),
            BlockRules: MaterializeRules(blockRules, PolicyRuleSection.Block, RuleShape.PatternRequired, null));
    }

    private static void AddRulePlaceholder(
        PolicySection section,
        Dictionary<string, string> currentRule,
        List<Dictionary<string, string>> allowRules,
        List<Dictionary<string, string>> sensitiveRules,
        List<Dictionary<string, string>> regexRules,
        List<Dictionary<string, string>> blockRules)
    {
        switch (section)
        {
            case PolicySection.Allow:
                allowRules.Add(currentRule);
                break;
            case PolicySection.Sensitive:
                sensitiveRules.Add(currentRule);
                break;
            case PolicySection.Regex:
                regexRules.Add(currentRule);
                break;
            case PolicySection.Block:
                blockRules.Add(currentRule);
                break;
            default:
                throw new PolicyValidationException();
        }
    }

    private static IReadOnlyList<PolicyRule> MaterializeRules(
        List<Dictionary<string, string>> ruleMaps,
        PolicyRuleSection section,
        RuleShape shape,
        string? defaultAction)
    {
        return ruleMaps
            .Select(rule => MaterializeRule(rule, section, shape, defaultAction))
            .ToArray();
    }

    private static PolicyRule MaterializeRule(
        IReadOnlyDictionary<string, string> rule,
        PolicyRuleSection section,
        RuleShape shape,
        string? defaultAction)
    {
        ValidateRuleKeys(rule, section);

        var type = Require(rule, "type");
        var action = rule.TryGetValue("action", out var configuredAction)
            ? configuredAction
            : defaultAction ?? throw new PolicyValidationException();

        if (!PolicyActions.IsKnown(action))
        {
            throw new PolicyValidationException();
        }

        var match = shape == RuleShape.MatchRequired ? Require(rule, "match") : Optional(rule, "match");
        var pattern = shape == RuleShape.PatternRequired ? Require(rule, "pattern") : Optional(rule, "pattern");
        var mode = Optional(rule, "mode");

        if (section == PolicyRuleSection.Regex && pattern is not null)
        {
            ValidateRegexPattern(pattern);
        }

        if (shape == RuleShape.MatchRequired)
        {
            mode ??= "exact";
            if (!KnownModes.Contains(mode))
            {
                throw new PolicyValidationException();
            }
        }

        return new PolicyRule(
            Type: type,
            Match: match,
            Pattern: pattern,
            Mode: mode,
            Action: action,
            Reason: Optional(rule, "reason"),
            Label: Optional(rule, "label"));
    }

    private static void ValidateRuleKeys(IReadOnlyDictionary<string, string> rule, PolicyRuleSection section)
    {
        var allowedKeys = section switch
        {
            PolicyRuleSection.Allow => new HashSet<string>(StringComparer.Ordinal)
            {
                "type",
                "match",
                "mode",
                "reason",
                "label"
            },
            PolicyRuleSection.Sensitive => new HashSet<string>(StringComparer.Ordinal)
            {
                "type",
                "match",
                "mode",
                "action",
                "label"
            },
            PolicyRuleSection.Block => new HashSet<string>(StringComparer.Ordinal)
            {
                "type",
                "pattern",
                "action",
                "label"
            },
            PolicyRuleSection.Regex => new HashSet<string>(StringComparer.Ordinal)
            {
                "type",
                "pattern",
                "action",
                "label"
            },
            _ => throw new PolicyValidationException()
        };

        if (rule.Keys.Any(key => !allowedKeys.Contains(key)))
        {
            throw new PolicyValidationException();
        }
    }

    private static void ValidateRegexPattern(string pattern)
    {
        if (pattern.Length > MaxRegexPatternLength || HasNestedQuantifiedGroup(pattern))
        {
            throw new PolicyValidationException();
        }

        try
        {
            _ = new Regex(
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromSeconds(1));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new PolicyValidationException();
        }
    }

    private static bool HasNestedQuantifiedGroup(string pattern)
    {
        var groups = new Stack<RegexGroupState>();
        var escaped = false;
        var inCharacterClass = false;
        var lastToken = RegexTokenKind.None;

        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];

            if (escaped)
            {
                escaped = false;
                lastToken = RegexTokenKind.Other;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '[')
            {
                inCharacterClass = true;
                lastToken = RegexTokenKind.Other;
                continue;
            }

            if (character == ']' && inCharacterClass)
            {
                inCharacterClass = false;
                lastToken = RegexTokenKind.Other;
                continue;
            }

            if (inCharacterClass)
            {
                continue;
            }

            if (character == '(')
            {
                groups.Push(new RegexGroupState());
                lastToken = RegexTokenKind.None;
                continue;
            }

            if (character == ')' && groups.Count > 0)
            {
                var group = groups.Pop();
                lastToken = group.ContainsQuantifier
                    ? RegexTokenKind.GroupWithQuantifier
                    : RegexTokenKind.Other;
                continue;
            }

            if (IsQuantifierStart(pattern, index))
            {
                if (lastToken == RegexTokenKind.GroupWithQuantifier)
                {
                    return true;
                }

                if (groups.TryPeek(out var currentGroup))
                {
                    currentGroup.ContainsQuantifier = true;
                }

                if (character == '{')
                {
                    index = pattern.IndexOf('}', index);
                    if (index < 0)
                    {
                        return false;
                    }
                }

                lastToken = RegexTokenKind.Other;
                continue;
            }

            lastToken = RegexTokenKind.Other;
        }

        return false;
    }

    private static bool IsQuantifierStart(string pattern, int index)
    {
        var character = pattern[index];

        if (character is '*' or '+' or '?')
        {
            return true;
        }

        if (character != '{')
        {
            return false;
        }

        var closeIndex = pattern.IndexOf('}', index);
        if (closeIndex < 0)
        {
            return false;
        }

        var content = pattern[(index + 1)..closeIndex];
        return content.Length > 0
            && content.All(character => char.IsDigit(character) || character == ',');
    }

    private static string Require(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new PolicyValidationException();
        }

        return value;
    }

    private static string? Optional(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) ? value : null;
    }

    private static PolicyDefaults ApplyDefault(PolicyDefaults defaults, string key, string value)
    {
        return key switch
        {
            "unknown_high_risk" => defaults with { UnknownHighRisk = value },
            "secret" => defaults with { Secret = value },
            "internal_identifier" => defaults with { InternalIdentifier = value },
            _ => throw new PolicyValidationException()
        };
    }

    private static PolicyScannerSettings ApplyScannerSetting(PolicyScannerSettings settings, string key, string value)
    {
        return key switch
        {
            "gitleaks_enabled" => settings with { GitleaksEnabled = ParseBoolean(value) },
            "gitleaks_timeout_ms" => settings with { GitleaksTimeoutMs = ParsePositiveInteger(value) },
            _ => throw new PolicyValidationException()
        };
    }

    private static void ValidateDefaultActions(PolicyDefaults defaults)
    {
        if (!PolicyActions.IsKnown(defaults.UnknownHighRisk)
            || !PolicyActions.IsKnown(defaults.Secret)
            || !PolicyActions.IsKnown(defaults.InternalIdentifier))
        {
            throw new PolicyValidationException();
        }
    }

    private static PolicySection ParseSection(string line)
    {
        return line switch
        {
            "[defaults]" => PolicySection.Defaults,
            "[scanners]" => PolicySection.Scanners,
            _ => throw new PolicyValidationException()
        };
    }

    private static PolicySection ParseArraySection(string line)
    {
        return line switch
        {
            "[[allow]]" => PolicySection.Allow,
            "[[sensitive]]" => PolicySection.Sensitive,
            "[[regex]]" => PolicySection.Regex,
            "[[block]]" => PolicySection.Block,
            _ => throw new PolicyValidationException()
        };
    }

    private static (string Key, string Value) ParseKeyValue(string line)
    {
        var separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
        {
            throw new PolicyValidationException();
        }

        return (line[..separatorIndex].Trim(), line[(separatorIndex + 1)..].Trim());
    }

    private static int ParseVersion(string value)
    {
        return ParsePositiveInteger(value);
    }

    private static int ParsePositiveInteger(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            throw new PolicyValidationException();
        }

        return parsed;
    }

    private static bool ParseBoolean(string value)
    {
        return value switch
        {
            "true" => true,
            "false" => false,
            _ => throw new PolicyValidationException()
        };
    }

    private static string ParseString(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            throw new PolicyValidationException();
        }

        return value[1..^1]
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static string RemoveComment(string line)
    {
        var inString = false;

        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"' && (index == 0 || line[index - 1] != '\\'))
            {
                inString = !inString;
            }

            if (!inString && line[index] == '#')
            {
                return line[..index];
            }
        }

        return line;
    }

    private enum PolicySection
    {
        Root,
        Defaults,
        Scanners,
        Allow,
        Sensitive,
        Regex,
        Block
    }

    private enum RuleShape
    {
        MatchRequired,
        PatternRequired
    }

    private enum PolicyRuleSection
    {
        Allow,
        Sensitive,
        Regex,
        Block
    }

    private enum RegexTokenKind
    {
        None,
        Other,
        GroupWithQuantifier
    }

    private sealed class RegexGroupState
    {
        public bool ContainsQuantifier { get; set; }
    }

    private sealed class PolicyValidationException : Exception
    {
    }
}
