using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

/// <summary>Reference-only owner for one admitted Send; production routing remains legacy until ticket 356.</summary>
internal sealed class ProtectedSendTransaction
{
    private readonly IProtectedSendTransactionEnvironment _environment;
    private readonly object _gate = new();
    private readonly Dictionary<AdmittedProtectedSend, ProtectedSendTerminalResult> _terminals = new();

    internal ProtectedSendTransaction(IProtectedSendTransactionEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    internal ProtectedSendTerminalResult Execute(AdmittedProtectedSend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_terminals.TryGetValue(request, out var completed)) return completed;
            try
            {
                if (!Trace(request, ProtectedSendTransactionStage.Admitted, "admitted") || !_environment.IsGenerationCurrent(request))
                    return Finish(request, ProtectedSendTerminalOutcome.Blocked, nameof(TransactionFailure.StaleGeneration));

                var session = _environment.CreateSession(request);
                if (session is null || !Matches(request, session.TargetIdentity))
                    return Finish(request, ProtectedSendTerminalOutcome.Blocked, nameof(TransactionFailure.TargetChanged));

                var read = session.Read();
                if (!read.Succeeded || read.Text is null || !Trace(request, ProtectedSendTransactionStage.Read, "read"))
                    return Finish(request, ProtectedSendTerminalOutcome.Blocked, Reason(read.Failure, "read_failed"));

                var prompt = read.Text;
                while (true)
                {
                    var sanitized = _environment.Sanitize(prompt);
                    if (!Trace(request, ProtectedSendTransactionStage.Sanitized, "sanitized"))
                        return Finish(request, ProtectedSendTerminalOutcome.Blocked, "trace_failure");
                    if (!sanitized.RequiresConfirmation) return WriteVerifyReplay(request, session, sanitized.Text);
                    if (!Trace(request, ProtectedSendTransactionStage.Confirmation, "confirmation_required"))
                        return Finish(request, ProtectedSendTerminalOutcome.Blocked, "trace_failure");

                    var decision = _environment.Confirm(sanitized);
                    if (decision.Kind == SanitizedPromptDecisionKind.Cancel)
                        return Finish(request, ProtectedSendTerminalOutcome.Cancelled, "cancelled");
                    if (decision.Kind == SanitizedPromptDecisionKind.Confirm)
                        return WriteVerifyReplay(request, session, sanitized.Text);
                    if (decision.Kind != SanitizedPromptDecisionKind.Edit || decision.EditedText is null)
                        return Finish(request, ProtectedSendTerminalOutcome.Blocked, "invalid_confirmation");
                    prompt = decision.EditedText;
                }
            }
            catch
            {
                return Finish(request, ProtectedSendTerminalOutcome.Blocked, "exception");
            }
        }
    }

    private ProtectedSendTerminalResult WriteVerifyReplay(AdmittedProtectedSend request, IProtectedSendTransactionSession session, string sanitizedText)
    {
        if (!_environment.IsGenerationCurrent(request)) return Finish(request, ProtectedSendTerminalOutcome.Blocked, nameof(TransactionFailure.StaleGeneration));
        if (!_environment.CanContinue(request)) return Finish(request, ProtectedSendTerminalOutcome.Blocked, nameof(TransactionFailure.ContinuationDenied));

        var target = session.Revalidate();
        if (!target.Succeeded || !Matches(request, target.TargetIdentity))
            return Finish(request, ProtectedSendTerminalOutcome.Blocked, Reason(target.Failure, nameof(TransactionFailure.TargetChanged)));

        using var lease = _environment.AcquireLease(request);
        if (lease is null) return Finish(request, ProtectedSendTerminalOutcome.Blocked, "lease_unavailable");

        var write = session.WriteAndVerify(sanitizedText);
        if (!write.Succeeded || !Trace(request, ProtectedSendTransactionStage.Verified, "write_verified"))
            return Finish(request, ProtectedSendTerminalOutcome.Blocked, Reason(write.Failure, "write_mismatch"));
        if (!_environment.CanContinue(request)) return Finish(request, ProtectedSendTerminalOutcome.Blocked, nameof(TransactionFailure.ContinuationDenied));
        if (!Trace(request, ProtectedSendTransactionStage.ReplayAuthorized, "replay_authorized"))
            return Finish(request, ProtectedSendTerminalOutcome.Blocked, "trace_failure");

        var replay = session.Replay();
        return replay.Succeeded
            ? Finish(request, ProtectedSendTerminalOutcome.SentSafely, "sent_safely")
            : Finish(request, ProtectedSendTerminalOutcome.Blocked, Reason(replay.Failure, "replay_failed"));
    }

    private ProtectedSendTerminalResult Finish(AdmittedProtectedSend request, ProtectedSendTerminalOutcome outcome, string reason)
    {
        if (!Trace(request, ProtectedSendTransactionStage.Terminal, outcome.ToString()))
        {
            outcome = ProtectedSendTerminalOutcome.Blocked;
            reason = "trace_failure";
        }

        var evidence = new ProtectedSendTerminalEvidence(request.Generation, outcome, reason);
        var published = false;
        try { published = _environment.PublishTerminal(evidence); } catch { }
        var result = published
            ? new ProtectedSendTerminalResult(request.Generation, outcome, reason, true)
            : new ProtectedSendTerminalResult(request.Generation, ProtectedSendTerminalOutcome.Blocked, "terminal_publication_failed", false);
        _terminals.Add(request, result);
        return result;
    }

    private bool Trace(AdmittedProtectedSend request, ProtectedSendTransactionStage stage, string code)
    {
        try { return _environment.PublishTrace(new ProtectedSendTraceEvidence(request.Generation, stage, code)); }
        catch { return false; }
    }

    private static bool Matches(AdmittedProtectedSend request, string? targetIdentity) => string.Equals(request.TargetIdentity, targetIdentity, StringComparison.Ordinal);
    private static string Reason(TransactionFailure? failure, string fallback) => failure?.ToString() ?? fallback;
}

/// <summary>Named deterministic/reference seam; production adapters are intentionally deferred.</summary>
internal interface IProtectedSendTransactionEnvironment
{
    IProtectedSendTransactionSession CreateSession(AdmittedProtectedSend request);
    SanitizedPrompt Sanitize(string prompt);
    SanitizedPromptDecision Confirm(SanitizedPrompt prompt);
    bool IsGenerationCurrent(AdmittedProtectedSend request);
    bool CanContinue(AdmittedProtectedSend request);
    IDisposable? AcquireLease(AdmittedProtectedSend request);
    bool PublishTrace(ProtectedSendTraceEvidence trace);
    bool PublishTerminal(ProtectedSendTerminalEvidence terminal);
}

internal sealed record AdmittedProtectedSend(long Generation, string TargetIdentity);
internal sealed record SanitizedPrompt(string Text, bool RequiresConfirmation);
internal enum SanitizedPromptDecisionKind { Confirm, Edit, Cancel }
internal sealed record SanitizedPromptDecision(SanitizedPromptDecisionKind Kind, string? EditedText)
{
    internal static SanitizedPromptDecision Confirm() => new(SanitizedPromptDecisionKind.Confirm, null);
    internal static SanitizedPromptDecision Edit(string text) => new(SanitizedPromptDecisionKind.Edit, text);
    internal static SanitizedPromptDecision Cancel() => new(SanitizedPromptDecisionKind.Cancel, null);
}

internal interface IProtectedSendTransactionSession
{
    string TargetIdentity { get; }
    TransactionStageResult Read();
    TransactionStageResult Revalidate();
    TransactionStageResult WriteAndVerify(string sanitizedText);
    TransactionStageResult Replay();
}

internal enum TransactionFailure { ReadFailed, TargetChanged, StaleGeneration, WriteMismatch, ReplayFailed, ReplayUncertain, ContinuationDenied }
internal sealed record TransactionStageResult(bool Succeeded, string? Text, string? TargetIdentity, TransactionFailure? Failure)
{
    internal static TransactionStageResult Success(string? text = null, string? targetIdentity = null) => new(true, text, targetIdentity, null);
    internal static TransactionStageResult Failed(TransactionFailure failure) => new(false, null, null, failure);
}

internal enum ProtectedSendTransactionStage { Admitted, Read, Sanitized, Confirmation, Verified, ReplayAuthorized, Terminal }
internal enum ProtectedSendTerminalOutcome { SentSafely, Blocked, Cancelled }
internal sealed record ProtectedSendTraceEvidence(long Generation, ProtectedSendTransactionStage Stage, string Code);
internal sealed record ProtectedSendTerminalEvidence(long Generation, ProtectedSendTerminalOutcome Outcome, string Reason);
internal sealed record ProtectedSendTerminalResult(long Generation, ProtectedSendTerminalOutcome Outcome, string Reason, bool TerminalPublished);
