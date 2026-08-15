using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

internal interface IProtectedSendPipelineHost
{
    ProtectionSnapshot? PublishProtectedSendAttempt(
        ProtectionSnapshot snapshot,
        ResidentProtectedSendOperation operation,
        string status,
        string action);

    ProtectionSnapshot? PublishProtectedSendTrace(
        ProtectionSnapshot snapshot,
        ResidentProtectedSendOperation operation,
        string stage,
        string resultCode,
        string? attemptStatus = null,
        string? attemptAction = null);

    ProtectionSnapshot? PublishTraceUnavailable(ProtectionSnapshot snapshot, string? profileId);

    void ObserveProtectedSendStage(string stage);

    bool CanContinueProtectedSendOperation(ResidentProtectedSendOperation operation);

    IDisposable? AcquireProtectedSendSideEffect(ResidentProtectedSendOperation operation);

    OsInteractionResult RunNativeSubmitFlow(
        NativeSubmitRuntime runtime,
        NativeSubmitTargetIdentity? target,
        Func<string, string, bool> traceStage,
        Func<bool> executionGuard,
        Func<IDisposable?> executionLease);
}

/// <summary>
/// Owns protected Send stage ordering and terminal trace publication.
/// Windows hook, UIA and persistence remain behind the host adapter.
/// </summary>
internal sealed class ProtectedSendPipeline
{
    private readonly IProtectedSendPipelineHost _host;

    internal ProtectedSendPipeline(IProtectedSendPipelineHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    internal NativeSubmitInterceptionResult Execute(
        ProtectionSnapshot eventSnapshot,
        NativeSubmitRuntime runtime,
        NativeSubmitInterceptionResult classification,
        ResidentProtectedSendOperation operation)
    {
        ArgumentNullException.ThrowIfNull(eventSnapshot);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(operation);

        var snapshot = _host.PublishProtectedSendTrace(
            eventSnapshot,
            operation,
            "send_detected",
            "checking_prompt",
            "detected",
            "checking_prompt");
        if (snapshot is null)
        {
            _host.PublishTraceUnavailable(eventSnapshot, runtime.Profile.ProfileId);
            return FailedClosedNativeSubmitResult();
        }

        snapshot = _host.PublishProtectedSendAttempt(
            snapshot,
            operation,
            "checking",
            "checking_prompt");
        if (snapshot is null)
        {
            _host.PublishTraceUnavailable(eventSnapshot, runtime.Profile.ProfileId);
            return FailedClosedNativeSubmitResult();
        }

        _host.ObserveProtectedSendStage("detection");
        _host.ObserveProtectedSendStage("checking");

        snapshot = _host.PublishProtectedSendTrace(
            snapshot,
            operation,
            "target_matched",
            "target_verified");
        if (snapshot is null)
        {
            _host.PublishTraceUnavailable(eventSnapshot, runtime.Profile.ProfileId);
            return FailedClosedNativeSubmitResult();
        }

        bool TraceStage(string stage, string resultCode)
        {
            if (stage == "send_injected"
                && operation.Trace.Count > 0
                && operation.Trace[^1].Stage == "sanitized"
                && !TraceCanonicalStage("overlay_decision", OsInteractionStatusIds.DryRunAllow))
            {
                return false;
            }

            var canonicalStage = stage switch
            {
                "overlay_created" => "overlay_decision",
                "send_injected" => "replayed",
                _ => stage
            };
            return TraceCanonicalStage(canonicalStage, resultCode);
        }

        bool TraceCanonicalStage(string stage, string resultCode)
        {
            _host.ObserveProtectedSendStage(stage switch
            {
                "overlay_decision" or "overlay_created" or "overlay_foreground_confirmed" or "approved" or "cancelled" => "overlay",
                "text_written" => "write",
                "replayed" or "send_injected" => "replay",
                _ => stage
            });
            var tracedSnapshot = _host.PublishProtectedSendTrace(snapshot, operation, stage, resultCode);
            if (tracedSnapshot is null)
            {
                return false;
            }

            snapshot = tracedSnapshot;
            return true;
        }

        var result = ProtectedSendExecution.ExecuteGuarded(
            classification,
            () => _host.RunNativeSubmitFlow(
                runtime,
                operation.Target,
                TraceStage,
                () => _host.CanContinueProtectedSendOperation(operation),
                () => _host.AcquireProtectedSendSideEffect(operation)));

        var disposition = AttemptDisposition(result.Status, result.Submitted);
        var terminalTrace = result.Submitted
            ? _host.PublishProtectedSendTrace(
                snapshot,
                operation,
                "sent_safely",
                result.Status,
                disposition.Status,
                disposition.Action)
            : _host.PublishProtectedSendTrace(
                snapshot,
                operation,
                "terminal_blocked",
                result.Status,
                disposition.Status,
                disposition.Action);
        if (terminalTrace is null)
        {
            _host.PublishTraceUnavailable(eventSnapshot, runtime.Profile.ProfileId);
            return FailedClosedNativeSubmitResult();
        }

        return result;
    }

    internal NativeSubmitInterceptionResult ExecuteTraceUnavailable(
        ProtectionSnapshot eventSnapshot,
        NativeSubmitRuntime runtime,
        NativeSubmitInterceptionResult classification,
        ResidentProtectedSendOperation operation)
    {
        ArgumentNullException.ThrowIfNull(eventSnapshot);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(operation);

        var snapshot = _host.PublishProtectedSendTrace(
            eventSnapshot,
            operation,
            "send_detected",
            "checking_prompt",
            "detected",
            "checking_prompt");
        if (snapshot is null)
        {
            _host.PublishTraceUnavailable(eventSnapshot, runtime.Profile.ProfileId);
            return FailedClosedNativeSubmitResult();
        }

        if (_host.PublishProtectedSendTrace(
                snapshot,
                operation,
                "terminal_blocked",
                OsInteractionStatusIds.TraceUnavailable,
                "trace_unavailable",
                "retry_protection") is null)
        {
            _host.PublishTraceUnavailable(eventSnapshot, runtime.Profile.ProfileId);
            return FailedClosedNativeSubmitResult();
        }

        return classification;
    }

    internal static ProtectedSendAttemptDisposition AttemptDisposition(
        string status,
        bool submitted)
    {
        if (submitted && status == OsInteractionStatusIds.Submitted)
        {
            return new("sent_safely", "none");
        }

        return status switch
        {
            OsInteractionStatusIds.NativeSubmitInProgress => new("in_progress", "wait_for_current_send"),
            OsInteractionStatusIds.NativeSubmitSetupRequired => new("setup_required", "set_up_prompt_protection"),
            OsInteractionStatusIds.ProfilesUnavailable => new("settings_unavailable", "repair_profile_settings"),
            OsInteractionStatusIds.SurfaceUnverified or OsInteractionStatusIds.NotComposer
                or OsInteractionStatusIds.BindingUnknown or OsInteractionStatusIds.NotConfigured
                => new("binding_not_verified", "set_up_prompt_protection"),
            OsInteractionStatusIds.FocusLost or OsInteractionStatusIds.StaleComposer
                => new("composer_changed", "focus_and_send_again"),
            OsInteractionStatusIds.CaptureFailed => new("capture_failed", "retry_protection"),
            OsInteractionStatusIds.WriteFailed => new("write_failed", "retry_protection"),
            OsInteractionStatusIds.VerificationFailed => new("verification_failed", "retry_protection"),
            OsInteractionStatusIds.SubmitFailed => new("submit_failed", "retry_protection"),
            OsInteractionStatusIds.Canceled => new("canceled", "edit_or_send_again"),
            OsInteractionStatusIds.ReplayIndeterminate => new("replay_indeterminate", "retry_protection"),
            OsInteractionStatusIds.TraceUnavailable => new("trace_unavailable", "retry_protection"),
            OsInteractionStatusIds.EnterpriseBlocked => new("policy_blocked", "contact_administrator"),
            OsInteractionStatusIds.Blocked => new("content_blocked", "edit_prompt_and_send_again"),
            LocalProtectionRecovery.RecoveryRequiredCode or LocalProtectionRecovery.RuntimeDegradedCode
                => new("local_protection_unavailable", "repair_local_protection"),
            _ => new("protection_unavailable", "retry_protection")
        };
    }

    private static NativeSubmitInterceptionResult FailedClosedNativeSubmitResult()
    {
        return new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.FailedClosed,
            SuppressOriginalInput: true,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>
            {
                ["trace_status"] = "trace_unavailable"
            });
    }
}

internal readonly record struct ProtectedSendAttemptDisposition(
    string Status,
    string Action);

internal static class ProtectedSendExecution
{
    internal static NativeSubmitInterceptionResult ExecuteGuarded(
        NativeSubmitInterceptionResult classification,
        Func<OsInteractionResult> submitFlow)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(submitFlow);
        if (classification.Status != OsInteractionStatusIds.NativeSubmitGuarded
            || !classification.SuppressOriginalInput)
        {
            return classification;
        }

        OsInteractionResult flowResult;
        try
        {
            flowResult = submitFlow();
        }
        catch (Exception ex)
        {
            var diagnostics = new Dictionary<string, string>(classification.Diagnostics, StringComparer.Ordinal)
            {
                ["flow_exception"] = "true",
                ["exception_type"] = ex.GetType().FullName ?? ex.GetType().Name,
                ["exception_status"] = "native_submit_flow_failure"
            };
            return new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.FailedClosed,
                SuppressOriginalInput: true,
                Applied: false,
                Submitted: false,
                Diagnostics: diagnostics);
        }

        var completedDiagnostics = new Dictionary<string, string>(classification.Diagnostics, StringComparer.Ordinal);
        foreach (var item in flowResult.Diagnostics)
        {
            completedDiagnostics[item.Key] = item.Value;
        }

        completedDiagnostics["flow_status"] = flowResult.Status;
        return new NativeSubmitInterceptionResult(
            flowResult.Status,
            SuppressOriginalInput: true,
            Applied: flowResult.Applied,
            Submitted: flowResult.Submitted,
            Diagnostics: completedDiagnostics);
    }
}
