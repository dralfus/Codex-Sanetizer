using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

public interface IRestorer
{
    RestorationResult Restore(RestoreRequest request);
}

public sealed record RestoreRequest(
    string SanitizedText,
    IReadOnlyList<Replacement> Replacements);

public sealed record RestorationResult(
    string Text,
    RestorationMetadata Metadata,
    IReadOnlyList<Warning> Warnings);

public sealed record RestorationMetadata(
    bool LocalSensitive,
    IReadOnlyDictionary<string, int> RestoredPseudonymCountsByType);

public sealed record RestoredOutputSubmitDecision(
    bool CanSubmit,
    bool CanCopyOrUse,
    IReadOnlyList<Warning> Warnings);

public static class RestoredOutputSubmissionGuard
{
    public static RestoredOutputSubmitDecision Evaluate(RestorationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Metadata.LocalSensitive)
        {
            return new RestoredOutputSubmitDecision(
                CanSubmit: true,
                CanCopyOrUse: true,
                Warnings: Array.Empty<Warning>());
        }

        return new RestoredOutputSubmitDecision(
            CanSubmit: false,
            CanCopyOrUse: true,
            Warnings: new[]
            {
                new Warning(
                    Code: "local_sensitive_resubmission_blocked",
                    Message: "Restored local-sensitive output must be sanitized again before submission.",
                    Severity: WarningSeverity.Warning)
            });
    }

    public static RestoredOutputSubmitDecision EvaluateSanitizedOutput(SanitizationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new RestoredOutputSubmitDecision(
            CanSubmit: result.Decision != SanitizeDecision.Block,
            CanCopyOrUse: true,
            Warnings: Array.Empty<Warning>());
    }
}

public sealed class LocalRestorer : IRestorer
{
    private readonly IMappingVault _mappingVault;

    public LocalRestorer(IMappingVault mappingVault)
    {
        _mappingVault = mappingVault;
    }

    public RestorationResult Restore(RestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var restoredText = request.SanitizedText;
        var warnings = new List<Warning>();
        var restoredCountsByType = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var replacement in request.Replacements.DistinctBy(replacement => replacement.Placeholder))
        {
            if (!replacement.Restorable)
            {
                warnings.Add(new Warning(
                    Code: "non_restorable_redaction_skipped",
                    Message: "Non-restorable redaction was left unchanged.",
                    Severity: WarningSeverity.Info));
                continue;
            }

            if (!_mappingVault.TryGetOriginal(replacement.Placeholder, out var record))
            {
                warnings.Add(new Warning(
                    Code: "unknown_pseudonym",
                    Message: "Unknown pseudonym was left unchanged.",
                    Severity: WarningSeverity.Warning));
                continue;
            }

            var occurrenceCount = CountOccurrences(restoredText, replacement.Placeholder);
            restoredText = restoredText.Replace(
                replacement.Placeholder,
                record.NormalizedValue,
                StringComparison.Ordinal);
            restoredCountsByType[record.EntityType] = restoredCountsByType.GetValueOrDefault(record.EntityType) + occurrenceCount;
        }

        return new RestorationResult(
            Text: restoredText,
            Metadata: new RestorationMetadata(
                LocalSensitive: restoredCountsByType.Count > 0,
                RestoredPseudonymCountsByType: restoredCountsByType),
            Warnings: warnings);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;

        while (offset < text.Length)
        {
            var index = text.IndexOf(value, offset, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            offset = index + value.Length;
        }

        return count;
    }
}
