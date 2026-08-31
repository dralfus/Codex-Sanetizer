using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace CodexRedactionGate;

[TestFixture]
public sealed class ResidentCanaryTests
{
    [Test]
    public void Lifecycle_AllowsOneProtectedPathToTerminalSuccess()
    {
        var lifecycle = new ResidentCanaryLifecycle();

        Assert.That(lifecycle.TryAdvance("none", "requested"), Is.True);
        Assert.That(lifecycle.TryAdvance("requested", "armed"), Is.True);
        Assert.That(lifecycle.TryAdvance("armed", "send_observed"), Is.True);
        Assert.That(lifecycle.TryAdvance("send_observed", "transaction_started"), Is.True);
        Assert.That(lifecycle.TryAdvance("transaction_started", "overlay_created"), Is.True);
        Assert.That(lifecycle.TryAdvance("overlay_created", "sanitized_written"), Is.True);
        Assert.That(lifecycle.TryAdvance("sanitized_written", "replay_verified"), Is.True);
        Assert.That(lifecycle.TryAdvance("replay_verified", "terminal_passed"), Is.True);
        Assert.That(lifecycle.IsTerminal, Is.True);
    }

    [Test]
    public void Lifecycle_RejectsSkippedStageAndTransitionAfterTerminal()
    {
        var lifecycle = new ResidentCanaryLifecycle();

        Assert.That(lifecycle.TryAdvance("none", "requested"), Is.True);
        Assert.That(lifecycle.TryAdvance("requested", "armed"), Is.True);
        Assert.That(lifecycle.TryAdvance("armed", "terminal_passed"), Is.False);
        Assert.That(lifecycle.TryAdvance("armed", "send_observed"), Is.True);
        Assert.That(lifecycle.TryAdvance("send_observed", "transaction_started"), Is.True);
        Assert.That(lifecycle.TryAdvance("transaction_started", "overlay_created"), Is.True);
        Assert.That(lifecycle.TryAdvance("overlay_created", "sanitized_written"), Is.True);
        Assert.That(lifecycle.TryAdvance("sanitized_written", "replay_verified"), Is.True);
        Assert.That(lifecycle.TryAdvance("replay_verified", "terminal_failed"), Is.True);
        Assert.That(lifecycle.TryAdvance("terminal_failed", "terminal_passed"), Is.False);
    }

    [Test]
    public void Session_ConsumesOnlyTheArmedProfileAndCannotBeConsumedTwice()
    {
        var session = new ResidentCanarySession();
        var arm = session.Arm(42, "chatgpt-desktop");

        Assert.That(session.TryObserveSend(42, "codex-desktop", 1, out _), Is.False);
        Assert.That(session.TryObserveSend(42, "chatgpt-desktop", 2, out _), Is.False);
        Assert.That(session.TryObserveSend(42, "chatgpt-desktop", 1, out var observed), Is.True);
        Assert.That(observed.Marker, Is.EqualTo(arm.Arm!.Marker));
        Assert.That(session.TryObserveSend(42, "chatgpt-desktop", 1, out _), Is.False);
        Assert.That(session.TryCancel(42), Is.True);
    }

    [Test]
    public void InstalledCanaryReplay_IsFailClosedUntilProductionReplayIsProven()
    {
        var action = new ResidentCanaryReplayUnavailableAction();
        var result = action.Submit(TestSurfaceFactory.CreateTestSurface("chatgpt-desktop"));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.ReplayIndeterminate));
        Assert.That(result.Diagnostics["canary_code"], Is.EqualTo("replay_unavailable"));
        Assert.That(result.Diagnostics["cloud_submission"], Is.EqualTo("false"));
    }

    [Test]
    public void Session_RefusesToClaimSuccessWhenCleanupFailed()
    {
        var session = new ResidentCanarySession();
        var arm = session.Arm(43, "chatgpt-desktop").Arm!;
        Assert.That(session.TryObserveSend(43, "chatgpt-desktop", arm.TargetGeneration, out _), Is.True);
        Assert.That(session.TryRecordStage(43, "transaction_started"), Is.True);
        Assert.That(session.TryRecordStage(43, "overlay_created"), Is.True);
        Assert.That(session.TryRecordStage(43, "sanitized_written"), Is.True);
        Assert.That(session.TryRecordStage(43, "replay_verified"), Is.True);

        var completed = session.Complete(arm, succeeded: true, "passed", cleanupSucceeded: false);

        Assert.That(completed.Succeeded, Is.False);
        Assert.That(completed.Code, Is.EqualTo("cleanup_failed"));
        Assert.That(completed.Stages[^1], Is.EqualTo("terminal_failed"));
    }

    [Test]
    public void Session_ExposesOnlyAnArmedCanaryForInputAdmission()
    {
        var session = new ResidentCanarySession();

        Assert.That(session.TryGetArmed(out _), Is.False);
        var arm = session.Arm(43, "chatgpt-desktop").Arm!;

        Assert.That(session.TryGetArmed(out var current), Is.True);
        Assert.That(current, Is.SameAs(arm));

        Assert.That(session.TryObserveSend(43, "chatgpt-desktop", arm.TargetGeneration, out _), Is.True);
        Assert.That(session.TryGetArmed(out _), Is.False);
    }

    [Test]
    public void BuildIdentity_ReadsOnlySafeInstallerIdentityFromSidecar()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "install-identity.txt"),
                "installer_identity=CodexRedactionGateSetup-0.1.20260826.t1200+37c329d.exe\n");

            Assert.That(
                ResidentCanaryBuildIdentity.ReadInstallerIdentity(directory),
                Is.EqualTo("CodexRedactionGateSetup-0.1.20260826.t1200+37c329d.exe"));

            File.WriteAllText(
                Path.Combine(directory, "install-identity.txt"),
                "installer_identity=bad value\n");
            Assert.That(ResidentCanaryBuildIdentity.ReadInstallerIdentity(directory), Is.EqualTo("unbound"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void ProductionRunner_ReportsSpecificTargetVerificationFailure()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "codex-redaction-gate-canary-runner-tests",
            Guid.NewGuid().ToString("N"));
        var runner = new ResidentCanaryProductionRunner(
            DefaultStorageLayout.Create(directory),
            new FixedDiscovery(TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.FocusLost)),
            new FixedConfirmationOverlay());
        var arm = new ResidentCanaryArm(1, "chatgpt-desktop", "CS_CANARY_TEST", 1);

        try
        {
            var result = runner.Run(
                new NativeSubmitTargetIdentity(1, "chatgpt-desktop", "1234"),
                arm,
                (_, _) => true,
                () => true,
                () => new CanaryLease());

            Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.FocusLost));
            Assert.That(result.Diagnostics["canary_code"], Is.EqualTo("target_verification_failed"));
            Assert.That(result.Diagnostics["canary_stage"], Is.EqualTo("target_verification"));
            Assert.That(result.Diagnostics["cloud_submission"], Is.EqualTo("false"));
        }
        finally
        {
            runner.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void ProductionRunner_ClassifiesProtectedPathFailureWithoutCanaryCode()
    {
        var result = new OsInteractionResult(
            OsInteractionStatusIds.FocusLost,
            null,
            null,
            null,
            false,
            false,
            new Dictionary<string, string>());

        Assert.That(
            ResidentCanaryProductionRunner.CanaryFailureCode(result),
            Is.EqualTo("target_reverification_failed"));
    }

    [Test]
    public void EvidenceStore_WritesRawFreeAtomicRecord()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var evidence = ResidentCanaryEvidence.Passed(
            attemptId: 42,
            profileId: "chatgpt-desktop",
            targetGeneration: 7,
            submitBinding: "Ctrl+Enter",
            stages: new[] { "requested", "armed", "terminal_passed" },
            cleanupSucceeded: true,
            buildVersion: "0.1.test",
            sourceCommit: new string('c', 40),
            executableSha256: new string('a', 64),
            installerIdentity: "installer_candidate",
            compatibilityFingerprint: new string('b', 64));

        try
        {
            var store = new ResidentCanaryEvidenceStore(layout);
            Assert.That(store.TrySave(evidence), Is.True);
            var persisted = File.ReadAllText(store.Path);
            Assert.That(persisted, Does.Not.Contain("marker"));
            Assert.That(persisted, Does.Not.Contain("raw"));
            Assert.That(store.TryLoad(out var loaded), Is.True);
            Assert.That(loaded!.AttemptId, Is.EqualTo(evidence.AttemptId));
            Assert.That(loaded.TargetGeneration, Is.EqualTo(evidence.TargetGeneration));
            Assert.That(loaded.Outcome, Is.EqualTo(evidence.Outcome));
            Assert.That(loaded.Stages, Is.EqualTo(evidence.Stages));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void EvidenceStore_RejectsPassedCanaryWithoutBoundCompatibilityEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var evidence = ResidentCanaryEvidence.Passed(
            attemptId: 42,
            profileId: "chatgpt-desktop",
            targetGeneration: 7,
            submitBinding: "Ctrl+Enter",
            stages: new[] { "requested", "armed", "terminal_passed" },
            cleanupSucceeded: true,
            buildVersion: "0.1.test",
            sourceCommit: new string('c', 40),
            executableSha256: new string('a', 64),
            installerIdentity: "installer_candidate",
            compatibilityFingerprint: "unbound");

        try
        {
            Assert.That(new ResidentCanaryEvidenceStore(layout).TrySave(evidence), Is.False);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void EvidenceStore_PersistsIncompleteFailureAsDiagnosticOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var evidence = ResidentCanaryEvidence.Failed(
            attemptId: 7,
            profileId: "chatgpt-desktop",
            targetGeneration: 9,
            submitBinding: "unknown",
            stages: new[] { "requested", "terminal_failed" },
            reason: "target_verification_failed",
            cleanupSucceeded: true,
            buildVersion: "0.1.test",
            sourceCommit: "unbound",
            executableSha256: "unbound",
            installerIdentity: "unbound",
            compatibilityFingerprint: "unbound");

        try
        {
            var store = new ResidentCanaryEvidenceStore(layout);
            Assert.That(store.TrySave(evidence), Is.True);
            Assert.That(store.TryLoad(out var loaded), Is.True);
            Assert.That(loaded!.EvidenceDisposition, Is.EqualTo("diagnostic"));
            Assert.That(loaded.IsAdvancingEvidence, Is.False);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void EvidenceStore_RejectsUnboundIdentityWhenFailureClaimsAdvancingEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var evidence = ResidentCanaryEvidence.Failed(
            attemptId: 8,
            profileId: "chatgpt-desktop",
            targetGeneration: 9,
            submitBinding: "unknown",
            stages: new[] { "requested", "terminal_failed" },
            reason: "target_verification_failed",
            cleanupSucceeded: true,
            buildVersion: "0.1.test",
            sourceCommit: "unbound",
            executableSha256: "unbound",
            installerIdentity: "unbound",
            compatibilityFingerprint: "unbound") with
        {
            EvidenceDisposition = "advancing"
        };

        try
        {
            Assert.That(new ResidentCanaryEvidenceStore(layout).TrySave(evidence), Is.False);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void EvidenceStore_DoesNotAdvanceForCancellationEvenWithCompleteIdentity()
    {
        var evidence = ResidentCanaryEvidence.Failed(
            attemptId: 9,
            profileId: "chatgpt-desktop",
            targetGeneration: 9,
            submitBinding: "ctrl_enter",
            stages: new[] { "requested", "armed", "cancelled" },
            reason: "cancelled",
            cleanupSucceeded: true,
            buildVersion: "0.1.test",
            sourceCommit: new string('c', 40),
            executableSha256: new string('a', 64),
            installerIdentity: "installer_candidate",
            compatibilityFingerprint: new string('b', 64));

        Assert.That(evidence.EvidenceDisposition, Is.EqualTo("diagnostic"));
        Assert.That(evidence.IsAdvancingEvidence, Is.False);
    }

    [Test]
    public void EvidenceStore_PersistsCompleteIdentityCancellationAsDiagnostic()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var evidence = ResidentCanaryEvidence.Failed(
            attemptId: 10,
            profileId: "chatgpt-desktop",
            targetGeneration: 9,
            submitBinding: "ctrl_enter",
            stages: new[] { "requested", "armed", "cancelled" },
            reason: "cancelled",
            cleanupSucceeded: true,
            buildVersion: "0.1.test",
            sourceCommit: new string('c', 40),
            executableSha256: new string('a', 64),
            installerIdentity: "installer_candidate",
            compatibilityFingerprint: new string('b', 64));

        try
        {
            var store = new ResidentCanaryEvidenceStore(layout);

            Assert.That(store.TrySave(evidence), Is.True);
            Assert.That(store.TryLoad(out var loaded), Is.True);
            Assert.That(loaded!.TerminalReason, Is.EqualTo("cancelled"));
            Assert.That(loaded.EvidenceDisposition, Is.EqualTo("diagnostic"));
            Assert.That(loaded.IsAdvancingEvidence, Is.False);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void BuildIdentity_DoesNotUseProfileIdAsCompatibilityFingerprintFallback()
    {
        var profile = new SubmitBindingProfile(
            "chatgpt-desktop",
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());

        Assert.That(ResidentCanaryBuildIdentity.TryGetCompatibilityFingerprint(profile, out var fingerprint), Is.False);
        Assert.That(fingerprint, Is.EqualTo("unbound"));
    }

    [Test]
    public void Controller_RefusesToArmCanaryWithoutCompatibilityEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = new SubmitBindingProfile(
            "chatgpt-desktop",
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());
        var controller = TrayProtectionController.CreateTest(
            new CanaryHotkeyHost(),
            () => throw new AssertionException("Manual scan must not run."),
            new CanaryHookHost(),
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new AssertionException("Protected runner must not run."),
            profile,
            storageLayout: layout,
            residentCanaryRunner: (_, _, _, _, _, _) => throw new AssertionException("Canary must not run."));

        try
        {
            Assert.That(controller.Start(), Is.True);
            var action = controller.StartOperationalAction(
                "resident_canary",
                "requested",
                true,
                "focus_composer_and_send_marker");
            Assert.That(action.Started, Is.True, action.Code);

            var arm = controller.ArmResidentCanary(action.AttemptId, _ => { });

            Assert.That(arm.Started, Is.False);
            Assert.That(arm.Code, Is.EqualTo("compatibility_unavailable"));
        }
        finally
        {
            controller.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void EvidenceStore_PersistsFailedCleanupWithoutClaimingSuccess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var evidence = ResidentCanaryEvidence.Failed(
            attemptId: 7,
            profileId: "chatgpt-desktop",
            targetGeneration: 9,
            submitBinding: "Ctrl+Enter",
            stages: new[] { "requested", "terminal_failed" },
            reason: "cleanup_failed",
            cleanupSucceeded: false,
            buildVersion: "0.1.test",
            sourceCommit: "unbound",
            executableSha256: new string('a', 64),
            installerIdentity: "unbound",
            compatibilityFingerprint: new string('b', 64));

        try
        {
            var store = new ResidentCanaryEvidenceStore(layout);
            Assert.That(store.TrySave(evidence), Is.True);
            Assert.That(store.TryLoad(out var loaded), Is.True);
            Assert.That(loaded!.Outcome, Is.EqualTo("failed"));
            Assert.That(loaded.CleanupSucceeded, Is.False);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void EvidenceStore_RejectsMalformedNullCollections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        try
        {
            var store = new ResidentCanaryEvidenceStore(layout);
            layout.EnsureDirectories();
            File.WriteAllText(store.Path, "{\"schema_version\":\"1\",\"ticket_id\":\"352\",\"stages\":null}");

            Assert.That(store.TryLoad(out _), Is.False);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void Controller_CancelledCanaryPersistsTerminalEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-controller-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = CreateProtectedProfile("chatgpt-desktop");
        var controller = TrayProtectionController.CreateTest(
            new CanaryHotkeyHost(),
            () => throw new AssertionException("Manual scan must not run."),
            new CanaryHookHost(),
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new AssertionException("Protected runner must not run."),
            profile,
            storageLayout: layout,
            residentCanaryRunner: (_, _, _, _, _, _) => throw new AssertionException("Canary runner must not run."));

        try
        {
            Assert.That(controller.Start(), Is.True);
            var action = controller.StartOperationalAction(
                "resident_canary",
                "requested",
                true,
                "focus_composer_and_send_marker");
            Assert.That(action.Started, Is.True, action.Code);
            Assert.That(controller.ArmResidentCanary(action.AttemptId, _ => { }).Started, Is.True);

            Assert.That(controller.CancelResidentCanary(action.AttemptId), Is.True);

            var store = new ResidentCanaryEvidenceStore(layout);
            Assert.That(store.TryLoad(out var evidence), Is.True);
            Assert.That(evidence!.Outcome, Is.EqualTo("failed"));
            Assert.That(evidence.TerminalReason, Is.EqualTo("cancelled"));
        }
        finally
        {
            controller.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void Controller_ArmedCanaryUsesCanaryRunnerInsteadOfProductionRunner()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-controller-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = CreateProtectedProfile("chatgpt-desktop");
        var hook = new CanaryHookHost();
        var productionRunnerCalls = 0;
        var canaryRunnerCalls = 0;
        var completionCalls = 0;
        var controller = TrayProtectionController.CreateTest(
            new CanaryHotkeyHost(),
            () => throw new AssertionException("Manual scan must not run."),
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () =>
            {
                productionRunnerCalls++;
                return new OsInteractionResult(
                    OsInteractionStatusIds.Submitted,
                    null,
                    null,
                    null,
                    true,
                    true,
                    new Dictionary<string, string>());
            },
            profile,
            storageLayout: layout,
            residentCanaryRunner: (_, target, arm, traceStage, _, _) =>
            {
                canaryRunnerCalls++;
                Assert.That(traceStage("overlay_created", "confirmation_requested"), Is.True);
                return new OsInteractionResult(
                    OsInteractionStatusIds.Submitted,
                    target.CapturedSurface,
                    null,
                    null,
                    true,
                    true,
                    new Dictionary<string, string>
                    {
                        ["canary_cleanup"] = "true",
                        ["cloud_submission"] = "false",
                        ["canary_marker_present"] = "true"
                    });
            });

        try
        {
            Assert.That(controller.Start(), Is.True);
            var action = controller.StartOperationalAction(
                "resident_canary",
                "requested",
                true,
                "focus_composer_and_send_marker");
            Assert.That(action.Started, Is.True, action.Code);
            var armed = controller.ArmResidentCanary(action.AttemptId, _ => completionCalls++);
            Assert.That(armed.Started, Is.True, armed.Code);

            var surface = TestSurfaceFactory.CreateTestSurface("chatgpt-desktop") with
            {
                Metadata = new SurfaceMetadata(
                    SurfaceKind: "test",
                    ComposerStatus: OsInteractionStatusIds.SupportedComposer,
                    ArbitraryMetadata: new Dictionary<string, string>
                    {
                        ["focused_element_hash"] = "test-focus"
                    })
            };
            var target = new NativeSubmitTargetIdentity(0, profile.ProfileId, "1234", surface);
            var result = controller.RunNativeSubmitFlow(
                new NativeSubmitRuntime(
                    hook,
                    new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                    profile,
                    ResidentTargetTracedRunner: (_, _, _, _) =>
                    {
                        productionRunnerCalls++;
                        return new OsInteractionResult(
                            OsInteractionStatusIds.Submitted,
                            null,
                            null,
                            null,
                            true,
                            true,
                            new Dictionary<string, string>());
                    }),
                target,
                (_, _) => true,
                () => true,
                () => new CanaryLease(),
                new ResidentCanaryAdmission(armed.Arm!, target));

            Assert.That(result.Submitted, Is.True);
            Assert.That(result.Diagnostics["canary_code"], Is.EqualTo("evidence_binding_incomplete"));
            Assert.That(result.Diagnostics["canary_evidence"], Is.EqualTo("diagnostic"));
            Assert.That(result.Diagnostics["cloud_submission"], Is.EqualTo("false"));
            Assert.That(canaryRunnerCalls, Is.EqualTo(1));
            Assert.That(productionRunnerCalls, Is.EqualTo(0));
            Assert.That(completionCalls, Is.EqualTo(1));
            var store = new ResidentCanaryEvidenceStore(layout);
            Assert.That(store.TryLoad(out var evidence), Is.True);
            Assert.That(evidence!.EvidenceDisposition, Is.EqualTo("diagnostic"));
            Assert.That(evidence.TerminalReason, Is.EqualTo("evidence_binding_incomplete"));
            Assert.That(evidence.IsAdvancingEvidence, Is.False);
        }
        finally
        {
            controller.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void Controller_ArmedCanaryAdmissionSuppressesConfiguredSendBeforeNormalClassification()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-admission-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = CreateProtectedProfile("chatgpt-desktop");
        var hook = new CanaryHookHost();
        var surface = TestSurfaceFactory.CreateTestSurface("chatgpt-desktop");
        var controller = TrayProtectionController.CreateTest(
            new CanaryHotkeyHost(),
            () => throw new AssertionException("Manual scan must not run."),
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new AssertionException("Normal production runner must not run."),
            profile,
            storageLayout: layout,
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(surface),
            residentCanaryRunner: (_, _, _, _, _, _) => throw new AssertionException("Canary runner is not reached by classification."));

        try
        {
            Assert.That(controller.Start(), Is.True);
            var action = controller.StartOperationalAction(
                "resident_canary",
                "requested",
                true,
                "focus_composer_and_send_marker");
            Assert.That(controller.ArmResidentCanary(action.AttemptId, _ => { }).Started, Is.True);

            var classification = hook.Classify(new NativeKeyGesture(
                "Enter",
                TargetWindow: new IntPtr(1),
                TargetProcessId: 1));

            Assert.That(classification.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
            Assert.That(classification.SuppressOriginalInput, Is.True);
            Assert.That(classification.Diagnostics["canary_admission"], Is.EqualTo("armed"));
            Assert.That(classification.Diagnostics["canary_attempt_id"], Is.EqualTo(action.AttemptId.ToString()));

            var newline = hook.Classify(new NativeKeyGesture(
                "Enter",
                Ctrl: true,
                TargetWindow: new IntPtr(1),
                TargetProcessId: 1));

            Assert.That(newline.SuppressOriginalInput, Is.False);
        }
        finally
        {
            controller.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void Controller_ArmedCanaryAdmissionRunsCanaryAfterSuppressingOriginalSend()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-callback-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = CreateProtectedProfile("chatgpt-desktop");
        var hook = new CanaryHookHost();
        var canaryRunnerCalls = 0;
        var surface = TestSurfaceFactory.CreateTestSurface("chatgpt-desktop") with
        {
            Metadata = new SurfaceMetadata(
                SurfaceKind: "test",
                ComposerStatus: OsInteractionStatusIds.SupportedComposer,
                WindowHandle: "1")
        };
        var controller = TrayProtectionController.CreateTest(
            new CanaryHotkeyHost(),
            () => throw new AssertionException("Manual scan must not run."),
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new AssertionException("Normal production runner must not run."),
            profile,
            storageLayout: layout,
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(surface),
            residentCanaryRunner: (_, target, _, traceStage, _, _) =>
            {
                canaryRunnerCalls++;
                Assert.That(traceStage("composer_read", "capture_verified"), Is.True);
                Assert.That(traceStage("sanitized", "sanitization_verified"), Is.True);
                Assert.That(traceStage("overlay_created", "confirmation_requested"), Is.True);
                return new OsInteractionResult(
                    OsInteractionStatusIds.Submitted,
                    target.CapturedSurface,
                    null,
                    null,
                    Applied: true,
                    Submitted: true,
                    Diagnostics: new Dictionary<string, string>
                    {
                        ["canary_cleanup"] = "true",
                        ["cloud_submission"] = "false",
                        ["canary_marker_present"] = "true"
                    });
            });

        try
        {
            Assert.That(controller.Start(), Is.True);
            var action = controller.StartOperationalAction(
                "resident_canary",
                "requested",
                true,
                "focus_composer_and_send_marker");
            Assert.That(controller.ArmResidentCanary(action.AttemptId, _ => { }).Started, Is.True);

            var result = hook.Trigger(new NativeKeyGesture(
                "Enter",
                TargetWindow: new IntPtr(1),
                TargetProcessId: 1));

            Assert.That(result.SuppressOriginalInput, Is.True);
            Assert.That(hook.OriginalInputSuppressed, Is.True);
            Assert.That(canaryRunnerCalls, Is.EqualTo(1));
        }
        finally
        {
            controller.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public void Controller_QueuedCanaryCallbacks_ExecuteAdmittedAttemptOrFailClosedWithoutNormalRunner()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-canary-callback-race-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(directory);
        var profile = CreateProtectedProfile("chatgpt-desktop");
        var hook = new CanaryHookHost();
        var canaryRunnerCalls = 0;
        var normalRunnerCalls = 0;
        var surface = TestSurfaceFactory.CreateTestSurface("chatgpt-desktop") with
        {
            Metadata = new SurfaceMetadata(
                SurfaceKind: "test",
                ComposerStatus: OsInteractionStatusIds.SupportedComposer,
                WindowHandle: "1")
        };
        var controller = TrayProtectionController.CreateTest(
            new CanaryHotkeyHost(),
            () => throw new AssertionException("Manual scan must not run."),
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () =>
            {
                normalRunnerCalls++;
                return new OsInteractionResult(
                    OsInteractionStatusIds.Submitted,
                    surface,
                    null,
                    null,
                    Applied: true,
                    Submitted: true,
                    Diagnostics: new Dictionary<string, string>());
            },
            profile,
            storageLayout: layout,
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(surface),
            residentCanaryRunner: (_, target, _, traceStage, _, _) =>
            {
                canaryRunnerCalls++;
                Assert.That(traceStage("composer_read", "capture_verified"), Is.True);
                Assert.That(traceStage("sanitized", "sanitization_verified"), Is.True);
                Assert.That(traceStage("overlay_created", "confirmation_requested"), Is.True);
                return new OsInteractionResult(
                    OsInteractionStatusIds.Submitted,
                    target.CapturedSurface,
                    null,
                    null,
                    Applied: true,
                    Submitted: true,
                    Diagnostics: new Dictionary<string, string>
                    {
                        ["canary_cleanup"] = "true",
                        ["cloud_submission"] = "false",
                        ["canary_marker_present"] = "true"
                    });
            });

        try
        {
            Assert.That(controller.Start(), Is.True);
            var action = controller.StartOperationalAction(
                "resident_canary",
                "requested",
                true,
                "focus_composer_and_send_marker");
            var armed = controller.ArmResidentCanary(action.AttemptId, _ => { });
            Assert.That(armed.Started, Is.True, armed.Code);

            var firstGesture = new NativeKeyGesture("Enter", TargetWindow: new IntPtr(1), TargetProcessId: 1);
            var secondGesture = new NativeKeyGesture("Enter", TargetWindow: new IntPtr(1), TargetProcessId: 1);
            var first = hook.Classify(firstGesture);
            var second = hook.Classify(secondGesture);

            Assert.That(first.SuppressOriginalInput, Is.True);
            Assert.That(second.SuppressOriginalInput, Is.True);

            hook.ExecuteSuppressed(firstGesture, first);
            hook.ExecuteSuppressed(secondGesture, second);

            Assert.That(hook.OriginalInputSuppressedCount, Is.EqualTo(2));
            Assert.That(canaryRunnerCalls, Is.EqualTo(1));
            Assert.That(normalRunnerCalls, Is.EqualTo(0));
            Assert.That(controller.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
            Assert.That(controller.State.LastSubmitted, Is.False);
            Assert.That(controller.State.ProtectedSendAttemptTrace, Is.Not.Null);
            Assert.That(controller.State.ProtectedSendAttemptTrace!.Last().Stage, Is.EqualTo("terminal_blocked"));
        }
        finally
        {
            controller.Stop();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static SubmitBindingProfile CreateProtectedProfile(string profileId)
    {
        return SubmitBindingOnboardingVerifier.VerifyUserBindings(
            profileId,
            "Enter",
            "Ctrl+Enter",
            ChatGptDiscoveryFixture.CreateVerified());
    }

    private sealed class CanaryHotkeyHost : ITrayHotkeyHost
    {
        public HotkeyBinding Binding { get; } = new("test", "test", "test");

        public string? LastErrorCode => null;

        public bool Start(Action onTriggered) => true;

        public void Stop()
        {
        }
    }

    private sealed class CanaryHookHost : INativeSubmitHookHost
    {
        private Func<NativeKeyGesture, NativeSubmitInterceptionResult>? _classify;
        private Action<NativeKeyGesture, NativeSubmitInterceptionResult>? _onSuppressedSubmit;

        internal bool OriginalInputSuppressed { get; private set; }

        internal int OriginalInputSuppressedCount { get; private set; }

        public string? LastErrorCode => null;

        public bool Start(
            Func<NativeKeyGesture, NativeSubmitInterceptionResult> classify,
            Action<NativeKeyGesture, NativeSubmitInterceptionResult> onSuppressedSubmit,
            Func<NativeKeyGesture, bool> shouldSuppressClassificationFailure)
        {
            _classify = classify;
            _onSuppressedSubmit = onSuppressedSubmit;
            return true;
        }

        internal NativeSubmitInterceptionResult Classify(NativeKeyGesture gesture) =>
            _classify?.Invoke(gesture)
            ?? throw new AssertionException("The native hook was not started.");

        internal NativeSubmitInterceptionResult Trigger(NativeKeyGesture gesture)
        {
            var classification = Classify(gesture);
            if (classification.SuppressOriginalInput)
            {
                ExecuteSuppressed(gesture, classification);
            }

            return classification;
        }

        internal void ExecuteSuppressed(
            NativeKeyGesture gesture,
            NativeSubmitInterceptionResult classification)
        {
            SuppressOriginalInput(classification);
            _onSuppressedSubmit?.Invoke(gesture, classification);
        }

        internal void SuppressOriginalInput(NativeSubmitInterceptionResult classification)
        {
            Assert.That(classification.SuppressOriginalInput, Is.True);
            OriginalInputSuppressed = true;
            OriginalInputSuppressedCount++;
        }

        public void Stop()
        {
        }
    }

    private sealed class CanaryLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class FixedDiscovery : IActiveTextSurfaceDiscovery
    {
        private readonly TextSurfaceDiscoveryResult _result;

        public FixedDiscovery(TextSurfaceDiscoveryResult result)
        {
            _result = result;
        }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface() => _result;
    }
}
