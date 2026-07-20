using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

public sealed record PolicySource(string Name, RedactionPolicy Policy);

public sealed record EffectivePolicyReport(
    IReadOnlyList<string> SourcePrecedence,
    IReadOnlyList<string> ActiveProfileIds,
    IReadOnlyDictionary<string, int> RuleCounts,
    IReadOnlyDictionary<string, string> WinningSourceByArea);

public static class PolicyPrecedenceReporter
{
    public static EffectivePolicyReport Build(IReadOnlyList<PolicySource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var ordered = sources.Where(source => !string.IsNullOrWhiteSpace(source.Name)).ToArray();
        return new EffectivePolicyReport(
            SourcePrecedence: ordered.Select(source => source.Name).ToArray(),
            ActiveProfileIds: ordered.Select(source => source.Policy.Profile).ToArray(),
            RuleCounts: new Dictionary<string, int>
            {
                ["allow"] = ordered.Sum(source => source.Policy.AllowRules.Count),
                ["sensitive"] = ordered.Sum(source => source.Policy.SensitiveRules.Count),
                ["regex"] = ordered.Sum(source => source.Policy.RegexRules.Count),
                ["block"] = ordered.Sum(source => source.Policy.BlockRules.Count)
            },
            WinningSourceByArea: new Dictionary<string, string>
            {
                ["defaults"] = LastSourceName(ordered),
                ["scanners"] = LastSourceName(ordered),
                ["allow"] = LastSourceNameWithRules(ordered, source => source.Policy.AllowRules.Count),
                ["sensitive"] = LastSourceNameWithRules(ordered, source => source.Policy.SensitiveRules.Count),
                ["regex"] = LastSourceNameWithRules(ordered, source => source.Policy.RegexRules.Count),
                ["block"] = LastSourceNameWithRules(ordered, source => source.Policy.BlockRules.Count),
                ["conflicts"] = "last_source_wins"
            });
    }

    private static string LastSourceName(IReadOnlyList<PolicySource> sources)
    {
        return sources.LastOrDefault()?.Name ?? "none";
    }

    private static string LastSourceNameWithRules(
        IReadOnlyList<PolicySource> sources,
        Func<PolicySource, int> ruleCount)
    {
        return sources.LastOrDefault(source => ruleCount(source) > 0)?.Name ?? "none";
    }
}
