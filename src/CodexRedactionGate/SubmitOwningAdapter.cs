using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

public sealed record SubmitOutcome(
    bool Submitted,
    string Status);

public interface IPromptSubmitter
{
    void Submit(string text);
}

public interface IConfirmationProvider
{
    ConfirmationDecision RequestConfirmation(ConfirmationUiModel model);
}

public sealed class SubmitOwningAdapter
{
    private readonly IPromptSubmitter _submitter;
    private readonly IConfirmationProvider _confirmationProvider;

    public SubmitOwningAdapter(IPromptSubmitter submitter, IConfirmationProvider confirmationProvider)
    {
        ArgumentNullException.ThrowIfNull(submitter);
        ArgumentNullException.ThrowIfNull(confirmationProvider);

        _submitter = submitter;
        _confirmationProvider = confirmationProvider;
    }

    public SubmitOutcome Handle(SanitizationResult result, ISanitizer? sanitizer = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Decision == SanitizeDecision.Block)
        {
            return new SubmitOutcome(Submitted: false, Status: "blocked");
        }

        if (result.Decision == SanitizeDecision.Allow)
        {
            return Submit(result.SanitizedText);
        }

        ConfirmationDecision decision;
        try
        {
            var model = ConfirmationUiShell.CreateModel(result);
            decision = _confirmationProvider.RequestConfirmation(model);
        }
        catch (Exception) when (
            OperatingSystem.IsWindows()
            || OperatingSystem.IsLinux()
            || OperatingSystem.IsMacOS())
        {
            return new SubmitOutcome(Submitted: false, Status: "confirmation_failed");
        }

        if (!decision.Approved || decision.Payload is null)
        {
            return new SubmitOutcome(Submitted: false, Status: "canceled");
        }

        var textToSubmit = decision.Payload.SanitizedText;
        if (!string.Equals(textToSubmit, result.SanitizedText, StringComparison.Ordinal))
        {
            if (sanitizer is null)
            {
                return new SubmitOutcome(Submitted: false, Status: "edited_text_verifier_missing");
            }

            var editedRequest = new SanitizeRequest(
                ContentParts: new[] { new ContentPart("prompt", ContentSources.PromptText, textToSubmit, new Dictionary<string, string>()) },
                Context: new SanitizationContext(
                    Application: "edited-prompt",
                    WorkspacePath: null,
                    ProjectId: null,
                    SessionId: null,
                    PolicyProfile: "default"),
                Options: new SanitizationOptions(
                    AllowSessionAliases: false,
                    AllowSecretStorage: false,
                    ConfirmationMode: "none"));

            var editedResult = sanitizer.Sanitize(editedRequest);

            if (editedResult.Decision == SanitizeDecision.Block)
            {
                return new SubmitOutcome(Submitted: false, Status: "edited_text_blocked");
            }

            if (editedResult.Decision == SanitizeDecision.Allow)
            {
                return Submit(editedResult.SanitizedText);
            }

            return new SubmitOutcome(Submitted: false, Status: "edited_text_requires_confirmation");
        }

        return Submit(textToSubmit);
    }

    private SubmitOutcome Submit(string text)
    {
        try
        {
            _submitter.Submit(text);
            return new SubmitOutcome(Submitted: true, Status: "submitted");
        }
        catch (Exception) when (
            OperatingSystem.IsWindows()
            || OperatingSystem.IsLinux()
            || OperatingSystem.IsMacOS())
        {
            return new SubmitOutcome(Submitted: false, Status: "submit_failed");
        }
    }
}
