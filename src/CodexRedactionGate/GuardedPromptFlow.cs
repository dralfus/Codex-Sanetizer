using System;

namespace CodexRedactionGate;

public sealed record GuardedPromptFlowOutcome(
    bool OriginalPromptPermitted,
    GuardHookDecision GuardDecision,
    SubmitOutcome SubmitOutcome);

public sealed class GuardedPromptFlow
{
    private readonly GuardHookShell _guardHook;
    private readonly SubmitOwningAdapter _submitAdapter;

    public GuardedPromptFlow(GuardHookShell guardHook, SubmitOwningAdapter submitAdapter)
    {
        ArgumentNullException.ThrowIfNull(guardHook);
        ArgumentNullException.ThrowIfNull(submitAdapter);

        _guardHook = guardHook;
        _submitAdapter = submitAdapter;
    }

    public GuardedPromptFlowOutcome Handle(SanitizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var guardDecision = _guardHook.Evaluate(request);
        var submitOutcome = guardDecision.PermitOriginalPrompt
            ? _submitAdapter.Handle(guardDecision.SanitizationResult)
            : guardDecision.RequiresConfirmationFlow
                ? _submitAdapter.Handle(guardDecision.SanitizationResult)
                : new SubmitOutcome(Submitted: false, Status: "blocked");

        return new GuardedPromptFlowOutcome(
            OriginalPromptPermitted: guardDecision.PermitOriginalPrompt,
            GuardDecision: guardDecision,
            SubmitOutcome: submitOutcome);
    }
}
