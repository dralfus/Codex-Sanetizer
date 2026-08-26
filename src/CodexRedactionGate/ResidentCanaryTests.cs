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
    public void BuildIdentity_ReadsOnlySafeInstallerIdentityFromSidecar()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "install-identity.txt"),
                "installer_identity=CodexRedactionGateSetup-0.1.20260826.t1200.exe\n");

            Assert.That(
                ResidentCanaryBuildIdentity.ReadInstallerIdentity(directory),
                Is.EqualTo("CodexRedactionGateSetup-0.1.20260826.t1200.exe"));

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
            sourceCommit: "unbound",
            executableSha256: new string('a', 64),
            installerIdentity: "unbound",
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
                () => new CanaryLease());

            Assert.That(result.Submitted, Is.True);
            Assert.That(result.Diagnostics["cloud_submission"], Is.EqualTo("false"));
            Assert.That(canaryRunnerCalls, Is.EqualTo(1));
            Assert.That(productionRunnerCalls, Is.EqualTo(0));
            Assert.That(completionCalls, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(layout.SettingsDirectory, "resident-canary.json")), Is.True);
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
        return new SubmitBindingProfile(
            profileId,
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());
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
        public string? LastErrorCode => null;

        public bool Start(
            Func<NativeKeyGesture, NativeSubmitInterceptionResult> classify,
            Action<NativeKeyGesture, NativeSubmitInterceptionResult> onSuppressedSubmit,
            Func<NativeKeyGesture, bool> shouldSuppressClassificationFailure) => true;

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
}
