using System;
using System.Collections.Generic;
using System.Threading;

namespace CodexRedactionGate;

/// <summary>
/// Owns resident setup, retry, and local-recovery operation ordering. The tray
/// supplies UI dispatch and renders the coordinator's published state; it does
/// not decide whether protection became active.
/// </summary>
internal sealed class ResidentProtectionWorkflowCoordinator
{
    private readonly IResidentProtectionWorkflowPort _runtime;
    private readonly DefaultStorageLayout _layout;
    private readonly Func<IFirstRunSetupController> _setupControllerFactory;
    private readonly Func<IReadOnlyList<SubmitBindingProfile>, NativeSubmitRuntimeSet?> _candidateRuntimeFactory;
    private readonly Func<NativeSubmitRuntimeSet?> _retryRuntimeFactory;
    private readonly Func<ResidentProtectionRuntime> _recoveredRuntimeFactory;
    private readonly Func<LocalProtectionRecoveryResult> _localProtectionRecovery;
    private readonly Action<Action> _backgroundQueue;
    private readonly Action<Action> _uiDispatcher;
    private readonly Action<Exception, string, string> _captureFailure;
    private int _setupScheduled;
    private int _workflowInProgress;

    public ResidentProtectionWorkflowCoordinator(
        IResidentProtectionWorkflowPort runtime,
        DefaultStorageLayout layout,
        Func<IFirstRunSetupController> setupControllerFactory,
        Func<IReadOnlyList<SubmitBindingProfile>, NativeSubmitRuntimeSet?> candidateRuntimeFactory,
        Func<NativeSubmitRuntimeSet?> retryRuntimeFactory,
        Func<ResidentProtectionRuntime> recoveredRuntimeFactory,
        Func<LocalProtectionRecoveryResult> localProtectionRecovery,
        Action<Action> backgroundQueue,
        Action<Action> uiDispatcher,
        Action<Exception, string, string> captureFailure)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _setupControllerFactory = setupControllerFactory ?? throw new ArgumentNullException(nameof(setupControllerFactory));
        _candidateRuntimeFactory = candidateRuntimeFactory ?? throw new ArgumentNullException(nameof(candidateRuntimeFactory));
        _retryRuntimeFactory = retryRuntimeFactory ?? throw new ArgumentNullException(nameof(retryRuntimeFactory));
        _recoveredRuntimeFactory = recoveredRuntimeFactory ?? throw new ArgumentNullException(nameof(recoveredRuntimeFactory));
        _localProtectionRecovery = localProtectionRecovery ?? throw new ArgumentNullException(nameof(localProtectionRecovery));
        _backgroundQueue = backgroundQueue ?? throw new ArgumentNullException(nameof(backgroundQueue));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _captureFailure = captureFailure ?? throw new ArgumentNullException(nameof(captureFailure));
    }

    public event Action<FirstRunSetupResult?>? SetupCompleted;

    public event Action<string, bool>? Notice;

    public bool StartResident()
    {
        _runtime.EnableResidentReadinessAdmission();
        return _runtime.Start();
    }

    public void RefreshOperationalState() => _runtime.RefreshOperationalState();

    public void CancelCurrentOperation()
    {
        var attemptId = _runtime.OperationalAction.AttemptId;
        _runtime.Publish(ResidentWorkflowPublication.Cancelled(), attemptId);
    }

    public void RetryCurrentOperation()
    {
        if (string.Equals(_runtime.OperationalAction.ActionKind, "local_readiness", StringComparison.Ordinal))
        {
            StartLocalReadiness();
            return;
        }

        StartFocusedSetup();
    }

    public void StartLocalReadiness()
    {
        if (string.Equals(_runtime.State.LocalReadinessStatus, "passed", StringComparison.Ordinal))
        {
            return;
        }

        var started = _runtime.StartAction(new ResidentWorkflowActionRequest(
            "local_readiness", "starting", false, "wait_for_result"));
        if (!started.Started)
        {
            return;
        }

        Queue(
            () => LocalReadinessWorkflow.Run(_layout),
            result => CompleteLocalReadiness(result, started.AttemptId),
            exception =>
            {
                _captureFailure(exception, "local_readiness", "check_failed");
                _runtime.Publish(
                    ResidentWorkflowPublication.Completed("local_readiness_check_failed", "retry_local_readiness"),
                    started.AttemptId);
            });
    }

    public void StartInitialSetup()
    {
        if (Interlocked.Exchange(ref _setupScheduled, 1) != 0)
        {
            return;
        }

        StartSetup(
            () => FirstRunSetupBackgroundRunner.Run(
                _layout,
                _setupControllerFactory,
                exception => _captureFailure(exception, "first_run_setup", "setup_failed")),
            resetScheduled: true);
    }

    public void StartFocusedSetup()
    {
        StartSetup(
            () =>
            {
                try
                {
                    var setup = _setupControllerFactory();
                    return setup is IFocusedProfileSetupController focused
                        ? focused.ConfigureFocusedProfile(_layout)
                        : setup.EnsureSetup(_layout);
                }
                catch (Exception exception)
                {
                    _captureFailure(exception, "tray_profile_verification", "verification_failed");
                    return new FirstRunSetupResult(
                        false,
                        "setup_failed",
                        new FirstRunSetupState(true, new[] { "focused_supported_app" }, "failed", false, false),
                        new Dictionary<string, string>());
                }
            },
            resetScheduled: false);
    }

    public void RetryPromptProtection()
    {
        if (Interlocked.Exchange(ref _workflowInProgress, 1) != 0)
        {
            return;
        }

        var started = _runtime.StartAction(new ResidentWorkflowActionRequest(
            "prompt_protection_retry", "starting", false, "wait_for_result"));
        if (!started.Started
            || !_runtime.Publish(
                ResidentWorkflowPublication.RetryStarted(started.AttemptId),
                started.AttemptId))
        {
            Interlocked.Exchange(ref _workflowInProgress, 0);
            return;
        }

        Queue(
            () =>
            {
                try
                {
                    return _retryRuntimeFactory();
                }
                catch (Exception exception)
                {
                    _captureFailure(exception, "tray_prompt_protection_retry", "runtime_create_failed");
                    return null;
                }
            },
            runtimeSet =>
            {
                var activated = false;
                using var attemptLease = _runtime.TryAcquireAttempt(
                    ResidentWorkflowAttempt.Operational(started.AttemptId));
                if (attemptLease is null)
                {
                    StopUnactivatedRuntime(runtimeSet);
                    Interlocked.Exchange(ref _workflowInProgress, 0);
                    return;
                }

                try
                {
                    activated = runtimeSet is not null
                        && _runtime.Reload(runtimeSet, started.AttemptId);
                }
                catch (Exception exception)
                {
                    StopUnactivatedRuntime(runtimeSet);
                    _captureFailure(exception, "tray_prompt_protection_retry", "runtime_activate_failed");
                }

                if (activated)
                {
                    _runtime.Publish(ResidentWorkflowPublication.RetrySucceeded(started.AttemptId), started.AttemptId);
                    _runtime.Publish(
                        ResidentWorkflowPublication.Completed("succeeded", "none"),
                        started.AttemptId);
                }
                else
                {
                    _runtime.Publish(ResidentWorkflowPublication.RetryFailed(started.AttemptId), started.AttemptId);
                    _runtime.Publish(
                        ResidentWorkflowPublication.Completed("failed", "retry_protection"),
                        started.AttemptId);
                }

                Interlocked.Exchange(ref _workflowInProgress, 0);
            },
            exception =>
            {
                _captureFailure(exception, "tray_prompt_protection_retry", "worker_failed");
                _runtime.Publish(ResidentWorkflowPublication.RetryFailed(started.AttemptId), started.AttemptId);
                _runtime.Publish(
                    ResidentWorkflowPublication.Completed("failed", "retry_protection"),
                    started.AttemptId);
                Interlocked.Exchange(ref _workflowInProgress, 0);
            });
    }

    public void RepairLocalProtection()
    {
        if (Interlocked.Exchange(ref _workflowInProgress, 1) != 0)
        {
            return;
        }

        var started = _runtime.StartAction(new ResidentWorkflowActionRequest(
            "local_protection_recovery", "starting", false, "wait_for_result"));
        if (!started.Started
            || !_runtime.Publish(
                ResidentWorkflowPublication.LocalProtection(
                    LocalProtectionRecovery.ReloadingCode,
                    started.AttemptId),
                started.AttemptId))
        {
            Interlocked.Exchange(ref _workflowInProgress, 0);
            return;
        }

        try
        {
            CompleteRecovery(_localProtectionRecovery(), started.AttemptId);
        }
        catch (Exception exception)
        {
            _captureFailure(exception, "local_protection_recovery", "worker_failed");
            _runtime.Publish(
                ResidentWorkflowPublication.LocalProtection(
                    LocalProtectionRecovery.RecoveryRequiredCode,
                    started.AttemptId),
                started.AttemptId);
            _runtime.Publish(
                ResidentWorkflowPublication.Completed("failed", "repair_local_protection"),
                started.AttemptId);
            Notice?.Invoke("Local protection repair could not be completed. Protected Send remains blocked.", true);
            Interlocked.Exchange(ref _workflowInProgress, 0);
        }
    }

    private void StartSetup(Func<FirstRunSetupResult?> worker, bool resetScheduled)
    {
        if (Interlocked.Exchange(ref _workflowInProgress, 1) != 0)
        {
            return;
        }

        var action = _runtime.OperationalAction;
        var attemptId = action.Status == "running"
            ? action.AttemptId
            : _runtime.StartAction(new ResidentWorkflowActionRequest(
                "first_run_setup", "starting", false, "focus_message_composer")).AttemptId;
        if (attemptId <= 0 || !_runtime.Publish(
                ResidentWorkflowPublication.ForStage("awaiting_user_focus", true, "focus_message_composer"),
                attemptId))
        {
            if (resetScheduled)
            {
                Interlocked.Exchange(ref _setupScheduled, 0);
            }

            Interlocked.Exchange(ref _workflowInProgress, 0);

            return;
        }

        Queue(
            worker,
            result =>
            {
                CompleteSetup(result, attemptId);
                if (resetScheduled)
                {
                    Interlocked.Exchange(ref _setupScheduled, 0);
                }

                Interlocked.Exchange(ref _workflowInProgress, 0);
            },
            exception =>
            {
                _captureFailure(exception, "first_run_setup", "worker_failed");
                _runtime.Publish(
                    ResidentWorkflowPublication.Completed("setup_failed", "retry_setup"),
                    attemptId);
                if (resetScheduled)
                {
                    Interlocked.Exchange(ref _setupScheduled, 0);
                }

                Interlocked.Exchange(ref _workflowInProgress, 0);
            });
    }

    private void CompleteSetup(FirstRunSetupResult? result, long operationalAttemptId)
    {
        using var operationalLease = _runtime.TryAcquireAttempt(
            ResidentWorkflowAttempt.Operational(operationalAttemptId));
        if (operationalLease is null)
        {
            return;
        }

        var success = false;
        var activationFailed = false;
        string? noticeMessage = null;
        var noticeIsError = false;
        var setupAttemptId = SetupAttemptId(result);
        if (result?.PendingProfiles is not null
            && (setupAttemptId <= 0 || !_runtime.IsCurrent(ResidentWorkflowAttempt.SetupVerification(setupAttemptId))))
        {
            // A partial or stale verification result cannot name the resident
            // attempt it belongs to. Ignore it without activating a candidate,
            // touching persisted profiles, or showing a UI prompt.
            return;
        }

        if (result?.Succeeded == true && !result.State.Required)
        {
            if ((result.PendingProfiles is null || setupAttemptId > 0)
                && (setupAttemptId <= 0 || _runtime.IsCurrent(ResidentWorkflowAttempt.SetupVerification(setupAttemptId))))
            {
                _runtime.Publish(ResidentWorkflowPublication.Setup(new PromptProtectionSetupProgress(
                    "activating_protection", "wait_for_verification", ProfileId(result),
                    _runtime.State.ProtectedSendBinding, setupAttemptId)));
                NativeSubmitRuntimeSet? candidate = null;
                var candidateActivated = false;
                try
                {
                    if (!ActivatePendingTarget(result))
                    {
                        throw new InvalidOperationException("active_target_activation_failed");
                    }

                    candidate = result.PendingProfiles is { } profiles
                        ? _candidateRuntimeFactory(profiles)
                        : _retryRuntimeFactory();
                    candidateActivated = candidate is not null
                        && _runtime.Reload(candidate, operationalAttemptId);
                    if (!candidateActivated || !CommitProfiles(result))
                    {
                        activationFailed = true;
                        if (!candidateActivated)
                        {
                            StopUnactivatedRuntime(candidate);
                        }

                        candidateActivated = false;
                        RollbackProfiles(result, operationalAttemptId);
                        ReloadPreviousRuntimeAfterSetupRollback(operationalAttemptId);
                    }
                }
                catch (Exception exception)
                {
                    activationFailed = true;
                    _captureFailure(exception, "first_run_setup", "runtime_reload_failed");
                    StopUnactivatedRuntime(candidate);
                    RollbackProfiles(result, operationalAttemptId);
                    ReloadPreviousRuntimeAfterSetupRollback(operationalAttemptId);
                }

                if (candidateActivated)
                {
                    _runtime.Publish(ResidentWorkflowPublication.Setup(new PromptProtectionSetupProgress(
                        "protected", "none", ProfileId(result),
                        _runtime.State.ProtectedSendBinding, setupAttemptId)));
                    success = true;
                    _runtime.Publish(
                        ResidentWorkflowPublication.Completed("succeeded", "none"),
                        operationalAttemptId);
                }
            }
        }

        if (!success)
        {
            var status = result?.Code == "setup_cancelled"
                ? "setup_cancelled"
                : activationFailed ? "activation_failed" : "verification_failed";
            _runtime.Publish(ResidentWorkflowPublication.Setup(new PromptProtectionSetupProgress(
                status, "retry_setup", AttemptId: SetupAttemptId(result))));
            noticeMessage = result?.Code == "setup_cancelled"
                ? "Prompt setup was cancelled. Protected Send remains blocked."
                : "Prompt setup could not activate protected Send. Protected Send remains blocked until verification succeeds.";
            noticeIsError = result?.Code != "setup_cancelled";
        }

        if (!success)
        {
            _runtime.Publish(
                result?.Code == "setup_cancelled"
                    ? ResidentWorkflowPublication.Cancelled()
                    : ResidentWorkflowPublication.Completed("setup_failed", "retry_setup"),
                operationalAttemptId);
        }

        operationalLease.Dispose();
        if (noticeMessage is not null)
        {
            PublishNotice(noticeMessage, noticeIsError);
        }

        SetupCompleted?.Invoke(result);
    }

    private void CompleteLocalReadiness(LocalReadinessResult result, long attemptId)
    {
        if (!_runtime.Publish(
                ResidentWorkflowPublication.Completed(
                    result.Succeeded ? "succeeded" : result.Code,
                    result.Succeeded ? "none" : "retry_local_readiness"),
                attemptId))
        {
            return;
        }

        if (result.Succeeded)
        {
            _runtime.Publish(ResidentWorkflowPublication.ReadinessProof(), attemptId);
        }

        _runtime.RefreshOperationalState();
    }

    private bool CommitProfiles(FirstRunSetupResult result)
    {
        try
        {
            if (result.PendingProfiles is not null
                && !_runtime.IsCurrent(ResidentWorkflowAttempt.SetupVerification(SetupAttemptId(result))))
            {
                return false;
            }

            if (result.PendingProfiles is not null
                && !SubmitBindingProfileStore.Save(_layout, result.PendingProfiles).Succeeded)
            {
                throw new InvalidOperationException("profile_commit_failed");
            }

            // A setup controller may report an already-complete installation
            // without proposing a profile mutation. Runtime activation is still
            // required, but there is no profile/target record to commit.
            return result.PendingProfiles is null
                || ProfileId(result) is { } profileId
                && FirstRunSetupController.MarkSetupComplete(_layout, profileId);
        }
        catch (Exception exception)
        {
            _captureFailure(exception, "first_run_setup", "profile_commit_failed");
            return false;
        }
    }

    private void RollbackProfiles(FirstRunSetupResult result, long operationalAttemptId)
    {
        if (result.PreviousProfiles is { } profiles)
        {
            SubmitBindingProfileStore.Save(_layout, profiles);
        }

        if (result.PreviousActiveTargetProfileId is { } profileId)
        {
            ActivePromptProtectionTargetStore.Save(_layout, profileId);
        }
        else
        {
            ActivePromptProtectionTargetStore.Clear(_layout);
        }

        _runtime.Publish(
            ResidentWorkflowPublication.RetryFailed(operationalAttemptId),
            operationalAttemptId);
    }

    private void ReloadPreviousRuntimeAfterSetupRollback(long operationalAttemptId)
    {
        try
        {
            var rollback = _retryRuntimeFactory();
            if (rollback is null || !_runtime.Reload(rollback, operationalAttemptId))
            {
                StopUnactivatedRuntime(rollback);
                _runtime.Publish(
                    ResidentWorkflowPublication.RetryFailed(operationalAttemptId),
                    operationalAttemptId);
                _runtime.Publish(
                    ResidentWorkflowPublication.Completed("setup_failed", "retry_setup"),
                    operationalAttemptId);
                _runtime.Stop();
            }
        }
        catch (Exception exception)
        {
            _captureFailure(exception, "first_run_setup", "runtime_rollback_create_failed");
            _runtime.Publish(
                ResidentWorkflowPublication.RetryFailed(operationalAttemptId),
                operationalAttemptId);
            _runtime.Publish(
                ResidentWorkflowPublication.Completed("setup_failed", "retry_setup"),
                operationalAttemptId);
            _runtime.Stop();
        }
    }

    private void CompleteRecovery(LocalProtectionRecoveryResult result, long attemptId)
    {
        using var attemptLease = _runtime.TryAcquireAttempt(
            ResidentWorkflowAttempt.Operational(attemptId));
        if (attemptLease is null)
        {
            Interlocked.Exchange(ref _workflowInProgress, 0);
            return;
        }

        try
        {
            if (!result.Succeeded)
            {
                _runtime.Publish(
                    ResidentWorkflowPublication.LocalProtection(
                        LocalProtectionRecovery.RecoveryRequiredCode,
                        attemptId),
                    attemptId);
                _runtime.Publish(
                    ResidentWorkflowPublication.Completed("failed", "repair_local_protection"),
                    attemptId);
                Notice?.Invoke("Local protection repair could not be completed. Protected Send remains blocked.", true);
                return;
            }

            var runtime = _recoveredRuntimeFactory();
            if ((!_runtime.State.Enabled && !_runtime.Start())
                || runtime.NativeSubmitRuntimeSet is null
                || !_runtime.Reload(runtime, attemptId)
                || !_runtime.Publish(
                    ResidentWorkflowPublication.LocalReady(attemptId),
                    attemptId))
            {
                _runtime.Publish(
                    ResidentWorkflowPublication.LocalProtection(
                        LocalProtectionRecovery.RuntimeDegradedCode,
                        attemptId),
                    attemptId);
                _runtime.Publish(
                    ResidentWorkflowPublication.Completed("failed", "repair_local_protection"),
                    attemptId);
                Notice?.Invoke("Local protection was repaired, but protected Send could not be reactivated. It remains blocked.", true);
                return;
            }

            _runtime.Publish(
                ResidentWorkflowPublication.Completed("succeeded", "none"),
                attemptId);

            var active = _runtime.State.NativeSubmitEnabled && _runtime.State.ComposerProtected;
            Notice?.Invoke(
                active
                    ? "Local protection was repaired and protected Send is active again."
                    : "Local protection was repaired. Protected Send remains blocked until profile verification succeeds.",
                !active);
        }
        catch (Exception exception)
        {
            _captureFailure(exception, "local_protection_recovery", "runtime_reload_failed");
            _runtime.Publish(
                ResidentWorkflowPublication.LocalProtection(
                    LocalProtectionRecovery.RuntimeDegradedCode,
                    attemptId),
                attemptId);
            _runtime.Publish(
                ResidentWorkflowPublication.Completed("failed", "repair_local_protection"),
                attemptId);
            Notice?.Invoke("Local protection was repaired, but protected Send could not be reactivated. It remains blocked.", true);
        }
        finally
        {
            Interlocked.Exchange(ref _workflowInProgress, 0);
        }
    }

    private void Queue<T>(Func<T> work, Action<T> completed, Action<Exception> failed)
    {
        try
        {
            _backgroundQueue(() =>
            {
                T result;
                try
                {
                    result = work();
                }
                catch (Exception exception)
                {
                    Dispatch(() => failed(exception));
                    return;
                }

                Dispatch(() => completed(result));
            });
        }
        catch (Exception exception)
        {
            failed(exception);
        }
    }

    private void Dispatch(Action action)
    {
        try
        {
            _uiDispatcher(action);
        }
        catch (Exception exception)
        {
            _captureFailure(exception, "resident_workflow", "ui_dispatch_failed");
            // A closing or unavailable UI dispatcher must not leave a resident
            // operation indefinitely running. Complete the fail-closed state
            // transition without attempting to render UI from this callback.
            action();
        }
    }

    private void PublishNotice(string message, bool isFailure)
    {
        try
        {
            _uiDispatcher(() => Notice?.Invoke(message, isFailure));
        }
        catch (Exception exception)
        {
            _captureFailure(exception, "resident_workflow", "notice_dispatch_failed");
        }
    }

    private static void StopUnactivatedRuntime(NativeSubmitRuntimeSet? runtimeSet)
    {
        if (runtimeSet is null)
        {
            return;
        }

        runtimeSet.HookHost.Stop();
        runtimeSet.Dispose();
    }

    private static long SetupAttemptId(FirstRunSetupResult? result) =>
        result?.Diagnostics.TryGetValue("setup_attempt_id", out var text) == true
        && long.TryParse(text, out var value)
            ? value
            : 0;

    private static string? ProfileId(FirstRunSetupResult result) =>
        result.Diagnostics.TryGetValue("profile_id", out var profileId)
            ? profileId
            : result.PendingProfiles is { Count: 1 } ? result.PendingProfiles[0].ProfileId : null;

    private bool ActivatePendingTarget(FirstRunSetupResult result) =>
        result.PendingProfiles is null
        || ProfileId(result) is { } profileId
        && ActivePromptProtectionTargetStore.Save(_layout, profileId).Succeeded;
}
