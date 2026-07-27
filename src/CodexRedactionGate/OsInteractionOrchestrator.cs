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

    public OsInteractionResult RunOnce(OsInteractionRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            return RunOnceInternal(options);
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

    private OsInteractionResult RunOnceInternal(OsInteractionRunOptions options)
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

        ConfirmationUiModel? model = null;
        var outgoingText = result.SanitizedText;

        if (result.Decision == SanitizeDecision.Confirm)
        {
            model = ConfirmationUiShell.CreateModel(result);

            if (options.DryRun)
            {
                return Finish(OsInteractionStatusIds.DryRunConfirm, surface, result, model, false, false, diagnostics);
            }

            var decision = _confirmationOverlay.RequestConfirmation(model);
            if (!decision.Approved || decision.Payload is null)
            {
                return Finish(OsInteractionStatusIds.Canceled, surface, result, model, false, false, diagnostics);
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

            var preSubmit = RediscoverSameSurface(surface);
            if (preSubmit.Status is not null)
            {
                return Finish(preSubmit.Status, preSubmit.Surface, result, model, true, false, Merge(
                    unchangedDiagnostics,
                    preSubmit.Diagnostics,
                    ("pre_submit_status", preSubmit.Status)));
            }

            var submitSurface = preSubmit.Surface ?? surface;
            var submitWithoutWrite = _submitAction.Submit(submitSurface);
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
        var replace = _writer.ReplaceText(writeSurface, outgoingText);
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

        if (!options.SubmitAfterApply)
        {
            return Finish(OsInteractionStatusIds.Applied, verificationSurface, result, model, true, false, Merge(
                diagnostics,
                replace.Diagnostics,
                verificationCapture.Diagnostics,
                ("write_status", replace.Status),
                ("verification_status", verificationCapture.Status)));
        }

        var submit = _submitAction.Submit(verificationSurface);
        return submit.Succeeded
            ? Finish(OsInteractionStatusIds.Submitted, verificationSurface, result, model, true, true, Merge(
                diagnostics,
                replace.Diagnostics,
                submit.Diagnostics,
                ("write_status", replace.Status),
                ("submit_status", submit.Status)))
            : Finish(OsInteractionStatusIds.SubmitFailed, verificationSurface, result, model, true, false, Merge(
                diagnostics,
                replace.Diagnostics,
                submit.Diagnostics,
                ("write_status", replace.Status),
                ("submit_status", submit.Status)));
    }

    private RediscoveredSurface RediscoverSameSurface(TextSurfaceDescriptor expectedSurface)
    {
        var rediscovery = _surfaceDiscovery.DiscoverActiveSurface();
        if (!rediscovery.Succeeded || rediscovery.Surface is null || !rediscovery.Surface.Supported)
        {
            return new RediscoveredSurface(
                OsInteractionStatusIds.FocusLost,
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
