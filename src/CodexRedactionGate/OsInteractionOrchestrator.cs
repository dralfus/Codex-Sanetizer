using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

public sealed record OsInteractionRunOptions(
    bool DryRun,
    bool ApplySanitizedText,
    bool SubmitAfterApply)
{
    public static OsInteractionRunOptions DryRunOnly { get; } = new(true, false, false);
    public static OsInteractionRunOptions ApplyOnly { get; } = new(false, true, false);
    public static OsInteractionRunOptions ConfirmAndSend { get; } = new(false, true, true);
}

public sealed record OsInteractionResult(
    string Status,
    TextSurfaceDescriptor? Surface,
    SanitizationResult? SanitizationResult,
    ConfirmationUiModel? ConfirmationModel,
    bool Applied,
    bool Submitted,
    IReadOnlyDictionary<string, string> Diagnostics);

public sealed class OsInteractionOrchestrator
{
    private readonly ISanitizer _sanitizer;
    private readonly IActiveTextSurfaceDiscovery _surfaceDiscovery;
    private readonly ITextSurfaceReader _reader;
    private readonly ITextSurfaceWriter _writer;
    private readonly ISubmitAction _submitAction;
    private readonly IConfirmationOverlay _confirmationOverlay;

    public OsInteractionOrchestrator(
        ISanitizer sanitizer,
        IActiveTextSurfaceDiscovery surfaceDiscovery,
        ITextSurfaceReader reader,
        ITextSurfaceWriter writer,
        ISubmitAction submitAction,
        IConfirmationOverlay confirmationOverlay)
    {
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        _surfaceDiscovery = surfaceDiscovery ?? throw new ArgumentNullException(nameof(surfaceDiscovery));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _submitAction = submitAction ?? throw new ArgumentNullException(nameof(submitAction));
        _confirmationOverlay = confirmationOverlay ?? throw new ArgumentNullException(nameof(confirmationOverlay));
    }

    public OsInteractionResult RunOnce(
        OsInteractionRunOptions options,
        Func<string, string, bool>? traceStage = null,
        Func<bool>? executionGuard = null,
        Func<IDisposable?>? executionLease = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            return RunOnceInternal(options, traceStage, executionGuard, executionLease);
        }
        catch (Exception ex)
        {
            // Return fail-closed status without exposing sensitive data
            return new OsInteractionResult(
                OsInteractionStatusIds.FailedClosed,
                Surface: null,
                SanitizationResult: null,
                ConfirmationModel: null,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>
                {
                    ["exception_type"] = ex.GetType().FullName ?? ex.GetType().Name,
                    ["exception_status"] = "orchestrator_failure",
                    ["failed_closed"] = "true"
                });
        }
    }

    private OsInteractionResult RunOnceInternal(
        OsInteractionRunOptions options,
        Func<string, string, bool>? traceStage,
        Func<bool>? executionGuard,
        Func<IDisposable?>? executionLease)
    {
        var discovery = _surfaceDiscovery.DiscoverActiveSurface();
        if (!discovery.Succeeded || discovery.Surface is null || !discovery.Surface.Supported)
        {
            return Finish(discovery.Status, discovery.Surface, null, null, false, false, discovery.Diagnostics);
        }

        var surface = discovery.Surface;
        var capture = _reader.CaptureText(surface);
        if (!capture.Succeeded || capture.Text is null)
        {
            return Finish(OsInteractionStatusIds.CaptureFailed, surface, null, null, false, false, Merge(
                discovery.Diagnostics,
                capture.Diagnostics,
                ("capture_status", capture.Status)));
        }

        if (!TryTrace(traceStage, "composer_read", "capture_verified"))
        {
            return Finish(OsInteractionStatusIds.FailedClosed, surface, null, null, false, false, Merge(
                discovery.Diagnostics,
                capture.Diagnostics,
                ("trace_status", "composer_read_unavailable")));
        }

        var result = _sanitizer.Sanitize(CreateRequest(capture.Text, surface));
        var diagnostics = Merge(
            discovery.Diagnostics,
            capture.Diagnostics,
            ("profile_id", surface.ProfileId),
            ("decision", FormatDecision(result.Decision)),
            ("captured_length", capture.Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("sanitized_length", result.SanitizedText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("replacement_count", result.Replacements.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("warning_count", result.Warnings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        if (result.Decision == SanitizeDecision.Block)
        {
            return Finish(OsInteractionStatusIds.Blocked, surface, result, null, false, false, diagnostics);
        }

        if (!TryTrace(traceStage, "sanitized", "sanitization_verified"))
        {
            return Finish(OsInteractionStatusIds.FailedClosed, surface, result, null, false, false, Merge(
                diagnostics,
                ("trace_status", "sanitized_unavailable")));
        }

        ConfirmationUiModel? model = null;
        var outgoingText = result.SanitizedText;

        if (result.Decision == SanitizeDecision.Confirm)
        {
            model = ConfirmationUiShell.CreateModel(result);

            if (options.DryRun)
            {
                return Finish(OsInteractionStatusIds.DryRunConfirm, surface, result, model, false, false, diagnostics);
            }

            if (!TryTrace(traceStage, "overlay_created", "confirmation_requested"))
            {
                return Finish(OsInteractionStatusIds.FailedClosed, surface, result, model, false, false, Merge(
                    diagnostics,
                    ("trace_status", "overlay_created_unavailable")));
            }

            if (!CanExecute(executionGuard))
            {
                return ExecutionGuardFailed(surface, result, model, false, diagnostics);
            }

            var decision = _confirmationOverlay is ITracedConfirmationOverlay tracedOverlay
                ? tracedOverlay.RequestConfirmation(model, traceStage ?? AlwaysTrace)
                : _confirmationOverlay.RequestConfirmation(model);
            if (!decision.Approved || decision.Payload is null)
            {
                if (!TryTrace(traceStage, "cancelled", "user_cancelled"))
                {
                    return Finish(OsInteractionStatusIds.FailedClosed, surface, result, model, false, false, Merge(
                        diagnostics,
                        ("trace_status", "cancellation_unavailable")));
                }

                return Finish(OsInteractionStatusIds.Canceled, surface, result, model, false, false, diagnostics);
            }

            if (!TryTrace(traceStage, "approved", "user_approved"))
            {
                return Finish(OsInteractionStatusIds.FailedClosed, surface, result, model, false, false, Merge(
                    diagnostics,
                    ("trace_status", "approval_unavailable")));
            }

            outgoingText = decision.Payload.SanitizedText;
            if (!string.Equals(outgoingText, result.SanitizedText, StringComparison.Ordinal))
            {
                var editedResult = _sanitizer.Sanitize(CreateRequest(outgoingText, surface));
                if (editedResult.Decision == SanitizeDecision.Block)
                {
                    return Finish(OsInteractionStatusIds.Blocked, surface, result, model, false, false, Merge(
                        diagnostics,
                        ("edited_text_verified", "false"),
                        ("edited_text_status", "blocked")));
                }

                if (editedResult.Decision == SanitizeDecision.Confirm)
                {
                    return Finish(OsInteractionStatusIds.FailedClosed, surface, result, model, false, false, Merge(
                        diagnostics,
                        ("edited_text_verified", "false"),
                        ("edited_text_status", "requires_confirmation")));
                }

                outgoingText = editedResult.SanitizedText;
                diagnostics = Merge(
                    diagnostics,
                    ("edited_text_verified", "true"),
                    ("edited_text_status", "allow"),
                    ("edited_sanitized_length", editedResult.SanitizedText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        else if (options.DryRun)
        {
            return Finish(OsInteractionStatusIds.DryRunAllow, surface, result, null, false, false, diagnostics);
        }

        if (!options.ApplySanitizedText)
        {
            return Finish(OsInteractionStatusIds.Applied, surface, result, model, false, false, diagnostics);
        }

        if (string.Equals(outgoingText, capture.Text, StringComparison.Ordinal))
        {
            var unchangedDiagnostics = Merge(diagnostics, ("write_status", "skipped_no_changes"));
            if (!options.SubmitAfterApply)
            {
                return Finish(OsInteractionStatusIds.Applied, surface, result, model, true, false, unchangedDiagnostics);
            }

            if (!CanExecute(executionGuard))
            {
                return ExecutionGuardFailed(surface, result, model, true, unchangedDiagnostics);
            }

            var preSubmit = RediscoverSameSurface(surface);
            if (preSubmit.Status is not null)
            {
                return Finish(preSubmit.Status, preSubmit.Surface, result, model, true, false, Merge(
                    unchangedDiagnostics,
                    preSubmit.Diagnostics,
                    ("pre_submit_status", preSubmit.Status)));
            }

            var submitSurface = preSubmit.Surface ?? surface;
            if (!TryTrace(traceStage, "send_injected", "submit_requested"))
            {
                return Finish(OsInteractionStatusIds.FailedClosed, submitSurface, result, model, true, false, Merge(
                    unchangedDiagnostics,
                    ("trace_status", "send_injected_unavailable")));
            }

            if (!CanExecute(executionGuard))
            {
                return ExecutionGuardFailed(submitSurface, result, model, true, unchangedDiagnostics);
            }

            if (!TryAcquireExecutionLease(executionGuard, executionLease, out var submitLease))
            {
                return ExecutionGuardFailed(submitSurface, result, model, true, unchangedDiagnostics);
            }

            if (!CanExecute(executionGuard))
            {
                submitLease?.Dispose();
                return ExecutionGuardFailed(submitSurface, result, model, true, unchangedDiagnostics);
            }

            SubmitActionResult submitWithoutWrite;
            try
            {
                submitWithoutWrite = _submitAction.Submit(submitSurface);
            }
            finally
            {
                submitLease?.Dispose();
            }

            return submitWithoutWrite.Succeeded
                ? Finish(OsInteractionStatusIds.Submitted, submitSurface, result, model, true, true, Merge(
                    unchangedDiagnostics,
                    submitWithoutWrite.Diagnostics,
                    ("submit_status", submitWithoutWrite.Status)))
                : Finish(OsInteractionStatusIds.SubmitFailed, submitSurface, result, model, true, false, Merge(
                    unchangedDiagnostics,
                    submitWithoutWrite.Diagnostics,
                    ("submit_status", submitWithoutWrite.Status)));
        }

        var preWrite = RediscoverSameSurface(surface);
        if (preWrite.Status is not null)
        {
            return Finish(preWrite.Status, preWrite.Surface, result, model, false, false, Merge(
                diagnostics,
                preWrite.Diagnostics,
                ("pre_write_status", preWrite.Status)));
        }

        var writeSurface = preWrite.Surface ?? surface;
        if (!CanExecute(executionGuard))
        {
            return ExecutionGuardFailed(writeSurface, result, model, false, diagnostics);
        }

        if (!TryAcquireExecutionLease(executionGuard, executionLease, out var writeLease))
        {
            return ExecutionGuardFailed(writeSurface, result, model, false, diagnostics);
        }

        if (!CanExecute(executionGuard))
        {
            writeLease?.Dispose();
            return ExecutionGuardFailed(writeSurface, result, model, false, diagnostics);
        }

        TextReplacementResult replace;
        try
        {
            replace = _writer.ReplaceText(writeSurface, outgoingText);
        }
        finally
        {
            writeLease?.Dispose();
        }
        if (!replace.Succeeded)
        {
            return Finish(OsInteractionStatusIds.WriteFailed, writeSurface, result, model, false, false, Merge(
                diagnostics,
                replace.Diagnostics,
                ("write_status", replace.Status)));
        }

        var preVerify = RediscoverSameSurface(writeSurface);
        if (preVerify.Status is not null)
        {
            return Finish(preVerify.Status, preVerify.Surface, result, model, true, false, Merge(
                diagnostics,
                replace.Diagnostics,
                preVerify.Diagnostics,
                ("write_status", replace.Status),
                ("pre_verify_status", preVerify.Status)));
        }

        var verificationSurface = preVerify.Surface ?? writeSurface;
        var verificationCapture = _reader.CaptureText(verificationSurface);
        if (!verificationCapture.Succeeded
            || !string.Equals(verificationCapture.Text, outgoingText, StringComparison.Ordinal))
        {
            return Finish(OsInteractionStatusIds.VerificationFailed, verificationSurface, result, model, true, false, Merge(
                diagnostics,
                replace.Diagnostics,
                verificationCapture.Diagnostics,
                ("write_status", replace.Status),
                ("verification_status", verificationCapture.Status)));
        }

        if (!TryTrace(traceStage, "text_written", "write_verified"))
        {
            return Finish(OsInteractionStatusIds.FailedClosed, verificationSurface, result, model, true, false, Merge(
                diagnostics,
                replace.Diagnostics,
                verificationCapture.Diagnostics,
                ("write_status", replace.Status),
                ("trace_status", "text_written_unavailable")));
        }

        if (!options.SubmitAfterApply)
        {
            return Finish(OsInteractionStatusIds.Applied, verificationSurface, result, model, true, false, Merge(
                diagnostics,
                replace.Diagnostics,
                verificationCapture.Diagnostics,
                ("write_status", replace.Status),
                ("verification_status", verificationCapture.Status)));
        }

        if (!CanExecute(executionGuard))
        {
            return ExecutionGuardFailed(verificationSurface, result, model, true, Merge(
                diagnostics,
                replace.Diagnostics,
                verificationCapture.Diagnostics,
                ("write_status", replace.Status),
                ("verification_status", verificationCapture.Status)));
        }

        var replayTarget = RediscoverSameSurface(verificationSurface);
        if (replayTarget.Status is not null)
        {
            return Finish(replayTarget.Status, replayTarget.Surface, result, model, true, false, Merge(
                diagnostics,
                replace.Diagnostics,
                verificationCapture.Diagnostics,
                replayTarget.Diagnostics,
                ("write_status", replace.Status),
                ("verification_status", verificationCapture.Status),
                ("pre_submit_status", replayTarget.Status)));
        }

        var replaySurface = replayTarget.Surface ?? verificationSurface;

        if (!TryTrace(traceStage, "send_injected", "submit_requested"))
        {
            return Finish(OsInteractionStatusIds.FailedClosed, replaySurface, result, model, true, false, Merge(
                diagnostics,
                replace.Diagnostics,
                verificationCapture.Diagnostics,
                ("write_status", replace.Status),
                ("trace_status", "send_injected_unavailable")));
        }

        if (!CanExecute(executionGuard))
        {
            return ExecutionGuardFailed(replaySurface, result, model, true, Merge(
                diagnostics,
                replace.Diagnostics,
                verificationCapture.Diagnostics,
                ("write_status", replace.Status),
                ("verification_status", verificationCapture.Status)));
        }

        if (!TryAcquireExecutionLease(executionGuard, executionLease, out var replayLease))
        {
            return ExecutionGuardFailed(replaySurface, result, model, true, Merge(
                diagnostics,
                replace.Diagnostics,
                verificationCapture.Diagnostics,
                ("write_status", replace.Status),
                ("verification_status", verificationCapture.Status)));
        }

        if (!CanExecute(executionGuard))
        {
            replayLease?.Dispose();
            return ExecutionGuardFailed(replaySurface, result, model, true, Merge(
                diagnostics,
                replace.Diagnostics,
                verificationCapture.Diagnostics,
                ("write_status", replace.Status),
                ("verification_status", verificationCapture.Status)));
        }

        SubmitActionResult submit;
        try
        {
            submit = _submitAction.Submit(replaySurface);
        }
        finally
        {
            replayLease?.Dispose();
        }
        return submit.Succeeded
            ? Finish(OsInteractionStatusIds.Submitted, replaySurface, result, model, true, true, Merge(
                diagnostics,
                replace.Diagnostics,
                submit.Diagnostics,
                ("write_status", replace.Status),
                ("submit_status", submit.Status)))
            : Finish(OsInteractionStatusIds.SubmitFailed, replaySurface, result, model, true, false, Merge(
                diagnostics,
                replace.Diagnostics,
                submit.Diagnostics,
                ("write_status", replace.Status),
                ("submit_status", submit.Status)));
    }

    private static bool TryTrace(
        Func<string, string, bool>? traceStage,
        string stage,
        string resultCode)
    {
        return traceStage?.Invoke(stage, resultCode) ?? true;
    }

    private static bool AlwaysTrace(string _, string __) => true;

    private static bool CanExecute(Func<bool>? executionGuard)
    {
        if (executionGuard is null)
        {
            return true;
        }

        try
        {
            return executionGuard();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAcquireExecutionLease(
        Func<bool>? executionGuard,
        Func<IDisposable?>? executionLease,
        out IDisposable? lease)
    {
        lease = null;
        if (!CanExecute(executionGuard))
        {
            return false;
        }

        if (executionLease is null)
        {
            return true;
        }

        try
        {
            lease = executionLease();
            return lease is not null;
        }
        catch
        {
            return false;
        }
    }

    private static OsInteractionResult ExecutionGuardFailed(
        TextSurfaceDescriptor submitSurface,
        SanitizationResult result,
        ConfirmationUiModel? model,
        bool applied,
        IReadOnlyDictionary<string, string> diagnostics)
    {
        return Finish(
            OsInteractionStatusIds.FailedClosed,
            submitSurface,
            result,
            model,
            applied,
            false,
            Merge(diagnostics, ("trace_status", "resident_operation_unavailable")));
    }

    private RediscoveredSurface RediscoverSameSurface(TextSurfaceDescriptor expectedSurface)
    {
        var rediscovery = _surfaceDiscovery.DiscoverActiveSurface();
        if (!rediscovery.Succeeded || rediscovery.Surface is null || !rediscovery.Surface.Supported)
        {
            var status = rediscovery.Status == OsInteractionStatusIds.StaleComposer
                ? OsInteractionStatusIds.StaleComposer
                : OsInteractionStatusIds.FocusLost;
            return new RediscoveredSurface(
                status,
                rediscovery.Surface,
                Merge(rediscovery.Diagnostics, ("rediscovery_status", rediscovery.Status)));
        }

        if (!IsSameSurface(expectedSurface, rediscovery.Surface))
        {
            return new RediscoveredSurface(
                OsInteractionStatusIds.StaleComposer,
                rediscovery.Surface,
                Merge(rediscovery.Diagnostics, ("rediscovery_status", rediscovery.Status)));
        }

        return new RediscoveredSurface(null, rediscovery.Surface, rediscovery.Diagnostics);
    }

    private static SanitizeRequest CreateRequest(string text, TextSurfaceDescriptor surface)
    {
        return new SanitizeRequest(
            ContentParts: new[]
            {
                new ContentPart("prompt", ContentSources.PromptText, text, new Dictionary<string, string>())
            },
            Context: new SanitizationContext(
                Application: surface.ProfileId,
                WorkspacePath: null,
                ProjectId: null,
                SessionId: null,
                PolicyProfile: "default"),
            Options: new SanitizationOptions(false, false, "os-adapter"));
    }

    private static OsInteractionResult Finish(
        string status,
        TextSurfaceDescriptor? surface,
        SanitizationResult? result,
        ConfirmationUiModel? confirmationModel,
        bool applied,
        bool submitted,
        IReadOnlyDictionary<string, string> diagnostics)
    {
        return new OsInteractionResult(status, surface, result, confirmationModel, applied, submitted, diagnostics);
    }

    private static IReadOnlyDictionary<string, string> Merge(
        params object[] items)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            switch (item)
            {
                case IReadOnlyDictionary<string, string> dictionary:
                    foreach (var entry in dictionary)
                    {
                        merged[entry.Key] = entry.Value;
                    }

                    break;
                case ValueTuple<string, string> pair:
                    merged[pair.Item1] = pair.Item2;
                    break;
            }
        }

        return merged;
    }

    private static string FormatDecision(SanitizeDecision decision)
    {
        return decision switch
        {
            SanitizeDecision.Allow => "allow",
            SanitizeDecision.Confirm => "confirm",
            SanitizeDecision.Block => "block",
            _ => decision.ToString()
        };
    }

    private static bool IsSameSurface(TextSurfaceDescriptor expected, TextSurfaceDescriptor actual)
    {
        return string.Equals(expected.SurfaceId, actual.SurfaceId, StringComparison.Ordinal)
            && string.Equals(expected.ProfileId, actual.ProfileId, StringComparison.Ordinal);
    }

    private sealed record RediscoveredSurface(
        string? Status,
        TextSurfaceDescriptor? Surface,
        IReadOnlyDictionary<string, string> Diagnostics);
}
