using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

public sealed record ConfirmationUiModel(
    string SanitizedPrompt,
    IReadOnlyList<HighlightedReplacementSpan> HighlightedSpans,
    IReadOnlyDictionary<string, int> CountsByType,
    IReadOnlyList<string> HighRiskWarnings,
    string PrimaryAction,
    string SecondaryAction,
    bool RawValuesVisible);

public sealed record HighlightedReplacementSpan(
    int Offset,
    int Length,
    string Text,
    string Type);

public sealed record ConfirmationDecision(
    bool Approved,
    ApprovedSanitizedPayload? Payload);

public sealed record ApprovedSanitizedPayload(string SanitizedText);

public static class ConfirmationDecisionContract
{
    public static ConfirmationDecision Confirm(ConfirmationUiModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new ConfirmationDecision(
            Approved: true,
            Payload: new ApprovedSanitizedPayload(model.SanitizedPrompt));
    }

    public static ConfirmationDecision Cancel(ConfirmationUiModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new ConfirmationDecision(
            Approved: false,
            Payload: null);
    }
}

public static class ConfirmationUiShell
{
    public static ConfirmationUiModel CreateModel(SanitizationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ConfirmationUiModel(
            SanitizedPrompt: result.SanitizedText,
            HighlightedSpans: CreateHighlightedSpans(result).ToArray(),
            CountsByType: result.AuditEvent.EntityCountsByType,
            HighRiskWarnings: CreateHighRiskWarnings(result).ToArray(),
            PrimaryAction: "Confirm sanitized prompt",
            SecondaryAction: "Cancel",
            RawValuesVisible: false);
    }

    private static IEnumerable<HighlightedReplacementSpan> CreateHighlightedSpans(SanitizationResult result)
    {
        var offsetDelta = 0;

        foreach (var replacement in result.Replacements.OrderBy(replacement => replacement.Offset))
        {
            var sanitizedOffset = replacement.Offset + offsetDelta;
            yield return new HighlightedReplacementSpan(
                Offset: sanitizedOffset,
                Length: replacement.Placeholder.Length,
                Text: replacement.Placeholder,
                Type: replacement.Type);
            offsetDelta += replacement.Placeholder.Length - replacement.Length;
        }
    }

    private static IEnumerable<string> CreateHighRiskWarnings(SanitizationResult result)
    {
        if (result.Replacements.Any(replacement => replacement.Action == PolicyActions.RedactNonRestorable))
        {
            yield return "Non-restorable secret redaction present.";
        }

        foreach (var warning in result.Warnings)
        {
            yield return warning.Message;
        }
    }
}
