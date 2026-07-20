using System;
using System.IO;
using System.Text;

namespace CodexRedactionGate;

public sealed record ManagedPolicyRuleMutationResult(
    bool Succeeded,
    string Code);

public sealed class ManagedPolicyRules
{
    private readonly string _policyDirectory;

    public ManagedPolicyRules(string policyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyDirectory);
        _policyDirectory = Path.GetFullPath(policyDirectory);
    }

    public ManagedPolicyRuleMutationResult AddUrlPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return new ManagedPolicyRuleMutationResult(false, "invalid_url_prefix_rule");
        }

        return Promote(AppendRule(CurrentPolicyText(), $$"""

            [[sensitive]]
            type = "url"
            match = "{{EscapeToml(prefix)}}"
            mode = "prefix"
            action = "pseudonymize_restorable"
            label = "managed url prefix"
            """));
    }

    public ManagedPolicyRuleMutationResult AddRegexRule(string type, string pattern)
    {
        if (!SensitiveEntityTypes.IsSupportedDictionaryType(type) || string.IsNullOrWhiteSpace(pattern))
        {
            return new ManagedPolicyRuleMutationResult(false, "invalid_regex_rule");
        }

        return Promote(AppendRule(CurrentPolicyText(), $$"""

            [[regex]]
            type = "{{EscapeToml(type)}}"
            pattern = "{{EscapeToml(pattern)}}"
            action = "pseudonymize_restorable"
            label = "managed regex rule"
            """));
    }

    private ManagedPolicyRuleMutationResult Promote(string candidate)
    {
        var activation = new PolicyActivationStore(_policyDirectory).PromoteCandidate(candidate);
        return new ManagedPolicyRuleMutationResult(
            activation.Activated,
            activation.Activated ? "managed_policy_rule_added" : "managed_policy_rule_rejected");
    }

    private string CurrentPolicyText()
    {
        var activePath = PolicyActivationStore.ActivePolicyPath(_policyDirectory);
        return File.Exists(activePath)
            ? File.ReadAllText(activePath)
            : """
                version = 1
                profile = "managed-policy"

                [defaults]
                unknown_high_risk = "confirm"
                secret = "redact_non_restorable"
                internal_identifier = "pseudonymize_restorable"

                [scanners]
                gitleaks_enabled = true
                gitleaks_timeout_ms = 5000
                """;
    }

    private static string AppendRule(string policyText, string ruleText)
    {
        return $"{policyText.TrimEnd()}{Environment.NewLine}{ruleText}{Environment.NewLine}";
    }

    private static string EscapeToml(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
