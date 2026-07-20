using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

internal static class PolicyTestReporter
{
    public static IReadOnlyList<string> Render(
        SanitizationResult result,
        ManagedPolicyLoadResult policy,
        bool includeSanitizedText)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(policy);

        var lines = new List<string>
        {
            $"policy_source: {policy.Source}",
            $"policy_activated: {policy.Activated.ToString().ToLowerInvariant()}",
            $"decision: {CliOutputFormatting.FormatDecision(result.Decision)}",
            $"replacement_count: {result.Replacements.Count}"
        };

        foreach (var profile in policy.Diagnostics.ActiveProfileIds)
        {
            lines.Add($"policy_profile: {profile}");
        }

        foreach (var item in policy.Diagnostics.RuleCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            lines.Add($"rule_count.{item.Key}: {item.Value}");
        }

        foreach (var item in policy.Diagnostics.WinningSourceByArea.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            lines.Add($"rule_source.{item.Key}: {item.Value}");
        }

        if (result.Entities.Any(entity => entity.DetectorId == SanitizerPipelineConstants.DictionaryDetectorId))
        {
            lines.Add("rule_source.dictionary: managed-dictionary");
        }

        var entityCounts = result.Entities
            .GroupBy(entity => entity.Type, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        lines.Add(entityCounts.Length == 0
            ? "entity_types: none"
            : $"entity_types: {string.Join(",", entityCounts.Select(group => group.Key))}");

        foreach (var group in entityCounts)
        {
            lines.Add($"entity.{group.Key}: {group.Count()}");
        }

        foreach (var warning in result.Warnings)
        {
            lines.Add($"warning: {warning.Code}");
        }

        foreach (var warning in policy.Warnings)
        {
            lines.Add($"policy_warning: {warning.Code}");
        }

        if (includeSanitizedText)
        {
            lines.Add($"sanitized_text: {result.SanitizedText}");
        }

        return lines;
    }
}
