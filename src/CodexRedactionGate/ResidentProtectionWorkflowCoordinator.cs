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
    private readonly IResidentProtectionWorkflowRuntime _runtime;
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
        IResidentProtectionWorkflowRuntime runtime,
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

    public void RefreshOperationalState() => _runtime.RefreshOperationalActionState();

    public void CancelCurrentOperation() => _runtime.CancelOperationalAction();

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

        var started = _runtime.StartOperationalAction("local_readiness", "starting", false, "wait_for_result");
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
                _runtime.CompleteOperationalAction("local_readiness_check_failed", "retry_local_readiness", started.AttemptId);
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

        _runtime.PublishPromptProtectionRetryStarted();
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
                try
                {
                    activated = runtimeSet is not null && _runtime.Reload(runtimeSet);
                }
                catch (Exception exception)
                {
                    StopUnactivatedRuntime(runtimeSet);
                    _captureFailure(exception, "tray_prompt_protection_retry", "runtime_activate_failed");
                }

                if (activated)
                {
                    _runtime.PublishPromptProtectionRetrySucceeded();
                }
                else
                {
                    _runtime.PublishPromptProtectionRetryFailure();
                }

                Interlocked.Exchange(ref _workflowInProgress, 0);
            },
            exception =>
            {
                _captureFailure(exception, "tray_prompt_protection_retry", "worker_failed");
                _runtime.PublishPromptProtectionRetryFailure();
                Interlocked.Exchange(ref _workflowInProgress, 0);
            });
    }

    public void RepairLocalProtection()
    {
        if (Interlocked.Exchange(ref _workflowInProgress, 1) != 0)
        {
            return;
        }

        _runtime.PublishLocalProtectionStatus(LocalProtectionRecovery.ReloadingCode);
        try
        {
            CompleteRecovery(_localProtectionRecovery());
        }
        catch (Exception exception)
        {
            _captureFailure(exception, "local_protection_recovery", "worker_failed");
            _runtime.PublishLocalProtectionStatus(LocalProtectionRecovery.RecoveryRequiredCode);
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
            : _runtime.StartOperationalAction("first_run_setup", "starting", false, "focus_message_composer").AttemptId;
        if (attemptId <= 0 || !_runtime.PublishOperationalActionStage(
                "awaiting_user_focus", true, "focus_message_composer", attemptId))
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
                _runtime.CompleteOperationalAction("setup_failed", "retry_setup", attemptId);
                if (resetScheduled)
                {
                    Interlocked.Exchange(ref _setupScheduled, 0);
                }

                Interlocked.Exchange(ref _workflowInProgress, 0);
            });
    }

    private void CompleteSetup(FirstRunSetupResult? result, long operationalAttemptId)
    {
        if (!_runtime.IsCurrentOperationalActionAttempt(operationalAttemptId))
        {
            return;
        }

        var success = false;
        var activationFailed = false;
        var setupAttemptId = SetupAttemptId(result);
        if (result?.PendingProfiles is not null
            && (setupAttemptId <= 0 || !_runtime.IsCurrentSetupVerificationAttempt(setupAttemptId)))
        {
            // A partial or stale verification result cannot name the resident
            // attempt it belongs to. Ignore it without activating a candidate,
            // touching persisted profiles, or showing a UI prompt.
            return;
        }

        if (result?.Succeeded == true && !result.State.Required)
        {
            if ((result.PendingProfiles is null || setupAttemptId > 0)
                && (setupAttemptId <= 0 || _runtime.IsCurrentSetupVerificationAttempt(setupAttemptId)))
            {
                _runtime.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                    "activating_protection", "wait_for_verification", ProfileId(result),
                    _runtime.State.ProtectedSendBinding, setupAttemptId));
                NativeSubmitRuntimeSet? candidate = null;
                try
                {
                    if (!ActivatePendingTarget(result))
                    {
                        throw new InvalidOperationException("active_target_activation_failed");
                    }

                    candidate = result.PendingProfiles is { } profiles
                        ? _candidateRuntimeFactory(profiles)
                        : _retryRuntimeFactory();
                    var candidateActivated = candidate is not null && _runtime.Reload(candidate);
                    if (candidateActivated && CommitProfiles(result))
                    {
                        _runtime.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                            "protected", "none", ProfileId(result),
                            _runtime.State.ProtectedSendBinding, setupAttemptId));
                        success = true;
                    }
                    else
                    {
                        activationFailed = true;
                        if (!candidateActivated)
                        {
                            StopUnactivatedRuntime(candidate);
                        }

                        RollbackProfiles(result);
                        ReloadPreviousRuntimeAfterSetupRollback();
                    }
                }
                catch (Exception exception)
                {
                    activationFailed = true;
                    _captureFailure(exception, "first_run_setup", "runtime_reload_failed");
                    StopUnactivatedRuntime(candidate);
                    RollbackProfiles(result);
                    ReloadPreviousRuntimeAfterSetupRollback();
                }
            }
        }

        if (!success)
        {
            var status = result?.Code == "setup_cancelled"
                ? "setup_cancelled"
                : activationFailed ? "activation_failed" : "verification_failed";
            _runtime.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                status, "retry_setup", AttemptId: SetupAttemptId(result)));
            PublishNotice(
                result?.Code == "setup_cancelled"
                    ? "Prompt setup was cancelled. Protected Send remains blocked."
                    : "Prompt setup could not activate protected Send. Protected Send remains blocked until verification succeeds.",
                result?.Code != "setup_cancelled");
        }

        _runtime.CompleteOperationalAction(success ? "succeeded" : result?.Code == "setup_cancelled" ? "cancelled" : "setup_failed",
            success ? "none" : "retry_setup", operationalAttemptId);
        SetupCompleted?.Invoke(result);
    }

    private void CompleteLocalReadiness(LocalReadinessResult result, long attemptId)
    {
        if (!_runtime.CompleteOperationalAction(
                result.Succeeded ? "succeeded" : result.Code,
                result.Succeeded ? "none" : "retry_local_readiness",
                attemptId))
        {
            return;
        }

        if (result.Succeeded)
        {
            _runtime.TryRecordResidentReadinessProof(attemptId);
        }

        _runtime.RefreshOperationalActionState();
    }

    private bool CommitProfiles(FirstRunSetupResult result)
    {
        try
        {
            if (result.PendingProfiles is not null
                && !_runtime.IsCurrentSetupVerificationAttempt(SetupAttemptId(result)))
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

    private void RollbackProfiles(FirstRunSetupResult result)
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

        _runtime.PublishPromptProtectionRetryFailure();
    }

    private void ReloadPreviousRuntimeAfterSetupRollback()
    {
        try
        {
            var rollback = _retryRuntimeFactory();
            if (rollback is null || !_runtime.Reload(rollback))
            {
                StopUnactivatedRuntime(rollback);
                _runtime.PublishPromptProtectionRetryFailure();
            }
        }
        catch (Exception exception)
        {
            _captureFailure(exception, "first_run_setup", "runtime_rollback_create_failed");
            _runtime.PublishPromptProtectionRetryFailure();
        }
    }

    private void CompleteRecovery(LocalProtectionRecoveryResult result)
    {
        try
        {
            if (!result.Succeeded)
            {
                _runtime.PublishLocalProtectionStatus(LocalProtectionRecovery.RecoveryRequiredCode);
                Notice?.Invoke("Local protection repair could not be completed. Protected Send remains blocked.", true);
                return;
            }

            var runtime = _recoveredRuntimeFactory();
            if (runtime.NativeSubmitRuntimeSet is null
                || !_runtime.Reload(runtime)
                || (!_runtime.State.Enabled && !_runtime.Start())
                || !_runtime.TryPublishLocalProtectionReady())
            {
                _runtime.PublishLocalProtectionStatus(LocalProtectionRecovery.RuntimeDegradedCode);
                Notice?.Invoke("Local protection was repaired, but protected Send could not be reactivated. It remains blocked.", true);
                return;
            }

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
            _runtime.PublishLocalProtectionStatus(LocalProtectionRecovery.RuntimeDegradedCode);
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
