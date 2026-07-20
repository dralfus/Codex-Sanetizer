using System;

namespace CodexRedactionGate;

public sealed record GuardHookDecision(
    bool PermitOriginalPrompt,
    bool RequiresConfirmationFlow,
    string Reason,
    string? HandoffMode,
    SanitizationResult SanitizationResult);

public sealed class GuardHookShell
{
    private readonly ISanitizer _sanitizer;

    public GuardHookShell(ISanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        _sanitizer = sanitizer;
    }

    public GuardHookDecision Evaluate(SanitizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = _sanitizer.Sanitize(request);
        return result.Decision switch
        {
            SanitizeDecision.Allow => new GuardHookDecision(
                PermitOriginalPrompt: true,
                RequiresConfirmationFlow: false,
                Reason: "Prompt allowed.",
                HandoffMode: null,
                SanitizationResult: result),
            SanitizeDecision.Confirm => new GuardHookDecision(
                PermitOriginalPrompt: false,
                RequiresConfirmationFlow: true,
                Reason: "Sensitive content requires sanitized confirmation.",
                HandoffMode: "fallback_clipboard",
                SanitizationResult: result),
            _ => new GuardHookDecision(
                PermitOriginalPrompt: false,
                RequiresConfirmationFlow: false,
                Reason: "Prompt blocked by sanitizer policy.",
                HandoffMode: null,
                SanitizationResult: result)
        };
    }
}
