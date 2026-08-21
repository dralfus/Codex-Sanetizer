using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CodexRedactionGate;
using NUnit.Framework;

public partial class SanitizerTests
{
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
        using var cancellationEntered = new ManualResetEventSlim(false);
        using var concurrentWorkFinished = new ManualResetEventSlim(false);
        var observePublication = false;
        var runtime = new ResidentProtectionRuntimeFacade(
            protection,
            operation =>
            {
                if (observePublication && operation == "publish")
                {
                    cancellationEntered.Set();
                }
            });
        var candidateHook = new FakeNativeSubmitHookHost();
        ResidentProtectionWorkflowCoordinator? coordinator = null;
        Thread? concurrentWork = null;
        var cancellationStatus = true;
        var terminalStatusBeforeNewWork = "unknown";
        OperationalActionStartResult newerWork = default!;
        candidateHook.OnStarted = _ =>
        {
            observePublication = true;
            concurrentWork = new Thread(() =>
            {
                coordinator!.CancelCurrentOperation();
                cancellationStatus = runtime.OperationalAction.Status == "cancelled";
                terminalStatusBeforeNewWork = runtime.OperationalAction.Status;
                newerWork = runtime.StartAction(new ResidentWorkflowActionRequest(
                    "newer_setup", "starting", false, "wait_for_result"));
                concurrentWorkFinished.Set();
            });
            concurrentWork.Start();
            Assert.That(cancellationEntered.Wait(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(concurrentWorkFinished.IsSet, Is.False);
        };

        var setupResult = SuccessfulSetupResult(previousProfile, candidateProfile);
        coordinator = CreateWorkflowCoordinator(
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

            Assert.That(concurrentWorkFinished.Wait(TimeSpan.FromSeconds(1)), Is.True);
            concurrentWork!.Join();
            Assert.That(cancellationStatus, Is.False);
            Assert.That(terminalStatusBeforeNewWork, Is.EqualTo("succeeded"));
            Assert.That(newerWork.Started, Is.True);
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
        using var cancellationEntered = new ManualResetEventSlim(false);
        using var concurrentWorkFinished = new ManualResetEventSlim(false);
        var observePublication = false;
        var runtime = new ResidentProtectionRuntimeFacade(
            protection,
            operation =>
            {
                if (observePublication && operation == "publish")
                {
                    cancellationEntered.Set();
                }
            });
        var recoveredHook = new FakeNativeSubmitHookHost();
        ResidentProtectionWorkflowCoordinator? coordinator = null;
        Thread? concurrentWork = null;
        var cancellationStatus = true;
        var terminalStatusBeforeNewWork = "unknown";
        OperationalActionStartResult newerWork = default!;
        recoveredHook.OnStarted = _ =>
        {
            observePublication = true;
            concurrentWork = new Thread(() =>
            {
                coordinator!.CancelCurrentOperation();
                cancellationStatus = runtime.OperationalAction.Status == "cancelled";
                terminalStatusBeforeNewWork = runtime.OperationalAction.Status;
                newerWork = runtime.StartAction(new ResidentWorkflowActionRequest(
                    "newer_recovery", "starting", false, "wait_for_result"));
                concurrentWorkFinished.Set();
            });
            concurrentWork.Start();
            Assert.That(cancellationEntered.Wait(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(concurrentWorkFinished.IsSet, Is.False);
        };

        coordinator = CreateWorkflowCoordinator(
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

            Assert.That(concurrentWorkFinished.Wait(TimeSpan.FromSeconds(1)), Is.True);
            concurrentWork!.Join();
            Assert.That(cancellationStatus, Is.False);
            Assert.That(terminalStatusBeforeNewWork, Is.EqualTo("succeeded"));
            Assert.That(newerWork.Started, Is.True);
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
    {
        return new ResidentProtectionWorkflowCoordinator(
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
            (_, _, _) => { });
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
        SubmitBindingProfile profile)
    {
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        return new NativeSubmitRuntimeSet(hook, new[] { runtime });
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

    internal sealed class WorkflowTestDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        internal DefaultStorageLayout Layout => DefaultStorageLayout.Create(_path);

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
