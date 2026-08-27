using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexRedactionGate;
using NUnit.Framework;

public partial class SanitizerTests
{
    [Test]
    public void ResidentWorkflow_StartResidentAutomaticallyRunsReadinessAndSetup()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = CreateWorkflowProfile() with { ProfileId = "chatgpt-desktop" };
        Assert.That(SubmitBindingProfileStore.Save(layout, new[] { profile }).Succeeded, Is.True);
        Assert.That(ActivePromptProtectionTargetStore.Save(layout, profile.ProfileId).Succeeded, Is.True);

        var initialHook = new FakeNativeSubmitHookHost();
        var initialController = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)));
        var initialRuntime = NativeSubmitRuntime.CreateTest(
            initialHook,
            initialController,
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        var protection = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => CreateSubmittedResult(profile.ProfileId),
            initialHook,
            initialController,
            profile,
            storageLayout: layout,
            nativeSubmitRuntimes: new[] { initialRuntime });
        var runtime = new ResidentProtectionRuntimeFacade(protection);
        var order = new List<string>();
        var candidateHook = new FakeNativeSubmitHookHost();
        var setupResult = new FirstRunSetupResult(
            Succeeded: true,
            Code: "setup_complete",
            State: new FirstRunSetupState(false, Array.Empty<string>(), "complete", false, true),
            Diagnostics: new Dictionary<string, string>
            {
                ["profile_id"] = profile.ProfileId
            });
        var coordinator = new ResidentProtectionWorkflowCoordinator(
            runtime,
            layout,
            () => new WorkflowSetupController(_ =>
            {
                order.Add("setup");
                return setupResult;
            }),
            _ => null,
            () => CreateRuntimeSet(candidateHook, profile),
            () => throw new InvalidOperationException("Recovery should not run."),
            () => throw new InvalidOperationException("Recovery should not run."),
            action => action(),
            action => action(),
            (_, _, _) => { },
            localReadinessCheck: () =>
            {
                order.Add("readiness");
                return new LocalReadinessResult(true, "local_readiness_passed", Array.Empty<ReadinessItem>());
            });

        try
        {
            Assert.That(coordinator.StartResident(), Is.True);

            Assert.That(order, Is.EqualTo(new[] { "readiness", "setup" }));
            Assert.That(runtime.State.LocalReadinessStatus, Is.EqualTo("passed"));
            Assert.That(runtime.State.NativeSubmitEnabled, Is.True);
            Assert.That(runtime.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
            Assert.That(runtime.State.ReadinessStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
            Assert.That(runtime.State.ComposerProtected, Is.True);

            order.Clear();
            protection.Stop();

            Assert.That(coordinator.StartResident(), Is.True);
            Assert.That(order, Is.EqualTo(new[] { "readiness", "setup" }));
            Assert.That(runtime.State.NativeSubmitEnabled, Is.True);
            Assert.That(runtime.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
            Assert.That(runtime.State.ComposerProtected, Is.True);
        }
        finally
        {
            protection.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void ResidentWorkflow_StartResidentCanaryCopiesMarkerToClipboard()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop",
            "Enter",
            "Ctrl+Enter",
            ChatGptDiscoveryFixture.CreateVerified());
        Assert.That(SubmitBindingProfileStore.Save(layout, new[] { profile }).Succeeded, Is.True);
        Assert.That(ActivePromptProtectionTargetStore.Save(layout, profile.ProfileId).Succeeded, Is.True);

        var hook = new FakeNativeSubmitHookHost();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)));
        var protection = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => CreateSubmittedResult(profile.ProfileId),
            hook,
            controller,
            () => CreateSubmittedResult(profile.ProfileId),
            profile,
            storageLayout: layout,
            residentCanaryRunner: (_, _, _, _, _, _) => throw new InvalidOperationException("Canary should not run before Send."));
        var runtime = new ResidentProtectionRuntimeFacade(protection);
        var notices = new List<(string Message, bool IsFailure)>();
        string? copiedMarker = null;
        var coordinator = CreateWorkflowCoordinator(
            runtime,
            layout,
            setupResult: null,
            setupCandidate: _ => null,
            retryCandidate: () => null,
            recoveredRuntime: null,
            copyCanaryMarker: marker => copiedMarker = marker,
            notice: (message, isFailure) => notices.Add((message, isFailure)),
            captureFailure: null);

        try
        {
            Assert.That(protection.Start(), Is.True);

            coordinator.StartResidentCanary();

            Assert.That(copiedMarker, Does.StartWith("CS_CANARY_"));
            Assert.That(notices, Has.Count.EqualTo(1));
            Assert.That(notices[0].Message, Does.Contain("copied to the clipboard"));
            Assert.That(notices[0].Message, Does.Not.Contain(copiedMarker));
            Assert.That(notices[0].IsFailure, Is.False);
        }
        finally
        {
            protection.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void ResidentWorkflow_StartResidentCanaryShowsManualMarkerWhenClipboardFails()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop",
            "Enter",
            "Ctrl+Enter",
            ChatGptDiscoveryFixture.CreateVerified());
        Assert.That(SubmitBindingProfileStore.Save(layout, new[] { profile }).Succeeded, Is.True);
        Assert.That(ActivePromptProtectionTargetStore.Save(layout, profile.ProfileId).Succeeded, Is.True);

        var hook = new FakeNativeSubmitHookHost();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)));
        var protection = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => CreateSubmittedResult(profile.ProfileId),
            hook,
            controller,
            () => CreateSubmittedResult(profile.ProfileId),
            profile,
            storageLayout: layout,
            residentCanaryRunner: (_, _, _, _, _, _) => throw new InvalidOperationException("Canary should not run before Send."));
        var runtime = new ResidentProtectionRuntimeFacade(protection);
        var notices = new List<(string Message, bool IsFailure)>();
        Exception? capturedFailure = null;
        var coordinator = CreateWorkflowCoordinator(
            runtime,
            layout,
            setupResult: null,
            setupCandidate: _ => null,
            retryCandidate: () => null,
            recoveredRuntime: null,
            copyCanaryMarker: _ => throw new InvalidOperationException("clipboard unavailable"),
            notice: (message, isFailure) => notices.Add((message, isFailure)),
            captureFailure: exception => capturedFailure = exception);

        try
        {
            Assert.That(protection.Start(), Is.True);

            coordinator.StartResidentCanary();

            Assert.That(notices, Has.Count.EqualTo(1));
            Assert.That(notices[0].Message, Does.Contain("could not be copied"));
            Assert.That(notices[0].Message, Does.Contain("CS_CANARY_"));
            Assert.That(notices[0].IsFailure, Is.True);
            Assert.That(capturedFailure?.Message, Is.EqualTo("clipboard unavailable"));
        }
        finally
        {
            protection.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void ResidentWorkflow_ReloadThatLeavesSetupRequiredCannotPublishSetupComplete()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var protectedProfile = CreateWorkflowProfile() with { ProfileId = "chatgpt-desktop" };
        var unverifiedProfile = CreateWorkflowProfile() with
        {
            ProfileId = "codex-desktop",
            BindingSource = "not_verified",
            SubmitBinding = null,
            NewlineBinding = null,
            CapabilityStatus = OsInteractionStatusIds.BindingUnknown
        };
        var setupResult = new FirstRunSetupResult(
            Succeeded: true,
            Code: "setup_complete",
            State: new FirstRunSetupState(false, Array.Empty<string>(), "complete", false, true),
            Diagnostics: new Dictionary<string, string>
            {
                ["profile_id"] = protectedProfile.ProfileId
            });
        var protection = CreateWorkflowProtection(layout);
        var runtime = new ResidentProtectionRuntimeFacade(protection);
        var candidateHook = new FakeNativeSubmitHookHost();
        var events = new List<(string Stage, string Status, string Result)>();
        var coordinator = new ResidentProtectionWorkflowCoordinator(
            runtime,
            layout,
            () => new WorkflowSetupController(_ => setupResult),
            _ => null,
            () => CreateRuntimeSet(candidateHook, protectedProfile, unverifiedProfile),
            () => throw new InvalidOperationException("Recovery should not run."),
            () => throw new InvalidOperationException("Recovery should not run."),
            action => action(),
            action => action(),
            (_, _, _) => { },
            (_, stage, status, result, _) => events.Add((stage, status, result)));

        try
        {
            Assert.That(protection.Start(), Is.True);

            coordinator.StartInitialSetup();

            Assert.That(runtime.State.SetupRequired, Is.True);
            Assert.That(runtime.OperationalAction.Status, Is.EqualTo("failed"));
            Assert.That(events, Does.Not.Contain(("protected", "succeeded", "setup_complete")));
            Assert.That(events, Does.Contain((
                "activation_failed",
                "failed",
                "resident_not_protected_after_reload")));
        }
        finally
        {
            protection.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void ResidentWorkflow_SetupCancelledBeforeAdmissionDoesNotActivateOrPersistCandidate()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var previousProfile = CreateWorkflowProfile();
        var candidateProfile = previousProfile with
        {
            SubmitBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding,
            NewlineBinding = SubmitKeyBinding.Parse("Enter").Binding
        };
        Assert.That(SubmitBindingProfileStore.Save(layout, new[] { previousProfile }).Succeeded, Is.True);

        var queuedWork = new Queue<Action>();
        var protection = CreateWorkflowProtection(layout);
        var runtime = new ResidentProtectionRuntimeFacade(protection);
        var candidateFactoryCalls = 0;
        var coordinator = new ResidentProtectionWorkflowCoordinator(
            runtime,
            layout,
            () => new WorkflowSetupController(_ => SuccessfulSetupResult(previousProfile, candidateProfile)),
            _ =>
            {
                candidateFactoryCalls++;
                return null;
            },
            () => null,
            () => throw new InvalidOperationException("Recovery should not run."),
            () => throw new InvalidOperationException("Recovery should not run."),
            action => queuedWork.Enqueue(action),
            action => action(),
            (_, _, _) => { });

        try
        {
            Assert.That(protection.Start(), Is.True);
            protection.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "waiting_for_focus", "focus_message_composer", AttemptId: 1));
            coordinator.StartFocusedSetup();
            Assert.That(queuedWork, Has.Count.EqualTo(1));

            coordinator.CancelCurrentOperation();
            var newer = runtime.StartAction(new ResidentWorkflowActionRequest(
                "newer_setup", "starting", false, "wait_for_result"));
            queuedWork.Dequeue().Invoke();

            Assert.That(newer.Started, Is.True);
            Assert.That(candidateFactoryCalls, Is.Zero);
            Assert.That(runtime.OperationalAction.ActionKind, Is.EqualTo("newer_setup"));
            Assert.That(runtime.OperationalAction.Status, Is.EqualTo("running"));
            Assert.That(
                SubmitBindingProfileStore.Load(layout).Profiles.Single().SubmitBinding!.DisplayText,
                Is.EqualTo("Enter"));
        }
        finally
        {
            protection.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void ResidentWorkflow_SetupTransactionCompletesBeforeCancellationOrNewerWork()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var previousProfile = CreateWorkflowProfile();
        var candidateProfile = previousProfile with
        {
            SubmitBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding,
            NewlineBinding = SubmitKeyBinding.Parse("Enter").Binding
        };
        Assert.That(SubmitBindingProfileStore.Save(layout, new[] { previousProfile }).Succeeded, Is.True);

        var protection = CreateWorkflowProtection(layout);
        using var contender = new WorkflowStartContender();
        var runtime = new ResidentProtectionRuntimeFacade(protection, contender.ObserveGate);
        var candidateHook = new FakeNativeSubmitHookHost();
        runtime.SnapshotChanged += (_, _) => contender.ObserveSnapshot(runtime);
        candidateHook.OnStarted = _ => contender.StartAndWaitUntilBlocked(runtime, "newer_setup");

        var setupResult = SuccessfulSetupResult(previousProfile, candidateProfile);
        var coordinator = CreateWorkflowCoordinator(
            runtime,
            layout,
            setupResult,
            setupCandidate: profiles => CreateRuntimeSet(candidateHook, profiles[0]),
            retryCandidate: () => null);

        try
        {
            Assert.That(protection.Start(), Is.True);
            protection.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "waiting_for_focus", "focus_message_composer", AttemptId: 1));

            coordinator.StartFocusedSetup();

            contender.WaitForCompletion();
            Assert.That(contender.TerminalObserved, Is.True);
            Assert.That(contender.Result.Started, Is.True);
            Assert.That(candidateHook.Started, Is.True);
            Assert.That(
                SubmitBindingProfileStore.Load(layout).Profiles[0].SubmitBinding!.DisplayText,
                Is.EqualTo("Ctrl+Enter"));
        }
        finally
        {
            protection.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void ResidentWorkflow_SetupRollbackFailurePublishesFailureAndStopsResident()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var previousProfile = CreateWorkflowProfile();
        Assert.That(SubmitBindingProfileStore.Save(layout, new[] { previousProfile }).Succeeded, Is.True);

        var protection = CreateWorkflowProtection(layout);
        var runtime = new ResidentProtectionRuntimeFacade(protection);
        var candidateHook = new FakeNativeSubmitHookHost();
        var invalidCandidate = (SubmitBindingProfile)null!;
        var setupResult = new FirstRunSetupResult(
            Succeeded: true,
            Code: "focused_profile_verified",
            State: new FirstRunSetupState(false, Array.Empty<string>(), "complete", true, false),
            Diagnostics: new Dictionary<string, string>
            {
                ["setup_attempt_id"] = "1",
                ["profile_id"] = previousProfile.ProfileId
            },
            PreviousProfiles: new[] { previousProfile },
            PendingProfiles: new[] { invalidCandidate });
        var coordinator = CreateWorkflowCoordinator(
            runtime,
            layout,
            setupResult,
            setupCandidate: _ => CreateRuntimeSet(candidateHook, previousProfile),
            retryCandidate: () => null);

        try
        {
            Assert.That(protection.Start(), Is.True);
            protection.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "waiting_for_focus", "focus_message_composer", AttemptId: 1));

            coordinator.StartFocusedSetup();

            Assert.That(runtime.OperationalAction.Status, Is.EqualTo("failed"));
            Assert.That(runtime.OperationalAction.OutcomeCode, Is.EqualTo("setup_failed"));
            Assert.That(runtime.OperationalAction.NextAction, Is.EqualTo("retry_setup"));
            Assert.That(runtime.State.Enabled, Is.False);
            Assert.That(candidateHook.Started, Is.False);
            Assert.That(
                SubmitBindingProfileStore.Load(layout).Profiles[0].SubmitBinding!.DisplayText,
                Is.EqualTo(previousProfile.SubmitBinding!.DisplayText));
        }
        finally
        {
            protection.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void ResidentWorkflow_RecoveryTransactionCompletesBeforeCancellationOrNewerWork()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = CreateWorkflowProfile();
        var protection = CreateWorkflowProtection(layout);
        using var contender = new WorkflowStartContender();
        var runtime = new ResidentProtectionRuntimeFacade(protection, contender.ObserveGate);
        var recoveredHook = new FakeNativeSubmitHookHost();
        runtime.SnapshotChanged += (_, _) => contender.ObserveSnapshot(runtime);
        recoveredHook.OnStarted = _ => contender.StartAndWaitUntilBlocked(runtime, "newer_recovery");

        var coordinator = CreateWorkflowCoordinator(
            runtime,
            layout,
            setupResult: null,
            setupCandidate: _ => null,
            retryCandidate: () => null,
            recoveredRuntime: () => new ResidentProtectionRuntime(
                () => CreateSubmittedResult(profile.ProfileId),
                CreateRuntimeSet(recoveredHook, profile)));

        try
        {
            Assert.That(protection.Start(), Is.True);

            coordinator.RepairLocalProtection();

            contender.WaitForCompletion();
            Assert.That(contender.TerminalObserved, Is.True);
            Assert.That(contender.Result.Started, Is.True);
            Assert.That(recoveredHook.Started, Is.True);
            Assert.That(runtime.State.LocalProtectionStatus, Is.EqualTo(LocalProtectionRecovery.ReadyCode));
        }
        finally
        {
            protection.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void ResidentWorkflow_RecoveryCancelledBeforeAdmissionDoesNotActivateAndLeavesExplicitBlockedState()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var protection = CreateWorkflowProtection(layout);
        var runtime = new ResidentProtectionRuntimeFacade(protection);
        ResidentProtectionWorkflowCoordinator? coordinator = null;
        var recoveredRuntimeCalls = 0;
        OperationalActionStartResult newer = default!;
        coordinator = new ResidentProtectionWorkflowCoordinator(
            runtime,
            layout,
            () => throw new InvalidOperationException("Setup should not run."),
            _ => null,
            () => null,
            () =>
            {
                recoveredRuntimeCalls++;
                throw new InvalidOperationException("A stale recovery must not create a runtime.");
            },
            () =>
            {
                coordinator!.CancelCurrentOperation();
                newer = runtime.StartAction(new ResidentWorkflowActionRequest(
                    "newer_recovery", "starting", false, "wait_for_result"));
                return new LocalProtectionRecoveryResult(
                    Succeeded: true,
                    Code: LocalProtectionRecovery.RecoveredCode,
                    RecoveryRequired: false,
                    ConfirmationRequired: false,
                    PreviousArtifactsPreserved: true,
                    VaultInitialized: true);
            },
            action => action(),
            action => action(),
            (_, _, _) => { });

        try
        {
            Assert.That(protection.Start(), Is.True);

            coordinator.RepairLocalProtection();

            Assert.That(newer.Started, Is.True);
            Assert.That(recoveredRuntimeCalls, Is.Zero);
            Assert.That(runtime.OperationalAction.ActionKind, Is.EqualTo("newer_recovery"));
            Assert.That(runtime.OperationalAction.Status, Is.EqualTo("running"));
            Assert.That(runtime.State.LocalProtectionStatus,
                Is.EqualTo(LocalProtectionRecovery.RecoveryRequiredCode));
            Assert.That(runtime.State.NativeSubmitEnabled, Is.False);
            Assert.That(runtime.State.ComposerProtected, Is.False);
        }
        finally
        {
            protection.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static ResidentProtectionWorkflowCoordinator CreateWorkflowCoordinator(
        IResidentProtectionWorkflowPort runtime,
        DefaultStorageLayout layout,
        FirstRunSetupResult? setupResult,
        Func<IReadOnlyList<SubmitBindingProfile>, NativeSubmitRuntimeSet?> setupCandidate,
        Func<NativeSubmitRuntimeSet?> retryCandidate,
        Func<ResidentProtectionRuntime>? recoveredRuntime = null)
        => CreateWorkflowCoordinator(
            runtime,
            layout,
            setupResult,
            setupCandidate,
            retryCandidate,
            recoveredRuntime,
            copyCanaryMarker: null,
            notice: null,
            captureFailure: null);

    private static ResidentProtectionWorkflowCoordinator CreateWorkflowCoordinator(
        IResidentProtectionWorkflowPort runtime,
        DefaultStorageLayout layout,
        FirstRunSetupResult? setupResult,
        Func<IReadOnlyList<SubmitBindingProfile>, NativeSubmitRuntimeSet?> setupCandidate,
        Func<NativeSubmitRuntimeSet?> retryCandidate,
        Func<ResidentProtectionRuntime>? recoveredRuntime,
        Action<string>? copyCanaryMarker,
        Action<string, bool>? notice,
        Action<Exception>? captureFailure)
    {
        var coordinator = new ResidentProtectionWorkflowCoordinator(
            runtime,
            layout,
            () => new WorkflowSetupController(_ => setupResult
                ?? throw new InvalidOperationException("Setup should not run.")),
            setupCandidate,
            retryCandidate,
            recoveredRuntime ?? (() => throw new InvalidOperationException("Recovery should not run.")),
            () => new LocalProtectionRecoveryResult(
                Succeeded: true,
                Code: LocalProtectionRecovery.RecoveredCode,
                RecoveryRequired: false,
                ConfirmationRequired: false,
                PreviousArtifactsPreserved: true,
                VaultInitialized: true),
            action => action(),
            action => action(),
            (exception, _, _) => captureFailure?.Invoke(exception),
            copyCanaryMarker: copyCanaryMarker);
        if (notice is not null)
        {
            coordinator.Notice += notice;
        }

        return coordinator;
    }

    private static FirstRunSetupResult SuccessfulSetupResult(
        SubmitBindingProfile previousProfile,
        SubmitBindingProfile candidateProfile)
    {
        return new FirstRunSetupResult(
            Succeeded: true,
            Code: "focused_profile_verified",
            State: new FirstRunSetupState(false, Array.Empty<string>(), "complete", true, false),
            Diagnostics: new Dictionary<string, string>
            {
                ["setup_attempt_id"] = "1",
                ["profile_id"] = candidateProfile.ProfileId
            },
            PreviousProfiles: new[] { previousProfile },
            PendingProfiles: new[] { candidateProfile });
    }

    private static NativeSubmitRuntimeSet CreateRuntimeSet(
        FakeNativeSubmitHookHost hook,
        params SubmitBindingProfile[] profiles)
    {
        var runtimes = profiles.Select(profile => NativeSubmitRuntime.CreateTest(
                hook,
                new NativeSubmitInterceptionController(
                    profile,
                    new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                () => CreateSubmittedResult(profile.ProfileId),
                profile))
            .ToArray();
        return new NativeSubmitRuntimeSet(hook, runtimes);
    }

    private static TrayProtectionController CreateWorkflowProtection(DefaultStorageLayout layout)
    {
        return new TrayProtectionController(
            new FakeTrayHotkeyHost(),
            () => CreateSubmittedResult("codex-desktop"),
            nativeSubmitHookHost: null,
            nativeSubmitController: null,
            storageLayout: layout);
    }

    private static SubmitBindingProfile CreateWorkflowProfile()
    {
        return new SubmitBindingProfile(
            "codex-desktop",
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());
    }

    private sealed class WorkflowSetupController : IFirstRunSetupController
    {
        private readonly Func<DefaultStorageLayout, FirstRunSetupResult> _result;

        internal WorkflowSetupController(Func<DefaultStorageLayout, FirstRunSetupResult> result)
        {
            _result = result;
        }

        public FirstRunSetupResult EnsureSetup(DefaultStorageLayout layout) => _result(layout);

        public FirstRunSetupResult GetSetupStatus(DefaultStorageLayout layout, string? profileId = null) =>
            _result(layout);

        public FirstRunSetupResult VerifyProfile(string profileId, DefaultStorageLayout layout) =>
            _result(layout);

        public bool IsSetupComplete(DefaultStorageLayout layout) => !_result(layout).State.Required;
    }

}
