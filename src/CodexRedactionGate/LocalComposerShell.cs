using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

public sealed record LocalComposerSubmissionResult(
    bool OriginalPromptPermitted,
    SubmitOutcome SubmitOutcome,
    SanitizationResult SanitizationResult);

public sealed class LocalComposerShell
{
    private readonly GuardHookShell _guardHook;
    private readonly SubmitOwningAdapter _submitAdapter;

    public LocalComposerShell(ISanitizer sanitizer, SubmitOwningAdapter submitAdapter)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(submitAdapter);

        _guardHook = new GuardHookShell(sanitizer);
        _submitAdapter = submitAdapter;
    }

    public LocalComposerSubmissionResult Submit(string prompt, IReadOnlyList<ContentPart>? additionalParts = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var contentParts = new List<ContentPart>
        {
            new("prompt", ContentSources.PromptText, prompt, new Dictionary<string, string>())
        };

        if (additionalParts is not null)
        {
            contentParts.AddRange(additionalParts);
        }

        var request = new SanitizeRequest(
            ContentParts: contentParts,
            Context: new SanitizationContext("local-composer", null, null, null, "default"),
            Options: new SanitizationOptions(false, false, "local"));
        var guardDecision = _guardHook.Evaluate(request);
        var submitOutcome = guardDecision.PermitOriginalPrompt || guardDecision.RequiresConfirmationFlow
            ? _submitAdapter.Handle(guardDecision.SanitizationResult)
            : new SubmitOutcome(false, "blocked");

        return new LocalComposerSubmissionResult(
            guardDecision.PermitOriginalPrompt,
            submitOutcome,
            guardDecision.SanitizationResult);
    }
}
