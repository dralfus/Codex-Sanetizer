using System;

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

    public SubmitOutcome Handle(SanitizationResult result)
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

        return Submit(decision.Payload.SanitizedText);
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
